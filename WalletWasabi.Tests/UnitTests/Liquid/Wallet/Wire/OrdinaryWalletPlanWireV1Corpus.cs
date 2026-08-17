using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

internal static class OrdinaryWalletPlanWireV1Corpus
{
	public const string CorpusId = "ordinary-wallet-plan-wire-v1-conformance-2";
	public const string ParentRootSha256 = "a1e1db8cba234d5154e947a32539c0ac461ddbaa812a0dd4e7c4e007a9541600";
	public const string NestedRootSha256 = "a4aaa0e0b13b5544fd8e53f703a685fc56f4ec95f1e1c052f19bf50365ce2f6c";
	public const int FileCount = 235;
	public const long AggregateBytes = 602_857;

	private static readonly string[] ExpectedRootEntries =
	[
		"CATALOG_FIXTURES_V1.tsv",
		"CONTEXTS_V1.tsv",
		"CORPUS_ID",
		"CORPUS_ROOT_SHA256",
		"ERROR_MAPPING_V1.tsv",
		"SHA256SUMS",
		"WIRE_FORMAT_V1.md",
		"vectors",
	];

	private static readonly string[] ExpectedVectorEntries =
	[
		"BOUNDARIES_V1.tsv",
		"CASES_V1.tsv",
		"CATALOG_OUTPUT_SCRIPTS_V1.tsv",
		"CORPUS_V1.md",
		"FIXTURES_V1.tsv",
		"FIXTURE_ASSERTIONS_V1.tsv",
		"FRAMES_V1.tsv",
		"FRAME_PAYLOAD_BINDINGS_V1.tsv",
		"MUTATIONS_V1.tsv",
		"PUBLIC_PROOF_CASES_V1.tsv",
		"SHA256SUMS",
		"SOURCE_MODELS_V1.tsv",
		"frames",
		"public",
		"source-models",
	];

	private static readonly string[] ExpectedDescendantDirectories =
	[
		"vectors",
		"vectors/frames",
		"vectors/public",
		"vectors/source-models",
	];

	private static readonly string[] ExpectedParentInventoryKeys =
	[
		"CATALOG_FIXTURES_V1.tsv",
		"CONTEXTS_V1.tsv",
		"CORPUS_ID",
		"ERROR_MAPPING_V1.tsv",
		"WIRE_FORMAT_V1.md",
		"vectors/SHA256SUMS",
	];

	public static string RootPath { get; } = Path.Combine(
		AppContext.BaseDirectory,
		"TestData",
		"Liquid",
		"OrdinaryWalletPlanWireV1",
		"nonlinkable-reference");

	private static string VectorPath => Path.Combine(RootPath, "vectors");

	public static void AssertAuthenticPacket()
	{
		Assert.True(Directory.Exists(RootPath), $"Missing corpus: {RootPath}");
		AssertNoReparsePoint(RootPath);

		CorpusTree tree = ReadTree();
		Assert.Equal(ExpectedRootEntries, EnumerateEntryNames(RootPath));
		Assert.Equal(ExpectedVectorEntries, EnumerateEntryNames(VectorPath));
		Assert.Equal(ExpectedDescendantDirectories, tree.Directories);
		Assert.Equal(FileCount, tree.Files.Count);
		Assert.Equal(
			AggregateBytes,
			tree.Files.Sum(relativePath => new FileInfo(ResolvePath(RootPath, relativePath)).Length));

		Assert.Equal($"{CorpusId}\n", ReadCanonicalText(Path.Combine(RootPath, "CORPUS_ID")));
		Assert.Equal(
			$"{ParentRootSha256}\n",
			ReadCanonicalText(Path.Combine(RootPath, "CORPUS_ROOT_SHA256")));

		string parentInventoryPath = Path.Combine(RootPath, "SHA256SUMS");
		string nestedInventoryPath = Path.Combine(VectorPath, "SHA256SUMS");
		Assert.Equal(ParentRootSha256, LowerHex(SHA256.HashData(File.ReadAllBytes(parentInventoryPath))));
		Assert.Equal(NestedRootSha256, LowerHex(SHA256.HashData(File.ReadAllBytes(nestedInventoryPath))));

		IReadOnlyDictionary<string, string> parent = ReadInventory(parentInventoryPath);
		Assert.Equal(ExpectedParentInventoryKeys, parent.Keys.Order(StringComparer.Ordinal));
		VerifyInventory(RootPath, parent);

		IReadOnlyDictionary<string, string> nested = ReadInventory(nestedInventoryPath);
		string[] actualNestedInventoryKeys = tree.Files
			.Where(relativePath => relativePath.StartsWith("vectors/", StringComparison.Ordinal))
			.Select(relativePath => relativePath["vectors/".Length..])
			.Where(relativePath => !relativePath.Equals("SHA256SUMS", StringComparison.Ordinal))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(actualNestedInventoryKeys, nested.Keys.Order(StringComparer.Ordinal));
		VerifyInventory(VectorPath, nested);

		var expectedLeaves = new HashSet<string>(StringComparer.Ordinal);
		foreach (string relativePath in parent.Keys)
		{
			Assert.True(expectedLeaves.Add(relativePath), $"Duplicate expected leaf: {relativePath}");
		}
		foreach (string relativePath in nested.Keys)
		{
			string rootedAtPacket = $"vectors/{relativePath}";
			Assert.True(expectedLeaves.Add(rootedAtPacket), $"Duplicate expected leaf: {rootedAtPacket}");
		}
		foreach (string relativePath in new[] { "CORPUS_ROOT_SHA256", "SHA256SUMS" })
		{
			Assert.True(expectedLeaves.Add(relativePath), $"Duplicate expected leaf: {relativePath}");
		}

		string[] expectedLeafPaths = expectedLeaves.Order(StringComparer.Ordinal).ToArray();
		Assert.Equal(FileCount, expectedLeafPaths.Length);
		Assert.Equal(expectedLeafPaths, tree.Files);
	}

	public static string ReadCanonicalText(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		Assert.NotEmpty(bytes);
		Assert.Equal((byte)'\n', bytes[^1]);
		Assert.DoesNotContain((byte)'\r', bytes);
		Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
	}

	private static CorpusTree ReadTree()
	{
		var directories = new List<string>();
		var files = new List<string>();
		var pending = new Stack<string>();
		pending.Push(RootPath);

		while (pending.TryPop(out string? directory))
		{
			foreach (string path in Directory.EnumerateFileSystemEntries(directory))
			{
				AssertNoReparsePoint(path);
				string relativePath = NormalizeEnumeratedPath(Path.GetRelativePath(RootPath, path));
				FileAttributes attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.Directory) != 0)
				{
					directories.Add(relativePath);
					pending.Push(path);
				}
				else
				{
					Assert.True(File.Exists(path), $"Corpus leaf is not a regular file: {relativePath}");
					files.Add(relativePath);
				}
			}
		}

		return new CorpusTree(
			directories.Order(StringComparer.Ordinal).ToArray(),
			files.Order(StringComparer.Ordinal).ToArray());
	}

	private static string[] EnumerateEntryNames(string directory) =>
		Directory.EnumerateFileSystemEntries(directory)
			.Select(path => Path.GetFileName(path)!)
			.Order(StringComparer.Ordinal)
			.ToArray();

	private static IReadOnlyDictionary<string, string> ReadInventory(string path)
	{
		return ParseInventory(ReadCanonicalText(path));
	}

	internal static IReadOnlyDictionary<string, string> ParseInventory(string text)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		string? prior = null;
		foreach (string line in text[..^1].Split('\n'))
		{
			Assert.True(
				line.Length > 66 &&
				line[64] == ' ' &&
				line[65] == ' ' &&
				!char.IsWhiteSpace(line[66]),
				"Checksum row is not canonical.");
			string digest = line[..64];
			string relativePath = line[66..];
			Assert.Equal(64, digest.Length);
			Assert.All(digest, value => Assert.True(value is >= '0' and <= '9' or >= 'a' and <= 'f'));
			ValidateInventoryPath(relativePath);
			if (prior is not null)
			{
				Assert.True(
					StringComparer.Ordinal.Compare(relativePath, prior) > 0,
					$"Checksum path is duplicated or out of order: {relativePath}");
			}
			Assert.True(result.TryAdd(relativePath, digest), $"Duplicate checksum path: {relativePath}");
			prior = relativePath;
		}
		return result;
	}

	private static void ValidateInventoryPath(string relativePath)
	{
		Assert.NotEmpty(relativePath);
		Assert.DoesNotContain('\\', relativePath);
		Assert.False(Path.IsPathRooted(relativePath), $"Rooted checksum path: {relativePath}");
		Assert.False(
			relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':',
			$"Drive-qualified checksum path: {relativePath}");
		string[] components = relativePath.Split('/', StringSplitOptions.None);
		Assert.All(
			components,
			component =>
			{
				Assert.NotEmpty(component);
				Assert.NotEqual(".", component);
				Assert.NotEqual("..", component);
				Assert.DoesNotContain('\0', component);
			});
	}

	private static void VerifyInventory(string root, IReadOnlyDictionary<string, string> inventory)
	{
		foreach ((string relativePath, string expectedHash) in inventory)
		{
			string path = ResolvePath(root, relativePath);
			AssertNoReparsePoint(path);
			Assert.True(File.Exists(path), $"Missing inventory file: {relativePath}");
			Assert.Equal(expectedHash, LowerHex(SHA256.HashData(File.ReadAllBytes(path))));
		}
	}

	private static string ResolvePath(string root, string relativePath)
	{
		ValidateInventoryPath(relativePath);
		string canonicalRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
		string path = Path.GetFullPath(
			Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
		Assert.StartsWith(canonicalRoot, path, StringComparison.Ordinal);
		return path;
	}

	private static string NormalizeEnumeratedPath(string relativePath)
	{
		string normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
		ValidateInventoryPath(normalized);
		return normalized;
	}

	private static void AssertNoReparsePoint(string path) =>
		Assert.False(
			(File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0,
			$"Corpus reparse point is forbidden: {path}");

	private static string LowerHex(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(value);

	private sealed record CorpusTree(IReadOnlyList<string> Directories, IReadOnlyList<string> Files);
}
