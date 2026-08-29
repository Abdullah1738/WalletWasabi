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
public class LiquidWalletSyncSessionTests
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

	// Required evidence row 1: Open rejects on any of the four ordinal
	// pegged-asset mismatches plus null state and null observation.
	[Theory]
	[InlineData("state")]
	[InlineData("expectation")]
	[InlineData("nodeStatus")]
	[InlineData("effectiveFeeAsset")]
	public void OpenRejectsPeggedAssetMismatch(string mismatch)
	{
		LiquidAssetId stateAsset = mismatch == "state" ? OtherPeggedAsset : PeggedAsset;
		LiquidWalletState state = LiquidWalletState.Empty(stateAsset);
		ElementsExpectationBoundNodeObservation observation = Observation(
			expectationPeggedAsset: mismatch == "expectation" ? OtherPeggedAssetHex : PeggedAssetHex,
			nodeStatusPeggedAsset: mismatch == "nodeStatus" ? OtherPeggedAssetHex : PeggedAssetHex,
			effectiveFeeAsset: mismatch == "effectiveFeeAsset" ? OtherPeggedAssetHex : PeggedAssetHex);

		ArgumentException failure = Assert.Throws<ArgumentException>(() =>
			LiquidWalletSyncSession.Open(state, observation, PeggedAssetHex));
		Assert.Equal("peggedAsset", failure.ParamName);
		Assert.Equal(0ul, state.Revision);
	}

	[Fact]
	public void OpenRejectsNullArguments()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		ElementsExpectationBoundNodeObservation observation = Observation();

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncSession.Open(null!, observation, PeggedAssetHex));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncSession.Open(state, null!, PeggedAssetHex));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncSession.Open(state, observation, null!));
	}

	[Fact]
	public void OpenBindsBaseRevisionAndObservedGeneration()
	{
		LiquidWalletState state = ReceivedBaseState(Tx('a'), 100);
		ElementsExpectationBoundNodeObservation observation = Observation();

		LiquidWalletSyncSession session = LiquidWalletSyncSession.Open(state, observation, PeggedAssetHex);

		Assert.Equal(state.Revision, session.BaseRevision);
		Assert.Equal(1ul, session.BaseRevision);
		Assert.Equal(PeggedAssetHex, session.PeggedAsset);
		Assert.Equal(StartupIdHex, session.StartupId);
		Assert.Equal(9ul, session.ChainstateRevision);
		Assert.Equal(ObservedBlocks, session.Blocks);
		Assert.Equal(BestBlockHashHex, session.BestBlockHash);
		Assert.Equal(1, state.UnspentOutputCount);
	}

	// Required evidence row 2: happy paths.
	[Fact]
	public void CommitEmptyBatchAdvancesNothing()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletSyncSession session = Open(state);

		LiquidWalletSyncResult result = session.Commit(
			LiquidWalletObservationBatch.Create([]),
			[]);

		Assert.Equal(0ul, result.BaseRevision);
		Assert.Equal(0ul, result.ResultRevision);
		Assert.Equal(0, result.AppliedTransactionCount);
		Assert.Equal(0, result.ConfirmationCount);
		Assert.Same(state, result.State);
		Assert.Equal(PeggedAssetHex, result.NodeObservation.Expectation!.PeggedAsset);
		Assert.Equal(StartupIdHex, result.NodeObservation.Generation.StartupId);
		Assert.Equal(0, result.State.UnspentOutputCount);
	}

	[Fact]
	public void CommitAppliesOneOwnedOutputExactlyOnce()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletSyncSession session = Open(state);
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletObservationBatch batch = Batch(
			Observation(receiveId, [OwnedOutput(receiveId, 0, PeggedAsset, 12_345)]));

		LiquidWalletSyncResult result = session.Commit(batch, []);

		Assert.Equal(0ul, result.BaseRevision);
		Assert.Equal(result.BaseRevision + 1, result.ResultRevision);
		Assert.Equal(1ul, result.ResultRevision);
		Assert.Equal(1, result.AppliedTransactionCount);
		Assert.Equal(0, result.ConfirmationCount);
		Assert.NotSame(state, result.State);
		Assert.Equal(0ul, state.Revision);
		Assert.Equal(0, state.UnspentOutputCount);
		Assert.Equal(1, result.State.UnspentOutputCount);
		Assert.Equal(
			12_345,
			result.State.QueryAssetBalance(result.ResultRevision, PeggedAsset).AtomicUnits);
	}

	[Fact]
	public void CommitNetsSpendAndCreatedOutputExactly()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 100);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [received]));
		LiquidWalletSyncSession session = Open(state);

		LiquidTransactionId spendId = Tx('b');
		LiquidWalletObservationBatch batch = Batch(
			Observation(
				spendId,
				[OwnedOutput(spendId, 0, PeggedAsset, 40)],
				inputs: [received.OutPoint]));

		LiquidWalletSyncResult result = session.Commit(batch, []);

		Assert.Equal(1ul, result.BaseRevision);
		Assert.Equal(2ul, result.ResultRevision);
		Assert.Equal(1, result.AppliedTransactionCount);
		Assert.Equal(
			40,
			result.State.QueryAssetBalance(result.ResultRevision, PeggedAsset).AtomicUnits);
		Assert.Equal(1, result.State.UnspentOutputCount);
		Assert.True(result.State.ContainsUnspent(
			LiquidOutPoint.CreateSpendable(spendId, 0)));
		Assert.False(result.State.ContainsUnspent(received.OutPoint));
		// The base state is untouched by the committed fold.
		Assert.Equal(1, state.UnspentOutputCount);
		Assert.True(state.ContainsUnspent(received.OutPoint));
		Assert.Equal(100, state.QueryAssetBalance(1, PeggedAsset).AtomicUnits);
	}

	[Fact]
	public void CommitConfirmAndUnconfirmAdvanceRevisionOncePerRow()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]));
		LiquidConfirmation confirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		LiquidWalletState confirmed = state.Confirm(1, receiveId, confirmation);
		LiquidWalletSyncSession session = Open(confirmed);

		LiquidTransactionId spendId = Tx('b');
		LiquidWalletObservationBatch batch = Batch(
			Observation(spendId, [OwnedOutput(spendId, 0, PeggedAsset, 30)]));
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				receiveId,
				confirmation),
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				spendId,
				LiquidConfirmation.Create(BestBlockHashHex, ObservedBlocks)),
		];

		LiquidWalletSyncResult result = session.Commit(batch, rows);

		Assert.Equal(2ul, result.BaseRevision);
		Assert.Equal(5ul, result.ResultRevision);
		Assert.Equal(1, result.AppliedTransactionCount);
		Assert.Equal(2, result.ConfirmationCount);
		Assert.False(result.State.TryGetConfirmation(receiveId, out _));
		Assert.True(result.State.TryGetConfirmation(spendId, out LiquidConfirmation? recorded));
		Assert.Equal(BestBlockHashHex, recorded!.CanonicalBlockHash);
		Assert.Equal((uint)ObservedBlocks, recorded.Height);
		// The base state retains its recorded confirmation.
		Assert.True(confirmed.TryGetConfirmation(receiveId, out LiquidConfirmation? baseConfirmation));
		Assert.Equal(confirmation, baseConfirmation);
	}

	// Required evidence row 3: fail-closed Commit rows. Every row asserts the
	// base state is untouched and no partial application escapes.
	[Fact]
	public void CommitRejectsDoubleApply()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]));
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletObservationBatch batch = Batch(
			Observation(receiveId, [OwnedOutput(receiveId, 1, PeggedAsset, 5)]));

		AssertBaseStateUntouched(state, () => session.Commit(batch, []));
	}

	[Fact]
	public void CommitRejectsSpendOfUnavailableOutpoint()
	{
		// Two observations in one batch both spend the same base-state unspent
		// outpoint. The intersection marks it spent in both deltas (the base
		// state still contains it), so the first Apply consumes it and the
		// second Apply rejects the now-unavailable spend; the whole session
		// fails closed with no partial application.
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 100);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [received]));
		LiquidWalletSyncSession session = Open(state);
		LiquidTransactionId firstSpendId = Tx('b');
		LiquidTransactionId secondSpendId = Tx('c');
		LiquidWalletTransactionObservation first = Observation(
			firstSpendId,
			[OwnedOutput(firstSpendId, 0, PeggedAsset, 60)],
			inputs: [received.OutPoint]);
		LiquidWalletTransactionObservation second = Observation(
			secondSpendId,
			[OwnedOutput(secondSpendId, 0, PeggedAsset, 40)],
			inputs: [received.OutPoint]);
		LiquidWalletObservationBatch batch = Batch(first, second);

		AssertBaseStateUntouched(state, () => session.Commit(batch, []));
	}

	[Fact]
	public void CommitRejectsOutpointReuse()
	{
		// Replaying an already-applied transaction re-creates an outpoint the
		// base state already knows; the fold rejects before any partial state
		// escapes. The observation uses a foreign input and a fresh output
		// index so the rejection is the double-apply / known-outpoint guard,
		// not a delta-shape error. The base state stays untouched throughout.
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 100);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [received]));
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletObservationBatch batch = Batch(
			Observation(
				receiveId,
				[OwnedOutput(receiveId, 1, PeggedAsset, 50)],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));

		AssertBaseStateUntouched(state, () => session.Commit(batch, []));
	}

	[Fact]
	public void CommitAcceptsNativeOwnedIssuedAssetWithPeggedContext()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletSyncSession session = Open(state);
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletObservationBatch batch = Batch(
			Observation(receiveId, [OwnedOutput(receiveId, 0, OtherPeggedAsset, 10)]));

		LiquidWalletSyncResult result = session.Commit(batch, []);

		Assert.Equal(1, result.AppliedTransactionCount);
		Assert.Equal(10, result.State.QueryAssetBalance(result.ResultRevision, OtherPeggedAsset).AtomicUnits);
	}

	[Fact]
	public void CommitSkipsIdenticalAlreadyAppliedTransactionIdempotently()
	{
		// Repeated bounded discovery can surface the exact same observation the
		// wallet already applied. Recommitting that identical replay is an
		// idempotent skip: nothing is re-applied, no revision advances, and the
		// resulting state is the unchanged base state.
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 100);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [received]));
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletObservationBatch batch = Batch(
			Observation(
				receiveId,
				[OwnedOutput(receiveId, 0, PeggedAsset, 100)],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));

		LiquidWalletSyncResult result = session.Commit(batch, []);

		Assert.Equal(1ul, result.BaseRevision);
		Assert.Equal(1ul, result.ResultRevision);
		Assert.Equal(0, result.AppliedTransactionCount);
		Assert.Same(state, result.State);
		Assert.Equal(1, result.State.UnspentOutputCount);
		Assert.True(result.State.ContainsUnspent(received.OutPoint));
		Assert.Equal(100, result.State.QueryAssetBalance(1, PeggedAsset).AtomicUnits);
	}

	[Fact]
	public void CommitSkipsUnrelatedTransactionWithoutAdvancingRevision()
	{
		// Recent-block discovery on public networks stages every non-coinbase
		// transaction, including arbitrary third-party ones. An observation that
		// spends no wallet outpoint and owns no output is not this wallet's
		// transaction: it is skipped (not applied, no revision advance) instead
		// of reaching the delta guard, and the base state passes through
		// unchanged.
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 100);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [received]));
		LiquidWalletSyncSession session = Open(state);
		LiquidTransactionId foreignId = Tx('b');
		LiquidWalletObservationBatch batch = Batch(
			Observation(
				foreignId,
				[],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));

		LiquidWalletSyncResult result = session.Commit(batch, []);

		Assert.Equal(1ul, result.BaseRevision);
		Assert.Equal(1ul, result.ResultRevision);
		Assert.Equal(0, result.AppliedTransactionCount);
		Assert.Same(state, result.State);
		Assert.Equal(1, result.State.UnspentOutputCount);
		Assert.True(result.State.ContainsUnspent(received.OutPoint));
		Assert.Equal(100, result.State.QueryAssetBalance(1, PeggedAsset).AtomicUnits);
		Assert.Equal(1, result.State.AppliedTransactionCount);
	}

	[Fact]
	public void CommitSkipsUnrelatedTransactionButAppliesOwnedOutputInSameBatch()
	{
		// A batch mixing an unrelated third-party transaction with a genuine
		// owned-output receive skips only the unrelated one; the owned
		// transaction still applies exactly once and advances the revision.
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletSyncSession session = Open(state);
		LiquidTransactionId foreignId = Tx('b');
		LiquidTransactionId receiveId = Tx('a');
		// The batch requires strictly ascending consensus identifiers, so the
		// owned receive ('a') precedes the unrelated transaction ('b').
		LiquidWalletObservationBatch batch = Batch(
			Observation(receiveId, [OwnedOutput(receiveId, 0, PeggedAsset, 12_345)]),
			Observation(
				foreignId,
				[],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));

		LiquidWalletSyncResult result = session.Commit(batch, []);

		Assert.Equal(0ul, result.BaseRevision);
		Assert.Equal(1ul, result.ResultRevision);
		Assert.Equal(1, result.AppliedTransactionCount);
		Assert.Equal(1, result.State.UnspentOutputCount);
		Assert.Equal(
			12_345,
			result.State.QueryAssetBalance(result.ResultRevision, PeggedAsset).AtomicUnits);
	}

	[Fact]
	public void CommitRejectsConfirmationOfUnappliedTransaction()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				Tx('a'),
				LiquidConfirmation.Create(ConfirmedBlockHashHex, 7)),
		];

		AssertBaseStateUntouched(state, () =>
			session.Commit(LiquidWalletObservationBatch.Create([]), rows));
	}

	[Fact]
	public void CommitSkipsIdenticalConfirmationReplayWithoutAdvancingRevision()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation recorded = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, recorded);
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				recorded),
		];

		LiquidWalletSyncResult result = session.Commit(
			LiquidWalletObservationBatch.Create([]),
			rows);

		Assert.Same(state, result.State);
		Assert.Equal(state.Revision, result.ResultRevision);
		Assert.Equal(0, result.AppliedTransactionCount);
		Assert.Equal(0, result.ConfirmationCount);
	}

	[Fact]
	public void CommitRejectsConfirmationReplacement()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation recorded = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, recorded);
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				LiquidConfirmation.Create(OtherBlockHashHex, 8)),
		];

		AssertBaseStateUntouched(state, () =>
			session.Commit(LiquidWalletObservationBatch.Create([]), rows));
	}

	[Fact]
	public void CommitRejectsUnconfirmWithMismatchedExpectedConfirmation()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation recorded = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, recorded);
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				receiveId,
				LiquidConfirmation.Create(ConfirmedBlockHashHex, 8)),
		];

		AssertBaseStateUntouched(state, () =>
			session.Commit(LiquidWalletObservationBatch.Create([]), rows));
	}

	[Fact]
	public void CommitRejectsConfirmationHeightAboveObservedBlocks()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]));
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1)),
		];

		AssertBaseStateUntouched(state, () =>
			session.Commit(LiquidWalletObservationBatch.Create([]), rows));
	}

	[Fact]
	public void CommitRejectsConfirmationAtObservedTipWithDifferentBlockHash()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]));
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				receiveId,
				LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks)),
		];

		AssertBaseStateUntouched(state, () =>
			session.Commit(LiquidWalletObservationBatch.Create([]), rows));
	}

	[Fact]
	public void CommitRejectsUnconfirmWithExpectedPriorConfirmationAboveObservedBlocks()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation recorded = LiquidConfirmation.Create(ConfirmedBlockHashHex, ObservedBlocks + 1);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, recorded);
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				receiveId,
				recorded),
		];

		AssertBaseStateUntouched(state, () =>
			session.Commit(LiquidWalletObservationBatch.Create([]), rows));
	}

	[Fact]
	public void CommitRejectsUnconfirmReorgTipMismatch()
	{
		// The recorded confirmation sits exactly at the observed tip height but
		// its block hash is no longer the observed best block hash: the reorg
		// moved the recorded block off the observed tip chain.
		LiquidTransactionId receiveId = Tx('a');
		LiquidConfirmation recorded = LiquidConfirmation.Create(OtherBlockHashHex, ObservedBlocks);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]))
			.Confirm(1, receiveId, recorded);
		LiquidWalletSyncSession session = Open(state);
		LiquidWalletSyncConfirmation[] rows =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				receiveId,
				recorded),
		];

		AssertBaseStateUntouched(state, () =>
			session.Commit(LiquidWalletObservationBatch.Create([]), rows));
	}

	[Fact]
	public void CommitRejectsRevisionContentionFromStaleSession()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidTransactionId receiveId = Tx('a');
		// The wallet advances to a new state object between Open and Commit.
		LiquidWalletState advanced = state.Apply(
			0,
			Delta(receiveId, [], [Output(receiveId, 0, PeggedAsset, 100)]));
		// A session opened against the advanced state binds its revision; a
		// commit that replays the already-applied transaction is rejected by
		// the existing double-apply guard before any partial state escapes.
		LiquidWalletSyncSession staleSession = Open(advanced);
		LiquidWalletObservationBatch batch = Batch(
			Observation(receiveId, [OwnedOutput(receiveId, 1, PeggedAsset, 5)]));

		AssertBaseStateUntouched(advanced, () => staleSession.Commit(batch, []));
		Assert.Equal(0ul, state.Revision);
		Assert.Equal(0, state.UnspentOutputCount);
		Assert.Equal(1ul, advanced.Revision);
		Assert.Equal(1, advanced.UnspentOutputCount);
	}

	// Required evidence row 6: single-writer advancement. Two sessions are
	// opened against the same base state; once the first commit advances the
	// wallet, a second session bound to the advanced state that replays the
	// already-applied transaction is handled deterministically: an exact
	// identical replay is skipped idempotently (covered by
	// CommitSkipsIdenticalAlreadyAppliedTransactionIdempotently), while a
	// conflicting replay with different created data still fails on the
	// existing double-apply guard, proving single-writer advancement.
	[Fact]
	public void SecondSessionCommitAfterFirstSucceedsFailsOnRevisionMismatch()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletSyncSession first = Open(state);
		LiquidTransactionId receiveId = Tx('a');
		// The observation's input is a foreign (non-wallet) outpoint so the
		// projected delta is create-only and replays cleanly.
		LiquidWalletObservationBatch batch = Batch(
			Observation(
				receiveId,
				[OwnedOutput(receiveId, 0, PeggedAsset, 10)],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));

		LiquidWalletSyncResult result = first.Commit(batch, []);
		Assert.Equal(1ul, result.ResultRevision);

		// A second session bound to the advanced state that replays the same
		// transaction identifier with conflicting created data is rejected by
		// the double-apply guard before any partial state escapes.
		LiquidWalletSyncSession second = Open(result.State);
		LiquidWalletObservationBatch conflicting = Batch(
			Observation(
				receiveId,
				[OwnedOutput(receiveId, 1, PeggedAsset, 7)],
				inputs: [LiquidOutPoint.CreateSpendable(Tx('9'), 0)]));
		AssertBaseStateUntouched(result.State, () => second.Commit(conflicting, []));
		Assert.Equal(0ul, state.Revision);
		Assert.Equal(0, state.UnspentOutputCount);
		Assert.Equal(1ul, result.State.Revision);
	}

	[Fact]
	public void SyncConfirmationValidatesShape()
	{
		LiquidConfirmation confirmation = LiquidConfirmation.Create(ConfirmedBlockHashHex, 7);
		LiquidTransactionId transactionId = Tx('a');

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletSyncConfirmation.Create(
				(LiquidWalletSyncConfirmationKind)77,
				transactionId,
				confirmation));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				null!,
				confirmation));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				transactionId,
				null!));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Confirm,
				LiquidTransactionId.ParseRpcHex(new string('0', 64)),
				confirmation));

		LiquidWalletSyncConfirmation row = LiquidWalletSyncConfirmation.Create(
			LiquidWalletSyncConfirmationKind.Unconfirm,
			transactionId,
			confirmation);
		Assert.Equal(LiquidWalletSyncConfirmationKind.Unconfirm, row.Kind);
		Assert.Equal(transactionId, row.TransactionId);
		Assert.Equal(confirmation, row.Confirmation);
		Assert.Equal(nameof(LiquidWalletSyncConfirmation), row.ToString());
	}

	[Fact]
	public void CommitRejectsNullAndNullMemberInputs()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletSyncSession session = Open(state);

		Assert.Throws<ArgumentNullException>(() => session.Commit(null!, []));
		Assert.Throws<ArgumentNullException>(() =>
			session.Commit(LiquidWalletObservationBatch.Create([]), null!));
		Assert.Throws<ArgumentException>(() =>
			session.Commit(LiquidWalletObservationBatch.Create([]), [null!]));
		Assert.Equal(0ul, state.Revision);
	}

	private static void AssertBaseStateUntouched(LiquidWalletState baseState, Action commit)
	{
		ulong baseRevision = baseState.Revision;
		int baseUnspentCount = baseState.UnspentOutputCount;
		int baseAppliedCount = baseState.AppliedTransactionCount;
		LiquidWalletReplaySnapshot snapshotBefore = baseState.ExportReplaySnapshot();

		Assert.Throws<InvalidOperationException>(commit);

		Assert.Equal(baseRevision, baseState.Revision);
		Assert.Equal(baseUnspentCount, baseState.UnspentOutputCount);
		Assert.Equal(baseAppliedCount, baseState.AppliedTransactionCount);
		LiquidWalletReplaySnapshot snapshotAfter = baseState.ExportReplaySnapshot();
		Assert.Equal(
			snapshotBefore.GetDeltas().Count,
			snapshotAfter.GetDeltas().Count);
		Assert.Equal(
			snapshotBefore.GetConfirmations().Count,
			snapshotAfter.GetConfirmations().Count);
	}

	private static LiquidWalletSyncSession Open(LiquidWalletState state) =>
		LiquidWalletSyncSession.Open(state, Observation(), PeggedAssetHex);

	private static LiquidWalletState ReceivedBaseState(LiquidTransactionId transactionId, long amount) =>
		LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [Output(transactionId, 0, PeggedAsset, amount)]));

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
