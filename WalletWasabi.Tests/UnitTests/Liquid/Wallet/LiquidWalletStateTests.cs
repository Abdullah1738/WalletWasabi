using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletStateTests
{
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
}
