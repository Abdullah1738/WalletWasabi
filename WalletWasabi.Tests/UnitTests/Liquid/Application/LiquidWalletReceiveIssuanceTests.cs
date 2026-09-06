using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Models.Wallets.Liquid;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Liquid.Wallet.Sync;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

[Collection("Serial unit tests collection")]
public sealed class LiquidWalletReceiveIssuanceTests
{
	[Fact]
	public async Task OpenIsStableAndIssuanceAdvancesExactlyOnceAcrossReopenAsync()
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		LiquidAuthenticatedWalletSession session = await fixture.OpenAsync();
		string original = Address(session);
		string path = LiquidWalletPersistencePaths.GetWalletStateFilePath(fixture.Root, "alpha");
		byte[] initialBytes = File.ReadAllBytes(path);
		await fixture.Provider.CloseAsync("alpha", CancellationToken.None);
		session = await fixture.OpenAsync();
		Assert.Equal(original, Address(session));
		Assert.Equal(0UL, session.StateOwner.ExternalIndexHighWater);
		Assert.Equal(0UL, session.StateOwner.PersistenceGeneration);
		Assert.Equal(initialBytes, File.ReadAllBytes(path));

		var before = session.CaptureRefreshState();
		var issued = LiquidWalletReceiveIssuanceCommand.Execute(fixture.Provider, "alpha", original, CancellationToken.None);
		string next = Address(session);
		Assert.NotEqual(original, next);
		Assert.Equal(LiquidWalletReceiveDerivation.Create(session.AuthenticatedMaster, NBitcoin.Network.TestNet, 0, 1).ScriptPubKey,
			issued.ReceiveMaterial.NextReceiveScriptPubKey);
		Assert.Same(issued, fixture.Provider.CurrentHandoff);
		Assert.Equal(1UL, session.StateOwner.LastIndex);
		Assert.Equal(1UL, session.StateOwner.ExternalIndexHighWater);
		Assert.Equal(1UL, session.StateOwner.PersistenceGeneration);
		Assert.Equal(before.StateRevision, session.StateOwner.StateRevision);
		Assert.Equal(before.InternalIndexHighWater, session.StateOwner.InternalIndexHighWater);
		Assert.False(session.ValidateRefreshState(before));
		byte[] issuedBytes = File.ReadAllBytes(path);
		var snapshot = session.CaptureRefreshSnapshot();
		for (int attempt = 0; attempt < 2; attempt++)
		{
			Assert.Throws<InvalidOperationException>(() => LiquidWalletReceiveIssuanceCommand.Execute(
				fixture.Provider, "alpha", original, CancellationToken.None));
			Assert.Same(snapshot, session.CaptureRefreshSnapshot());
			Assert.Same(issued, fixture.Provider.CurrentHandoff);
			Assert.Equal(issuedBytes, File.ReadAllBytes(path));
		}
		await fixture.Provider.CloseAsync("alpha", CancellationToken.None);
		session = await fixture.OpenAsync();
		Assert.Equal(next, Address(session));
		Assert.Equal(1UL, session.StateOwner.ExternalIndexHighWater);
		Assert.Equal(1UL, session.StateOwner.PersistenceGeneration);
		Assert.Equal(issuedBytes, File.ReadAllBytes(path));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StaleGenerationOrIndexBoundRefusesWithoutDurableOrSessionDriftAsync(bool exhausted)
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		LiquidAuthenticatedWalletSession session = await fixture.OpenAsync();
		byte[] key = Derive(session, "replay");
		byte[] context = Derive(session, "context");
		try
		{
			LiquidWalletLoadSave.SaveWithExternalIndexHighWater(fixture.Root, "alpha", session.StateOwner.State,
				1, exhausted ? LiquidSpendKeyReference.MaximumIndex : 0UL, 0, key, context);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
		if (exhausted)
		{
			await fixture.Provider.CloseAsync("alpha", CancellationToken.None);
			session = await fixture.OpenAsync();
			Assert.Equal((ulong)LiquidSpendKeyReference.MaximumIndex, session.StateOwner.LastIndex);
		}
		string path = LiquidWalletPersistencePaths.GetWalletStateFilePath(fixture.Root, "alpha");
		byte[] bytes = File.ReadAllBytes(path);
		var snapshot = session.CaptureRefreshSnapshot();
		var handoff = fixture.Provider.CurrentHandoff;
		var failure = Assert.Throws<InvalidOperationException>(() => LiquidWalletReceiveIssuanceCommand.Execute(
			fixture.Provider, "alpha", Address(session), CancellationToken.None));
		Assert.Contains(exhausted ? "native catalog bound" : "state changed", failure.Message);
		Assert.Same(snapshot, session.CaptureRefreshSnapshot());
		Assert.Same(handoff, fixture.Provider.CurrentHandoff);
		Assert.Equal(bytes, File.ReadAllBytes(path));
	}

	[Fact]
	public async Task CancellationBeforeIssuanceLeavesAddressAndDurableStateUntouchedAsync()
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		var session = await fixture.OpenAsync();
		string path = LiquidWalletPersistencePaths.GetWalletStateFilePath(fixture.Root, "alpha");
		byte[] bytes = File.ReadAllBytes(path);
		var snapshot = session.CaptureRefreshSnapshot();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		Assert.Throws<OperationCanceledException>(() => LiquidWalletReceiveIssuanceCommand.Execute(
			fixture.Provider, "alpha", Address(session), cancellation.Token));
		Assert.Same(snapshot, session.CaptureRefreshSnapshot());
		Assert.Equal(bytes, File.ReadAllBytes(path));
	}

	[Fact]
	public async Task PublicLabelWriteRejectsRetiredAddressAndCurrentLabelSurvivesReopenAsync()
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		await using var client = LiquidWalletApplicationClient.Create(new(fixture.Root, fixture.Root, fixture.Identity.NetworkManifestId));
		var session = await client.RuntimeProvider.OpenAsync(fixture.Identity, "TestPassword".ToCharArray(), CancellationToken.None);
		string oldAddress = Address(session);
		await client.IssueNextReceiveAddressAsync("alpha", oldAddress, CancellationToken.None);
		string current = Address(session);
		string path = LiquidWalletPersistencePaths.GetWalletStateFilePath(fixture.Root, "alpha");
		byte[] bytes = File.ReadAllBytes(path);
		var snapshot = session.CaptureRefreshSnapshot();
		await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetNextReceiveLabelsAsync(
			new("alpha", ["old"], oldAddress), CancellationToken.None));
		Assert.Throws<ArgumentException>(() => new LiquidWalletUiSetReceiveLabelsRequest("alpha", ["missing"], ""));
		Assert.Same(snapshot, session.CaptureRefreshSnapshot());
		Assert.Equal(bytes, File.ReadAllBytes(path));
		await client.SetNextReceiveLabelsAsync(new("alpha", ["current"], current), CancellationToken.None);
		Assert.Equal(["current"], client.CurrentHandoff!.ReceiveMaterial.NextReceiveLabels);
		Assert.Null(session.StateOwner.State.GetReceiveLabels(0));
		Assert.Equal(1UL, session.StateOwner.ExternalIndexHighWater);
		Assert.Equal(2UL, session.StateOwner.PersistenceGeneration);
		await client.RuntimeProvider.CloseAsync("alpha", CancellationToken.None);
		session = await client.RuntimeProvider.OpenAsync(fixture.Identity, "TestPassword".ToCharArray(), CancellationToken.None);
		Assert.Equal(current, Address(session));
		Assert.Equal(["current"], session.StateOwner.ReceiveMaterial.NextReceiveLabels);
		Assert.Equal(2UL, session.StateOwner.PersistenceGeneration);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ApplicationClientReturnsPendingTaskWhilePersistenceFenceIsHeldAsync(bool labels)
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		await using var client = LiquidWalletApplicationClient.Create(new(fixture.Root, fixture.Root, fixture.Identity.NetworkManifestId));
		var session = await client.RuntimeProvider.OpenAsync(fixture.Identity, "TestPassword".ToCharArray(), CancellationToken.None);
		Task pending;
		lock (LiquidWalletLoadSave.GenerationFence)
		{
			pending = labels
				? client.SetNextReceiveLabelsAsync(new("alpha", ["saved"], Address(session)), CancellationToken.None)
				: client.IssueNextReceiveAddressAsync("alpha", Address(session), CancellationToken.None);
			Assert.False(pending.IsCompleted);
		}
		await pending.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(labels ? 0UL : 1UL, session.StateOwner.ExternalIndexHighWater);
		Assert.Equal(1UL, session.StateOwner.PersistenceGeneration);
		if (labels) Assert.Equal(["saved"], client.CurrentHandoff!.ReceiveMaterial.NextReceiveLabels);
	}

	[Fact]
	public async Task PublicLabelWriteStaleGenerationAndCancellationDoNotInvalidateReceiveAsync()
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		await using var client = LiquidWalletApplicationClient.Create(new(fixture.Root, fixture.Root, fixture.Identity.NetworkManifestId));
		var session = await client.RuntimeProvider.OpenAsync(fixture.Identity, "TestPassword".ToCharArray(), CancellationToken.None);
		var before = session.CaptureRefreshState();
		var handoff = client.CurrentHandoff!;
		using var model = new LiquidWalletModel("alpha", session.Manifest, handoff.Balances,
			handoff.ReceiveMaterial.NextReceiveScriptPubKey, handoff.ReceiveMaterial.NextReceiveBlindingPublicKey,
			setNextReceiveLabelsCommand: client.SetNextReceiveLabelsAsync, currentHandoff: () => client.CurrentHandoff);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAsync<OperationCanceledException>(() => model.SetNextReceiveLabelsAsync(["cancelled"], cancellation.Token, Address(session)));
		byte[] key = Derive(session, "replay");
		byte[] context = Derive(session, "context");
		try
		{
			Assert.Equal(0UL, LiquidWalletLoadSave.Load(fixture.Root, "alpha", key, context).Generation);
			LiquidWalletLoadSave.SaveWithExpectedGeneration(fixture.Root, "alpha", before.State, 1, 0, key, context);
			byte[] bytes = File.ReadAllBytes(LiquidWalletPersistencePaths.GetWalletStateFilePath(fixture.Root, "alpha"));
			await Assert.ThrowsAsync<InvalidOperationException>(() => model.SetNextReceiveLabelsAsync(["stale"], CancellationToken.None, Address(session)));
			Assert.Equal(bytes, File.ReadAllBytes(LiquidWalletPersistencePaths.GetWalletStateFilePath(fixture.Root, "alpha")));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
		Assert.True(session.ValidateRefreshState(before));
		Assert.Same(handoff, client.CurrentHandoff);
		Assert.False(model.IsReceiveMaterialInvalidated);
		Assert.Equal(Address(session), model.CreateNextReceiveAddress().ConfidentialAddressText);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task IssuanceCancellationAtPersistenceFenceDistinguishesCommittedWriteAsync(bool afterSave)
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		var session = await fixture.OpenAsync();
		var before = session.CaptureRefreshState();
		using var cancellation = new CancellationTokenSource();
		Task<LiquidWalletRuntimeHandoff> pending;
		lock (LiquidWalletLoadSave.GenerationFence)
		{
			pending = Task.Run(() => LiquidWalletReceiveIssuanceCommand.Execute(fixture.Provider, "alpha", Address(session), cancellation.Token,
				(key, context) =>
				{
					Assert.True(afterSave);
					var saved = LiquidWalletExternalIndexAllocator.Allocate(fixture.Root, "alpha", key, context);
					cancellation.Cancel();
					return saved;
				}));
			if (!afterSave) cancellation.Cancel();
		}
		if (afterSave)
		{
			var failure = await Assert.ThrowsAsync<LiquidWalletReceiveIssuanceCommand.LiquidWalletReceiveIssuanceUncertainException>(() => pending);
			Assert.IsType<OperationCanceledException>(failure.InnerException);
			Assert.Equal(1UL, session.StateOwner.PersistenceGeneration);
		}
		else
		{
			await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
			Assert.True(session.ValidateRefreshState(before));
		}
		Assert.Same(before.PublicHandoff, fixture.Provider.CurrentHandoff);
		byte[] key = Derive(session, "replay");
		byte[] context = Derive(session, "context");
		try { Assert.Equal(afterSave ? 1UL : 0UL, LiquidWalletLoadSave.Load(fixture.Root, "alpha", key, context).Generation); }
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LabelCancellationAtPersistenceFenceDistinguishesCommittedWriteAsync(bool afterSave)
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		var session = await fixture.OpenAsync();
		var before = session.CaptureRefreshState();
		using var cancellation = new CancellationTokenSource();
		int saves = 0;
		int publications = 0;
		var command = LiquidWalletReceiveLabelCommandService.CreateSetReceiveLabelsCommandForTesting(
			fixture.Provider, LiquidWalletReceiveLabelCommandService.Dependencies.CreateForTesting(
				request =>
				{
					saves++;
					var saved = LiquidWalletReceiveLabelCommandService.Dependencies.Production.Save(request);
					cancellation.Cancel();
					return saved;
				},
				(_, _, _) => { publications++; return true; },
				request =>
				{
					LiquidWalletReceiveLabelCommandService.Dependencies.Production.Validate(request);
					if (!afterSave) cancellation.Cancel();
				}));
		using var model = new LiquidWalletModel("alpha", session.Manifest, before.PublicHandoff.Balances,
			before.Owner.ReceiveMaterial.NextReceiveScriptPubKey, before.Owner.ReceiveMaterial.NextReceiveBlindingPublicKey,
			setNextReceiveLabelsCommand: (request, token) => Task.Run(() => command(new(
				"alpha", 0, request.Labels, request.ExpectedConfidentialAddress, token)), token));
		Task pending = model.SetNextReceiveLabelsAsync(["saved"], cancellation.Token, Address(session));
		if (afterSave)
		{
			var failure = await Assert.ThrowsAsync<LiquidWalletReceiveIssuanceCommand.LiquidWalletReceiveIssuanceUncertainException>(() => pending);
			Assert.IsType<OperationCanceledException>(failure.InnerException);
		}
		else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
		Assert.Equal(afterSave, model.IsReceiveMaterialInvalidated);
		Assert.Equal(afterSave ? 1 : 0, saves);
		Assert.Equal(0, publications);
		Assert.Equal(!afterSave, session.ValidateRefreshState(before));
		Assert.Same(before.PublicHandoff, fixture.Provider.CurrentHandoff);
		byte[] key = Derive(session, "replay");
		byte[] context = Derive(session, "context");
		try { Assert.Equal(afterSave ? 1UL : 0UL, LiquidWalletLoadSave.Load(fixture.Root, "alpha", key, context).Generation); }
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	private static string Address(LiquidAuthenticatedWalletSession session) =>
		LiquidWalletUiFacade.CreateReceiveAddress(session.Manifest, session.StateOwner.ReceiveMaterial.NextReceiveScriptPubKey,
			session.StateOwner.ReceiveMaterial.NextReceiveBlindingPublicKey).ConfidentialAddressText;

	[Theory]
	[InlineData(false, 0)]
	[InlineData(false, 1)]
	[InlineData(false, 2)]
	[InlineData(true, 0)]
	[InlineData(true, 1)]
	[InlineData(true, 2)]
	public async Task ReceiveWriteAmbiguityInvalidatesModelUntilAuthenticatedReopenAsync(bool labels, int failurePoint)
	{
		await using var fixture = new LiquidWalletChangeReservationTests.Fixture();
		var session = await fixture.OpenAsync();
		var before = session.CaptureRefreshState();
		var originalHandoff = fixture.Provider.CurrentHandoff!;
		string originalAddress = Address(session);
		int saves = 0;
		int publications = 0;
		var injectedFailure = new IOException("Injected failure after commit.");
		bool Publish(LiquidAuthenticatedWalletSession publishingSession, LiquidWalletRuntimeHandoff handoff)
		{
			publications++;
			Assert.Same(session, publishingSession);
			Assert.Same(handoff, session.PublicHandoff);
			Assert.NotSame(before.SnapshotReference, session.CaptureRefreshSnapshot());
			Assert.Equal(1UL, session.StateOwner.PersistenceGeneration);
			if (failurePoint == 2) throw injectedFailure;
			return false;
		}
		LiquidWalletExternalIndexAllocation Allocate(byte[] key, byte[] context)
		{
			saves++;
			Assert.Same(before.SnapshotReference, session.CaptureRefreshSnapshot());
			var saved = LiquidWalletExternalIndexAllocator.Allocate(fixture.Root, "alpha", key, context);
			Assert.Equal(1UL, LiquidWalletLoadSave.Load(fixture.Root, "alpha", key, context).Generation);
			if (failurePoint == 0) throw injectedFailure;
			return saved;
		}
		var labelCommand = LiquidWalletReceiveLabelCommandService.CreateSetReceiveLabelsCommandForTesting(
			fixture.Provider, LiquidWalletReceiveLabelCommandService.Dependencies.CreateForTesting(
				request =>
				{
					saves++;
					Assert.Same(before.SnapshotReference, session.CaptureRefreshSnapshot());
					var saved = LiquidWalletReceiveLabelCommandService.Dependencies.Production.Save(request);
					Assert.Equal(1UL, LiquidWalletLoadSave.Load(fixture.Root, "alpha", request.ReplayKey, request.Context).Generation);
					if (failurePoint == 0) throw injectedFailure;
					return saved;
				}, (_, publishingSession, handoff) => Publish(publishingSession, handoff)));
		using var model = new LiquidWalletModel("alpha", session.Manifest, originalHandoff.Balances,
			originalHandoff.ReceiveMaterial.NextReceiveScriptPubKey, originalHandoff.ReceiveMaterial.NextReceiveBlindingPublicKey,
			setNextReceiveLabelsCommand: async (request, _) => await labelCommand(new("alpha", 0, request.Labels, request.ExpectedConfidentialAddress)),
			issueReceiveCommand: (_, expected, token) => Task.FromResult(LiquidWalletReceiveIssuanceCommand.Execute(
				fixture.Provider, "alpha", expected, token, Allocate, Publish)),
			currentHandoff: () => fixture.Provider.CurrentHandoff);

		var failure = await Assert.ThrowsAsync<LiquidWalletReceiveIssuanceCommand.LiquidWalletReceiveIssuanceUncertainException>(() =>
			labels ? model.SetNextReceiveLabelsAsync(["saved"], CancellationToken.None, originalAddress)
				: model.IssueNextReceiveAddressAsync(originalAddress, CancellationToken.None));
		if (failurePoint != 1) Assert.Same(injectedFailure, failure.InnerException);
		Assert.Equal(1, saves);
		Assert.Equal(failurePoint == 0 ? 0 : 1, publications);
		Assert.Same(originalHandoff, fixture.Provider.CurrentHandoff);
		Assert.Equal(failurePoint == 0, session.ValidateRefreshState(before));
		Assert.True(model.IsReceiveMaterialInvalidated);
		Assert.Throws<InvalidOperationException>(() => model.CreateNextReceiveAddress());
		await Assert.ThrowsAsync<InvalidOperationException>(() => model.IssueNextReceiveAddressAsync(originalAddress, CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => model.SetNextReceiveLabelsAsync(["retry"], CancellationToken.None, originalAddress));
		Assert.Equal(1, saves);
		model.RefreshReceiveMaterial(originalHandoff);
		Assert.True(model.IsReceiveMaterialInvalidated);
		Assert.Throws<InvalidOperationException>(() => model.CreateNextReceiveAddress());

		await fixture.Provider.CloseAsync("alpha", CancellationToken.None);
		session = await fixture.OpenAsync();
		Assert.Equal(1UL, session.StateOwner.PersistenceGeneration);
		Assert.Equal(labels ? 0UL : 1UL, session.StateOwner.ExternalIndexHighWater);
		Assert.Equal(before.InternalIndexHighWater, session.StateOwner.InternalIndexHighWater);
		Assert.Equal(before.StateRevision, session.StateOwner.StateRevision);
		if (labels)
		{
			Assert.Equal(originalAddress, Address(session));
			Assert.Equal(["saved"], session.StateOwner.ReceiveMaterial.NextReceiveLabels);
		}
		else Assert.NotEqual(originalAddress, Address(session));
		var reopenedHandoff = fixture.Provider.CurrentHandoff!;
		using var reopenedModel = new LiquidWalletModel("alpha", session.Manifest, reopenedHandoff.Balances,
			reopenedHandoff.ReceiveMaterial.NextReceiveScriptPubKey, reopenedHandoff.ReceiveMaterial.NextReceiveBlindingPublicKey,
			reopenedHandoff.ReceiveMaterial.NextReceiveLabels);
		Assert.False(reopenedModel.IsReceiveMaterialInvalidated);
		Assert.Equal(Address(session), reopenedModel.CreateNextReceiveAddress().ConfidentialAddressText);
		Assert.True(model.IsReceiveMaterialInvalidated);
	}

	private static byte[] Derive(LiquidAuthenticatedWalletSession session, string domain)
	{
		ExtKey child = session.AuthenticatedMaster.Derive(new KeyPath(1108790945U | 0x80000000U));
		using var privateKey = child.PrivateKey;
		byte[] bytes = privateKey.ToBytes();
		try
		{
			byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes(session.Manifest.ManifestId + "alpha"));
			return LiquidKeyDomain.DeriveHkdf(bytes, salt, "WalletWasabi/Liquid/v1/" + domain);
		}
		finally { CryptographicOperations.ZeroMemory(bytes); }
	}
}
