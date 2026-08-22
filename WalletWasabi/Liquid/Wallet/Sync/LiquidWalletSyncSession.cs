using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// One immutable, single-use wallet sync session binding a
/// <see cref="LiquidWalletState"/> base revision to one self-reported
/// expectation-bound node observation. <see cref="Open"/> rejects exactly on a
/// four-way ordinal pegged-asset mismatch; the base-revision guard fires in
/// <see cref="Commit"/>. The node remains self-reported only: this session
/// grants no currentness, reservation, broadcast, artifact-source, or runtime
/// authority.
/// </summary>
internal sealed class LiquidWalletSyncSession
{
	private readonly LiquidWalletState _baseState;
	private readonly ElementsExpectationBoundNodeObservation _nodeObservation;

	private LiquidWalletSyncSession(
		LiquidWalletState baseState,
		ElementsExpectationBoundNodeObservation nodeObservation)
	{
		_baseState = baseState;
		_nodeObservation = nodeObservation;
		BaseRevision = baseState.Revision;
		PeggedAsset = baseState.PeggedAssetId.CanonicalRpcHex;
		StartupId = nodeObservation.Generation.StartupId;
		ChainstateRevision = nodeObservation.Generation.ChainstateRevision;
		Blocks = nodeObservation.Generation.Blocks;
		BestBlockHash = nodeObservation.Generation.BestBlockHash;
	}

	public ulong BaseRevision { get; }
	public string PeggedAsset { get; }
	public string StartupId { get; }
	public ulong ChainstateRevision { get; }
	public int Blocks { get; }
	public string BestBlockHash { get; }

	public static LiquidWalletSyncSession Open(
		LiquidWalletState state,
		ElementsExpectationBoundNodeObservation observation,
		string peggedAsset)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(observation);
		string sessionPeggedAsset = LiquidAssetId
			.ParseRpcHex(peggedAsset, nameof(peggedAsset))
			.CanonicalRpcHex;
		if (!StringComparer.Ordinal.Equals(sessionPeggedAsset, observation.EffectiveFeeAsset) ||
			!StringComparer.Ordinal.Equals(sessionPeggedAsset, observation.NodeStatus.PeggedAsset) ||
			(observation.Expectation is not null &&
				!StringComparer.Ordinal.Equals(sessionPeggedAsset, observation.Expectation.PeggedAsset)) ||
			!StringComparer.Ordinal.Equals(sessionPeggedAsset, state.PeggedAssetId.CanonicalRpcHex))
		{
			throw new ArgumentException(
				"The Liquid wallet sync session pegged asset must equal the observed effective fee asset, node-status pegged asset, expectation pegged asset, and wallet-state pegged asset.",
				nameof(peggedAsset));
		}

		return new LiquidWalletSyncSession(state, observation);
	}

	/// <summary>
	/// Purely folds the observation batch and the confirmation rows through the
	/// existing revision-bound wallet-state transitions, fully checked before a
	/// result is produced. On any rejection the base state is returned unchanged
	/// and the session is consumed; a session is never re-committed.
	/// </summary>
	public LiquidWalletSyncResult Commit(
		LiquidWalletObservationBatch observations,
		IReadOnlyList<LiquidWalletSyncConfirmation> confirmations)
	{
		ArgumentNullException.ThrowIfNull(observations);
		ArgumentNullException.ThrowIfNull(confirmations);

		int confirmationCount = confirmations.Count;
		var copiedConfirmations = new LiquidWalletSyncConfirmation[confirmationCount];
		for (int index = 0; index < confirmationCount; index++)
		{
			copiedConfirmations[index] = confirmations[index]
				?? throw new ArgumentException(
					"Every Liquid wallet sync confirmation row is required.",
					nameof(confirmations));
		}

		LiquidWalletState state = _baseState;
		ulong expectedRevision = BaseRevision;
		IReadOnlyList<LiquidWalletTransactionObservation> transactions = observations.GetTransactions();
		for (int index = 0; index < transactions.Count; index++)
		{
			LiquidWalletTransactionObservation observation = transactions[index];
			LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(
				observation.GetTransactionIdConsensusBytes());

			LiquidOutPoint[] spentOutPoints = ComputeSpentOutPoints(observation);
			LiquidOwnedOutput[] createdOutputs = ProjectCreatedOutputs(observation);
			LiquidWalletTransactionDelta delta = LiquidWalletTransactionDelta.Create(
				transactionId,
				spentOutPoints,
				createdOutputs);
			state = state.Apply(expectedRevision, delta);
			expectedRevision = state.Revision;
		}

		int appliedTransactionCount = transactions.Count;
		for (int index = 0; index < copiedConfirmations.Length; index++)
		{
			LiquidWalletSyncConfirmation row = copiedConfirmations[index];
			EnsureBoundToObservedTip(row.Confirmation);
			state = row.Kind switch
			{
				LiquidWalletSyncConfirmationKind.Confirm => state.Confirm(
					expectedRevision,
					row.TransactionId,
					row.Confirmation),
				LiquidWalletSyncConfirmationKind.Unconfirm => state.Unconfirm(
					expectedRevision,
					row.TransactionId,
					row.Confirmation),
				_ => throw new InvalidOperationException(
					"An unsupported Liquid wallet sync confirmation kind was retained."),
			};
			expectedRevision = state.Revision;
		}

		return new LiquidWalletSyncResult(
			state,
			_nodeObservation,
			BaseRevision,
			state.Revision,
			appliedTransactionCount,
			copiedConfirmations.Length);
	}

	public override string ToString() => nameof(LiquidWalletSyncSession);

	private LiquidOutPoint[] ComputeSpentOutPoints(LiquidWalletTransactionObservation observation)
	{
		IReadOnlyList<LiquidOutPoint> inputs = observation.GetInputs();
		var spent = new List<LiquidOutPoint>(inputs.Count);
		for (int index = 0; index < inputs.Count; index++)
		{
			LiquidOutPoint input = inputs[index];
			if (_baseState.ContainsUnspent(input))
			{
				spent.Add(input);
			}
		}

		return spent.ToArray();
	}

	private LiquidOwnedOutput[] ProjectCreatedOutputs(LiquidWalletTransactionObservation observation)
	{
		IReadOnlyList<LiquidOwnedOutputObservation> ownedOutputs = observation.GetOwnedOutputs();
		var created = new LiquidOwnedOutput[ownedOutputs.Count];
		for (int index = 0; index < ownedOutputs.Count; index++)
		{
			LiquidOwnedOutputObservation ownedOutput = ownedOutputs[index];
			LiquidAssetId assetId = LiquidAssetId.ParseConsensusBytes(
				ownedOutput.GetAssetIdConsensusBytes());
			if (!StringComparer.Ordinal.Equals(assetId.CanonicalRpcHex, PeggedAsset) &&
				!StringComparer.Ordinal.Equals(
					assetId.CanonicalRpcHex,
					_baseState.PeggedAssetId.CanonicalRpcHex))
			{
				throw new InvalidOperationException(
					"A Liquid wallet sync observation output belongs to a different pegged-asset context.");
			}

			LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(
				LiquidTransactionId.ParseConsensusBytes(ownedOutput.GetTransactionIdConsensusBytes()),
				ownedOutput.OutputIndex);
			LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
				ownedOutput.GetSpendPublicKey(),
				ownedOutput.Branch,
				ownedOutput.DerivationIndex);
			created[index] = LiquidOwnedOutput.Create(
				outPoint,
				ownedOutput.GetScriptPubKey(),
				LiquidAssetAmount.Create(assetId, _baseState.PeggedAssetId, ownedOutput.Value),
				spendKey);
		}

		return created;
	}

	private void EnsureBoundToObservedTip(LiquidConfirmation confirmation)
	{
		uint observedBlocks = checked((uint)_nodeObservation.Generation.Blocks);
		if (confirmation.Height > observedBlocks ||
			(confirmation.Height == observedBlocks &&
				!StringComparer.Ordinal.Equals(
					confirmation.CanonicalBlockHash,
					_nodeObservation.Generation.BestBlockHash)))
		{
			throw new InvalidOperationException(
				"A Liquid wallet sync confirmation is not bound to the observed node tip.");
		}
	}
}
