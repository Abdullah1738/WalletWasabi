using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NuGet.Versioning;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Wire;
using Xunit;
using LiquidOrdinaryWalletPlanEncodedFrame = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanEncodedFrame;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;
using LiquidOrdinaryWalletPlanFundingRow = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingRow;
using LockedPackageAuthority = (string Type, string? Requested, string ResolvedVersion, string? ContentHash, System.Collections.Generic.IReadOnlyDictionary<string, string> Dependencies);

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

public class LiquidOrdinaryWalletPlanWireTests
{
	private const string LinuxX64TargetFramework = "net10.0/linux-x64";
	private const string ExpectedDebugWireSurfaceSha256 = "fc58193325d4e920020d9b24e8f3caf9ca8a6da2275b7680a56d463e4e7e9de6";
	private const string ExpectedReleaseWireSurfaceSha256 = "78d0d2b0c2fce5d178ba34a9f517ee4c4b2fd8f8a04ed7d8d14cb1805b52ca22";
	private const string ExpectedDebugWireClosureSha256 = "014cf01f4bda42f8c36a7dd41cae78e5d464ad716f598256d0ff5642493a2c54";
	private const string ExpectedReleaseWireClosureSha256 = "d83901f434193cc0aa69ed3446170922a12f397b6bbb394906c7de1815fd7cfe";
	private const string ExpectedDebugRuntimeDispatchAuthoritySha256 = "486ddbf38f33d2eb2b6f12c09d6acc3244c486c7f3278e930a08a90b56392e38";
	private const string ExpectedReleaseRuntimeDispatchAuthoritySha256 = "a56cae7e3b96b0de3838437a05280304471802de8dacaba700d3f3cc4e4a9908";
	private const string ExpectedDebugAmbientRuntimeDispatchAuthoritySha256 = "b30f09d21d2b3a2f38e3fdc52925906f64d5c325fdd66de80113858ce18edb7e";
	private const string ExpectedReleaseAmbientRuntimeDispatchAuthoritySha256 = "9114556617725c4f2d52936d0b2e58d245408577439cb6a8a1d566959e90d9da";
	private const string ExpectedDebugModuleInitializerBodySha256 = "23d1ae5ddc95da66864101267cfbd2d82a7942762a4cee19ebb85013b7dcd3c3";
	private const string ExpectedReleaseModuleInitializerBodySha256 = "23d1ae5ddc95da66864101267cfbd2d82a7942762a4cee19ebb85013b7dcd3c3";
	private const string ExpectedDebugAmbientClosureSha256 = "7cbab0e3ce7f01621fea42595285e374a7139f7e0e307e1ff95a95f5f7dc6ba6";
	private const string ExpectedReleaseAmbientClosureSha256 = "2389ceb7f1c29bc55f6656dcda0f701f0b2e61c0a2a97fd73a04e16a4c66bc8a";
	private const string ExpectedDebugGeneratedSourcesSha256 = "5f9abe4582b34b708d20504a398880e6f8e1922d52f8f8ab3c98d933b9e3c6e8";
	private const string ExpectedReleaseGeneratedSourcesSha256 = "5f9abe4582b34b708d20504a398880e6f8e1922d52f8f8ab3c98d933b9e3c6e8";
	private static readonly (string MacOsArm64, string LinuxX64) ExpectedDebugImportClosureSha256 =
		("8bfa4868f51556f60144f7746b44a46aea48a66ef1e6e0329f9f4fde3b2073ef", "PENDING-LINUX-X64-DEBUG-IMPORT-AUTHORITY-V2");
	private static readonly (string MacOsArm64, string LinuxX64) ExpectedReleaseImportClosureSha256 =
		("8bfa4868f51556f60144f7746b44a46aea48a66ef1e6e0329f9f4fde3b2073ef", "98ded521ee2cdcc32e313eac442b46ede3e23bc22d5f8321e24135a60dfd9c05");
	private static readonly (string MacOsArm64, string LinuxX64) ExpectedDebugReferenceAuthoritySha256 =
		("ef61142bc45c04415be4ee870ff4b4db9345dc8beb787c67ee67bc6c7d3d8fdb", "PENDING-LINUX-X64-DEBUG-REFERENCE-AUTHORITY-V2");
	private static readonly (string MacOsArm64, string LinuxX64) ExpectedReleaseReferenceAuthoritySha256 =
		("ef61142bc45c04415be4ee870ff4b4db9345dc8beb787c67ee67bc6c7d3d8fdb", "9e4db43c7921291756478b9a2fed358d8082a7d9ebaff84a6920dc51f71fead7");
	private static readonly (string MacOsArm64, string LinuxX64) ExpectedDebugCompilerInputAuthoritySha256 =
		("PENDING-MACOS-ARM64-DEBUG-COMPILER-INPUT-AUTHORITY-V2", "PENDING-LINUX-X64-DEBUG-COMPILER-INPUT-AUTHORITY-V2");
	private static readonly (string MacOsArm64, string LinuxX64) ExpectedReleaseCompilerInputAuthoritySha256 =
		("PENDING-MACOS-ARM64-RELEASE-COMPILER-INPUT-AUTHORITY-V2", "240c4731039e8fa7153c1cead6cf04e1b10e4872673b1aa5f3ae93c574737b42");
	private const string ExpectedMacOsArm64ToolchainDependencyAuthoritySha256 =
		"PENDING-MACOS-ARM64-TOOLCHAIN-AUTHORITY-V2";
	private const string ExpectedLinuxX64ToolchainDependencyAuthoritySha256 =
		"8b677c4a014bc645d41520f40d538b91f42122489d05cdd3bd7080a4b5f6fabb";
	private static readonly string[] CompilerAuthoritySectionOrder =
	[
		"ARG", "SOURCE", "ANALYZER", "REFERENCE", "ADDITIONAL", "ANALYZERCONFIG", "EMBED",
		"ANALYZER_DEP", "AUX", "DIAGNOSTIC_TASK", "DIAGNOSTIC_COMPILER", "DIAGNOSTIC_PARAMETER",
		"CSC_START", "CSC_INPUT", "CSC_ARG",
	];
	private static readonly string[] CompilerAuxiliaryPrefixes =
	[
		"/ruleset:", "/appconfig:", "/keyfile:", "/win32icon:", "/win32res:",
		"/win32manifest:", "/sourcelink:", "/resource:", "/linkresource:", "/addmodule:",
	];
	private const string IssuedAssetHex =
		"2222222222222222222222222222222222222222222222222222222222222222";
	private const string PublicKeyHex =
		"0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string FirstScriptHex = "00140102030405060708090a0b0c0d0e0f1011121314";
	private const string SecondScriptHex = "001415161718191a1b1c1d1e1f202122232425262728";
	private static readonly byte[] SourceEpoch = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

	[Fact]
	public void CompilerAuthorityNormalizationRejectsReservedTokensAndCoversEveryAddModule()
	{
		string fixtureRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-compiler-authority-{Guid.NewGuid():N}");
		try
		{
			string repositoryRoot = Path.Combine(fixtureRoot, "repo");
			string dotnetRoot = Path.Combine(fixtureRoot, "dotnet");
			string packageRoot = Path.Combine(fixtureRoot, "packages");
			string authorityRoot = Path.Combine(fixtureRoot, "authority");
			string generatedRoot = Path.Combine(fixtureRoot, "generated");
			string intermediateRoot = Path.Combine(fixtureRoot, "intermediate");
			Directory.CreateDirectory(repositoryRoot);
			Directory.CreateDirectory(dotnetRoot);
			Directory.CreateDirectory(packageRoot);
			Directory.CreateDirectory(authorityRoot);
			Directory.CreateDirectory(generatedRoot);
			Directory.CreateDirectory(intermediateRoot);
			var packageAuthority = (PrimaryRoot: packageRoot, OrderedRoots: new[] { packageRoot });
			foreach ((string token, string physicalRoot) in new[]
			{
				("{REPO}", repositoryRoot),
				("{DOTNET}", dotnetRoot),
				("{AUTHORITY}", authorityRoot),
				("{NUGET}", packageRoot),
			})
			{
				string expected = $"/reference:{token}/lib/Compiler.dll";
				Assert.Equal(
					expected,
					NormalizeCompilerAuthorityStringWithPackages(
						$"/reference:{Path.Combine(physicalRoot, "lib/Compiler.dll")}",
						packageAuthority,
						("{REPO}", repositoryRoot),
						("{DOTNET}", dotnetRoot),
						("{AUTHORITY}", authorityRoot)));
				Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
					NormalizeCompilerAuthorityStringWithPackages(
						expected,
						packageAuthority,
						("{REPO}", repositoryRoot),
						("{DOTNET}", dotnetRoot),
						("{AUTHORITY}", authorityRoot)));
			}
			foreach ((string token, string physicalRoot) in new[]
			{
				("{GENERATED}", generatedRoot),
				("{INTERMEDIATE}", intermediateRoot),
			})
			{
				string expected = $"Output={token}/Compiler.dll";
				Assert.Equal(
					expected,
					NormalizeCompilerAuthorityString(
						$"Output={Path.Combine(physicalRoot, "Compiler.dll")}",
						("{GENERATED}", generatedRoot),
						("{INTERMEDIATE}", intermediateRoot)));
				Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
					NormalizeCompilerAuthorityString(
						expected,
						("{GENERATED}", generatedRoot),
						("{INTERMEDIATE}", intermediateRoot)));
			}
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				NormalizeCompilerAuthorityString(
					"TaskId:{TASK}",
					("{DOTNET}", dotnetRoot)));
			string backslashMetadata = NormalizeCompilerAuthorityStringWithPackages(
				$"/resource:{Path.Combine(repositoryRoot, "resource.bin")},Logical\\Name",
				packageAuthority,
				("{REPO}", repositoryRoot));
			string slashMetadata = NormalizeCompilerAuthorityStringWithPackages(
				$"/resource:{Path.Combine(repositoryRoot, "resource.bin")},Logical/Name",
				packageAuthority,
				("{REPO}", repositoryRoot));
			Assert.Equal("/resource:{REPO}/resource.bin,Logical\\Name", backslashMetadata);
			Assert.Equal("/resource:{REPO}/resource.bin,Logical/Name", slashMetadata);
			Assert.NotEqual(backslashMetadata, slashMetadata);

			string moduleRoot = Path.Combine(repositoryRoot, "modules");
			Directory.CreateDirectory(moduleRoot);
			string firstModule = Path.Combine(moduleRoot, "first.netmodule");
			string secondModule = Path.Combine(moduleRoot, "second.netmodule");
			File.WriteAllBytes(firstModule, [1, 2, 3]);
			File.WriteAllBytes(secondModule, [4, 5, 6]);
			CompilerAuthorityEntry[] initialEntries = CreateCompilerAuxiliaryAuthorityEntries(
				$"/addmodule:{firstModule},{secondModule}",
				repositoryRoot,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				authorityRoot,
				new string('0', 40));
			Assert.Equal(2, initialEntries.Length);
			Assert.Equal("REPO|modules/first.netmodule", initialEntries[0].Detail);
			Assert.Equal("REPO|modules/second.netmodule", initialEntries[1].Detail);
			string[] initialRows = BuildSyntheticCompilerAuthorityRows(initialEntries);

			File.WriteAllBytes(secondModule, [7, 8, 9]);
			CompilerAuthorityEntry[] mutatedEntries = CreateCompilerAuxiliaryAuthorityEntries(
				$"/addmodule:{firstModule},{secondModule}",
				repositoryRoot,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				authorityRoot,
				new string('0', 40));
			string[] mutatedRows = BuildSyntheticCompilerAuthorityRows(mutatedEntries);
			Assert.Equal(initialRows[0], mutatedRows[0]);
			Assert.NotEqual(initialRows[1], mutatedRows[1]);
			Assert.NotEqual(
				Sha256Text(string.Join('\n', initialRows) + "\n"),
				Sha256Text(string.Join('\n', mutatedRows) + "\n"));

			string resource = Path.Combine(moduleRoot, "resource,with-comma.bin");
			File.WriteAllBytes(resource, [10, 11, 12]);
			CompilerAuthorityEntry resourceEntry = Assert.Single(
				CreateCompilerAuxiliaryAuthorityEntries(
					$"/resource:\"{resource}\",LogicalName,public",
					repositoryRoot,
					repositoryRoot,
					dotnetRoot,
					packageAuthority,
					authorityRoot,
					new string('0', 40)));
			Assert.Equal("/resource:", resourceEntry.Identity);
			Assert.Equal("REPO|modules/resource,with-comma.bin", resourceEntry.Detail);

			const string SyntheticSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
			var requiredCompilerEntries = new List<CompilerAuthorityEntry>
			{
				CreateCompilerAuthorityEntry("ARG", "/noconfig"),
				CreateCompilerAuthorityEntry("SOURCE", "REPO|source.cs", sha256: SyntheticSha256),
				CreateCompilerAuthorityEntry("ANALYZER", "NUGET|analyzer.dll", sha256: SyntheticSha256),
				CreateCompilerAuthorityEntry("REFERENCE", "DOTNET|reference.dll", sha256: SyntheticSha256),
				CreateCompilerAuthorityEntry("ANALYZER_DEP", "NUGET|analyzer-dependency.dll", sha256: SyntheticSha256),
				CreateCompilerAuthorityEntry("DIAGNOSTIC_TASK", "DOTNET|task.dll", sha256: SyntheticSha256),
				CreateCompilerAuthorityEntry("DIAGNOSTIC_COMPILER", "DOTNET|csc", sha256: SyntheticSha256),
			};
			for (int index = 0; index < 7; index++)
			{
				requiredCompilerEntries.Add(CreateCompilerAuthorityEntry(
					"DIAGNOSTIC_PARAMETER",
					$"parameter-{index}"));
			}
			requiredCompilerEntries.Add(CreateCompilerAuthorityEntry(
				"CSC_START",
				"DOTNET|task.dll",
				sha256: SyntheticSha256));
			requiredCompilerEntries.Add(CreateCompilerAuthorityEntry(
				"CSC_INPUT",
				"Sources",
				qualifier: "Compile",
				values: JsonSerializer.Serialize(new[] { "REPO|source.cs" })));
			requiredCompilerEntries.Add(CreateCompilerAuthorityEntry("CSC_ARG", "/noconfig"));

			string zeroAuxiliaryManifest = BuildCanonicalCompilerInputAuthorityManifest(requiredCompilerEntries);
			string[] zeroAuxiliaryRows = AssertCanonicalCompilerInputAuthorityManifest(zeroAuxiliaryManifest);
			Assert.DoesNotContain(zeroAuxiliaryRows, row =>
				ParseCanonicalAuthorityManifestRow(row, "COMPILER_INPUT_V2", 8)[1] == "AUX");

			var presentAuxiliaryEntries = requiredCompilerEntries.ToList();
			presentAuxiliaryEntries.Insert(
				5,
				CreateCompilerAuthorityEntry(
					"AUX",
					"/sourcelink:",
					detail: "REPO|sourcelink.json",
					sha256: SyntheticSha256));
			string presentAuxiliaryManifest = BuildCanonicalCompilerInputAuthorityManifest(presentAuxiliaryEntries);
			string mutatedAuxiliaryHashManifest =
				CreateCompilerManifestWithMutatedFirstAuxiliarySha256(presentAuxiliaryManifest);
			_ = AssertCanonicalCompilerInputAuthorityManifest(mutatedAuxiliaryHashManifest);
			Assert.NotEqual(presentAuxiliaryManifest, mutatedAuxiliaryHashManifest);
			Assert.NotEqual(
				Sha256Text(presentAuxiliaryManifest),
				Sha256Text(mutatedAuxiliaryHashManifest));
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertCanonicalCompilerInputAuthorityManifest(
					CreateCompilerManifestWithInvalidAuxiliaryPrefix(presentAuxiliaryManifest)));

			var syntheticAnalyzerEntries = new[]
			{
				(Identity: "DOTNET|packs/example/analyzers/First.dll",
					ContentSha256: SyntheticSha256,
					Provenance: "DOTNET|sdk/10.0.100/Sdks/Example/targets/First.targets"),
				(Identity: "NUGET|example.analyzers/1.0.0/analyzers/dotnet/Second.dll",
					ContentSha256: new string('a', 64),
					Provenance: "DOTNET|sdk/10.0.100/Sdks/Example/targets/Second.targets"),
			};
			string syntheticAnalyzerManifest = BuildCanonicalAnalyzerAuthorityManifest(
				syntheticAnalyzerEntries);
			string syntheticAnalyzerSha256 = Sha256Text(syntheticAnalyzerManifest);
			Assert.Equal(2, AssertCanonicalAnalyzerAuthorityManifest(syntheticAnalyzerManifest).Length);
			AssertExactAnalyzerAuthoritySha256(syntheticAnalyzerSha256, syntheticAnalyzerManifest);
			Assert.Equal(
				syntheticAnalyzerManifest,
				BuildCanonicalAnalyzerAuthorityManifest(syntheticAnalyzerEntries.Reverse()));
			Assert.Equal(
				"PENDING-MACOS-ARM64-ANALYZER-AUTHORITY-V2",
				GetExpectedAnalyzerAuthoritySha256(true, false, Architecture.Arm64));
			Assert.Equal(
				"4939c19b5cd53069db3edcd5e6d5c42544e22418e8dadf46b07df1938f325291",
				GetExpectedAnalyzerAuthoritySha256(false, true, Architecture.X64));
			Assert.NotEqual(
				"PENDING-MACOS-ARM64-ANALYZER-AUTHORITY-V2",
				"4939c19b5cd53069db3edcd5e6d5c42544e22418e8dadf46b07df1938f325291");
			AssertAnalyzerAuthorityPlatformRejected(true, true, Architecture.Arm64);
			AssertAnalyzerAuthorityPlatformRejected(false, false, Architecture.X64);
			AssertAnalyzerAuthorityPlatformRejected(true, false, Architecture.X64);
			AssertAnalyzerAuthorityPlatformRejected(false, true, Architecture.Arm64);

			var mutatedAnalyzerContent = syntheticAnalyzerEntries.ToArray();
			mutatedAnalyzerContent[0] = (
				mutatedAnalyzerContent[0].Identity,
				new string('1', 64),
				mutatedAnalyzerContent[0].Provenance);
			var mutatedAnalyzerIdentity = syntheticAnalyzerEntries.ToArray();
			mutatedAnalyzerIdentity[0] = (
				"DOTNET|packs/example/analyzers/First-mutated.dll",
				mutatedAnalyzerIdentity[0].ContentSha256,
				mutatedAnalyzerIdentity[0].Provenance);
			var mutatedAnalyzerProvenance = syntheticAnalyzerEntries.ToArray();
			mutatedAnalyzerProvenance[0] = (
				mutatedAnalyzerProvenance[0].Identity,
				mutatedAnalyzerProvenance[0].ContentSha256,
				"DOTNET|sdk/10.0.100/Sdks/Example/targets/First-mutated.targets");
			string[] canonicalAnalyzerMutations =
			[
				BuildCanonicalAnalyzerAuthorityManifest(mutatedAnalyzerContent),
				BuildCanonicalAnalyzerAuthorityManifest(mutatedAnalyzerIdentity),
				BuildCanonicalAnalyzerAuthorityManifest(mutatedAnalyzerProvenance),
				BuildCanonicalAnalyzerAuthorityManifest(syntheticAnalyzerEntries.Take(1)),
				BuildCanonicalAnalyzerAuthorityManifest(syntheticAnalyzerEntries.Append((
					"NUGET|example.analyzers/1.0.0/analyzers/dotnet/Third.dll",
					new string('b', 64),
					"DOTNET|sdk/10.0.100/Sdks/Example/targets/Third.targets"))),
			];
			foreach (string analyzerMutation in canonicalAnalyzerMutations)
			{
				_ = AssertCanonicalAnalyzerAuthorityManifest(analyzerMutation);
				Assert.NotEqual(syntheticAnalyzerManifest, analyzerMutation);
				AssertExactAnalyzerAuthorityRejected(syntheticAnalyzerSha256, analyzerMutation);
			}
			AssertAnalyzerAuthorityBuildRejected(
				syntheticAnalyzerEntries.Append(syntheticAnalyzerEntries[0]));
			string caseAliasedAnalyzerIdentity = syntheticAnalyzerEntries[0].Identity.Replace(
				"/First.dll",
				"/first.dll",
				StringComparison.Ordinal);
			Assert.StartsWith("DOTNET|", caseAliasedAnalyzerIdentity, StringComparison.Ordinal);
			AssertCanonicalAnalyzerAuthorityPath(caseAliasedAnalyzerIdentity, allowPackage: true);
			Assert.False(StringComparer.Ordinal.Equals(
				syntheticAnalyzerEntries[0].Identity,
				caseAliasedAnalyzerIdentity));
			Assert.True(StringComparer.OrdinalIgnoreCase.Equals(
				syntheticAnalyzerEntries[0].Identity,
				caseAliasedAnalyzerIdentity));
			AssertAnalyzerAuthorityBuildRejected(
				syntheticAnalyzerEntries.Append((
					caseAliasedAnalyzerIdentity,
					new string('c', 64),
					syntheticAnalyzerEntries[0].Provenance)));

			string[] swappedAnalyzerLines = syntheticAnalyzerManifest.Split('\n', StringSplitOptions.None);
			string[] firstAnalyzerFields = ParseCanonicalAuthorityManifestRow(
				swappedAnalyzerLines[1],
				"ANALYZER_V2",
				4);
			string[] secondAnalyzerFields = ParseCanonicalAuthorityManifestRow(
				swappedAnalyzerLines[2],
				"ANALYZER_V2",
				4);
			for (int fieldIndex = 1; fieldIndex < firstAnalyzerFields.Length; fieldIndex++)
			{
				(firstAnalyzerFields[fieldIndex], secondAnalyzerFields[fieldIndex]) =
					(secondAnalyzerFields[fieldIndex], firstAnalyzerFields[fieldIndex]);
			}
			swappedAnalyzerLines[1] = BuildCanonicalAuthorityManifestRow(
				"ANALYZER_V2",
				firstAnalyzerFields);
			swappedAnalyzerLines[2] = BuildCanonicalAuthorityManifestRow(
				"ANALYZER_V2",
				secondAnalyzerFields);
			AssertAnalyzerAuthorityManifestRejected(string.Join('\n', swappedAnalyzerLines));

			AssertAnalyzerAuthorityManifestRejected(syntheticAnalyzerManifest[..^1]);
			AssertAnalyzerAuthorityManifestRejected(syntheticAnalyzerManifest + "\n");
			AssertAnalyzerAuthorityManifestRejected(
				syntheticAnalyzerManifest.Replace("\n", "\r\n", StringComparison.Ordinal));
			AssertAnalyzerAuthorityManifestRejected(
				syntheticAnalyzerManifest.Replace(
					"ANALYZER_AUTHORITY_V2",
					"ANALYZER_AUTHORITY_V1",
					StringComparison.Ordinal));
			string[] malformedAnalyzerLines = syntheticAnalyzerManifest.Split('\n', StringSplitOptions.None);
			malformedAnalyzerLines[1] = malformedAnalyzerLines[1].Replace("|[", "|[ ", StringComparison.Ordinal);
			AssertAnalyzerAuthorityManifestRejected(string.Join('\n', malformedAnalyzerLines));
			malformedAnalyzerLines = syntheticAnalyzerManifest.Split('\n', StringSplitOptions.None);
			firstAnalyzerFields = ParseCanonicalAuthorityManifestRow(
				malformedAnalyzerLines[1],
				"ANALYZER_V2",
				4);
			malformedAnalyzerLines[1] = "ANALYZER_V2|" + JsonSerializer.Serialize(
				new object[]
				{
					0,
					firstAnalyzerFields[1],
					firstAnalyzerFields[2],
					firstAnalyzerFields[3],
				});
			AssertAnalyzerAuthorityManifestRejected(string.Join('\n', malformedAnalyzerLines));
			foreach ((int fieldIndex, string invalidValue) in new[]
			{
				(1, "REPO|outside/analyzer.dll"),
				(1, "DOTNET|../outside/analyzer.dll"),
				(2, new string('A', 64)),
				(3, "NUGET|example/targets/Example.targets"),
			})
			{
				malformedAnalyzerLines = syntheticAnalyzerManifest.Split('\n', StringSplitOptions.None);
				firstAnalyzerFields = ParseCanonicalAuthorityManifestRow(
					malformedAnalyzerLines[1],
					"ANALYZER_V2",
					4);
				firstAnalyzerFields[fieldIndex] = invalidValue;
				malformedAnalyzerLines[1] = BuildCanonicalAuthorityManifestRow(
					"ANALYZER_V2",
					firstAnalyzerFields);
				AssertAnalyzerAuthorityManifestRejected(string.Join('\n', malformedAnalyzerLines));
			}

			Xunit.Sdk.XunitException analyzerDiagnostics = GetExactAnalyzerAuthorityRejection(
				new string('f', 64),
				syntheticAnalyzerManifest);
			Assert.StartsWith(syntheticAnalyzerSha256, analyzerDiagnostics.Message, StringComparison.Ordinal);
			Assert.Contains("\nCOUNT|2", analyzerDiagnostics.Message, StringComparison.Ordinal);
			Assert.Contains("\nROW|000|ROW_SHA256|", analyzerDiagnostics.Message, StringComparison.Ordinal);
			Assert.Contains("\nROW|001|ROW_SHA256|", analyzerDiagnostics.Message, StringComparison.Ordinal);
			Assert.Contains("\nSORTED_ENTRIES_SHA256|", analyzerDiagnostics.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("EXPECTED HEX", analyzerDiagnostics.Message, StringComparison.Ordinal);
			foreach ((string identity, string _, string provenance) in syntheticAnalyzerEntries)
			{
				Assert.Contains(
					$"PATH_SHA256|{Sha256Text(identity)}",
					analyzerDiagnostics.Message,
					StringComparison.Ordinal);
				Assert.Contains(
					$"PROVENANCE_SHA256|{Sha256Text(provenance)}",
					analyzerDiagnostics.Message,
					StringComparison.Ordinal);
				Assert.DoesNotContain(identity, analyzerDiagnostics.Message, StringComparison.Ordinal);
				Assert.DoesNotContain(provenance, analyzerDiagnostics.Message, StringComparison.Ordinal);
				Assert.DoesNotContain(
					Convert.ToHexString(Encoding.UTF8.GetBytes(identity)),
					analyzerDiagnostics.Message,
					StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(
					Convert.ToHexString(Encoding.UTF8.GetBytes(provenance)),
					analyzerDiagnostics.Message,
					StringComparison.OrdinalIgnoreCase);
			}

			string syntheticToolchainManifest = BuildCanonicalToolchainFileAuthorityManifest(
				[
					("dotnet", SyntheticSha256),
					($"host/fxr/{PinnedDotnetHostFxrVersion}/libhostfxr.so", new string('a', 64)),
				]);
			string[] syntheticToolchainRows = AssertCanonicalToolchainFileAuthorityManifest(
				syntheticToolchainManifest);
			Assert.Equal(2, syntheticToolchainRows.Length);
			string mutatedToolchainContent = CreateToolchainFileManifestWithMutatedFirstSha256(
				syntheticToolchainManifest);
			_ = AssertCanonicalToolchainFileAuthorityManifest(mutatedToolchainContent);
			Assert.NotEqual(Sha256Text(syntheticToolchainManifest), Sha256Text(mutatedToolchainContent));

			AssertToolchainFileAuthorityManifestRejected(syntheticToolchainManifest[..^1]);
			AssertToolchainFileAuthorityManifestRejected(syntheticToolchainManifest + "\n");
			AssertToolchainFileAuthorityManifestRejected(
				syntheticToolchainManifest.Replace("\n", "\r\n", StringComparison.Ordinal));
			AssertToolchainFileAuthorityManifestRejected(
				syntheticToolchainManifest.Replace(
					"TOOLCHAIN_FILE_AUTHORITY_V2",
					"TOOLCHAIN_FILE_AUTHORITY_V1",
					StringComparison.Ordinal));
			string[] syntheticToolchainLines = syntheticToolchainManifest.Split('\n', StringSplitOptions.None);
			syntheticToolchainLines[1] = syntheticToolchainLines[1].Replace("|[", "|[ ", StringComparison.Ordinal);
			AssertToolchainFileAuthorityManifestRejected(string.Join('\n', syntheticToolchainLines));
			syntheticToolchainLines = syntheticToolchainManifest.Split('\n', StringSplitOptions.None);
			string[] firstToolchainFields = ParseCanonicalAuthorityManifestRow(
				syntheticToolchainLines[1],
				"TOOLCHAIN_FILE_V2",
				3);
			firstToolchainFields[0] = "1";
			syntheticToolchainLines[1] = BuildCanonicalAuthorityManifestRow(
				"TOOLCHAIN_FILE_V2",
				firstToolchainFields);
			AssertToolchainFileAuthorityManifestRejected(string.Join('\n', syntheticToolchainLines));
			syntheticToolchainLines = syntheticToolchainManifest.Split('\n', StringSplitOptions.None);
			firstToolchainFields = ParseCanonicalAuthorityManifestRow(
				syntheticToolchainLines[1],
				"TOOLCHAIN_FILE_V2",
				3);
			string[] secondToolchainFields = ParseCanonicalAuthorityManifestRow(
				syntheticToolchainLines[2],
				"TOOLCHAIN_FILE_V2",
				3);
			(firstToolchainFields[1], secondToolchainFields[1]) =
				(secondToolchainFields[1], firstToolchainFields[1]);
			(firstToolchainFields[2], secondToolchainFields[2]) =
				(secondToolchainFields[2], firstToolchainFields[2]);
			syntheticToolchainLines[1] = BuildCanonicalAuthorityManifestRow(
				"TOOLCHAIN_FILE_V2",
				firstToolchainFields);
			syntheticToolchainLines[2] = BuildCanonicalAuthorityManifestRow(
				"TOOLCHAIN_FILE_V2",
				secondToolchainFields);
			AssertToolchainFileAuthorityManifestRejected(string.Join('\n', syntheticToolchainLines));
			syntheticToolchainLines = syntheticToolchainManifest.Split('\n', StringSplitOptions.None);
			secondToolchainFields = ParseCanonicalAuthorityManifestRow(
				syntheticToolchainLines[2],
				"TOOLCHAIN_FILE_V2",
				3);
			secondToolchainFields[1] = "dotnet";
			syntheticToolchainLines[2] = BuildCanonicalAuthorityManifestRow(
				"TOOLCHAIN_FILE_V2",
				secondToolchainFields);
			AssertToolchainFileAuthorityManifestRejected(string.Join('\n', syntheticToolchainLines));
			secondToolchainFields = ParseCanonicalAuthorityManifestRow(
				syntheticToolchainManifest.Split('\n', StringSplitOptions.None)[2],
				"TOOLCHAIN_FILE_V2",
				3);
			secondToolchainFields[2] = secondToolchainFields[2].ToUpperInvariant();
			syntheticToolchainLines = syntheticToolchainManifest.Split('\n', StringSplitOptions.None);
			syntheticToolchainLines[2] = BuildCanonicalAuthorityManifestRow(
				"TOOLCHAIN_FILE_V2",
				secondToolchainFields);
			AssertToolchainFileAuthorityManifestRejected(string.Join('\n', syntheticToolchainLines));
			syntheticToolchainLines = syntheticToolchainManifest.Split('\n', StringSplitOptions.None);
			firstToolchainFields = ParseCanonicalAuthorityManifestRow(
				syntheticToolchainLines[1],
				"TOOLCHAIN_FILE_V2",
				3);
			syntheticToolchainLines[1] = BuildCanonicalAuthorityManifestRow(
				"TOOLCHAIN_FILE_V2",
				[firstToolchainFields[0], firstToolchainFields[1], firstToolchainFields[2], "EXTRA"]);
			AssertToolchainFileAuthorityManifestRejected(string.Join('\n', syntheticToolchainLines));
			syntheticToolchainLines = syntheticToolchainManifest.Split('\n', StringSplitOptions.None);
			firstToolchainFields = ParseCanonicalAuthorityManifestRow(
				syntheticToolchainLines[1],
				"TOOLCHAIN_FILE_V2",
				3);
			syntheticToolchainLines[1] = "TOOLCHAIN_FILE_V2|" + JsonSerializer.Serialize(
				new object[] { 0, firstToolchainFields[1], firstToolchainFields[2] });
			AssertToolchainFileAuthorityManifestRejected(string.Join('\n', syntheticToolchainLines));
			AssertToolchainRelativeIdentityRejected("sdk/10.0.100/a\\b");
			AssertToolchainRelativeIdentityRejected("sdk/10.0.100/a|b");
			AssertToolchainRelativeIdentityRejected("sdk/10.0.100/a\rb");
			AssertToolchainRelativeIdentityRejected("sdk/10.0.100/a\nb");

			string fixtureSdkRoot = Path.Combine(dotnetRoot, "sdk", PinnedDotnetSdkVersion);
			string fixtureHostFxrRoot = Path.Combine(dotnetRoot, "host/fxr", PinnedDotnetHostFxrVersion);
			string fixtureRuntimeRoot = Path.Combine(
				dotnetRoot,
				"shared/Microsoft.NETCore.App",
				PinnedDotnetRuntimeVersion);
			Directory.CreateDirectory(Path.Combine(fixtureSdkRoot, "Sdks/Microsoft.NET.Sdk/Sdk"));
			Directory.CreateDirectory(fixtureHostFxrRoot);
			Directory.CreateDirectory(fixtureRuntimeRoot);
			string fixtureDotnetHost = Path.Combine(
				dotnetRoot,
				OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			File.WriteAllBytes(fixtureDotnetHost, [1]);
			File.WriteAllBytes(Path.Combine(fixtureSdkRoot, "MSBuild.dll"), [2]);
			File.WriteAllBytes(
				Path.Combine(fixtureSdkRoot, "Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props"),
				[3]);
			File.WriteAllBytes(Path.Combine(fixtureHostFxrRoot, GetPinnedHostFxrFileName()), [4]);
			File.WriteAllBytes(Path.Combine(fixtureRuntimeRoot, "System.Private.CoreLib.dll"), [5]);
			File.WriteAllBytes(Path.Combine(fixtureRuntimeRoot, GetPinnedHostPolicyFileName()), [6]);
			AssertApprovedDotnetHost(fixtureDotnetHost, dotnetRoot, fixtureRuntimeRoot);
			AssertExtraDotnetVersionDirectoryRejected(
				fixtureDotnetHost,
				dotnetRoot,
				fixtureRuntimeRoot,
				"sdk",
				"10.0.101");
			AssertExtraDotnetVersionDirectoryRejected(
				fixtureDotnetHost,
				dotnetRoot,
				fixtureRuntimeRoot,
				"host/fxr",
				"10.0.1");
			AssertExtraDotnetVersionDirectoryRejected(
				fixtureDotnetHost,
				dotnetRoot,
				fixtureRuntimeRoot,
				"shared/Microsoft.NETCore.App",
				"10.0.1");
			AssertApprovedDotnetHostRejected(
				fixtureDotnetHost,
				dotnetRoot,
				Path.Combine(dotnetRoot, "shared/Microsoft.NETCore.App/10.0.1"));
		}
		finally
		{
			Directory.Delete(fixtureRoot, recursive: true);
		}
	}

	[Fact]
	public void ErrorCodesAndMessagesAreFrozenAndPrivacyRedacted()
	{
		Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(LiquidOrdinaryWalletPlanWireErrorCode)));
		Assert.Equal(
			[
				"None",
				"InvalidArgument",
				"VersionMismatch",
				"InvalidEncoding",
				"LimitExceeded",
				"SourceBindingMismatch",
				"ContextRejected",
				"PlanRejected",
				"FundingRejected",
			],
			Enum.GetNames<LiquidOrdinaryWalletPlanWireErrorCode>());
		Assert.Equal(
			Enumerable.Range(0, 9).Select(value => (uint)value),
			Enum.GetValues<LiquidOrdinaryWalletPlanWireErrorCode>().Select(value => (uint)value));

		AssertExactErrorMessageMapping(code => code.GetMessage());
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactErrorMessageMapping(code =>
				code == LiquidOrdinaryWalletPlanWireErrorCode.FundingRejected
					? "ordinary wallet plan wire funding was rejecteD"
					: code.GetMessage()));

		string[] messages = Enumerable.Range(1, 8)
			.Select(value => ((LiquidOrdinaryWalletPlanWireErrorCode)value).GetMessage())
			.ToArray();
		Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
		Assert.All(messages, message =>
		{
			Assert.Equal(message.ToLowerInvariant(), message);
			Assert.DoesNotContain(IssuedAssetHex, message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("transaction", message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("address", message, StringComparison.OrdinalIgnoreCase);
		});
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidOrdinaryWalletPlanWireErrorCode.None.GetMessage());
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			((LiquidOrdinaryWalletPlanWireErrorCode)9).GetMessage());
	}

	[Fact]
	public void FundingRowNullPrecedenceIsAllocationFreeAndExhaustive()
	{
		var hostile = new ThrowingPayloadList();
		AssertRowRejected(
			null,
			hostile,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		Assert.Equal(0, hostile.CountReads);

		AssertRowRejected(
			[1],
			null,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertRowRejected(
			[],
			new byte[]?[] { [2], null, [1] },
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertRowRejected(
			new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength + 1],
			new byte[]?[] { null },
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
	}

	[Fact]
	public void FundingRowEnforcesEveryLengthCountAndOrderingBoundary()
	{
		AssertRowRejected([], [], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		AssertRowRejected([1], [[]], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		AssertRowRejected(
			[1],
			new NegativeCountList<byte[]?>(),
			LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);

		byte[] oversized = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength + 1];
		try
		{
			AssertRowRejected(oversized, [], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
			AssertRowRejected([1], [oversized], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(oversized);
		}

		byte[] same = [1, 2];
		AssertRowRejected([1], [same, same], LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding);
		AssertRowRejected([1], [[2], [1]], LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding);

		byte[] maximumPayload = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength];
		try
		{
			AssertRowRejected(
				[1],
				Enumerable.Repeat<byte[]?>(maximumPayload, 16).ToArray(),
				LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(maximumPayload);
		}

		byte[] shared = [1];
		var overCount = Enumerable.Repeat<byte[]?>(
			shared,
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount + 1).ToArray();
		AssertRowRejected([1], overCount, LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);

		byte[] maximumCandidate = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength];
		maximumCandidate[^1] = 1;
		try
		{
			using LiquidOrdinaryWalletPlanFundingRow maximum = CreateRow(maximumCandidate);
			Assert.Equal(
				LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength,
				GetField<byte[]>(maximum, "_candidateTransaction").Length);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(maximumCandidate);
		}

		byte[]?[] maximumPrevious = Enumerable.Range(
			0,
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount)
			.Select(index => (byte[]?)[(byte)(index >> 8), (byte)index])
			.ToArray();
		using LiquidOrdinaryWalletPlanFundingRow maximumPreviousRow = CreateRow([1], maximumPrevious);
		Assert.Equal(
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount,
			GetField<byte[][]>(maximumPreviousRow, "_previousTransactions").Length);
	}

	[Fact]
	public void FundingRowRejectsHostileOversizedCountBeforeSnapshotAllocationOrPayloadCopy()
	{
		byte[] payload = [0x7a];
		var oversized = new RepeatedValueList<byte[]?>(
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount + 1,
			payload);
		AssertRowRejected([1], oversized, LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		long before = GC.GetAllocatedBytesForCurrentThread();
		AssertRowRejected([1], oversized, LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(allocated < 1_024, $"Oversized funding-row rejection allocated {allocated} bytes.");
		Assert.Equal(4 * oversized.Count, oversized.ReadCount);

		var nullLast = new RepeatedValueList<byte[]?>(
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount + 1,
			payload,
			nullAt: LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount);
		AssertRowRejected(
			new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength + 1],
			nullLast,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
	}

	[Fact]
	public void FundingRowDefensivelyCopiesAndClearsEveryOwnedPayload()
	{
		byte[] candidate = [0xaa, 0xbb];
		byte[] previousA = [0x01];
		byte[] previousB = [0x02, 0x00];
		var source = new byte[]?[] { previousA, previousB };
		LiquidOrdinaryWalletPlanFundingRow row = CreateRow(candidate, source);
		byte[] retainedCandidate = GetField<byte[]>(row, "_candidateTransaction");
		byte[][] retainedPrevious = GetField<byte[][]>(row, "_previousTransactions");
		byte[] retainedPreviousA = retainedPrevious[0];
		byte[] retainedPreviousB = retainedPrevious[1];

		candidate.AsSpan().Fill(0xff);
		previousA.AsSpan().Fill(0xff);
		previousB.AsSpan().Fill(0xff);
		source[0] = [0xee];
		Assert.Equal(new byte[] { 0xaa, 0xbb }, retainedCandidate);
		Assert.Equal(new byte[] { 0x01 }, retainedPrevious[0]);
		Assert.Equal(new byte[] { 0x02, 0x00 }, retainedPrevious[1]);

		row.Dispose();
		row.Dispose();
		Assert.All(retainedCandidate, value => Assert.Equal(0, value));
		Assert.All(retainedPreviousA, value => Assert.Equal(0, value));
		Assert.All(retainedPreviousB, value => Assert.Equal(0, value));
		Assert.True(GetField<bool>(row, "_disposed"));
		Assert.Equal(nameof(LiquidOrdinaryWalletPlanFundingRow), row.ToString());
	}

	[Fact]
	public async Task FundingRowSnapshotsStatefulAndConcurrentlyMutatedSourcesAfterNullPreflightAsync()
	{
		var stableSnapshot = new StatefulPayloadList(
			firstReads: [[0xf1], [0xf2]],
			snapshotReads: [[0x01], [0x02]]);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([0xaa], stableSnapshot);
		Assert.Equal(2, GetField<byte[][]>(row, "_previousTransactions").Length);
		Assert.Equal([3, 3], stableSnapshot.ReadCounts);

		var nullOnSnapshot = new StatefulPayloadList(
			firstReads: [[1]],
			snapshotReads: [null]);
		AssertRowRejected(
			[],
			nullOnSnapshot,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		Assert.Equal([2], nullOnSnapshot.ReadCounts);

		using var firstRead = new ManualResetEventSlim();
		using var mutationComplete = new ManualResetEventSlim();
		byte[]? concurrentValue = [1];
		var concurrent = new CoordinatedSingleItemList<byte[]?>(
			() => concurrentValue,
			firstRead,
			mutationComplete);
		Task mutation = Task.Run(() =>
		{
			firstRead.Wait();
			concurrentValue = null;
			mutationComplete.Set();
		});
		AssertRowRejected(
			[],
			concurrent,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		await mutation;
	}

	[Fact]
	public void FundingBatchNullLifecycleAndCountPrecedenceIsFrozen()
	{
		LiquidOrdinaryWalletExactSpendPlan oneInput = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			100);
		LiquidOrdinaryWalletExactSpendPlan twoInputs = CreateTwoAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet).Plan;
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([2]);

		AssertBatchRejected(null, null, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(oneInput, null, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(oneInput, [null], LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(oneInput, [], LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(
			oneInput,
			new NegativeCountList<LiquidOrdinaryWalletPlanFundingRow?>(),
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		first.Dispose();
		AssertBatchRejected(
			twoInputs,
			[first, null],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		ObjectDisposedException disposedBeforeCount = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
				oneInput,
				[first, second],
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding row is disposed.",
			disposedBeforeCount.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public void FundingBatchEnforcesExpandedPreviousCountBeforeCopying()
	{
		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		byte[]?[] previous = Enumerable.Range(0, 8_193)
			.Select(index => (byte[]?)[(byte)(index >> 8), (byte)index])
			.ToArray();
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([1], previous);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([2], previous);

		AssertBatchRejected(
			fixture.Plan,
			[first, second],
			LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
	}

	[Fact]
	public void FundingBatchRejectsHostileOversizedCountBeforeSnapshotAllocationOrRowCopy()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			149);
		using LiquidOrdinaryWalletPlanFundingRow live = CreateRow([0x7a]);
		var oversized = new RepeatedValueList<LiquidOrdinaryWalletPlanFundingRow?>(
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1,
			live);
		AssertBatchRejected(plan, oversized, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		long before = GC.GetAllocatedBytesForCurrentThread();
		AssertBatchRejected(plan, oversized, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(allocated < 1_024, $"Oversized funding-batch rejection allocated {allocated} bytes.");
		Assert.Equal(4 * oversized.Count, oversized.ReadCount);

		using LiquidOrdinaryWalletPlanFundingRow disposed = CreateRow([0x7b]);
		disposed.Dispose();
		var nullLast = new RepeatedValueList<LiquidOrdinaryWalletPlanFundingRow?>(
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1,
			disposed,
			nullAt: LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount);
		AssertBatchRejected(plan, nullLast, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		var disposedOversized = new RepeatedValueList<LiquidOrdinaryWalletPlanFundingRow?>(
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1,
			disposed);
		ObjectDisposedException lifecycleBeforeCount = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
				plan,
				disposedOversized,
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding row is disposed.",
			lifecycleBeforeCount.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public async Task FundingBatchSnapshotsStatefulAndConcurrentlyMutatedRowsAfterNullPreflightAsync()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			150);
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([2]);
		var stableSnapshot = new StatefulRowList(
			firstReads: [second],
			snapshotReads: [first]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, stableSnapshot);
		Assert.Equal([3], stableSnapshot.ReadCounts);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(plan, batch, SourceEpoch);
		byte[] encoded = Copy(frame);
		try
		{
			Assert.Equal(1, encoded[240]);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(encoded);
		}

		var nullOnSnapshot = new StatefulRowList(
			firstReads: [first],
			snapshotReads: [null]);
		AssertBatchRejected(
			plan,
			nullOnSnapshot,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		using var firstRead = new ManualResetEventSlim();
		using var mutationComplete = new ManualResetEventSlim();
		LiquidOrdinaryWalletPlanFundingRow? concurrentValue = first;
		var concurrent = new CoordinatedSingleItemList<LiquidOrdinaryWalletPlanFundingRow?>(
			() => concurrentValue,
			firstRead,
			mutationComplete);
		Task mutation = Task.Run(() =>
		{
			firstRead.Wait();
			concurrentValue = null;
			mutationComplete.Set();
		});
		AssertBatchRejected(
			plan,
			concurrent,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		await mutation;
	}

	[Fact]
	public void EncoderWritesEveryCanonicalFieldInFrozenOrder()
	{
		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow(
			[0xaa],
			[0x01],
			[0x02, 0x00]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([0xbb, 0xcc]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(fixture.Plan, first, second);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(fixture.Plan, batch, SourceEpoch);
		byte[] encoded = Copy(frame);
		try
		{
			Assert.Equal("WLPQ"u8.ToArray(), encoded[..4]);
			Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(4, 2)));
			Assert.Equal(152, BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(6, 2)));
			Assert.Equal((ulong)encoded.Length, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(8, 8)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(16, 4)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(20, 4)));
			Assert.Equal(SourceEpoch, encoded[24..56]);
			Assert.Equal(fixture.Plan.SourceRevision, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(56, 8)));
			Assert.Equal(Convert.FromHexString(fixture.Manifest.ManifestId), encoded[64..96]);
			Assert.Equal(fixture.PeggedAsset.ToConsensusBytes(), encoded[96..128]);
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(128, 4)));
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(132, 4)));
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(136, 4)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(140, 4)));
			Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(144, 8)));

			int cursor = 152;
			AssertSelectedRow(
				encoded,
				ref cursor,
				fixture.FirstSelected,
				[0xaa],
				[[0x01], [0x02, 0x00]]);
			AssertSelectedRow(
				encoded,
				ref cursor,
				fixture.SecondSelected,
				[0xbb, 0xcc],
				[]);
			AssertDestination(encoded, ref cursor, fixture.FirstDestination);
			AssertDestination(encoded, ref cursor, fixture.SecondDestination);
			Assert.Equal(encoded.Length, cursor);
			Assert.Equal(
				152 + 2 * 88 + 2 * 48 + 2 * 4 + 6 +
				fixture.FirstDestination.GetAddress().GetCanonicalAddressText().Length +
				fixture.SecondDestination.GetAddress().GetCanonicalAddressText().Length,
				encoded.Length);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(encoded);
		}
	}

	[Fact]
	public void WireLimitsAndReachableLengthArithmeticAreFrozen()
	{
		Assert.Equal(32, LiquidOrdinaryWalletPlanWireLimits.SourceEpochLength);
		Assert.Equal(152, LiquidOrdinaryWalletPlanWireLimits.HeaderLength);
		Assert.Equal(88, LiquidOrdinaryWalletPlanWireLimits.SelectedFixedLength);
		Assert.Equal(48, LiquidOrdinaryWalletPlanWireLimits.DestinationFixedLength);
		Assert.Equal(4, LiquidOrdinaryWalletPlanWireLimits.PreviousLengthPrefix);
		Assert.Equal(256, LiquidOrdinaryWalletPlanWireLimits.MaximumAddressLength);
		Assert.Equal(4_194_304, LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength);
		Assert.Equal(16_384, LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount);
		Assert.Equal(67_108_864, LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength);
		Assert.Equal(
			LiquidOrdinaryWalletPlanWireLimits.HeaderLength +
				LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount *
				LiquidOrdinaryWalletPlanWireLimits.SelectedFixedLength +
				LiquidOrdinaryWalletExactSpendPlan.MaximumConfidentialOutputCount *
				LiquidOrdinaryWalletPlanWireLimits.DestinationFixedLength +
				LiquidOrdinaryWalletExactSpendPlan.MaximumConfidentialOutputCount *
				LiquidOrdinaryWalletPlanWireLimits.MaximumAddressLength +
				LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount *
				LiquidOrdinaryWalletPlanWireLimits.PreviousLengthPrefix +
				LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength,
			LiquidOrdinaryWalletPlanWireLimits.MaximumReachableFrameLength);
	}

	[Fact]
	public void EncoderSupportsBothReviewedContextsAndIsDeterministic()
	{
		foreach (ElementsPublicNetworkManifest manifest in new[]
		{
			ElementsPublicNetworkManifest.LiquidMainnet,
			ElementsPublicNetworkManifest.LiquidTestnet,
		})
		{
			LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(manifest, 200);
#pragma warning disable CA2000 // Each disposable owner is immediately declared with using.
			using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([0x01]);
			using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, row);
			using LiquidOrdinaryWalletPlanEncodedFrame first = Encode(plan, batch, SourceEpoch);
			using LiquidOrdinaryWalletPlanEncodedFrame second = Encode(plan, batch, SourceEpoch);
#pragma warning restore CA2000
			byte[] firstBytes = Copy(first);
			byte[] secondBytes = Copy(second);
			try
			{
				Assert.Equal(firstBytes, secondBytes);
				Assert.Equal(Convert.FromHexString(manifest.ManifestId), firstBytes[64..96]);
				Assert.Equal(
					LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId).ToConsensusBytes(),
					firstBytes[96..128]);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(firstBytes);
				CryptographicOperations.ZeroMemory(secondBytes);
			}
		}
	}

	[Fact]
	public void EncoderInvalidArgumentAndDisposedLifecyclePrecedenceIsFrozen()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			300);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, row);

		AssertEncodeRejected([], plan, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(new byte[31], plan, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(new byte[32], plan, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(SourceEpoch, null, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(SourceEpoch, plan, null, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		batch.Dispose();
		AssertEncodeRejected(SourceEpoch, null, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		ObjectDisposedException disposed = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanEncoder.TryEncode(
				new byte[31],
				plan,
				batch,
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding batch is disposed.",
			disposed.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public void BatchBindingMismatchIsInvalidArgumentWithFrozenCombinedPrecedence()
	{
		LiquidOrdinaryWalletExactSpendPlan firstPlan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			401);
		LiquidOrdinaryWalletExactSpendPlan secondPlan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidMainnet,
			402);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(firstPlan, row);

		AssertEncodeRejected(
			new byte[31],
			secondPlan,
			batch,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		LiquidOrdinaryWalletPlanEncodedFrame? frame = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanEncoder.TryEncode(
				SourceEpoch,
				secondPlan,
				batch,
				out frame,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(frame);
			Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, errorCode);
			string message = errorCode.GetMessage();
			Assert.DoesNotContain("401", message, StringComparison.Ordinal);
			Assert.DoesNotContain("402", message, StringComparison.Ordinal);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidTestnet.ManifestId,
				message,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidMainnet.ManifestId,
				message,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				message,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId,
				message,
				StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			frame?.Dispose();
		}

		batch.Dispose();
		ObjectDisposedException disposed = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanEncoder.TryEncode(
				new byte[31],
				secondPlan,
				batch,
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding batch is disposed.",
			disposed.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public void FundingAndFrameOwnershipIsIndependentAndDeterministicallyCleared()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			500);
		byte[] candidate = [0xaa, 0xbb];
		byte[] previous = [0x01];
		byte[] epoch = SourceEpoch.ToArray();
		LiquidOrdinaryWalletPlanFundingRow sourceRow = CreateRow(candidate, previous);
		LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, sourceRow);
		LiquidOrdinaryWalletPlanFundingRow[] retainedRows =
			GetField<LiquidOrdinaryWalletPlanFundingRow[]>(batch, "_rows");
		byte[] retainedCandidate = GetField<byte[]>(retainedRows[0], "_candidateTransaction");
		byte[][] retainedPrevious = GetField<byte[][]>(retainedRows[0], "_previousTransactions");
		byte[] retainedPreviousPayload = retainedPrevious[0];

		sourceRow.Dispose();
		candidate.AsSpan().Fill(0xff);
		previous.AsSpan().Fill(0xff);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(plan, batch, epoch);
		byte[] firstCopy = Copy(frame);
		byte[] secondCopy = Copy(frame);
		byte[] retainedFrame = GetField<byte[]>(frame, "_frame");
		try
		{
			epoch.AsSpan().Fill(0xff);
			firstCopy.AsSpan().Fill(0xee);
			Assert.NotEqual(firstCopy, secondCopy);
			Assert.Contains((byte)0xaa, secondCopy);
			Assert.Equal(SourceEpoch, secondCopy[24..56]);
			Assert.Equal(new byte[] { 0xaa, 0xbb }, retainedCandidate);
			Assert.Equal(new byte[] { 0x01 }, retainedPrevious[0]);
			ArgumentException wrongLength = Assert.Throws<ArgumentException>(() =>
				frame.CopyFrameTo(new byte[frame.Length - 1]));
			Assert.Equal(
				"An exact Liquid ordinary-wallet plan wire frame destination is required. (Parameter 'exactDestination')",
				wrongLength.Message);

			frame.Dispose();
			frame.Dispose();
			Assert.All(retainedFrame, value => Assert.Equal(0, value));
			Assert.Throws<ObjectDisposedException>(() => _ = frame.Length);
			Assert.Throws<ObjectDisposedException>(() => frame.CopyFrameTo(secondCopy));

			batch.Dispose();
			batch.Dispose();
			Assert.All(retainedCandidate, value => Assert.Equal(0, value));
			Assert.All(retainedPreviousPayload, value => Assert.Equal(0, value));
			Assert.All(retainedRows, Assert.Null);
			Assert.All(candidate, value => Assert.Equal(0xff, value));
			Assert.All(previous, value => Assert.Equal(0xff, value));
			Assert.All(epoch, value => Assert.Equal(0xff, value));
			Assert.Equal(nameof(LiquidOrdinaryWalletPlanFundingBatch), batch.ToString());
			Assert.Equal(nameof(LiquidOrdinaryWalletPlanEncodedFrame), frame.ToString());
		}
		finally
		{
			batch.Dispose();
			CryptographicOperations.ZeroMemory(firstCopy);
			CryptographicOperations.ZeroMemory(secondCopy);
			CryptographicOperations.ZeroMemory(epoch);
		}
	}

	[Fact]
	public void SurfaceIsInternalOwnedAndContainsNoExcludedAuthority()
	{
		Type[] ownerTypes =
		[
			typeof(LiquidOrdinaryWalletPlanFundingRow),
			typeof(LiquidOrdinaryWalletPlanFundingBatch),
			typeof(LiquidOrdinaryWalletPlanEncodedFrame),
		];
		foreach (Type type in ownerTypes)
		{
			Assert.False(type.IsVisible);
			Assert.Equal(typeof(LiquidOrdinaryWalletPlanEncoder), type.DeclaringType);
			Assert.True(type.IsSealed);
			Assert.Equal([typeof(IDisposable)], type.GetInterfaces());
			Assert.All(
				type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
				constructor => Assert.True(constructor.IsPrivate));
			Assert.DoesNotContain(type.GetCustomAttributesData(), attribute =>
				attribute.AttributeType.Name.Contains("Serializable", StringComparison.OrdinalIgnoreCase) ||
				attribute.AttributeType.Name.Contains("Debugger", StringComparison.OrdinalIgnoreCase));
		}

		Type encoder = typeof(LiquidOrdinaryWalletPlanEncoder);
		Assert.True(encoder.IsNotPublic);
		Assert.True(encoder.IsAbstract && encoder.IsSealed);
		Assert.Equal(
			["TryEncode"],
			encoder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
				.Where(method => !method.IsPrivate)
				.Select(method => method.Name)
				.Distinct(StringComparer.Ordinal));

		string[] forbidden =
		[
			"Decode", "Native", "PInvoke", "DllImport", "Provider", "Signer", "Pset",
			"Rpc", "Node", "File", "Directory", "Process", "Socket", "Http",
			"Broadcast", "CoinJoin", "Sponsor", "Usdt", "Regtest", "Fault", "Probe", "TestHook",
		];
		Type[] wireTypes = GetExactProductionWireTypes();
		Assert.All(wireTypes, type => Assert.False(type.IsVisible));
		Assert.DoesNotContain(wireTypes, type => forbidden.Any(fragment =>
			type.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
		Assert.DoesNotContain(
			wireTypes.SelectMany(type => type.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly)),
			method => method.GetCustomAttribute<DllImportAttribute>() is not null ||
				forbidden.Any(fragment => method.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
		Assert.DoesNotContain(
			encoder.Assembly.GetReferencedAssemblies(),
			assembly => (assembly.Name ?? "").Contains("liquid-native", StringComparison.OrdinalIgnoreCase));

		FieldInfo capability = Assert.Single(
			encoder.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			field => field.Name == "CooperationCapability");
		Assert.Equal("CooperationCapability", capability.Name);
		Assert.Equal(typeof(object), capability.FieldType);
		Assert.True(capability.IsPrivate && capability.IsInitOnly);
		MethodInfo ensureCooperation = Assert.Single(
			encoder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "EnsureCooperation" &&
				method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(object)]));
		Assert.True(ensureCooperation.IsPrivate);
		AssertCapabilityGuarded(
			typeof(LiquidOrdinaryWalletPlanEncodedFrame),
			ensureCooperation,
			"TakeOwnership");
		AssertCapabilityGuarded(
			typeof(LiquidOrdinaryWalletPlanFundingBatch),
			ensureCooperation,
			"TryEncode");
		AssertCapabilityGuarded(
			typeof(LiquidOrdinaryWalletPlanFundingRow),
			ensureCooperation,
			"CreateOwnedCopy", "EnsureNotDisposed", "GetEncodingShape", "WritePayloads");

		MethodInfo lockedEncode = Assert.Single(
			encoder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "TryEncodeLocked");
		Assert.True(lockedEncode.IsPrivate);
		MethodBase[] lockedCallers = GetExactProductionWireTypes()
			.SelectMany(GetDeclaredMethods)
			.Where(method => GetIlReferences(method).Contains(lockedEncode))
			.ToArray();
		MethodInfo batchEncode = Assert.Single(
			typeof(LiquidOrdinaryWalletPlanFundingBatch).GetMethods(
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "TryEncode");
		Assert.Equal([batchEncode], lockedCallers);
	}

	[Fact]
	public void CooperationCapabilityRejectsDirectTypeStateBypassesBeforeOwnershipTransfer()
	{
		byte[] arbitraryBytes = [0x57, 0x4c, 0x50, 0x51];
		byte[]? callerStorage = arbitraryBytes;
		Assert.Throws<InvalidOperationException>(() =>
			LiquidOrdinaryWalletPlanEncodedFrame.TakeOwnership(null, ref callerStorage));
		Assert.Same(arbitraryBytes, callerStorage);
		Assert.Equal(new byte[] { 0x57, 0x4c, 0x50, 0x51 }, arbitraryBytes);

		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			699);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, row);
		Assert.Throws<InvalidOperationException>(() => row.EnsureNotDisposed(null));
		Assert.Throws<InvalidOperationException>(() => row.GetEncodingShape(null));
		Assert.Throws<InvalidOperationException>(() => row.CreateOwnedCopy(null));
		Assert.Throws<InvalidOperationException>(() =>
		{
			int cursor = 0;
			row.WritePayloads(null, new byte[1], ref cursor);
		});
		Assert.Throws<InvalidOperationException>(() => batch.TryEncode(
			null,
			SourceEpoch,
			plan,
			out _,
			out _));
	}

	[Fact]
	public void ProductionSourceInventorySurfaceAndAuthorityAreFailClosed()
	{
		string[] expectedImplementationPaths =
		[
			"Liquid/Wallet/LiquidOrdinaryWalletExactSpendPlan.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanEncodedFrame.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanEncoder.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanFundingBatch.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanFundingRow.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanWireErrorCode.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanWireLimits.cs",
		];
		string productionRoot = GetProductionRoot();
		string wireRoot = GetWireProductionRoot();
		var buildAuthority = GetEvaluatedProductionBuildAuthority(productionRoot);
		AssertExactBuildAuthority(
			buildAuthority.Properties,
			buildAuthority.DotnetRoot,
			productionRoot,
			buildAuthority.GeneratedRoot);
		(string FullPath, string RelativePath, string Source)[] evaluatedCompileInputs =
			buildAuthority.CompileInputs;
		AssertExactImplementationCompileInputs(expectedImplementationPaths, productionRoot, evaluatedCompileInputs);
		AssertExactAmbientCompileAuthority(evaluatedCompileInputs);
		AssertExactAnalyzerAuthority(
			buildAuthority.Analyzers,
			buildAuthority.DotnetRoot,
			buildAuthority.PackageAuthority);
		AssertExactGeneratedSourceAuthority(buildAuthority.GeneratedSources);

		var declaredTypes = new List<string>();
		foreach (string sourcePath in expectedImplementationPaths)
		{
			string source = File.ReadAllText(Path.Combine(productionRoot, sourcePath));
			Assert.True(IsSafeWireSource(source));
			CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
				CSharpSyntaxTree.ParseText(source).GetRoot());
			Assert.DoesNotContain(root.DescendantTrivia(descendIntoTrivia: true),
				trivia => trivia.GetStructure() is DirectiveTriviaSyntax);
			Assert.DoesNotContain(
				root.DescendantTokens(),
				token => token.RawKind is (int)SyntaxKind.UnsafeKeyword or (int)SyntaxKind.ExternKeyword);
			Assert.DoesNotContain(
				root.DescendantNodes(),
				node => node is PointerTypeSyntax or FunctionPointerTypeSyntax or
					ImplicitStackAllocArrayCreationExpressionSyntax or FixedStatementSyntax);
			declaredTypes.AddRange(root.DescendantNodes()
				.OfType<BaseTypeDeclarationSyntax>()
				.Select(declaration => declaration.Identifier.ValueText));
		}

		Assert.Equal(
			new[]
			{
				"EncodingShape",
				"LiquidOrdinaryWalletExactSpendPlan",
				"LiquidOrdinaryWalletPlanEncodedFrame",
				"LiquidOrdinaryWalletPlanEncoder",
				"LiquidOrdinaryWalletPlanFundingBatch",
				"LiquidOrdinaryWalletPlanFundingRow",
				"LiquidOrdinaryWalletPlanWireErrorCode",
				"LiquidOrdinaryWalletPlanWireErrorCodeExtensions",
				"LiquidOrdinaryWalletPlanWireLimits",
			},
			declaredTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
		AssertExactPlanWireAccessorSource(File.ReadAllText(Path.Combine(
			productionRoot,
			"Liquid/Wallet/LiquidOrdinaryWalletExactSpendPlan.cs")));

		string encoderSource = File.ReadAllText(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanEncoder.cs"));
		Assert.Contains("fresh unpredictable epoch", encoderSource, StringComparison.Ordinal);
		Assert.Contains("never reuse", encoderSource, StringComparison.Ordinal);
		Assert.Contains("plaintext", encoderSource, StringComparison.Ordinal);
		Assert.Contains("not a secret", encoderSource, StringComparison.Ordinal);
		Assert.Contains("anti-replay", encoderSource, StringComparison.Ordinal);
		Assert.Contains("variable-time", encoderSource, StringComparison.Ordinal);
		Assert.Contains("linkable", encoderSource, StringComparison.Ordinal);
		Assert.Contains("actual confidential selected assets or values", encoderSource, StringComparison.Ordinal);
		Assert.Contains(
			"caller must clear every destination copy separately",
			File.ReadAllText(Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanEncodedFrame.cs")),
			StringComparison.Ordinal);

		Type[] exactTypes = GetExactProductionWireTypes();
		AssertExactWireTypeNames(
			new[]
			{
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanEncodedFrame",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingBatch",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingRow",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingRow+EncodingShape",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireErrorCode",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireErrorCodeExtensions",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireLimits",
			},
			exactTypes.Select(type => type.FullName!));
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanEncodedFrame),
			"CopyFrameTo", "Dispose", "TakeOwnership", "ToString", "get_Length");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanEncoder),
			"TryEncode");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanFundingBatch),
			"Dispose", "ToString", "TryCreate", "TryEncode");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanFundingRow),
			"CreateOwnedCopy", "Dispose", "EnsureNotDisposed", "GetEncodingShape", "ToString",
			"TryCreate", "WritePayloads");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanWireErrorCodeExtensions),
			"GetMessage");
		string surfaceManifest = string.Join(
			'\n',
			exactTypes.SelectMany(GetTypeSurfaceManifest).Order(StringComparer.Ordinal)) + "\n";
#if DEBUG
		string expectedSurfaceSha256 = ExpectedDebugWireSurfaceSha256;
#else
		string expectedSurfaceSha256 = ExpectedReleaseWireSurfaceSha256;
#endif
		string actualSurfaceSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(surfaceManifest))).ToLowerInvariant();
		Assert.True(
			StringComparer.Ordinal.Equals(expectedSurfaceSha256, actualSurfaceSha256),
			actualSurfaceSha256);

		MethodInfo[] exactPlanEntryPoints = GetExactPlanWireEntryPoints(exactTypes);
		Assert.Equal(
			new[]
			{
				"GetDestinationNetworkManifestId",
				"GetDestinationsForWireEncoding",
				"GetExplicitFee",
				"GetPeggedAssetId",
				"GetSelectedEntriesForWireEncoding",
				"get_SelectedInputCount",
				"get_SourceRevision",
			},
			exactPlanEntryPoints.Select(method => method.Name));
		MethodBase[] wireRoots = exactTypes
			.SelectMany(GetDeclaredMethods)
			.Concat(exactPlanEntryPoints)
			.Distinct()
			.OrderBy(MethodIdentity, StringComparer.Ordinal)
			.ToArray();
		MethodBase[] wireClosure = AssertWireMethodClosureSafe(wireRoots);
		Assert.All(wireRoots, root => Assert.Contains(root, wireClosure));
		string wireClosureManifest = BuildMethodClosureManifest(wireClosure);
#if DEBUG
		string expectedWireClosureSha256 = ExpectedDebugWireClosureSha256;
#else
		string expectedWireClosureSha256 = ExpectedReleaseWireClosureSha256;
#endif
		string actualWireClosureSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(wireClosureManifest))).ToLowerInvariant();
		Assert.True(
			StringComparer.Ordinal.Equals(
				expectedWireClosureSha256,
				actualWireClosureSha256),
			actualWireClosureSha256);
		AssertPeModuleInitializerAndAmbientClosureAuthority(
			typeof(LiquidOrdinaryWalletPlanEncoder).Assembly);

		foreach (Type type in exactTypes)
		{
			Assert.False(IsForbiddenWireIdentity(type.FullName ?? type.Name));
			foreach (MemberInfo member in type.GetMembers(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				Assert.False(IsForbiddenWireMember(member), $"forbidden member {type.FullName}.{member.Name}");
			}
			foreach (MethodBase method in GetDeclaredMethods(type))
			{
				Assert.DoesNotContain(
					method.GetMethodBody()?.ExceptionHandlingClauses ?? [],
					clause => clause.Flags == ExceptionHandlingClauseOptions.Clause &&
						IsForbiddenWireType(clause.CatchType));
				Assert.DoesNotContain(
					method.GetMethodBody()?.LocalVariables ?? [],
					local => IsForbiddenWireType(local.LocalType));
				Assert.DoesNotContain(GetIlReferences(method), IsForbiddenWireMember);
			}
		}

		Assert.True(IsForbiddenWireMember(typeof(WalletWasabi.Logging.Logger)
			.GetMethods().First(method => method.Name == "LogInfo")));
		Assert.True(IsForbiddenWireType(typeof(IServiceProvider)));
		Assert.True(IsForbiddenWireType(typeof(WalletWasabi.Liquid.Rpc.ElementsNodeStatus)));
		Assert.True(IsForbiddenWireType(typeof(FileStream)));
		Assert.True(IsForbiddenWireType(typeof(System.Net.Http.HttpClient)));
		Assert.True(IsForbiddenWireType(typeof(Thread)));
		Assert.True(IsForbiddenWireType(typeof(RandomNumberGenerator)));
		Assert.True(IsForbiddenWireType(typeof(NativeLibrary)));
		Assert.True(IsForbiddenWireIdentity("GetRawFrame"));
		Assert.False(IsSafeWireSource("#if DEBUG\ninternal static class Added { }\n#endif"));
		Assert.False(IsSafeWireSource("internal unsafe static class Added { }"));
		Assert.False(IsSafeWireSource("internal static class Added { internal static extern void Call(); }"));
		Assert.False(IsSafeWireSource("internal static class FaultProbe { }"));
		Assert.False(IsSafeWireSource("internal static class Added { private static void TestHook() { } }"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertApprovedDotnetHost(
				Path.Combine(Path.GetTempPath(), "fake-dotnet"),
				buildAuthority.DotnetRoot,
				GetLoadedRuntimeDirectory()));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactBuildAuthority(
				MutateBuildProperty(buildAuthority.Properties, "Configuration", "Unexpected"),
				buildAuthority.DotnetRoot,
				productionRoot,
				buildAuthority.GeneratedRoot));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactBuildAuthority(
				MutateBuildProperty(buildAuthority.Properties, "TargetFramework", "net9.0"),
				buildAuthority.DotnetRoot,
				productionRoot,
				buildAuthority.GeneratedRoot));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactBuildAuthority(
				MutateBuildProperty(buildAuthority.Properties, "Platform", "x64"),
				buildAuthority.DotnetRoot,
				productionRoot,
				buildAuthority.GeneratedRoot));
		foreach ((string property, string value) in new[]
		{
			("DirectoryBuildTargetsPath", "/wlpq/injected-directory-build.targets"),
			("CustomBeforeMicrosoftCommonTargets", "/wlpq/injected-analyzer.targets"),
			("CscToolPath", "/wlpq/unreviewed-compiler"),
			("Version", "9.9.9.9"),
			("AssemblyVersion", "9.9.9.9"),
			("FileVersion", "9.9.9.9"),
			("InformationalVersion", "9.9.9+wrong"),
			("IncludeSourceRevisionInInformationalVersion", "true"),
			("CommitHash", new string('b', 40)),
		})
		{
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertExactChildGlobalProperties(
					MutateBuildProperty(buildAuthority.GlobalProperties, property, value),
					buildAuthority.GlobalProperties));
		}
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactChildEnvironment(
				MutateBuildProperty(buildAuthority.ChildEnvironment, "NUGET_PACKAGES", "/wlpq/unreviewed-packages"),
				buildAuthority.ChildEnvironment));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactInvocationArguments(
				buildAuthority.InvocationArguments.Append("@/wlpq/injected-response.rsp").ToArray(),
				buildAuthority.InvocationArguments));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertConfiguredAuthorityHashes(
				buildAuthority.ImportClosureManifest + "IMPORT_EVENT_V2|[]\n",
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest,
				buildAuthority.ToolchainAuthorityManifest));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertConfiguredAuthorityHashes(
				string.Join('\n', buildAuthority.ImportClosureManifest.Split('\n').Reverse()),
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest,
				buildAuthority.ToolchainAuthorityManifest));
		string mutatedImportAssetPath = buildAuthority.ImportClosureManifest.Replace(
			"project.assets.json",
			"project.assets.MUTATED.json",
			StringComparison.Ordinal);
		Assert.NotEqual(buildAuthority.ImportClosureManifest, mutatedImportAssetPath);
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertConfiguredAuthorityHashes(
				mutatedImportAssetPath,
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest,
				buildAuthority.ToolchainAuthorityManifest));
		string blankImportRow = buildAuthority.ImportClosureManifest.Replace(
			"\nPIN_V2|",
			"\n\nPIN_V2|",
			StringComparison.Ordinal);
		Assert.NotEqual(buildAuthority.ImportClosureManifest, blankImportRow);
		AssertConfiguredAuthorityHashesRejects(
			blankImportRow,
			buildAuthority.ReferenceAuthorityManifest,
			buildAuthority.CompilerInputAuthorityManifest,
			buildAuthority.ToolchainAuthorityManifest);
		AssertConfiguredAuthorityHashesRejects(
			buildAuthority.ImportClosureManifest[..^1],
			buildAuthority.ReferenceAuthorityManifest,
			buildAuthority.CompilerInputAuthorityManifest,
			buildAuthority.ToolchainAuthorityManifest);
		AssertConfiguredAuthorityHashesRejects(
			buildAuthority.ImportClosureManifest + "\n",
			buildAuthority.ReferenceAuthorityManifest,
			buildAuthority.CompilerInputAuthorityManifest,
			buildAuthority.ToolchainAuthorityManifest);
		AssertConfiguredAuthorityHashesRejects(
			buildAuthority.ImportClosureManifest.Replace("\n", "\r\n", StringComparison.Ordinal),
			buildAuthority.ReferenceAuthorityManifest,
			buildAuthority.CompilerInputAuthorityManifest,
			buildAuthority.ToolchainAuthorityManifest);
		foreach (string canonicalRowMutation in new[]
		{
			CreateImportManifestWithDuplicatedLastImport(buildAuthority.ImportClosureManifest),
			CreateImportManifestWithoutLastImport(buildAuthority.ImportClosureManifest),
			CreateImportManifestWithSwappedFirstImports(buildAuthority.ImportClosureManifest),
			CreateImportManifestWithDuplicatedFirstPin(buildAuthority.ImportClosureManifest),
			CreateImportManifestWithMutatedFirstImportField(
				buildAuthority.ImportClosureManifest,
				2,
				"{REPO}/../outside.props"),
			CreateImportManifestWithMutatedFirstImportField(
				buildAuthority.ImportClosureManifest,
				2,
				"{REPO}\\..\\outside.props"),
			CreateImportManifestWithMutatedFirstImportField(
				buildAuthority.ImportClosureManifest,
				2,
				"$([MSBuild]::NormalizePath(`/tmp/unapproved.props`))"),
			CreateImportManifestWithMutatedFirstImportField(
				buildAuthority.ImportClosureManifest,
				3,
				"REPO|../outside.props"),
			CreateImportManifestWithMutatedFirstImportField(
				buildAuthority.ImportClosureManifest,
				3,
				"REPO|/absolute.props"),
		})
		{
			AssertConfiguredAuthorityHashesRejects(
				canonicalRowMutation,
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest,
				buildAuthority.ToolchainAuthorityManifest);
		}
		string mutatedReferenceManifest = CreateReferenceManifestWithMutatedFirstContent(
			buildAuthority.ReferenceAuthorityManifest);
		Assert.NotEqual(buildAuthority.ReferenceAuthorityManifest, mutatedReferenceManifest);
		AssertConfiguredAuthorityHashesRejects(
			buildAuthority.ImportClosureManifest,
			mutatedReferenceManifest,
			buildAuthority.CompilerInputAuthorityManifest,
			buildAuthority.ToolchainAuthorityManifest);

		const string HostileCompilerArgument = "/define:PIPE=left|right\nLINE=second";
		string hostileCompilerArgumentRow = BuildCanonicalAuthorityManifestRow(
			"COMPILER_INPUT_V2",
			["0", "ARG", "0", HostileCompilerArgument, "", "", "", ""]);
		Assert.DoesNotContain('\n', hostileCompilerArgumentRow);
		Assert.Equal(
			HostileCompilerArgument,
			ParseCanonicalAuthorityManifestRow(hostileCompilerArgumentRow, "COMPILER_INPUT_V2", 8)[3]);
		string hostileCompilerValues = JsonSerializer.Serialize(new[] { "left|right", "second\nline" });
		string hostileCompilerInputRow = BuildCanonicalAuthorityManifestRow(
			"COMPILER_INPUT_V2",
			["0", "CSC_INPUT", "0", "Sources", "", "Compile", hostileCompilerValues, ""]);
		string[] hostileCompilerInputFields = ParseCanonicalAuthorityManifestRow(
			hostileCompilerInputRow,
			"COMPILER_INPUT_V2",
			8);
		Assert.Equal(
			new[] { "left|right", "second\nline" },
			ParseCanonicalCompilerAuthorityValues(hostileCompilerInputFields[6]));

		var canonicalCompilerMutations = new List<string>
		{
			CreateCompilerManifestWithoutFirstArgument(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithDuplicatedFirstArgument(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithSwappedFirstArguments(buildAuthority.CompilerInputAuthorityManifest),
		};
		bool hasCompilerAuxiliaryRow = AssertCanonicalCompilerInputAuthorityManifest(
			buildAuthority.CompilerInputAuthorityManifest).Any(row =>
				ParseCanonicalAuthorityManifestRow(row, "COMPILER_INPUT_V2", 8)[1] == "AUX");
		if (hasCompilerAuxiliaryRow)
		{
			canonicalCompilerMutations.Add(
				CreateCompilerManifestWithMutatedFirstAuxiliarySha256(
					buildAuthority.CompilerInputAuthorityManifest));
		}
		foreach (string canonicalCompilerMutation in canonicalCompilerMutations)
		{
			Assert.NotEqual(buildAuthority.CompilerInputAuthorityManifest, canonicalCompilerMutation);
			_ = AssertCanonicalCompilerInputAuthorityManifest(canonicalCompilerMutation);
			AssertConfiguredAuthorityHashesRejects(
				buildAuthority.ImportClosureManifest,
				buildAuthority.ReferenceAuthorityManifest,
				canonicalCompilerMutation,
				buildAuthority.ToolchainAuthorityManifest);
		}
		var malformedCompilerMutations = new List<string>
		{
			buildAuthority.CompilerInputAuthorityManifest[..^1],
			buildAuthority.CompilerInputAuthorityManifest + "\n",
			buildAuthority.CompilerInputAuthorityManifest.Replace("\n", "\r\n", StringComparison.Ordinal),
			CreateCompilerManifestWithV1Header(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithNonCanonicalFirstRow(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithSkippedGlobalIndex(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithSkippedSectionIndex(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithUnknownFirstSection(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithExtraFirstField(buildAuthority.CompilerInputAuthorityManifest),
			CreateCompilerManifestWithNumericFirstIndex(buildAuthority.CompilerInputAuthorityManifest),
		};
		if (hasCompilerAuxiliaryRow)
		{
			malformedCompilerMutations.Add(
				CreateCompilerManifestWithInvalidAuxiliaryPrefix(
					buildAuthority.CompilerInputAuthorityManifest));
		}
		foreach (string malformedCompilerMutation in malformedCompilerMutations)
		{
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertCanonicalCompilerInputAuthorityManifest(malformedCompilerMutation));
		}
		string canonicalToolchainMutation = CreateCombinedToolchainAuthorityWithMutatedFirstFileSha256(
			buildAuthority.ToolchainAuthorityManifest);
		Assert.NotEqual(buildAuthority.ToolchainAuthorityManifest, canonicalToolchainMutation);
		AssertConfiguredAuthorityHashesRejects(
			buildAuthority.ImportClosureManifest,
			buildAuthority.ReferenceAuthorityManifest,
			buildAuthority.CompilerInputAuthorityManifest,
			canonicalToolchainMutation);
		string mutatedToolchainManifest =
			buildAuthority.ToolchainAuthorityManifest +
			"TOOLCHAIN_FILE_V2|[\"injected\",\"" + new string('0', 64) + "\"]\n";
		Assert.NotEqual(buildAuthority.ToolchainAuthorityManifest, mutatedToolchainManifest);
		AssertConfiguredAuthorityHashesRejects(
			buildAuthority.ImportClosureManifest,
			buildAuthority.ReferenceAuthorityManifest,
			buildAuthority.CompilerInputAuthorityManifest,
			mutatedToolchainManifest);

		string packageMutationRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-package-authority-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(packageMutationRoot);
			string primaryPackageRoot = Path.Combine(packageMutationRoot, "packages");
			string fallbackPackageRoot = Path.Combine(packageMutationRoot, "fallback");
			string nestedPackageRoot = Path.Combine(primaryPackageRoot, "nested");
			string undeclaredPackageRoot = Path.Combine(packageMutationRoot, "unapproved/.nuget/packages");
			Directory.CreateDirectory(primaryPackageRoot);
			Directory.CreateDirectory(fallbackPackageRoot);
			Directory.CreateDirectory(nestedPackageRoot);
			Directory.CreateDirectory(undeclaredPackageRoot);
			string syntheticAssets = Path.Combine(packageMutationRoot, "project.assets.json");

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(fallbackPackageRoot, true));
			(string PrimaryRoot, string[] OrderedRoots) multiRoot =
				GetPinnedPackageAuthority(syntheticAssets);
			Assert.Equal(primaryPackageRoot, multiRoot.PrimaryRoot);
			Assert.Equal([primaryPackageRoot, fallbackPackageRoot], multiRoot.OrderedRoots);

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true));
			(string PrimaryRoot, string[] OrderedRoots) singleRoot =
				GetPinnedPackageAuthority(syntheticAssets);
			Assert.Equal(primaryPackageRoot, singleRoot.PrimaryRoot);
			Assert.Equal([primaryPackageRoot], singleRoot.OrderedRoots);

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(fallbackPackageRoot, true),
				(primaryPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(primaryPackageRoot + Path.DirectorySeparatorChar, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(nestedPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				Path.Combine(primaryPackageRoot, "..", "fallback"),
				(fallbackPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				"relative/packages",
				(primaryPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(syntheticAssets, primaryPackageRoot);
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, false));
			AssertPackageAuthorityRejected(syntheticAssets);
			string missingPackageRoot = Path.Combine(packageMutationRoot, "missing");
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				missingPackageRoot,
				(missingPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			File.WriteAllText(
				syntheticAssets,
				"{\"project\":{\"restore\":{}},\"packageFolders\":{}}",
				Encoding.UTF8);
			AssertPackageAuthorityRejected(syntheticAssets);

			string linkedPackageRoot = Path.Combine(packageMutationRoot, "linked-packages");
			Directory.CreateSymbolicLink(linkedPackageRoot, fallbackPackageRoot);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				linkedPackageRoot,
				(linkedPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(fallbackPackageRoot, true));
			multiRoot = GetPinnedPackageAuthority(syntheticAssets);
			string relativePackageFile = "example.package/1.2.3/lib/net10.0/Example.dll";
			string primaryPackageFile = Path.Combine(
				primaryPackageRoot,
				relativePackageFile.Replace('/', Path.DirectorySeparatorChar));
			string fallbackPackageFile = Path.Combine(
				fallbackPackageRoot,
				relativePackageFile.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(primaryPackageFile)!);
			Directory.CreateDirectory(Path.GetDirectoryName(fallbackPackageFile)!);
			File.WriteAllBytes(primaryPackageFile, [1, 2, 3, 4]);
			File.WriteAllBytes(fallbackPackageFile, [1, 2, 3, 4]);
			string expectedPackageIdentity = $"NUGET|{relativePackageFile}";
			Assert.Equal(
				expectedPackageIdentity,
				NormalizeAuthorityPath(
					primaryPackageFile,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					multiRoot));
			Assert.Equal(
				expectedPackageIdentity,
				NormalizeAuthorityPath(
					fallbackPackageFile,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					multiRoot));
			Assert.Equal(
				$"/reference:{{NUGET}}/{relativePackageFile}",
				NormalizeAuthorityStringWithPackages(
					$"/reference:{fallbackPackageFile}",
					multiRoot));
			string adjacentRootLookalike = NormalizeAuthorityStringWithPackages(
				$"/reference:{fallbackPackageRoot}-undeclared/{relativePackageFile}",
				multiRoot);
			Assert.DoesNotContain("{NUGET}", adjacentRootLookalike, StringComparison.Ordinal);
			string normalizedImportExpression = NormalizeAndValidateUnexpandedImportProject(
				$"$(ImportRoot)={buildAuthority.RepositoryRoot}/Directory.Build.props",
				multiRoot,
				buildAuthority.RepositoryRoot,
				buildAuthority.DotnetRoot,
				undeclaredPackageRoot);
			Assert.Equal("$(ImportRoot)={REPO}/Directory.Build.props", normalizedImportExpression);
			Assert.Equal(
				"$(ImportRoot)={NUGET}/example.props",
				NormalizeAndValidateUnexpandedImportProject(
					$"$(ImportRoot)={fallbackPackageRoot}/example.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$(ImportRoot)={DOTNET}/sdk/example.props",
				NormalizeAndValidateUnexpandedImportProject(
					$"$(ImportRoot)={buildAuthority.DotnetRoot}/sdk/example.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$(ImportRoot)={AUTHORITY}/example.props",
				NormalizeAndValidateUnexpandedImportProject(
					$"$(ImportRoot)={undeclaredPackageRoot}/example.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$(MSBuildToolsPath)/Microsoft.Common.props",
				NormalizeAndValidateUnexpandedImportProject(
					"$(MSBuildToolsPath)/Microsoft.Common.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$(Root)/x;../y",
				NormalizeAndValidateUnexpandedImportProject(
					"$(Root)/x;../y",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$(MSBuildThisFileDirectory)../x.props",
				NormalizeAndValidateUnexpandedImportProject(
					"$(MSBuildThisFileDirectory)../x.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$([MSBuild]::GetToolsDirectory32())/../x.props",
				NormalizeAndValidateUnexpandedImportProject(
					"$([MSBuild]::GetToolsDirectory32())/../x.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$(Root)/packages+/x.props",
				NormalizeAndValidateUnexpandedImportProject(
					"$(Root)/packages+/x.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			Assert.Equal(
				"$(Root)/foo?/x.props",
				NormalizeAndValidateUnexpandedImportProject(
					"$(Root)/foo?/x.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			foreach (string punctuationPath in new[]
			{
				"$(Root)/packages,/x.props",
				"$(Root)/packages(/x.props",
				"$(Root)/packages[/x.props",
			})
			{
				Assert.Equal(
					punctuationPath,
					NormalizeAndValidateUnexpandedImportProject(
						punctuationPath,
						multiRoot,
						buildAuthority.RepositoryRoot,
						buildAuthority.DotnetRoot,
						undeclaredPackageRoot));
			}
			Assert.Equal(
				"{REPO}/nested/../inside.props",
				NormalizeAndValidateUnexpandedImportProject(
					$"{buildAuthority.RepositoryRoot}/nested/../inside.props",
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot));
			foreach (string rejectedImportExpression in new[]
			{
				"/tmp/unapproved.props",
				"//server/share/unapproved.props",
				"\\\\server\\share\\unapproved.props",
				"C:\\temp\\unapproved.props",
				"file:///tmp/unapproved.props",
				"https://example.invalid/unapproved.props",
				"$([MSBuild]::NormalizePath(`/tmp/unapproved.props`))",
				"$([System.IO.Path]::Combine(a/b,/tmp/unapproved.props))",
				"$(ImportRoot)=/tmp/unapproved.props",
				$"{buildAuthority.RepositoryRoot}/../outside.props",
				$"{buildAuthority.RepositoryRoot}/nested/../../outside.props",
				$"{buildAuthority.DotnetRoot}/../outside.props",
				$"{fallbackPackageRoot}/../outside.props",
				$"{undeclaredPackageRoot}/../outside.props",
				$"$(ImportRoot)={buildAuthority.RepositoryRoot}-undeclared/Directory.Build.props",
				$"$(ImportRoot)={fallbackPackageRoot}-undeclared/example.props",
				"{REPO}/literal-token.props",
				"{DOTNET}/literal-token.props",
				"{AUTHORITY}/literal-token.props",
				"{NUGET}/literal-token.props",
			})
			{
				Xunit.Sdk.XunitException exception = AssertUnexpandedImportProjectRejected(
					rejectedImportExpression,
					multiRoot,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					undeclaredPackageRoot);
				Assert.DoesNotContain(rejectedImportExpression, exception.Message, StringComparison.Ordinal);
			}

			string hostileImportValue = "x|SOURCE|forged\nPIN_V2|[\"spoof\"]\r\\\"";
			string hostileImportRow = BuildCanonicalImportManifestRow(
				"IMPORT_EVENT_V2",
				["0", "0", hostileImportValue, "REPO|Directory.Build.props", "1", "1", "null", "", ""]);
			Assert.DoesNotContain('\n', hostileImportRow);
			Assert.DoesNotContain('\r', hostileImportRow);
			Assert.Equal(
				hostileImportValue,
				ParseCanonicalImportManifestRow(hostileImportRow, "IMPORT_EVENT_V2", 9)[2]);
			string hostileReferencePath = "NUGET|package/with|PROVENANCE|marker/reference.dll";
			string hostileReferenceProvenance = "REPO|WalletWasabi/with|ALIASES|marker.csproj";
			string hostileReferenceAliases = "global|REFERENCE_V2|forged\nrow\r";
			string hostileReferenceRow = BuildCanonicalAuthorityManifestRow(
				"REFERENCE_V2",
				[
					"0",
					hostileReferencePath,
					new string('a', 64),
					hostileReferenceProvenance,
					hostileReferenceAliases,
				]);
			Assert.DoesNotContain('\n', hostileReferenceRow);
			Assert.DoesNotContain('\r', hostileReferenceRow);
			string[] hostileReferenceRows = AssertCanonicalReferenceAuthorityManifest(
				"REFERENCE_AUTHORITY_V2\n" + hostileReferenceRow + "\n");
			string[] hostileReferenceFields = ParseCanonicalAuthorityManifestRow(
				Assert.Single(hostileReferenceRows),
				"REFERENCE_V2",
				5);
			Assert.Equal(hostileReferencePath, hostileReferenceFields[1]);
			Assert.Equal(hostileReferenceProvenance, hostileReferenceFields[3]);
			Assert.Equal(hostileReferenceAliases, hostileReferenceFields[4]);
			string optionalImportExpression = NormalizeAndValidateUnexpandedImportProject(
				"$(OptionalImport)/x.props",
				multiRoot,
				buildAuthority.RepositoryRoot,
				buildAuthority.DotnetRoot,
				undeclaredPackageRoot);
			string skippedImportRow = BuildCanonicalImportManifestRow(
				"IMPORT_EVENT_V2",
				["0", "1", optionalImportExpression, "REPO|Directory.Build.props", "1", "1", "null", "", ""]);
			string[] skippedImportFields = ParseCanonicalImportManifestRow(skippedImportRow, "IMPORT_EVENT_V2", 9);
			Assert.Equal("$(OptionalImport)/x.props", skippedImportFields[2]);
			Assert.Equal("null", skippedImportFields[6]);
			Assert.Equal("", skippedImportFields[7]);
			Assert.Equal("", skippedImportFields[8]);
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				ParseCanonicalImportManifestRow("IMPORT_EVENT_V2|[\"0\"]", "IMPORT_EVENT_V2", 9));
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				ParseCanonicalImportManifestRow(
					"IMPORT_EVENT_V2|[0,\"0\",\"A\",\"REPO|x\",\"1\",\"1\",\"null\",\"\",\"\"]",
					"IMPORT_EVENT_V2",
					9));
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				ParseCanonicalImportManifestRow(
					"IMPORT_EVENT_V2|[ \"0\",\"0\",\"A\",\"REPO|x\",\"1\",\"1\",\"null\",\"\",\"\"]",
					"IMPORT_EVENT_V2",
					9));
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				BuildCanonicalImportManifestRow(
					"IMPORT_EVENT_V2",
					["0", "0", "\ud800", "REPO|x", "1", "1", "null", "", ""]));
			string repeatedA0 = BuildCanonicalImportManifestRow(
				"IMPORT_EVENT_V2",
				["0", "0", "A", "REPO|Directory.Build.props", "1", "1", "null", "", ""]);
			string distinctB1 = BuildCanonicalImportManifestRow(
				"IMPORT_EVENT_V2",
				["1", "0", "B", "REPO|Directory.Build.props", "1", "1", "null", "", ""]);
			string repeatedA2 = BuildCanonicalImportManifestRow(
				"IMPORT_EVENT_V2",
				["2", "0", "A", "REPO|Directory.Build.props", "1", "1", "null", "", ""]);
			string orderedRepeatedImports = string.Join('\n', repeatedA0, distinctB1, repeatedA2);
			Assert.NotEqual(
				Sha256Text(orderedRepeatedImports),
				Sha256Text(string.Join('\n', distinctB1, repeatedA0, repeatedA2)));
			Assert.Equal(
				"A",
				ParseCanonicalImportManifestRow(repeatedA2, "IMPORT_EVENT_V2", 9)[2]);

			File.WriteAllBytes(fallbackPackageFile, [1, 2, 3, 5]);
			AssertPackagePathRejected(
				primaryPackageFile,
				buildAuthority.RepositoryRoot,
				buildAuthority.DotnetRoot,
				multiRoot);

			string undeclaredPackageFile = Path.Combine(
				undeclaredPackageRoot,
				relativePackageFile.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(undeclaredPackageFile)!);
			File.WriteAllBytes(undeclaredPackageFile, [1, 2, 3, 4]);
			AssertPackagePathRejected(
				undeclaredPackageFile,
				buildAuthority.RepositoryRoot,
				buildAuthority.DotnetRoot,
				multiRoot);

			string nestedAuthorityRoot = Path.Combine(packageMutationRoot, "specific-authority");
			Directory.CreateDirectory(nestedAuthorityRoot);
			string nestedAuthorityFile = Path.Combine(nestedAuthorityRoot, "input.txt");
			File.WriteAllText(nestedAuthorityFile, "authority", Encoding.UTF8);
			Assert.Equal(
				"AUTHORITY|input.txt",
				NormalizeAuthorityPath(
					nestedAuthorityFile,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					multiRoot,
					nestedAuthorityRoot));
			Assert.Equal(
				"DOTNET|dotnet",
				NormalizeAuthorityPath(
					Path.Combine(buildAuthority.DotnetRoot, "dotnet"),
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					multiRoot));
			bool exactRootOverlapRejected = false;
			try
			{
				_ = NormalizeAuthorityPath(
					nestedAuthorityFile,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					multiRoot,
					buildAuthority.RepositoryRoot);
			}
			catch (Xunit.Sdk.XunitException)
			{
				exactRootOverlapRejected = true;
			}
			Assert.True(exactRootOverlapRejected, "An exactly overlapping authority root was accepted.");
		}
		finally
		{
			if (Directory.Exists(packageMutationRoot))
			{
				Directory.Delete(packageMutationRoot, recursive: true);
			}
		}

		string symlinkMutationRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-symlink-mutation-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(symlinkMutationRoot);
			string targetDirectory = Path.Combine(symlinkMutationRoot, "target");
			string link = Path.Combine(symlinkMutationRoot, "linked-directory");
			Directory.CreateDirectory(targetDirectory);
			string target = Path.Combine(targetDirectory, "regular.props");
			File.WriteAllText(target, "<Project />", Encoding.UTF8);
			Directory.CreateSymbolicLink(link, targetDirectory);
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertRegularAuthorityFile(
					Path.Combine(link, "regular.props"),
					"symbolic-link ancestor mutation"));
		}
		finally
		{
			if (Directory.Exists(symlinkMutationRoot))
			{
				Directory.Delete(symlinkMutationRoot, recursive: true);
			}
		}

		string linkedFullPath = Path.GetFullPath(Path.Combine(
			productionRoot,
			"../linked/LiquidOrdinaryWalletPlanEncoder.Linked.cs"));
		var linkedExplicitInclude = (
			FullPath: linkedFullPath,
			RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, linkedFullPath)),
			Source: "namespace WalletWasabi.Liquid.Wallet.Wire; internal static partial class LiquidOrdinaryWalletPlanEncoder { }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactImplementationCompileInputs(
				expectedImplementationPaths,
				productionRoot,
				evaluatedCompileInputs.Append(linkedExplicitInclude).ToArray()));

		string generatedFullPath = Path.GetFullPath(Path.Combine(
			productionRoot,
			"obj/Debug/net10.0/LiquidOrdinaryWalletPlanEncoder.Generated.cs"));
		var generatedCompileItem = (
			FullPath: generatedFullPath,
			RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, generatedFullPath)),
			Source: "namespace WalletWasabi.Liquid.Wallet.Wire; internal static partial class LiquidOrdinaryWalletPlanEncoder { }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactImplementationCompileInputs(
				expectedImplementationPaths,
				productionRoot,
				evaluatedCompileInputs.Append(generatedCompileItem).ToArray()));

		string nestedFullPath = Path.GetFullPath(Path.Combine(
			productionRoot,
			"Liquid/Wallet/Wire/Nested/AdditionalWireAuthority.cs"));
		var nestedPartialContribution = (
			FullPath: nestedFullPath,
			RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, nestedFullPath)),
			Source: "namespace WalletWasabi.Liquid.Wallet.Wire.Nested; internal static partial class AdditionalWireAuthority { }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactImplementationCompileInputs(
				expectedImplementationPaths,
				productionRoot,
				evaluatedCompileInputs.Append(nestedPartialContribution).ToArray()));

		foreach (string condition in new[] { "Configuration", "TargetFramework", "Platform" })
		{
			string conditionalFullPath = Path.GetFullPath(Path.Combine(
				productionRoot,
				$"obj/authority-mutation/{condition}/LiquidOrdinaryWalletPlanEncoder.Conditional.cs"));
			var conditionalContributor = (
				FullPath: conditionalFullPath,
				RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, conditionalFullPath)),
				Source: "namespace WalletWasabi.Liquid.Wallet.Wire; internal static partial class LiquidOrdinaryWalletPlanEncoder { }");
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertExactImplementationCompileInputs(
					expectedImplementationPaths,
					productionRoot,
					evaluatedCompileInputs.Append(conditionalContributor).ToArray()));
		}

		var ambientModuleInitializer = (
			FullPath: Path.GetFullPath(Path.Combine(productionRoot, "AmbientModuleInitializer.cs")),
			RelativePath: "AmbientModuleInitializer.cs",
			Source: "using System.Runtime.CompilerServices; internal static class Added { [ModuleInitializer] internal static void Initialize() { } }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactAmbientCompileAuthority(evaluatedCompileInputs.Append(ambientModuleInitializer)));
		var ambientAssemblyAttribute = (
			FullPath: Path.GetFullPath(Path.Combine(productionRoot, "AmbientAssemblyAttribute.cs")),
			RelativePath: "AmbientAssemblyAttribute.cs",
			Source: "[assembly: System.CLSCompliant(true)]");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactAmbientCompileAuthority(evaluatedCompileInputs.Append(ambientAssemblyAttribute)));
		var ambientGlobalAlias = (
			FullPath: Path.GetFullPath(Path.Combine(productionRoot, "AmbientGlobalAlias.cs")),
			RelativePath: "AmbientGlobalAlias.cs",
			Source: "global using WireAlias = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder;");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactAmbientCompileAuthority(evaluatedCompileInputs.Append(ambientGlobalAlias)));
		Assert.Contains(
			"BeforeTargets=\"CoreCompile\"",
			buildAuthority.InjectedAnalyzerTargetContent,
			StringComparison.Ordinal);
		string injectedAnalyzerCompilerManifest = CreateCompilerManifestWithInjectedAnalyzerArguments(
			buildAuthority.CompilerInputAuthorityManifest);
		_ = AssertCanonicalCompilerInputAuthorityManifest(injectedAnalyzerCompilerManifest);
		AssertConfiguredAuthorityHashesRejects(
			buildAuthority.ImportClosureManifest,
			buildAuthority.ReferenceAuthorityManifest,
			injectedAnalyzerCompilerManifest,
			buildAuthority.ToolchainAuthorityManifest);
		Assert.Contains(
			"<Analyzer Include=\"/wlpq/injected-analyzer.dll\" />",
			buildAuthority.InjectedAnalyzerTargetContent,
			StringComparison.Ordinal);
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactGeneratedSourceAuthority(buildAuthority.GeneratedSources.Append((
				new GeneratedBuildFile(
					"FakeGenerator/Fake.Generated.cs",
					"namespace WalletWasabi.Liquid.Wallet.Wire; internal static class GeneratedAuthority { }",
					new string('0', 64))))));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactPlanWireAccessorSource(
				File.ReadAllText(Path.Combine(productionRoot, "Liquid/Wallet/LiquidOrdinaryWalletExactSpendPlan.cs"))
					.Replace("public int SelectedInputCount => _selectedEntries.Length;", "public int SelectedInputCount => 0;", StringComparison.Ordinal)));

		foreach (MethodInfo forbiddenClosureMutation in CreateForbiddenClosureMutations())
		{
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertWireMethodClosureSafe([forbiddenClosureMutation]));
		}
		Assert.True(IsProductionWireNamespace("WalletWasabi.Liquid.Wallet.Wire.Nested"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactWireTypeNames(
				exactTypes.Select(type => type.FullName!),
				exactTypes.Select(type => type.FullName!)
					.Append("WalletWasabi.Liquid.Wallet.Wire.Nested.Added")));
	}

	[Fact]
	public void RestoreArtifactAuthorityIsPortableAndMutationClosed()
	{
		const string currentRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
		const string otherRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
		const string numericRevision = "37647bc08a1af3de43979429880e40f14de20290";
		const string filteredNumericRevision = "70000a12bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
		const string retainedBoundaryRevision = "65534abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
		const string filteredBoundaryRevision = "65535a12bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
		const string zeroPaddedRevision = "00001abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
		Assert.Equal(40, retainedBoundaryRevision.Length);
		Assert.Equal(40, filteredBoundaryRevision.Length);
		Assert.Equal(40, zeroPaddedRevision.Length);
		Assert.Equal(
			"1.2.3+release",
			RemoveSdkSourceRevisionSuffix("1.2.3+release", "", currentRevision));
		Assert.Equal(
			"1.2.3+release",
			RemoveSdkSourceRevisionSuffix($"1.2.3+release.{currentRevision}", "", currentRevision));
		Assert.Equal(
			$"1.2.3+{currentRevision}",
			RemoveSdkSourceRevisionSuffix($"1.2.3+{currentRevision}", "", null));
		Assert.Equal(
			$"1.2.3+{currentRevision}",
			RemoveSdkSourceRevisionSuffix($"1.2.3+{currentRevision}", "", new string('b', 40)));
		Assert.Equal(
			$"1.2.3+{currentRevision}",
			RemoveSdkSourceRevisionSuffix($"1.2.3+{currentRevision}", currentRevision, null));
		Assert.Equal(
			"1.2.3+release",
			RemoveSdkSourceRevisionSuffix(
				$"1.2.3+release.{currentRevision}",
				currentRevision,
				currentRevision));
		AssertPinnedNixInformationalVersionAuthority(
			$"2.0.0-20260812-{currentRevision}",
			currentRevision,
			pinnedNixProfile: true);
		AssertPinnedNixInformationalVersionAuthority(
			$"2.0.0-20260812-{currentRevision}+{currentRevision}",
			currentRevision,
			pinnedNixProfile: true);
		Assert.Equal(
			$"2.0.0-20260812-{currentRevision}",
			GetPinnedNixProjectAssetsVersion(
				$"2.0.0-20260812-{currentRevision}",
				currentRevision));
		Assert.Equal(
			$"2.0.0-20260812-{currentRevision}",
			GetPinnedNixProjectAssetsVersion(
				$"2.0.0-20260812-{currentRevision}+{currentRevision}",
				currentRevision));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			GetPinnedNixProjectAssetsVersion(
				$"2.0.0-20260812-{currentRevision}+{otherRevision}",
				currentRevision));
		AssertPinnedNixInformationalVersionAuthority(
			"2.0.0-beta",
			currentRevision,
			pinnedNixProfile: false);
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertPinnedNixInformationalVersionAuthority(
				"9.9.9",
				currentRevision,
				pinnedNixProfile: true));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertPinnedNixInformationalVersionAuthority(
				$"2.0.0-2026812-{currentRevision}+{currentRevision}",
				currentRevision,
				pinnedNixProfile: true));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertPinnedNixInformationalVersionAuthority(
				$"2.0.0-20260812-{otherRevision}+{currentRevision}",
				currentRevision,
				pinnedNixProfile: true));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertPinnedNixInformationalVersionAuthority(
				$"2.0.0-20260812-{currentRevision}+release",
				currentRevision,
				pinnedNixProfile: true));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertPinnedNixInformationalVersionAuthority(
				$"2.0.0-20260812-{currentRevision}+{otherRevision}",
				currentRevision,
				pinnedNixProfile: true));
		AssertLoadedProductBuildIdentityAuthority(
			"1.2.3.4",
			"1.2.3.4",
			"2.0.0-beta",
			currentRevision,
			pinnedNixProfile: false);
		AssertLoadedProductBuildIdentityAuthority(
			"2.0.0.0",
			"2.0.0.0",
			$"2.0.0-20260812-{currentRevision}",
			currentRevision,
			pinnedNixProfile: true);
		AssertLoadedProductBuildIdentityAuthority(
			"2.0.0.37647",
			"2.0.0.37647",
			$"2.0.0-20260812-{numericRevision}+{numericRevision}",
			numericRevision,
			pinnedNixProfile: true);
		Assert.Equal(
			"2.0.0.12",
			GetPinnedNixVersionForDotnet($"2.0.0-20260812-{filteredNumericRevision}"));
		Assert.Equal(
			"2.0.0.65534",
			GetPinnedNixVersionForDotnet($"2.0.0-20260812-{retainedBoundaryRevision}"));
		Assert.Equal(
			"2.0.0.12",
			GetPinnedNixVersionForDotnet($"2.0.0-20260812-{filteredBoundaryRevision}"));
		Assert.Equal(
			"2.0.0.1",
			GetPinnedNixVersionForDotnet($"2.0.0-20260812-{zeroPaddedRevision}"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertLoadedProductBuildIdentityAuthority(
				"1.2.3.4",
				"02.0.0.0",
				"2.0.0-beta",
				currentRevision,
				pinnedNixProfile: false));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertLoadedProductBuildIdentityAuthority(
				"9.9.9.9",
				"9.9.9.9",
				$"2.0.0-20260812-{currentRevision}",
				currentRevision,
				pinnedNixProfile: true));
		Assert.Equal(
			"prefix-{ASSEMBLY_VERSION}-suffix",
			ReplaceExactGeneratedAssemblyIdentity(
				"prefix-1.2.3.4-suffix",
				"1.2.3.4",
				"{ASSEMBLY_VERSION}"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			ReplaceExactGeneratedAssemblyIdentity(
				"prefix-suffix",
				"1.2.3.4",
				"{ASSEMBLY_VERSION}"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			ReplaceExactGeneratedAssemblyIdentity(
				"1.2.3.4-prefix-1.2.3.4",
				"1.2.3.4",
				"{ASSEMBLY_VERSION}"));
		foreach (string replacement in new[]
		{
			"{FILE_VERSION}",
			"{INFORMATIONAL_VERSION}",
			"{ASSEMBLY_VERSION}",
			"{COMMIT_HASH}",
		})
		{
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				ReplaceExactGeneratedAssemblyIdentity(
					$"prefix-{replacement}-1.2.3.4",
					"1.2.3.4",
					replacement));
		}
		Assert.True(IsValidGitReferenceName("refs/heads/release-é@candidate"));
		Assert.False(IsValidGitReferenceName("refs/heads/../escape"));
		Assert.False(IsValidGitReferenceName("refs/heads/control\u0001name"));

		string fixtureRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-restore-artifact-{Guid.NewGuid():N}");
		try
		{
			string firstRoot = Path.Combine(fixtureRoot, "first");
			string secondRoot = Path.Combine(fixtureRoot, "second");
			string firstRepository = Path.Combine(firstRoot, "repo");
			string secondRepository = Path.Combine(secondRoot, "repo");
			string firstDotnet = Path.Combine(firstRoot, "dotnet");
			string secondDotnet = Path.Combine(secondRoot, "dotnet");
			string firstPrimary = Path.Combine(firstRoot, "packages");
			string secondPrimary = Path.Combine(secondRoot, "packages");
			string secondFallback = Path.Combine(secondRoot, "fallback");
			Directory.CreateDirectory(firstRepository);
			Directory.CreateDirectory(secondRepository);
			Directory.CreateDirectory(firstDotnet);
			Directory.CreateDirectory(secondDotnet);
			Directory.CreateDirectory(firstPrimary);
			Directory.CreateDirectory(secondPrimary);
			Directory.CreateDirectory(secondFallback);
			string sourceLinkFixture = Path.Combine(firstRoot, "WalletWasabi.sourcelink.json");
			string sourcePattern = firstRepository.Replace('\\', '/').TrimEnd('/') + "/*";
			string sourceUri = $"https://raw.githubusercontent.com/Abdullah1738/WalletWasabi/{currentRevision}/*";
			File.WriteAllText(
				sourceLinkFixture,
				$"{{\"documents\":{{{JsonSerializer.Serialize(sourcePattern)}:{JsonSerializer.Serialize(sourceUri)}}}}}",
				Encoding.UTF8);
			string canonicalSourceLink = GetCompilerAuxiliaryInputAuthoritySha256(
				"/sourcelink:",
				sourceLinkFixture,
				firstRepository,
				firstRoot,
				currentRevision);
			Assert.False(string.IsNullOrWhiteSpace(canonicalSourceLink));
			File.WriteAllText(
				sourceLinkFixture,
				$"{{\"documents\":{{\"{{REPO}}/*\":{JsonSerializer.Serialize(sourceUri)}}}}}",
				Encoding.UTF8);
			bool reservedSourceLinkTokenRejected = false;
			try
			{
				_ = GetCompilerAuxiliaryInputAuthoritySha256(
					"/sourcelink:",
					sourceLinkFixture,
					firstRepository,
					firstRoot,
					currentRevision);
			}
			catch (Xunit.Sdk.XunitException)
			{
				reservedSourceLinkTokenRejected = true;
			}
			Assert.True(reservedSourceLinkTokenRejected, "A reserved SourceLink authority token was accepted as raw input.");
			File.WriteAllText(
				sourceLinkFixture,
				$"{{\"documents\":{{{JsonSerializer.Serialize(sourcePattern)}:" +
				$"{JsonSerializer.Serialize($"https://raw.githubusercontent.com/Abdullah1738/WalletWasabi/{{COMMIT_HASH}}/{currentRevision}/*")}}}}}",
				Encoding.UTF8);
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				GetCompilerAuxiliaryInputAuthoritySha256(
					"/sourcelink:",
					sourceLinkFixture,
					firstRepository,
					firstRoot,
					currentRevision));

			const string SystemEventsIdentity = "Microsoft.Win32.SystemEvents/10.0.2";
			const string SqliteIdentity = "SQLitePCLRaw.lib.e_sqlite3/2.1.11";
			const string LinuxNativeAsset = "runtimes/linux-x64/native/libe_sqlite3.so";
			string systemEventsBase =
				"{\"type\":\"package\",\"compile\":{},\"runtime\":{},\"build\":{},\"runtimeTargets\":{" +
				$"\"runtimes/win/lib/net10.0/Microsoft.Win32.SystemEvents.dll\":{{\"assetType\":\"runtime\",\"rid\":\"win\"}}}}}}";
			const string SystemEventsRid =
				"{\"type\":\"package\",\"compile\":{},\"runtime\":{},\"build\":{}}";
			string sqliteBase =
				"{\"type\":\"package\",\"compile\":{},\"runtime\":{},\"build\":{},\"runtimeTargets\":{" +
				$"{JsonSerializer.Serialize(LinuxNativeAsset)}:{{\"assetType\":\"native\",\"rid\":\"linux-x64\"}}}}}}";
			string sqliteRid =
				"{\"type\":\"package\",\"compile\":{},\"runtime\":{},\"native\":{" +
				$"{JsonSerializer.Serialize(LinuxNativeAsset)}:{{}}}},\"build\":{{}}}}";
			string baseRidTarget =
				$"{{{JsonSerializer.Serialize(SystemEventsIdentity)}:{systemEventsBase}," +
				$"{JsonSerializer.Serialize(SqliteIdentity)}:{sqliteBase}}}";
			string exactRidTarget =
				$"{{{JsonSerializer.Serialize(SystemEventsIdentity)}:{SystemEventsRid}," +
				$"{JsonSerializer.Serialize(SqliteIdentity)}:{sqliteRid}}}";
			const string ExactRidProject = "{\"runtimes\":{\"linux-x64\":{\"#import\":[]}}}";
			using (JsonDocument ridFixture = JsonDocument.Parse(
				$"{{\"base\":{baseRidTarget},\"rid\":{exactRidTarget},\"project\":{ExactRidProject}}}"))
			{
				AssertExactLinuxX64AssetsOverlay(
					ridFixture.RootElement.GetProperty("base"),
					ridFixture.RootElement.GetProperty("rid"),
					ridFixture.RootElement.GetProperty("project"));
			}
			string extraRidTarget = exactRidTarget[..^1] + ",\"Extra.Package/1.0.0\":{\"type\":\"package\"}}";
			bool extraRidIdentityRejected = false;
			try
			{
				using JsonDocument mutation = JsonDocument.Parse(
					$"{{\"base\":{baseRidTarget},\"rid\":{extraRidTarget},\"project\":{ExactRidProject}}}");
				AssertExactLinuxX64AssetsOverlay(
					mutation.RootElement.GetProperty("base"),
					mutation.RootElement.GetProperty("rid"),
					mutation.RootElement.GetProperty("project"));
			}
			catch (Xunit.Sdk.XunitException)
			{
				extraRidIdentityRejected = true;
			}
			Assert.True(extraRidIdentityRejected, "An extra linux-x64 target identity was accepted.");
			bool missingRidIdentityRejected = false;
			try
			{
				string missingRidTarget =
					$"{{{JsonSerializer.Serialize(SystemEventsIdentity)}:{SystemEventsRid}}}";
				using JsonDocument mutation = JsonDocument.Parse(
					$"{{\"base\":{baseRidTarget},\"rid\":{missingRidTarget},\"project\":{ExactRidProject}}}");
				AssertExactLinuxX64AssetsOverlay(
					mutation.RootElement.GetProperty("base"),
					mutation.RootElement.GetProperty("rid"),
					mutation.RootElement.GetProperty("project"));
			}
			catch (Xunit.Sdk.XunitException)
			{
				missingRidIdentityRejected = true;
			}
			Assert.True(missingRidIdentityRejected, "A missing linux-x64 target identity was accepted.");
			bool wrongRidRuntimeRejected = false;
			try
			{
				const string WrongRidProject = "{\"runtimes\":{\"linux-arm64\":{\"#import\":[]}}}";
				using JsonDocument mutation = JsonDocument.Parse(
					$"{{\"base\":{baseRidTarget},\"rid\":{exactRidTarget},\"project\":{WrongRidProject}}}");
				AssertExactLinuxX64AssetsOverlay(
					mutation.RootElement.GetProperty("base"),
					mutation.RootElement.GetProperty("rid"),
					mutation.RootElement.GetProperty("project"));
			}
			catch (Xunit.Sdk.XunitException)
			{
				wrongRidRuntimeRejected = true;
			}
			Assert.True(wrongRidRuntimeRejected, "An unapproved project runtime was accepted.");
			bool wrongNativeSelectionRejected = false;
			try
			{
				string wrongSqliteRid = sqliteRid.Replace(
					LinuxNativeAsset,
					"runtimes/linux-arm64/native/libe_sqlite3.so",
					StringComparison.Ordinal);
				string wrongRidTarget =
					$"{{{JsonSerializer.Serialize(SystemEventsIdentity)}:{SystemEventsRid}," +
					$"{JsonSerializer.Serialize(SqliteIdentity)}:{wrongSqliteRid}}}";
				using JsonDocument mutation = JsonDocument.Parse(
					$"{{\"base\":{baseRidTarget},\"rid\":{wrongRidTarget},\"project\":{ExactRidProject}}}");
				AssertExactLinuxX64AssetsOverlay(
					mutation.RootElement.GetProperty("base"),
					mutation.RootElement.GetProperty("rid"),
					mutation.RootElement.GetProperty("project"));
			}
			catch (Xunit.Sdk.XunitException)
			{
				wrongNativeSelectionRejected = true;
			}
			Assert.True(wrongNativeSelectionRejected, "An altered SQLite native RID selection was accepted.");

			string systemHash = CreateSemanticRestoreContentHash(9);
			string sqliteHash = CreateSemanticRestoreContentHash(10);
			string systemLock =
				$"{{\"type\":\"Direct\",\"requested\":\"[10.0.2, )\",\"resolved\":\"10.0.2\",\"contentHash\":{JsonSerializer.Serialize(systemHash)}}}";
			string sqliteLock =
				$"{{\"type\":\"Transitive\",\"resolved\":\"2.1.11\",\"contentHash\":{JsonSerializer.Serialize(sqliteHash)}}}";
			string lockBase =
				$"{JsonSerializer.Serialize("Microsoft.Win32.SystemEvents")}:{systemLock}," +
				$"{JsonSerializer.Serialize("SQLitePCLRaw.lib.e_sqlite3")}:{sqliteLock}";
			string ridLockFixture = Path.Combine(firstRoot, "rid-packages.lock.json");
			File.WriteAllText(
				ridLockFixture,
				$"{{\"version\":2,\"dependencies\":{{\"net10.0\":{{{lockBase}}}," +
				$"\"{LinuxX64TargetFramework}\":{{{lockBase}}}}}}}",
				Encoding.UTF8);
			(
				IReadOnlyDictionary<string, LockedPackageAuthority> ridLockAuthority,
				bool hasRidLockOverlay,
				bool ridLockHasContentHashes) =
				ReadLockedPackageAuthority(ridLockFixture, "net10.0");
			Assert.True(hasRidLockOverlay);
			Assert.True(ridLockHasContentHashes);
			Assert.Equal(2, ridLockAuthority.Count);
			string ridTransportWithOverlay =
				BuildPackageTransportAuthorityManifest(ridLockFixture, "net10.0");
			Assert.Contains("RID_OVERLAY|LINUX_X64_PRESENT\n", ridTransportWithOverlay, StringComparison.Ordinal);
			File.WriteAllText(
				ridLockFixture,
				$"{{\"version\":2,\"dependencies\":{{\"net10.0\":{{{lockBase}}}}}}}",
				Encoding.UTF8);
			string ridTransportWithoutOverlay =
				BuildPackageTransportAuthorityManifest(ridLockFixture, "net10.0");
			Assert.Contains("RID_OVERLAY|LINUX_X64_ABSENT\n", ridTransportWithoutOverlay, StringComparison.Ordinal);
			Assert.NotEqual(ridTransportWithOverlay, ridTransportWithoutOverlay);
			string systemLockWithoutHash =
				"{\"type\":\"Direct\",\"requested\":\"[10.0.2, )\",\"resolved\":\"10.0.2\"}";
			string sqliteLockWithoutHash =
				"{\"type\":\"Transitive\",\"resolved\":\"2.1.11\"}";
			string lockBaseWithoutHashes =
				$"{JsonSerializer.Serialize("Microsoft.Win32.SystemEvents")}:{systemLockWithoutHash}," +
				$"{JsonSerializer.Serialize("SQLitePCLRaw.lib.e_sqlite3")}:{sqliteLockWithoutHash}";
			File.WriteAllText(
				ridLockFixture,
				$"{{\"version\":2,\"dependencies\":{{\"net10.0\":{{{lockBaseWithoutHashes}}}," +
				$"\"{LinuxX64TargetFramework}\":{{{lockBaseWithoutHashes}}}}}}}",
				Encoding.UTF8);
			(
				IReadOnlyDictionary<string, LockedPackageAuthority> nixRidLockAuthority,
				bool hasNixRidLockOverlay,
				bool nixRidLockHasContentHashes) =
				ReadLockedPackageAuthority(ridLockFixture, "net10.0");
			Assert.True(hasNixRidLockOverlay);
			Assert.False(nixRidLockHasContentHashes);
			Assert.Equal(2, nixRidLockAuthority.Count);
			string mutatedSqliteLock = sqliteLock.Replace(
				JsonSerializer.Serialize(sqliteHash),
				JsonSerializer.Serialize(CreateSemanticRestoreContentHash(11)),
				StringComparison.Ordinal);
			bool mismatchedRidLockRejected = false;
			try
			{
				File.WriteAllText(
					ridLockFixture,
					$"{{\"version\":2,\"dependencies\":{{\"net10.0\":{{{lockBase}}}," +
					$"\"{LinuxX64TargetFramework}\":{{{JsonSerializer.Serialize("Microsoft.Win32.SystemEvents")}:{systemLock}," +
					$"{JsonSerializer.Serialize("SQLitePCLRaw.lib.e_sqlite3")}:{mutatedSqliteLock}}}}}}}",
					Encoding.UTF8);
				_ = ReadLockedPackageAuthority(ridLockFixture, "net10.0");
			}
			catch (Xunit.Sdk.XunitException)
			{
				mismatchedRidLockRejected = true;
			}
			Assert.True(mismatchedRidLockRejected, "A divergent linux-x64 lock overlay was accepted.");

			string detachedRepository = Path.Combine(fixtureRoot, "git-detached");
			string detachedGitDirectory = Path.Combine(detachedRepository, ".git");
			Directory.CreateDirectory(detachedGitDirectory);
			File.WriteAllText(Path.Combine(detachedGitDirectory, "HEAD"), currentRevision, Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(detachedRepository));

			string looseRepository = Path.Combine(fixtureRoot, "git-loose");
			string looseGitDirectory = Path.Combine(looseRepository, ".git");
			string looseReference = "refs/heads/release-é@candidate";
			Directory.CreateDirectory(Path.Combine(looseGitDirectory, "refs/heads"));
			File.WriteAllText(Path.Combine(looseGitDirectory, "HEAD"), $"ref: {looseReference}\n", Encoding.UTF8);
			File.WriteAllText(Path.Combine(looseGitDirectory, looseReference), currentRevision, Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(looseRepository));

			string packedRepository = Path.Combine(fixtureRoot, "git-packed");
			string packedGitDirectory = Path.Combine(packedRepository, ".git");
			string packedReference = "refs/heads/packed@candidate";
			Directory.CreateDirectory(packedGitDirectory);
			File.WriteAllText(Path.Combine(packedGitDirectory, "HEAD"), $"ref: {packedReference}\n", Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(packedGitDirectory, "packed-refs"),
				$"# pack-refs with: peeled fully-peeled sorted\n{currentRevision} {packedReference}\n",
				Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(packedRepository));

			string linkedRepository = Path.Combine(fixtureRoot, "git-linked");
			string commonGitDirectory = Path.Combine(fixtureRoot, "git-common");
			string linkedGitDirectory = Path.Combine(commonGitDirectory, "worktrees/linked");
			string linkedReference = "refs/heads/linked-é@candidate";
			Directory.CreateDirectory(linkedRepository);
			Directory.CreateDirectory(Path.Combine(commonGitDirectory, "refs/heads"));
			Directory.CreateDirectory(linkedGitDirectory);
			File.WriteAllText(
				Path.Combine(linkedRepository, ".git"),
				$"gitdir: {linkedGitDirectory}\n",
				Encoding.UTF8);
			File.WriteAllText(Path.Combine(linkedGitDirectory, "commondir"), "../..\n", Encoding.UTF8);
			File.WriteAllText(Path.Combine(linkedGitDirectory, "HEAD"), $"ref: {linkedReference}\n", Encoding.UTF8);
			File.WriteAllText(Path.Combine(commonGitDirectory, linkedReference), currentRevision, Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(linkedRepository));

			string firstImport = CreateSemanticRestorePackageImport(firstPrimary, [1, 2, 3, 4]);
			string secondImport = CreateSemanticRestorePackageImport(secondFallback, [1, 2, 3, 4]);
			string expectedContentHash = CreateSemanticRestoreContentHash(7);
			string firstAssets = WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			string secondAssets = WriteSemanticRestoreFixture(
				secondRepository,
				secondDotnet,
				secondPrimary,
				[secondPrimary, secondFallback],
				secondImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3",
				usePinnedNixFallbackProfile: true);
			string firstLock = Path.Combine(firstRepository, "WalletWasabi/packages.lock.json");
			string secondLock = Path.Combine(secondRepository, "WalletWasabi/packages.lock.json");
			(string PrimaryRoot, string[] OrderedRoots) firstAuthority = GetPinnedPackageAuthority(firstAssets);
			(string PrimaryRoot, string[] OrderedRoots) secondAuthority = GetPinnedPackageAuthority(secondAssets);
			string firstManifest = BuildSemanticRestoreFixtureManifest(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			string secondManifest = BuildSemanticRestoreFixtureManifest(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			Assert.Equal(firstManifest, secondManifest);
			string canonicalFirstAssets = File.ReadAllText(firstAssets);
			string canonicalSecondAssets = File.ReadAllText(secondAssets);
			const string CanonicalFixtureProjectVersion = "\"project\":{\"version\":\"1.0.0\"";
			string changedNonNixProjectVersion = canonicalFirstAssets.Replace(
				CanonicalFixtureProjectVersion,
				"\"project\":{\"version\":\"1.0.1\"",
				StringComparison.Ordinal);
			Assert.NotEqual(canonicalFirstAssets, changedNonNixProjectVersion);
			File.WriteAllText(firstAssets, changedNonNixProjectVersion, Encoding.UTF8);
			Assert.NotEqual(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					firstAuthority));
			File.WriteAllText(firstAssets, canonicalFirstAssets, Encoding.UTF8);

			string currentPinnedNixProjectVersion = $"2.0.0-20260812-{currentRevision}";
			string otherPinnedNixProjectVersion = $"2.0.0-20260812-{otherRevision}";
			string currentPinnedNixAssets = canonicalSecondAssets.Replace(
				CanonicalFixtureProjectVersion,
				"\"project\":{\"version\":" + JsonSerializer.Serialize(currentPinnedNixProjectVersion),
				StringComparison.Ordinal);
			string otherPinnedNixAssets = canonicalSecondAssets.Replace(
				CanonicalFixtureProjectVersion,
				"\"project\":{\"version\":" + JsonSerializer.Serialize(otherPinnedNixProjectVersion),
				StringComparison.Ordinal);
			Assert.NotEqual(canonicalSecondAssets, currentPinnedNixAssets);
			Assert.NotEqual(currentPinnedNixAssets, otherPinnedNixAssets);
			File.WriteAllText(secondAssets, currentPinnedNixAssets, Encoding.UTF8);
			string currentPinnedNixManifest = BuildSemanticRestoreFixtureManifest(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority,
				currentPinnedNixProjectVersion);
			File.WriteAllText(secondAssets, otherPinnedNixAssets, Encoding.UTF8);
			Assert.Equal(
				currentPinnedNixManifest,
				BuildSemanticRestoreFixtureManifest(
					secondAssets,
					secondRepository,
					secondDotnet,
					secondAuthority,
					otherPinnedNixProjectVersion));
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority,
				currentPinnedNixProjectVersion);
			string invalidPinnedNixProjectVersionType = currentPinnedNixAssets.Replace(
				JsonSerializer.Serialize(currentPinnedNixProjectVersion),
				"7",
				StringComparison.Ordinal);
			Assert.NotEqual(currentPinnedNixAssets, invalidPinnedNixProjectVersionType);
			File.WriteAllText(secondAssets, invalidPinnedNixProjectVersionType, Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority,
				currentPinnedNixProjectVersion);
			string injectedMarkerAssets = canonicalFirstAssets.Replace(
				CanonicalFixtureProjectVersion,
				"\"project\":{\"version\":\"{VALIDATED_PINNED_NIX_PROJECT_VERSION}\"",
				StringComparison.Ordinal);
			File.WriteAllText(firstAssets, injectedMarkerAssets, Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			File.WriteAllText(firstAssets, canonicalFirstAssets, Encoding.UTF8);
			File.WriteAllText(secondAssets, canonicalSecondAssets, Encoding.UTF8);
			string secondOfflineSource = Path.Combine(secondRoot, "source");
			string secondLibraryPacksSource = Path.Combine(secondDotnet, "library-packs");
			string serializedOfflineSource = JsonSerializer.Serialize(secondOfflineSource);
			string serializedLibraryPacksSource = JsonSerializer.Serialize(secondLibraryPacksSource);
			string offlineSourceEntry = $"{serializedOfflineSource}:{{}}";
			string libraryPacksEntry = $"{serializedLibraryPacksSource}:{{}}";
			string canonicalPinnedNixSources =
				$"\"sources\":{{{offlineSourceEntry},{libraryPacksEntry}}}";
			Assert.Contains(canonicalPinnedNixSources, canonicalSecondAssets, StringComparison.Ordinal);
			string extraRestoreSource = Path.Combine(secondRoot, "unexpected-source");
			string[] rejectedPinnedNixSources =
			[
				$"\"sources\":{{{offlineSourceEntry}}}",
				$"\"sources\":{{{libraryPacksEntry}}}",
				$"\"sources\":{{{libraryPacksEntry},{offlineSourceEntry}}}",
				$"\"sources\":{{{offlineSourceEntry},{libraryPacksEntry}," +
					$"{JsonSerializer.Serialize(extraRestoreSource)}:{{}}}}",
				$"\"sources\":{{{offlineSourceEntry}," +
					$"{JsonSerializer.Serialize(Path.Combine(secondDotnet, "Library-Packs"))}:{{}}}}",
				$"\"sources\":{{{offlineSourceEntry}," +
					$"{JsonSerializer.Serialize(secondLibraryPacksSource + Path.DirectorySeparatorChar)}:{{}}}}",
				$"\"sources\":{{{offlineSourceEntry}," +
					$"{JsonSerializer.Serialize(Path.Combine(secondDotnet, "sdk", "..", "library-packs"))}:{{}}}}",
				$"\"sources\":{{{offlineSourceEntry}," +
					$"{JsonSerializer.Serialize(Path.Combine(secondDotnet, "library-packs-copy"))}:{{}}}}",
				$"\"sources\":{{" +
					$"{JsonSerializer.Serialize(Path.Combine(secondOfflineSource, "..", Path.GetFileName(secondOfflineSource)))}:{{}}," +
					$"{libraryPacksEntry}}}",
				$"\"sources\":{{{offlineSourceEntry},{serializedLibraryPacksSource}:{{\"unexpected\":true}}}}",
				$"\"sources\":{{{offlineSourceEntry},{offlineSourceEntry},{libraryPacksEntry}}}",
				$"\"sources\":{{" +
					$"{JsonSerializer.Serialize("https://api.nuget.org/v3/index.json")}:{{}},{libraryPacksEntry}}}",
				$"\"sources\":{{{JsonSerializer.Serialize("https://api.nuget.org/v3/index.json")}:{{}}}}",
			];
			foreach (string rejectedSources in rejectedPinnedNixSources)
			{
				AssertPinnedNixRestoreSourcesRejected(
					secondAssets,
					canonicalSecondAssets,
					canonicalPinnedNixSources,
					rejectedSources,
					secondRepository,
					secondDotnet,
					secondAuthority);
			}
			File.WriteAllText(secondAssets, canonicalSecondAssets, Encoding.UTF8);
			const string NuGetSourceEntry = "\"https://api.nuget.org/v3/index.json\":{}";
			Assert.Contains(NuGetSourceEntry, canonicalFirstAssets, StringComparison.Ordinal);
			File.WriteAllText(
				firstAssets,
				canonicalFirstAssets.Replace(
					NuGetSourceEntry,
					$"{NuGetSourceEntry},{JsonSerializer.Serialize(Path.Combine(firstDotnet, "library-packs"))}:{{}}",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			File.WriteAllText(
				firstAssets,
				canonicalFirstAssets.Replace(
					$"\"sources\":{{{NuGetSourceEntry}}}",
					$"\"sources\":{{{JsonSerializer.Serialize(Path.Combine(firstRoot, "source"))}:{{}}," +
					$"{JsonSerializer.Serialize(Path.Combine(firstDotnet, "library-packs"))}:{{}}}}",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			File.WriteAllText(firstAssets, canonicalFirstAssets, Encoding.UTF8);
			string firstTransportManifest = BuildPackageTransportAuthorityManifest(firstLock, "net10.0");
			string secondTransportManifest = BuildPackageTransportAuthorityManifest(secondLock, "net10.0");
			Assert.Contains("PROFILE|CONTENT_HASHES_PRESENT\n", firstTransportManifest, StringComparison.Ordinal);
			Assert.Contains("PROFILE|CONTENT_HASHES_ABSENT_PINNED_NIX\n", secondTransportManifest, StringComparison.Ordinal);
			Assert.NotEqual(firstTransportManifest, secondTransportManifest);
			File.WriteAllText(
				secondLock,
				File.ReadAllText(secondLock).Replace(
					"\"resolved\":\"1.2.3\"",
					$"\"resolved\":\"1.2.3\",\"contentHash\":{JsonSerializer.Serialize(expectedContentHash)}",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			WriteSemanticPackagesLockFixture(
				secondLock,
				"1.2.3",
				expectedContentHash,
				omitContentHashes: true);
			string firstPayloadManifest = BuildPackagePayloadAuthorityManifest(firstAssets, firstAuthority);
			string secondPayloadManifest = BuildPackagePayloadAuthorityManifest(secondAssets, secondAuthority);
			Assert.Equal(firstPayloadManifest, secondPayloadManifest);
			string firstMaterializationManifest =
				BuildPackageMaterializationAuthorityManifest(firstAssets, firstAuthority);
			string secondMaterializationManifest =
				BuildPackageMaterializationAuthorityManifest(secondAssets, secondAuthority);
			Assert.NotEqual(firstMaterializationManifest, secondMaterializationManifest);
			Assert.Contains(
				JsonSerializer.Serialize("[Content_Types].xml"),
				secondMaterializationManifest,
				StringComparison.Ordinal);
			const string NativePayload = "runtimes/linux-x64/native/libexample.so";
			string firstNativePayload = Path.Combine(
				firstPrimary,
				"example.package/1.2.3",
				NativePayload.Replace('/', Path.DirectorySeparatorChar));
			byte[] firstNativeBytes = File.ReadAllBytes(firstNativePayload);
			firstNativeBytes[0] ^= 1;
			File.WriteAllBytes(firstNativePayload, firstNativeBytes);
			Assert.NotEqual(
				firstPayloadManifest,
				BuildPackagePayloadAuthorityManifest(firstAssets, firstAuthority));
			firstNativeBytes[0] ^= 1;
			File.WriteAllBytes(firstNativePayload, firstNativeBytes);
			string secondNativePayload = Path.Combine(
				secondFallback,
				"example.package/1.2.3",
				NativePayload.Replace('/', Path.DirectorySeparatorChar));
			byte[] secondNativeBytes = File.ReadAllBytes(secondNativePayload);
			secondNativeBytes[^1] ^= 1;
			File.WriteAllBytes(secondNativePayload, secondNativeBytes);
			Assert.NotEqual(
				secondPayloadManifest,
				BuildPackagePayloadAuthorityManifest(secondAssets, secondAuthority));
			secondNativeBytes[^1] ^= 1;
			File.WriteAllBytes(secondNativePayload, secondNativeBytes);
			string secondContentTypesPath = Path.Combine(
				secondFallback,
				"example.package/1.2.3/[Content_Types].xml");
			byte[] secondContentTypesBytes = File.ReadAllBytes(secondContentTypesPath);
			secondContentTypesBytes[0] ^= 1;
			File.WriteAllBytes(secondContentTypesPath, secondContentTypesBytes);
			Assert.NotEqual(
				secondMaterializationManifest,
				BuildPackageMaterializationAuthorityManifest(secondAssets, secondAuthority));
			secondContentTypesBytes[0] ^= 1;
			File.WriteAllBytes(secondContentTypesPath, secondContentTypesBytes);
			string unexpectedEmptyDirectory = Path.Combine(
				secondFallback,
				"example.package/1.2.3/unexpected-empty-directory");
			Directory.CreateDirectory(unexpectedEmptyDirectory);
			Assert.NotEqual(
				secondMaterializationManifest,
				BuildPackageMaterializationAuthorityManifest(secondAssets, secondAuthority));
			Directory.Delete(unexpectedEmptyDirectory);
			string unexpectedNixPackageFile = Path.Combine(
				secondFallback,
				"example.package/1.2.3/unexpected.bin");
			File.WriteAllBytes(unexpectedNixPackageFile, [1]);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			File.Delete(unexpectedNixPackageFile);
			File.Delete(secondContentTypesPath);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			File.WriteAllBytes(secondContentTypesPath, secondContentTypesBytes);
			string secondRelationshipsPath = Path.Combine(
				secondFallback,
				"example.package/1.2.3/_rels/.rels");
			byte[] secondRelationshipsBytes = File.ReadAllBytes(secondRelationshipsPath);
			File.Delete(secondRelationshipsPath);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			File.WriteAllBytes(secondRelationshipsPath, secondRelationshipsBytes);
			string secondCorePropertiesDirectory = Path.Combine(
				secondFallback,
				"example.package/1.2.3/package/services/metadata/core-properties");
			string secondCorePropertiesPath = Path.Combine(
				secondCorePropertiesDirectory,
				"fedcba9876543210fedcba9876543210.psmdcp");
			File.WriteAllBytes(secondCorePropertiesPath, [1]);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			File.Delete(secondCorePropertiesPath);
			string invalidCorePropertiesPath = Path.Combine(
				secondCorePropertiesDirectory,
				"not-canonical.psmdcp");
			File.WriteAllBytes(invalidCorePropertiesPath, [1]);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			File.Delete(invalidCorePropertiesPath);
			string aliasedContentTypesPath = Path.Combine(
				secondFallback,
				"example.package/1.2.3/[content_types].xml");
			File.Delete(secondContentTypesPath);
			File.WriteAllBytes(aliasedContentTypesPath, [1]);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			File.Delete(aliasedContentTypesPath);
			File.WriteAllBytes(secondContentTypesPath, secondContentTypesBytes);
			string secondPackageIdDirectory = Path.Combine(secondFallback, "example.package");
			string aliasedPackageIdDirectory = Path.Combine(secondFallback, "Example.Package");
			string packageIdRenameDirectory = Path.Combine(secondFallback, "package-id-rename-tmp");
			Directory.Move(secondPackageIdDirectory, packageIdRenameDirectory);
			Directory.Move(packageIdRenameDirectory, aliasedPackageIdDirectory);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			Directory.Move(aliasedPackageIdDirectory, packageIdRenameDirectory);
			Directory.Move(packageIdRenameDirectory, secondPackageIdDirectory);
			File.WriteAllText(
				secondAssets,
				File.ReadAllText(secondAssets).Replace(
					"\"enableAudit\":\"true\"",
					"\"enableAudit\":\"false\"",
					StringComparison.Ordinal),
				Encoding.UTF8);
			Assert.Equal(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					secondAssets,
					secondRepository,
					secondDotnet,
					secondAuthority));

			File.WriteAllText(
				firstAssets,
				File.ReadAllText(firstAssets).Replace(".signature.p7s", ".SIGNATURE.P7S", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				firstAssets,
				File.ReadAllText(firstAssets).Replace(
					"\".signature.p7s\"",
					"\".signature.p7s\",\".nix-patched\"",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				secondAssets,
				File.ReadAllText(secondAssets).Replace(".nix-patched", ".NIX-PATCHED", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			WriteSemanticRestoreFixture(
				secondRepository,
				secondDotnet,
				secondPrimary,
				[secondPrimary, secondFallback],
				secondImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3",
				usePinnedNixFallbackProfile: true);
			string firstPackageDirectory = Path.Combine(firstPrimary, "example.package/1.2.3");
			File.Delete(Path.Combine(firstPackageDirectory, ".signature.p7s"));
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.Delete(Path.Combine(firstPackageDirectory, "lib/net10.0/Example.Package.dll"));
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			string secondPackageDirectory = Path.Combine(secondFallback, "example.package/1.2.3");
			File.WriteAllBytes(
				Path.Combine(secondPackageDirectory, ".nupkg.metadata"),
				Encoding.ASCII.GetBytes("{}"));
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			WriteSemanticRestoreFixture(
				secondRepository,
				secondDotnet,
				secondPrimary,
				[secondPrimary, secondFallback],
				secondImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3",
				usePinnedNixFallbackProfile: true);
			File.Delete(Path.Combine(secondPackageDirectory, ".nix-patched"));
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			WriteSemanticRestoreFixture(
				secondRepository,
				secondDotnet,
				secondPrimary,
				[secondPrimary, secondFallback],
				secondImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3",
				usePinnedNixFallbackProfile: true);

			string normalHashProperty = $"\"sha512\":{JsonSerializer.Serialize(expectedContentHash)},";
			string normalAssetsText = File.ReadAllText(firstAssets);
			Assert.Contains(normalHashProperty, normalAssetsText, StringComparison.Ordinal);
			File.WriteAllText(
				firstAssets,
				normalAssetsText.Replace(normalHashProperty, "", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				CreateSemanticRestoreContentHash(8));
			Assert.NotEqual(
				firstTransportManifest,
				BuildPackageTransportAuthorityManifest(firstLock, "net10.0"));
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				expectedContentHash,
				omitContentHashes: true);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				firstAssets,
				File.ReadAllText(firstAssets).Replace(
					expectedContentHash,
					CreateSemanticRestoreContentHash(8),
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				firstLock,
				File.ReadAllText(firstLock).Replace(
					JsonSerializer.Serialize(expectedContentHash),
					JsonSerializer.Serialize("not-base64"),
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				firstLock,
				File.ReadAllText(firstLock).Replace(
					JsonSerializer.Serialize(expectedContentHash),
					"42",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				expectedContentHash,
				additionalPackageId: "example.package");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				expectedContentHash,
				additionalPackageId: "Extra.Package",
				omitAdditionalPackageContentHash: true);
			bool mixedContentHashProfileRejected = false;
			try
			{
				_ = ReadLockedPackageAuthority(firstLock, "net10.0");
			}
			catch (Xunit.Sdk.XunitException)
			{
				mixedContentHashProfileRejected = true;
			}
			Assert.True(mixedContentHashProfileRejected, "A mixed lock content-hash profile was accepted.");

			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				expectedContentHash,
				additionalPackageId: "Extra.Package");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				expectedContentHash,
				dependencyId: "Missing.Package");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				expectedContentHash,
				dependencyId: "Example.Package",
				dependencyMinimumVersion: "9.0.0");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3",
				expectedContentHash,
				dependencyId: "Example.Package",
				dependencyAliasId: "example.package");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticPackagesLockFixture(firstLock, "1.2.3", expectedContentHash);
			File.WriteAllText(
				firstLock,
				File.ReadAllText(firstLock).Replace("[1.2.3, )", "[1.2.4, )", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticPackagesLockFixture(firstLock, "1.2.3", expectedContentHash);
			File.WriteAllText(
				firstLock,
				File.ReadAllText(firstLock).Replace("[1.2.3, )", "not-a-range", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			string prereleaseImport = CreateSemanticRestorePackageImport(
				firstPrimary,
				Convert.FromHexString("01020304"),
				dependencyVersion: "1.2.3-alpha");
			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				prereleaseImport,
				"1.2.3-alpha",
				expectedContentHash,
				"example.package/1.2.3-alpha");
			File.WriteAllText(
				firstLock,
				File.ReadAllText(firstLock).Replace(
					"[1.2.3-alpha, )",
					"[1.2.3, )",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				prereleaseImport,
				"1.2.3-alpha",
				expectedContentHash,
				"example.package/1.2.3-alpha");
			WriteSemanticPackagesLockFixture(
				firstLock,
				"1.2.3-alpha",
				expectedContentHash,
				dependencyId: "Example.Package",
				dependencyMinimumVersion: "1.2.3-beta");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticPackagesLockFixture(firstLock, "1.2.3", expectedContentHash);
			normalAssetsText = File.ReadAllText(firstAssets);
			int librariesStart = normalAssetsText.IndexOf("\"libraries\":{", StringComparison.Ordinal) +
				"\"libraries\":{".Length;
			int librariesEnd = normalAssetsText.IndexOf(
				"},\"projectFileDependencyGroups\"",
				librariesStart,
				StringComparison.Ordinal);
			Assert.True(librariesStart >= "\"libraries\":{".Length && librariesEnd > librariesStart);
			File.WriteAllText(
				firstAssets,
				normalAssetsText[..librariesStart] + normalAssetsText[librariesEnd..],
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				firstAssets,
				File.ReadAllText(firstAssets).Replace("\"type\":\"package\"", "\"type\":42", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				firstLock,
				File.ReadAllText(firstLock).Replace("\"resolved\":\"1.2.3\"", "\"resolved\":\"01.2.3\"", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");
			File.WriteAllText(
				firstLock,
				File.ReadAllText(firstLock).Replace("\"version\":2,", "\"version\":2,\"version\":2,", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				expectedContentHash,
				"example.package/1.2.3");

			string secondFallbackProperty =
				$"\"fallbackFolders\":[{JsonSerializer.Serialize(secondFallback)}],";
			string secondAssetsText = File.ReadAllText(secondAssets);
			Assert.Contains(secondFallbackProperty, secondAssetsText, StringComparison.Ordinal);
			File.WriteAllText(
				secondAssets,
				secondAssetsText.Replace(secondFallbackProperty, "", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);

			const string RestoreSourceProperty =
				"\"sources\":{\"https://api.nuget.org/v3/index.json\":{}},";
			string firstAssetsText = File.ReadAllText(firstAssets);
			Assert.Contains(RestoreSourceProperty, firstAssetsText, StringComparison.Ordinal);
			File.WriteAllText(
				firstAssets,
				firstAssetsText.Replace(
					RestoreSourceProperty,
					RestoreSourceProperty + "\"fallbackFolders\":[],",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			string updatedVersionImport = CreateSemanticRestorePackageImport(
				firstPrimary,
				Convert.FromHexString("01020304"),
				dependencyVersion: "1.2.4");
			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				updatedVersionImport,
				"1.2.4",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.4");
			Assert.NotEqual(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					GetPinnedPackageAuthority(firstAssets)));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			File.WriteAllText(
				firstAssets,
				File.ReadAllText(firstAssets).Replace(
					"https://api.nuget.org/v3/index.json",
					"https://unapproved.invalid/v3/index.json",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));
			string generatedProps =
				Path.Combine(firstRepository, "WalletWasabi/obj/WalletWasabi.csproj.nuget.g.props");
			string sourceRootDeclaration =
				$"<SourceRoot Include=\"{System.Security.SecurityElement.Escape(firstPrimary + Path.DirectorySeparatorChar)}\" />";
			string generatedPropsText;

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			File.WriteAllText(
				generatedProps,
				generatedPropsText.Replace(
					sourceRootDeclaration,
					$"<SourceRoot Include=\"{System.Security.SecurityElement.Escape(firstPrimary)}\" />",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			File.WriteAllText(
				generatedProps,
				generatedPropsText.Replace(
					sourceRootDeclaration,
					$"<SourceRoot Include=\"{System.Security.SecurityElement.Escape(firstPrimary + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar)}\" />",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			Assert.Contains(sourceRootDeclaration, generatedPropsText, StringComparison.Ordinal);
			File.WriteAllText(
				generatedProps,
				generatedPropsText.Replace(sourceRootDeclaration, "", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			Assert.Contains("NuGetPackageRoot", generatedPropsText, StringComparison.Ordinal);
			File.WriteAllText(
				generatedProps,
				generatedPropsText
					.Replace(
						"<NuGetPackageRoot>",
						"<VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY>",
						StringComparison.Ordinal)
					.Replace(
						"</NuGetPackageRoot>",
						"</VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY>",
						StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(8),
				"example.package/1.2.3");
			Assert.Equal(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					GetPinnedPackageAuthority(firstAssets)));
			Assert.NotEqual(
				firstTransportManifest,
				BuildPackageTransportAuthorityManifest(firstLock, "net10.0"));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"../example.package/1.2.3");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			string alternateImport = CreateSemanticRestorePackageImport(
				firstPrimary,
				[1, 2, 3, 4],
				"alternate.props");
			WriteSemanticNuGetPropsFixture(
				Path.Combine(firstRepository, "WalletWasabi/obj/WalletWasabi.csproj.nuget.g.props"),
				[firstPrimary],
				alternateImport);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));
			File.Delete(alternateImport);

			WriteSemanticNuGetPropsFixture(
				Path.Combine(firstRepository, "WalletWasabi/obj/WalletWasabi.csproj.nuget.g.props"),
				[firstPrimary],
				firstImport);
			File.WriteAllBytes(firstImport, [1, 2, 3, 5]);
			Assert.NotEqual(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					GetPinnedPackageAuthority(firstAssets)));
		}
		finally
		{
			if (Directory.Exists(fixtureRoot))
			{
				Directory.Delete(fixtureRoot, recursive: true);
			}
		}
	}

	[Fact]
	public void EncoderDefenseInDepthRechecksEveryMutableAcceptedTypeState()
	{
		ElementsPublicNetworkManifest testnet = ElementsPublicNetworkManifest.LiquidTestnet;
		ElementsPublicNetworkManifest mainnet = ElementsPublicNetworkManifest.LiquidMainnet;

		LiquidOrdinaryWalletExactSpendPlan contextPlan = CreateSingleAssetPlan(testnet, 601);
		using LiquidOrdinaryWalletPlanFundingRow contextRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch contextBatch = CreateBatch(contextPlan, contextRow);
		SetField(contextPlan, "_destinationNetworkManifestId", new string('0', 64));
		AssertFixedInvariant(contextPlan, contextBatch);

		LiquidOrdinaryWalletExactSpendPlan countPlan = CreateSingleAssetPlan(testnet, 602);
		using LiquidOrdinaryWalletPlanFundingRow countRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch countBatch = CreateBatch(countPlan, countRow);
		SetField(countPlan, "_destinations", Array.Empty<LiquidSuppliedConfidentialDestination>());
		AssertFixedInvariant(countPlan, countBatch);

		PlanFixture orderFixture = CreateTwoAssetPlan(testnet);
		using LiquidOrdinaryWalletPlanFundingRow firstOrderRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow secondOrderRow = CreateRow([2]);
		using LiquidOrdinaryWalletPlanFundingBatch orderBatch = CreateBatch(
			orderFixture.Plan,
			firstOrderRow,
			secondOrderRow);
		LiquidWalletCoinControlEntry[] selected =
			GetField<LiquidWalletCoinControlEntry[]>(orderFixture.Plan, "_selectedEntries");
		selected[1] = selected[0];
		AssertFixedInvariant(orderFixture.Plan, orderBatch);

		LiquidOrdinaryWalletExactSpendPlan destinationPlan = CreateSingleAssetPlan(testnet, 603);
		using LiquidOrdinaryWalletPlanFundingRow destinationRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch destinationBatch = CreateBatch(
			destinationPlan,
			destinationRow);
		LiquidSuppliedConfidentialDestination[] destinations =
			GetField<LiquidSuppliedConfidentialDestination[]>(destinationPlan, "_destinations");
		LiquidAssetId mainnetPegged = LiquidAssetId.ParseRpcHex(mainnet.PeggedAssetId);
		destinations[0] = Destination(mainnet, FirstScriptHex, mainnetPegged, 9);
		AssertFixedInvariant(destinationPlan, destinationBatch);

		LiquidOrdinaryWalletExactSpendPlan conservationPlan = CreateSingleAssetPlan(testnet, 604);
		using LiquidOrdinaryWalletPlanFundingRow conservationRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch conservationBatch = CreateBatch(
			conservationPlan,
			conservationRow);
		LiquidAssetId testnetPegged = LiquidAssetId.ParseRpcHex(testnet.PeggedAssetId);
		GetField<LiquidSuppliedConfidentialDestination[]>(conservationPlan, "_destinations")[0] =
			Destination(testnet, FirstScriptHex, testnetPegged, 8);
		AssertFixedInvariant(conservationPlan, conservationBatch);

		LiquidOrdinaryWalletExactSpendPlan feePlan = CreateSingleAssetPlan(testnet, 605);
		using LiquidOrdinaryWalletPlanFundingRow feeRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch feeBatch = CreateBatch(feePlan, feeRow);
		SetField(feePlan, "_explicitFee", LiquidAssetAmount.Zero(testnetPegged, testnetPegged));
		AssertFixedInvariant(feePlan, feeBatch);

		LiquidOrdinaryWalletExactSpendPlan candidatePlan = CreateSingleAssetPlan(testnet, 606);
		using LiquidOrdinaryWalletPlanFundingRow candidateRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch candidateBatch = CreateBatch(candidatePlan, candidateRow);
		LiquidOrdinaryWalletPlanFundingRow ownedCandidateRow =
			GetField<LiquidOrdinaryWalletPlanFundingRow[]>(candidateBatch, "_rows")[0];
		SetField(ownedCandidateRow, "_candidateTransaction", Array.Empty<byte>());
		AssertFixedInvariant(candidatePlan, candidateBatch);

		LiquidOrdinaryWalletExactSpendPlan previousPlan = CreateSingleAssetPlan(testnet, 607);
		using LiquidOrdinaryWalletPlanFundingRow previousRow = CreateRow([1], [1], [2]);
		using LiquidOrdinaryWalletPlanFundingBatch previousBatch = CreateBatch(previousPlan, previousRow);
		LiquidOrdinaryWalletPlanFundingRow ownedPreviousRow =
			GetField<LiquidOrdinaryWalletPlanFundingRow[]>(previousBatch, "_rows")[0];
		byte[][] previous = GetField<byte[][]>(ownedPreviousRow, "_previousTransactions");
		(previous[0], previous[1]) = (previous[1], previous[0]);
		AssertFixedInvariant(previousPlan, previousBatch);

		LiquidOrdinaryWalletExactSpendPlan malformedAddressPlan = CreateSingleAssetPlan(testnet, 608);
		using LiquidOrdinaryWalletPlanFundingRow malformedAddressRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch malformedAddressBatch = CreateBatch(
			malformedAddressPlan,
			malformedAddressRow);
		LiquidAddress malformedAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(malformedAddressPlan, "_destinations")[0]
				.GetAddress();
		SetField(malformedAddress, "_canonicalAddressText", "malformed-address");
		AssertFixedInvariant(malformedAddressPlan, malformedAddressBatch);

		LiquidOrdinaryWalletExactSpendPlan noncanonicalAddressPlan = CreateSingleAssetPlan(testnet, 609);
		using LiquidOrdinaryWalletPlanFundingRow noncanonicalAddressRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch noncanonicalAddressBatch = CreateBatch(
			noncanonicalAddressPlan,
			noncanonicalAddressRow);
		LiquidAddress noncanonicalAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(noncanonicalAddressPlan, "_destinations")[0]
				.GetAddress();
		SetField(
			noncanonicalAddress,
			"_canonicalAddressText",
			noncanonicalAddress.GetCanonicalAddressText().ToUpperInvariant());
		AssertFixedInvariant(noncanonicalAddressPlan, noncanonicalAddressBatch);

		LiquidOrdinaryWalletExactSpendPlan scriptPlan = CreateSingleAssetPlan(testnet, 610);
		using LiquidOrdinaryWalletPlanFundingRow scriptRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch scriptBatch = CreateBatch(scriptPlan, scriptRow);
		LiquidAddress scriptAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(scriptPlan, "_destinations")[0]
				.GetAddress();
		SetField(scriptAddress, "_scriptPubKey", Convert.FromHexString(SecondScriptHex));
		AssertFixedInvariant(scriptPlan, scriptBatch);

		LiquidOrdinaryWalletExactSpendPlan blindingPlan = CreateSingleAssetPlan(testnet, 611);
		using LiquidOrdinaryWalletPlanFundingRow blindingRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch blindingBatch = CreateBatch(blindingPlan, blindingRow);
		LiquidAddress blindingAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(blindingPlan, "_destinations")[0]
				.GetAddress();
		SetField(
			blindingAddress,
			"_blindingPublicKey",
			LiquidBlindingPublicKey.Create(Convert.FromHexString(
				"0379be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798")));
		AssertFixedInvariant(blindingPlan, blindingBatch);

		LiquidOrdinaryWalletExactSpendPlan nonhexTransactionPlan = CreateSingleAssetPlan(testnet, 612);
		using LiquidOrdinaryWalletPlanFundingRow nonhexTransactionRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch nonhexTransactionBatch = CreateBatch(
			nonhexTransactionPlan,
			nonhexTransactionRow);
		LiquidTransactionId nonhexTransactionId = GetField<LiquidWalletCoinControlEntry[]>(
			nonhexTransactionPlan,
			"_selectedEntries")[0].OutPoint.TransactionId;
		SetField(nonhexTransactionId, "<CanonicalRpcHex>k__BackingField", new string('g', 64));
		AssertFixedInvariant(nonhexTransactionPlan, nonhexTransactionBatch);

		LiquidOrdinaryWalletExactSpendPlan staleZeroTransactionPlan = CreateSingleAssetPlan(testnet, 613);
		using LiquidOrdinaryWalletPlanFundingRow staleZeroTransactionRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch staleZeroTransactionBatch = CreateBatch(
			staleZeroTransactionPlan,
			staleZeroTransactionRow);
		LiquidTransactionId staleZeroTransactionId = GetField<LiquidWalletCoinControlEntry[]>(
			staleZeroTransactionPlan,
			"_selectedEntries")[0].OutPoint.TransactionId;
		Assert.False(staleZeroTransactionId.IsZero);
		SetField(staleZeroTransactionId, "<CanonicalRpcHex>k__BackingField", new string('0', 64));
		Assert.False(staleZeroTransactionId.IsZero);
		AssertFixedInvariant(staleZeroTransactionPlan, staleZeroTransactionBatch);

		PlanFixture nonhexIssuedFixture = CreateTwoAssetPlan(testnet);
		using LiquidOrdinaryWalletPlanFundingRow nonhexIssuedFirstRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow nonhexIssuedSecondRow = CreateRow([2]);
		using LiquidOrdinaryWalletPlanFundingBatch nonhexIssuedBatch = CreateBatch(
			nonhexIssuedFixture.Plan,
			nonhexIssuedFirstRow,
			nonhexIssuedSecondRow);
		LiquidAssetId nonhexIssuedAsset = AssertSharedIssuedAsset(nonhexIssuedFixture.Plan);
		SetField(
			nonhexIssuedAsset,
			"<CanonicalRpcHex>k__BackingField",
			"g" + IssuedAssetHex[1..]);
		AssertFixedInvariant(nonhexIssuedFixture.Plan, nonhexIssuedBatch);

		PlanFixture zeroIssuedFixture = CreateTwoAssetPlan(testnet);
		using LiquidOrdinaryWalletPlanFundingRow zeroIssuedFirstRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow zeroIssuedSecondRow = CreateRow([2]);
		using LiquidOrdinaryWalletPlanFundingBatch zeroIssuedBatch = CreateBatch(
			zeroIssuedFixture.Plan,
			zeroIssuedFirstRow,
			zeroIssuedSecondRow);
		LiquidAssetId zeroIssuedAsset = AssertSharedIssuedAsset(zeroIssuedFixture.Plan);
		SetField(zeroIssuedAsset, "<CanonicalRpcHex>k__BackingField", new string('0', 64));
		AssertFixedInvariant(zeroIssuedFixture.Plan, zeroIssuedBatch);
	}

	[Fact]
	public void CleanupProofIsTestOnlyAndSuccessStorageHasNoInstrumentationAlias()
	{
		string wireRoot = GetWireProductionRoot();
		AssertOwnedCleanupRegion(
			Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanFundingRow.cs"),
			["ownedCandidate = candidateTransaction.ToArray()", "ownedPrevious[index] = sourcePrevious[index]!.ToArray()"],
			["cleanupOwner?.Dispose()", "Clear(ownedCandidate, ownedPrevious)"]);
		AssertFundingRowOwnershipTransfer(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanFundingRow.cs"));
		AssertOwnedCleanupRegion(
			Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanFundingBatch.cs"),
			["ownedRows[copiedCount++] = copiedRow"],
			["cleanupOwner?.Dispose()", "ownedRows[index].Dispose()"]);
		AssertFundingBatchOwnershipTransfer(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanFundingBatch.cs"));
		AssertOwnedCleanupRegion(
			Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanEncoder.cs"),
			[
				"temporaryFrame = new byte[checked((int)exactLength)]",
				"ownedFrame = LiquidOrdinaryWalletPlanEncodedFrame.TakeOwnership( CooperationCapability, ref temporaryFrame)",
			],
			["ownedFrame?.Dispose()", "CryptographicOperations.ZeroMemory(temporaryFrame)"]);
		AssertOwnershipTransferOrder(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanEncodedFrame.cs"));

		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([0xaa], [0x01]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([0xbb], [0x02]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(fixture.Plan, first, second);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(fixture.Plan, batch, SourceEpoch);
		byte[] expected = Copy(frame);
		byte[] afterCollection = new byte[frame.Length];
		try
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			frame.CopyFrameTo(afterCollection);
			Assert.Equal(expected, afterCollection);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(expected);
			CryptographicOperations.ZeroMemory(afterCollection);
		}
	}

	[Fact]
	public async Task OwnerRacesReturnOnlyCompleteSuccessOrFixedLifecycleOutcomeAsync()
	{
		for (int iteration = 0; iteration < 64; iteration++)
		{
			LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
				ElementsPublicNetworkManifest.LiquidTestnet,
				(uint)(700 + iteration));
			LiquidOrdinaryWalletPlanFundingRow sourceRow = CreateRow([0xaa], [0x01]);
			LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, sourceRow);
			LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(plan, batch, SourceEpoch);
			byte[] expected = Copy(frame);
			byte[] destination = Enumerable.Repeat((byte)0xee, frame.Length).ToArray();
			LiquidOrdinaryWalletPlanEncodedFrame? encoded = null;
			LiquidOrdinaryWalletPlanFundingBatch? copiedBatch = null;
			using var start = new ManualResetEventSlim();
			try
			{
				Exception? copyFailure = null;
				Task copy = Task.Run(() =>
				{
					start.Wait();
					try
					{
						frame.CopyFrameTo(destination);
					}
					catch (Exception exception)
					{
						copyFailure = exception;
					}
				});
				Task frameDispose = Task.Run(() =>
				{
					start.Wait();
					frame.Dispose();
				});
				start.Set();
				await Task.WhenAll(copy, frameDispose);
				if (copyFailure is null)
				{
					Assert.Equal(expected, destination);
				}
				else
				{
					AssertFixedDisposed(copyFailure, nameof(LiquidOrdinaryWalletPlanEncodedFrame));
					Assert.All(destination, value => Assert.Equal(0xee, value));
				}

				Exception? encodeFailure = null;
				using var encodeStart = new ManualResetEventSlim();
				Task encode = Task.Run(() =>
				{
					encodeStart.Wait();
					try
					{
						LiquidOrdinaryWalletPlanEncoder.TryEncode(
							SourceEpoch,
							plan,
							batch,
							out encoded,
							out _);
					}
					catch (Exception exception)
					{
						encodeFailure = exception;
					}
				});
				Task batchDispose = Task.Run(() =>
				{
					encodeStart.Wait();
					batch.Dispose();
				});
				encodeStart.Set();
				await Task.WhenAll(encode, batchDispose);
				if (encodeFailure is null)
				{
					Assert.NotNull(encoded);
					byte[] racedFrame = Copy(encoded);
					try
					{
						Assert.Equal(expected, racedFrame);
					}
					finally
					{
						CryptographicOperations.ZeroMemory(racedFrame);
					}
				}
				else
				{
					Assert.Null(encoded);
					AssertFixedDisposed(encodeFailure, nameof(LiquidOrdinaryWalletPlanFundingBatch));
				}

				Exception? rowCopyFailure = null;
				using var rowStart = new ManualResetEventSlim();
				Task rowCopy = Task.Run(() =>
				{
					rowStart.Wait();
					try
					{
						LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
							plan,
							new LiquidOrdinaryWalletPlanFundingRow?[] { sourceRow },
							out copiedBatch,
							out _);
					}
					catch (Exception exception)
					{
						rowCopyFailure = exception;
					}
				});
				Task rowDispose = Task.Run(() =>
				{
					rowStart.Wait();
					sourceRow.Dispose();
				});
				rowStart.Set();
				await Task.WhenAll(rowCopy, rowDispose);
				if (rowCopyFailure is null)
				{
					Assert.NotNull(copiedBatch);
					using LiquidOrdinaryWalletPlanEncodedFrame copiedFrame = Encode(
						plan,
						copiedBatch,
						SourceEpoch);
					byte[] copiedBytes = Copy(copiedFrame);
					try
					{
						Assert.Equal(expected, copiedBytes);
					}
					finally
					{
						CryptographicOperations.ZeroMemory(copiedBytes);
					}
				}
				else
				{
					Assert.Null(copiedBatch);
					AssertFixedDisposed(rowCopyFailure, nameof(LiquidOrdinaryWalletPlanFundingRow));
				}
			}
			finally
			{
				copiedBatch?.Dispose();
				encoded?.Dispose();
				frame.Dispose();
				batch.Dispose();
				sourceRow.Dispose();
				CryptographicOperations.ZeroMemory(expected);
				CryptographicOperations.ZeroMemory(destination);
			}
		}
	}

	private static void AssertSelectedRow(
		byte[] encoded,
		ref int cursor,
		LiquidWalletCoinControlEntry selected,
		byte[] candidate,
		byte[][] previous)
	{
		Assert.Equal(selected.OutPoint.TransactionId.ToConsensusBytes(), encoded[cursor..(cursor + 32)]);
		cursor += 32;
		Assert.Equal(selected.OutPoint.OutputIndex, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(selected.Amount.AssetId.ToConsensusBytes(), encoded[cursor..(cursor + 32)]);
		cursor += 32;
		Assert.Equal((ulong)selected.Amount.AtomicUnits, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(cursor, 8)));
		cursor += 8;
		Assert.Equal((uint)candidate.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal((uint)previous.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(candidate, encoded[cursor..(cursor + candidate.Length)]);
		cursor += candidate.Length;
		foreach (byte[] payload in previous)
		{
			Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
			cursor += 4;
			Assert.Equal(payload, encoded[cursor..(cursor + payload.Length)]);
			cursor += payload.Length;
		}
	}

	private static void AssertDestination(
		byte[] encoded,
		ref int cursor,
		LiquidSuppliedConfidentialDestination destination)
	{
		string address = destination.GetAddress().GetCanonicalAddressText();
		Assert.Equal(destination.GetAssetId().ToConsensusBytes(), encoded[cursor..(cursor + 32)]);
		cursor += 32;
		Assert.Equal((ulong)destination.GetAmount()!.AtomicUnits, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(cursor, 8)));
		cursor += 8;
		Assert.Equal((uint)address.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(address, System.Text.Encoding.ASCII.GetString(encoded, cursor, address.Length));
		cursor += address.Length;
	}

	private static void AssertRowRejected(
		byte[]? candidate,
		IReadOnlyList<byte[]?>? previous,
		LiquidOrdinaryWalletPlanWireErrorCode expected)
	{
		LiquidOrdinaryWalletPlanFundingRow? row = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanFundingRow.TryCreate(
				candidate,
				previous,
				out row,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(row);
			Assert.Equal(expected, errorCode);
		}
		finally
		{
			row?.Dispose();
		}
	}

	private static void AssertBatchRejected(
		LiquidOrdinaryWalletExactSpendPlan? plan,
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?>? rows,
		LiquidOrdinaryWalletPlanWireErrorCode expected)
	{
		LiquidOrdinaryWalletPlanFundingBatch? batch = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
				plan,
				rows,
				out batch,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(batch);
			Assert.Equal(expected, errorCode);
		}
		finally
		{
			batch?.Dispose();
		}
	}

	private static void AssertEncodeRejected(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidOrdinaryWalletExactSpendPlan? plan,
		LiquidOrdinaryWalletPlanFundingBatch? batch,
		LiquidOrdinaryWalletPlanWireErrorCode expected)
	{
		LiquidOrdinaryWalletPlanEncodedFrame? frame = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanEncoder.TryEncode(
				sourceEpoch,
				plan,
				batch,
				out frame,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(frame);
			Assert.Equal(expected, errorCode);
		}
		finally
		{
			frame?.Dispose();
		}
	}

	private static LiquidOrdinaryWalletPlanFundingRow CreateRow(
		byte[] candidate,
		params byte[]?[] previous)
		=> CreateRow(candidate, (IReadOnlyList<byte[]?>)previous);

	private static LiquidOrdinaryWalletPlanFundingRow CreateRow(
		byte[] candidate,
		IReadOnlyList<byte[]?> previous)
	{
		bool succeeded = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
			candidate,
			previous,
			out LiquidOrdinaryWalletPlanFundingRow? row,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		return row ?? throw new InvalidOperationException("Funding row creation returned no owner.");
	}

	private static LiquidOrdinaryWalletPlanFundingBatch CreateBatch(
		LiquidOrdinaryWalletExactSpendPlan plan,
		params LiquidOrdinaryWalletPlanFundingRow?[] rows)
		=> CreateBatch(plan, (IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?>)rows);

	private static LiquidOrdinaryWalletPlanFundingBatch CreateBatch(
		LiquidOrdinaryWalletExactSpendPlan plan,
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?> rows)
	{
		bool succeeded = LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
			plan,
			rows,
			out LiquidOrdinaryWalletPlanFundingBatch? batch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		return batch ?? throw new InvalidOperationException("Funding batch creation returned no owner.");
	}

	private static LiquidOrdinaryWalletPlanEncodedFrame Encode(
		LiquidOrdinaryWalletExactSpendPlan plan,
		LiquidOrdinaryWalletPlanFundingBatch batch,
		ReadOnlySpan<byte> sourceEpoch)
	{
		bool succeeded = LiquidOrdinaryWalletPlanEncoder.TryEncode(
			sourceEpoch,
			plan,
			batch,
			out LiquidOrdinaryWalletPlanEncodedFrame? frame,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		return frame ?? throw new InvalidOperationException("Encoding returned no frame owner.");
	}

	private static byte[] Copy(LiquidOrdinaryWalletPlanEncodedFrame frame)
	{
		byte[] bytes = new byte[frame.Length];
		frame.CopyFrameTo(bytes);
		return bytes;
	}

	private static PlanFixture CreateTwoAssetPlan(ElementsPublicNetworkManifest manifest)
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidAssetId issuedAsset = LiquidAssetId.ParseRpcHex(IssuedAssetHex);
		LiquidTransactionId secondId = Tx(2);
		LiquidOwnedOutput second = Output(secondId, 0, issuedAsset, peggedAsset, 7);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(secondId, [], [second]));
		LiquidTransactionId firstId = Tx(1);
		LiquidOwnedOutput first = Output(firstId, 0, peggedAsset, peggedAsset, 4);
		state = state.Apply(
			state.Revision,
			LiquidWalletTransactionDelta.Create(firstId, [], [first]));
		LiquidSuppliedConfidentialDestination firstDestination = Destination(
			manifest,
			SecondScriptHex,
			issuedAsset,
			7);
		LiquidSuppliedConfidentialDestination secondDestination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			3);
		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[second.OutPoint, first.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([firstDestination, secondDestination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
		IReadOnlyList<LiquidWalletCoinControlEntry> selected = plan.GetSelectedEntries();
		return new PlanFixture(
			manifest,
			peggedAsset,
			plan,
			selected[0],
			selected[1],
			firstDestination,
			secondDestination);
	}

	private static LiquidOrdinaryWalletExactSpendPlan CreateSingleAssetPlan(
		ElementsPublicNetworkManifest manifest,
		uint transactionValue)
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidTransactionId transactionId = Tx(transactionValue);
		LiquidOwnedOutput output = Output(transactionId, 0, peggedAsset, peggedAsset, 10);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], [output]));
		LiquidSuppliedConfidentialDestination destination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			9);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[output.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
	}

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		LiquidAssetId peggedAssetId,
		long atomicUnits)
	{
		LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString(PublicKeyHex),
			LiquidKeyBranch.External,
			outputIndex);
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			spendKey.GetScriptPubKey(),
			LiquidAssetAmount.Create(assetId, peggedAssetId, atomicUnits),
			spendKey);
	}

	private static LiquidSuppliedConfidentialDestination Destination(
		ElementsPublicNetworkManifest manifest,
		string scriptHex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString(scriptHex),
			LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex)));
		return LiquidSuppliedConfidentialDestination.Create(
			manifest,
			address,
			assetId,
			LiquidAssetAmount.Create(assetId, peggedAsset, atomicUnits),
			LiquidWalletLabelSet.Create(["wire-test"]));
	}

	private static LiquidTransactionId Tx(uint value) =>
		LiquidTransactionId.ParseRpcHex(value.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));

	private static T GetField<T>(object owner, string fieldName) =>
		Assert.IsType<T>(owner.GetType()
			.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(owner));

	private static void SetField<T>(object owner, string fieldName, T value) =>
		owner.GetType()
			.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(owner, value);

	private static void AssertFixedInvariant(
		LiquidOrdinaryWalletExactSpendPlan plan,
		LiquidOrdinaryWalletPlanFundingBatch batch)
	{
		LiquidOrdinaryWalletPlanEncodedFrame? frame = null;
		try
		{
			LiquidOrdinaryWalletPlanEncoder.TryEncode(SourceEpoch, plan, batch, out frame, out _);
		}
		catch (InvalidOperationException exception)
		{
			Assert.Null(frame);
			Assert.Equal(LiquidOrdinaryWalletPlanEncoder.InvariantMessage, exception.Message);
			return;
		}
		finally
		{
			frame?.Dispose();
		}

		throw new Xunit.Sdk.XunitException("A mutated accepted type state was encoded.");
	}

	private static LiquidAssetId AssertSharedIssuedAsset(
		LiquidOrdinaryWalletExactSpendPlan plan)
	{
		LiquidWalletCoinControlEntry selected = Assert.Single(
			plan.GetSelectedEntries(),
			entry => StringComparer.Ordinal.Equals(entry.Amount.AssetId.CanonicalRpcHex, IssuedAssetHex));
		LiquidSuppliedConfidentialDestination destination = Assert.Single(
			plan.GetDestinations(),
			item => StringComparer.Ordinal.Equals(item.GetAssetId().CanonicalRpcHex, IssuedAssetHex));
		Assert.Same(selected.Amount.AssetId, destination.GetAssetId());
		Assert.Same(destination.GetAssetId(), destination.GetAmount()!.AssetId);
		return destination.GetAssetId();
	}

	private static void AssertFixedDisposed(Exception exception, string objectName)
	{
		ObjectDisposedException disposed = Assert.IsType<ObjectDisposedException>(exception);
		Assert.Equal(objectName, disposed.ObjectName);
		Assert.DoesNotContain("System.Byte", disposed.ToString(), StringComparison.Ordinal);
	}

	private static string FailureMessage(LiquidOrdinaryWalletPlanWireErrorCode errorCode) =>
		errorCode == LiquidOrdinaryWalletPlanWireErrorCode.None
			? "The operation returned false without an error code."
			: errorCode.GetMessage();

	private static void AssertExactErrorMessageMapping(
		Func<LiquidOrdinaryWalletPlanWireErrorCode, string> getMessage)
	{
		string[] expected =
		[
			"ordinary wallet plan wire argument is invalid",
			"ordinary wallet plan wire version is unsupported",
			"ordinary wallet plan wire encoding is invalid",
			"ordinary wallet plan wire limit exceeded",
			"ordinary wallet plan wire source binding does not match",
			"ordinary wallet plan wire context was rejected",
			"ordinary wallet plan wire plan was rejected",
			"ordinary wallet plan wire funding was rejected",
		];
		string[] actual = Enumerable.Range(1, 8)
			.Select(value => getMessage((LiquidOrdinaryWalletPlanWireErrorCode)value))
			.ToArray();
		Assert.Equal(expected, actual);
	}

	private static string GetProductionRoot([CallerFilePath] string testFilePath = "") =>
		Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(testFilePath)!,
			"../../../../../WalletWasabi"));

	private static string GetWireProductionRoot([CallerFilePath] string testFilePath = "") =>
		Path.Combine(GetProductionRoot(testFilePath), "Liquid/Wallet/Wire");

	private static Type[] GetExactProductionWireTypes() =>
		typeof(LiquidOrdinaryWalletPlanEncoder).Assembly.GetTypes()
			.Where(type => IsProductionWireNamespace(type.Namespace))
			.OrderBy(type => type.FullName, StringComparer.Ordinal)
			.ToArray();

	private static bool IsProductionWireNamespace(string? candidate)
	{
		string expected = typeof(LiquidOrdinaryWalletPlanEncoder).Namespace!;
		return StringComparer.Ordinal.Equals(candidate, expected) ||
			candidate?.StartsWith(expected + ".", StringComparison.Ordinal) is true;
	}

	private static void AssertExactWireTypeNames(
		IEnumerable<string> expected,
		IEnumerable<string> actual) =>
		Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

	private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

	private static void AssertExactPlanWireAccessorSource(string source)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(source).GetRoot());
		string[] methods = root.DescendantNodes()
			.OfType<MethodDeclarationSyntax>()
			.Where(method => method.Identifier.ValueText is
				"GetDestinationNetworkManifestId" or "GetPeggedAssetId" or "GetExplicitFee" or
				"GetSelectedEntriesForWireEncoding" or "GetDestinationsForWireEncoding")
			.Select(method => NormalizeSyntax(method.ToString()))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(
			new[]
			{
				"public LiquidAssetAmount GetExplicitFee() => _explicitFee;",
				"public LiquidAssetId GetPeggedAssetId() => _peggedAssetId;",
				"public string GetDestinationNetworkManifestId() => _destinationNetworkManifestId;",
				"internal ReadOnlySpan<LiquidSuppliedConfidentialDestination> GetDestinationsForWireEncoding() => _destinations;",
				"internal ReadOnlySpan<LiquidWalletCoinControlEntry> GetSelectedEntriesForWireEncoding() => _selectedEntries;",
			}.Order(StringComparer.Ordinal),
			methods);

		string[] properties = root.DescendantNodes()
			.OfType<PropertyDeclarationSyntax>()
			.Where(property => property.Identifier.ValueText is "SourceRevision" or "SelectedInputCount")
			.Select(property => NormalizeSyntax(property.ToString()))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(
			new[]
			{
				"public int SelectedInputCount => _selectedEntries.Length;",
				"public ulong SourceRevision { get; }",
			}.Order(StringComparer.Ordinal),
			properties);
	}

	private static bool IsSafeWireSource(string source)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(source).GetRoot());
		return !root.DescendantTrivia(descendIntoTrivia: true)
			.Any(trivia => trivia.GetStructure() is DirectiveTriviaSyntax) &&
			!root.DescendantTokens().Any(token =>
				token.RawKind is (int)SyntaxKind.UnsafeKeyword or (int)SyntaxKind.ExternKeyword ||
				token.RawKind == (int)SyntaxKind.IdentifierToken &&
					(token.ValueText.Contains("Fault", StringComparison.OrdinalIgnoreCase) ||
						token.ValueText.Contains("Probe", StringComparison.OrdinalIgnoreCase) ||
						token.ValueText.Contains("TestHook", StringComparison.OrdinalIgnoreCase))) &&
			!root.DescendantNodes().Any(node => node is PointerTypeSyntax or
				FunctionPointerTypeSyntax or ImplicitStackAllocArrayCreationExpressionSyntax or
				FixedStatementSyntax);
	}

	private static void AssertNonPrivateMethodNames(Type type, params string[] expected)
	{
		string[] actual = GetDeclaredMethods(type)
			.Where(method => !method.IsPrivate)
			.Select(method => method.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
	}

	private static void AssertCapabilityGuarded(
		Type type,
		MethodInfo ensureCooperation,
		params string[] methodNames)
	{
		foreach (string methodName in methodNames)
		{
			MethodInfo method = Assert.Single(
				type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
					BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
				candidate => candidate.Name == methodName);
			Assert.Equal(typeof(object), Assert.Single(
				method.GetParameters(),
				parameter => parameter.Position == 0).ParameterType);
			Assert.Equal(ensureCooperation, GetIlReferences(method).First());
		}
	}

	private static void AssertOwnedCleanupRegion(
		string sourcePath,
		IReadOnlyList<string> stagingStatements,
		IReadOnlyList<string> cleanupStatements)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		foreach (string staging in stagingStatements)
		{
			string normalizedStaging = NormalizeSyntax(staging);
			StatementSyntax statement = Assert.Single(
				root.DescendantNodes().OfType<StatementSyntax>(),
				node => NormalizeSyntax(node.ToString()).Contains(normalizedStaging, StringComparison.Ordinal) &&
					!node.DescendantNodes().OfType<StatementSyntax>()
						.Any(child => NormalizeSyntax(child.ToString()).Contains(normalizedStaging, StringComparison.Ordinal)));
			TryStatementSyntax guarded = Assert.Single(
				statement.Ancestors().OfType<TryStatementSyntax>(),
				candidate => candidate.Finally is not null);
			string cleanup = NormalizeSyntax(guarded.Finally!.ToString());
			Assert.All(cleanupStatements, expected =>
				Assert.Contains(NormalizeSyntax(expected), cleanup, StringComparison.Ordinal));
		}
	}

	private static void AssertOwnershipTransferOrder(string sourcePath)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		MethodDeclarationSyntax transfer = Assert.Single(
			root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			method => method.Identifier.ValueText == "TakeOwnership");
		BlockSyntax transferBody = transfer.Body ??
			throw new Xunit.Sdk.XunitException("TakeOwnership must have a block body.");
		string body = NormalizeSyntax(transferBody.ToString());
		int construct = body.IndexOf(
			"var owner = new LiquidOrdinaryWalletPlanEncodedFrame(ownedFrame);",
			StringComparison.Ordinal);
		int releaseCaller = body.IndexOf("frame = null;", StringComparison.Ordinal);
		int returnOwner = body.IndexOf("return owner;", StringComparison.Ordinal);
		Assert.True(construct >= 0 && construct < releaseCaller && releaseCaller < returnOwner, body);
	}

	private static void AssertFundingBatchOwnershipTransfer(string sourcePath)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		MethodDeclarationSyntax tryCreate = Assert.Single(
			root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			IsTryCreateMethod);
		TryStatementSyntax guarded = Assert.Single(
			tryCreate.DescendantNodes().OfType<TryStatementSyntax>(),
			HasFinally);
		StatementSyntax[] successStatements = guarded.Block.Statements.ToArray();

		AssignmentExpressionSyntax[] assignments = guarded.Block.DescendantNodes()
			.OfType<AssignmentExpressionSyntax>()
			.ToArray();
		VariableDeclaratorSyntax guard = Assert.Single(
			tryCreate.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
			IsCleanupOwnerDeclaration);
		Assert.Equal((int)SyntaxKind.NullLiteralExpression, guard.Initializer?.Value.RawKind);
		ObjectCreationExpressionSyntax ownerConstruction = Assert.Single(
			tryCreate.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
			IsFundingBatchOwnerCreation);
		AssignmentExpressionSyntax construct = Assert.Single(assignments, IsFundingBatchGuardConstruction);
		Assert.Same(ownerConstruction, construct.Right);
		AssignmentExpressionSyntax releaseRows = Assert.Single(assignments, IsOwnedRowsNullAssignment);
		AssignmentExpressionSyntax publish = Assert.Single(assignments, IsBatchPublication);
		AssignmentExpressionSyntax releaseOwner = Assert.Single(assignments, IsCleanupOwnerNullAssignment);

		int constructIndex = Array.IndexOf(successStatements, construct.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseRowsIndex = Array.IndexOf(successStatements, releaseRows.FirstAncestorOrSelf<StatementSyntax>()!);
		int publishIndex = Array.IndexOf(successStatements, publish.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseOwnerIndex = Array.IndexOf(successStatements, releaseOwner.FirstAncestorOrSelf<StatementSyntax>()!);
		Assert.True(
			constructIndex >= 0 && constructIndex < releaseRowsIndex && releaseRowsIndex < publishIndex &&
			publishIndex + 1 == releaseOwnerIndex,
			NormalizeSyntax(guarded.Block.ToString()));
		Assert.IsType<ReturnStatementSyntax>(successStatements[releaseOwnerIndex + 1]);
		Assert.Equal(releaseOwnerIndex + 2, successStatements.Length);

		Assert.DoesNotContain(
			assignments,
			IsOwnedRowsCollectionAssignment);
		Assert.Equal(
			2,
			tryCreate.DescendantNodes().OfType<AssignmentExpressionSyntax>().Count(IsBatchAssignment));
		Assert.Equal(2, assignments.Count(IsCleanupOwnerAssignment));
		string cleanup = NormalizeSyntax(guarded.Finally!.ToString());
		Assert.Contains("cleanupOwner?.Dispose();", cleanup, StringComparison.Ordinal);
		Assert.Contains("ownedRows[index].Dispose();", cleanup, StringComparison.Ordinal);
	}

	private static void AssertFundingRowOwnershipTransfer(string sourcePath)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		MethodDeclarationSyntax tryCreate = Assert.Single(
			root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			IsTryCreateMethod);
		TryStatementSyntax guarded = Assert.Single(
			tryCreate.DescendantNodes().OfType<TryStatementSyntax>(),
			HasFinally);
		StatementSyntax[] successStatements = guarded.Block.Statements.ToArray();

		AssignmentExpressionSyntax[] assignments = guarded.Block.DescendantNodes()
			.OfType<AssignmentExpressionSyntax>()
			.ToArray();
		VariableDeclaratorSyntax guard = Assert.Single(
			tryCreate.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
			IsCleanupOwnerDeclaration);
		Assert.Equal((int)SyntaxKind.NullLiteralExpression, guard.Initializer?.Value.RawKind);
		ObjectCreationExpressionSyntax ownerConstruction = Assert.Single(
			tryCreate.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
			IsFundingRowOwnerCreation);
		AssignmentExpressionSyntax construct = Assert.Single(assignments, IsFundingRowGuardConstruction);
		Assert.Same(ownerConstruction, construct.Right);
		AssignmentExpressionSyntax releaseCandidate = Assert.Single(assignments, IsOwnedCandidateNullAssignment);
		AssignmentExpressionSyntax releasePrevious = Assert.Single(assignments, IsOwnedPreviousNullAssignment);
		AssignmentExpressionSyntax publish = Assert.Single(assignments, IsRowPublication);
		AssignmentExpressionSyntax releaseOwner = Assert.Single(assignments, IsCleanupOwnerNullAssignment);

		int constructIndex = Array.IndexOf(successStatements, construct.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseCandidateIndex = Array.IndexOf(
			successStatements,
			releaseCandidate.FirstAncestorOrSelf<StatementSyntax>()!);
		int releasePreviousIndex = Array.IndexOf(
			successStatements,
			releasePrevious.FirstAncestorOrSelf<StatementSyntax>()!);
		int publishIndex = Array.IndexOf(successStatements, publish.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseOwnerIndex = Array.IndexOf(successStatements, releaseOwner.FirstAncestorOrSelf<StatementSyntax>()!);
		Assert.True(
			constructIndex >= 0 && constructIndex < releaseCandidateIndex &&
			releaseCandidateIndex < releasePreviousIndex && releasePreviousIndex < publishIndex &&
			publishIndex + 1 == releaseOwnerIndex,
			NormalizeSyntax(guarded.Block.ToString()));
		Assert.IsType<ReturnStatementSyntax>(successStatements[releaseOwnerIndex + 1]);
		Assert.Equal(releaseOwnerIndex + 2, successStatements.Length);
		Assert.Equal(
			2,
			tryCreate.DescendantNodes().OfType<AssignmentExpressionSyntax>().Count(IsRowAssignment));
		Assert.Equal(2, assignments.Count(IsCleanupOwnerAssignment));
		string cleanup = NormalizeSyntax(guarded.Finally!.ToString());
		Assert.Contains("cleanupOwner?.Dispose();", cleanup, StringComparison.Ordinal);
		Assert.Contains("Clear(ownedCandidate, ownedPrevious);", cleanup, StringComparison.Ordinal);
	}

	private static bool IsTryCreateMethod(MethodDeclarationSyntax method) =>
		method.Identifier.ValueText == "TryCreate";

	private static bool HasFinally(TryStatementSyntax statement) => statement.Finally is not null;

	private static bool IsCleanupOwnerDeclaration(VariableDeclaratorSyntax declaration) =>
		declaration.Identifier.ValueText == "cleanupOwner";

	private static bool IsFundingBatchOwnerCreation(ObjectCreationExpressionSyntax creation) =>
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingBatch";

	private static bool IsFundingRowOwnerCreation(ObjectCreationExpressionSyntax creation) =>
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingRow";

	private static bool IsFundingBatchGuardConstruction(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "cleanupOwner" &&
		assignment.Right is ObjectCreationExpressionSyntax creation &&
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingBatch";

	private static bool IsFundingRowGuardConstruction(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "cleanupOwner" &&
		assignment.Right is ObjectCreationExpressionSyntax creation &&
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingRow";

	private static bool IsOwnedRowsNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "ownedRows");

	private static bool IsOwnedCandidateNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "ownedCandidate");

	private static bool IsOwnedPreviousNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "ownedPrevious");

	private static bool IsCleanupOwnerNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "cleanupOwner");

	private static bool IsNullAssignment(AssignmentExpressionSyntax assignment, string left) =>
		assignment.Left.ToString() == left &&
		assignment.Right.RawKind == (int)SyntaxKind.NullLiteralExpression;

	private static bool IsBatchPublication(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "batch" && assignment.Right.ToString() == "cleanupOwner";

	private static bool IsRowPublication(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "row" && assignment.Right.ToString() == "cleanupOwner";

	private static bool IsOwnedRowsCollectionAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "ownedRows" && assignment.Right is CollectionExpressionSyntax;

	private static bool IsBatchAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "batch";

	private static bool IsRowAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "row";

	private static bool IsCleanupOwnerAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "cleanupOwner";

	private static string NormalizeSyntax(string value) =>
		string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	private static IEnumerable<string> GetTypeSurfaceManifest(Type type)
	{
		yield return $"TYPE|{TypeIdentity(type)}|{(int)type.Attributes}|{TypeIdentity(type.BaseType)}|" +
			string.Join(",", type.GetInterfaces().Select(TypeIdentity).Order(StringComparer.Ordinal)) + "|" +
			AttributeIdentity(type.GetCustomAttributesData());
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		foreach (FieldInfo field in type.GetFields(Declared).OrderBy(field => field.Name, StringComparer.Ordinal))
		{
			yield return $"FIELD|{TypeIdentity(type)}|{field.Name}|{TypeIdentity(field.FieldType)}|" +
				$"{(int)field.Attributes}|{AttributeIdentity(field.GetCustomAttributesData())}";
		}
		foreach (PropertyInfo property in type.GetProperties(Declared).OrderBy(property => property.Name, StringComparer.Ordinal))
		{
			yield return $"PROPERTY|{TypeIdentity(type)}|{property.Name}|{TypeIdentity(property.PropertyType)}|" +
				$"{(int)property.Attributes}|{property.GetMethod?.Name}|{property.SetMethod?.Name}|" +
				AttributeIdentity(property.GetCustomAttributesData());
		}
		foreach (MethodBase method in GetDeclaredMethods(type).OrderBy(MethodIdentity, StringComparer.Ordinal))
		{
			MethodBody? body = method.GetMethodBody();
			yield return $"METHOD|{MethodIdentity(method)}|{(int)method.Attributes}|" +
				$"{(int)method.GetMethodImplementationFlags()}|{(int)method.CallingConvention}|" +
				AttributeIdentity(method.GetCustomAttributesData());
			if (method is MethodInfo methodInfo)
			{
				yield return $"RETURN|{MethodIdentity(method)}|{TypeIdentity(methodInfo.ReturnType)}|" +
					AttributeIdentity(methodInfo.ReturnParameter.GetCustomAttributesData());
			}
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				yield return $"PARAM|{MethodIdentity(method)}|{parameter.Position}|{parameter.Name}|" +
					$"{TypeIdentity(parameter.ParameterType)}|{(int)parameter.Attributes}|" +
					AttributeIdentity(parameter.GetCustomAttributesData());
			}
			if (body is null)
			{
				yield return $"BODY|{MethodIdentity(method)}|null";
				continue;
			}

			yield return $"BODY|{MethodIdentity(method)}|{body.InitLocals}|{body.MaxStackSize}|" +
				Convert.ToHexString(body.GetILAsByteArray() ?? []).ToLowerInvariant();
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				yield return $"LOCAL|{MethodIdentity(method)}|{local.LocalIndex}|" +
					$"{TypeIdentity(local.LocalType)}|{local.IsPinned}";
			}
			foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
			{
				yield return $"EH|{MethodIdentity(method)}|{(int)clause.Flags}|{clause.TryOffset}|" +
					$"{clause.TryLength}|{clause.HandlerOffset}|{clause.HandlerLength}|" +
					TypeIdentity(clause.Flags == ExceptionHandlingClauseOptions.Clause ? clause.CatchType : null);
			}
			foreach (MemberInfo reference in GetIlReferences(method))
			{
				yield return $"REF|{MethodIdentity(method)}|{MemberIdentity(reference)}";
			}
		}
	}

	private static IEnumerable<MethodBase> GetDeclaredMethods(Type type)
	{
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		return type.GetConstructors(Declared).Cast<MethodBase>().Concat(type.GetMethods(Declared));
	}

	private static string TypeIdentity(Type? type) =>
		WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests.NormalizeProductAssemblyVersion(
			type?.AssemblyQualifiedName ?? "null");

	private static string MethodIdentity(MethodBase method) =>
		$"{TypeIdentity(method.DeclaringType)}::{method.Name}" +
		$"`{(method.IsGenericMethod ? method.GetGenericArguments().Length : 0)}" +
		$"<{(method.IsGenericMethod ? string.Join(",", method.GetGenericArguments().Select(TypeIdentity)) : "")}>" +
		"(" +
		$"{string.Join(",", method.GetParameters().Select(parameter => TypeIdentity(parameter.ParameterType)))})" +
		$"->{(method is MethodInfo methodInfo ? TypeIdentity(methodInfo.ReturnType) : "void")}";

	private static string MemberIdentity(MemberInfo member) => member switch
	{
		MethodBase method => MethodIdentity(method),
		FieldInfo field => $"{TypeIdentity(field.DeclaringType)}::{field.Name}:{TypeIdentity(field.FieldType)}",
		Type memberType => TypeIdentity(memberType),
		_ => $"{TypeIdentity(member.DeclaringType)}::{member.Name}",
	};

	private static string AttributeIdentity(IEnumerable<CustomAttributeData> attributes) =>
		string.Join(",", attributes.Select(attribute => TypeIdentity(attribute.AttributeType))
			.Order(StringComparer.Ordinal));

	private static bool IsForbiddenWireMember(MemberInfo member)
	{
		if (member is MethodBase { DeclaringType: { } declaringType } monitorMethod &&
			declaringType == typeof(Monitor) &&
			monitorMethod.Name is nameof(Monitor.Enter) or nameof(Monitor.Exit))
		{
			return false;
		}

		if (IsForbiddenWireIdentity(MemberIdentity(member)) || IsForbiddenWireType(member.DeclaringType))
		{
			return true;
		}
		if (member is MethodInfo method && IsForbiddenWireType(method.ReturnType))
		{
			return true;
		}
		if (member is MethodBase methodBase && methodBase.GetParameters().Any(parameter =>
			IsForbiddenWireType(parameter.ParameterType)))
		{
			return true;
		}
		if (member is FieldInfo field && IsForbiddenWireType(field.FieldType))
		{
			return true;
		}
		if (member is PropertyInfo property && IsForbiddenWireType(property.PropertyType))
		{
			return true;
		}

		return false;
	}

	private static bool IsForbiddenWireType(Type? type)
	{
		if (type is null)
		{
			return false;
		}
		if (IsForbiddenWireIdentity(type.FullName ?? type.Name) ||
			IsForbiddenWireIdentity(type.Assembly.FullName ?? ""))
		{
			return true;
		}
		if (type.HasElementType)
		{
			return IsForbiddenWireType(type.GetElementType());
		}

		return type.IsGenericType && type.GetGenericArguments().Any(IsForbiddenWireType);
	}

	private static bool IsForbiddenWireIdentity(string identity) =>
		identity.Contains("WalletWasabi.Logging", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Serilog", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("NLog", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Logger", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Runtime.InteropServices", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains(".Interop.", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("PInvoke", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("DllImport", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("WalletWasabi.Liquid.Rpc", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.IO", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Net", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Threading", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("HttpClient", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("RandomNumberGenerator", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Random", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Randomness", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("TimeProvider", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Environment", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("ElementsNode", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("RawFrame", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("GetFrame", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Pset", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Psbt", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Signer", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Broadcast", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("CoinJoin", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Sponsor", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains(".Fault", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Probe", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("TestHook", StringComparison.OrdinalIgnoreCase);

	private static IEnumerable<MemberInfo> GetIlReferences(MethodBase method)
		=> GetIlInstructions(method)
			.Where(instruction => instruction.Member is not null)
			.Select(instruction => instruction.Member!);

	private static IEnumerable<(OpCode OpCode, MemberInfo? Member)> GetIlInstructions(MethodBase method)
		=> GetIlInstructionsWithOffsets(method)
			.Select(instruction => (instruction.OpCode, instruction.Member));

	private static IEnumerable<(int Offset, OpCode OpCode, MemberInfo? Member)> GetIlInstructionsWithOffsets(
		MethodBase method)
	{
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		Type[]? typeArguments = method.DeclaringType?.IsGenericType == true
			? method.DeclaringType.GetGenericArguments()
			: null;
		Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
		for (int offset = 0; offset < il.Length;)
		{
			int instructionOffset = offset;
			OpCode opCode = ReadOpCode(il, ref offset);
			MemberInfo? resolvedMember = null;
			if (opCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
				OperandType.InlineTok or OperandType.InlineType)
			{
				int token = BitConverter.ToInt32(il, offset);
				resolvedMember = method.Module.ResolveMember(token, typeArguments, methodArguments);
			}

			yield return (instructionOffset, opCode, resolvedMember);
			offset += OperandSize(opCode.OperandType, il, offset);
		}
	}

	private static OpCode ReadOpCode(byte[] il, ref int offset)
	{
		short value = il[offset++];
		if (value == 0xfe)
		{
			value = (short)(0xfe00 | il[offset++]);
		}

		return typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
			.Select(field => Assert.IsType<OpCode>(field.GetValue(null)))
			.First(opCode => opCode.Value == value);
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
			_ => throw new Xunit.Sdk.XunitException($"Unsupported operand type {operandType}."),
		};

	private sealed record EvaluatedBuildItem(
		string Identity,
		string FullPath,
		string DefiningProjectFullPath,
		IReadOnlyDictionary<string, string> Metadata);

	private sealed record GeneratedBuildFile(string RelativePath, string Source, string Sha256);
	private sealed record CompilerAuthorityEntry(
		string Section,
		string Identity,
		string Detail,
		string Qualifier,
		string Values,
		string Sha256);
	private sealed record BinaryBuildTrace(
		string[] CommandLineArgs,
		IReadOnlyDictionary<string, string[]> TaskInputs,
		string[] ImportedProjects,
		string ImportManifest,
		CompilerAuthorityEntry[] CscAuthorityEntries);
	private readonly record struct BuildContextKey(
		int NodeId,
		int ProjectContextId,
		int TargetId,
		int TaskId,
		int SubmissionId,
		int ProjectInstanceId,
		int EvaluationId);

	private sealed record ProductionBuildAuthority(
		IReadOnlyDictionary<string, string> Properties,
		IReadOnlyDictionary<string, string> GlobalProperties,
		IReadOnlyDictionary<string, string> ChildEnvironment,
		string[] InvocationArguments,
		(string FullPath, string RelativePath, string Source)[] CompileInputs,
		(string FullPath, string DefiningProjectFullPath)[] Analyzers,
		EvaluatedBuildItem[] ReferencePaths,
		EvaluatedBuildItem[] AdditionalFiles,
		EvaluatedBuildItem[] EditorConfigFiles,
		EvaluatedBuildItem[] EmbeddedFiles,
		string[] CscCommandLineArgs,
		GeneratedBuildFile[] GeneratedSources,
		string[] ImportedProjects,
		string ImportClosureManifest,
		string ReferenceAuthorityManifest,
		string CompilerInputAuthorityManifest,
		string ToolchainAuthorityManifest,
		string OutputAssemblySha256,
		string DotnetHost,
		string DotnetRoot,
		string RepositoryRoot,
		(string PrimaryRoot, string[] OrderedRoots) PackageAuthority,
		string AuthorityRoot,
		string GeneratedRoot,
		string InjectedAnalyzerTargetContent);

	private static ProductionBuildAuthority GetEvaluatedProductionBuildAuthority(
		string expectedProductionRoot)
	{
		string projectPath = Path.GetFullPath(Path.Combine(expectedProductionRoot, "WalletWasabi.csproj"));
		string repositoryRoot = Path.GetFullPath(Path.GetDirectoryName(expectedProductionRoot)!);
		string projectAssetsFile = Path.GetFullPath(Path.Combine(expectedProductionRoot, "obj/project.assets.json"));
		string projectExtensionsPath = Path.GetFullPath(Path.Combine(expectedProductionRoot, "obj")) +
			Path.DirectorySeparatorChar;
		(string dotnetHost, string dotnetRoot) = GetApprovedDotnetHost();
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority = GetPinnedPackageAuthority(projectAssetsFile);
		bool pinnedNixProfile = !ReadLockedPackageAuthority(
			Path.Combine(expectedProductionRoot, "packages.lock.json"),
			"net10.0").HasContentHashes;
		string packageRoot = packageAuthority.PrimaryRoot;
		string authorityRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-authority-{Guid.NewGuid():N}");
		string baseIntermediateOutputPath = Path.Combine(authorityRoot, "obj") + Path.DirectorySeparatorChar;
		string intermediateOutputPath = Path.Combine(authorityRoot, "obj/net10.0") + Path.DirectorySeparatorChar;
		string baseOutputPath = Path.Combine(authorityRoot, "base-bin") + Path.DirectorySeparatorChar;
		string outputPath = Path.Combine(authorityRoot, "bin") + Path.DirectorySeparatorChar;
		string generatedRoot = Path.Combine(authorityRoot, "generated");
		string disabledImportsRoot = Path.Combine(authorityRoot, "disabled-imports");
		string childHome = Path.Combine(authorityRoot, "home");
		string childTemp = Path.Combine(authorityRoot, "temp");
		string injectedAnalyzerTarget = Path.Combine(authorityRoot, "injected-analyzer.targets");
		string automaticResponseFile = Path.Combine(authorityRoot, "MSBuild.rsp");
		string diagnosticLog = Path.Combine(authorityRoot, "build.diagnostic.log");
		string binaryLog = Path.Combine(authorityRoot, "build.binlog");
		const string InjectedAnalyzerTargetContent =
			"<Project><Target Name=\"InjectAnalyzer\" BeforeTargets=\"CoreCompile\"><ItemGroup>" +
			"<Analyzer Include=\"/wlpq/injected-analyzer.dll\" />" +
			"</ItemGroup></Target></Project>";
		Directory.CreateDirectory(authorityRoot);
		Directory.CreateDirectory(generatedRoot);
		Directory.CreateDirectory(disabledImportsRoot);
		Directory.CreateDirectory(childHome);
		Directory.CreateDirectory(childTemp);
		File.WriteAllText(
			injectedAnalyzerTarget,
			InjectedAnalyzerTargetContent,
			Encoding.UTF8);
		File.WriteAllText(
			automaticResponseFile,
			"-property:CscToolPath=/wlpq/automatic-response-file-must-be-ignored\n",
			Encoding.UTF8);

		try
		{
#if DEBUG
			const string configuration = "Debug";
#else
			const string configuration = "Release";
#endif
			var buildIdentity = GetLoadedProductBuildIdentity(pinnedNixProfile);
			string? expectedPinnedNixProjectVersion = pinnedNixProfile
				? GetPinnedNixProjectAssetsVersion(
					buildIdentity.InformationalVersion,
					buildIdentity.CommitHash)
				: null;
			string sdkRoot = Path.Combine(dotnetRoot, "sdk", PinnedDotnetSdkVersion);
			string roslynRoot = Path.Combine(sdkRoot, "Roslyn");
			var globalProperties = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["Configuration"] = configuration,
				["Version"] = buildIdentity.Version,
				["AssemblyVersion"] = buildIdentity.AssemblyVersion,
				["FileVersion"] = buildIdentity.FileVersion,
				["InformationalVersion"] = buildIdentity.InformationalVersion,
				["IncludeSourceRevisionInInformationalVersion"] = "false",
				["CommitHash"] = buildIdentity.CommitHash,
				["TargetFramework"] = "net10.0",
				["Platform"] = "AnyCPU",
				["BaseIntermediateOutputPath"] = baseIntermediateOutputPath,
				["IntermediateOutputPath"] = intermediateOutputPath,
				["BaseOutputPath"] = baseOutputPath,
				["OutputPath"] = outputPath,
				["PathMap"] = $"{generatedRoot}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{configuration}/net10.0/," +
					$"{intermediateOutputPath}=WalletWasabi/obj/{configuration}/net10.0/," +
					$"{expectedProductionRoot}{Path.DirectorySeparatorChar}=WalletWasabi",
				["DefaultExcludesInProjectFolder"] = "bin/**;obj/**;**/.*/**",
				["MSBuildProjectExtensionsPath"] = projectExtensionsPath,
				["ProjectAssetsFile"] = projectAssetsFile,
				["BuildProjectReferences"] = "false",
				["UseSharedCompilation"] = "false",
				["UseHostCompilerIfAvailable"] = "false",
				["ProvideCommandLineArgs"] = "true",
				["EmitCompilerGeneratedFiles"] = "true",
				["CompilerGeneratedFilesOutputPath"] = generatedRoot,
				["RestoreDuringBuild"] = "false",
				["RestorePackagesPath"] = packageRoot,
				["NuGetPackageRoot"] = packageRoot + Path.DirectorySeparatorChar,
				["DisableImplicitNuGetFallbackFolder"] = "true",
				["ImportDirectoryBuildProps"] = "true",
				["DirectoryBuildPropsPath"] = Path.Combine(repositoryRoot, "Directory.Build.props"),
				["ImportDirectoryBuildTargets"] = "false",
				["DirectoryBuildTargetsPath"] = "",
				["CustomBeforeDirectoryBuildProps"] = "",
				["CustomAfterDirectoryBuildProps"] = "",
				["CustomBeforeDirectoryBuildTargets"] = "",
				["CustomAfterDirectoryBuildTargets"] = "",
				["ImportProjectExtensionProps"] = "true",
				["ImportProjectExtensionTargets"] = "true",
				["ImportByWildcardBeforeMicrosoftCommonProps"] = "false",
				["ImportByWildcardAfterMicrosoftCommonProps"] = "false",
				["ImportUserLocationsByWildcardBeforeMicrosoftCommonProps"] = "false",
				["ImportUserLocationsByWildcardAfterMicrosoftCommonProps"] = "false",
				["ImportByWildcardBeforeMicrosoftCommonTargets"] = "false",
				["ImportByWildcardAfterMicrosoftCommonTargets"] = "false",
				["ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets"] = "false",
				["ImportUserLocationsByWildcardAfterMicrosoftCommonTargets"] = "false",
				["ImportByWildcardBeforeMicrosoftCSharpTargets"] = "false",
				["ImportByWildcardAfterMicrosoftCSharpTargets"] = "false",
				["ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets"] = "false",
				["ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets"] = "false",
				["CustomBeforeMicrosoftCommonProps"] = "",
				["CustomAfterMicrosoftCommonProps"] = "",
				["CustomBeforeMicrosoftCommonTargets"] = "",
				["CustomAfterMicrosoftCommonTargets"] = "",
				["CustomBeforeMicrosoftCSharpTargets"] = "",
				["CustomAfterMicrosoftCSharpTargets"] = "",
				["MSBuildUserExtensionsPath"] = disabledImportsRoot,
				["MSBuildSDKsPath"] = Path.Combine(sdkRoot, "Sdks"),
				["RoslynTargetsPath"] = roslynRoot,
				["CSharpCoreTargetsPath"] = Path.Combine(roslynRoot, "Microsoft.CSharp.Core.targets"),
				["CscToolPath"] = "",
				["CscToolExe"] = "",
				["MSBuildDisableAllAutoResponseFiles"] = "true",
			};
			var childEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["DOTNET_ROOT"] = dotnetRoot,
				["DOTNET_MULTILEVEL_LOOKUP"] = "0",
				["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
				["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
				["DOTNET_NOLOGO"] = "1",
				["MSBUILDDISABLENODEREUSE"] = "1",
				["HOME"] = childHome,
				["TMPDIR"] = childTemp,
			};
			var startInfo = new ProcessStartInfo
			{
				FileName = dotnetHost,
				WorkingDirectory = authorityRoot,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			startInfo.Environment.Clear();
			foreach ((string name, string value) in childEnvironment)
			{
				startInfo.Environment.Add(name, value);
			}
			startInfo.ArgumentList.Add("msbuild");
			startInfo.ArgumentList.Add(projectPath);
			startInfo.ArgumentList.Add("-target:Rebuild");
			startInfo.ArgumentList.Add("-noAutoResponse");
			string[] queriedProperties = new[]
			{
				"MSBuildProjectDirectory", "TargetFrameworkIdentifier", "TargetFrameworkVersion",
				"TargetFrameworks", "RuntimeIdentifier", "RuntimeIdentifiers", "NETCoreSdkVersion",
				"MSBuildVersion", "LangVersion", "DefineConstants", "AllowUnsafeBlocks",
				"MSBuildToolsPath", "CompileDependsOn", "CoreCompileDependsOn", "TargetsTriggeredByCompilation",
			}.Concat(globalProperties.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			startInfo.ArgumentList.Add("-getProperty:" + string.Join(',', queriedProperties));
			startInfo.ArgumentList.Add(
				"-getItem:Compile,Analyzer,ReferencePathWithRefAssemblies,AdditionalFiles," +
				"EditorConfigFiles,EmbeddedFiles,CscCommandLineArgs");
			startInfo.ArgumentList.Add($"-binaryLogger:{binaryLog};ProjectImports=None");
			startInfo.ArgumentList.Add("-fileLogger");
			startInfo.ArgumentList.Add(
				$"-fileLoggerParameters:LogFile={diagnosticLog};Verbosity=diagnostic;Encoding=UTF-8");
			foreach ((string name, string value) in globalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			{
				startInfo.ArgumentList.Add($"-property:{name}={EscapeMsbuildPropertyValue(value)}");
			}
			startInfo.ArgumentList.Add("-nologo");
			startInfo.ArgumentList.Add("-verbosity:quiet");
			string[] invocationArguments = startInfo.ArgumentList.ToArray();
			AssertExactChildGlobalProperties(globalProperties, CreateExpectedGlobalProperties(
				configuration,
				repositoryRoot,
				expectedProductionRoot,
				dotnetRoot,
				packageRoot,
				authorityRoot,
				buildIdentity));
			AssertExactChildEnvironment(
				startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.Ordinal),
				CreateExpectedChildEnvironment(dotnetRoot, childHome, childTemp));
			AssertExactInvocationArguments(
				invocationArguments,
				CreateExpectedInvocationArguments(
					projectPath,
					queriedProperties,
					globalProperties,
					binaryLog,
					diagnosticLog));

			using var process = new Process { StartInfo = startInfo };
			Assert.True(process.Start(), "The bound MSBuild Rebuild authority process did not start.");
			Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(TimeSpan.FromMinutes(4)))
			{
				process.Kill(entireProcessTree: true);
				throw new Xunit.Sdk.XunitException("The bound MSBuild Rebuild authority process timed out.");
			}

			string output = outputTask.GetAwaiter().GetResult();
			string error = errorTask.GetAwaiter().GetResult();
			Assert.True(
				process.ExitCode == 0,
				$"Bound MSBuild Rebuild authority failed with exit code {process.ExitCode}: {error}\n{output}");
			using JsonDocument document = JsonDocument.Parse(output);
			var properties = document.RootElement
				.GetProperty("Properties")
				.EnumerateObject()
				.ToDictionary(
					property => property.Name,
					property => property.Value.GetString() ?? "",
					StringComparer.Ordinal);
			string evaluatedProjectRoot = Path.GetFullPath(properties["MSBuildProjectDirectory"]);
			Assert.Equal(Path.GetFullPath(expectedProductionRoot), evaluatedProjectRoot);
			Assert.Equal("", properties["CscToolPath"]);

			EvaluatedBuildItem[] compileItems = ReadEvaluatedItems(document, "Compile", requireFile: true);
			Assert.DoesNotContain(compileItems, item =>
				IsPathWithin(item.FullPath, Path.Combine(evaluatedProjectRoot, "obj")) ||
				IsPathWithin(item.FullPath, Path.Combine(evaluatedProjectRoot, "bin")));
			var inputs = compileItems.Select(item => (
				item.FullPath,
				NormalizeRelativePath(Path.GetRelativePath(evaluatedProjectRoot, item.FullPath)),
				File.ReadAllText(item.FullPath))).ToArray();
			EvaluatedBuildItem[] analyzerItems = ReadEvaluatedItems(document, "Analyzer", requireFile: true);
			var analyzers = analyzerItems.Select(item =>
			{
				Assert.False(string.IsNullOrWhiteSpace(item.DefiningProjectFullPath));
				Assert.True(File.Exists(item.DefiningProjectFullPath));
				return (item.FullPath, item.DefiningProjectFullPath);
			}).ToArray();
			EvaluatedBuildItem[] referencePaths =
				ReadEvaluatedItems(document, "ReferencePathWithRefAssemblies", requireFile: true);
			EvaluatedBuildItem[] additionalFiles =
				ReadEvaluatedItems(document, "AdditionalFiles", requireFile: true);
			EvaluatedBuildItem[] editorConfigFiles =
				ReadEvaluatedItems(document, "EditorConfigFiles", requireFile: true);
			EvaluatedBuildItem[] embeddedFiles =
				ReadEvaluatedItems(document, "EmbeddedFiles", requireFile: true);
			string[] cscCommandLineArgs = ReadEvaluatedItems(
				document,
				"CscCommandLineArgs",
				requireFile: false).Select(item => item.Identity).ToArray();
			Assert.NotEmpty(cscCommandLineArgs);
			AssertCompilerArgumentsCoverInputs(
				cscCommandLineArgs,
				evaluatedProjectRoot,
				compileItems,
				analyzerItems,
				referencePaths,
				additionalFiles,
				editorConfigFiles,
				embeddedFiles);

			GeneratedBuildFile[] generatedSources = Directory
				.EnumerateFiles(generatedRoot, "*", SearchOption.AllDirectories)
				.Select(path => new GeneratedBuildFile(
					NormalizeRelativePath(Path.GetRelativePath(generatedRoot, Path.GetFullPath(path))),
					Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
						? File.ReadAllText(path)
						: "",
					Sha256File(path)))
				.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
				.ToArray();
			Assert.NotEmpty(generatedSources);
			Assert.Contains(
				generatedSources,
				generated => generated.RelativePath.Contains(
					"System.Text.RegularExpressions.Generator",
					StringComparison.Ordinal));

			Assert.True(File.Exists(binaryLog), "The single Rebuild did not produce its binary evaluation trace.");
			Assert.True(new FileInfo(binaryLog).Length > 0, "The binary evaluation trace is empty.");
			Assert.True(File.Exists(diagnosticLog), "The single Rebuild did not produce its diagnostic trace.");
			CompilerAuthorityEntry[] diagnosticCscAuthorityEntries = AssertCscDiagnosticAuthority(
				File.ReadAllText(diagnosticLog),
				dotnetRoot,
				generatedRoot,
				intermediateOutputPath);
			BinaryBuildTrace binaryTrace = ReadAndAssertBinaryBuildTrace(
				binaryLog,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				authorityRoot,
				projectPath,
				projectAssetsFile,
				expectedPinnedNixProjectVersion);
			Assert.Equal(cscCommandLineArgs, binaryTrace.CommandLineArgs);
			AssertCscTaskInputsMatchArguments(binaryTrace, evaluatedProjectRoot);
			string[] importedProjects = binaryTrace.ImportedProjects;
			string importClosureManifest = binaryTrace.ImportManifest;
			string referenceAuthorityManifest = BuildReferenceAuthorityManifest(
				referencePaths,
				repositoryRoot,
				dotnetRoot,
				packageAuthority);
			string compilerInputAuthorityManifest = BuildCompilerInputAuthorityManifest(
				cscCommandLineArgs,
				compileItems,
				analyzerItems,
				referencePaths,
				additionalFiles,
				editorConfigFiles,
				embeddedFiles,
				evaluatedProjectRoot,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				authorityRoot,
				generatedRoot,
				intermediateOutputPath,
				buildIdentity.CommitHash,
				diagnosticCscAuthorityEntries,
				binaryTrace.CscAuthorityEntries);
			string toolchainAuthorityManifest =
				BuildToolchainAuthorityManifest(dotnetHost, dotnetRoot) +
				BuildPackageTransportAuthorityManifest(
					Path.Combine(repositoryRoot, "WalletWasabi/packages.lock.json"),
					"net10.0") +
				BuildPackageMaterializationAuthorityManifest(projectAssetsFile, packageAuthority) +
				BuildPackagePayloadAuthorityManifest(projectAssetsFile, packageAuthority);
			AssertConfiguredAuthorityHashes(
				importClosureManifest,
				referenceAuthorityManifest,
				compilerInputAuthorityManifest,
				toolchainAuthorityManifest);

			string rebuiltAssembly = Path.Combine(outputPath, "WalletWasabi.dll");
			string loadedAssembly = Path.GetFullPath(typeof(LiquidOrdinaryWalletPlanEncoder).Assembly.Location);
			Assert.True(File.Exists(rebuiltAssembly), $"Isolated Rebuild output is absent: {rebuiltAssembly}");
			byte[] loadedAssemblyBytes = File.ReadAllBytes(loadedAssembly);
			byte[] rebuiltAssemblyBytes = File.ReadAllBytes(rebuiltAssembly);
			AssertExactArtifactBytes(loadedAssemblyBytes, rebuiltAssemblyBytes);
			byte[] swappedAssemblyBytes = rebuiltAssemblyBytes.ToArray();
			swappedAssemblyBytes[^1] ^= 1;
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertExactArtifactBytes(loadedAssemblyBytes, swappedAssemblyBytes));
			string rebuiltPdb = Path.Combine(outputPath, "WalletWasabi.pdb");
			string loadedPdb = Path.ChangeExtension(loadedAssembly, ".pdb");
			Assert.True(File.Exists(rebuiltPdb), $"Isolated Rebuild PDB is absent: {rebuiltPdb}");
			Assert.True(File.Exists(loadedPdb), $"Loaded audited PDB is absent: {loadedPdb}");
			AssertExactArtifactBytes(File.ReadAllBytes(loadedPdb), File.ReadAllBytes(rebuiltPdb));
			string outputAssemblySha256 = Sha256File(rebuiltAssembly);

			return new ProductionBuildAuthority(
				properties,
				globalProperties,
				childEnvironment,
				invocationArguments,
				inputs,
				analyzers,
				referencePaths,
				additionalFiles,
				editorConfigFiles,
				embeddedFiles,
				cscCommandLineArgs,
				generatedSources,
				importedProjects,
				importClosureManifest,
				referenceAuthorityManifest,
				compilerInputAuthorityManifest,
				toolchainAuthorityManifest,
				outputAssemblySha256,
				dotnetHost,
				dotnetRoot,
				repositoryRoot,
				packageAuthority,
				authorityRoot,
				generatedRoot,
				InjectedAnalyzerTargetContent);
		}
		finally
		{
			Directory.Delete(authorityRoot, recursive: true);
		}
	}

	private static (string PrimaryRoot, string[] OrderedRoots) GetPinnedPackageAuthority(string projectAssetsFile)
	{
		AssertRegularAuthorityFile(projectAssetsFile, "project assets authority");
		using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(projectAssetsFile));
		JsonElement root = assets.RootElement;
		Assert.Equal(JsonValueKind.Object, root.ValueKind);
		Assert.True(root.TryGetProperty("project", out JsonElement project));
		Assert.Equal(JsonValueKind.Object, project.ValueKind);
		Assert.True(project.TryGetProperty("restore", out JsonElement restore));
		Assert.Equal(JsonValueKind.Object, restore.ValueKind);
		Assert.True(restore.TryGetProperty("packagesPath", out JsonElement packagesPath));
		Assert.Equal(JsonValueKind.String, packagesPath.ValueKind);
		string primaryRoot = ParseCanonicalPackageRoot(
			Assert.IsType<string>(packagesPath.GetString()),
			"primary package root");
		Assert.True(root.TryGetProperty("packageFolders", out JsonElement packageFolders));
		Assert.Equal(JsonValueKind.Object, packageFolders.ValueKind);
		var orderedRoots = new List<string>();
		foreach (JsonProperty property in packageFolders.EnumerateObject())
		{
			Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
			Assert.Empty(property.Value.EnumerateObject());
			orderedRoots.Add(ParseCanonicalPackageRoot(property.Name, "declared package root"));
		}
		Assert.NotEmpty(orderedRoots);
		Assert.Equal(primaryRoot, orderedRoots[0]);
		var uniqueRoots = new HashSet<string>(PackagePathComparer);
		foreach (string packageRoot in orderedRoots)
		{
			Assert.True(uniqueRoots.Add(packageRoot), $"Duplicate declared package root: {packageRoot}");
		}
		for (int first = 0; first < orderedRoots.Count; first++)
		{
			for (int second = first + 1; second < orderedRoots.Count; second++)
			{
				Assert.False(
					IsPathWithin(orderedRoots[first], orderedRoots[second]) ||
					IsPathWithin(orderedRoots[second], orderedRoots[first]),
					$"Declared package roots overlap: {orderedRoots[first]} and {orderedRoots[second]}");
			}
		}
		return (primaryRoot, orderedRoots.ToArray());
	}

	private static IReadOnlyDictionary<string, string> CreateExpectedGlobalProperties(
		string configuration,
		string repositoryRoot,
		string productionRoot,
		string dotnetRoot,
		string packageRoot,
		string authorityRoot,
		(string Version, string AssemblyVersion, string FileVersion, string InformationalVersion, string CommitHash)
			buildIdentity)
	{
		string sdkRoot = Path.Combine(dotnetRoot, "sdk", PinnedDotnetSdkVersion);
		string roslynRoot = Path.Combine(sdkRoot, "Roslyn");
		return new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["Configuration"] = configuration,
			["Version"] = buildIdentity.Version,
			["AssemblyVersion"] = buildIdentity.AssemblyVersion,
			["FileVersion"] = buildIdentity.FileVersion,
			["InformationalVersion"] = buildIdentity.InformationalVersion,
			["IncludeSourceRevisionInInformationalVersion"] = "false",
			["CommitHash"] = buildIdentity.CommitHash,
			["TargetFramework"] = "net10.0",
			["Platform"] = "AnyCPU",
			["BaseIntermediateOutputPath"] = Path.Combine(authorityRoot, "obj") + Path.DirectorySeparatorChar,
			["IntermediateOutputPath"] = Path.Combine(authorityRoot, "obj/net10.0") + Path.DirectorySeparatorChar,
			["BaseOutputPath"] = Path.Combine(authorityRoot, "base-bin") + Path.DirectorySeparatorChar,
			["OutputPath"] = Path.Combine(authorityRoot, "bin") + Path.DirectorySeparatorChar,
			["PathMap"] = $"{Path.Combine(authorityRoot, "generated")}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{configuration}/net10.0/," +
				$"{Path.Combine(authorityRoot, "obj/net10.0")}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{configuration}/net10.0/," +
				$"{productionRoot}{Path.DirectorySeparatorChar}=WalletWasabi",
			["DefaultExcludesInProjectFolder"] = "bin/**;obj/**;**/.*/**",
			["MSBuildProjectExtensionsPath"] = Path.Combine(productionRoot, "obj") + Path.DirectorySeparatorChar,
			["ProjectAssetsFile"] = Path.Combine(productionRoot, "obj/project.assets.json"),
			["BuildProjectReferences"] = "false",
			["UseSharedCompilation"] = "false",
			["UseHostCompilerIfAvailable"] = "false",
			["ProvideCommandLineArgs"] = "true",
			["EmitCompilerGeneratedFiles"] = "true",
			["CompilerGeneratedFilesOutputPath"] = Path.Combine(authorityRoot, "generated"),
			["RestoreDuringBuild"] = "false",
			["RestorePackagesPath"] = packageRoot,
			["NuGetPackageRoot"] = packageRoot + Path.DirectorySeparatorChar,
			["DisableImplicitNuGetFallbackFolder"] = "true",
			["ImportDirectoryBuildProps"] = "true",
			["DirectoryBuildPropsPath"] = Path.Combine(repositoryRoot, "Directory.Build.props"),
			["ImportDirectoryBuildTargets"] = "false",
			["DirectoryBuildTargetsPath"] = "",
			["CustomBeforeDirectoryBuildProps"] = "",
			["CustomAfterDirectoryBuildProps"] = "",
			["CustomBeforeDirectoryBuildTargets"] = "",
			["CustomAfterDirectoryBuildTargets"] = "",
			["ImportProjectExtensionProps"] = "true",
			["ImportProjectExtensionTargets"] = "true",
			["ImportByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["CustomBeforeMicrosoftCommonProps"] = "",
			["CustomAfterMicrosoftCommonProps"] = "",
			["CustomBeforeMicrosoftCommonTargets"] = "",
			["CustomAfterMicrosoftCommonTargets"] = "",
			["CustomBeforeMicrosoftCSharpTargets"] = "",
			["CustomAfterMicrosoftCSharpTargets"] = "",
			["MSBuildUserExtensionsPath"] = Path.Combine(authorityRoot, "disabled-imports"),
			["MSBuildSDKsPath"] = Path.Combine(sdkRoot, "Sdks"),
			["RoslynTargetsPath"] = roslynRoot,
			["CSharpCoreTargetsPath"] = Path.Combine(roslynRoot, "Microsoft.CSharp.Core.targets"),
			["CscToolPath"] = "",
			["CscToolExe"] = "",
			["MSBuildDisableAllAutoResponseFiles"] = "true",
		};
	}

	private static IReadOnlyDictionary<string, string> CreateExpectedChildEnvironment(
		string dotnetRoot,
		string childHome,
		string childTemp) =>
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["DOTNET_ROOT"] = dotnetRoot,
			["DOTNET_MULTILEVEL_LOOKUP"] = "0",
			["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
			["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
			["DOTNET_NOLOGO"] = "1",
			["MSBUILDDISABLENODEREUSE"] = "1",
			["HOME"] = childHome,
			["TMPDIR"] = childTemp,
		};

	private static string[] CreateExpectedInvocationArguments(
		string projectPath,
		IReadOnlyList<string> queriedProperties,
		IReadOnlyDictionary<string, string> globalProperties,
		string binaryLog,
		string diagnosticLog)
	{
		var result = new List<string>
		{
			"msbuild",
			projectPath,
			"-target:Rebuild",
			"-noAutoResponse",
			"-getProperty:" + string.Join(',', queriedProperties),
			"-getItem:Compile,Analyzer,ReferencePathWithRefAssemblies,AdditionalFiles," +
				"EditorConfigFiles,EmbeddedFiles,CscCommandLineArgs",
			$"-binaryLogger:{binaryLog};ProjectImports=None",
			"-fileLogger",
			$"-fileLoggerParameters:LogFile={diagnosticLog};Verbosity=diagnostic;Encoding=UTF-8",
		};
		result.AddRange(globalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => $"-property:{pair.Key}={EscapeMsbuildPropertyValue(pair.Value)}"));
		result.Add("-nologo");
		result.Add("-verbosity:quiet");
		return result.ToArray();
	}

	private static EvaluatedBuildItem[] ReadEvaluatedItems(
		JsonDocument document,
		string itemName,
		bool requireFile)
	{
		var result = new List<EvaluatedBuildItem>();
		foreach (JsonElement item in document.RootElement
			.GetProperty("Items")
			.GetProperty(itemName)
			.EnumerateArray())
		{
			string identity = item.GetProperty("Identity").GetString() ?? "";
			Assert.False(string.IsNullOrWhiteSpace(identity), $"{itemName} has an empty identity.");
			var metadata = item.EnumerateObject()
				.Where(property => property.Name != "Identity")
				.ToDictionary(
					property => property.Name,
					property => property.Value.GetString() ?? property.Value.ToString(),
					StringComparer.Ordinal);
			string fullPath = metadata.TryGetValue("FullPath", out string? capturedFullPath) &&
				!string.IsNullOrWhiteSpace(capturedFullPath)
					? Path.GetFullPath(capturedFullPath)
					: "";
			if (requireFile)
			{
				Assert.False(string.IsNullOrWhiteSpace(fullPath), $"{itemName} has no FullPath: {identity}");
				Assert.True(File.Exists(fullPath), $"{itemName} input does not exist: {fullPath}");
			}
			string definingProject = metadata.TryGetValue(
				"DefiningProjectFullPath",
				out string? capturedDefiningProject) && !string.IsNullOrWhiteSpace(capturedDefiningProject)
					? Path.GetFullPath(capturedDefiningProject)
					: "";
			result.Add(new EvaluatedBuildItem(identity, fullPath, definingProject, metadata));
		}

		return result.ToArray();
	}

	private static void AssertCompilerArgumentsCoverInputs(
		IReadOnlyList<string> arguments,
		string projectRoot,
		params EvaluatedBuildItem[][] inventories)
	{
		Assert.NotEmpty(arguments);
		Assert.DoesNotContain(arguments, argument => argument.StartsWith('@'));
		Assert.Equal(
			inventories[0].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "source").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[1].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/analyzer:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[2].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/reference:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[3].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/additionalfile:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[4].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/analyzerconfig:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		string[] embeddedArguments = GetCompilerArgumentPaths(arguments, projectRoot, "/embed:");
		Assert.All(inventories[5], item =>
		{
			string expected = NormalizeMacTemporaryAlias(item.FullPath);
			string[] actual = embeddedArguments.Select(NormalizeMacTemporaryAlias).ToArray();
			Assert.True(
				actual.Contains(expected, StringComparer.Ordinal),
				$"Expected embed {Convert.ToHexString(Encoding.UTF8.GetBytes(expected))}; actual " +
				string.Join(',', actual.Select(value => Convert.ToHexString(Encoding.UTF8.GetBytes(value)))));
		});
	}

	private static string[] GetCompilerArgumentPaths(
		IEnumerable<string> arguments,
		string projectRoot,
		string category)
	{
		var result = new List<string>();
		foreach (string raw in arguments)
		{
			string argument = raw.Trim().Trim('"');
			string[] values;
			if (category == "source")
			{
				if (!argument.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
					argument.StartsWith("/reference:", StringComparison.Ordinal) ||
					argument.StartsWith("/analyzer:", StringComparison.Ordinal) ||
					argument.StartsWith("/additionalfile:", StringComparison.Ordinal) ||
					argument.StartsWith("/analyzerconfig:", StringComparison.Ordinal) ||
					argument.StartsWith("/embed:", StringComparison.Ordinal))
				{
					continue;
				}
				values = [argument];
			}
			else
			{
				if (!argument.StartsWith(category, StringComparison.Ordinal))
				{
					continue;
				}
				string valueList = argument[category.Length..];
				values = category is "/reference:" or "/analyzer:"
					? valueList.Split(',', StringSplitOptions.RemoveEmptyEntries)
					: [valueList];
			}

			foreach (string rawValue in values)
			{
				string value = rawValue.Trim().Trim('"');
				if (category == "/reference:" && value.Contains('='))
				{
					value = value[(value.IndexOf('=') + 1)..];
				}
				result.Add(Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectRoot, value)));
			}
		}
		return result.ToArray();
	}

	private static string NormalizeMacTemporaryAlias(string path) =>
		OperatingSystem.IsMacOS() && path.StartsWith("/private/var/", StringComparison.Ordinal)
			? path[8..]
			: path;

	private static CompilerAuthorityEntry[] AssertCscDiagnosticAuthority(
		string diagnostic,
		string dotnetRoot,
		string generatedRoot,
		string intermediateOutputPath)
	{
		Match[] taskAssemblies = Regex.Matches(
			diagnostic,
			"Using \"Csc\" task from assembly \"(?<path>[^\"]+)\"\\.")
			.Cast<Match>()
			.ToArray();
		Match taskAssemblyMatch = Assert.Single(taskAssemblies);
		string taskAssembly = Path.GetFullPath(taskAssemblyMatch.Groups["path"].Value);
		Assert.Equal(
			Path.Combine(
				dotnetRoot,
				"sdk",
				PinnedDotnetSdkVersion,
				"Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll"),
			taskAssembly);
		Match[] starts = Regex.Matches(diagnostic, "Task \"Csc\" \\(TaskId:(?<id>[0-9]+)\\)")
			.Cast<Match>()
			.ToArray();
		string taskId = Assert.Single(starts).Groups["id"].Value;
		Assert.Single(Regex.Matches(
			diagnostic,
			$"Done executing task \"Csc\"\\. \\(TaskId:{taskId}\\)").Cast<Match>());
		string csc = Path.Combine(dotnetRoot, "sdk", PinnedDotnetSdkVersion, "Roslyn/bincore/csc");
		string[] requiredParameters =
		[
			$"Task Parameter:GeneratedFilesOutputPath={generatedRoot} (TaskId:{taskId})",
			$"Task Parameter:UseSharedCompilation=False (TaskId:{taskId})",
			$"Task Parameter:ProvideCommandLineArgs=True (TaskId:{taskId})",
			$"Task Parameter:UseHostCompilerIfAvailable=False (TaskId:{taskId})",
			$"Task Parameter:OutputAssembly={Path.Combine(intermediateOutputPath, "WalletWasabi.dll")} (TaskId:{taskId})",
			$"Setting DOTNET_ROOT to '{dotnetRoot}' (TaskId:{taskId})",
			$"CompilerServer: tool - using command line tool by design '{csc}' - WalletWasabi (net10.0) (TaskId:{taskId})",
		];
		Assert.All(requiredParameters, expected => Assert.Contains(expected, diagnostic, StringComparison.Ordinal));
		Assert.DoesNotContain("NUGET_PACKAGES=", diagnostic, StringComparison.OrdinalIgnoreCase);
		Assert.Single(diagnostic.Split('\n'), line =>
			line.TrimStart().StartsWith(csc + " /noconfig ", StringComparison.Ordinal) &&
			line.Contains($"(TaskId:{taskId})", StringComparison.Ordinal));
		var entries = new List<CompilerAuthorityEntry>
		{
			CreateCompilerAuthorityEntry(
				"DIAGNOSTIC_TASK",
				"DOTNET|" + NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, taskAssembly)),
				sha256: Sha256File(taskAssembly)),
			CreateCompilerAuthorityEntry(
				"DIAGNOSTIC_COMPILER",
				"DOTNET|" + NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, csc)),
				sha256: Sha256File(csc)),
		};
		entries.AddRange(requiredParameters.Select(parameter => CreateCompilerAuthorityEntry(
			"DIAGNOSTIC_PARAMETER",
			NormalizeCompilerAuthorityString(
				parameter,
				("{DOTNET}", dotnetRoot),
				("{GENERATED}", generatedRoot),
				("{INTERMEDIATE}", intermediateOutputPath))
				.Replace($"TaskId:{taskId}", "TaskId:{TASK}", StringComparison.Ordinal))));
		return entries.ToArray();
	}

	private static BinaryBuildTrace ReadAndAssertBinaryBuildTrace(
		string binaryLog,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string authorityRoot,
		string projectPath,
		string projectAssetsFile,
		string? expectedPinnedNixProjectVersion)
	{
		string packagesLockFile = Path.Combine(
			Path.GetDirectoryName(projectPath)!,
			"packages.lock.json");
		const string ExpectedTargetFramework = "net10.0";
		Assert.True(File.Exists(binaryLog), "The Rebuild binary trace is absent.");
		var starts = new Dictionary<BuildContextKey, TaskStartedEventArgs>();
		var finishes = new Dictionary<BuildContextKey, TaskFinishedEventArgs>();
		var parameters = new Dictionary<BuildContextKey, List<TaskParameterEventArgs>>();
		var imports = new List<ProjectImportedEventArgs>();
		var errors = new List<string>();
		using var compressedLog = new GZipStream(File.OpenRead(binaryLog), CompressionMode.Decompress);
		using var binaryReader = new BinaryReader(compressedLog, Encoding.UTF8, leaveOpen: false);
		using BuildEventArgsReader reader = BinaryLogReplayEventSource.OpenBuildEventsReader(
			binaryReader,
			closeInput: true,
			allowForwardCompatibility: false);
		reader.SkipUnknownEvents = false;
		reader.SkipUnknownEventParts = false;
		reader.RecoverableReadError += error => errors.Add(error.ToString() ?? "Unknown recoverable binlog error.");
		while (reader.Read() is BuildEventArgs buildEvent)
		{
			switch (buildEvent)
			{
				case ProjectImportedEventArgs imported:
					imports.Add(imported);
					break;
				case TaskStartedEventArgs started when StringComparer.Ordinal.Equals(started.TaskName, "Csc"):
					starts.Add(GetBuildContext(started), started);
					break;
				case TaskParameterEventArgs parameter:
					BuildContextKey parameterContext = GetBuildContext(parameter);
					if (!parameters.TryGetValue(parameterContext, out List<TaskParameterEventArgs>? values))
					{
						values = [];
						parameters.Add(parameterContext, values);
					}
					values.Add(parameter);
					break;
				case TaskFinishedEventArgs finished when StringComparer.Ordinal.Equals(finished.TaskName, "Csc"):
					finishes.Add(GetBuildContext(finished), finished);
					break;
			}
		}
		Assert.Empty(errors);
		BuildContextKey cscContext = Assert.Single(starts.Keys);
		TaskStartedEventArgs cscStart = starts[cscContext];
		TaskFinishedEventArgs cscFinish = Assert.Single(finishes, pair => pair.Key == cscContext).Value;
		Assert.True(cscFinish.Succeeded, "The exact Csc task captured in the Rebuild trace did not succeed.");
		string expectedTaskAssembly = Path.Combine(
			dotnetRoot,
			"sdk/10.0.100/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll");
		Assert.Equal(expectedTaskAssembly, Path.GetFullPath(cscStart.TaskAssemblyLocation));
		Assert.Equal(Path.GetFullPath(projectPath), Path.GetFullPath(cscStart.ProjectFile));
		TaskParameterEventArgs[] cscParameters = parameters.GetValueOrDefault(cscContext, []).ToArray();
		TaskParameterEventArgs commandLine = Assert.Single(cscParameters, parameter =>
			parameter.Kind == TaskParameterMessageKind.TaskOutput &&
			StringComparer.Ordinal.Equals(parameter.ParameterName, "CommandLineArgs") &&
			StringComparer.Ordinal.Equals(parameter.ItemType, "CscCommandLineArgs"));
		string[] orderedArgs = commandLine.Items.Cast<object>().Select(GetBuildItemSpec).ToArray();
		Assert.NotEmpty(orderedArgs);
		TaskParameterEventArgs[] inputs = cscParameters
			.Where(parameter => parameter.Kind == TaskParameterMessageKind.TaskInput)
			.ToArray();
		Assert.NotEmpty(inputs);
		var taskInputs = inputs
			.Where(input => input.Items.Cast<object>().Any())
			.GroupBy(input => input.ParameterName ?? input.PropertyName ?? input.ItemType ?? "", StringComparer.Ordinal)
			.ToDictionary(
				group => group.Key,
				group => Assert.Single(group).Items.Cast<object>().Select(GetBuildItemSpec).ToArray(),
				StringComparer.Ordinal);
		Assert.Contains("Sources", taskInputs.Keys);
		Assert.Contains("Analyzers", taskInputs.Keys);
		Assert.Contains("References", taskInputs.Keys);

		var paths = new List<string>();
		var rows = new List<string>();
		for (int index = 0; index < imports.Count; index++)
		{
			ProjectImportedEventArgs imported = imports[index];
			string rawUnexpandedProject = Assert.IsType<string>(imported.UnexpandedProject);
			Assert.False(string.IsNullOrWhiteSpace(rawUnexpandedProject));
			string unexpandedProject = NormalizeAndValidateUnexpandedImportProject(
				rawUnexpandedProject,
				packageAuthority,
				repositoryRoot,
				dotnetRoot,
				authorityRoot);
			string sourcePath = Assert.IsType<string>(imported.ProjectFile);
			Assert.False(string.IsNullOrWhiteSpace(sourcePath));
			sourcePath = NormalizeAuthorityPath(sourcePath, repositoryRoot, dotnetRoot, packageAuthority);

			string resolvedState;
			string resolvedPath;
			string resolvedSha256;
			if (imported.ImportedProjectFile is null)
			{
				resolvedState = "null";
				resolvedPath = "";
				resolvedSha256 = "";
			}
			else if (imported.ImportedProjectFile.Length == 0)
			{
				resolvedState = "empty";
				resolvedPath = "";
				resolvedSha256 = "";
			}
			else
			{
				Assert.False(string.IsNullOrWhiteSpace(imported.ImportedProjectFile));
				string path = Path.GetFullPath(imported.ImportedProjectFile);
				AssertRegularAuthorityFile(path, "captured import");
				paths.Add(path);
				resolvedState = "file";
				resolvedPath = NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority);
				resolvedSha256 = GetBuildAuthorityFileSha256(
					path,
					projectAssetsFile,
					packagesLockFile,
					ExpectedTargetFramework,
					repositoryRoot,
					dotnetRoot,
					packageAuthority,
					expectedPinnedNixProjectVersion);
			}
			rows.Add(BuildCanonicalImportManifestRow(
				"IMPORT_EVENT_V2",
				[
					index.ToString(System.Globalization.CultureInfo.InvariantCulture),
					imported.ImportIgnored ? "1" : "0",
					unexpandedProject,
					sourcePath,
					imported.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
					imported.ColumnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
					resolvedState,
					resolvedPath,
					resolvedSha256,
				]));
		}
		Assert.NotEmpty(imports);

		string[] independentlyPinned =
		[
			Path.Combine(repositoryRoot, "global.json"),
			Path.Combine(repositoryRoot, "Directory.Build.props"),
			Path.Combine(repositoryRoot, "Directory.Packages.props"),
			projectPath,
			packagesLockFile,
			projectAssetsFile,
			Path.Combine(Path.GetDirectoryName(projectAssetsFile)!, "WalletWasabi.csproj.nuget.g.props"),
			Path.Combine(Path.GetDirectoryName(projectAssetsFile)!, "WalletWasabi.csproj.nuget.g.targets"),
		];
		foreach (string path in independentlyPinned)
		{
			AssertRegularAuthorityFile(path, "pinned build-authority file");
			rows.Add(BuildCanonicalImportManifestRow(
				"PIN_V2",
				[
					NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority),
					GetBuildAuthorityFileSha256(
						path,
						projectAssetsFile,
						packagesLockFile,
						ExpectedTargetFramework,
						repositoryRoot,
						dotnetRoot,
						packageAuthority,
						expectedPinnedNixProjectVersion),
				]));
		}
		CompilerAuthorityEntry[] cscInputEntries = inputs
			.OrderBy(input => input.ParameterName ?? "", StringComparer.Ordinal)
			.ThenBy(input => input.PropertyName ?? "", StringComparer.Ordinal)
			.ThenBy(input => input.ItemType ?? "", StringComparer.Ordinal)
			.Select(input => CreateCompilerAuthorityEntry(
				"CSC_INPUT",
				input.ParameterName ?? "",
				detail: input.PropertyName ?? "",
				qualifier: input.ItemType ?? "",
				values: JsonSerializer.Serialize(input.Items.Cast<object>().Select(item =>
					NormalizeCompilerAuthorityStringWithPackages(
						GetBuildItemSpec(item),
						packageAuthority,
						("{REPO}", repositoryRoot),
						("{DOTNET}", dotnetRoot),
						("{AUTHORITY}", authorityRoot))).ToArray())))
			.ToArray();
		CompilerAuthorityEntry[] cscArgumentEntries = orderedArgs.Select(argument =>
			CreateCompilerAuthorityEntry(
				"CSC_ARG",
				NormalizeCompilerAuthorityStringWithPackages(
					argument,
					packageAuthority,
					("{REPO}", repositoryRoot),
					("{DOTNET}", dotnetRoot),
					("{AUTHORITY}", authorityRoot)))).ToArray();
		var cscAuthorityEntries = new List<CompilerAuthorityEntry>
		{
			CreateCompilerAuthorityEntry(
				"CSC_START",
				NormalizeAuthorityPath(
					cscStart.TaskAssemblyLocation,
					repositoryRoot,
					dotnetRoot,
					packageAuthority),
				sha256: Sha256File(cscStart.TaskAssemblyLocation)),
		};
		cscAuthorityEntries.AddRange(cscInputEntries);
		cscAuthorityEntries.AddRange(cscArgumentEntries);
		return new BinaryBuildTrace(
			orderedArgs,
			taskInputs,
			paths.ToArray(),
			"IMPORT_AUTHORITY_V2\n" + string.Join('\n', rows) + "\n",
			cscAuthorityEntries.ToArray());
	}

	private static string GetBuildItemSpec(object item)
	{
		if (item is ITaskItem taskItem)
		{
			return taskItem.ItemSpec;
		}
		PropertyInfo? itemSpec = item.GetType().GetProperty("ItemSpec", BindingFlags.Public | BindingFlags.Instance);
		return itemSpec?.GetValue(item)?.ToString() ??
			throw new Xunit.Sdk.XunitException($"Build trace item exposes no ItemSpec: {item.GetType().FullName}");
	}

	private static void AssertCscTaskInputsMatchArguments(BinaryBuildTrace trace, string projectRoot)
	{
		foreach ((string parameter, string category) in new[]
		{
			("Sources", "source"),
			("Analyzers", "/analyzer:"),
			("References", "/reference:"),
			("AdditionalFiles", "/additionalfile:"),
			("AnalyzerConfigFiles", "/analyzerconfig:"),
			("EmbeddedFiles", "/embed:"),
		})
		{
			string[] expected = trace.TaskInputs.TryGetValue(parameter, out string[]? items)
				? items.Select(item => Path.GetFullPath(
					Path.IsPathRooted(item.Trim('"'))
						? item.Trim('"')
						: Path.Combine(projectRoot, item.Trim('"')))).ToArray()
				: [];
			Assert.Equal(
				expected.Order(StringComparer.Ordinal),
				GetCompilerArgumentPaths(trace.CommandLineArgs, projectRoot, category).Order(StringComparer.Ordinal));
		}
	}

	private static BuildContextKey GetBuildContext(BuildEventArgs buildEvent)
	{
		BuildEventContext context = buildEvent.BuildEventContext ??
			throw new Xunit.Sdk.XunitException("Build event has no context.");
		return new BuildContextKey(
			context.NodeId,
			context.ProjectContextId,
			context.TargetId,
			context.TaskId,
			context.SubmissionId,
			context.ProjectInstanceId,
			context.EvaluationId);
	}

	private static string NormalizeOptionalAuthorityPath(
		string? path,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority) =>
		string.IsNullOrWhiteSpace(path)
			? "EMPTY"
			: NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority);

	private static string BuildReferenceAuthorityManifest(
		IEnumerable<EvaluatedBuildItem> references,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string[] rows = references.Select((reference, index) =>
		{
			string provenance = string.IsNullOrEmpty(reference.DefiningProjectFullPath)
				? "NONE"
				: NormalizeAuthorityPath(
					reference.DefiningProjectFullPath,
					repositoryRoot,
					dotnetRoot,
					packageAuthority);
			return BuildCanonicalAuthorityManifestRow(
				"REFERENCE_V2",
				[
					index.ToString(System.Globalization.CultureInfo.InvariantCulture),
					NormalizeAuthorityPath(reference.FullPath, repositoryRoot, dotnetRoot, packageAuthority),
					Sha256File(reference.FullPath),
					provenance,
					reference.Metadata.TryGetValue("Aliases", out string? aliases) ? aliases : "",
				]);
		}).ToArray();
		Assert.NotEmpty(rows);
		return "REFERENCE_AUTHORITY_V2\n" + string.Join('\n', rows) + "\n";
	}

	private static string BuildCompilerInputAuthorityManifest(
		IReadOnlyList<string> arguments,
		EvaluatedBuildItem[] compile,
		EvaluatedBuildItem[] analyzers,
		EvaluatedBuildItem[] references,
		EvaluatedBuildItem[] additionalFiles,
		EvaluatedBuildItem[] editorConfigs,
		EvaluatedBuildItem[] embeddedFiles,
		string projectRoot,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string authorityRoot,
		string generatedRoot,
		string intermediateRoot,
		string commitHash,
		CompilerAuthorityEntry[] diagnosticCscAuthorityEntries,
		CompilerAuthorityEntry[] binaryCscAuthorityEntries)
	{
		var entries = arguments.Select(argument => CreateCompilerAuthorityEntry(
			"ARG",
			NormalizeCompilerAuthorityStringWithPackages(
				argument,
				packageAuthority,
				("{REPO}", repositoryRoot),
				("{DOTNET}", dotnetRoot),
				("{AUTHORITY}", authorityRoot))))
			.ToList();
		foreach ((string category, EvaluatedBuildItem[] items) in new[]
		{
			("SOURCE", compile),
			("ANALYZER", analyzers),
			("REFERENCE", references),
			("ADDITIONAL", additionalFiles),
			("ANALYZERCONFIG", editorConfigs),
			("EMBED", embeddedFiles),
		})
		{
			entries.AddRange(items.Select(item =>
			{
				string identity = NormalizeAuthorityPath(
					item.FullPath,
					repositoryRoot,
					dotnetRoot,
					packageAuthority,
					authorityRoot);
				return CreateCompilerAuthorityEntry(
					category,
					identity,
					sha256: GetCompilerInputAuthoritySha256(
						category,
						identity,
						item.FullPath,
						projectRoot,
						repositoryRoot,
						authorityRoot,
						generatedRoot,
						intermediateRoot,
						commitHash));
			}));
		}

		foreach (string analyzerDirectory in analyzers
			.Select(item => Path.GetDirectoryName(item.FullPath)!)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal))
		{
			entries.AddRange(Directory.EnumerateFiles(analyzerDirectory, "*.dll", SearchOption.TopDirectoryOnly)
				.Order(StringComparer.Ordinal)
				.Select(path => CreateCompilerAuthorityEntry(
					"ANALYZER_DEP",
					NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority),
					sha256: Sha256File(path))));
		}

		foreach (string argument in arguments)
		{
			entries.AddRange(CreateCompilerAuxiliaryAuthorityEntries(
				argument,
				projectRoot,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				authorityRoot,
				commitHash));
		}
		entries.AddRange(diagnosticCscAuthorityEntries);
		entries.AddRange(binaryCscAuthorityEntries);
		return BuildCanonicalCompilerInputAuthorityManifest(entries);
	}

	private static CompilerAuthorityEntry CreateCompilerAuthorityEntry(
		string section,
		string identity,
		string detail = "",
		string qualifier = "",
		string values = "",
		string sha256 = "") =>
		new(section, identity, detail, qualifier, values, sha256);

	private static CompilerAuthorityEntry[] CreateCompilerAuxiliaryAuthorityEntries(
		string argument,
		string projectRoot,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string authorityRoot,
		string commitHash)
	{
		string? prefix = CompilerAuxiliaryPrefixes.FirstOrDefault(candidate =>
			argument.StartsWith(candidate, StringComparison.Ordinal));
		if (prefix is null)
		{
			return [];
		}
		AssertNoReservedCompilerAuthorityTokens(argument);
		string[] values = SplitCompilerAuxiliaryArgumentValues(
			argument[prefix.Length..],
			includeEveryValue: StringComparer.Ordinal.Equals(prefix, "/addmodule:"));
		return values.Select(value =>
		{
			string unquotedValue = value.Trim().Trim('"');
			Assert.False(string.IsNullOrEmpty(unquotedValue));
			string path = Path.GetFullPath(
				Path.IsPathRooted(unquotedValue)
					? unquotedValue
					: Path.Combine(projectRoot, unquotedValue));
			Assert.True(File.Exists(path), $"Compiler auxiliary input is absent: {path}");
			return CreateCompilerAuthorityEntry(
				"AUX",
				prefix,
				detail: NormalizeAuthorityPath(
					path,
					repositoryRoot,
					dotnetRoot,
					packageAuthority,
					authorityRoot),
				sha256: GetCompilerAuxiliaryInputAuthoritySha256(
					prefix,
					path,
					repositoryRoot,
					authorityRoot,
					commitHash));
		}).ToArray();
	}

	private static string[] SplitCompilerAuxiliaryArgumentValues(
		string value,
		bool includeEveryValue)
	{
		var result = new List<string>();
		int start = 0;
		bool quoted = false;
		for (int index = 0; index < value.Length; index++)
		{
			if (value[index] == '"')
			{
				quoted = !quoted;
				continue;
			}
			if (value[index] != ',' || quoted)
			{
				continue;
			}
			result.Add(value[start..index]);
			if (!includeEveryValue)
			{
				return result.ToArray();
			}
			start = index + 1;
		}
		Assert.False(quoted, "Compiler auxiliary input has an unterminated quote.");
		result.Add(value[start..]);
		return result.ToArray();
	}

	private static string[] BuildSyntheticCompilerAuthorityRows(
		IReadOnlyList<CompilerAuthorityEntry> entries) =>
		entries.Select((entry, index) => BuildCanonicalAuthorityManifestRow(
			"COMPILER_INPUT_V2",
			[
				index.ToString(System.Globalization.CultureInfo.InvariantCulture),
				entry.Section,
				index.ToString(System.Globalization.CultureInfo.InvariantCulture),
				entry.Identity,
				entry.Detail,
				entry.Qualifier,
				entry.Values,
				entry.Sha256,
			])).ToArray();

	private static string BuildCanonicalCompilerInputAuthorityManifest(
		IReadOnlyList<CompilerAuthorityEntry> entries)
	{
		Assert.NotEmpty(entries);
		var sectionIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
		string[] rows = entries.Select((entry, globalIndex) =>
		{
			int sectionIndex = sectionIndexes.GetValueOrDefault(entry.Section);
			sectionIndexes[entry.Section] = sectionIndex + 1;
			return BuildCanonicalAuthorityManifestRow(
				"COMPILER_INPUT_V2",
				[
					globalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
					entry.Section,
					sectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
					entry.Identity,
					entry.Detail,
					entry.Qualifier,
					entry.Values,
					entry.Sha256,
				]);
		}).ToArray();
		string manifest = "COMPILER_INPUT_AUTHORITY_V2\n" + string.Join('\n', rows) + "\n";
		_ = AssertCanonicalCompilerInputAuthorityManifest(manifest);
		return manifest;
	}

	private static string[] AssertCanonicalCompilerInputAuthorityManifest(string manifest)
	{
		Assert.DoesNotContain('\r', manifest);
		Assert.True(manifest.EndsWith('\n'));
		string[] lines = manifest.Split('\n', StringSplitOptions.None);
		Assert.True(lines.Length >= 3);
		Assert.Equal("", lines[^1]);
		Assert.Equal("COMPILER_INPUT_AUTHORITY_V2", lines[0]);
		var rows = new string[lines.Length - 2];
		var sectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		int priorSectionOrdinal = -1;
		for (int lineIndex = 1; lineIndex < lines.Length - 1; lineIndex++)
		{
			string row = lines[lineIndex];
			Assert.False(string.IsNullOrEmpty(row));
			rows[lineIndex - 1] = row;
			string[] fields = ParseCanonicalAuthorityManifestRow(row, "COMPILER_INPUT_V2", 8);
			Assert.Equal(
				(lineIndex - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
				fields[0]);
			int sectionOrdinal = Array.IndexOf(CompilerAuthoritySectionOrder, fields[1]);
			Assert.True(sectionOrdinal >= 0, "Unknown compiler authority section.");
			Assert.True(
				sectionOrdinal >= priorSectionOrdinal,
				$"Compiler authority section is out of order: {fields[1]}");
			priorSectionOrdinal = sectionOrdinal;
			int expectedSectionIndex = sectionCounts.GetValueOrDefault(fields[1]);
			Assert.Equal(
				expectedSectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
				fields[2]);
			sectionCounts[fields[1]] = expectedSectionIndex + 1;
			AssertCanonicalCompilerAuthorityEntry(fields);
		}

		Assert.True(sectionCounts.GetValueOrDefault("ARG") > 0);
		Assert.True(sectionCounts.GetValueOrDefault("SOURCE") > 0);
		Assert.True(sectionCounts.GetValueOrDefault("ANALYZER") > 0);
		Assert.True(sectionCounts.GetValueOrDefault("REFERENCE") > 0);
		Assert.True(sectionCounts.GetValueOrDefault("ANALYZER_DEP") > 0);
		Assert.Equal(1, sectionCounts.GetValueOrDefault("DIAGNOSTIC_TASK"));
		Assert.Equal(1, sectionCounts.GetValueOrDefault("DIAGNOSTIC_COMPILER"));
		Assert.Equal(7, sectionCounts.GetValueOrDefault("DIAGNOSTIC_PARAMETER"));
		Assert.Equal(1, sectionCounts.GetValueOrDefault("CSC_START"));
		Assert.True(sectionCounts.GetValueOrDefault("CSC_INPUT") > 0);
		Assert.True(sectionCounts.GetValueOrDefault("CSC_ARG") > 0);
		return rows;
	}

	private static void AssertCanonicalCompilerAuthorityEntry(string[] fields)
	{
		string section = fields[1];
		string identity = fields[3];
		string detail = fields[4];
		string qualifier = fields[5];
		string values = fields[6];
		string sha256 = fields[7];
		switch (section)
		{
			case "ARG":
			case "DIAGNOSTIC_PARAMETER":
			case "CSC_ARG":
				Assert.False(string.IsNullOrEmpty(identity));
				Assert.Equal("", detail);
				Assert.Equal("", qualifier);
				Assert.Equal("", values);
				Assert.Equal("", sha256);
				break;
			case "SOURCE":
			case "ANALYZER":
			case "REFERENCE":
			case "ADDITIONAL":
			case "ANALYZERCONFIG":
			case "EMBED":
			case "ANALYZER_DEP":
			case "DIAGNOSTIC_TASK":
			case "DIAGNOSTIC_COMPILER":
			case "CSC_START":
				AssertCanonicalCompilerAuthorityPath(identity);
				Assert.Equal("", detail);
				Assert.Equal("", qualifier);
				Assert.Equal("", values);
				Assert.Matches("^[0-9a-f]{64}$", sha256);
				break;
			case "AUX":
				Assert.Contains(identity, CompilerAuxiliaryPrefixes);
				AssertCanonicalCompilerAuthorityPath(detail);
				Assert.Equal("", qualifier);
				Assert.Equal("", values);
				Assert.Matches("^[0-9a-f]{64}$", sha256);
				break;
			case "CSC_INPUT":
				Assert.True(
					!string.IsNullOrEmpty(identity) ||
					!string.IsNullOrEmpty(detail) ||
					!string.IsNullOrEmpty(qualifier));
				_ = ParseCanonicalCompilerAuthorityValues(values);
				Assert.Equal("", sha256);
				break;
			default:
				throw new Xunit.Sdk.XunitException("Unknown compiler authority section.");
		}
	}

	private static void AssertCanonicalCompilerAuthorityPath(string value)
	{
		Assert.True(
			value.StartsWith("REPO|", StringComparison.Ordinal) ||
			value.StartsWith("DOTNET|", StringComparison.Ordinal) ||
			value.StartsWith("NUGET|", StringComparison.Ordinal) ||
			value.StartsWith("AUTHORITY|", StringComparison.Ordinal));
		int delimiter = value.IndexOf('|');
		Assert.True(delimiter > 0 && delimiter < value.Length - 1);
		AssertSafePackageRelativePath(value[(delimiter + 1)..]);
	}

	private static string[] ParseCanonicalCompilerAuthorityValues(string value)
	{
		using JsonDocument document = JsonDocument.Parse(
			value,
			new JsonDocumentOptions
			{
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
				MaxDepth = 4,
			});
		Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
		var result = new string[document.RootElement.GetArrayLength()];
		for (int index = 0; index < result.Length; index++)
		{
			JsonElement item = document.RootElement[index];
			Assert.Equal(JsonValueKind.String, item.ValueKind);
			result[index] = Assert.IsType<string>(item.GetString());
		}
		Assert.Equal(value, JsonSerializer.Serialize(result));
		return result;
	}

	private static string GetCompilerAuxiliaryInputAuthoritySha256(
		string prefix,
		string path,
		string repositoryRoot,
		string authorityRoot,
		string commitHash)
	{
		if (!StringComparer.Ordinal.Equals(prefix, "/sourcelink:"))
		{
			return Sha256File(path);
		}

		AssertRegularAuthorityFile(path, "generated SourceLink authority");
		Assert.Matches("^[0-9a-f]{40}$", commitHash);
		using JsonDocument document = JsonDocument.Parse(
			File.ReadAllText(path),
			new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
		JsonElement root = document.RootElement;
		AssertExactJsonProperties(root, ["documents"]);
		JsonElement documents = root.GetProperty("documents");
		Assert.Equal(JsonValueKind.Object, documents.ValueKind);
		JsonProperty mapping = Assert.Single(documents.EnumerateObject());
		Assert.DoesNotContain("{REPO}", mapping.Name, StringComparison.Ordinal);
		Assert.DoesNotContain("{AUTHORITY}", mapping.Name, StringComparison.Ordinal);
		Assert.DoesNotContain("{COMMIT_HASH}", mapping.Name, StringComparison.Ordinal);
		string expectedSourcePattern = Path.GetFullPath(repositoryRoot).Replace('\\', '/').TrimEnd('/') + "/*";
		Assert.Equal(expectedSourcePattern, mapping.Name.Replace('\\', '/'));
		string normalizedSourcePattern = NormalizeAuthorityString(
			mapping.Name,
			("{REPO}", repositoryRoot),
			("{AUTHORITY}", authorityRoot));
		Assert.Equal("{REPO}/*", normalizedSourcePattern);
		string uriPattern = GetRequiredJsonString(mapping.Value, "SourceLink URI pattern");
		AssertNoCanonicalAuthorityToken(uriPattern, "{COMMIT_HASH}");
		string revisionSegment = $"/{commitHash}/";
		Assert.Equal(2, uriPattern.Split(revisionSegment, StringSplitOptions.None).Length);
		Assert.EndsWith("/*", uriPattern, StringComparison.Ordinal);
		string canonicalUriPattern = uriPattern.Replace(
			revisionSegment,
			"/{COMMIT_HASH}/",
			StringComparison.Ordinal);
		return Sha256Text(
			"SOURCELINK_SEMANTIC_V1|" +
			JsonSerializer.Serialize(normalizedSourcePattern) + "|" +
			JsonSerializer.Serialize(canonicalUriPattern));
	}

	private static string GetCompilerInputAuthoritySha256(
		string category,
		string identity,
		string path,
		string projectRoot,
		string repositoryRoot,
		string authorityRoot,
		string generatedRoot,
		string intermediateRoot,
		string commitHash)
	{
		string fullPath = Path.GetFullPath(path);
		string relativePath = NormalizeRelativePath(Path.GetRelativePath(authorityRoot, fullPath));
		if (StringComparer.Ordinal.Equals(category, "ANALYZERCONFIG") &&
			StringComparer.Ordinal.Equals(
				identity,
				"AUTHORITY|obj/net10.0/WalletWasabi.GeneratedMSBuildEditorConfig.editorconfig"))
		{
			Assert.Equal(
				"obj/net10.0/WalletWasabi.GeneratedMSBuildEditorConfig.editorconfig",
				relativePath);
			AssertRegularAuthorityFile(fullPath, "generated MSBuild editor-config authority");
			return Sha256Text(CanonicalizeGeneratedMsBuildEditorConfigBytes(
				File.ReadAllBytes(fullPath),
				projectRoot,
				repositoryRoot,
				authorityRoot,
				generatedRoot,
				intermediateRoot));
		}
		if (!StringComparer.Ordinal.Equals(relativePath, "obj/net10.0/WalletWasabi.AssemblyInfo.cs"))
		{
			return Sha256File(fullPath);
		}

		AssertRegularAuthorityFile(fullPath, "generated product assembly identity");
		Assembly productAssembly = typeof(LiquidOrdinaryWalletPlanEncoder).Assembly;
		string assemblyVersion = productAssembly.GetName().Version?.ToString() ??
			throw new Xunit.Sdk.XunitException("The loaded product assembly version is absent.");
		string fileVersion = Assert.Single(
			productAssembly.GetCustomAttributes<AssemblyFileVersionAttribute>()).Version;
		string informationalVersion = Assert.Single(
			productAssembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>()).InformationalVersion;
		string canonical = File.ReadAllText(fullPath);
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyFileVersionAttribute(\"{fileVersion}\")",
			"System.Reflection.AssemblyFileVersionAttribute(\"{FILE_VERSION}\")");
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyInformationalVersionAttribute(\"{informationalVersion}\")",
			"System.Reflection.AssemblyInformationalVersionAttribute(\"{INFORMATIONAL_VERSION}\")");
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyVersionAttribute(\"{assemblyVersion}\")",
			"System.Reflection.AssemblyVersionAttribute(\"{ASSEMBLY_VERSION}\")");
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyMetadata(\"CommitHash\", \"{commitHash}\")",
			"System.Reflection.AssemblyMetadata(\"CommitHash\", \"{COMMIT_HASH}\")");
		return Sha256Text(canonical);
	}

	private static string ReplaceExactGeneratedAssemblyIdentity(
		string source,
		string expected,
		string replacement)
	{
		string[] replacementTokens = new[]
		{
			"{FILE_VERSION}",
			"{INFORMATIONAL_VERSION}",
			"{ASSEMBLY_VERSION}",
			"{COMMIT_HASH}",
		}.Where(token => replacement.Contains(token, StringComparison.Ordinal)).ToArray();
		Assert.NotEmpty(replacementTokens);
		Assert.All(replacementTokens, token => AssertNoCanonicalAuthorityToken(source, token));
		Assert.Equal(2, source.Split(expected, StringSplitOptions.None).Length);
		return source.Replace(expected, replacement, StringComparison.Ordinal);
	}

	private static string BuildToolchainAuthorityManifest(string dotnetHost, string dotnetRoot)
	{
		AssertApprovedDotnetHost(dotnetHost, dotnetRoot, GetLoadedRuntimeDirectory());
		string sdkRoot = Path.Combine(dotnetRoot, "sdk", PinnedDotnetSdkVersion);
		string hostFxrRoot = Path.Combine(dotnetRoot, "host/fxr", PinnedDotnetHostFxrVersion);
		string sharedRuntimeRoot = Path.Combine(
			dotnetRoot,
			"shared/Microsoft.NETCore.App",
			PinnedDotnetRuntimeVersion);
		AssertExactArtifactBytes(
			File.ReadAllBytes(Path.Combine(sdkRoot, "Microsoft.Build.dll")),
			File.ReadAllBytes(typeof(BinaryLogReplayEventSource).Assembly.Location));
		AssertExactArtifactBytes(
			File.ReadAllBytes(Path.Combine(sdkRoot, "Microsoft.Build.Framework.dll")),
			File.ReadAllBytes(typeof(BuildEventArgs).Assembly.Location));
		var files = new List<string> { Path.GetFullPath(dotnetHost) };
		foreach (string root in new[] { sdkRoot, hostFxrRoot, sharedRuntimeRoot })
		{
			files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
		}
		var physicalPaths = new HashSet<string>(StringComparer.Ordinal);
		var entries = new List<(string RelativePath, string Sha256)>();
		foreach (string path in files)
		{
			string fullPath = Path.GetFullPath(path);
			Assert.True(physicalPaths.Add(fullPath), $"Duplicate toolchain file path: {fullPath}");
			AssertRegularAuthorityFile(fullPath, "pinned toolchain dependency");
			entries.Add((
				GetCanonicalToolchainRelativePath(dotnetRoot, fullPath),
				Sha256File(fullPath)));
		}
		string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
		Assert.Contains(entries, entry => StringComparer.Ordinal.Equals(entry.RelativePath, executableName));
		return BuildCanonicalToolchainFileAuthorityManifest(entries);
	}

	private static string BuildPackagePayloadAuthorityManifest(
		string projectAssetsFile,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		AssertRegularAuthorityFile(projectAssetsFile, "package-payload project assets authority");
		using JsonDocument document = JsonDocument.Parse(
			File.ReadAllText(projectAssetsFile),
			new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 });
		JsonElement libraries = document.RootElement.GetProperty("libraries");
		Assert.Equal(JsonValueKind.Object, libraries.ValueKind);
		JsonProperty[] packages = libraries.EnumerateObject().ToArray();
		SortJsonProperties(packages);
		var identities = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var manifest = new StringBuilder("PACKAGE_PAYLOAD_AUTHORITY_V1\n");
		foreach (JsonProperty package in packages)
		{
			Assert.True(identities.Add(package.Name), $"Duplicate package-payload identity: {package.Name}");
			Assert.True(aliases.Add(package.Name), $"Duplicate or case-aliased package-payload identity: {package.Name}");
			JsonElement library = package.Value;
			string packagePath = GetRequiredJsonString(
				library.GetProperty("path"),
				$"package-payload path for {package.Name}");
			AssertSafePackageRelativePath(packagePath);
			string? selectedPackageDirectory = null;
			foreach (string packageRoot in packageAuthority.OrderedRoots)
			{
				string? candidate = TryResolveExactPackageDirectory(
					packageRoot,
					packagePath,
					$"package-payload directory for {package.Name}");
				if (candidate is not null)
				{
					selectedPackageDirectory = candidate;
					break;
				}
			}
			Assert.NotNull(selectedPackageDirectory);
			JsonElement files = library.GetProperty("files");
			Assert.Equal(JsonValueKind.Array, files.ValueKind);
			string expectedSidecar = packagePath.Replace('/', '.') + ".nupkg.sha512";
			var payloadFiles = new List<string>();
			foreach (JsonElement fileElement in files.EnumerateArray())
			{
				string relativeFile = GetRequiredJsonString(
					fileElement,
					$"package-payload file for {package.Name}");
				AssertSafePackageRelativePath(relativeFile);
				if (relativeFile is ".nupkg.metadata" or ".signature.p7s" or ".nix-patched" ||
					StringComparer.Ordinal.Equals(relativeFile, expectedSidecar))
				{
					continue;
				}
				payloadFiles.Add(relativeFile);
			}
			Assert.NotEmpty(payloadFiles);
			payloadFiles.Sort(StringComparer.Ordinal);
			foreach (string relativeFile in payloadFiles)
			{
				string physicalFile = Path.GetFullPath(Path.Combine(
					selectedPackageDirectory,
					relativeFile.Replace('/', Path.DirectorySeparatorChar)));
				Assert.True(IsPathWithin(physicalFile, selectedPackageDirectory));
				manifest.Append("PAYLOAD|");
				manifest.Append(JsonSerializer.Serialize(package.Name));
				manifest.Append('|');
				manifest.Append(JsonSerializer.Serialize(relativeFile));
				manifest.Append('|');
				manifest.Append(Sha256File(physicalFile));
				manifest.Append('\n');
			}
		}
		Assert.NotEmpty(identities);
		return manifest.ToString();
	}

	private static void AssertConfiguredAuthorityHashes(
		string importManifest,
		string referenceManifest,
		string compilerManifest,
		string toolchainManifest)
	{
#if DEBUG
		string expectedImport = GetExpectedImportClosureSha256(debug: true);
		string expectedReferences = GetExpectedReferenceAuthoritySha256(debug: true);
		string expectedCompiler = GetExpectedCompilerInputAuthoritySha256(debug: true);
#else
		string expectedImport = GetExpectedImportClosureSha256(debug: false);
		string expectedReferences = GetExpectedReferenceAuthoritySha256(debug: false);
		string expectedCompiler = GetExpectedCompilerInputAuthoritySha256(debug: false);
#endif
		AssertExactImportAuthoritySha256(expectedImport, importManifest);
		AssertExactReferenceAuthoritySha256(expectedReferences, referenceManifest);
		AssertExactCompilerInputAuthoritySha256(expectedCompiler, compilerManifest);
		string expectedToolchain = OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64
			? ExpectedMacOsArm64ToolchainDependencyAuthoritySha256
			: OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64
				? ExpectedLinuxX64ToolchainDependencyAuthoritySha256
				: throw new Xunit.Sdk.XunitException(
					$"Unsupported toolchain authority platform: {RuntimeInformation.OSDescription}/{RuntimeInformation.OSArchitecture}");
		_ = GetCanonicalToolchainFileAuthorityPrefix(toolchainManifest);
		AssertExactSha256(expectedToolchain, toolchainManifest);
	}

	private static string NormalizeAuthorityPath(
		string path,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string? authorityRoot = null)
	{
		string fullPath = Path.GetFullPath(path);
		if (TryNormalizePackageAuthorityPath(fullPath, packageAuthority, out string normalizedPackagePath))
		{
			return normalizedPackagePath;
		}
		(string Token, string Root)[] roots =
		{
			("REPO", Path.GetFullPath(repositoryRoot)),
			("DOTNET", Path.GetFullPath(dotnetRoot)),
			("AUTHORITY", Path.GetFullPath(authorityRoot ?? Path.Combine(Path.GetTempPath(), "authority-not-present"))),
		};
		for (int left = 0; left < roots.Length; left++)
		{
			for (int right = left + 1; right < roots.Length; right++)
			{
				Assert.False(
					PackagePathComparer.Equals(roots[left].Root, roots[right].Root),
					$"Authority roots overlap exactly: {roots[left].Token}/{roots[right].Token}");
			}
		}
		SortAuthorityRootsMostSpecific(roots);
		foreach ((string token, string root) in roots)
		{
			if (IsPathWithin(fullPath, root) || PackagePathComparer.Equals(fullPath, root))
			{
				return $"{token}|{NormalizeRelativePath(Path.GetRelativePath(root, fullPath))}";
			}
		}
		throw new Xunit.Sdk.XunitException($"Authority path is outside all pinned roots: {fullPath}");
	}

	private static string NormalizeAuthorityString(
		string value,
		params (string Token, string Root)[] roots)
	{
		string normalized = value.Replace('\\', '/');
		SortAuthorityRootsMostSpecific(roots);
		foreach ((string token, string root) in roots)
		{
			normalized = ReplaceAuthorityRoot(normalized, root, token);
		}
		return normalized;
	}

	private static string NormalizeCompilerAuthorityString(
		string value,
		params (string Token, string Root)[] roots)
	{
		AssertNoReservedCompilerAuthorityTokens(value, roots.Select(root => root.Token).ToArray());
		return NormalizeCompilerAuthorityRoots(value, roots);
	}

	private static string NormalizeCompilerAuthorityRoots(
		string value,
		params (string Token, string Root)[] roots)
	{
		string normalized = value;
		SortAuthorityRootsMostSpecific(roots);
		foreach ((string token, string root) in roots)
		{
			normalized = ReplaceAuthorityRoot(normalized, root, token);
		}
		return normalized;
	}

	private static void AssertNoReservedCompilerAuthorityTokens(
		string value,
		params string[] rootTokens)
	{
		string[] reservedTokens =
		[
			"{REPO}", "{DOTNET}", "{AUTHORITY}", "{NUGET}",
			"{GENERATED}", "{INTERMEDIATE}", "{TASK}",
			"{FILE_VERSION}", "{INFORMATIONAL_VERSION}", "{ASSEMBLY_VERSION}", "{COMMIT_HASH}",
			.. rootTokens,
		];
		foreach (string token in reservedTokens.Distinct(StringComparer.Ordinal))
		{
			AssertNoCanonicalAuthorityToken(value, token);
		}
	}

	private static void AssertNoCanonicalAuthorityToken(string value, string token)
	{
		int tokenOffset = value.IndexOf(token, StringComparison.Ordinal);
		if (tokenOffset >= 0)
		{
			throw new Xunit.Sdk.XunitException(
				$"Reserved compiler authority token at offset {tokenOffset}; value SHA256 {Sha256Text(value)}.");
		}
	}

	private static void SortAuthorityRootsMostSpecific((string Token, string Root)[] roots)
	{
		for (int index = 0; index < roots.Length - 1; index++)
		{
			int mostSpecific = index;
			for (int candidate = index + 1; candidate < roots.Length; candidate++)
			{
				if (roots[candidate].Root.Length > roots[mostSpecific].Root.Length)
				{
					mostSpecific = candidate;
				}
			}
			if (mostSpecific != index)
			{
				(roots[index], roots[mostSpecific]) = (roots[mostSpecific], roots[index]);
			}
		}
	}

	private static string ReplaceAuthorityRoot(string value, string root, string token)
	{
		string normalizedRoot = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
		StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var result = new StringBuilder(value.Length);
		int copied = 0;
		int search = 0;
		while (search < value.Length)
		{
			int match = value.IndexOf(normalizedRoot, search, comparison);
			if (match < 0)
			{
				break;
			}
			int end = match + normalizedRoot.Length;
			bool validStart = match == 0 || IsAuthorityValueBoundary(value[match - 1]);
			bool validEnd = end == value.Length || value[end] == '/' || IsAuthorityValueBoundary(value[end]);
			if (!validStart || !validEnd)
			{
				search = match + 1;
				continue;
			}
			result.Append(value, copied, match - copied);
			result.Append(token);
			copied = end;
			search = end;
		}
		result.Append(value, copied, value.Length - copied);
		return result.ToString();
	}

	private static bool IsAuthorityValueBoundary(char value) =>
		char.IsWhiteSpace(value) || value is '"' or '\'' or '=' or ':' or ';' or ',' or '(' or ')' or '[' or ']';

	private static void AssertExactArtifactBytes(byte[] inspectedAssembly, byte[] rebuiltAssembly)
	{
		Assert.NotEmpty(inspectedAssembly);
		Assert.Equal(inspectedAssembly, rebuiltAssembly);
	}

	private static void AssertExactChildGlobalProperties(
		IReadOnlyDictionary<string, string> actual,
		IReadOnlyDictionary<string, string> expected)
	{
		Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
		Assert.Equal("false", actual["ImportDirectoryBuildTargets"]);
		Assert.Equal("", actual["DirectoryBuildTargetsPath"]);
		Assert.Equal("", actual["CustomBeforeMicrosoftCommonTargets"]);
		Assert.Equal("", actual["CustomAfterMicrosoftCommonTargets"]);
		Assert.Equal("", actual["CustomBeforeMicrosoftCSharpTargets"]);
		Assert.Equal("", actual["CustomAfterMicrosoftCSharpTargets"]);
		Assert.Equal("false", actual["UseSharedCompilation"]);
		Assert.Equal("true", actual["ProvideCommandLineArgs"]);
		Assert.Equal("true", actual["EmitCompilerGeneratedFiles"]);
		Assert.Equal("true", actual["MSBuildDisableAllAutoResponseFiles"]);
	}

	private static void AssertExactChildEnvironment(
		IReadOnlyDictionary<string, string> actual,
		IReadOnlyDictionary<string, string> expected)
	{
		Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
		Assert.DoesNotContain(actual.Keys, name =>
			name.Equals("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("CscToolPath", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("DirectoryBuildTargetsPath", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("MSBuildProject", StringComparison.OrdinalIgnoreCase));
		Assert.All(actual.Keys, name => Assert.DoesNotContain('=', name));
	}

	private static void AssertExactInvocationArguments(
		IReadOnlyList<string> actual,
		IReadOnlyList<string> expected)
	{
		Assert.Equal(expected, actual);
		Assert.Single(actual, argument => argument == "-target:Rebuild");
		Assert.Single(actual, argument => argument == "-noAutoResponse");
		Assert.DoesNotContain(actual, argument => argument.StartsWith('@'));
		Assert.DoesNotContain(actual, argument => argument.Contains("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase));
	}

	private static string Sha256File(string path)
	{
		AssertRegularAuthorityFile(path, "hashed authority file");
		return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
	}

	private static string GetBuildAuthorityFileSha256(
		string path,
		string projectAssetsFile,
		string packagesLockFile,
		string expectedTargetFramework,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string? expectedPinnedNixProjectVersion = null)
	{
		string fullPath = Path.GetFullPath(path);
		string fullAssetsPath = Path.GetFullPath(projectAssetsFile);
		string fullLockPath = Path.GetFullPath(packagesLockFile);
		if (PackagePathComparer.Equals(fullPath, fullLockPath))
		{
			(IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages, _, _) =
				ReadLockedPackageAuthority(fullLockPath, expectedTargetFramework);
			var lockManifest = new StringBuilder();
			AppendLockedPackageAuthority(lockManifest, lockedPackages);
			return Sha256Text(lockManifest.ToString());
		}
		if (PackagePathComparer.Equals(fullPath, fullAssetsPath))
		{
			return Sha256Text(BuildProjectAssetsSemanticManifest(
				fullPath,
				packagesLockFile,
				expectedTargetFramework,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				expectedPinnedNixProjectVersion));
		}

		string fileName = Path.GetFileName(fullPath);
		if (PackagePathComparer.Equals(Path.GetDirectoryName(fullPath), Path.GetDirectoryName(fullAssetsPath)) &&
			(fileName.EndsWith(".nuget.g.props", StringComparison.Ordinal) ||
			 fileName.EndsWith(".nuget.g.targets", StringComparison.Ordinal)))
		{
			return Sha256Text(BuildGeneratedNuGetSemanticManifest(
				fullPath,
				repositoryRoot,
				dotnetRoot,
				packageAuthority));
		}

		return Sha256File(fullPath);
	}

	private static string BuildProjectAssetsSemanticManifest(
		string projectAssetsFile,
		string packagesLockFile,
		string expectedTargetFramework,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string? expectedPinnedNixProjectVersion)
	{
		Assert.Equal("net10.0", expectedTargetFramework);
		string expectedLockFile = Path.GetFullPath(Path.Combine(
			repositoryRoot,
			"WalletWasabi/packages.lock.json"));
		Assert.True(PackagePathComparer.Equals(expectedLockFile, Path.GetFullPath(packagesLockFile)));
		(
			IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages,
			bool lockHasLinuxX64Overlay,
			_) =
			ReadLockedPackageAuthority(packagesLockFile, expectedTargetFramework);
		AssertRegularAuthorityFile(projectAssetsFile, "semantic project assets authority");
		using JsonDocument document = JsonDocument.Parse(
			File.ReadAllText(projectAssetsFile),
			new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 });
		JsonElement root = document.RootElement;
		Assert.Equal(JsonValueKind.Object, root.ValueKind);
		AssertExactJsonProperties(
			root,
			["version", "targets", "libraries", "projectFileDependencyGroups", "packageFolders", "project"]);
		Assert.Equal(3, root.GetProperty("version").GetInt32());
		AssertProjectAssetsDependencyAuthority(
			root,
			expectedTargetFramework,
			lockedPackages,
			lockHasLinuxX64Overlay,
			packageAuthority);
		AssertProjectAssetsPackageTopology(root, packageAuthority);
		AssertProjectAssetsFallbackFolderTopology(root, packageAuthority);

		var manifest = new StringBuilder();
		AppendLockedPackageAuthority(manifest, lockedPackages);
		AppendCanonicalProjectAssetsJson(
			manifest,
			root,
			"$",
			repositoryRoot,
			dotnetRoot,
			packageAuthority,
			lockedPackages,
			expectedPinnedNixProjectVersion);
		return manifest.ToString();
	}

	private static (
		IReadOnlyDictionary<string, LockedPackageAuthority> Packages,
		bool HasLinuxX64Overlay,
		bool HasContentHashes)
		ReadLockedPackageAuthority(string packagesLockFile, string expectedTargetFramework)
	{
		AssertRegularAuthorityFile(packagesLockFile, "tracked packages lock authority");
		using JsonDocument document = JsonDocument.Parse(
			File.ReadAllText(packagesLockFile),
			new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 });
		JsonElement root = document.RootElement;
		AssertExactJsonProperties(root, ["version", "dependencies"]);
		Assert.Equal(JsonValueKind.Number, root.GetProperty("version").ValueKind);
		Assert.Equal(2, root.GetProperty("version").GetInt32());
		JsonElement frameworks = root.GetProperty("dependencies");
		Assert.Equal(JsonValueKind.Object, frameworks.ValueKind);
		bool hasLinuxX64Overlay = frameworks.TryGetProperty(LinuxX64TargetFramework, out JsonElement linuxX64Overlay);
		AssertExactJsonProperties(
			frameworks,
			hasLinuxX64Overlay
				? new[] { expectedTargetFramework, LinuxX64TargetFramework }
				: new[] { expectedTargetFramework });
		JsonElement dependencies = frameworks.GetProperty(expectedTargetFramework);
		Assert.Equal(JsonValueKind.Object, dependencies.ValueKind);
		var packages = new Dictionary<string, LockedPackageAuthority>(StringComparer.Ordinal);
		var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		bool? contentHashProfile = null;
		foreach (JsonProperty package in dependencies.EnumerateObject())
		{
			AssertPackageId(package.Name, "locked package ID");
			Assert.True(aliases.Add(package.Name), $"Duplicate or case-aliased locked package ID: {package.Name}");
			Assert.Equal(JsonValueKind.Object, package.Value.ValueKind);
			var names = new HashSet<string>(StringComparer.Ordinal);
			var nameAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (JsonProperty property in package.Value.EnumerateObject())
			{
				Assert.True(names.Add(property.Name), $"Duplicate locked package property: {package.Name}/{property.Name}");
				Assert.True(nameAliases.Add(property.Name), $"Case-aliased locked package property: {package.Name}/{property.Name}");
				Assert.Contains(property.Name, new[] { "type", "requested", "resolved", "contentHash", "dependencies" });
			}
			Assert.Contains("type", names);
			Assert.Contains("resolved", names);
			bool hasContentHash = names.Contains("contentHash");
			contentHashProfile ??= hasContentHash;
			Assert.Equal(contentHashProfile.Value, hasContentHash);
			string type = GetRequiredJsonString(package.Value.GetProperty("type"), $"locked package type for {package.Name}");
			Assert.Contains(type, new[] { "Direct", "Transitive" });
			Assert.Equal(type == "Direct", names.Contains("requested"));
			string? requested = null;
			if (names.Contains("requested"))
			{
				requested = GetRequiredJsonString(
					package.Value.GetProperty("requested"),
					$"locked requested version for {package.Name}");
				Assert.False(string.IsNullOrWhiteSpace(requested));
			}
			string resolvedVersion = GetRequiredJsonString(
				package.Value.GetProperty("resolved"),
				$"locked resolved version for {package.Name}");
			AssertSemanticPackageVersion(resolvedVersion, $"locked package version for {package.Name}");
			Assert.True(
				NuGetVersion.TryParse(resolvedVersion, out NuGetVersion? parsedResolvedVersion),
				$"The locked resolved version is not a NuGet version: {package.Name}/{resolvedVersion}");
			if (requested is not null)
			{
				Assert.True(
					VersionRange.TryParse(requested, out VersionRange? requestedRange),
					$"The locked requested range is not a NuGet range: {package.Name}/{requested}");
				Assert.True(
					requestedRange.Satisfies(parsedResolvedVersion),
					$"The locked resolved version is outside its direct requested range: {package.Name}");
			}
			string? contentHash = hasContentHash
				? AssertCanonicalSha512(
					package.Value.GetProperty("contentHash"),
					$"locked content hash for {package.Name}/{resolvedVersion}")
				: null;
			var dependencyAuthority = new Dictionary<string, string>(StringComparer.Ordinal);
			if (names.Contains("dependencies"))
			{
				JsonElement transitiveDependencies = package.Value.GetProperty("dependencies");
				Assert.Equal(JsonValueKind.Object, transitiveDependencies.ValueKind);
				var dependencyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (JsonProperty dependency in transitiveDependencies.EnumerateObject())
				{
					AssertPackageId(dependency.Name, $"locked dependency ID for {package.Name}");
					Assert.True(dependencyAliases.Add(dependency.Name), $"Duplicate or case-aliased locked dependency: {package.Name}/{dependency.Name}");
					string dependencyVersion = GetRequiredJsonString(
						dependency.Value,
						$"locked dependency version for {package.Name}/{dependency.Name}");
					AssertSemanticPackageVersion(dependencyVersion, $"locked dependency version for {package.Name}/{dependency.Name}");
					Assert.True(dependencyAuthority.TryAdd(dependency.Name, dependencyVersion));
				}
			}
			Assert.True(packages.TryAdd(
				package.Name,
				(type, requested, resolvedVersion, contentHash, dependencyAuthority)));
		}
		Assert.NotEmpty(packages);
		Assert.NotNull(contentHashProfile);
		if (hasLinuxX64Overlay)
		{
			AssertExactJsonProperties(
				linuxX64Overlay,
				["Microsoft.Win32.SystemEvents", "SQLitePCLRaw.lib.e_sqlite3"]);
			foreach (JsonProperty overlayPackage in linuxX64Overlay.EnumerateObject())
			{
				Assert.True(dependencies.TryGetProperty(overlayPackage.Name, out JsonElement basePackage));
				Assert.True(
					JsonElement.DeepEquals(basePackage, overlayPackage.Value),
					$"The linux-x64 lock overlay diverges from base authority: {overlayPackage.Name}");
			}
		}
		foreach (KeyValuePair<string, LockedPackageAuthority> package in packages)
		{
			foreach (KeyValuePair<string, string> dependency in package.Value.Dependencies)
			{
				Assert.True(
					packages.TryGetValue(dependency.Key, out LockedPackageAuthority resolvedDependency),
					$"Locked dependency edge has no package authority: {package.Key}/{dependency.Key}");
				Assert.True(
					VersionRange.TryParse(dependency.Value, out VersionRange? dependencyRange),
					$"The locked dependency constraint is not a NuGet range: {package.Key}/{dependency.Key}");
				Assert.True(
					NuGetVersion.TryParse(resolvedDependency.ResolvedVersion, out NuGetVersion? resolvedVersion),
					$"The locked dependency resolution is not a NuGet version: {package.Key}/{dependency.Key}");
				Assert.True(
					dependencyRange.Satisfies(resolvedVersion),
					$"Locked dependency resolution is outside its declared constraint: {package.Key}/{dependency.Key}");
			}
		}
		return (packages, hasLinuxX64Overlay, contentHashProfile.Value);
	}

	private static void AppendLockedPackageAuthority(
		StringBuilder manifest,
		IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages)
	{
		manifest.Append("PROJECT_ASSETS_SEMANTIC_V2|LOCKED_PACKAGES|");
		string[] packageIds = lockedPackages.Keys.ToArray();
		Array.Sort(packageIds, StringComparer.Ordinal);
		foreach (string packageId in packageIds)
		{
			LockedPackageAuthority package = lockedPackages[packageId];
			manifest.Append(JsonSerializer.Serialize(packageId));
			manifest.Append('|');
			manifest.Append(JsonSerializer.Serialize(package.Type));
			manifest.Append('|');
			manifest.Append(JsonSerializer.Serialize(package.Requested));
			manifest.Append('|');
			manifest.Append(JsonSerializer.Serialize(package.ResolvedVersion));
			manifest.Append('|');
			manifest.Append("{VALIDATED_PACKAGE_CONTENT_AUTHORITY}");
			string[] dependencyIds = package.Dependencies.Keys.ToArray();
			Array.Sort(dependencyIds, StringComparer.Ordinal);
			foreach (string dependencyId in dependencyIds)
			{
				manifest.Append('|');
				manifest.Append(JsonSerializer.Serialize(dependencyId));
				manifest.Append('=');
				manifest.Append(JsonSerializer.Serialize(package.Dependencies[dependencyId]));
			}
			manifest.Append(';');
		}
	}

	private static void AssertProjectAssetsDependencyAuthority(
		JsonElement root,
		string expectedTargetFramework,
		IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages,
		bool lockHasLinuxX64Overlay,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		JsonElement libraries = root.GetProperty("libraries");
		Assert.Equal(JsonValueKind.Object, libraries.ValueKind);
		var identities = new HashSet<string>(StringComparer.Ordinal);
		var identityAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var packageIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty library in libraries.EnumerateObject())
		{
			Assert.True(identities.Add(library.Name), $"Duplicate project-assets library identity: {library.Name}");
			Assert.True(identityAliases.Add(library.Name), $"Duplicate or case-aliased project-assets library identity: {library.Name}");
			int separator = library.Name.LastIndexOf('/');
			Assert.True(separator > 0 && separator < library.Name.Length - 1, $"Invalid library identity: {library.Name}");
			string packageId = library.Name[..separator];
			string resolvedVersion = library.Name[(separator + 1)..];
			AssertPackageId(packageId, "project-assets package ID");
			AssertSemanticPackageVersion(resolvedVersion, $"project-assets package version for {packageId}");
			Assert.True(packageIds.Add(packageId), $"Duplicate project-assets package ID: {packageId}");
			Assert.True(lockedPackages.TryGetValue(packageId, out LockedPackageAuthority locked));
			Assert.Equal(locked.ResolvedVersion, resolvedVersion);
			Assert.Equal(JsonValueKind.Object, library.Value.ValueKind);
			AssertProjectAssetsLibraryProperties(library.Name, library.Value);
			Assert.Equal("package", GetRequiredJsonString(library.Value.GetProperty("type"), $"project-assets type for {library.Name}"));
			string packagePath = GetRequiredJsonString(library.Value.GetProperty("path"), $"project-assets path for {library.Name}");
			AssertSafePackageRelativePath(packagePath);
			Assert.Equal(library.Name.ToLowerInvariant(), packagePath);
			JsonElement files = library.Value.GetProperty("files");
			AssertProjectAssetsLibraryTransportProfile(
				library.Name,
				packageId,
				resolvedVersion,
				packagePath,
				library.Value,
				files,
				locked.ContentHash,
				packageAuthority);
		}
		Assert.NotEmpty(identities);
		Assert.Equal(lockedPackages.Keys.Order(StringComparer.Ordinal), packageIds.Order(StringComparer.Ordinal));

		JsonElement targets = root.GetProperty("targets");
		bool hasLinuxX64Target = targets.TryGetProperty(LinuxX64TargetFramework, out JsonElement linuxX64Target);
		Assert.Equal(lockHasLinuxX64Overlay, hasLinuxX64Target);
		AssertExactJsonProperties(
			targets,
			hasLinuxX64Target
				? new[] { expectedTargetFramework, LinuxX64TargetFramework }
				: new[] { expectedTargetFramework });
		JsonElement target = targets.GetProperty(expectedTargetFramework);
		Assert.Equal(JsonValueKind.Object, target.ValueKind);
		var targetIdentities = new HashSet<string>(StringComparer.Ordinal);
		var targetAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (JsonProperty dependency in target.EnumerateObject())
		{
			Assert.True(targetIdentities.Add(dependency.Name), $"Duplicate project-assets target identity: {dependency.Name}");
			Assert.True(targetAliases.Add(dependency.Name), $"Duplicate or case-aliased project-assets target identity: {dependency.Name}");
			Assert.Contains(dependency.Name, identities);
			Assert.Equal(JsonValueKind.Object, dependency.Value.ValueKind);
			Assert.Equal(
				"package",
				GetRequiredJsonString(dependency.Value.GetProperty("type"), $"project-assets target type for {dependency.Name}"));
		}
		Assert.Equal(identities.Order(StringComparer.Ordinal), targetIdentities.Order(StringComparer.Ordinal));

		JsonElement project = root.GetProperty("project");
		bool hasProjectRuntimes = project.TryGetProperty("runtimes", out JsonElement projectRuntimes);
		Assert.Equal(hasLinuxX64Target, hasProjectRuntimes);
		JsonElement restore = project.GetProperty("restore");
		JsonElement audit = restore.GetProperty("restoreAuditProperties");
		AssertExactJsonProperties(
			audit,
			["enableAudit", "auditLevel", "auditMode", "suppressedAdvisories"]);
		Assert.Contains(
			GetRequiredJsonString(audit.GetProperty("enableAudit"), "restore audit enablement"),
			new[] { "true", "false" });
		Assert.Equal("low", GetRequiredJsonString(audit.GetProperty("auditLevel"), "restore audit level"));
		Assert.Equal("all", GetRequiredJsonString(audit.GetProperty("auditMode"), "restore audit mode"));
		JsonElement suppressedAdvisories = audit.GetProperty("suppressedAdvisories");
		AssertExactJsonProperties(
			suppressedAdvisories,
			["https://github.com/advisories/GHSA-2m69-gcr7-jv3q"]);
		Assert.Equal(
			JsonValueKind.Null,
			suppressedAdvisories.GetProperty("https://github.com/advisories/GHSA-2m69-gcr7-jv3q").ValueKind);
		if (!hasLinuxX64Target)
		{
			return;
		}
		AssertExactLinuxX64AssetsOverlay(target, linuxX64Target, project);
	}

	private static void AssertExactLinuxX64AssetsOverlay(
		JsonElement target,
		JsonElement linuxX64Target,
		JsonElement project)
	{
		JsonElement projectRuntimes = project.GetProperty("runtimes");
		AssertExactJsonProperties(projectRuntimes, ["linux-x64"]);
		JsonElement linuxX64Runtime = projectRuntimes.GetProperty("linux-x64");
		AssertExactJsonProperties(linuxX64Runtime, ["#import"]);
		JsonElement imports = linuxX64Runtime.GetProperty("#import");
		Assert.Equal(JsonValueKind.Array, imports.ValueKind);
		Assert.Empty(imports.EnumerateArray());
		var changedRidPackages = new HashSet<string>(StringComparer.Ordinal);
		var ridIdentities = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty ridDependency in linuxX64Target.EnumerateObject())
		{
			Assert.True(ridIdentities.Add(ridDependency.Name));
			Assert.True(target.TryGetProperty(ridDependency.Name, out JsonElement baseDependency));
			if (JsonElement.DeepEquals(baseDependency, ridDependency.Value))
			{
				continue;
			}
			Assert.True(changedRidPackages.Add(ridDependency.Name));
			if (ridDependency.Name == "Microsoft.Win32.SystemEvents/10.0.2")
			{
				AssertExactJsonProperties(baseDependency, ["type", "compile", "runtime", "build", "runtimeTargets"]);
				AssertExactJsonProperties(ridDependency.Value, ["type", "compile", "runtime", "build"]);
				foreach (string propertyName in new[] { "type", "compile", "runtime", "build" })
				{
					Assert.True(JsonElement.DeepEquals(
						baseDependency.GetProperty(propertyName),
						ridDependency.Value.GetProperty(propertyName)));
				}
				JsonElement runtimeTargets = baseDependency.GetProperty("runtimeTargets");
				AssertExactJsonProperties(
					runtimeTargets,
					["runtimes/win/lib/net10.0/Microsoft.Win32.SystemEvents.dll"]);
				JsonElement winRuntime = runtimeTargets.GetProperty(
					"runtimes/win/lib/net10.0/Microsoft.Win32.SystemEvents.dll");
				AssertExactJsonProperties(winRuntime, ["assetType", "rid"]);
				Assert.Equal("runtime", GetRequiredJsonString(winRuntime.GetProperty("assetType"), "SystemEvents asset type"));
				Assert.Equal("win", GetRequiredJsonString(winRuntime.GetProperty("rid"), "SystemEvents RID"));
				continue;
			}
			Assert.Equal("SQLitePCLRaw.lib.e_sqlite3/2.1.11", ridDependency.Name);
			AssertExactJsonProperties(baseDependency, ["type", "compile", "runtime", "build", "runtimeTargets"]);
			AssertExactJsonProperties(ridDependency.Value, ["type", "compile", "runtime", "native", "build"]);
			foreach (string propertyName in new[] { "type", "compile", "runtime", "build" })
			{
				Assert.True(JsonElement.DeepEquals(
					baseDependency.GetProperty(propertyName),
					ridDependency.Value.GetProperty(propertyName)));
			}
			const string LinuxNativeAsset = "runtimes/linux-x64/native/libe_sqlite3.so";
			JsonElement baseLinuxNative = baseDependency.GetProperty("runtimeTargets").GetProperty(LinuxNativeAsset);
			AssertExactJsonProperties(baseLinuxNative, ["assetType", "rid"]);
			Assert.Equal("native", GetRequiredJsonString(baseLinuxNative.GetProperty("assetType"), "SQLite native asset type"));
			Assert.Equal("linux-x64", GetRequiredJsonString(baseLinuxNative.GetProperty("rid"), "SQLite native RID"));
			JsonElement ridNative = ridDependency.Value.GetProperty("native");
			AssertExactJsonProperties(ridNative, [LinuxNativeAsset]);
			Assert.Empty(ridNative.GetProperty(LinuxNativeAsset).EnumerateObject());
		}
		string[] baseIdentities = target.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
		Assert.Equal(baseIdentities, ridIdentities.Order(StringComparer.Ordinal));
		Assert.Equal(
			new[] { "Microsoft.Win32.SystemEvents/10.0.2", "SQLitePCLRaw.lib.e_sqlite3/2.1.11" },
			changedRidPackages.Order(StringComparer.Ordinal));
	}

	private static void AssertProjectAssetsLibraryProperties(string libraryIdentity, JsonElement library)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (JsonProperty property in library.EnumerateObject())
		{
			Assert.True(names.Add(property.Name), $"Duplicate project-assets library property: {libraryIdentity}/{property.Name}");
			Assert.True(aliases.Add(property.Name), $"Case-aliased project-assets library property: {libraryIdentity}/{property.Name}");
			Assert.Contains(property.Name, new[] { "sha512", "type", "path", "files", "hasTools" });
		}
		Assert.Contains("type", names);
		Assert.Contains("path", names);
		Assert.Contains("files", names);
		if (names.Contains("hasTools"))
		{
			Assert.Contains(
				library.GetProperty("hasTools").ValueKind,
				new[] { JsonValueKind.True, JsonValueKind.False });
		}
	}

	private static void AssertProjectAssetsLibraryTransportProfile(
		string libraryIdentity,
		string packageId,
		string resolvedVersion,
		string packagePath,
		JsonElement library,
		JsonElement files,
		string? lockedContentHash,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		Assert.Equal(JsonValueKind.Array, files.ValueKind);
		var fileIdentities = new HashSet<string>(StringComparer.Ordinal);
		var fileAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (JsonElement file in files.EnumerateArray())
		{
			Assert.Equal(JsonValueKind.String, file.ValueKind);
			string relativeFile = Assert.IsType<string>(file.GetString());
			AssertSafePackageRelativePath(relativeFile);
			Assert.True(fileIdentities.Add(relativeFile), $"Duplicate package file identity: {libraryIdentity}/{relativeFile}");
			Assert.True(fileAliases.Add(relativeFile), $"Duplicate or case-aliased package file identity: {libraryIdentity}/{relativeFile}");
		}
		Assert.NotEmpty(fileIdentities);
		Assert.Contains(".nupkg.metadata", fileIdentities);
		string expectedSidecar = packagePath.Replace('/', '.') + ".nupkg.sha512";
		foreach (string file in fileIdentities)
		{
			if (file.Contains('/'))
			{
				continue;
			}
			if (file.Equals(".signature.p7s", StringComparison.OrdinalIgnoreCase))
			{
				Assert.Equal(".signature.p7s", file);
			}
			if (file.Equals(".nix-patched", StringComparison.OrdinalIgnoreCase))
			{
				Assert.Equal(".nix-patched", file);
			}
			if (file.EndsWith(".nupkg.sha512", StringComparison.OrdinalIgnoreCase))
			{
				Assert.Equal(expectedSidecar, file);
			}
		}

		bool hasSignature = fileIdentities.Contains(".signature.p7s");
		bool hasSidecar = fileIdentities.Contains(expectedSidecar);
		bool hasNixPatchMarker = fileIdentities.Contains(".nix-patched");
		bool hasAssetsHash = library.TryGetProperty("sha512", out JsonElement assetsHash);
		Assert.Equal(lockedContentHash is not null, hasAssetsHash);
		if (hasAssetsHash)
		{
			Assert.NotNull(lockedContentHash);
			Assert.Equal(
				lockedContentHash,
				AssertCanonicalSha512(assetsHash, $"project-assets content hash for {libraryIdentity}"));
		}

		bool normalProfile = hasSignature && hasSidecar && !hasNixPatchMarker && hasAssetsHash;
		bool nixFallbackProfile = !hasSignature && !hasSidecar && hasNixPatchMarker && !hasAssetsHash;
		Assert.True(
			normalProfile ^ nixFallbackProfile,
			$"The package transport profile is hybrid or unknown: {libraryIdentity}");

		string? selectedPackageDirectory = null;
		int selectedRootIndex = -1;
		for (int index = 0; index < packageAuthority.OrderedRoots.Length; index++)
		{
			string? packageDirectory = TryResolveExactPackageDirectory(
				packageAuthority.OrderedRoots[index],
				packagePath,
				$"resolved package directory for {packageId}/{resolvedVersion}");
			if (packageDirectory is null)
			{
				continue;
			}
			selectedPackageDirectory = packageDirectory;
			selectedRootIndex = index;
			break;
		}
		Assert.NotNull(selectedPackageDirectory);
		if (nixFallbackProfile)
		{
			Assert.True(selectedRootIndex > 0, $"The pinned-Nix package resolved from the primary root: {libraryIdentity}");
		}
		foreach (string relativeFile in fileIdentities)
		{
			string physicalFile = Path.GetFullPath(Path.Combine(
				selectedPackageDirectory,
				relativeFile.Replace('/', Path.DirectorySeparatorChar)));
			Assert.True(IsPathWithin(physicalFile, selectedPackageDirectory));
			AssertRegularAuthorityFile(physicalFile, $"declared package file for {libraryIdentity}/{relativeFile}");
		}
		var physicalIdentities = new HashSet<string>(StringComparer.Ordinal);
		var physicalAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string physicalFile in Directory.EnumerateFiles(selectedPackageDirectory, "*", SearchOption.AllDirectories))
		{
			AssertRegularAuthorityFile(physicalFile, $"materialized package file for {libraryIdentity}");
			string relativeFile = NormalizeRelativePath(
				Path.GetRelativePath(selectedPackageDirectory, physicalFile));
			AssertSafePackageRelativePath(relativeFile);
			Assert.True(physicalIdentities.Add(relativeFile));
			Assert.True(
				physicalAliases.Add(relativeFile),
				$"Duplicate or case-aliased materialized package file: {libraryIdentity}/{relativeFile}");
		}
		string metadataPath = Path.Combine(selectedPackageDirectory, ".nupkg.metadata");
		if (normalProfile)
		{
			Assert.NotNull(lockedContentHash);
			string expectedNupkg = packagePath.Replace('/', '.') + ".nupkg";
			Assert.True(physicalIdentities.Remove(expectedNupkg), $"The cached nupkg is absent: {libraryIdentity}");
			Assert.Equal(fileIdentities.Order(StringComparer.Ordinal), physicalIdentities.Order(StringComparer.Ordinal));
			using JsonDocument metadata = JsonDocument.Parse(
				File.ReadAllText(metadataPath),
				new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
			Assert.Equal(JsonValueKind.Object, metadata.RootElement.ValueKind);
			Assert.Equal(
				lockedContentHash,
				AssertCanonicalSha512(
					metadata.RootElement.GetProperty("contentHash"),
					$"NuGet metadata content hash for {libraryIdentity}"));
			Assert.Equal(
				Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(Path.Combine(selectedPackageDirectory, expectedNupkg)))),
				File.ReadAllText(Path.Combine(selectedPackageDirectory, expectedSidecar)));
			Assert.True(new FileInfo(Path.Combine(selectedPackageDirectory, ".signature.p7s")).Length > 0);
			Assert.False(File.Exists(Path.Combine(selectedPackageDirectory, ".nix-patched")));
		}
		else
		{
			string[] nixArchiveMetadata = physicalIdentities
				.Except(fileIdentities, StringComparer.Ordinal)
				.Order(StringComparer.Ordinal)
				.ToArray();
			Assert.Contains("[Content_Types].xml", nixArchiveMetadata, StringComparer.Ordinal);
			Assert.Contains("_rels/.rels", nixArchiveMetadata, StringComparer.Ordinal);
			int corePropertiesCount = 0;
			foreach (string relativeFile in nixArchiveMetadata)
			{
				Assert.True(
					IsPinnedNixArchiveMetadataPath(relativeFile),
					$"The pinned-Nix package has an unapproved archive metadata file: {libraryIdentity}/{relativeFile}");
				if (relativeFile.StartsWith(
					"package/services/metadata/core-properties/",
					StringComparison.Ordinal))
				{
					corePropertiesCount++;
				}
			}
			Assert.InRange(corePropertiesCount, 0, 1);
			Assert.Equal(Encoding.ASCII.GetBytes("{}\n"), File.ReadAllBytes(metadataPath));
			Assert.Equal(0, new FileInfo(Path.Combine(selectedPackageDirectory, ".nix-patched")).Length);
			Assert.False(File.Exists(Path.Combine(selectedPackageDirectory, ".signature.p7s")));
			Assert.False(File.Exists(Path.Combine(selectedPackageDirectory, expectedSidecar)));
		}
	}

	private static string AssertCanonicalSha512(JsonElement value, string description)
	{
		Assert.Equal(JsonValueKind.String, value.ValueKind);
		string encoded = Assert.IsType<string>(value.GetString());
		Span<byte> decoded = stackalloc byte[64];
		Assert.True(
			Convert.TryFromBase64String(encoded, decoded, out int written) && written == decoded.Length,
			$"The {description} is not a 64-byte Base64 value.");
		Assert.Equal(encoded, Convert.ToBase64String(decoded));
		return encoded;
	}

	private static string GetRequiredJsonString(JsonElement value, string description)
	{
		Assert.Equal(JsonValueKind.String, value.ValueKind);
		string? result = value.GetString();
		Assert.NotNull(result);
		return result;
	}

	private static void AssertPackageId(string value, string description)
	{
		Assert.Matches("^[0-9A-Za-z_][0-9A-Za-z_.-]*$", value);
		Assert.DoesNotContain("..", value, StringComparison.Ordinal);
		Assert.False(value.EndsWith('.'), $"The {description} has a trailing dot: {value}");
	}

	private static void AssertSemanticPackageVersion(string value, string description)
	{
		const string NumericIdentifier = "(?:0|[1-9][0-9]*)";
		const string Label = "[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*";
		Assert.True(
			Regex.IsMatch(
				value,
				$"^{NumericIdentifier}(?:\\.{NumericIdentifier}){{1,3}}(?:-{Label})?(?:\\+{Label})?$",
				RegexOptions.CultureInvariant),
			$"The {description} is not a canonical semantic package version: {value}");
	}

	private static void AssertProjectAssetsPackageTopology(
		JsonElement root,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		JsonElement packageFolders = root.GetProperty("packageFolders");
		Assert.Equal(JsonValueKind.Object, packageFolders.ValueKind);
		int index = 0;
		foreach (JsonProperty folder in packageFolders.EnumerateObject())
		{
			Assert.True(index < packageAuthority.OrderedRoots.Length);
			Assert.True(PackagePathComparer.Equals(
				ParseCanonicalPackageRoot(folder.Name, "semantic project-assets package root"),
				packageAuthority.OrderedRoots[index]));
			Assert.Equal(JsonValueKind.Object, folder.Value.ValueKind);
			Assert.Empty(folder.Value.EnumerateObject());
			index++;
		}
		Assert.Equal(packageAuthority.OrderedRoots.Length, index);
	}

	private static void AppendCanonicalProjectAssetsJson(
		StringBuilder manifest,
		JsonElement value,
		string jsonPath,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages,
		string? expectedPinnedNixProjectVersion = null)
	{
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.version") &&
			expectedPinnedNixProjectVersion is not null)
		{
			Assert.Equal(JsonValueKind.String, value.ValueKind);
		}
		switch (value.ValueKind)
		{
			case JsonValueKind.Object:
				manifest.Append('{');
				JsonProperty[] properties = value.EnumerateObject().ToArray();
				SortJsonProperties(properties);
				var names = new HashSet<string>(StringComparer.Ordinal);
				bool first = true;
				foreach (JsonProperty property in properties)
				{
					Assert.True(names.Add(property.Name), $"Duplicate JSON property at {jsonPath}: {property.Name}");
					string childPath = jsonPath + "." + property.Name;
					if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.fallbackFolders") ||
						StringComparer.Ordinal.Equals(childPath, "$.project.runtimes"))
					{
						if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.fallbackFolders"))
						{
							AssertProjectAssetsFallbackFolders(property.Value, packageAuthority);
						}
						continue;
					}
					if (!first)
					{
						manifest.Append(',');
					}
					first = false;
					manifest.Append(JsonSerializer.Serialize(property.Name));
					manifest.Append(':');
					if (StringComparer.Ordinal.Equals(childPath, "$.targets"))
					{
						manifest.Append('{');
						manifest.Append(JsonSerializer.Serialize("net10.0"));
						manifest.Append(':');
						AppendCanonicalProjectAssetsJson(
							manifest,
							property.Value.GetProperty("net10.0"),
							"$.targets.net10.0",
							repositoryRoot,
							dotnetRoot,
							packageAuthority,
							lockedPackages);
						manifest.Append('}');
					}
					else if (StringComparer.Ordinal.Equals(childPath, "$.libraries"))
					{
						AppendCanonicalProjectAssetsLibraries(
							manifest,
							property.Value,
							repositoryRoot,
							dotnetRoot,
							packageAuthority,
							lockedPackages);
					}
					else if (StringComparer.Ordinal.Equals(childPath, "$.packageFolders"))
					{
						manifest.Append(JsonSerializer.Serialize("{VALIDATED_PACKAGE_ROOT_TOPOLOGY}"));
					}
					else if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.configFilePaths"))
					{
						AssertProjectAssetsConfigFileTopology(property.Value, repositoryRoot);
						manifest.Append(JsonSerializer.Serialize("{VALIDATED_CONFIG_FILE_TOPOLOGY}"));
					}
					else if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.sources"))
					{
						AssertProjectAssetsRestoreSources(
							property.Value,
							dotnetRoot,
							packageAuthority,
							lockedPackages);
						manifest.Append(JsonSerializer.Serialize("{VALIDATED_RESTORE_SOURCE}"));
					}
					else if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.restoreAuditProperties"))
					{
						manifest.Append(JsonSerializer.Serialize("{VALIDATED_RESTORE_AUDIT_PROFILE}"));
					}
					else
					{
						AppendCanonicalProjectAssetsJson(
							manifest,
							property.Value,
							childPath,
							repositoryRoot,
							dotnetRoot,
							packageAuthority,
							lockedPackages,
							expectedPinnedNixProjectVersion);
					}
				}
				manifest.Append('}');
				break;
			case JsonValueKind.Array:
				manifest.Append('[');
				int index = 0;
				foreach (JsonElement item in value.EnumerateArray())
				{
					if (index != 0)
					{
						manifest.Append(',');
					}
					AppendCanonicalProjectAssetsJson(
						manifest,
						item,
						jsonPath + "[]",
						repositoryRoot,
						dotnetRoot,
						packageAuthority,
						lockedPackages,
						expectedPinnedNixProjectVersion);
					index++;
				}
				manifest.Append(']');
				break;
			case JsonValueKind.String:
				manifest.Append(JsonSerializer.Serialize(NormalizeProjectAssetsString(
					Assert.IsType<string>(value.GetString()),
					jsonPath,
					repositoryRoot,
					dotnetRoot,
					packageAuthority,
					expectedPinnedNixProjectVersion)));
				break;
			case JsonValueKind.Number:
				manifest.Append(value.GetRawText());
				break;
			case JsonValueKind.True:
				manifest.Append("true");
				break;
			case JsonValueKind.False:
				manifest.Append("false");
				break;
			case JsonValueKind.Null:
				manifest.Append("null");
				break;
			default:
				throw new Xunit.Sdk.XunitException($"Unsupported JSON value at {jsonPath}: {value.ValueKind}");
		}
		}

	private static void AppendCanonicalProjectAssetsLibraries(
		StringBuilder manifest,
		JsonElement libraries,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages)
	{
		Assert.Equal(JsonValueKind.Object, libraries.ValueKind);
		JsonProperty[] properties = libraries.EnumerateObject().ToArray();
		SortJsonProperties(properties);
		var names = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		manifest.Append('{');
		for (int index = 0; index < properties.Length; index++)
		{
			JsonProperty library = properties[index];
			Assert.True(names.Add(library.Name), $"Duplicate project-assets library identity: {library.Name}");
			Assert.True(aliases.Add(library.Name), $"Duplicate or case-aliased project-assets library identity: {library.Name}");
			int separator = library.Name.LastIndexOf('/');
			Assert.True(separator > 0);
			string packageId = library.Name[..separator];
			Assert.True(lockedPackages.TryGetValue(packageId, out LockedPackageAuthority locked));
			if (index != 0)
			{
				manifest.Append(',');
			}
			manifest.Append(JsonSerializer.Serialize(library.Name));
			manifest.Append(':');
			AppendCanonicalProjectAssetsLibrary(
				manifest,
				library.Name,
				library.Value,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				lockedPackages);
		}
		manifest.Append('}');
	}

	private static void AppendCanonicalProjectAssetsLibrary(
		StringBuilder manifest,
		string libraryIdentity,
		JsonElement library,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages)
	{
		Assert.Equal(JsonValueKind.Object, library.ValueKind);
		JsonProperty[] properties = library.EnumerateObject().ToArray();
		SortJsonProperties(properties);
		var names = new HashSet<string>(StringComparer.Ordinal);
		bool first = true;
		bool injectedHash = false;
		manifest.Append('{');
		foreach (JsonProperty property in properties)
		{
			Assert.True(names.Add(property.Name), $"Duplicate project-assets library property: {libraryIdentity}/{property.Name}");
			if (property.Name == "sha512")
			{
				continue;
			}
			if (!injectedHash && StringComparer.Ordinal.Compare("sha512", property.Name) < 0)
			{
				AppendCanonicalProjectAssetsInjectedHash(manifest, ref first);
				injectedHash = true;
			}
			if (!first)
			{
				manifest.Append(',');
			}
			first = false;
			manifest.Append(JsonSerializer.Serialize(property.Name));
			manifest.Append(':');
			if (property.Name == "files")
			{
				string packagePath = Assert.IsType<string>(library.GetProperty("path").GetString());
				AppendCanonicalProjectAssetsLibraryFiles(manifest, property.Value, packagePath);
			}
			else
			{
				AppendCanonicalProjectAssetsJson(
					manifest,
					property.Value,
					$"$.libraries.{libraryIdentity}.{property.Name}",
					repositoryRoot,
					dotnetRoot,
					packageAuthority,
					lockedPackages);
			}
		}
		if (!injectedHash)
		{
			AppendCanonicalProjectAssetsInjectedHash(manifest, ref first);
		}
		manifest.Append('}');
	}

	private static void AppendCanonicalProjectAssetsInjectedHash(
		StringBuilder manifest,
		ref bool first)
	{
		if (!first)
		{
			manifest.Append(',');
		}
		first = false;
		manifest.Append("\"sha512\":");
		manifest.Append(JsonSerializer.Serialize("{VALIDATED_PACKAGE_CONTENT_AUTHORITY}"));
	}

	private static void AppendCanonicalProjectAssetsLibraryFiles(
		StringBuilder manifest,
		JsonElement files,
		string packagePath)
	{
		const string ValidatedProfileMarker = "{VALIDATED_PACKAGE_TRANSPORT_PROFILE}";
		string expectedSidecar = packagePath.Replace('/', '.') + ".nupkg.sha512";
		var normalizedFiles = new List<string>();
		foreach (JsonElement fileElement in files.EnumerateArray())
		{
			string file = Assert.IsType<string>(fileElement.GetString());
			if (file != ".signature.p7s" && file != ".nix-patched" && file != expectedSidecar)
			{
				normalizedFiles.Add(file);
			}
		}
		normalizedFiles.Sort(StringComparer.Ordinal);
		Assert.DoesNotContain(ValidatedProfileMarker, normalizedFiles);
		manifest.Append('[');
		manifest.Append(JsonSerializer.Serialize(ValidatedProfileMarker));
		foreach (string file in normalizedFiles)
		{
			manifest.Append(',');
			manifest.Append(JsonSerializer.Serialize(file));
		}
		manifest.Append(']');
	}

	private static void AssertProjectAssetsConfigFileTopology(JsonElement configFiles, string repositoryRoot)
	{
		Assert.Equal(JsonValueKind.Array, configFiles.ValueKind);
		string[] paths = configFiles.EnumerateArray().Select(item =>
		{
			Assert.Equal(JsonValueKind.String, item.ValueKind);
			return Path.GetFullPath(Assert.IsType<string>(item.GetString()));
		}).ToArray();
		Assert.InRange(paths.Length, 1, 2);
		Assert.True(PackagePathComparer.Equals(
			paths[0],
			Path.GetFullPath(Path.Combine(repositoryRoot, "NuGet.Config"))));
		if (paths.Length == 2)
		{
			Assert.EndsWith(
				"/.nuget/NuGet/NuGet.Config",
				paths[1].Replace('\\', '/'),
				StringComparison.OrdinalIgnoreCase);
		}
	}

	private static void AssertProjectAssetsRestoreSources(
		JsonElement sources,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		IReadOnlyDictionary<string, LockedPackageAuthority> lockedPackages)
	{
		Assert.Equal(JsonValueKind.Object, sources.ValueKind);
		bool pinnedNixProfile = lockedPackages.Values.All(package => package.ContentHash is null);
		string packageParent = Directory.GetParent(packageAuthority.PrimaryRoot)?.FullName ??
			throw new Xunit.Sdk.XunitException("The primary package root has no parent.");
		string expectedOfflineSource = Path.Combine(packageParent, "source");
		string expectedLibraryPacks = Path.Combine(dotnetRoot, "library-packs");
		string[] expectedSources = pinnedNixProfile
			? [expectedOfflineSource, expectedLibraryPacks]
			: ["https://api.nuget.org/v3/index.json"];
		AssertExactJsonProperties(sources, expectedSources);
		foreach (JsonProperty source in sources.EnumerateObject())
		{
			Assert.Equal(JsonValueKind.Object, source.Value.ValueKind);
			Assert.Empty(source.Value.EnumerateObject());
		}
		if (pinnedNixProfile)
		{
			AssertRegularAuthorityDirectory(expectedOfflineSource, "pinned-Nix offline restore source");
			AssertRegularAuthorityDirectory(expectedLibraryPacks, "SDK library-packs restore source");
		}
	}

	private static string NormalizeProjectAssetsString(
		string value,
		string jsonPath,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string? expectedPinnedNixProjectVersion)
	{
		const string ValidatedPinnedNixProjectVersionMarker =
			"{VALIDATED_PINNED_NIX_PROJECT_VERSION}";
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.version"))
		{
			if (expectedPinnedNixProjectVersion is null)
			{
				Assert.NotEqual(ValidatedPinnedNixProjectVersionMarker, value);
				return value;
			}
			Assert.Matches(
				"^2\\.0\\.0-[0-9]{8}-[0-9a-f]{40}$",
				expectedPinnedNixProjectVersion);
			Assert.Equal(expectedPinnedNixProjectVersion, value);
			return ValidatedPinnedNixProjectVersionMarker;
		}
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.packagesPath"))
		{
			Assert.True(PackagePathComparer.Equals(
				value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				packageAuthority.PrimaryRoot));
			return "{NUGET_PRIMARY}";
		}
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.projectUniqueName") ||
			StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.projectPath") ||
			StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.outputPath"))
		{
			return NormalizeAuthorityPath(value, repositoryRoot, dotnetRoot, packageAuthority);
		}
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.configFilePaths[]"))
		{
			string normalized = Path.GetFullPath(value).Replace('\\', '/');
			const string UserConfigSuffix = "/.nuget/NuGet/NuGet.Config";
			if (normalized.EndsWith(UserConfigSuffix, StringComparison.OrdinalIgnoreCase))
			{
				return "{HOME}" + UserConfigSuffix;
			}
			return NormalizeAuthorityPath(value, repositoryRoot, dotnetRoot, packageAuthority);
		}
		if (jsonPath.StartsWith("$.project.frameworks.", StringComparison.Ordinal) &&
			jsonPath.EndsWith(".runtimeIdentifierGraphPath", StringComparison.Ordinal))
		{
			string fullPath = Path.GetFullPath(value);
			Assert.True(IsPathWithin(fullPath, dotnetRoot));
			return $"DOTNET|{NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, fullPath))}";
		}
		return value;
	}

	private static void AssertProjectAssetsFallbackFolders(
		JsonElement fallbackFolders,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		Assert.Equal(JsonValueKind.Array, fallbackFolders.ValueKind);
		int index = 1;
		foreach (JsonElement fallback in fallbackFolders.EnumerateArray())
		{
			Assert.Equal(JsonValueKind.String, fallback.ValueKind);
			Assert.True(index < packageAuthority.OrderedRoots.Length);
			Assert.True(PackagePathComparer.Equals(
				ParseCanonicalPackageRoot(Assert.IsType<string>(fallback.GetString()), "project-assets fallback root"),
				packageAuthority.OrderedRoots[index]));
			index++;
		}
		Assert.Equal(packageAuthority.OrderedRoots.Length, index);
	}

	private static string BuildGeneratedNuGetSemanticManifest(
		string generatedProjectFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		AssertRegularAuthorityFile(generatedProjectFile, "generated NuGet authority");
		var settings = new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit,
			IgnoreComments = false,
			IgnoreProcessingInstructions = false,
			XmlResolver = null,
		};
		using XmlReader reader = XmlReader.Create(generatedProjectFile, settings);
		XDocument document = XDocument.Load(reader, LoadOptions.None);
		XElement root = Assert.IsType<XElement>(document.Root);
		Assert.All(
			document.Nodes().Where(node => !ReferenceEquals(node, root)),
			node => Assert.True(node is XText text && string.IsNullOrWhiteSpace(text.Value)));
		XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
		Assert.Equal(msbuild + "Project", root.Name);
		bool requiresSourceRoot = Path.GetFileName(generatedProjectFile)
			.EndsWith(".nuget.g.props", StringComparison.Ordinal);
		AssertGeneratedNuGetSourceRootTopology(root, msbuild, packageAuthority, requiresSourceRoot);
		var manifest = new StringBuilder();
		manifest.Append("NUGET_GENERATED|");
		manifest.Append(Path.GetFileName(generatedProjectFile));
		manifest.Append('|');
		AppendCanonicalGeneratedNuGetXml(
			manifest,
			root,
			msbuild,
			repositoryRoot,
			dotnetRoot,
			packageAuthority);
		return manifest.ToString();
	}

	private static void AssertGeneratedNuGetSourceRootTopology(
		XElement root,
		XNamespace msbuild,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		bool required)
	{
		XElement[] sourceRoots = root.Descendants(msbuild + "SourceRoot").ToArray();
		if (sourceRoots.Length == 0)
		{
			Assert.False(required, "Generated NuGet props must declare the validated SourceRoot topology.");
			return;
		}
		Assert.Equal(packageAuthority.OrderedRoots.Length, sourceRoots.Length);
		XElement sourceRootParent = Assert.IsType<XElement>(sourceRoots[0].Parent);
		Assert.Equal(msbuild + "ItemGroup", sourceRootParent.Name);
		Assert.Same(root, sourceRootParent.Parent);
		Assert.Equal(sourceRoots.Length, sourceRootParent.Elements().Count());
		for (int index = 0; index < sourceRoots.Length; index++)
		{
			XElement sourceRoot = sourceRoots[index];
			Assert.Same(sourceRootParent, sourceRoot.Parent);
			Assert.Same(sourceRoot, sourceRootParent.Elements().ElementAt(index));
			Assert.Empty(sourceRoot.Elements());
			Assert.True(string.IsNullOrWhiteSpace(sourceRoot.Value));
			XAttribute include = Assert.Single(sourceRoot.Attributes());
			Assert.Equal("Include", include.Name.LocalName);
			Assert.True(PackagePathComparer.Equals(
				include.Value,
				packageAuthority.OrderedRoots[index] + Path.DirectorySeparatorChar));
		}
	}

	private static void AppendCanonicalGeneratedNuGetXml(
		StringBuilder manifest,
		XElement element,
		XNamespace msbuild,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		Assert.Equal(msbuild.NamespaceName, element.Name.NamespaceName);
		Assert.NotEqual(msbuild + "VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY", element.Name);
		if (element.Name == msbuild + "SourceRoot")
		{
			if (element.ElementsBeforeSelf().Any())
			{
				return;
			}
			manifest.Append("<VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY></VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY>");
			return;
		}
		manifest.Append('<');
		manifest.Append(element.Name.LocalName);
		XAttribute[] attributes = element.Attributes().ToArray();
		SortXmlAttributes(attributes);
		string? importIdentity = null;
		string? importBytes = null;
		if (element.Name == msbuild + "Import")
		{
			XAttribute project = Assert.Single(attributes, attribute => attribute.Name.LocalName == "Project");
			(importIdentity, importBytes) = GetGeneratedNuGetImportAuthority(project.Value, packageAuthority);
		}
		foreach (XAttribute attribute in attributes)
		{
			if (attribute.IsNamespaceDeclaration)
			{
				continue;
			}
			Assert.True(string.IsNullOrEmpty(attribute.Name.NamespaceName));
			manifest.Append('|');
			manifest.Append(attribute.Name.LocalName);
			manifest.Append('=');
			string attributeValue = attribute.Value;
			if (element.Name == msbuild + "Import" && attribute.Name.LocalName == "Project")
			{
				attributeValue = Assert.IsType<string>(importIdentity);
			}
			else if (element.Name == msbuild + "Import" && attribute.Name.LocalName == "Condition")
			{
				XAttribute project = Assert.Single(attributes, candidate => candidate.Name.LocalName == "Project");
				Assert.Equal($"Exists('{project.Value}')", attributeValue);
				attributeValue = "Exists('{NUGET_IMPORT}')";
			}
			else
			{
				attributeValue = AssertGeneratedNuGetStableValue(
					attributeValue,
					repositoryRoot,
					dotnetRoot,
					packageAuthority);
			}
			manifest.Append(JsonSerializer.Serialize(attributeValue));
		}
		if (importBytes is not null)
		{
			manifest.Append("|SELECTED_SHA256=");
			manifest.Append(importBytes);
		}
		manifest.Append('>');

		XNode[] nodes = element.Nodes().ToArray();
		bool hasElements = element.HasElements;
		foreach (XNode node in nodes)
		{
			if (node is XElement child)
			{
				AppendCanonicalGeneratedNuGetXml(
					manifest,
					child,
					msbuild,
					repositoryRoot,
					dotnetRoot,
					packageAuthority);
			}
			else if (node is XText text)
			{
				Assert.True(string.IsNullOrWhiteSpace(text.Value) || !hasElements);
			}
			else
			{
				throw new Xunit.Sdk.XunitException($"Unsupported generated NuGet XML node: {node.NodeType}");
			}
		}
		if (!hasElements)
		{
			string semanticValue = element.Value;
			if (element.Name == msbuild + "NuGetPackageRoot")
			{
				Assert.True(PackagePathComparer.Equals(
					semanticValue.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
					packageAuthority.PrimaryRoot));
				semanticValue = "{NUGET_PRIMARY}";
			}
			else if (element.Name == msbuild + "NuGetPackageFolders")
			{
				AssertGeneratedNuGetPackageFolders(semanticValue, packageAuthority);
				semanticValue = "{VALIDATED_PACKAGE_ROOT_TOPOLOGY}";
			}
			else if (element.Name.LocalName.StartsWith("Pkg", StringComparison.Ordinal))
			{
				semanticValue = NormalizePackageDirectoryIdentity(semanticValue, packageAuthority);
			}
			else
			{
				semanticValue = AssertGeneratedNuGetStableValue(
					semanticValue,
					repositoryRoot,
					dotnetRoot,
					packageAuthority);
			}
			manifest.Append(JsonSerializer.Serialize(semanticValue));
		}
		manifest.Append("</");
		manifest.Append(element.Name.LocalName);
		manifest.Append('>');
	}

	private static (string Identity, string Sha256) GetGeneratedNuGetImportAuthority(
		string project,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		const string NuGetPackageRootToken = "$(NuGetPackageRoot)";
		string relativePath;
		string? selectedPath = null;
		string normalizedProject = project.Replace('\\', '/');
		if (normalizedProject.StartsWith(NuGetPackageRootToken, StringComparison.Ordinal))
		{
			// NuGet emits the token with or without a trailing slash depending on whether the
			// configured packages path itself ends with a directory separator; accept both.
			relativePath = normalizedProject[NuGetPackageRootToken.Length..].TrimStart('/');
		}
		else
		{
			string fullPath = Path.GetFullPath(project);
			string? selectedRoot = GetContainingPackageRoot(fullPath, packageAuthority);
			Assert.NotNull(selectedRoot);
			relativePath = NormalizeRelativePath(Path.GetRelativePath(selectedRoot, fullPath));
			selectedPath = fullPath;
		}
		AssertSafePackageRelativePath(relativePath);
		if (selectedPath is null)
		{
			foreach (string packageRoot in packageAuthority.OrderedRoots)
			{
				string candidate = Path.GetFullPath(Path.Combine(
					packageRoot,
					relativePath.Replace('/', Path.DirectorySeparatorChar)));
				if (File.Exists(candidate))
				{
					selectedPath = candidate;
					break;
				}
			}
		}
		Assert.NotNull(selectedPath);
		AssertPackageShadowConsistency(selectedPath, relativePath, packageAuthority);
		return ($"NUGET|{relativePath}", Sha256File(selectedPath));
	}

	[Fact]
	public void GeneratedNuGetImportAuthorityAcceptsPackageRootTokenWithAndWithoutTrailingSlash()
	{
		string fixtureRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-nuget-import-authority-{Guid.NewGuid():N}");
		try
		{
			string packageRoot = Path.Combine(fixtureRoot, "packages");
			string packageDirectory = Path.Combine(packageRoot, "avalonia", "11.3.14", "buildTransitive");
			Directory.CreateDirectory(packageDirectory);
			string propsFile = Path.Combine(packageDirectory, "Avalonia.props");
			File.WriteAllBytes(propsFile, [1, 2, 3, 4]);
			var packageAuthority = (PrimaryRoot: packageRoot, OrderedRoots: new[] { packageRoot });

			const string RelativePath = "avalonia/11.3.14/buildTransitive/Avalonia.props";
			(string slashIdentity, string slashSha256) = GetGeneratedNuGetImportAuthority(
				$"$(NuGetPackageRoot)/{RelativePath}",
				packageAuthority);
			(string noSlashIdentity, string noSlashSha256) = GetGeneratedNuGetImportAuthority(
				$"$(NuGetPackageRoot){RelativePath}",
				packageAuthority);

			Assert.Equal($"NUGET|{RelativePath}", slashIdentity);
			Assert.Equal(slashIdentity, noSlashIdentity);
			Assert.Equal(slashSha256, noSlashSha256);
		}
		finally
		{
			Directory.Delete(fixtureRoot, recursive: true);
		}
	}

	private static string NormalizePackageDirectoryIdentity(
		string path,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string fullPath = Path.GetFullPath(path);
		string? packageRoot = GetContainingPackageRoot(fullPath, packageAuthority);
		Assert.NotNull(packageRoot);
		AssertRegularAuthorityDirectory(fullPath, "generated NuGet package directory");
		string relativePath = NormalizeRelativePath(Path.GetRelativePath(packageRoot, fullPath));
		AssertSafePackageRelativePath(relativePath);
		return $"NUGET|{relativePath}";
	}

	private static string? GetContainingPackageRoot(
		string path,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string fullPath = Path.GetFullPath(path);
		string? result = null;
		foreach (string packageRoot in packageAuthority.OrderedRoots)
		{
			if (!IsPathWithin(fullPath, packageRoot))
			{
				continue;
			}
			Assert.Null(result);
			result = packageRoot;
		}
		return result;
	}

	private static void AssertGeneratedNuGetPackageFolders(
		string value,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string[] folders = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(packageAuthority.OrderedRoots.Length, folders.Length);
		for (int index = 0; index < folders.Length; index++)
		{
			Assert.True(PackagePathComparer.Equals(
				folders[index].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				packageAuthority.OrderedRoots[index]));
		}
	}

	private static string AssertGeneratedNuGetStableValue(
		string value,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string normalizedValue = value.Replace('\\', '/');
		foreach (string packageRoot in packageAuthority.OrderedRoots)
		{
			Assert.DoesNotContain(
				Path.GetFullPath(packageRoot).Replace('\\', '/').TrimEnd('/'),
				normalizedValue,
				StringComparison.Ordinal);
		}
		Assert.DoesNotContain(
			Path.GetFullPath(repositoryRoot).Replace('\\', '/').TrimEnd('/'),
			normalizedValue,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Path.GetFullPath(dotnetRoot).Replace('\\', '/').TrimEnd('/'),
			normalizedValue,
			StringComparison.Ordinal);
		return value;
	}

	private static void AssertSafePackageRelativePath(string value)
	{
		Assert.False(string.IsNullOrWhiteSpace(value));
		Assert.DoesNotContain('\\', value);
		Assert.False(Path.IsPathFullyQualified(value));
		string normalized = NormalizeRelativePath(value);
		Assert.Equal(value, normalized);
		Assert.All(value.Split('/'), component =>
			Assert.False(component is "" or "." or ".."));
	}

	private static void AssertExactJsonProperties(JsonElement value, string[] expected)
	{
		Assert.Equal(JsonValueKind.Object, value.ValueKind);
		var actual = new List<string>();
		var unique = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty property in value.EnumerateObject())
		{
			Assert.True(unique.Add(property.Name), $"Duplicate JSON property: {property.Name}");
			actual.Add(property.Name);
		}
		Assert.Equal(expected, actual);
	}

	private static void SortJsonProperties(JsonProperty[] properties)
	{
		for (int index = 1; index < properties.Length; index++)
		{
			JsonProperty current = properties[index];
			int insertion = index;
			while (insertion > 0 && StringComparer.Ordinal.Compare(properties[insertion - 1].Name, current.Name) > 0)
			{
				properties[insertion] = properties[insertion - 1];
				insertion--;
			}
			properties[insertion] = current;
		}
	}

	private static void SortXmlAttributes(XAttribute[] attributes)
	{
		for (int index = 1; index < attributes.Length; index++)
		{
			XAttribute current = attributes[index];
			int insertion = index;
			while (insertion > 0 && StringComparer.Ordinal.Compare(
				attributes[insertion - 1].Name.ToString(), current.Name.ToString()) > 0)
			{
				attributes[insertion] = attributes[insertion - 1];
				insertion--;
			}
			attributes[insertion] = current;
		}
	}

	private static string Sha256Text(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private static void AssertRegularAuthorityFile(string path, string description)
	{
		Assert.True(File.Exists(path), $"The {description} is absent: {path}");
		AssertAuthorityPathHasNoSymbolicLinks(path, description);
	}

	private static string EscapeMsbuildPropertyValue(string value)
	{
		string escaped = value.Replace("%", "%25", StringComparison.Ordinal);
		return escaped.IndexOfAny([';', ',']) >= 0 ? $"\"{escaped}\"" : escaped;
	}

	private static (string DotnetHost, string DotnetRoot) GetApprovedDotnetHost()
	{
		string runtimeDirectory = GetLoadedRuntimeDirectory();
		DirectoryInfo? dotnetRootDirectory = new DirectoryInfo(runtimeDirectory).Parent?.Parent?.Parent;
		Assert.NotNull(dotnetRootDirectory);
		string dotnetRoot = Path.GetFullPath(dotnetRootDirectory.FullName);
		string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
		string dotnetHost = Path.GetFullPath(Path.Combine(dotnetRoot, executableName));
		AssertApprovedDotnetHost(dotnetHost, dotnetRoot, runtimeDirectory);
		return (dotnetHost, dotnetRoot);
	}

	private static (
		string Version,
		string AssemblyVersion,
		string FileVersion,
		string InformationalVersion,
		string CommitHash)
		GetLoadedProductBuildIdentity(bool pinnedNixProfile)
	{
		Assembly productAssembly = typeof(LiquidOrdinaryWalletPlanEncoder).Assembly;
		string assemblyVersion = productAssembly.GetName().Version?.ToString() ??
			throw new Xunit.Sdk.XunitException("The loaded product assembly version is absent.");
		string fileVersion = Assert.Single(
			productAssembly.GetCustomAttributes<AssemblyFileVersionAttribute>()).Version;
		AssemblyInformationalVersionAttribute informationalVersion = Assert.Single(
			productAssembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>());
		string value = informationalVersion.InformationalVersion;
		string? commitHash = null;
		foreach (AssemblyMetadataAttribute metadata in productAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
		{
			if (!StringComparer.Ordinal.Equals(metadata.Key, "CommitHash"))
			{
				continue;
			}
			Assert.Null(commitHash);
			commitHash = metadata.Value;
		}
		Assert.NotNull(commitHash);
		AssertLoadedProductBuildIdentityAuthority(
			assemblyVersion,
			fileVersion,
			value,
			commitHash,
			pinnedNixProfile);
		string productVersion = RemoveSdkSourceRevisionSuffix(
			value,
			commitHash,
			TryReadCurrentRepositoryRevision());
		Assert.Matches(
			"^[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*(?:\\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
			productVersion);
		return (assemblyVersion, assemblyVersion, fileVersion, value, commitHash);
	}

	private static string RemoveSdkSourceRevisionSuffix(
		string informationalVersion,
		string commitHash,
		string? currentRepositoryRevision)
	{
		Assert.True(
			currentRepositoryRevision is null ||
			Regex.IsMatch(currentRepositoryRevision, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant),
			"The current repository revision evidence is not a full lowercase Git identity.");
		int metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
		if (metadataSeparator < 0)
		{
			return informationalVersion;
		}
		string metadata = informationalVersion[(metadataSeparator + 1)..];
		int revisionSeparator = metadata.LastIndexOf('.');
		string revision = revisionSeparator < 0 ? metadata : metadata[(revisionSeparator + 1)..];
		if (!Regex.IsMatch(revision, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
		{
			return informationalVersion;
		}
		if (Regex.IsMatch(commitHash, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
		{
			Assert.Equal(commitHash, revision);
		}
		if (!StringComparer.Ordinal.Equals(currentRepositoryRevision, revision))
		{
			return informationalVersion;
		}
		return revisionSeparator < 0
			? informationalVersion[..metadataSeparator]
			: informationalVersion[..(metadataSeparator + 1 + revisionSeparator)];
	}

	private static void AssertApprovedDotnetHost(
		string candidate,
		string dotnetRoot,
		string loadedRuntimeDirectory)
	{
		string canonicalRoot = Path.GetFullPath(dotnetRoot);
		string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
		string expected = Path.GetFullPath(Path.Combine(canonicalRoot, executableName));
		Assert.Equal(expected, Path.GetFullPath(candidate));
		AssertRegularAuthorityFile(expected, "running runtime's canonical dotnet host");
		AssertExactDotnetVersionDirectories(canonicalRoot, "sdk", PinnedDotnetSdkVersion);
		AssertExactDotnetVersionDirectories(canonicalRoot, "host/fxr", PinnedDotnetHostFxrVersion);
		AssertExactDotnetVersionDirectories(
			canonicalRoot,
			"shared/Microsoft.NETCore.App",
			PinnedDotnetRuntimeVersion);
		string sdkRoot = Path.Combine(canonicalRoot, "sdk", PinnedDotnetSdkVersion);
		string hostFxrRoot = Path.Combine(canonicalRoot, "host/fxr", PinnedDotnetHostFxrVersion);
		string runtimeRoot = Path.Combine(
			canonicalRoot,
			"shared/Microsoft.NETCore.App",
			PinnedDotnetRuntimeVersion);
		Assert.Equal(Path.GetFullPath(runtimeRoot), Path.GetFullPath(loadedRuntimeDirectory));
		AssertRegularAuthorityDirectory(loadedRuntimeDirectory, "loaded pinned runtime");
		AssertRegularAuthorityFile(Path.Combine(sdkRoot, "MSBuild.dll"), "pinned MSBuild entry point");
		AssertRegularAuthorityFile(
			Path.Combine(sdkRoot, "Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props"),
			"pinned .NET SDK entry point");
		AssertRegularAuthorityFile(
			Path.Combine(hostFxrRoot, GetPinnedHostFxrFileName()),
			"pinned hostfxr entry point");
		AssertRegularAuthorityFile(
			Path.Combine(runtimeRoot, "System.Private.CoreLib.dll"),
			"loaded pinned runtime core library");
		AssertRegularAuthorityFile(
			Path.Combine(runtimeRoot, GetPinnedHostPolicyFileName()),
			"pinned hostpolicy entry point");
	}

	private static void AssertExactBuildAuthority(
		IReadOnlyDictionary<string, string> properties,
		string dotnetRoot,
		string productionRoot,
		string generatedRoot)
	{
#if DEBUG
		const string ExpectedConfiguration = "Debug";
		const string ExpectedDefineConstants =
			"TRACE;DEBUG;NET;NET10_0;NETCOREAPP;NET5_0_OR_GREATER;NET6_0_OR_GREATER;" +
			"NET7_0_OR_GREATER;NET8_0_OR_GREATER;NET9_0_OR_GREATER;NET10_0_OR_GREATER;" +
			"NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;" +
			"NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;" +
			"NETCOREAPP3_1_OR_GREATER";
#else
		const string ExpectedConfiguration = "Release";
		const string ExpectedDefineConstants =
			"TRACE;RELEASE;NET;NET10_0;NETCOREAPP;NET5_0_OR_GREATER;NET6_0_OR_GREATER;" +
			"NET7_0_OR_GREATER;NET8_0_OR_GREATER;NET9_0_OR_GREATER;NET10_0_OR_GREATER;" +
			"NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;" +
			"NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;" +
			"NETCOREAPP3_1_OR_GREATER";
#endif
		string repositoryRoot = Path.GetDirectoryName(Path.GetFullPath(productionRoot))!;
		string authorityRoot = Path.GetDirectoryName(Path.GetFullPath(generatedRoot))!;
		string projectAssetsFile = Path.GetFullPath(Path.Combine(productionRoot, "obj/project.assets.json"));
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority = GetPinnedPackageAuthority(projectAssetsFile);
		bool pinnedNixProfile = !ReadLockedPackageAuthority(
			Path.Combine(productionRoot, "packages.lock.json"),
			"net10.0").HasContentHashes;
		string packageRoot = packageAuthority.PrimaryRoot;
		string sdkRoot = Path.Combine(dotnetRoot, "sdk", PinnedDotnetSdkVersion);
		string roslynRoot = Path.Combine(sdkRoot, "Roslyn");
		string outputPath = Path.Combine(authorityRoot, "bin") + Path.DirectorySeparatorChar;
		string intermediateOutputPath = Path.Combine(authorityRoot, "obj/net10.0") + Path.DirectorySeparatorChar;
		string baseOutputPath = Path.Combine(authorityRoot, "base-bin") + Path.DirectorySeparatorChar;
		string baseIntermediateOutputPath = Path.Combine(authorityRoot, "obj") + Path.DirectorySeparatorChar;
		var buildIdentity = GetLoadedProductBuildIdentity(pinnedNixProfile);
		var expected = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["MSBuildProjectDirectory"] = Path.GetFullPath(productionRoot),
			["Configuration"] = ExpectedConfiguration,
			["Version"] = buildIdentity.Version,
			["AssemblyVersion"] = buildIdentity.AssemblyVersion,
			["FileVersion"] = buildIdentity.FileVersion,
			["InformationalVersion"] = buildIdentity.InformationalVersion,
			["IncludeSourceRevisionInInformationalVersion"] = "false",
			["CommitHash"] = buildIdentity.CommitHash,
			["Platform"] = "AnyCPU",
			["TargetFramework"] = "net10.0",
			["TargetFrameworkIdentifier"] = ".NETCoreApp",
			["TargetFrameworkVersion"] = "v10.0",
			["TargetFrameworks"] = "",
			["RuntimeIdentifier"] = "",
			["RuntimeIdentifiers"] = "",
			["NETCoreSdkVersion"] = PinnedDotnetSdkVersion,
			["MSBuildVersion"] = "18.0.2",
			["LangVersion"] = "14",
			["DefineConstants"] = ExpectedDefineConstants,
			["AllowUnsafeBlocks"] = "true",
			["BaseIntermediateOutputPath"] = baseIntermediateOutputPath,
			["IntermediateOutputPath"] = intermediateOutputPath,
			["BaseOutputPath"] = baseOutputPath,
			["OutputPath"] = outputPath,
			["PathMap"] = $"{generatedRoot}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{ExpectedConfiguration}/net10.0/," +
				$"{intermediateOutputPath}=WalletWasabi/obj/{ExpectedConfiguration}/net10.0/," +
				$"{productionRoot}{Path.DirectorySeparatorChar}=WalletWasabi",
			["DefaultExcludesInProjectFolder"] = "bin/**;obj/**;**/.*/**",
			["MSBuildProjectExtensionsPath"] = Path.GetFullPath(Path.Combine(productionRoot, "obj")) +
				Path.DirectorySeparatorChar,
			["EmitCompilerGeneratedFiles"] = "true",
			["CompilerGeneratedFilesOutputPath"] = Path.GetFullPath(generatedRoot),
			["ProjectAssetsFile"] = projectAssetsFile,
			["BuildProjectReferences"] = "false",
			["UseSharedCompilation"] = "false",
			["UseHostCompilerIfAvailable"] = "false",
			["ProvideCommandLineArgs"] = "true",
			["RestoreDuringBuild"] = "false",
			["RestorePackagesPath"] = packageRoot,
			["NuGetPackageRoot"] = packageRoot + Path.DirectorySeparatorChar,
			["DisableImplicitNuGetFallbackFolder"] = "true",
			["ImportDirectoryBuildProps"] = "true",
			["DirectoryBuildPropsPath"] = Path.Combine(repositoryRoot, "Directory.Build.props"),
			["ImportDirectoryBuildTargets"] = "false",
			["DirectoryBuildTargetsPath"] = "",
			["CustomBeforeDirectoryBuildProps"] = "",
			["CustomAfterDirectoryBuildProps"] = "",
			["CustomBeforeDirectoryBuildTargets"] = "",
			["CustomAfterDirectoryBuildTargets"] = "",
			["ImportProjectExtensionProps"] = "true",
			["ImportProjectExtensionTargets"] = "true",
			["ImportByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["CustomBeforeMicrosoftCommonProps"] = "",
			["CustomAfterMicrosoftCommonProps"] = "",
			["CustomBeforeMicrosoftCommonTargets"] = "",
			["CustomAfterMicrosoftCommonTargets"] = "",
			["CustomBeforeMicrosoftCSharpTargets"] = "",
			["CustomAfterMicrosoftCSharpTargets"] = "",
			["MSBuildUserExtensionsPath"] = Path.Combine(authorityRoot, "disabled-imports"),
			["MSBuildToolsPath"] = Path.GetFullPath(sdkRoot),
			["MSBuildSDKsPath"] = Path.GetFullPath(Path.Combine(sdkRoot, "Sdks")),
			["RoslynTargetsPath"] = Path.GetFullPath(roslynRoot),
			["CSharpCoreTargetsPath"] = Path.GetFullPath(Path.Combine(roslynRoot, "Microsoft.CSharp.Core.targets")),
			["CscToolPath"] = "",
			["CscToolExe"] = "",
			["MSBuildDisableAllAutoResponseFiles"] = "true",
		};
		string[] targetListProperties = ["CompileDependsOn", "CoreCompileDependsOn", "TargetsTriggeredByCompilation"];
		Assert.Equal(
			expected.OrderBy(pair => pair.Key),
			properties.Where(pair => !targetListProperties.Contains(pair.Key, StringComparer.Ordinal))
				.OrderBy(pair => pair.Key));
		Assert.Equal(
			new[]
			{
				"ResolveReferences", "ResolveKeySource", "SetWin32ManifestProperties",
				"_SetPreferNativeArm64Win32ManifestProperties", "FindReferenceAssembliesForReferences",
				"_GenerateCompileInputs", "BeforeCompile", "_TimeStampBeforeCompile",
				"_GenerateCompileDependencyCache", "CoreCompile", "_TimeStampAfterCompile",
				"AfterCompile", "_CreateAppHost", "_CreateComHost", "_GetIjwHostPaths",
			},
			SplitTargetList(properties["CompileDependsOn"]));
		Assert.Equal(
			new[] { "_ComputeNonExistentFileProperty", "ResolveCodeAnalysisRuleSet" },
			SplitTargetList(properties["CoreCompileDependsOn"]));
		Assert.Empty(SplitTargetList(properties["TargetsTriggeredByCompilation"]));

		Assembly productionAssembly = typeof(LiquidOrdinaryWalletPlanEncoder).Assembly;
		AssemblyConfigurationAttribute configuration = Assert.Single(
			productionAssembly.GetCustomAttributes<AssemblyConfigurationAttribute>());
		TargetFrameworkAttribute framework = Assert.Single(
			productionAssembly.GetCustomAttributes<TargetFrameworkAttribute>());
		Assert.Equal(ExpectedConfiguration, configuration.Configuration);
		Assert.Equal(".NETCoreApp,Version=v10.0", framework.FrameworkName);
		Assert.Equal(10, Environment.Version.Major);
	}

	private static string[] SplitTargetList(string value) =>
		value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private static IReadOnlyDictionary<string, string> MutateBuildProperty(
		IReadOnlyDictionary<string, string> properties,
		string name,
		string value)
	{
		var mutated = new Dictionary<string, string>(properties, StringComparer.Ordinal)
		{
			[name] = value,
		};
		return mutated;
	}

	private static void AssertExactAmbientCompileAuthority(
		IEnumerable<(string FullPath, string RelativePath, string Source)> compileInputs)
	{
		string[] actual = GetAmbientCompileAuthority(compileInputs);

		string[] expected =
		[
			"ATTRIBUTE|AssemblyInfo.cs|assembly|InternalsVisibleTo",
			"ATTRIBUTE|obj/{configuration}/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs|assembly|global::System.Runtime.Versioning.TargetFrameworkAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyCompanyAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyConfigurationAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyFileVersionAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyInformationalVersionAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyMetadata",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyProductAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyTitleAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyVersionAttribute",
			"GLOBAL_USING|GlobalUsings.cs|global using System;",
			"GLOBAL_USING|GlobalUsings.cs|global using static WalletWasabi.Models.Height;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WabiSabi.Crypto.ZeroKnowledge;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.Blockchain.TransactionOutputs;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.Helpers;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.Logging;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Models;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Rounds;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Models.MultipartyTransaction;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Models;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Manager/GlobalUsings.cs|global using WalletWasabi.Wallets;",
			"GLOBAL_USING|WabiSabi/Client/GlobalUsings.cs|global using WalletWasabi.Blockchain.Keys;",
			"GLOBAL_USING|WabiSabi/Client/GlobalUsings.cs|global using WalletWasabi.Extensions;",
			"GLOBAL_USING|WabiSabi/Coordinator/GlobalUsings.cs|global using WalletWasabi.Logging;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using NBitcoin;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Collections.Generic;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Collections.Immutable;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Linq;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Threading.Tasks;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Threading;",
			"GLOBAL_USING|WabiSabi/Models/GlobalUsings.cs|global using WabiSabi.CredentialRequesting;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.Crypto;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.Extensions;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.Helpers;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Models;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Rounds;",
			"MODULE_INITIALIZER|ModuleInitializer.cs|ModuleInitializer.PatchTestNet",
		];
		Assert.True(
			expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)),
			string.Join('\n', actual.Order(StringComparer.Ordinal)));
	}

	private static string[] GetAmbientCompileAuthority(
		IEnumerable<(string FullPath, string RelativePath, string Source)> compileInputs)
	{
		var actual = new List<string>();
		foreach ((string _, string relativePath, string source) in compileInputs)
		{
			string normalizedPath = NormalizeAmbientPath(relativePath);
			CompilationUnitSyntax root = Assert.IsType<CompilationUnitSyntax>(
				CSharpSyntaxTree.ParseText(source).GetRoot());
			foreach (AttributeListSyntax list in root.AttributeLists)
			{
				string? target = list.Target?.Identifier.ValueText;
				if (target is "assembly" or "module")
				{
					actual.AddRange(list.Attributes.Select(attribute =>
						$"ATTRIBUTE|{normalizedPath}|{target}|{attribute.Name}"));
				}
			}
			foreach (UsingDirectiveSyntax directive in root.Usings.Where(usingDirective =>
				usingDirective.GlobalKeyword.RawKind != (int)SyntaxKind.None))
			{
				actual.Add($"GLOBAL_USING|{normalizedPath}|{NormalizeSyntax(directive.ToString())}");
			}
			foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
			{
				if (method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
					attribute.Name.ToString() is "ModuleInitializer" or "ModuleInitializerAttribute" or
					"System.Runtime.CompilerServices.ModuleInitializer" or
					"System.Runtime.CompilerServices.ModuleInitializerAttribute"))
				{
					string declaringType = method.Ancestors().OfType<BaseTypeDeclarationSyntax>()
						.First().Identifier.ValueText;
					actual.Add($"MODULE_INITIALIZER|{normalizedPath}|{declaringType}.{method.Identifier.ValueText}");
				}
			}
		}

		return actual.ToArray();
	}

	private static string NormalizeAmbientPath(string relativePath)
	{
		string normalized = NormalizeRelativePath(relativePath);
		if (normalized.EndsWith(
			"/obj/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs",
			StringComparison.Ordinal))
		{
			return "obj/{configuration}/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs";
		}
		if (normalized.EndsWith(
			"/obj/net10.0/WalletWasabi.AssemblyInfo.cs",
			StringComparison.Ordinal))
		{
			return "obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs";
		}
		return normalized
			.Replace("obj/Debug/", "obj/{configuration}/", StringComparison.Ordinal)
			.Replace("obj/Release/", "obj/{configuration}/", StringComparison.Ordinal);
	}

	private static void AssertExactAnalyzerAuthority(
		IEnumerable<(string FullPath, string DefiningProjectFullPath)> analyzers,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		int sourceIndex = -1;
		List<(string Identity, string ContentSha256, string Provenance)> entries = analyzers.Select(
			analyzer =>
		{
			sourceIndex++;
			try
			{
				string definingProject = Path.GetFullPath(analyzer.DefiningProjectFullPath);
				Assert.True(IsPathWithin(definingProject, dotnetRoot));
				AssertRegularAuthorityFile(definingProject, "analyzer provenance");
				string provenance = "DOTNET|" +
					NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, definingProject));
				string fullPath = Path.GetFullPath(analyzer.FullPath);
				AssertRegularAuthorityFile(fullPath, "analyzer");
				string identity;
				if (IsPathWithin(fullPath, dotnetRoot))
				{
					identity = "DOTNET|" +
						NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, fullPath));
				}
				else
				{
					Assert.True(TryNormalizePackageAuthorityPath(
						fullPath,
						packageAuthority,
						out identity));
				}
				return (identity, Sha256File(fullPath), provenance);
			}
			catch (Exception)
			{
				throw new Xunit.Sdk.XunitException(
					$"Analyzer authority input {sourceIndex:D3} was rejected.\n" +
					$"PATH_SHA256|{Sha256Text(analyzer.FullPath)}\n" +
					$"PROVENANCE_SHA256|{Sha256Text(analyzer.DefiningProjectFullPath)}");
			}
		}).ToList();

		string manifest = BuildCanonicalAnalyzerAuthorityManifest(entries);
		string[] rows = AssertCanonicalAnalyzerAuthorityManifest(manifest);
		Assert.Equal(12, rows.Length);
		string expectedSha256 = GetExpectedAnalyzerAuthoritySha256(
			OperatingSystem.IsMacOS(),
			OperatingSystem.IsLinux(),
			RuntimeInformation.OSArchitecture);
		AssertExactAnalyzerAuthoritySha256(expectedSha256, manifest);
	}

	private static bool IsPathWithin(string candidate, string root)
	{
		string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
		return !Path.IsPathRooted(relative) &&
			relative != ".." &&
			!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static void AssertExactGeneratedSourceAuthority(
		IEnumerable<GeneratedBuildFile> generatedSources)
	{
		var sources = generatedSources.OrderBy(source => source.RelativePath, StringComparer.Ordinal).ToArray();
		Assert.NotEmpty(sources);
		foreach (GeneratedBuildFile generated in sources)
		{
			string relativePath = generated.RelativePath;
			string source = generated.Source;
			Assert.Equal(NormalizeRelativePath(relativePath), relativePath);
			Assert.Matches("^[0-9a-f]{64}$", generated.Sha256);
			Assert.False(string.IsNullOrEmpty(source), $"Generated authority is not C# source: {relativePath}");
			Assert.False(IsImplementationContributor(source), $"Generated source contributes WLPQ authority: {relativePath}");
			Assert.Empty(GetAmbientCompileAuthority(
				[(Path.GetFullPath(Path.Combine(Path.GetTempPath(), relativePath)), relativePath, source)]));
		}
		string manifest = string.Join(
			'\n',
			sources.Select(source => $"{source.RelativePath}|{source.Sha256}")) + "\n";
#if DEBUG
		string expectedSha256 = ExpectedDebugGeneratedSourcesSha256;
#else
		string expectedSha256 = ExpectedReleaseGeneratedSourcesSha256;
#endif
		string actualSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedSha256, actualSha256), actualSha256);
	}

	private static void AssertExactImplementationCompileInputs(
		IEnumerable<string> expectedRelativePaths,
		string productionRoot,
		IEnumerable<(string FullPath, string RelativePath, string Source)> evaluatedCompileInputs)
	{
		string[] expectedRelative = expectedRelativePaths
			.Select(NormalizeRelativePath)
			.Order(StringComparer.Ordinal)
			.ToArray();
		string[] expectedFull = expectedRelative
			.Select(path => Path.GetFullPath(Path.Combine(productionRoot, path)))
			.Order(StringComparer.Ordinal)
			.ToArray();
		var evaluated = evaluatedCompileInputs.ToArray();
		Assert.All(evaluated, input =>
		{
			Assert.Equal(Path.GetFullPath(input.FullPath), input.FullPath);
			Assert.Equal(
				NormalizeRelativePath(Path.GetRelativePath(productionRoot, input.FullPath)),
				input.RelativePath);
		});
		var implementation = evaluated
			.Where(input => IsImplementationContributor(input.Source))
			.ToArray();
		Assert.Equal(
			expectedRelative,
			implementation.Select(input => input.RelativePath).Order(StringComparer.Ordinal));
		Assert.Equal(
			expectedFull,
			implementation.Select(input => input.FullPath).Order(StringComparer.Ordinal));
		Assert.Equal(expectedRelative.Length, implementation.Select(input => input.FullPath).Distinct().Count());
	}

	private static bool IsImplementationContributor(string source)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(source).GetRoot());
		foreach (BaseTypeDeclarationSyntax declaration in root.DescendantNodes()
			.OfType<BaseTypeDeclarationSyntax>())
		{
			string declaredNamespace = GetDeclaredNamespace(declaration);
			if (IsProductionWireNamespace(declaredNamespace))
			{
				return true;
			}
			if (StringComparer.Ordinal.Equals(declaredNamespace, "WalletWasabi.Liquid.Wallet") &&
				declaration.Identifier.ValueText == nameof(LiquidOrdinaryWalletExactSpendPlan) &&
				!declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any())
			{
				return true;
			}
		}

		return false;
	}

	private static string GetDeclaredNamespace(BaseTypeDeclarationSyntax declaration) =>
		string.Join(
			'.',
			declaration.Ancestors()
				.OfType<BaseNamespaceDeclarationSyntax>()
				.Reverse()
				.Select(namespaceDeclaration => namespaceDeclaration.Name.ToString()));

	private static MethodInfo[] GetExactPlanWireEntryPoints(IEnumerable<Type> exactWireTypes) =>
		exactWireTypes
			.SelectMany(GetDeclaredMethods)
			.SelectMany(GetIlReferences)
			.OfType<MethodInfo>()
			.Where(method => method.DeclaringType == typeof(LiquidOrdinaryWalletExactSpendPlan))
			.Distinct()
			.OrderBy(method => method.Name, StringComparer.Ordinal)
			.ThenBy(MethodIdentity, StringComparer.Ordinal)
			.ToArray();

	private static MethodBase[] AssertWireMethodClosureSafe(
		IEnumerable<MethodBase> roots,
		string? expectedRuntimeDispatchAuthoritySha256 = null)
	{
		MethodBase[] rootMethods = roots.OrderBy(MethodIdentity, StringComparer.Ordinal).ToArray();
		Assert.NotEmpty(rootMethods);
		Assembly assembly = rootMethods[0].Module.Assembly;
		Assert.All(rootMethods, method => Assert.Equal(assembly, method.Module.Assembly));
		var pending = new Stack<(MethodBase Method, bool StrictDispatch)>(
			rootMethods.Reverse().Select(method => (method, true)));
		var closure = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
		var strictMethods = new HashSet<string>(StringComparer.Ordinal);
		var unresolvedDispatches = new List<string>();
		var reviewedDispatches = new List<string>();

		void EnqueueTypeInitializer(Type? type, bool strictDispatch)
		{
			if (type?.Assembly == assembly && type.TypeInitializer is { } initializer)
			{
				pending.Push((initializer, strictDispatch));
			}
		}

		while (pending.Count > 0)
		{
			(MethodBase method, bool strictDispatch) = pending.Pop();
			string identity = MethodIdentity(method);
			bool newlyStrict = strictDispatch && strictMethods.Add(identity);
			if (!closure.TryAdd(identity, method) && !newlyStrict)
			{
				continue;
			}

			Assert.False(IsForbiddenWireMember(method), $"forbidden closure method {identity}");
			MethodBody body = method.GetMethodBody() ??
				throw new Xunit.Sdk.XunitException($"Closure method has no managed body: {identity}");
			Assert.DoesNotContain(body.ExceptionHandlingClauses, clause =>
				clause.Flags == ExceptionHandlingClauseOptions.Clause && IsForbiddenWireType(clause.CatchType));
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				Assert.False(
					IsForbiddenWireType(local.LocalType),
					$"forbidden closure local {identity} -> {TypeIdentity(local.LocalType)}");
			}
			EnqueueTypeInitializer(method.DeclaringType, strictDispatch);
			foreach ((int instructionOffset, OpCode opCode, MemberInfo? reference) in
				GetIlInstructionsWithOffsets(method))
			{
				Assert.NotEqual(OpCodes.Calli, opCode);
				Assert.NotEqual(OpCodes.Ldftn, opCode);
				Assert.NotEqual(OpCodes.Ldvirtftn, opCode);
				if (reference is null)
				{
					continue;
				}
				Assert.False(
					IsForbiddenWireMember(reference),
					$"forbidden closure reference {identity} -> {MemberIdentity(reference)}");
				if (strictDispatch)
				{
					if (IsUnresolvedRuntimeDispatch(
						method,
						instructionOffset,
						opCode,
						reference,
						out string? reviewedDispatch))
					{
						unresolvedDispatches.Add(RuntimeDispatchSite(
							method,
							instructionOffset,
							opCode,
							reference));
					}
					else if (reviewedDispatch is not null)
					{
						reviewedDispatches.Add(reviewedDispatch);
					}
				}
				Type? touchedType = reference switch
				{
					Type referencedType => referencedType,
					_ => reference.DeclaringType,
				};
				EnqueueTypeInitializer(touchedType, strictDispatch);
				if (reference is MethodBase called && called.Module.Assembly == assembly)
				{
					pending.Push((called, strictDispatch));
				}
			}
		}

		Assert.True(unresolvedDispatches.Count == 0, string.Join('\n', unresolvedDispatches));
		string reviewedDispatchManifest = string.Join(
			'\n',
			reviewedDispatches.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "\n";
		if (expectedRuntimeDispatchAuthoritySha256 is null)
		{
#if DEBUG
			expectedRuntimeDispatchAuthoritySha256 = ExpectedDebugRuntimeDispatchAuthoritySha256;
#else
			expectedRuntimeDispatchAuthoritySha256 = ExpectedReleaseRuntimeDispatchAuthoritySha256;
#endif
		}
		AssertExactSha256(expectedRuntimeDispatchAuthoritySha256, reviewedDispatchManifest);
		return closure.Values.OrderBy(MethodIdentity, StringComparer.Ordinal).ToArray();
	}

	private static bool IsUnresolvedRuntimeDispatch(
		MethodBase caller,
		int instructionOffset,
		OpCode opCode,
		MemberInfo reference,
		out string? reviewedDispatch)
	{
		reviewedDispatch = null;
		if (opCode is var instruction && (instruction == OpCodes.Ldftn || instruction == OpCodes.Ldvirtftn))
		{
			return true;
		}
		if (reference is not MethodBase method)
		{
			return false;
		}

		Type? declaringType = method.DeclaringType;
		if (declaringType is not null && typeof(Delegate).IsAssignableFrom(declaringType) &&
			method.Name == "Invoke")
		{
			return true;
		}
		if (opCode == OpCodes.Callvirt &&
			(declaringType?.IsInterface is true || method is MethodInfo { IsVirtual: true, IsFinal: false }))
		{
			string? receiverProvenance = ClassifyReviewedRuntimeReceiver(caller, instructionOffset, method);
			if (receiverProvenance is null)
			{
				return true;
			}

			reviewedDispatch = $"{RuntimeDispatchSite(caller, instructionOffset, opCode, method)}|" +
				$"RECEIVER|{receiverProvenance}";
			return false;
		}

		string identity = MemberIdentity(reference);
		return identity.Contains("System.Reflection", StringComparison.Ordinal) ||
			identity.Contains("System.Runtime.CompilerServices.CallSite", StringComparison.Ordinal) ||
			identity.Contains("Microsoft.CSharp.RuntimeBinder", StringComparison.Ordinal) ||
			identity.Contains("::DynamicInvoke(", StringComparison.Ordinal) ||
			identity.Contains("System.Activator", StringComparison.Ordinal) ||
			identity.Contains("System.Type", StringComparison.Ordinal) &&
				(method.Name.StartsWith("GetMethod", StringComparison.Ordinal) ||
					method.Name.StartsWith("GetField", StringComparison.Ordinal) ||
					method.Name.StartsWith("GetProperty", StringComparison.Ordinal) ||
					method.Name.StartsWith("GetMember", StringComparison.Ordinal) ||
					method.Name == "InvokeMember");
	}

	private static string? ClassifyReviewedRuntimeReceiver(
		MethodBase caller,
		int instructionOffset,
		MethodBase callee)
	{
		Type? declaringType = callee.DeclaringType;
		if (caller.DeclaringType?.FullName == "WalletWasabi.ModuleInitializer" &&
			caller.Name == "PatchTestNet" && declaringType == typeof(Type) &&
			callee.Name is "get_TypeHandle" or nameof(Type.GetField))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(Type) && producer.Name == nameof(Type.GetTypeFromHandle),
				"exact RuntimeType returned by Type.GetTypeFromHandle in pinned PatchTestNet IL");
		}
		if (caller.DeclaringType?.FullName == "WalletWasabi.ModuleInitializer" &&
			caller.Name == "PatchTestNet" && declaringType == typeof(FieldInfo) &&
			callee.Name == nameof(FieldInfo.GetValue))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(Type) && producer.Name == nameof(Type.GetField),
				"exact FieldInfo returned by Type.GetField in pinned PatchTestNet IL");
		}
		if (declaringType == typeof(StringComparer) &&
			callee.Name is nameof(StringComparer.Equals) or nameof(StringComparer.Compare))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(StringComparer) && producer.Name == "get_Ordinal",
				"StringComparer.Ordinal singleton");
		}
		if (declaringType == typeof(Encoding) && callee.Name == nameof(Encoding.GetBytes))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(Encoding) && producer.Name == "get_ASCII",
				"Encoding.ASCII singleton");
		}
		if (declaringType?.IsGenericType is true &&
			declaringType.GetGenericTypeDefinition() == typeof(EqualityComparer<>) &&
			callee.Name == nameof(EqualityComparer<object>.Equals))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == declaringType && producer.Name == "get_Default",
				$"EqualityComparer<{TypeIdentity(declaringType.GetGenericArguments()[0])}>.Default singleton");
		}
		if (declaringType?.IsGenericType is true &&
			(declaringType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>) ||
				declaringType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) &&
			callee.Name is "get_Count" or "get_Item")
		{
			ParameterInfo parameter = Assert.Single(
				caller.GetParameters(),
				candidate => candidate.ParameterType.IsInterface &&
					declaringType.IsAssignableFrom(candidate.ParameterType));
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => GetLoadedArgumentIndex(caller, instruction) ==
					parameter.Position + (caller.IsStatic ? 0 : 1),
				$"parameter {parameter.Position}:{parameter.Name}:{TypeIdentity(parameter.ParameterType)}");
		}
		if (declaringType == typeof(object) && callee.Name == nameof(ToString) &&
			caller.DeclaringType == typeof(LiquidAddressCodec) &&
			((caller.Name == "EncodeBase58" && instructionOffset is 0x10b or 0xdb) ||
				(caller.Name == "EncodeWitnessAddress" && instructionOffset is 0x162 or 0x13c)))
		{
			LocalVariableInfo local = Assert.Single(
				caller.GetMethodBody()?.LocalVariables ?? [],
				local => local.LocalType == typeof(StringBuilder));
			Assert.True(typeof(StringBuilder).IsSealed);
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => GetLoadedLocalIndex(caller, instruction) == local.LocalIndex,
				$"sealed System.Text.StringBuilder local {local.LocalIndex}");
		}

		return null;
	}

	private static string BuildReviewedReceiverProvenance(
		MethodBase caller,
		int callOffset,
		Func<(int Offset, OpCode OpCode, MemberInfo? Member), bool> isProducer,
		string sourceIdentity)
	{
		var instructions = GetIlInstructionsWithOffsets(caller).ToArray();
		int callIndex = Array.FindIndex(instructions, instruction => instruction.Offset == callOffset);
		Assert.True(callIndex >= 0, $"Reviewed dispatch offset is absent: {MethodIdentity(caller)} IL_{callOffset:x4}");
		int producerIndex = -1;
		for (int index = callIndex - 1; index >= 0; index--)
		{
			if (isProducer(instructions[index]))
			{
				producerIndex = index;
				break;
			}
		}
		Assert.True(
			producerIndex >= 0,
			$"Reviewed receiver producer is absent: {MethodIdentity(caller)} IL_{callOffset:x4} {sourceIdentity}\n" +
			string.Join('\n', instructions.Take(callIndex).TakeLast(20).Select(instruction =>
				$"IL_{instruction.Offset:x4}|{instruction.OpCode.Name}|" +
				(instruction.Member is null ? "NONE" : MemberIdentity(instruction.Member)))));
		var producer = instructions[producerIndex];
		byte[] il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
		int endOffset = callIndex + 1 < instructions.Length ? instructions[callIndex + 1].Offset : il.Length;
		string windowSha256 = Convert.ToHexString(SHA256.HashData(
			il.AsSpan(producer.Offset, endOffset - producer.Offset))).ToLowerInvariant();
		return $"{sourceIdentity}|PRODUCER|IL_{producer.Offset:x4}|{producer.OpCode.Name}|" +
			$"{(producer.Member is null ? "NONE" : MemberIdentity(producer.Member))}|WINDOW_SHA256|{windowSha256}";
	}

	private static int? GetLoadedArgumentIndex(
		MethodBase caller,
		(int Offset, OpCode OpCode, MemberInfo? Member) instruction)
	{
		if (instruction.OpCode == OpCodes.Ldarg_0)
		{
			return 0;
		}
		if (instruction.OpCode == OpCodes.Ldarg_1)
		{
			return 1;
		}
		if (instruction.OpCode == OpCodes.Ldarg_2)
		{
			return 2;
		}
		if (instruction.OpCode == OpCodes.Ldarg_3)
		{
			return 3;
		}

		return ReadVariableOperand(caller, instruction, OpCodes.Ldarg_S, OpCodes.Ldarg);
	}

	private static int? GetLoadedLocalIndex(
		MethodBase caller,
		(int Offset, OpCode OpCode, MemberInfo? Member) instruction)
	{
		if (instruction.OpCode == OpCodes.Ldloc_0)
		{
			return 0;
		}
		if (instruction.OpCode == OpCodes.Ldloc_1)
		{
			return 1;
		}
		if (instruction.OpCode == OpCodes.Ldloc_2)
		{
			return 2;
		}
		if (instruction.OpCode == OpCodes.Ldloc_3)
		{
			return 3;
		}

		return ReadVariableOperand(caller, instruction, OpCodes.Ldloc_S, OpCodes.Ldloc);
	}

	private static int? ReadVariableOperand(
		MethodBase caller,
		(int Offset, OpCode OpCode, MemberInfo? Member) instruction,
		OpCode shortForm,
		OpCode longForm)
	{
		byte[] il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
		int operandOffset = instruction.Offset + instruction.OpCode.Size;
		if (instruction.OpCode == shortForm)
		{
			Assert.InRange(operandOffset, 0, il.Length - 1);
			return il[operandOffset];
		}
		if (instruction.OpCode == longForm)
		{
			Assert.InRange(operandOffset, 0, il.Length - sizeof(ushort));
			return BitConverter.ToUInt16(il, operandOffset);
		}

		return null;
	}

	private static string RuntimeDispatchSite(
		MethodBase caller,
		int instructionOffset,
		OpCode opCode,
		MemberInfo callee) =>
		$"{MethodIdentity(caller)}|IL_{instructionOffset:x4}|{opCode.Name}|{MemberIdentity(callee)}";

	private static string BuildMethodClosureManifest(IEnumerable<MethodBase> methods)
	{
		var rows = new List<string>();
		foreach (MethodBase method in methods.OrderBy(MethodIdentity, StringComparer.Ordinal))
		{
			MethodBody body = method.GetMethodBody() ??
				throw new Xunit.Sdk.XunitException($"Closure method has no managed body: {MethodIdentity(method)}");
			rows.Add($"METHOD|{MethodIdentity(method)}|{(int)method.Attributes}|" +
				$"{(int)method.GetMethodImplementationFlags()}|{(int)method.CallingConvention}");
			if (method is MethodInfo methodInfo)
			{
				rows.Add($"RETURN|{TypeIdentity(methodInfo.ReturnType)}|" +
					AttributeIdentity(methodInfo.ReturnParameter.GetCustomAttributesData()));
			}
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				rows.Add($"PARAM|{parameter.Position}|{parameter.Name}|{TypeIdentity(parameter.ParameterType)}|" +
					$"{(int)parameter.Attributes}|{AttributeIdentity(parameter.GetCustomAttributesData())}");
			}
			rows.Add($"BODY|{body.InitLocals}|{body.MaxStackSize}|" +
				Convert.ToHexString(body.GetILAsByteArray() ?? []).ToLowerInvariant());
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				rows.Add($"LOCAL|{local.LocalIndex}|{TypeIdentity(local.LocalType)}|{local.IsPinned}");
			}
			foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
			{
				rows.Add($"EH|{(int)clause.Flags}|{clause.TryOffset}|{clause.TryLength}|" +
					$"{clause.HandlerOffset}|{clause.HandlerLength}|" +
					TypeIdentity(clause.Flags == ExceptionHandlingClauseOptions.Clause ? clause.CatchType : null));
			}
			foreach (MemberInfo reference in GetIlReferences(method))
			{
				rows.Add($"REF|{MemberIdentity(reference)}");
			}
		}

		return string.Join('\n', rows) + "\n";
	}

	private static void AssertPeModuleInitializerAndAmbientClosureAuthority(Assembly assembly)
	{
		string assemblyPath = Path.GetFullPath(assembly.Location);
		using FileStream stream = File.OpenRead(assemblyPath);
		using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
		MetadataReader metadata = peReader.GetMetadataReader();
		TypeDefinition moduleType = metadata.GetTypeDefinition(
			Assert.Single(metadata.TypeDefinitions, handle =>
				metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "<Module>"));
		MethodDefinitionHandle moduleCctorHandle = Assert.Single(
			moduleType.GetMethods(),
			handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == ".cctor");
		MethodDefinition moduleCctor = metadata.GetMethodDefinition(moduleCctorHandle);
		MethodBodyBlock body = peReader.GetMethodBody(moduleCctor.RelativeVirtualAddress);
		string peBodyManifest = $"TOKEN|{MetadataTokens.GetToken(moduleCctorHandle):x8}\n" +
			$"MAXSTACK|{body.MaxStack}\nLOCALS|{MetadataTokens.GetToken(body.LocalSignature):x8}\n" +
			$"IL|{Convert.ToHexString(body.GetILBytes() ?? []).ToLowerInvariant()}\n" +
			string.Join('\n', body.ExceptionRegions.Select(region =>
				$"EH|{region.Kind}|{region.TryOffset}|{region.TryLength}|{region.HandlerOffset}|" +
				$"{region.HandlerLength}|{region.FilterOffset}|{MetadataTokens.GetToken(region.CatchType):x8}")) + "\n";
		string peBodySha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(peBodyManifest))).ToLowerInvariant();
#if DEBUG
		string expectedPeBodySha256 = ExpectedDebugModuleInitializerBodySha256;
		string expectedAmbientSha256 = ExpectedDebugAmbientClosureSha256;
		string expectedAmbientDispatchSha256 = ExpectedDebugAmbientRuntimeDispatchAuthoritySha256;
#else
		string expectedPeBodySha256 = ExpectedReleaseModuleInitializerBodySha256;
		string expectedAmbientSha256 = ExpectedReleaseAmbientClosureSha256;
		string expectedAmbientDispatchSha256 = ExpectedReleaseAmbientRuntimeDispatchAuthoritySha256;
#endif
		Assert.True(StringComparer.Ordinal.Equals(expectedPeBodySha256, peBodySha256), peBodySha256);

		MethodBase reflectionModuleCctor = Assert.IsAssignableFrom<MethodBase>(
			assembly.ManifestModule.ResolveMethod(MetadataTokens.GetToken(moduleCctorHandle)));
		Assert.Equal(".cctor", reflectionModuleCctor.Name);
		Type moduleInitializerType = assembly.GetType("WalletWasabi.ModuleInitializer", throwOnError: true)!;
		MethodInfo patchTestNet = Assert.Single(
			moduleInitializerType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
			method => method.Name == "PatchTestNet");
		MethodBase[] ambientClosure = AssertWireMethodClosureSafe(
			[reflectionModuleCctor, patchTestNet],
			expectedAmbientDispatchSha256);
		Assert.Contains(reflectionModuleCctor, ambientClosure);
		Assert.Contains(patchTestNet, ambientClosure);
		string ambientManifest = BuildMethodClosureManifest(ambientClosure);
		string ambientSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(ambientManifest))).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedAmbientSha256, ambientSha256), ambientSha256);
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactSha256(expectedPeBodySha256, peBodyManifest + "MODULE_INITIALIZER_MUTATION"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactSha256(expectedAmbientSha256, ambientManifest + "PATCH_TESTNET_MUTATION"));
	}

	private static MethodBase[] GetSameAssemblyAmbientClosure(
		Assembly assembly,
		IEnumerable<MethodBase> roots)
	{
		var pending = new Stack<MethodBase>(roots.Reverse());
		var closure = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
		while (pending.TryPop(out MethodBase? method))
		{
			if (!closure.TryAdd(MethodIdentity(method), method))
			{
				continue;
			}
			MethodBody body = method.GetMethodBody() ??
				throw new Xunit.Sdk.XunitException($"Ambient closure method has no managed body: {MethodIdentity(method)}");
			if (method.DeclaringType?.Assembly == assembly && method.DeclaringType.TypeInitializer is { } typeInitializer)
			{
				pending.Push(typeInitializer);
			}
			foreach ((_, _, MemberInfo? reference) in GetIlInstructionsWithOffsets(method))
			{
				if (reference?.DeclaringType?.Assembly == assembly &&
					reference.DeclaringType.TypeInitializer is { } referencedInitializer)
				{
					pending.Push(referencedInitializer);
				}
				if (reference is MethodBase called && called.Module.Assembly == assembly &&
					called.GetMethodBody() is not null)
				{
					pending.Push(called);
				}
			}
			_ = body;
		}
		return closure.Values.OrderBy(MethodIdentity, StringComparer.Ordinal).ToArray();
	}

	private static void AssertExactSha256(string expectedSha256, string manifest)
	{
		string actualSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedSha256, actualSha256), actualSha256);
	}

	private static MethodInfo[] CreateForbiddenClosureMutations()
	{
		AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName("WlpqClosureMutation"),
			AssemblyBuilderAccess.Run);
		ModuleBuilder module = assembly.DefineDynamicModule("WlpqClosureMutation");
		TypeBuilder type = module.DefineType(
			"WlpqClosureMutation.PlanAccessors",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodInfo fileExists = typeof(File).GetMethod(nameof(File.Exists), [typeof(string)])!;

		MethodBuilder direct = type.DefineMethod(
			"ReadSelected",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator directIl = direct.GetILGenerator();
		directIl.Emit(OpCodes.Ldnull);
		directIl.Emit(OpCodes.Call, fileExists);
		directIl.Emit(OpCodes.Pop);
		directIl.Emit(OpCodes.Ret);

		MethodBuilder wrapper = type.DefineMethod(
			"Forward",
			MethodAttributes.Private | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator wrapperIl = wrapper.GetILGenerator();
		wrapperIl.Emit(OpCodes.Ldnull);
		wrapperIl.Emit(OpCodes.Call, fileExists);
		wrapperIl.Emit(OpCodes.Pop);
		wrapperIl.Emit(OpCodes.Ret);

		MethodBuilder transitive = type.DefineMethod(
			"ReadDestinations",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator transitiveIl = transitive.GetILGenerator();
		transitiveIl.Emit(OpCodes.Call, wrapper);
		transitiveIl.Emit(OpCodes.Ret);

		Type created = type.CreateType()!;

		TypeBuilder reflectionType = module.DefineType(
			"WlpqClosureMutation.ReflectionDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder reflection = reflectionType.DefineMethod(
			"Reflect",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator reflectionIl = reflection.GetILGenerator();
		reflectionIl.Emit(OpCodes.Ldtoken, typeof(string));
		reflectionIl.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
		reflectionIl.Emit(OpCodes.Ldstr, nameof(string.ToString));
		reflectionIl.Emit(OpCodes.Callvirt, typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string)])!);
		reflectionIl.Emit(OpCodes.Pop);
		reflectionIl.Emit(OpCodes.Ret);
		Type createdReflection = reflectionType.CreateType()!;

		TypeBuilder delegateType = module.DefineType(
			"WlpqClosureMutation.DelegateDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder delegateTarget = delegateType.DefineMethod(
			"Target",
			MethodAttributes.Private | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		delegateTarget.GetILGenerator().Emit(OpCodes.Ret);
		MethodBuilder delegateDispatch = delegateType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator delegateIl = delegateDispatch.GetILGenerator();
		delegateIl.Emit(OpCodes.Ldnull);
		delegateIl.Emit(OpCodes.Ldftn, delegateTarget);
		delegateIl.Emit(OpCodes.Newobj, typeof(Action).GetConstructor([typeof(object), typeof(IntPtr)])!);
		delegateIl.Emit(OpCodes.Callvirt, typeof(Action).GetMethod(nameof(Action.Invoke))!);
		delegateIl.Emit(OpCodes.Ret);
		Type createdDelegate = delegateType.CreateType()!;

		TypeBuilder dynamicType = module.DefineType(
			"WlpqClosureMutation.DynamicDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder dynamicDispatch = dynamicType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator dynamicIl = dynamicDispatch.GetILGenerator();
		Type closedCallSite = typeof(CallSite<>).MakeGenericType(typeof(Action));
		dynamicIl.Emit(OpCodes.Ldnull);
		dynamicIl.Emit(
			OpCodes.Call,
			closedCallSite.GetMethod(nameof(CallSite<Action>.Create), [typeof(CallSiteBinder)])!);
		dynamicIl.Emit(OpCodes.Pop);
		dynamicIl.Emit(OpCodes.Ret);
		Type createdDynamic = dynamicType.CreateType()!;

		TypeBuilder reviewedCalleeType = module.DefineType(
			"WlpqClosureMutation.ReviewedCalleeAtNewSite",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder reviewedCalleeDispatch = reviewedCalleeType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(bool),
			[typeof(string), typeof(string)]);
		ILGenerator reviewedCalleeIl = reviewedCalleeDispatch.GetILGenerator();
		reviewedCalleeIl.Emit(
			OpCodes.Call,
			typeof(StringComparer).GetProperty(nameof(StringComparer.Ordinal))!.GetMethod!);
		reviewedCalleeIl.Emit(OpCodes.Ldarg_0);
		reviewedCalleeIl.Emit(OpCodes.Ldarg_1);
		reviewedCalleeIl.Emit(
			OpCodes.Callvirt,
			typeof(StringComparer).GetMethod(
				nameof(StringComparer.Equals),
				[typeof(string), typeof(string)])!);
		reviewedCalleeIl.Emit(OpCodes.Ret);
		Type createdReviewedCallee = reviewedCalleeType.CreateType()!;

		TypeBuilder localOverrideType = module.DefineType(
			"WlpqClosureMutation.LocalOverrideDispatch",
			TypeAttributes.Public | TypeAttributes.Sealed);
		MethodBuilder localToString = localOverrideType.DefineMethod(
			nameof(ToString),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
			typeof(string),
			Type.EmptyTypes);
		ILGenerator localToStringIl = localToString.GetILGenerator();
		localToStringIl.Emit(OpCodes.Ldstr, "local override");
		localToStringIl.Emit(OpCodes.Ret);
		localOverrideType.DefineMethodOverride(localToString, typeof(object).GetMethod(nameof(ToString))!);
		MethodBuilder localOverrideDispatch = localOverrideType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(string),
			[localOverrideType]);
		ILGenerator localOverrideIl = localOverrideDispatch.GetILGenerator();
		localOverrideIl.Emit(OpCodes.Ldarg_0);
		localOverrideIl.Emit(OpCodes.Callvirt, typeof(object).GetMethod(nameof(ToString))!);
		localOverrideIl.Emit(OpCodes.Ret);
		Type createdLocalOverride = localOverrideType.CreateType()!;

		TypeBuilder calliType = module.DefineType(
			"WlpqClosureMutation.CalliDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder calliDispatch = calliType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator calliIl = calliDispatch.GetILGenerator();
		calliIl.Emit(OpCodes.Ldc_I4_0);
		calliIl.Emit(OpCodes.Conv_I);
		calliIl.EmitCalli(
			OpCodes.Calli,
			CallingConvention.Cdecl,
			typeof(void),
			Type.EmptyTypes);
		calliIl.Emit(OpCodes.Ret);
		Type createdCalli = calliType.CreateType()!;

		TypeBuilder interfaceBuilder = module.DefineType(
			"WlpqClosureMutation.IUnknownDispatch",
			TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
		interfaceBuilder.DefineMethod(
			"Run",
			MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual,
			typeof(void),
			Type.EmptyTypes);
		Type interfaceType = interfaceBuilder.CreateType()!;
		TypeBuilder virtualType = module.DefineType(
			"WlpqClosureMutation.VirtualDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder virtualDispatch = virtualType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			[interfaceType]);
		ILGenerator virtualIl = virtualDispatch.GetILGenerator();
		virtualIl.Emit(OpCodes.Ldarg_0);
		virtualIl.Emit(OpCodes.Callvirt, interfaceType.GetMethod("Run")!);
		virtualIl.Emit(OpCodes.Ret);
		Type createdVirtual = virtualType.CreateType()!;

		TypeBuilder cctorType = module.DefineType(
			"WlpqClosureMutation.TouchedTypeInitializer",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		FieldBuilder cctorField = cctorType.DefineField(
			"Value",
			typeof(int),
			FieldAttributes.Private | FieldAttributes.Static);
		ConstructorBuilder typeInitializer = cctorType.DefineTypeInitializer();
		ILGenerator cctorIl = typeInitializer.GetILGenerator();
		cctorIl.Emit(OpCodes.Ldnull);
		cctorIl.Emit(OpCodes.Call, fileExists);
		cctorIl.Emit(OpCodes.Pop);
		cctorIl.Emit(OpCodes.Ret);
		MethodBuilder touch = cctorType.DefineMethod(
			"Touch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator touchIl = touch.GetILGenerator();
		touchIl.Emit(OpCodes.Ldsfld, cctorField);
		touchIl.Emit(OpCodes.Pop);
		touchIl.Emit(OpCodes.Ret);
		Type createdCctor = cctorType.CreateType()!;

		TypeBuilder propertyType = module.DefineType(
			"WlpqClosureMutation.PropertyAccessor",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		PropertyBuilder property = propertyType.DefineProperty(
			"Value",
			PropertyAttributes.None,
			typeof(int),
			null);
		MethodBuilder getter = propertyType.DefineMethod(
			"get_Value",
			MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName,
			typeof(int),
			Type.EmptyTypes);
		ILGenerator getterIl = getter.GetILGenerator();
		getterIl.Emit(OpCodes.Ldnull);
		getterIl.Emit(OpCodes.Call, fileExists);
		getterIl.Emit(OpCodes.Pop);
		getterIl.Emit(OpCodes.Ldc_I4_0);
		getterIl.Emit(OpCodes.Ret);
		property.SetGetMethod(getter);
		Type createdProperty = propertyType.CreateType()!;

		return
		[
			created.GetMethod("ReadSelected", BindingFlags.Public | BindingFlags.Static)!,
			created.GetMethod("ReadDestinations", BindingFlags.Public | BindingFlags.Static)!,
			createdReflection.GetMethod("Reflect", BindingFlags.Public | BindingFlags.Static)!,
			createdDelegate.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdDynamic.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdReviewedCallee.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdLocalOverride.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdCalli.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdVirtual.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdCctor.GetMethod("Touch", BindingFlags.Public | BindingFlags.Static)!,
			createdProperty.GetMethod("get_Value", BindingFlags.Public | BindingFlags.Static)!,
		];
	}

	private sealed record PlanFixture(
		ElementsPublicNetworkManifest Manifest,
		LiquidAssetId PeggedAsset,
		LiquidOrdinaryWalletExactSpendPlan Plan,
		LiquidWalletCoinControlEntry FirstSelected,
		LiquidWalletCoinControlEntry SecondSelected,
		LiquidSuppliedConfidentialDestination FirstDestination,
		LiquidSuppliedConfidentialDestination SecondDestination);

	private sealed class ThrowingPayloadList : IReadOnlyList<byte[]?>
	{
		public int Count
		{
			get
			{
				CountReads++;
				throw new InvalidOperationException("The payload collection must not be inspected.");
			}
		}

		public int CountReads { get; private set; }

		public byte[]? this[int index] =>
			throw new InvalidOperationException("The payload collection must not be inspected.");

		public IEnumerator<byte[]?> GetEnumerator() =>
			throw new InvalidOperationException("The payload collection must not be enumerated.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class NegativeCountList<T> : IReadOnlyList<T>
	{
		public int Count => -1;

		public T this[int index] => throw new InvalidOperationException("A negative-count list has no elements.");

		public IEnumerator<T> GetEnumerator() =>
			throw new InvalidOperationException("A negative-count list has no elements.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class RepeatedValueList<T>(int count, T value, int nullAt = -1) : IReadOnlyList<T>
	{
		public int Count => count;

		public int ReadCount { get; private set; }

		public T this[int index]
		{
			get
			{
				if ((uint)index >= (uint)count)
				{
					throw new ArgumentOutOfRangeException(nameof(index));
				}

				ReadCount++;
				return index == nullAt ? default! : value;
			}
		}

		public IEnumerator<T> GetEnumerator() =>
			throw new InvalidOperationException("The repeated-value list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class StatefulPayloadList(
		IReadOnlyList<byte[]?> firstReads,
		IReadOnlyList<byte[]?> snapshotReads) : IReadOnlyList<byte[]?>
	{
		private readonly int[] _readCounts = new int[firstReads.Count];

		public int Count => firstReads.Count;

		public IReadOnlyList<int> ReadCounts => _readCounts;

		public byte[]? this[int index]
		{
			get
			{
				int read = Interlocked.Increment(ref _readCounts[index]);
				return read switch
				{
					1 => firstReads[index],
					2 or 3 => snapshotReads[index],
					_ => throw new InvalidOperationException("The payload list was read after snapshotting."),
				};
			}
		}

		public IEnumerator<byte[]?> GetEnumerator() =>
			throw new InvalidOperationException("The payload list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class StatefulRowList(
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?> firstReads,
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?> snapshotReads) :
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?>
	{
		private readonly int[] _readCounts = new int[firstReads.Count];

		public int Count => firstReads.Count;

		public IReadOnlyList<int> ReadCounts => _readCounts;

		public LiquidOrdinaryWalletPlanFundingRow? this[int index]
		{
			get
			{
				int read = Interlocked.Increment(ref _readCounts[index]);
				return read switch
				{
					1 => firstReads[index],
					2 or 3 => snapshotReads[index],
					_ => throw new InvalidOperationException("The row list was read after snapshotting."),
				};
			}
		}

		public IEnumerator<LiquidOrdinaryWalletPlanFundingRow?> GetEnumerator() =>
			throw new InvalidOperationException("The row list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CoordinatedSingleItemList<T>(
		Func<T> readCurrent,
		ManualResetEventSlim firstRead,
		ManualResetEventSlim mutationComplete) : IReadOnlyList<T>
	{
		private int _readCount;

		public int Count => 1;

		public T this[int index]
		{
			get
			{
				Assert.Equal(0, index);
				if (Interlocked.Increment(ref _readCount) == 1)
				{
					T first = readCurrent();
					firstRead.Set();
					return first;
				}

				Assert.True(mutationComplete.Wait(TimeSpan.FromSeconds(10)));
				return readCurrent();
			}
		}

		public IEnumerator<T> GetEnumerator() =>
			throw new InvalidOperationException("The coordinated list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private static StringComparer PackagePathComparer =>
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;

	private static string ParseCanonicalPackageRoot(string value, string description)
	{
		Assert.False(string.IsNullOrWhiteSpace(value), $"The {description} is blank.");
		Assert.True(Path.IsPathFullyQualified(value), $"The {description} is not absolute: {value}");
		string provided = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string canonical = Path.GetFullPath(value)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		Assert.False(string.IsNullOrEmpty(canonical), $"The {description} is a filesystem root.");
		Assert.True(
			PackagePathComparer.Equals(provided, canonical),
			$"The {description} is not canonical: {value}");
		string filesystemRoot = (Path.GetPathRoot(Path.GetFullPath(value)) ?? "")
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		Assert.False(
			PackagePathComparer.Equals(canonical, filesystemRoot),
			$"The {description} is a filesystem root: {value}");
		AssertRegularAuthorityDirectory(canonical, description);
		return canonical;
	}

	private static bool TryNormalizePackageAuthorityPath(
		string path,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		out string normalizedPath)
	{
		string fullPath = Path.GetFullPath(path);
		string? packageRoot = null;
		foreach (string candidateRoot in packageAuthority.OrderedRoots)
		{
			if (!IsPathWithin(fullPath, candidateRoot))
			{
				continue;
			}
			Assert.Null(packageRoot);
			packageRoot = candidateRoot;
		}
		if (packageRoot is null)
		{
			normalizedPath = "";
			return false;
		}
		string relativePath = NormalizeRelativePath(Path.GetRelativePath(packageRoot, fullPath));
		Assert.NotEqual(".", relativePath);
		AssertPackageShadowConsistency(fullPath, relativePath, packageAuthority);
		normalizedPath = $"NUGET|{relativePath}";
		return true;
	}

	private static void AssertPackageShadowConsistency(
		string selectedPath,
		string relativePath,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		AssertRegularAuthorityFile(selectedPath, "selected package authority file");
		byte[] selectedBytes = File.ReadAllBytes(selectedPath);
		foreach (string packageRoot in packageAuthority.OrderedRoots)
		{
			string candidate = Path.GetFullPath(Path.Combine(
				packageRoot,
				relativePath.Replace('/', Path.DirectorySeparatorChar)));
			if (PackagePathComparer.Equals(candidate, Path.GetFullPath(selectedPath)))
			{
				continue;
			}
			Assert.False(
				Directory.Exists(candidate),
				$"A package authority file is shadowed by a directory: {candidate}");
			if (!File.Exists(candidate))
			{
				continue;
			}
			AssertRegularAuthorityFile(candidate, "shadow package authority file");
			Assert.Equal(selectedBytes, File.ReadAllBytes(candidate));
		}
	}

	private static string NormalizeAuthorityStringWithPackages(
		string value,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		params (string Token, string Root)[] roots)
	{
		string normalized = value.Replace('\\', '/');
		string[] packageRoots = packageAuthority.OrderedRoots.ToArray();
		for (int index = 1; index < packageRoots.Length; index++)
		{
			string current = packageRoots[index];
			int insertion = index;
			while (insertion > 0 && packageRoots[insertion - 1].Length < current.Length)
			{
				packageRoots[insertion] = packageRoots[insertion - 1];
				insertion--;
			}
			packageRoots[insertion] = current;
		}
		foreach (string packageRoot in packageRoots)
		{
			normalized = ReplaceAuthorityRoot(normalized, packageRoot, "{NUGET}");
		}
		return NormalizeAuthorityString(normalized, roots);
	}

	private static string NormalizeCompilerAuthorityStringWithPackages(
		string value,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		params (string Token, string Root)[] roots)
	{
		AssertNoReservedCompilerAuthorityTokens(
			value,
			[.. roots.Select(root => root.Token), "{NUGET}"]);
		string normalized = value;
		string[] packageRoots = packageAuthority.OrderedRoots.ToArray();
		for (int index = 1; index < packageRoots.Length; index++)
		{
			string current = packageRoots[index];
			int insertion = index;
			while (insertion > 0 && packageRoots[insertion - 1].Length < current.Length)
			{
				packageRoots[insertion] = packageRoots[insertion - 1];
				insertion--;
			}
			packageRoots[insertion] = current;
		}
		foreach (string packageRoot in packageRoots)
		{
			normalized = ReplaceAuthorityRoot(normalized, packageRoot, "{NUGET}");
		}
		return NormalizeCompilerAuthorityRoots(normalized, roots);
	}

	private static void WritePackageAssetsAuthorityFixture(
		string path,
		string primaryRoot,
		params (string Root, bool EmptyObject)[] orderedRoots)
	{
		var json = new StringBuilder();
		json.Append("{\"project\":{\"restore\":{\"packagesPath\":");
		json.Append(JsonSerializer.Serialize(primaryRoot));
		json.Append("}},\"packageFolders\":{");
		for (int index = 0; index < orderedRoots.Length; index++)
		{
			if (index != 0)
			{
				json.Append(',');
			}
			json.Append(JsonSerializer.Serialize(orderedRoots[index].Root));
			json.Append(orderedRoots[index].EmptyObject ? ":{}" : ":{\"unexpected\":true}");
		}
		json.Append("}}");
		File.WriteAllText(path, json.ToString(), Encoding.UTF8);
	}

	private static string WriteSemanticRestoreFixture(
		string repositoryRoot,
		string dotnetRoot,
		string primaryPackageRoot,
		string[] orderedPackageRoots,
		string importedPackageFile,
		string dependencyVersion,
		string contentHash,
		string libraryPath,
		bool usePinnedNixFallbackProfile = false,
		string projectVersion = "1.0.0")
	{
		string projectRoot = Path.Combine(repositoryRoot, "WalletWasabi");
		string generatedRoot = Path.Combine(projectRoot, "obj");
		Directory.CreateDirectory(generatedRoot);
		string projectPath = Path.Combine(projectRoot, "WalletWasabi.csproj");
		string assetsPath = Path.Combine(generatedRoot, "project.assets.json");
		string propsPath = Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.props");
		string targetsPath = Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.targets");
		string packagesLockPath = Path.Combine(projectRoot, "packages.lock.json");
		string dependencyIdentity = $"Example.Package/{dependencyVersion}";
		string packageDirectory = Path.GetDirectoryName(Path.GetDirectoryName(importedPackageFile)!)!;
		Directory.CreateDirectory(Path.Combine(packageDirectory, "lib/net10.0"));
		Directory.CreateDirectory(Path.Combine(packageDirectory, "runtimes/linux-x64/native"));
		File.WriteAllBytes(
			Path.Combine(packageDirectory, "lib/net10.0/Example.Package.dll"),
			Convert.FromHexString("01020304"));
		File.WriteAllBytes(
			Path.Combine(packageDirectory, "runtimes/linux-x64/native/libexample.so"),
			Convert.FromHexString("05060708"));
		if (usePinnedNixFallbackProfile)
		{
			File.WriteAllBytes(
				Path.Combine(packageDirectory, ".nupkg.metadata"),
				Encoding.ASCII.GetBytes("{}\n"));
			File.WriteAllBytes(Path.Combine(packageDirectory, ".nix-patched"), []);
			File.WriteAllBytes(
				Path.Combine(packageDirectory, "[Content_Types].xml"),
				Encoding.ASCII.GetBytes("<Types />\n"));
			string relationshipsDirectory = Path.Combine(packageDirectory, "_rels");
			Directory.CreateDirectory(relationshipsDirectory);
			File.WriteAllBytes(
				Path.Combine(relationshipsDirectory, ".rels"),
				Encoding.ASCII.GetBytes("<Relationships />\n"));
			string corePropertiesDirectory = Path.Combine(
				packageDirectory,
				"package/services/metadata/core-properties");
			Directory.CreateDirectory(corePropertiesDirectory);
			File.WriteAllBytes(
				Path.Combine(corePropertiesDirectory, "0123456789abcdef0123456789abcdef.psmdcp"),
				Encoding.ASCII.GetBytes("<coreProperties />\n"));
		}
		else
		{
			File.WriteAllText(
				Path.Combine(packageDirectory, ".nupkg.metadata"),
				$"{{\"contentHash\":{JsonSerializer.Serialize(contentHash)}}}",
				Encoding.UTF8);
			File.WriteAllBytes(
				Path.Combine(packageDirectory, ".signature.p7s"),
				Convert.FromHexString("01020304"));
			byte[] fixtureNupkg = Convert.FromHexString("01020304");
			File.WriteAllBytes(
				Path.Combine(packageDirectory, $"example.package.{dependencyVersion}.nupkg"),
				fixtureNupkg);
			File.WriteAllText(
				Path.Combine(packageDirectory, $"example.package.{dependencyVersion}.nupkg.sha512"),
				Convert.ToBase64String(SHA512.HashData(fixtureNupkg)),
				Encoding.UTF8);
		}
		var json = new StringBuilder();
		json.Append("{\"version\":3,\"targets\":{\"net10.0\":{");
		json.Append(JsonSerializer.Serialize(dependencyIdentity));
		json.Append(":{\"type\":\"package\",\"compile\":{\"lib/net10.0/Example.Package.dll\":{}}}}},");
		json.Append("\"libraries\":{");
		json.Append(JsonSerializer.Serialize(dependencyIdentity));
		json.Append(":{");
		if (!usePinnedNixFallbackProfile)
		{
			json.Append("\"sha512\":");
			json.Append(JsonSerializer.Serialize(contentHash));
			json.Append(',');
		}
		json.Append("\"type\":\"package\",\"path\":");
		json.Append(JsonSerializer.Serialize(libraryPath));
		json.Append(",\"files\":[\".nupkg.metadata\",");
		json.Append(usePinnedNixFallbackProfile ? "\".nix-patched\"" : "\".signature.p7s\"");
		json.Append(",\"lib/net10.0/Example.Package.dll\",\"build/example.props\",");
		json.Append("\"runtimes/linux-x64/native/libexample.so\"");
		if (!usePinnedNixFallbackProfile)
		{
			json.Append(",\"example.package.");
			json.Append(dependencyVersion);
			json.Append(".nupkg.sha512\"");
		}
		json.Append("]}},");
		json.Append("\"projectFileDependencyGroups\":{\"net10.0\":[");
		json.Append(JsonSerializer.Serialize($"Example.Package >= {dependencyVersion}"));
		json.Append("]},\"packageFolders\":{");
		for (int index = 0; index < orderedPackageRoots.Length; index++)
		{
			if (index != 0)
			{
				json.Append(',');
			}
			json.Append(JsonSerializer.Serialize(orderedPackageRoots[index]));
			json.Append(":{}");
		}
		json.Append("},\"project\":{\"version\":");
		json.Append(JsonSerializer.Serialize(projectVersion));
		json.Append(",\"restore\":{");
		json.Append("\"projectUniqueName\":");
		json.Append(JsonSerializer.Serialize(projectPath));
		json.Append(",\"projectName\":\"WalletWasabi\",\"projectPath\":");
		json.Append(JsonSerializer.Serialize(projectPath));
		json.Append(",\"packagesPath\":");
		json.Append(JsonSerializer.Serialize(primaryPackageRoot));
		json.Append(",\"outputPath\":");
		json.Append(JsonSerializer.Serialize(generatedRoot + Path.DirectorySeparatorChar));
		json.Append(",\"projectStyle\":\"PackageReference\",\"configFilePaths\":[");
		json.Append(JsonSerializer.Serialize(Path.Combine(repositoryRoot, "NuGet.Config")));
		json.Append(',');
		json.Append(JsonSerializer.Serialize(Path.Combine(repositoryRoot, "home/.nuget/NuGet/NuGet.Config")));
		json.Append("],\"originalTargetFrameworks\":[\"net10.0\"],\"sources\":{");
		if (usePinnedNixFallbackProfile)
		{
			string packageParent = Directory.GetParent(primaryPackageRoot)?.FullName ??
				throw new Xunit.Sdk.XunitException("The fixture package root has no parent.");
			string offlineSource = Path.Combine(packageParent, "source");
			string libraryPacksSource = Path.Combine(dotnetRoot, "library-packs");
			Directory.CreateDirectory(offlineSource);
			Directory.CreateDirectory(libraryPacksSource);
			json.Append(JsonSerializer.Serialize(offlineSource));
			json.Append(":{},");
			json.Append(JsonSerializer.Serialize(libraryPacksSource));
			json.Append(":{}");
		}
		else
		{
			json.Append("\"https://api.nuget.org/v3/index.json\":{}");
		}
		json.Append("},");
		json.Append("\"restoreAuditProperties\":{\"enableAudit\":\"true\",\"auditLevel\":\"low\",\"auditMode\":\"all\",");
		json.Append("\"suppressedAdvisories\":{\"https://github.com/advisories/GHSA-2m69-gcr7-jv3q\":null}},");
		if (orderedPackageRoots.Length > 1)
		{
			json.Append("\"fallbackFolders\":[");
			for (int index = 1; index < orderedPackageRoots.Length; index++)
			{
				if (index != 1)
				{
					json.Append(',');
				}
				json.Append(JsonSerializer.Serialize(orderedPackageRoots[index]));
			}
			json.Append("],");
		}
		json.Append("\"frameworks\":{\"net10.0\":{\"targetAlias\":\"net10.0\",\"projectReferences\":{}}}},");
		json.Append("\"frameworks\":{\"net10.0\":{\"targetAlias\":\"net10.0\",\"dependencies\":{");
		json.Append("\"Example.Package\":{\"target\":\"Package\",\"version\":");
		json.Append(JsonSerializer.Serialize($"[{dependencyVersion}, )"));
		json.Append("}},\"runtimeIdentifierGraphPath\":");
		json.Append(JsonSerializer.Serialize(Path.Combine(
			dotnetRoot,
			"sdk",
			PinnedDotnetSdkVersion,
			"PortableRuntimeIdentifierGraph.json")));
		json.Append("}}}}");
		File.WriteAllText(assetsPath, json.ToString(), Encoding.UTF8);
		WriteSemanticPackagesLockFixture(
			packagesLockPath,
			dependencyVersion,
			contentHash,
			omitContentHashes: usePinnedNixFallbackProfile);
		WriteSemanticNuGetPropsFixture(propsPath, orderedPackageRoots, importedPackageFile);
		File.WriteAllText(
			targetsPath,
			"<Project ToolsVersion=\"14.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />",
			Encoding.UTF8);
		return assetsPath;
	}

	private static void WriteSemanticPackagesLockFixture(
		string path,
		string dependencyVersion,
		string contentHash,
		string packageId = "Example.Package",
		string? additionalPackageId = null,
		string? dependencyId = null,
		string dependencyMinimumVersion = "1.0.0",
		string? dependencyAliasId = null,
		bool omitContentHashes = false,
		bool omitAdditionalPackageContentHash = false)
	{
		var json = new StringBuilder();
		json.Append("{\"version\":2,\"dependencies\":{\"net10.0\":{");
		json.Append(JsonSerializer.Serialize(packageId));
		json.Append(":{");
		json.Append("\"type\":\"Direct\",\"requested\":");
		json.Append(JsonSerializer.Serialize($"[{dependencyVersion}, )"));
		json.Append(",\"resolved\":");
		json.Append(JsonSerializer.Serialize(dependencyVersion));
		if (!omitContentHashes)
		{
			json.Append(",\"contentHash\":");
			json.Append(JsonSerializer.Serialize(contentHash));
		}
		if (dependencyId is not null)
		{
			json.Append(",\"dependencies\":{");
			json.Append(JsonSerializer.Serialize(dependencyId));
			json.Append(':');
			json.Append(JsonSerializer.Serialize(dependencyMinimumVersion));
			if (dependencyAliasId is not null)
			{
				json.Append(',');
				json.Append(JsonSerializer.Serialize(dependencyAliasId));
				json.Append(':');
				json.Append(JsonSerializer.Serialize(dependencyMinimumVersion));
			}
			json.Append('}');
		}
		json.Append('}');
		if (additionalPackageId is not null)
		{
			json.Append(',');
			json.Append(JsonSerializer.Serialize(additionalPackageId));
			json.Append(":{\"type\":\"Transitive\",\"resolved\":\"1.0.0\"");
			if (!omitContentHashes && !omitAdditionalPackageContentHash)
			{
				json.Append(",\"contentHash\":");
				json.Append(JsonSerializer.Serialize(contentHash));
			}
			json.Append('}');
		}
		json.Append("}}}");
		File.WriteAllText(path, json.ToString(), Encoding.UTF8);
	}

	private static void WriteSemanticNuGetPropsFixture(
		string path,
		string[] orderedPackageRoots,
		string importedPackageFile)
	{
		var xml = new StringBuilder();
		xml.Append("<Project ToolsVersion=\"14.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
		xml.Append("<PropertyGroup Condition=\" '$(ExcludeRestorePackageImports)' != 'true' \">");
		xml.Append("<NuGetPackageRoot>");
		xml.Append(System.Security.SecurityElement.Escape(orderedPackageRoots[0]));
		xml.Append("</NuGetPackageRoot><NuGetPackageFolders>");
		xml.Append(System.Security.SecurityElement.Escape(string.Join(';', orderedPackageRoots)));
		xml.Append("</NuGetPackageFolders><PkgExample_Package>");
		string packageDirectory = Path.GetDirectoryName(Path.GetDirectoryName(importedPackageFile)!)!;
		xml.Append(System.Security.SecurityElement.Escape(packageDirectory));
		xml.Append("</PkgExample_Package></PropertyGroup><ItemGroup>");
		foreach (string packageRoot in orderedPackageRoots)
		{
			xml.Append("<SourceRoot Include=\"");
			xml.Append(System.Security.SecurityElement.Escape(packageRoot + Path.DirectorySeparatorChar));
			xml.Append("\" />");
		}
		xml.Append("</ItemGroup><ImportGroup><Import Project=\"");
		xml.Append(System.Security.SecurityElement.Escape(importedPackageFile));
		xml.Append("\" Condition=\"Exists('");
		xml.Append(System.Security.SecurityElement.Escape(importedPackageFile));
		xml.Append("')\" /></ImportGroup></Project>");
		File.WriteAllText(path, xml.ToString(), Encoding.UTF8);
	}

	private static string CreateSemanticRestorePackageImport(
		string packageRoot,
		byte[] content,
		string fileName = "example.props",
		string dependencyVersion = "1.2.3")
	{
		string path = Path.Combine(packageRoot, $"example.package/{dependencyVersion}/build", fileName);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, content);
		return path;
	}

	private static string CreateSemanticRestoreContentHash(byte value)
	{
		byte[] bytes = new byte[64];
		Array.Fill(bytes, value);
		return Convert.ToBase64String(bytes);
	}

	private static string BuildSemanticRestoreFixtureManifest(
		string projectAssetsFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string? expectedPinnedNixProjectVersion = null)
	{
		string generatedRoot = Path.GetDirectoryName(projectAssetsFile)!;
		string packagesLockFile = Path.Combine(repositoryRoot, "WalletWasabi/packages.lock.json");
		const string ExpectedTargetFramework = "net10.0";
		return GetBuildAuthorityFileSha256(
			projectAssetsFile,
			projectAssetsFile,
			packagesLockFile,
			ExpectedTargetFramework,
			repositoryRoot,
			dotnetRoot,
			packageAuthority,
			expectedPinnedNixProjectVersion) + "|" +
			GetBuildAuthorityFileSha256(
				Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.props"),
				projectAssetsFile,
				packagesLockFile,
				ExpectedTargetFramework,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				expectedPinnedNixProjectVersion) + "|" +
			GetBuildAuthorityFileSha256(
				Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.targets"),
				projectAssetsFile,
				packagesLockFile,
				ExpectedTargetFramework,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				expectedPinnedNixProjectVersion);
	}

	private static void AssertSemanticRestoreFixtureRejected(
		string projectAssetsFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string? expectedPinnedNixProjectVersion = null)
	{
		bool rejected = false;
		try
		{
			_ = BuildSemanticRestoreFixtureManifest(
				projectAssetsFile,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				expectedPinnedNixProjectVersion);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid semantic restore authority was accepted.");
	}

	private static void AssertPinnedNixRestoreSourcesRejected(
		string projectAssetsFile,
		string canonicalProjectAssets,
		string canonicalSources,
		string mutatedSources,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string mutatedProjectAssets = canonicalProjectAssets.Replace(
			canonicalSources,
			mutatedSources,
			StringComparison.Ordinal);
		Assert.NotEqual(canonicalProjectAssets, mutatedProjectAssets);
		File.WriteAllText(projectAssetsFile, mutatedProjectAssets, Encoding.UTF8);
		AssertSemanticRestoreFixtureRejected(
			projectAssetsFile,
			repositoryRoot,
			dotnetRoot,
			packageAuthority);
	}

	private static void AssertPackageAuthorityRejected(string projectAssetsFile)
	{
		bool rejected = false;
		try
		{
			_ = GetPinnedPackageAuthority(projectAssetsFile);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid package authority was accepted.");
	}

	private static void AssertPackagePathRejected(
		string path,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		bool rejected = false;
		try
		{
			_ = NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid package authority path was accepted.");
	}

	private static void AssertRegularAuthorityDirectory(string path, string description)
	{
		Assert.True(Directory.Exists(path), $"The {description} is absent: {path}");
		AssertAuthorityPathHasNoSymbolicLinks(path, description);
	}

	private static void AssertAuthorityPathHasNoSymbolicLinks(string path, string description)
	{
		string fullPath = Path.GetFullPath(path);
		if (OperatingSystem.IsMacOS() && fullPath.StartsWith("/var/", StringComparison.Ordinal))
		{
			fullPath = "/private" + fullPath;
		}
		string? current = Path.GetPathRoot(fullPath);
		foreach (string component in fullPath[(current?.Length ?? 0)..].Split(
			Path.DirectorySeparatorChar,
			StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current ?? "", component);
			Assert.False(
				new FileInfo(current).LinkTarget is not null ||
				new DirectoryInfo(current).LinkTarget is not null,
				$"The {description} reaches a symbolic link at: {current}");
		}
	}

	private static void AssertProjectAssetsFallbackFolderTopology(
		JsonElement root,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		JsonElement project = root.GetProperty("project");
		Assert.Equal(JsonValueKind.Object, project.ValueKind);
		JsonElement restore = project.GetProperty("restore");
		Assert.Equal(JsonValueKind.Object, restore.ValueKind);
		bool hasFallbackFolders = restore.TryGetProperty("fallbackFolders", out JsonElement fallbackFolders);
		if (packageAuthority.OrderedRoots.Length == 1)
		{
			Assert.False(hasFallbackFolders, "A single package root must not declare restore fallbackFolders.");
			return;
		}

		Assert.True(hasFallbackFolders, "Multiple package roots require restore fallbackFolders.");
		AssertProjectAssetsFallbackFolders(fallbackFolders, packageAuthority);
	}

	private static string? TryReadCurrentRepositoryRevision()
	{
		DirectoryInfo? repository = Directory.GetParent(GetProductionRoot());
		return repository is null ? null : TryReadRepositoryRevision(repository.FullName);
	}

	private static string? TryReadRepositoryRevision(string repositoryRoot)
	{
		string canonicalRepositoryRoot = Path.GetFullPath(repositoryRoot);
		string gitEntry = Path.Combine(canonicalRepositoryRoot, ".git");
		string gitDirectory;
		if (Directory.Exists(gitEntry))
		{
			AssertRegularAuthorityDirectory(gitEntry, "current Git authority directory");
			gitDirectory = Path.GetFullPath(gitEntry);
		}
		else if (File.Exists(gitEntry))
		{
			AssertRegularAuthorityFile(gitEntry, "current Git authority indirection");
			string indirection = File.ReadAllText(gitEntry).Trim();
			const string GitDirectoryPrefix = "gitdir: ";
			if (!indirection.StartsWith(GitDirectoryPrefix, StringComparison.Ordinal))
			{
				return null;
			}
			string declaredDirectory = indirection[GitDirectoryPrefix.Length..];
			gitDirectory = Path.GetFullPath(Path.Combine(canonicalRepositoryRoot, declaredDirectory));
			AssertRegularAuthorityDirectory(gitDirectory, "current linked-worktree Git authority directory");
		}
		else
		{
			return null;
		}

		string commonGitDirectory = gitDirectory;
		string commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
		if (File.Exists(commonDirectoryPath))
		{
			AssertRegularAuthorityFile(commonDirectoryPath, "current Git common-directory authority");
			string declaredCommonDirectory = File.ReadAllText(commonDirectoryPath).Trim();
			if (string.IsNullOrWhiteSpace(declaredCommonDirectory))
			{
				return null;
			}
			commonGitDirectory = Path.GetFullPath(Path.Combine(gitDirectory, declaredCommonDirectory));
			AssertRegularAuthorityDirectory(commonGitDirectory, "current Git common authority directory");
		}

		string headPath = Path.Combine(gitDirectory, "HEAD");
		if (!File.Exists(headPath))
		{
			return null;
		}
		AssertRegularAuthorityFile(headPath, "current Git HEAD authority");
		string head = File.ReadAllText(headPath).Trim();
		if (Regex.IsMatch(head, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
		{
			return head;
		}

		const string RefPrefix = "ref: ";
		if (!head.StartsWith(RefPrefix, StringComparison.Ordinal))
		{
			return null;
		}
		string referenceName = head[RefPrefix.Length..];
		if (!IsValidGitReferenceName(referenceName))
		{
			return null;
		}
		string referencePath = Path.GetFullPath(Path.Combine(commonGitDirectory, referenceName));
		if (!IsPathWithin(referencePath, commonGitDirectory))
		{
			return null;
		}
		if (File.Exists(referencePath))
		{
			AssertRegularAuthorityFile(referencePath, "current Git reference authority");
			string revision = File.ReadAllText(referencePath).Trim();
			return Regex.IsMatch(revision, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
				? revision
				: null;
		}

		string packedReferencesPath = Path.Combine(commonGitDirectory, "packed-refs");
		if (!File.Exists(packedReferencesPath))
		{
			return null;
		}
		AssertRegularAuthorityFile(packedReferencesPath, "current packed Git reference authority");
		string? packedRevision = null;
		foreach (string line in File.ReadAllLines(packedReferencesPath))
		{
			int separator = line.IndexOf(' ');
			if (separator <= 0 || !StringComparer.Ordinal.Equals(line[(separator + 1)..], referenceName))
			{
				continue;
			}
			Assert.Null(packedRevision);
			string candidate = line[..separator];
			if (!Regex.IsMatch(candidate, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
			{
				return null;
			}
			packedRevision = candidate;
		}
		return packedRevision;
	}

	private static bool IsValidGitReferenceName(string referenceName)
	{
		if (!referenceName.StartsWith("refs/", StringComparison.Ordinal) ||
			referenceName.EndsWith('/') ||
			referenceName.EndsWith('.') ||
			referenceName.Contains("..", StringComparison.Ordinal) ||
			referenceName.Contains("//", StringComparison.Ordinal) ||
			referenceName.Contains("@{", StringComparison.Ordinal))
		{
			return false;
		}

		foreach (string component in referenceName.Split('/'))
		{
			if (component.Length == 0 ||
				component.StartsWith('.') ||
				component.EndsWith(".lock", StringComparison.Ordinal))
			{
				return false;
			}
		}

		foreach (char character in referenceName)
		{
			if (character <= ' ' || character == '\u007f' ||
				character is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
			{
				return false;
			}
		}
		return true;
	}

	private static string BuildPackageTransportAuthorityManifest(
		string packagesLockFile,
		string expectedTargetFramework)
	{
		(
			IReadOnlyDictionary<string, LockedPackageAuthority> packages,
			bool hasLinuxX64Overlay,
			bool hasContentHashes) =
			ReadLockedPackageAuthority(packagesLockFile, expectedTargetFramework);
		var manifest = new StringBuilder("PACKAGE_TRANSPORT_AUTHORITY_V1\n");
		manifest.Append(hasContentHashes
			? "PROFILE|CONTENT_HASHES_PRESENT\n"
			: "PROFILE|CONTENT_HASHES_ABSENT_PINNED_NIX\n");
		manifest.Append(hasLinuxX64Overlay
			? "RID_OVERLAY|LINUX_X64_PRESENT\n"
			: "RID_OVERLAY|LINUX_X64_ABSENT\n");
		string[] packageIds = packages.Keys.ToArray();
		Array.Sort(packageIds, StringComparer.Ordinal);
		foreach (string packageId in packageIds)
		{
			LockedPackageAuthority package = packages[packageId];
			manifest.Append("PACKAGE|");
			manifest.Append(packageId);
			manifest.Append('|');
			manifest.Append(package.ContentHash ?? "{ABSENT_PINNED_NIX_CONTENT_HASH}");
			manifest.Append('\n');
		}
		return manifest.ToString();
	}

	private static bool IsPinnedNixArchiveMetadataPath(string relativeFile)
	{
		if (relativeFile is "[Content_Types].xml" or "_rels/.rels")
		{
			return true;
		}

		const string CorePropertiesPrefix = "package/services/metadata/core-properties/";
		const string CorePropertiesSuffix = ".psmdcp";
		if (!relativeFile.StartsWith(CorePropertiesPrefix, StringComparison.Ordinal) ||
			!relativeFile.EndsWith(CorePropertiesSuffix, StringComparison.Ordinal))
		{
			return false;
		}
		string identity = relativeFile[
			CorePropertiesPrefix.Length..^CorePropertiesSuffix.Length];
		return Regex.IsMatch(identity, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
	}

	private static string BuildPackageMaterializationAuthorityManifest(
		string projectAssetsFile,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		AssertRegularAuthorityFile(projectAssetsFile, "package-materialization project assets authority");
		using JsonDocument document = JsonDocument.Parse(
			File.ReadAllText(projectAssetsFile),
			new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 });
		JsonElement libraries = document.RootElement.GetProperty("libraries");
		Assert.Equal(JsonValueKind.Object, libraries.ValueKind);
		JsonProperty[] packages = libraries.EnumerateObject().ToArray();
		SortJsonProperties(packages);
		var identities = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var manifest = new StringBuilder("PACKAGE_MATERIALIZATION_AUTHORITY_V2\n");
		foreach (JsonProperty package in packages)
		{
			Assert.True(identities.Add(package.Name), $"Duplicate package-materialization identity: {package.Name}");
			Assert.True(aliases.Add(package.Name), $"Duplicate or case-aliased package-materialization identity: {package.Name}");
			string packagePath = GetRequiredJsonString(
				package.Value.GetProperty("path"),
				$"package-materialization path for {package.Name}");
			AssertSafePackageRelativePath(packagePath);
			string? selectedPackageDirectory = null;
			foreach (string packageRoot in packageAuthority.OrderedRoots)
			{
				string? candidate = TryResolveExactPackageDirectory(
					packageRoot,
					packagePath,
					$"package-materialization directory for {package.Name}");
				if (candidate is not null)
				{
					selectedPackageDirectory = candidate;
					break;
				}
			}
			Assert.NotNull(selectedPackageDirectory);
			var directories = new List<string>();
			var directoryIdentities = new HashSet<string>(StringComparer.Ordinal);
			var topologyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string physicalDirectory in Directory.EnumerateDirectories(
				selectedPackageDirectory,
				"*",
				SearchOption.AllDirectories))
			{
				AssertRegularAuthorityDirectory(
					physicalDirectory,
					$"package-materialization directory for {package.Name}");
				string relativeDirectory = NormalizeRelativePath(
					Path.GetRelativePath(selectedPackageDirectory, physicalDirectory));
				AssertSafePackageRelativePath(relativeDirectory);
				Assert.True(directoryIdentities.Add(relativeDirectory));
				Assert.True(
					topologyAliases.Add(relativeDirectory),
					$"Duplicate or case-aliased package-materialization path: {package.Name}/{relativeDirectory}");
				directories.Add(relativeDirectory);
			}
			directories.Sort(StringComparer.Ordinal);
			foreach (string relativeDirectory in directories)
			{
				manifest.Append("DIRECTORY|");
				manifest.Append(JsonSerializer.Serialize(package.Name));
				manifest.Append('|');
				manifest.Append(JsonSerializer.Serialize(relativeDirectory));
				manifest.Append('\n');
			}

			var files = new List<string>();
			var fileIdentities = new HashSet<string>(StringComparer.Ordinal);
			foreach (string physicalFile in Directory.EnumerateFiles(
				selectedPackageDirectory,
				"*",
				SearchOption.AllDirectories))
			{
				AssertRegularAuthorityFile(physicalFile, $"package-materialization file for {package.Name}");
				string relativeFile = NormalizeRelativePath(
					Path.GetRelativePath(selectedPackageDirectory, physicalFile));
				AssertSafePackageRelativePath(relativeFile);
				Assert.True(fileIdentities.Add(relativeFile));
				Assert.True(
					topologyAliases.Add(relativeFile),
					$"Duplicate or case-aliased package-materialization path: {package.Name}/{relativeFile}");
				files.Add(relativeFile);
			}
			Assert.NotEmpty(files);
			files.Sort(StringComparer.Ordinal);
			foreach (string relativeFile in files)
			{
				string physicalFile = Path.GetFullPath(Path.Combine(
					selectedPackageDirectory,
					relativeFile.Replace('/', Path.DirectorySeparatorChar)));
				Assert.True(IsPathWithin(physicalFile, selectedPackageDirectory));
				manifest.Append("FILE|");
				manifest.Append(JsonSerializer.Serialize(package.Name));
				manifest.Append('|');
				manifest.Append(JsonSerializer.Serialize(relativeFile));
				manifest.Append('|');
				manifest.Append(Sha256File(physicalFile));
				manifest.Append('\n');
			}
		}
		Assert.NotEmpty(identities);
		return manifest.ToString();
	}

	private static string? TryResolveExactPackageDirectory(
		string packageRoot,
		string packagePath,
		string description)
	{
		AssertRegularAuthorityDirectory(packageRoot, "package authority root");
		AssertSafePackageRelativePath(packagePath);
		string current = Path.GetFullPath(packageRoot);
		foreach (string component in packagePath.Split('/'))
		{
			string? exactDirectory = null;
			int aliasCount = 0;
			foreach (string candidate in Directory.EnumerateDirectories(
				current,
				"*",
				SearchOption.TopDirectoryOnly))
			{
				string actualComponent = Path.GetFileName(candidate);
				if (!StringComparer.OrdinalIgnoreCase.Equals(actualComponent, component))
				{
					continue;
				}
				aliasCount++;
				if (StringComparer.Ordinal.Equals(actualComponent, component))
				{
					exactDirectory = candidate;
				}
			}
			if (aliasCount == 0)
			{
				return null;
			}
			Assert.Equal(1, aliasCount);
			Assert.NotNull(exactDirectory);
			AssertRegularAuthorityDirectory(exactDirectory, description);
			current = Path.GetFullPath(exactDirectory);
		}
		Assert.Equal(
			packagePath,
			NormalizeRelativePath(Path.GetRelativePath(packageRoot, current)));
		return current;
	}

	private static void AssertPinnedNixInformationalVersionAuthority(
		string informationalVersion,
		string commitHash,
		bool pinnedNixProfile)
	{
		if (!pinnedNixProfile)
		{
			return;
		}
		Assert.Matches("^[0-9a-f]{40}$", commitHash);
		Match pinnedNixVersion = Regex.Match(
			informationalVersion,
			"^2\\.0\\.0-[0-9]{8}-(?<revision>[0-9a-f]{40})(?:\\+(?<sourceRevision>[0-9a-f]{40}))?$",
			RegexOptions.CultureInvariant);
		Assert.True(pinnedNixVersion.Success, "The pinned-Nix informational version is not canonical.");
		Assert.Equal(commitHash, pinnedNixVersion.Groups["revision"].Value);
		if (pinnedNixVersion.Groups["sourceRevision"].Success)
		{
			Assert.Equal(commitHash, pinnedNixVersion.Groups["sourceRevision"].Value);
		}
	}

	private static string GetPinnedNixProjectAssetsVersion(
		string informationalVersion,
		string commitHash)
	{
		AssertPinnedNixInformationalVersionAuthority(
			informationalVersion,
			commitHash,
			pinnedNixProfile: true);
		int metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
		string projectVersion = metadataSeparator < 0
			? informationalVersion
			: informationalVersion[..metadataSeparator];
		Assert.Matches("^2\\.0\\.0-[0-9]{8}-[0-9a-f]{40}$", projectVersion);
		return projectVersion;
	}

	private static void AssertLoadedProductBuildIdentityAuthority(
		string assemblyVersion,
		string fileVersion,
		string informationalVersion,
		string commitHash,
		bool pinnedNixProfile)
	{
		Assert.True(
			commitHash.Length == 0 ||
			Regex.IsMatch(commitHash, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant),
			"The loaded CommitHash metadata is not empty or a full lowercase Git identity.");
		AssertPinnedNixInformationalVersionAuthority(informationalVersion, commitHash, pinnedNixProfile);
		Assert.True(Version.TryParse(assemblyVersion, out Version? parsedAssemblyVersion));
		Assert.Equal(4, parsedAssemblyVersion.ToString().Split('.').Length);
		Assert.Equal(assemblyVersion, parsedAssemblyVersion.ToString());
		Assert.True(Version.TryParse(fileVersion, out Version? parsedFileVersion));
		Assert.Equal(4, parsedFileVersion.ToString().Split('.').Length);
		Assert.Equal(fileVersion, parsedFileVersion.ToString());
		if (pinnedNixProfile)
		{
			Assert.Equal(GetPinnedNixVersionForDotnet(informationalVersion), assemblyVersion);
			Assert.Equal(assemblyVersion, fileVersion);
		}
	}

	private static string GetPinnedNixVersionForDotnet(string informationalVersion)
	{
		int metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
		string nixVersion = metadataSeparator < 0
			? informationalVersion
			: informationalVersion[..metadataSeparator];
		var numericComponents = new List<string>();
		int index = 0;
		while (index < nixVersion.Length)
		{
			while (index < nixVersion.Length && nixVersion[index] is '.' or '-')
			{
				index++;
			}
			if (index == nixVersion.Length)
			{
				break;
			}
			int start = index;
			if (char.IsAsciiDigit(nixVersion[index]))
			{
				while (index < nixVersion.Length && char.IsAsciiDigit(nixVersion[index]))
				{
					index++;
				}
				string component = nixVersion[start..index];
				string canonical = component.TrimStart('0');
				canonical = canonical.Length == 0 ? "0" : canonical;
				if (canonical.Length < 5 ||
					(canonical.Length == 5 && StringComparer.Ordinal.Compare(canonical, "65535") < 0))
				{
					numericComponents.Add(component);
				}
			}
			else
			{
				while (index < nixVersion.Length &&
					!char.IsAsciiDigit(nixVersion[index]) &&
					nixVersion[index] is not ('.' or '-'))
				{
					index++;
				}
			}
		}
		Assert.NotEmpty(numericComponents);
		while (numericComponents.Count < 4)
		{
			numericComponents.Add("0");
		}
		string versionProperty = string.Join('.', numericComponents.Take(4));
		Assert.True(Version.TryParse(versionProperty, out Version? parsedVersion));
		return parsedVersion.ToString();
	}

	private static string GetExpectedImportClosureSha256(bool debug)
	{
		if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
		{
			return debug
				? ExpectedDebugImportClosureSha256.MacOsArm64
				: ExpectedReleaseImportClosureSha256.MacOsArm64;
		}
		if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
		{
			return debug
				? ExpectedDebugImportClosureSha256.LinuxX64
				: ExpectedReleaseImportClosureSha256.LinuxX64;
		}
		throw new Xunit.Sdk.XunitException(
			$"Unsupported import authority platform: {RuntimeInformation.OSDescription}/{RuntimeInformation.OSArchitecture}");
	}

	private static string GetExpectedReferenceAuthoritySha256(bool debug)
	{
		if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
		{
			return debug
				? ExpectedDebugReferenceAuthoritySha256.MacOsArm64
				: ExpectedReleaseReferenceAuthoritySha256.MacOsArm64;
		}
		if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
		{
			return debug
				? ExpectedDebugReferenceAuthoritySha256.LinuxX64
				: ExpectedReleaseReferenceAuthoritySha256.LinuxX64;
		}
		throw new Xunit.Sdk.XunitException(
			$"Unsupported reference authority platform: {RuntimeInformation.OSDescription}/{RuntimeInformation.OSArchitecture}");
	}

	private static string NormalizeAndValidateUnexpandedImportProject(
		string value,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string repositoryRoot,
		string dotnetRoot,
		string authorityRoot)
	{
		foreach (string token in new[] { "{REPO}", "{DOTNET}", "{AUTHORITY}", "{NUGET}" })
		{
			int tokenOffset = value.IndexOf(token, StringComparison.Ordinal);
			if (tokenOffset >= 0)
			{
				throw new Xunit.Sdk.XunitException(
					$"Reserved import authority token at offset {tokenOffset}; expression SHA256 {Sha256Text(value)}.");
			}
		}
		string normalized = NormalizeAuthorityStringWithPackages(
			value,
			packageAuthority,
			("{REPO}", repositoryRoot),
			("{DOTNET}", dotnetRoot),
			("{AUTHORITY}", authorityRoot));
		AssertCanonicalUnexpandedImportProject(normalized);
		return normalized;
	}

	private static void AssertCanonicalUnexpandedImportProject(string value)
	{
		int backslashOffset = value.IndexOf('\\');
		if (backslashOffset >= 0)
		{
			throw new Xunit.Sdk.XunitException(
				$"Noncanonical import path separator at offset {backslashOffset}; expression SHA256 {Sha256Text(value)}.");
		}
		AssertNoUnapprovedAbsoluteImportPath(value);
		foreach (string token in new[] { "{REPO}", "{DOTNET}", "{AUTHORITY}", "{NUGET}" })
		{
			int searchOffset = 0;
			while (searchOffset < value.Length)
			{
				int tokenOffset = value.IndexOf(token, searchOffset, StringComparison.Ordinal);
				if (tokenOffset < 0)
				{
					break;
				}
				AssertTokenRelativeImportPathDoesNotEscape(value, tokenOffset + token.Length);
				searchOffset = tokenOffset + token.Length;
			}
		}
	}

	private static void AssertTokenRelativeImportPathDoesNotEscape(string value, int tokenEndOffset)
	{
		if (tokenEndOffset >= value.Length || value[tokenEndOffset] != '/')
		{
			return;
		}

		int depth = 0;
		int componentStart = tokenEndOffset + 1;
		for (int index = componentStart; index <= value.Length; index++)
		{
			bool atEnd = index == value.Length;
			bool atSeparator = !atEnd && value[index] == '/';
			bool atBoundary = !atEnd && IsLiteralImportPathTerminator(value, index);
			if (!atEnd && !atSeparator && !atBoundary)
			{
				continue;
			}

			ReadOnlySpan<char> component = value.AsSpan(componentStart, index - componentStart);
			if (component.SequenceEqual(".."))
			{
				if (depth == 0)
				{
					throw new Xunit.Sdk.XunitException(
						$"Import authority path escapes its normalized root at offset {componentStart}; expression SHA256 {Sha256Text(value)}.");
				}
				depth--;
			}
			else if (component.Length > 0 && !component.SequenceEqual("."))
			{
				depth++;
			}
			if (atEnd || atBoundary)
			{
				return;
			}
			componentStart = index + 1;
		}
	}

	private static void AssertNoUnapprovedAbsoluteImportPath(string value)
	{
		for (int index = 0; index < value.Length; index++)
		{
			bool atBoundary = IsRootedImportPathStart(value, index);
			bool rootedSlash = value[index] == '/' && atBoundary;
			bool rootedBackslash = value[index] == '\\' && atBoundary;
			bool rootedDrive = index + 2 < value.Length &&
				char.IsAsciiLetter(value[index]) && value[index + 1] == ':' && value[index + 2] is '/' or '\\' && atBoundary;
			if (rootedSlash || rootedBackslash || rootedDrive)
			{
				throw new Xunit.Sdk.XunitException(
					$"Unapproved absolute import path at offset {index}; expression SHA256 {Sha256Text(value)}.");
			}
		}
	}

	private static bool IsRootedImportPathStart(string value, int offset)
	{
		if (offset == 0)
		{
			return true;
		}
		char previous = value[offset - 1];
		if (char.IsWhiteSpace(previous) || previous is '"' or '\'' or '`' or ':' or ';')
		{
			return true;
		}
		if (previous is ',' or '(')
		{
			return IsInsideActiveMsBuildExpression(value, offset - 1);
		}
		return previous == '=' && !HasPriorImportPathSeparatorInExpression(value, offset);
	}

	private static bool HasPriorImportPathSeparatorInExpression(string value, int exclusiveEndOffset)
	{
		for (int index = exclusiveEndOffset - 1; index >= 0; index--)
		{
			if (value[index] is '/' or '\\')
			{
				return true;
			}
			if (char.IsWhiteSpace(value[index]) || value[index] is '"' or '\'' or '`' or ';')
			{
				return false;
			}
		}
		return false;
	}

	private static bool IsInsideActiveMsBuildExpression(string value, int exclusiveEndOffset)
	{
		int expressionDepth = 0;
		char quote = '\0';
		for (int index = 0; index < exclusiveEndOffset; index++)
		{
			char character = value[index];
			if (quote != '\0')
			{
				if (character == quote)
				{
					quote = '\0';
				}
				continue;
			}
			if (character is '"' or '\'' or '`')
			{
				quote = character;
				continue;
			}
			if (character == '$' && index + 1 < exclusiveEndOffset && value[index + 1] == '(')
			{
				expressionDepth++;
				index++;
				continue;
			}
			if (expressionDepth > 0 && character == '(')
			{
				expressionDepth++;
			}
			else if (expressionDepth > 0 && character == ')')
			{
				expressionDepth--;
			}
		}
		return expressionDepth > 0 && quote == '\0';
	}

	private static bool IsLiteralImportPathTerminator(string value, int offset)
	{
		char character = value[offset];
		return char.IsWhiteSpace(character) ||
			character is '"' or '\'' or '`' or ';' ||
			(character == ',' && IsInsideActiveMsBuildExpression(value, offset));
	}

	private static string BuildCanonicalImportManifestRow(string prefix, IReadOnlyList<string> fields)
	{
		Assert.True(prefix is "IMPORT_EVENT_V2" or "PIN_V2");
		return BuildCanonicalAuthorityManifestRow(prefix, fields);
	}

	private static string BuildCanonicalAuthorityManifestRow(string prefix, IReadOnlyList<string> fields)
	{
		Assert.True(prefix is
			"IMPORT_EVENT_V2" or "PIN_V2" or "REFERENCE_V2" or "COMPILER_INPUT_V2" or
			"TOOLCHAIN_FILE_V2" or "ANALYZER_V2");
		var values = new string[fields.Count];
		var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
		for (int index = 0; index < fields.Count; index++)
		{
			string value = Assert.IsType<string>(fields[index]);
			try
			{
				_ = strictUtf8.GetByteCount(value);
			}
			catch (EncoderFallbackException)
			{
				throw new Xunit.Sdk.XunitException($"Authority manifest field {index} is not valid UTF-8 text.");
			}
			values[index] = value;
		}
		return prefix + "|" + JsonSerializer.Serialize(values);
	}

	private static string[] ParseCanonicalImportManifestRow(string row, string expectedPrefix, int expectedFieldCount)
	{
		Assert.True(expectedPrefix is "IMPORT_EVENT_V2" or "PIN_V2");
		return ParseCanonicalAuthorityManifestRow(row, expectedPrefix, expectedFieldCount);
	}

	private static string[] ParseCanonicalAuthorityManifestRow(
		string row,
		string expectedPrefix,
		int expectedFieldCount)
	{
		Assert.True(expectedPrefix is
			"IMPORT_EVENT_V2" or "PIN_V2" or "REFERENCE_V2" or "COMPILER_INPUT_V2" or
			"TOOLCHAIN_FILE_V2" or "ANALYZER_V2");
		string prefix = expectedPrefix + "|";
		Assert.StartsWith(prefix, row, StringComparison.Ordinal);
		string payload = row[prefix.Length..];
		using JsonDocument document = JsonDocument.Parse(
			payload,
			new JsonDocumentOptions
			{
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
				MaxDepth = 4,
			});
		Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
		Assert.Equal(expectedFieldCount, document.RootElement.GetArrayLength());
		var fields = new string[expectedFieldCount];
		for (int index = 0; index < fields.Length; index++)
		{
			JsonElement field = document.RootElement[index];
			Assert.Equal(JsonValueKind.String, field.ValueKind);
			fields[index] = Assert.IsType<string>(field.GetString());
		}
		Assert.Equal(payload, JsonSerializer.Serialize(fields));
		return fields;
	}

	private static string[] AssertCanonicalImportAuthorityManifest(string manifest)
	{
		Assert.DoesNotContain('\r', manifest);
		Assert.True(manifest.EndsWith('\n'));
		string[] lines = manifest.Split('\n', StringSplitOptions.None);
		Assert.True(lines.Length >= 3);
		Assert.Equal("", lines[^1]);
		Assert.Equal("IMPORT_AUTHORITY_V2", lines[0]);
		var rows = new string[lines.Length - 2];
		int importCount = 0;
		int pinCount = 0;
		bool pinsStarted = false;
		for (int lineIndex = 1; lineIndex < lines.Length - 1; lineIndex++)
		{
			string row = lines[lineIndex];
			Assert.False(string.IsNullOrEmpty(row));
			rows[lineIndex - 1] = row;
			if (row.StartsWith("IMPORT_EVENT_V2|", StringComparison.Ordinal))
			{
				Assert.False(pinsStarted, "An import event appeared after the pinned-file section.");
				string[] fields = ParseCanonicalImportManifestRow(row, "IMPORT_EVENT_V2", 9);
				Assert.Equal(importCount.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[0]);
				Assert.True(fields[1] is "0" or "1");
				Assert.False(string.IsNullOrWhiteSpace(fields[2]));
				AssertCanonicalUnexpandedImportProject(fields[2]);
				Assert.False(string.IsNullOrWhiteSpace(fields[3]));
				AssertCanonicalNormalizedImportPath(fields[3]);
				AssertCanonicalNonNegativeInteger(fields[4]);
				AssertCanonicalNonNegativeInteger(fields[5]);
				if (fields[6] is "null" or "empty")
				{
					Assert.Equal("", fields[7]);
					Assert.Equal("", fields[8]);
				}
				else
				{
					Assert.Equal("file", fields[6]);
					AssertCanonicalNormalizedImportPath(fields[7]);
					Assert.Matches("^[0-9a-f]{64}$", fields[8]);
				}
				importCount++;
			}
			else
			{
				pinsStarted = true;
				string[] fields = ParseCanonicalImportManifestRow(row, "PIN_V2", 2);
				AssertCanonicalNormalizedImportPath(fields[0]);
				Assert.Matches("^[0-9a-f]{64}$", fields[1]);
				pinCount++;
			}
		}
		Assert.True(importCount > 0);
		Assert.Equal(8, pinCount);
		return rows;
	}

	private static void AssertCanonicalNormalizedImportPath(string value)
	{
		Assert.True(
			value.StartsWith("REPO|", StringComparison.Ordinal) ||
			value.StartsWith("DOTNET|", StringComparison.Ordinal) ||
			value.StartsWith("NUGET|", StringComparison.Ordinal));
		int delimiter = value.IndexOf('|');
		Assert.True(delimiter > 0 && delimiter < value.Length - 1);
		Assert.DoesNotContain('\\', value);
		Assert.DoesNotContain('\r', value);
		Assert.DoesNotContain('\n', value);
		AssertSafePackageRelativePath(value[(delimiter + 1)..]);
	}

	private static void AssertCanonicalNonNegativeInteger(string value)
	{
		Assert.True(int.TryParse(
			value,
			System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture,
			out int parsed));
		Assert.True(parsed >= 0);
		Assert.Equal(parsed.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
	}

	private static void AssertConfiguredAuthorityHashesRejects(
		string importManifest,
		string referenceManifest,
		string compilerManifest,
		string toolchainManifest)
	{
		bool rejected = false;
		try
		{
			AssertConfiguredAuthorityHashes(importManifest, referenceManifest, compilerManifest, toolchainManifest);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "The mutated build-authority manifest was accepted.");
	}

	private static string CreateCompilerManifestWithoutFirstArgument(string manifest)
	{
		List<string[]> fields = ReadCanonicalCompilerAuthorityFields(manifest);
		int firstArgument = fields.FindIndex(row => row[1] == "ARG");
		Assert.True(firstArgument >= 0);
		Assert.True(fields.Count(row => row[1] == "ARG") > 1);
		fields.RemoveAt(firstArgument);
		return RebuildCanonicalCompilerInputAuthorityManifest(fields);
	}

	private static string CreateCompilerManifestWithDuplicatedFirstArgument(string manifest)
	{
		List<string[]> fields = ReadCanonicalCompilerAuthorityFields(manifest);
		int firstArgument = fields.FindIndex(row => row[1] == "ARG");
		Assert.True(firstArgument >= 0);
		fields.Insert(firstArgument + 1, fields[firstArgument].ToArray());
		return RebuildCanonicalCompilerInputAuthorityManifest(fields);
	}

	private static string CreateCompilerManifestWithSwappedFirstArguments(string manifest)
	{
		List<string[]> fields = ReadCanonicalCompilerAuthorityFields(manifest);
		int firstArgument = fields.FindIndex(row => row[1] == "ARG");
		int secondArgument = fields.FindIndex(firstArgument + 1, row => row[1] == "ARG");
		Assert.True(firstArgument >= 0 && secondArgument > firstArgument);
		(fields[firstArgument], fields[secondArgument]) = (fields[secondArgument], fields[firstArgument]);
		return RebuildCanonicalCompilerInputAuthorityManifest(fields);
	}

	private static string CreateCompilerManifestWithMutatedFirstAuxiliarySha256(string manifest)
	{
		List<string[]> fields = ReadCanonicalCompilerAuthorityFields(manifest);
		int auxiliaryIndex = fields.FindIndex(row => row[1] == "AUX");
		Assert.True(auxiliaryIndex >= 0);
		string[] auxiliary = fields[auxiliaryIndex];
		auxiliary[7] = auxiliary[7][0] == '0'
			? "1" + auxiliary[7][1..]
			: "0" + auxiliary[7][1..];
		return RebuildCanonicalCompilerInputAuthorityManifest(fields);
	}

	private static string CreateCompilerManifestWithInjectedAnalyzerArguments(string manifest)
	{
		List<string[]> fields = ReadCanonicalCompilerAuthorityFields(manifest);
		int firstNonArgument = fields.FindIndex(row => row[1] != "ARG");
		Assert.True(firstNonArgument > 0);
		fields.Insert(
			firstNonArgument,
			CreateCompilerAuthorityFields("ARG", "/analyzer:/wlpq/injected-analyzer.dll"));
		int firstCscArgument = fields.FindIndex(row => row[1] == "CSC_ARG");
		Assert.True(firstCscArgument > 0);
		fields.Insert(
			firstCscArgument,
			CreateCompilerAuthorityFields(
				"CSC_INPUT",
				"Analyzers",
				qualifier: "Analyzer",
				values: JsonSerializer.Serialize(new[] { "/wlpq/injected-analyzer.dll" })));
		fields.Add(CreateCompilerAuthorityFields("CSC_ARG", "/analyzer:/wlpq/injected-analyzer.dll"));
		return RebuildCanonicalCompilerInputAuthorityManifest(fields);
	}

	private static string CreateCompilerManifestWithV1Header(string manifest)
	{
		const string Header = "COMPILER_INPUT_AUTHORITY_V2";
		Assert.StartsWith(Header + "\n", manifest, StringComparison.Ordinal);
		return "COMPILER_INPUT_AUTHORITY_V1" + manifest[Header.Length..];
	}

	private static string CreateCompilerManifestWithNonCanonicalFirstRow(string manifest)
	{
		string[] lines = SplitCanonicalCompilerInputAuthorityManifest(manifest);
		Assert.Contains("|[", lines[1], StringComparison.Ordinal);
		lines[1] = lines[1].Replace("|[", "|[ ", StringComparison.Ordinal);
		return string.Join('\n', lines);
	}

	private static string CreateCompilerManifestWithSkippedGlobalIndex(string manifest)
	{
		string[] lines = SplitCanonicalCompilerInputAuthorityManifest(manifest);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[1], "COMPILER_INPUT_V2", 8);
		fields[0] = "1";
		lines[1] = BuildCanonicalAuthorityManifestRow("COMPILER_INPUT_V2", fields);
		return string.Join('\n', lines);
	}

	private static string CreateCompilerManifestWithSkippedSectionIndex(string manifest)
	{
		string[] lines = SplitCanonicalCompilerInputAuthorityManifest(manifest);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[1], "COMPILER_INPUT_V2", 8);
		fields[2] = "1";
		lines[1] = BuildCanonicalAuthorityManifestRow("COMPILER_INPUT_V2", fields);
		return string.Join('\n', lines);
	}

	private static string CreateCompilerManifestWithUnknownFirstSection(string manifest)
	{
		string[] lines = SplitCanonicalCompilerInputAuthorityManifest(manifest);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[1], "COMPILER_INPUT_V2", 8);
		fields[1] = "UNKNOWN";
		lines[1] = BuildCanonicalAuthorityManifestRow("COMPILER_INPUT_V2", fields);
		return string.Join('\n', lines);
	}

	private static string CreateCompilerManifestWithExtraFirstField(string manifest)
	{
		string[] lines = SplitCanonicalCompilerInputAuthorityManifest(manifest);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[1], "COMPILER_INPUT_V2", 8);
		lines[1] = BuildCanonicalAuthorityManifestRow(
			"COMPILER_INPUT_V2",
			fields.Append("EXTRA").ToArray());
		return string.Join('\n', lines);
	}

	private static string CreateCompilerManifestWithNumericFirstIndex(string manifest)
	{
		string[] lines = SplitCanonicalCompilerInputAuthorityManifest(manifest);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[1], "COMPILER_INPUT_V2", 8);
		object[] typedFields = fields.Cast<object>().ToArray();
		typedFields[0] = 0;
		lines[1] = "COMPILER_INPUT_V2|" + JsonSerializer.Serialize(typedFields);
		return string.Join('\n', lines);
	}

	private static string CreateCompilerManifestWithInvalidAuxiliaryPrefix(string manifest)
	{
		string[] lines = SplitCanonicalCompilerInputAuthorityManifest(manifest);
		int auxiliaryLine = Array.FindIndex(lines, line =>
			line.StartsWith("COMPILER_INPUT_V2|", StringComparison.Ordinal) &&
			ParseCanonicalAuthorityManifestRow(line, "COMPILER_INPUT_V2", 8)[1] == "AUX");
		Assert.True(auxiliaryLine > 0);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[auxiliaryLine], "COMPILER_INPUT_V2", 8);
		fields[3] = "/unapproved:";
		lines[auxiliaryLine] = BuildCanonicalAuthorityManifestRow("COMPILER_INPUT_V2", fields);
		return string.Join('\n', lines);
	}

	private static List<string[]> ReadCanonicalCompilerAuthorityFields(string manifest) =>
		AssertCanonicalCompilerInputAuthorityManifest(manifest)
			.Select(row => ParseCanonicalAuthorityManifestRow(row, "COMPILER_INPUT_V2", 8))
			.ToList();

	private static string RebuildCanonicalCompilerInputAuthorityManifest(IReadOnlyList<string[]> fields)
	{
		var sectionIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
		string[] rows = fields.Select((sourceFields, globalIndex) =>
		{
			Assert.Equal(8, sourceFields.Length);
			string[] rebuiltFields = sourceFields.ToArray();
			rebuiltFields[0] = globalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
			int sectionIndex = sectionIndexes.GetValueOrDefault(rebuiltFields[1]);
			rebuiltFields[2] = sectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
			sectionIndexes[rebuiltFields[1]] = sectionIndex + 1;
			return BuildCanonicalAuthorityManifestRow("COMPILER_INPUT_V2", rebuiltFields);
		}).ToArray();
		string rebuilt = "COMPILER_INPUT_AUTHORITY_V2\n" + string.Join('\n', rows) + "\n";
		_ = AssertCanonicalCompilerInputAuthorityManifest(rebuilt);
		return rebuilt;
	}

	private static string[] CreateCompilerAuthorityFields(
		string section,
		string identity,
		string detail = "",
		string qualifier = "",
		string values = "",
		string sha256 = "") =>
		["0", section, "0", identity, detail, qualifier, values, sha256];

	private static string[] SplitCanonicalCompilerInputAuthorityManifest(string manifest)
	{
		_ = AssertCanonicalCompilerInputAuthorityManifest(manifest);
		return manifest.Split('\n', StringSplitOptions.None);
	}

	private static string CreateImportManifestWithDuplicatedLastImport(string manifest)
	{
		string[] lines = SplitCanonicalImportAuthorityManifest(manifest);
		int firstPinLineIndex = GetFirstPinManifestLineIndex(lines);
		string[] duplicateFields = ParseCanonicalImportManifestRow(
			lines[firstPinLineIndex - 1],
			"IMPORT_EVENT_V2",
			9);
		duplicateFields[0] = (firstPinLineIndex - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
		var mutatedLines = new List<string>(lines);
		mutatedLines.Insert(
			firstPinLineIndex,
			BuildCanonicalImportManifestRow("IMPORT_EVENT_V2", duplicateFields));
		string mutated = string.Join('\n', mutatedLines);
		Assert.NotEqual(manifest, mutated);
		_ = AssertCanonicalImportAuthorityManifest(mutated);
		return mutated;
	}

	private static string CreateImportManifestWithoutLastImport(string manifest)
	{
		string[] lines = SplitCanonicalImportAuthorityManifest(manifest);
		int firstPinLineIndex = GetFirstPinManifestLineIndex(lines);
		var mutatedLines = new List<string>(lines);
		mutatedLines.RemoveAt(firstPinLineIndex - 1);
		string mutated = string.Join('\n', mutatedLines);
		Assert.NotEqual(manifest, mutated);
		_ = AssertCanonicalImportAuthorityManifest(mutated);
		return mutated;
	}

	private static string CreateImportManifestWithSwappedFirstImports(string manifest)
	{
		string[] lines = SplitCanonicalImportAuthorityManifest(manifest);
		int firstPinLineIndex = GetFirstPinManifestLineIndex(lines);
		Assert.True(firstPinLineIndex >= 3);
		string[] firstFields = ParseCanonicalImportManifestRow(lines[1], "IMPORT_EVENT_V2", 9);
		string[] secondFields = ParseCanonicalImportManifestRow(lines[2], "IMPORT_EVENT_V2", 9);
		for (int fieldIndex = 1; fieldIndex < firstFields.Length; fieldIndex++)
		{
			(firstFields[fieldIndex], secondFields[fieldIndex]) = (secondFields[fieldIndex], firstFields[fieldIndex]);
		}
		lines[1] = BuildCanonicalImportManifestRow("IMPORT_EVENT_V2", firstFields);
		lines[2] = BuildCanonicalImportManifestRow("IMPORT_EVENT_V2", secondFields);
		string mutated = string.Join('\n', lines);
		Assert.NotEqual(manifest, mutated);
		_ = AssertCanonicalImportAuthorityManifest(mutated);
		return mutated;
	}

	private static string CreateImportManifestWithDuplicatedFirstPin(string manifest)
	{
		string[] lines = SplitCanonicalImportAuthorityManifest(manifest);
		int firstPinLineIndex = GetFirstPinManifestLineIndex(lines);
		var mutatedLines = new List<string>(lines);
		mutatedLines.Insert(firstPinLineIndex, lines[firstPinLineIndex]);
		string mutated = string.Join('\n', mutatedLines);
		Assert.NotEqual(manifest, mutated);
		return mutated;
	}

	private static string CreateImportManifestWithMutatedFirstImportField(
		string manifest,
		int fieldIndex,
		string value)
	{
		string[] lines = SplitCanonicalImportAuthorityManifest(manifest);
		string[] fields = ParseCanonicalImportManifestRow(lines[1], "IMPORT_EVENT_V2", 9);
		Assert.InRange(fieldIndex, 0, fields.Length - 1);
		fields[fieldIndex] = value;
		lines[1] = BuildCanonicalImportManifestRow("IMPORT_EVENT_V2", fields);
		string mutated = string.Join('\n', lines);
		Assert.NotEqual(manifest, mutated);
		return mutated;
	}

	private static string[] SplitCanonicalImportAuthorityManifest(string manifest)
	{
		_ = AssertCanonicalImportAuthorityManifest(manifest);
		return manifest.Split('\n', StringSplitOptions.None);
	}

	private static int GetFirstPinManifestLineIndex(string[] lines)
	{
		for (int lineIndex = 1; lineIndex < lines.Length - 1; lineIndex++)
		{
			if (lines[lineIndex].StartsWith("PIN_V2|", StringComparison.Ordinal))
			{
				return lineIndex;
			}
		}
		throw new Xunit.Sdk.XunitException("The canonical import-authority manifest has no pinned-file section.");
	}

	private static Xunit.Sdk.XunitException AssertUnexpandedImportProjectRejected(
		string value,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string repositoryRoot,
		string dotnetRoot,
		string authorityRoot)
	{
		try
		{
			_ = NormalizeAndValidateUnexpandedImportProject(
				value,
				packageAuthority,
				repositoryRoot,
				dotnetRoot,
				authorityRoot);
		}
		catch (Xunit.Sdk.XunitException exception)
		{
			return exception;
		}
		throw new Xunit.Sdk.XunitException("The unapproved import expression was accepted.");
	}

	private static void AssertExactImportAuthoritySha256(string expectedSha256, string manifest)
	{
		string[] rows = AssertCanonicalImportAuthorityManifest(manifest);
		string actualSha256 = Sha256Text(manifest);
		if (StringComparer.Ordinal.Equals(expectedSha256, actualSha256))
		{
			return;
		}

		var importRows = new StringBuilder();
		var pinRows = new StringBuilder();
		var diagnostics = new StringBuilder(actualSha256);
		for (int index = 0; index < rows.Length; index++)
		{
			string row = rows[index];
			if (row.StartsWith("IMPORT_EVENT_V2|", StringComparison.Ordinal))
			{
				importRows.Append(row).Append('\n');
			}
			else if (row.StartsWith("PIN_V2|", StringComparison.Ordinal))
			{
				pinRows.Append(row).Append('\n');
			}
			diagnostics.Append("\nROW_SHA256|");
			diagnostics.Append(index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
			diagnostics.Append('|');
			diagnostics.Append(Sha256Text(row));
		}
		string[] sortedRows = rows.ToArray();
		Array.Sort(sortedRows, StringComparer.Ordinal);
		diagnostics.Append("\nIMPORT_ROWS_SHA256|").Append(Sha256Text(importRows.ToString()));
		diagnostics.Append("\nPIN_ROWS_SHA256|").Append(Sha256Text(pinRows.ToString()));
		diagnostics.Append("\nSORTED_ROWS_SHA256|").Append(Sha256Text(string.Join('\n', sortedRows) + "\n"));
		throw new Xunit.Sdk.XunitException(diagnostics.ToString());
	}

	private static void AssertExactCompilerInputAuthoritySha256(string expectedSha256, string manifest)
	{
		string[] rows = AssertCanonicalCompilerInputAuthorityManifest(manifest);
		string actualSha256 = Sha256Text(manifest);
		if (StringComparer.Ordinal.Equals(expectedSha256, actualSha256))
		{
			return;
		}

		var diagnostics = new StringBuilder(actualSha256);
		var sectionRows = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
		var sectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		for (int index = 0; index < rows.Length; index++)
		{
			string row = rows[index];
			string[] fields = ParseCanonicalAuthorityManifestRow(row, "COMPILER_INPUT_V2", 8);
			diagnostics.Append("\nROW|").Append(fields[0]);
			diagnostics.Append("|SECTION|").Append(fields[1]);
			diagnostics.Append("|SECTION_INDEX|").Append(fields[2]);
			diagnostics.Append("|ROW_SHA256|").Append(Sha256Text(row));
			if (!sectionRows.TryGetValue(fields[1], out StringBuilder? sectionManifest))
			{
				sectionManifest = new StringBuilder();
				sectionRows.Add(fields[1], sectionManifest);
			}
			sectionManifest.Append(row).Append('\n');
			sectionCounts[fields[1]] = sectionCounts.GetValueOrDefault(fields[1]) + 1;
		}
		foreach (string section in CompilerAuthoritySectionOrder)
		{
			if (!sectionRows.TryGetValue(section, out StringBuilder? sectionManifest))
			{
				continue;
			}
			diagnostics.Append("\nSECTION|").Append(section);
			diagnostics.Append("|COUNT|").Append(sectionCounts[section]);
			diagnostics.Append("|SHA256|").Append(Sha256Text(sectionManifest.ToString()));
		}
		string[] sortedRows = rows.ToArray();
		Array.Sort(sortedRows, StringComparer.Ordinal);
		diagnostics.Append("\nSORTED_ROWS_SHA256|").Append(Sha256Text(string.Join('\n', sortedRows) + "\n"));
		throw new Xunit.Sdk.XunitException(diagnostics.ToString());
	}

	private static void AssertExactReferenceAuthoritySha256(string expectedSha256, string manifest)
	{
		string[] rows = AssertCanonicalReferenceAuthorityManifest(manifest);
		string actualSha256 = Sha256Text(manifest);
		var diagnostics = new StringBuilder(actualSha256);
		for (int index = 0; index < rows.Length; index++)
		{
			string[] fields = ParseCanonicalAuthorityManifestRow(rows[index], "REFERENCE_V2", 5);
			diagnostics.Append("\nROW|").Append(fields[0]);
			diagnostics.Append("|ROW_SHA256|").Append(Sha256Text(rows[index]));
			diagnostics.Append("|PATH_SHA256|").Append(Sha256Text(fields[1]));
			diagnostics.Append("|CONTENT_SHA256|").Append(fields[2]);
			diagnostics.Append("|PROVENANCE_SHA256|").Append(Sha256Text(fields[3]));
			diagnostics.Append("|ALIASES_SHA256|").Append(Sha256Text(fields[4]));
		}
		if (StringComparer.Ordinal.Equals(expectedSha256, actualSha256))
		{
			return;
		}
		string[] sortedRows = rows.ToArray();
		Array.Sort(sortedRows, StringComparer.Ordinal);
		diagnostics.Append("\nSORTED_ROWS_SHA256|").Append(Sha256Text(string.Join('\n', sortedRows) + "\n"));
		throw new Xunit.Sdk.XunitException(diagnostics.ToString());
	}

	private static string CreateReferenceManifestWithMutatedFirstContent(string manifest)
	{
		_ = AssertCanonicalReferenceAuthorityManifest(manifest);
		string[] lines = manifest.Split('\n', StringSplitOptions.None);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[1], "REFERENCE_V2", 5);
		fields[2] = fields[2][0] == '0'
			? "1" + fields[2][1..]
			: "0" + fields[2][1..];
		lines[1] = BuildCanonicalAuthorityManifestRow("REFERENCE_V2", fields);
		string mutated = string.Join('\n', lines);
		Assert.NotEqual(manifest, mutated);
		_ = AssertCanonicalReferenceAuthorityManifest(mutated);
		return mutated;
	}

	private static string[] AssertCanonicalReferenceAuthorityManifest(string manifest)
	{
		Assert.DoesNotContain('\r', manifest);
		Assert.True(manifest.EndsWith('\n'));
		string[] lines = manifest.Split('\n', StringSplitOptions.None);
		Assert.True(lines.Length >= 3);
		Assert.Equal("", lines[^1]);
		Assert.Equal("REFERENCE_AUTHORITY_V2", lines[0]);
		var rows = new string[lines.Length - 2];
		for (int lineIndex = 1; lineIndex < lines.Length - 1; lineIndex++)
		{
			string row = lines[lineIndex];
			Assert.False(string.IsNullOrEmpty(row));
			rows[lineIndex - 1] = row;
			string[] fields = ParseCanonicalAuthorityManifestRow(row, "REFERENCE_V2", 5);
			Assert.Equal(
				(lineIndex - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
				fields[0]);
			AssertCanonicalNormalizedImportPath(fields[1]);
			Assert.Matches("^[0-9a-f]{64}$", fields[2]);
			if (fields[3] != "NONE")
			{
				AssertCanonicalNormalizedImportPath(fields[3]);
			}
		}
		return rows;
	}

	[Fact]
	public void GeneratedMsBuildEditorConfigAuthorityCanonicalizesOnlyProjectDirectory()
	{
		string fixtureRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-editorconfig-authority-{Guid.NewGuid():N}");
		try
		{
			string firstRepositoryRoot = Path.Combine(fixtureRoot, "first/repository");
			string secondRepositoryRoot = Path.Combine(fixtureRoot, "second/repository");
			string firstProjectRoot = Path.Combine(firstRepositoryRoot, "WalletWasabi");
			string secondProjectRoot = Path.Combine(secondRepositoryRoot, "WalletWasabi");
			string firstAuthorityRoot = Path.Combine(fixtureRoot, "first/authority");
			string secondAuthorityRoot = Path.Combine(fixtureRoot, "second/authority");
			string firstGeneratedRoot = Path.Combine(firstAuthorityRoot, "generated");
			string secondGeneratedRoot = Path.Combine(secondAuthorityRoot, "generated");
			string firstIntermediateRoot = Path.Combine(firstAuthorityRoot, "obj/net10.0");
			string secondIntermediateRoot = Path.Combine(secondAuthorityRoot, "obj/net10.0");
			Directory.CreateDirectory(firstProjectRoot);
			Directory.CreateDirectory(secondProjectRoot);
			Directory.CreateDirectory(firstGeneratedRoot);
			Directory.CreateDirectory(secondGeneratedRoot);
			Directory.CreateDirectory(firstIntermediateRoot);
			Directory.CreateDirectory(secondIntermediateRoot);

			string firstProjectLine = "build_property.ProjectDir = " +
				Path.GetFullPath(firstProjectRoot).Replace('\\', '/').TrimEnd('/') + "/";
			string secondProjectLine = "build_property.ProjectDir = " +
				Path.GetFullPath(secondProjectRoot).Replace('\\', '/').TrimEnd('/') + "/";
			string firstSource =
				"is_global = true\n" +
				"build_property.RootNamespace = WalletWasabi\n" +
				firstProjectLine + "\n" +
				"build_property.Stable = value\n";
			string secondSource = firstSource.Replace(
				firstProjectLine,
				secondProjectLine,
				StringComparison.Ordinal);
			byte[] firstSourceBytes = Encoding.UTF8.GetBytes(firstSource);
			byte[] secondSourceBytes = Encoding.UTF8.GetBytes(secondSource);
			string firstCanonical = CanonicalizeGeneratedMsBuildEditorConfigBytes(
				firstSourceBytes,
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			string secondCanonical = CanonicalizeGeneratedMsBuildEditorConfigBytes(
				secondSourceBytes,
				secondProjectRoot,
				secondRepositoryRoot,
				secondAuthorityRoot,
				secondGeneratedRoot,
				secondIntermediateRoot);
			Assert.Equal(firstCanonical, secondCanonical);
			Assert.Equal(Sha256Text(firstCanonical), Sha256Text(secondCanonical));
			Assert.Contains(
				"build_property.ProjectDir = {REPO}/WalletWasabi/\n",
				firstCanonical,
				StringComparison.Ordinal);

			byte[] utf8BomSource = new byte[firstSourceBytes.Length + 3];
			utf8BomSource[0] = 0xef;
			utf8BomSource[1] = 0xbb;
			utf8BomSource[2] = 0xbf;
			firstSourceBytes.CopyTo(utf8BomSource, 3);
			AssertGeneratedMsBuildEditorConfigBytesRejected(
				utf8BomSource,
				"must not have a UTF-8 BOM",
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);

			byte[] utf16LittleEndianPayload = Encoding.Unicode.GetBytes(firstSource);
			byte[] utf16LittleEndianSource = new byte[utf16LittleEndianPayload.Length + 2];
			utf16LittleEndianSource[0] = 0xff;
			utf16LittleEndianSource[1] = 0xfe;
			utf16LittleEndianPayload.CopyTo(utf16LittleEndianSource, 2);
			AssertGeneratedMsBuildEditorConfigBytesRejected(
				utf16LittleEndianSource,
				"must not have a UTF-16 little-endian BOM",
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);

			byte[] utf16BigEndianPayload = Encoding.BigEndianUnicode.GetBytes(firstSource);
			byte[] utf16BigEndianSource = new byte[utf16BigEndianPayload.Length + 2];
			utf16BigEndianSource[0] = 0xfe;
			utf16BigEndianSource[1] = 0xff;
			utf16BigEndianPayload.CopyTo(utf16BigEndianSource, 2);
			AssertGeneratedMsBuildEditorConfigBytesRejected(
				utf16BigEndianSource,
				"must not have a UTF-16 big-endian BOM",
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			byte[] malformedUtf8Source = Encoding.UTF8.GetBytes(
				firstSource.Replace("value", "vXlue", StringComparison.Ordinal));
			int malformedByteIndex = -1;
			for (int index = 0; index < malformedUtf8Source.Length; index++)
			{
				if (malformedUtf8Source[index] != (byte)'X')
				{
					continue;
				}
				Assert.Equal(-1, malformedByteIndex);
				malformedByteIndex = index;
			}
			Assert.True(malformedByteIndex >= 0);
			malformedUtf8Source[malformedByteIndex] = 0xff;
			AssertGeneratedMsBuildEditorConfigBytesRejected(
				malformedUtf8Source,
				"is not strict BOM-less UTF-8",
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);

			AssertGeneratedMsBuildEditorConfigRejected(
				firstSource.Replace(firstProjectLine + "\n", "", StringComparison.Ordinal),
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			AssertGeneratedMsBuildEditorConfigRejected(
				firstSource.Replace(
					firstProjectLine + "\n",
					firstProjectLine + "\n" + firstProjectLine + "\n",
					StringComparison.Ordinal),
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			AssertGeneratedMsBuildEditorConfigRejected(
				secondSource,
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			AssertGeneratedMsBuildEditorConfigRejected(
				firstSource.Replace("\n", "\r\n", StringComparison.Ordinal),
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			AssertGeneratedMsBuildEditorConfigRejected(
				firstSource[..^1],
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			AssertGeneratedMsBuildEditorConfigRejected(
				firstSource + "\n",
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			AssertGeneratedMsBuildEditorConfigRejected(
				firstSource.Replace("value", "{REPO}", StringComparison.Ordinal),
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			byte[] singleByteMutation = Encoding.UTF8.GetBytes(
				firstSource.Replace("value", "walue", StringComparison.Ordinal));
			Assert.Equal(firstSourceBytes.Length, singleByteMutation.Length);
			int differingByteCount = 0;
			for (int index = 0; index < firstSourceBytes.Length; index++)
			{
				if (firstSourceBytes[index] != singleByteMutation[index])
				{
					differingByteCount++;
				}
			}
			Assert.Equal(1, differingByteCount);
			string nonvolatileMutation = CanonicalizeGeneratedMsBuildEditorConfigBytes(
				singleByteMutation,
				firstProjectRoot,
				firstRepositoryRoot,
				firstAuthorityRoot,
				firstGeneratedRoot,
				firstIntermediateRoot);
			Assert.NotEqual(Sha256Text(firstCanonical), Sha256Text(nonvolatileMutation));
		}
		finally
		{
			Directory.Delete(fixtureRoot, recursive: true);
		}
	}

	private static string BuildCanonicalToolchainFileAuthorityManifest(
		IReadOnlyList<(string RelativePath, string Sha256)> entries)
	{
		Assert.NotEmpty(entries);
		(string RelativePath, string Sha256)[] ordered = entries.ToArray();
		for (int index = 1; index < ordered.Length; index++)
		{
			(string RelativePath, string Sha256) current = ordered[index];
			int insertion = index;
			while (insertion > 0 && StringComparer.Ordinal.Compare(
				ordered[insertion - 1].RelativePath,
				current.RelativePath) > 0)
			{
				ordered[insertion] = ordered[insertion - 1];
				insertion--;
			}
			ordered[insertion] = current;
		}
		string[] rows = new string[ordered.Length];
		for (int index = 0; index < ordered.Length; index++)
		{
			string relativePath = NormalizeAndValidateToolchainRelativePath(ordered[index].RelativePath);
			Assert.Matches("^[0-9a-f]{64}$", ordered[index].Sha256);
			rows[index] = BuildCanonicalAuthorityManifestRow(
				"TOOLCHAIN_FILE_V2",
				[
					index.ToString(System.Globalization.CultureInfo.InvariantCulture),
					relativePath,
					ordered[index].Sha256,
				]);
		}
		string manifest = "TOOLCHAIN_FILE_AUTHORITY_V2\n" + string.Join('\n', rows) + "\n";
		_ = AssertCanonicalToolchainFileAuthorityManifest(manifest);
		return manifest;
	}

	private static string[] AssertCanonicalToolchainFileAuthorityManifest(string manifest)
	{
		Assert.DoesNotContain('\r', manifest);
		Assert.True(manifest.EndsWith('\n'));
		string[] lines = manifest.Split('\n', StringSplitOptions.None);
		Assert.True(lines.Length >= 3);
		Assert.Equal("", lines[^1]);
		Assert.Equal("TOOLCHAIN_FILE_AUTHORITY_V2", lines[0]);
		var identities = new HashSet<string>(StringComparer.Ordinal);
		var rows = new string[lines.Length - 2];
		string? priorIdentity = null;
		for (int lineIndex = 1; lineIndex < lines.Length - 1; lineIndex++)
		{
			string row = lines[lineIndex];
			Assert.False(string.IsNullOrEmpty(row));
			rows[lineIndex - 1] = row;
			string[] fields = ParseCanonicalAuthorityManifestRow(row, "TOOLCHAIN_FILE_V2", 3);
			Assert.Equal(
				(lineIndex - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
				fields[0]);
			string identity = NormalizeAndValidateToolchainRelativePath(fields[1]);
			Assert.Equal(fields[1], identity);
			Assert.True(identities.Add(identity), $"Duplicate toolchain file identity: {identity}");
			if (priorIdentity is not null)
			{
				Assert.True(
					StringComparer.Ordinal.Compare(priorIdentity, identity) < 0,
					$"Toolchain file identity is out of order: {identity}");
			}
			priorIdentity = identity;
			Assert.Matches("^[0-9a-f]{64}$", fields[2]);
		}
		return rows;
	}

	private static string GetCanonicalToolchainRelativePath(string dotnetRoot, string path)
	{
		string canonicalRoot = Path.GetFullPath(dotnetRoot);
		string fullPath = Path.GetFullPath(path);
		Assert.True(IsPathWithin(fullPath, canonicalRoot), $"Toolchain file is outside the pinned root: {fullPath}");
		return NormalizeAndValidateToolchainRelativePath(Path.GetRelativePath(canonicalRoot, fullPath));
	}

	private static string NormalizeAndValidateToolchainRelativePath(string relativePath)
	{
		Assert.False(string.IsNullOrWhiteSpace(relativePath));
		Assert.False(Path.IsPathFullyQualified(relativePath));
		Assert.DoesNotContain('\\', relativePath);
		Assert.DoesNotContain('|', relativePath);
		Assert.DoesNotContain('\r', relativePath);
		Assert.DoesNotContain('\n', relativePath);
		Assert.DoesNotContain('\0', relativePath);
		string normalized = NormalizeRelativePath(relativePath);
		Assert.Equal(relativePath, normalized);
		AssertSafePackageRelativePath(normalized);
		return normalized;
	}

	private static string GetCanonicalToolchainFileAuthorityPrefix(string combinedManifest)
	{
		const string PackageBoundary = "PACKAGE_TRANSPORT_AUTHORITY_V1\n";
		int boundary = combinedManifest.IndexOf(PackageBoundary, StringComparison.Ordinal);
		Assert.True(boundary > 0, "The package-transport authority boundary is absent.");
		Assert.Equal(boundary, combinedManifest.LastIndexOf(PackageBoundary, StringComparison.Ordinal));
		string toolchainFileManifest = combinedManifest[..boundary];
		_ = AssertCanonicalToolchainFileAuthorityManifest(toolchainFileManifest);
		return toolchainFileManifest;
	}

	private static string CreateToolchainFileManifestWithMutatedFirstSha256(string manifest)
	{
		_ = AssertCanonicalToolchainFileAuthorityManifest(manifest);
		string[] lines = manifest.Split('\n', StringSplitOptions.None);
		string[] fields = ParseCanonicalAuthorityManifestRow(lines[1], "TOOLCHAIN_FILE_V2", 3);
		fields[2] = fields[2][0] == '0'
			? "1" + fields[2][1..]
			: "0" + fields[2][1..];
		lines[1] = BuildCanonicalAuthorityManifestRow("TOOLCHAIN_FILE_V2", fields);
		string mutated = string.Join('\n', lines);
		_ = AssertCanonicalToolchainFileAuthorityManifest(mutated);
		return mutated;
	}

	private static string CreateCombinedToolchainAuthorityWithMutatedFirstFileSha256(string manifest)
	{
		string fileManifest = GetCanonicalToolchainFileAuthorityPrefix(manifest);
		return CreateToolchainFileManifestWithMutatedFirstSha256(fileManifest) + manifest[fileManifest.Length..];
	}

	private static void AssertToolchainFileAuthorityManifestRejected(string manifest)
	{
		bool rejected = false;
		try
		{
			_ = AssertCanonicalToolchainFileAuthorityManifest(manifest);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "The malformed toolchain-file authority manifest was accepted.");
	}

	private static void AssertToolchainRelativeIdentityRejected(string relativePath)
	{
		bool rejected = false;
		try
		{
			_ = NormalizeAndValidateToolchainRelativePath(relativePath);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "The unsafe toolchain-file identity was accepted.");
	}

	private static string GetLoadedRuntimeDirectory()
	{
		string? runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
		Assert.False(string.IsNullOrWhiteSpace(runtimeDirectory));
		return Path.GetFullPath(runtimeDirectory);
	}

	private static string GetPinnedHostFxrFileName()
	{
		if (OperatingSystem.IsWindows())
		{
			return "hostfxr.dll";
		}
		if (OperatingSystem.IsMacOS())
		{
			return "libhostfxr.dylib";
		}
		if (OperatingSystem.IsLinux())
		{
			return "libhostfxr.so";
		}
		throw new Xunit.Sdk.XunitException("Unsupported pinned hostfxr platform.");
	}

	private static string GetPinnedHostPolicyFileName()
	{
		if (OperatingSystem.IsWindows())
		{
			return "hostpolicy.dll";
		}
		if (OperatingSystem.IsMacOS())
		{
			return "libhostpolicy.dylib";
		}
		if (OperatingSystem.IsLinux())
		{
			return "libhostpolicy.so";
		}
		throw new Xunit.Sdk.XunitException("Unsupported pinned hostpolicy platform.");
	}

	private static void AssertExactDotnetVersionDirectories(
		string dotnetRoot,
		string relativeParent,
		string expectedVersion)
	{
		string canonicalRoot = Path.GetFullPath(dotnetRoot);
		string versionParent = Path.GetFullPath(Path.Combine(canonicalRoot, relativeParent));
		Assert.True(IsPathWithin(versionParent, canonicalRoot));
		AssertRegularAuthorityDirectory(versionParent, $"pinned {relativeParent} version parent");
		string[] directories = Directory.EnumerateDirectories(
			versionParent,
			"*",
			SearchOption.TopDirectoryOnly).ToArray();
		var versions = new string[directories.Length];
		for (int index = 0; index < directories.Length; index++)
		{
			AssertRegularAuthorityDirectory(directories[index], $"pinned {relativeParent} version directory");
			versions[index] = Path.GetFileName(Path.TrimEndingDirectorySeparator(directories[index]));
		}
		Array.Sort(versions, StringComparer.Ordinal);
		Assert.Equal([expectedVersion], versions);
	}

	private static void AssertApprovedDotnetHostRejected(
		string candidate,
		string dotnetRoot,
		string loadedRuntimeDirectory)
	{
		bool rejected = false;
		try
		{
			AssertApprovedDotnetHost(candidate, dotnetRoot, loadedRuntimeDirectory);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "The mismatched pinned .NET host authority was accepted.");
	}

	private static void AssertExtraDotnetVersionDirectoryRejected(
		string candidate,
		string dotnetRoot,
		string loadedRuntimeDirectory,
		string relativeParent,
		string extraVersion)
	{
		string extraDirectory = Path.GetFullPath(Path.Combine(dotnetRoot, relativeParent, extraVersion));
		Assert.True(IsPathWithin(extraDirectory, dotnetRoot));
		Directory.CreateDirectory(extraDirectory);
		try
		{
			AssertApprovedDotnetHostRejected(candidate, dotnetRoot, loadedRuntimeDirectory);
		}
		finally
		{
			Directory.Delete(extraDirectory);
		}
	}

	private static string CanonicalizeGeneratedMsBuildEditorConfig(
		string source,
		string projectRoot,
		string repositoryRoot,
		string authorityRoot,
		string generatedRoot,
		string intermediateRoot)
	{
		string canonicalRepositoryRoot = Path.GetFullPath(repositoryRoot);
		string canonicalProjectRoot = Path.GetFullPath(projectRoot);
		Assert.True(
			StringComparer.Ordinal.Equals(
				Path.GetFullPath(Path.Combine(canonicalRepositoryRoot, "WalletWasabi")),
				canonicalProjectRoot),
			"The generated MSBuild editor-config project root is invalid.");
		AssertNoReservedGeneratedEditorConfigTokens(source);
		Assert.False(
			source.Contains('\r'),
			"The generated MSBuild editor-config must use LF line endings.");
		Assert.True(
			source.EndsWith('\n'),
			"The generated MSBuild editor-config must have one terminal LF.");
		Assert.True(
			source.Length == 1 || source[^2] != '\n',
			"The generated MSBuild editor-config must have exactly one terminal LF.");
		string[] lines = source.Split('\n', StringSplitOptions.None);
		Assert.True(lines.Length >= 2);
		Assert.Equal("", lines[^1]);
		const string ProjectDirectoryPrefix = "build_property.ProjectDir = ";
		string expectedProjectDirectoryLine = ProjectDirectoryPrefix +
			canonicalProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
		int projectDirectoryIndex = -1;
		int projectDirectoryCount = 0;
		for (int index = 0; index < lines.Length - 1; index++)
		{
			if (!lines[index].StartsWith(ProjectDirectoryPrefix, StringComparison.Ordinal))
			{
				continue;
			}
			projectDirectoryCount++;
			projectDirectoryIndex = index;
			Assert.True(
				StringComparer.Ordinal.Equals(expectedProjectDirectoryLine, lines[index]),
				"The generated MSBuild editor-config ProjectDir line is invalid.");
		}
		Assert.Equal(1, projectDirectoryCount);
		Assert.True(projectDirectoryIndex >= 0);
		lines[projectDirectoryIndex] = "build_property.ProjectDir = {REPO}/WalletWasabi/";
		string canonical = string.Join('\n', lines);
		foreach (string root in new[]
		{
			canonicalRepositoryRoot,
			canonicalProjectRoot,
			Path.GetFullPath(authorityRoot),
			Path.GetFullPath(generatedRoot),
			Path.GetFullPath(intermediateRoot),
		})
		{
			string normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
			Assert.False(string.IsNullOrEmpty(normalizedRoot));
			Assert.False(
				canonical.Contains(normalizedRoot, StringComparison.Ordinal),
				"The canonical generated MSBuild editor-config retains a physical root.");
		}
		Assert.True(
			canonical.Contains(
				"build_property.ProjectDir = {REPO}/WalletWasabi/\n",
				StringComparison.Ordinal),
			"The canonical generated MSBuild editor-config ProjectDir line is absent.");
		return canonical;
	}

	private static void AssertNoReservedGeneratedEditorConfigTokens(string source)
	{
		foreach (string token in new[]
		{
			"{REPO}", "{DOTNET}", "{AUTHORITY}", "{NUGET}", "{GENERATED}", "{INTERMEDIATE}",
			"{TASK}", "{FILE_VERSION}", "{INFORMATIONAL_VERSION}", "{ASSEMBLY_VERSION}",
			"{COMMIT_HASH}", "{PROJECT_DIR}", "{HOME}", "{NUGET_IMPORT}", "{NUGET_PRIMARY}",
			"{ABSENT_PINNED_NIX_CONTENT_HASH}", "{VALIDATED_CONFIG_FILE_TOPOLOGY}",
			"{VALIDATED_PACKAGE_CONTENT_AUTHORITY}", "{VALIDATED_PACKAGE_ROOT_TOPOLOGY}",
			"{VALIDATED_PACKAGE_TRANSPORT_PROFILE}", "{VALIDATED_PINNED_NIX_PROJECT_VERSION}",
			"{VALIDATED_RESTORE_AUDIT_PROFILE}", "{VALIDATED_RESTORE_SOURCE}",
		})
		{
			AssertNoCanonicalAuthorityToken(source, token);
		}
	}

	private static void AssertGeneratedMsBuildEditorConfigRejected(
		string source,
		string projectRoot,
		string repositoryRoot,
		string authorityRoot,
		string generatedRoot,
		string intermediateRoot)
	{
		bool rejected = false;
		try
		{
			_ = CanonicalizeGeneratedMsBuildEditorConfig(
				source,
				projectRoot,
				repositoryRoot,
				authorityRoot,
				generatedRoot,
				intermediateRoot);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "The malformed generated MSBuild editor-config authority was accepted.");
	}

	private static string GetExpectedCompilerInputAuthoritySha256(bool debug)
	{
		if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
		{
			return debug
				? ExpectedDebugCompilerInputAuthoritySha256.MacOsArm64
				: ExpectedReleaseCompilerInputAuthoritySha256.MacOsArm64;
		}
		if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
		{
			return debug
				? ExpectedDebugCompilerInputAuthoritySha256.LinuxX64
				: ExpectedReleaseCompilerInputAuthoritySha256.LinuxX64;
		}
		throw new Xunit.Sdk.XunitException(
			$"Unsupported compiler input authority platform: {RuntimeInformation.OSDescription}/{RuntimeInformation.OSArchitecture}");
	}

	private const string PinnedDotnetSdkVersion = "10.0.100";
	private const string PinnedDotnetHostFxrVersion = "10.0.0";
	private const string PinnedDotnetRuntimeVersion = "10.0.0";

	private static string CanonicalizeGeneratedMsBuildEditorConfigBytes(
		ReadOnlySpan<byte> source,
		string projectRoot,
		string repositoryRoot,
		string authorityRoot,
		string generatedRoot,
		string intermediateRoot)
	{
		Assert.False(
			source.Length >= 3 && source[0] == 0xef && source[1] == 0xbb && source[2] == 0xbf,
			"The generated MSBuild editor-config must not have a UTF-8 BOM.");
		Assert.False(
			source.Length >= 2 && source[0] == 0xff && source[1] == 0xfe,
			"The generated MSBuild editor-config must not have a UTF-16 little-endian BOM.");
		Assert.False(
			source.Length >= 2 && source[0] == 0xfe && source[1] == 0xff,
			"The generated MSBuild editor-config must not have a UTF-16 big-endian BOM.");
		var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
		string decoded;
		try
		{
			decoded = strictUtf8.GetString(source);
		}
		catch (DecoderFallbackException)
		{
			throw new Xunit.Sdk.XunitException(
				"The generated MSBuild editor-config is not strict BOM-less UTF-8.");
		}
		byte[] reencoded = strictUtf8.GetBytes(decoded);
		Assert.True(
			source.SequenceEqual(reencoded),
			"The generated MSBuild editor-config failed the UTF-8 round-trip check.");
		return CanonicalizeGeneratedMsBuildEditorConfig(
			decoded,
			projectRoot,
			repositoryRoot,
			authorityRoot,
			generatedRoot,
			intermediateRoot);
	}

	private static void AssertGeneratedMsBuildEditorConfigBytesRejected(
		byte[] source,
		string expectedFailure,
		string projectRoot,
		string repositoryRoot,
		string authorityRoot,
		string generatedRoot,
		string intermediateRoot)
	{
		Xunit.Sdk.XunitException? rejection = null;
		try
		{
			_ = CanonicalizeGeneratedMsBuildEditorConfigBytes(
				source,
				projectRoot,
				repositoryRoot,
				authorityRoot,
				generatedRoot,
				intermediateRoot);
		}
		catch (Xunit.Sdk.XunitException exception)
		{
			rejection = exception;
		}
		Assert.NotNull(rejection);
		Assert.Contains(expectedFailure, rejection.Message, StringComparison.Ordinal);
	}

	private static string BuildCanonicalAnalyzerAuthorityManifest(
		IEnumerable<(string Identity, string ContentSha256, string Provenance)> analyzers)
	{
		var entries = new List<string[]>();
		foreach ((string identity, string contentSha256, string provenance) in analyzers)
		{
			entries.Add([identity, contentSha256, provenance]);
		}
		Assert.NotEmpty(entries);
		for (int index = 1; index < entries.Count; index++)
		{
			string[] current = entries[index];
			string currentKey = JsonSerializer.Serialize(current);
			int insertion = index;
			while (insertion > 0 && StringComparer.Ordinal.Compare(
				JsonSerializer.Serialize(entries[insertion - 1]),
				currentKey) > 0)
			{
				entries[insertion] = entries[insertion - 1];
				insertion--;
			}
			entries[insertion] = current;
		}

		var manifest = new StringBuilder("ANALYZER_AUTHORITY_V2\n");
		for (int index = 0; index < entries.Count; index++)
		{
			manifest.Append(BuildCanonicalAuthorityManifestRow(
				"ANALYZER_V2",
				[
					index.ToString(System.Globalization.CultureInfo.InvariantCulture),
					entries[index][0],
					entries[index][1],
					entries[index][2],
				]));
			manifest.Append('\n');
		}
		string result = manifest.ToString();
		_ = AssertCanonicalAnalyzerAuthorityManifest(result);
		return result;
	}

	private static string[] AssertCanonicalAnalyzerAuthorityManifest(string manifest)
	{
		if (manifest.Contains('\r') || !manifest.EndsWith('\n'))
		{
			throw new Xunit.Sdk.XunitException(
				$"Analyzer authority framing is not canonical. MANIFEST_SHA256|{Sha256Text(manifest)}");
		}
		string[] lines = manifest.Split('\n', StringSplitOptions.None);
		if (lines.Length < 3 || lines[^1] != "")
		{
			throw new Xunit.Sdk.XunitException(
				$"Analyzer authority terminal structure is not canonical. MANIFEST_SHA256|{Sha256Text(manifest)}");
		}
		if (!StringComparer.Ordinal.Equals(lines[0], "ANALYZER_AUTHORITY_V2"))
		{
			throw new Xunit.Sdk.XunitException(
				$"Analyzer authority header is not canonical. HEADER_SHA256|{Sha256Text(lines[0])}");
		}

		var rows = new string[lines.Length - 2];
		var identities = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string? priorEntry = null;
		for (int lineIndex = 1; lineIndex < lines.Length - 1; lineIndex++)
		{
			string row = lines[lineIndex];
			try
			{
				Assert.False(string.IsNullOrEmpty(row));
				string[] fields = ParseCanonicalAuthorityManifestRow(row, "ANALYZER_V2", 4);
				Assert.Equal(
					(lineIndex - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
					fields[0]);
				AssertCanonicalAnalyzerAuthorityPath(fields[1], allowPackage: true);
				Assert.Matches("^[0-9a-f]{64}$", fields[2]);
				AssertCanonicalAnalyzerAuthorityPath(fields[3], allowPackage: false);
				Assert.True(identities.Add(fields[1]));
				Assert.True(aliases.Add(fields[1]));
				string entry = JsonSerializer.Serialize(fields[1..]);
				Assert.True(priorEntry is null || StringComparer.Ordinal.Compare(priorEntry, entry) < 0);
				priorEntry = entry;
				rows[lineIndex - 1] = row;
			}
			catch (Exception)
			{
				throw new Xunit.Sdk.XunitException(
					$"Analyzer authority row {lineIndex - 1:D3} is not canonical. ROW_SHA256|{Sha256Text(row)}");
			}
		}
		return rows;
	}

	private static void AssertCanonicalAnalyzerAuthorityPath(string value, bool allowPackage)
	{
		Assert.True(value.StartsWith("DOTNET|", StringComparison.Ordinal) ||
			(allowPackage && value.StartsWith("NUGET|", StringComparison.Ordinal)));
		int delimiter = value.IndexOf('|');
		Assert.True(delimiter > 0 && delimiter < value.Length - 1);
		Assert.DoesNotContain(value, char.IsControl);
		AssertSafePackageRelativePath(value[(delimiter + 1)..]);
	}

	private static void AssertExactAnalyzerAuthoritySha256(string expectedSha256, string manifest)
	{
		string[] rows = AssertCanonicalAnalyzerAuthorityManifest(manifest);
		string actualSha256 = Sha256Text(manifest);
		if (StringComparer.Ordinal.Equals(expectedSha256, actualSha256))
		{
			return;
		}

		var diagnostics = new StringBuilder(actualSha256);
		diagnostics.Append("\nCOUNT|").Append(rows.Length);
		var entries = new string[rows.Length];
		for (int index = 0; index < rows.Length; index++)
		{
			string[] fields = ParseCanonicalAuthorityManifestRow(rows[index], "ANALYZER_V2", 4);
			diagnostics.Append("\nROW|").Append(index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
			diagnostics.Append("|ROW_SHA256|").Append(Sha256Text(rows[index]));
			diagnostics.Append("|PATH_SHA256|").Append(Sha256Text(fields[1]));
			diagnostics.Append("|CONTENT_SHA256|").Append(fields[2]);
			diagnostics.Append("|PROVENANCE_SHA256|").Append(Sha256Text(fields[3]));
			entries[index] = JsonSerializer.Serialize(fields[1..]);
		}
		Array.Sort(entries, StringComparer.Ordinal);
		diagnostics.Append("\nSORTED_ENTRIES_SHA256|")
			.Append(Sha256Text(string.Join('\n', entries) + "\n"));
		throw new Xunit.Sdk.XunitException(diagnostics.ToString());
	}

	private static string GetExpectedAnalyzerAuthoritySha256(
		bool isMacOs,
		bool isLinux,
		Architecture architecture)
	{
		if (isMacOs && !isLinux && architecture == Architecture.Arm64)
		{
			return "PENDING-MACOS-ARM64-ANALYZER-AUTHORITY-V2";
		}
		if (isLinux && !isMacOs && architecture == Architecture.X64)
		{
			return "4939c19b5cd53069db3edcd5e6d5c42544e22418e8dadf46b07df1938f325291";
		}
		throw new Xunit.Sdk.XunitException(
			$"Unsupported analyzer authority platform flags/architecture: {isMacOs}/{isLinux}/{architecture}");
	}

	private static void AssertAnalyzerAuthorityPlatformRejected(
		bool isMacOs,
		bool isLinux,
		Architecture architecture)
	{
		Xunit.Sdk.XunitException? rejection = null;
		try
		{
			_ = GetExpectedAnalyzerAuthoritySha256(isMacOs, isLinux, architecture);
		}
		catch (Xunit.Sdk.XunitException exception)
		{
			rejection = exception;
		}
		Assert.NotNull(rejection);
	}

	private static void AssertAnalyzerAuthorityBuildRejected(
		IEnumerable<(string Identity, string ContentSha256, string Provenance)> analyzers)
	{
		Xunit.Sdk.XunitException? rejection = null;
		try
		{
			_ = BuildCanonicalAnalyzerAuthorityManifest(analyzers);
		}
		catch (Xunit.Sdk.XunitException exception)
		{
			rejection = exception;
		}
		Assert.NotNull(rejection);
	}

	private static void AssertAnalyzerAuthorityManifestRejected(string manifest)
	{
		Xunit.Sdk.XunitException? rejection = null;
		try
		{
			_ = AssertCanonicalAnalyzerAuthorityManifest(manifest);
		}
		catch (Xunit.Sdk.XunitException exception)
		{
			rejection = exception;
		}
		Assert.NotNull(rejection);
	}

	private static void AssertExactAnalyzerAuthorityRejected(string expectedSha256, string manifest)
	{
		_ = GetExactAnalyzerAuthorityRejection(expectedSha256, manifest);
	}

	private static Xunit.Sdk.XunitException GetExactAnalyzerAuthorityRejection(
		string expectedSha256,
		string manifest)
	{
		Xunit.Sdk.XunitException? rejection = null;
		try
		{
			AssertExactAnalyzerAuthoritySha256(expectedSha256, manifest);
		}
		catch (Xunit.Sdk.XunitException exception)
		{
			rejection = exception;
		}
		Assert.NotNull(rejection);
		return rejection;
	}

	[Fact]
	public void GenerationFencedRawTransactionsCreateCanonicalClosedFundingRows()
	{
		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		string firstCandidateId = fixture.FirstSelected.OutPoint.TransactionId.CanonicalRpcHex;
		string secondCandidateId = fixture.SecondSelected.OutPoint.TransactionId.CanonicalRpcHex;
		string firstPreviousId = Tx(3).CanonicalRpcHex;
		string secondPreviousId = Tx(4).CanonicalRpcHex;
		string thirdPreviousId = Tx(5).CanonicalRpcHex;
		byte[] firstCandidate = [0xa1];
		byte[] secondCandidate = [0xa2];
		byte[] firstPrevious = [0x30];
		byte[] secondPrevious = [0x10];
		byte[] thirdPrevious = [0x20];
		ElementsExpectationBoundRawTransactionBatch rawTransactions = CreateRawTransactionBatch(
			(secondCandidateId, secondCandidate),
			(secondPreviousId, secondPrevious),
			(firstCandidateId, firstCandidate),
			(thirdPreviousId, thirdPrevious),
			(firstPreviousId, firstPrevious));
		IReadOnlyList<string>?[] previousIdsBySelectedInput =
		[
			new[] { firstPreviousId, secondPreviousId },
			new[] { secondPreviousId, thirdPreviousId },
		];

		bool succeeded = rawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
			fixture.Plan,
			previousIdsBySelectedInput,
			out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		Assert.NotNull(fundingBatch);
		Assert.False(rawTransactions.HasTransactionIdValidation);
		Assert.False(rawTransactions.HasBlockMembershipAuthority);
		Assert.False(rawTransactions.HasCurrentnessAuthority);
		using (fundingBatch)
		using (LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(
			fixture.Plan,
			fundingBatch,
			SourceEpoch))
		{
			byte[] encoded = Copy(frame);
			try
			{
				int cursor = 152;
				AssertSelectedRow(
					encoded,
					ref cursor,
					fixture.FirstSelected,
					firstCandidate,
					[secondPrevious, firstPrevious]);
				AssertSelectedRow(
					encoded,
					ref cursor,
					fixture.SecondSelected,
					secondCandidate,
					[secondPrevious, thirdPrevious]);
				AssertDestination(encoded, ref cursor, fixture.FirstDestination);
				AssertDestination(encoded, ref cursor, fixture.SecondDestination);
				Assert.Equal(encoded.Length, cursor);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(encoded);
			}
		}

		LiquidOrdinaryWalletExactSpendPlan confirmedPlan = CreateConfirmedSingleTransactionPlan();
		LiquidWalletCoinControlEntry confirmedEntry = confirmedPlan.GetSelectedEntries()[0];
		ElementsExpectationBoundRawTransactionBatch confirmedRawTransactions =
			CreateRawTransactionBatchFromRequests(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				(new ElementsRawTransactionRequest(
					confirmedEntry.OutPoint.TransactionId.CanonicalRpcHex,
					confirmedEntry.Confirmation!.CanonicalBlockHash),
				[0xee]));
		bool confirmedSucceeded = confirmedRawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
			confirmedPlan,
			[Array.Empty<string>()],
			out LiquidOrdinaryWalletPlanFundingBatch? confirmedFundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode confirmedErrorCode);
		try
		{
			Assert.True(confirmedSucceeded, FailureMessage(confirmedErrorCode));
			Assert.NotNull(confirmedFundingBatch);
		}
		finally
		{
			confirmedFundingBatch?.Dispose();
		}
	}

	[Fact]
	public void GenerationFencedFundingCompositionRejectsEveryOpenOrAmbiguousMapping()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			71);
		string candidateId = Tx(71).CanonicalRpcHex;
		string firstPreviousId = Tx(0xab).CanonicalRpcHex;
		string secondPreviousId = Tx(0xac).CanonicalRpcHex;
		string missingPreviousId = Tx(0xad).CanonicalRpcHex;
		ElementsExpectationBoundRawTransactionBatch exact = CreateRawTransactionBatch(
			(candidateId, [0xaa]),
			(firstPreviousId, [0xbb]));

		AssertFundingCompositionRejected(
			exact,
			null,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			null,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			new IReadOnlyList<string>?[] { null },
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((firstPreviousId, [0xbb])),
			plan,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xcc])),
			plan,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[new[] { firstPreviousId.ToUpperInvariant() }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[new[] { new string('0', 64) }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xcc])),
			plan,
			[new[] { secondPreviousId, firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[new[] { firstPreviousId, firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((candidateId, [0xaa])),
			plan,
			[new[] { candidateId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((candidateId, [0xaa])),
			plan,
			[new[] { missingPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xbb])),
			plan,
			[new[] { firstPreviousId, secondPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding);

		LiquidOrdinaryWalletExactSpendPlan sharedCandidatePlan = CreateSameTransactionPlan();
		string sharedCandidateId = sharedCandidatePlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(sharedCandidateId, [0xdd]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xcc])),
			sharedCandidatePlan,
			[new[] { firstPreviousId }, new[] { secondPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		LiquidOrdinaryWalletExactSpendPlan confirmedPlan = CreateConfirmedSingleTransactionPlan();
		string confirmedCandidateId = confirmedPlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((confirmedCandidateId, [0xee])),
			confirmedPlan,
			[Array.Empty<string>()],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		LiquidOrdinaryWalletExactSpendPlan futureConfirmationPlan =
			CreateConfirmedSingleTransactionPlan(new string('d', 64), 2);
		string futureCandidateId = futureConfirmationPlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatchFromRequests(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				(new ElementsRawTransactionRequest(futureCandidateId, new string('d', 64)), [0xef])),
			futureConfirmationPlan,
			[Array.Empty<string>()],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		LiquidOrdinaryWalletExactSpendPlan wrongTipPlan =
			CreateConfirmedSingleTransactionPlan(new string('d', 64), 1);
		string wrongTipCandidateId = wrongTipPlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatchFromRequests(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				(new ElementsRawTransactionRequest(wrongTipCandidateId, new string('d', 64)), [0xf0])),
			wrongTipPlan,
			[Array.Empty<string>()],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatchWithEffectiveFeeAsset(
				IssuedAssetHex,
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb])),
			plan,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		byte[] maximumPayload = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength];
		try
		{
			var expandedTransactions =
				new (string TransactionId, byte[] Bytes)[10];
			expandedTransactions[0] = (sharedCandidateId, [0xdd]);
			var expandedPreviousIds = new string[9];
			for (int index = 0; index < expandedPreviousIds.Length; index++)
			{
				string previousId = Tx(checked((uint)(200 + index))).CanonicalRpcHex;
				expandedPreviousIds[index] = previousId;
				expandedTransactions[index + 1] = (previousId, maximumPayload);
			}
			AssertFundingCompositionRejected(
				CreateRawTransactionBatch(expandedTransactions),
				sharedCandidatePlan,
				[expandedPreviousIds, expandedPreviousIds.ToArray()],
				LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(maximumPayload);
		}
	}

	private static LiquidOrdinaryWalletExactSpendPlan CreateSameTransactionPlan()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidTransactionId transactionId = Tx(91);
		LiquidOwnedOutput first = Output(transactionId, 0, peggedAsset, peggedAsset, 4);
		LiquidOwnedOutput second = Output(transactionId, 1, peggedAsset, peggedAsset, 6);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], [first, second]));
		LiquidSuppliedConfidentialDestination destination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			9);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[first.OutPoint, second.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
	}

	private static LiquidOrdinaryWalletExactSpendPlan CreateConfirmedSingleTransactionPlan(
		string? blockHash = null,
		uint height = 1)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidTransactionId transactionId = Tx(92);
		LiquidOwnedOutput output = Output(transactionId, 0, peggedAsset, peggedAsset, 10);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], [output]));
		state = state.Confirm(
			state.Revision,
			transactionId,
			LiquidConfirmation.Create(blockHash ?? new string('b', 64), height));
		LiquidSuppliedConfidentialDestination destination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			9);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[output.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
	}

	private static ElementsExpectationBoundRawTransactionBatch CreateRawTransactionBatch(
		params (string TransactionId, byte[] Bytes)[] transactions) =>
		CreateRawTransactionBatchWithEffectiveFeeAsset(
			ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
			transactions);

	private static ElementsExpectationBoundRawTransactionBatch CreateRawTransactionBatchWithEffectiveFeeAsset(
		string effectiveFeeAsset,
		params (string TransactionId, byte[] Bytes)[] transactions)
	{
		var requests = new (ElementsRawTransactionRequest Request, byte[] Bytes)[transactions.Length];
		for (int index = 0; index < transactions.Length; index++)
		{
			(string transactionId, byte[] bytes) = transactions[index];
			requests[index] = (new ElementsRawTransactionRequest(transactionId, null), bytes);
		}
		return CreateRawTransactionBatchFromRequests(effectiveFeeAsset, requests);
	}

	private static ElementsExpectationBoundRawTransactionBatch CreateRawTransactionBatchFromRequests(
		string effectiveFeeAsset,
		params (ElementsRawTransactionRequest Request, byte[] Bytes)[] transactions)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		string genesisBlockHash = new('a', 64);
		string bestBlockHash = new('b', 64);
		string startupId = new('c', 64);
		var expectation = new ElementsNodeExpectation(
			manifest.ChainRpcName,
			genesisBlockHash,
			"51",
			manifest.PeggedAssetId,
			new string('0', 64),
			2,
			false,
			1,
			1,
			"/wire-test:1/");
		var status = new ElementsNodeStatus(
			expectation.Chain,
			1,
			1,
			bestBlockHash,
			expectation.GenesisBlockHash,
			false,
			false,
			false,
			false,
			true,
			true,
			false,
			expectation.FedpegScript,
			expectation.PeggedAsset,
			expectation.ParentGenesisBlockHash,
			expectation.PeginConfirmationDepth,
			expectation.EnforcePak,
			expectation.Version,
			expectation.ProtocolVersion,
			expectation.Subversion);
		var generation = new ElementsNodeGenerationObservation(
			startupId,
			1,
			status.Blocks,
			status.BestBlockHash);
		var nodeObservation = new ElementsExpectationBoundNodeObservation(
			expectation,
			effectiveFeeAsset,
			status,
			generation);
		var observations = new ElementsRawTransactionObservation[transactions.Length];
		for (int index = 0; index < transactions.Length; index++)
		{
			(ElementsRawTransactionRequest request, byte[] bytes) = transactions[index];
			observations[index] = new ElementsRawTransactionObservation(
				request,
				bytes);
		}

		return new ElementsExpectationBoundRawTransactionBatch(nodeObservation, observations);
	}

	private static void AssertFundingCompositionRejected(
		ElementsExpectationBoundRawTransactionBatch rawTransactions,
		LiquidOrdinaryWalletExactSpendPlan? plan,
		IReadOnlyList<IReadOnlyList<string>?>? previousTransactionIdsBySelectedInput,
		LiquidOrdinaryWalletPlanWireErrorCode expectedErrorCode)
	{
		bool succeeded = rawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
			plan,
			previousTransactionIdsBySelectedInput,
			out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		try
		{
			Assert.False(succeeded);
			Assert.Null(fundingBatch);
			Assert.Equal(expectedErrorCode, errorCode);
		}
		finally
		{
			fundingBatch?.Dispose();
		}
	}
}
