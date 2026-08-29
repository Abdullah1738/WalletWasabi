using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Network;

public sealed class ElementsPublicNetworkManifest
{
	private const int MaxManifestBytes = 4096;
	private const string MainnetCborSha256 = "7a99aca826aeefd659c4af97347ae302c72c3093c07697d8baa4dc03139cb908";
	private const string MainnetManifestId = "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b";
	private const string TestnetCborSha256 = "9fc3e29fe188d63826c18a9f8ab59b42b83e47f57fbf16ca3842d970c16994f1";
	private const string TestnetManifestId = "e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20";
	private static readonly byte[] ManifestIdDomain = "wasabi-liquid/network-manifest/v1\0"u8.ToArray();
	private static readonly byte[] MainnetCanonicalCbor = Convert.FromBase64String(
		"mB8CbkxJUVVJRF9NQUlOTkVUaGxpcXVpZHYxWCAUZidYNiINspRMoFmjoQ72/S6mhLBojSw3kpaIiiBgA1gg3cNUu+fy/jPlqbfi+/8XZeFvG9BizFfJpVuBSSN9ZNQZAtRYIMLR4M9JxsPNmxdz87NiWyEYHm9R75ZtngJBrsgyTOl7AFggbwJ56e0EHD1xCp9X0MApKEFkYMS3Iq40V6Ee7DgcUm1YIG8CeentBBw9cQqfV9DAKShBZGDEtyKuNFehHuw4HFJtWCAAAAAAABnWaJwIWuFlgx6TT/djrkaipsFys/G2Cozib/X1hRg5GCcMYmV4YmxxZjIzLjMuMxoAARGAiHgnRUxFTUVOVFNfMjNfM18zX1NPVVJDRV9JREVOVElUWV9PTkxZX1Yy9fX19PT09IN4KEdFTkVSQVRJT05fQVBJX1NDSEVNQV9WMV9SRVFVSVJFRF9BQlNFTlSEanN0YXJ0dXBfaWRzY2hhaW5zdGF0ZV9yZXZpc2lvbmZoZWlnaHRoYmVzdGhhc2j0eBlHRU5FUkFUSU9OX0FCQV9JTkNPTVBMRVRFRPq/tdoZG4JYIOUSEekdnPSuw73DcKAwOs3l0kuu2xIjX90nhohQadkcWCCpESuesrFaLvRR6FmPMmeINC945sX6k8C+ltotAaPHi4dobGlxdWlkdjFqSU5DT01QTEVURXhZTk9fQUxMT1dFRF9MT0NBTF9QVUJMSUNfQ09OVEFJTklOR19CTE9DS19XSVRIX05PTkVNUFRZX09VVFBVVF9TVVJKRUNUSU9OX0FORF9SQU5HRV9QUk9PRlNYIKNnQlu7FE8bpdKiN2GPAj+qTWFEhej29NDCfanhQL7n9PT0hnRsaXF1aWR2MS1wZWdpbi1wcm9vZlggW+QRpwpe08euo0vduPK1zDpFtXUKHVpaFvI2NysTsSJYILnLbNUna8a6P0hr/UKgff8m/giwqve25VUjCtuiHfP3WCBxTXjoHhQ5s4Y86v0vu/fmndTrUCUq4P8uHuaei5HJ/3g7cGVnaW4tcGFyZW50LWluY2x1c2lvbi1wcm9vZi1jb2RlYy1leGFtcGxlLW5vLW91dHB1dC1wcm9vZnOCggAAggAAhm9lbGVtZW50cy0yMy4zLjNUGvek2b6pO01/Kad/l1Gg5uA6Q5BURA/KViur7QUYvru0UpEbfA0/IAtYIEvhl47wqCrggDkhTjyM8qp5k8KO+TWGSTP9HRrAsUUSWCCCLBYyr7r6iKdHtxZ79uL5fukRtzKgr8AM9hz8y0OCbFggfJQnUNtAOIzL3FWZHWkqsfsyPip2GKvKMgVu4geYgZ2G9WhlbGVtZW50c/X19QBYIPC9xX3gMyh1elk70InACX9PMS3Wp9YNh1sFZVQVA5w0lnglTk9ERV9ORVRNQU5JRkVTVF9CT1VOREVEX1JFU09VUkNFU19WMRkQABoAAQAAGCAaAQAAABoAgAAAGgACAAAIGQEAGQIACBhAGIAaAAEAABAaAIAAABknEBoAQAAAGScQGScQGQPoGgBAAAB4PU5PREUtTkVUTUFOSUZFU1QtMDAxLVNPVVJDRS1PTkxZLUNULUlOQ09NUExFVEUtTk8tR0FURS1DUkVESVR4HVBVQkxJQ19DVF9TRU5USU5FTF9JTkNPTVBMRVRF");
	private static readonly byte[] TestnetCanonicalCbor = Convert.FromBase64String(
		"mB8CbkxJUVVJRF9URVNUTkVUbWxpcXVpZHRlc3RuZXRYIKdx2o5S7mrVge0empmCXls7eZIiVTTqoq4jJE/iarHBWCB+fWn5ohzQbnUfG+6W2U9ehksjdR51bnophhosF72+ihkBqVggaunKVmNw0VFkK6GeUHJMd2H91M85jkBeIZdVfTaUS2MBWCAUTGVDRKpxbW86vMHKkOVkHk4qf2M7wJ/juvZFhYGaSVggFExlQ0SqcW1vOrzBypDlZB5OKn9jO8Cf47r2RYWBmklYIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA9PSFGCQTF2N0ZXhjdGxxZjIzLjMuMxoAARGAiHgnRUxFTUVOVFNfMjNfM18zX1NPVVJDRV9JREVOVElUWV9PTkxZX1Yy9fX19PT09IN4KEdFTkVSQVRJT05fQVBJX1NDSEVNQV9WMV9SRVFVSVJFRF9BQlNFTlSEanN0YXJ0dXBfaWRzY2hhaW5zdGF0ZV9yZXZpc2lvbmZoZWlnaHRoYmVzdGhhc2j0eBlHRU5FUkFUSU9OX0FCQV9JTkNPTVBMRVRFREEO3WIZSctYIOnkEXVA9/I7Pt18LK1mChf7M8eVm4w3z2HZKxiRM5KaWCBK6BVy8G4biP1c7XoaAAlFQy6D4VUeb3Ie6cALjMMyYIdtbGlxdWlkdGVzdG5ldGpJTkNPTVBMRVRFeFlOT19BTExPV0VEX0xPQ0FMX1BVQkxJQ19DT05UQUlOSU5HX0JMT0NLX1dJVEhfTk9ORU1QVFlfT1VUUFVUX1NVUkpFQ1RJT05fQU5EX1JBTkdFX1BST09GU1gguOdA84fuekLzfoCUxA5JR92EAGlswxgJiCW68GdTrWL09PSGeBxsaXF1aWR0ZXN0bmV0LXNjcmlwdC13aXRuZXNzWCBTYlN1IscYsms6irouu/Qc8CizDNKp54jIyzIoggsG5FggiCZYdKXpbay3httndTFXm6bMGOu02EWiusX7cZjHBNlYID8BcqGj0YPuWE/TJxBQmw2prvOslj+ssUgZPkTGRnZZeC1zY3JpcHQtd2l0bmVzcy1jb2RlYy1leGFtcGxlLW5vLW91dHB1dC1wcm9vZnOCggAAggAAhm9lbGVtZW50cy0yMy4zLjNUGvek2b6pO01/Kad/l1Gg5uA6Q5BURA/KViur7QUYvru0UpEbfA0/IAtYIEvhl47wqCrggDkhTjyM8qp5k8KO+TWGSTP9HRrAsUUSWCCCLBYyr7r6iKdHtxZ79uL5fukRtzKgr8AM9hz8y0OCbFggfJQnUNtAOIzL3FWZHWkqsfsyPip2GKvKMgVu4geYgZ2G9WhlbGVtZW50c/X19RsAB3XwWgdAAFggQh51DfFpHa53qJDXLn0ZONSA3/VZ27hUI8a7m5I8pQKWeCVOT0RFX05FVE1BTklGRVNUX0JPVU5ERURfUkVTT1VSQ0VTX1YxGRAAGgABAAAYIBoBAAAAGgCAAAAaAAIAAAgZAQAZAgAIGEAYgBoAAQAAEBoAgAAAGScQGgBAAAAZJxAZJxAZA+gaAEAAAHg9Tk9ERS1ORVRNQU5JRkVTVC0wMDEtU09VUkNFLU9OTFktQ1QtSU5DT01QTEVURS1OTy1HQVRFLUNSRURJVHgdUFVCTElDX0NUX1NFTlRJTkVMX0lOQ09NUExFVEU=");

	private ElementsPublicNetworkManifest(
		byte[] canonicalCbor,
		string canonicalCborSha256,
		string manifestId,
		int manifestSchema,
		string productNetworkId,
		string chainRpcName,
		string genesisBlockHash,
		string rawGenesisSha256,
		ulong rawGenesisSize,
		string genesisUtxoSetDigest,
		ulong genesisUtxoCount,
		string peggedAssetId,
		string requiredFeeAssetId,
		string parentGenesisHash,
		bool hasParentChain,
		bool enforcePak,
		ElementsAddressEncodingProfile addressEncoding,
		string elementsVersion,
		int elementsNumericVersion,
		int elementsProtocolVersion,
		string messageStart,
		int defaultPort,
		string signblockScriptSha256,
		string fedpegScriptSha256,
		string scopeMarker,
		string ctFixtureEligibility)
	{
		_canonicalCbor = canonicalCbor;
		CanonicalCborSha256 = canonicalCborSha256;
		ManifestId = manifestId;
		ManifestSchema = manifestSchema;
		ProductNetworkId = productNetworkId;
		ChainRpcName = chainRpcName;
		GenesisBlockHash = genesisBlockHash;
		RawGenesisSha256 = rawGenesisSha256;
		RawGenesisSize = rawGenesisSize;
		GenesisUtxoSetDigest = genesisUtxoSetDigest;
		GenesisUtxoCount = genesisUtxoCount;
		_peggedAssetId = LiquidAssetId.ParseRpcHex(peggedAssetId, nameof(peggedAssetId));
		_requiredFeeAssetId = LiquidAssetId.ParseRpcHex(requiredFeeAssetId, nameof(requiredFeeAssetId));
		ParentGenesisHash = parentGenesisHash;
		HasParentChain = hasParentChain;
		EnforcePak = enforcePak;
		AddressEncoding = addressEncoding;
		ElementsVersion = elementsVersion;
		ElementsNumericVersion = elementsNumericVersion;
		ElementsProtocolVersion = elementsProtocolVersion;
		MessageStart = messageStart;
		DefaultPort = defaultPort;
		SignblockScriptSha256 = signblockScriptSha256;
		FedpegScriptSha256 = fedpegScriptSha256;
		ScopeMarker = scopeMarker;
		CtFixtureEligibility = ctFixtureEligibility;
	}

	private readonly byte[] _canonicalCbor;
	private readonly LiquidAssetId _peggedAssetId;
	private readonly LiquidAssetId _requiredFeeAssetId;

	public static ElementsPublicNetworkManifest LiquidMainnet { get; } =
		DecodeKnown(MainnetCanonicalCbor, MainnetCborSha256, MainnetManifestId, expectedSchema: 2);

	public static ElementsPublicNetworkManifest LiquidTestnet { get; } =
		DecodeKnown(TestnetCanonicalCbor, TestnetCborSha256, TestnetManifestId, expectedSchema: 2);

	/// <summary>
	/// Resolves a reviewed manifest by its domain-separated manifest id (ordinal). The catalog is
	/// the reviewed allowlist; an id with no reviewed manifest throws fail-closed. Never fabricates
	/// or parses an unreviewed manifest.
	/// </summary>
	public static ElementsPublicNetworkManifest GetByManifestId(string manifestId)
	{
		ArgumentException.ThrowIfNullOrEmpty(manifestId);
		if (StringComparer.Ordinal.Equals(manifestId, LiquidMainnet.ManifestId))
		{
			return LiquidMainnet;
		}
		if (StringComparer.Ordinal.Equals(manifestId, LiquidTestnet.ManifestId))
		{
			return LiquidTestnet;
		}

		throw new ElementsNetworkManifestException(
			"No reviewed Elements public-network manifest exists for the supplied manifest id.");
	}

	public string CanonicalCborSha256 { get; }
	public string ManifestId { get; }
	public int ManifestSchema { get; }
	public string ProductNetworkId { get; }
	public string ChainRpcName { get; }
	public string GenesisBlockHash { get; }
	public string RawGenesisSha256 { get; }
	public ulong RawGenesisSize { get; }
	public string GenesisUtxoSetDigest { get; }
	public ulong GenesisUtxoCount { get; }
	public string PeggedAssetId => _peggedAssetId.CanonicalRpcHex;
	public string RequiredFeeAssetId => _requiredFeeAssetId.CanonicalRpcHex;
	public string ParentGenesisHash { get; }
	public bool HasParentChain { get; }
	public bool EnforcePak { get; }
	public ElementsAddressEncodingProfile AddressEncoding { get; }
	public string ElementsVersion { get; }
	public int ElementsNumericVersion { get; }
	public int ElementsProtocolVersion { get; }
	public string ExpectedSubversion => $"/Elements Core:{ElementsVersion}/";
	public string MessageStart { get; }
	public int DefaultPort { get; }
	public string SignblockScriptSha256 { get; }
	public string FedpegScriptSha256 { get; }
	public string ScopeMarker { get; }
	public string CtFixtureEligibility { get; }

	public static ElementsPublicNetworkManifest ParseReviewed(ReadOnlySpan<byte> canonicalCbor)
	{
		if (canonicalCbor.Length is 0 or > MaxManifestBytes)
		{
			throw new ElementsNetworkManifestException("A reviewed Elements public-network manifest must be between one and 4096 bytes.");
		}
		if (canonicalCbor.SequenceEqual(MainnetCanonicalCbor))
		{
			return LiquidMainnet;
		}
		if (canonicalCbor.SequenceEqual(TestnetCanonicalCbor))
		{
			return LiquidTestnet;
		}

		throw new ElementsNetworkManifestException("The Elements public-network manifest is not in the reviewed byte allowlist.");
	}

	public byte[] ExportCanonicalCbor() => [.. _canonicalCbor];

	public ElementsManifestBoundObservation BindNodeObservation(ElementsNodeStatus nodeStatus)
	{
		ArgumentNullException.ThrowIfNull(nodeStatus);
		var mismatches = new List<string>();

		AddMismatch(mismatches, "chain", nodeStatus.Chain, ChainRpcName);
		AddMismatch(mismatches, "genesis_block_hash", nodeStatus.GenesisBlockHash, GenesisBlockHash);
		try
		{
			LiquidAssetId observedPeggedAsset = LiquidAssetId.ParseRpcHex(nodeStatus.PeggedAsset, nameof(nodeStatus.PeggedAsset));
			if (observedPeggedAsset != _peggedAssetId)
			{
				mismatches.Add("pegged_asset");
			}
		}
		catch (ArgumentException)
		{
			mismatches.Add("pegged_asset");
		}
		AddMismatch(mismatches, "parent_blockhash", nodeStatus.ParentGenesisBlockHash, ParentGenesisHash);
		if (nodeStatus.EnforcePak != EnforcePak)
		{
			mismatches.Add("enforce_pak");
		}
		if (nodeStatus.Version != ElementsNumericVersion)
		{
			mismatches.Add("version");
		}
		if (nodeStatus.ProtocolVersion != ElementsProtocolVersion)
		{
			mismatches.Add("protocolversion");
		}
		AddMismatch(mismatches, "subversion", nodeStatus.Subversion, ExpectedSubversion);

		try
		{
			string fedpegScript = ElementsNodeStatus.RequireHex(nodeStatus.FedpegScript, nameof(nodeStatus.FedpegScript));
			string fedpegScriptSha256 = LowerHex(SHA256.HashData(Convert.FromHexString(fedpegScript)));
			AddMismatch(mismatches, "fedpeg_script_sha256", fedpegScriptSha256, FedpegScriptSha256);
		}
		catch (ArgumentException)
		{
			mismatches.Add("fedpeg_script_sha256");
		}

		if (mismatches.Count > 0)
		{
			throw new ElementsNodeMismatchException(mismatches);
		}

		return new ElementsManifestBoundObservation(this, nodeStatus);
	}

	private static ElementsPublicNetworkManifest DecodeKnown(
		byte[] canonicalCbor,
		string expectedCborSha256,
		string expectedManifestId,
		int expectedSchema)
	{
		string cborSha256 = LowerHex(SHA256.HashData(canonicalCbor));
		Require(StringComparer.Ordinal.Equals(cborSha256, expectedCborSha256), "canonical_cbor_sha256");

		byte[] idInput = new byte[ManifestIdDomain.Length + canonicalCbor.Length];
		ManifestIdDomain.CopyTo(idInput, 0);
		canonicalCbor.CopyTo(idInput, ManifestIdDomain.Length);
		string manifestId = LowerHex(SHA256.HashData(idInput));
		Require(StringComparer.Ordinal.Equals(manifestId, expectedManifestId), "manifest_id");

		var reader = new CanonicalCborReader(canonicalCbor);
		reader.ReadArray(31, "manifest");
		int schema = ReadInt32(ref reader, "manifest_schema");
		string productNetworkId = reader.ReadText("product_network_id");
		string chainRpcName = reader.ReadText("chain_rpc_name");
		string genesisBlockHash = ReadHex(ref reader, 32, "genesis_block_hash");
		string rawGenesisSha256 = ReadHex(ref reader, 32, "raw_genesis_sha256");
		ulong rawGenesisSize = reader.ReadUnsigned("raw_genesis_size");
		string genesisUtxoSetDigest = ReadHex(ref reader, 32, "genesis_utxo_set_digest");
		ulong genesisUtxoCount = reader.ReadUnsigned("genesis_utxo_count");
		string peggedAssetId = ReadHex(ref reader, 32, "pegged_asset_id");
		string requiredFeeAssetId = ReadHex(ref reader, 32, "required_fee_asset_id");
		string parentGenesisHash = ReadHex(ref reader, 32, "parent_genesis_hash");
		bool hasParentChain = reader.ReadBoolean("has_parent_chain");
		bool enforcePak = reader.ReadBoolean("enforce_pak");

		reader.ReadArray(5, "address_encoding_profile");
		var addressEncoding = new ElementsAddressEncodingProfile(
			P2PkhPrefix: ReadByte(ref reader, "address_encoding_profile.p2pkh"),
			P2ShPrefix: ReadByte(ref reader, "address_encoding_profile.p2sh"),
			ConfidentialPrefix: ReadByte(ref reader, "address_encoding_profile.confidential"),
			Bech32Hrp: reader.ReadText("address_encoding_profile.bech32_hrp"),
			Blech32Hrp: reader.ReadText("address_encoding_profile.blech32_hrp"));

		string elementsVersion = reader.ReadText("elements_version");
		int elementsProtocolVersion = ReadInt32(ref reader, "elements_protocol_version");

		reader.ReadArray(8, "capability_profile");
		string capabilityProfile = reader.ReadText("capability_profile.profile");
		bool deterministicGenesisCodec = reader.ReadBoolean("capability_profile.genesis_codec");
		bool seededUtxoCodec = reader.ReadBoolean("capability_profile.seeded_utxo_codec");
		bool addressCodec = reader.ReadBoolean("capability_profile.address_codec");
		bool arbitraryFeeAsset = reader.ReadBoolean("capability_profile.arbitrary_fee_asset");
		bool generationApi = reader.ReadBoolean("capability_profile.generation_api");
		bool publicCtFixture = reader.ReadBoolean("capability_profile.public_ct_fixture");
		bool runtimeQualification = reader.ReadBoolean("capability_profile.runtime");

		reader.ReadArray(3, "generation_api_schema");
		string generationSchema = reader.ReadText("generation_api_schema.profile");
		reader.ReadArray(4, "generation_api_schema.fields");
		string generationField0 = reader.ReadText("generation_api_schema.fields.0");
		string generationField1 = reader.ReadText("generation_api_schema.fields.1");
		string generationField2 = reader.ReadText("generation_api_schema.fields.2");
		string generationField3 = reader.ReadText("generation_api_schema.fields.3");
		bool generationImplemented = reader.ReadBoolean("generation_api_schema.implemented");
		string generationStatus = reader.ReadText("generation_status");
		string messageStart = ReadHex(ref reader, 4, "message_start");
		int defaultPort = ReadInt32(ref reader, "default_port");
		string signblockScriptSha256 = ReadHex(ref reader, 32, "signblock_script_sha256");
		string fedpegScriptSha256 = ReadHex(ref reader, 32, "fedpeg_script_sha256");

		reader.ReadArray(7, "public_ct_sentinel_disposition");
		string ctNetwork = reader.ReadText("public_ct_sentinel_disposition.network");
		string ctStatus = reader.ReadText("public_ct_sentinel_disposition.status");
		string ctReason = reader.ReadText("public_ct_sentinel_disposition.reason");
		_ = reader.ReadByteString(32, "public_ct_sentinel_disposition.evidence_digest");
		bool ctContainingBlock = reader.ReadBoolean("public_ct_sentinel_disposition.containing_block");
		bool ctSurjectionProof = reader.ReadBoolean("public_ct_sentinel_disposition.surjection_proof");
		bool ctRangeProof = reader.ReadBoolean("public_ct_sentinel_disposition.range_proof");

		reader.ReadArray(6, "source_manual_codec_example");
		_ = reader.ReadText("source_manual_codec_example.name");
		_ = reader.ReadByteString(32, "source_manual_codec_example.raw_sha256");
		_ = reader.ReadByteString(32, "source_manual_codec_example.txid");
		_ = reader.ReadByteString(32, "source_manual_codec_example.wtxid");
		_ = reader.ReadText("source_manual_codec_example.class");
		reader.ReadArray(2, "source_manual_codec_example.output_proofs");
		for (int outputIndex = 0; outputIndex < 2; outputIndex++)
		{
			reader.ReadArray(2, $"source_manual_codec_example.output_proofs.{outputIndex}");
			Require(reader.ReadUnsigned($"source_manual_codec_example.output_proofs.{outputIndex}.surjection") == 0, "source_manual_codec_example.output_proofs");
			Require(reader.ReadUnsigned($"source_manual_codec_example.output_proofs.{outputIndex}.range") == 0, "source_manual_codec_example.output_proofs");
		}

		reader.ReadArray(6, "source_provenance");
		string sourceRelease = reader.ReadText("source_provenance.release");
		_ = reader.ReadByteString(20, "source_provenance.commit");
		_ = reader.ReadByteString(20, "source_provenance.tree");
		_ = reader.ReadByteString(32, "source_provenance.source_manifest");
		_ = reader.ReadByteString(32, "source_provenance.reseal");
		_ = reader.ReadByteString(32, "source_provenance.review");

		reader.ReadArray(6, "genesis_construction_profile");
		bool connectGenesisOutputs = reader.ReadBoolean("genesis_construction_profile.connect_outputs");
		string genesisMode = reader.ReadText("genesis_construction_profile.mode");
		bool genesisHashVerified = reader.ReadBoolean("genesis_construction_profile.genesis_hash");
		bool genesisMerkleVerified = reader.ReadBoolean("genesis_construction_profile.genesis_merkle");
		bool genesisUtxoVerified = reader.ReadBoolean("genesis_construction_profile.genesis_utxo");
		_ = reader.ReadUnsigned("genesis_construction_profile.initial_free_coins");
		_ = reader.ReadByteString(32, "address_vector_set_digest");

		ReadResourceProfile(ref reader);
		string scopeMarker = reader.ReadText("scope_marker");
		string ctFixtureEligibility = reader.ReadText("ct_fixture_eligibility");
		reader.EnsureFinished();

		Require(schema == expectedSchema, "manifest_schema");
		Require(StringComparer.Ordinal.Equals(peggedAssetId, requiredFeeAssetId), "required_fee_asset_id");
		Require(hasParentChain != IsZeroHash(parentGenesisHash), "has_parent_chain");
		Require(StringComparer.Ordinal.Equals(elementsVersion, expectedSchema == 2 ? "23.3.3" : "28.99.0"), "elements_version");
		Require(elementsProtocolVersion == 70016, "elements_protocol_version");
		Require(StringComparer.Ordinal.Equals(capabilityProfile, expectedSchema == 2 ? "ELEMENTS_23_3_3_SOURCE_IDENTITY_ONLY_V2" : "ELEMENTS_28_99_0_COMBINED_LOCAL_REGTEST_V1"), "capability_profile");
		Require(deterministicGenesisCodec && seededUtxoCodec && addressCodec, "capability_profile");
		Require(expectedSchema == 2
			? !arbitraryFeeAsset && !generationApi && !publicCtFixture && !runtimeQualification
			: !arbitraryFeeAsset && generationApi && !publicCtFixture && runtimeQualification, "capability_profile");
		Require(StringComparer.Ordinal.Equals(generationSchema, expectedSchema == 2 ? "GENERATION_API_SCHEMA_V1_REQUIRED_ABSENT" : "GENERATION_API_SCHEMA_V2"), "generation_api_schema");
		Require(StringComparer.Ordinal.Equals(generationField0, "startup_id"), "generation_api_schema");
		Require(StringComparer.Ordinal.Equals(generationField1, "chainstate_revision"), "generation_api_schema");
		Require(StringComparer.Ordinal.Equals(generationField2, expectedSchema == 2 ? "height" : "blocks"), "generation_api_schema");
		Require(StringComparer.Ordinal.Equals(generationField3, expectedSchema == 2 ? "besthash" : "bestblockhash"), "generation_api_schema");
		Require(generationImplemented == (expectedSchema == 3) && StringComparer.Ordinal.Equals(generationStatus, expectedSchema == 2 ? "GENERATION_ABA_INCOMPLETE" : "GENERATION_ABA_COMPLETE_BOUNDED_LOCAL_V1"), "generation_status");
		Require(StringComparer.Ordinal.Equals(ctNetwork, chainRpcName), "public_ct_sentinel_disposition");
		Require(StringComparer.Ordinal.Equals(ctStatus, "INCOMPLETE"), "public_ct_sentinel_disposition");
		Require(StringComparer.Ordinal.Equals(
			ctReason,
			expectedSchema == 2
				? "NO_ALLOWED_LOCAL_PUBLIC_CONTAINING_BLOCK_WITH_NONEMPTY_OUTPUT_SURJECTION_AND_RANGE_PROOFS"
				: "NO_REVIEWED_LOCAL_CONTAINING_BLOCK_WITH_NONEMPTY_OUTPUT_SURJECTION_AND_RANGE_PROOFS_FOR_EXACT_SOURCE_TREE"), "public_ct_sentinel_disposition");
		Require(!ctContainingBlock && !ctSurjectionProof && !ctRangeProof, "public_ct_sentinel_disposition");
		Require(StringComparer.Ordinal.Equals(sourceRelease, expectedSchema == 2 ? "elements-23.3.3" : "elements-28.99.0-59211529be66"), "source_provenance.release");
		Require(connectGenesisOutputs && genesisHashVerified && genesisMerkleVerified && genesisUtxoVerified, "genesis_construction_profile");
		Require(StringComparer.Ordinal.Equals(genesisMode, "elements"), "genesis_construction_profile.mode");
		Require(StringComparer.Ordinal.Equals(scopeMarker, expectedSchema == 2 ? "NODE-NETMANIFEST-001-SOURCE-ONLY-CT-INCOMPLETE-NO-GATE-CREDIT" : "CONTROLLED-REGTEST-MANIFEST-001-LOCAL-DEMO-ONLY-NO-GATE-CREDIT"), "scope_marker");
		Require(StringComparer.Ordinal.Equals(ctFixtureEligibility, expectedSchema == 2 ? "PUBLIC_CT_SENTINEL_INCOMPLETE" : "CONTROLLED_REGTEST_CT_FIXTURE_INCOMPLETE"), "ct_fixture_eligibility");
		ValidateNetworkTuple(productNetworkId, chainRpcName, hasParentChain, enforcePak, addressEncoding, defaultPort);

		return new ElementsPublicNetworkManifest(
			canonicalCbor: [.. canonicalCbor],
			canonicalCborSha256: cborSha256,
			manifestId,
			manifestSchema: schema,
			productNetworkId,
			chainRpcName,
			genesisBlockHash,
			rawGenesisSha256,
			rawGenesisSize,
			genesisUtxoSetDigest,
			genesisUtxoCount,
			peggedAssetId,
			requiredFeeAssetId,
			parentGenesisHash,
			hasParentChain,
			enforcePak,
			addressEncoding,
			elementsVersion,
			elementsNumericVersion: expectedSchema == 2 ? 230303 : 289900,
			elementsProtocolVersion,
			messageStart,
			defaultPort,
			signblockScriptSha256,
			fedpegScriptSha256,
			scopeMarker,
			ctFixtureEligibility);
	}

	private static void ReadResourceProfile(ref CanonicalCborReader reader)
	{
		reader.ReadArray(22, "resource_profile");
		Require(StringComparer.Ordinal.Equals(reader.ReadText("resource_profile.profile"), "NODE_NETMANIFEST_BOUNDED_RESOURCES_V1"), "resource_profile");
		ulong[] expected =
		[
			4096,
			65536,
			32,
			16777216,
			8388608,
			131072,
			8,
			256,
			512,
			8,
			64,
			128,
			65536,
			16,
			8388608,
			10000,
			4194304,
			10000,
			10000,
			1000,
			4194304,
		];
		for (int index = 0; index < expected.Length; index++)
		{
			Require(reader.ReadUnsigned($"resource_profile.{index + 1}") == expected[index], "resource_profile");
		}
	}

	private static void ValidateNetworkTuple(
		string productNetworkId,
		string chainRpcName,
		bool hasParentChain,
		bool enforcePak,
		ElementsAddressEncodingProfile addressEncoding,
		int defaultPort)
	{
		if (StringComparer.Ordinal.Equals(chainRpcName, "liquidv1"))
		{
			Require(StringComparer.Ordinal.Equals(productNetworkId, "LIQUID_MAINNET"), "product_network_id");
			Require(hasParentChain && enforcePak, "liquidv1 policy");
			Require(addressEncoding == new ElementsAddressEncodingProfile(57, 39, 12, "ex", "lq"), "address_encoding_profile");
			Require(defaultPort == 7042, "default_port");
			return;
		}
		if (StringComparer.Ordinal.Equals(chainRpcName, "liquidtestnet"))
		{
			Require(StringComparer.Ordinal.Equals(productNetworkId, "LIQUID_TESTNET"), "product_network_id");
			Require(!hasParentChain && !enforcePak, "liquidtestnet policy");
			Require(addressEncoding == new ElementsAddressEncodingProfile(36, 19, 23, "tex", "tlq"), "address_encoding_profile");
			Require(defaultPort == 18891, "default_port");
			return;
		}
		if (StringComparer.Ordinal.Equals(chainRpcName, "elementsregtest"))
		{
			Require(StringComparer.Ordinal.Equals(productNetworkId, "LIQUID_REGTEST_CONTROLLED"), "product_network_id");
			Require(hasParentChain && !enforcePak, "elementsregtest policy");
			Require(addressEncoding == new ElementsAddressEncodingProfile(235, 75, 4, "ert", "el"), "address_encoding_profile");
			Require(defaultPort == 18444, "default_port");
			return;
		}

		throw new ElementsNetworkManifestException("The reviewed Elements manifest contains an unknown public network tuple.");
	}

	private static string ReadHex(ref CanonicalCborReader reader, int bytes, string field) =>
		LowerHex(reader.ReadByteString(bytes, field));

	private static int ReadInt32(ref CanonicalCborReader reader, string field)
	{
		ulong value = reader.ReadUnsigned(field);
		if (value > int.MaxValue)
		{
			throw new ElementsNetworkManifestException($"The reviewed Elements network manifest has an out-of-range value at '{field}'.");
		}
		return (int)value;
	}

	private static byte ReadByte(ref CanonicalCborReader reader, string field)
	{
		ulong value = reader.ReadUnsigned(field);
		if (value > byte.MaxValue)
		{
			throw new ElementsNetworkManifestException($"The reviewed Elements network manifest has an out-of-range value at '{field}'.");
		}
		return (byte)value;
	}

	private static bool IsZeroHash(string value) => value.AsSpan().IndexOfAnyExcept('0') < 0;

	private static string LowerHex(ReadOnlySpan<byte> value) =>
		Convert.ToHexString(value).ToLower(CultureInfo.InvariantCulture);

	private static void Require(bool condition, string field)
	{
		if (!condition)
		{
			throw new ElementsNetworkManifestException($"The reviewed Elements network manifest violates '{field}'.");
		}
	}

	private static void AddMismatch(List<string> mismatches, string field, string actual, string expected)
	{
		if (!StringComparer.Ordinal.Equals(actual, expected))
		{
			mismatches.Add(field);
		}
	}
}

public sealed record ElementsAddressEncodingProfile(
	byte P2PkhPrefix,
	byte P2ShPrefix,
	byte ConfidentialPrefix,
	string Bech32Hrp,
	string Blech32Hrp);

public enum ElementsNodeManifestBindingLevel
{
	SelfReportedManifestTupleObservationOnly = 0,
}

public sealed class ElementsManifestBoundObservation
{
	internal ElementsManifestBoundObservation(
		ElementsPublicNetworkManifest manifest,
		ElementsNodeStatus nodeStatus)
	{
		Manifest = manifest;
		NodeStatus = nodeStatus;
	}

	public ElementsPublicNetworkManifest Manifest { get; }
	public ElementsNodeStatus NodeStatus { get; }
	public ElementsNodeManifestBindingLevel BindingLevel => ElementsNodeManifestBindingLevel.SelfReportedManifestTupleObservationOnly;
	public bool HasArtifactSourceAttestation => false;
	public bool HasEffectiveFeeAssetObservation => false;
	public bool HasAtomicGenerationObservation => false;
	public bool HasRuntimeQualification => false;
	public bool HasPublicCtFixtureQualification => false;
}

public sealed class ElementsNetworkManifestException : FormatException
{
	public ElementsNetworkManifestException(string message)
		: base(message)
	{
	}
}
