using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletRefreshSnapshotTests
{
	[Fact]
	public void CaptureAndValidateRefreshStateUsesExactOwnerHandoffSnapshot()
	{
		using SessionFixture fixture = SessionFixture.Open();
		LiquidAuthenticatedWalletSession session = fixture.Session;
		const string acceptedId = "1111111111111111111111111111111111111111111111111111111111111111";
		session.RecordAcceptedTransactionId(acceptedId);

		LiquidWalletRefreshStateCapture captured = session.CaptureRefreshState();

		Assert.Same(session.StateOwner, captured.Owner);
		Assert.Same(session.PublicHandoff, captured.PublicHandoff);
		Assert.Same(session.StateOwner.State, captured.State);
		Assert.Equal(session.StateOwner.StateRevision, captured.StateRevision);
		Assert.Equal(session.StateOwner.PersistenceGeneration, captured.PersistenceGeneration);
		Assert.Equal(session.StateOwner.ExternalIndexHighWater, captured.ExternalIndexHighWater);
		Assert.Equal([acceptedId], captured.AcceptedTransactionIds);
		Assert.True(session.ValidateRefreshState(captured));

		LiquidAuthenticatedWalletStateOwner replacement = fixture.CreateAdvancedOwner();
		var replacementHandoff = new LiquidWalletRuntimeHandoff(
			fixture.Identity.CanonicalWalletId,
			fixture.Identity.NetworkManifestId,
			replacement.Balances,
			replacement.SelectableOutputs,
			replacement.History,
			replacement.ReceiveMaterial);
		Assert.True(session.TryInstallRefreshSnapshot(captured.SnapshotReference, replacement, replacementHandoff));
		Assert.False(session.ValidateRefreshState(captured));
	}

	[Fact]
	public void RemoveCapturedAcceptedIdsPreservesConcurrentRecords()
	{
		using SessionFixture fixture = SessionFixture.Open();
		LiquidAuthenticatedWalletSession session = fixture.Session;
		const string rerecordedId = "1111111111111111111111111111111111111111111111111111111111111111";
		const string removableId = "2222222222222222222222222222222222222222222222222222222222222222";
		const string concurrentId = "3333333333333333333333333333333333333333333333333333333333333333";
		session.RecordAcceptedTransactionId(rerecordedId);
		session.RecordAcceptedTransactionId(removableId);
		LiquidWalletRefreshStateCapture captured = session.CaptureRefreshState();

		session.RecordAcceptedTransactionId(concurrentId);
		session.RecordAcceptedTransactionId(rerecordedId);
		session.RemoveCapturedAcceptedIds(
			captured,
			new HashSet<string>([rerecordedId, removableId, concurrentId], StringComparer.Ordinal));

		Assert.Equal([rerecordedId, concurrentId], session.GetRecordedAcceptedTransactionIds());
	}

	[Fact]
	public void SessionHoldsOwnerAndHandoffAsOneImmutableSnapshot()
	{
		using SessionFixture fixture = SessionFixture.Open();
		LiquidAuthenticatedWalletSession session = fixture.Session;

		// Capture yields one reference whose owner/handoff exactly match the public getters.
		object captured = session.CaptureRefreshSnapshot();
		Assert.NotNull(captured);
		Assert.Same(session.StateOwner, session.StateOwner);
		Assert.Same(session.PublicHandoff, session.PublicHandoff);

		// A prepared replacement installs atomically against the captured reference: after the
		// install, both getters project the new pair with no mixed owner/handoff observable.
		LiquidAuthenticatedWalletStateOwner newOwner = fixture.CreateAdvancedOwner();
		var newHandoff = new LiquidWalletRuntimeHandoff(
			fixture.Identity.CanonicalWalletId,
			fixture.Identity.NetworkManifestId,
			newOwner.Balances,
			newOwner.SelectableOutputs,
			newOwner.History,
			newOwner.ReceiveMaterial);

		Assert.True(session.TryInstallRefreshSnapshot(captured, newOwner, newHandoff));
		Assert.Same(newOwner, session.StateOwner);
		Assert.Same(newHandoff, session.PublicHandoff);
		Assert.Same(newOwner.ReceiveMaterial, session.PublicHandoff.ReceiveMaterial);

		// A stale captured reference no longer installs.
		LiquidAuthenticatedWalletStateOwner newerOwner = fixture.CreateAdvancedOwner();
		var newerHandoff = new LiquidWalletRuntimeHandoff(
			fixture.Identity.CanonicalWalletId,
			fixture.Identity.NetworkManifestId,
			newerOwner.Balances,
			newerOwner.SelectableOutputs,
			newerOwner.History,
			newerOwner.ReceiveMaterial);
		Assert.False(session.TryInstallRefreshSnapshot(captured, newerOwner, newerHandoff));
		Assert.Same(newOwner, session.StateOwner);
		Assert.Same(newHandoff, session.PublicHandoff);
	}

	[Fact]
	public async System.Threading.Tasks.Task ProviderPublishesRefreshOnlyForLiveMatchingSessionAsync()
	{
		using SessionFixture fixture = SessionFixture.Open();
		LiquidAuthenticatedRuntimeProvider provider = fixture.Provider;
		LiquidAuthenticatedWalletSession session = fixture.Session;

		object captured = session.CaptureRefreshSnapshot();
		LiquidAuthenticatedWalletStateOwner newOwner = fixture.CreateAdvancedOwner();
		var newHandoff = new LiquidWalletRuntimeHandoff(
			fixture.Identity.CanonicalWalletId,
			fixture.Identity.NetworkManifestId,
			newOwner.Balances,
			newOwner.SelectableOutputs,
			newOwner.History,
			newOwner.ReceiveMaterial);
		Assert.True(session.TryInstallRefreshSnapshot(captured, newOwner, newHandoff));

		// Live, matching session: publication succeeds and CurrentHandoff advances.
		Assert.True(provider.TryPublishRefresh(session, newHandoff));
		Assert.Same(newHandoff, provider.CurrentHandoff);

		// Mismatched handoff (not the session's current snapshot handoff): no-op.
		var foreignHandoff = new LiquidWalletRuntimeHandoff(
			fixture.Identity.CanonicalWalletId,
			fixture.Identity.NetworkManifestId,
			newOwner.Balances,
			newOwner.SelectableOutputs,
			newOwner.History,
			newOwner.ReceiveMaterial);
		Assert.False(provider.TryPublishRefresh(session, foreignHandoff));
		Assert.Same(newHandoff, provider.CurrentHandoff);

		// After close detaches the session, publication is a no-op and never throws.
		await provider.CloseAsync(fixture.Identity.CanonicalWalletId, default);
		Assert.False(provider.TryPublishRefresh(session, newHandoff));
		Assert.Null(provider.CurrentHandoff);
	}

	private sealed class SessionFixture : IDisposable
	{
		private SessionFixture()
		{
			Root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
			WalletDirectory = Directory.CreateDirectory(Path.Combine(Root, "wallets")).FullName;
			WalletFile = Path.Combine(WalletDirectory, "alpha.json");
			KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, WalletFile);
			Manifest = ElementsPublicNetworkManifest.LiquidMainnet;
			SeedPersistedState();
			Identity = LiquidWalletIdentity.Create(
				"alpha", WalletFile, "local", Manifest.ManifestId, new LiquidWalletDirectories(WalletDirectory));
			WriteRpcProfile();
			Provider = new LiquidAuthenticatedRuntimeProvider(
				new LiquidRpcProfileSource(Root),
				new LiquidWalletDirectories(WalletDirectory),
				new ElementsPublicNetworkManifestSource(Identity.NetworkManifestId));
			Session = OpenSession();
		}

		internal string Root { get; }
		private string WalletDirectory { get; }
		private string WalletFile { get; }
		private ElementsPublicNetworkManifest Manifest { get; }
		internal LiquidWalletIdentity Identity { get; }
		internal LiquidAuthenticatedRuntimeProvider Provider { get; }
		internal LiquidAuthenticatedWalletSession Session { get; }
		private LiquidAuthenticatedWalletStateOwner BaseOwner { get; set; } = null!;

		internal static SessionFixture Open() => new();

		internal LiquidAuthenticatedWalletStateOwner CreateAdvancedOwner()
		{
			// Build a committed state one revision above the session's current owner state.
			LiquidWalletState current = Session.StateOwner.State;
			LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
			LiquidSpendKeyReference externalKey = LiquidSpendKeyReference.Create(
				Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
				LiquidKeyBranch.External,
				0);
			char marker = (char)('b' + (int)current.Revision);
			LiquidTransactionId txId = LiquidTransactionId.ParseRpcHex(new string(marker, 64));
			LiquidOwnedOutput received = LiquidOwnedOutput.Create(
				LiquidOutPoint.CreateSpendable(txId, 0),
				externalKey.GetScriptPubKey(),
				LiquidAssetAmount.Create(peggedAsset, peggedAsset, 5_000),
				externalKey);
			LiquidWalletState committed = current.Apply(
				current.Revision,
				LiquidWalletTransactionDelta.Create(txId, [], [received]));
			return Session.StateOwner.CreateReplacement(committed, Session.StateOwner.PersistenceGeneration + 1);
		}

		private LiquidAuthenticatedWalletSession OpenSession()
		{
			KeyManager keyManager = KeyManager.FromFile(WalletFile);
			ExtKey master = keyManager.GetMasterExtKey("TestPassword");
#pragma warning disable CA2000 // Ownership transfers to the session, which disposes these on DisposeAsync.
			var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18884") };
			var rpcClient = new ElementsRpcClient(httpClient);
			var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());
#pragma warning restore CA2000
			ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
				Manifest,
				new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), Path.Combine(Root, "cookie"), Manifest.ChainRpcName, Manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
			LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(
				Identity, Manifest, bound, WalletDirectory, master, adapter, rpcClient);
			BaseOwner = owner;
			var handoff = new LiquidWalletRuntimeHandoff(
				Identity.CanonicalWalletId,
				Identity.NetworkManifestId,
				owner.Balances,
				owner.SelectableOutputs,
				owner.History,
				owner.ReceiveMaterial);
			var session = new LiquidAuthenticatedWalletSession(
				Identity, handoff, keyManager, adapter, Manifest, rpcClient, master, owner,
				owner.Descriptor, owner.LastIndex, WalletDirectory);
			// Publish through the provider so TryPublishRefresh has a live session registry entry.
			PublishThroughProvider(session, handoff);
			return session;
		}

		private void PublishThroughProvider(LiquidAuthenticatedWalletSession session, LiquidWalletRuntimeHandoff handoff)
		{
			// Mirror the provider's Open publication: register the exact session and handoff.
			System.Reflection.FieldInfo sessionField = typeof(LiquidAuthenticatedRuntimeProvider)
				.GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
			System.Reflection.FieldInfo handoffField = typeof(LiquidAuthenticatedRuntimeProvider)
				.GetField("_currentHandoff", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
			object gate = typeof(LiquidAuthenticatedRuntimeProvider)
				.GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
				.GetValue(Provider)!;
			lock (gate)
			{
				sessionField.SetValue(Provider, session);
				handoffField.SetValue(Provider, handoff);
			}
		}

		private void SeedPersistedState()
		{
			KeyManager keyManager = KeyManager.FromFile(WalletFile);
			ExtKey master = keyManager.GetMasterExtKey("TestPassword");
			ExtKey replayChild = master.Derive(new KeyPath(1108790945U | 0x80000000U));
			byte[] childMaterial = replayChild.PrivateKey.ToBytes();
			byte[] saltInput = System.Text.Encoding.UTF8.GetBytes(Manifest.ManifestId + "alpha");
			byte[] salt = System.Security.Cryptography.SHA256.HashData(saltInput);
			byte[] replayKey = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/replay");
			byte[] context = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/context");
			try
			{
				LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
				LiquidSpendKeyReference externalKey = LiquidSpendKeyReference.Create(
					Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
					LiquidKeyBranch.External,
					0);
				LiquidOwnedOutput received = LiquidOwnedOutput.Create(
					LiquidOutPoint.CreateSpendable(LiquidTransactionId.ParseRpcHex(new string('a', 64)), 0),
					externalKey.GetScriptPubKey(),
					LiquidAssetAmount.Create(peggedAsset, peggedAsset, 12_345),
					externalKey);
				LiquidWalletState state = LiquidWalletState.Empty(peggedAsset)
					.Apply(0, LiquidWalletTransactionDelta.Create(LiquidTransactionId.ParseRpcHex(new string('a', 64)), [], [received]));
				_ = LiquidWalletLoadSave.Save(WalletDirectory, "alpha", state, 0, replayKey, context);
				_ = LiquidWalletExternalIndexAllocator.Allocate(WalletDirectory, "alpha", replayKey, context);
			}
			finally
			{
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(context);
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(replayKey);
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(saltInput);
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(childMaterial);
			}
		}

		private void WriteRpcProfile()
		{
			string profileDirectory = Directory.CreateDirectory(Path.Combine(Root, "liquid-rpc-profiles")).FullName;
			string cookieFile = Path.Combine(Root, "cookie");
			File.WriteAllText(cookieFile, "user:password\n");
			string profileFile = Path.Combine(profileDirectory, "local.json");
			File.WriteAllText(profileFile, $$"""
				{"schema":"walletwasabi-liquid-rpc-profile/v1","name":"local","endpoint":"http://127.0.0.1:18884","cookieFile":"{{cookieFile}}","network":"{{Manifest.ChainRpcName}}","manifest":"{{Manifest.ManifestId}}","connectTimeoutMs":1000,"requestTimeoutMs":1000}
				""");
			if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
			{
				File.SetUnixFileMode(cookieFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
				File.SetUnixFileMode(profileFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
		}

		public void Dispose()
		{
			try
			{
				Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
			}
			catch
			{
			}
			try
			{
				Provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
			}
			catch
			{
			}
			try
			{
				if (Directory.Exists(Root))
				{
					Directory.Delete(Root, recursive: true);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}
