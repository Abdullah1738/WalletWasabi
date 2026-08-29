using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletState
{
	public const int MaximumQueriedAssetCount = 256;

	private sealed record AppliedDelta(
		LiquidWalletTransactionDelta Delta,
		LiquidOwnedOutput[] SpentOutputs);

	private sealed class TransactionEffectTotals
	{
		public TransactionEffectTotals(LiquidAssetId assetId)
		{
			AssetId = assetId;
		}

		public LiquidAssetId AssetId { get; }
		public long SpentAtomicUnits { get; private set; }
		public long CreatedAtomicUnits { get; private set; }

		public void AddSpent(LiquidAssetAmount amount) =>
			SpentAtomicUnits = CheckedEffectTotal(SpentAtomicUnits, amount);

		public void AddCreated(LiquidAssetAmount amount) =>
			CreatedAtomicUnits = CheckedEffectTotal(CreatedAtomicUnits, amount);

		private long CheckedEffectTotal(long current, LiquidAssetAmount amount)
		{
			long updated;
			try
			{
				updated = checked(current + amount.AtomicUnits);
			}
			catch (OverflowException)
			{
				throw new OverflowException(
					"Liquid wallet transaction effect accumulation exceeded the supported range.");
			}

			if (AssetId == amount.PeggedAssetId &&
				updated > LiquidAssetAmount.MaxPeggedAssetAtomicUnits)
			{
				throw new OverflowException(
					"Liquid wallet transaction effect accumulation exceeded the supported range.");
			}

			return updated;
		}
	}

	private sealed class ReplayBuilder
	{
		private readonly LiquidAssetId _peggedAssetId;
		private readonly Dictionary<LiquidOutPoint, LiquidOwnedOutput> _unspentOutputs = [];
		private readonly Dictionary<LiquidOutPoint, LiquidOwnedOutput> _knownOutputs = [];
		private readonly HashSet<LiquidTransactionId> _appliedTransactionIds = [];
		private readonly List<AppliedDelta> _history = [];
		private readonly Dictionary<LiquidTransactionId, LiquidConfirmation> _confirmations = [];
		private readonly Dictionary<string, LiquidAssetAmount> _balances = new(StringComparer.Ordinal);
		private ulong _revision;

		public ReplayBuilder(LiquidAssetId peggedAssetId)
		{
			_peggedAssetId = peggedAssetId;
		}

		public void Apply(LiquidWalletTransactionDelta delta)
		{
			ArgumentNullException.ThrowIfNull(delta);
			if (_appliedTransactionIds.Contains(delta.TransactionId))
			{
				throw new InvalidOperationException("A Liquid wallet transaction cannot be applied more than once.");
			}

			IReadOnlyList<LiquidOutPoint> spentOutPoints = delta.GetSpentOutPoints();
			IReadOnlyList<LiquidOwnedOutput> createdOutputs = delta.GetCreatedOutputs();
			var spentOutputs = new LiquidOwnedOutput[spentOutPoints.Count];
			for (int index = 0; index < spentOutPoints.Count; index++)
			{
				LiquidOutPoint spentOutPoint = spentOutPoints[index];
				if (!_unspentOutputs.TryGetValue(spentOutPoint, out LiquidOwnedOutput? spentOutput))
				{
					throw new InvalidOperationException("A Liquid wallet transaction attempted to spend an unavailable owned output.");
				}

				spentOutputs[index] = spentOutput;
				SubtractBalance(spentOutput.Amount);
			}

			foreach (LiquidOwnedOutput createdOutput in createdOutputs)
			{
				if (createdOutput.Amount.PeggedAssetId != _peggedAssetId)
				{
					throw new InvalidOperationException("A Liquid wallet output belongs to a different pegged-asset context.");
				}
				if (_knownOutputs.ContainsKey(createdOutput.OutPoint))
				{
					throw new InvalidOperationException("A Liquid wallet transaction attempted to reuse an owned outpoint.");
				}

				AddBalance(createdOutput.Amount);
			}

			ulong nextRevision = CheckedNextRevision(_revision);
			foreach (LiquidOutPoint spentOutPoint in spentOutPoints)
			{
				_unspentOutputs.Remove(spentOutPoint);
			}
			foreach (LiquidOwnedOutput createdOutput in createdOutputs)
			{
				_unspentOutputs.Add(createdOutput.OutPoint, createdOutput);
				_knownOutputs.Add(createdOutput.OutPoint, createdOutput);
			}
			_appliedTransactionIds.Add(delta.TransactionId);
			_history.Add(new AppliedDelta(delta, spentOutputs));
			_revision = nextRevision;
		}

		public void Confirm(LiquidWalletReplayConfirmation replayConfirmation)
		{
			ArgumentNullException.ThrowIfNull(replayConfirmation);
			if (!_appliedTransactionIds.Contains(replayConfirmation.TransactionId))
			{
				throw new InvalidOperationException("Only an applied Liquid wallet transaction can be confirmed.");
			}
			if (_confirmations.ContainsKey(replayConfirmation.TransactionId))
			{
				throw new InvalidOperationException("A Liquid wallet transaction confirmation cannot be replaced.");
			}

			ulong nextRevision = CheckedNextRevision(_revision);
			_confirmations.Add(replayConfirmation.TransactionId, replayConfirmation.Confirmation);
			_revision = nextRevision;
		}

		public LiquidWalletState Build(ulong requestedRevision)
		{
			if (requestedRevision < _revision)
			{
				throw new InvalidOperationException("A Liquid wallet replay revision precedes its derived state.");
			}

			ulong revisionGap = requestedRevision - _revision;
			if (revisionGap == 1)
			{
				throw new InvalidOperationException("A Liquid wallet replay revision gap is unreachable.");
			}

			// Replay steps use expected-linear hash operations. This one-time
			// canonical balance freeze is O(A log A) for A active assets.
			return new LiquidWalletState(
				_peggedAssetId,
				requestedRevision,
				_unspentOutputs,
				_knownOutputs,
				_appliedTransactionIds,
				_history,
				_confirmations,
				LiquidAssetBalanceMap.FromAmounts(_peggedAssetId, _balances.Values));
		}

		private void AddBalance(LiquidAssetAmount amount)
		{
			string key = amount.AssetId.CanonicalRpcHex;
			_balances[key] = _balances.TryGetValue(key, out LiquidAssetAmount? current)
				? current.Add(amount)
				: amount;
		}

		private void SubtractBalance(LiquidAssetAmount amount)
		{
			string key = amount.AssetId.CanonicalRpcHex;
			if (!_balances.TryGetValue(key, out LiquidAssetAmount? current))
			{
				throw new OverflowException("Liquid asset balance subtraction cannot produce a negative result.");
			}

			LiquidAssetAmount remaining = current.Subtract(amount);
			if (remaining.IsZero)
			{
				_balances.Remove(key);
			}
			else
			{
				_balances[key] = remaining;
			}
		}
	}

	private readonly Dictionary<LiquidOutPoint, LiquidOwnedOutput> _unspentOutputs;
	private readonly Dictionary<LiquidOutPoint, LiquidOwnedOutput> _knownOutputs;
	private readonly HashSet<LiquidTransactionId> _appliedTransactionIds;
	private readonly List<AppliedDelta> _history;
	private readonly Dictionary<LiquidTransactionId, LiquidConfirmation> _confirmations;
	private readonly LiquidAssetBalanceMap _balances;

	private LiquidWalletState(
		LiquidAssetId peggedAssetId,
		ulong revision,
		Dictionary<LiquidOutPoint, LiquidOwnedOutput> unspentOutputs,
		Dictionary<LiquidOutPoint, LiquidOwnedOutput> knownOutputs,
		HashSet<LiquidTransactionId> appliedTransactionIds,
		List<AppliedDelta> history,
		Dictionary<LiquidTransactionId, LiquidConfirmation> confirmations,
		LiquidAssetBalanceMap balances)
	{
		PeggedAssetId = peggedAssetId;
		Revision = revision;
		_unspentOutputs = unspentOutputs;
		_knownOutputs = knownOutputs;
		_appliedTransactionIds = appliedTransactionIds;
		_history = history;
		_confirmations = confirmations;
		_balances = balances;
	}

	public LiquidAssetId PeggedAssetId { get; }
	public ulong Revision { get; }
	public int UnspentOutputCount => _unspentOutputs.Count;
	public int AppliedTransactionCount => _history.Count;

	/// <summary>
	/// The wallet's confirmed-history high-water: the lowest block height the wallet already
	/// holds a confirmation for, or <see langword="null"/> when it holds none. Refresh discovery
	/// uses it as the anchor for the bounded confirmed-block rescan window: anything at or above
	/// it is potentially already known, so only the gap below it down to the rescan floor needs
	/// scanning. A fresh wallet (no confirmations) anchors at <see langword="null"/>.
	/// </summary>
	internal uint? MinConfirmedHeight
	{
		get
		{
			uint? minimum = null;
			foreach (LiquidConfirmation confirmation in _confirmations.Values)
			{
				if (minimum is null || confirmation.Height < minimum.Value)
				{
					minimum = confirmation.Height;
				}
			}
			return minimum;
		}
	}

	internal bool ContainsAppliedTransaction(LiquidTransactionId transactionId)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		return _appliedTransactionIds.Contains(transactionId);
	}

	/// <summary>
	/// Returns the delta already recorded for an applied transaction, or null when
	/// the transaction has not been applied. Used to decide whether a repeated
	/// observation is an exact idempotent replay or an inconsistent duplicate.
	/// </summary>
	internal LiquidWalletTransactionDelta? GetAppliedDelta(LiquidTransactionId transactionId)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		for (int historyIndex = 0; historyIndex < _history.Count; historyIndex++)
		{
			AppliedDelta applied = _history[historyIndex];
			if (applied.Delta.TransactionId == transactionId)
			{
				return applied.Delta;
			}
		}

		return null;
	}

	public static LiquidWalletState Empty(LiquidAssetId peggedAssetId)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		return new LiquidWalletState(
			peggedAssetId,
			0,
			[],
			[],
			[],
			[],
			[],
			LiquidAssetBalanceMap.Empty(peggedAssetId));
	}

	public LiquidWalletReplaySnapshot ExportReplaySnapshot() =>
		LiquidWalletReplaySnapshot.Create(
			PeggedAssetId,
			Revision,
			_history.Select(applied => applied.Delta),
			_confirmations.Select(entry =>
				LiquidWalletReplayConfirmation.Create(entry.Key, entry.Value)));

	public static LiquidWalletState RestoreReplaySnapshot(LiquidWalletReplaySnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		var builder = new ReplayBuilder(snapshot.PeggedAssetId);
		foreach (LiquidWalletTransactionDelta delta in snapshot.GetDeltas())
		{
			builder.Apply(delta);
		}
		foreach (LiquidWalletReplayConfirmation confirmation in snapshot.GetConfirmations())
		{
			builder.Confirm(confirmation);
		}
		return builder.Build(snapshot.Revision);
	}

	public LiquidWalletState Apply(ulong expectedRevision, LiquidWalletTransactionDelta delta)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(delta);
		if (_appliedTransactionIds.Contains(delta.TransactionId))
		{
			throw new InvalidOperationException("A Liquid wallet transaction cannot be applied more than once.");
		}

		IReadOnlyList<LiquidOutPoint> spentOutPoints = delta.GetSpentOutPoints();
		IReadOnlyList<LiquidOwnedOutput> createdOutputs = delta.GetCreatedOutputs();
		var spentOutputs = new LiquidOwnedOutput[spentOutPoints.Count];
		LiquidAssetBalanceMap balances = _balances;
		for (int index = 0; index < spentOutPoints.Count; index++)
		{
			LiquidOutPoint spentOutPoint = spentOutPoints[index];
			if (!_unspentOutputs.TryGetValue(spentOutPoint, out LiquidOwnedOutput? spentOutput))
			{
				throw new InvalidOperationException("A Liquid wallet transaction attempted to spend an unavailable owned output.");
			}

			spentOutputs[index] = spentOutput;
			balances = balances.Subtract(spentOutput.Amount);
		}

		foreach (LiquidOwnedOutput createdOutput in createdOutputs)
		{
			if (createdOutput.Amount.PeggedAssetId != PeggedAssetId)
			{
				throw new InvalidOperationException("A Liquid wallet output belongs to a different pegged-asset context.");
			}
			if (_knownOutputs.ContainsKey(createdOutput.OutPoint))
			{
				throw new InvalidOperationException("A Liquid wallet transaction attempted to reuse an owned outpoint.");
			}

			balances = balances.Add(createdOutput.Amount);
		}

		ulong nextRevision = CheckedNextRevision();
		var unspentOutputs = new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_unspentOutputs);
		var knownOutputs = new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_knownOutputs);
		foreach (LiquidOutPoint spentOutPoint in spentOutPoints)
		{
			unspentOutputs.Remove(spentOutPoint);
		}
		foreach (LiquidOwnedOutput createdOutput in createdOutputs)
		{
			unspentOutputs.Add(createdOutput.OutPoint, createdOutput);
			knownOutputs.Add(createdOutput.OutPoint, createdOutput);
		}

		var appliedTransactionIds = new HashSet<LiquidTransactionId>(_appliedTransactionIds)
		{
			delta.TransactionId,
		};
		var history = new List<AppliedDelta>(_history)
		{
			new(delta, spentOutputs),
		};

		return new LiquidWalletState(
			PeggedAssetId,
			nextRevision,
			unspentOutputs,
			knownOutputs,
			appliedTransactionIds,
			history,
			new Dictionary<LiquidTransactionId, LiquidConfirmation>(_confirmations),
			balances);
	}

	public LiquidWalletState Confirm(
		ulong expectedRevision,
		LiquidTransactionId transactionId,
		LiquidConfirmation confirmation)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(transactionId);
		ArgumentNullException.ThrowIfNull(confirmation);
		if (!_appliedTransactionIds.Contains(transactionId))
		{
			throw new InvalidOperationException("Only an applied Liquid wallet transaction can be confirmed.");
		}
		if (_confirmations.ContainsKey(transactionId))
		{
			throw new InvalidOperationException("A Liquid wallet transaction confirmation cannot be replaced.");
		}

		var confirmations = new Dictionary<LiquidTransactionId, LiquidConfirmation>(_confirmations)
		{
			[transactionId] = confirmation,
		};

		return new LiquidWalletState(
			PeggedAssetId,
			CheckedNextRevision(),
			new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_unspentOutputs),
			new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_knownOutputs),
			new HashSet<LiquidTransactionId>(_appliedTransactionIds),
			new List<AppliedDelta>(_history),
			confirmations,
			_balances);
	}

	public LiquidWalletState Unconfirm(
		ulong expectedRevision,
		LiquidTransactionId transactionId,
		LiquidConfirmation expectedConfirmation)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(transactionId);
		ArgumentNullException.ThrowIfNull(expectedConfirmation);
		if (!_appliedTransactionIds.Contains(transactionId))
		{
			throw new InvalidOperationException("Only an applied Liquid wallet transaction can be unconfirmed.");
		}
		if (!_confirmations.TryGetValue(transactionId, out LiquidConfirmation? currentConfirmation) ||
			currentConfirmation != expectedConfirmation)
		{
			throw new InvalidOperationException("The Liquid wallet transaction confirmation changed before it could be removed.");
		}

		var confirmations = new Dictionary<LiquidTransactionId, LiquidConfirmation>(_confirmations);
		confirmations.Remove(transactionId);

		return new LiquidWalletState(
			PeggedAssetId,
			CheckedNextRevision(),
			new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_unspentOutputs),
			new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_knownOutputs),
			new HashSet<LiquidTransactionId>(_appliedTransactionIds),
			new List<AppliedDelta>(_history),
			confirmations,
			_balances);
	}

	public LiquidWalletState RollbackLast(
		ulong expectedRevision,
		LiquidTransactionId expectedTransactionId)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(expectedTransactionId);
		if (_history.Count == 0)
		{
			throw new InvalidOperationException("The Liquid wallet state has no transaction to roll back.");
		}

		AppliedDelta applied = _history[^1];
		if (applied.Delta.TransactionId != expectedTransactionId)
		{
			throw new InvalidOperationException("Liquid wallet rollback must follow exact reverse application order.");
		}

		IReadOnlyList<LiquidOwnedOutput> createdOutputs = applied.Delta.GetCreatedOutputs();
		LiquidAssetBalanceMap balances = _balances;
		foreach (LiquidOwnedOutput createdOutput in createdOutputs)
		{
			if (!_unspentOutputs.ContainsKey(createdOutput.OutPoint))
			{
				throw new InvalidOperationException("The latest Liquid wallet transaction has a dependent spend and cannot be rolled back.");
			}
			balances = balances.Subtract(createdOutput.Amount);
		}
		foreach (LiquidOwnedOutput spentOutput in applied.SpentOutputs)
		{
			balances = balances.Add(spentOutput.Amount);
		}

		ulong nextRevision = CheckedNextRevision();
		var unspentOutputs = new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_unspentOutputs);
		var knownOutputs = new Dictionary<LiquidOutPoint, LiquidOwnedOutput>(_knownOutputs);
		foreach (LiquidOwnedOutput createdOutput in createdOutputs)
		{
			unspentOutputs.Remove(createdOutput.OutPoint);
			knownOutputs.Remove(createdOutput.OutPoint);
		}
		foreach (LiquidOwnedOutput spentOutput in applied.SpentOutputs)
		{
			unspentOutputs.Add(spentOutput.OutPoint, spentOutput);
		}

		var appliedTransactionIds = new HashSet<LiquidTransactionId>(_appliedTransactionIds);
		appliedTransactionIds.Remove(applied.Delta.TransactionId);
		var history = new List<AppliedDelta>(_history);
		history.RemoveAt(history.Count - 1);
		var confirmations = new Dictionary<LiquidTransactionId, LiquidConfirmation>(_confirmations);
		confirmations.Remove(applied.Delta.TransactionId);

		return new LiquidWalletState(
			PeggedAssetId,
			nextRevision,
			unspentOutputs,
			knownOutputs,
			appliedTransactionIds,
			history,
			confirmations,
			balances);
	}

	public LiquidAssetBalanceMap GetBalances() =>
		LiquidAssetBalanceMap.FromAmounts(PeggedAssetId, _balances.GetAmounts());

	public LiquidAssetAmount QueryAssetBalance(
		ulong expectedRevision,
		LiquidAssetId assetId)
	{
		EnsureRevision(expectedRevision);
		LiquidAssetAmount balance = _balances.GetAmountOrZero(assetId);
		LiquidAssetId copiedAssetId = LiquidAssetId.ParseConsensusBytes(assetId.ToConsensusBytes());
		LiquidAssetId copiedPeggedAssetId = LiquidAssetId.ParseConsensusBytes(
			PeggedAssetId.ToConsensusBytes());
		return LiquidAssetAmount.Create(
			copiedAssetId,
			copiedPeggedAssetId,
			balance.AtomicUnits);
	}

	public LiquidWalletAssetBalanceQueryResult QueryAssetBalances(
		ulong expectedRevision,
		IReadOnlyList<LiquidAssetId> assetIds)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(assetIds);

		int count = assetIds.Count;
		if (count is < 1 or > MaximumQueriedAssetCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(assetIds),
				null,
				"The Liquid asset balance query could not be accepted.");
		}

		var capturedAssetIds = new LiquidAssetId[count];
		for (int index = 0; index < capturedAssetIds.Length; index++)
		{
			capturedAssetIds[index] = assetIds[index];
		}

		for (int index = 0; index < capturedAssetIds.Length; index++)
		{
			if (capturedAssetIds[index] is null)
			{
				throw new ArgumentException(
					"The Liquid asset balance query could not be accepted.",
					nameof(assetIds));
			}
		}

		var amounts = new LiquidAssetAmount[capturedAssetIds.Length];
		for (int index = 0; index < amounts.Length; index++)
		{
			LiquidAssetId assetId = capturedAssetIds[index];
			LiquidAssetAmount balance = _balances.GetAmountOrZero(assetId);
			LiquidAssetId copiedAssetId = LiquidAssetId.ParseConsensusBytes(assetId.ToConsensusBytes());
			LiquidAssetId copiedPeggedAssetId = LiquidAssetId.ParseConsensusBytes(
				PeggedAssetId.ToConsensusBytes());
			amounts[index] = LiquidAssetAmount.Create(
				copiedAssetId,
				copiedPeggedAssetId,
				balance.AtomicUnits);
		}

		return new LiquidWalletAssetBalanceQueryResult(amounts);
	}

	public LiquidWalletTransactionEffectSnapshot GetTransactionEffectSnapshot()
	{
		var effects = new LiquidWalletTransactionEffect[_history.Count];
		for (int historyIndex = 0; historyIndex < _history.Count; historyIndex++)
		{
			AppliedDelta applied = _history[historyIndex];
			_confirmations.TryGetValue(applied.Delta.TransactionId, out LiquidConfirmation? confirmation);
			effects[historyIndex] = CreateTransactionEffect(
				applied.Delta.TransactionId,
				applied.SpentOutputs,
				applied.Delta.GetRetainedCreatedOutputsForStateProjection(),
				PeggedAssetId,
				confirmation);
		}

		return LiquidWalletTransactionEffectSnapshot.TakeOwnershipFromState(
			PeggedAssetId,
			Revision,
			effects);
	}

	public LiquidWalletTransactionEffectSnapshot QueryTransactionEffect(
		ulong expectedRevision,
		LiquidTransactionId transactionId)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(transactionId);
		if (transactionId.IsZero)
		{
			throw new ArgumentException(
				"A nonzero Liquid transaction identifier is required.",
				nameof(transactionId));
		}

		AppliedDelta? applied = FindAppliedDeltaForTransactionEffectQuery(transactionId);
		var effects = new LiquidWalletTransactionEffect[applied is null ? 0 : 1];
		if (applied is not null)
		{
			_confirmations.TryGetValue(transactionId, out LiquidConfirmation? confirmation);
			effects[0] = CreateTransactionEffect(
				applied.Delta.TransactionId,
				applied.SpentOutputs,
				applied.Delta.GetRetainedCreatedOutputsForStateProjection(),
				PeggedAssetId,
				confirmation);
		}

		return LiquidWalletTransactionEffectSnapshot.TakeOwnershipFromState(
			PeggedAssetId,
			Revision,
			effects);
	}

	public LiquidWalletCoinControlSnapshot GetCoinControlSnapshot()
	{
		var entries = new LiquidWalletCoinControlEntry[_unspentOutputs.Count];
		int entryIndex = 0;
		foreach (LiquidOwnedOutput output in _unspentOutputs.Values)
		{
			entries[entryIndex++] = CreateCoinControlEntry(output);
		}

		Array.Sort(entries, CompareCoinControlEntries);
		return LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(
			PeggedAssetId,
			Revision,
			entries);
	}

	public LiquidWalletCoinControlSnapshot QueryUnspentCoinControlEntry(
		ulong expectedRevision,
		LiquidOutPoint outPoint)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(outPoint);
		bool found = _unspentOutputs.TryGetValue(outPoint, out LiquidOwnedOutput? output);
		var entries = new LiquidWalletCoinControlEntry[found ? 1 : 0];
		if (found)
		{
			entries[0] = CreateCoinControlEntry(output!);
		}

		return LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(
			PeggedAssetId,
			Revision,
			entries);
	}

	public LiquidWalletCoinControlSelection CreateCoinControlSelection(
		ulong expectedRevision,
		IReadOnlyList<LiquidOutPoint> selectedOutPoints) =>
		CreateCoinControlSelectionCore(expectedRevision, selectedOutPoints, null);

	public LiquidOrdinaryWalletExactSpendPlan CreateExactOrdinaryWalletSpendPlan(
		ulong expectedRevision,
		IReadOnlyList<LiquidOutPoint> selectedOutPoints,
		LiquidSuppliedConfidentialDestinationBatch destinations,
		LiquidAssetAmount explicitFee)
	{
		LiquidWalletCoinControlSelection selection = CreateCoinControlSelectionCore(
			expectedRevision,
			selectedOutPoints,
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount);
		return LiquidOrdinaryWalletExactSpendPlan.Create(selection, destinations, explicitFee);
	}

	private LiquidWalletCoinControlSelection CreateCoinControlSelectionCore(
		ulong expectedRevision,
		IReadOnlyList<LiquidOutPoint> selectedOutPoints,
		int? maximumSelectedInputCount)
	{
		EnsureRevision(expectedRevision);
		ArgumentNullException.ThrowIfNull(selectedOutPoints);
		int selectedCount = selectedOutPoints.Count;
		if (selectedCount == 0)
		{
			throw new ArgumentException(
				"A Liquid coin-control selection requires at least one outpoint.",
				nameof(selectedOutPoints));
		}
		if (maximumSelectedInputCount is int maximum &&
			(selectedCount < 0 || selectedCount > maximum))
		{
			throw new ArgumentOutOfRangeException(
				nameof(selectedOutPoints),
				"The exact Liquid ordinary-wallet input selection could not be accepted.");
		}
		if (selectedCount > _unspentOutputs.Count)
		{
			throw new ArgumentException(
				"A Liquid coin-control selection exceeds the current unspent-output count.",
				nameof(selectedOutPoints));
		}

		var uniqueOutPoints = new HashSet<LiquidOutPoint>(selectedCount);
		var entries = new LiquidWalletCoinControlEntry[selectedCount];
		for (int index = 0; index < entries.Length; index++)
		{
			LiquidOutPoint outPoint = selectedOutPoints[index]
				?? throw new ArgumentException(
					"A Liquid coin-control selection cannot contain a null outpoint.",
					nameof(selectedOutPoints));
			if (!uniqueOutPoints.Add(outPoint))
			{
				throw new ArgumentException(
					"A Liquid coin-control selection cannot contain a duplicate outpoint.",
					nameof(selectedOutPoints));
			}
			if (!_unspentOutputs.TryGetValue(outPoint, out LiquidOwnedOutput? output))
			{
				throw new InvalidOperationException(
					"A Liquid coin-control selection contains an output not currently retained as unspent.");
			}

			entries[index] = CreateCoinControlEntry(output);
		}

		Array.Sort(entries, CompareCoinControlEntries);
		return LiquidWalletCoinControlSelection.TakeOwnershipFromState(
			PeggedAssetId,
			Revision,
			entries);
	}

	public IReadOnlyList<LiquidOwnedOutput> GetUnspentOutputs() =>
		new ReadOnlyCollection<LiquidOwnedOutput>(
			_unspentOutputs.Values
				.OrderBy(output => output.OutPoint.TransactionId.CanonicalRpcHex, StringComparer.Ordinal)
				.ThenBy(output => output.OutPoint.OutputIndex)
				.ToArray());

	public bool ContainsUnspent(LiquidOutPoint outPoint)
	{
		ArgumentNullException.ThrowIfNull(outPoint);
		return _unspentOutputs.ContainsKey(outPoint);
	}

	public bool TryGetConfirmation(
		LiquidTransactionId transactionId,
		out LiquidConfirmation? confirmation)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		return _confirmations.TryGetValue(transactionId, out confirmation);
	}

	public override string ToString() => nameof(LiquidWalletState);

	private LiquidWalletCoinControlEntry CreateCoinControlEntry(LiquidOwnedOutput output)
	{
		_confirmations.TryGetValue(
			output.OutPoint.TransactionId,
			out LiquidConfirmation? confirmation);
		return LiquidWalletCoinControlEntry.Create(
			output.OutPoint,
			output.Amount,
			PeggedAssetId,
			confirmation);
	}

	private static int CompareCoinControlEntries(
		LiquidWalletCoinControlEntry left,
		LiquidWalletCoinControlEntry right)
	{
		int transactionOrder = StringComparer.Ordinal.Compare(
			left.OutPoint.TransactionId.CanonicalRpcHex,
			right.OutPoint.TransactionId.CanonicalRpcHex);
		return transactionOrder != 0
			? transactionOrder
			: left.OutPoint.OutputIndex.CompareTo(right.OutPoint.OutputIndex);
	}

	private AppliedDelta? FindAppliedDeltaForTransactionEffectQuery(
		LiquidTransactionId transactionId)
	{
		AppliedDelta? match = null;
		for (int historyIndex = 0; historyIndex < _history.Count; historyIndex++)
		{
			AppliedDelta candidate = _history[historyIndex];
			if (candidate.Delta.TransactionId == transactionId)
			{
				match = candidate;
			}
		}

		return match;
	}

	private static LiquidWalletTransactionEffect CreateTransactionEffect(
		LiquidTransactionId transactionId,
		ReadOnlySpan<LiquidOwnedOutput> spentOutputs,
		ReadOnlySpan<LiquidOwnedOutput> createdOutputs,
		LiquidAssetId peggedAssetId,
		LiquidConfirmation? confirmation)
	{
		var totals = new Dictionary<string, TransactionEffectTotals>(StringComparer.Ordinal);
		foreach (LiquidOwnedOutput spentOutput in spentOutputs)
		{
			AccumulateTransactionEffectAmount(
				totals,
				spentOutput.Amount,
				peggedAssetId,
				isSpent: true);
		}

		foreach (LiquidOwnedOutput createdOutput in createdOutputs)
		{
			AccumulateTransactionEffectAmount(
				totals,
				createdOutput.Amount,
				peggedAssetId,
				isSpent: false);
		}

		var changes = new List<LiquidWalletAssetNetChange>();
		foreach (TransactionEffectTotals assetTotals in totals.Values)
		{
			long netAtomicUnits = assetTotals.CreatedAtomicUnits >= assetTotals.SpentAtomicUnits
				? assetTotals.CreatedAtomicUnits - assetTotals.SpentAtomicUnits
				: -(assetTotals.SpentAtomicUnits - assetTotals.CreatedAtomicUnits);
			if (netAtomicUnits != 0)
			{
				changes.Add(LiquidWalletAssetNetChange.Create(
					assetTotals.AssetId,
					peggedAssetId,
					netAtomicUnits));
			}
		}

		changes.Sort(static (left, right) => StringComparer.Ordinal.Compare(
			left.AssetId.CanonicalRpcHex,
			right.AssetId.CanonicalRpcHex));
		return new LiquidWalletTransactionEffect(
			transactionId,
			peggedAssetId,
			confirmation,
			changes);
	}

	private static void AccumulateTransactionEffectAmount(
		Dictionary<string, TransactionEffectTotals> totals,
		LiquidAssetAmount amount,
		LiquidAssetId peggedAssetId,
		bool isSpent)
	{
		if (amount.PeggedAssetId != peggedAssetId)
		{
			throw new InvalidOperationException(
				"A Liquid wallet transaction effect belongs to a different pegged-asset context.");
		}

		string key = amount.AssetId.CanonicalRpcHex;
		if (!totals.TryGetValue(key, out TransactionEffectTotals? assetTotals))
		{
			assetTotals = new TransactionEffectTotals(amount.AssetId);
			totals.Add(key, assetTotals);
		}

		if (isSpent)
		{
			assetTotals.AddSpent(amount);
		}
		else
		{
			assetTotals.AddCreated(amount);
		}
	}

	private void EnsureRevision(ulong expectedRevision)
	{
		if (expectedRevision != Revision)
		{
			throw new InvalidOperationException("The Liquid wallet state revision changed before the requested transition.");
		}
	}

	private ulong CheckedNextRevision()
		=> CheckedNextRevision(Revision);

	private static ulong CheckedNextRevision(ulong revision)
	{
		try
		{
			return checked(revision + 1);
		}
		catch (OverflowException)
		{
			throw new OverflowException("The Liquid wallet state revision exceeded the supported range.");
		}
	}
}
