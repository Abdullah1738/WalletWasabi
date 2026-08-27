using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
	public void MultiassetBalanceQueryPreservesPositionsAndOwnsEveryDisclosure()
	{
		LiquidAssetId peggedQuery = PeggedAsset;
		LiquidAssetId issuedQuery = IssuedAsset;
		LiquidAssetId missingQuery = Asset(3);
		LiquidTransactionId transactionId = Tx('9');
		LiquidOwnedOutput peggedOutput = Output(transactionId, 0, peggedQuery, 12_345_678);
		LiquidOwnedOutput issuedOutput = Output(transactionId, 1, issuedQuery, 23_456_789);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset).Apply(
			0,
			Delta(transactionId, [], [peggedOutput, issuedOutput]));
		LiquidAssetId[] requested = [issuedQuery, missingQuery, peggedQuery, issuedQuery];

		LiquidWalletAssetBalanceQueryResult first = state.QueryAssetBalances(1, requested);
		LiquidWalletAssetBalanceQueryResult second = state.QueryAssetBalances(1, requested);

		Assert.Equal(4, first.Count);
		Assert.Equal(
			new[] { IssuedAssetHex, Asset(3).CanonicalRpcHex, PeggedAssetHex, IssuedAssetHex },
			first.Select(amount => amount.AssetId.CanonicalRpcHex));
		AssertFreshBalance(first[0], issuedQuery, state.PeggedAssetId, 23_456_789);
		AssertFreshBalance(first[1], missingQuery, state.PeggedAssetId, 0);
		AssertFreshBalance(first[2], peggedQuery, state.PeggedAssetId, 12_345_678);
		AssertFreshBalance(first[3], issuedQuery, state.PeggedAssetId, 23_456_789);
		AssertEqualIndependentResults(first[0], first[3]);
		Assert.NotSame(first, second);
		for (int index = 0; index < first.Count; index++)
		{
			AssertEqualIndependentResults(first[index], second[index]);
			Assert.NotSame(requested[index], first[index].AssetId);
		}

		Assert.NotSame(issuedOutput.Amount, first[0]);
		Assert.NotSame(issuedOutput.Amount.AssetId, first[0].AssetId);
		Assert.NotSame(peggedOutput.Amount, first[2]);
		Assert.NotSame(peggedOutput.Amount.PeggedAssetId, first[2].PeggedAssetId);
	}

	[Fact]
	public void MultiassetBalanceQueryUsesOneBoundedIndexedSnapshot()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		var one = new IndexedAssetList([IssuedAsset]);

		LiquidWalletAssetBalanceQueryResult accepted = state.QueryAssetBalances(0, one);

		Assert.Single(accepted);
		Assert.Equal(1, one.CountReads);
		Assert.Equal([0], one.IndexReads);
		Assert.Equal(0, one.EnumerationRequests);

		var maximum = new IndexedAssetList(
			Enumerable.Range(1, LiquidWalletState.MaximumQueriedAssetCount)
				.Select(index => Asset((uint)index))
				.ToArray());
		Assert.Equal(
			LiquidWalletState.MaximumQueriedAssetCount,
			state.QueryAssetBalances(0, maximum).Count);
		Assert.Equal(1, maximum.CountReads);
		Assert.Equal(Enumerable.Range(0, LiquidWalletState.MaximumQueriedAssetCount), maximum.IndexReads);
		Assert.Equal(0, maximum.EnumerationRequests);

		var empty = new IndexedAssetList([]);
		ArgumentOutOfRangeException emptyFailure = Assert.Throws<ArgumentOutOfRangeException>(() =>
			state.QueryAssetBalances(0, empty));
		Assert.Equal("assetIds", emptyFailure.ParamName);
		Assert.Null(emptyFailure.ActualValue);
		Assert.Equal(1, empty.CountReads);
		Assert.Empty(empty.IndexReads);

		var tooLarge = new IndexedAssetList(
			Enumerable.Repeat(
				IssuedAsset,
				LiquidWalletState.MaximumQueriedAssetCount + 1).ToArray());
		ArgumentOutOfRangeException largeFailure = Assert.Throws<ArgumentOutOfRangeException>(() =>
			state.QueryAssetBalances(0, tooLarge));
		Assert.Equal("assetIds", largeFailure.ParamName);
		Assert.Null(largeFailure.ActualValue);
		Assert.Equal(1, tooLarge.CountReads);
		Assert.Empty(tooLarge.IndexReads);
	}

	[Fact]
	public void MultiassetBalanceQueryValidatesRevisionBeforeInspectingRequest()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		const string HostileRenderingCanary = "private-stale-request-canary-482017";
		var hostile = new IndexedAssetList(
			[IssuedAsset],
			throwOnCount: true,
			renderingCanary: HostileRenderingCanary);

		InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
			state.QueryAssetBalances(1, hostile));

		Assert.Equal(
			"The Liquid wallet state revision changed before the requested transition.",
			failure.Message);
		Assert.Equal(0, hostile.CountReads);
		Assert.Empty(hostile.IndexReads);
		Assert.Equal(0, hostile.EnumerationRequests);
		AssertOwnedPrivateFailure(
			failure,
			"The Liquid wallet state revision changed before the requested transition.",
			[hostile, IssuedAsset],
			[HostileRenderingCanary]);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void MultiassetBalanceQueryRejectsNullMembersBeforeResults(int nullIndex)
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidAssetId[] values = [IssuedAsset, Asset(3), PeggedAsset];
		values[nullIndex] = null!;
		string indexCanary = $"private-null-index-{nullIndex}-canary";
		var request = new IndexedAssetList(values, renderingCanary: indexCanary);

		ArgumentException failure = Assert.Throws<ArgumentException>(() =>
			state.QueryAssetBalances(0, request));

		Assert.Equal("assetIds", failure.ParamName);
		Assert.Equal(1, request.CountReads);
		Assert.Equal([0, 1, 2], request.IndexReads);
		Assert.Equal(0, request.EnumerationRequests);
		AssertOwnedPrivateFailure(
			failure,
			"The Liquid asset balance query could not be accepted. (Parameter 'assetIds')",
			[request, .. values.Where(value => value is not null)],
			[indexCanary]);
	}

	[Fact]
	public void MultiassetBalanceQueryOwnedFailuresAreOpaqueAndRetainNoInputs()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		const string RequestRenderingCanary = "private-request-rendering-canary-804193";
		LiquidAssetId canary = LiquidAssetId.ParseRpcHex(
			"7392517392517392517392517392517392517392517392517392517392517392");

		ArgumentNullException nullRequest = Assert.Throws<ArgumentNullException>(() =>
			state.QueryAssetBalances(0, null!));
		Assert.Equal("assetIds", nullRequest.ParamName);
		AssertOwnedPrivateFailure(
			nullRequest,
			"Value cannot be null. (Parameter 'assetIds')",
			[],
			[RequestRenderingCanary]);

		const string EmptyCountCanary = "request-count-canary-zero";
		var empty = new IndexedAssetList(
			[],
			renderingCanary: $"{RequestRenderingCanary}|{EmptyCountCanary}");
		ArgumentOutOfRangeException emptyFailure = Assert.Throws<ArgumentOutOfRangeException>(() =>
			state.QueryAssetBalances(0, empty));
		Assert.Equal("assetIds", emptyFailure.ParamName);
		AssertOwnedPrivateFailure(
			emptyFailure,
			"The Liquid asset balance query could not be accepted. (Parameter 'assetIds')",
			[empty],
			[RequestRenderingCanary, EmptyCountCanary]);

		LiquidAssetId[] largeValues = Enumerable.Repeat(
			canary,
			LiquidWalletState.MaximumQueriedAssetCount + 1).ToArray();
		const string LargeCountCanary = "request-count-canary-257";
		var large = new IndexedAssetList(
			largeValues,
			renderingCanary: $"{RequestRenderingCanary}|{LargeCountCanary}");
		ArgumentOutOfRangeException largeFailure = Assert.Throws<ArgumentOutOfRangeException>(() =>
			state.QueryAssetBalances(0, large));
		Assert.Equal("assetIds", largeFailure.ParamName);
		AssertOwnedPrivateFailure(
			largeFailure,
			"The Liquid asset balance query could not be accepted. (Parameter 'assetIds')",
			[large, canary],
			[RequestRenderingCanary, LargeCountCanary]);

		LiquidAssetId[] nullValues = [canary, null!];
		const string NullIndexCanary = "request-index-canary-one";
		var nullMember = new IndexedAssetList(
			nullValues,
			renderingCanary: $"{RequestRenderingCanary}|{NullIndexCanary}");
		ArgumentException nullMemberFailure = Assert.Throws<ArgumentException>(() =>
			state.QueryAssetBalances(0, nullMember));
		Assert.Equal("assetIds", nullMemberFailure.ParamName);
		AssertOwnedPrivateFailure(
			nullMemberFailure,
			"The Liquid asset balance query could not be accepted. (Parameter 'assetIds')",
			[nullMember, canary],
			[RequestRenderingCanary, NullIndexCanary]);

		ArgumentNullException nullResult = Assert.Throws<ArgumentNullException>(() =>
			new LiquidWalletAssetBalanceQueryResult(null!));
		Assert.Equal("amounts", nullResult.ParamName);
		AssertOwnedPrivateFailure(
			nullResult,
			"Value cannot be null. (Parameter 'amounts')",
			[],
			[RequestRenderingCanary]);

		LiquidAssetAmount validAmount = Amount(canary, 57);
		LiquidAssetAmount[] invalidAmounts = [validAmount, null!];
		ArgumentException nullResultMember = Assert.Throws<ArgumentException>(() =>
			new LiquidWalletAssetBalanceQueryResult(invalidAmounts));
		Assert.Equal("amounts", nullResultMember.ParamName);
		AssertOwnedPrivateFailure(
			nullResultMember,
			"The Liquid asset balance query result could not be accepted. (Parameter 'amounts')",
			[invalidAmounts, validAmount, canary],
			[RequestRenderingCanary, "result-index-canary-one"]);
	}

	[Fact]
	public void MultiassetBalanceQueryOwnsStorageAndExposesNoMutableCollectionSurface()
	{
		LiquidAssetAmount first = Amount(IssuedAsset, 41);
		LiquidAssetAmount second = Amount(Asset(3), 42);
		LiquidAssetAmount[] source = [first, second];
		var result = new LiquidWalletAssetBalanceQueryResult(source);

		source[0] = second;
		Array.Clear(source);

		Assert.Equal([first, second], result);
		object boxedResult = result;
		Type resultType = typeof(LiquidWalletAssetBalanceQueryResult);
		Assert.True(resultType.IsClass);
		Assert.True(resultType.IsNotPublic);
		Assert.True(resultType.IsSealed);
		Assert.False(resultType.IsAbstract);
		Assert.False(resultType.IsNested);
		Assert.Equal(typeof(object), resultType.BaseType);

		ConstructorInfo resultConstructor = Assert.Single(resultType.GetConstructors(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
			BindingFlags.Static | BindingFlags.DeclaredOnly));
		Assert.Equal(
			MethodAttributes.Assembly | MethodAttributes.HideBySig |
				MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
			resultConstructor.Attributes);
		Assert.False(resultConstructor.IsStatic);
		Assert.Equal(
			[typeof(LiquidAssetAmount[])],
			resultConstructor.GetParameters().Select(parameter => parameter.ParameterType));

		FieldInfo amountsField = Assert.Single(resultType.GetFields(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
			BindingFlags.Static | BindingFlags.DeclaredOnly));
		Assert.Equal("_amounts", amountsField.Name);
		Assert.Equal(typeof(LiquidAssetAmount[]), amountsField.FieldType);
		Assert.Equal(FieldAttributes.Private | FieldAttributes.InitOnly, amountsField.Attributes);

		Assert.False(boxedResult is IList);
		Assert.False(boxedResult is ICollection);
		Assert.False(boxedResult is IList<LiquidAssetAmount>);
		Assert.False(boxedResult is ICollection<LiquidAssetAmount>);
		Assert.Equal(
			new[]
			{
				typeof(IEnumerable<LiquidAssetAmount>),
				typeof(IReadOnlyCollection<LiquidAssetAmount>),
				typeof(IReadOnlyList<LiquidAssetAmount>),
				typeof(IEnumerable),
			},
			resultType.GetInterfaces()
				.OrderBy(type => type.FullName, StringComparer.Ordinal));
		Assert.Empty(resultType.GetFields(
			BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
		Assert.Equal(
			[
				"GetEnumerator()->System.Collections.Generic.IEnumerator`1[[WalletWasabi.Liquid.Amounts.LiquidAssetAmount, WalletWasabi, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]",
				"System.Collections.IEnumerable.GetEnumerator()->System.Collections.IEnumerator",
				"ToString()->System.String",
				"get_Count()->System.Int32",
				"get_Item(System.Int32)->WalletWasabi.Liquid.Amounts.LiquidAssetAmount",
			],
			resultType.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly)
				.Select(method =>
				{
					string parameters = string.Join(",", method.GetParameters()
						.Select(parameter => parameter.ParameterType.FullName));
					return NormalizeProductAssemblyVersion(
						$"{method.Name}({parameters})->{method.ReturnType.FullName}");
				})
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.DoesNotContain(
			resultType.GetProperties(
				BindingFlags.Public | BindingFlags.Instance),
			property => property.PropertyType.IsArray ||
				property.PropertyType.IsGenericType &&
				(property.PropertyType.GetGenericTypeDefinition() == typeof(Memory<>) ||
				 property.PropertyType.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>)));
		Assert.Equal(nameof(LiquidWalletAssetBalanceQueryResult), result.ToString());
		Assert.Equal([first, second], result.ToArray());

		IEnumerator<LiquidAssetAmount> genericEnumerator = result.GetEnumerator();
		IEnumerator nonGenericEnumerator = ((IEnumerable)result).GetEnumerator();
		AssertEnumeratorCannotExposeOrMutateStorage(genericEnumerator, [first, second]);
		AssertEnumeratorCannotExposeOrMutateStorage(nonGenericEnumerator, [first, second]);
		Assert.Equal([first, second], result);
	}

	[Fact]
	public void MultiassetBalanceQueryTracksTransitionsAndReplayWithoutMutation()
	{
		LiquidTransactionId receiveId = Tx('8');
		LiquidOwnedOutput issued = Output(receiveId, 0, IssuedAsset, 77);
		LiquidOwnedOutput pegged = Output(receiveId, 1, PeggedAsset, 88);
		LiquidWalletState received = LiquidWalletState.Empty(PeggedAsset).Apply(
			0,
			Delta(receiveId, [], [issued, pegged]));
		LiquidAssetId[] requested = [IssuedAsset, PeggedAsset, Asset(3)];
		LiquidWalletReplaySnapshot replayBefore = received.ExportReplaySnapshot();
		LiquidAssetAmount[] balancesBefore = received.GetBalances().GetAmounts().ToArray();
		LiquidWalletCoinControlSnapshot coinControlBefore = received.GetCoinControlSnapshot();
		string[] effectsBefore = TransactionEffectRows(received);
		LiquidWalletAssetBalanceQueryResult before = received.QueryAssetBalances(1, requested);
		LiquidAssetAmount[] beforeValues = before.ToArray();

		LiquidWalletReplaySnapshot replayAfter = received.ExportReplaySnapshot();
		AssertReplayEquivalent(replayBefore, replayAfter);
		Assert.Equal(balancesBefore, received.GetBalances().GetAmounts());
		AssertCoinControlEquivalent(coinControlBefore, received.GetCoinControlSnapshot());
		Assert.Equal(effectsBefore, TransactionEffectRows(received));
		AssertQueryResultUnchanged(before, beforeValues);

		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 51);
		LiquidWalletState confirmed = received.Confirm(1, receiveId, confirmation);
		LiquidWalletAssetBalanceQueryResult afterConfirmation = confirmed.QueryAssetBalances(2, requested);
		AssertEqualIndependentResults(before, afterConfirmation);
		LiquidWalletState unconfirmed = confirmed.Unconfirm(2, receiveId, confirmation);
		AssertEqualIndependentResults(
			afterConfirmation,
			unconfirmed.QueryAssetBalances(3, requested));

		LiquidTransactionId unrelatedId = Tx('6');
		LiquidOwnedOutput unrelatedOutput = Output(unrelatedId, 0, Asset(4), 99);
		LiquidWalletState unrelated = unconfirmed.Apply(
			3,
			Delta(unrelatedId, [], [unrelatedOutput]));
		LiquidWalletAssetBalanceQueryResult afterUnrelated = unrelated.QueryAssetBalances(4, requested);
		AssertEqualIndependentResults(before, afterUnrelated);

		LiquidTransactionId zeroNetId = Tx('5');
		LiquidOwnedOutput replacement = Output(zeroNetId, 0, IssuedAsset, 77);
		LiquidWalletState zeroNet = unrelated.Apply(
			4,
			Delta(zeroNetId, [issued.OutPoint], [replacement]));
		LiquidWalletAssetBalanceQueryResult afterZeroNet = zeroNet.QueryAssetBalances(5, requested);
		AssertEqualIndependentResults(afterUnrelated, afterZeroNet);

		LiquidWalletState rolledBack = zeroNet.RollbackLast(5, zeroNetId);
		LiquidWalletAssetBalanceQueryResult afterRollback = rolledBack.QueryAssetBalances(6, requested);
		AssertEqualIndependentResults(afterZeroNet, afterRollback);

		LiquidTransactionId spendId = Tx('4');
		LiquidOwnedOutput change = Output(spendId, 0, IssuedAsset, 70);
		LiquidWalletState spent = rolledBack.Apply(
			6,
			Delta(spendId, [issued.OutPoint], [change]));
		LiquidWalletAssetBalanceQueryResult afterSpend = spent.QueryAssetBalances(7, requested);
		Assert.Equal([70, 88, 0], afterSpend.Select(amount => amount.AtomicUnits));
		AssertQueryResultUnchanged(before, beforeValues);

		LiquidWalletState replayed = LiquidWalletState.RestoreReplaySnapshot(
			received.ExportReplaySnapshot());
		AssertEqualIndependentResults(before, replayed.QueryAssetBalances(1, requested));

		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(
			LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(
					received.ExportReplaySnapshot(),
					91,
					key,
					context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletReplayOpenResult opened = protectedPayload.Open(key, context);
			LiquidWalletState protectedRestored = LiquidWalletState.RestoreReplaySnapshot(
				opened.Snapshot);
			Assert.Equal(91ul, opened.Generation);
			AssertEqualIndependentResults(
				before,
				protectedRestored.QueryAssetBalances(1, requested));
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
	public void MultiassetBalanceQuerySelectsPositionsAcrossManyRetainedAssets()
	{
		const int AssetCount = 1_500;
		LiquidTransactionId transactionId = Tx('7');
		var outputs = new LiquidOwnedOutput[AssetCount];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] = Output(transactionId, (uint)index, Asset((uint)index + 1), index + 1);
		}
		LiquidWalletState state = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				1,
				[Delta(transactionId, [], outputs)],
				[]));
		LiquidAssetId[] requested =
		[
			Asset(1),
			Asset(751),
			Asset(1_500),
			Asset(751),
			Asset(1_501),
		];

		LiquidWalletAssetBalanceQueryResult result = state.QueryAssetBalances(1, requested);

		Assert.Equal([1, 751, 1_500, 751, 0], result.Select(amount => amount.AtomicUnits));
		AssertEqualIndependentResults(result[1], result[3]);
		for (int index = 0; index < result.Count; index++)
		{
			AssertFreshBalance(result[index], requested[index], state.PeggedAssetId, result[index].AtomicUnits);
		}
	}

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
	public void MultiassetBalanceQueryDirectDependencySourcesRemainExact()
	{
		string root = FindRepositoryRoot();
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Amounts/LiquidAssetBalanceMap.cs",
			"c95631f4f642002dd95cc684e549fdc567540d3c2f3ca4b0e5cdfb3f89522acb");
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Amounts/LiquidAssetAmount.cs",
			"8c3b2a403b8139f1e7bcc0689c8ca3e45499dfdd1364283d739cd40e93e249e4");
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Assets/LiquidAssetId.cs",
			"806fd6bb70d9b326385eae70f1ec99882aba04d4e0a31f38c6fc6a150266ba2b");
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

	private static string[] TransactionEffectRows(LiquidWalletState state) =>
		state.GetTransactionEffectSnapshot().GetEffects()
			.Select(effect => string.Join(
				'|',
				effect.TransactionId.CanonicalRpcHex,
				effect.PeggedAssetId.CanonicalRpcHex,
				effect.Confirmation?.CanonicalBlockHash ?? string.Empty,
				effect.Confirmation?.Height.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
				string.Join(
					',',
					effect.GetAssetNetChanges().Select(change =>
						$"{change.AssetId.CanonicalRpcHex}:{change.NetAtomicUnits}"))))
			.ToArray();

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

	private static void AssertEqualIndependentResults(
		LiquidWalletAssetBalanceQueryResult first,
		LiquidWalletAssetBalanceQueryResult second)
	{
		Assert.NotSame(first, second);
		Assert.Equal(first.Count, second.Count);
		for (int index = 0; index < first.Count; index++)
		{
			AssertEqualIndependentResults(first[index], second[index]);
		}
	}

	private static void AssertEnumeratorCannotExposeOrMutateStorage(
		IEnumerator enumerator,
		IReadOnlyList<LiquidAssetAmount> expected)
	{
		object boxedEnumerator = enumerator;
		IEnumerator<LiquidAssetAmount> genericView =
			Assert.IsAssignableFrom<IEnumerator<LiquidAssetAmount>>(boxedEnumerator);
		Assert.False(boxedEnumerator.GetType().IsArray);
		Assert.False(boxedEnumerator is IEnumerable);
		Assert.False(boxedEnumerator is IList);
		Assert.False(boxedEnumerator is ICollection);
		Assert.False(boxedEnumerator is IList<LiquidAssetAmount>);
		Assert.False(boxedEnumerator is ICollection<LiquidAssetAmount>);
		Assert.False(boxedEnumerator is List<LiquidAssetAmount>);
		Assert.False(boxedEnumerator is ArraySegment<LiquidAssetAmount>);
		Assert.False(boxedEnumerator is Memory<LiquidAssetAmount>);
		Assert.False(boxedEnumerator is ReadOnlyMemory<LiquidAssetAmount>);
		Assert.Equal(
			new[]
			{
				typeof(IEnumerator<LiquidAssetAmount>),
				typeof(IDisposable),
				typeof(IEnumerator),
			}.OrderBy(type => type.FullName, StringComparer.Ordinal),
			boxedEnumerator.GetType().GetInterfaces()
				.OrderBy(type => type.FullName, StringComparer.Ordinal));
		Assert.Empty(boxedEnumerator.GetType().GetFields(
			BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
		Assert.DoesNotContain(
			boxedEnumerator.GetType().GetProperties(
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
			property => property.Name == "SyncRoot" || IsMutableStorageType(property.PropertyType));
		Assert.DoesNotContain(
			boxedEnumerator.GetType().GetMethods(
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
			method => IsMutableStorageType(method.ReturnType) ||
				method.GetParameters().Any(parameter => parameter.IsOut &&
					IsMutableStorageType(parameter.ParameterType.GetElementType()!)));
		foreach (Type interfaceType in boxedEnumerator.GetType().GetInterfaces())
		{
			InterfaceMapping map = boxedEnumerator.GetType().GetInterfaceMap(interfaceType);
			Assert.Equal(map.InterfaceMethods.Length, map.TargetMethods.Length);
			for (int index = 0; index < map.InterfaceMethods.Length; index++)
			{
				MethodInfo interfaceMethod = map.InterfaceMethods[index];
				MethodInfo targetMethod = map.TargetMethods[index];
				Assert.True(targetMethod.DeclaringType!.IsAssignableFrom(boxedEnumerator.GetType()));
				Assert.False(IsMutableStorageType(interfaceMethod.ReturnType));
				Assert.False(IsMutableStorageType(targetMethod.ReturnType));
				Assert.DoesNotContain(
					interfaceMethod.GetParameters().Concat(targetMethod.GetParameters()),
					parameter => parameter.ParameterType.IsByRef &&
						IsMutableStorageType(parameter.ParameterType.GetElementType()!));
			}
		}

		var observed = new List<LiquidAssetAmount>();
		while (enumerator.MoveNext())
		{
			LiquidAssetAmount current = genericView.Current;
			Assert.Same(current, Assert.IsType<LiquidAssetAmount>(enumerator.Current));
			Assert.Same(expected[observed.Count], current);
			observed.Add(current);
		}
		Assert.Equal(expected, observed);
		Assert.False(enumerator.MoveNext());
		enumerator.Reset();
		Assert.True(enumerator.MoveNext());
		Assert.Same(expected[0], genericView.Current);
		Assert.Same(genericView.Current, Assert.IsType<LiquidAssetAmount>(enumerator.Current));
		genericView.Dispose();
	}

	private static bool IsMutableStorageType(Type type)
	{
		Type candidate = type.IsByRef ? type.GetElementType()! : type;
		if (candidate.IsArray || candidate == typeof(Array) ||
			type == typeof(IList) || type == typeof(ICollection))
		{
			return true;
		}

		return candidate.IsGenericType &&
			(candidate.GetGenericTypeDefinition() == typeof(List<>) ||
			 candidate.GetGenericTypeDefinition() == typeof(IList<>) ||
			 candidate.GetGenericTypeDefinition() == typeof(ICollection<>) ||
			 candidate.GetGenericTypeDefinition() == typeof(ArraySegment<>) ||
			 candidate.GetGenericTypeDefinition() == typeof(Memory<>) ||
			 candidate.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>));
	}

	private static void AssertReplayEquivalent(
		LiquidWalletReplaySnapshot expected,
		LiquidWalletReplaySnapshot actual)
	{
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletTransactionDelta> expectedDeltas = expected.GetDeltas();
		IReadOnlyList<LiquidWalletTransactionDelta> actualDeltas = actual.GetDeltas();
		Assert.Equal(expectedDeltas.Count, actualDeltas.Count);
		for (int index = 0; index < expectedDeltas.Count; index++)
		{
			Assert.Equal(expectedDeltas[index].TransactionId, actualDeltas[index].TransactionId);
			Assert.Equal(expectedDeltas[index].GetSpentOutPoints(), actualDeltas[index].GetSpentOutPoints());
			Assert.Equal(expectedDeltas[index].GetCreatedOutputs(), actualDeltas[index].GetCreatedOutputs());
		}
		Assert.Equal(expected.GetConfirmations(), actual.GetConfirmations());
	}

	private static void AssertCoinControlEquivalent(
		LiquidWalletCoinControlSnapshot expected,
		LiquidWalletCoinControlSnapshot actual)
	{
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletCoinControlEntry> expectedEntries = expected.GetEntries();
		IReadOnlyList<LiquidWalletCoinControlEntry> actualEntries = actual.GetEntries();
		Assert.Equal(expectedEntries.Count, actualEntries.Count);
		for (int index = 0; index < expectedEntries.Count; index++)
		{
			Assert.Equal(expectedEntries[index].OutPoint, actualEntries[index].OutPoint);
			Assert.Equal(expectedEntries[index].Amount, actualEntries[index].Amount);
			Assert.Equal(expectedEntries[index].PeggedAssetId, actualEntries[index].PeggedAssetId);
			Assert.Equal(expectedEntries[index].Confirmation, actualEntries[index].Confirmation);
		}
	}

	private static void AssertQueryResultUnchanged(
		LiquidWalletAssetBalanceQueryResult result,
		IReadOnlyList<LiquidAssetAmount> expected)
	{
		Assert.Equal(expected.Count, result.Count);
		for (int index = 0; index < expected.Count; index++)
		{
			Assert.Same(expected[index], result[index]);
			Assert.Equal(expected[index], result[index]);
		}
	}

	private static void AssertOwnedPrivateFailure(
		Exception failure,
		string expectedMessage,
		IReadOnlyList<object> suppliedValues,
		IReadOnlyList<string> additionalCanaries)
	{
		Assert.Equal(expectedMessage, failure.Message);
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
		var sensitiveText = new List<string>(additionalCanaries);
		foreach (LiquidAssetId assetId in suppliedValues.OfType<LiquidAssetId>())
		{
			sensitiveText.Add(assetId.CanonicalRpcHex);
			sensitiveText.Add(Convert.ToHexString(assetId.ToConsensusBytes()));
		}
		foreach (string canary in sensitiveText)
		{
			Assert.DoesNotContain(canary, failure.Message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(canary, failure.ToString(), StringComparison.OrdinalIgnoreCase);
		}

		if (failure is ArgumentOutOfRangeException rangeFailure)
		{
			Assert.Null(rangeFailure.ActualValue);
		}

		foreach (object? retained in GetDirectExceptionValues(failure))
		{
			Assert.DoesNotContain(suppliedValues, supplied => ReferenceEquals(supplied, retained));
		}
	}

	private static IEnumerable<object?> GetDirectExceptionValues(Exception failure)
	{
		for (Type? type = failure.GetType(); type is not null; type = type.BaseType)
		{
			foreach (FieldInfo field in type.GetFields(
				BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				yield return field.GetValue(failure);
			}
		}

		foreach (PropertyInfo property in failure.GetType().GetProperties(
			BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.GetMethod is not null && property.GetIndexParameters().Length == 0)
			{
				yield return property.GetValue(failure);
			}
		}
	}

	private sealed class IndexedAssetList : IReadOnlyList<LiquidAssetId>
	{
		private readonly LiquidAssetId[] _values;
		private readonly bool _throwOnCount;
		private readonly string _renderingCanary;

		public IndexedAssetList(
			LiquidAssetId[] values,
			bool throwOnCount = false,
			string renderingCanary = "private-indexed-list-canary-159307")
		{
			_values = values;
			_throwOnCount = throwOnCount;
			_renderingCanary = renderingCanary;
		}

		public int Count
		{
			get
			{
				CountReads++;
				if (_throwOnCount)
				{
					throw new InvalidOperationException("The hostile request count was inspected.");
				}
				return _values.Length;
			}
		}

		public LiquidAssetId this[int index]
		{
			get
			{
				IndexReads.Add(index);
				return _values[index];
			}
		}

		public int CountReads { get; private set; }
		public List<int> IndexReads { get; } = [];
		public int EnumerationRequests { get; private set; }

		public IEnumerator<LiquidAssetId> GetEnumerator()
		{
			EnumerationRequests++;
			return ((IEnumerable<LiquidAssetId>)_values).GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		public override string ToString() => _renderingCanary;
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


	private static string FindRepositoryRoot()
	{
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
			directory is not null;
			directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "WalletWasabi.slnx")))
			{
				return directory.FullName;
			}
		}

		throw new DirectoryNotFoundException("The repository root could not be located.");
	}

	private static void AssertSourceSha256(string root, string relativePath, string expected)
	{
		string actual = Convert.ToHexString(SHA256.HashData(
			File.ReadAllBytes(Path.Combine(root, relativePath)))).ToLowerInvariant();
		Assert.Equal(expected, actual);
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


	[Fact]
	public void MultiassetBalanceQueryAddsOnlyPermittedAssemblyTypes()
	{
		const string LiquidTestNamespacePrefix = "WalletWasabi.Tests.UnitTests.Liquid";
		// Only this explicit allowlist of top-level Liquid test types is pinned. Nested helper
		// types, lambdas, and other compiler-generated types are counted but never named, so a
		// compiler or host change that alters generated type names does not touch this test.
		string[] expectedTestClasses =
		[
			"WalletWasabi.Tests.UnitTests.Liquid.Addresses.LiquidAddressTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Amounts.LiquidAssetAmountTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Amounts.LiquidAssetBalanceMapTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Assets.LiquidAssetIdTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.ElementsReviewedNodeExpectationSourceTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.LiquidApplicationCompositionTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.LiquidApplicationLifecycleCoordinatorTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.LiquidKeyDomainTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.LiquidPreRefreshOwnerTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.LiquidProviderOwnershipSeamTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.LiquidReceiveMaterialTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Client.LiquidRpcProfileSourceTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Network.ElementsPublicNetworkManifestTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsExpectationBoundBroadcastTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Transactions.LiquidTransactionIdentityTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Transactions.LiquidTransactionWitnessBindingTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOwnedOutputObservationTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidSuppliedConfidentialDestinationBatchTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidSuppliedConfidentialDestinationTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletCoinControlTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletFundingDependencyDeriverTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletLabelSetTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletLoadSaveTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletObservationBatchTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletReplayProtectionTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletReplaySnapshotTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletTransactionEffectTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletTransactionObservationTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync.LiquidWalletPersistenceFormatTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync.LiquidWalletPersistenceHandoffTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync.LiquidWalletRecoverySyncTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync.LiquidWalletReorgPlannerTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync.LiquidWalletScanIntentDeriverTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync.LiquidWalletSyncBatchPlannerTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync.LiquidWalletSyncSessionTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletHistoryPresentationTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletNativeFactsBindingTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletNativeFactsObserverTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletNativeSignerTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletNativeSigningBindingTransactionIdTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletRuntimeHandoffTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletUiFacadeTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletUiHistoryTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletUiSignRequestTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui.LiquidWalletUiSpendPlanTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1CorpusTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1LiveValidationTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1NativeValidation",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1ValidationParityTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.OrdinaryWalletPlanWireV1Corpus",
			"WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire.CorpusFrame",
			"WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1StructuralRequestCodecTests",
			"WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1UntrustedStructuralResponseTests",
			"WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire.WalletFactsWireV1Corpus",
		];
		Type[] liquidTypes = typeof(LiquidWalletStateTests).Assembly.GetTypes()
			.Where(type =>
				type.FullName is string fullName &&
				fullName.StartsWith(LiquidTestNamespacePrefix + ".", StringComparison.Ordinal))
			.ToArray();
		Assert.NotEmpty(liquidTypes);

		string[] topLevelTypeNames = liquidTypes
			.Where(type => type.DeclaringType is null)
			.Select(type => Assert.IsType<string>(type.FullName))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.All(
			expectedTestClasses,
			name => Assert.Contains(name, topLevelTypeNames));

		Assert.All(
			liquidTypes,
			type => Assert.False(
				type.DeclaringType is null && type.Name.StartsWith('<'),
				$"A compiler-generated type appears as a top-level Liquid test type: {type.FullName}"));
		Assert.Equal(liquidTypes.Length, topLevelTypeNames.Length + liquidTypes.Count(type => type.DeclaringType is not null));
	}


	[Fact]
	public void ProductAssemblyVersionNormalizationIsNarrowAndFailClosed()
	{
		const string CanonicalIdentity =
			"WalletWasabi, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
		string runtimeIdentity = Assert.IsType<string>(typeof(LiquidWalletState).Assembly.FullName);
		Assert.Equal(
			CanonicalIdentity,
			NormalizeProductAssemblyVersion(runtimeIdentity));
		string productTypeIdentity =
			Assert.IsType<string>(typeof(LiquidWalletState).AssemblyQualifiedName);
		string normalizedProductTypeIdentity = NormalizeProductAssemblyVersion(productTypeIdentity);
		if (!StringComparer.Ordinal.Equals(runtimeIdentity, CanonicalIdentity))
		{
			Assert.DoesNotContain(runtimeIdentity, normalizedProductTypeIdentity, StringComparison.Ordinal);
		}
		Assert.Contains(CanonicalIdentity, normalizedProductTypeIdentity, StringComparison.Ordinal);
		string closedGenericIdentity = Assert.IsType<string>(
			typeof(IReadOnlyList<LiquidWalletAssetBalanceQueryResult>).AssemblyQualifiedName);
		string normalizedClosedGenericIdentity = NormalizeProductAssemblyVersion(closedGenericIdentity);
		if (!StringComparer.Ordinal.Equals(runtimeIdentity, CanonicalIdentity))
		{
			Assert.DoesNotContain(runtimeIdentity, normalizedClosedGenericIdentity, StringComparison.Ordinal);
		}
		Assert.Contains(CanonicalIdentity, normalizedClosedGenericIdentity, StringComparison.Ordinal);

		const string AlternateVersionIdentity =
			"WalletWasabi, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null";
		Assert.NotEqual(AlternateVersionIdentity, runtimeIdentity);
		Assert.Equal(
			AlternateVersionIdentity,
			NormalizeProductAssemblyVersion(AlternateVersionIdentity));

		string foreignIdentity = Assert.IsType<string>(typeof(string).AssemblyQualifiedName);
		Assert.Equal(foreignIdentity, NormalizeProductAssemblyVersion(foreignIdentity));

		foreach (string alteredIdentity in new[]
		{
			runtimeIdentity.Replace("WalletWasabi,", "ForeignWallet,", StringComparison.Ordinal),
			runtimeIdentity.Replace("Culture=neutral", "Culture=en-US", StringComparison.Ordinal),
			runtimeIdentity.Replace("PublicKeyToken=null", "PublicKeyToken=0011223344556677", StringComparison.Ordinal),
		})
		{
			Assert.Equal(alteredIdentity, NormalizeProductAssemblyVersion(alteredIdentity));
		}
	}

	internal static string NormalizeProductAssemblyVersion(string identity)
	{
		const string CanonicalIdentity =
			"WalletWasabi, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
		Assembly productAssembly = typeof(LiquidWalletState).Assembly;
		Version runtimeVersion = productAssembly.GetName().Version ??
			throw new Xunit.Sdk.XunitException("The WalletWasabi assembly version is absent.");
		string runtimeIdentity =
			$"WalletWasabi, Version={runtimeVersion}, Culture=neutral, PublicKeyToken=null";
		Assert.Equal(runtimeIdentity, productAssembly.FullName);
		return identity.Replace(
			runtimeIdentity,
			CanonicalIdentity,
			StringComparison.Ordinal);
	}

}
