using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using WabiSabi.Crypto.ZeroKnowledge;
using WalletWasabi.Liquid.CoinJoin.Client.Internal;
using WalletWasabi.Liquid.CoinJoin.Protocol;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Serialization;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.CoinJoin;

public sealed class LiquidCoinJoinCredentialCoreTests
{
	private const long MaxAmount = (1L << 51) - 1;

	[Fact]
	public async Task TwoParticipantsIssueReissueAndConsumeRealCredentials()
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var admission = new AdmissionVerifier();
		var coordinator = Coordinator(key, random, admission);
		var alice = Client(key, random);
		var bob = Client(key, random);
		await Issue(alice, coordinator, "alice", 0);
		await Issue(bob, coordinator, "bob", 0);
		await Issue(alice, coordinator, "alice", 1000, admission);
		await Issue(bob, coordinator, "bob", 2000, admission);
		await Issue(alice, coordinator, "alice", 1500, admission);
		Assert.Equal(3, admission.Calls);
		Assert.Equal(1500, alice.Credentials.Sum(item => item.Value));
		await Issue(alice, coordinator, "alice", 1500);
		Assert.Equal(3, admission.Calls);

		var before = alice.Credentials;
		var operation = alice.CreateConsumption();
		Assert.Equal(-1500, operation.Request.Delta);
		Assert.Throws<InvalidOperationException>(() => alice.CreateConsumption());
		Assert.Throws<InvalidOperationException>(() => alice.CreateIssuance(0));
		Assert.Throws<InvalidOperationException>(() => alice.CommitConsumption(operation.OperationId, Guid.NewGuid()));
		var response = await coordinator.IssueAsync(Envelope("alice", operation.Request), CancellationToken.None);
		Assert.Throws<InvalidOperationException>(() => alice.AcceptResponse(Guid.NewGuid(), response));
		var result = alice.AcceptResponse(operation.OperationId, response);
		Assert.Equal(before.ToArray(), alice.Credentials.ToArray());
		Assert.Throws<InvalidOperationException>(() => alice.CreateIssuance(0));
		Assert.Throws<InvalidOperationException>(() => alice.AcceptResponse(operation.OperationId, response));
		Assert.Throws<InvalidOperationException>(() => alice.CommitConsumption(Guid.NewGuid(), result));
		Assert.Throws<InvalidOperationException>(() => alice.CommitConsumption(operation.OperationId, Guid.NewGuid()));
		alice.CommitConsumption(operation.OperationId, result);
		Assert.Throws<InvalidOperationException>(() => alice.CommitConsumption(operation.OperationId, result));
		Assert.Empty(alice.Credentials);
		Assert.Equal(2000, bob.Credentials.Sum(item => item.Value));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RejectedResponsePreservesImmutableSnapshotsButTerminatesClient(bool consuming)
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var client = Client(key, random);
		var admission = new AdmissionVerifier();
		var coordinator = Coordinator(key, random, admission);
		await Issue(client, coordinator, "alice", 0);
		await Issue(client, coordinator, "alice", 10, admission);
		var before = client.Credentials.ToArray();
		var operation = consuming ? client.CreateConsumption() : client.CreateIssuance(10);
		var response = await coordinator.IssueAsync(Envelope("alice", operation.Request), CancellationToken.None);
		var proofs = response.Proofs.ToArray();
		proofs[0] = proofs[1];
		var invalid = new CredentialsResponse(response.IssuedCredentials, proofs);
		Assert.Throws<WabiSabiCryptoException>(() => client.AcceptResponse(operation.OperationId, invalid));
		Assert.Equal(before, client.Credentials.ToArray());
		Assert.Throws<InvalidOperationException>(() => client.AcceptResponse(operation.OperationId, response));
		Assert.Throws<InvalidOperationException>(() => client.CreateIssuance(0));
		Assert.Throws<InvalidOperationException>(() => client.CreateConsumption());
		Assert.Throws<InvalidOperationException>(() => client.CommitConsumption(operation.OperationId, Guid.NewGuid()));
	}

	[Fact]
	public async Task CompleteRequestFingerprintSeparatesParticipantsAndCachesExactRetries()
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var coordinator = Coordinator(key, random);
		var alice = Client(key, random);
		var bob = Client(key, random);
		var a = alice.CreateIssuance(0);
		var b = bob.CreateIssuance(0);
		// Use identical envelope metadata to prove content, not participant id, separates requests.
		var ea = Envelope("same", a.Request);
		var eb = Envelope("same", b.Request);
		Assert.NotEqual(LiquidCoinJoinCoordinatorCore.Fingerprint(ea), LiquidCoinJoinCoordinatorCore.Fingerprint(eb));
		var responses = await Task.WhenAll(coordinator.IssueAsync(ea, CancellationToken.None), coordinator.IssueAsync(ea, CancellationToken.None));
		Assert.Equal(Encode.CredentialsResponse(responses[0]).ToJsonString(), Encode.CredentialsResponse(responses[1]).ToJsonString());
		var bobResponse = await coordinator.IssueAsync(eb, CancellationToken.None);
		Assert.NotSame(responses[0], bobResponse);
		alice.AcceptResponse(a.OperationId, responses[0]);
		bob.AcceptResponse(b.OperationId, bobResponse);
		var zero = Assert.IsType<ZeroCredentialsRequest>(a.Request);
		var copy = ea with { Request = new ZeroCredentialsRequest(zero.Requested.ToArray(), zero.Proofs.ToArray()) };
		Assert.Equal(Encode.CredentialsResponse(responses[0]).ToJsonString(),
			Encode.CredentialsResponse(await coordinator.IssueAsync(copy, CancellationToken.None)).ToJsonString());

		var real = Assert.IsType<RealCredentialsRequest>(alice.CreateIssuance(100).Request);
		var envelope = Envelope("alice", real);
		var fingerprint = LiquidCoinJoinCoordinatorCore.Fingerprint(envelope);
		ICredentialsRequest[] mutations =
		[
			new RealCredentialsRequest(real.Delta + 1, real.Presented, real.Requested, real.Proofs),
			new RealCredentialsRequest(real.Delta, real.Presented.Reverse(), real.Requested, real.Proofs),
			new RealCredentialsRequest(real.Delta, real.Presented, real.Requested.Reverse(), real.Proofs),
			new RealCredentialsRequest(real.Delta, real.Presented, real.Requested, real.Proofs.Reverse())
		];
		foreach (var mutation in mutations)
			Assert.NotEqual(fingerprint, LiquidCoinJoinCoordinatorCore.Fingerprint(envelope with { Request = mutation }));
		Assert.NotEqual(LiquidCoinJoinCoordinatorCore.Fingerprint(ea), LiquidCoinJoinCoordinatorCore.Fingerprint(ea with
		{
			Request = new RealCredentialsRequest(0, zero.Presented, zero.Requested, zero.Proofs)
		}));
		Assert.Contains("BitCommitments", Encode.RealCredentialsRequest(real).ToJsonString());
	}

	[Fact]
	public async Task ThirdPositiveOwnerIsRejectedBeforeIssuerAndDoesNotTerminateRound()
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var admission = new AdmissionVerifier();
		var coordinator = Coordinator(key, random, admission);
		var alice = Client(key, random);
		var bob = Client(key, random);
		var charlie = Client(key, random);
		await Issue(alice, coordinator, "alice", 0);
		await Issue(bob, coordinator, "bob", 0);
		await Issue(charlie, coordinator, "charlie", 0);
		await Issue(alice, coordinator, "alice", 10, admission);
		await Issue(bob, coordinator, "bob", 20, admission);
		var operation = charlie.CreateIssuance(30);
		var envelope = Envelope("charlie", operation.Request);
		admission.Expected = Admission(envelope);
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(envelope, CancellationToken.None));
		Assert.Contains("maximum", error.Message);
		Assert.Equal(2, admission.Calls);
		// A rejection must not enter the nontransactional issuer, even for an invalid proof.
		var real = Assert.IsType<RealCredentialsRequest>(operation.Request);
		var invalid = envelope with { Request = new RealCredentialsRequest(real.Delta, real.Presented, real.Requested, real.Proofs.Reverse()) };
		var invalidError = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(invalid, CancellationToken.None));
		Assert.Contains("maximum", invalidError.Message);
		await Issue(alice, coordinator, "alice", 15, admission);
		Assert.Equal(15, alice.Credentials.Sum(item => item.Value));
		var consumption = bob.CreateConsumption();
		var response = await coordinator.IssueAsync(Envelope("bob", consumption.Request), CancellationToken.None);
		bob.CommitConsumption(consumption.OperationId, bob.AcceptResponse(consumption.OperationId, response));
		// Consumption does not free an already admitted participant slot.
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(envelope, CancellationToken.None));
		Assert.Equal(3, admission.Calls);
		Assert.Throws<InvalidOperationException>(() => charlie.CreateIssuance(30));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MutatingReturnedResponseDoesNotChangeExactReplay(bool positive)
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var admission = new AdmissionVerifier();
		var coordinator = Coordinator(key, random, admission);
		var client = Client(key, random);
		if (positive) await Issue(client, coordinator, "alice", 0);
		var operation = client.CreateIssuance(positive ? 10 : 0);
		var envelope = Envelope("alice", operation.Request);
		admission.Expected = Admission(envelope);
		var response = await coordinator.IssueAsync(envelope, CancellationToken.None);
		var expected = Encode.CredentialsResponse(response).ToJsonString();
		var issued = Assert.IsAssignableFrom<IList<MAC>>(response.IssuedCredentials);
		var proofs = Assert.IsAssignableFrom<IList<Proof>>(response.Proofs);
		issued[0] = issued[1];
		proofs[0] = proofs[1];
		Assert.NotEqual(expected, Encode.CredentialsResponse(response).ToJsonString());
		var replay = await coordinator.IssueAsync(envelope, CancellationToken.None);
		Assert.NotSame(response, replay);
		Assert.Equal(expected, Encode.CredentialsResponse(replay).ToJsonString());
		// A returned replay must also be detached from the retained cache.
		Assert.IsAssignableFrom<IList<Proof>>(replay.Proofs)[0] = replay.Proofs.Last();
		Assert.NotEqual(expected, Encode.CredentialsResponse(replay).ToJsonString());
		var nextReplay = await coordinator.IssueAsync(envelope, CancellationToken.None);
		Assert.Equal(expected, Encode.CredentialsResponse(nextReplay).ToJsonString());
		client.AcceptResponse(operation.OperationId, nextReplay);
		Assert.Equal(positive ? 10 : 0, client.Credentials.Sum(item => item.Value));
		Assert.Equal(positive ? 1 : 0, admission.Calls);
	}

	[Fact]
	public async Task EveryRoundBindingIsValidatedIndependently()
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var coordinator = Coordinator(key, random);
		var envelope = Envelope("alice", Client(key, random).CreateIssuance(0).Request);
		LiquidCoinJoinCredentialRequestEnvelope[] mutations =
		[
			envelope with { RoundId = "round-2" },
			envelope with { IssuerRole = "other" },
			envelope with { NetworkManifestId = "other" },
			envelope with { GenesisHash = "other" },
			envelope with { PeggedAssetId = "other" }
		];
		foreach (var mutation in mutations)
			await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(mutation, CancellationToken.None));
		await coordinator.IssueAsync(envelope, CancellationToken.None);
	}

	[Fact]
	public async Task EveryPositiveDeltaRequiresFreshBoundAdmissionAndExactRetriesAreCached()
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var admission = new AdmissionVerifier();
		var coordinator = Coordinator(key, random, admission);
		var client = Client(key, random);
		Assert.Throws<InvalidOperationException>(() => client.CreateIssuance(10));
		await Issue(client, coordinator, "alice", 0);
		var operation = client.CreateIssuance(10);
		Assert.Throws<InvalidOperationException>(() => client.CreateConsumption());
		Assert.Throws<InvalidOperationException>(() => client.CreateIssuance(10));
		var envelope = Envelope("alice", operation.Request);
		await Assert.ThrowsAsync<InvalidOperationException>(() => Coordinator(key, random).IssueAsync(envelope, CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(envelope with { Amount = 0 }, CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(envelope with { Delta = 0 }, CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(envelope, CancellationToken.None));
		Assert.Equal(1, admission.Calls);
		admission.Expected = Admission(envelope);
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(envelope with { ParticipantId = "bob" }, CancellationToken.None));
		var response = await coordinator.IssueAsync(envelope, CancellationToken.None);
		Assert.Equal(3, admission.Calls);
		Assert.Equal(Encode.CredentialsResponse(response).ToJsonString(),
			Encode.CredentialsResponse(await coordinator.IssueAsync(envelope, CancellationToken.None)).ToJsonString());
		Assert.Equal(3, admission.Calls);
		client.AcceptResponse(operation.OperationId, response);
		Assert.Equal(10, client.Credentials.Sum(item => item.Value));
		var reissue = client.CreateIssuance(20);
		var second = Envelope("alice", reissue.Request);
		Assert.Equal(10, second.Delta);
		// The first admission cannot authorize a new request, even for the same owner and delta.
		admission.Expected = Admission(envelope);
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(second, CancellationToken.None));
		Assert.Equal(4, admission.Calls);
		var expected = Admission(second);
		LiquidCoinJoinAdmission[] invalidAdmissions =
		[
			expected with { RoundId = "round-2" },
			expected with { ParticipantId = "bob" },
			expected with { NetworkManifestId = "other" },
			expected with { GenesisHash = "other" },
			expected with { PeggedAssetId = "other" },
			expected with { IssuerRole = "other" },
			expected with { Amount = expected.Amount + 1 },
			expected with { Delta = expected.Delta + 1 },
			expected with { RequestFingerprint = LiquidCoinJoinCoordinatorCore.Fingerprint(envelope) }
		];
		foreach (var invalidAdmission in invalidAdmissions)
		{
			admission.Expected = invalidAdmission;
			await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(second, CancellationToken.None));
		}
		Assert.Equal(4 + invalidAdmissions.Length, admission.Calls);
		Assert.Equal(10, client.Credentials.Sum(item => item.Value));
		Assert.Throws<InvalidOperationException>(() => client.CreateIssuance(20));
		admission.Expected = expected;
		var responses = await Task.WhenAll(coordinator.IssueAsync(second, CancellationToken.None), coordinator.IssueAsync(second, CancellationToken.None));
		Assert.Equal(5 + invalidAdmissions.Length, admission.Calls);
		Assert.Equal(Encode.CredentialsResponse(responses[0]).ToJsonString(), Encode.CredentialsResponse(responses[1]).ToJsonString());
		client.AcceptResponse(reissue.OperationId, responses[0]);
		Assert.Equal(20, client.Credentials.Sum(item => item.Value));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task IssuerFailureIsTerminalButCompletedResponsesRemainCached(bool positiveReissue)
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var admission = new AdmissionVerifier();
		var coordinator = Coordinator(key, random, admission);
		var client = Client(key, random);
		var bootstrap = client.CreateIssuance(0);
		var envelope = Envelope("alice", bootstrap.Request);
		var response = await coordinator.IssueAsync(envelope, CancellationToken.None);
		client.AcceptResponse(bootstrap.OperationId, response);
		if (positiveReissue)
		{
			var initial = client.CreateIssuance(10);
			envelope = Envelope("alice", initial.Request);
			admission.Expected = Admission(envelope);
			response = await coordinator.IssueAsync(envelope, CancellationToken.None);
			client.AcceptResponse(initial.OperationId, response);
		}
		var real = Assert.IsType<RealCredentialsRequest>(client.CreateIssuance(positiveReissue ? 20 : 0).Request);
		var invalid = Envelope("alice", new RealCredentialsRequest(real.Delta, real.Presented, real.Requested, real.Proofs.Reverse()));
		admission.Expected = Admission(invalid);
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.IssueAsync(invalid, CancellationToken.None));
		Assert.Equal(positiveReissue ? 2 : 0, admission.Calls);
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(invalid, CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(Envelope("alice", real), CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.IssueAsync(Envelope("bob", Client(key, random).CreateIssuance(0).Request), CancellationToken.None));
		Assert.Equal(Encode.CredentialsResponse(response).ToJsonString(),
			Encode.CredentialsResponse(await coordinator.IssueAsync(envelope, CancellationToken.None)).ToJsonString());
	}

	[Fact]
	public async Task BootstrapIsOptInAndCancellationBeforeIssuerDoesNotTerminateRound()
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var envelope = Envelope("alice", Client(key, random).CreateIssuance(0).Request);
		var closed = new LiquidCoinJoinCoordinatorCore(Parameters(), key, random, MaxAmount);
		await Assert.ThrowsAsync<InvalidOperationException>(() => closed.IssueAsync(envelope, CancellationToken.None));
		var coordinator = Coordinator(key, random);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.IssueAsync(envelope, new CancellationToken(true)));
		await coordinator.IssueAsync(envelope, CancellationToken.None);
	}

	private static LiquidCoinJoinRoundParameters Parameters() => new("round-1", ElementsPublicNetworkManifest.LiquidTestnet,
		ElementsPublicNetworkManifest.LiquidTestnet.GenesisBlockHash, ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId, 1, "credential-issuer", 2);

	private static LiquidCoinJoinCredentialCore Client(CredentialIssuerSecretKey key, SecureRandom random) => new(key.ComputeCredentialIssuerParameters(), random, MaxAmount);

	private static LiquidCoinJoinCoordinatorCore Coordinator(CredentialIssuerSecretKey key, SecureRandom random, ILiquidCoinJoinAdmissionVerifier? admission = null) =>
		new(Parameters(), key, random, MaxAmount, admission, allowZeroBootstrap: true);

	private static LiquidCoinJoinCredentialRequestEnvelope Envelope(string participant, ICredentialsRequest request)
	{
		var parameters = Parameters();
		return new(parameters.RoundId, parameters.IssuerRole, parameters.NetworkManifestId, parameters.GenesisHash, parameters.PeggedAssetId,
			participant, Math.Max(0, request.Delta), request.Delta, request);
	}

	private static LiquidCoinJoinAdmission Admission(LiquidCoinJoinCredentialRequestEnvelope envelope) => new(envelope.RoundId, envelope.ParticipantId,
		envelope.NetworkManifestId, envelope.GenesisHash, envelope.PeggedAssetId, envelope.IssuerRole, envelope.Amount, envelope.Delta,
		LiquidCoinJoinCoordinatorCore.Fingerprint(envelope));

	private static async Task Issue(LiquidCoinJoinCredentialCore client, LiquidCoinJoinCoordinatorCore coordinator, string participant, long amount, AdmissionVerifier? admission = null)
	{
		var operation = client.CreateIssuance(amount);
		var envelope = Envelope(participant, operation.Request);
		if (admission is not null) admission.Expected = Admission(envelope);
		var response = await coordinator.IssueAsync(envelope, CancellationToken.None);
		client.AcceptResponse(operation.OperationId, response);
	}

	private sealed class AdmissionVerifier : ILiquidCoinJoinAdmissionVerifier
	{
		public LiquidCoinJoinAdmission? Expected { get; set; }
		public int Calls { get; private set; }
		public bool VerifyAdmission(LiquidCoinJoinAdmission admission)
		{
			Calls++;
			if (admission != Expected) return false;
			Expected = null;
			return true;
		}
	}
}
