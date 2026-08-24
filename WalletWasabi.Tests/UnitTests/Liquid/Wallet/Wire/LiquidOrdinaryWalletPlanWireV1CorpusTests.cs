using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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

[Collection("Serial unit tests collection")]
public class LiquidOrdinaryWalletPlanWireV1CorpusTests
{
	private const string FirstDigest = "0000000000000000000000000000000000000000000000000000000000000000";
	private const string SecondDigest = "1111111111111111111111111111111111111111111111111111111111111111";

	[Fact]
	public void ExactImportedCorpusIsClosedAndAuthentic()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
	}

	[Fact]
	public void InventoryRowsMustBeStrictlyIncreasingAndExactlyDelimited()
	{
		string reordered = $"{FirstDigest}  b\n{SecondDigest}  a\n";
		string duplicated = $"{FirstDigest}  a\n{SecondDigest}  a\n";
		string thirdSpace = $"{FirstDigest}   a\n";

		Assert.ThrowsAny<XunitException>(() => OrdinaryWalletPlanWireV1Corpus.ParseInventory(reordered));
		Assert.ThrowsAny<XunitException>(() => OrdinaryWalletPlanWireV1Corpus.ParseInventory(duplicated));
		Assert.ThrowsAny<XunitException>(() => OrdinaryWalletPlanWireV1Corpus.ParseInventory(thirdSpace));
	}

	[Fact]
	public void ProductionCorpusReplaysEveryManagedConstructionAndEncoderCase()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
		(string[][] cases, string[][] models, string[][] frames, string[][] fixtures) = LoadManagedReplayTables();
		AssertManagedSourceModelBijection(cases, models);
		AssertEncoderEpochBeforeIdentityAuthority(false);
		int rowCount = 0;
		int batchCount = 0;
		int encoderCount = 0;

		for (int index = 0; index < cases.Length; index++)
		{
			string[] corpusCase = cases[index];
			if (!IsManagedReplayPartition(corpusCase[1]))
			{
				continue;
			}

			Assert.Equal("managed", corpusCase[3]);
			Assert.Equal("symbolic-only", corpusCase[4]);
			Assert.Equal("-", corpusCase[5]);
			Assert.Equal("-", corpusCase[8]);
			Assert.Equal("-", corpusCase[11]);
			AssertCaseExpectation(corpusCase);
			string[] model = FindUniqueRow(models, corpusCase[6], "source model");
			Assert.Equal(corpusCase[1], model[1]);
			Assert.Equal(corpusCase[2], model[2]);
			Assert.Equal(corpusCase[4], model[3]);
			Assert.Equal(corpusCase[9], model[7]);
			Assert.Equal(corpusCase[10], model[8]);
			Assert.Equal(corpusCase[12], model[9]);
			Assert.Equal(model[6], corpusCase[14]);
			AssertCaseBinding(corpusCase);

			string json = ReadBoundCanonicalText(
				ResolveCorpusLeaf($"vectors/{model[4]}", "source-models/", ".json"),
				ParseCanonicalUnsigned(model[5]),
				model[6]);
			using JsonDocument document = ParseCanonicalJson(json);
			AssertSourceModelEnvelope(document.RootElement, corpusCase[6]);

			switch (corpusCase[1])
			{
				case "managed-funding-row":
					ReplayFundingRowCase(corpusCase, document.RootElement, frames, fixtures);
					rowCount++;
					break;
				case "managed-funding-batch":
					ReplayFundingBatchCase(corpusCase, document.RootElement, frames, fixtures);
					batchCount++;
					break;
				case "managed-encoder":
					ReplayEncoderCase(corpusCase, document.RootElement, frames, fixtures);
					encoderCount++;
					break;
				default:
					throw new XunitException("Managed replay partition escaped its closed dispatch.");
			}
		}

		Assert.Equal(12, rowCount);
		Assert.Equal(9, batchCount);
		Assert.Equal(11, encoderCount);
		Assert.Equal(32, rowCount + batchCount + encoderCount);
	}

	[Fact]
	public void ProductionCorpusReplayLoaderRejectsMutations()
	{
		(string[][] cases, string[][] models, string[][] frames, string[][] fixtures) = LoadManagedReplayTables();
		AssertManagedSourceModelBijection(cases, models);
		AssertEncoderEpochBeforeIdentityAuthority(true);
		string[] orphanModel = FindUniqueRow(models, "model-boundary-address-bytes-maximum", "source model");
		string orphanPartition = orphanModel[1];
		Assert.False(IsManagedReplayPartition(orphanPartition));
		orphanModel[1] = "managed-encoder";
		Assert.True(IsManagedReplayPartition(orphanModel[1]));
		AssertManagedSourceModelBijectionRejected(cases, models);
		orphanModel[1] = orphanPartition;
		string[] repartitionedModel = FindUniqueRow(models, "model-managed-batch-accepted", "source model");
		string managedPartition = repartitionedModel[1];
		Assert.Equal("managed-funding-batch", managedPartition);
		repartitionedModel[1] = "managed-encoder";
		AssertManagedSourceModelBijectionRejected(cases, models);
		repartitionedModel[1] = managedPartition;
		AssertManagedSourceModelBijection(cases, models);
		string[] countCase = FindUniqueRow(cases, "managed-batch-expanded-count-plus-one", "case");
		string[] countModel = FindUniqueRow(models, countCase[6], "source model");
		string countJson = ReadBoundCanonicalText(
			ResolveCorpusLeaf($"vectors/{countModel[4]}", "source-models/", ".json"),
			ParseCanonicalUnsigned(countModel[5]),
			countModel[6]);
		string countAlias = countJson.Replace(
			"\"length\":8192,\"op\":\"resize-list\",\"path\":\"batch.rows[0].previous\"",
			"\"length\":16385,\"op\":\"resize-list\",\"path\":\"batch.rows[0].previous\"",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, countAlias);
		AssertMutatedBatchFirstInvalidRowIsLimitExceeded(countCase, countAlias, frames, fixtures, 0, true);
		string unknownCountLength = countJson.Replace(
			"\"length\":8192,\"op\":\"resize-list\",\"path\":\"batch.rows[0].previous\"",
			"\"length\":16386,\"op\":\"resize-list\",\"path\":\"batch.rows[0].previous\"",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, unknownCountLength);
		AssertMutatedBatchRejected(countCase, unknownCountLength, frames, fixtures);
		string unboundedCountLength = countJson.Replace(
			"\"length\":8192,\"op\":\"resize-list\",\"path\":\"batch.rows[0].previous\"",
			"\"length\":2147483648,\"op\":\"resize-list\",\"path\":\"batch.rows[0].previous\"",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, unboundedCountLength);
		AssertMutatedBatchOverflowRejected(countCase, unboundedCountLength, frames, fixtures);

		string[] bytesCase = FindUniqueRow(cases, "managed-batch-expanded-bytes-plus-one", "case");
		string[] bytesModel = FindUniqueRow(models, bytesCase[6], "source model");
		string bytesJson = ReadBoundCanonicalText(
			ResolveCorpusLeaf($"vectors/{bytesModel[4]}", "source-models/", ".json"),
			ParseCanonicalUnsigned(bytesModel[5]),
			bytesModel[6]);
		string bytesAlias = bytesJson.Replace(
			"\"path\":\"batch.rows[1].candidate\",\"value\":{\"byte_hex\":\"00\",\"kind\":\"repeat\",\"length\":1}",
			"\"path\":\"batch.rows[1].candidate\",\"value\":{\"byte_hex\":\"00\",\"kind\":\"repeat\",\"length\":0}",
			StringComparison.Ordinal);
		Assert.NotEqual(bytesJson, bytesAlias);
		AssertMutatedBatchFirstInvalidRowIsLimitExceeded(bytesCase, bytesAlias, frames, fixtures, 1, false);

		AssertCanonicalJsonRejected("{\"schema\":\"wlpq-source-object-v1\", \"root\":{}}\n");
		AssertCanonicalJsonRejected("{\"root\":{},\"root\":{},\"schema\":\"wlpq-source-object-v1\"}\n");
		AssertCanonicalTableRejected("a\tb\r\n", ["a", "b"]);
		AssertCanonicalTableRejected("a\tb\nb\t1\na\t2\n", ["a", "b"]);
		AssertCorpusPathRejected("vectors/../CASES_V1.tsv", "source-models/", ".json");

		string schemaAlias = countJson.Replace(
			"\"schema\":\"wlpq-source-object-v2\"",
			"\"schema\":\"wlpq-source-object-v1\"",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, schemaAlias);
		using JsonDocument schemaAliasDocument = ParseCanonicalJson(schemaAlias);
		AssertSourceModelEnvelopeRejected(schemaAliasDocument.RootElement, countCase[6]);

		string[] v1IndexedCase = (string[])countCase.Clone();
		v1IndexedCase[6] = "model-managed-batch-expanded-bytes-plus-one";
		AssertMutatedBatchRejected(v1IndexedCase, schemaAlias, frames, fixtures);

		string extraField = countJson.Replace(
			"],\"root\":",
			"],\"rogue\":0,\"root\":",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, extraField);
		using JsonDocument extraFieldDocument = ParseCanonicalJson(extraField);
		AssertSourceModelEnvelopeRejected(extraFieldDocument.RootElement, countCase[6]);

		string unknownOperation = countJson.Replace(
			"\"op\":\"clear-list\"",
			"\"op\":\"unknown\"",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, unknownOperation);
		AssertMutatedBatchRejected(countCase, unknownOperation, frames, fixtures);

		string unknownPath = countJson.Replace(
			"batch.rows[0].previous",
			"batch.rows[2].previous",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, unknownPath);
		AssertMutatedBatchRejected(countCase, unknownPath, frames, fixtures);

		string indexedWrongPath = countJson.Replace(
			"\"length\":8192,\"op\":\"resize-list\",\"path\":\"batch.rows[0].previous\"",
			"\"length\":8192,\"op\":\"resize-list\",\"path\":\"batch.rows[0].candidate\"",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, indexedWrongPath);
		AssertMutatedBatchRejected(countCase, indexedWrongPath, frames, fixtures);

		string indexedExtra = countJson.Replace(
			"{\"kind\":\"indexed-u32be\"}",
			"{\"extra\":0,\"kind\":\"indexed-u32be\"}",
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, indexedExtra);
		AssertMutatedBatchRejected(countCase, indexedExtra, frames, fixtures);

		string indexedBeforeClear = countJson.Replace(
			"{\"op\":\"clear-list\",\"path\":\"batch.rows[0].previous\"},",
			string.Empty,
			StringComparison.Ordinal);
		Assert.NotEqual(countJson, indexedBeforeClear);
		AssertMutatedBatchRejected(countCase, indexedBeforeClear, frames, fixtures);

		string duplicateBytes = bytesJson.Replace(
			"\"path\":\"batch.rows[0].previous[2]\",\"value\":{\"byte_hex\":\"02\"",
			"\"path\":\"batch.rows[0].previous[2]\",\"value\":{\"byte_hex\":\"01\"",
			StringComparison.Ordinal);
		Assert.NotEqual(bytesJson, duplicateBytes);
		AssertMutatedBatchRejected(bytesCase, duplicateBytes, frames, fixtures);

		string noOpBytes = bytesJson.Replace(
			"\"path\":\"batch.rows[0].candidate\",\"value\":{\"byte_hex\":\"00\"",
			"\"path\":\"batch.rows[0].candidate\",\"value\":{\"byte_hex\":\"01\"",
			StringComparison.Ordinal);
		Assert.NotEqual(bytesJson, noOpBytes);
		AssertMutatedBatchRejected(bytesCase, noOpBytes, frames, fixtures);

		AssertBoundCanonicalTextRejected(
			ResolveCorpusLeaf($"vectors/{countModel[4]}", "source-models/", ".json"),
			ParseCanonicalUnsigned(countModel[5]),
			FirstDigest);

		string[] accepted = FindUniqueRow(cases, "managed-encoder-accepted", "case");
		string[] badOutput = (string[])accepted.Clone();
		badOutput[15] = FirstDigest;
		AssertCaseExpectationRejected(badOutput);
		string[] badBinding = (string[])accepted.Clone();
		badBinding[16] = FirstDigest;
		AssertCaseBindingRejected(badBinding);
		string[] badResult = (string[])accepted.Clone();
		badResult[9] = "error";
		AssertCaseExpectationRejected(badResult);
		string[] badCode = (string[])accepted.Clone();
		badCode[10] = "1";
		AssertCaseExpectationRejected(badCode);
		string[] badPrecedence = (string[])accepted.Clone();
		badPrecedence[12] = "invalid-argument";
		AssertCaseExpectationRejected(badPrecedence);
		string[] badCoverage = (string[])accepted.Clone();
		badCoverage[13] = "zero-epoch";
		AssertCaseExpectationRejected(badCoverage);
	}

	private static (string[][] Cases, string[][] Models, string[][] Frames, string[][] Fixtures) LoadManagedReplayTables()
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
		Assert.StartsWith($"vectors/{vectorsPrefix}", relativePath, StringComparison.Ordinal);
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

	private static void AssertSourceModelEnvelope(JsonElement model, string modelId)
	{
		AssertExactProperties(model, ["operations", "root", "schema"]);
		Assert.Equal(JsonValueKind.Array, model.GetProperty("operations").ValueKind);
		JsonElement root = model.GetProperty("root");
		Assert.Equal(JsonValueKind.Object, root.ValueKind);
		string schema = RequireJsonString(model, "schema");
		if (StringComparer.Ordinal.Equals(modelId, "model-managed-batch-expanded-count-plus-one"))
		{
			Assert.Equal("wlpq-source-object-v2", schema);
		}
		else
		{
			Assert.StartsWith("model-managed-", modelId, StringComparison.Ordinal);
			Assert.Equal("wlpq-source-object-v1", schema);
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

	private static bool IsManagedReplayPartition(string partition) =>
		partition is "managed-funding-row" or "managed-funding-batch" or "managed-encoder";

	private static void AssertLowerSha256(string value)
	{
		Assert.Equal(64, value.Length);
		for (int index = 0; index < value.Length; index++)
		{
			Assert.True(value[index] is >= '0' and <= '9' or >= 'a' and <= 'f');
		}
	}

	private static string LowerHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

	private static void AssertCaseExpectation(string[] corpusCase)
	{
		Assert.Equal(17, corpusCase.Length);
		AssertCanonicalIdentifier(corpusCase[0]);
		Assert.Equal($"model-{corpusCase[0]}", corpusCase[6]);
		Assert.Equal("4141414141414141414141414141414141414141414141414141414141414141", corpusCase[7]);
		AssertLowerSha256(corpusCase[14]);
		AssertLowerSha256(corpusCase[16]);
		(string result, string code, string precedence, string coverage, string output) expected;
		string caseId = corpusCase[0];
		if (caseId == "managed-batch-accepted") expected = ("ok", "0", "-", "accepted", "-");
		else if (caseId == "managed-batch-disposed-row") expected = ("lifecycle", "0", "object-disposed", "disposed-row", "-");
		else if (caseId == "managed-batch-expanded-bytes-plus-one") expected = ("error", "4", "limit", "expanded-bytes-plus-one", "-");
		else if (caseId == "managed-batch-expanded-count-plus-one") expected = ("error", "4", "limit", "expanded-count-plus-one", "-");
		else if (caseId == "managed-batch-null-before-disposed-row") expected = ("error", "1", "null-before-lifecycle", "null-before-disposed-row", "-");
		else if (caseId == "managed-batch-null-plan") expected = ("error", "1", "invalid-argument", "null-plan", "-");
		else if (caseId == "managed-batch-null-row") expected = ("error", "1", "invalid-argument", "null-row", "-");
		else if (caseId == "managed-batch-null-row-collection") expected = ("error", "1", "invalid-argument", "null-row-collection", "-");
		else if (caseId == "managed-batch-plan-count-mismatch") expected = ("error", "1", "invalid-argument", "plan-count-mismatch", "-");
		else if (caseId == "managed-encoder-accepted") expected = ("ok", "0", "-", "accepted", "fa8cf8321c8de34a8d4d1f8c881b327bf3961520857e2480e5564ccff012153f");
		else if (caseId == "managed-encoder-bad-epoch-plan-batch-identity") expected = ("error", "1", "epoch-before-identity", "bad-epoch-plan-batch-identity", "-");
		else if (caseId == "managed-encoder-disposed-batch") expected = ("lifecycle", "0", "object-disposed", "disposed-batch", "-");
		else if (caseId == "managed-encoder-disposed-batch-bad-epoch") expected = ("lifecycle", "0", "lifecycle-before-epoch", "disposed-batch-bad-epoch", "-");
		else if (caseId == "managed-encoder-epoch-length-long") expected = ("error", "1", "invalid-argument", "epoch-length-long", "-");
		else if (caseId == "managed-encoder-epoch-length-short") expected = ("error", "1", "invalid-argument", "epoch-length-short", "-");
		else if (caseId == "managed-encoder-null-batch") expected = ("error", "1", "invalid-argument", "null-batch", "-");
		else if (caseId == "managed-encoder-null-before-disposed-batch") expected = ("error", "1", "null-before-lifecycle", "null-before-disposed-batch", "-");
		else if (caseId == "managed-encoder-null-plan") expected = ("error", "1", "invalid-argument", "null-plan", "-");
		else if (caseId == "managed-encoder-plan-batch-identity") expected = ("error", "1", "identity-after-epoch", "plan-batch-identity", "-");
		else if (caseId == "managed-encoder-zero-epoch") expected = ("error", "1", "invalid-argument", "zero-epoch", "-");
		else if (caseId == "managed-row-candidate-plus-one") expected = ("error", "4", "limit", "candidate-plus-one", "-");
		else if (caseId == "managed-row-combined-null-before-length") expected = ("error", "1", "null-before-limit", "combined-null-before-length", "-");
		else if (caseId == "managed-row-combined-null-before-order") expected = ("error", "1", "null-before-encoding", "combined-null-before-order", "-");
		else if (caseId == "managed-row-empty-candidate") expected = ("error", "4", "limit", "empty-candidate", "-");
		else if (caseId == "managed-row-empty-previous") expected = ("ok", "0", "-", "empty-previous", "-");
		else if (caseId == "managed-row-empty-previous-payload") expected = ("error", "4", "limit", "empty-previous-payload", "-");
		else if (caseId == "managed-row-null-candidate") expected = ("error", "1", "invalid-argument", "null-candidate", "-");
		else if (caseId == "managed-row-null-previous-collection") expected = ("error", "1", "invalid-argument", "null-previous-collection", "-");
		else if (caseId == "managed-row-null-previous-element") expected = ("error", "1", "invalid-argument", "null-previous-element", "-");
		else if (caseId == "managed-row-previous-duplicate") expected = ("error", "3", "encoding", "previous-duplicate", "-");
		else if (caseId == "managed-row-previous-out-of-order") expected = ("error", "3", "encoding", "previous-out-of-order", "-");
		else if (caseId == "managed-row-previous-plus-one") expected = ("error", "4", "limit", "previous-plus-one", "-");
		else throw new XunitException("Managed case identity is not reviewed.");
		(string result, string code, string precedence, string coverage, string output) = expected;

		Assert.Equal(result, corpusCase[9]);
		Assert.Equal(code, corpusCase[10]);
		Assert.Equal(precedence, corpusCase[12]);
		Assert.Equal(coverage, corpusCase[13]);
		Assert.Equal(output, corpusCase[15]);
		Assert.Equal(
			corpusCase[1] == "managed-funding-row" ? "funding-row-create" :
			corpusCase[1] == "managed-funding-batch" ? "funding-batch-create" : "encode",
			corpusCase[2]);
	}

	private static void AssertCaseBinding(string[] corpusCase)
	{
		string bindingInput = string.Join(
			'\0',
			new string[]
			{
				"wlpq-case-binding-v1",
				corpusCase[0],
				corpusCase[1],
				corpusCase[2],
				corpusCase[3],
				corpusCase[4],
				corpusCase[14],
				corpusCase[7],
				"-",
				corpusCase[9],
				corpusCase[10],
				corpusCase[15],
			});
		Assert.Equal(corpusCase[16], LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(bindingInput))));
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
			previousHashManifest.Add(string.Join(',', previousHashes));
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

	private static void ReplayFundingRowCase(
		string[] corpusCase,
		JsonElement model,
		string[][] frames,
		string[][] fixtures)
	{
		JsonElement root = model.GetProperty("root");
		AssertExactProperties(root, ["frame_id", "kind", "selected_index"]);
		Assert.Equal("funding-row-from-frame", RequireJsonString(root, "kind"));
		Assert.Equal(0ul, RequireJsonUnsigned(root, "selected_index"));
		(byte[] frame, byte[] epoch, _, List<byte[]?> candidates, List<List<byte[]?>?> previousRows) =
			LoadManagedFrame(frames, RequireJsonString(root, "frame_id"));
		Assert.Equal(corpusCase[7], LowerHex(epoch));
		Assert.Single(candidates);
		Assert.Single(previousRows);
		LiquidOrdinaryWalletPlanFundingRow? row = null;
		try
		{
			ApplyFundingRowOperations(model.GetProperty("operations"), candidates, previousRows, fixtures);
			bool accepted = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
				candidates[0],
				previousRows[0],
				out row,
				out LiquidOrdinaryWalletPlanWireErrorCode code);
			Assert.Equal(corpusCase[9] == "ok", accepted);
			Assert.Equal(checked((uint)ParseCanonicalUnsigned(corpusCase[10])), (uint)code);
			Assert.Equal(accepted, row is not null);
		}
		finally
		{
			row?.Dispose();
			ClearManagedMaterialization(frame, epoch, candidates, previousRows);
		}
	}

	private static void ApplyFundingRowOperations(
		JsonElement operations,
		List<byte[]?> candidates,
		List<List<byte[]?>?> previousRows,
		string[][] fixtures)
	{
		Assert.Equal(JsonValueKind.Array, operations.ValueKind);
		foreach (JsonElement operation in operations.EnumerateArray())
		{
			string name = RequireJsonString(operation, "op");
			string path = RequireJsonString(operation, "path");
			switch (name)
			{
				case "set-bytes":
					AssertExactProperties(operation, ["op", "path", "value"]);
					byte[] replacement = MaterializeByteValue(operation.GetProperty("value"), fixtures, false, 0);
					if (path == "row.candidate")
					{
						Assert.True(candidates[0] is null || !candidates[0]!.AsSpan().SequenceEqual(replacement));
						ClearBytes(candidates[0]);
						candidates[0] = replacement;
					}
					else if (path == "row.previous[0]")
					{
						Assert.NotNull(previousRows[0]);
						Assert.NotEmpty(previousRows[0]!);
						Assert.True(previousRows[0]![0] is null || !previousRows[0]![0]!.AsSpan().SequenceEqual(replacement));
						ClearBytes(previousRows[0]![0]);
						previousRows[0]![0] = replacement;
					}
					else
					{
						CryptographicOperations.ZeroMemory(replacement);
						throw new XunitException("Funding-row set-bytes path is not reviewed.");
					}
					break;
				case "set-null":
					AssertExactProperties(operation, ["op", "path"]);
					if (path == "row.candidate")
					{
						Assert.NotNull(candidates[0]);
						ClearBytes(candidates[0]);
						candidates[0] = null;
					}
					else if (path == "row.previous")
					{
						Assert.NotNull(previousRows[0]);
						ClearByteList(previousRows[0]);
						previousRows[0] = null;
					}
					else if (path == "row.previous[0]")
					{
						Assert.NotNull(previousRows[0]);
						Assert.NotEmpty(previousRows[0]!);
						Assert.NotNull(previousRows[0]![0]);
						ClearBytes(previousRows[0]![0]);
						previousRows[0]![0] = null;
					}
					else
					{
						throw new XunitException("Funding-row set-null path is not reviewed.");
					}
					break;
				case "clear-list":
					AssertExactProperties(operation, ["op", "path"]);
					Assert.Equal("row.previous", path);
					Assert.NotNull(previousRows[0]);
					Assert.NotEmpty(previousRows[0]!);
					ClearByteList(previousRows[0]);
					previousRows[0] = [];
					break;
				case "resize-list":
					AssertExactProperties(operation, ["fill", "length", "op", "path"]);
					Assert.Equal("row.previous", path);
					Assert.NotNull(previousRows[0]);
					int length = checked((int)RequireJsonUnsigned(operation, "length"));
					Assert.NotEqual(previousRows[0]!.Count, length);
					byte[] fill = MaterializeByteValue(operation.GetProperty("fill"), fixtures, false, 0);
					List<byte[]?> resized = previousRows[0]!;
					while (resized.Count > length)
					{
						int last = resized.Count - 1;
						ClearBytes(resized[last]);
						resized.RemoveAt(last);
					}
					while (resized.Count < length)
					{
						resized.Add(CloneBytes(fill));
					}
					CryptographicOperations.ZeroMemory(fill);
					break;
				default:
					throw new XunitException("Funding-row operation is not reviewed.");
			}
		}
	}

	private static byte[] MaterializeByteValue(
		JsonElement value,
		string[][] fixtures,
		bool allowIndexed,
		int index)
	{
		Assert.Equal(JsonValueKind.Object, value.ValueKind);
		string kind = RequireJsonString(value, "kind");
		switch (kind)
		{
			case "literal":
				AssertExactProperties(value, ["hex", "kind"]);
				return DecodeLowerHex(RequireJsonString(value, "hex"));
			case "repeat":
				AssertExactProperties(value, ["byte_hex", "kind", "length"]);
				byte[] repeated = DecodeLowerHex(RequireJsonString(value, "byte_hex"));
				Assert.Single(repeated);
				byte repeatedByte = repeated[0];
				CryptographicOperations.ZeroMemory(repeated);
				return CreateRepeatedBytes(checked((int)RequireJsonUnsigned(value, "length")), repeatedByte);
			case "fixture":
				AssertExactProperties(value, ["fixture_id", "kind"]);
				return LoadFixture(fixtures, RequireJsonString(value, "fixture_id"));
			case "indexed-u32be":
				Assert.True(allowIndexed);
				AssertExactProperties(value, ["kind"]);
				byte[] indexed = new byte[sizeof(uint)];
				BinaryPrimitives.WriteUInt32BigEndian(indexed, checked((uint)index));
				return indexed;
			default:
				throw new XunitException("Byte-source kind is not reviewed.");
		}
	}

	private static byte[] LoadFixture(string[][] fixtures, string fixtureId)
	{
		string[] fixture = FindUniqueRow(fixtures, fixtureId, "fixture");
		Assert.Equal("previous", fixture[1]);
		Assert.Equal("test", fixture[2]);
		Assert.True(fixture[8] is "canonical-direct-previous" or "unrelated-previous");
		string text = ReadStrictText(ResolveCorpusLeaf($"vectors/{fixture[3]}", "public/", ".hex"));
		byte[] bytes = DecodeLowerHex(text[..^1]);
		Assert.Equal(ParseCanonicalUnsigned(fixture[4]), checked((ulong)bytes.LongLength));
		AssertLowerSha256(fixture[5]);
		Assert.Equal(fixture[5], LowerHex(SHA256.HashData(bytes)));
		Assert.Equal(64, fixture[6].Length);
		Assert.Equal(64, fixture[7].Length);
		return bytes;
	}

	private static byte[] CreateRepeatedBytes(int length, byte value)
	{
		Assert.True(length >= 0);
		byte[] bytes = new byte[length];
		bytes.AsSpan().Fill(value);
		return bytes;
	}

	private static void ClearManagedMaterialization(
		byte[] frame,
		byte[] epoch,
		List<byte[]?>? candidates,
		List<List<byte[]?>?>? previousRows)
	{
		CryptographicOperations.ZeroMemory(frame);
		CryptographicOperations.ZeroMemory(epoch);
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

	private static void ReplayFundingBatchCase(
		string[] corpusCase,
		JsonElement model,
		string[][] frames,
		string[][] fixtures)
	{
		JsonElement root = model.GetProperty("root");
		AssertExactProperties(root, ["frame_id", "kind"]);
		Assert.Equal("funding-batch-from-frame", RequireJsonString(root, "kind"));
		(byte[] frame, byte[] epoch, LiquidOrdinaryWalletExactSpendPlan parsedPlan, List<byte[]?> parsedCandidates, List<List<byte[]?>?> parsedPrevious) =
			LoadManagedFrame(frames, RequireJsonString(root, "frame_id"));
		Assert.Equal(corpusCase[7], LowerHex(epoch));
		LiquidOrdinaryWalletExactSpendPlan? plan = parsedPlan;
		List<byte[]?>? candidates = parsedCandidates;
		List<List<byte[]?>?>? previousRows = parsedPrevious;
		LiquidOrdinaryWalletPlanFundingBatch? batch = null;
		var sourceRows = new List<LiquidOrdinaryWalletPlanFundingRow?>();
		try
		{
			(bool rowsNull, HashSet<int> nullRows, HashSet<int> disposedRows) = ApplyFundingBatchOperations(
				model.GetProperty("operations"),
				corpusCase[6],
				ref plan,
				ref candidates,
				ref previousRows,
				fixtures,
				false);
			Assert.NotNull(candidates);
			Assert.NotNull(previousRows);
			Assert.Equal(candidates!.Count, previousRows!.Count);
			for (int rowIndex = 0; rowIndex < candidates.Count; rowIndex++)
			{
				if (nullRows.Contains(rowIndex))
				{
					sourceRows.Add(null);
					continue;
				}
				bool rowAccepted = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
					candidates[rowIndex],
					previousRows[rowIndex],
					out LiquidOrdinaryWalletPlanFundingRow? sourceRow,
					out LiquidOrdinaryWalletPlanWireErrorCode rowCode);
				if (!rowAccepted || sourceRow is null || rowCode != LiquidOrdinaryWalletPlanWireErrorCode.None)
				{
					sourceRow?.Dispose();
					throw new XunitException("Funding-batch source row is invalid before batch evaluation.");
				}
				if (disposedRows.Contains(rowIndex))
				{
					sourceRow.Dispose();
				}
				sourceRows.Add(sourceRow);
			}

			bool threwLifecycle = false;
			bool accepted = false;
			LiquidOrdinaryWalletPlanWireErrorCode code = LiquidOrdinaryWalletPlanWireErrorCode.None;
			try
			{
				accepted = LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
					plan,
					rowsNull ? null : sourceRows,
					out batch,
					out code);
			}
			catch (ObjectDisposedException exception)
			{
				threwLifecycle = true;
				Assert.Equal("LiquidOrdinaryWalletPlanFundingRow", exception.ObjectName);
				Assert.StartsWith("Liquid ordinary-wallet plan funding row is disposed.", exception.Message, StringComparison.Ordinal);
			}

			if (corpusCase[9] == "lifecycle")
			{
				Assert.True(threwLifecycle);
				Assert.Equal(0u, checked((uint)ParseCanonicalUnsigned(corpusCase[10])));
				Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.None, code);
				Assert.Null(batch);
			}
			else
			{
				Assert.False(threwLifecycle);
				Assert.Equal(corpusCase[9] == "ok", accepted);
				Assert.Equal(checked((uint)ParseCanonicalUnsigned(corpusCase[10])), (uint)code);
				Assert.Equal(accepted, batch is not null);
			}
		}
		finally
		{
			batch?.Dispose();
			for (int index = sourceRows.Count - 1; index >= 0; index--)
			{
				sourceRows[index]?.Dispose();
			}
			sourceRows.Clear();
			ClearManagedMaterialization(frame, epoch, candidates, previousRows);
		}
	}

	private static (bool RowsNull, HashSet<int> NullRows, HashSet<int> DisposedRows) ApplyFundingBatchOperations(
		JsonElement operations,
		string modelId,
		ref LiquidOrdinaryWalletExactSpendPlan? plan,
		ref List<byte[]?>? candidates,
		ref List<List<byte[]?>?>? previousRows,
		string[][] fixtures,
		bool allowKnownIndexedRowLimitAlias)
	{
		Assert.NotNull(candidates);
		Assert.NotNull(previousRows);
		bool rowsNull = false;
		var nullRows = new HashSet<int>();
		var disposedRows = new HashSet<int>();
		foreach (JsonElement operation in operations.EnumerateArray())
		{
			string name = RequireJsonString(operation, "op");
			string path = RequireJsonString(operation, "path");
			switch (name)
			{
				case "set-null":
					AssertExactProperties(operation, ["op", "path"]);
					if (path == "batch.plan")
					{
						Assert.NotNull(plan);
						plan = null;
					}
					else if (path == "batch.rows")
					{
						Assert.False(rowsNull);
						rowsNull = true;
					}
					else if (TryParseBatchRowPath(path, out int nullIndex))
					{
						Assert.True(nullIndex >= 0 && nullIndex < candidates!.Count);
						Assert.True(nullRows.Add(nullIndex));
					}
					else
					{
						throw new XunitException("Funding-batch set-null path is not reviewed.");
					}
					break;
				case "dispose":
					AssertExactProperties(operation, ["op", "path"]);
					Assert.True(TryParseBatchRowPath(path, out int disposeIndex));
					Assert.True(disposeIndex >= 0 && disposeIndex < candidates!.Count);
					Assert.True(disposedRows.Add(disposeIndex));
					break;
				case "clear-list":
					AssertExactProperties(operation, ["op", "path"]);
					Assert.True(TryParseBatchPreviousListPath(path, out int clearIndex));
					Assert.True(clearIndex >= 0 && clearIndex < previousRows!.Count);
					Assert.NotNull(previousRows[clearIndex]);
					Assert.NotEmpty(previousRows[clearIndex]!);
					ClearByteList(previousRows[clearIndex]);
					previousRows[clearIndex] = [];
					break;
				case "set-bytes":
					AssertExactProperties(operation, ["op", "path", "value"]);
					if (TryParseBatchCandidatePath(path, out int candidateIndex))
					{
						Assert.True(candidateIndex >= 0 && candidateIndex < candidates!.Count);
						byte[] replacement = MaterializeByteValue(operation.GetProperty("value"), fixtures, false, 0);
						Assert.True(candidates[candidateIndex] is null || !candidates[candidateIndex]!.AsSpan().SequenceEqual(replacement));
						ClearBytes(candidates[candidateIndex]);
						candidates[candidateIndex] = replacement;
					}
					else if (TryParseBatchPreviousItemPath(path, out int rowIndex, out int previousIndex))
					{
						Assert.True(rowIndex >= 0 && rowIndex < previousRows!.Count);
						Assert.NotNull(previousRows[rowIndex]);
						Assert.True(previousIndex >= 0 && previousIndex < previousRows[rowIndex]!.Count);
						byte[] replacement = MaterializeByteValue(operation.GetProperty("value"), fixtures, false, 0);
						Assert.True(previousRows[rowIndex]![previousIndex] is null || !previousRows[rowIndex]![previousIndex]!.AsSpan().SequenceEqual(replacement));
						ClearBytes(previousRows[rowIndex]![previousIndex]);
						previousRows[rowIndex]![previousIndex] = replacement;
					}
					else
					{
						throw new XunitException("Funding-batch set-bytes path is not reviewed.");
					}
					break;
				case "resize-list":
					AssertExactProperties(operation, ["fill", "length", "op", "path"]);
					int length = checked((int)RequireJsonUnsigned(operation, "length"));
					if (path == "batch.rows")
					{
						Assert.False(rowsNull);
						AssertExactProperties(operation.GetProperty("fill"), ["index", "kind"]);
						Assert.Equal("row-copy", RequireJsonString(operation.GetProperty("fill"), "kind"));
						int copyIndex = checked((int)RequireJsonUnsigned(operation.GetProperty("fill"), "index"));
						Assert.True(copyIndex >= 0 && copyIndex < candidates!.Count);
						Assert.NotEqual(candidates.Count, length);
						byte[]? sourceCandidate = CloneBytes(candidates[copyIndex]);
						List<byte[]?>? sourcePrevious = CloneByteList(previousRows![copyIndex]);
						while (candidates.Count > length)
						{
							int last = candidates.Count - 1;
							ClearBytes(candidates[last]);
							ClearByteList(previousRows[last]);
							candidates.RemoveAt(last);
							previousRows.RemoveAt(last);
							nullRows.Remove(last);
							disposedRows.Remove(last);
						}
						while (candidates.Count < length)
						{
							candidates.Add(CloneBytes(sourceCandidate));
							previousRows.Add(CloneByteList(sourcePrevious));
						}
						ClearBytes(sourceCandidate);
						ClearByteList(sourcePrevious);
					}
					else if (TryParseBatchPreviousListPath(path, out int resizeIndex))
					{
						Assert.True(resizeIndex >= 0 && resizeIndex < previousRows!.Count);
						Assert.NotNull(previousRows[resizeIndex]);
						Assert.NotEqual(previousRows[resizeIndex]!.Count, length);
						bool indexed = RequireJsonString(operation.GetProperty("fill"), "kind") == "indexed-u32be";
						if (indexed)
						{
							Assert.Equal("model-managed-batch-expanded-count-plus-one", modelId);
							Assert.Empty(previousRows[resizeIndex]!);
							int authenticatedLength = resizeIndex == 0 ? 8192 : 8193;
							bool knownRowLimitAlias =
								allowKnownIndexedRowLimitAlias &&
								resizeIndex == 0 &&
								length == LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount + 1;
							Assert.True(length == authenticatedLength || knownRowLimitAlias);
						}
						List<byte[]?> resized = previousRows[resizeIndex]!;
						while (resized.Count > length)
						{
							int last = resized.Count - 1;
							ClearBytes(resized[last]);
							resized.RemoveAt(last);
						}
						while (resized.Count < length)
						{
							resized.Add(MaterializeByteValue(operation.GetProperty("fill"), fixtures, indexed, resized.Count));
						}
					}
					else
					{
						throw new XunitException("Funding-batch resize-list path is not reviewed.");
					}
					break;
				default:
					throw new XunitException("Funding-batch operation is not reviewed.");
			}
		}
		return (rowsNull, nullRows, disposedRows);
	}

	private static bool TryParseBatchRowPath(string path, out int index) =>
		TryParseClosedIndex(path, "batch.rows[", "]", out index);

	private static bool TryParseBatchCandidatePath(string path, out int index) =>
		TryParseClosedIndex(path, "batch.rows[", "].candidate", out index);

	private static bool TryParseBatchPreviousListPath(string path, out int index) =>
		TryParseClosedIndex(path, "batch.rows[", "].previous", out index);

	private static bool TryParseBatchPreviousItemPath(string path, out int rowIndex, out int previousIndex)
	{
		rowIndex = -1;
		previousIndex = -1;
		const string Prefix = "batch.rows[";
		const string Middle = "].previous[";
		if (!path.StartsWith(Prefix, StringComparison.Ordinal) || !path.EndsWith(']'))
		{
			return false;
		}
		int middle = path.IndexOf(Middle, Prefix.Length, StringComparison.Ordinal);
		if (middle < 0)
		{
			return false;
		}
		string row = path[Prefix.Length..middle];
		string previous = path[(middle + Middle.Length)..^1];
		return TryParseCanonicalIndex(row, out rowIndex) && TryParseCanonicalIndex(previous, out previousIndex);
	}

	private static bool TryParseClosedIndex(string path, string prefix, string suffix, out int index)
	{
		index = -1;
		if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
		{
			return false;
		}
		string value = path[prefix.Length..^suffix.Length];
		return TryParseCanonicalIndex(value, out index);
	}

	private static bool TryParseCanonicalIndex(string value, out int index)
	{
		index = -1;
		if (value.Length == 0 || (value.Length > 1 && value[0] == '0'))
		{
			return false;
		}
		for (int digit = 0; digit < value.Length; digit++)
		{
			if (value[digit] is < '0' or > '9')
			{
				return false;
			}
		}
		return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index);
	}

	private static List<byte[]?>? CloneByteList(List<byte[]?>? source)
	{
		if (source is null)
		{
			return null;
		}
		var result = new List<byte[]?>(source.Count);
		for (int index = 0; index < source.Count; index++)
		{
			result.Add(CloneBytes(source[index]));
		}
		return result;
	}

	private static void ReplayMutatedBatchSource(
		string[] corpusCase,
		string json,
		string[][] frames,
		string[][] fixtures)
	{
		using JsonDocument document = ParseCanonicalJson(json);
		AssertSourceModelEnvelope(document.RootElement, corpusCase[6]);
		ReplayFundingBatchCase(corpusCase, document.RootElement, frames, fixtures);
	}

	private static void ReplayEncoderCase(
		string[] corpusCase,
		JsonElement model,
		string[][] frames,
		string[][] fixtures)
	{
		JsonElement root = model.GetProperty("root");
		AssertExactProperties(root, ["frame_id", "kind"]);
		Assert.Equal("encoder-call-from-frame", RequireJsonString(root, "kind"));
		string frameId = RequireJsonString(root, "frame_id");
		(byte[] frame, byte[] epoch, LiquidOrdinaryWalletExactSpendPlan parsedPlan, List<byte[]?> candidates, List<List<byte[]?>?> previousRows) =
			LoadManagedFrame(frames, frameId);
		Assert.Equal(corpusCase[7], LowerHex(epoch));
		LiquidOrdinaryWalletExactSpendPlan? plan = parsedPlan;
		LiquidOrdinaryWalletPlanFundingBatch? batch = null;
		LiquidOrdinaryWalletPlanFundingBatch? ownedBatch = null;
		var sourceRows = new List<LiquidOrdinaryWalletPlanFundingRow?>();
		LiquidOrdinaryWalletPlanEncodedFrame? encoded = null;
		byte[]? encodedCopy = null;
		try
		{
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
					parsedPlan,
					sourceRows,
					out batch,
					out LiquidOrdinaryWalletPlanWireErrorCode batchCode));
			Assert.NotNull(batch);
			Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.None, batchCode);
			ownedBatch = batch;
			for (int index = 0; index < sourceRows.Count; index++)
			{
				sourceRows[index]?.Dispose();
				sourceRows[index] = null;
			}

			ApplyEncoderOperations(
				model.GetProperty("operations"),
				frameId,
				frames,
				ref epoch,
				ref plan,
				ref batch);
			bool threwLifecycle = false;
			bool accepted = false;
			LiquidOrdinaryWalletPlanWireErrorCode code = LiquidOrdinaryWalletPlanWireErrorCode.None;
			try
			{
				accepted = LiquidOrdinaryWalletPlanEncoder.TryEncode(epoch, plan, batch, out encoded, out code);
			}
			catch (ObjectDisposedException exception)
			{
				threwLifecycle = true;
				Assert.Equal("LiquidOrdinaryWalletPlanFundingBatch", exception.ObjectName);
				Assert.StartsWith("Liquid ordinary-wallet plan funding batch is disposed.", exception.Message, StringComparison.Ordinal);
			}

			if (corpusCase[9] == "lifecycle")
			{
				Assert.True(threwLifecycle);
				Assert.Equal(0u, checked((uint)ParseCanonicalUnsigned(corpusCase[10])));
				Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.None, code);
				Assert.Null(encoded);
			}
			else
			{
				Assert.False(threwLifecycle);
				Assert.Equal(corpusCase[9] == "ok", accepted);
				Assert.Equal(checked((uint)ParseCanonicalUnsigned(corpusCase[10])), (uint)code);
				Assert.Equal(accepted, encoded is not null);
			}
			if (accepted)
			{
				Assert.NotNull(encoded);
				encodedCopy = new byte[encoded!.Length];
				encoded.CopyFrameTo(encodedCopy);
				Assert.Equal(frame, encodedCopy);
				Assert.Equal(corpusCase[15], LowerHex(SHA256.HashData(encodedCopy)));
			}
		}
		finally
		{
			encoded?.Dispose();
			ClearBytes(encodedCopy);
			ownedBatch?.Dispose();
			for (int index = sourceRows.Count - 1; index >= 0; index--)
			{
				sourceRows[index]?.Dispose();
			}
			sourceRows.Clear();
			ClearManagedMaterialization(frame, epoch, candidates, previousRows);
		}
	}

	private static void ApplyEncoderOperations(
		JsonElement operations,
		string frameId,
		string[][] frames,
		ref byte[] epoch,
		ref LiquidOrdinaryWalletExactSpendPlan? plan,
		ref LiquidOrdinaryWalletPlanFundingBatch? batch)
	{
		foreach (JsonElement operation in operations.EnumerateArray())
		{
			string name = RequireJsonString(operation, "op");
			string path = RequireJsonString(operation, "path");
			switch (name)
			{
				case "set-bytes":
					AssertExactProperties(operation, ["op", "path", "value"]);
					Assert.Equal("call.source_epoch", path);
					byte[] replacement = MaterializeByteValue(operation.GetProperty("value"), [], false, 0);
					Assert.False(epoch.AsSpan().SequenceEqual(replacement));
					CryptographicOperations.ZeroMemory(epoch);
					epoch = replacement;
					break;
				case "set-null":
					AssertExactProperties(operation, ["op", "path"]);
					if (path == "call.plan")
					{
						Assert.NotNull(plan);
						plan = null;
					}
					else if (path == "call.batch")
					{
						Assert.NotNull(batch);
						batch = null;
					}
					else
					{
						throw new XunitException("Encoder set-null path is not reviewed.");
					}
					break;
				case "dispose":
					AssertExactProperties(operation, ["op", "path"]);
					Assert.Equal("call.batch", path);
					Assert.NotNull(batch);
					batch!.Dispose();
					break;
				case "clone-instance":
					AssertExactProperties(operation, ["from", "op", "path"]);
					Assert.Equal("call.plan", path);
					Assert.Equal("call.plan", RequireJsonString(operation, "from"));
					Assert.NotNull(plan);
					LiquidOrdinaryWalletExactSpendPlan clone = CloneManagedPlan(frames, frameId);
					Assert.NotSame(plan, clone);
					plan = clone;
					break;
				default:
					throw new XunitException("Encoder operation is not reviewed.");
			}
		}
	}

	private static LiquidOrdinaryWalletExactSpendPlan CloneManagedPlan(string[][] frames, string frameId)
	{
		(byte[] frame, byte[] epoch, LiquidOrdinaryWalletExactSpendPlan plan, List<byte[]?> candidates, List<List<byte[]?>?> previousRows) =
			LoadManagedFrame(frames, frameId);
		ClearManagedMaterialization(frame, epoch, candidates, previousRows);
		return plan;
	}

	private static void AssertMutatedBatchRejected(
		string[] corpusCase,
		string json,
		string[][] frames,
		string[][] fixtures)
	{
		bool rejected = false;
		try
		{
			ReplayMutatedBatchSource(corpusCase, json, frames, fixtures);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Mutated batch source was accepted.");
	}

	private static void AssertCanonicalJsonRejected(string json)
	{
		bool rejected = false;
		JsonDocument? document = null;
		try
		{
			document = ParseCanonicalJson(json);
		}
		catch (Exception exception) when (exception is XunitException or JsonException)
		{
			rejected = true;
		}
		finally
		{
			document?.Dispose();
		}
		Assert.True(rejected, "Noncanonical JSON was accepted.");
	}

	private static void AssertCanonicalTableRejected(string text, string[] header)
	{
		bool rejected = false;
		try
		{
			_ = ParseCanonicalTable(text, header);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Noncanonical table was accepted.");
	}

	private static void AssertCorpusPathRejected(string relativePath, string prefix, string suffix)
	{
		bool rejected = false;
		try
		{
			_ = ResolveCorpusLeaf(relativePath, prefix, suffix);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Unsafe corpus path was accepted.");
	}

	private static void AssertSourceModelEnvelopeRejected(JsonElement model, string modelId)
	{
		bool rejected = false;
		try
		{
			AssertSourceModelEnvelope(model, modelId);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid source-model envelope was accepted.");
	}

	private static void AssertCaseExpectationRejected(string[] corpusCase)
	{
		bool rejected = false;
		try
		{
			AssertCaseExpectation(corpusCase);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid case expectation was accepted.");
	}

	private static void AssertCaseBindingRejected(string[] corpusCase)
	{
		bool rejected = false;
		try
		{
			AssertCaseBinding(corpusCase);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid case binding was accepted.");
	}

	private static void AssertBoundCanonicalTextRejected(string path, ulong length, string sha256)
	{
		bool rejected = false;
		try
		{
			_ = ReadBoundCanonicalText(path, length, sha256);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Unbound source-model content was accepted.");
	}

	private static void AssertMutatedBatchFirstInvalidRowIsLimitExceeded(
		string[] corpusCase,
		string json,
		string[][] frames,
		string[][] fixtures,
		int expectedInvalidRowIndex,
		bool allowKnownIndexedRowLimitAlias)
	{
		using JsonDocument document = ParseCanonicalJson(json);
		AssertSourceModelEnvelope(document.RootElement, corpusCase[6]);
		JsonElement root = document.RootElement.GetProperty("root");
		AssertExactProperties(root, ["frame_id", "kind"]);
		Assert.Equal("funding-batch-from-frame", RequireJsonString(root, "kind"));
		(byte[] frame, byte[] epoch, LiquidOrdinaryWalletExactSpendPlan parsedPlan, List<byte[]?> parsedCandidates, List<List<byte[]?>?> parsedPrevious) =
			LoadManagedFrame(frames, RequireJsonString(root, "frame_id"));
		LiquidOrdinaryWalletExactSpendPlan? plan = parsedPlan;
		List<byte[]?>? candidates = parsedCandidates;
		List<List<byte[]?>?>? previousRows = parsedPrevious;
		try
		{
			(bool rowsNull, HashSet<int> nullRows, HashSet<int> disposedRows) = ApplyFundingBatchOperations(
				document.RootElement.GetProperty("operations"),
				corpusCase[6],
				ref plan,
				ref candidates,
				ref previousRows,
				fixtures,
				allowKnownIndexedRowLimitAlias);
			Assert.False(rowsNull);
			Assert.Empty(nullRows);
			Assert.Empty(disposedRows);
			Assert.NotNull(plan);
			Assert.NotNull(candidates);
			Assert.NotNull(previousRows);
			Assert.Equal(candidates!.Count, previousRows!.Count);
			Assert.True(expectedInvalidRowIndex >= 0 && expectedInvalidRowIndex < candidates.Count);

			bool observedExpectedProductionFailure = false;
			for (int rowIndex = 0; rowIndex <= expectedInvalidRowIndex; rowIndex++)
			{
				LiquidOrdinaryWalletPlanFundingRow? row = null;
				try
				{
					bool accepted = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
						candidates[rowIndex],
						previousRows[rowIndex],
						out row,
						out LiquidOrdinaryWalletPlanWireErrorCode code);
					if (rowIndex < expectedInvalidRowIndex)
					{
						Assert.True(accepted);
						Assert.NotNull(row);
						Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.None, code);
						continue;
					}

					Assert.False(accepted);
					Assert.Null(row);
					Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded, code);
					observedExpectedProductionFailure = true;
				}
				finally
				{
					row?.Dispose();
				}
			}
			Assert.True(observedExpectedProductionFailure, "Mutation did not reach the reviewed funding-row production failure.");
		}
		finally
		{
			ClearManagedMaterialization(frame, epoch, candidates, previousRows);
		}
	}

	private static void AssertManagedSourceModelBijection(string[][] cases, string[][] models)
	{
		var caseModelIds = new List<string>();
		var casePartitions = new List<string>();
		for (int index = 0; index < cases.Length; index++)
		{
			if (IsManagedReplayPartition(cases[index][1]))
			{
				caseModelIds.Add(cases[index][6]);
				casePartitions.Add(cases[index][1]);
			}
		}

		var managedModelIds = new List<string>();
		var managedModelPartitions = new List<string>();
		for (int index = 0; index < models.Length; index++)
		{
			if (IsManagedReplayPartition(models[index][1]))
			{
				managedModelIds.Add(models[index][0]);
				managedModelPartitions.Add(models[index][1]);
			}
		}

		Assert.Equal(32, caseModelIds.Count);
		Assert.Equal(caseModelIds.Count, managedModelIds.Count);
		for (int index = 0; index < caseModelIds.Count; index++)
		{
			if (index > 0)
			{
				Assert.True(StringComparer.Ordinal.Compare(caseModelIds[index - 1], caseModelIds[index]) < 0);
				Assert.True(StringComparer.Ordinal.Compare(managedModelIds[index - 1], managedModelIds[index]) < 0);
			}
			Assert.Equal(caseModelIds[index], managedModelIds[index]);
			Assert.Equal(casePartitions[index], managedModelPartitions[index]);
		}
	}

	private static void AssertManagedSourceModelBijectionRejected(string[][] cases, string[][] models)
	{
		bool rejected = false;
		try
		{
			AssertManagedSourceModelBijection(cases, models);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Managed source-model orphan or repartition was accepted.");
	}

	private static void AssertEncoderEpochBeforeIdentityAuthority(bool probeReversedOrder)
	{
		MethodInfo lockedEncoder = typeof(LiquidOrdinaryWalletPlanEncoder).GetMethod(
			"TryEncodeLocked",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new XunitException("Encoder locked method is absent or ambiguous.");
		ParameterInfo[] parameters = lockedEncoder.GetParameters();
		Assert.Equal(6, parameters.Length);
		Assert.Equal(typeof(ReadOnlySpan<byte>), parameters[0].ParameterType);
		Assert.Equal(typeof(LiquidOrdinaryWalletExactSpendPlan), parameters[1].ParameterType);
		Assert.Equal(typeof(LiquidOrdinaryWalletExactSpendPlan), parameters[2].ParameterType);
		Assert.Equal(typeof(LiquidOrdinaryWalletPlanFundingRow[]), parameters[3].ParameterType);
		Assert.True(parameters[4].IsOut);
		Assert.Equal(typeof(LiquidOrdinaryWalletPlanEncodedFrame).MakeByRefType(), parameters[4].ParameterType);
		Assert.True(parameters[5].IsOut);
		Assert.Equal(typeof(LiquidOrdinaryWalletPlanWireErrorCode).MakeByRefType(), parameters[5].ParameterType);

		MethodInfo isNonzero = typeof(LiquidOrdinaryWalletPlanEncoder).GetMethod(
			"IsNonzero",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new XunitException("Encoder nonzero check is absent or ambiguous.");
		byte[] il = lockedEncoder.GetMethodBody()?.GetILAsByteArray() ??
			throw new XunitException("Encoder locked method has no IL body.");
		byte[] nonzeroCall = new byte[5];
		nonzeroCall[0] = 0x28;
		BinaryPrimitives.WriteInt32LittleEndian(nonzeroCall.AsSpan(1), isNonzero.MetadataToken);
#if DEBUG
		const int ExpectedIlLength = 157;
		const string ExpectedIlSha256 = "1d45e27ea836d38df9e4f0d3a1abc2c593fdefd23ec87680fdfd1e9bb9cb21de";
		byte[] identityCheck = new byte[] { 0x03, 0x04, 0xfe, 0x01, 0x16, 0xfe, 0x01 };
#else
		const int ExpectedIlLength = 121;
		const string ExpectedIlSha256 = "f3ac7b263e459be48a534132b5a1ddeaa4e43429a44061825fbce3f28ebe4f";
		byte[] identityCheck = new byte[] { 0x03, 0x04, 0x2e };
#endif
		try
		{
			Assert.Equal(ExpectedIlLength, il.Length);
			Assert.Equal(ExpectedIlSha256, LowerHex(SHA256.HashData(il)));
			int epochCheckOffset = FindUniqueBytePattern(il, nonzeroCall);
			int identityCheckOffset = FindUniqueBytePattern(il, identityCheck);
			AssertEncoderEpochBeforeIdentityOffsets(epochCheckOffset, identityCheckOffset);
			if (probeReversedOrder)
			{
				byte[] reversedOrderIl = ReverseReviewedBytePatternOrder(
					il,
					epochCheckOffset,
					nonzeroCall.Length,
					identityCheckOffset,
					identityCheck.Length);
				try
				{
					Assert.False(il.AsSpan().SequenceEqual(reversedOrderIl));
					int reversedEpochOffset = FindUniqueBytePattern(reversedOrderIl, nonzeroCall);
					int reversedIdentityOffset = FindUniqueBytePattern(reversedOrderIl, identityCheck);
					Assert.True(reversedIdentityOffset < reversedEpochOffset);
					AssertEncoderEpochBeforeIdentityOffsetsRejected(reversedEpochOffset, reversedIdentityOffset);
					AssertEncoderIlAuthorityRejected(
						reversedOrderIl,
						ExpectedIlLength,
						ExpectedIlSha256,
						nonzeroCall,
						identityCheck);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(reversedOrderIl);
				}
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(identityCheck);
			CryptographicOperations.ZeroMemory(nonzeroCall);
			CryptographicOperations.ZeroMemory(il);
		}
	}

	private static int FindUniqueBytePattern(byte[] source, byte[] pattern)
	{
		Assert.NotEmpty(source);
		Assert.NotEmpty(pattern);
		Assert.True(pattern.Length <= source.Length);
		int found = -1;
		for (int offset = 0; offset <= source.Length - pattern.Length; offset++)
		{
			if (!source.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
			{
				continue;
			}
			Assert.Equal(-1, found);
			found = offset;
		}
		Assert.True(found >= 0, "Reviewed IL marker is absent.");
		return found;
	}

	private static void AssertEncoderEpochBeforeIdentityOffsets(int epochCheckOffset, int identityCheckOffset)
	{
		Assert.True(epochCheckOffset >= 0);
		Assert.True(identityCheckOffset >= 0);
		Assert.True(epochCheckOffset < identityCheckOffset, "Encoder identity validation precedes epoch validation.");
	}

	private static void AssertEncoderEpochBeforeIdentityOffsetsRejected(int epochCheckOffset, int identityCheckOffset)
	{
		bool rejected = false;
		try
		{
			AssertEncoderEpochBeforeIdentityOffsets(epochCheckOffset, identityCheckOffset);
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Reversed encoder validation order was accepted.");
	}

	private static void AssertMutatedBatchOverflowRejected(
		string[] corpusCase,
		string json,
		string[][] frames,
		string[][] fixtures)
	{
		bool rejected = false;
		try
		{
			ReplayMutatedBatchSource(corpusCase, json, frames, fixtures);
		}
		catch (OverflowException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Unbounded batch length was accepted by the loader.");
	}

	private static byte[] ReverseReviewedBytePatternOrder(
		byte[] source,
		int firstOffset,
		int firstLength,
		int secondOffset,
		int secondLength)
	{
		Assert.True(firstOffset >= 0);
		Assert.True(firstLength > 0);
		Assert.True(secondOffset >= firstOffset + firstLength);
		Assert.True(secondLength > 0);
		Assert.True(secondOffset + secondLength <= source.Length);
		int middleLength = secondOffset - firstOffset - firstLength;
		byte[] reversed = (byte[])source.Clone();
		Buffer.BlockCopy(source, secondOffset, reversed, firstOffset, secondLength);
		Buffer.BlockCopy(source, firstOffset + firstLength, reversed, firstOffset + secondLength, middleLength);
		Buffer.BlockCopy(source, firstOffset, reversed, firstOffset + secondLength + middleLength, firstLength);
		return reversed;
	}

	private static void AssertEncoderIlAuthorityRejected(
		byte[] il,
		int expectedLength,
		string expectedSha256,
		byte[] nonzeroCall,
		byte[] identityCheck)
	{
		bool rejected = false;
		try
		{
			Assert.Equal(expectedLength, il.Length);
			Assert.Equal(expectedSha256, LowerHex(SHA256.HashData(il)));
			AssertEncoderEpochBeforeIdentityOffsets(
				FindUniqueBytePattern(il, nonzeroCall),
				FindUniqueBytePattern(il, identityCheck));
		}
		catch (XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Reversed encoder IL order retained exact authority.");
	}
}
