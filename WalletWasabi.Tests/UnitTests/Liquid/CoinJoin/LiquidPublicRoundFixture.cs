using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using WalletWasabi.Liquid.CoinJoin.Native;

namespace WalletWasabi.Liquid.CoinJoin.Client.Internal;

internal sealed record LiquidPublicRoundFixture(
	byte[] PreblindPset,
	byte[] FinalPset,
	byte[] PreblindStateDigest,
	byte[] FinalStateDigest)
{
	internal static LiquidPublicRoundFixture Load(string directory, LiquidCoinJoinNativeOperations native)
	{
		if (directory is null || native is null)
			throw new ArgumentNullException();
		string root = Path.GetFullPath(directory);
		if (!Directory.Exists(root) || Path.GetFileName(root) is "" || Directory.GetFiles(root).Length != 4)
			throw new FormatException("The fixture directory must contain exactly four regular files.");
		string[] names = ["manifest.json", "SHA256SUMS", "preblind.pset", "final.pset"];
		var files = names.ToDictionary(name => name, name => ReadRegular(root, name));
		ValidateSums(files);
		using JsonDocument document = JsonDocument.Parse(files["manifest.json"], new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
		JsonElement manifest = document.RootElement;
		RequireKeys(manifest, "schema limitation profile network_hex genesis_hex round_hex asset_hex fee role_map_hex inputs outputs states files verified_ops assembled_txid_wire_hex assembled_state_digest");
		if (manifest.GetProperty("schema").GetString() != "wlcj-public-round-v1" || manifest.GetProperty("profile").GetInt32() != 1)
			throw new FormatException("Unsupported public fixture schema.");
		string limitation = manifest.GetProperty("limitation").GetString() ?? "";
		if (!limitation.Contains("participant-owned", StringComparison.Ordinal) || !limitation.Contains("op13", StringComparison.Ordinal) || !limitation.Contains("different round", StringComparison.Ordinal))
			throw new FormatException("Fixture limitation is incomplete.");
		byte[] asset = Hex(manifest, "asset_hex", 32);
		byte[] genesis = Hex(manifest, "genesis_hex", 32);
		byte[] roleMap = Hex(manifest, "role_map_hex", 14);
		byte[] roundId = HexAny(manifest.GetProperty("round_hex").GetString()!, 256);
		if (!roleMap.AsSpan().SequenceEqual(LiquidLocalCoinJoinRound.Join(LiquidLocalCoinJoinRound.U32(2), LiquidLocalCoinJoinRound.U32(0), new byte[] { 1 }, LiquidLocalCoinJoinRound.U32(1), new byte[] { 2 })))
			throw new FormatException("Fixture role map is not the exact two-participant map.");
		if (!manifest.GetProperty("network_hex").GetString()!.Equals(Convert.ToHexString("elements-liquid-mainnet"u8.ToArray()).ToLowerInvariant(), StringComparison.Ordinal) || !manifest.GetProperty("verified_ops").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(Enumerable.Range(1, 13)))
			throw new FormatException("Fixture network or operation profile is invalid.");
		long fee = manifest.GetProperty("fee").GetProperty("explicit_value").GetInt64();
		if (fee <= 0 || fee > 21_000_000_000_000L || Hex(manifest, "assembled_txid_wire_hex", 32).Length != 32)
			throw new FormatException("Fixture fee or assembled transaction metadata is invalid.");
		ValidateArtifact(manifest, files, "preblind.pset");
		ValidateArtifact(manifest, files, "final.pset");
		// Manifest metadata is untrusted descriptive data. These consistency checks do not
		// bind amounts, ownership, or transaction identity to the PSETs. In particular,
		// public canonicalization cannot establish confidential input amounts without openings.
		ValidateParticipantMetadata(manifest, "inputs", asset);
		ValidateParticipantMetadata(manifest, "outputs", asset);
		var inputs = ReadParticipantMetadata(manifest, "inputs");
		var outputs = ReadParticipantMetadata(manifest, "outputs");
		if (inputs.Select(x => x.Index).Order().SequenceEqual([0, 1]) == false || outputs.Select(x => x.Index).Order().SequenceEqual([0, 1]) == false ||
			inputs.Select(x => x.Role).Order().SequenceEqual([1, 2]) == false || outputs.Select(x => x.Role).Order().SequenceEqual([1, 2]) == false ||
			inputs.Sum(x => x.ExplicitValue) - outputs.Sum(x => x.ExplicitValue) != fee)
			throw new FormatException("Participant indices, roles, or fee accounting are contradictory.");
		var states = manifest.GetProperty("states").EnumerateArray().Select(ReadState).ToArray();
		if (states.Length != 3)
			throw new FormatException("State count is invalid.");
		if (!Hex(manifest, "assembled_state_digest", 32).AsSpan().SequenceEqual(HexValue(states[2].Digest, 32)))
			throw new FormatException("Assembled state digest does not match the final state.");
		var contextPlan = new LiquidLocalRoundPlan([], Convert.FromHexString(manifest.GetProperty("network_hex").GetString()!), genesis, asset, roundId, []);
		byte[][] expectedContexts = [
			LiquidLocalCoinJoinRound.StateContext(contextPlan, 1, 1, 1, null),
			LiquidLocalCoinJoinRound.StateContext(contextPlan, 2, 1, 2, HexValue(states[0].Digest, 32)),
			LiquidLocalCoinJoinRound.StateContext(contextPlan, 3, 2, 3, HexValue(states[1].Digest, 32))];
		for (int i = 0; i < states.Length; i++)
			if (!HexAny(states[i].Context, 1_000_000).AsSpan().SequenceEqual(expectedContexts[i]))
				throw new FormatException("State context does not match the canonical round identity.");
		if (states[0].Phase != 1 || states[0].Role != 1 || states[0].Ordinal != 1 || states[1].Phase != 2 || states[1].Role != 1 || states[1].Ordinal != 2 || states[2].Phase != 3 || states[2].Role != 2 || states[2].Ordinal != 3 || states[0].Predecessor is not null || states[1].Predecessor != Convert.ToHexString(HexValue(states[0].Digest, 32)).ToLowerInvariant() || states[2].Predecessor != Convert.ToHexString(HexValue(states[1].Digest, 32)).ToLowerInvariant())
			throw new FormatException("State predecessor chain is invalid.");
		if (states[0].File != "preblind.pset" || states[2].File != "final.pset" || states[1].File is not null)
			throw new FormatException("State artifact associations are invalid.");
		ValidatePset(native, files["preblind.pset"], HexAny(states[0].Context, 1_000_000), HexValue(states[0].Digest, 32));
		ValidatePset(native, files["final.pset"], HexAny(states[2].Context, 1_000_000), HexValue(states[2].Digest, 32));
		// Only these two PSET/digest pairs were checked by native canonicalization.
		return new(files["preblind.pset"], files["final.pset"], HexValue(states[0].Digest, 32), HexValue(states[2].Digest, 32));
	}

	private static byte[] ReadRegular(string root, string name)
	{
		string path = Path.Combine(root, name);
		if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			throw new FormatException("Fixture contains a missing or linked file.");
		byte[] bytes = File.ReadAllBytes(path);
		if (bytes.Length is 0 or > 1_048_576) throw new FormatException("Fixture file exceeds its bound.");
		return bytes;
	}

	private static void ValidateSums(IReadOnlyDictionary<string, byte[]> files)
	{
		var expected = files.Keys.Where(name => name != "SHA256SUMS").Select(name => Convert.ToHexString(SHA256.HashData(files[name])).ToLowerInvariant() + "  " + name).Order(StringComparer.Ordinal);
		string sums = System.Text.Encoding.ASCII.GetString(files["SHA256SUMS"]);
		if (!sums.EndsWith('\n') || !sums[..^1].Split('\n').Order(StringComparer.Ordinal).SequenceEqual(expected)) throw new FormatException("SHA256SUMS mismatch.");
	}

	private static void ValidateArtifact(JsonElement manifest, IReadOnlyDictionary<string, byte[]> files, string name)
	{
		JsonElement row = manifest.GetProperty("files").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
		byte[] bytes = files[name];
		if (row.GetProperty("bytes").GetInt32() != bytes.Length || row.GetProperty("sha256").GetString() != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() || bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual(new byte[] { 0x70, 0x73, 0x65, 0x74, 0xff })) throw new FormatException("PSET artifact hash or header is invalid.");
	}

	private static void ValidatePset(LiquidCoinJoinNativeOperations native, byte[] pset, byte[] context, byte[] digest)
	{
		LiquidCoinJoinNativeOperations.Response response = native.Canonicalize(pset, context);
		// Operation 1 returns the canonical projection, not serialized PSET bytes.
		// The state digest is the stable validation result across that projection.
		if (response.Fields[0].Length == 0) throw new FormatException("Native canonical projection is empty.");
		if (!response.Fields[1].AsSpan().SequenceEqual(digest)) throw new FormatException("PSET state digest does not match the manifest.");
	}

	private static void ValidateParticipantMetadata(JsonElement manifest, string name, byte[] asset)
	{
		JsonElement rows = manifest.GetProperty(name);
		if (rows.GetArrayLength() != 2) throw new FormatException("Fixture participant count is invalid.");
		foreach (JsonElement row in rows.EnumerateArray())
		{
			RequireKeys(row, name == "inputs" ? "index role txid_wire_hex vout script_hex spend_public_key_hex receiver_public_key_hex asset_hex explicit_value asset_commitment_hex value_commitment_hex nonce_public_key_hex rangeproof_hex surjection_proof_hex asset_proof_hex" : "index role blinder_index script_hex spend_public_key_hex receiver_public_key_hex asset_hex explicit_value asset_commitment_hex value_commitment_hex nonce_public_key_hex rangeproof_hex surjection_proof_hex asset_proof_hex value_proof_hex");
			if (row.GetProperty("index").GetInt32() is < 0 or > 1 || row.GetProperty("role").GetInt32() is < 1 or > 2 || !Hex(row, "asset_hex", 32).AsSpan().SequenceEqual(asset) || row.GetProperty("explicit_value").GetInt64() <= 0 || row.GetProperty("explicit_value").GetInt64() > 21_000_000_000_000L) throw new FormatException("Participant metadata is out of bounds.");
			foreach (string field in new[] { "spend_public_key_hex", "receiver_public_key_hex", "nonce_public_key_hex" }) if (Hex(row, field, 33)[0] is not (2 or 3)) throw new FormatException("Public key encoding is invalid.");
		}
	}

	private static (int Index, int Role, long ExplicitValue)[] ReadParticipantMetadata(JsonElement m, string name) => m.GetProperty(name).EnumerateArray().Select(x => (x.GetProperty("index").GetInt32(), x.GetProperty("role").GetInt32(), x.GetProperty("explicit_value").GetInt64())).ToArray();
	private static LiquidPublicRoundState ReadState(JsonElement x) { RequireKeys(x, "name file context_hex digest phase role ordinal predecessor"); HexAny(x.GetProperty("context_hex").GetString()!, 1_000_000); HexValue(x.GetProperty("digest").GetString()!, 32); return new(x.GetProperty("name").GetString()!, x.GetProperty("file").ValueKind == JsonValueKind.Null ? null : x.GetProperty("file").GetString(), x.GetProperty("context_hex").GetString()!, x.GetProperty("digest").GetString()!, x.GetProperty("phase").GetByte(), x.GetProperty("role").GetByte(), x.GetProperty("ordinal").GetUInt32(), x.GetProperty("predecessor").ValueKind == JsonValueKind.Null ? null : x.GetProperty("predecessor").GetString()); }
	private static byte[] Hex(JsonElement parent, string name, int size) => HexValue(parent.GetProperty(name).GetString()!, size);
	private static byte[] HexValue(string value, int size) { if (value.Length != size * 2 || !value.All(Uri.IsHexDigit)) throw new FormatException("Invalid hexadecimal public field."); try { return Convert.FromHexString(value); } catch (FormatException) { throw new FormatException("Invalid hexadecimal public field."); } }
	private static byte[] HexAny(string value, int maxBytes) { if (value.Length == 0 || value.Length > maxBytes * 2 || value.Length % 2 != 0 || !value.All(Uri.IsHexDigit)) throw new FormatException("Invalid hexadecimal public field."); return Convert.FromHexString(value); }
	private static void RequireKeys(JsonElement value, string expected) { if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(x => x.Name).OrderBy(x => x).SequenceEqual(expected.Split(' ').OrderBy(x => x))) throw new FormatException("Unknown or missing public fixture field."); }
	private sealed record LiquidPublicRoundState(string Name, string? File, string Context, string Digest, byte Phase, byte Role, uint Ordinal, string? Predecessor);
}
