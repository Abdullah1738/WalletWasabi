using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;
#pragma warning disable CA2000

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletRefreshCommandServiceTests
{
	private const string WalletName = "alpha";

	[Fact]
	public async Task AcquisitionGenerationFenceTransientIsRetriedThenCommitsAsync()
	{
		// LIQUID-REFRESH-GENERATION-RETRY-001: a forward-block fence trip on the
		// refresh-observation acquisition ("node generation changed during the …")
		// is a retry signal, not a hard failure. The service re-acquires the whole
		// observation and commits the first internally-consistent one.
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		const string candidateId = "4444444444444444444444444444444444444444444444444444444444444444";
		ElementsWalletRefreshObservation rpcObservation = CandidateObservation(
			session.StateOwner.NodeExpectation,
			session.Manifest,
			candidateId);
		LiquidWalletObservationBatch batch = NativeBatch(candidateId, session.Manifest);
		const int transientsBeforeSuccess = 3;
		int acquisitionCalls = 0;
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, captured, suppliedId, cancellationToken) =>
			{
				acquisitionCalls++;
				if (acquisitionCalls <= transientsBeforeSuccess)
				{
					throw AcquisitionTransient();
				}
				return Task.FromResult(rpcObservation);
			},
			observeNative: _ => batch,
			save: request => LiquidWalletLoadSaveResult.CreateSaved(
				request.State.Revision, request.NextGeneration, request.ExternalIndexHighWater, request.InternalIndexHighWater),
			publish: (_, _, _) => true,
			stageObserver: _ => { });
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		LiquidWalletUiRefreshResult result = await command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None);

		Assert.Equal(transientsBeforeSuccess + 1, acquisitionCalls);
		Assert.Equal(LiquidWalletUiRefreshStatus.Committed, result.Status);
		Assert.Equal(1, result.CandidateCount);
		Assert.Equal(1, result.AppliedTransactionCount);
	}

	[Fact]
	public async Task AcquisitionGenerationFenceTransientExhaustionSurfacesAsync()
	{
		// The retry is BOUNDED: a genuinely unstable node (transient on every
		// attempt) surfaces the exception after exactly the bounded number of
		// attempts — no infinite loop.
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		int acquisitionCalls = 0;
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, captured, suppliedId, cancellationToken) =>
			{
				acquisitionCalls++;
				throw AcquisitionTransient();
			},
			observeNative: _ => throw new InvalidOperationException("Native must not run when acquisition never succeeds."),
			save: _ => throw new InvalidOperationException("Save must not run when acquisition never succeeds."),
			publish: (_, _, _) => throw new InvalidOperationException("Publish must not run when acquisition never succeeds."),
			stageObserver: _ => { });
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() => command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None));

		Assert.Equal(6, acquisitionCalls);
		Assert.Contains("node generation changed during the ", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task NonRetryableAcquisitionFailureIsNotRetriedAsync()
	{
		// Fail-closed is preserved: a non-fence RPC rejection — the
		// rollback/restart/inconsistency fence "node status did not match the
		// generation fence" and any generic RPC failure — is NOT a forward-block
		// transient. It surfaces immediately with no retry.
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		const string fatalMessage =
			"Elements RPC 'wallet refresh observation' returned an invalid result: node status did not match the generation fence.";
		int acquisitionCalls = 0;
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, captured, suppliedId, cancellationToken) =>
			{
				acquisitionCalls++;
				throw new ElementsRpcException(ElementsRpcFailureKind.Protocol, fatalMessage, method: "wallet refresh observation");
			},
			observeNative: _ => throw new InvalidOperationException("Native must not run for a fatal acquisition."),
			save: _ => throw new InvalidOperationException("Save must not run for a fatal acquisition."),
			publish: (_, _, _) => throw new InvalidOperationException("Publish must not run for a fatal acquisition."),
			stageObserver: _ => { });
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() => command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None));

		Assert.Equal(1, acquisitionCalls);
		Assert.Equal(fatalMessage, exception.Message);
	}

	private static ElementsRpcException AcquisitionTransient() =>
		new(
			ElementsRpcFailureKind.Protocol,
			"Elements RPC 'wallet refresh observation' returned an invalid result: node generation changed during the acquisition.",
			method: "wallet refresh observation");

	[Fact]
	public async Task NonemptyCandidateExecutesTypedPipelineInExactOrderAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		var stages = new List<string>();
		const string candidateId = "4444444444444444444444444444444444444444444444444444444444444444";
		ElementsWalletRefreshObservation rpcObservation = CandidateObservation(
			session.StateOwner.NodeExpectation,
			session.Manifest,
			candidateId);
		LiquidTransactionId nativeTransactionId = LiquidTransactionId.ParseRpcHex(candidateId);
		byte[] witnessBinding = Enumerable.Repeat((byte)7, 32).ToArray();
		LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
			LiquidKeyBranch.External,
			0);
		LiquidWalletObservationBatch nativeBatch = LiquidWalletObservationBatch.Create(
			[LiquidWalletTransactionObservation.Create(
				nativeTransactionId.ToConsensusBytes(),
				witnessBinding,
				[WalletWasabi.Liquid.Transactions.LiquidOutPoint.CreateSpendable(
					LiquidTransactionId.ParseRpcHex(new string('5', 64)), 0)],
				[LiquidOwnedOutputObservation.Create(
					nativeTransactionId.ToConsensusBytes(),
					0,
					witnessBinding,
					spendKey.GetScriptPubKey(),
					spendKey.GetCompressedPublicKey(),
					[0x02, .. Enumerable.Repeat((byte)2, 32)],
					LiquidKeyBranch.External,
					0,
					WalletWasabi.Liquid.Assets.LiquidAssetId.ParseRpcHex(session.Manifest.PeggedAssetId).ToConsensusBytes(),
					100)])]);
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, captured, suppliedId, cancellationToken) =>
			{
				stages.Add("rpc");
				return Task.FromResult(rpcObservation);
			},
			observeNative: request =>
			{
				stages.Add("native");
				Assert.Single(request.Candidates);
				return nativeBatch;
			},
			save: request =>
			{
				stages.Add("save");
				return LiquidWalletLoadSaveResult.CreateSaved(
					request.State.Revision,
					request.NextGeneration,
					request.ExternalIndexHighWater,
					request.InternalIndexHighWater);
			},
			publish: (publishedProvider, publishedSession, handoff) =>
			{
				stages.Add("publish");
				return true;
			},
			stageObserver: stages.Add);
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		LiquidWalletUiRefreshResult result = await command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None);

		Assert.Equal(
			["rpc", "derive/stage", "native", "sync", "project", "validate", "save", "install", "publish", "remove"],
			stages);
		Assert.Equal(LiquidWalletUiRefreshStatus.Committed, result.Status);
		Assert.Equal(1, result.CandidateCount);
		Assert.Equal(1, result.AppliedTransactionCount);
		Assert.Equal(1UL, result.ResultRevision);
		Assert.Equal(1UL, result.ResultGeneration);
		Assert.True(result.HandoffPublished);
	}

	[Fact]
	public async Task UnknownManifestHasNoReviewedDescriptorNetworkClassAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		const string candidateId = "6666666666666666666666666666666666666666666666666666666666666666";
		// A session whose manifest is none of the reviewed instances (a hand-constructed
		// schema-2-shaped foreign manifest) must fail closed at the network-class fence,
		// before any native observation or persistence.
		ElementsPublicNetworkManifest foreign = CreateForeignManifest();
		SetField(session, "_manifest", foreign);
		bool nativeCalled = false;
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, _, _, _) =>
				Task.FromResult(CandidateObservation(capturedSession.StateOwner.NodeExpectation, capturedSession.Manifest, candidateId)),
			observeNative: _ =>
			{
				nativeCalled = true;
				return NativeBatch(candidateId, foreign);
			},
			save: _ => throw new InvalidOperationException("Save must not run for an unreviewed manifest."),
			publish: (_, _, _) => true,
			stageObserver: _ => { });
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None));

		Assert.Equal("The refresh manifest has no reviewed descriptor network class.", exception.Message);
		Assert.False(nativeCalled);
	}

	[Fact]
	public async Task AcceptedSendRequiresCapturedMembershipAndForcesSuppliedIdFirstAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		const string olderId = "6666666666666666666666666666666666666666666666666666666666666666";
		const string suppliedId = "7777777777777777777777777777777777777777777777777777777777777777";
		session.RecordAcceptedTransactionId(olderId);
		int rpcCalls = 0;
		LiquidWalletRefreshCommandService.Dependencies missingDependencies = Dependencies(
			(_, _, _, _) =>
			{
				rpcCalls++;
				return Task.FromResult(EmptyObservation(session.StateOwner.NodeExpectation, session.Manifest));
			});
		var missingCommand = LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, missingDependencies);
		await Assert.ThrowsAsync<InvalidOperationException>(() => missingCommand(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.AcceptedSend, suppliedId),
			CancellationToken.None));
		Assert.Equal(0, rpcCalls);

		session.RecordAcceptedTransactionId(suppliedId);
		string? observedSuppliedId = null;
		IReadOnlyList<string>? capturedIds = null;
		LiquidWalletRefreshCommandService.Dependencies presentDependencies = Dependencies(
			(_, captured, forcedId, _) =>
			{
				observedSuppliedId = forcedId;
				capturedIds = captured.AcceptedTransactionIds;
				return Task.FromResult(EmptyObservation(session.StateOwner.NodeExpectation, session.Manifest));
			});
		var presentCommand = LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, presentDependencies);
		await Assert.ThrowsAsync<InvalidOperationException>(() => presentCommand(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.AcceptedSend, suppliedId),
			CancellationToken.None));
		Assert.Equal(suppliedId, observedSuppliedId);
		Assert.Equal([suppliedId, olderId], capturedIds);
	}

	[Fact]
	public async Task NativeAndSaveFailuresRetainSnapshotAndAcceptedIdsAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		const string acceptedId = "8888888888888888888888888888888888888888888888888888888888888888";
		session.RecordAcceptedTransactionId(acceptedId);
		object priorSnapshot = session.CaptureRefreshSnapshot();
		LiquidWalletRuntimeHandoff priorHandoff = session.PublicHandoff;
		LiquidAuthenticatedWalletStateOwner priorOwner = session.StateOwner;

		LiquidWalletRefreshCommandService.Dependencies nativeFailure = Dependencies(
			(_, _, _, _) => Task.FromResult(CandidateObservation(session.StateOwner.NodeExpectation, session.Manifest, acceptedId)),
			observeNative: _ => throw new InvalidOperationException("native failed"));
		var nativeCommand = LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, nativeFailure);
		await Assert.ThrowsAsync<InvalidOperationException>(() => nativeCommand(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.AcceptedSend, acceptedId),
			CancellationToken.None));
		Assert.Same(priorSnapshot, session.CaptureRefreshSnapshot());
		Assert.Same(priorOwner, session.StateOwner);
		Assert.Same(priorHandoff, session.PublicHandoff);
		Assert.Equal([acceptedId], session.GetRecordedAcceptedTransactionIds());

		LiquidWalletObservationBatch batch = NativeBatch(acceptedId, session.Manifest);
		LiquidWalletRefreshCommandService.Dependencies saveFailure = Dependencies(
			(_, _, _, _) => Task.FromResult(CandidateObservation(session.StateOwner.NodeExpectation, session.Manifest, acceptedId)),
			observeNative: _ => batch,
			save: _ => throw new InvalidOperationException("save failed"));
		var saveCommand = LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, saveFailure);
		await Assert.ThrowsAsync<InvalidOperationException>(() => saveCommand(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.AcceptedSend, acceptedId),
			CancellationToken.None));
		Assert.Same(priorSnapshot, session.CaptureRefreshSnapshot());
		Assert.Same(priorOwner, session.StateOwner);
		Assert.Same(priorHandoff, session.PublicHandoff);
		Assert.Equal([acceptedId], session.GetRecordedAcceptedTransactionIds());
	}

	[Fact]
	public async Task SuccessfulRefreshPreservesAcceptedIdRecordedAfterCaptureAndReportsDetachedPublicationAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		const string acceptedId = "9999999999999999999999999999999999999999999999999999999999999999";
		const string concurrentId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
		session.RecordAcceptedTransactionId(acceptedId);
		LiquidWalletObservationBatch batch = NativeBatch(acceptedId, session.Manifest);
		LiquidWalletRefreshCommandService.Dependencies dependencies = Dependencies(
			(_, _, _, _) =>
			{
				session.RecordAcceptedTransactionId(concurrentId);
				return Task.FromResult(CandidateObservation(session.StateOwner.NodeExpectation, session.Manifest, acceptedId));
			},
			observeNative: _ => batch,
			save: request => LiquidWalletLoadSaveResult.CreateSaved(
				request.State.Revision, request.NextGeneration, request.ExternalIndexHighWater, request.InternalIndexHighWater),
			publish: (_, _, _) => false);
		var command = LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		LiquidWalletUiRefreshResult result = await command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.AcceptedSend, acceptedId),
			CancellationToken.None);

		Assert.Equal(LiquidWalletUiRefreshStatus.Committed, result.Status);
		Assert.True(result.IsPostSubmit);
		Assert.False(result.HandoffPublished);
		Assert.Equal([concurrentId], session.GetRecordedAcceptedTransactionIds());
	}

	[Fact]
	public async Task PostSaveSnapshotChangeReturnsCommittedWithoutStalePublicationAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		const string candidateId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
		LiquidWalletObservationBatch batch = NativeBatch(candidateId, session.Manifest);
		int publishCalls = 0;
		LiquidWalletRefreshCommandService.Dependencies dependencies = Dependencies(
			(_, _, _, _) => Task.FromResult(CandidateObservation(
				session.StateOwner.NodeExpectation,
				session.Manifest,
				candidateId)),
			observeNative: _ => batch,
			save: request =>
			{
				object snapshot = session.CaptureRefreshSnapshot();
				LiquidAuthenticatedWalletStateOwner competingOwner = session.StateOwner.CreateReplacement(
					request.State,
					request.NextGeneration);
				var competingHandoff = new LiquidWalletRuntimeHandoff(
					session.Identity.CanonicalWalletId,
					session.Identity.NetworkManifestId,
					competingOwner.Balances,
					competingOwner.SelectableOutputs,
					competingOwner.History,
					competingOwner.ReceiveMaterial);
				Assert.True(session.TryInstallRefreshSnapshot(snapshot, competingOwner, competingHandoff));
				return LiquidWalletLoadSaveResult.CreateSaved(
					request.State.Revision,
					request.NextGeneration,
					request.ExternalIndexHighWater,
					request.InternalIndexHighWater);
			},
			publish: (_, _, _) =>
			{
				publishCalls++;
				return true;
			});
		var command = LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		LiquidWalletUiRefreshResult result = await command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None);

		Assert.Equal(LiquidWalletUiRefreshStatus.Committed, result.Status);
		Assert.False(result.HandoffPublished);
		Assert.Equal(0, publishCalls);
		Assert.Equal(1UL, result.ResultRevision);
		Assert.Equal(1UL, result.ResultGeneration);
	}

	[Fact]
	public async Task ManualNoCandidatesUsesOneRealAcquisitionAndDoesNotReachLaterStagesAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		var stages = new List<string>();
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, captured, suppliedId, cancellationToken) =>
			{
				stages.Add("rpc");
				Assert.Same(session, capturedSession);
				Assert.Same(session.StateOwner.NodeExpectation, captured.Owner.NodeExpectation);
				Assert.Same(session.Manifest, capturedSession.Manifest);
				Assert.Empty(captured.AcceptedTransactionIds);
				Assert.Null(suppliedId);
				return Task.FromResult(EmptyObservation(captured.Owner.NodeExpectation, capturedSession.Manifest));
			},
			observeNative: _ =>
			{
				stages.Add("native");
				throw new InvalidOperationException("Native must not run for no candidates.");
			},
			save: _ =>
			{
				stages.Add("save");
				throw new InvalidOperationException("Save must not run for no candidates.");
			},
			publish: (_, _, _) =>
			{
				stages.Add("publish");
				return false;
			},
			stageObserver: stages.Add);
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		LiquidWalletUiRefreshResult result = await command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None);

		Assert.Equal(["rpc"], stages);
		Assert.Equal(LiquidWalletUiRefreshStatus.NoCandidates, result.Status);
		Assert.Equal(session.StateOwner.StateRevision, result.ResultRevision);
		Assert.Equal(session.StateOwner.PersistenceGeneration, result.ResultGeneration);
		Assert.Equal(0, result.CandidateCount);
		Assert.Equal(0, result.AppliedTransactionCount);
		Assert.False(result.IsPostSubmit);
		Assert.False(result.HandoffPublished);
		Assert.Same(session.StateOwner, session.StateOwner);
		Assert.Same(session.PublicHandoff, session.PublicHandoff);
	}

	[Fact]
	public async Task ConfirmedUnrelatedCandidateProducesNoConfirmRowAndCommitDoesNotThrowAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		// A candidate that is confirmed (has block hash and height) but that the
		// native observation reports as having zero owned outputs and spending no
		// wallet outpoint is unrelated to the wallet. The refresh must NOT emit a
		// Confirm row for it; the sync session skips the empty delta, and the
		// commit must not throw "Only an applied Liquid wallet transaction can be
		// confirmed". This is the regression test for the unconditional
		// Confirm-row design flaw.
		const string unrelatedId = "3333333333333333333333333333333333333333333333333333333333333333";
		ElementsWalletRefreshObservation rpcObservation = ConfirmedCandidateObservation(
			session.StateOwner.NodeExpectation,
			session.Manifest,
			unrelatedId);
		// Native batch: one transaction observation with zero owned outputs and
		// only non-wallet inputs (a '5' previous txid outpoint the wallet state
		// does not contain), so it is irrelevant.
		LiquidWalletObservationBatch irrelevantBatch = IrrelevantNativeBatch(unrelatedId);
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, captured, suppliedId, cancellationToken) =>
				Task.FromResult(rpcObservation),
			observeNative: _ => irrelevantBatch,
			save: request => LiquidWalletLoadSaveResult.CreateSaved(
				request.State.Revision,
				request.NextGeneration,
				request.ExternalIndexHighWater,
				request.InternalIndexHighWater),
			publish: (_, _, _) => true,
			stageObserver: _ => { });
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		LiquidWalletUiRefreshResult result = await command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None);

		Assert.Equal(LiquidWalletUiRefreshStatus.Committed, result.Status);
		Assert.Equal(1, result.CandidateCount);
		// Zero applied: the unrelated transaction's empty delta was skipped.
		Assert.Equal(0, result.AppliedTransactionCount);
		// No Confirm row was emitted, so the commit revision did not advance
		// (base revision 0 is preserved through the save fence).
		Assert.Equal(0UL, result.ResultRevision);
	}

	[Fact]
	public async Task ConfirmedOwnedCandidateReceivesConfirmRowAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		// A confirmed candidate that pays the wallet (>= 1 owned output) is
		// wallet-relevant and MUST receive its Confirm row. This guards the fix
		// against weakening confirmation of genuinely-owned transactions.
		const string ownedId = "4444444444444444444444444444444444444444444444444444444444444444";
		ElementsWalletRefreshObservation rpcObservation = ConfirmedCandidateObservation(
			session.StateOwner.NodeExpectation,
			session.Manifest,
			ownedId);
		LiquidWalletObservationBatch ownedBatch = NativeBatch(ownedId, session.Manifest);
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync: (capturedSession, captured, suppliedId, cancellationToken) =>
				Task.FromResult(rpcObservation),
			observeNative: _ => ownedBatch,
			save: request => LiquidWalletLoadSaveResult.CreateSaved(
				request.State.Revision,
				request.NextGeneration,
				request.ExternalIndexHighWater,
				request.InternalIndexHighWater),
			publish: (_, _, _) => true,
			stageObserver: _ => { });
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> command =
			LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(provider, dependencies);

		LiquidWalletUiRefreshResult result = await command(
			new LiquidWalletUiRefreshRequest(WalletName, LiquidWalletUiRefreshTrigger.Manual, null),
			CancellationToken.None);

		Assert.Equal(LiquidWalletUiRefreshStatus.Committed, result.Status);
		Assert.Equal(1, result.CandidateCount);
		Assert.Equal(1, result.AppliedTransactionCount);
		// One Apply (revision 0 -> 1) plus one Confirm row (revision 1 -> 2):
		// the owned transaction's confirmation advanced the revision past the
		// apply, proving the Confirm row was emitted and committed.
		Assert.Equal(2UL, result.ResultRevision);
	}

	private static ElementsWalletRefreshObservation ConfirmedCandidateObservation(
		ElementsNodeExpectation expectation,
		ElementsPublicNetworkManifest manifest,
		string candidateId)
	{
		ElementsWalletRefreshObservation empty = EmptyObservation(expectation, manifest);
		// The observed node tip is blocks = 1 with bestBlockHash of '2's; bind the
		// confirmation to that exact tip so EnsureBoundToObservedTip passes.
		const string tipBlockHash = "2222222222222222222222222222222222222222222222222222222222222222";
		var candidate = new ElementsWalletRefreshCandidate(
			candidateId,
			blockHash: tipBlockHash,
			blockHeight: 1U,
			[new ElementsWalletRefreshInput(new string('5', 64))],
			[new string('5', 64)]);
		var result = new ElementsWalletRefreshObservation(
			empty.NodeObservation,
			[candidate],
			[
				new ElementsWalletRefreshRawTransaction(candidateId, [1, 2, 3]),
				new ElementsWalletRefreshRawTransaction(new string('5', 64), [4, 5, 6]),
			]);
		empty.Dispose();
		return result;
	}

	private static LiquidWalletObservationBatch IrrelevantNativeBatch(string transactionId)
	{
		LiquidTransactionId nativeTransactionId = LiquidTransactionId.ParseRpcHex(transactionId);
		byte[] witnessBinding = Enumerable.Repeat((byte)7, 32).ToArray();
		// Zero owned outputs and only a non-wallet input outpoint (previous txid
		// '5'...5 at index 0) that the empty wallet state does not contain.
		return LiquidWalletObservationBatch.Create(
			[LiquidWalletTransactionObservation.Create(
				nativeTransactionId.ToConsensusBytes(),
				witnessBinding,
				[LiquidOutPoint.CreateSpendable(LiquidTransactionId.ParseRpcHex(new string('5', 64)), 0)],
				[])]);
	}

	private static LiquidWalletRefreshCommandService.Dependencies Dependencies(
		Func<LiquidAuthenticatedWalletSession, LiquidWalletRefreshStateCapture, string?, CancellationToken, Task<ElementsWalletRefreshObservation>> acquireObservationAsync,
		Func<LiquidWalletRefreshCommandService.NativeObservationRequest, LiquidWalletObservationBatch>? observeNative = null,
		Func<LiquidWalletRefreshCommandService.SaveRequest, LiquidWalletLoadSaveResult>? save = null,
		Func<LiquidAuthenticatedRuntimeProvider, LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool>? publish = null) =>
		LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			acquireObservationAsync,
			observeNative ?? (_ => throw new InvalidOperationException("No native observation was configured.")),
			save ?? (_ => throw new InvalidOperationException("No save was configured.")),
			publish ?? ((_, _, _) => true),
			_ => { });

	private static LiquidWalletObservationBatch NativeBatch(
		string transactionId,
		ElementsPublicNetworkManifest manifest)
	{
		LiquidTransactionId nativeTransactionId = LiquidTransactionId.ParseRpcHex(transactionId);
		byte[] witnessBinding = Enumerable.Repeat((byte)7, 32).ToArray();
		LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
			LiquidKeyBranch.External,
			0);
		return LiquidWalletObservationBatch.Create(
			[LiquidWalletTransactionObservation.Create(
				nativeTransactionId.ToConsensusBytes(),
				witnessBinding,
				[LiquidOutPoint.CreateSpendable(LiquidTransactionId.ParseRpcHex(new string('5', 64)), 0)],
				[LiquidOwnedOutputObservation.Create(
					nativeTransactionId.ToConsensusBytes(), 0, witnessBinding,
					spendKey.GetScriptPubKey(), spendKey.GetCompressedPublicKey(),
					[0x02, .. Enumerable.Repeat((byte)2, 32)], LiquidKeyBranch.External, 0,
					WalletWasabi.Liquid.Assets.LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId).ToConsensusBytes(),
					100)])]);
	}

	private static ElementsWalletRefreshObservation CandidateObservation(
		ElementsNodeExpectation expectation,
		ElementsPublicNetworkManifest manifest,
		string candidateId)
	{
		ElementsWalletRefreshObservation empty = EmptyObservation(expectation, manifest);
		var candidate = new ElementsWalletRefreshCandidate(
			candidateId,
			blockHash: null,
			blockHeight: null,
			[new ElementsWalletRefreshInput(new string('5', 64))],
			[new string('5', 64)]);
		var result = new ElementsWalletRefreshObservation(
			empty.NodeObservation,
			[candidate],
			[
				new ElementsWalletRefreshRawTransaction(candidateId, [1, 2, 3]),
				new ElementsWalletRefreshRawTransaction(new string('5', 64), [4, 5, 6]),
			]);
		empty.Dispose();
		return result;
	}

	private static ElementsWalletRefreshObservation EmptyObservation(
		ElementsNodeExpectation expectation,
		ElementsPublicNetworkManifest manifest)
	{
		ElementsNodeExpectation normalized = expectation.Normalize();
		const string startupId = "1111111111111111111111111111111111111111111111111111111111111111";
		const int blocks = 1;
		const string bestBlockHash = "2222222222222222222222222222222222222222222222222222222222222222";
		var status = new ElementsNodeStatus(
			normalized.Chain,
			blocks,
			blocks,
			bestBlockHash,
			normalized.GenesisBlockHash,
			InitialBlockDownload: false,
			Pruned: false,
			TrimHeaders: false,
			BlockchainWarningsPresent: false,
			NetworkActive: true,
			LocalRelay: true,
			NetworkWarningsPresent: false,
			normalized.FedpegScript,
			normalized.PeggedAsset,
			normalized.ParentGenesisBlockHash,
			normalized.PeginConfirmationDepth,
			normalized.EnforcePak,
			normalized.Version,
			normalized.ProtocolVersion,
			normalized.Subversion);
		var generation = new ElementsNodeGenerationObservation(startupId, 1, blocks, bestBlockHash);
		return new ElementsWalletRefreshObservation(
			new ElementsExpectationBoundNodeObservation(
				normalized,
				manifest.RequiredFeeAssetId,
				status,
				generation),
			[],
			[]);
	}

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(LiquidAuthenticatedWalletSession session) =>
		CreateProvider(session, ElementsPublicNetworkManifest.LiquidMainnet);

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(LiquidAuthenticatedWalletSession session, ElementsPublicNetworkManifest manifest)
	{
		var provider = (LiquidAuthenticatedRuntimeProvider)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedRuntimeProvider));
		SetField(provider, "_gate", new object());
		SetField(provider, "_manifestSource", new ElementsPublicNetworkManifestSource(manifest.ManifestId));
		SetField(provider, "_session", session);
		return provider;
	}

	private static LiquidAuthenticatedWalletSession CreateSession(RejectingHandler handler) =>
		CreateSession(handler, ElementsPublicNetworkManifest.LiquidMainnet);

	private static LiquidAuthenticatedWalletSession CreateSession(RejectingHandler handler, ElementsPublicNetworkManifest manifest)
	{
		var session = (LiquidAuthenticatedWalletSession)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedWalletSession));
		var master = new ExtKey();
		LiquidWalletReceiveDerivation receive = LiquidWalletReceiveDerivation.Create(master, NBitcoin.Network.Main, 0, 0);
		var adapter = new LiquidWalletSignerKeyAdapter(master, _ => (0, 0, 0), NBitcoin.Network.Main);
		var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
		var rpcClient = new ElementsRpcClient(httpClient);
		SetField(rpcClient, "_ownsHttpClient", true);

		LiquidAuthenticatedWalletStateOwner owner = CreateOwner(manifest);
		var handoff = new LiquidWalletRuntimeHandoff(
			WalletName,
			manifest.ManifestId,
			owner.Balances,
			owner.SelectableOutputs,
			owner.History,
			owner.ReceiveMaterial);
		object snapshot = CreateSnapshot(owner, handoff);

		SetField(session, "_refreshGate", new object());
		SetField(session, "_lifetimeGate", new object());
		SetField(session, "_acceptedTransactionIds", new List<string>());
		SetField(session, "<Identity>k__BackingField", CreateIdentity(manifest));
		SetField(session, "<AuthenticatedMaster>k__BackingField", master);
		SetField(session, "<Descriptor>k__BackingField", receive.Descriptor);
		SetField(session, "<LastIndex>k__BackingField", receive.LastIndex);
		SetField(session, "<SignerKeyAdapter>k__BackingField", adapter);
		SetField(session, "<RpcClient>k__BackingField", rpcClient);
		SetField(session, "<WalletDataDirectory>k__BackingField", AppContext.BaseDirectory);
		SetField(session, "_manifest", manifest);
		SetField(session, "_snapshot", snapshot);
		return session;
	}

	private static LiquidAuthenticatedWalletStateOwner CreateOwner() =>
		CreateOwner(ElementsPublicNetworkManifest.LiquidMainnet);

	private static LiquidAuthenticatedWalletStateOwner CreateOwner(ElementsPublicNetworkManifest manifest)
	{
		using var handler = new RejectingHandler();
		var master = new ExtKey();
		var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, NBitcoin.Network.Main);
		var rpcClient = new ElementsRpcClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") });
		var owner = (LiquidAuthenticatedWalletStateOwner)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedWalletStateOwner));
		var allocation = new WalletWasabi.Liquid.Wallet.LiquidWalletExternalIndexAllocation(
			index: 0,
			stateRevision: 0,
			persistedGeneration: 0,
			persistedExternalIndexHighWater: 0,
			persistedInternalIndexHighWater: 0,
			WalletWasabi.Liquid.Wallet.LiquidWalletState.Empty(
				WalletWasabi.Liquid.Assets.LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId)));
		SetField(owner, "_allocation", allocation);
		SetField(owner, "_walletName", WalletName);
		SetField(owner, "_manifest", manifest);
		SetAutoProperty(owner, "StateRevision", 0UL);
		SetAutoProperty(owner, "PersistenceGeneration", 0UL);
		SetAutoProperty(owner, "Descriptor", "wpkh(test)");
		SetAutoProperty(owner, "LastIndex", 0UL);
		SetAutoProperty(owner, "ReceiveMaterial", CreateReceiveMaterial());
		SetAutoProperty(owner, "Balances", WalletWasabi.Liquid.Wallet.Ui.LiquidWalletUiFacade.CaptureAllocationBalances(WalletName, manifest, allocation));
		SetAutoProperty(owner, "SelectableOutputs", WalletWasabi.Liquid.Wallet.Ui.LiquidWalletUiFacade.CaptureSelectableOutputs(WalletName, manifest, allocation));
		SetAutoProperty(owner, "History", WalletWasabi.Liquid.Wallet.Ui.LiquidWalletUiFacade.CaptureAllocationHistory(WalletName, manifest, allocation));
		SetAutoProperty(owner, "NodeExpectation", BoundExpectation(manifest));
		adapter.Dispose();
		rpcClient.Dispose();
		return owner;
	}

	private static LiquidWalletUiReceiveMaterial CreateReceiveMaterial() =>
		new([0x00, 0x14, .. Enumerable.Repeat((byte)1, 20)], [0x02, .. Enumerable.Repeat((byte)2, 32)]);

	private static ElementsNodeExpectation BoundExpectation() =>
		BoundExpectation(ElementsPublicNetworkManifest.LiquidMainnet);

	private static ElementsNodeExpectation BoundExpectation(ElementsPublicNetworkManifest manifest) =>
		ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/unused", manifest.ChainRpcName, manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

	private static object CreateSnapshot(LiquidAuthenticatedWalletStateOwner owner, LiquidWalletRuntimeHandoff handoff)
	{
		Type type = typeof(LiquidAuthenticatedWalletSession).GetNestedType("RefreshSnapshot", BindingFlags.NonPublic)!;
		return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [owner, handoff], null)!;
	}

	private static LiquidWalletIdentity CreateIdentity() =>
		CreateIdentity(ElementsPublicNetworkManifest.LiquidMainnet);

	private static LiquidWalletIdentity CreateIdentity(ElementsPublicNetworkManifest manifest)
	{
		ConstructorInfo constructor = typeof(LiquidWalletIdentity).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			null,
			[typeof(string), typeof(string), typeof(string), typeof(string)],
			null)!;
		return (LiquidWalletIdentity)constructor.Invoke(
			[WalletName, "/unused/wallet.json", "unused", manifest.ManifestId]);
	}

	// A manifest instance that is reference-distinct from every reviewed catalog instance,
	// so the refresh network-class fence (which matches by reference) rejects it. The
	// shallow copy preserves every reviewed value while yielding a new reference that
	// ReferenceEquals never matches.
	private static ElementsPublicNetworkManifest CreateForeignManifest() =>
		(ElementsPublicNetworkManifest)typeof(ElementsPublicNetworkManifest)
			.GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(ElementsPublicNetworkManifest.LiquidMainnet, null)!;

	private static void SetAutoProperty(object target, string name, object? value) =>
		SetField(target, $"<{name}>k__BackingField", value);

	private static void SetField(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private sealed class RejectingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("No HTTP request is expected from this orchestration test seam.");
	}
}
