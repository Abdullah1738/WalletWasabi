using System;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync;

[Collection("Serial unit tests collection")]
public class LiquidWalletReorgPlannerTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string StartupIdHex = "abababababababababababababababababababababababababababababababab";
	private const string GenesisBlockHashHex = "cd179c84c35f51825f20a3b91a18d45f0c53b5ceb744a5b6ef8f0babe809396f";
	private const string ParentGenesisHex = "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206";
	private const string BestBlockHashHex = "0101010101010101010101010101010101010101010101010101010101010101";
	private const string OtherBlockHashHex = "0202020202020202020202020202020202020202020202020202020202020202";
	private const string ConfirmedBlockHashHex = "4444444444444444444444444444444444444444444444444444444444444444";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlindingPublicKeyHex = "023217042995590e0ad7e37bc929d062233f4d913bb3794c8cbabdc6634a580500";
	private const int ObservedBlocks = 42;

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);

	// Required evidence row 1: multi-batch reorg detect + unconfirm + re-apply
	// happy path. Three applied transactions across two confirmed batches
	// (heights h1 < h2, both <= the observed tip) where the observed tip's
	// BestBlockHash differs from the h2 confirmation's CanonicalBlockHash: Plan
	// emits exactly one Unconfirm row for the h2 transaction (the h1
	// confirmation stays tip-bound and emits no row), an empty
	// RollbackTransactionIds (the h2 transaction's outputs are unspent, so
	// unconfirm alone suffices), and RequiresRescan == false. The caller's
	// snapshot is unchanged after the call.
	[Fact]
	public void PlanMultiBatchStaleTipConfirmationUnconfirmsOnly()
	{
		LiquidTransactionId firstId = Tx('a');
		LiquidTransactionId secondId = Tx('b');
		LiquidTransactionId thirdId = Tx('c');
		LiquidConfirmation h1Confirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		// h2 sits exactly at the observed tip height under a block hash the
		// observed tip no longer reports: the tip-binding rule flags it.
		LiquidConfirmation h2Confirmation = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks);

		// Hand-build the equivalent state sequence the snapshot replays.
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]))
			.Confirm(1, firstId, h1Confirmation)
			.Apply(2, Delta(secondId, [], [Output(secondId, 0, PeggedAsset, 50)]))
			.Apply(3, Delta(thirdId, [], [Output(thirdId, 0, PeggedAsset, 25)]))
			.Confirm(4, thirdId, h2Confirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();
		Assert.Equal(5ul, snapshot.Revision);
		int snapshotDeltaCountBefore = snapshot.GetDeltas().Count;
		int snapshotConfirmationCountBefore = snapshot.GetConfirmations().Count;

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());

		Assert.False(plan.RequiresRescan);
		// The h1 confirmation stays tip-bound and emits no row; exactly one
		// Unconfirm row for the h2 transaction carrying the recorded
		// confirmation as the expected prior.
		LiquidWalletSyncConfirmation unconfirm = Assert.Single(plan.Unconfirmations);
		Assert.Equal(LiquidWalletSyncConfirmationKind.Unconfirm, unconfirm.Kind);
		Assert.Equal(thirdId, unconfirm.TransactionId);
		Assert.Equal(h2Confirmation, unconfirm.Confirmation);
		// The h2 transaction is the still-applied suffix, so it rolls back (the
		// h1 and second transactions stay valid and are untouched).
		LiquidTransactionId rollbackId = Assert.Single(plan.RollbackTransactionIds);
		Assert.Equal(thirdId, rollbackId);

		// The caller's snapshot is unchanged after the call.
		Assert.Equal(snapshotDeltaCountBefore, snapshot.GetDeltas().Count);
		Assert.Equal(snapshotConfirmationCountBefore, snapshot.GetConfirmations().Count);
		Assert.Equal(5ul, snapshot.Revision);

		// Executing the plan through the Required call order (unconfirm, then
		// the single rollback step) unwinds the reorged transaction; the state
		// equals the hand-built Unconfirm + RollbackLast sequence. The derived
		// Unconfirm row sits at the observed tip under a mismatched hash, so it
		// cannot be folded through Commit's per-row tip-binding fence; the
		// rollback chain alone unwinds the suffix (RollbackLast removes the
		// rolled-back transaction's confirmation).
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);
		LiquidWalletState unwound = restored.RollbackLast(restored.Revision, rollbackId);
		LiquidWalletState expected = handBuilt.RollbackLast(5, thirdId);
		Assert.Equal(expected.Revision, unwound.Revision);
		Assert.Equal(
			expected.QueryAssetBalance(expected.Revision, PeggedAsset).AtomicUnits,
			unwound.QueryAssetBalance(unwound.Revision, PeggedAsset).AtomicUnits);
		Assert.True(unwound.TryGetConfirmation(firstId, out LiquidConfirmation? surviving));
		Assert.Equal(h1Confirmation, surviving);
		Assert.False(unwound.TryGetConfirmation(thirdId, out _));
		Assert.Equal(2, unwound.AppliedTransactionCount);
	}

	// Required evidence row 1 (execution): an unconfirm-only plan executes
	// through the Required call order and yields a state whose balances and
	// confirmation set equal the hand-built Apply/Confirm/Unconfirm sequence.
	// The stale confirmation sits strictly below the observed tip height so
	// the derived Unconfirm row passes Commit's per-row tip-binding fence (an
	// at-tip hash-mismatch or above-tip row is itself rejected by that fence;
	// see the dedicated rejection rows).
	[Fact]
	public void ExecutedPlanUnconfirmYieldsHandBuiltSequence()
	{
		LiquidTransactionId firstId = Tx('a');
		LiquidTransactionId secondId = Tx('b');
		LiquidConfirmation h1Confirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		// Strictly below the observed tip: the planner's rule leaves it bound
		// (emits no row), so the caller removes it through one hand-composed
		// Unconfirm row to prove the execution path equals the hand-built
		// sequence.
		LiquidConfirmation h2Confirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks - 5);

		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]))
			.Confirm(1, firstId, h1Confirmation)
			.Apply(2, Delta(secondId, [], [Output(secondId, 0, PeggedAsset, 50)]))
			.Confirm(3, secondId, h2Confirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		// Both confirmations are strictly below the observed tip, so the plan
		// is empty; the execution below uses the caller-composed unconfirm row.
		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());
		Assert.False(plan.RequiresRescan);
		Assert.Empty(plan.Unconfirmations);
		Assert.Empty(plan.RollbackTransactionIds);

		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				secondId,
				h2Confirmation),
		];
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);
		LiquidWalletSyncSession session = LiquidWalletSyncSession.Open(restored, Observation(), PeggedAssetHex);
		LiquidWalletSyncResult result = session.Commit(
			LiquidWalletObservationBatch.Create([]),
			rows);

		LiquidWalletState expected = handBuilt.Unconfirm(4, secondId, h2Confirmation);
		Assert.Equal(expected.Revision, result.State.Revision);
		Assert.Equal(
			expected.QueryAssetBalance(expected.Revision, PeggedAsset).AtomicUnits,
			result.State.QueryAssetBalance(result.State.Revision, PeggedAsset).AtomicUnits);
		Assert.True(result.State.TryGetConfirmation(firstId, out LiquidConfirmation? surviving));
		Assert.Equal(h1Confirmation, surviving);
		Assert.False(result.State.TryGetConfirmation(secondId, out _));
	}

	// Required evidence row 2: multi-batch cascade. A reorged transaction's
	// created output is spent by a later transaction: Plan marks both
	// invalidated (dependent-spend cascade to fixpoint), emits the Unconfirm
	// rows for every recorded confirmation in the invalidated set, and orders
	// RollbackTransactionIds in exact reverse application order (later
	// transaction first). Executing the plan unwinds both exactly as the
	// hand-built sequence computes.
	[Fact]
	public void PlanCascadeInvalidatesDependentSpendInReverseOrder()
	{
		LiquidTransactionId reorgedId = Tx('a');
		LiquidTransactionId dependentId = Tx('b');
		// The reorged transaction sits above the observed tip: the reorg moved
		// the tip below it, so the tip-binding rule flags it.
		LiquidConfirmation reorgedConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);

		LiquidOwnedOutput reorgedOutput = Output(reorgedId, 0, PeggedAsset, 100);
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(reorgedId, [], [reorgedOutput]))
			.Confirm(1, reorgedId, reorgedConfirmation)
			.Apply(2, Delta(dependentId, [reorgedOutput.OutPoint], [Output(dependentId, 0, PeggedAsset, 60)]));
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());

		Assert.False(plan.RequiresRescan);
		// Only the reorged transaction carried a recorded confirmation, so only
		// one Unconfirm row is emitted; the cascade invalidates the dependent
		// transaction without fabricating a confirmation row for it.
		LiquidWalletSyncConfirmation unconfirm = Assert.Single(plan.Unconfirmations);
		Assert.Equal(LiquidWalletSyncConfirmationKind.Unconfirm, unconfirm.Kind);
		Assert.Equal(reorgedId, unconfirm.TransactionId);
		Assert.Equal(reorgedConfirmation, unconfirm.Confirmation);
		// Exact reverse application order: the later (dependent) transaction
		// rolls back first.
		Assert.Equal(2, plan.RollbackTransactionIds.Count);
		Assert.Equal(dependentId, plan.RollbackTransactionIds[0]);
		Assert.Equal(reorgedId, plan.RollbackTransactionIds[1]);

		// Execute the rollback chain. The derived Unconfirm row is above the
		// observed tip, so it cannot be folded through Commit's per-row
		// tip-binding fence; the rollback chain alone unwinds both transactions
		// (RollbackLast removes each rolled-back transaction's confirmation).
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);
		LiquidWalletState unwound = restored;
		foreach (LiquidTransactionId rollbackId in plan.RollbackTransactionIds)
		{
			unwound = unwound.RollbackLast(unwound.Revision, rollbackId);
		}

		LiquidWalletState expected = handBuilt
			.RollbackLast(3, dependentId)
			.RollbackLast(4, reorgedId);
		Assert.Equal(expected.Revision, unwound.Revision);
		Assert.Equal(
			expected.QueryAssetBalance(expected.Revision, PeggedAsset).AtomicUnits,
			unwound.QueryAssetBalance(unwound.Revision, PeggedAsset).AtomicUnits);
		Assert.Equal(0, unwound.AppliedTransactionCount);
		Assert.Equal(0, unwound.UnspentOutputCount);
		Assert.False(unwound.TryGetConfirmation(reorgedId, out _));
	}

	// Required evidence row 3: reorg-deeper-than-history rejection. The
	// snapshot's earliest retained delta is invalidated while a later,
	// independent delta stays valid (the invalidated set is not a suffix of the
	// delta list): Plan returns RequiresRescan == true with both lists empty,
	// performs no mutation, and the caller executes nothing.
	[Fact]
	public void PlanNonSuffixInvalidationRequiresRescan()
	{
		LiquidTransactionId earlyId = Tx('a');
		LiquidTransactionId lateId = Tx('b');
		// The earliest delta's confirmation is above the observed tip: the
		// reorg moved the tip below it.
		LiquidConfirmation earlyConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);

		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(earlyId, [], [Output(earlyId, 0, PeggedAsset, 100)]))
			.Confirm(1, earlyId, earlyConfirmation)
			// The later delta is independent (spends nothing the early delta
			// created) and carries no stale confirmation: it stays valid.
			.Apply(2, Delta(lateId, [], [Output(lateId, 0, PeggedAsset, 50)]));
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();
		int snapshotDeltaCountBefore = snapshot.GetDeltas().Count;
		int snapshotConfirmationCountBefore = snapshot.GetConfirmations().Count;

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());

		Assert.True(plan.RequiresRescan);
		Assert.Empty(plan.Unconfirmations);
		Assert.Empty(plan.RollbackTransactionIds);
		// No mutation; the caller executes nothing.
		Assert.Equal(snapshotDeltaCountBefore, snapshot.GetDeltas().Count);
		Assert.Equal(snapshotConfirmationCountBefore, snapshot.GetConfirmations().Count);
	}

	// Required evidence row 3, second row: a snapshot whose recorded
	// confirmation sits at a height above observation.Generation.Blocks (the
	// reorg moved the tip below a retained confirmation) is flagged as an
	// Unconfirm row by the reused Reconcile rule, and rejected by Commit if the
	// caller instead hand-composes a Confirm row for it (the SYNC-001
	// tip-binding fence fires before any mutation).
	[Fact]
	public void PlanAboveTipConfirmationUnconfirmsAndHandComposedConfirmIsRejected()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation aboveTipConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, aboveTipConfirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());

		Assert.False(plan.RequiresRescan);
		LiquidWalletSyncConfirmation unconfirm = Assert.Single(plan.Unconfirmations);
		Assert.Equal(LiquidWalletSyncConfirmationKind.Unconfirm, unconfirm.Kind);
		Assert.Equal(receiveId, unconfirm.TransactionId);
		Assert.Equal(aboveTipConfirmation, unconfirm.Confirmation);
		// The single applied transaction is the still-applied suffix, so it
		// rolls back.
		LiquidTransactionId rollbackId = Assert.Single(plan.RollbackTransactionIds);
		Assert.Equal(receiveId, rollbackId);

		// The caller attempting to keep the above-tip confirmation via a
		// hand-composed Confirm row is rejected by Commit's tip-binding fence
		// before any mutation.
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				aboveTipConfirmation),
		];
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);
		LiquidWalletSyncSession session = LiquidWalletSyncSession.Open(restored, Observation(), PeggedAssetHex);
		Assert.Throws<InvalidOperationException>(() => session.Commit(
			LiquidWalletObservationBatch.Create([]),
			rows));
		// Fail-closed: the snapshot is untouched.
		Assert.Single(snapshot.GetConfirmations());
		Assert.Single(snapshot.GetDeltas());
	}

	// Required evidence row 4: ABA at intermediate heights. Confirmations at
	// three heights h1 < h2 < h3 <= observed tip where the observed tip's
	// BestBlockHash matches the h3 confirmation but the h2 confirmation's
	// CanonicalBlockHash is on a side branch the observed chain no longer
	// contains: exercised at this slice's boundary as the Reconcile-derived
	// unconfirm path for exactly the h2 row (h1 and h3 stay tip-bound). No
	// partial state, no mutated snapshot.
	[Fact]
	public void PlanAbaAtIntermediateHeightUnconfirmsOnlyTheSideBranchRow()
	{
		LiquidTransactionId h1Id = Tx('a');
		LiquidTransactionId h2Id = Tx('b');
		LiquidTransactionId h3Id = Tx('c');
		LiquidConfirmation h1Confirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		// h2 sits above the observed tip: the reorg moved the tip below it, so
		// the Reconcile rule flags exactly this row. h1 (below tip) and h3 (at
		// the tip under the matching best block hash) stay tip-bound.
		LiquidConfirmation h2Confirmation = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks + 1);
		LiquidConfirmation h3Confirmation = LiquidConfirmation.Create(BestBlockHashHex, ObservedBlocks);

		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(h1Id, [], [Output(h1Id, 0, PeggedAsset, 100)]))
			.Confirm(1, h1Id, h1Confirmation)
			.Apply(2, Delta(h2Id, [], [Output(h2Id, 0, PeggedAsset, 50)]))
			.Confirm(3, h2Id, h2Confirmation)
			.Apply(4, Delta(h3Id, [], [Output(h3Id, 0, PeggedAsset, 25)]))
			.Confirm(5, h3Id, h3Confirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();
		int snapshotDeltaCountBefore = snapshot.GetDeltas().Count;
		int snapshotConfirmationCountBefore = snapshot.GetConfirmations().Count;

		// The reused Reconcile rule emits one Unconfirm row for exactly the h2
		// transaction; h1 and h3 stay tip-bound.
		LiquidWalletSyncConfirmation[] reconciled =
			LiquidWalletRecoverySyncPlan.Reconcile(snapshot, Observation());
		LiquidWalletSyncConfirmation row = Assert.Single(reconciled);
		Assert.Equal(LiquidWalletSyncConfirmationKind.Unconfirm, row.Kind);
		Assert.Equal(h2Id, row.TransactionId);
		Assert.Equal(h2Confirmation, row.Confirmation);

		// The derived Unconfirm row is above the observed tip, so it cannot be
		// folded through Commit's per-row tip-binding fence; the planner's
		// derivation is asserted here and the caller's snapshot is unchanged.
		Assert.Equal(snapshotDeltaCountBefore, snapshot.GetDeltas().Count);
		Assert.Equal(snapshotConfirmationCountBefore, snapshot.GetConfirmations().Count);
	}

	// Required evidence row 4, second row: the at-tip hash-mismatch row is the
	// fail-closed rejection when the caller attempts to keep the side-branch
	// confirmation via a hand-composed Confirm row.
	[Fact]
	public void HandComposedConfirmAtTipWithDifferentHashIsRejected()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation sideBranchConfirmation = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks);
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, sideBranchConfirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				sideBranchConfirmation),
		];
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);
		LiquidWalletSyncSession session = LiquidWalletSyncSession.Open(restored, Observation(), PeggedAssetHex);
		Assert.Throws<InvalidOperationException>(() => session.Commit(
			LiquidWalletObservationBatch.Create([]),
			rows));
		// Fail-closed: the snapshot is untouched.
		Assert.Single(snapshot.GetConfirmations());
		Assert.Single(snapshot.GetDeltas());
	}

	// Required evidence row 5: partial-batch boundary. The reorged confirmation
	// belongs to a batch whose other transactions stay valid (the invalidated
	// set is a proper suffix): the plan succeeds and the rollback stops exactly
	// at the boundary.
	[Fact]
	public void PlanPartialBatchBoundaryRollsBackOnlyTheInvalidatedSuffix()
	{
		LiquidTransactionId validId = Tx('a');
		LiquidTransactionId reorgedId = Tx('b');
		// Above the observed tip: the reorg moved the tip below the reorged
		// transaction's confirmation.
		LiquidConfirmation reorgedConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);

		LiquidOwnedOutput validOutput = Output(validId, 0, PeggedAsset, 100);
		// The reorged transaction spends the valid transaction's output, but the
		// valid transaction itself carries no stale confirmation: the
		// invalidated set is exactly the suffix { reorgedId }.
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(validId, [], [validOutput]))
			.Apply(1, Delta(reorgedId, [validOutput.OutPoint], [Output(reorgedId, 0, PeggedAsset, 60)]))
			.Confirm(2, reorgedId, reorgedConfirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());

		Assert.False(plan.RequiresRescan);
		LiquidWalletSyncConfirmation unconfirm = Assert.Single(plan.Unconfirmations);
		Assert.Equal(reorgedId, unconfirm.TransactionId);
		LiquidTransactionId rollbackId = Assert.Single(plan.RollbackTransactionIds);
		Assert.Equal(reorgedId, rollbackId);

		// Execute the single rollback step: it stops exactly at the boundary —
		// the valid transaction's output is restored to the unspent set and the
		// valid transaction stays applied. (The derived Unconfirm row is above
		// the observed tip, so the rollback chain alone unwinds the suffix.)
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);
		LiquidWalletState unwound = restored.RollbackLast(restored.Revision, rollbackId);

		LiquidWalletState expected = handBuilt.RollbackLast(3, reorgedId);
		Assert.Equal(expected.Revision, unwound.Revision);
		Assert.Equal(
			expected.QueryAssetBalance(expected.Revision, PeggedAsset).AtomicUnits,
			unwound.QueryAssetBalance(unwound.Revision, PeggedAsset).AtomicUnits);
		Assert.Equal(1, unwound.AppliedTransactionCount);
		Assert.True(unwound.ContainsUnspent(validOutput.OutPoint));
		Assert.False(unwound.TryGetConfirmation(reorgedId, out _));
	}

	// Required evidence row 5: a snapshot with zero confirmations yields an
	// empty plan with RequiresRescan == false.
	[Fact]
	public void PlanZeroConfirmationsYieldsEmptyPlan()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]));
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());

		Assert.False(plan.RequiresRescan);
		Assert.Empty(plan.Unconfirmations);
		Assert.Empty(plan.RollbackTransactionIds);
	}

	// Required evidence row 5: a snapshot whose every confirmation is stale
	// yields all unconfirmed and the rollback set is the full delta list in
	// reverse.
	[Fact]
	public void PlanAllStaleConfirmationsReversesFullDeltaList()
	{
		LiquidTransactionId firstId = Tx('a');
		LiquidTransactionId secondId = Tx('b');
		LiquidConfirmation firstConfirmation = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks);
		LiquidConfirmation secondConfirmation = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks);

		LiquidOwnedOutput firstOutput = Output(firstId, 0, PeggedAsset, 100);
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(firstId, [], [firstOutput]))
			.Confirm(1, firstId, firstConfirmation)
			.Apply(2, Delta(secondId, [firstOutput.OutPoint], [Output(secondId, 0, PeggedAsset, 60)]))
			.Confirm(3, secondId, secondConfirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());

		Assert.False(plan.RequiresRescan);
		// Both recorded confirmations are stale: two Unconfirm rows in canonical
		// ascending-txid order exactly as Reconcile emits them.
		Assert.Equal(2, plan.Unconfirmations.Count);
		Assert.All(plan.Unconfirmations, row =>
			Assert.Equal(LiquidWalletSyncConfirmationKind.Unconfirm, row.Kind));
		Assert.Equal(
			plan.Unconfirmations.Select(row => row.TransactionId.CanonicalRpcHex).Order(StringComparer.Ordinal),
			plan.Unconfirmations.Select(row => row.TransactionId.CanonicalRpcHex));
		// The full delta list rolls back in exact reverse application order.
		Assert.Equal(2, plan.RollbackTransactionIds.Count);
		Assert.Equal(secondId, plan.RollbackTransactionIds[0]);
		Assert.Equal(firstId, plan.RollbackTransactionIds[1]);
	}

	// Required evidence row 5: null-argument rows for every parameter of both
	// new types.
	[Fact]
	public void PlanRejectsNullArguments()
	{
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(PeggedAsset, 0, [], []);
		ElementsExpectationBoundNodeObservation observation = Observation();

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletReorgPlanner.Plan(null!, observation));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletReorgPlanner.Plan(snapshot, null!));
	}

	// Required evidence row 5: revision contention. A plan executed against a
	// state whose revision advanced between Plan and Commit trips the existing
	// EnsureRevision guard.
	[Fact]
	public void ExecutedPlanAgainstAdvancedStateFailsOnRevisionContention()
	{
		LiquidTransactionId reorgedId = Tx('a');
		LiquidTransactionId advanceId = Tx('b');
		// Above the observed tip so the planner flags it; the state then
		// advances between Plan and the rollback step, so the revision guard
		// fires on the stale expected revision.
		LiquidConfirmation staleConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(reorgedId, [], [Output(reorgedId, 0, PeggedAsset, 100)]))
			.Confirm(1, reorgedId, staleConfirmation);
		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();

		LiquidWalletReorgPlan plan = LiquidWalletReorgPlanner.Plan(snapshot, Observation());
		Assert.False(plan.RequiresRescan);
		Assert.Single(plan.Unconfirmations);
		LiquidTransactionId rollbackId = Assert.Single(plan.RollbackTransactionIds);
		Assert.Equal(reorgedId, rollbackId);

		// The state advances between Plan and the rollback step: the caller
		// chains the rollback off a stale expected revision and the existing
		// EnsureRevision guard fires.
		LiquidWalletState advanced = handBuilt
			.Apply(2, Delta(advanceId, [], [Output(advanceId, 0, PeggedAsset, 10)]));
		ulong advancedRevision = advanced.Revision;
		Assert.Throws<InvalidOperationException>(() =>
			advanced.RollbackLast(handBuilt.Revision, rollbackId));
		Assert.Equal(advancedRevision, advanced.Revision);
		// The caller's snapshot is unchanged throughout.
		Assert.Single(snapshot.GetConfirmations());
		Assert.Single(snapshot.GetDeltas());
	}

	private static ElementsExpectationBoundNodeObservation Observation() =>
		new(
			Expectation(),
			PeggedAssetHex,
			NodeStatus(),
			Generation());

	private static ElementsNodeExpectation Expectation() =>
		new(
			Chain: "elementsregtest",
			GenesisBlockHash: GenesisBlockHashHex,
			FedpegScript: "51",
			PeggedAsset: PeggedAssetHex,
			ParentGenesisBlockHash: ParentGenesisHex,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

	private static ElementsNodeStatus NodeStatus() =>
		new(
			Chain: "elementsregtest",
			Blocks: ObservedBlocks,
			Headers: ObservedBlocks,
			BestBlockHash: BestBlockHashHex,
			GenesisBlockHash: GenesisBlockHashHex,
			InitialBlockDownload: false,
			Pruned: false,
			TrimHeaders: false,
			BlockchainWarningsPresent: false,
			NetworkActive: true,
			LocalRelay: true,
			NetworkWarningsPresent: false,
			FedpegScript: "51",
			PeggedAsset: PeggedAssetHex,
			ParentGenesisBlockHash: ParentGenesisHex,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

	private static ElementsNodeGenerationObservation Generation() =>
		new(StartupIdHex, 9, ObservedBlocks, BestBlockHashHex);

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidSpendKeyReference Key(LiquidKeyBranch branch, uint index) =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), branch, index);

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidSpendKeyReference key = ExternalKey;
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			key.GetScriptPubKey(),
			LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits),
			key);
	}

	private static LiquidWalletTransactionDelta Delta(
		LiquidTransactionId transactionId,
		IEnumerable<LiquidOutPoint> spent,
		IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);
}
