using WalletWasabi.Liquid.Assets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Assets;

public class LiquidAssetIdTests
{
	private const string MainnetLbtc = "6f0279e9ed041c3d710a9f57d0c02928416460c4b722ae3457a11eec381c526d";
	private const string TestnetLbtc = "144c654344aa716d6f3abcc1ca90e5641e4e2a7f633bc09fe3baf64585819a49";

	[Theory]
	[InlineData(MainnetLbtc)]
	[InlineData(TestnetLbtc)]
	public void ParsesAndPreservesCanonicalRpcHex(string canonicalRpcHex)
	{
		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(canonicalRpcHex);

		Assert.Equal(canonicalRpcHex, assetId.CanonicalRpcHex);
		Assert.Equal(canonicalRpcHex, assetId.ToString());
	}

	[Fact]
	public void UsesCanonicalIdentityForEquality()
	{
		LiquidAssetId first = LiquidAssetId.ParseRpcHex(MainnetLbtc);
		LiquidAssetId same = LiquidAssetId.ParseRpcHex(MainnetLbtc);
		LiquidAssetId different = LiquidAssetId.ParseRpcHex(TestnetLbtc);

		Assert.Equal(first, same);
		Assert.NotEqual(first, different);
	}

	[Fact]
	public void RejectsNoncanonicalOrZeroValuesWithoutNormalization()
	{
		Assert.Throws<ArgumentNullException>(() => LiquidAssetId.ParseRpcHex(null!));

		string[] invalidValues =
		[
			"",
			new string('a', 63),
			new string('a', 65),
			MainnetLbtc.ToUpperInvariant(),
			$"0x{MainnetLbtc}",
			$" {MainnetLbtc}",
			$"{MainnetLbtc} ",
			$"{MainnetLbtc[..^1]}g",
			new string('0', 64),
		];

		foreach (string invalidValue in invalidValues)
		{
			var exception = Assert.Throws<ArgumentException>(() => LiquidAssetId.ParseRpcHex(invalidValue));
			if (invalidValue.Length > 0)
			{
				Assert.DoesNotContain(invalidValue, exception.Message, StringComparison.Ordinal);
			}
		}
	}
}
