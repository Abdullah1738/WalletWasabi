using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletStateTests
{
#if DEBUG
	private const string ExpectedBalanceQueryGraphManifestSha256 =
		"c80bc7bd948f7f8b4626d68864bea7ac133dffb19ca98a38d06fe1da416a88a5";
#else
	private const string ExpectedBalanceQueryGraphManifestSha256 =
		"93f8974db98e3b4c52a00b4b646ea412b55b70c6fc8efcb7def7d87bdb4b2019";
#endif

	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherPeggedAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlockHash = "4444444444444444444444444444444444444444444444444444444444444444";
	private const string ReplacementBlockHash = "5555555555555555555555555555555555555555555555555555555555555555";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);

	[Fact]
	public void AppliesIndependentMultiassetReceiveAndSpendAccounting()
	{
		LiquidWalletState empty = LiquidWalletState.Empty(PeggedAsset);
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput lbtc = Output(receiveId, 0, PeggedAsset, 100);
		LiquidOwnedOutput usdt = Output(receiveId, 1, IssuedAsset, 200);
		LiquidWalletState received = empty.Apply(0, Delta(receiveId, [], [lbtc, usdt]));

		Assert.Equal(100, received.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits);
		Assert.Equal(200, received.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.Equal(2, received.UnspentOutputCount);

		LiquidTransactionId usdtSpendId = Tx('b');
		LiquidOwnedOutput usdtChange = Output(usdtSpendId, 0, IssuedAsset, 150);
		LiquidWalletState afterUsdtSpend = received.Apply(
			1,
			Delta(usdtSpendId, [usdt.OutPoint], [usdtChange]));

		Assert.Equal(100, afterUsdtSpend.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits);
		Assert.Equal(150, afterUsdtSpend.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);

		LiquidTransactionId lbtcSpendId = Tx('c');
		LiquidOwnedOutput lbtcChange = Output(lbtcSpendId, 0, PeggedAsset, 90);
		LiquidWalletState afterLbtcSpend = afterUsdtSpend.Apply(
			2,
			Delta(lbtcSpendId, [lbtc.OutPoint], [lbtcChange]));

		Assert.Equal(90, afterLbtcSpend.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits);
		Assert.Equal(150, afterLbtcSpend.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.True(empty.GetBalances().IsEmpty);
		Assert.Equal(2, received.UnspentOutputCount);
	}

	[Fact]
	public void RollsBackDependentTransactionsInExactReverseOrder()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput receivedOutput = Output(receiveId, 0, PeggedAsset, 100);
		LiquidWalletState received = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [receivedOutput]));

		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput change = Output(spendId, 0, PeggedAsset, 90);
		LiquidWalletState spent = received.Apply(1, Delta(spendId, [receivedOutput.OutPoint], [change]));

		Assert.Throws<InvalidOperationException>(() => spent.RollbackLast(2, receiveId));
		LiquidWalletState restoredReceive = spent.RollbackLast(2, spendId);
		LiquidWalletState restoredEmpty = restoredReceive.RollbackLast(3, receiveId);

		Assert.Equal(3ul, restoredReceive.Revision);
		Assert.Equal(received.GetBalances().GetAmounts(), restoredReceive.GetBalances().GetAmounts());
		Assert.Equal(received.GetUnspentOutputs(), restoredReceive.GetUnspentOutputs());
		Assert.True(restoredEmpty.GetBalances().IsEmpty);
		Assert.Empty(restoredEmpty.GetUnspentOutputs());
		Assert.Equal(4ul, restoredEmpty.Revision);
		Assert.Equal(0, restoredEmpty.AppliedTransactionCount);
	}

	[Fact]
	public void ConfirmsWithoutRewritingOwnedOutputFacts()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput output = Output(receiveId, 0, PeggedAsset, 100);
		LiquidWalletState received = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [output]));
		IReadOnlyList<LiquidOwnedOutput> factsBefore = received.GetUnspentOutputs();
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);

		LiquidWalletState confirmed = received.Confirm(1, receiveId, confirmation);

		Assert.False(received.TryGetConfirmation(receiveId, out _));
		Assert.True(confirmed.TryGetConfirmation(receiveId, out LiquidConfirmation? observed));
		Assert.Equal(confirmation, observed);
		Assert.Equal(factsBefore, confirmed.GetUnspentOutputs());
		Assert.Equal(received.GetBalances().GetAmounts(), confirmed.GetBalances().GetAmounts());
		Assert.Throws<InvalidOperationException>(() => confirmed.Confirm(2, receiveId, confirmation));

		LiquidWalletState rolledBack = confirmed.RollbackLast(2, receiveId);
		Assert.False(rolledBack.TryGetConfirmation(receiveId, out _));
		Assert.True(rolledBack.GetBalances().IsEmpty);
	}

	[Fact]
	public void UnconfirmsAndReconfirmsEarlierTransactionWithoutRewritingWalletFacts()
	{
		LiquidTransactionId firstId = Tx('a');
		LiquidOwnedOutput firstOutput = Output(firstId, 0, PeggedAsset, 100);
		LiquidConfirmation firstConfirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState confirmed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(firstId, [], [firstOutput]))
			.Confirm(1, firstId, firstConfirmation);

		LiquidTransactionId laterId = Tx('b');
		LiquidOwnedOutput laterOutput = Output(laterId, 0, IssuedAsset, 200);
		LiquidWalletState withLaterTransaction = confirmed.Apply(
			2,
			Delta(laterId, [], [laterOutput]));
		IReadOnlyList<LiquidOwnedOutput> expectedOutputs = withLaterTransaction.GetUnspentOutputs();
		IReadOnlyList<LiquidAssetAmount> expectedBalances = withLaterTransaction.GetBalances().GetAmounts();

		LiquidConfirmation staleExpectation = LiquidConfirmation.Create(ReplacementBlockHash, 43);
		Assert.Throws<InvalidOperationException>(() =>
			withLaterTransaction.Unconfirm(3, firstId, staleExpectation));
		LiquidWalletState unconfirmed = withLaterTransaction.Unconfirm(3, firstId, firstConfirmation);

		Assert.False(unconfirmed.TryGetConfirmation(firstId, out _));
		Assert.Equal(expectedOutputs, unconfirmed.GetUnspentOutputs());
		Assert.Equal(expectedBalances, unconfirmed.GetBalances().GetAmounts());
		Assert.Equal(2, unconfirmed.AppliedTransactionCount);
		Assert.Equal(4ul, unconfirmed.Revision);

		LiquidWalletState reconfirmed = unconfirmed.Confirm(4, firstId, staleExpectation);

		Assert.True(reconfirmed.TryGetConfirmation(firstId, out LiquidConfirmation? observed));
		Assert.Equal(staleExpectation, observed);
		Assert.Equal(expectedOutputs, reconfirmed.GetUnspentOutputs());
		Assert.Equal(expectedBalances, reconfirmed.GetBalances().GetAmounts());
		Assert.Equal(5ul, reconfirmed.Revision);
	}

	[Fact]
	public void RejectsUnknownSpendReplayAndRevisionMismatchWithoutMutation()
	{
		LiquidWalletState empty = LiquidWalletState.Empty(PeggedAsset);
		LiquidTransactionId unknownSpendId = Tx('b');
		LiquidOutPoint unknown = LiquidOutPoint.CreateSpendable(Tx('a'), 0);
		LiquidWalletTransactionDelta unknownSpend = Delta(unknownSpendId, [unknown], []);

		Assert.Throws<InvalidOperationException>(() => empty.Apply(0, unknownSpend));
		Assert.Throws<InvalidOperationException>(() => empty.Apply(1, unknownSpend));
		Assert.Equal(0ul, empty.Revision);
		Assert.True(empty.GetBalances().IsEmpty);

		LiquidTransactionId receiveId = Tx('c');
		LiquidWalletTransactionDelta receive = Delta(
			receiveId,
			[],
			[Output(receiveId, 0, PeggedAsset, 1)]);
		LiquidWalletState received = empty.Apply(0, receive);

		Assert.Throws<InvalidOperationException>(() => received.Apply(1, receive));
		Assert.Equal(1ul, received.Revision);
		Assert.Single(received.GetUnspentOutputs());
	}

	[Fact]
	public void RejectsForeignContextAndBalanceOverflowAtomically()
	{
		LiquidWalletState empty = LiquidWalletState.Empty(PeggedAsset);
		LiquidTransactionId foreignId = Tx('a');
		LiquidAssetAmount foreignAmount = LiquidAssetAmount.Create(
			IssuedAsset,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			1);
		LiquidOwnedOutput foreignOutput = Output(foreignId, 0, foreignAmount);

		Assert.Throws<InvalidOperationException>(() =>
			empty.Apply(0, Delta(foreignId, [], [foreignOutput])));

		LiquidTransactionId overflowId = Tx('b');
		LiquidWalletTransactionDelta overflow = Delta(
			overflowId,
			[],
			[
				Output(overflowId, 0, IssuedAsset, long.MaxValue),
				Output(overflowId, 1, IssuedAsset, 1),
			]);

		Assert.Throws<OverflowException>(() => empty.Apply(0, overflow));
		Assert.Equal(0ul, empty.Revision);
		Assert.Empty(empty.GetUnspentOutputs());
		Assert.True(empty.GetBalances().IsEmpty);
	}

	[Fact]
	public void DeltaRejectsDuplicateEmptyAndMismatchedShapes()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidOwnedOutput output = Output(transactionId, 0, PeggedAsset, 1);
		LiquidOutPoint spent = LiquidOutPoint.CreateSpendable(Tx('b'), 0);

		Assert.Throws<ArgumentException>(() => Delta(transactionId, [], []));
		Assert.Throws<ArgumentException>(() => Delta(transactionId, [spent, spent], []));
		Assert.Throws<ArgumentException>(() => Delta(transactionId, [], [output, output]));
		Assert.Throws<ArgumentException>(() => Delta(transactionId, [], [Output(Tx('c'), 0, PeggedAsset, 1)]));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletTransactionDelta.Create(LiquidTransactionId.ParseRpcHex(new string('0', 64)), [], [output]));
	}

	[Fact]
	public void OwnedOutputRequiresPositiveAmountAndMatchingP2wpkhScript()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(transactionId, 0);
		LiquidSpendKeyReference key = ExternalKey;
		byte[] script = key.GetScriptPubKey();
		byte[] mismatched = [.. script];
		mismatched[^1] ^= 1;

		Assert.Throws<ArgumentOutOfRangeException>(() => LiquidOwnedOutput.Create(
			outPoint,
			script,
			LiquidAssetAmount.Zero(PeggedAsset, PeggedAsset),
			key));
		Assert.Throws<ArgumentException>(() => LiquidOwnedOutput.Create(
			outPoint,
			mismatched,
			LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 1),
			key));
	}

	[Fact]
	public void KeyAndOwnedOutputDoNotRetainCallerBuffers()
	{
		byte[] publicKey = Convert.FromHexString(PublicKeyHex);
		LiquidSpendKeyReference key = LiquidSpendKeyReference.Create(publicKey, LiquidKeyBranch.External, 7);
		publicKey.AsSpan().Clear();
		byte[] script = key.GetScriptPubKey();
		LiquidTransactionId transactionId = Tx('a');
		LiquidOwnedOutput output = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, 0),
			script,
			LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 1),
			key);
		script.AsSpan().Clear();

		Assert.Equal(Convert.FromHexString(PublicKeyHex), key.GetCompressedPublicKey());
		Assert.True(key.MatchesScriptPubKey(output.GetScriptPubKey()));
		byte[] exportedScript = output.GetScriptPubKey();
		exportedScript.AsSpan().Clear();
		Assert.True(key.MatchesScriptPubKey(output.GetScriptPubKey()));
	}

	[Fact]
	public void RejectsInvalidPublicKeysAndBranches()
	{
		byte[] valid = Convert.FromHexString(PublicKeyHex);
		byte[] invalidPoint = new byte[33];
		invalidPoint[0] = 0x02;

		Assert.Throws<ArgumentException>(() => LiquidSpendKeyReference.Create([], LiquidKeyBranch.External, 0));
		Assert.Throws<ArgumentException>(() => LiquidSpendKeyReference.Create(new byte[65], LiquidKeyBranch.External, 0));
		Assert.Throws<ArgumentException>(() => LiquidSpendKeyReference.Create(invalidPoint, LiquidKeyBranch.External, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => LiquidSpendKeyReference.Create(valid, (LiquidKeyBranch)2, 0));
		LiquidSpendKeyReference maximum = LiquidSpendKeyReference.Create(
			valid,
			LiquidKeyBranch.External,
			LiquidSpendKeyReference.MaximumIndex);
		Assert.Equal(LiquidSpendKeyReference.MaximumIndex, maximum.Index);
		Assert.Equal(LiquidOwnedOutputObservation.MaxDerivationIndex, LiquidSpendKeyReference.MaximumIndex);
		Assert.Throws<ArgumentOutOfRangeException>(() => LiquidSpendKeyReference.Create(
			valid,
			LiquidKeyBranch.External,
			LiquidSpendKeyReference.MaximumIndex + 1));
	}

	[Fact]
	public void ReturnsReadOnlyDeterministicSnapshots()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidOwnedOutput second = Output(transactionId, 1, IssuedAsset, 2);
		LiquidOwnedOutput first = Output(transactionId, 0, PeggedAsset, 1);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [second, first]));

		IReadOnlyList<LiquidOwnedOutput> snapshot = state.GetUnspentOutputs();
		var mutableView = Assert.IsAssignableFrom<IList<LiquidOwnedOutput>>(snapshot);

		Assert.Equal([first, second], snapshot);
		Assert.True(mutableView.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableView.Add(first));
		Assert.Equal(2, state.UnspentOutputCount);
	}

	[Fact]
	public void ExactAssetBalanceQueryReturnsFreshContextualResultsForHitsAndMisses()
	{
		LiquidAssetId peggedQuery = PeggedAsset;
		LiquidAssetId opaqueQuery = IssuedAsset;
		LiquidWalletState empty = LiquidWalletState.Empty(PeggedAsset);
		LiquidAssetAmount firstMiss = empty.QueryAssetBalance(0, opaqueQuery);
		LiquidAssetAmount secondMiss = empty.QueryAssetBalance(0, opaqueQuery);
		AssertFreshBalance(firstMiss, opaqueQuery, empty.PeggedAssetId, 0);
		AssertFreshBalance(secondMiss, opaqueQuery, empty.PeggedAssetId, 0);
		AssertIndependentResults(firstMiss, secondMiss);
		AssertFreshBalance(empty.QueryAssetBalance(0, peggedQuery), peggedQuery, empty.PeggedAssetId, 0);

		LiquidTransactionId transactionId = Tx('a');
		LiquidOwnedOutput peggedOutput = Output(transactionId, 0, peggedQuery, 81_234_567);
		LiquidOwnedOutput opaqueOutput = Output(transactionId, 1, opaqueQuery, 92_345_678);
		LiquidWalletState received = empty.Apply(
			0,
			Delta(transactionId, [], [peggedOutput, opaqueOutput]));

		LiquidAssetAmount peggedHit = received.QueryAssetBalance(1, peggedQuery);
		LiquidAssetAmount opaqueHit = received.QueryAssetBalance(1, opaqueQuery);
		AssertFreshBalance(peggedHit, peggedQuery, received.PeggedAssetId, 81_234_567);
		AssertFreshBalance(opaqueHit, opaqueQuery, received.PeggedAssetId, 92_345_678);
		Assert.NotSame(peggedOutput.Amount, peggedHit);
		Assert.NotSame(peggedOutput.Amount.AssetId, peggedHit.AssetId);
		Assert.NotSame(peggedOutput.Amount.PeggedAssetId, peggedHit.PeggedAssetId);
		Assert.NotSame(opaqueOutput.Amount, opaqueHit);
		Assert.NotSame(opaqueOutput.Amount.AssetId, opaqueHit.AssetId);
		Assert.NotSame(opaqueOutput.Amount.PeggedAssetId, opaqueHit.PeggedAssetId);
	}

	[Fact]
	public void ExactAssetBalanceQueryTracksTransitionsWithoutAliasingHistory()
	{
		LiquidAssetId queryAsset = IssuedAsset;
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput receivedOutput = Output(receiveId, 0, queryAsset, 123_456_789);
		LiquidWalletState received = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [receivedOutput]));
		LiquidAssetAmount beforeConfirmation = received.QueryAssetBalance(1, queryAsset);

		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState confirmed = received.Confirm(1, receiveId, confirmation);
		LiquidAssetAmount afterConfirmation = confirmed.QueryAssetBalance(2, queryAsset);
		AssertEqualIndependentResults(beforeConfirmation, afterConfirmation);

		LiquidWalletState unconfirmed = confirmed.Unconfirm(2, receiveId, confirmation);
		LiquidAssetAmount afterUnconfirmation = unconfirmed.QueryAssetBalance(3, queryAsset);
		AssertEqualIndependentResults(afterConfirmation, afterUnconfirmation);

		LiquidTransactionId unrelatedId = Tx('b');
		LiquidOwnedOutput unrelatedOutput = Output(unrelatedId, 0, PeggedAsset, 7_654_321);
		LiquidWalletState unrelated = unconfirmed.Apply(
			3,
			Delta(unrelatedId, [], [unrelatedOutput]));
		LiquidAssetAmount afterUnrelated = unrelated.QueryAssetBalance(4, queryAsset);
		AssertEqualIndependentResults(afterUnconfirmation, afterUnrelated);

		LiquidTransactionId zeroNetId = Tx('c');
		LiquidOwnedOutput replacement = Output(zeroNetId, 0, queryAsset, 123_456_789);
		LiquidWalletState zeroNet = unrelated.Apply(
			4,
			Delta(zeroNetId, [receivedOutput.OutPoint], [replacement]));
		LiquidAssetAmount afterZeroNet = zeroNet.QueryAssetBalance(5, queryAsset);
		AssertEqualIndependentResults(afterUnrelated, afterZeroNet);

		LiquidWalletState rolledBack = zeroNet.RollbackLast(5, zeroNetId);
		LiquidAssetAmount afterRollback = rolledBack.QueryAssetBalance(6, queryAsset);
		AssertEqualIndependentResults(afterZeroNet, afterRollback);
		LiquidAssetAmount unrelatedBeforeSpend = rolledBack.QueryAssetBalance(6, PeggedAsset);
		AssertFreshBalance(
			unrelatedBeforeSpend,
			PeggedAsset,
			rolledBack.PeggedAssetId,
			7_654_321);

		LiquidTransactionId spendId = Tx('d');
		LiquidOwnedOutput change = Output(spendId, 0, queryAsset, 100_000_000);
		LiquidWalletState spent = rolledBack.Apply(
			6,
			Delta(spendId, [receivedOutput.OutPoint], [change]));
		LiquidAssetAmount afterSpend = spent.QueryAssetBalance(7, queryAsset);
		AssertFreshBalance(afterSpend, queryAsset, spent.PeggedAssetId, 100_000_000);
		LiquidAssetAmount unrelatedAfterSpend = spent.QueryAssetBalance(7, PeggedAsset);
		AssertEqualIndependentResults(unrelatedBeforeSpend, unrelatedAfterSpend);
		LiquidAssetAmount priorStateAfterSpend = rolledBack.QueryAssetBalance(6, queryAsset);
		AssertEqualIndependentResults(afterRollback, priorStateAfterSpend);
		LiquidAssetAmount priorUnrelatedAfterSpend = rolledBack.QueryAssetBalance(6, PeggedAsset);
		AssertEqualIndependentResults(unrelatedBeforeSpend, priorUnrelatedAfterSpend);
		Assert.Equal(123_456_789, beforeConfirmation.AtomicUnits);
		Assert.Equal(123_456_789, afterRollback.AtomicUnits);
	}

	[Fact]
	public void ExactAssetBalanceQuerySurvivesReplayAndProtectedReplay()
	{
		LiquidAssetId queryAsset = IssuedAsset;
		LiquidAssetId missingAsset = LiquidAssetId.ParseRpcHex(
			"6666666666666666666666666666666666666666666666666666666666666666");
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput output = Output(receiveId, 0, queryAsset, 234_567_891);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [output]));
		LiquidWalletState replayRestored = LiquidWalletState.RestoreReplaySnapshot(
			state.ExportReplaySnapshot());
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(
			LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(
					state.ExportReplaySnapshot(),
					73,
					key,
					context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletReplayOpenResult opened = protectedPayload.Open(key, context);
			LiquidWalletState protectedRestored = LiquidWalletState.RestoreReplaySnapshot(
				opened.Snapshot);

			Assert.Equal(73ul, opened.Generation);
			foreach (LiquidAssetId assetId in new[] { queryAsset, missingAsset })
			{
				LiquidAssetAmount expected = state.QueryAssetBalance(state.Revision, assetId);
				LiquidAssetAmount replayed = replayRestored.QueryAssetBalance(
					replayRestored.Revision,
					assetId);
				LiquidAssetAmount protectedReplayed = protectedRestored.QueryAssetBalance(
					protectedRestored.Revision,
					assetId);
				AssertEqualIndependentResults(expected, replayed);
				AssertEqualIndependentResults(expected, protectedReplayed);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelope is not null)
			{
				CryptographicOperations.ZeroMemory(envelope);
			}
		}
	}

	[Fact]
	public void ExactAssetBalanceQueryFindsFirstMiddleLastAndMissAcrossManyAssets()
	{
		const int AssetCount = 1_500;
		LiquidTransactionId transactionId = Tx('e');
		var outputs = new LiquidOwnedOutput[AssetCount];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] = Output(
				transactionId,
				(uint)index,
				Asset((uint)index + 1),
				index + 1);
		}
		LiquidWalletState state = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				1,
				[Delta(transactionId, [], outputs)],
				[]));

		foreach (int index in new[] { 0, AssetCount / 2, AssetCount - 1 })
		{
			LiquidAssetId assetId = Asset((uint)index + 1);
			AssertFreshBalance(
				state.QueryAssetBalance(1, assetId),
				assetId,
				state.PeggedAssetId,
				index + 1);
		}
		LiquidAssetId missing = Asset((uint)AssetCount + 1);
		AssertFreshBalance(
			state.QueryAssetBalance(1, missing),
			missing,
			state.PeggedAssetId,
			0);
	}

	[Fact]
	public void ExactAssetBalanceQueryValidatesRevisionFirstAndRedactsFailures()
	{
		LiquidAssetId canaryAsset = LiquidAssetId.ParseRpcHex(
			"7392517392517392517392517392517392517392517392517392517392517392");
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		InvalidOperationException staleFailure;
		try
		{
			state.QueryAssetBalance(1, canaryAsset);
			throw new Xunit.Sdk.XunitException("The stale balance query unexpectedly succeeded.");
		}
		catch (Exception exception)
		{
			staleFailure = Assert.IsType<InvalidOperationException>(exception);
		}
		Assert.Equal(
			"The Liquid wallet state revision changed before the requested transition.",
			staleFailure.Message);
		AssertPrivateFailure(staleFailure, canaryAsset);

		ArgumentNullException nullFailure;
		try
		{
			state.QueryAssetBalance(0, null!);
			throw new Xunit.Sdk.XunitException("The null balance query unexpectedly succeeded.");
		}
		catch (Exception exception)
		{
			nullFailure = Assert.IsType<ArgumentNullException>(exception);
		}
		Assert.Equal("assetId", nullFailure.ParamName);
		AssertPrivateFailure(nullFailure, canaryAsset);
	}

	[Fact]
	public void ExactAssetBalanceQueryHasClosedOneLookupDataflow()
	{
		MethodInfo query = RequiredMethod(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.QueryAssetBalance),
			typeof(ulong),
			typeof(LiquidAssetId));
		MethodInfo ensureRevision = RequiredMethod(
			typeof(LiquidWalletState),
			"EnsureRevision",
			typeof(ulong));
		MethodInfo revisionGetter = RequiredPropertyGetter(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.Revision));
		MethodInfo peggedAssetGetter = RequiredPropertyGetter(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.PeggedAssetId));
		MethodInfo getAmountOrZero = RequiredMethod(
			typeof(LiquidAssetBalanceMap),
			nameof(LiquidAssetBalanceMap.GetAmountOrZero),
			typeof(LiquidAssetId));
		MethodInfo toConsensusBytes = RequiredMethod(
			typeof(LiquidAssetId),
			nameof(LiquidAssetId.ToConsensusBytes));
		MethodInfo parseConsensusBytes = RequiredMethod(
			typeof(LiquidAssetId),
			nameof(LiquidAssetId.ParseConsensusBytes),
			typeof(ReadOnlySpan<byte>),
			typeof(string));
		MethodInfo byteArrayToReadOnlySpan = RequiredMethod(
			typeof(ReadOnlySpan<byte>),
			"op_Implicit",
			typeof(byte[]));
		MethodInfo atomicUnitsGetter = RequiredPropertyGetter(
			typeof(LiquidAssetAmount),
			nameof(LiquidAssetAmount.AtomicUnits));
		MethodInfo createAmount = RequiredMethod(
			typeof(LiquidAssetAmount),
			nameof(LiquidAssetAmount.Create),
			typeof(LiquidAssetId),
			typeof(LiquidAssetId),
			typeof(long));

		Assert.Equal(
			new MethodBase[]
			{
				ensureRevision,
				getAmountOrZero,
				toConsensusBytes,
				byteArrayToReadOnlySpan,
				parseConsensusBytes,
				peggedAssetGetter,
				toConsensusBytes,
				byteArrayToReadOnlySpan,
				parseConsensusBytes,
				atomicUnitsGetter,
				createAmount,
			},
			GetReferencedMethods(query));
		Assert.Equal(
			[RequiredField(typeof(LiquidWalletState), "_balances")],
			GetReferencedFields(query));
		Assert.Equal(
			new MethodBase[]
			{
				revisionGetter,
				typeof(InvalidOperationException).GetConstructor([typeof(string)])!,
			},
			GetReferencedMethods(ensureRevision));
		Assert.Equal(
			[RequiredField(typeof(LiquidWalletState), "<Revision>k__BackingField")],
			GetReferencedFields(revisionGetter));
		Assert.Empty(GetReferencedMethods(revisionGetter));
		Assert.Equal(
			ExpectedBalanceQueryGraphManifestSha256,
			Sha256Utf8(BuildClosedGraphManifest(query, ensureRevision, revisionGetter)));

		var queryInstructions = GetIlInstructions(query).ToArray();
		Assert.DoesNotContain(queryInstructions, instruction => IsConditionalControlTransfer(instruction.OpCode));
		Assert.DoesNotContain(queryInstructions, instruction => IsForbiddenQueryInstruction(instruction.OpCode));
		Assert.Equal(1, queryInstructions.Count(instruction => instruction.OpCode == OpCodes.Ret));
		foreach (var instruction in queryInstructions.Where(
			instruction => instruction.OpCode == OpCodes.Br || instruction.OpCode == OpCodes.Br_S))
		{
			Assert.Equal(instruction.NextOffset, instruction.BranchTarget);
		}
#if DEBUG
		Assert.Equal(
			new[]
			{
				typeof(LiquidAssetAmount),
				typeof(LiquidAssetId),
				typeof(LiquidAssetId),
				typeof(LiquidAssetAmount),
			},
			query.GetMethodBody()!.LocalVariables.Select(local => local.LocalType));
#else
		Assert.Equal(
			new[] { typeof(LiquidAssetAmount), typeof(LiquidAssetId) },
			query.GetMethodBody()!.LocalVariables.Select(local => local.LocalType));
#endif

		var ensureInstructions = GetIlInstructions(ensureRevision).ToArray();
		var ensureBranch = Assert.Single(
			ensureInstructions,
			instruction => IsConditionalControlTransfer(instruction.OpCode));
		var ensureReturn = Assert.Single(
			ensureInstructions,
			instruction => instruction.OpCode == OpCodes.Ret);
		var ensureThrow = Assert.Single(
			ensureInstructions,
			instruction => instruction.OpCode == OpCodes.Throw);
		Assert.Equal(ensureReturn.Offset, ensureBranch.BranchTarget);
		Assert.True(ensureBranch.NextOffset < ensureThrow.Offset);
		Assert.True(ensureThrow.Offset < ensureReturn.Offset);
		Assert.DoesNotContain(
			ensureInstructions,
			instruction => instruction.OpCode == OpCodes.Switch ||
				instruction.OpCode == OpCodes.Calli ||
				instruction.OpCode == OpCodes.Localloc ||
				IsIndirectWrite(instruction.OpCode));
		Assert.Equal(
			new[] { OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ret },
			GetIlInstructions(revisionGetter).Select(instruction => instruction.OpCode));
		foreach (MethodBase method in new MethodBase[] { query, ensureRevision, revisionGetter })
		{
			Assert.DoesNotContain(
				GetIlInstructions(method),
				instruction => instruction.OpCode == OpCodes.Stfld ||
					instruction.OpCode == OpCodes.Stsfld);
			Assert.Empty(method.GetMethodBody()?.ExceptionHandlingClauses ?? []);
		}

		string[] expectedStateFields =
		[
			"<PeggedAssetId>k__BackingField",
			"<Revision>k__BackingField",
			"_appliedTransactionIds",
			"_balances",
			"_confirmations",
			"_history",
			"_knownOutputs",
			"_unspentOutputs",
		];
		Assert.Equal(
			expectedStateFields,
			typeof(LiquidWalletState)
				.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
				.Select(field => field.Name)
				.OrderBy(name => name, StringComparer.Ordinal));
		Assert.DoesNotContain(
			typeof(LiquidWalletState).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic),
			type => type.Name.Contains(
				nameof(LiquidWalletState.QueryAssetBalance),
				StringComparison.Ordinal));
	}

	[Fact]
	public void RedactsWalletFactsFromStringsAndErrors()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidOwnedOutput output = Output(transactionId, 0, IssuedAsset, 987_654_321);
		LiquidWalletTransactionDelta delta = Delta(transactionId, [], [output]);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset).Apply(0, delta);

		foreach (string text in new[]
		{
			transactionId.ToString(),
			output.OutPoint.ToString(),
			output.SpendKey.ToString(),
			output.ToString(),
			delta.ToString(),
			state.ToString(),
			LiquidConfirmation.Create(BlockHash, 42).ToString(),
		})
		{
			Assert.DoesNotContain(transactionId.CanonicalRpcHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(IssuedAssetHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(PublicKeyHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("987654321", text, StringComparison.Ordinal);
		}

		var exception = Assert.Throws<InvalidOperationException>(() => state.Apply(1, delta));
		Assert.DoesNotContain(transactionId.CanonicalRpcHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(IssuedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("987654321", exception.Message, StringComparison.Ordinal);
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidAssetId Asset(uint value) =>
		LiquidAssetId.ParseRpcHex(value.ToString("x64", CultureInfo.InvariantCulture));

	private static LiquidSpendKeyReference Key(LiquidKeyBranch branch, uint index) =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), branch, index);

	private static LiquidAssetAmount Amount(LiquidAssetId assetId, long atomicUnits) =>
		LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits);

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits) =>
		Output(transactionId, outputIndex, Amount(assetId, atomicUnits));

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetAmount amount)
	{
		LiquidSpendKeyReference key = ExternalKey;
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			key.GetScriptPubKey(),
			amount,
			key);
	}

	private static LiquidWalletTransactionDelta Delta(
		LiquidTransactionId transactionId,
		IEnumerable<LiquidOutPoint> spent,
		IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);

	private static void AssertFreshBalance(
		LiquidAssetAmount actual,
		LiquidAssetId queriedAsset,
		LiquidAssetId statePeggedAsset,
		long expectedAtomicUnits)
	{
		Assert.Equal(queriedAsset, actual.AssetId);
		Assert.Equal(statePeggedAsset, actual.PeggedAssetId);
		Assert.Equal(expectedAtomicUnits, actual.AtomicUnits);
		Assert.NotSame(queriedAsset, actual.AssetId);
		Assert.NotSame(statePeggedAsset, actual.PeggedAssetId);
		Assert.NotSame(actual.AssetId, actual.PeggedAssetId);
	}

	private static void AssertIndependentResults(
		LiquidAssetAmount first,
		LiquidAssetAmount second)
	{
		Assert.NotSame(first, second);
		Assert.NotSame(first.AssetId, second.AssetId);
		Assert.NotSame(first.PeggedAssetId, second.PeggedAssetId);
	}

	private static void AssertEqualIndependentResults(
		LiquidAssetAmount first,
		LiquidAssetAmount second)
	{
		Assert.Equal(first, second);
		AssertIndependentResults(first, second);
	}

	private static void AssertPrivateFailure(Exception failure, LiquidAssetId canaryAsset)
	{
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
		Assert.DoesNotContain(canaryAsset.CanonicalRpcHex, failure.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(canaryAsset.CanonicalRpcHex, failure.ToString(), StringComparison.Ordinal);
		PropertyInfo? actualValue = failure.GetType().GetProperty(
			"ActualValue",
			BindingFlags.Public | BindingFlags.Instance);
		if (actualValue is not null)
		{
			Assert.Null(actualValue.GetValue(failure));
		}

		for (Type? type = failure.GetType(); type is not null; type = type.BaseType)
		{
			foreach (FieldInfo field in type.GetFields(
				BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				Assert.False(ReferenceEquals(canaryAsset, field.GetValue(failure)));
			}
		}
		foreach (PropertyInfo property in failure.GetType().GetProperties(
			BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.GetMethod is not null && property.GetIndexParameters().Length == 0)
			{
				Assert.False(ReferenceEquals(canaryAsset, property.GetValue(failure)));
			}
		}
	}

	private static MethodInfo RequiredMethod(Type type, string name, params Type[] parameterTypes) =>
		type.GetMethod(
			name,
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly,
			binder: null,
			parameterTypes,
			modifiers: null) ?? throw new Xunit.Sdk.XunitException($"Missing method {type.FullName}.{name}.");

	private static MethodInfo RequiredPropertyGetter(Type type, string name) =>
		type.GetProperty(
			name,
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetMethod ??
		throw new Xunit.Sdk.XunitException($"Missing property getter {type.FullName}.{name}.");

	private static FieldInfo RequiredField(Type type, string name) =>
		type.GetField(
			name,
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly) ??
		throw new Xunit.Sdk.XunitException($"Missing field {type.FullName}.{name}.");

	private static IReadOnlyList<MethodBase> GetReferencedMethods(MethodBase method) =>
		GetIlInstructions(method)
			.Select(instruction => instruction.Member)
			.OfType<MethodBase>()
			.ToArray();

	private static IReadOnlyList<FieldInfo> GetReferencedFields(MethodBase method) =>
		GetIlInstructions(method)
			.Select(instruction => instruction.Member)
			.OfType<FieldInfo>()
			.ToArray();

	private static IReadOnlyList<(
		int Offset,
		int NextOffset,
		OpCode OpCode,
		MemberInfo? Member,
		string OperandIdentity,
		int? BranchTarget)> GetIlInstructions(
		MethodBase method)
	{
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		var instructions = new List<(
			int Offset,
			int NextOffset,
			OpCode OpCode,
			MemberInfo? Member,
			string OperandIdentity,
			int? BranchTarget)>();
		int offset = 0;
		while (offset < il.Length)
		{
			int instructionOffset = offset;
			OpCode opCode = ReadOpCode(il, ref offset);
			int operandOffset = offset;
			int operandSize = OperandSize(opCode.OperandType, il, operandOffset);
			MemberInfo? member = null;
			if (opCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
				OperandType.InlineTok or OperandType.InlineType)
			{
				int token = BitConverter.ToInt32(il, operandOffset);
				member = method.Module.ResolveMember(
					token,
					method.DeclaringType?.GetGenericArguments(),
					(method as MethodInfo)?.GetGenericArguments());
			}
			int nextOffset = operandOffset + operandSize;
			int? branchTarget = opCode.OperandType switch
			{
				OperandType.ShortInlineBrTarget => nextOffset + unchecked((sbyte)il[operandOffset]),
				OperandType.InlineBrTarget => nextOffset + BitConverter.ToInt32(il, operandOffset),
				_ => null,
			};
			instructions.Add((
				instructionOffset,
				nextOffset,
				opCode,
				member,
				OperandIdentity(method, opCode, il, operandOffset, operandSize, member, nextOffset),
				branchTarget));
			offset = nextOffset;
		}
		return instructions;
	}

	private static string BuildClosedGraphManifest(params MethodBase[] methods)
	{
		var rows = new List<string>();
		foreach (MethodBase method in methods)
		{
			MethodBody body = method.GetMethodBody() ??
				throw new Xunit.Sdk.XunitException($"Method {MethodIdentity(method)} has no body.");
			rows.Add($"METHOD|{MethodIdentity(method)}|{body.InitLocals}|{body.MaxStackSize}");
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				rows.Add($"LOCAL|{local.LocalIndex}|{TypeIdentity(local.LocalType)}|{local.IsPinned}");
			}
			foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
			{
				rows.Add(
					$"EH|{(int)clause.Flags}|{clause.TryOffset}|{clause.TryLength}|" +
					$"{clause.HandlerOffset}|{clause.HandlerLength}|{clause.FilterOffset}|" +
					TypeIdentity(clause.CatchType));
			}
			foreach (var instruction in GetIlInstructions(method))
			{
				rows.Add(
					$"IL|{instruction.Offset}|{instruction.NextOffset}|{instruction.OpCode.Value}|" +
					$"{instruction.OpCode.Name}|{instruction.OperandIdentity}");
			}
		}
		return string.Join('\n', rows) + "\n";
	}

	private static string OperandIdentity(
		MethodBase method,
		OpCode opCode,
		byte[] il,
		int operandOffset,
		int operandSize,
		MemberInfo? member,
		int nextOffset) => opCode.OperandType switch
		{
			OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or
				OperandType.InlineType => MemberIdentity(member ??
					throw new Xunit.Sdk.XunitException("An IL metadata token did not resolve.")),
			OperandType.InlineString => Convert.ToHexString(Encoding.UTF8.GetBytes(
				method.Module.ResolveString(BitConverter.ToInt32(il, operandOffset)))).ToLowerInvariant(),
			OperandType.InlineSig => Convert.ToHexString(
				method.Module.ResolveSignature(BitConverter.ToInt32(il, operandOffset))).ToLowerInvariant(),
			OperandType.ShortInlineBrTarget =>
				(nextOffset + unchecked((sbyte)il[operandOffset])).ToString(CultureInfo.InvariantCulture),
			OperandType.InlineBrTarget =>
				(nextOffset + BitConverter.ToInt32(il, operandOffset)).ToString(CultureInfo.InvariantCulture),
			OperandType.InlineSwitch => SwitchTargetIdentity(il, operandOffset, nextOffset),
			_ => Convert.ToHexString(il.AsSpan(operandOffset, operandSize)).ToLowerInvariant(),
		};

	private static string SwitchTargetIdentity(byte[] il, int operandOffset, int nextOffset)
	{
		int count = BitConverter.ToInt32(il, operandOffset);
		var targets = new string[count];
		for (int index = 0; index < count; index++)
		{
			targets[index] = (nextOffset + BitConverter.ToInt32(
				il,
				operandOffset + sizeof(int) + index * sizeof(int)))
				.ToString(CultureInfo.InvariantCulture);
		}
		return string.Join(",", targets);
	}

	private static string Sha256Utf8(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private static string MethodIdentity(MethodBase method)
	{
		string parameters = string.Join(",", method.GetParameters()
			.Select(parameter => TypeIdentity(parameter.ParameterType)));
		string returnType = method is MethodInfo info ? TypeIdentity(info.ReturnType) : "void";
		return $"{TypeIdentity(method.DeclaringType)}::{method.Name}({parameters})->{returnType}";
	}

	private static string MemberIdentity(MemberInfo member) => member switch
	{
		MethodBase method => MethodIdentity(method),
		FieldInfo field => $"{TypeIdentity(field.DeclaringType)}::{field.Name}:{TypeIdentity(field.FieldType)}",
		Type type => TypeIdentity(type),
		_ => $"{TypeIdentity(member.DeclaringType)}::{member.Name}",
	};

	private static string TypeIdentity(Type? type) => type?.FullName ?? "null";

	private static bool IsConditionalControlTransfer(OpCode opCode) =>
		opCode.FlowControl == FlowControl.Cond_Branch;

	private static bool IsForbiddenQueryInstruction(OpCode opCode) =>
		opCode == OpCodes.Calli || opCode == OpCodes.Localloc || opCode == OpCodes.Newarr ||
		opCode == OpCodes.Newobj || opCode == OpCodes.Box || opCode == OpCodes.Switch ||
		IsIndirectWrite(opCode);

	private static bool IsIndirectWrite(OpCode opCode) =>
		opCode == OpCodes.Stind_I || opCode == OpCodes.Stind_I1 || opCode == OpCodes.Stind_I2 ||
		opCode == OpCodes.Stind_I4 || opCode == OpCodes.Stind_I8 || opCode == OpCodes.Stind_R4 ||
		opCode == OpCodes.Stind_R8 || opCode == OpCodes.Stind_Ref || opCode == OpCodes.Stobj ||
		opCode == OpCodes.Cpobj || opCode == OpCodes.Initobj;

	private static OpCode ReadOpCode(byte[] il, ref int offset)
	{
		byte first = il[offset++];
		short value = first == 0xfe
			? unchecked((short)(0xfe00 | il[offset++]))
			: first;
		foreach (FieldInfo field in typeof(OpCodes).GetFields(
			BindingFlags.Public | BindingFlags.Static))
		{
			if (field.GetValue(null) is OpCode candidate && candidate.Value == value)
			{
				return candidate;
			}
		}
		throw new Xunit.Sdk.XunitException($"Unknown IL opcode 0x{(ushort)value:x4}.");
	}

	private static int OperandSize(OperandType operandType, byte[] il, int operandOffset) =>
		operandType switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
				OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
				OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
				OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, operandOffset),
			_ => throw new Xunit.Sdk.XunitException($"Unsupported operand type {operandType}.")
		};

}
