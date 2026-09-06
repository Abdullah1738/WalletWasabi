using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WalletWasabi.Liquid.CoinJoin.Client.Internal;
using WalletWasabi.Liquid.CoinJoin.Native;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.CoinJoin;

public sealed class LiquidPublicRoundFixtureTests
{
	[NativeFixtureFact("WLCJ_PUBLIC_ROUND_FIXTURE_DIR")]
	public void ExplicitNativeFixtureReturnsOnlyPsetsAndCheckedDigests()
	{
		string? path = Environment.GetEnvironmentVariable("WLCJ_PUBLIC_ROUND_FIXTURE_DIR");
		LiquidPublicRoundFixture fixture = LiquidPublicRoundFixture.Load(path!, new LiquidCoinJoinNativeOperations());
		var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(path!, "manifest.json")))!;
		Assert.Equal(File.ReadAllBytes(Path.Combine(path!, "preblind.pset")), fixture.PreblindPset);
		Assert.Equal(File.ReadAllBytes(Path.Combine(path!, "final.pset")), fixture.FinalPset);
		Assert.Equal(Convert.FromHexString(manifest["states"]![0]!["digest"]!.GetValue<string>()), fixture.PreblindStateDigest);
		Assert.Equal(Convert.FromHexString(manifest["states"]![2]!["digest"]!.GetValue<string>()), fixture.FinalStateDigest);
	}

	[Fact]
	public void ReturnedSurfaceHasNoMetadataFactOrIdentityClaims()
	{
		Assert.Equal(new[] { "FinalPset", "FinalStateDigest", "PreblindPset", "PreblindStateDigest" },
			typeof(LiquidPublicRoundFixture).GetProperties().Select(x => x.Name).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void MissingFixturePathFailsClosed()
	{
		Assert.Throws<FormatException>(() => LiquidPublicRoundFixture.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), new LiquidCoinJoinNativeOperations()));
	}

	[NativeFixtureFact("WLCJ_PUBLIC_ROUND_FIXTURE_DIR")]
	public void MetadataMutationsRespectValidationBoundary()
	{
		string source = Environment.GetEnvironmentVariable("WLCJ_PUBLIC_ROUND_FIXTURE_DIR")!;
		// A valid native export must load before any mutation is applied.
		LiquidPublicRoundFixture original = LiquidPublicRoundFixture.Load(source, new LiquidCoinJoinNativeOperations());
		foreach (string mutation in new[] { "round_hex", "genesis_hex", "asset_hex", "network_hex", "role_map_hex", "context-0", "context-1", "context-2", "state-count", "descriptive-metadata" })
		{
			string directory = Path.Combine(Path.GetTempPath(), "wlcj-public-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				foreach (string name in new[] { "manifest.json", "SHA256SUMS", "preblind.pset", "final.pset" })
					File.Copy(Path.Combine(source, name), Path.Combine(directory, name));
				var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")))!;
				JsonNode parent = manifest;
				string field = mutation;
				if (mutation == "state-count")
					manifest["states"]!.AsArray().RemoveAt(2);
				else if (mutation == "descriptive-metadata")
				{
					// Preserve declared fee accounting while changing both unbound amounts.
					foreach (string name in new[] { "inputs", "outputs" })
						manifest[name]![0]!["explicit_value"] = manifest[name]![0]!["explicit_value"]!.GetValue<long>() + 1;
					manifest["inputs"]![0]!["txid_wire_hex"] = new string('1', 64);
					manifest["assembled_txid_wire_hex"] = new string('2', 64);
				}
				else
				{
					if (mutation.StartsWith("context-", StringComparison.Ordinal))
					{
						parent = manifest["states"]![int.Parse(mutation[^1..])]!;
						field = "context_hex";
					}
					byte[] bytes = Convert.FromHexString(parent[field]!.GetValue<string>());
					bytes[^1] ^= 1;
					parent[field] = Convert.ToHexStringLower(bytes);
				}
				File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToJsonString(), new UTF8Encoding(false));
				string sums = string.Join('\n', new[] { "manifest.json", "preblind.pset", "final.pset" }.Order(StringComparer.Ordinal).Select(name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))).ToLowerInvariant() + "  " + name)) + "\n";
				File.WriteAllText(Path.Combine(directory, "SHA256SUMS"), sums, Encoding.ASCII);
				if (mutation == "descriptive-metadata")
				{
					LiquidPublicRoundFixture changed = LiquidPublicRoundFixture.Load(directory, new LiquidCoinJoinNativeOperations());
					Assert.Equal(original.PreblindPset, changed.PreblindPset);
					Assert.Equal(original.FinalPset, changed.FinalPset);
					Assert.Equal(original.PreblindStateDigest, changed.PreblindStateDigest);
					Assert.Equal(original.FinalStateDigest, changed.FinalStateDigest);
				}
				else
				{
					var error = Assert.Throws<FormatException>(() => LiquidPublicRoundFixture.Load(directory, new LiquidCoinJoinNativeOperations()));
					Assert.NotEqual("SHA256SUMS mismatch.", error.Message);
				}
			}
			finally { Directory.Delete(directory, true); }
		}
	}
}

public sealed class NativeFixtureFactAttribute : FactAttribute
{
	public NativeFixtureFactAttribute(string variable)
	{
		if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)) || !Directory.Exists(Environment.GetEnvironmentVariable(variable)))
			Skip = $"Set {variable} to an existing local fixture directory to run this integration test.";
	}
}
