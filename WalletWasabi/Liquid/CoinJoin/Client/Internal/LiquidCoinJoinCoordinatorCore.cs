using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using WabiSabi.Crypto.ZeroKnowledge;
using WalletWasabi.Liquid.CoinJoin.Protocol;
using WalletWasabi.Serialization;

namespace WalletWasabi.Liquid.CoinJoin.Client.Internal;

internal sealed record LiquidCoinJoinAdmission(
	string RoundId, string ParticipantId, string NetworkManifestId, string GenesisHash,
	string PeggedAssetId, string IssuerRole, long Amount, long Delta, string RequestFingerprint);

internal interface ILiquidCoinJoinAdmissionVerifier
{
	// Verify ownership and atomically consume fresh backing authorization for this exact request.
	// Prior participant admission alone must never authorize another positive delta.
	bool VerifyAdmission(LiquidCoinJoinAdmission admission);
}

internal sealed class LiquidCoinJoinCoordinatorCore
{
	private readonly LiquidCoinJoinRoundParameters _parameters;
	private readonly CredentialIssuer _issuer;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Dictionary<string, (ImmutableArray<MAC> IssuedCredentials, ImmutableArray<Proof> Proofs)> _completedResponses = new(StringComparer.Ordinal);
	private readonly HashSet<string> _admittedParticipants = new(StringComparer.Ordinal);
	private readonly ILiquidCoinJoinAdmissionVerifier? _admissionVerifier;
	private readonly bool _allowZeroBootstrap;
	private bool _terminal;

	public LiquidCoinJoinCoordinatorCore(LiquidCoinJoinRoundParameters parameters, CredentialIssuerSecretKey issuerKey, WasabiRandom random, long maxAmount,
		ILiquidCoinJoinAdmissionVerifier? admissionVerifier = null, bool allowZeroBootstrap = false)
	{
		ArgumentNullException.ThrowIfNull(parameters);
		_parameters = parameters;
		_issuer = new CredentialIssuer(issuerKey, random, maxAmount);
		_admissionVerifier = admissionVerifier;
		_allowZeroBootstrap = allowZeroBootstrap;
	}

	public async Task<CredentialsResponse> IssueAsync(LiquidCoinJoinCredentialRequestEnvelope envelope, CancellationToken cancellationToken)
	{
		Validate(envelope);
		var fingerprint = Fingerprint(envelope);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// Completed responses remain retrievable even if a later operation terminated the round.
			if (_completedResponses.TryGetValue(fingerprint, out var completed))
				return new CredentialsResponse(completed.IssuedCredentials.ToArray(), completed.Proofs.ToArray());
			if (_terminal) throw new InvalidOperationException("The round is terminal after an issuer failure.");
			if (envelope.Delta > 0)
			{
				if (!_admittedParticipants.Contains(envelope.ParticipantId) && _admittedParticipants.Count >= _parameters.OwnerCount)
					throw new InvalidOperationException("The round has reached its maximum number of positive owners.");
				if (_admissionVerifier is null ||
					!_admissionVerifier.VerifyAdmission(new(envelope.RoundId, envelope.ParticipantId, envelope.NetworkManifestId,
						envelope.GenesisHash, envelope.PeggedAssetId, envelope.IssuerRole, envelope.Amount, envelope.Delta, fingerprint)))
				{
					throw new InvalidOperationException("Positive issuance requires fresh, verified request-bound admission.");
				}
			}
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				// HandleRequestAsync is not transactional. Any failure from here abandons the round.
				var response = await _issuer.HandleRequestAsync(envelope.Request, cancellationToken).ConfigureAwait(false);
				// Shipped MAC/proof values are immutable; retain no caller-visible mutable collections.
				completed = (response.IssuedCredentials.ToImmutableArray(), response.Proofs.ToImmutableArray());
				_completedResponses.Add(fingerprint, completed);
				if (envelope.Delta > 0) _admittedParticipants.Add(envelope.ParticipantId);
				return new CredentialsResponse(completed.IssuedCredentials.ToArray(), completed.Proofs.ToArray());
			}
			catch
			{
				_terminal = true;
				throw;
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	private void Validate(LiquidCoinJoinCredentialRequestEnvelope envelope)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		ArgumentNullException.ThrowIfNull(envelope.Request);
		if (!StringComparer.Ordinal.Equals(envelope.RoundId, _parameters.RoundId) ||
			!StringComparer.Ordinal.Equals(envelope.IssuerRole, _parameters.IssuerRole) ||
			!StringComparer.Ordinal.Equals(envelope.NetworkManifestId, _parameters.NetworkManifestId) ||
			!StringComparer.Ordinal.Equals(envelope.GenesisHash, _parameters.GenesisHash) ||
			!StringComparer.Ordinal.Equals(envelope.PeggedAssetId, _parameters.PeggedAssetId))
			throw new InvalidOperationException("Credential request is bound to a different round.");
		if (String.IsNullOrWhiteSpace(envelope.ParticipantId)) throw new InvalidOperationException("Participant identity is required.");
		if (envelope.Delta != envelope.Request.Delta || envelope.Amount != Math.Max(0, envelope.Request.Delta) || envelope.Amount > _issuer.MaxAmount)
			throw new InvalidOperationException("Admission amount must equal the positive request delta.");
		if (envelope.Request is ZeroCredentialsRequest && !_allowZeroBootstrap)
			throw new InvalidOperationException("Zero bootstrap is not enabled.");
	}

	internal static string Fingerprint(LiquidCoinJoinCredentialRequestEnvelope envelope)
	{
		var (kind, request) = envelope.Request switch
		{
			RealCredentialsRequest real => ("real", Encode.RealCredentialsRequest(real)),
			ZeroCredentialsRequest zero => ("zero", Encode.ZeroCredentialsRequest(zero)),
			_ => throw new InvalidOperationException("Unsupported credential request type.")
		};
		using var bytes = new MemoryStream();
		using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
		{
			// Length-prefixed strings and fixed-width numbers avoid delimiter ambiguities.
			writer.Write("liquid-credential-request-v1");
			writer.Write(envelope.RoundId);
			writer.Write(envelope.IssuerRole);
			writer.Write(envelope.NetworkManifestId);
			writer.Write(envelope.GenesisHash);
			writer.Write(envelope.PeggedAssetId);
			writer.Write(envelope.ParticipantId);
			writer.Write(envelope.Amount);
			writer.Write(envelope.Delta);
			writer.Write(kind);
			writer.Write(request.ToJsonString());
		}
		return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
	}
}
