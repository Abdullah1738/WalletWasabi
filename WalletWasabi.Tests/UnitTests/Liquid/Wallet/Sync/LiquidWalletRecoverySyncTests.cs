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
public class LiquidWalletRecoverySyncTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string OtherPeggedAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
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
	private static LiquidAssetId OtherPeggedAsset => LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);

	// Required evidence row 1: empty snapshot restores to LiquidWalletState.Empty
	// equivalence; an empty-batch commit returns ResultRevision == BaseRevision == 0.
	[Fact]
	public void RestoreAndSyncEmptySnapshotEmptyBatchAdvancesNothing()
	{
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			0,
			[],
			[]);

		LiquidWalletSyncResult result = LiquidWalletRecoverySync.RestoreAndSync(
			snapshot,
			Observation(),
			PeggedAssetHex,
			LiquidWalletObservationBatch.Create([]),
			[]);

		Assert.Equal(0ul, result.BaseRevision);
		Assert.Equal(0ul, result.ResultRevision);
		Assert.Equal(0, result.AppliedTransactionCount);
		Assert.Equal(0, result.ConfirmationCount);
		Assert.Equal(0, result.State.UnspentOutputCount);
		Assert.Equal(0, result.State.AppliedTransactionCount);
		Assert.Equal(
			0,
			result.State.QueryAssetBalance(result.ResultRevision, PeggedAsset).AtomicUnits);
		// The caller's snapshot is unchanged after the call.
		Assert.Empty(snapshot.GetDeltas());
		Assert.Empty(snapshot.GetConfirmations());
		Assert.Equal(0ul, snapshot.Revision);
	}

	// Required evidence row 1: a non-empty snapshot (two applied transactions, one
	// confirmation) restores and advances with one new observed transaction so
	// ResultRevision == snapshot.Revision + 1.
	[Fact]
	public void RestoreAndSyncNonEmptySnapshotAdvancesWithNewObservation()
	{
		LiquidTransactionId firstId = Tx('a');
		LiquidTransactionId secondId = Tx('b');
		LiquidOwnedOutput firstOutput = Output(firstId, 0, PeggedAsset, 100);
		LiquidConfirmation recordedConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);

		// Hand-build the equivalent state sequence the snapshot replays.
		LiquidWalletState handBuilt = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(firstId, [], [firstOutput]))
			.Apply(1, Delta(secondId, [], [Output(secondId, 0, PeggedAsset, 50)]))
			.Confirm(2, firstId, recordedConfirmation);

		LiquidWalletReplaySnapshot snapshot = handBuilt.ExportReplaySnapshot();
		Assert.Equal(3ul, snapshot.Revision);

		int snapshotDeltaCountBefore = snapshot.GetDeltas().Count;
		int snapshotConfirmationCountBefore = snapshot.GetConfirmations().Count;

		// One new observed transaction advances the restored state by exactly one.
		LiquidTransactionId newId = Tx('c');
		LiquidWalletObservationBatch batch = Batch(
			Observation(newId, [OwnedOutput(newId, 0, PeggedAsset, 25)]));

		LiquidWalletSyncResult result = LiquidWalletRecoverySync.RestoreAndSync(
			snapshot,
			Observation(),
			PeggedAssetHex,
			batch,
			[]);

		Assert.Equal(3ul, result.BaseRevision);
		Assert.Equal(snapshot.Revision + 1, result.ResultRevision);
		Assert.Equal(4ul, result.ResultRevision);
		Assert.Equal(1, result.AppliedTransactionCount);
		Assert.Equal(0, result.ConfirmationCount);

		// The returned state balance equals what the hand-built sequence computes
		// after applying the same new transaction.
		LiquidWalletState expected = handBuilt
			.Apply(3, Delta(newId, [], [Output(newId, 0, PeggedAsset, 25)]));
		Assert.Equal(
			expected.QueryAssetBalance(expected.Revision, PeggedAsset).AtomicUnits,
			result.State.QueryAssetBalance(result.ResultRevision, PeggedAsset).AtomicUnits);
		Assert.Equal(175, result.State.QueryAssetBalance(result.ResultRevision, PeggedAsset).AtomicUnits);
		// The restored confirmation survived the advance.
		Assert.True(result.State.TryGetConfirmation(firstId, out LiquidConfirmation? restored));
		Assert.Equal(recordedConfirmation, restored);
		// The caller's snapshot is unchanged after the call.
		Assert.Equal(snapshotDeltaCountBefore, snapshot.GetDeltas().Count);
		Assert.Equal(snapshotConfirmationCountBefore, snapshot.GetConfirmations().Count);
		Assert.Equal(3ul, snapshot.Revision);
	}

	// Required evidence row 1: reconcile-only recovery (empty batch, one unconfirm
	// row) removes the stale confirmation and advances the revision by exactly one.
	// The recorded confirmation sits strictly below the observed tip height, so the
	// unconfirm row passes Commit's tip-binding check and LiquidWalletState.Unconfirm's
	// equality check removes it.
	[Fact]
	public void RestoreAndSyncReconcileOnlyRemovesStaleConfirmation()
	{
		LiquidTransactionId receiveId = Tx('a');
		// Strictly below the observed tip height: the unconfirm row passes Commit's
		// tip check (Height < Blocks, so neither the above-tip nor the at-tip
		// hash-mismatch branch fires).
		LiquidConfirmation staleConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks - 5);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, staleConfirmation);
		LiquidWalletReplaySnapshot snapshot = confirmed.ExportReplaySnapshot();

		// A below-tip recorded confirmation is still bound to the observed tip, so
		// the reconcile planner emits no row for it.
		Assert.Empty(LiquidWalletRecoverySyncPlan.Reconcile(snapshot, Observation()));

		// The caller removes the stale confirmation through one hand-composed
		// unconfirm row carrying the recorded confirmation as the expected prior.
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				receiveId,
				staleConfirmation),
		];

		LiquidWalletSyncResult result = LiquidWalletRecoverySync.RestoreAndSync(
			snapshot,
			Observation(),
			PeggedAssetHex,
			LiquidWalletObservationBatch.Create([]),
			rows);

		Assert.Equal(2ul, result.BaseRevision);
		Assert.Equal(3ul, result.ResultRevision);
		Assert.Equal(0, result.AppliedTransactionCount);
		Assert.Equal(1, result.ConfirmationCount);
		// The stale confirmation was removed; the resulting state holds none.
		Assert.False(result.State.TryGetConfirmation(receiveId, out _));
		// Balance matches the hand-built unconfirm of the same sequence.
		LiquidWalletState expected = confirmed.Unconfirm(2, receiveId, staleConfirmation);
		Assert.Equal(
			expected.QueryAssetBalance(expected.Revision, PeggedAsset).AtomicUnits,
			result.State.QueryAssetBalance(result.ResultRevision, PeggedAsset).AtomicUnits);
		// The caller's snapshot is unchanged after the call.
		Assert.Single(snapshot.GetConfirmations());
	}

	// Required evidence row 1 / row 2: the reconcile planner emits no row for a
	// recorded confirmation that is still bound to the observed tip.
	[Fact]
	public void ReconcileEmitsNoRowForBoundConfirmation()
	{
		LiquidTransactionId receiveId = Tx('a');
		// Below the observed tip: still bound.
		LiquidConfirmation boundConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, boundConfirmation);
		LiquidWalletReplaySnapshot snapshot = confirmed.ExportReplaySnapshot();

		LiquidWalletSyncConfirmation[] reconciled =
			LiquidWalletRecoverySyncPlan.Reconcile(snapshot, Observation());
		Assert.Empty(reconciled);
	}

	[Fact]
	public void ReconcileEmitsNoRowForAtTipMatchingHashConfirmation()
	{
		LiquidTransactionId receiveId = Tx('a');
		// Exactly at the observed tip height with the matching best block hash.
		LiquidConfirmation boundConfirmation = LiquidConfirmation.Create(BestBlockHashHex, ObservedBlocks);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, boundConfirmation);
		LiquidWalletReplaySnapshot snapshot = confirmed.ExportReplaySnapshot();

		LiquidWalletSyncConfirmation[] reconciled =
			LiquidWalletRecoverySyncPlan.Reconcile(snapshot, Observation());
		Assert.Empty(reconciled);
	}

	// Required evidence row 2(a): a confirmation with Height > observed Blocks is
	// flagged by Reconcile as an unconfirm row carrying the recorded confirmation.
	[Fact]
	public void ReconcileFlagsConfirmationAboveObservedTip()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation staleConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, staleConfirmation);
		LiquidWalletReplaySnapshot snapshot = confirmed.ExportReplaySnapshot();

		LiquidWalletSyncConfirmation[] reconciled =
			LiquidWalletRecoverySyncPlan.Reconcile(snapshot, Observation());

		Assert.Single(reconciled);
		Assert.Equal(LiquidWalletSyncConfirmationKind.Unconfirm, reconciled[0].Kind);
		Assert.Equal(receiveId, reconciled[0].TransactionId);
		Assert.Equal(staleConfirmation, reconciled[0].Confirmation);
	}

	// Required evidence row 2(b): RestoreAndSync rejects when the caller instead
	// hand-composes a Confirm row for the same stale confirmation; the Commit
	// tip-binding fence fires before any mutation.
	[Fact]
	public void RestoreAndSyncRejectsHandComposedConfirmAboveObservedTip()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation staleConfirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, staleConfirmation);
		LiquidWalletReplaySnapshot snapshot = confirmed.ExportReplaySnapshot();
		int snapshotDeltaCountBefore = snapshot.GetDeltas().Count;
		int snapshotConfirmationCountBefore = snapshot.GetConfirmations().Count;

		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				staleConfirmation),
		];

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(
				snapshot,
				Observation(),
				PeggedAssetHex,
				LiquidWalletObservationBatch.Create([]),
				rows));

		// No partial state escapes; the caller's snapshot is untouched.
		Assert.Equal(snapshotDeltaCountBefore, snapshot.GetDeltas().Count);
		Assert.Equal(snapshotConfirmationCountBefore, snapshot.GetConfirmations().Count);
	}

	// Required evidence row 3: reorg-past-snapshot. A reorg deeper than a recorded
	// confirmation's height moves that confirmation's block off the observed tip
	// chain. The recorded confirmation sits strictly below the observed tip height,
	// so a hand-composed unconfirm row carrying it as the expected prior passes
	// Commit's tip-binding check and LiquidWalletState.Unconfirm's equality check
	// removes it; the resulting state holds no confirmation for that txid.
	[Fact]
	public void RestoreAndSyncReconcilesReorgedConfirmationBelowTip()
	{
		LiquidTransactionId receiveId = Tx('a');
		// Below the observed tip on a block hash the observed tip chain no longer
		// contains (the reorg moved that block off the chain).
		LiquidConfirmation reorgedConfirmation = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks - 5);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, reorgedConfirmation);
		LiquidWalletReplaySnapshot snapshot = confirmed.ExportReplaySnapshot();

		// The recorded confirmation sits strictly below the observed tip height, so
		// an unconfirm row carrying it passes Commit's tip check and is removed by
		// LiquidWalletState.Unconfirm's equality check.
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				receiveId,
				reorgedConfirmation),
		];

		LiquidWalletSyncResult result = LiquidWalletRecoverySync.RestoreAndSync(
			snapshot,
			Observation(),
			PeggedAssetHex,
			LiquidWalletObservationBatch.Create([]),
			rows);

		Assert.Equal(2ul, result.BaseRevision);
		Assert.Equal(3ul, result.ResultRevision);
		Assert.False(result.State.TryGetConfirmation(receiveId, out _));
		// The caller's snapshot still holds its recorded confirmation (no mutation).
		Assert.Single(snapshot.GetConfirmations());
	}

	// Required evidence row 3: the at-tip hash-mismatch row is the fail-closed
	// rejection when the caller attempts to keep the reorged confirmation via a
	// hand-composed Confirm row.
	[Fact]
	public void RestoreAndSyncRejectsHandComposedConfirmAtTipWithDifferentHash()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation reorgedConfirmation = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, reorgedConfirmation);
		LiquidWalletReplaySnapshot snapshot = confirmed.ExportReplaySnapshot();

		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				reorgedConfirmation),
		];

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(
				snapshot,
				Observation(),
				PeggedAssetHex,
				LiquidWalletObservationBatch.Create([]),
				rows));

		// Fail-closed: the snapshot is untouched.
		Assert.Single(snapshot.GetConfirmations());
		Assert.Single(snapshot.GetDeltas());
	}

	// Required evidence row 4: snapshot/node pegged-asset mismatch. The snapshot's
	// PeggedAssetId differs from the observation context, so Open's four-way
	// ordinal equality rejects.
	[Fact]
	public void RestoreAndSyncRejectsPeggedAssetMismatch()
	{
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(
			OtherPeggedAsset,
			0,
			[],
			[]);

		Assert.Throws<ArgumentException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(
				snapshot,
				Observation(),
				PeggedAssetHex,
				LiquidWalletObservationBatch.Create([]),
				[]));
	}

	// Required evidence row 4: restore-time rejection of an inconsistent journal.
	// A snapshot whose deltas double-apply the same transaction fails closed at
	// restore time, before Open is reached.
	[Fact]
	public void RestoreAndSyncRejectsInconsistentJournalDoubleApply()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletTransactionDelta delta = Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]);
		// The same transaction applied twice: the replay-time double-apply guard
		// fires inside RestoreReplaySnapshot before any session exists.
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			2,
			[delta, delta],
			[]);

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(
				snapshot,
				Observation(),
				PeggedAssetHex,
				LiquidWalletObservationBatch.Create([]),
				[]));
	}

	// Required evidence row 4: restore-time rejection of an unreachable one-step
	// revision gap (requested revision below the derived state).
	[Fact]
	public void RestoreAndSyncRejectsRevisionGap()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletTransactionDelta delta = Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]);
		// One delta but revision 0: the requested revision is below the derived
		// state, so ReplayBuilder.Build throws before Open is reached.
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			0,
			[delta],
			[]);

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(
				snapshot,
				Observation(),
				PeggedAssetHex,
				LiquidWalletObservationBatch.Create([]),
				[]));
	}

	// Required evidence row 4: null-argument rows for every parameter of both new
	// types.
	[Fact]
	public void RestoreAndSyncRejectsNullArguments()
	{
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(PeggedAsset, 0, [], []);
		ElementsExpectationBoundNodeObservation observation = Observation();
		LiquidWalletObservationBatch batch = LiquidWalletObservationBatch.Create([]);
		LiquidWalletSyncConfirmation[] rows = [];

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(null!, observation, PeggedAssetHex, batch, rows));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(snapshot, null!, PeggedAssetHex, batch, rows));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(snapshot, observation, null!, batch, rows));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(snapshot, observation, PeggedAssetHex, null!, rows));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletRecoverySync.RestoreAndSync(snapshot, observation, PeggedAssetHex, batch, null!));
	}

	[Fact]
	public void ReconcileRejectsNullArguments()
	{
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(PeggedAsset, 0, [], []);
		ElementsExpectationBoundNodeObservation observation = Observation();

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletRecoverySyncPlan.Reconcile(null!, observation));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletRecoverySyncPlan.Reconcile(snapshot, null!));
	}

	// Required evidence row 4: an exact replay is idempotent, while a conflicting
	// replay remains fail-closed.
	[Fact]
	public void SecondRestoreAndSyncAfterFirstAdvancesSkipsIdenticalReplay()
	{
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(PeggedAsset, 0, [], []);
		LiquidTransactionId receiveId = Tx('a');
		// The observation's input is a foreign (non-wallet) outpoint so the
		// projected delta is create-only and replays cleanly.
		LiquidWalletObservationBatch batch = Batch(
			Observation(
				receiveId,
				[OwnedOutput(receiveId, 0, PeggedAsset, 10)],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));

		LiquidWalletSyncResult first = LiquidWalletRecoverySync.RestoreAndSync(
			snapshot,
			Observation(),
			PeggedAssetHex,
			batch,
			[]);
		Assert.Equal(1ul, first.ResultRevision);

		// A second recovery against the same snapshot replays the same transaction
		// through a fresh session bound to the already-advanced state. The exact
		// replay is skipped idempotently without advancing the revision.
		LiquidWalletSyncSession second = LiquidWalletSyncSession.Open(
			first.State,
			Observation(),
			PeggedAssetHex);
		LiquidWalletSyncResult replay = second.Commit(batch, []);
		Assert.Equal(1ul, replay.BaseRevision);
		Assert.Equal(first.ResultRevision, replay.ResultRevision);
		Assert.Equal(0, replay.AppliedTransactionCount);
		Assert.Same(first.State, replay.State);
		Assert.Equal(1, replay.State.UnspentOutputCount);
		// The caller's snapshot is unchanged throughout.
		Assert.Equal(0ul, snapshot.Revision);
		Assert.Empty(snapshot.GetDeltas());
		Assert.Empty(snapshot.GetConfirmations());

		// The same transaction identifier with conflicting created data remains
		// rejected before any partial state escapes.
		LiquidWalletSyncSession conflictingSession = LiquidWalletSyncSession.Open(
			first.State,
			Observation(),
			PeggedAssetHex);
		LiquidWalletObservationBatch conflicting = Batch(
			Observation(
				receiveId,
				[OwnedOutput(receiveId, 1, PeggedAsset, 7)],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));
		Assert.Throws<InvalidOperationException>(() => conflictingSession.Commit(conflicting, []));
		Assert.Same(first.State, replay.State);
		Assert.Equal(1ul, first.State.Revision);
		Assert.Equal(1, first.State.UnspentOutputCount);
		Assert.Equal(0ul, snapshot.Revision);
		Assert.Empty(snapshot.GetDeltas());
	}

	private static ElementsExpectationBoundNodeObservation Observation(
		string? expectationPeggedAsset = null,
		string? nodeStatusPeggedAsset = null,
		string? effectiveFeeAsset = null) =>
		new(
			Expectation(expectationPeggedAsset ?? PeggedAssetHex),
			effectiveFeeAsset ?? PeggedAssetHex,
			NodeStatus(nodeStatusPeggedAsset ?? PeggedAssetHex),
			Generation());

	private static ElementsNodeExpectation Expectation(string peggedAsset) =>
		new(
			Chain: "elementsregtest",
			GenesisBlockHash: GenesisBlockHashHex,
			FedpegScript: "51",
			PeggedAsset: peggedAsset,
			ParentGenesisBlockHash: ParentGenesisHex,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

	private static ElementsNodeStatus NodeStatus(string peggedAsset) =>
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
			PeggedAsset: peggedAsset,
			ParentGenesisBlockHash: ParentGenesisHex,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

	private static ElementsNodeGenerationObservation Generation() =>
		new(StartupIdHex, 9, ObservedBlocks, BestBlockHashHex);

	private static LiquidWalletObservationBatch Batch(
		params LiquidWalletTransactionObservation[] observations) =>
		LiquidWalletObservationBatch.Create(observations);

	private static LiquidWalletTransactionObservation Observation(
		LiquidTransactionId transactionId,
		LiquidOwnedOutputObservation[] ownedOutputs,
		LiquidOutPoint[]? inputs = null) =>
		LiquidWalletTransactionObservation.Create(
			transactionId.ToConsensusBytes(),
			new byte[LiquidTransactionWitnessBinding.ByteLength],
			inputs ?? [LiquidOutPoint.CreateSpendable(transactionId, 0)],
			ownedOutputs);

	private static LiquidOwnedOutputObservation OwnedOutput(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		ulong value) =>
		LiquidOwnedOutputObservation.Create(
			transactionId.ToConsensusBytes(),
			outputIndex,
			new byte[LiquidTransactionWitnessBinding.ByteLength],
			ExternalKey.GetScriptPubKey(),
			ExternalKey.GetCompressedPublicKey(),
			Convert.FromHexString(BlindingPublicKeyHex),
			LiquidKeyBranch.External,
			0,
			assetId.ToConsensusBytes(),
			value);

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
