using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Tests.Helpers;
using Xunit;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;
using LiquidOrdinaryWalletPlanWireErrorCode = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireErrorCode;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

/// <summary>
/// PRE-FUNDING-DEPENDENCY-ROWS-001 acceptance tests. Coverage is split at the
/// landed ownership boundary: the load-layer tests prove corrupt persisted
/// replay never becomes a <see cref="LiquidWalletState"/> (the fresh child
/// calls <see cref="LiquidWalletLoadSave.Load"/> exactly once and never calls
/// Derive); the deriver tests prove behavior only after a lawful fresh-child
/// load (the child asserts the loaded state is non-null before Derive). The
/// fresh child is a clean OS process compiled from typed test-owned source
/// against the landed internal surface; no reflection bypass, unsafe
/// construction, post-load corruption seam, or test-only production hook is
/// used. The child protocol is deterministic and privacy-safe: exit code plus
/// fixed tokens/counts and fixture ids only.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletFundingDependencyDeriverTests
{
	private const int EnvelopeHeaderLength = 48;
	private const int FrameHeaderLength = 16;
	private const int CanonicalStartOffset = 12;
	private const string PublicKeyHex =
		"0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string ZeroTransactionIdHex =
		"0000000000000000000000000000000000000000000000000000000000000000";

	// ---------------------------------------------------------------------
	// Fresh-child runner: a typed test-owned console program compiled once
	// per test process with the WalletWasabi.Tests assembly identity (the
	// landed InternalsVisibleTo grant), executed via `dotnet exec` with the
	// test run's own runtime configuration. Input travels over stdin; the
	// result is a single JSON object of fixed tokens, counts, and fixture
	// ids on stdout.
	// ---------------------------------------------------------------------

	private const string ChildProgramSource = """
		using System;
		using System.Collections.Generic;
		using System.IO;
		using System.Text;
		using System.Text.Json;
		using WalletWasabi.Liquid.Wallet;
		using WalletWasabi.Liquid.Wallet.Sync;

		internal static class Program
		{
			private static int Main()
			{
				string inputJson = Console.In.ReadToEnd();
				using JsonDocument inputDocument = JsonDocument.Parse(inputJson);
				JsonElement input = inputDocument.RootElement;
				string mode = input.GetProperty("mode").GetString() ?? "";
				string walletDataDir = input.GetProperty("walletDataDir").GetString() ?? "";
				string walletName = input.GetProperty("walletName").GetString() ?? "";
				byte[] key = Convert.FromHexString(input.GetProperty("keyHex").GetString() ?? "");
				byte[] context = Convert.FromHexString(input.GetProperty("contextHex").GetString() ?? "");

				using var outputStream = new MemoryStream();
				using (var writer = new Utf8JsonWriter(outputStream))
				{
					writer.WriteStartObject();
					if (mode == "load-corruption")
					{
						WriteLoadCorruptionResult(writer, walletDataDir, walletName, key, context);
					}
					else if (mode == "derive")
					{
						WriteDeriveResult(writer, input, walletDataDir, walletName, key, context);
					}
					else
					{
						writer.WriteString("outcome", "UNKNOWN_MODE");
					}

					writer.WriteEndObject();
				}

				Console.Out.Write(Encoding.UTF8.GetString(outputStream.ToArray()));
				Console.Out.Flush();
				return 0;
			}

			private static void WriteLoadCorruptionResult(
				Utf8JsonWriter writer,
				string walletDataDir,
				string walletName,
				byte[] key,
				byte[] context)
			{
				// Exactly one Load call and never a Derive call: the decisive
				// assertion for a corruption vector is the load-layer rejection.
				string outcome;
				try
				{
					LiquidWalletLoadSave.Load(walletDataDir, walletName, key, context);
					outcome = "LOADED";
				}
				catch (LiquidWalletReplayProtectionException)
				{
					outcome = "REPLAY_PROTECTION";
				}
				catch (LiquidWalletPersistenceFormatException)
				{
					outcome = "PERSISTENCE_FORMAT";
				}
				catch (InvalidOperationException)
				{
					outcome = "INVALID_OPERATION";
				}
				catch (ArgumentException)
				{
					outcome = "ARGUMENT";
				}
				catch (Exception)
				{
					outcome = "OTHER";
				}

				writer.WriteString("outcome", outcome);
			}

			private static void WriteDeriveResult(
				Utf8JsonWriter writer,
				JsonElement input,
				string walletDataDir,
				string walletName,
				byte[] key,
				byte[] context)
			{
				LiquidWalletLoadSaveResult load;
				try
				{
					load = LiquidWalletLoadSave.Load(walletDataDir, walletName, key, context);
				}
				catch (Exception)
				{
					writer.WriteString("outcome", "LOAD_FAILED");
					return;
				}

				// The mandatory load-result nullability contract: assert the
				// loaded state is non-null, assign it, and only then derive.
				if (load.State is not LiquidWalletState state)
				{
					writer.WriteString("outcome", "LOAD_STATE_NULL");
					return;
				}

				writer.WriteNumber("revision", state.Revision);

				writer.WriteStartArray("probeOutcomes");
				if (input.TryGetProperty("probes", out JsonElement probes))
				{
					foreach (JsonElement probe in probes.EnumerateArray())
					{
						ulong probeRevision = probe.GetProperty("revision").GetUInt64();
						List<string> probeSelected = ReadSelected(probe);
						writer.WriteStringValue(RunProbe(state, probeSelected, probeRevision));
					}
				}

				writer.WriteEndArray();

				if (!input.TryGetProperty("main", out JsonElement main))
				{
					writer.WriteString("outcome", "OK");
					return;
				}

				ulong mainRevision = main.GetProperty("revision").GetUInt64();
				List<string> mainSelected = ReadSelected(main);
				LiquidWalletFundingDependencySelection result;
				try
				{
					result = LiquidWalletFundingDependencyDeriver.Derive(state, mainSelected, mainRevision);
				}
				catch (Exception exception)
				{
					writer.WriteString("outcome", "MAIN_" + Classify(exception));
					return;
				}

				// Mutate the caller-owned selection after Derive returns; the
				// result emitted below must be unaffected.
				mainSelected.Reverse();
				if (mainSelected.Count > 0)
				{
					mainSelected[0] = "00";
				}

				IReadOnlyList<IReadOnlyList<string>> rows = result.PreviousTransactionIdsBySelectedInput;
				bool rowsSeparatelyOwned = true;
				bool noNullRows = rows is not null;
				for (int left = 0; noNullRows && left < rows.Count; left++)
				{
					rowsSeparatelyOwned &= rows[left] is not null;
					noNullRows &= rows[left] is not null;
					for (int right = left + 1; right < rows.Count; right++)
					{
						rowsSeparatelyOwned &= !ReferenceEquals(rows[left], rows[right]);
					}
				}

				bool collectionsReadOnly = noNullRows &&
					IsReadOnlyStringList(result.CanonicalSelectedOutPointHexes) &&
					IsReadOnlyRowList(rows);
				for (int rowIndex = 0; noNullRows && rowIndex < rows.Count; rowIndex++)
				{
					collectionsReadOnly &= IsReadOnlyStringList(rows[rowIndex]);
				}

				writer.WriteString("outcome", "OK");
				writer.WriteBoolean("rowsSeparatelyOwned", rowsSeparatelyOwned);
				writer.WriteBoolean("noNullRows", noNullRows);
				writer.WriteBoolean("collectionsReadOnly", collectionsReadOnly);
				writer.WriteStartArray("selected");
				foreach (string selectedHex in result.CanonicalSelectedOutPointHexes)
				{
					writer.WriteStringValue(selectedHex);
				}

				writer.WriteEndArray();
				writer.WriteStartArray("rows");
				foreach (IReadOnlyList<string> row in rows)
				{
					writer.WriteStartArray();
					foreach (string previousId in row)
					{
						writer.WriteStringValue(previousId);
					}

					writer.WriteEndArray();
				}

				writer.WriteEndArray();
			}

			private static List<string> ReadSelected(JsonElement element)
			{
				var selected = new List<string>();
				foreach (JsonElement row in element.GetProperty("selected").EnumerateArray())
				{
					selected.Add(row.ValueKind == JsonValueKind.Null ? null! : row.GetString()!);
				}

				return selected;
			}

			private static string RunProbe(LiquidWalletState state, List<string> selected, ulong revision)
			{
				try
				{
					LiquidWalletFundingDependencyDeriver.Derive(state, selected, revision);
					return "NONE";
				}
				catch (Exception exception)
				{
					return Classify(exception);
				}
			}

			private static string Classify(Exception exception) =>
				exception switch
				{
					ArgumentNullException => "ARGUMENT_NULL",
					ArgumentException => "ARGUMENT",
					InvalidOperationException => "INVALID_OPERATION",
					_ => "OTHER",
				};

			private static bool IsReadOnlyStringList(IReadOnlyList<string> list)
			{
				if (list is not IList<string> mutable)
				{
					return true;
				}

				try
				{
					mutable.Add("00");
					return false;
				}
				catch (NotSupportedException)
				{
				}

				try
				{
					if (mutable.Count > 0)
					{
						mutable[0] = "00";
						return false;
					}
				}
				catch (NotSupportedException)
				{
				}

				return true;
			}

			private static bool IsReadOnlyRowList(IReadOnlyList<IReadOnlyList<string>> list)
			{
				if (list is not IList<IReadOnlyList<string>> mutable)
				{
					return true;
				}

				try
				{
					mutable.Add(Array.Empty<string>());
					return false;
				}
				catch (NotSupportedException)
				{
				}

				return true;
			}
		}
		""";

	private static readonly Lazy<string> ChildAssemblyPath = new(CompileChildAssembly);

	private static string CompileChildAssembly()
	{
		var syntaxTree = CSharpSyntaxTree.ParseText(
			ChildProgramSource,
			new CSharpParseOptions(LanguageVersion.Latest));
		var referencePaths = new HashSet<string>(StringComparer.Ordinal)
		{
			typeof(object).Assembly.Location,
			typeof(Console).Assembly.Location,
			typeof(JsonDocument).Assembly.Location,
			typeof(List<>).Assembly.Location,
			typeof(System.Buffers.ReadOnlySequence<>).Assembly.Location,
			typeof(LiquidWalletLoadSave).Assembly.Location,
			Assembly.Load("System.Runtime").Location,
		};
		var references = new List<MetadataReference>();
		foreach (string referencePath in referencePaths)
		{
			references.Add(MetadataReference.CreateFromFile(referencePath));
		}

		// The child carries the test assembly identity so the landed
		// InternalsVisibleTo("WalletWasabi.Tests") grant applies. It lives in
		// its own subdirectory (with a private copy of the production
		// assembly) so its identity never collides with the real test
		// assembly on the child process probing path; it is only ever loaded
		// by path as a child process entry point.
		var compilation = CSharpCompilation.Create(
			"WalletWasabi.Tests",
			[syntaxTree],
			references,
			new CSharpCompilationOptions(
				OutputKind.ConsoleApplication,
				optimizationLevel: OptimizationLevel.Release));
		string childDirectory = Path.Combine(
			AppContext.BaseDirectory,
			"liquid-funding-dependency-child");
		Directory.CreateDirectory(childDirectory);
		File.Copy(
			typeof(LiquidWalletLoadSave).Assembly.Location,
			Path.Combine(childDirectory, "WalletWasabi.dll"),
			overwrite: true);
		// The production assembly's module initializer patches NBitcoin
		// networks, so the child probing path needs these assemblies too.
		foreach (string moduleDependency in new[]
		{
			"NBitcoin.dll",
			"NBitcoin.Secp256k1.dll",
			"Microsoft.Extensions.Logging.Abstractions.dll",
		})
		{
			File.Copy(
				Path.Combine(AppContext.BaseDirectory, moduleDependency),
				Path.Combine(childDirectory, moduleDependency),
				overwrite: true);
		}
		string childPath = Path.Combine(childDirectory, "liquid-funding-dependency-child.dll");
		using (FileStream stream = File.Create(childPath))
		{
			EmitResult emitted = compilation.Emit(stream);
			Assert.True(
				emitted.Success,
				string.Join(
					"\n",
					emitted.Diagnostics
						.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
						.Select(diagnostic => diagnostic.ToString())));
		}

		return childPath;
	}

	private static string ResolveDotnetHostPath()
	{
		string? processPath = Environment.ProcessPath;
		if (processPath is not null &&
			string.Equals(
				Path.GetFileNameWithoutExtension(processPath),
				"dotnet",
				StringComparison.OrdinalIgnoreCase))
		{
			return processPath;
		}

		string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
		if (!string.IsNullOrEmpty(dotnetRoot))
		{
			string candidate = Path.Combine(
				dotnetRoot,
				OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return "dotnet";
	}

	private static JsonDocument RunChild(object inputPayload)
	{
		string childAssemblyPath = ChildAssemblyPath.Value;
		string runtimeConfigPath = Path.Combine(
			AppContext.BaseDirectory,
			"WalletWasabi.Tests.runtimeconfig.json");
		Assert.True(File.Exists(runtimeConfigPath), "The test runtime configuration is missing.");

		var startInfo = new ProcessStartInfo
		{
			FileName = ResolveDotnetHostPath(),
			WorkingDirectory = AppContext.BaseDirectory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		startInfo.ArgumentList.Add("exec");
		startInfo.ArgumentList.Add("--runtimeconfig");
		startInfo.ArgumentList.Add(runtimeConfigPath);
		startInfo.ArgumentList.Add(childAssemblyPath);

		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), "The fresh child process did not start.");
		process.StandardInput.Write(JsonSerializer.Serialize(inputPayload));
		process.StandardInput.Close();
		Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
		Task<string> errorTask = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
		{
			process.Kill(entireProcessTree: true);
			throw new Xunit.Sdk.XunitException("The fresh child process timed out.");
		}

		string error = errorTask.GetAwaiter().GetResult();
		string output = outputTask.GetAwaiter().GetResult();
		Assert.True(
			process.ExitCode == 0 && output.Length > 0,
			$"The fresh child process exited with code {process.ExitCode}. stderr: {error}");
		return JsonDocument.Parse(output);
	}

	private static Dictionary<string, object> BuildLoadCorruptionInput(
		string walletDataDir,
		string walletName,
		string keyHex,
		string contextHex) =>
		new(StringComparer.Ordinal)
		{
			["mode"] = "load-corruption",
			["walletDataDir"] = walletDataDir,
			["walletName"] = walletName,
			["keyHex"] = keyHex,
			["contextHex"] = contextHex,
		};

	private static Dictionary<string, object> BuildDeriveInput(
		string walletDataDir,
		string walletName,
		string keyHex,
		string contextHex,
		object[]? probes = null,
		Dictionary<string, object>? main = null)
	{
		var input = new Dictionary<string, object>(StringComparer.Ordinal)
		{
			["mode"] = "derive",
			["walletDataDir"] = walletDataDir,
			["walletName"] = walletName,
			["keyHex"] = keyHex,
			["contextHex"] = contextHex,
		};
		if (probes is not null)
		{
			input["probes"] = probes;
		}
		if (main is not null)
		{
			input["main"] = main;
		}

		return input;
	}

	private static Dictionary<string, object> DeriveCall(ulong revision, string[] selected) =>
		new(StringComparer.Ordinal)
		{
			["revision"] = revision,
			["selected"] = selected,
		};

	// ---------------------------------------------------------------------
	// Sealed-fixture machinery: framed-file read/write plus authenticated
	// decrypt/reseal of the landed envelope (header and nonce preserved), the
	// same test-side pattern the landed replay-protection tests use.
	// ---------------------------------------------------------------------

	private static string WalletFilePath(string walletDataDir, string walletName) =>
		Path.Combine(walletDataDir, walletName + ".lwwal");

	private static byte[] ReadEnvelope(string walletFilePath)
	{
		byte[] framed = File.ReadAllBytes(walletFilePath);
		Assert.True(framed.Length > FrameHeaderLength, "The sealed wallet fixture frame is too short.");
		Assert.Equal("WLWALFMT"u8.ToArray(), framed[..8]);
		return framed[FrameHeaderLength..];
	}

	private static void WriteEnvelope(string walletFilePath, byte[] envelope)
	{
		byte[] framed = new byte[FrameHeaderLength + envelope.Length];
		"WLWALFMT"u8.CopyTo(framed.AsSpan());
		BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(10), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(12), (uint)envelope.Length);
		envelope.CopyTo(framed.AsSpan(FrameHeaderLength));
		File.WriteAllBytes(walletFilePath, framed);
	}

	private static byte[] BuildAssociatedData(byte[] envelope, byte[] context)
	{
		byte[] associatedData = new byte[EnvelopeHeaderLength + context.Length];
		envelope.AsSpan(0, EnvelopeHeaderLength).CopyTo(associatedData);
		context.CopyTo(associatedData.AsSpan(EnvelopeHeaderLength));
		return associatedData;
	}

	private static byte[] DecryptEnvelopePlaintext(byte[] envelope, byte[] key, byte[] context)
	{
		int plaintextLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(20));
		byte[] plaintext = new byte[plaintextLength];
		byte[] associatedData = BuildAssociatedData(envelope, context);
		using var aes = new AesGcm(key, LiquidWalletReplayProtectedPayload.TagLength);
		aes.Decrypt(
			envelope.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength),
			envelope.AsSpan(EnvelopeHeaderLength, plaintextLength),
			envelope.AsSpan(
				EnvelopeHeaderLength + plaintextLength,
				LiquidWalletReplayProtectedPayload.TagLength),
			plaintext,
			associatedData);
		return plaintext;
	}

	private static byte[] ResealEnvelopePlaintext(
		byte[] envelope,
		byte[] plaintext,
		byte[] key,
		byte[] context)
	{
		byte[] resealed = [.. envelope];
		byte[] associatedData = BuildAssociatedData(resealed, context);
		using var aes = new AesGcm(key, LiquidWalletReplayProtectedPayload.TagLength);
		aes.Encrypt(
			resealed.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength),
			plaintext,
			resealed.AsSpan(EnvelopeHeaderLength, plaintext.Length),
			resealed.AsSpan(
				EnvelopeHeaderLength + plaintext.Length,
				LiquidWalletReplayProtectedPayload.TagLength),
			associatedData);
		return resealed;
	}

	private static int ReadCanonicalLength(byte[] plaintext) =>
		BinaryPrimitives.ReadInt32LittleEndian(plaintext.AsSpan(8));

	private static void WriteCanonicalLength(byte[] plaintext, uint canonicalLength) =>
		BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(8), canonicalLength);

	/// <summary>
	/// Walks the canonical replay encoding inside the decrypted plaintext to
	/// the start (transaction-id field offset, relative to the plaintext) of
	/// the delta with the given ordinal. Layout per the landed codec: assetId
	/// (32), revision (8), deltaCount (4), then per delta txid (32),
	/// spentCount (4), spent outpoints (36 each), createdCount (4), created
	/// outputs (36 + 4 + script + 32 + 8 + 1 + 4 + 33 each).
	/// </summary>
	private static int LocateDeltaOffset(byte[] plaintext, int deltaOrdinal)
	{
		int position = CanonicalStartOffset + 32 + 8;
		uint deltaCount = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(position));
		position += 4;
		Assert.True((uint)deltaOrdinal < deltaCount, "The corruption target delta ordinal is out of range.");
		for (int ordinal = 0; ordinal < deltaOrdinal; ordinal++)
		{
			position += 32;
			uint spentCount = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(position));
			position += 4 + checked((int)spentCount * 36);
			uint createdCount = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(position));
			position += 4;
			for (uint createdIndex = 0; createdIndex < createdCount; createdIndex++)
			{
				position += 36;
				uint scriptLength = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(position));
				position += 4 + checked((int)scriptLength) + 32 + 8 + 1 + 4 + 33;
			}
		}

		return position;
	}

	private static byte[] ConsensusTransactionIdBytes(string canonicalRpcHex)
	{
		byte[] consensusBytes = Convert.FromHexString(canonicalRpcHex);
		Array.Reverse(consensusBytes);
		return consensusBytes;
	}

	private static string SelectedOutPointHex(string transactionIdHex, uint outputIndex)
	{
		byte[] outPointBytes = new byte[36];
		ConsensusTransactionIdBytes(transactionIdHex).CopyTo(outPointBytes.AsSpan());
		BinaryPrimitives.WriteUInt32LittleEndian(outPointBytes.AsSpan(32), outputIndex);
		return Convert.ToHexString(outPointBytes).ToLowerInvariant();
	}

	// ---------------------------------------------------------------------
	// Deterministic typed fixture builders.
	// ---------------------------------------------------------------------

	private static string RepeatHex(string pair)
	{
		Assert.Equal(2, pair.Length);
		return string.Concat(Enumerable.Repeat(pair, 32));
	}

	private static LiquidTransactionId Tx(string canonicalRpcHex) =>
		LiquidTransactionId.ParseRpcHex(canonicalRpcHex);

	private static LiquidOutPoint OutPoint(string transactionIdHex, uint outputIndex) =>
		LiquidOutPoint.CreateSpendable(Tx(transactionIdHex), outputIndex);

	private static LiquidOwnedOutput Output(
		string transactionIdHex,
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
			OutPoint(transactionIdHex, outputIndex),
			spendKey.GetScriptPubKey(),
			LiquidAssetAmount.Create(assetId, peggedAssetId, atomicUnits),
			spendKey);
	}

	private static LiquidWalletTransactionDelta Delta(
		string transactionIdHex,
		IEnumerable<LiquidOutPoint> spentOutPoints,
		IEnumerable<LiquidOwnedOutput> createdOutputs) =>
		LiquidWalletTransactionDelta.Create(Tx(transactionIdHex), spentOutPoints, createdOutputs);

	private static string GetFixtureDirectory(string testName)
	{
		string directory = Path.Combine(Common.GetWorkDir(), testName);
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static void SaveWallet(
		string walletDataDir,
		string walletName,
		LiquidWalletState state,
		ulong generation,
		string keyHex,
		string contextHex) =>
		LiquidWalletLoadSave.Save(
			walletDataDir,
			walletName,
			state,
			generation,
			Convert.FromHexString(keyHex),
			Convert.FromHexString(contextHex));

	// =====================================================================
	// Section 4.1: load-layer sealed-file corruption tests (fresh child).
	// Each case starts from its own valid sealed wallet fixture, applies one
	// independently literal corruption to the inner replay encoding, reseals
	// the otherwise valid outer frame, and asserts in a clean child process
	// that LiquidWalletLoadSave.Load rejects the file with exactly
	// LiquidWalletReplayProtectionException. The child never calls Derive.
	// =====================================================================

	private static void RunLoadLayerCorruptionCase(
		string testName,
		string walletName,
		string peggedAssetPair,
		string keyHex,
		string contextHex,
		LiquidWalletState sealedState,
		ulong generation,
		Action<byte[]> applyLiteralCorruption)
	{
		string walletDataDir = GetFixtureDirectory(testName);
		byte[] key = Convert.FromHexString(keyHex);
		byte[] context = Convert.FromHexString(contextHex);
		_ = peggedAssetPair;
		SaveWallet(walletDataDir, walletName, sealedState, generation, keyHex, contextHex);

		string walletFilePath = WalletFilePath(walletDataDir, walletName);
		byte[] envelope = ReadEnvelope(walletFilePath);
		byte[] plaintext = DecryptEnvelopePlaintext(envelope, key, context);
		applyLiteralCorruption(plaintext);
		byte[] resealed = ResealEnvelopePlaintext(envelope, plaintext, key, context);
		WriteEnvelope(walletFilePath, resealed);

		using JsonDocument result = RunChild(BuildLoadCorruptionInput(
			walletDataDir,
			walletName,
			keyHex,
			contextHex));
		Assert.Equal("REPLAY_PROTECTION", result.RootElement.GetProperty("outcome").GetString());
	}

	[Fact]
	public void LoadRejectsRetainedDeltaWithZeroTransactionIdInFreshChild()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("f1"));
		string rootId = RepeatHex("1a");
		string spenderId = RepeatHex("1b");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(rootId, [], [Output(rootId, 0, peggedAsset, peggedAsset, 101)]))
			.Apply(1, Delta(spenderId, [OutPoint(rootId, 0)], []));

		RunLoadLayerCorruptionCase(
			nameof(LoadRejectsRetainedDeltaWithZeroTransactionIdInFreshChild),
			"zero-delta-id",
			"f1",
			RepeatHex("21"),
			RepeatHex("22"),
			state,
			11,
			plaintext =>
			{
				int spenderOffset = LocateDeltaOffset(plaintext, 1);
				Assert.Equal(
					ConsensusTransactionIdBytes(spenderId),
					plaintext[spenderOffset..(spenderOffset + 32)]);
				// Literal corruption: the retained delta's fixed 32-byte
				// transaction-id field becomes all zero bytes.
				plaintext.AsSpan(spenderOffset, 32).Clear();
			});
	}

	[Fact]
	public void LoadRejectsDuplicateRetainedTransactionIdsInFreshChild()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("f2"));
		string rootId = RepeatHex("2a");
		string firstSpenderId = RepeatHex("2b");
		string secondSpenderId = RepeatHex("2c");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(rootId, [], [
				Output(rootId, 0, peggedAsset, peggedAsset, 102),
				Output(rootId, 1, peggedAsset, peggedAsset, 103),
			]))
			.Apply(1, Delta(firstSpenderId, [OutPoint(rootId, 0)], []))
			.Apply(2, Delta(secondSpenderId, [OutPoint(rootId, 1)], []));

		RunLoadLayerCorruptionCase(
			nameof(LoadRejectsDuplicateRetainedTransactionIdsInFreshChild),
			"duplicate-delta-ids",
			"f2",
			RepeatHex("23"),
			RepeatHex("24"),
			state,
			12,
			plaintext =>
			{
				int secondSpenderOffset = LocateDeltaOffset(plaintext, 2);
				Assert.Equal(
					ConsensusTransactionIdBytes(secondSpenderId),
					plaintext[secondSpenderOffset..(secondSpenderOffset + 32)]);
				// Literal corruption: two retained deltas now encode the same
				// canonical transaction id.
				ConsensusTransactionIdBytes(firstSpenderId)
					.CopyTo(plaintext.AsSpan(secondSpenderOffset, 32));
			});
	}

	[Fact]
	public void LoadRejectsTruncatedRetainedTransactionIdFieldInFreshChild()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("f3"));
		string rootId = RepeatHex("3a");
		string spenderId = RepeatHex("3b");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(rootId, [], [Output(rootId, 0, peggedAsset, peggedAsset, 104)]))
			.Apply(1, Delta(spenderId, [OutPoint(rootId, 0)], []));

		RunLoadLayerCorruptionCase(
			nameof(LoadRejectsTruncatedRetainedTransactionIdFieldInFreshChild),
			"truncated-delta-id",
			"f3",
			RepeatHex("25"),
			RepeatHex("26"),
			state,
			13,
			plaintext =>
			{
				int spenderOffset = LocateDeltaOffset(plaintext, 1);
				Assert.Equal(
					ConsensusTransactionIdBytes(spenderId),
					plaintext[spenderOffset..(spenderOffset + 32)]);
				// Literal corruption: the canonical encoding ends 17 bytes into
				// the second delta's fixed 32-byte transaction-id field, so
				// LiquidTransactionId.ConsensusByteLength is never available.
				uint truncatedLength = (uint)(spenderOffset - CanonicalStartOffset + 17);
				Assert.True(truncatedLength < (uint)ReadCanonicalLength(plaintext));
				WriteCanonicalLength(plaintext, truncatedLength);
			});
	}

	[Fact]
	public void LoadRejectsTruncatedRetainedSpentOutPointEncodingInFreshChild()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("f4"));
		string rootId = RepeatHex("4a");
		string spenderId = RepeatHex("4b");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(rootId, [], [Output(rootId, 0, peggedAsset, peggedAsset, 105)]))
			.Apply(1, Delta(spenderId, [OutPoint(rootId, 0)], []));

		RunLoadLayerCorruptionCase(
			nameof(LoadRejectsTruncatedRetainedSpentOutPointEncodingInFreshChild),
			"truncated-spent-outpoint",
			"f4",
			RepeatHex("27"),
			RepeatHex("28"),
			state,
			14,
			plaintext =>
			{
				int spenderOffset = LocateDeltaOffset(plaintext, 1);
				int spentOutPointOffset = spenderOffset + 32 + 4;
				Assert.Equal(
					1u,
					BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(spenderOffset + 32)));
				Assert.Equal(
					ConsensusTransactionIdBytes(rootId),
					plaintext[spentOutPointOffset..(spentOutPointOffset + 32)]);
				// Literal corruption: the declared spent-outpoint entry ends 20
				// bytes in, so the fixed 36-byte LiquidOutPoint.ConsensusByteLength
				// is never available.
				uint truncatedLength = (uint)(spentOutPointOffset - CanonicalStartOffset + 20);
				Assert.True(truncatedLength < (uint)ReadCanonicalLength(plaintext));
				WriteCanonicalLength(plaintext, truncatedLength);
			});
	}

	[Fact]
	public void LoadRejectsNonSpendableRetainedSpentOutPointEncodingInFreshChild()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("f5"));
		string rootId = RepeatHex("5a");
		string spenderId = RepeatHex("5b");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(rootId, [], [Output(rootId, 0, peggedAsset, peggedAsset, 106)]))
			.Apply(1, Delta(spenderId, [OutPoint(rootId, 0)], []));

		RunLoadLayerCorruptionCase(
			nameof(LoadRejectsNonSpendableRetainedSpentOutPointEncodingInFreshChild),
			"non-spendable-spent-outpoint",
			"f5",
			RepeatHex("29"),
			RepeatHex("2a"),
			state,
			15,
			plaintext =>
			{
				int spenderOffset = LocateDeltaOffset(plaintext, 1);
				int outputIndexOffset = spenderOffset + 32 + 4 + 32;
				Assert.Equal(
					0u,
					BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(outputIndexOffset)));
				// Literal corruption: the spent-outpoint output index becomes
				// 0xffffffff, which carries input flag bits and is not spendable.
				plaintext[outputIndexOffset] = 0xff;
				plaintext[outputIndexOffset + 1] = 0xff;
				plaintext[outputIndexOffset + 2] = 0xff;
				plaintext[outputIndexOffset + 3] = 0xff;
			});
	}

	[Fact]
	public void LoadRejectsZeroTransactionIdRetainedSpentOutPointEncodingInFreshChild()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("f6"));
		string rootId = RepeatHex("6a");
		string spenderId = RepeatHex("6b");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(rootId, [], [Output(rootId, 0, peggedAsset, peggedAsset, 107)]))
			.Apply(1, Delta(spenderId, [OutPoint(rootId, 0)], []));

		RunLoadLayerCorruptionCase(
			nameof(LoadRejectsZeroTransactionIdRetainedSpentOutPointEncodingInFreshChild),
			"zero-spent-outpoint-id",
			"f6",
			RepeatHex("2b"),
			RepeatHex("2c"),
			state,
			16,
			plaintext =>
			{
				int spenderOffset = LocateDeltaOffset(plaintext, 1);
				int spentOutPointOffset = spenderOffset + 32 + 4;
				Assert.Equal(
					ConsensusTransactionIdBytes(rootId),
					plaintext[spentOutPointOffset..(spentOutPointOffset + 32)]);
				// Literal corruption: the spent outpoint's 32-byte transaction id
				// becomes all zero bytes.
				plaintext.AsSpan(spentOutPointOffset, 32).Clear();
			});
	}

	// =====================================================================
	// Section 4.2: deriver tests (fresh child Load + Derive). The parent
	// writes a lawful sealed wallet fixture through the landed persistence
	// surface; the child loads once, asserts the loaded state is non-null,
	// and only then derives. All expected vectors below are per-test literals
	// authored from fixed bytes before any production call.
	// =====================================================================

	[Fact]
	public void FreshProcessReopenDerivesCanonicalRowsAtExactNonzeroRevision()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e1"));
		string p0 = RepeatHex("3a");
		string p1 = RepeatHex("3b");
		string c0 = RepeatHex("3c");
		string c1 = RepeatHex("3d");
		string u0 = RepeatHex("3e");
		string keyHex = RepeatHex("41");
		string contextHex = RepeatHex("42");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 50)]))
			.Apply(1, Delta(p1, [], [
				Output(p1, 0, peggedAsset, peggedAsset, 40),
				Output(p1, 1, peggedAsset, peggedAsset, 30),
			]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0), OutPoint(p1, 0)], [
				Output(c0, 5, peggedAsset, peggedAsset, 35),
				Output(c0, 2, peggedAsset, peggedAsset, 25),
				Output(c0, 7, peggedAsset, peggedAsset, 30),
			]))
			.Apply(3, Delta(c1, [OutPoint(c0, 7)], [Output(c1, 0, peggedAsset, peggedAsset, 30)]))
			.Apply(4, Delta(u0, [], [Output(u0, 3, peggedAsset, peggedAsset, 60)]));
		Assert.Equal(5ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(FreshProcessReopenDerivesCanonicalRowsAtExactNonzeroRevision));
		SaveWallet(walletDataDir, "reopen", state, 13, keyHex, contextHex);

		// Independent expected vectors, authored from fixed bytes before the
		// production call in the child.
		string[] expectedSelected =
		[
			SelectedOutPointHex(p1, 1),
			SelectedOutPointHex(c0, 2),
			SelectedOutPointHex(c0, 5),
			SelectedOutPointHex(c1, 0),
		];
		string[][] expectedRows =
		[
			[],
			[p0, p1],
			[p0, p1],
			[c0],
		];

		// Deliberately reversed and mixed-case caller spellings.
		string[] callerSelected =
		[
			SelectedOutPointHex(c1, 0).ToUpperInvariant(),
			SelectedOutPointHex(c0, 5),
			SelectedOutPointHex(c0, 2).ToUpperInvariant(),
			SelectedOutPointHex(p1, 1),
		];

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"reopen",
			keyHex,
			contextHex,
			probes:
			[
				DeriveCall(4ul, callerSelected),
				DeriveCall(6ul, callerSelected),
				DeriveCall(0ul, callerSelected),
			],
			main: DeriveCall(5ul, callerSelected)));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.Equal(5ul, root.GetProperty("revision").GetUInt64());
		Assert.Equal(
			new[] { "INVALID_OPERATION", "INVALID_OPERATION", "INVALID_OPERATION" },
			ReadStringArray(root.GetProperty("probeOutcomes")));
		Assert.True(root.GetProperty("rowsSeparatelyOwned").GetBoolean());
		Assert.True(root.GetProperty("noNullRows").GetBoolean());
		Assert.True(root.GetProperty("collectionsReadOnly").GetBoolean());

		string[] actualSelected = ReadStringArray(root.GetProperty("selected"));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Equal(expectedSelected, actualSelected);
		Assert.Equal(expectedRows.Length, actualRows.Length);
		for (int rowIndex = 0; rowIndex < expectedRows.Length; rowIndex++)
		{
			Assert.Equal(expectedRows[rowIndex], actualRows[rowIndex]);
		}

		// Repeated-candidate rows are byte-for-byte identical.
		Assert.Equal(actualRows[1], actualRows[2]);

		// The unrelated retained delta never appears in any row.
		Assert.All(actualRows, row => Assert.DoesNotContain(u0, row));

		// Lowercase, nonzero, unique, strictly-ordinal-ascending invariants.
		AssertRowInvariants(actualSelected, actualRows);
	}

	[Fact]
	public void IndependentExpectedVectorsRejectOmittedExtraCandidateAndMisorderedDependenciesInFreshProcess()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		string p0 = RepeatHex("4a");
		string p1 = RepeatHex("4b");
		string c0 = RepeatHex("4c");
		string c1 = RepeatHex("4d");
		string absentId = RepeatHex("4e");
		string keyHex = RepeatHex("43");
		string contextHex = RepeatHex("44");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 6)]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 5)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0), OutPoint(p1, 0)], [
				Output(c0, 1, peggedAsset, peggedAsset, 6),
				Output(c0, 2, peggedAsset, peggedAsset, 5),
			]))
			.Apply(3, Delta(c1, [OutPoint(c0, 1)], [Output(c1, 0, peggedAsset, peggedAsset, 6)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(IndependentExpectedVectorsRejectOmittedExtraCandidateAndMisorderedDependenciesInFreshProcess));
		SaveWallet(walletDataDir, "boundary", state, 17, keyHex, contextHex);

		// Independent expected vectors from fixed bytes: canonical selected
		// outpoints and exact dependency rows.
		string[] expectedSelected =
		[
			SelectedOutPointHex(c0, 2),
			SelectedOutPointHex(c1, 0),
		];
		string[][] expectedRows =
		[
			[p0, p1],
			[c0],
		];

		using (JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"boundary",
			keyHex,
			contextHex,
			main: DeriveCall(
				4ul,
				new[]
				{
					SelectedOutPointHex(c1, 0),
					SelectedOutPointHex(c0, 2),
				}))))
		{
			JsonElement root = result.RootElement;
			Assert.Equal("OK", root.GetProperty("outcome").GetString());
			Assert.Equal(expectedSelected, ReadStringArray(root.GetProperty("selected")));
			string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
			Assert.Equal(expectedRows.Length, actualRows.Length);
			for (int rowIndex = 0; rowIndex < expectedRows.Length; rowIndex++)
			{
				Assert.Equal(expectedRows[rowIndex], actualRows[rowIndex]);
			}
		}

		// The real funding boundary: a fixture plan and expectation-bound raw
		// batch accept exactly the independent literal rows and reject every
		// independently literal mutated dependency vector. Mutations are
		// authored as literals here and are never normalized by production
		// code before the boundary call.
		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			4,
			[OutPoint(c0, 2), OutPoint(c1, 0)],
			LiquidSuppliedConfidentialDestinationBatch.Create([
				CreateDestination(manifest, peggedAsset, 10),
			]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
		ElementsExpectationBoundRawTransactionBatch rawBatch = CreateExpectationBoundBatch(
			manifest,
			[
				(c0, new byte[] { 0x01 }),
				(c1, new byte[] { 0x02 }),
				(p0, new byte[] { 0x03 }),
				(p1, new byte[] { 0x04 }),
			]);

		// The unmutated independent vectors pass through the landed seam.
		bool accepted = rawBatch.TryCreateOrdinaryWalletPlanFundingBatch(
			plan,
			[new[] { p0, p1 }, new[] { c0 }],
			out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode acceptedErrorCode);
		Assert.True(accepted, acceptedErrorCode.ToString());
		Assert.NotNull(fundingBatch);
		fundingBatch.Dispose();

		// (a) omitted dependency id.
		AssertFundingCompositionRejected(rawBatch, plan, [new[] { p0 }, new[] { c0 }]);
		// (b) extra dependency id.
		AssertFundingCompositionRejected(rawBatch, plan, [new[] { p0, p1 }, new[] { c0, absentId }]);
		// (c) candidate id inside its own row.
		AssertFundingCompositionRejected(rawBatch, plan, [new[] { p0, p1 }, new[] { c0, c1 }]);
		// (d) duplicate dependency id.
		AssertFundingCompositionRejected(rawBatch, plan, [new[] { p0, p0, p1 }, new[] { c0 }]);
		// (e) uppercase dependency id.
		AssertFundingCompositionRejected(
			rawBatch,
			plan,
			[new[] { p0.ToUpperInvariant(), p1 }, new[] { c0 }]);
		// (f) reversed dependency order.
		AssertFundingCompositionRejected(rawBatch, plan, [new[] { p1, p0 }, new[] { c0 }]);
	}

	[Fact]
	public void NonzeroRevisionSeedDistinguishesBootstrapAndStaleRevisionAcrossFreshReopen()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e2"));
		string p0 = RepeatHex("5a");
		string p1 = RepeatHex("5b");
		string c0 = RepeatHex("5c");
		string c1 = RepeatHex("5d");
		string keyHex = RepeatHex("45");
		string contextHex = RepeatHex("46");
		const ulong SeededRevision = 4;
		const ulong SeededGeneration = 21;

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 70)]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 71)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0)], [Output(c0, 0, peggedAsset, peggedAsset, 70)]))
			.Apply(3, Delta(c1, [OutPoint(c0, 0)], [Output(c1, 0, peggedAsset, peggedAsset, 70)]));
		Assert.Equal(SeededRevision, state.Revision);
		Assert.NotEqual(0ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(NonzeroRevisionSeedDistinguishesBootstrapAndStaleRevisionAcrossFreshReopen));
		SaveWallet(walletDataDir, "nonzero", state, SeededGeneration, keyHex, contextHex);

		string[] expectedSelected = [SelectedOutPointHex(c1, 0)];
		string[][] expectedRows = [[c0]];
		string[] callerSelected = [SelectedOutPointHex(c1, 0)];

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"nonzero",
			keyHex,
			contextHex,
			probes:
			[
				DeriveCall(0ul, callerSelected),
				DeriveCall(3ul, callerSelected),
				DeriveCall(5ul, callerSelected),
			],
			main: DeriveCall(SeededRevision, callerSelected)));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.Equal(SeededRevision, root.GetProperty("revision").GetUInt64());
		Assert.NotEqual(0ul, root.GetProperty("revision").GetUInt64());
		Assert.Equal(
			new[] { "INVALID_OPERATION", "INVALID_OPERATION", "INVALID_OPERATION" },
			ReadStringArray(root.GetProperty("probeOutcomes")));
		Assert.Equal(expectedSelected, ReadStringArray(root.GetProperty("selected")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Single(actualRows);
		Assert.Equal(expectedRows[0], actualRows[0]);
	}

	[Fact]
	public void RetainedOlderStateRollbackReopenDerivesOlderRowsInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e3"));
		string p0 = RepeatHex("6a");
		string c0 = RepeatHex("6b");
		string c1 = RepeatHex("6c");
		string u0 = RepeatHex("6d");
		string keyHex = RepeatHex("47");
		string contextHex = RepeatHex("48");

		LiquidWalletState olderState = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [
				Output(p0, 0, peggedAsset, peggedAsset, 80),
				Output(p0, 1, peggedAsset, peggedAsset, 81),
			]))
			.Apply(1, Delta(c0, [OutPoint(p0, 0)], [Output(c0, 0, peggedAsset, peggedAsset, 80)]));
		Assert.Equal(2ul, olderState.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(RetainedOlderStateRollbackReopenDerivesOlderRowsInFreshProcess));
		SaveWallet(walletDataDir, "rollback", olderState, 31, keyHex, contextHex);
		string walletFilePath = WalletFilePath(walletDataDir, "rollback");
		byte[] olderSealedBytes = File.ReadAllBytes(walletFilePath);

		// Advance a different state instance and save the newer revision.
		LiquidWalletState newerState = olderState
			.Apply(2, Delta(c1, [OutPoint(c0, 0)], [Output(c1, 0, peggedAsset, peggedAsset, 80)]))
			.Apply(3, Delta(u0, [], [Output(u0, 0, peggedAsset, peggedAsset, 82)]));
		Assert.Equal(4ul, newerState.Revision);
		Assert.NotSame(olderState, newerState);
		SaveWallet(walletDataDir, "rollback", newerState, 32, keyHex, contextHex);
		byte[] newerSealedBytes = File.ReadAllBytes(walletFilePath);
		Assert.False(newerSealedBytes.AsSpan().SequenceEqual(olderSealedBytes));

		// Real rollback: restore the retained older sealed bytes without
		// calling Save on either state instance.
		File.WriteAllBytes(walletFilePath, olderSealedBytes);

		string[] expectedSelected = [SelectedOutPointHex(c0, 0)];
		string[][] expectedRows = [[p0]];
		string[] callerSelected = [SelectedOutPointHex(c0, 0)];

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"rollback",
			keyHex,
			contextHex,
			probes:
			[
				DeriveCall(4ul, callerSelected),
				DeriveCall(2ul, new[] { SelectedOutPointHex(c1, 0) }),
				DeriveCall(2ul, new[] { SelectedOutPointHex(u0, 0) }),
			],
			main: DeriveCall(2ul, callerSelected)));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.Equal(2ul, root.GetProperty("revision").GetUInt64());
		Assert.Equal(
			new[] { "INVALID_OPERATION", "INVALID_OPERATION", "INVALID_OPERATION" },
			ReadStringArray(root.GetProperty("probeOutcomes")));
		Assert.Equal(expectedSelected, ReadStringArray(root.GetProperty("selected")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Single(actualRows);
		Assert.Equal(expectedRows[0], actualRows[0]);
		Assert.All(actualRows, row =>
		{
			Assert.DoesNotContain(c1, row);
			Assert.DoesNotContain(u0, row);
		});
	}

	// ---------------------------------------------------------------------
	// Additional mandatory deriver tests: same fresh-child Load + Derive
	// protocol with independent literal vectors from lawful sealed fixtures.
	// ---------------------------------------------------------------------

	[Fact]
	public void RevisionMismatchBetweenCallerAndLoadedStateFailsClosedInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e4"));
		string p0 = RepeatHex("7a");
		string p1 = RepeatHex("7b");
		string c0 = RepeatHex("7c");
		string c1 = RepeatHex("7d");
		string keyHex = RepeatHex("49");
		string contextHex = RepeatHex("4a");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 90)]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 91)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0)], [Output(c0, 0, peggedAsset, peggedAsset, 90)]))
			.Apply(3, Delta(c1, [OutPoint(p1, 0)], [Output(c1, 0, peggedAsset, peggedAsset, 91)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(RevisionMismatchBetweenCallerAndLoadedStateFailsClosedInFreshProcess));
		SaveWallet(walletDataDir, "revision-mismatch", state, 33, keyHex, contextHex);

		string[] callerSelected = [SelectedOutPointHex(c0, 0)];
		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"revision-mismatch",
			keyHex,
			contextHex,
			probes:
			[
				DeriveCall(3ul, callerSelected),
				DeriveCall(5ul, callerSelected),
				DeriveCall(0ul, callerSelected),
			],
			main: DeriveCall(4ul, callerSelected)));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.Equal(
			new[] { "INVALID_OPERATION", "INVALID_OPERATION", "INVALID_OPERATION" },
			ReadStringArray(root.GetProperty("probeOutcomes")));
		Assert.Equal(
			new[] { SelectedOutPointHex(c0, 0) },
			ReadStringArray(root.GetProperty("selected")));
	}

	[Fact]
	public void SelectedSpentOutputFailsClosedInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e5"));
		string p0 = RepeatHex("8a");
		string p1 = RepeatHex("8b");
		string c0 = RepeatHex("8c");
		string c1 = RepeatHex("8d");
		string keyHex = RepeatHex("4b");
		string contextHex = RepeatHex("4c");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [
				Output(p0, 0, peggedAsset, peggedAsset, 92),
				Output(p0, 1, peggedAsset, peggedAsset, 93),
			]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 94)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0)], [Output(c0, 0, peggedAsset, peggedAsset, 92)]))
			.Apply(3, Delta(c1, [OutPoint(p1, 0)], [Output(c1, 0, peggedAsset, peggedAsset, 94)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(SelectedSpentOutputFailsClosedInFreshProcess));
		SaveWallet(walletDataDir, "selected-spent", state, 34, keyHex, contextHex);

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"selected-spent",
			keyHex,
			contextHex,
			probes:
			[
				DeriveCall(4ul, new[] { SelectedOutPointHex(p0, 0) }),
			],
			main: DeriveCall(4ul, new[] { SelectedOutPointHex(c0, 0) })));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.Equal(
			new[] { "INVALID_OPERATION" },
			ReadStringArray(root.GetProperty("probeOutcomes")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Single(actualRows);
		Assert.Equal(new[] { p0 }, actualRows[0]);
	}

	[Fact]
	public void SelectedUnknownOutputFailsClosedInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e6"));
		string p0 = RepeatHex("9a");
		string p1 = RepeatHex("9b");
		string c0 = RepeatHex("9c");
		string c1 = RepeatHex("9d");
		string unknownId = RepeatHex("9e");
		string keyHex = RepeatHex("4d");
		string contextHex = RepeatHex("4e");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 95)]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 96)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0)], [Output(c0, 0, peggedAsset, peggedAsset, 95)]))
			.Apply(3, Delta(c1, [OutPoint(p1, 0)], [Output(c1, 0, peggedAsset, peggedAsset, 96)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(SelectedUnknownOutputFailsClosedInFreshProcess));
		SaveWallet(walletDataDir, "selected-unknown", state, 35, keyHex, contextHex);

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"selected-unknown",
			keyHex,
			contextHex,
			probes:
			[
				DeriveCall(4ul, new[] { SelectedOutPointHex(unknownId, 0) }),
			],
			main: DeriveCall(4ul, new[] { SelectedOutPointHex(c1, 0) })));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.Equal(
			new[] { "INVALID_OPERATION" },
			ReadStringArray(root.GetProperty("probeOutcomes")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Single(actualRows);
		Assert.Equal(new[] { p1 }, actualRows[0]);
	}

	[Fact]
	public void MultipleSelectedOutputsFromOneCandidateShareByteIdenticalSeparatelyOwnedRowsInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e7"));
		string p0 = RepeatHex("aa");
		string p1 = RepeatHex("ab");
		string c0 = RepeatHex("ac");
		string c1 = RepeatHex("ad");
		string keyHex = RepeatHex("4f");
		string contextHex = RepeatHex("51");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 60)]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 61)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0), OutPoint(p1, 0)], [
				Output(c0, 7, peggedAsset, peggedAsset, 40),
				Output(c0, 1, peggedAsset, peggedAsset, 41),
				Output(c0, 4, peggedAsset, peggedAsset, 40),
			]))
			.Apply(3, Delta(c1, [OutPoint(c0, 7)], [Output(c1, 0, peggedAsset, peggedAsset, 40)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(MultipleSelectedOutputsFromOneCandidateShareByteIdenticalSeparatelyOwnedRowsInFreshProcess));
		SaveWallet(walletDataDir, "repeated-candidate", state, 36, keyHex, contextHex);

		string[] expectedSelected =
		[
			SelectedOutPointHex(c0, 1),
			SelectedOutPointHex(c0, 4),
		];
		string[][] expectedRows =
		[
			[p0, p1],
			[p0, p1],
		];

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"repeated-candidate",
			keyHex,
			contextHex,
			main: DeriveCall(
				4ul,
				new[]
				{
					SelectedOutPointHex(c0, 4),
					SelectedOutPointHex(c0, 1),
				})));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.True(root.GetProperty("rowsSeparatelyOwned").GetBoolean());
		Assert.Equal(expectedSelected, ReadStringArray(root.GetProperty("selected")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Equal(2, actualRows.Length);
		Assert.Equal(expectedRows[0], actualRows[0]);
		Assert.Equal(expectedRows[1], actualRows[1]);
		Assert.Equal(actualRows[0], actualRows[1]);
	}

	[Fact]
	public void RootCandidateDerivesProvedAllocatedEmptyRowInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e8"));
		string p0 = RepeatHex("ba");
		string p1 = RepeatHex("bb");
		string c0 = RepeatHex("bc");
		string c1 = RepeatHex("bd");
		string keyHex = RepeatHex("52");
		string contextHex = RepeatHex("53");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 62)]))
			.Apply(1, Delta(p1, [], [
				Output(p1, 0, peggedAsset, peggedAsset, 63),
				Output(p1, 1, peggedAsset, peggedAsset, 64),
			]))
			.Apply(2, Delta(c0, [OutPoint(p1, 0)], [Output(c0, 0, peggedAsset, peggedAsset, 63)]))
			.Apply(3, Delta(c1, [OutPoint(c0, 0)], [Output(c1, 0, peggedAsset, peggedAsset, 63)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(RootCandidateDerivesProvedAllocatedEmptyRowInFreshProcess));
		SaveWallet(walletDataDir, "root-empty-row", state, 37, keyHex, contextHex);

		// The root candidate P1's retained delta exists with a genuinely
		// zero-length spent-outpoint list; its row is the allocated empty
		// vector. The C1 row is non-empty in the same derivation.
		string[] expectedSelected =
		[
			SelectedOutPointHex(p1, 1),
			SelectedOutPointHex(c1, 0),
		];
		string[][] expectedRows =
		[
			[],
			[c0],
		];

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"root-empty-row",
			keyHex,
			contextHex,
			main: DeriveCall(
				4ul,
				new[]
				{
					SelectedOutPointHex(c1, 0),
					SelectedOutPointHex(p1, 1),
				})));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.True(root.GetProperty("noNullRows").GetBoolean());
		Assert.Equal(expectedSelected, ReadStringArray(root.GetProperty("selected")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Equal(2, actualRows.Length);
		Assert.Empty(actualRows[0]);
		Assert.Equal(expectedRows[1], actualRows[1]);
	}

	[Fact]
	public void DerivedSelectionDefensivelyOwnsTopLevelAndNestedCollectionsInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("e9"));
		string p0 = RepeatHex("ca");
		string p1 = RepeatHex("cb");
		string c0 = RepeatHex("cc");
		string c1 = RepeatHex("cd");
		string keyHex = RepeatHex("54");
		string contextHex = RepeatHex("56");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 65)]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 66)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0), OutPoint(p1, 0)], [
				Output(c0, 0, peggedAsset, peggedAsset, 65),
				Output(c0, 1, peggedAsset, peggedAsset, 66),
			]))
			.Apply(3, Delta(c1, [OutPoint(c0, 0)], [Output(c1, 0, peggedAsset, peggedAsset, 65)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(DerivedSelectionDefensivelyOwnsTopLevelAndNestedCollectionsInFreshProcess));
		SaveWallet(walletDataDir, "ownership", state, 38, keyHex, contextHex);

		// The child mutates its caller-owned selection list after Derive
		// returns and attempts mutation of the returned collections through
		// writable interface casts before serializing the result; the emitted
		// vectors must equal the independent literals.
		string[] expectedSelected =
		[
			SelectedOutPointHex(c0, 1),
			SelectedOutPointHex(c1, 0),
		];
		string[][] expectedRows =
		[
			[p0, p1],
			[c0],
		];

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"ownership",
			keyHex,
			contextHex,
			main: DeriveCall(
				4ul,
				new[]
				{
					SelectedOutPointHex(c1, 0),
					SelectedOutPointHex(c0, 1),
				})));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.True(root.GetProperty("rowsSeparatelyOwned").GetBoolean());
		Assert.True(root.GetProperty("noNullRows").GetBoolean());
		Assert.True(root.GetProperty("collectionsReadOnly").GetBoolean());
		Assert.Equal(expectedSelected, ReadStringArray(root.GetProperty("selected")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Equal(2, actualRows.Length);
		Assert.Equal(expectedRows[0], actualRows[0]);
		Assert.Equal(expectedRows[1], actualRows[1]);
	}

	[Fact]
	public void CanonicalTransactionIdThenOutputIndexOrderingGovernsSelectionInFreshProcess()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("ea"));
		// Transaction-id hex ordering deliberately differs from application
		// order: the later-applied candidate sorts first by ordinal hex.
		string p0 = RepeatHex("df");
		string p1 = RepeatHex("de");
		string c0 = RepeatHex("d1");
		string c1 = RepeatHex("d0");
		string keyHex = RepeatHex("57");
		string contextHex = RepeatHex("58");

		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 67)]))
			.Apply(1, Delta(p1, [], [Output(p1, 0, peggedAsset, peggedAsset, 68)]))
			.Apply(2, Delta(c0, [OutPoint(p0, 0)], [
				Output(c0, 9, peggedAsset, peggedAsset, 33),
				Output(c0, 0, peggedAsset, peggedAsset, 34),
			]))
			.Apply(3, Delta(c1, [OutPoint(p1, 0)], [Output(c1, 3, peggedAsset, peggedAsset, 68)]));
		Assert.Equal(4ul, state.Revision);

		string walletDataDir = GetFixtureDirectory(
			nameof(CanonicalTransactionIdThenOutputIndexOrderingGovernsSelectionInFreshProcess));
		SaveWallet(walletDataDir, "canonical-order", state, 39, keyHex, contextHex);

		// Canonical order: C1 ("d0"...) before C0 ("d1"...), then C0 index 0
		// before index 9, regardless of caller order and casing.
		string[] expectedSelected =
		[
			SelectedOutPointHex(c1, 3),
			SelectedOutPointHex(c0, 0),
			SelectedOutPointHex(c0, 9),
		];
		string[][] expectedRows =
		[
			[p1],
			[p0],
			[p0],
		];

		using JsonDocument result = RunChild(BuildDeriveInput(
			walletDataDir,
			"canonical-order",
			keyHex,
			contextHex,
			main: DeriveCall(
				4ul,
				new[]
				{
					SelectedOutPointHex(c0, 9).ToUpperInvariant(),
					SelectedOutPointHex(c1, 3),
					SelectedOutPointHex(c0, 0).ToUpperInvariant(),
				})));

		JsonElement root = result.RootElement;
		Assert.Equal("OK", root.GetProperty("outcome").GetString());
		Assert.Equal(expectedSelected, ReadStringArray(root.GetProperty("selected")));
		string[][] actualRows = ReadRowArray(root.GetProperty("rows"));
		Assert.Equal(3, actualRows.Length);
		for (int rowIndex = 0; rowIndex < expectedRows.Length; rowIndex++)
		{
			Assert.Equal(expectedRows[rowIndex], actualRows[rowIndex]);
		}

		AssertRowInvariants(
			ReadStringArray(root.GetProperty("selected")),
			actualRows);
	}

	// ---------------------------------------------------------------------
	// Pure caller-argument validation tests. These are explicitly
	// in-process-only: they satisfy no persistence, reopen, rollback,
	// load-corruption, or retained-replay criterion.
	// ---------------------------------------------------------------------

	[Fact]
	public void InProcessOnlyNullStateNullListNullElementAndEmptySelectionFailClosed()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("eb"));
		string p0 = RepeatHex("1c");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 21)]));

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(null!, [SelectedOutPointHex(p0, 0)], 1));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(state, null!, 1));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(state, [null!], 1));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(state, [], 1));
	}

	[Fact]
	public void InProcessOnlyMalformedSelectedOutPointRowsFailClosed()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("ec"));
		string p0 = RepeatHex("1d");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [Output(p0, 0, peggedAsset, peggedAsset, 22)]));
		string validSelected = SelectedOutPointHex(p0, 0);

		// Odd-length hex.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(state, [validSelected[..71]], 1));
		// Non-hex characters.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(state, [new string('z', 72)], 1));
		// Wrong byte length: 35 bytes.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(state, [validSelected[..70]], 1));
		// Wrong byte length: 37 bytes.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(state, [validSelected + "00"], 1));
		// Non-spendable output index (input flag bits set).
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(
				state,
				[validSelected[..64] + "ffffffff"],
				1));
		// Zero transaction id inside the selected outpoint.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(
				state,
				[ZeroTransactionIdHex + "00000000"],
				1));
	}

	[Fact]
	public void InProcessOnlyDuplicateSelectedOutPointsUnderDifferentCasingFailClosed()
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(RepeatHex("ed"));
		string p0 = RepeatHex("1e");
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
			.Apply(0, Delta(p0, [], [
				Output(p0, 0, peggedAsset, peggedAsset, 23),
				Output(p0, 1, peggedAsset, peggedAsset, 24),
			]));
		string selected = SelectedOutPointHex(p0, 0);

		Assert.Throws<ArgumentException>(() =>
			LiquidWalletFundingDependencyDeriver.Derive(
				state,
				[selected, selected.ToUpperInvariant()],
				1));
	}

	// ---------------------------------------------------------------------
	// Shared assertion and fixture helpers.
	// ---------------------------------------------------------------------

	private static string[] ReadStringArray(JsonElement element)
	{
		var values = new List<string>();
		foreach (JsonElement item in element.EnumerateArray())
		{
			values.Add(item.GetString() ?? throw new Xunit.Sdk.XunitException(
				"A child result string was null."));
		}

		return [.. values];
	}

	private static string[][] ReadRowArray(JsonElement element)
	{
		var rows = new List<string[]>();
		foreach (JsonElement row in element.EnumerateArray())
		{
			rows.Add(ReadStringArray(row));
		}

		return [.. rows];
	}

	private static void AssertRowInvariants(string[] selected, string[][] rows)
	{
		Assert.Equal(selected.Length, rows.Length);
		for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
		{
			string candidateId = selected[rowIndex][..64];
			string[] row = rows[rowIndex];
			Assert.NotNull(row);
			for (int idIndex = 0; idIndex < row.Length; idIndex++)
			{
				string previousId = row[idIndex];
				Assert.Equal(64, previousId.Length);
				Assert.Equal(previousId.ToLowerInvariant(), previousId);
				Assert.Matches("^[0-9a-f]{64}$", previousId);
				Assert.NotEqual(ZeroTransactionIdHex, previousId);
				// The canonical selected outpoint hex leads with the reversed
				// consensus transaction-id bytes, so compare against the
				// canonical RPC form.
				string candidateRpcHex = Convert.ToHexString(
					ConsensusTransactionIdBytes(candidateId)).ToLowerInvariant();
				Assert.NotEqual(candidateRpcHex, previousId);
				if (idIndex > 0)
				{
					Assert.True(
						StringComparer.Ordinal.Compare(row[idIndex - 1], previousId) < 0,
						"A dependency row is not strictly ordinal ascending.");
				}
			}
		}
	}

	private static LiquidSuppliedConfidentialDestination CreateDestination(
		ElementsPublicNetworkManifest manifest,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString("00140102030405060708090a0b0c0d0e0f1011121314"),
			LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex)));
		return LiquidSuppliedConfidentialDestination.Create(
			manifest,
			address,
			assetId,
			LiquidAssetAmount.Create(assetId, peggedAsset, atomicUnits),
			LiquidWalletLabelSet.Create(["funding-dependency-test"]));
	}

	private static ElementsExpectationBoundRawTransactionBatch CreateExpectationBoundBatch(
		ElementsPublicNetworkManifest manifest,
		IReadOnlyList<(string TransactionIdHex, byte[] TransactionBytes)> transactions)
	{
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
			"/funding-dependency-test:1/");
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
			manifest.PeggedAssetId,
			status,
			generation);
		var observations = new ElementsRawTransactionObservation[transactions.Count];
		for (int index = 0; index < observations.Length; index++)
		{
			(string transactionIdHex, byte[] transactionBytes) = transactions[index];
			observations[index] = new ElementsRawTransactionObservation(
				new ElementsRawTransactionRequest(transactionIdHex, null),
				transactionBytes);
		}

		return new ElementsExpectationBoundRawTransactionBatch(nodeObservation, observations);
	}

	private static void AssertFundingCompositionRejected(
		ElementsExpectationBoundRawTransactionBatch rawBatch,
		LiquidOrdinaryWalletExactSpendPlan plan,
		IReadOnlyList<IReadOnlyList<string>?> previousTransactionIdsBySelectedInput)
	{
		bool succeeded = rawBatch.TryCreateOrdinaryWalletPlanFundingBatch(
			plan,
			previousTransactionIdsBySelectedInput,
			out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		try
		{
			Assert.False(succeeded);
			Assert.Null(fundingBatch);
			Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, errorCode);
		}
		finally
		{
			fundingBatch?.Dispose();
		}
	}
}
