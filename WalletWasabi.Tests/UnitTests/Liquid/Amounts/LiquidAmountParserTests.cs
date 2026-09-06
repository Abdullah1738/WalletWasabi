using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using System.Globalization;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Amounts;

public class LiquidAmountParserTests
{
	private static readonly LiquidAssetMetadata Precision2 = new(new string('a', 64), "FIX", "Fixture", 2);

	[Fact]
	public void ParsesReviewedTestAssetExactDecimalToAtomicUnits()
	{
		const string assetId = "38fca2d939696061a8f76d4e6b5eecd54e3b4221c846f24a6b279e79952850a5";
		var testnet = LiquidAssetMetadataRegistry.ForManifest(ElementsPublicNetworkManifest.LiquidTestnet);
		Assert.True(testnet.TryGet(assetId, out var metadata));
		var result = LiquidAmountParser.Parse("1.234", metadata);
		Assert.True(result.Success);
		Assert.Equal(1234, result.AtomicUnits);
		Assert.False(LiquidAmountParser.Parse("1.2340", metadata).Success);
		var mainnet = LiquidAssetMetadataRegistry.ForManifest(ElementsPublicNetworkManifest.LiquidMainnet);
		Assert.False(mainnet.TryGet(assetId, out var unknown));
		Assert.False(LiquidAmountParser.Parse("1.234", unknown).Success);
		Assert.Equal(1234, LiquidAmountParser.Parse("1234", unknown).AtomicUnits);
	}

	[Theory]
	[InlineData("12.34", 1234)]
	[InlineData("12", 1200)]
	[InlineData("0.01", 1)]
	[InlineData("92233720368547758.07", long.MaxValue)]
	[InlineData("000.10", 10)]
	[InlineData("0", 0)]
	[InlineData("0.00", 0)]
	public void ParsesExactInvariantDecimals(string text, long expected)
	{
		var result = LiquidAmountParser.Parse(text, Precision2);
		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Equal(expected, result.AtomicUnits);
	}

	[Theory]
	[InlineData("1.234")]
	[InlineData("1e2")]
	[InlineData("-1")]
	[InlineData("1,2")]
	[InlineData("1.2 ")]
	[InlineData("+1")]
	[InlineData("1E2")]
	[InlineData(".1")]
	[InlineData("1.")]
	[InlineData("1.2.3")]
	[InlineData("1.230")]
	[InlineData("")]
	[InlineData(null)]
	[InlineData("\u0661")]
	[InlineData("9_000")]
	[InlineData(" 1")]
	[InlineData("1\n")]
	[InlineData("-0")]
	public void RejectsAmbiguousOrInexactText(string? text) => Assert.False(LiquidAmountParser.Parse(text, Precision2).Success);

	[Fact]
	public void UnknownAssetAcceptsOnlyAtomicIntegers()
	{
		Assert.Equal(42, LiquidAmountParser.Parse("42", null).AtomicUnits);
		Assert.False(LiquidAmountParser.Parse("42.0", null).Success);
		Assert.Equal(long.MaxValue, LiquidAmountParser.Parse("9223372036854775807", null).AtomicUnits);
		Assert.False(LiquidAmountParser.Parse("9223372036854775808", null).Success);
	}

	[Fact]
	public void RejectsOverflow() => Assert.False(LiquidAmountParser.Parse("92233720368547758.08", Precision2).Success);

	[Theory]
	[InlineData(",", ".")]
	[InlineData(".", ",")]
	[InlineData("/", " ")]
	public void ParsingDoesNotUseCurrentCulture(string decimalSeparator, string groupSeparator)
	{
		var previous = CultureInfo.CurrentCulture;
		try
		{
			var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
			culture.NumberFormat.NumberDecimalSeparator = decimalSeparator;
			culture.NumberFormat.NumberGroupSeparator = groupSeparator;
			CultureInfo.CurrentCulture = culture;
			Assert.Equal(1234, LiquidAmountParser.Parse("12.34", Precision2).AtomicUnits);
			Assert.False(LiquidAmountParser.Parse("12,34", Precision2).Success);
		}
		finally { CultureInfo.CurrentCulture = previous; }
	}

	[Theory]
	[InlineData(0, "9223372036854775807", long.MaxValue)]
	[InlineData(8, "0.00000001", 1)]
	[InlineData(18, "9.223372036854775807", long.MaxValue)]
	public void SupportsPrecisionBoundaries(int precision, string text, long atomic)
	{
		var metadata = new LiquidAssetMetadata(new string('b', 64), "FIX", "Fixture", precision);
		var result = LiquidAmountParser.Parse(text, metadata);
		Assert.True(result.Success);
		Assert.Equal(atomic, result.AtomicUnits);
	}

	[Theory]
	[InlineData(0, "1.0")]
	[InlineData(0, "9223372036854775808")]
	[InlineData(2, "92233720368547759")]
	[InlineData(8, "0.000000001")]
	[InlineData(8, "92233720368.54775808")]
	[InlineData(8, "92233720369")]
	[InlineData(18, "9.223372036854775808")]
	public void InvalidAmountsNeverExposePartialAtomicUnits(int precision, string text)
	{
		var result = LiquidAmountParser.Parse(text, new(new string('b', 64), "FIX", "Fixture", precision));
		Assert.False(result.Success);
		Assert.Equal(0, result.AtomicUnits);
		Assert.NotNull(result.Error);
	}
}
