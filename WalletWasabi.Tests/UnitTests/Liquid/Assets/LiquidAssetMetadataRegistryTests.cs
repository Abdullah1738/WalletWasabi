using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Assets;

public class LiquidAssetMetadataRegistryTests
{
	private const string TestnetAssetId = "38fca2d939696061a8f76d4e6b5eecd54e3b4221c846f24a6b279e79952850a5";
	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	[Fact]
	public void BuiltInRegistryPreservesProtocolPeggedMetadataAndUnknownFallback()
	{
		var registry = LiquidAssetMetadataRegistry.ForManifest(ElementsPublicNetworkManifest.LiquidTestnet);
		Assert.True(registry.TryGet(registry.PeggedAssetId, out var metadata));
		Assert.Equal("L-BTC", metadata.Ticker);
		Assert.Equal("Liquid Bitcoin", metadata.Name);
		Assert.Equal(8, metadata.Precision);
		Assert.False(registry.TryGet(new string('a', 64), out _));
	}

	[Fact]
	public void ReviewedTestnetSnapshotContainsTestMetadataAndProvenance()
	{
		var registry = LiquidAssetMetadataRegistry.ForManifest(ElementsPublicNetworkManifest.LiquidTestnet);
		Assert.True(registry.TryGet(TestnetAssetId, out var metadata));
		Assert.Equal("TEST", metadata.Ticker);
		Assert.Equal("Testnet Asset", metadata.Name);
		Assert.Equal(3, metadata.Precision);
		Assert.Equal(TestnetAssetId, metadata.AssetIdHex);
		Assert.Equal("Blockstream/asset_registry_testnet_db", LiquidTestnetAssetSnapshot.Source);
		Assert.Equal("e07ca133ed964a5978cd57b836f2eacb342df588", LiquidTestnetAssetSnapshot.Revision);
		Assert.Equal($"38/{TestnetAssetId}.json", LiquidTestnetAssetSnapshot.SourcePath);
		Assert.Equal("8c4f129205a2ea6a290826eb7121dc103bf5bb94dc9b3ebe210257b00f63dde2", LiquidTestnetAssetSnapshot.SourceSha256);
		Assert.Equal(LiquidTestnetAssetSnapshot.SourceSha256,
			Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LiquidTestnetAssetSnapshot.SourceJson))));
		using var source = JsonDocument.Parse(LiquidTestnetAssetSnapshot.SourceJson);
		Assert.Equal(metadata.AssetIdHex, source.RootElement.GetProperty("asset_id").GetString());
		foreach (var fields in new[] { source.RootElement, source.RootElement.GetProperty("contract") })
		{
			Assert.Equal(metadata.Ticker, fields.GetProperty("ticker").GetString());
			Assert.Equal(metadata.Name, fields.GetProperty("name").GetString());
			Assert.Equal(metadata.Precision, fields.GetProperty("precision").GetInt32());
		}
	}

	[Fact]
	public void TestMetadataNeverAppearsOnMainnetOrUnknownFallback()
	{
		var mainnet = LiquidAssetMetadataRegistry.ForManifest(ElementsPublicNetworkManifest.LiquidMainnet);
		Assert.False(mainnet.TryGet(TestnetAssetId, out _));
		Assert.False(mainnet.TryGet(new string('a', 64), out _));
		Assert.False(LiquidAssetMetadataRegistry.ForManifest(Manifest).TryGet(TestnetAssetId.ToUpperInvariant(), out _));
	}

	[Fact]
	public void InjectedIssuedMetadataIsKeyedToManifest()
	{
		var registry = new LiquidAssetMetadataRegistry(Manifest, Manifest.ManifestId, [new(new string('a', 64), "FIX", "Fixture", 2)]);
		Assert.Equal(ElementsPublicNetworkManifest.LiquidTestnet.ManifestId, registry.NetworkManifestId);
		Assert.True(registry.TryGet(new string('a', 64), out var metadata));
		Assert.Equal(2, metadata.Precision);
	}

	[Fact]
	public void SnapshotIsCopiedAndDoesNotLeakAcrossNetworks()
	{
		var entries = new[] { new LiquidAssetMetadata(new string('a', 64), "FIX", "Fixture", 2) };
		var registry = new LiquidAssetMetadataRegistry(Manifest, Manifest.ManifestId, entries);
		entries[0] = new(new string('b', 64), "FIX", "Another fixture", 0);
		Assert.True(registry.TryGet(new string('a', 64), out _));
		Assert.False(registry.TryGet(new string('b', 64), out _));
		var mainnet = LiquidAssetMetadataRegistry.ForManifest(ElementsPublicNetworkManifest.LiquidMainnet);
		Assert.NotEqual(mainnet.NetworkManifestId, registry.NetworkManifestId);
		Assert.False(mainnet.TryGet(registry.PeggedAssetId, out _));
		Assert.False(mainnet.TryGet(new string('a', 64), out _));
	}

	[Fact]
	public void RejectsDuplicatesAndPeggedOverridesButNotDuplicateTickers()
	{
		var first = new LiquidAssetMetadata(new string('a', 64), "FIX", "Fixture", 2);
		Assert.Throws<ArgumentException>(() => new LiquidAssetMetadataRegistry(Manifest, Manifest.ManifestId, [first, first]));
		Assert.Throws<ArgumentException>(() => new LiquidAssetMetadataRegistry(Manifest, Manifest.ManifestId,
			[first, new(first.AssetIdHex, "OTHER", "Conflicting metadata", 8)]));
		Assert.Throws<ArgumentException>(() => new LiquidAssetMetadataRegistry(Manifest, Manifest.ManifestId,
			[new(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId, "FAKE", "Not pegged", 2)]));
		var registry = new LiquidAssetMetadataRegistry(Manifest, Manifest.ManifestId,
			[first, new(new string('b', 64), "FIX", "Different asset", 0)]);
		Assert.True(registry.TryGet(new string('a', 64), out _));
		Assert.True(registry.TryGet(new string('b', 64), out _));
	}

	[Fact]
	public void RejectsForeignSnapshotBeforeAcceptingEntries()
	{
		Assert.Throws<ArgumentException>(() => new LiquidAssetMetadataRegistry(Manifest,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId, [new(new string('a', 64), "FIX", "Fixture", 2)]));
		Assert.Throws<ArgumentException>(() => new LiquidAssetMetadataRegistry(Manifest, "", []));
	}

	[Fact]
	public void ManifestAndMetadataAreImmutable()
	{
		var registry = LiquidAssetMetadataRegistry.ForManifest(Manifest);
		var exported = Manifest.ExportCanonicalCbor();
		Array.Clear(exported);
		Assert.Equal(Manifest.ManifestId, ElementsPublicNetworkManifest.ParseReviewed(Manifest.ExportCanonicalCbor()).ManifestId);
		Assert.True(registry.TryGet(Manifest.PeggedAssetId, out var metadata));
		Assert.Equal(Manifest.PeggedAssetId, metadata.AssetIdHex);
		Assert.All(typeof(LiquidAssetMetadata).GetProperties(), property => Assert.Null(property.SetMethod));
		Assert.All(typeof(LiquidAssetMetadataRegistry).GetProperties(), property => Assert.Null(property.SetMethod));
		Assert.False(registry.TryGet(Manifest.PeggedAssetId.ToUpperInvariant(), out _));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(19)]
	public void RejectsUnsupportedPrecision(int precision) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidAssetMetadata(new string('a', 64), "FIX", "Fixture", precision));

	[Theory]
	[InlineData("ABC")]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
	public void RequiresCanonicalAssetIds(string id) => Assert.Throws<ArgumentException>(() => new LiquidAssetMetadata(id, "FIX", "Fixture", 2));
}
