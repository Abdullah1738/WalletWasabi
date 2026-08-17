using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire;

internal static class WalletFactsWireV1Corpus
{
	public const string CorpusId = "wallet-facts-wire-v1-conformance-1";
	public const string ParentRootSha256 = "9a3d11662670d13e23ed248f2ae145c87a52739e2e3bb03f7628e4d12e147c63";
	public const string NestedRootSha256 = "9bcdcf31ffe90e7a23ada162c61c71cfc84343ba1c190865e0ed34af8c7da933";

	public static string RootPath { get; } = Path.Combine(
		AppContext.BaseDirectory,
		"TestData",
		"Liquid",
		"WalletFactsWireV1",
		"nonlinkable-reference");

	public static string VectorPath => Path.Combine(RootPath, "vectors");

	public static IReadOnlyList<string[]> ReadRows(string relativePath, params string[] expectedHeader)
	{
		string text = ReadCanonicalText(Path.Combine(VectorPath, relativePath));
		string[] lines = text[..^1].Split('\n');
		Assert.NotEmpty(lines);
		Assert.Equal(expectedHeader, lines[0].Split('\t'));

		var rows = new List<string[]>(lines.Length - 1);
		var identifiers = new HashSet<string>(StringComparer.Ordinal);
		foreach (string line in lines.Skip(1))
		{
			Assert.NotEmpty(line);
			string[] fields = line.Split('\t');
			Assert.Equal(expectedHeader.Length, fields.Length);
			Assert.True(identifiers.Add(fields[0]), $"Duplicate identifier: {fields[0]}");
			rows.Add(fields);
		}

		return rows;
	}

	public static IReadOnlyDictionary<string, CorpusFrame> LoadFrames()
	{
		IReadOnlyList<string[]> rows = ReadRows(
			"FRAMES_V1.tsv",
			"frame_id",
			"frame_kind",
			"relative_path",
			"decoded_length",
			"decoded_sha256",
			"parent_frame_id",
			"mutation_kind",
			"mutation_offset",
			"old_hex",
			"new_hex");
		Assert.Equal(86, rows.Count);

		var frames = new Dictionary<string, CorpusFrame>(StringComparer.Ordinal);
		foreach (string[] row in rows)
		{
			Assert.Contains(row[1], new[] { "request", "response" });
			Assert.StartsWith("frames/", row[2], StringComparison.Ordinal);
			Assert.DoesNotContain("..", row[2], StringComparison.Ordinal);
			Assert.DoesNotContain('\\', row[2]);

			string framePath = Path.Combine(VectorPath, row[2].Replace('/', Path.DirectorySeparatorChar));
			string canonicalFramePath = Path.GetFullPath(framePath);
			string canonicalFramesRoot = Path.GetFullPath(Path.Combine(VectorPath, "frames")) + Path.DirectorySeparatorChar;
			Assert.StartsWith(canonicalFramesRoot, canonicalFramePath, StringComparison.Ordinal);

			byte[] text = File.ReadAllBytes(canonicalFramePath);
			Assert.NotEmpty(text);
			Assert.Equal((byte)'\n', text[^1]);
			Assert.DoesNotContain((byte)'\r', text);
			ReadOnlySpan<byte> hex = text.AsSpan(0, text.Length - 1);
			Assert.Equal(0, hex.Length % 2);
			Assert.All(hex.ToArray(), value =>
				Assert.True(value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f'));

			byte[] decoded = Convert.FromHexString(Encoding.ASCII.GetString(hex));
			Assert.Equal(int.Parse(row[3], CultureInfo.InvariantCulture), decoded.Length);
			Assert.Equal(row[4], LowerHex(SHA256.HashData(decoded)));
			Assert.True(frames.TryAdd(row[0], new CorpusFrame(row[0], row[1], row[2], decoded)));
		}

		string[] listed = frames.Values.Select(frame => frame.RelativePath).Order(StringComparer.Ordinal).ToArray();
		string[] actual = Directory.EnumerateFiles(Path.Combine(VectorPath, "frames"), "*", SearchOption.TopDirectoryOnly)
			.Select(path => $"frames/{Path.GetFileName(path)}")
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(listed, actual);
		return frames;
	}

	public static void AssertChecksumPacket()
	{
		Assert.True(Directory.Exists(RootPath), $"Missing corpus: {RootPath}");
		Assert.Equal(
			new[] { "ERROR_MAPPING_V1.tsv", "SHA256SUMS", "WIRE_FORMAT_V1.md", "vectors" },
			Directory.EnumerateFileSystemEntries(RootPath)
				.Select(Path.GetFileName)
				.Order(StringComparer.Ordinal));
		Assert.Equal(
			new[]
			{
				"API_CASES_V1.tsv",
				"BOUNDARIES_V1.tsv",
				"CASES_V1.tsv",
				"CORPUS_V1.md",
				"FRAMES_V1.tsv",
				"RECIPES_V1.tsv",
				"SHA256SUMS",
				"frames",
			},
			Directory.EnumerateFileSystemEntries(VectorPath)
				.Select(Path.GetFileName)
				.Order(StringComparer.Ordinal));
		Assert.Equal(
			new[] { Path.GetFullPath(Path.Combine(VectorPath, "frames")) },
			Directory.EnumerateDirectories(VectorPath, "*", SearchOption.AllDirectories)
				.Select(Path.GetFullPath)
				.Order(StringComparer.Ordinal));

		foreach (string path in Directory.EnumerateFileSystemEntries(RootPath, "*", SearchOption.AllDirectories).Prepend(RootPath))
		{
			Assert.False((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
		}

		string parentInventory = Path.Combine(RootPath, "SHA256SUMS");
		string nestedInventory = Path.Combine(VectorPath, "SHA256SUMS");
		Assert.Equal(ParentRootSha256, LowerHex(SHA256.HashData(File.ReadAllBytes(parentInventory))));
		Assert.Equal(NestedRootSha256, LowerHex(SHA256.HashData(File.ReadAllBytes(nestedInventory))));

		IReadOnlyDictionary<string, string> parent = ReadInventory(parentInventory);
		Assert.Equal(
			new[] { "ERROR_MAPPING_V1.tsv", "WIRE_FORMAT_V1.md", "vectors/SHA256SUMS" },
			parent.Keys.Order(StringComparer.Ordinal));
		VerifyInventory(RootPath, parent);

		IReadOnlyDictionary<string, string> nested = ReadInventory(nestedInventory);
		string[] nestedFiles = Directory.EnumerateFiles(VectorPath, "*", SearchOption.AllDirectories)
			.Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(nestedInventory), StringComparison.Ordinal))
			.Select(path => Path.GetRelativePath(VectorPath, path).Replace(Path.DirectorySeparatorChar, '/'))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(nestedFiles, nested.Keys.Order(StringComparer.Ordinal));
		VerifyInventory(VectorPath, nested);

		string corpus = ReadCanonicalText(Path.Combine(VectorPath, "CORPUS_V1.md"));
		Assert.Contains($"Corpus ID: {CorpusId}\n", corpus, StringComparison.Ordinal);
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

	private static IReadOnlyDictionary<string, string> ReadInventory(string path)
	{
		string text = ReadCanonicalText(path);
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string line in text[..^1].Split('\n'))
		{
			Assert.Equal(66, line.IndexOf("  ", StringComparison.Ordinal) + 2);
			string digest = line[..64];
			string relativePath = line[66..];
			Assert.Equal(64, digest.Length);
			Assert.All(digest, value => Assert.True(value is >= '0' and <= '9' or >= 'a' and <= 'f'));
			Assert.NotEmpty(relativePath);
			Assert.DoesNotContain('\\', relativePath);
			Assert.DoesNotContain("..", relativePath, StringComparison.Ordinal);
			Assert.False(Path.IsPathRooted(relativePath));
			Assert.True(result.TryAdd(relativePath, digest), $"Duplicate checksum path: {relativePath}");
		}
		return result;
	}

	private static void VerifyInventory(string root, IReadOnlyDictionary<string, string> inventory)
	{
		string canonicalRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
		foreach ((string relativePath, string expectedHash) in inventory)
		{
			string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
			Assert.StartsWith(canonicalRoot, path, StringComparison.Ordinal);
			Assert.True(File.Exists(path), $"Missing inventory file: {relativePath}");
			Assert.Equal(expectedHash, LowerHex(SHA256.HashData(File.ReadAllBytes(path))));
		}
	}

	private static string LowerHex(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(value);
}

internal sealed record CorpusFrame(string Id, string Kind, string RelativePath, byte[] Bytes);
