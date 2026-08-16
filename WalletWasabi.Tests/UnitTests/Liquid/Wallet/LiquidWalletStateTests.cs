using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
	private const string ExpectedMultiassetBalanceQueryGraphManifestSha256 =
		"8906ddac369d14a81db00bbedfe28359e1aeef2d0a7a26f2ae1af741c0a86dfc";
#else
	private const string ExpectedBalanceQueryGraphManifestSha256 =
		"93f8974db98e3b4c52a00b4b646ea412b55b70c6fc8efcb7def7d87bdb4b2019";
	private const string ExpectedMultiassetBalanceQueryGraphManifestSha256 =
		"b757187abd90aebafd4d29b0394bcf79668b4ef2a267e5ad53f6891609965d26";
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
	public void MultiassetBalanceQueryHasClosedBoundedDataflowAndResultSurface()
	{
		MethodInfo query = RequiredMethod(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.QueryAssetBalances),
			typeof(ulong),
			typeof(IReadOnlyList<LiquidAssetId>));
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
		MethodInfo throwIfNull = RequiredMethod(
			typeof(ArgumentNullException),
			nameof(ArgumentNullException.ThrowIfNull),
			typeof(object),
			typeof(string));
		MethodInfo countGetter = RequiredPropertyGetter(
			typeof(IReadOnlyCollection<LiquidAssetId>),
			nameof(IReadOnlyCollection<LiquidAssetId>.Count));
		MethodInfo indexerGetter = RequiredPropertyGetter(
			typeof(IReadOnlyList<LiquidAssetId>),
			"Item");
		ConstructorInfo rangeConstructor = typeof(ArgumentOutOfRangeException).GetConstructor(
			[typeof(string), typeof(object), typeof(string)])!;
		ConstructorInfo argumentConstructor = typeof(ArgumentException).GetConstructor(
			[typeof(string), typeof(string)])!;
		MethodInfo getAmountOrZero = RequiredMethod(
			typeof(LiquidAssetBalanceMap),
			nameof(LiquidAssetBalanceMap.GetAmountOrZero),
			typeof(LiquidAssetId));
		MethodInfo toConsensusBytes = RequiredMethod(
			typeof(LiquidAssetId),
			nameof(LiquidAssetId.ToConsensusBytes));
		MethodInfo byteArrayToReadOnlySpan = RequiredMethod(
			typeof(ReadOnlySpan<byte>),
			"op_Implicit",
			typeof(byte[]));
		MethodInfo parseConsensusBytes = RequiredMethod(
			typeof(LiquidAssetId),
			nameof(LiquidAssetId.ParseConsensusBytes),
			typeof(ReadOnlySpan<byte>),
			typeof(string));
		MethodInfo atomicUnitsGetter = RequiredPropertyGetter(
			typeof(LiquidAssetAmount),
			nameof(LiquidAssetAmount.AtomicUnits));
		MethodInfo createAmount = RequiredMethod(
			typeof(LiquidAssetAmount),
			nameof(LiquidAssetAmount.Create),
			typeof(LiquidAssetId),
			typeof(LiquidAssetId),
			typeof(long));
		ConstructorInfo resultConstructor = typeof(LiquidWalletAssetBalanceQueryResult).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			[typeof(LiquidAssetAmount[])],
			modifiers: null)!;
		MethodInfo resultCountGetter = RequiredPropertyGetter(
			typeof(LiquidWalletAssetBalanceQueryResult),
			nameof(LiquidWalletAssetBalanceQueryResult.Count));
		MethodInfo resultIndexerGetter = RequiredPropertyGetter(
			typeof(LiquidWalletAssetBalanceQueryResult),
			"Item");
		MethodInfo genericEnumerator = RequiredMethod(
			typeof(LiquidWalletAssetBalanceQueryResult),
			nameof(LiquidWalletAssetBalanceQueryResult.GetEnumerator));
		MethodInfo nonGenericEnumerator = Assert.Single(
			typeof(LiquidWalletAssetBalanceQueryResult).GetMethods(
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "System.Collections.IEnumerable.GetEnumerator");
		MethodInfo toString = RequiredMethod(
			typeof(LiquidWalletAssetBalanceQueryResult),
			nameof(LiquidWalletAssetBalanceQueryResult.ToString));

		Assert.Equal(
			new MethodBase[]
			{
				ensureRevision,
				throwIfNull,
				countGetter,
				rangeConstructor,
				indexerGetter,
				argumentConstructor,
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
				resultConstructor,
			},
			GetReferencedMethods(query));
		Assert.Equal(
			[RequiredField(typeof(LiquidWalletState), "_balances")],
			GetReferencedFields(query));
		Assert.Equal(
			2,
			GetIlInstructions(query).Count(instruction => instruction.OpCode == OpCodes.Newarr));
		Assert.Empty(query.GetMethodBody()!.ExceptionHandlingClauses);
		Assert.DoesNotContain(
			GetIlInstructions(query),
			instruction => instruction.OpCode is var opCode &&
				(opCode == OpCodes.Stfld || opCode == OpCodes.Stsfld || opCode == OpCodes.Switch ||
				 opCode == OpCodes.Calli || opCode == OpCodes.Localloc || IsIndirectWrite(opCode)));
		AssertFullInputLoopsDominateFirstLookup(query, indexerGetter, argumentConstructor, getAmountOrZero);

		Assert.Equal(
			new MethodBase[]
			{
				typeof(object).GetConstructor(Type.EmptyTypes)!,
				throwIfNull,
				argumentConstructor,
			},
			GetReferencedMethods(resultConstructor));
		Assert.Equal(
			[RequiredField(typeof(LiquidWalletAssetBalanceQueryResult), "_amounts")],
			GetReferencedFields(resultConstructor));
		Assert.Equal(
			1,
			GetIlInstructions(resultConstructor).Count(instruction => instruction.OpCode == OpCodes.Newarr));
		Assert.Equal(
			1,
			GetIlInstructions(resultConstructor).Count(instruction => instruction.OpCode == OpCodes.Stfld));
		Assert.Empty(resultConstructor.GetMethodBody()!.ExceptionHandlingClauses);

		FieldInfo amountsField = RequiredField(
			typeof(LiquidWalletAssetBalanceQueryResult),
			"_amounts");
		Assert.Equal([amountsField], GetReferencedFields(resultCountGetter));
		Assert.Equal([amountsField], GetReferencedFields(resultIndexerGetter));
		Assert.Equal([amountsField], GetReferencedFields(genericEnumerator));
		Assert.Empty(GetReferencedFields(nonGenericEnumerator));
		Assert.Empty(GetReferencedFields(toString));
		Assert.Empty(GetReferencedMethods(resultCountGetter));
		Assert.Empty(GetReferencedMethods(resultIndexerGetter));
		Assert.Single(GetReferencedMethods(genericEnumerator));
		Assert.Equal([genericEnumerator], GetReferencedMethods(nonGenericEnumerator));
		Assert.Empty(GetReferencedMethods(toString));

		MethodBase[] resultConstructorCallers = typeof(LiquidWalletState).Assembly.GetTypes()
			.SelectMany(type => type.GetMethods(
					BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
					BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
				.Cast<MethodBase>()
				.Concat(type.GetConstructors(
					BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
					BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
			.Where(method => method.GetMethodBody() is not null &&
				GetReferencedMethods(method).Contains(resultConstructor))
			.ToArray();
		Assert.Equal([query], resultConstructorCallers);

		Assert.Equal(
			ExpectedMultiassetBalanceQueryGraphManifestSha256,
			Sha256Utf8(BuildClosedGraphManifest(
				query,
				ensureRevision,
				revisionGetter,
				resultConstructor,
				resultCountGetter,
				resultIndexerGetter,
				genericEnumerator,
				nonGenericEnumerator,
				toString)));
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
	public void MechanicallyAdjustedSubjectsRemainTokenNormalizedEqualToBase()
	{
		string root = FindRepositoryRoot();
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Wallet/LiquidSuppliedConfidentialDestination.cs",
			"ce73126abb53838790e9254658552641f908fd48ce0504c2bb3fbc7e9fbd65f5");
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Wallet/LiquidSuppliedConfidentialDestinationBatch.cs",
			"23e5b1017e25ba35be85b012f2faac9cc819f67596e4fba1161569901871661e");
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Wallet/LiquidWalletLabelSet.cs",
			"e94c502ceaaa54afca0266b092dacf5c88216e1c92586c46a248ef95ab147fe1");
#if DEBUG
		Assert.Equal("a7cafd4d5d94c44d87fd9f20a41c3e3d23599f2caecd65c02d657e471bd58cb4", Sha256Utf8(
			BuildTokenNormalizedTypeManifest(typeof(LiquidSuppliedConfidentialDestination))));
		Assert.Equal("3b47193e64f9bef4574838a1a214e35535614445f314036074db0f503049c030", Sha256Utf8(
			BuildTokenNormalizedTypeManifest(typeof(LiquidSuppliedConfidentialDestinationBatch))));
		Assert.Equal("c3a3d10e56e7efb85d25f751f1e8fb2d4e34650d45b5a5dadb4da247de74fa2b", Sha256Utf8(
			BuildTokenNormalizedTypeManifest(typeof(LiquidWalletLabelSet))));
#else
		Assert.Equal("52a87300c11d118eae8789e66f8f14628aa85fcf550658678ad424b17d3663c9", Sha256Utf8(
			BuildTokenNormalizedTypeManifest(typeof(LiquidSuppliedConfidentialDestination))));
		Assert.Equal("fd9305850967d4a9b9f5e67cc0d20db28359604919d6e37ed0b1247d450e0656", Sha256Utf8(
			BuildTokenNormalizedTypeManifest(typeof(LiquidSuppliedConfidentialDestinationBatch))));
		Assert.Equal("c036d5f129a882a89ef54c2863ab16c99bfe451828a56c239f0501c0cacce3b3", Sha256Utf8(
			BuildTokenNormalizedTypeManifest(typeof(LiquidWalletLabelSet))));
#endif
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

	private static void AssertFullInputLoopsDominateFirstLookup(
		MethodInfo query,
		MethodInfo indexerGetter,
		ConstructorInfo argumentConstructor,
		MethodInfo getAmountOrZero)
	{
		var instructions = GetIlInstructions(query).ToArray();
		var lookup = Assert.Single(
			instructions,
			instruction => Equals(instruction.Member, getAmountOrZero));
		var backwardBranches = instructions
			.Where(instruction =>
				instruction.Offset < lookup.Offset &&
				instruction.BranchTarget is int target &&
				target < instruction.Offset &&
				IsConditionalControlTransfer(instruction.OpCode))
			.OrderBy(instruction => instruction.Offset)
			.ToArray();
		Assert.Equal(2, backwardBranches.Length);

		var dominators = BuildDominators(instructions);
		foreach (var branch in backwardBranches)
		{
			Assert.Contains(branch.Offset, dominators[lookup.Offset]);
			Assert.Contains(branch.NextOffset, dominators[lookup.Offset]);
		}

		int snapshotLoopStart = backwardBranches[0].BranchTarget!.Value;
		int snapshotLoopEnd = backwardBranches[0].NextOffset;
		Assert.Single(
			instructions,
			instruction => instruction.Offset >= snapshotLoopStart &&
				instruction.Offset < snapshotLoopEnd &&
				Equals(instruction.Member, indexerGetter));

		int nullLoopStart = backwardBranches[1].BranchTarget!.Value;
		int nullLoopEnd = backwardBranches[1].NextOffset;
		var nullFailureConstruction = Assert.Single(
			instructions,
			instruction => instruction.Offset >= nullLoopStart &&
				instruction.Offset < nullLoopEnd &&
				Equals(instruction.Member, argumentConstructor));
		var nullThrow = Assert.Single(
			instructions,
			instruction => instruction.Offset == nullFailureConstruction.NextOffset);
		Assert.Equal(OpCodes.Throw, nullThrow.OpCode);
		Assert.True(snapshotLoopEnd < nullLoopStart);
		Assert.True(nullLoopEnd < lookup.Offset);
	}

	private static Dictionary<int, HashSet<int>> BuildDominators(
		IReadOnlyList<(
			int Offset,
			int NextOffset,
			OpCode OpCode,
			MemberInfo? Member,
			string OperandIdentity,
			int? BranchTarget)> instructions)
	{
		var byOffset = instructions.ToDictionary(instruction => instruction.Offset);
		var successors = instructions.ToDictionary(
			instruction => instruction.Offset,
			instruction =>
			{
				var next = new List<int>();
				if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
				{
					return next;
				}
				if (instruction.OpCode.FlowControl == FlowControl.Branch)
				{
					next.Add(Assert.IsType<int>(instruction.BranchTarget));
					return next;
				}
				if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
				{
					next.Add(Assert.IsType<int>(instruction.BranchTarget));
				}
				if (byOffset.ContainsKey(instruction.NextOffset))
				{
					next.Add(instruction.NextOffset);
				}
				return next;
			});
		int entry = instructions[0].Offset;
		var reachable = new HashSet<int> { entry };
		var pending = new Stack<int>();
		pending.Push(entry);
		while (pending.TryPop(out int current))
		{
			foreach (int successor in successors[current])
			{
				if (reachable.Add(successor))
				{
					pending.Push(successor);
				}
			}
		}

		var predecessors = reachable.ToDictionary(offset => offset, _ => new List<int>());
		foreach (int source in reachable)
		{
			foreach (int target in successors[source].Where(reachable.Contains))
			{
				predecessors[target].Add(source);
			}
		}

		var dominators = reachable.ToDictionary(
			offset => offset,
			offset => offset == entry ? new HashSet<int> { entry } : new HashSet<int>(reachable));
		bool changed;
		do
		{
			changed = false;
			foreach (int offset in reachable.Where(value => value != entry).Order())
			{
				List<int> incoming = predecessors[offset];
				var updated = incoming.Count == 0
					? []
					: new HashSet<int>(dominators[incoming[0]]);
				foreach (int predecessor in incoming.Skip(1))
				{
					updated.IntersectWith(dominators[predecessor]);
				}
				updated.Add(offset);
				if (!updated.SetEquals(dominators[offset]))
				{
					dominators[offset] = updated;
					changed = true;
				}
			}
		}
		while (changed);

		return dominators;
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
				int filterOffset = clause.Flags == ExceptionHandlingClauseOptions.Filter
					? clause.FilterOffset
					: -1;
				Type? catchType = clause.Flags == ExceptionHandlingClauseOptions.Clause
					? clause.CatchType
					: null;
				rows.Add(
					$"EH|{(int)clause.Flags}|{clause.TryOffset}|{clause.TryLength}|" +
					$"{clause.HandlerOffset}|{clause.HandlerLength}|{filterOffset}|" +
					TypeIdentity(catchType));
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

	private static string BuildTokenNormalizedTypeManifest(Type type)
	{
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		var rows = new List<string>
		{
			$"TYPE|{TypeIdentity(type)}|{(int)type.Attributes}",
		};
		foreach (FieldInfo field in type.GetFields(Declared).OrderBy(field => field.Name, StringComparer.Ordinal))
		{
			object? constant = field.IsLiteral ? field.GetRawConstantValue() : null;
			rows.Add(
				$"FIELD|{field.Name}|{TypeIdentity(field.FieldType)}|{(int)field.Attributes}|" +
				$"{constant ?? "null"}");
		}
		foreach (PropertyInfo property in type.GetProperties(Declared)
			.OrderBy(property => property.Name, StringComparer.Ordinal))
		{
			rows.Add(
				$"PROPERTY|{property.Name}|{TypeIdentity(property.PropertyType)}|" +
				$"{(int)property.Attributes}|{property.GetMethod?.Name}|{property.SetMethod?.Name}");
		}
		IEnumerable<MethodBase> methods = type.GetConstructors(Declared).Cast<MethodBase>()
			.Concat(type.GetMethods(Declared));
		foreach (MethodBase method in methods.OrderBy(MethodIdentity, StringComparer.Ordinal))
		{
			rows.Add(
				$"METHOD-ATTRIBUTES|{MethodIdentity(method)}|{(int)method.Attributes}|" +
				$"{(int)method.GetMethodImplementationFlags()}|{(int)method.CallingConvention}");
			rows.Add(BuildClosedGraphManifest(method));
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

	private static string TypeIdentity(Type? type) =>
		NormalizeProductAssemblyVersion(type?.FullName ?? "null");

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

	[Fact]
	public void MultiassetBalanceQueryAddsOnlyPermittedAssemblyTypes()
	{
		string[] removedTestTypes =
		[
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass14_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass19_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass20_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass22_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass30_0",
		];
		string[] addedTestTypes =
		[
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<BindsExactExpectationAndFeeInsideUnchangedGenerationAsync>d__86",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsGenerationOrStatusDriftBeforeExpectationAndFeeMismatchAsync>d__87",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsIdentityOrFeeMismatchOnlyAfterStableFenceAsync>d__88",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<ValidatesExpectationBoundInputsBeforeTransportAsync>d__89",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<FetchesExpectationBoundRawTransactionsInsideExactFenceAsync>d__90",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsMalformedOrDriftingRawTransactionsWithoutPartialAuthorityAsync>d__91",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<EncodesOneExpectationBoundPlanFromCanonicalAcquiredTransactionsAsync>d__117",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsInvalidPlanCompositionBeforeRpcAndInvalidFundingWithoutPartialFrameAsync>d__118",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<AssertPlanEncodingArgumentRejectedAsync>d__119",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass89_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_2",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_3",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass17_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass18_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass19_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass21_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass23_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass24_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass25_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass25_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+<>c__DisplayClass26_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+CountedHostileSelectionList",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlanTests+HostileSelectionInspectedException",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>O",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass24_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass25_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass26_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass27_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass28_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass29_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass31_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass32_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass39_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass42_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass60_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass64_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<>c__DisplayClass65_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+<GetDirectExceptionValues>d__61",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests+IndexedAssetList",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1CorpusTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1CorpusTests+<>c__DisplayClass3_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1LiveValidationTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1LiveValidationTests+<>c",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1LiveValidationTests+<>c__DisplayClass8_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1LiveValidationTests+PlatformLibraryPin",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1NativeValidation",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1NativeValidation+NativeMethods",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1ValidationParityTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>O",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass122_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass130_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass130_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass136_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass139_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass140_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass142_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass145_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass146_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass146_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass148_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass151_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass158_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass159_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass198_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass217_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass221_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass221_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass223_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass226_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass230_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass232_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass232_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass232_2",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass233_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass239_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass30_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass30_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass30_2",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass314_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass36_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass37_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass39_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass40_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass44_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass45_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass46_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass47_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass47_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass47_2",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass48_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass49_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass49_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass49_2",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass49_3",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass49_4",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass49_5",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass50_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass50_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass53_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass53_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass76_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass86_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<>c__DisplayClass87_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<FundingBatchSnapshotsStatefulAndConcurrentlyMutatedRowsAfterNullPreflightAsync>d__40",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<FundingRowSnapshotsStatefulAndConcurrentlyMutatedSourcesAfterNullPreflightAsync>d__36",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<GetIlInstructionsWithOffsets>d__121",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<GetTypeSurfaceManifest>d__110",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+<OwnerRacesReturnOnlyCompleteSuccessOrFixedLifecycleOutcomeAsync>d__53",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+BinaryBuildTrace",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+BuildContextKey",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+CompilerAuthorityEntry",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+CoordinatedSingleItemList`1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+EvaluatedBuildItem",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+GeneratedBuildFile",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+NegativeCountList`1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+PlanFixture",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+ProductionBuildAuthority",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+RepeatedValueList`1",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+StatefulPayloadList",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+StatefulRowList",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireTests+ThrowingPayloadList",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.OrdinaryWalletPlanWireV1Corpus",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.OrdinaryWalletPlanWireV1Corpus+<>c",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.OrdinaryWalletPlanWireV1Corpus+CorpusTree",
		];
		string[] addedProductionTypes =
		[
			"WalletWasabi.Liquid.Rpc.ElementsExpectationBoundNodeObservation",
			"WalletWasabi.Liquid.Rpc.ElementsExpectationBoundRawTransactionBatch",
			"WalletWasabi.Liquid.Rpc.ElementsNodeExpectationBindingLevel",
			"WalletWasabi.Liquid.Rpc.ElementsRawTransactionBindingLevel",
			"WalletWasabi.Liquid.Rpc.ElementsRawTransactionObservation",
			"WalletWasabi.Liquid.Rpc.ElementsRawTransactionRequest",
			"WalletWasabi.Liquid.Rpc.ElementsRpcClient+<GetExpectationBoundNodeObservationAsync>d__65",
			"WalletWasabi.Liquid.Rpc.ElementsRpcClient+<GetExpectationBoundNodeObservationCoreAsync>d__66",
			"WalletWasabi.Liquid.Rpc.ElementsRpcClient+<GetExpectationBoundRawTransactionsAsync>d__68",
			"WalletWasabi.Liquid.Rpc.ElementsRpcClient+<GetExpectationBoundRawTransactionsCoreAsync>d__69",
			"WalletWasabi.Liquid.Rpc.ElementsRpcClient+<GetRawTransactionBytesCoreAsync>d__70",
			"WalletWasabi.Liquid.Rpc.ElementsRpcClient+<CallWithResponseLimitsAsync>d__71",
			"WalletWasabi.Liquid.Rpc.ElementsRpcClient+<EncodeExpectationBoundOrdinaryWalletPlanAsync>d__73",
			"WalletWasabi.Liquid.Wallet.LiquidOrdinaryWalletExactSpendPlan",
			"WalletWasabi.Liquid.Wallet.LiquidWalletAssetBalanceQueryResult",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanEncodedFrame",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingBatch",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingRow",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingRow+EncodingShape",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireErrorCode",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireErrorCodeExtensions",
			"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireLimits",
		];
#if DEBUG
		AssertReconstructedTypeManifest(
			typeof(LiquidWalletState).Assembly,
			"WalletWasabi",
			1_706,
			"e5610b069c1dfe11a7ddc201d2f0e60a250a3305a47018a1abe5eb3983234022",
			[],
			addedProductionTypes);
		AssertReconstructedTypeManifest(
			typeof(LiquidWalletStateTests).Assembly,
			"WalletWasabi.Tests",
			1_739,
			"3672905579f6f0594c4827f6beaf3844b9e9d668ecca2da34981c2b417502476",
			removedTestTypes,
			addedTestTypes);
#else
		AssertReconstructedTypeManifest(
			typeof(LiquidWalletState).Assembly,
			"WalletWasabi",
			1_703,
			"552d9f8e4423a1d3ea02a0dc63e3198d812a4c02e370d798868cfef0234f7173",
			[],
			addedProductionTypes);
		AssertReconstructedTypeManifest(
			typeof(LiquidWalletStateTests).Assembly,
			"WalletWasabi.Tests",
			1_734,
			"88f028ca20723e0ee26a859f24897c7f3df542abc2cdc8ff509543e9375a78ff",
			removedTestTypes,
			addedTestTypes);
#endif
	}

	private static void AssertReconstructedTypeManifest(
		Assembly currentAssembly,
		string expectedSimpleName,
		int expectedBaseCount,
		string expectedBaseSha256,
		IReadOnlyList<string> removedFromBase,
		IReadOnlyList<string> addedToCurrent)
	{
		var reconstructedBase = new HashSet<string>(StringComparer.Ordinal);
		foreach (Type type in currentAssembly.GetTypes())
		{
			Assert.True(reconstructedBase.Add(Assert.IsType<string>(type.FullName)));
		}
		foreach (string added in addedToCurrent)
		{
			Assert.True(reconstructedBase.Remove(added), $"Missing expected added type: {added}");
		}
		foreach (string removed in removedFromBase)
		{
			Assert.True(reconstructedBase.Add(removed), $"Unexpected retained base type: {removed}");
		}

		Assert.Equal(expectedBaseCount, reconstructedBase.Count);
		var rows = new List<string>(reconstructedBase);
		rows.Sort(StringComparer.Ordinal);
		var manifest = new StringBuilder(expectedSimpleName).Append('\0');
		foreach (string row in rows)
		{
			manifest.Append(row).Append('\0');
		}
		Assert.Equal(
			expectedBaseSha256,
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())))
				.ToLowerInvariant());
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
