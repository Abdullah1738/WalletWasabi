using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

[Collection("Serial unit tests collection")]
public sealed class LiquidWalletChangeReservationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RejectedOrCancelledReservationsAreNotReusedAndReopenRetainsCatalogAsync(bool cancel)
	{
		await using var fixture = new Fixture();
		LiquidAuthenticatedWalletSession session = await fixture.OpenAsync();
		var addresses = new HashSet<string>(StringComparer.Ordinal);
		var command = LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(
			fixture.Provider, (request, factory, _) =>
			{
				using ILiquidWalletSendExecutionScope scope = factory.Open(request.WalletName);
				Assert.True(scope.TryReserveChangeDestination(out string? address));
				Assert.True(addresses.Add(address!));
				if (cancel)
				{
					throw new OperationCanceledException();
				}
				return Task.FromResult(Rejected(request));
			});
		for (int index = 0; index < 3; index++)
		{
			if (cancel)
			{
				await Assert.ThrowsAsync<OperationCanceledException>(() => command(Request(), CancellationToken.None));
			}
			else
			{
				Assert.Equal(LiquidWalletUiSendExecutionStatus.RejectedBeforeSubmit,
					(await command(Request(), CancellationToken.None)).Status);
			}
		}
		Assert.Equal(3UL, session.StateOwner.InternalIndexHighWater);
		Assert.Equal(0UL, session.StateOwner.ExternalIndexHighWater);
		Assert.Equal(2UL, session.StateOwner.CatalogLastIndex);
		await fixture.Provider.CloseAsync("alpha", CancellationToken.None);
		session = await fixture.OpenAsync();
		Assert.Equal(3UL, session.StateOwner.InternalIndexHighWater);
		Assert.Equal(2UL, session.StateOwner.CatalogLastIndex);
		if (cancel)
		{
			await Assert.ThrowsAsync<OperationCanceledException>(() => command(Request(), CancellationToken.None));
		}
		else
		{
			await command(Request(), CancellationToken.None);
		}
		Assert.Equal(4, addresses.Count);
		Assert.Equal(4UL, session.StateOwner.InternalIndexHighWater);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StaleDurableGenerationOrExhaustedCatalogRejectsWithoutAllocationAsync(bool exhausted)
	{
		await using var fixture = new Fixture();
		LiquidAuthenticatedWalletSession session = await fixture.OpenAsync();
		var command = LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(
			fixture.Provider, (request, factory, _) =>
			{
				using ILiquidWalletSendExecutionScope scope = factory.Open(request.WalletName);
				var before = session.CaptureRefreshState();
				ulong highWater = exhausted ? 100_001UL : 0;
				LiquidWalletLoadSave.SaveWithIndexHighWaters(fixture.Root, "alpha", before.State,
					before.PersistenceGeneration + 1, 0, highWater, before.PersistenceGeneration,
					scope.ReplayProtectionKey, scope.ExternalWalletNetworkContext);
				if (exhausted)
				{
					var replacement = before.Owner.CreateReplacement(before.State, before.PersistenceGeneration + 1, highWater);
					Assert.True(session.TryInstallRefreshSnapshot(before.SnapshotReference, replacement, before.PublicHandoff));
				}
				object snapshot = session.CaptureRefreshSnapshot();
				Assert.False(scope.TryReserveChangeDestination(out var refusedAddress));
				Assert.Null(refusedAddress);
				Assert.Same(snapshot, session.CaptureRefreshSnapshot());
				var loaded = LiquidWalletLoadSave.Load(fixture.Root, "alpha", scope.ReplayProtectionKey, scope.ExternalWalletNetworkContext);
				Assert.Equal(before.PersistenceGeneration + 1, loaded.Generation);
				Assert.Equal(highWater, loaded.InternalIndexHighWater);
				return Task.FromResult(Rejected(request));
			});
		await command(Request(), CancellationToken.None);
	}

	[Fact]
	public async Task RefreshAfterRepeatedReservationsObservesAndSignsInternalBeyondExternalAsync()
	{
		await using var fixture = new Fixture();
		LiquidAuthenticatedWalletSession session = await fixture.OpenAsync();
		LiquidWalletUiSignedTransaction? funding = null;
		string? changeAddress = null;
		var reserve = LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(fixture.Provider,
			(request, factory, _) =>
			{
				using ILiquidWalletSendExecutionScope scope = factory.Open(request.WalletName);
				Assert.True(scope.TryReserveChangeDestination(out changeAddress));
				return Task.FromResult(Rejected(request));
			});
		for (int index = 0; index < 3; index++)
		{
			await reserve(Request(), CancellationToken.None);
		}

		// Build a real confidential payment to the reserved branch-1 index 2 with the
		// committed foreign-wallet fixture. No RPC or broadcast participates.
		byte[] fixtureFunding = FixtureBytes("funding_tx");
		byte[] fixturePrevious = FixtureBytes("previous_tx");
		LiquidTransactionId fixtureId = LiquidTransactionId.ParseConsensusBytes(FixtureBytes("funding_txid"));
		LiquidTransactionId previousId = LiquidTransactionId.ParseConsensusBytes(FixtureBytes("previous_txid"));
		LiquidAssetId pegged = LiquidAssetId.ParseRpcHex(session.Manifest.PeggedAssetId);
		using var fixtureKey = new Key(FixtureBytes("spend_key"));
		LiquidSpendKeyReference fixtureReference = LiquidSpendKeyReference.Create(fixtureKey.PubKey.ToBytes(), LiquidKeyBranch.External, 0);
		LiquidOwnedOutput fixtureOutput = LiquidOwnedOutput.Create(LiquidOutPoint.CreateSpendable(fixtureId, 0),
			fixtureReference.GetScriptPubKey(), LiquidAssetAmount.Create(pegged, pegged, 900), fixtureReference);
		LiquidWalletState sourceState = LiquidWalletState.Empty(pegged).Apply(0,
			LiquidWalletTransactionDelta.Create(fixtureId, [], [fixtureOutput]));
		ElementsExpectationBoundRawTransactionBatch fixtureSource = FundingSource(session,
			(fixtureId, fixtureFunding), (previousId, fixturePrevious));
		using (LiquidWalletNativeSigner signer = LiquidWalletNativeSigner.Create(new FixtureSigner(fixtureKey),
			FixtureText("descriptor"), 1, FixtureBytes("slip77")))
		{
			LiquidWalletUiSignRequest request = LiquidWalletUiFacade.CreateSignRequest("foreign", session.Manifest,
				sourceState, [Convert.ToHexStringLower(fixtureOutput.OutPoint.ToConsensusBytes())], changeAddress!,
				pegged.CanonicalRpcHex, 800, 100, RandomNumberGenerator.GetBytes(32), fixtureSource, [[previousId.CanonicalRpcHex]], 1);
			Assert.True(signer.TrySignAndFinalize(request, out funding));
		}
		Assert.NotNull(funding);
		LiquidTransactionId fundingId = LiquidTransactionId.ParseRpcHex(funding.TransactionIdHex);
		byte[] fundingBytes = Convert.FromHexString(funding.SignedTransactionHex);
		var dependencies = LiquidWalletRefreshCommandService.Dependencies.CreateForTesting(
			(_, _, _, _) => Task.FromResult(new ElementsWalletRefreshObservation(NodeObservation(session),
				[new ElementsWalletRefreshCandidate(fundingId.CanonicalRpcHex, null, null,
					[new ElementsWalletRefreshInput(fixtureId.CanonicalRpcHex)], [fixtureId.CanonicalRpcHex])],
				[new ElementsWalletRefreshRawTransaction(fundingId.CanonicalRpcHex, fundingBytes),
					new ElementsWalletRefreshRawTransaction(fixtureId.CanonicalRpcHex, fixtureFunding)])),
			request =>
			{
				Assert.Equal(2U, request.LastIndex);
				return LiquidWalletRefreshCommandService.Dependencies.Production.ObserveNative(request);
			}, LiquidWalletRefreshCommandService.Dependencies.Production.Save,
			LiquidWalletRefreshCommandService.Dependencies.Production.Publish, _ => { });
		var refresh = LiquidWalletRefreshCommandService.CreateRefreshCommandForTesting(fixture.Provider, dependencies);
		var refreshed = await refresh(new LiquidWalletUiRefreshRequest("alpha", LiquidWalletUiRefreshTrigger.Manual, null), CancellationToken.None);
		Assert.Equal(4UL, refreshed.ResultGeneration);
		Assert.True(refreshed.HandoffPublished);
		LiquidOwnedOutput change = Assert.Single(session.StateOwner.State.GetUnspentOutputs());
		Assert.Equal(LiquidKeyBranch.Internal, change.SpendKey.Branch);
		Assert.Equal(2U, change.SpendKey.Index);
		string outpoint = Convert.ToHexStringLower(change.OutPoint.ToConsensusBytes());
		Assert.Equal(Convert.ToHexStringLower(change.SpendKey.GetCompressedPublicKey()), session.SignerKeyAdapter.GetPublicKeyHex(outpoint));

		var spend = LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(fixture.Provider,
			(request, factory, _) =>
			{
				using ILiquidWalletSendExecutionScope scope = factory.Open(request.WalletName);
				Assert.Equal(2UL, scope.LastIndex);
				var source = FundingSource(session, (fundingId, fundingBytes), (fixtureId, fixtureFunding));
				LiquidWalletUiSignRequest signRequest = LiquidWalletUiFacade.CreateSignRequest("alpha", session.Manifest,
					session.StateOwner.State, [outpoint], changeAddress!, pegged.CanonicalRpcHex, 700, 100, scope.SourceEpoch,
					source, [[fixtureId.CanonicalRpcHex]], 1);
				using var tooShort = LiquidWalletNativeSigner.Create(scope.KeyOwner, scope.DescriptorString, 0, scope.Slip77MasterKey);
				Assert.False(tooShort.TrySignAndFinalize(signRequest, out var refused));
				Assert.Null(refused);
				Assert.True(scope.Signer.TrySignAndFinalize(signRequest, out var signed));
				Assert.NotNull(signed);
				return Task.FromResult(Rejected(request));
			});
		await spend(Request(), CancellationToken.None);
		await fixture.Provider.CloseAsync("alpha", CancellationToken.None);
		session = await fixture.OpenAsync();
		await spend(Request(), CancellationToken.None);
	}

	private static string FixtureText(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
		"TestData", "Liquid", "OrdinaryWalletPlanWireV1", "signable", name + ".txt")).Trim();
	private static byte[] FixtureBytes(string name) => Convert.FromHexString(FixtureText(name));

	private static ElementsExpectationBoundNodeObservation NodeObservation(LiquidAuthenticatedWalletSession session)
	{
		var e = session.NodeExpectation.Normalize();
		string block = new string('2', 64);
		var status = new ElementsNodeStatus(e.Chain, 1, 1, block, e.GenesisBlockHash, false, false, false, false,
			true, true, false, e.FedpegScript, e.PeggedAsset, e.ParentGenesisBlockHash, e.PeginConfirmationDepth,
			e.EnforcePak, e.Version, e.ProtocolVersion, e.Subversion);
		return new ElementsExpectationBoundNodeObservation(e, session.Manifest.RequiredFeeAssetId, status,
			ElementsNodeGenerationObservation.CreateFallbackTipObservation(1, block));
	}

	private static ElementsExpectationBoundRawTransactionBatch FundingSource(LiquidAuthenticatedWalletSession session,
		params (LiquidTransactionId Id, byte[] Bytes)[] transactions) => new(NodeObservation(session),
		transactions.Select(tx => new ElementsRawTransactionObservation(new ElementsRawTransactionRequest(tx.Id.CanonicalRpcHex, null), tx.Bytes)).ToArray());

	private sealed class FixtureSigner(Key key) : ILiquidWalletSigner
	{
		public string? GetPublicKeyHex(string outPointHex) => key.PubKey.ToHex();
		public string? SignDigestHex(string outPointHex, string digestHex) =>
			Convert.ToHexStringLower([.. key.Sign(new uint256(Convert.FromHexString(digestHex), lendian: true)).ToDER(), 0x41]);
		public bool TrySignAndFinalize(LiquidWalletUiSignRequest request, out LiquidWalletUiSignedTransaction? signedTransaction)
		{
			signedTransaction = null;
			return false;
		}
	}

	[Fact]
	public async Task ProductionExecutorRejectionRetainsReservationAsync()
	{
		await using var fixture = new Fixture();
		LiquidAuthenticatedWalletSession session = await fixture.OpenAsync();
		var command = LiquidWalletSendExecutionCommandService.CreateSendCommand(fixture.Provider);
		for (ulong count = 1; count <= 2; count++)
		{
			await Assert.ThrowsAsync<WalletWasabi.Liquid.Addresses.LiquidAddressFormatException>(
				() => command(Request(), CancellationToken.None));
			Assert.Equal(count, session.StateOwner.InternalIndexHighWater);
			Assert.Equal(count, session.StateOwner.PersistenceGeneration);
		}
	}

	[Fact]
	public async Task ReservationPublishesDurableGenerationAndInvalidatesOldCaptureAsync()
	{
		await using var fixture = new Fixture();
		LiquidAuthenticatedWalletSession session = await fixture.OpenAsync();
		LiquidWalletRefreshStateCapture before = session.CaptureRefreshState();
		var command = LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(
			fixture.Provider, (request, factory, _) =>
			{
				using ILiquidWalletSendExecutionScope scope = factory.Open(request.WalletName);
				Assert.True(scope.TryReserveChangeDestination(out string? address));
				Assert.NotNull(address);
				Assert.True(scope.TryReserveChangeDestination(out string? cached));
				Assert.Equal(address, cached);
				LiquidWalletRefreshStateCapture after = session.CaptureRefreshState();
				// This is the same expected-generation save the post-send refresh uses.
				LiquidWalletLoadSaveResult saved = LiquidWalletLoadSave.SaveWithExpectedGeneration(
					fixture.Root, "alpha", after.State, after.PersistenceGeneration + 1,
					after.PersistenceGeneration, scope.ReplayProtectionKey, scope.ExternalWalletNetworkContext);
				Assert.Equal(before.PersistenceGeneration + 2, saved.Generation);
				Assert.Equal(before.PersistenceGeneration + 1, after.PersistenceGeneration);
				Assert.Equal(1UL, after.InternalIndexHighWater);
				Assert.Equal(before.ExternalIndexHighWater, after.ExternalIndexHighWater);
				Assert.Equal(before.StateRevision, after.StateRevision);
				Assert.False(session.ValidateRefreshState(before));
				Assert.True(session.ValidateRefreshState(after));
				return Task.FromResult(Rejected(request));
			});
		await command(Request(), CancellationToken.None);
	}

	internal static LiquidWalletUiSendExecutionRequest Request() => new(
		"alpha", [new string('1', 64) + "00000000"], "unused", ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
		1, 1, 0, [null]);

	internal static LiquidWalletUiSendExecutionResult Rejected(LiquidWalletUiSendExecutionRequest request) => new(
		LiquidWalletUiSendExecutionStatus.RejectedBeforeSubmit, request.WalletName,
		ElementsPublicNetworkManifest.LiquidTestnet.ManifestId, 0, null, null, false, false, "test-rejection");

	internal sealed class Fixture : IAsyncDisposable
	{
		internal Fixture()
		{
			Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "liquid-w2-" + Guid.NewGuid().ToString("N"))).FullName;
			string walletFile = Path.Combine(Root, "alpha.json");
			KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.TestNet, walletFile);
			var manifest = ElementsPublicNetworkManifest.LiquidTestnet;
			string cookie = Path.Combine(Root, "cookie");
			File.WriteAllText(cookie, "test:password");
			string profiles = Directory.CreateDirectory(Path.Combine(Root, "liquid-rpc-profiles")).FullName;
			string profile = Path.Combine(profiles, "local.json");
			File.WriteAllText(profile, $$"""
				{"schema":"walletwasabi-liquid-rpc-profile/v1","name":"local","endpoint":"http://127.0.0.1:1","cookieFile":"{{cookie}}","network":"{{manifest.ChainRpcName}}","manifest":"{{manifest.ManifestId}}","connectTimeoutMs":1000,"requestTimeoutMs":1000}
				""");
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(cookie, UnixFileMode.UserRead | UnixFileMode.UserWrite);
				File.SetUnixFileMode(profile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
			var directories = new LiquidWalletDirectories(Root);
			Identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", manifest.ManifestId, directories);
			Provider = new LiquidAuthenticatedRuntimeProvider(new LiquidRpcProfileSource(Root), directories,
				new ElementsPublicNetworkManifestSource(manifest.ManifestId));
		}

		internal string Root { get; }
		internal LiquidWalletIdentity Identity { get; }
		internal LiquidAuthenticatedRuntimeProvider Provider { get; }
		internal async Task<LiquidAuthenticatedWalletSession> OpenAsync() =>
			await Provider.OpenAsync(Identity, "TestPassword".ToCharArray(), CancellationToken.None);

		public async ValueTask DisposeAsync()
		{
			await Provider.DisposeAsync();
			Directory.Delete(Root, recursive: true);
		}
	}
}
