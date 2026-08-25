using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Client.Liquid;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidProviderOwnershipSeamTests
{
	[Fact]
	public void PasswordAuthorizationLeaseRejectsEmptyPassword()
	{
		Assert.Throws<ArgumentException>(() => LiquidPasswordAuthorizationLease.Create(ReadOnlySpan<char>.Empty));
	}

	[Fact]
	public void PasswordAuthorizationLeaseRejectsOversizedPassword()
	{
		Assert.Throws<ArgumentException>(() => LiquidPasswordAuthorizationLease.Create(new string('x', 1025)));
	}

	[Fact]
	public void PasswordAuthorizationLeaseDisposesAndZeroizesOwnedBuffer()
	{
		LiquidPasswordAuthorizationLease lease = LiquidPasswordAuthorizationLease.Create("secret");
		char[] buffer = Assert.IsType<char[]>(typeof(LiquidPasswordAuthorizationLease)
			.GetField("_password", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(lease));

		lease.Dispose();
		lease.Dispose();

		Assert.True(lease.IsDisposed);
		Assert.All(buffer, value => Assert.Equal('\0', value));
		Assert.Throws<ObjectDisposedException>(() => ReadPassword(lease));
	}

	[Fact]
	public void WalletIdentityCanonicalizesOnlyReviewedIdentityComponents()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		File.WriteAllText(walletFile, "{}");
		LiquidWalletDirectories directories = new(walletDirectory);

		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"  alpha  ",
			walletFile,
			" local ",
			" b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b ",
			directories);

		Assert.Equal("alpha", identity.CanonicalWalletId);
		Assert.Equal(Path.GetFullPath(walletFile), identity.CanonicalWalletFilePath);
		Assert.Equal("local", identity.RuntimeProfileName);
		Assert.Equal("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", identity.NetworkManifestId);
	}

	[Fact]
	public void WalletIdentityRejectsFilesOutsideConfiguredWalletDirectory()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string outsideFile = Path.Combine(directory.Path, "outside.json");
		File.WriteAllText(outsideFile, "{}");

		Assert.Throws<InvalidDataException>(() => LiquidWalletIdentity.Create(
			"alpha", outsideFile, "local", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", new LiquidWalletDirectories(walletDirectory)));
	}

	[Fact]
	public async System.Threading.Tasks.Task OpenPublishesExactReceiveMaterialAndSameRevisionStateSnapshotsAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		// Seed a nonzero-revision state plus one already-issued index, so the expected
		// revision and the expected next index are known constants, not production echoes.
		PersistedLiquidState seeded = CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha");
		Assert.Equal(1UL, seeded.State.Revision);
		const ulong expectedNextIndex = 1UL;
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", new LiquidWalletDirectories(walletDirectory));
		CreateRpcProfile(directory.Path, "local", identity.NetworkManifestId);
		LiquidWalletRuntimeHandoff? published = null;
		await using LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource(identity.NetworkManifestId),
			handoff => published = handoff);

		using LiquidPasswordAuthorizationLease lease = LiquidPasswordAuthorizationLease.Create("TestPassword");
		LiquidAuthenticatedWalletSession session = await provider.OpenAsync(identity, lease, default);
		LiquidWalletRuntimeHandoff handoff = Assert.IsType<LiquidWalletRuntimeHandoff>(published);
		Assert.Same(session.StateOwner.NodeExpectation, session.NodeExpectation);
		Assert.Equal(ElementsPublicNetworkManifest.LiquidMainnet.ChainRpcName, session.NodeExpectation.Chain);
		Assert.Equal(100, session.NodeExpectation.PeginConfirmationDepth);
		LiquidWalletUiReceiveMaterial receiveMaterial = Assert.IsType<LiquidWalletUiReceiveMaterial>(handoff.ReceiveMaterial);
		LiquidWalletUiSnapshot balances = Assert.IsType<LiquidWalletUiSnapshot>(handoff.Balances);
		LiquidWalletUiSelectableOutputsSnapshot selectableOutputs = Assert.IsType<LiquidWalletUiSelectableOutputsSnapshot>(handoff.SelectableOutputs);
		LiquidWalletUiHistorySnapshot history = Assert.IsType<LiquidWalletUiHistorySnapshot>(handoff.History);

		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		// Independent vector: derive the expected spend script and SLIP-77 blinding public key
		// directly from the landed path/HKDF definitions, not through the production helpers
		// under test (LiquidWalletReceiveDerivation / LiquidSlip77PublicKey). The index is the
		// seeded constant, never a value read back from the production path.
		Assert.Equal(expectedNextIndex, session.LastIndex);
		ExtPubKey accountPublicKey = master.Derive(new KeyPath("2089617494h/1984574463h/0h")).Neuter();
		byte[] expectedScript = accountPublicKey.Derive(0).Derive((uint)expectedNextIndex).PubKey.WitHash.ScriptPubKey.ToBytes();
		byte[] rootPrivateKey = master.PrivateKey.ToBytes();
		byte[] slip77 = Array.Empty<byte>();
		byte[] scalar = Array.Empty<byte>();
		try
		{
			slip77 = LiquidKeyDomain.DeriveHkdf(rootPrivateKey, [], "WalletWasabi/Liquid/v1/slip77");
			scalar = HMACSHA256.HashData(slip77, expectedScript);
			byte[] expectedBlinding;
			using (var blindingKey = new Key(scalar))
			{
				expectedBlinding = blindingKey.PubKey.ToBytes();
			}

			Assert.Equal(expectedScript, receiveMaterial.NextReceiveScriptPubKey);
			Assert.Equal(expectedBlinding, receiveMaterial.NextReceiveBlindingPublicKey);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(scalar);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(rootPrivateKey);
		}

		// The handoff projections must carry the seeded nonzero revision and exact seeded
		// content, not a bootstrap revision-zero projection.
		Assert.Equal(seeded.State.Revision, balances.Revision);
		Assert.Equal(seeded.State.Revision, selectableOutputs.Revision);
		Assert.Equal(seeded.State.Revision, history.Revision);
		LiquidWalletUiAssetBalance peggedBalance = Assert.Single(balances.Balances);
		Assert.Equal(ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId, peggedBalance.AssetIdHex);
		Assert.Equal(12_345, peggedBalance.AtomicUnits);
		LiquidWalletUiSelectableOutput output = Assert.Single(selectableOutputs.Outputs);
		Assert.Equal(12_345, output.AtomicUnits);
		Assert.True(output.IsPeggedAsset);
		Assert.Equal(ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId, output.AssetIdHex);
		Assert.Single(history.Rows);
		Assert.Equal(identity.CanonicalWalletId, balances.WalletName);
		Assert.Equal(identity.NetworkManifestId, balances.NetworkManifestId);
	}

	[Fact]
	public async Task PreRefreshRawFetchCarriesExactBoundExpectationAndRequiredFeeAssetAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha");
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(walletDirectory));
		ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", manifest.ChainRpcName, manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		using var httpClient = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:18884") };
		using var rpcClient = new ElementsRpcClient(httpClient);
		using var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());
		LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);
		var requests = new[] { new ElementsRawTransactionRequest(new string('a', 64), null) };
		ElementsNodeExpectation? capturedExpectation = null;
		string? capturedFeeAsset = null;

		await Assert.ThrowsAsync<OperationCanceledException>(() => owner.GetPreRefreshRawTransactionsAsync(
			manifest,
			rpcClient,
			requests,
			CancellationToken.None,
			(expectation, feeAsset, capturedRequests, cancellationToken) =>
			{
				capturedExpectation = expectation;
				capturedFeeAsset = feeAsset;
				Assert.Same(requests, capturedRequests);
				return Task.FromException<ElementsExpectationBoundRawTransactionBatch>(new OperationCanceledException());
			}));

		Assert.True(ReferenceEquals(bound, owner.NodeExpectation));
		Assert.True(ReferenceEquals(owner.NodeExpectation, capturedExpectation));
		Assert.Equal(manifest.RequiredFeeAssetId, capturedFeeAsset, StringComparer.Ordinal);
		const string independentWrongFeeAsset = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
		Assert.NotEqual(independentWrongFeeAsset, capturedFeeAsset, StringComparer.Ordinal);
	}

	[Fact]
	public async System.Threading.Tasks.Task ProviderConsumesLeaseAndRejectsDuplicatePublishedIdentityAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha");
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", new LiquidWalletDirectories(walletDirectory));
		LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b"));
		CreateRpcProfile(directory.Path, "local", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b");

		using LiquidPasswordAuthorizationLease firstLease = LiquidPasswordAuthorizationLease.Create("TestPassword");
		LiquidAuthenticatedWalletSession session = await provider.OpenAsync(identity, firstLease, default);
		using LiquidPasswordAuthorizationLease duplicateLease = LiquidPasswordAuthorizationLease.Create("TestPassword");

		await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.OpenAsync(identity, duplicateLease, default));

		Assert.Equal(identity.CanonicalWalletId, session.PublicHandoff.CanonicalWalletId);

		await provider.CloseAsync(identity, default);
		Assert.True(session.IsDisposed);
		await provider.DisposeAsync();
	}

	[Fact]
	public async System.Threading.Tasks.Task ProviderDisposalDrainsPublishedSessionsAndRejectsNewOpensAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha");
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b", new LiquidWalletDirectories(walletDirectory));
		CreateRpcProfile(directory.Path, "local", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b");
		LiquidAuthenticatedRuntimeProvider provider = new(new LiquidRpcProfileSource(directory.Path), new LiquidWalletDirectories(walletDirectory), new ElementsPublicNetworkManifestSource("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b"));
		using LiquidPasswordAuthorizationLease openLease = LiquidPasswordAuthorizationLease.Create("TestPassword");
		LiquidAuthenticatedWalletSession session = await provider.OpenAsync(identity, openLease, default);

		await provider.DisposeAsync();

		Assert.True(session.IsDisposed);
		using LiquidPasswordAuthorizationLease rejectedLease = LiquidPasswordAuthorizationLease.Create("TestPassword");
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await provider.OpenAsync(identity, rejectedLease, default));
	}

	private sealed record PersistedLiquidState(LiquidWalletState State);

	private static PersistedLiquidState CreatePersistedLiquidState(string walletDirectory, string walletFile, string password, string walletName)
	{
		const string manifestId = "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b";
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey(password);
		ExtKey replayChild = master.Derive(new KeyPath(1108790945U | 0x80000000U));
		byte[] childMaterial = replayChild.PrivateKey.ToBytes();
		byte[] saltInput = Encoding.UTF8.GetBytes(manifestId + walletName);
		byte[] salt = SHA256.HashData(saltInput);
		byte[] replayKey = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/replay");
		byte[] context = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/context");
		try
		{
			// Seed a real nonzero-revision state: apply one owned pegged-asset output at
			// revision 0 -> 1, persist it, then issue one index (high-water becomes 1).
			LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId);
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
			_ = LiquidWalletLoadSave.Save(walletDirectory, walletName, state, 0, replayKey, context);
			_ = LiquidWalletExternalIndexAllocator.Allocate(walletDirectory, walletName, replayKey, context);
			return new PersistedLiquidState(state);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(replayKey);
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(saltInput);
			CryptographicOperations.ZeroMemory(childMaterial);
		}
	}

	private static void CreateRpcProfile(string dataDirectory, string profileName, string manifest)
	{
		string profileDirectory = Directory.CreateDirectory(Path.Combine(dataDirectory, "liquid-rpc-profiles")).FullName;
		string cookieFile = Path.Combine(dataDirectory, "cookie");
		File.WriteAllText(cookieFile, "user:password\n");
		string profileFile = Path.Combine(profileDirectory, profileName + ".json");
		File.WriteAllText(profileFile, $$"""
			{"schema":"walletwasabi-liquid-rpc-profile/v1","name":"{{profileName}}","endpoint":"http://127.0.0.1:18884","cookieFile":"{{cookieFile}}","network":"liquidv1","manifest":"{{manifest}}","connectTimeoutMs":1000,"requestTimeoutMs":1000}
			""");
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
		{
			File.SetUnixFileMode(cookieFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.SetUnixFileMode(profileFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	private static string ReadPassword(LiquidPasswordAuthorizationLease lease) => new(lease.Password);

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-identity-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}
}
