using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Liquid.CoinJoin.Native;
using WalletWasabi.Liquid.CoinJoin.Protocol;

namespace WalletWasabi.Liquid.CoinJoin.Client.Internal;

internal sealed record LiquidLocalParticipantFacts(long InputAmount, long OutputAmount, byte[] Outpoint, uint Vout, byte[] PublicKey, byte[] OutputScript, byte[] RangeProof, byte[] SurjectionProof);
internal sealed record LiquidLocalRoundPlan(byte[] PreblindPset, byte[] Network, byte[] Genesis, byte[] Asset, byte[] RoundId, LiquidLocalParticipantFacts[] Participants);
internal sealed record LiquidLocalRoundResult(byte[] FinalPset, byte[] StateDigest, byte[] Transaction, byte[] TransactionId);

// These callbacks run in participant custody. In particular, blinding, op13,
// credential randomness and signing keys never become coordinator arguments.
internal interface ILiquidLocalRoundParticipant
{
	byte[] Blind(byte[] original, byte[] roles, byte[]? intermediate);
	void Start(CredentialIssuerParameters amount, CredentialIssuerParameters weight);
	(Guid Id, ICredentialsRequest Request) Request(string issuer, long value, bool consume);
	void Accept(string issuer, Guid id, CredentialsResponse response, bool consume);
	byte[] ProveAmount(Guid id, byte[] pset, byte[] context, bool output);
	byte[] ProveBalance(byte[] pset, byte[] context);
	byte[] Sign(byte[] pset, byte[] context, byte[] digest, byte[] authorization, byte[] owned);
}

/// <summary>Single-use, two-owner, in-process round. No broadcast or wallet custody.</summary>
internal sealed class LiquidLocalCoinJoinRound
{
	private const long MaxAmount = (1L << 51) - 1;
	private readonly LiquidCoinJoinNativeOperations _native;
	private int _started;

	internal LiquidLocalCoinJoinRound(LiquidCoinJoinNativeOperations native) => _native = native;

	internal async Task<LiquidLocalRoundResult> RunAsync(LiquidLocalRoundPlan source, ILiquidLocalRoundParticipant alice, ILiquidLocalRoundParticipant bob, CancellationToken cancellationToken = default)
	{
		if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The local round is single-use, including after failure.");
		ArgumentNullException.ThrowIfNull(alice);
		ArgumentNullException.ThrowIfNull(bob);
		if (ReferenceEquals(alice, bob)) throw new ArgumentException("Two distinct participant roles are required.");
		// Freeze caller-owned public state before crossing any participant callback.
		var plan = source with { PreblindPset = source.PreblindPset.ToArray(), Network = source.Network.ToArray(), Genesis = source.Genesis.ToArray(), Asset = source.Asset.ToArray(), RoundId = source.RoundId.ToArray(), Participants = source.Participants.Select(p => p with { Outpoint = p.Outpoint.ToArray(), PublicKey = p.PublicKey.ToArray(), OutputScript = p.OutputScript.ToArray(), RangeProof = p.RangeProof.ToArray(), SurjectionProof = p.SurjectionProof.ToArray() }).ToArray() };
		if (plan.Participants.Length != 2 || plan.Genesis.Length != 32 || plan.Asset.Length != 32 || plan.Network.Length is 0 or > 256 || plan.RoundId.Length is 0 or > 256 || plan.PreblindPset.Length is 0 or > 1_048_576 || plan.Participants.Any(p => p.InputAmount <= p.OutputAmount || p.OutputAmount <= 0 || p.InputAmount > MaxAmount || p.Outpoint.Length != 32 || p.PublicKey.Length != 33 || p.OutputScript.Length != 22))
			throw new ArgumentException("Invalid bounded two-participant plan.");
		var participants = new[] { alice, bob };
		byte[] roles = Join(U32(2), U32(0), new byte[] { 1 }, U32(1), new byte[] { 2 });
		byte[] preContext = StateContext(plan, 1, 1, 1, null);
		byte[] preDigest = _native.Canonicalize(plan.PreblindPset, preContext).Fields[1];
		cancellationToken.ThrowIfCancellationRequested();
		byte[] intermediate = alice.Blind(plan.PreblindPset.ToArray(), roles.ToArray(), null);
		byte[] finalPset;
		byte[] middleDigest;
		try
		{
			middleDigest = _native.Canonicalize(intermediate, StateContext(plan, 2, 1, 2, preDigest)).Fields[1];
			finalPset = bob.Blind(plan.PreblindPset.ToArray(), roles.ToArray(), intermediate.ToArray()).ToArray();
		}
		finally { CryptographicOperations.ZeroMemory(intermediate); }
		byte[] context = StateContext(plan, 3, 2, 3, middleDigest);
		byte[] digest = _native.FinalView(finalPset, context).Fields[1];
		long fee = plan.Participants.Sum(p => p.InputAmount - p.OutputAmount);
		var random = SecureRandom.Instance;
		var amountKey = new CredentialIssuerSecretKey(random);
		var weightKey = new CredentialIssuerSecretKey(random);
		var backing = new Backing();
		var amountParameters = Parameters("amount");
		var weightParameters = Parameters("weight");
		var amountIssuer = new LiquidCoinJoinCoordinatorCore(amountParameters, amountKey, random, MaxAmount, backing, allowZeroBootstrap: true);
		var weightIssuer = new LiquidCoinJoinCoordinatorCore(weightParameters, weightKey, random, 8191, backing, allowZeroBootstrap: true);
		var inputVerified = new bool[2];
		var outputVerified = new bool[2];
		var authorization = Join([U32(2), .. plan.Participants.Select((p, i) => Join(U32((uint)i), p.Outpoint, U32(p.Vout), p.PublicKey))]);
		for (int i = 0; i < 2; i++)
		{
			participants[i].Start(amountKey.ComputeCredentialIssuerParameters(), weightKey.ComputeCredentialIssuerParameters());
			await Exchange(i, "amount", 0, false).ConfigureAwait(false);
			await Exchange(i, "weight", 0, false).ConfigureAwait(false);
			await Exchange(i, "amount", plan.Participants[i].InputAmount, false, input: true).ConfigureAwait(false);
			inputVerified[i] = true;
			await Exchange(i, "weight", 1, false, input: true).ConfigureAwait(false);
		}
		for (int i = 0; i < 2; i++)
		{
			await Exchange(i, "amount", plan.Participants[i].OutputAmount, false, output: true).ConfigureAwait(false);
			await Exchange(i, "weight", 1, false).ConfigureAwait(false);
			byte[] balance = BalanceContext(plan, i, digest);
			Verify(_native.VerifyPartialBalance(finalPset, balance, participants[i].ProveBalance(finalPset.ToArray(), balance.ToArray())));
			outputVerified[i] = true;
		}
		// Obtain both owner signatures before irreversibly consuming credentials.
		var contributions = new byte[2][];
		for (int i = 0; i < 2; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			contributions[i] = participants[i].Sign(finalPset.ToArray(), context.ToArray(), digest.ToArray(), authorization.ToArray(), Join(U32(1), U32((uint)i)));
		}
		var assembled = _native.Assembly(finalPset, context, digest, authorization, Join([U32(2), .. contributions]));
		if (!assembled.Fields[1].AsSpan().SequenceEqual(digest)) throw new InvalidOperationException("Assembly changed the state digest.");
		for (int i = 0; i < 2; i++)
		{
			await Exchange(i, "amount", 0, true).ConfigureAwait(false);
			await Exchange(i, "weight", 0, true).ConfigureAwait(false);
		}
		return new(finalPset, digest, assembled.Fields[2], assembled.Fields[3]);

		LiquidCoinJoinRoundParameters Parameters(string issuer) => new(Convert.ToHexString(plan.RoundId), Encoding.UTF8.GetString(plan.Network), plan.Genesis, plan.Asset, fee, issuer);

		async Task Exchange(int index, string issuer, long value, bool consume, bool input = false, bool output = false)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var operation = participants[index].Request(issuer, value, consume);
			var parameters = issuer == "amount" ? amountParameters : weightParameters;
			var coordinator = issuer == "amount" ? amountIssuer : weightIssuer;
			long expectedDelta = consume ? -(issuer == "amount" ? plan.Participants[index].OutputAmount : 1) : input ? value : output ? plan.Participants[index].OutputAmount - plan.Participants[index].InputAmount : 0;
			if (operation.Request.Delta != expectedDelta || (consume && !outputVerified[index])) throw new InvalidOperationException("Credential debit is not bound to the verified output.");
			var envelope = new LiquidCoinJoinCredentialRequestEnvelope(parameters.RoundId, issuer, parameters.NetworkManifestId, parameters.GenesisHash, parameters.PeggedAssetId, index.ToString(), Math.Max(0, expectedDelta), expectedDelta, operation.Request);
			if ((input || output) && issuer == "amount")
			{
				if (operation.Request is not RealCredentialsRequest real) throw new InvalidOperationException("Amount registration requires a real credential request.");
				byte[] ma = real.Requested.Select(x => x.Ma).Aggregate((a, b) => a + b).ToBytes();
				byte[] registration = RegistrationContext(plan, index, digest, output);
				byte[] proof = participants[index].ProveAmount(operation.Id, finalPset.ToArray(), registration.ToArray(), output);
				var facts = plan.Participants[index];
				Verify(output ? _native.VerifyOutputRegistration(finalPset, registration, proof, ma, facts.RangeProof, facts.SurjectionProof) : _native.VerifyInputRegistration(finalPset, registration, proof, ma));
			}
			if (input)
			{
				if (issuer == "weight" && !inputVerified[index]) throw new InvalidOperationException("Output weight requires native-verified input admission.");
				backing.Authorize(envelope);
			}
			CredentialsResponse response;
			try
			{
				response = await coordinator.IssueAsync(envelope, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException($"Native-backed {issuer} credential exchange failed (input={input}, output={output}, consume={consume}, expectedDelta={expectedDelta}).", ex);
			}
			participants[index].Accept(issuer, operation.Id, response, consume);
		}
	}

	private sealed class Backing : ILiquidCoinJoinAdmissionVerifier
	{
		private string? _fingerprint;
		internal void Authorize(LiquidCoinJoinCredentialRequestEnvelope request) => _fingerprint = LiquidCoinJoinCoordinatorCore.Fingerprint(request);
		public bool VerifyAdmission(LiquidCoinJoinAdmission admission)
		{
			if (_fingerprint is null || _fingerprint != admission.RequestFingerprint) return false;
			_fingerprint = null;
			return true;
		}
	}

	internal static void Verify(LiquidCoinJoinNativeOperations.Response response)
	{
		if (!response.Fields[0].AsSpan().SequenceEqual("OK\0\0"u8)) throw new InvalidDataException("Native proof verification rejected.");
	}
	internal static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
	internal static byte[] U64(long value) { var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return bytes; }
	internal static byte[] Join(params byte[][] fields) => fields.SelectMany(x => x).ToArray();
	private static byte[] Field(byte[] bytes) => Join(U32((uint)bytes.Length), bytes);
	private static byte[] Prefix(LiquidLocalRoundPlan p) => Join(new byte[] { 1 }, Field(p.Network), p.Genesis, p.Asset);
	internal static byte[] StateContext(LiquidLocalRoundPlan p, byte phase, byte role, uint ordinal, byte[]? prior) => Join(Prefix(p), p.Asset, Field(p.RoundId), new[] { phase, role }, U32(ordinal), prior is null ? new byte[] { 0 } : Join(new byte[] { 1 }, prior));
	internal static byte[] RegistrationContext(LiquidLocalRoundPlan p, int i, byte[] digest, bool output) => Join(Prefix(p), Field(p.RoundId), new byte[] { 2, (byte)(i + 1) }, U32((uint)(i + 1)), new byte[] { output ? (byte)2 : (byte)1 }, U32((uint)i), digest);
	internal static byte[] BalanceContext(LiquidLocalRoundPlan p, int i, byte[] digest) => Join(Prefix(p), Field(p.RoundId), new byte[] { 2, (byte)(i + 1) }, U32((uint)(i + 1)), digest, U32(1), U32((uint)i), U32(1), U32((uint)i), U64(p.Participants[i].InputAmount - p.Participants[i].OutputAmount));
}
