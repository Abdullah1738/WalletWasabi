using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Wire;
using Xunit;
using Xunit.Sdk;
using LiquidOrdinaryWalletPlanEncodedFrame = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanEncodedFrame;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;
using LiquidOrdinaryWalletPlanFundingRow = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingRow;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

/// <summary>
/// MANAGED-WLPQ-OFFLINE-PARITY-001: test-only offline validation-parity consumption harness.
/// Takes frames produced by the existing managed fenced encoder for existing managed plan-owner
/// values and proves byte identity and acceptance outcomes against the mirrored conformance-2
/// corpus and the native validation verdicts recorded in the accepted native CI evidence
/// (195-case native replay at native commit 0c8c751e: 42 encode, 99 decode, 28 reencode,
/// 26 prepare; frozen wln_wlpq_validate_v1 statuses 0, -1..-8, -9). This harness adds no
/// managed decoder, no native bridge, and no production surface; the corpus and the recorded
/// verdict set are consumed as inert checked-in reference authority only.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidOrdinaryWalletPlanWireV1ValidationParityTests
{
	private const string ManagedEncoderAcceptedOutputSha256 =
		"fa8cf8321c8de34a8d4d1f8c881b327bf3961520857e2480e5564ccff012153f";
	private const string EvidenceDirectoryName = "managed-wlpq-offline-parity";
	private const string EvidenceFileName = "evidence-v1.json";
	private const string EvidenceDigestFileName = "evidence-v1.json.sha256";

	[Fact]
	public void ManagedEncoderSharedFramesAreByteIdenticalToNativeAuthority()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
		(string[][] cases, string[][] models, string[][] frames, string[][] fixtures) = LoadCorpusTables();

		int sharedAccepted = 0;
		int sharedRejected = 0;
		int managedAccepted = 0;
		for (int index = 0; index < cases.Length; index++)
		{
			string[] corpusCase = cases[index];
			string partition = corpusCase[1];
			bool isShared = partition == "shared-encoder";
			bool isManagedAccepted = corpusCase[0] == "managed-encoder-accepted";
			if (!isShared && !isManagedAccepted)
			{
				continue;
			}

			Assert.Equal("encode", corpusCase[2]);
			Assert.Equal(isShared ? "managed+native" : "managed", corpusCase[3]);
			string[] model = FindUniqueRow(models, corpusCase[6], "source model");
			Assert.Equal(partition, model[1]);
			Assert.Equal("encode", model[2]);
			Assert.Equal(corpusCase[9], model[7]);
			Assert.Equal(corpusCase[10], model[8]);
			string json = ReadBoundCanonicalText(
				ResolveCorpusLeaf($"vectors/{model[4]}", "source-models/", ".json"),
				ParseCanonicalUnsigned(model[5]),
				model[6]);
			using JsonDocument document = ParseCanonicalJson(json);
			JsonElement root = document.RootElement.GetProperty("root");
			string kind = RequireJsonString(root, "kind");
			Assert.True(
				kind == "request-from-frame" || kind == "encoder-call-from-frame",
				"Shared encoder source model kind is not reviewed.");
			string frameId = RequireJsonString(root, "frame_id");
			if (corpusCase[4] == "concrete-frame")
			{
				Assert.Equal(frameId, corpusCase[5]);
			}
			else
			{
				Assert.Equal("symbolic-only", corpusCase[4]);
				Assert.Equal("-", corpusCase[5]);
			}

			(byte[] frame, byte[] epoch, LiquidOrdinaryWalletExactSpendPlan plan, List<byte[]?> candidates, List<List<byte[]?>?> previousRows) =
				LoadManagedFrame(frames, frameId);
			LiquidOrdinaryWalletPlanFundingBatch? batch = null;
			LiquidOrdinaryWalletPlanEncodedFrame? encoded = null;
			byte[]? encodedCopy = null;
			var sourceRows = new List<LiquidOrdinaryWalletPlanFundingRow?>();
			try
			{
				ApplySharedEncoderOperations(document.RootElement.GetProperty("operations"), ref epoch);
				Assert.Equal(corpusCase[7], LowerHex(epoch));

				for (int rowIndex = 0; rowIndex < candidates.Count; rowIndex++)
				{
					bool rowAccepted = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
						candidates[rowIndex],
						previousRows[rowIndex],
						out LiquidOrdinaryWalletPlanFundingRow? sourceRow,
						out LiquidOrdinaryWalletPlanWireErrorCode rowCode);
					Assert.True(rowAccepted);
					Assert.NotNull(sourceRow);
					Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.None, rowCode);
					sourceRows.Add(sourceRow);
				}

				Assert.True(
					LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
						plan,
						sourceRows,
						out batch,
						out LiquidOrdinaryWalletPlanWireErrorCode batchCode));
				Assert.NotNull(batch);
				Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.None, batchCode);

				bool accepted = LiquidOrdinaryWalletPlanEncoder.TryEncode(epoch, plan, batch, out encoded, out LiquidOrdinaryWalletPlanWireErrorCode code);

				// Acceptance-outcome parity with the recorded native verdict.
				Assert.Equal(corpusCase[9] == "ok", accepted);
				Assert.Equal(checked((uint)ParseCanonicalUnsigned(corpusCase[10])), (uint)code);
				Assert.Equal(accepted, encoded is not null);
				Assert.Equal(corpusCase[9], model[7]);
				Assert.Equal(corpusCase[10], model[8]);

				if (accepted)
				{
					// Byte identity against the mirrored corpus frame and both recorded digests.
					Assert.NotNull(encoded);
					encodedCopy = new byte[encoded!.Length];
					encoded.CopyFrameTo(encodedCopy);
					Assert.Equal(frame, encodedCopy);
					string producedSha256 = LowerHex(SHA256.HashData(encodedCopy));
					string[] frameMetadata = FindUniqueRow(frames, frameId, "frame");
					Assert.Equal(frameMetadata[4], producedSha256);
					Assert.Equal(corpusCase[15], producedSha256);
					// Native validation status 0 semantics: the recorded native decode verdict
					// for this exact frame under its own source epoch is ok, and the recorded
					// reencode binding is identity.
					string[] frameRow = FindUniqueRow(frames, frameId, "frame");
					string[] decodeCase = FindUniqueDecodeCase(cases, frameId, frameRow[9]);
					Assert.Equal("ok", decodeCase[9]);
					Assert.Equal("0", decodeCase[10]);
					string[] reencodeCase = FindUniqueFrameCase(cases, "native-reencode", frameId);
					Assert.Equal("ok", reencodeCase[9]);
					Assert.Equal("0", reencodeCase[10]);
					Assert.Equal(frameId, reencodeCase[11]);
					if (isManagedAccepted)
					{
						Assert.Equal(ManagedEncoderAcceptedOutputSha256, producedSha256);
						managedAccepted++;
					}
					else
					{
						sharedAccepted++;
					}
				}
				else
				{
					Assert.Equal("error", corpusCase[9]);
					Assert.Equal("1", corpusCase[10]);
					Assert.Equal("invalid-argument", corpusCase[12]);
					Assert.Equal("-", corpusCase[15]);
					sharedRejected++;
				}
			}
			finally
			{
				encoded?.Dispose();
				ClearBytes(encodedCopy);
				batch?.Dispose();
				for (int rowIndex = sourceRows.Count - 1; rowIndex >= 0; rowIndex--)
				{
					sourceRows[rowIndex]?.Dispose();
				}
				sourceRows.Clear();
				ClearManagedMaterialization(frame, epoch, candidates, previousRows);
			}
		}

		Assert.Equal(7, sharedAccepted);
		Assert.Equal(1, sharedRejected);
		Assert.Equal(1, managedAccepted);
	}

	[Fact]
	public void NativeValidationVerdictSetMapsExactlyIntoManagedWireFamily()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
		(string[][] cases, string[][] models, string[][] frames, string[][] fixtures) = LoadCorpusTables();
		Assert.Equal(227, cases.Length);

		// The managed wire error family is exactly codes 0..8, mirroring the frozen native
		// negated WLPQ error statuses -1..-8; native status 0 is acceptance with byte identity
		// after native decode/re-encode; native status -9 (panic / decode-re-encode mismatch)
		// has no managed code and no recorded corpus verdict.
		string[][] errorMapping = ParseErrorMappingTable(
			ReadStrictText(ResolveCorpusRootLeaf("ERROR_MAPPING_V1.tsv", ".tsv")));
		Assert.Equal(8, errorMapping.Length);
		for (int index = 0; index < errorMapping.Length; index++)
		{
			uint code = checked((uint)ParseCanonicalUnsigned(errorMapping[index][0]));
			Assert.Equal((uint)(index + 1), code);
			var managedCode = (LiquidOrdinaryWalletPlanWireErrorCode)code;
			Assert.True(Enum.IsDefined(managedCode));
			Assert.Equal(errorMapping[index][1], managedCode.ToString());
			Assert.Equal(errorMapping[index][2], managedCode.GetMessage());
		}
		Assert.False(Enum.IsDefined((LiquidOrdinaryWalletPlanWireErrorCode)9));

		var decodeAcceptedFrames = new List<string>();
		var reencodeIdentityFrames = new List<string>();
		var structuralAcceptedFrames = new List<string>();
		var verdictHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
		int accepted = 0;
		int rejected = 0;
		int lifecycle = 0;
		for (int index = 0; index < cases.Length; index++)
		{
			string[] corpusCase = cases[index];
			Assert.Equal(17, corpusCase.Length);
			string partition = corpusCase[1];
			Assert.True(
				partition is "managed-funding-row" or "managed-funding-batch" or "managed-encoder" or
					"native-decoder" or "native-prepare" or "native-raw-encoder" or "native-reencode" or
					"shared-encoder",
				"Case partition is not reviewed.");
			string result = corpusCase[9];
			uint code = checked((uint)ParseCanonicalUnsigned(corpusCase[10]));
			Assert.True(code <= 8, "Recorded verdict escapes the frozen native negated error family.");
			string verdictKey = $"{partition}|{result}|{code}";
			verdictHistogram[verdictKey] = verdictHistogram.TryGetValue(verdictKey, out int count) ? count + 1 : 1;

			switch (result)
			{
				case "ok":
					Assert.Equal(0u, code);
					accepted++;
					break;
				case "error":
					Assert.True(code is >= 1 and <= 8);
					rejected++;
					break;
				case "lifecycle":
					// Managed-only lifecycle surface: no native validation status exists; the
					// recorded code is 0 and the managed surface throws before producing a verdict.
					Assert.Equal(0u, code);
					Assert.StartsWith("managed-", partition, StringComparison.Ordinal);
					lifecycle++;
					break;
				default:
					throw new XunitException("Recorded verdict result is not reviewed.");
			}

			// A recorded native validation acceptance (status 0) requires the decode ok verdict
			// and the success-only reencode identity binding on the same frame.
			if (partition == "native-decoder" && result == "ok")
			{
				Assert.Equal("decode", corpusCase[2]);
				Assert.Equal("native", corpusCase[3]);
				Assert.Equal("concrete-frame", corpusCase[4]);
				decodeAcceptedFrames.Add(corpusCase[5]);
			}
			if (partition == "native-reencode")
			{
				Assert.Equal("ok", result);
				Assert.Equal(0u, code);
				Assert.Equal("concrete-frame", corpusCase[4]);
				Assert.Equal(corpusCase[5], corpusCase[11]);
				reencodeIdentityFrames.Add(corpusCase[5]);
			}
			if (result == "error")
			{
				Assert.Equal("-", corpusCase[11]);
			}
		}

		for (int index = 0; index < frames.Length; index++)
		{
			if (frames[index][5] == "ok")
			{
				Assert.Equal("0", frames[index][6]);
				structuralAcceptedFrames.Add(frames[index][0]);
			}
			else
			{
				Assert.Equal("error", frames[index][5]);
				uint structuralCode = checked((uint)ParseCanonicalUnsigned(frames[index][6]));
				Assert.True(structuralCode is >= 1 and <= 8);
			}
		}

		decodeAcceptedFrames.Sort(StringComparer.Ordinal);
		reencodeIdentityFrames.Sort(StringComparer.Ordinal);
		structuralAcceptedFrames.Sort(StringComparer.Ordinal);
		Assert.Equal(28, decodeAcceptedFrames.Count);
		Assert.Equal(decodeAcceptedFrames, reencodeIdentityFrames);
		Assert.Equal(decodeAcceptedFrames, structuralAcceptedFrames);

		// The managed encoder's produced frames are a subset of the native-accepted set: every
		// shared-encoder ok case binds a frame the native side accepted and re-encoded identically.
		for (int index = 0; index < cases.Length; index++)
		{
			string[] corpusCase = cases[index];
			if (corpusCase[1] == "shared-encoder" && corpusCase[9] == "ok")
			{
				Assert.Contains(corpusCase[5], decodeAcceptedFrames);
			}
		}

		Assert.Equal(69, accepted);
		Assert.Equal(155, rejected);
		Assert.Equal(3, lifecycle);
		Assert.Equal(227, accepted + rejected + lifecycle);
	}

	[Fact]
	public void RevisionTwoRowVersusBatchCodeFourAliasDistinctionHolds()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
		(string[][] cases, string[][] models, string[][] frames, string[][] fixtures) = LoadCorpusTables();

		string[][] boundaries = ParseCanonicalTable(
			ReadStrictText(ResolveCorpusLeaf("vectors/BOUNDARIES_V1.tsv", string.Empty, ".tsv")),
			[
				"boundary_id", "source_model_id", "operation", "boundary_kind", "production_constant",
				"numeric_domain", "formula", "execution_class", "expected_status", "expected_value",
				"expected_error_code", "coverage",
			]);

		// The designated V2 count model is the unique v2 source object in the packet.
		string[] countModel = FindUniqueRow(models, "model-managed-batch-expanded-count-plus-one", "source model");
		Assert.Equal("managed-funding-batch", countModel[1]);
		string countJson = ReadBoundCanonicalText(
			ResolveCorpusLeaf($"vectors/{countModel[4]}", "source-models/", ".json"),
			ParseCanonicalUnsigned(countModel[5]),
			countModel[6]);
		using JsonDocument countDocument = ParseCanonicalJson(countJson);
		Assert.Equal("wlpq-source-object-v2", RequireJsonString(countDocument.RootElement, "schema"));
		for (int index = 0; index < models.Length; index++)
		{
			if (!StringComparer.Ordinal.Equals(models[index][0], countModel[0]))
			{
				string modelJson = ReadBoundCanonicalText(
					ResolveCorpusLeaf($"vectors/{models[index][4]}", "source-models/", ".json"),
					ParseCanonicalUnsigned(models[index][5]),
					models[index][6]);
				Assert.DoesNotContain("\"schema\":\"wlpq-source-object-v2\"", modelJson);
			}
		}

		string[] countCase = FindUniqueRow(cases, "managed-batch-expanded-count-plus-one", "case");
		Assert.Equal("model-managed-batch-expanded-count-plus-one", countCase[6]);
		Assert.Equal("error", countCase[9]);
		Assert.Equal("4", countCase[10]);
		Assert.Equal("limit", countCase[12]);
		Assert.Equal("expanded-count-plus-one", countCase[13]);

		string[] bytesCase = FindUniqueRow(cases, "managed-batch-expanded-bytes-plus-one", "case");
		Assert.Equal("error", bytesCase[9]);
		Assert.Equal("4", bytesCase[10]);
		Assert.Equal("limit", bytesCase[12]);
		Assert.Equal("expanded-bytes-plus-one", bytesCase[13]);

		// The recorded batch-level code 4 is an aggregate-boundary verdict: replaying the V2 count
		// model through the managed surface must leave every individual row valid and fail only at
		// batch creation, while the row-level alias (one row over the per-row count limit) must
		// fail at row creation before any batch verdict exists. A row-level code 4 cannot satisfy
		// the expected batch-level code 4.
		(byte[] frame, byte[] epoch, LiquidOrdinaryWalletExactSpendPlan plan, List<byte[]?> candidates, List<List<byte[]?>?> previousRows) =
			LoadManagedFrame(frames, "frame-test-toy-ordered");
		try
		{
			Assert.Equal(2, candidates.Count);
			Assert.Equal(2, previousRows.Count);

			// Batch-level count boundary: 8192 + 8193 = 16385 expanded previous entries.
			var batchCandidates = new List<byte[]?> { CloneBytes(candidates[0]), CloneBytes(candidates[1]) };
			var batchPrevious = new List<List<byte[]?>?>
			{
				CreateIndexedPreviousList(8192),
				CreateIndexedPreviousList(8193),
			};
			try
			{
				var rows = new List<LiquidOrdinaryWalletPlanFundingRow?>();
				LiquidOrdinaryWalletPlanFundingBatch? batch = null;
				try
				{
					for (int rowIndex = 0; rowIndex < batchCandidates.Count; rowIndex++)
					{
						bool rowAccepted = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
							batchCandidates[rowIndex],
							batchPrevious[rowIndex],
							out LiquidOrdinaryWalletPlanFundingRow? row,
							out LiquidOrdinaryWalletPlanWireErrorCode rowCode);
						Assert.True(rowAccepted, "Batch-level count boundary row must stay individually valid.");
						Assert.NotNull(row);
						Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.None, rowCode);
						rows.Add(row);
					}

					bool batchAccepted = LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
						plan,
						rows,
						out batch,
						out LiquidOrdinaryWalletPlanWireErrorCode batchCode);
					Assert.False(batchAccepted);
					Assert.Null(batch);
					Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded, batchCode);
				}
				finally
				{
					batch?.Dispose();
					for (int rowIndex = rows.Count - 1; rowIndex >= 0; rowIndex--)
					{
						rows[rowIndex]?.Dispose();
					}
				}
			}
			finally
			{
				ClearManagedMaterialization([], [], batchCandidates, batchPrevious);
			}

			// Row-level alias: a single row over the per-row previous-count limit fails at row
			// creation; no batch verdict is reachable.
			var aliasCandidates = new List<byte[]?> { CloneBytes(candidates[0]) };
			var aliasPrevious = new List<List<byte[]?>?> { CreateIndexedPreviousList(16385) };
			LiquidOrdinaryWalletPlanFundingRow? aliasRow = null;
			try
			{
				bool aliasAccepted = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
					aliasCandidates[0],
					aliasPrevious[0],
					out aliasRow,
					out LiquidOrdinaryWalletPlanWireErrorCode aliasCode);
				Assert.False(aliasAccepted);
				Assert.Null(aliasRow);
				Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded, aliasCode);
			}
			finally
			{
				aliasRow?.Dispose();
				ClearManagedMaterialization([], [], aliasCandidates, aliasPrevious);
			}
		}
		finally
		{
			ClearManagedMaterialization(frame, epoch, candidates, previousRows);
		}

		// The recorded native decode boundary authority pins the same aggregate limits, and the
		// row-level and aggregate-level count boundaries are distinct rows with distinct verdicts.
		string[] aggregateCountBoundary = FindUniqueRow(boundaries, "aggregate-previous-entries-plus-one", "boundary");
		Assert.Equal("aggregate-previous-count", aggregateCountBoundary[3]);
		Assert.Equal("error", aggregateCountBoundary[8]);
		Assert.Equal("16385", aggregateCountBoundary[9]);
		Assert.Equal("4", aggregateCountBoundary[10]);
		string[] rowCountBoundary = FindUniqueRow(boundaries, "row-previous-entries-plus-one", "boundary");
		Assert.Equal("row-previous-count", rowCountBoundary[3]);
		Assert.Equal("error", rowCountBoundary[8]);
		Assert.Equal("16385", rowCountBoundary[9]);
		Assert.Equal("4", rowCountBoundary[10]);
		Assert.NotEqual(aggregateCountBoundary[3], rowCountBoundary[3]);
		string[] bytesBoundary = FindUniqueRow(boundaries, "expanded-transaction-bytes-plus-one", "boundary");
		Assert.Equal("error", bytesBoundary[8]);
		Assert.Equal("67108865", bytesBoundary[9]);
		Assert.Equal("4", bytesBoundary[10]);
		Assert.Equal(LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount, 16_384);
		Assert.Equal(LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength, 67_108_864);
	}

	[Fact]
	public void HarnessEvidenceIsRecordedUnderIgnoredTmpLocation()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
		(string[][] cases, string[][] models, string[][] frames, string[][] fixtures) = LoadCorpusTables();

		var partitionCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
		var verdictCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
		for (int index = 0; index < cases.Length; index++)
		{
			string partition = cases[index][1];
			partitionCounts[partition] = partitionCounts.TryGetValue(partition, out int count) ? count + 1 : 1;
			string verdict = $"{cases[index][9]}:{cases[index][10]}";
			verdictCounts[verdict] = verdictCounts.TryGetValue(verdict, out int seen) ? seen + 1 : 1;
		}

		string casesTablePath = ResolveCorpusLeaf("vectors/CASES_V1.tsv", string.Empty, ".tsv");
		string framesTablePath = ResolveCorpusLeaf("vectors/FRAMES_V1.tsv", string.Empty, ".tsv");
		string casesDigest = LowerHex(SHA256.HashData(File.ReadAllBytes(casesTablePath)));
		string framesDigest = LowerHex(SHA256.HashData(File.ReadAllBytes(framesTablePath)));

		var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal)
		{
			["slice"] = "MANAGED-WLPQ-OFFLINE-PARITY-001",
			["corpus_id"] = OrdinaryWalletPlanWireV1Corpus.CorpusId,
			["corpus_parent_root_sha256"] = OrdinaryWalletPlanWireV1Corpus.ParentRootSha256,
			["corpus_nested_root_sha256"] = OrdinaryWalletPlanWireV1Corpus.NestedRootSha256,
			["corpus_file_count"] = OrdinaryWalletPlanWireV1Corpus.FileCount,
			["cases_table_sha256"] = casesDigest,
			["frames_table_sha256"] = framesDigest,
			["case_count"] = cases.Length,
			["source_model_count"] = models.Length,
			["frame_count"] = frames.Length,
			["fixture_count"] = fixtures.Length,
			["partition_counts"] = partitionCounts,
			["verdict_counts"] = verdictCounts,
			["native_validation_status_family"] = "0 accepted-and-byte-identical; -1..-8 negated WLPQ error codes; -9 panic/decode-re-encode mismatch",
			["managed_wire_error_family"] = "codes 0..8 mirror native statuses 0 and -1..-8",
			["managed_encoder_accepted_output_sha256"] = ManagedEncoderAcceptedOutputSha256,
		};
		string json = JsonSerializer.Serialize(evidence) + "\n";
		string evidenceDigest = LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

		string evidenceRoot = Path.Combine(GetRepositoryRoot(), "tmp", EvidenceDirectoryName);
		Directory.CreateDirectory(evidenceRoot);
		string evidencePath = Path.Combine(evidenceRoot, EvidenceFileName);
		string digestPath = Path.Combine(evidenceRoot, EvidenceDigestFileName);
		File.WriteAllText(evidencePath, json, new UTF8Encoding(false));
		File.WriteAllText(digestPath, evidenceDigest + "  " + EvidenceFileName + "\n", new UTF8Encoding(false));

		// The recorded evidence is inert and exactly re-readable.
		string reread = File.ReadAllText(evidencePath, new UTF8Encoding(false));
		Assert.Equal(json, reread);
		Assert.Equal(evidenceDigest, LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(reread))));
		string rereadDigest = File.ReadAllText(digestPath, new UTF8Encoding(false));
		Assert.Equal(evidenceDigest + "  " + EvidenceFileName + "\n", rereadDigest);
		string repositoryRoot = GetRepositoryRoot();
		string relativeEvidencePath = evidencePath[(repositoryRoot.Length + 1)..];
		Assert.Equal(
			Path.Combine("tmp", EvidenceDirectoryName, EvidenceFileName),
			relativeEvidencePath);
	}

	private static string GetRepositoryRoot([CallerFilePath] string testFilePath = "") =>
		Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(testFilePath)!,
			"../../../../.."));

	private static (string[][] Cases, string[][] Models, string[][] Frames, string[][] Fixtures) LoadCorpusTables()
	{
		string[][] cases = ParseCanonicalTable(
			ReadStrictText(ResolveCorpusLeaf("vectors/CASES_V1.tsv", string.Empty, ".tsv")),
			[
				"case_id", "partition", "operation", "implementation", "execution_class", "frame_id",
				"source_model_id", "expected_source_epoch_hex", "catalog_fixture_id", "expected_result",
				"expected_error_code", "expected_reencode_frame_id", "combined_precedence", "coverage_tags",
				"input_identity_sha256", "expected_output_sha256", "case_binding_sha256",
			]);
		string[][] models = ParseCanonicalTable(
			ReadStrictText(ResolveCorpusLeaf("vectors/SOURCE_MODELS_V1.tsv", string.Empty, ".tsv")),
			[
				"source_model_id", "partition", "operation", "execution_class", "relative_path",
				"decoded_length", "decoded_sha256", "expected_result", "expected_error_code", "precedence",
			]);
		string[][] frames = ParseCanonicalTable(
			ReadStrictText(ResolveCorpusLeaf("vectors/FRAMES_V1.tsv", string.Empty, ".tsv")),
			[
				"frame_id", "execution_class", "relative_path", "decoded_length", "decoded_sha256",
				"structural_result", "structural_error_code", "parent_frame_id", "mutation_id",
				"source_epoch_hex", "source_revision", "manifest_id_hex", "pegged_asset_consensus_hex",
				"selected_count", "destination_count", "aggregate_previous_count", "fee_value",
				"selected_txids_consensus_hex", "selected_txids_display_hex",
				"destination_assets_consensus_hex", "destination_addresses_hex", "payload_hash_manifest",
			]);
		string[][] fixtures = ParseCanonicalTable(
			ReadStrictText(ResolveCorpusLeaf("vectors/FIXTURES_V1.tsv", string.Empty, ".tsv")),
			[
				"fixture_id", "fixture_kind", "network", "relative_path", "decoded_length",
				"decoded_sha256", "txid_consensus_hex", "txid_display_hex", "public_property",
			]);
		Assert.Equal(227, cases.Length);
		Assert.Equal(119, models.Length);
		Assert.Equal(86, frames.Length);
		Assert.Equal(11, fixtures.Length);
		return (cases, models, frames, fixtures);
	}

	private static void ApplySharedEncoderOperations(JsonElement operations, ref byte[] epoch)
	{
		Assert.Equal(JsonValueKind.Array, operations.ValueKind);
		foreach (JsonElement operation in operations.EnumerateArray())
		{
			string name = RequireJsonString(operation, "op");
			string path = RequireJsonString(operation, "path");
			Assert.Equal("set-bytes", name);
			Assert.Equal("request.source_epoch", path);
			AssertExactProperties(operation, ["op", "path", "value"]);
			JsonElement value = operation.GetProperty("value");
			AssertExactProperties(value, ["byte_hex", "kind", "length"]);
			Assert.Equal("repeat", RequireJsonString(value, "kind"));
			byte[] repeated = DecodeLowerHex(RequireJsonString(value, "byte_hex"));
			Assert.Single(repeated);
			byte fill = repeated[0];
			CryptographicOperations.ZeroMemory(repeated);
			int length = checked((int)RequireJsonUnsigned(value, "length"));
			Assert.Equal(LiquidOrdinaryWalletPlanWireLimits.SourceEpochLength, length);
			byte[] replacement = new byte[length];
			replacement.AsSpan().Fill(fill);
			Assert.False(epoch.AsSpan().SequenceEqual(replacement));
			CryptographicOperations.ZeroMemory(epoch);
			epoch = replacement;
		}
	}

	private static List<byte[]?> CreateIndexedPreviousList(int length)
	{
		Assert.True(length >= 0);
		var result = new List<byte[]?>(length);
		for (int index = 0; index < length; index++)
		{
			byte[] item = new byte[sizeof(uint)];
			BinaryPrimitives.WriteUInt32BigEndian(item, checked((uint)index));
			result.Add(item);
		}
		return result;
	}

	private static (byte[] Frame, byte[] Epoch, LiquidOrdinaryWalletExactSpendPlan Plan, List<byte[]?> Candidates, List<List<byte[]?>?> Previous) LoadManagedFrame(
		string[][] frames,
		string frameId)
	{
		string[] metadata = FindUniqueRow(frames, frameId, "frame");
		Assert.Equal("concrete-frame", metadata[1]);
		Assert.Equal("ok", metadata[5]);
		Assert.Equal("0", metadata[6]);
		Assert.Equal("-", metadata[7]);
		Assert.Equal("-", metadata[8]);
		string path = ResolveCorpusLeaf($"vectors/{metadata[2]}", "frames/", ".hex");
		string hex = ReadStrictText(path);
		byte[] frame = DecodeLowerHex(hex[..^1]);
		Assert.Equal(ParseCanonicalUnsigned(metadata[3]), checked((ulong)frame.LongLength));
		AssertLowerSha256(metadata[4]);
		Assert.Equal(metadata[4], LowerHex(SHA256.HashData(frame)));

		int cursor = 0;
		Assert.Equal("574c5051", LowerHex(ReadFrameBytes(frame, ref cursor, 4)));
		Assert.Equal((ushort)1, ReadFrameUInt16(frame, ref cursor));
		Assert.Equal((ushort)152, ReadFrameUInt16(frame, ref cursor));
		Assert.Equal(checked((ulong)frame.LongLength), ReadFrameUInt64(frame, ref cursor));
		Assert.Equal(0u, ReadFrameUInt32(frame, ref cursor));
		Assert.Equal(0u, ReadFrameUInt32(frame, ref cursor));
		byte[] epoch = ReadFrameBytes(frame, ref cursor, 32);
		ulong revision = ReadFrameUInt64(frame, ref cursor);
		byte[] manifestBytes = ReadFrameBytes(frame, ref cursor, 32);
		byte[] peggedBytes = ReadFrameBytes(frame, ref cursor, 32);
		uint selectedCount = ReadFrameUInt32(frame, ref cursor);
		uint destinationCount = ReadFrameUInt32(frame, ref cursor);
		uint aggregatePreviousCount = ReadFrameUInt32(frame, ref cursor);
		Assert.Equal(0u, ReadFrameUInt32(frame, ref cursor));
		ulong feeValue = ReadFrameUInt64(frame, ref cursor);

		Assert.Equal(metadata[9], LowerHex(epoch));
		Assert.Equal(ParseCanonicalUnsigned(metadata[10]), revision);
		Assert.Equal(metadata[11], LowerHex(manifestBytes));
		Assert.Equal(metadata[12], LowerHex(peggedBytes));
		Assert.Equal(ParseCanonicalUnsigned(metadata[13]), selectedCount);
		Assert.Equal(ParseCanonicalUnsigned(metadata[14]), destinationCount);
		Assert.Equal(ParseCanonicalUnsigned(metadata[15]), aggregatePreviousCount);
		Assert.Equal(ParseCanonicalUnsigned(metadata[16]), feeValue);

		ElementsPublicNetworkManifest manifest = GetManifest(manifestBytes);
		LiquidAssetId peggedAssetId = LiquidAssetId.ParseConsensusBytes(peggedBytes);
		Assert.Equal(manifest.PeggedAssetId, peggedAssetId.CanonicalRpcHex);
		var entries = new List<LiquidWalletCoinControlEntry>(checked((int)selectedCount));
		var candidates = new List<byte[]?>(checked((int)selectedCount));
		var previousRows = new List<List<byte[]?>?>(checked((int)selectedCount));
		var selectedConsensus = new List<string>(checked((int)selectedCount));
		var selectedDisplay = new List<string>(checked((int)selectedCount));
		var candidateHashes = new List<string>(checked((int)selectedCount));
		var previousHashManifest = new List<string>(checked((int)selectedCount));
		ulong observedPrevious = 0;
		for (uint selectedIndex = 0; selectedIndex < selectedCount; selectedIndex++)
		{
			byte[] txidBytes = ReadFrameBytes(frame, ref cursor, 32);
			uint outputIndex = ReadFrameUInt32(frame, ref cursor);
			byte[] assetBytes = ReadFrameBytes(frame, ref cursor, 32);
			ulong value = ReadFrameUInt64(frame, ref cursor);
			uint candidateLength = ReadFrameUInt32(frame, ref cursor);
			uint previousCount = ReadFrameUInt32(frame, ref cursor);
			Assert.Equal(0u, ReadFrameUInt32(frame, ref cursor));
			byte[] candidate = ReadFrameBytes(frame, ref cursor, checked((int)candidateLength));
			var previous = new List<byte[]?>(checked((int)previousCount));
			var previousHashes = new List<string>(checked((int)previousCount));
			for (uint previousIndex = 0; previousIndex < previousCount; previousIndex++)
			{
				uint previousLength = ReadFrameUInt32(frame, ref cursor);
				byte[] transaction = ReadFrameBytes(frame, ref cursor, checked((int)previousLength));
				previous.Add(transaction);
				previousHashes.Add(LowerHex(SHA256.HashData(transaction)));
			}
			observedPrevious = checked(observedPrevious + previousCount);
			LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(txidBytes);
			LiquidAssetId assetId = LiquidAssetId.ParseConsensusBytes(assetBytes);
			entries.Add(
				LiquidWalletCoinControlEntry.Create(
					LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
					LiquidAssetAmount.Create(assetId, peggedAssetId, checked((long)value)),
					peggedAssetId,
					null));
			candidates.Add(candidate);
			previousRows.Add(previous);
			selectedConsensus.Add(LowerHex(txidBytes));
			selectedDisplay.Add(transactionId.CanonicalRpcHex);
			candidateHashes.Add(LowerHex(SHA256.HashData(candidate)));
			previousHashManifest.Add(previousHashes.Count == 0 ? "-" : string.Join(',', previousHashes));
			CryptographicOperations.ZeroMemory(txidBytes);
			CryptographicOperations.ZeroMemory(assetBytes);
		}
		Assert.Equal(aggregatePreviousCount, observedPrevious);

		var destinations = new List<LiquidSuppliedConfidentialDestination>(checked((int)destinationCount));
		var destinationAssets = new List<string>(checked((int)destinationCount));
		var destinationAddresses = new List<string>(checked((int)destinationCount));
		for (uint destinationIndex = 0; destinationIndex < destinationCount; destinationIndex++)
		{
			byte[] assetBytes = ReadFrameBytes(frame, ref cursor, 32);
			ulong value = ReadFrameUInt64(frame, ref cursor);
			uint addressLength = ReadFrameUInt32(frame, ref cursor);
			Assert.Equal(0u, ReadFrameUInt32(frame, ref cursor));
			byte[] addressBytes = ReadFrameBytes(frame, ref cursor, checked((int)addressLength));
			for (int addressIndex = 0; addressIndex < addressBytes.Length; addressIndex++)
			{
				Assert.True(addressBytes[addressIndex] <= 0x7f, "Managed frame address is not ASCII.");
			}
			string addressText = Encoding.ASCII.GetString(addressBytes);
			Assert.Equal(addressBytes, Encoding.ASCII.GetBytes(addressText));
			LiquidAssetId assetId = LiquidAssetId.ParseConsensusBytes(assetBytes);
			destinations.Add(
				LiquidSuppliedConfidentialDestination.Create(
					manifest,
					LiquidAddress.Parse(manifest, addressText),
					assetId,
					LiquidAssetAmount.Create(assetId, peggedAssetId, checked((long)value)),
					LiquidWalletLabelSet.Empty));
			destinationAssets.Add(LowerHex(assetBytes));
			destinationAddresses.Add(LowerHex(addressBytes));
			CryptographicOperations.ZeroMemory(assetBytes);
			CryptographicOperations.ZeroMemory(addressBytes);
		}
		Assert.Equal(frame.Length, cursor);
		Assert.Equal(metadata[17], string.Join(',', selectedConsensus));
		Assert.Equal(metadata[18], string.Join(',', selectedDisplay));
		Assert.Equal(metadata[19], string.Join(',', destinationAssets));
		Assert.Equal(metadata[20], string.Join(',', destinationAddresses));
		Assert.Equal(metadata[21], $"{string.Join(',', candidateHashes)}/{string.Join(';', previousHashManifest)}");

		var selection = new LiquidWalletCoinControlSelection(peggedAssetId, revision, entries);
		LiquidOrdinaryWalletExactSpendPlan plan = LiquidOrdinaryWalletExactSpendPlan.Create(
			selection,
			LiquidSuppliedConfidentialDestinationBatch.Create(destinations),
			LiquidAssetAmount.Create(peggedAssetId, peggedAssetId, checked((long)feeValue)));
		CryptographicOperations.ZeroMemory(manifestBytes);
		CryptographicOperations.ZeroMemory(peggedBytes);
		return (frame, epoch, plan, candidates, previousRows);
	}

	private static ElementsPublicNetworkManifest GetManifest(byte[] manifestId)
	{
		string value = LowerHex(manifestId);
		if (StringComparer.Ordinal.Equals(value, ElementsPublicNetworkManifest.LiquidTestnet.ManifestId))
		{
			return ElementsPublicNetworkManifest.LiquidTestnet;
		}
		if (StringComparer.Ordinal.Equals(value, ElementsPublicNetworkManifest.LiquidMainnet.ManifestId))
		{
			return ElementsPublicNetworkManifest.LiquidMainnet;
		}
		throw new XunitException("Managed frame network manifest is not reviewed.");
	}

	private static string[] FindUniqueRow(string[][] rows, string identifier, string kind)
	{
		string[]? found = null;
		for (int index = 0; index < rows.Length; index++)
		{
			if (StringComparer.Ordinal.Equals(rows[index][0], identifier))
			{
				Assert.Null(found);
				found = rows[index];
			}
		}
		Assert.True(found is not null, $"Missing {kind}.");
		return found!;
	}

	private static string[] FindUniqueFrameCase(string[][] cases, string partition, string frameId)
	{
		string[]? found = null;
		for (int index = 0; index < cases.Length; index++)
		{
			if (StringComparer.Ordinal.Equals(cases[index][1], partition) &&
				StringComparer.Ordinal.Equals(cases[index][5], frameId))
			{
				Assert.Null(found);
				found = cases[index];
			}
		}
		Assert.True(found is not null, $"Missing {partition} case for frame {frameId}.");
		return found!;
	}

	private static string[] FindUniqueDecodeCase(string[][] cases, string frameId, string sourceEpochHex)
	{
		string[]? found = null;
		for (int index = 0; index < cases.Length; index++)
		{
			if (StringComparer.Ordinal.Equals(cases[index][1], "native-decoder") &&
				StringComparer.Ordinal.Equals(cases[index][5], frameId) &&
				StringComparer.Ordinal.Equals(cases[index][7], sourceEpochHex))
			{
				Assert.Null(found);
				found = cases[index];
			}
		}
		Assert.True(found is not null, $"Missing native-decoder case for frame {frameId} under its own source epoch.");
		return found!;
	}

	private static string[][] ParseErrorMappingTable(string text)
	{
		Assert.NotEmpty(text);
		Assert.Equal('\n', text[^1]);
		Assert.DoesNotContain('\r', text);
		string[] lines = text[..^1].Split('\n', StringSplitOptions.None);
		Assert.NotEmpty(lines);
		Assert.Equal(["code", "name", "text"], lines[0].Split('	', StringSplitOptions.None));
		var rows = new string[lines.Length - 1][];
		for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
		{
			Assert.NotEmpty(lines[lineIndex]);
			string[] fields = lines[lineIndex].Split('	', StringSplitOptions.None);
			Assert.Equal(3, fields.Length);
			Assert.Equal((uint)(lineIndex), checked((uint)ParseCanonicalUnsigned(fields[0])));
			Assert.NotEmpty(fields[1]);
			Assert.DoesNotContain('\0', fields[1]);
			Assert.NotEmpty(fields[2]);
			Assert.DoesNotContain('\0', fields[2]);
			rows[lineIndex - 1] = fields;
		}
		Assert.NotEmpty(rows);
		return rows;
	}

	private static string[][] ParseCanonicalTable(string text, string[] expectedHeader)
	{
		Assert.NotEmpty(text);
		Assert.Equal('\n', text[^1]);
		Assert.DoesNotContain('\r', text);
		string[] lines = text[..^1].Split('\n', StringSplitOptions.None);
		Assert.NotEmpty(lines);
		Assert.Equal(expectedHeader, lines[0].Split('\t', StringSplitOptions.None));
		var rows = new string[lines.Length - 1][];
		string? previous = null;
		for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
		{
			Assert.NotEmpty(lines[lineIndex]);
			string[] fields = lines[lineIndex].Split('\t', StringSplitOptions.None);
			Assert.Equal(expectedHeader.Length, fields.Length);
			for (int fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
			{
				Assert.NotEmpty(fields[fieldIndex]);
				Assert.DoesNotContain('\0', fields[fieldIndex]);
			}
			AssertCanonicalIdentifier(fields[0]);
			if (previous is not null)
			{
				Assert.True(StringComparer.Ordinal.Compare(previous, fields[0]) < 0, "Table identifiers are not strictly ordered.");
			}
			previous = fields[0];
			rows[lineIndex - 1] = fields;
		}
		Assert.NotEmpty(rows);
		return rows;
	}

	private static string ReadStrictText(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		Assert.NotEmpty(bytes);
		Assert.Equal((byte)'\n', bytes[^1]);
		Assert.DoesNotContain((byte)'\r', bytes);
		Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
		var utf8 = new UTF8Encoding(false, true);
		string text = utf8.GetString(bytes);
		Assert.Equal(bytes, utf8.GetBytes(text));
		return text;
	}

	private static string ReadBoundCanonicalText(string path, ulong expectedLength, string expectedSha256)
	{
		byte[] bytes = File.ReadAllBytes(path);
		Assert.Equal(expectedLength, checked((ulong)bytes.LongLength));
		AssertLowerSha256(expectedSha256);
		Assert.Equal(expectedSha256, LowerHex(SHA256.HashData(bytes)));
		Assert.NotEmpty(bytes);
		Assert.Equal((byte)'\n', bytes[^1]);
		Assert.DoesNotContain((byte)'\r', bytes);
		Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
		var utf8 = new UTF8Encoding(false, true);
		string text = utf8.GetString(bytes);
		Assert.Equal(bytes, utf8.GetBytes(text));
		return text;
	}

	private static string ResolveCorpusLeaf(string relativePath, string vectorsPrefix, string suffix)
	{
		Assert.NotEmpty(relativePath);
		Assert.DoesNotContain('\\', relativePath);
		Assert.DoesNotContain('\0', relativePath);
		Assert.False(Path.IsPathRooted(relativePath));
		if (vectorsPrefix.Length == 0)
		{
			Assert.StartsWith("vectors/", relativePath, StringComparison.Ordinal);
		}
		else
		{
			Assert.StartsWith($"vectors/{vectorsPrefix}", relativePath, StringComparison.Ordinal);
		}
		Assert.EndsWith(suffix, relativePath, StringComparison.Ordinal);
		string[] components = relativePath.Split('/', StringSplitOptions.None);
		for (int index = 0; index < components.Length; index++)
		{
			Assert.NotEmpty(components[index]);
			Assert.NotEqual(".", components[index]);
			Assert.NotEqual("..", components[index]);
		}
		string root = Path.GetFullPath(OrdinaryWalletPlanWireV1Corpus.RootPath);
		string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
		Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
		Assert.True(File.Exists(path));
		return path;
	}

	private static string ResolveCorpusRootLeaf(string relativePath, string suffix)
	{
		Assert.NotEmpty(relativePath);
		Assert.DoesNotContain('\\', relativePath);
		Assert.DoesNotContain('\0', relativePath);
		Assert.False(Path.IsPathRooted(relativePath));
		Assert.DoesNotContain('/', relativePath);
		Assert.EndsWith(suffix, relativePath, StringComparison.Ordinal);
		string root = Path.GetFullPath(OrdinaryWalletPlanWireV1Corpus.RootPath);
		string path = Path.GetFullPath(Path.Combine(root, relativePath));
		Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
		Assert.True(File.Exists(path));
		return path;
	}

	private static JsonDocument ParseCanonicalJson(string text)
	{
		Assert.NotEmpty(text);
		Assert.Equal('\n', text[^1]);
		Assert.DoesNotContain('\r', text);
		JsonDocument document = JsonDocument.Parse(
			text[..^1],
			new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
		try
		{
			AssertCanonicalJsonValue(document.RootElement);
			Assert.Equal(text, JsonSerializer.Serialize(document.RootElement) + "\n");
			return document;
		}
		catch
		{
			document.Dispose();
			throw;
		}
	}

	private static void AssertCanonicalJsonValue(JsonElement value)
	{
		switch (value.ValueKind)
		{
			case JsonValueKind.Object:
				string? previous = null;
				foreach (JsonProperty property in value.EnumerateObject())
				{
					if (previous is not null)
					{
						Assert.True(StringComparer.Ordinal.Compare(previous, property.Name) < 0, "JSON object keys are not unique and ordered.");
					}
					previous = property.Name;
					AssertCanonicalJsonValue(property.Value);
				}
				break;
			case JsonValueKind.Array:
				foreach (JsonElement element in value.EnumerateArray())
				{
					AssertCanonicalJsonValue(element);
				}
				break;
			case JsonValueKind.Number:
				Assert.True(value.TryGetUInt64(out ulong number));
				Assert.Equal(number.ToString(CultureInfo.InvariantCulture), value.GetRawText());
				break;
			case JsonValueKind.String:
			case JsonValueKind.Null:
			case JsonValueKind.True:
			case JsonValueKind.False:
				break;
			default:
				throw new XunitException("JSON value kind is not canonical.");
		}
	}

	private static void AssertExactProperties(JsonElement value, string[] names)
	{
		Assert.Equal(JsonValueKind.Object, value.ValueKind);
		int index = 0;
		foreach (JsonProperty property in value.EnumerateObject())
		{
			Assert.True(index < names.Length, "JSON object has extra properties.");
			Assert.Equal(names[index], property.Name);
			index++;
		}
		Assert.Equal(names.Length, index);
	}

	private static string RequireJsonString(JsonElement value, string property)
	{
		JsonElement item = value.GetProperty(property);
		Assert.Equal(JsonValueKind.String, item.ValueKind);
		string? result = item.GetString();
		Assert.NotNull(result);
		return result;
	}

	private static ulong RequireJsonUnsigned(JsonElement value, string property)
	{
		JsonElement item = value.GetProperty(property);
		Assert.Equal(JsonValueKind.Number, item.ValueKind);
		Assert.True(item.TryGetUInt64(out ulong result));
		return result;
	}

	private static ulong ParseCanonicalUnsigned(string value)
	{
		Assert.NotEmpty(value);
		Assert.True(value == "0" || value[0] is >= '1' and <= '9');
		for (int index = 0; index < value.Length; index++)
		{
			Assert.True(value[index] is >= '0' and <= '9');
		}
		Assert.True(ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result));
		Assert.Equal(value, result.ToString(CultureInfo.InvariantCulture));
		return result;
	}

	private static void AssertCanonicalIdentifier(string value)
	{
		Assert.NotEmpty(value);
		Assert.True(value[0] is >= 'a' and <= 'z');
		for (int index = 0; index < value.Length; index++)
		{
			char character = value[index];
			Assert.True(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
		}
	}

	private static void AssertLowerSha256(string value)
	{
		Assert.Equal(64, value.Length);
		for (int index = 0; index < value.Length; index++)
		{
			Assert.True(value[index] is >= '0' and <= '9' or >= 'a' and <= 'f');
		}
	}

	private static string LowerHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

	private static byte[] ReadFrameBytes(byte[] frame, ref int cursor, int length)
	{
		Assert.True(length >= 0);
		int end = checked(cursor + length);
		Assert.True(end <= frame.Length, "Managed frame is truncated.");
		byte[] result = frame.AsSpan(cursor, length).ToArray();
		cursor = end;
		return result;
	}

	private static ushort ReadFrameUInt16(byte[] frame, ref int cursor)
	{
		byte[] value = ReadFrameBytes(frame, ref cursor, sizeof(ushort));
		ushort result = BinaryPrimitives.ReadUInt16LittleEndian(value);
		CryptographicOperations.ZeroMemory(value);
		return result;
	}

	private static uint ReadFrameUInt32(byte[] frame, ref int cursor)
	{
		byte[] value = ReadFrameBytes(frame, ref cursor, sizeof(uint));
		uint result = BinaryPrimitives.ReadUInt32LittleEndian(value);
		CryptographicOperations.ZeroMemory(value);
		return result;
	}

	private static ulong ReadFrameUInt64(byte[] frame, ref int cursor)
	{
		byte[] value = ReadFrameBytes(frame, ref cursor, sizeof(ulong));
		ulong result = BinaryPrimitives.ReadUInt64LittleEndian(value);
		CryptographicOperations.ZeroMemory(value);
		return result;
	}

	private static byte[] DecodeLowerHex(string value)
	{
		Assert.Equal(0, value.Length % 2);
		for (int index = 0; index < value.Length; index++)
		{
			Assert.True(value[index] is >= '0' and <= '9' or >= 'a' and <= 'f');
		}
		return Convert.FromHexString(value);
	}

	private static void ClearManagedMaterialization(
		byte[] frame,
		byte[] epoch,
		List<byte[]?>? candidates,
		List<List<byte[]?>?>? previousRows)
	{
		ClearBytes(frame);
		ClearBytes(epoch);
		if (candidates is not null)
		{
			for (int index = 0; index < candidates.Count; index++)
			{
				ClearBytes(candidates[index]);
				candidates[index] = null;
			}
			candidates.Clear();
		}
		if (previousRows is not null)
		{
			for (int index = 0; index < previousRows.Count; index++)
			{
				ClearByteList(previousRows[index]);
				previousRows[index] = null;
			}
			previousRows.Clear();
		}
	}

	private static void ClearByteList(List<byte[]?>? values)
	{
		if (values is null)
		{
			return;
		}
		for (int index = 0; index < values.Count; index++)
		{
			ClearBytes(values[index]);
			values[index] = null;
		}
		values.Clear();
	}

	private static void ClearBytes(byte[]? value)
	{
		if (value is not null)
		{
			CryptographicOperations.ZeroMemory(value);
		}
	}

	private static byte[]? CloneBytes(byte[]? value) => value is null ? null : (byte[])value.Clone();
}
