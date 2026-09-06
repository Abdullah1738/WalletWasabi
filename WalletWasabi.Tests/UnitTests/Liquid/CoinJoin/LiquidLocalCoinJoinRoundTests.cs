using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using WalletWasabi.Liquid.CoinJoin.Client.Internal;
using WalletWasabi.Liquid.CoinJoin.Native;
using Xunit;
using static WalletWasabi.Liquid.CoinJoin.Client.Internal.LiquidLocalCoinJoinRound;
using SHA256 = System.Security.Cryptography.SHA256;

namespace WalletWasabi.Tests.UnitTests.Liquid.CoinJoin;

public sealed class LiquidLocalCoinJoinRoundTests
{
	[NativeFixtureFact("WLCJ_PRIVATE_ROUND_FIXTURE_DIR")]
	public async Task TwoOwnersCompleteOneNativeRoundWithRealAmountAndWeightCredentials()
	{
		using var fixture = new PrivateFixture();
		var trace = new List<uint>();
		var native = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> response, out ulong length) =>
		{
			if (!response.IsEmpty) trace.Add(BinaryPrimitives.ReadUInt32BigEndian(request[8..]));
			return LiquidCoinJoinNativeBinding.Execute(request, response, out length);
		});
		using var alice = new Participant(fixture, 0, native);
		using var bob = new Participant(fixture, 1, native);
		var assembledBody = TransactionBody(fixture.AssembledTransaction);
		byte[] independentlyDerivedTxid = SHA256.HashData(SHA256.HashData(assembledBody.Body));
		Assert.True(independentlyDerivedTxid.AsSpan().SequenceEqual(fixture.Txid), "Fixture txid does not match its assembled transaction body.");
		var round = new LiquidLocalCoinJoinRound(native);
		var result = await round.RunAsync(fixture.Plan, alice, bob);
		Assert.True(result.FinalPset.AsSpan().SequenceEqual(fixture.FinalPset), "The evolving round must reproduce the final public PSET.");
		Assert.True(result.StateDigest.AsSpan().SequenceEqual(fixture.Digest));
		Assert.True(result.TransactionId.AsSpan().SequenceEqual(fixture.Txid));
		var actual = TransactionBody(result.Transaction);
		var expected = TransactionBody(fixture.AssembledTransaction);
		Assert.True(actual.Body.AsSpan().SequenceEqual(expected.Body), "Transaction body changed during managed signing.");
		Assert.True(SHA256.HashData(SHA256.HashData(actual.Body)).AsSpan().SequenceEqual(result.TransactionId));
		Assert.Equal(2, actual.Witnesses.Count);
		for (int i = 0; i < 2; i++)
		{
			Assert.Equal(2, actual.Witnesses[i].Length);
			Assert.Equal(0x41, actual.Witnesses[i][0][^1]);
			Assert.True(actual.Witnesses[i][1].AsSpan().SequenceEqual(fixture.Plan.Participants[i].PublicKey));
		}
		Assert.Equal(new uint[] { 1, 4, 1, 5, 6 }, trace.Take(5));
		foreach (uint op in Enumerable.Range(1, 13).Select(x => (uint)x)) Assert.Contains(op, trace);
		Assert.Equal(2, trace.Count(x => x == 8));
		Assert.Equal(2, trace.Count(x => x == 9));
		Assert.Equal(2, trace.Count(x => x == 13));
		Assert.Equal(2, trace.Count(x => x == 11));
		foreach (var participant in new[] { alice, bob })
		{
			Assert.Equal(new[] { "amount:bootstrap", "weight:bootstrap", "amount:issue", "weight:issue", "amount:reissue", "weight:reissue", "amount:consume", "weight:consume" }, participant.CredentialTrace);
			Assert.Empty(participant.Amount!.Credentials);
			Assert.Empty(participant.Weight!.Credentials);
			Assert.Equal(1, participant.Openings);
		}
		await Assert.ThrowsAsync<InvalidOperationException>(() => round.RunAsync(fixture.Plan, alice, bob));
	}

	[NativeFixtureFact("WLCJ_PRIVATE_ROUND_FIXTURE_DIR")]
	public async Task BothRolesRejectWrongOpeningsProofBindingsAndSigningKeysBeforeConsumption()
	{
		using var fixture = new PrivateFixture();
		var native = new LiquidCoinJoinNativeOperations();
		foreach (int role in new[] { 0, 1 })
		foreach (string fault in new[] { "input-witness", "receiver", "input-proof", "output-proof", "stale-context", "balance", "signer", "refuse" })
		{
			using var alice = new Participant(fixture, 0, native) { Fault = role == 0 ? fault : "" };
			using var bob = new Participant(fixture, 1, native) { Fault = role == 1 ? fault : "" };
			var round = new LiquidLocalCoinJoinRound(native);
			await Assert.ThrowsAnyAsync<Exception>(() => round.RunAsync(fixture.Plan, alice, bob));
			Assert.DoesNotContain("amount:consume", alice.CredentialTrace);
			Assert.DoesNotContain("amount:consume", bob.CredentialTrace);
			await Assert.ThrowsAsync<InvalidOperationException>(() => round.RunAsync(fixture.Plan, alice, bob));
		}
	}

	[NativeFixtureFact("WLCJ_PRIVATE_ROUND_FIXTURE_DIR")]
	public async Task ChangedFeesOutputProofAssociationAndOwnershipFailClosed()
	{
		using var fixture = new PrivateFixture();
		foreach (int role in new[] { 0, 1 })
		foreach (string fault in new[] { "fee", "association", "owner" })
		{
			var facts = fixture.Plan.Participants.ToArray();
			facts[role] = fault switch
			{
				"fee" => facts[role] with { OutputAmount = facts[role].OutputAmount - 1 },
				"association" => facts[role] with { RangeProof = facts[1 - role].RangeProof },
				_ => facts[role] with { PublicKey = facts[1 - role].PublicKey }
			};
			var native = new LiquidCoinJoinNativeOperations();
			using var alice = new Participant(fixture, 0, native);
			using var bob = new Participant(fixture, 1, native);
			await Assert.ThrowsAnyAsync<Exception>(() => new LiquidLocalCoinJoinRound(native).RunAsync(fixture.Plan with { Participants = facts }, alice, bob));
			Assert.DoesNotContain("amount:consume", alice.CredentialTrace);
			Assert.DoesNotContain("amount:consume", bob.CredentialTrace);
		}
	}

	private sealed class Participant : ILiquidLocalRoundParticipant, IDisposable
	{
		private readonly PrivateFixture _fixture;
		private readonly int _index;
		private readonly LiquidCoinJoinNativeOperations _native;
		private readonly byte[] _receiver;
		private readonly byte[] _spend;
		private readonly byte[] _abf;
		private readonly byte[] _vbf;
		private byte[]? _opening;
		public string Fault { get; init; } = "";
		public LiquidCoinJoinCredentialCore? Amount { get; private set; }
		public LiquidCoinJoinCredentialCore? Weight { get; private set; }
		public List<string> CredentialTrace { get; } = [];
		public int Openings { get; private set; }
		public Participant(PrivateFixture fixture, int index, LiquidCoinJoinNativeOperations native)
		{
			(_fixture, _index, _native) = (fixture, index, native);
			var secrets = fixture.Secrets.RootElement.GetProperty("participants")[index];
			_receiver = Hex(secrets, "receiver_secret_key_hex");
			_spend = Hex(secrets, "spend_secret_key_hex");
			_abf = Hex(secrets, "input_asset_bf_hex");
			_vbf = Hex(secrets, "input_value_bf_hex");
		}
		public byte[] Blind(byte[] original, byte[] roles, byte[]? intermediate)
		{
			byte[] secrets = Join(U32((uint)_index), _fixture.Plan.Asset, _abf, U64(_fixture.Plan.Participants[_index].InputAmount), _vbf);
			try
			{
				if (Fault == "input-witness") secrets[^1] ^= 1;
				return _index == 0 ? _native.BlindNonLast(original, roles, secrets, _fixture.Entropy[0]).Fields[0] : _native.BlindLast(original, roles, intermediate!, secrets, _fixture.Entropy[1]).Fields[0];
			}
			finally
			{
				CryptographicOperations.ZeroMemory(secrets);
				if (intermediate is not null) CryptographicOperations.ZeroMemory(intermediate);
			}
		}
		public void Start(CredentialIssuerParameters amount, CredentialIssuerParameters weight)
		{
			Amount = new(amount, new SecureRandom());
			Weight = new(weight, new SecureRandom(), 8191);
		}
		public (Guid Id, ICredentialsRequest Request) Request(string issuer, long value, bool consume)
		{
			var core = issuer == "amount" ? Amount! : Weight!;
			return consume ? core.CreateConsumption() : core.CreateIssuance(value);
		}
		public void Accept(string issuer, Guid id, CredentialsResponse response, bool consume)
		{
			var core = issuer == "amount" ? Amount! : Weight!;
			var before = core.Credentials.Sum(x => x.Value);
			var result = core.AcceptResponse(id, response);
			if (consume) core.CommitConsumption(id, result);
			CredentialTrace.Add(issuer + ":" + (consume ? "consume" : core.Credentials.Sum(x => x.Value) == 0 ? "bootstrap" : before == 0 ? "issue" : "reissue"));
		}
		public byte[] ProveAmount(Guid id, byte[] pset, byte[] context, bool output)
		{
			if (output)
			{
				var facts = _fixture.Plan.Participants[_index];
				byte[] receiver = _receiver.ToArray();
				try
				{
					if (Fault == "receiver") receiver[0] ^= 1;
					_opening = _native.OpenOutput(pset, U32((uint)_index), receiver, _fixture.Txid, facts.OutputScript, _fixture.Plan.Asset, U64(facts.OutputAmount)).Fields[0];
					Assert.True(_opening.Length == 104 && _opening.AsSpan(0, 32).SequenceEqual(_fixture.Plan.Asset));
					Assert.Equal(facts.OutputAmount, BinaryPrimitives.ReadInt64BigEndian(_opening.AsSpan(32, 8)));
					Openings++;
				}
				finally { CryptographicOperations.ZeroMemory(receiver); }
			}
			return Amount!.WithRequestedAmountWitness(id, (value, ma, r1) =>
			{
				var facts = _fixture.Plan.Participants[_index];
				byte[] entropy = RandomNumberGenerator.GetBytes(32);
				try
				{
					if (Fault == "stale-context") context[^1] ^= 1;
					byte[] proof = output ? _native.EqualityProofOutput(pset, context, ma, U64(value), r1, _opening!.AsMemory(72, 32), entropy, facts.RangeProof, facts.SurjectionProof).Fields[0] : _native.EqualityProof(pset, context, ma, U64(value), r1, _vbf, entropy).Fields[0];
					if (Fault == (output ? "output-proof" : "input-proof")) proof[^1] ^= 1;
					return proof;
				}
				finally { CryptographicOperations.ZeroMemory(entropy); }
			});
		}
		public byte[] ProveBalance(byte[] pset, byte[] context)
		{
			var facts = _fixture.Plan.Participants[_index];
			// Effective blinder includes v*abf, not just the value blinder.
			var residual = new Scalar(_vbf) + new Scalar((ulong)facts.InputAmount) * new Scalar(_abf) + (new Scalar(_opening!.AsSpan(72, 32)) + new Scalar((ulong)facts.OutputAmount) * new Scalar(_opening.AsSpan(40, 32))).Negate();
			byte[] scalar = residual.ToBytes();
			byte[] entropy = RandomNumberGenerator.GetBytes(32);
			try
			{
				byte[] proof = _native.BalanceProof(pset, context, scalar, entropy).Fields[0];
				if (Fault == "balance") proof[^1] ^= 1;
				return proof;
			}
			finally { CryptographicOperations.ZeroMemory(scalar); CryptographicOperations.ZeroMemory(entropy); }
		}
		public byte[] Sign(byte[] pset, byte[] context, byte[] digest, byte[] authorization, byte[] owned)
		{
			if (Fault == "refuse") throw new InvalidOperationException("Participant refused signing.");
			var view = _native.OwnedDigests(pset, context, digest, authorization, owned);
			var row = view.Fields[2];
			var facts = _fixture.Plan.Participants[_index];
			Assert.True(row.Length == 110 && BinaryPrimitives.ReadUInt32BigEndian(row) == 1 && BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(4)) == _index);
			Assert.True(row.AsSpan(8, 32).SequenceEqual(facts.Outpoint));
			Assert.True(row.AsSpan(44, 33).SequenceEqual(facts.PublicKey));
			Assert.True(view.Fields[1].AsSpan().SequenceEqual(digest));
			Assert.Equal(0x41, row[109]);
			using var key = Fault == "signer" ? new Key() : new Key(_spend);
			byte[] signature = [.. key.Sign(new uint256(row.AsSpan(77, 32).ToArray(), lendian: true)).ToDER(), 0x41];
			return Join(view.Fields[0], U32((uint)_index), facts.PublicKey, U32((uint)signature.Length), signature);
		}
		public void Dispose()
		{
			foreach (byte[] secret in new[] { _receiver, _spend, _abf, _vbf }) CryptographicOperations.ZeroMemory(secret);
			if (_opening is not null) CryptographicOperations.ZeroMemory(_opening);
		}
	}

	// Runtime-only fixture. Never embeds witness bytes or prints private JSON.
	private sealed class PrivateFixture : IDisposable
	{
		private readonly List<byte[]> _buffers = [];
		public JsonDocument Secrets { get; }
		public LiquidLocalRoundPlan Plan { get; }
		public byte[][] Entropy { get; }
		public byte[] FinalPset { get; }
		public byte[] Digest { get; }
		public byte[] Txid { get; }
		public byte[] AssembledTransaction { get; }
		public PrivateFixture()
		{
			string path = Path.GetFullPath(Environment.GetEnvironmentVariable("WLCJ_PRIVATE_ROUND_FIXTURE_DIR")!);
			if (!path.Split(Path.DirectorySeparatorChar).Contains("tmp")) throw new InvalidDataException("Private test material must remain under tmp.");
			const UnixFileMode publicAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & publicAccess) != 0))
				throw new InvalidDataException("Private fixture directory must be unlinked and owner-only.");
			byte[] Read(string name)
			{
				var info = new FileInfo(Path.Combine(path, name));
				if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is <= 0 or > 1_048_576) throw new InvalidDataException("Invalid bounded fixture file.");
				if (!OperatingSystem.IsWindows() && (info.UnixFileMode & publicAccess) != 0)
					throw new InvalidDataException("Private fixture files must not be group/world accessible.");
				byte[] bytes = File.ReadAllBytes(info.FullName);
				_buffers.Add(bytes);
				return bytes;
			}
			string[] names = ["preblind.pset", "intermediate.pset", "final.pset", "private-manifest.json", "manifest.json", "secrets.json", "round-facts.json", "requests.bin", "responses.bin"];
			var files = names.ToDictionary(x => x, Read);
			var sums = System.Text.Encoding.ASCII.GetString(Read("SHA256SUMS")).TrimEnd('\n').Split('\n').Order(StringComparer.Ordinal);
			Assert.True(sums.SequenceEqual(files.Select(x => Convert.ToHexStringLower(SHA256.HashData(x.Value)) + "  " + x.Key).Order(StringComparer.Ordinal)), "Private fixture checksums failed.");
			using var manifest = JsonDocument.Parse(files["manifest.json"]);
			using var privateManifest = JsonDocument.Parse(files["private-manifest.json"]);
			Assert.True(privateManifest.RootElement.GetProperty("private_test_only").GetBoolean());
			Assert.Equal("wlcj-private-round-v1", privateManifest.RootElement.GetProperty("schema").GetString());
			Assert.Equal(LiquidCoinJoinNativeBinding.NativeCommit, privateManifest.RootElement.GetProperty("source_commit").GetString());
			Assert.Equal("59e8957a619a006a3753337180261f4eef05e6a9", privateManifest.RootElement.GetProperty("source_tree_hash").GetString());
			Assert.Equal("manifest.json", privateManifest.RootElement.GetProperty("public_manifest_file").GetString());
			Secrets = JsonDocument.Parse(files["secrets.json"]);
			using var facts = JsonDocument.Parse(files["round-facts.json"]);
			Entropy = facts.RootElement.GetProperty("entropy_hex").EnumerateArray().Select(x => Convert.FromHexString(x.GetString()!)).ToArray();
			_buffers.AddRange(Entropy);
			var m = manifest.RootElement;
			Plan = new(files["preblind.pset"], Hex(m, "network_hex"), Hex(m, "genesis_hex"), Hex(m, "asset_hex"), Hex(m, "round_hex"), Enumerable.Range(0, 2).Select(i =>
			{
				var input = m.GetProperty("inputs")[i];
				var output = m.GetProperty("outputs")[i];
				return new LiquidLocalParticipantFacts(input.GetProperty("explicit_value").GetInt64(), output.GetProperty("explicit_value").GetInt64(), Hex(input, "txid_wire_hex"), input.GetProperty("vout").GetUInt32(), Hex(input, "spend_public_key_hex"), Hex(output, "script_hex"), Hex(output, "rangeproof_hex"), Hex(output, "surjection_proof_hex"));
			}).ToArray());
			FinalPset = files["final.pset"];
			Digest = Hex(m.GetProperty("states")[2], "digest");
			Txid = Hex(m, "assembled_txid_wire_hex");
			byte[] responses = files["responses.bin"];
			byte[]? assembled = null;
			for (int offset = 0; offset < responses.Length;)
			{
				int length = checked(16 + (int)BinaryPrimitives.ReadUInt32BigEndian(responses.AsSpan(offset + 12)));
				if (BinaryPrimitives.ReadUInt32BigEndian(responses.AsSpan(offset + 8)) == 12)
					assembled = LiquidCoinJoinFrame.Decode(responses.AsSpan(offset, length)).Fields[2];
				offset += length;
			}
			AssembledTransaction = assembled ?? throw new InvalidDataException("Missing reference assembly.");
		}
		public void Dispose() { Secrets.Dispose(); foreach (byte[] bytes in _buffers) CryptographicOperations.ZeroMemory(bytes); }
	}

	private static byte[] Hex(JsonElement parent, string property) => Convert.FromHexString(parent.GetProperty(property).GetString()!);

	private static (byte[] Body, List<byte[][]> Witnesses) TransactionBody(byte[] tx)
	{
		using var stream = new MemoryStream(tx);
		using var reader = new BinaryReader(stream);
		ulong Size() => reader.ReadByte() switch { 253 => reader.ReadUInt16(), 254 => reader.ReadUInt32(), 255 => reader.ReadUInt64(), var n => n };
		byte[] Vector() => reader.ReadBytes(checked((int)Size()));
		byte[][] Stack() => Enumerable.Range(0, checked((int)Size())).Select(_ => Vector()).ToArray();
		void Confidential(int explicitSize) { byte prefix = reader.ReadByte(); reader.ReadBytes(prefix == 0 ? 0 : prefix == 1 ? explicitSize : 32); }
		reader.ReadInt32();
		Assert.Equal(1, reader.ReadByte());
		int inputs = checked((int)Size());
		for (int i = 0; i < inputs; i++)
		{
			reader.ReadBytes(32);
			Assert.Equal(0u, reader.ReadUInt32() & 0xc0000000u);
			Vector(); reader.ReadUInt32();
		}
		int outputs = checked((int)Size());
		for (int i = 0; i < outputs; i++) { Confidential(32); Confidential(8); Confidential(32); Vector(); }
		reader.ReadUInt32();
		byte[] body = tx[..checked((int)stream.Position)];
		body[4] = 0;
		var witnesses = new List<byte[][]>();
		for (int i = 0; i < inputs; i++) { Vector(); Vector(); witnesses.Add(Stack()); Assert.Empty(Stack()); }
		for (int i = 0; i < outputs; i++) { Vector(); Vector(); }
		Assert.Equal(stream.Length, stream.Position);
		return (body, witnesses);
	}
}
