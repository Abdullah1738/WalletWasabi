using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletState
{
	private sealed record AppliedDelta(
		LiquidWalletTransactionDelta Delta,
		LiquidOwnedOutput[] SpentOutputs);

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

	private void EnsureRevision(ulong expectedRevision)
	{
		if (expectedRevision != Revision)
		{
			throw new InvalidOperationException("The Liquid wallet state revision changed before the requested transition.");
		}
	}

	private ulong CheckedNextRevision()
	{
		try
		{
			return checked(Revision + 1);
		}
		catch (OverflowException)
		{
			throw new OverflowException("The Liquid wallet state revision exceeded the supported range.");
		}
	}
}
