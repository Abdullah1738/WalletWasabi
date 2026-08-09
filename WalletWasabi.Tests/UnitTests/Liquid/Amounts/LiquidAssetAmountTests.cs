using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Amounts;

public class LiquidAssetAmountTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string OtherPeggedAssetHex = "4444444444444444444444444444444444444444444444444444444444444444";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(LiquidAssetAmount.MaxPeggedAssetAtomicUnits)]
	public void CreatesPeggedAssetAmountsWithinElementsRange(long atomicUnits)
	{
		LiquidAssetAmount amount = LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, atomicUnits);

		Assert.Equal(atomicUnits, amount.AtomicUnits);
		Assert.True(amount.IsPeggedAsset);
		Assert.Equal(atomicUnits == 0, amount.IsZero);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(LiquidAssetAmount.MaxPeggedAssetAtomicUnits + 1)]
	public void RejectsInvalidPeggedAssetAmountsWithoutDisclosingValue(long atomicUnits)
	{
		var exception = Assert.Throws<ArgumentOutOfRangeException>(
			() => LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, atomicUnits));

		Assert.DoesNotContain(atomicUnits.ToString(), exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(PeggedAssetHex, exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(LiquidAssetAmount.MaxPeggedAssetAtomicUnits + 1)]
	[InlineData(long.MaxValue)]
	public void CreatesIssuedAssetAmountsAcrossFullNonnegativeLongRange(long atomicUnits)
	{
		LiquidAssetAmount amount = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, atomicUnits);

		Assert.Equal(atomicUnits, amount.AtomicUnits);
		Assert.False(amount.IsPeggedAsset);
	}

	[Fact]
	public void AddsSameAssetAmountsAndPreservesContext()
	{
		LiquidAssetAmount zero = LiquidAssetAmount.Zero(IssuedAsset, PeggedAsset);
		LiquidAssetAmount amount = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 41);

		LiquidAssetAmount unchanged = amount.Add(zero);
		LiquidAssetAmount sum = amount.Add(LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 1));

		Assert.Equal(amount, unchanged);
		Assert.NotSame(amount, unchanged);
		Assert.Equal(42, sum.AtomicUnits);
		Assert.Equal(IssuedAsset, sum.AssetId);
		Assert.Equal(PeggedAsset, sum.PeggedAssetId);
	}

	[Fact]
	public void RejectsPeggedAssetAdditionBeyondElementsRange()
	{
		LiquidAssetAmount maximum = LiquidAssetAmount.Create(
			PeggedAsset,
			PeggedAsset,
			LiquidAssetAmount.MaxPeggedAssetAtomicUnits);

		var exception = Assert.Throws<OverflowException>(
			() => maximum.Add(LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 1)));

		Assert.DoesNotContain(PeggedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(LiquidAssetAmount.MaxPeggedAssetAtomicUnits.ToString(), exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RejectsIssuedAssetAdditionBeyondLongRange()
	{
		LiquidAssetAmount maximum = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, long.MaxValue);

		var exception = Assert.Throws<OverflowException>(
			() => maximum.Add(LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 1)));

		Assert.DoesNotContain(IssuedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(long.MaxValue.ToString(), exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void SubtractsSameAssetAmountsWithoutAllowingNegativeResults()
	{
		LiquidAssetAmount amount = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 42);

		LiquidAssetAmount positive = amount.Subtract(LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 2));
		LiquidAssetAmount zero = amount.Subtract(amount);
		var exception = Assert.Throws<OverflowException>(
			() => amount.Subtract(LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 43)));

		Assert.Equal(40, positive.AtomicUnits);
		Assert.True(zero.IsZero);
		Assert.DoesNotContain(IssuedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("43", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RejectsArithmeticAcrossDifferentAssets(bool subtract)
	{
		LiquidAssetAmount left = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 41);
		LiquidAssetAmount right = LiquidAssetAmount.Create(
			LiquidAssetId.ParseRpcHex(OtherAssetHex),
			PeggedAsset,
			1);

		var exception = Assert.Throws<InvalidOperationException>(
			() => subtract ? left.Subtract(right) : left.Add(right));

		Assert.DoesNotContain(IssuedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(OtherAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("41", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RejectsArithmeticAcrossDifferentPeggedAssetContexts(bool subtract)
	{
		LiquidAssetAmount left = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 41);
		LiquidAssetAmount right = LiquidAssetAmount.Create(
			IssuedAsset,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			1);

		var exception = Assert.Throws<InvalidOperationException>(
			() => subtract ? left.Subtract(right) : left.Add(right));

		Assert.DoesNotContain(IssuedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(OtherPeggedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("41", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void UsesValueEqualityAcrossTheCompleteAccountingContext()
	{
		LiquidAssetAmount amount = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 42);
		LiquidAssetAmount equal = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 42);
		LiquidAssetAmount differentAsset = LiquidAssetAmount.Create(
			LiquidAssetId.ParseRpcHex(OtherAssetHex),
			PeggedAsset,
			42);
		LiquidAssetAmount differentPeggedAsset = LiquidAssetAmount.Create(
			IssuedAsset,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			42);
		LiquidAssetAmount differentAmount = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 43);

		Assert.Equal(amount, equal);
		Assert.Equal(amount.GetHashCode(), equal.GetHashCode());
		Assert.NotEqual(amount, differentAsset);
		Assert.NotEqual(amount, differentPeggedAsset);
		Assert.NotEqual(amount, differentAmount);
	}

	[Fact]
	public void RejectsNullIdentitiesAndNullArithmeticOperand()
	{
		Assert.Throws<ArgumentNullException>(() => LiquidAssetAmount.Create(null!, PeggedAsset, 0));
		Assert.Throws<ArgumentNullException>(() => LiquidAssetAmount.Create(IssuedAsset, null!, 0));

		LiquidAssetAmount amount = LiquidAssetAmount.Zero(IssuedAsset, PeggedAsset);
		Assert.Throws<ArgumentNullException>(() => amount.Add(null!));
		Assert.Throws<ArgumentNullException>(() => amount.Subtract(null!));
	}

	[Fact]
	public void RedactsAmountAndAssetIdentityFromStringRepresentation()
	{
		LiquidAssetAmount amount = LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 987_654_321);

		string text = amount.ToString();

		Assert.Equal(nameof(LiquidAssetAmount), text);
		Assert.DoesNotContain(IssuedAssetHex, text, StringComparison.Ordinal);
		Assert.DoesNotContain(PeggedAssetHex, text, StringComparison.Ordinal);
		Assert.DoesNotContain("987654321", text, StringComparison.Ordinal);
	}
}
