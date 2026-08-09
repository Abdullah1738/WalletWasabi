using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Amounts;

public class LiquidAssetBalanceMapTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string OtherPeggedAssetHex = "4444444444444444444444444444444444444444444444444444444444444444";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidAssetId OtherAsset => LiquidAssetId.ParseRpcHex(OtherAssetHex);
	private static LiquidAssetId OtherPeggedAsset => LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);

	[Fact]
	public void CreatesEmptyMapAndReturnsContextualZeroForMissingAsset()
	{
		LiquidAssetBalanceMap balances = LiquidAssetBalanceMap.Empty(PeggedAsset);

		LiquidAssetAmount missing = balances.GetAmountOrZero(IssuedAsset);

		Assert.True(balances.IsEmpty);
		Assert.Equal(0, balances.AssetCount);
		Assert.Empty(balances.GetAmounts());
		Assert.Equal(IssuedAsset, missing.AssetId);
		Assert.Equal(PeggedAsset, missing.PeggedAssetId);
		Assert.True(missing.IsZero);
	}

	[Fact]
	public void ConstructsCanonicalOrderIndependentOfInputOrder()
	{
		LiquidAssetAmount pegged = Amount(PeggedAsset, 1);
		LiquidAssetAmount issued = Amount(IssuedAsset, 2);
		LiquidAssetAmount other = Amount(OtherAsset, 3);

		LiquidAssetBalanceMap forward = LiquidAssetBalanceMap.FromAmounts(
			PeggedAsset,
			[other, pegged, issued]);
		LiquidAssetBalanceMap reverse = LiquidAssetBalanceMap.FromAmounts(
			PeggedAsset,
			[issued, pegged, other]);

		string[] expected = [PeggedAssetHex, IssuedAssetHex, OtherAssetHex];
		Assert.Equal(expected, forward.GetAmounts().Select(x => x.AssetId.CanonicalRpcHex));
		Assert.Equal(expected, reverse.GetAmounts().Select(x => x.AssetId.CanonicalRpcHex));
	}

	[Fact]
	public void AggregatesDuplicatesAndOmitsZeroEntries()
	{
		LiquidAssetBalanceMap balances = LiquidAssetBalanceMap.FromAmounts(
			PeggedAsset,
			[Amount(IssuedAsset, 3), Amount(OtherAsset, 0), Amount(IssuedAsset, 4)]);

		Assert.Equal(1, balances.AssetCount);
		Assert.Equal(7, balances.GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.False(balances.TryGetAmount(OtherAsset, out LiquidAssetAmount? zero));
		Assert.Null(zero);
	}

	[Fact]
	public void AddsNewAndExistingAssetsWithoutMutatingSource()
	{
		LiquidAssetBalanceMap empty = LiquidAssetBalanceMap.Empty(PeggedAsset);
		LiquidAssetBalanceMap one = empty.Add(Amount(IssuedAsset, 40));
		LiquidAssetBalanceMap two = one.Add(Amount(IssuedAsset, 2));
		LiquidAssetBalanceMap three = two.Add(Amount(OtherAsset, 9));

		Assert.True(empty.IsEmpty);
		Assert.Equal(40, one.GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.Equal(42, two.GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.Equal(1, two.AssetCount);
		Assert.Equal(2, three.AssetCount);
		Assert.Equal(9, three.GetAmountOrZero(OtherAsset).AtomicUnits);
	}

	[Fact]
	public void AddingOrSubtractingZeroReturnsDistinctUnchangedSnapshot()
	{
		LiquidAssetBalanceMap source = LiquidAssetBalanceMap.Empty(PeggedAsset).Add(Amount(IssuedAsset, 42));

		LiquidAssetBalanceMap afterAdd = source.Add(Amount(OtherAsset, 0));
		LiquidAssetBalanceMap afterSubtract = source.Subtract(Amount(OtherAsset, 0));

		Assert.NotSame(source, afterAdd);
		Assert.NotSame(source, afterSubtract);
		Assert.Single(afterAdd.GetAmounts());
		Assert.Single(afterSubtract.GetAmounts());
		Assert.Equal(42, afterAdd.GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.Equal(42, afterSubtract.GetAmountOrZero(IssuedAsset).AtomicUnits);
	}

	[Fact]
	public void SubtractsToPositiveAndZeroWithoutMutatingSource()
	{
		LiquidAssetBalanceMap source = LiquidAssetBalanceMap.Empty(PeggedAsset).Add(Amount(IssuedAsset, 42));

		LiquidAssetBalanceMap positive = source.Subtract(Amount(IssuedAsset, 2));
		LiquidAssetBalanceMap empty = positive.Subtract(Amount(IssuedAsset, 40));

		Assert.Equal(42, source.GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.Equal(40, positive.GetAmountOrZero(IssuedAsset).AtomicUnits);
		Assert.True(empty.IsEmpty);
		Assert.False(empty.TryGetAmount(IssuedAsset, out LiquidAssetAmount? removed));
		Assert.Null(removed);
	}

	[Fact]
	public void RejectsAbsentAssetAndUnderflowWithoutDisclosingValues()
	{
		LiquidAssetBalanceMap balances = LiquidAssetBalanceMap.Empty(PeggedAsset).Add(Amount(IssuedAsset, 41));

		var absent = Assert.Throws<OverflowException>(() => balances.Subtract(Amount(OtherAsset, 987_654_321)));
		var underflow = Assert.Throws<OverflowException>(() => balances.Subtract(Amount(IssuedAsset, 42)));

		Assert.DoesNotContain(OtherAssetHex, absent.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("987654321", absent.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(IssuedAssetHex, underflow.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("42", underflow.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void PreservesAmountRangeChecksDuringAggregation()
	{
		LiquidAssetBalanceMap peggedMaximum = LiquidAssetBalanceMap.Empty(PeggedAsset)
			.Add(Amount(PeggedAsset, LiquidAssetAmount.MaxPeggedAssetAtomicUnits));
		LiquidAssetBalanceMap issuedMaximum = LiquidAssetBalanceMap.Empty(PeggedAsset)
			.Add(Amount(IssuedAsset, long.MaxValue));

		Assert.Throws<OverflowException>(() => peggedMaximum.Add(Amount(PeggedAsset, 1)));
		Assert.Throws<OverflowException>(() => issuedMaximum.Add(Amount(IssuedAsset, 1)));
		Assert.Throws<OverflowException>(() => LiquidAssetBalanceMap.FromAmounts(
			PeggedAsset,
			[Amount(PeggedAsset, LiquidAssetAmount.MaxPeggedAssetAtomicUnits), Amount(PeggedAsset, 1)]));
	}

	[Fact]
	public void RejectsForeignPeggedAssetContextsWithoutDisclosingIdentity()
	{
		LiquidAssetAmount foreign = LiquidAssetAmount.Create(IssuedAsset, OtherPeggedAsset, 987_654_321);
		LiquidAssetBalanceMap balances = LiquidAssetBalanceMap.Empty(PeggedAsset).Add(Amount(IssuedAsset, 42));

		var construction = Assert.Throws<InvalidOperationException>(
			() => LiquidAssetBalanceMap.FromAmounts(PeggedAsset, [foreign]));
		var addition = Assert.Throws<InvalidOperationException>(() => balances.Add(foreign));
		var subtraction = Assert.Throws<InvalidOperationException>(() => balances.Subtract(foreign));

		foreach (Exception exception in new Exception[] { construction, addition, subtraction })
		{
			Assert.DoesNotContain(OtherPeggedAssetHex, exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(IssuedAssetHex, exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("987654321", exception.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ReportsPresentAndAbsentAssetsWithoutAmbiguousDefaults()
	{
		LiquidAssetBalanceMap balances = LiquidAssetBalanceMap.Empty(PeggedAsset).Add(Amount(IssuedAsset, 42));

		bool found = balances.TryGetAmount(IssuedAsset, out LiquidAssetAmount? present);
		bool missing = balances.TryGetAmount(OtherAsset, out LiquidAssetAmount? absent);

		Assert.True(found);
		Assert.NotNull(present);
		Assert.Equal(42, present.AtomicUnits);
		Assert.False(missing);
		Assert.Null(absent);
	}

	[Fact]
	public void ReturnsReadOnlySnapshotsThatCannotAffectMap()
	{
		LiquidAssetBalanceMap source = LiquidAssetBalanceMap.Empty(PeggedAsset).Add(Amount(IssuedAsset, 42));
		IReadOnlyList<LiquidAssetAmount> snapshot = source.GetAmounts();
		LiquidAssetBalanceMap updated = source.Add(Amount(OtherAsset, 9));
		var mutableView = Assert.IsAssignableFrom<IList<LiquidAssetAmount>>(snapshot);

		Assert.True(mutableView.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableView.Add(Amount(OtherAsset, 5)));
		Assert.Single(snapshot);
		Assert.Single(source.GetAmounts());
		Assert.Equal(2, updated.AssetCount);
	}

	[Fact]
	public void RejectsNullArgumentsAndNullElements()
	{
		LiquidAssetBalanceMap balances = LiquidAssetBalanceMap.Empty(PeggedAsset);

		Assert.Throws<ArgumentNullException>(() => LiquidAssetBalanceMap.Empty(null!));
		Assert.Throws<ArgumentNullException>(() => LiquidAssetBalanceMap.FromAmounts(null!, []));
		Assert.Throws<ArgumentNullException>(() => LiquidAssetBalanceMap.FromAmounts(PeggedAsset, null!));
		Assert.Throws<ArgumentNullException>(() => LiquidAssetBalanceMap.FromAmounts(PeggedAsset, [null!]));
		Assert.Throws<ArgumentNullException>(() => balances.Add(null!));
		Assert.Throws<ArgumentNullException>(() => balances.Subtract(null!));
		Assert.Throws<ArgumentNullException>(() => balances.GetAmountOrZero(null!));
		Assert.Throws<ArgumentNullException>(() => balances.TryGetAmount(null!, out _));
	}

	[Fact]
	public void RedactsStringRepresentation()
	{
		LiquidAssetBalanceMap balances = LiquidAssetBalanceMap.Empty(PeggedAsset)
			.Add(Amount(IssuedAsset, 987_654_321));

		string text = balances.ToString();

		Assert.Equal(nameof(LiquidAssetBalanceMap), text);
		Assert.DoesNotContain(IssuedAssetHex, text, StringComparison.Ordinal);
		Assert.DoesNotContain(PeggedAssetHex, text, StringComparison.Ordinal);
		Assert.DoesNotContain("987654321", text, StringComparison.Ordinal);
	}

	private static LiquidAssetAmount Amount(LiquidAssetId assetId, long atomicUnits) =>
		LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits);
}
