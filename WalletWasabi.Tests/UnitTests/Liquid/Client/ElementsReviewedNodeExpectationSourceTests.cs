using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class ElementsReviewedNodeExpectationSourceTests
{
	private const string MainnetScript = "745c87635b21020e0338c96a8870479f2396c373cc7696ba124e8635d41b0ea581112b678172612102675333a4e4b8fb51d9d4e22fa5a8eaced3fdac8a8cbf9be8c030f75712e6af992102896807d54bc55c24981f24a453c60ad3e8993d693732288068a23df3d9f50d4821029e51a5ef5db3137051de8323b001749932f2ff0d34c82e96a2c2461de96ae56c2102a4e1a9638d46923272c266631d94d36bdb03a64ee0e14c7518e49d2f29bc40102102f8a00b269f8c5e59c67d36db3cdc11b11b21f64b4bffb2815e9100d9aa8daf072103079e252e85abffd3c401a69b087e590a9b86f33f574f08129ccbd3521ecf516b2103111cf405b627e22135b3b3733a4a34aa5723fb0f58379a16d32861bf576b0ec2210318f331b3e5d38156da6633b31929c5b220349859cc9ca3d33fb4e68aa08401742103230dae6b4ac93480aeab26d000841298e3b8f6157028e47b0897c1e025165de121035abff4281ff00660f99ab27bb53e6b33689c2cd8dcd364bc3c90ca5aea0d71a62103bd45cddfacf2083b14310ae4a84e25de61e451637346325222747b157446614c2103cc297026b06c71cbfa52089149157b5ff23de027ac5ab781800a578192d175462103d3bde5d63bdb3a6379b461be64dad45eabff42f758543a9645afd42f6d4248282103ed1e8d5109c9ed66f7941bc53cc71137baa76d50d274bda8d5e8ffbd6e61fe9a5f6702c00fb275522103aab896d53a8e7d6433137bbba940f9c521e085dd07e60994579b64a6d992cf79210291b7d0b1b692f8f524516ed950872e5da10fb1b808b5a526dedc6fed1cf29807210386aa9372fbab374593466bc5451dc59954e90787f08060964d95c87ef34ca5bb5368ae";
	private const string MainnetHash = "a9112b9eb2b15a2ef451e8598f326788342f78e6c5fa93c0be96da2d01a3c78b";
	private const string TestnetScript = "51";
	private const string TestnetHash = "4ae81572f06e1b88fd5ced7a1a000945432e83e1551e6f721ee9c00b8cc33260";
	private const string ControlledRegtestManifestId = "71115e296e89e5f9161a74649f3a16fa2bb7ed9cf59d42ec203750b8a54350da";

	[Theory]
	[InlineData("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", MainnetScript, MainnetHash, 100)]
	[InlineData("e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20", TestnetScript, TestnetHash, 8)]
	[InlineData(ControlledRegtestManifestId, TestnetScript, TestnetHash, 0)]
	public void BindsIndependentReviewedLiteralVector(string manifestId, string script, string expectedHash, int expectedDepth)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(manifestId);
		LiquidRpcProfile profile = Profile(manifest.ManifestId, manifest.ChainRpcName);

		ElementsNodeExpectation expectation = ElementsReviewedNodeExpectationSource.Bind(manifest, profile);

		Assert.Equal(script, expectation.FedpegScript);
		Assert.Equal(expectedDepth, expectation.PeginConfirmationDepth);
		Assert.Equal(expectedHash, LowerHex(SHA256.HashData(Convert.FromHexString(script))));
		Assert.Equal(expectedHash, manifest.FedpegScriptSha256);
		Assert.Equal(manifest.ChainRpcName, expectation.Chain);
		Assert.Equal(manifest.GenesisBlockHash, expectation.GenesisBlockHash);
		Assert.Equal(manifest.PeggedAssetId, expectation.PeggedAsset);
		Assert.Equal(manifest.ParentGenesisHash, expectation.ParentGenesisBlockHash);
		Assert.Equal(manifest.EnforcePak, expectation.EnforcePak);
		Assert.Equal(manifest.ElementsNumericVersion, expectation.Version);
		Assert.Equal(manifest.ElementsProtocolVersion, expectation.ProtocolVersion);
		Assert.Equal(manifest.ExpectedSubversion, expectation.Subversion);
	}

	[Fact]
	public void ControlledRegtestRowIsLocalDemoOnlyWithIncompleteCtFixture()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidControlledRegtest;
		Assert.Equal(ControlledRegtestManifestId, manifest.ManifestId);
		Assert.Equal("CONTROLLED-REGTEST-MANIFEST-001-LOCAL-DEMO-ONLY-NO-GATE-CREDIT", manifest.ScopeMarker);
		Assert.Equal("CONTROLLED_REGTEST_CT_FIXTURE_INCOMPLETE", manifest.CtFixtureEligibility);

		ElementsNodeExpectation expectation = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			Profile(manifest.ManifestId, manifest.ChainRpcName));
		Assert.Equal("elementsregtest", expectation.Chain);
		Assert.Equal("51", expectation.FedpegScript);
		Assert.Equal(0, expectation.PeginConfirmationDepth);
		Assert.False(expectation.EnforcePak);
	}

	[Fact]
	public void ValidateDescriptorRejectsIndependentFedpegHashMismatch()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		var descriptor = new ElementsReviewedNodeExpectationDescriptor(
			manifest.ManifestId, manifest.ChainRpcName, TestnetScript, 100);

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			ElementsReviewedNodeExpectationSource.ValidateDescriptor(descriptor, manifest, Profile(manifest.ManifestId, manifest.ChainRpcName)));

		Assert.Contains("fedpeg_script_sha256", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(TestnetScript, exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("profile_manifest")]
	[InlineData("profile_network")]
	[InlineData("descriptor_manifest")]
	[InlineData("descriptor_network")]
	public void IdentityMismatchesFailClosedWithNamedLabel(string mismatch)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		string profileManifest = mismatch == "profile_manifest" ? ElementsPublicNetworkManifest.LiquidTestnet.ManifestId : manifest.ManifestId;
		string profileNetwork = mismatch == "profile_network" ? "LIQUIDV1" : manifest.ChainRpcName;
		string descriptorManifest = mismatch == "descriptor_manifest" ? ElementsPublicNetworkManifest.LiquidTestnet.ManifestId : manifest.ManifestId;
		string descriptorNetwork = mismatch == "descriptor_network" ? "LIQUIDV1" : manifest.ChainRpcName;
		var descriptor = new ElementsReviewedNodeExpectationDescriptor(descriptorManifest, descriptorNetwork, MainnetScript, 100);

		Exception exception = Assert.ThrowsAny<Exception>(() =>
			ElementsReviewedNodeExpectationSource.ValidateDescriptor(descriptor, manifest, Profile(profileManifest, profileNetwork)));

		Assert.Contains(mismatch, exception.Message, StringComparison.Ordinal);
		Assert.True(exception is InvalidDataException or InvalidOperationException);
	}

	[Fact]
	public void ProductionCatalogShapeIsClosedOneToOneAndObservationFree()
	{
		ElementsReviewedNodeExpectationSource.AssertCatalogShape();
		string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "WalletWasabi", "Liquid", "Application", "ElementsReviewedNodeExpectationSource.cs"));
		Assert.DoesNotContain("Http", source, StringComparison.Ordinal);
		Assert.DoesNotContain("RpcClient", source, StringComparison.Ordinal);
		Assert.DoesNotContain("Json", source, StringComparison.Ordinal);
		Assert.DoesNotContain("Environment", source, StringComparison.Ordinal);
		Assert.Equal(3, source.Split("new(\n", StringSplitOptions.None).Length - 1);
	}

	[Theory]
	[InlineData("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", "liquidv1")]
	[InlineData("e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20", "liquidtestnet")]
	[InlineData(ControlledRegtestManifestId, "elementsregtest")]
	public void CatalogShapeHasExactOrdinalManifestNetworkPairs(string manifestId, string network)
	{
		ElementsReviewedNodeExpectationSource.AssertCatalogShape();
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(manifestId);

		ElementsNodeExpectation expectation = ElementsReviewedNodeExpectationSource.Bind(manifest, Profile(manifestId, network));

		Assert.Equal(manifestId, manifest.ManifestId);
		Assert.Equal(network, manifest.ChainRpcName);
		Assert.Equal(network, expectation.Chain);
	}

	[Theory]
	[InlineData("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", 99)]
	[InlineData("e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20", 7)]
	[InlineData(ControlledRegtestManifestId, 1)]
	public void OwnerValidationRejectsDepthNotInReviewedCatalog(string manifestId, int wrongDepth)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(manifestId);
		ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			Profile(manifest.ManifestId, manifest.ChainRpcName));
		ElementsNodeExpectation wrong = bound with { PeginConfirmationDepth = wrongDepth };
		string directory = Path.Combine(Path.GetTempPath(), "owner-depth-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			string walletFile = Path.Combine(directory, "wallet.json");
			File.WriteAllText(walletFile, "{}");
			LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
				"wallet", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(directory));

			InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
				ElementsReviewedNodeExpectationSource.ValidateOwnerExpectation(identity, manifest, wrong));

			Assert.Contains("pegin_confirmation_depth", exception.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void FreshChildBindsReviewedManifestWithoutObservationInput()
	{
		string coreAssembly = typeof(LiquidAuthenticatedRuntimeProvider).Assembly.Location;
		string childPath = RoslynFreshChildHarness.CompileChildAssembly(
			"""
			using System;
			using System.Text.Json;
			using WalletWasabi.Liquid.Application;
			using WalletWasabi.Liquid.Network;

			string input = Console.In.ReadToEnd();
			using JsonDocument document = JsonDocument.Parse(input);
			string manifestId = document.RootElement.GetProperty("manifest").GetString()!;
			ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(manifestId);
			var profile = new LiquidRpcProfile("local", new Uri("http://127.0.0.1:1"), "/tmp/unused", manifest.ChainRpcName, manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			var expectation = ElementsReviewedNodeExpectationSource.Bind(manifest, profile);
			Console.Write(JsonSerializer.Serialize(new { token = "BOUND_V1", fields = 10, chain = expectation.Chain, depth = expectation.PeginConfirmationDepth }));
			""",
			"pre-refresh-node-expectation-child",
			"PreRefreshNodeExpectationChild.dll",
			[coreAssembly, typeof(Uri).Assembly.Location]);
		File.Copy(coreAssembly, Path.Combine(Path.GetDirectoryName(childPath)!, "WalletWasabi.dll"), overwrite: true);

		using JsonDocument output = RoslynFreshChildHarness.RunChild(
			childPath,
			new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
			{
				["manifest"] = ElementsPublicNetworkManifest.LiquidTestnet.ManifestId,
			});
		Assert.Equal("BOUND_V1", output.RootElement.GetProperty("token").GetString());
		Assert.Equal(10, output.RootElement.GetProperty("fields").GetInt32());
		Assert.Equal("liquidtestnet", output.RootElement.GetProperty("chain").GetString());
		Assert.Equal(8, output.RootElement.GetProperty("depth").GetInt32());
	}

	private static LiquidRpcProfile Profile(string manifest, string network) =>
		new("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", network, manifest, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

	private static string LowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "WalletWasabi.Client")))
		{
			directory = directory.Parent;
		}
		return Assert.IsType<DirectoryInfo>(directory).FullName;
	}
}
