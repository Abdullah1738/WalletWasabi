using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Network;

public class ElementsPublicNetworkManifestTests
{
	private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

	[Fact]
	public void LoadsExactReviewedPublicManifests()
	{
		ElementsPublicNetworkManifest mainnet = ElementsPublicNetworkManifest.LiquidMainnet;
		Assert.Equal(2, mainnet.ManifestSchema);
		Assert.Equal("LIQUID_MAINNET", mainnet.ProductNetworkId);
		Assert.Equal("liquidv1", mainnet.ChainRpcName);
		Assert.Equal("1466275836220db2944ca059a3a10ef6fd2ea684b0688d2c379296888a206003", mainnet.GenesisBlockHash);
		Assert.Equal("6f0279e9ed041c3d710a9f57d0c02928416460c4b722ae3457a11eec381c526d", mainnet.PeggedAssetId);
		Assert.Equal(mainnet.PeggedAssetId, mainnet.RequiredFeeAssetId);
		Assert.Equal("000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f", mainnet.ParentGenesisHash);
		Assert.True(mainnet.HasParentChain);
		Assert.True(mainnet.EnforcePak);
		Assert.Equal(new ElementsAddressEncodingProfile(57, 39, 12, "ex", "lq"), mainnet.AddressEncoding);
		Assert.Equal(7042, mainnet.DefaultPort);

		ElementsPublicNetworkManifest testnet = ElementsPublicNetworkManifest.LiquidTestnet;
		Assert.Equal("LIQUID_TESTNET", testnet.ProductNetworkId);
		Assert.Equal("liquidtestnet", testnet.ChainRpcName);
		Assert.Equal("a771da8e52ee6ad581ed1e9a99825e5b3b7992225534eaa2ae23244fe26ab1c1", testnet.GenesisBlockHash);
		Assert.Equal("144c654344aa716d6f3abcc1ca90e5641e4e2a7f633bc09fe3baf64585819a49", testnet.PeggedAssetId);
		Assert.Equal(testnet.PeggedAssetId, testnet.RequiredFeeAssetId);
		Assert.Equal(ZeroHash, testnet.ParentGenesisHash);
		Assert.False(testnet.HasParentChain);
		Assert.False(testnet.EnforcePak);
		Assert.Equal(new ElementsAddressEncodingProfile(36, 19, 23, "tex", "tlq"), testnet.AddressEncoding);
		Assert.Equal(18891, testnet.DefaultPort);

		Assert.Equal("23.3.3", mainnet.ElementsVersion);
		Assert.Equal(230303, mainnet.ElementsNumericVersion);
		Assert.Equal(70016, mainnet.ElementsProtocolVersion);
		Assert.Equal("/Elements Core:23.3.3/", mainnet.ExpectedSubversion);
		Assert.Equal("NODE-NETMANIFEST-001-SOURCE-ONLY-CT-INCOMPLETE-NO-GATE-CREDIT", mainnet.ScopeMarker);
		Assert.Equal("PUBLIC_CT_SENTINEL_INCOMPLETE", mainnet.CtFixtureEligibility);
	}

	[Fact]
	public void LoadsExactReviewedControlledRegtestManifest()
	{
		ElementsPublicNetworkManifest regtest = ElementsPublicNetworkManifest.LiquidControlledRegtest;

		Assert.Equal(3, regtest.ManifestSchema);
		Assert.Equal("LIQUID_REGTEST_CONTROLLED", regtest.ProductNetworkId);
		Assert.Equal("elementsregtest", regtest.ChainRpcName);
		Assert.Equal("cd179c84c35f51825f20a3b91a18d45f0c53b5ceb744a5b6ef8f0babe809396f", regtest.GenesisBlockHash);
		Assert.Equal("b2e15d0d7a0c94e4e2ce0fe6e8691b9e451377f6e46e8045a86f7c4b5d4f0f23", regtest.PeggedAssetId);
		Assert.Equal(regtest.PeggedAssetId, regtest.RequiredFeeAssetId);
		Assert.Equal("0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206", regtest.ParentGenesisHash);
		Assert.True(regtest.HasParentChain);
		Assert.False(regtest.EnforcePak);
		Assert.Equal(new ElementsAddressEncodingProfile(235, 75, 4, "ert", "el"), regtest.AddressEncoding);
		Assert.Equal(18444, regtest.DefaultPort);
		Assert.Equal("28.99.0", regtest.ElementsVersion);
		Assert.Equal(289900, regtest.ElementsNumericVersion);
		Assert.Equal(70016, regtest.ElementsProtocolVersion);
		Assert.Equal("/Elements Core:28.99.0/", regtest.ExpectedSubversion);
		Assert.Equal("5319f20e", regtest.MessageStart);
		Assert.Equal("4ae81572f06e1b88fd5ced7a1a000945432e83e1551e6f721ee9c00b8cc33260", regtest.FedpegScriptSha256);
		Assert.Equal("CONTROLLED-REGTEST-MANIFEST-001-LOCAL-DEMO-ONLY-NO-GATE-CREDIT", regtest.ScopeMarker);
		Assert.Equal("CONTROLLED_REGTEST_CT_FIXTURE_INCOMPLETE", regtest.CtFixtureEligibility);
	}

	[Theory]
	[InlineData(2, 1260, "7a99aca826aeefd659c4af97347ae302c72c3093c07697d8baa4dc03139cb908", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b")]
	[InlineData(2, 1274, "9fc3e29fe188d63826c18a9f8ab59b42b83e47f57fbf16ca3842d970c16994f1", "e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20")]
	[InlineData(3, 1371, "76844ce95da0c91bab521e9ea41631e53469f1ae5b826d4652c3a14d97a71679", "71115e296e89e5f9161a74649f3a16fa2bb7ed9cf59d42ec203750b8a54350da")]
	public void PreservesCanonicalBytesAndDomainSeparatedIdentity(
		int schema,
		int expectedLength,
		string expectedCborSha256,
		string expectedManifestId)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(expectedManifestId);
		Assert.Equal(schema, manifest.ManifestSchema);
		byte[] canonicalCbor = manifest.ExportCanonicalCbor();
		byte[] domain = "wasabi-liquid/network-manifest/v1\0"u8.ToArray();
		byte[] idInput = new byte[domain.Length + canonicalCbor.Length];
		domain.CopyTo(idInput, 0);
		canonicalCbor.CopyTo(idInput, domain.Length);

		Assert.Equal(expectedLength, canonicalCbor.Length);
		Assert.Equal(expectedCborSha256, LowerHex(SHA256.HashData(canonicalCbor)));
		Assert.Equal(expectedManifestId, LowerHex(SHA256.HashData(idInput)));
		Assert.Equal(expectedCborSha256, manifest.CanonicalCborSha256);
		Assert.Equal(expectedManifestId, manifest.ManifestId);
		Assert.Same(manifest, ElementsPublicNetworkManifest.ParseReviewed(canonicalCbor));
	}

	[Fact]
	public void RejectsEverySingleByteMutation()
	{
		foreach (ElementsPublicNetworkManifest manifest in new[]
		{
			ElementsPublicNetworkManifest.LiquidMainnet,
			ElementsPublicNetworkManifest.LiquidTestnet,
			ElementsPublicNetworkManifest.LiquidControlledRegtest,
		})
		{
			byte[] canonicalCbor = manifest.ExportCanonicalCbor();
			for (int index = 0; index < canonicalCbor.Length; index++)
			{
				byte[] mutated = [.. canonicalCbor];
				mutated[index] ^= 1;
				Assert.Throws<ElementsNetworkManifestException>(
					() => ElementsPublicNetworkManifest.ParseReviewed(mutated));
			}
		}
	}

	[Fact]
	public void RejectsUnreviewedOrOutOfBoundManifestBytes()
	{
		Assert.Throws<ElementsNetworkManifestException>(
			() => ElementsPublicNetworkManifest.ParseReviewed([]));
		Assert.Throws<ElementsNetworkManifestException>(
			() => ElementsPublicNetworkManifest.ParseReviewed(new byte[4097]));

		byte[] prefixed = [0, .. ElementsPublicNetworkManifest.LiquidMainnet.ExportCanonicalCbor()];
		byte[] trailed = [.. ElementsPublicNetworkManifest.LiquidTestnet.ExportCanonicalCbor(), 0];
		Assert.Throws<ElementsNetworkManifestException>(
			() => ElementsPublicNetworkManifest.ParseReviewed(prefixed));
		Assert.Throws<ElementsNetworkManifestException>(
			() => ElementsPublicNetworkManifest.ParseReviewed(trailed));
	}

	[Fact]
	public void ExportedBytesCannotMutateTheCatalog()
	{
		byte[] exported = ElementsPublicNetworkManifest.LiquidMainnet.ExportCanonicalCbor();
		exported[0] ^= 1;

		byte[] fresh = ElementsPublicNetworkManifest.LiquidMainnet.ExportCanonicalCbor();
		Assert.NotEqual(exported[0], fresh[0]);
		Assert.Equal("7a99aca826aeefd659c4af97347ae302c72c3093c07697d8baa4dc03139cb908", LowerHex(SHA256.HashData(fresh)));
	}

	[Fact]
	public void ReviewedCatalogIsExactlyThreeAndFailsClosedElsewhere()
	{
		ElementsPublicNetworkManifest[] catalog =
		[
			ElementsPublicNetworkManifest.LiquidMainnet,
			ElementsPublicNetworkManifest.LiquidTestnet,
			ElementsPublicNetworkManifest.LiquidControlledRegtest,
		];

		Assert.Equal(3, catalog.Select(manifest => manifest.ManifestId).Distinct(StringComparer.Ordinal).Count());
		Assert.Equal(3, catalog.Select(manifest => manifest.CanonicalCborSha256).Distinct(StringComparer.Ordinal).Count());
		Assert.Equal(3, catalog.Select(manifest => LowerHex(SHA256.HashData(manifest.ExportCanonicalCbor()))).Distinct(StringComparer.Ordinal).Count());
		Assert.Equal([2, 2, 3], catalog.OrderBy(manifest => manifest.ManifestSchema).Select(manifest => manifest.ManifestSchema).ToArray());

		foreach (ElementsPublicNetworkManifest manifest in catalog)
		{
			Assert.Same(manifest, ElementsPublicNetworkManifest.GetByManifestId(manifest.ManifestId));
		}
		Assert.Throws<ElementsNetworkManifestException>(
			() => ElementsPublicNetworkManifest.GetByManifestId(ZeroHash));
		Assert.Throws<ElementsNetworkManifestException>(
			() => ElementsPublicNetworkManifest.GetByManifestId(ZeroHash[..^1] + "1"));
		Assert.Throws<ArgumentException>(
			() => ElementsPublicNetworkManifest.GetByManifestId(""));
	}

	[Fact]
	public void BindsExactLiquidTestnetSourceIdentityObservation()
	{
		ElementsNodeStatus status = LiquidTestnetStatus();

		ElementsManifestBoundObservation observation =
			ElementsPublicNetworkManifest.LiquidTestnet.BindNodeObservation(status);

		Assert.Same(ElementsPublicNetworkManifest.LiquidTestnet, observation.Manifest);
		Assert.Same(status, observation.NodeStatus);
		Assert.Equal(ElementsNodeManifestBindingLevel.SelfReportedManifestTupleObservationOnly, observation.BindingLevel);
		Assert.False(observation.HasArtifactSourceAttestation);
		Assert.False(observation.HasEffectiveFeeAssetObservation);
		Assert.False(observation.HasAtomicGenerationObservation);
		Assert.False(observation.HasRuntimeQualification);
		Assert.False(observation.HasPublicCtFixtureQualification);
	}

	[Fact]
	public void RejectsEachManifestBoundFieldMismatchWithoutValues()
	{
		ElementsNodeStatus valid = LiquidTestnetStatus();
		var mutations = new (ElementsNodeStatus Status, string Field)[]
		{
			(valid with { Chain = "liquidv1" }, "chain"),
			(valid with { GenesisBlockHash = Hash(0x31) }, "genesis_block_hash"),
			(valid with { PeggedAsset = Hash(0x32) }, "pegged_asset"),
			(valid with { ParentGenesisBlockHash = Hash(0x33) }, "parent_blockhash"),
			(valid with { EnforcePak = true }, "enforce_pak"),
			(valid with { Version = 230304 }, "version"),
			(valid with { ProtocolVersion = 70017 }, "protocolversion"),
			(valid with { Subversion = "/Elements Core:23.3.4/" }, "subversion"),
			(valid with { FedpegScript = "52" }, "fedpeg_script_sha256"),
		};

		foreach ((ElementsNodeStatus status, string field) in mutations)
		{
			var exception = Assert.Throws<ElementsNodeMismatchException>(
				() => ElementsPublicNetworkManifest.LiquidTestnet.BindNodeObservation(status));
			Assert.Equal([field], exception.MismatchedFields);
			Assert.DoesNotContain(valid.PeggedAsset, exception.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void RejectsMalformedManifestBoundAssetWithoutDisclosingIt()
	{
		ElementsNodeStatus valid = LiquidTestnetStatus();
		string malformedAsset = valid.PeggedAsset.ToUpperInvariant();
		ElementsNodeStatus malformed = valid with { PeggedAsset = malformedAsset };

		var exception = Assert.Throws<ElementsNodeMismatchException>(
			() => ElementsPublicNetworkManifest.LiquidTestnet.BindNodeObservation(malformed));

		Assert.Equal(["pegged_asset"], exception.MismatchedFields);
		Assert.DoesNotContain(malformedAsset, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(valid.PeggedAsset, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void DynamicNodeObservationsDoNotChangeManifestBinding()
	{
		ElementsNodeStatus changed = LiquidTestnetStatus() with
		{
			Blocks = 90,
			Headers = 100,
			BestBlockHash = Hash(0x44),
			InitialBlockDownload = true,
			Pruned = true,
			TrimHeaders = true,
			BlockchainWarningsPresent = true,
			NetworkActive = false,
			LocalRelay = false,
			NetworkWarningsPresent = true,
			PeginConfirmationDepth = 144,
		};

		ElementsManifestBoundObservation observation =
			ElementsPublicNetworkManifest.LiquidTestnet.BindNodeObservation(changed);

		Assert.Equal(ElementsPublicNetworkManifest.LiquidTestnet.ManifestId, observation.Manifest.ManifestId);
		Assert.False(observation.NodeStatus.HasSynchronizedTipObservation);
		Assert.False(observation.NodeStatus.HasCompleteArchiveObservation);
		Assert.False(observation.NodeStatus.HasClearWarningObservation);
		Assert.False(observation.NodeStatus.HasOnlineNetworkObservation);
	}

	private static ElementsNodeStatus LiquidTestnetStatus() => new(
		Chain: "liquidtestnet",
		Blocks: 100,
		Headers: 100,
		BestBlockHash: Hash(0x22),
		GenesisBlockHash: "a771da8e52ee6ad581ed1e9a99825e5b3b7992225534eaa2ae23244fe26ab1c1",
		InitialBlockDownload: false,
		Pruned: false,
		TrimHeaders: false,
		BlockchainWarningsPresent: false,
		NetworkActive: true,
		LocalRelay: true,
		NetworkWarningsPresent: false,
		FedpegScript: "51",
		PeggedAsset: "144c654344aa716d6f3abcc1ca90e5641e4e2a7f633bc09fe3baf64585819a49",
		ParentGenesisBlockHash: ZeroHash,
		PeginConfirmationDepth: 0,
		EnforcePak: false,
		Version: 230303,
		ProtocolVersion: 70016,
		Subversion: "/Elements Core:23.3.3/");

	private static string Hash(byte value) =>
		Convert.ToHexString(Enumerable.Repeat(value, 32).ToArray()).ToLower(CultureInfo.InvariantCulture);

	private static string LowerHex(ReadOnlySpan<byte> value) =>
		Convert.ToHexString(value).ToLower(CultureInfo.InvariantCulture);
}
