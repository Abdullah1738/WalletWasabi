using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Application;

using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;
#pragma warning disable CA2000 // Provider ownership is explicitly closed in each test.

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidProviderOwnershipSeamTests
{
	[Fact]
	public async Task PasswordAuthorizationRejectsEmptyPasswordAsync()
	{
		await using LiquidWalletApplicationClient client = CreateApplicationClient();
		Assert.Throws<ArgumentException>(() => client.CreateOpenAuthorization(ReadOnlySpan<char>.Empty));
	}

	[Fact]
	public async Task PasswordAuthorizationRejectsOversizedPasswordAsync()
	{
		await using LiquidWalletApplicationClient client = CreateApplicationClient();
		Assert.Throws<ArgumentException>(() => client.CreateOpenAuthorization(new string('x', 1025)));
	}

	[Fact]
	public async Task PasswordAuthorizationDisposesAndZeroizesOwnedBufferAsync()
	{
		await using LiquidWalletApplicationClient client = CreateApplicationClient();
		LiquidWalletOpenAuthorization authorization = client.CreateOpenAuthorization("secret");
		char[] buffer = Assert.IsType<char[]>(typeof(LiquidWalletOpenAuthorization)
			.GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(authorization));

		authorization.Dispose();
		authorization.Dispose();

		Assert.All(buffer, value => Assert.Equal('\0', value));
		Assert.Throws<InvalidOperationException>(() => authorization.TakeBuffer());
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
		await using LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource(identity.NetworkManifestId));

		LiquidAuthenticatedWalletSession session = await OpenAsync(provider, identity, "TestPassword");
		LiquidWalletRuntimeHandoff handoff = Assert.IsType<LiquidWalletRuntimeHandoff>(provider.CurrentHandoff);
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
	public async Task PreRefreshRawFetchRejectsNullRequestsBeforeRpcEntryAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha", manifest);
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(walletDirectory));
		ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", "liquidtestnet", manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		using var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18884") };
		int rpcEntries = 0;
		using var rpcClient = new ElementsRpcClient(
			httpClient,
			timeouts: null,
			(expectation, feeAsset, requests, _) => rpcEntries++);
		using var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());
		LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);

		await Assert.ThrowsAsync<ArgumentNullException>(() => owner.GetPreRefreshRawTransactionsAsync(
			manifest,
			rpcClient,
			requests: null!,
			CancellationToken.None));
		Assert.Equal(0, rpcEntries);

		IReadOnlyList<ElementsRawTransactionRequest> requestsWithNull = [new ElementsRawTransactionRequest(new string('a', 64), null), null!];
		await Assert.ThrowsAsync<ArgumentException>(() => owner.GetPreRefreshRawTransactionsAsync(
			manifest,
			rpcClient,
			requestsWithNull,
			CancellationToken.None));
		Assert.Equal(0, rpcEntries);
	}

	[Fact]
	public async Task PreRefreshEntryObserverCannotSuppressRealRpcCallAsync()
	{
		const string TransactionId = "1111111111111111111111111111111111111111111111111111111111111111";
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha", manifest);
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(walletDirectory));
		ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", "liquidtestnet", manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		using var handler = new PreRefreshRpcHandler(TransactionId);
		using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18884/") };
		using var rpcClient = new ElementsRpcClient(
			httpClient,
			timeouts: null,
			(expectation, feeAsset, requests, _) => throw new InvalidOperationException("Observer failure marker."));
		using var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());
		LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);
		var requests = new[] { new ElementsRawTransactionRequest(TransactionId, null) };

		InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.GetPreRefreshRawTransactionsAsync(
			manifest,
			rpcClient,
			requests,
			CancellationToken.None));

		Assert.Equal("The expectation-bound raw-transaction entry observer failed after the real RPC call completed.", failure.Message);
		Assert.IsType<InvalidOperationException>(failure.InnerException);
		Assert.Equal("Observer failure marker.", failure.InnerException!.Message);
		Assert.Contains("getrawtransaction", handler.Methods);
		Assert.Equal(17, handler.Methods.Count);
	}

	[Fact]
	public async Task PreRefreshEntryObserverOperationCanceledExceptionCannotSuppressRealRpcCallAsync()
	{
		const string TransactionId = "1111111111111111111111111111111111111111111111111111111111111111";
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha", manifest);
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(walletDirectory));
		ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", "liquidtestnet", manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		using var handler = new PreRefreshRpcHandler(TransactionId);
		using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18884/") };
		using var rpcClient = new ElementsRpcClient(
			httpClient,
			timeouts: null,
			(expectation, feeAsset, requests, _) => throw new OperationCanceledException("Observer cancellation marker."));
		using var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());
		LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);
		var requests = new[] { new ElementsRawTransactionRequest(TransactionId, null) };

		OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(() => owner.GetPreRefreshRawTransactionsAsync(
			manifest,
			rpcClient,
			requests,
			CancellationToken.None));

		Assert.Equal("Observer cancellation marker.", failure.Message);
		Assert.Contains("getrawtransaction", handler.Methods);
		Assert.Equal(17, handler.Methods.Count);
	}

	[Fact]
	public async Task SessionPreRefreshRawFetchRunsRealRpcFullFenceAndEmptySkipAsync()
	{
		const string TransactionId = "1111111111111111111111111111111111111111111111111111111111111111";
		const string ExpectedFeeAsset = "144c654344aa716d6f3abcc1ca90e5641e4e2a7f633bc09fe3baf64585819a49";
		const string IndependentWrongFeeAsset = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha", manifest);
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(walletDirectory));
		ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", "liquidtestnet", manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		using var handler = new PreRefreshRpcHandler(TransactionId);
		using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18884/") };
		ElementsNodeExpectation? capturedExpectation = null;
		string? capturedFeeAsset = null;
		IReadOnlyList<ElementsRawTransactionRequest>? capturedRequests = null;
		int rpcEntries = 0;
		using var rpcClient = new ElementsRpcClient(
			httpClient,
			timeouts: null,
			(expectation, feeAsset, requests, _) =>
			{
				rpcEntries++;
				capturedExpectation = expectation;
				capturedFeeAsset = feeAsset;
				capturedRequests = requests;
			});
		using var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());
		LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);
		var handoff = new LiquidWalletRuntimeHandoff(
			identity.CanonicalWalletId,
			identity.NetworkManifestId,
			owner.Balances,
			owner.SelectableOutputs,
			owner.History,
			owner.ReceiveMaterial);
		await using var session = new LiquidAuthenticatedWalletSession(
			identity, handoff, keyManager, adapter, manifest, rpcClient, master, owner,
			owner.Descriptor, owner.LastIndex, walletDirectory);

		using var canceled = new CancellationTokenSource();
		canceled.Cancel();
		Task<ElementsExpectationBoundRawTransactionBatch?> emptyFetch = session.FetchPreRefreshRawTransactionsAsync(canceled.Token);
		Assert.True(emptyFetch.IsCompletedSuccessfully);
		Assert.Null(await emptyFetch);
		Assert.Equal(0, rpcEntries);
		Assert.Empty(handler.Methods);

		session.RecordAcceptedTransactionId(TransactionId);
		ElementsExpectationBoundRawTransactionBatch? batch =
			await session.FetchPreRefreshRawTransactionsAsync(CancellationToken.None);

		Assert.NotNull(batch);
		Assert.Equal(1, batch.TransactionCount);
		Assert.Same(manifest, session.Manifest);
		Assert.True(ReferenceEquals(bound, owner.NodeExpectation));
		Assert.True(ReferenceEquals(owner.NodeExpectation, capturedExpectation));
		Assert.True(ReferenceEquals(bound, capturedExpectation));
		Assert.Equal(ExpectedFeeAsset, capturedFeeAsset, StringComparer.Ordinal);
		Assert.NotEqual(IndependentWrongFeeAsset, capturedFeeAsset, StringComparer.Ordinal);
		ElementsRawTransactionRequest capturedRequest = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ElementsRawTransactionRequest>>(capturedRequests));
		Assert.Equal(TransactionId, capturedRequest.TransactionId, StringComparer.Ordinal);
		Assert.Null(capturedRequest.BlockHash);
		Assert.Equal(1, rpcEntries);
		Assert.Equal(
			["getblockchaininfo", "getblockhash", "getnetworkinfo", "getblockchaininfo", "getblockhash", "getblockhash", "getsidechaininfo", "getblockchaininfo", "getblockhash", "getblockchaininfo", "getblockhash", "getsidechaininfo", "getblockchaininfo", "getblockhash", "getrawtransaction", "getblockchaininfo", "getblockhash"],
			handler.Methods);
	}

	[Fact]
	public void PreRefreshOwnerConsumerIlUsesRequiredFeeAssetAndOneRealRpcCall()
	{
		MethodInfo consumer = typeof(LiquidAuthenticatedWalletStateOwner).GetMethod(
			"GetPreRefreshRawTransactionsAsync",
			BindingFlags.Instance | BindingFlags.NonPublic)!;
		MethodBase[] calls = ReadCalledMethods(consumer).ToArray();

		Assert.Equal(1, calls.Count(method => method.DeclaringType == typeof(ElementsRpcClient)
			&& method.Name == nameof(ElementsRpcClient.GetExpectationBoundRawTransactionsAsync)));
		Assert.Equal(2, calls.Count(method => method.DeclaringType == typeof(ElementsPublicNetworkManifest)
			&& method.Name == "get_RequiredFeeAssetId"));
		Assert.DoesNotContain(calls, method => method.Name is "get_PeggedAssetId" or "get_PeggedAsset" or "Normalize" or "GetObservedRawTransactionsAsync");
		Assert.DoesNotContain(calls, method => method.IsConstructor && method.DeclaringType == typeof(ElementsNodeExpectation));
	}

	[Fact]
	public void FreshChildLoadsNonzeroSeedAndBuildsIndependentPreRefreshRequests()
	{
		const string TransactionId = "1111111111111111111111111111111111111111111111111111111111111111";
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha", ElementsPublicNetworkManifest.LiquidTestnet);
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		ExtKey replayChild = master.Derive(new KeyPath(1108790945U | 0x80000000U));
		byte[] childMaterial = replayChild.PrivateKey.ToBytes();
		byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes(ElementsPublicNetworkManifest.LiquidTestnet.ManifestId + "alpha"));
		byte[] replayKey = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/replay");
		byte[] context = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/context");
		try
		{
			string coreAssembly = typeof(LiquidAuthenticatedRuntimeProvider).Assembly.Location;
			string childPath = RoslynFreshChildHarness.CompileChildAssembly(
				""""
				using System;
				using System.Collections.Generic;
				using System.IO;
				using System.Linq;
				using System.Net;
				using System.Net.Http;
				using System.Text;
				using System.Text.Json;
				using System.Threading;
				using System.Threading.Tasks;
				using NBitcoin;
				using WalletWasabi.Blockchain.Keys;
				using WalletWasabi.Liquid.Application;
				using WalletWasabi.Liquid.Network;
				using WalletWasabi.Liquid.Rpc;
				using WalletWasabi.Liquid.Wallet;
				using WalletWasabi.Liquid.Wallet.Ui;

				using JsonDocument input = JsonDocument.Parse(Console.In.ReadToEnd());
				JsonElement root = input.RootElement;
				byte[] key = Convert.FromHexString(root.GetProperty("key").GetString()!);
				byte[] context = Convert.FromHexString(root.GetProperty("context").GetString()!);
				string transactionId = root.GetProperty("transactionId").GetString()!;
				string walletDirectory = root.GetProperty("walletDirectory").GetString()!;
				LiquidWalletLoadSaveResult loaded = LiquidWalletLoadSave.Load(walletDirectory, "alpha", key, context);
				ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
				var profile = new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", "liquidtestnet", manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
				ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(manifest, profile);
				var handler = new Handler(transactionId);
				using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18884/") };
				ElementsNodeExpectation? captured = null;
				IReadOnlyList<ElementsRawTransactionRequest>? capturedRequests = null;
				using var rpcClient = new ElementsRpcClient(httpClient, null, (expectation, _, requests, _) => { captured = expectation; capturedRequests = requests; });
				string walletFile = root.GetProperty("walletFile").GetString()!;
				KeyManager keyManager = KeyManager.FromFile(walletFile);
				ExtKey master = keyManager.GetMasterExtKey("TestPassword");
				using var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());
				var identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(walletDirectory));
				LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(identity, manifest, bound, walletDirectory, master, adapter, rpcClient);
				var handoff = new LiquidWalletRuntimeHandoff(identity.CanonicalWalletId, identity.NetworkManifestId, owner.Balances, owner.SelectableOutputs, owner.History, owner.ReceiveMaterial);
				var session = new LiquidAuthenticatedWalletSession(identity, handoff, keyManager, adapter, manifest, rpcClient, master, owner, owner.Descriptor, owner.LastIndex, walletDirectory);
				session.RecordAcceptedTransactionId(transactionId);
				ElementsExpectationBoundRawTransactionBatch? batch = session.FetchPreRefreshRawTransactionsAsync(CancellationToken.None).GetAwaiter().GetResult();
				var request = capturedRequests!.Single();
				Console.Write(JsonSerializer.Serialize(new {
					token = "PRE_REFRESH_CHILD_V1",
					revision = loaded.Revision,
					generation = loaded.Generation,
					transactionId = request.TransactionId,
					nullBlockHash = request.BlockHash is null,
					boundToOwner = ReferenceEquals(bound, owner.NodeExpectation),
					ownerToRpcEntry = ReferenceEquals(owner.NodeExpectation, captured),
					boundToRpcEntry = ReferenceEquals(bound, captured),
					batchOk = batch is not null && batch.TransactionCount == 1,
					methods = handler.Methods.ToArray()
				}));

				sealed class Handler(string txid) : HttpMessageHandler
				{
					const string Startup = "abababababababababababababababababababababababababababababababab";
					const string Best = "0101010101010101010101010101010101010101010101010101010101010101";
					const string Genesis = "a771da8e52ee6ad581ed1e9a99825e5b3b7992225534eaa2ae23244fe26ab1c1";
					const string Fee = "144c654344aa716d6f3abcc1ca90e5641e4e2a7f633bc09fe3baf64585819a49";
					int _sidechainCalls;
					internal List<string> Methods { get; } = [];
					protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
					{
						using JsonDocument requestJson = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
						string method = requestJson.RootElement.GetProperty("method").GetString()!;
						string id = requestJson.RootElement.GetProperty("id").GetString()!;
						string parameters = requestJson.RootElement.GetProperty("params").GetRawText();
						Methods.Add(method);
						int sidechainCallIndex = method == "getsidechaininfo" ? _sidechainCalls++ : -1;
						string result = method switch {
							"getnodegeneration" => $$"""{"startup_id":"{{Startup}}","chainstate_revision":9,"blocks":42,"bestblockhash":"{{Best}}"}""",
							"getnetworkinfo" => """{"version":230303,"protocolversion":70016,"subversion":"/Elements Core:23.3.3/","localrelay":true,"networkactive":true,"warnings":""}""",
							"getblockchaininfo" => $$"""{"chain":"liquidtestnet","blocks":42,"headers":42,"bestblockhash":"{{Best}}","initialblockdownload":false,"pruned":false,"trim_headers":false,"warnings":""}""",
							"getblockhash" when parameters == "[0]" => JsonSerializer.Serialize(Genesis),
							"getblockhash" => JsonSerializer.Serialize(Best),
							"getsidechaininfo" when sidechainCallIndex > 0 => $$"""{"pegged_asset":"{{Fee}}","fee_asset":"{{Fee}}"}""",
							"getsidechaininfo" => $$"""{"fedpegscript":"51","pegged_asset":"{{Fee}}","parent_blockhash":"0000000000000000000000000000000000000000000000000000000000000000","pegin_confirmation_depth":8,"enforce_pak":false}""",
							"getrawtransaction" when parameters == $"[\"{txid}\",false]" => JsonSerializer.Serialize("010203"),
							_ => throw new InvalidOperationException()
						};
						return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"result\":{result},\"error\":null,\"id\":\"{id}\"}}", Encoding.UTF8, "application/json"), RequestMessage = request };
					}
				}
				"""",
				"pre-refresh-owner-child",
				"PreRefreshOwnerChild.dll",
				[
					coreAssembly,
					typeof(Enumerable).Assembly.Location,
					typeof(HttpClient).Assembly.Location,
					typeof(Uri).Assembly.Location,
					typeof(HttpStatusCode).Assembly.Location,
					typeof(ExtKey).Assembly.Location,
					typeof(LiquidWalletRuntimeHandoff).Assembly.Location,
				]);
			File.Copy(coreAssembly, Path.Combine(Path.GetDirectoryName(childPath)!, "WalletWasabi.dll"), overwrite: true);
			using JsonDocument output = RoslynFreshChildHarness.RunChild(childPath, new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["walletDirectory"] = walletDirectory,
				["walletFile"] = walletFile,
				["key"] = Convert.ToHexString(replayKey),
				["context"] = Convert.ToHexString(context),
				["transactionId"] = TransactionId,
			});
			Assert.Equal("PRE_REFRESH_CHILD_V1", output.RootElement.GetProperty("token").GetString());
			Assert.Equal(1UL, output.RootElement.GetProperty("revision").GetUInt64());
			Assert.Equal(1UL, output.RootElement.GetProperty("generation").GetUInt64());
			Assert.Equal(TransactionId, output.RootElement.GetProperty("transactionId").GetString(), StringComparer.Ordinal);
			Assert.True(output.RootElement.GetProperty("nullBlockHash").GetBoolean());
			Assert.True(output.RootElement.GetProperty("boundToOwner").GetBoolean());
			Assert.True(output.RootElement.GetProperty("ownerToRpcEntry").GetBoolean());
			Assert.True(output.RootElement.GetProperty("boundToRpcEntry").GetBoolean());
			Assert.True(output.RootElement.GetProperty("batchOk").GetBoolean());
			Assert.Equal(
				new[] { "getblockchaininfo", "getblockhash", "getnetworkinfo", "getblockchaininfo", "getblockhash", "getblockhash", "getsidechaininfo", "getblockchaininfo", "getblockhash", "getblockchaininfo", "getblockhash", "getsidechaininfo", "getblockchaininfo", "getblockhash", "getrawtransaction", "getblockchaininfo", "getblockhash" },
				output.RootElement.GetProperty("methods").EnumerateArray().Select(method => method.GetString()!).ToArray());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(replayKey);
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(childMaterial);
		}
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

		LiquidAuthenticatedWalletSession session = await OpenAsync(provider, identity, "TestPassword");

		await Assert.ThrowsAsync<InvalidOperationException>(async () => await OpenAsync(provider, identity, "TestPassword"));

		Assert.Equal(identity.CanonicalWalletId, session.PublicHandoff.CanonicalWalletId);

		await provider.CloseAsync(identity, default);
		Assert.True(session.IsDisposed);
		await provider.DisposeAsync();
	}

	[Fact]
	public async Task ProductionSessionOperationLeaseBlocksProviderDisposalAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha");
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha",
			walletFile,
			"local",
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId,
			new LiquidWalletDirectories(walletDirectory));
		CreateRpcProfile(directory.Path, "local", ElementsPublicNetworkManifest.LiquidMainnet.ManifestId);
#pragma warning disable CA2000 // Provider ownership is closed after the real leased operation drains.
		LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource(ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
#pragma warning restore CA2000
		LiquidAuthenticatedWalletSession session = await OpenAsync(provider, identity, "TestPassword");
		using LiquidWalletOperationLease operationLease = provider.AcquireOperation(identity.CanonicalWalletId);
		Assert.Same(session, operationLease.Session);

		Task disposal = provider.DisposeAsync().AsTask();
		Assert.False(disposal.IsCompleted);
		Assert.False(session.IsDisposed);
		Assert.Throws<ObjectDisposedException>(() => provider.AcquireOperation(identity.CanonicalWalletId));

		operationLease.Dispose();
		await disposal;

		Assert.True(session.IsDisposed);
		Assert.Null(provider.CurrentHandoff);
	}

	[Fact]
	public async Task ConcurrentCloseAndProviderDisposalJoinDetachedSessionDrainAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha");
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha",
			walletFile,
			"local",
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId,
			new LiquidWalletDirectories(walletDirectory));
		CreateRpcProfile(directory.Path, "local", ElementsPublicNetworkManifest.LiquidMainnet.ManifestId);
#pragma warning disable CA2000 // Provider is disposed after all close/disposal joiners complete.
		LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource(ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
#pragma warning restore CA2000
		LiquidAuthenticatedWalletSession session = await OpenAsync(provider, identity, "TestPassword");
		using LiquidWalletOperationLease operationLease = provider.AcquireOperation(identity.CanonicalWalletId);

		Task firstClose = provider.CloseAsync(identity, default).AsTask();
		Assert.False(firstClose.IsCompleted);
		Task providerDisposal = provider.DisposeAsync().AsTask();
		Assert.False(providerDisposal.IsCompleted);
		Task secondClose = provider.CloseAsync(identity, default).AsTask();
		Assert.False(secondClose.IsCompleted);
		Assert.False(session.IsDisposed);
		Assert.Null(provider.CurrentHandoff);

		operationLease.Dispose();
		await Task.WhenAll(firstClose, providerDisposal, secondClose);

		Assert.True(session.IsDisposed);
		Assert.Null(provider.CurrentHandoff);
	}

	[Fact]
	public void OpenPeeksNextReceiveIndexAndScriptPubKeyStableAcrossOpens()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", "alpha");
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha", walletFile, "local", manifest.ManifestId, new LiquidWalletDirectories(walletDirectory));
		ElementsNodeExpectation bound = ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/cookie", manifest.ChainRpcName, manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey("TestPassword");
		using var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18884") };
		using var rpcClient = new ElementsRpcClient(httpClient);
		using var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, keyManager.GetNetwork());

		LiquidAuthenticatedWalletStateOwner firstOpen = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);
		LiquidAuthenticatedWalletStateOwner secondOpen = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);

		Assert.Equal(firstOpen.LastIndex, secondOpen.LastIndex);
		Assert.Equal(firstOpen.ReceiveMaterial.NextReceiveScriptPubKey, secondOpen.ReceiveMaterial.NextReceiveScriptPubKey);
		Assert.Equal(firstOpen.ReceiveMaterial.NextReceiveBlindingPublicKey, secondOpen.ReceiveMaterial.NextReceiveBlindingPublicKey);
		Assert.Equal(firstOpen.Descriptor, secondOpen.Descriptor);
		Assert.Equal(firstOpen.PersistenceGeneration, secondOpen.PersistenceGeneration);
		Assert.Equal(firstOpen.ExternalIndexHighWater, secondOpen.ExternalIndexHighWater);
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
#pragma warning disable CA2000 // Provider is explicitly disposed below.
		LiquidAuthenticatedRuntimeProvider provider = new(new LiquidRpcProfileSource(directory.Path), new LiquidWalletDirectories(walletDirectory), new ElementsPublicNetworkManifestSource("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b"));
#pragma warning restore CA2000
		LiquidAuthenticatedWalletSession session = await OpenAsync(provider, identity, "TestPassword");

		await provider.DisposeAsync();

		Assert.True(session.IsDisposed);
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await OpenAsync(provider, identity, "TestPassword"));
	}

	private sealed record PersistedLiquidState(LiquidWalletState State);

	private static PersistedLiquidState CreatePersistedLiquidState(
		string walletDirectory,
		string walletFile,
		string password,
		string walletName,
		ElementsPublicNetworkManifest? manifest = null)
	{
		manifest ??= ElementsPublicNetworkManifest.LiquidMainnet;
		string manifestId = manifest.ManifestId;
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
			LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
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

	private static async Task<LiquidAuthenticatedWalletSession> OpenAsync(
		LiquidAuthenticatedRuntimeProvider provider,
		LiquidWalletIdentity identity,
		string password)
	{
		char[] buffer = password.ToCharArray();
		try
		{
			return await provider.OpenAsync(identity, buffer, default);
		}
		finally
		{
			LiquidWalletOpenAuthorization.ZeroBuffer(buffer);
		}
	}

	private static LiquidWalletApplicationClient CreateApplicationClient()
	{
		string root = Path.GetTempPath();
		return LiquidWalletApplicationClient.Create(new(
			root,
			root,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
	}

	private static IEnumerable<MethodBase> ReadCalledMethods(MethodBase method)
	{
		byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
		for (int offset = 0; offset < il.Length;)
		{
			byte first = il[offset++];
			OpCode opCode = first == 0xfe ? MultiByteOpCodes[il[offset++]] : SingleByteOpCodes[first];
			if (opCode.OperandType == OperandType.InlineMethod)
			{
				int token = BitConverter.ToInt32(il, offset);
				yield return method.Module.ResolveMethod(token)!;
			}
			offset += OperandSize(opCode.OperandType, il, offset);
		}
	}

	private static int OperandSize(OperandType operandType, byte[] il, int offset) => operandType switch
	{
		OperandType.InlineNone => 0,
		OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
		OperandType.InlineVar => 2,
		OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
		OperandType.InlineI8 or OperandType.InlineR => 8,
		OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, offset) * 4),
		_ => throw new InvalidOperationException($"Unsupported IL operand type {operandType}."),
	};

	private static readonly OpCode[] SingleByteOpCodes = BuildOpCodeTable(multiByte: false);
	private static readonly OpCode[] MultiByteOpCodes = BuildOpCodeTable(multiByte: true);

	private static OpCode[] BuildOpCodeTable(bool multiByte)
	{
		var table = new OpCode[256];
		foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
		{
			OpCode opCode = (OpCode)field.GetValue(null)!;
			ushort value = unchecked((ushort)opCode.Value);
			if ((value > 0xff) == multiByte)
			{
				table[value & 0xff] = opCode;
			}
		}
		return table;
	}

	private sealed class PreRefreshRpcHandler(string transactionId) : HttpMessageHandler
	{
		private const string StartupId = "abababababababababababababababababababababababababababababababab";
		private const string BestBlockHash = "0101010101010101010101010101010101010101010101010101010101010101";
		private const string GenesisBlockHash = "a771da8e52ee6ad581ed1e9a99825e5b3b7992225534eaa2ae23244fe26ab1c1";
		private const string FeeAsset = "144c654344aa716d6f3abcc1ca90e5641e4e2a7f633bc09fe3baf64585819a49";

		private int _sidechainCalls;

		internal List<string> Methods { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument document = JsonDocument.Parse(body);
			string method = document.RootElement.GetProperty("method").GetString()!;
			string id = document.RootElement.GetProperty("id").GetString()!;
			string parameters = document.RootElement.GetProperty("params").GetRawText();
			Methods.Add(method);
			int sidechainCallIndex = method == "getsidechaininfo" ? _sidechainCalls++ : -1;
			string result = method switch
			{
				"getnodegeneration" => $$"""{"startup_id":"{{StartupId}}","chainstate_revision":9,"blocks":42,"bestblockhash":"{{BestBlockHash}}"}""",
				"getnetworkinfo" => """{"version":230303,"protocolversion":70016,"subversion":"/Elements Core:23.3.3/","localrelay":true,"networkactive":true,"warnings":""}""",
				"getblockchaininfo" => $$"""{"chain":"liquidtestnet","blocks":42,"headers":42,"bestblockhash":"{{BestBlockHash}}","initialblockdownload":false,"pruned":false,"trim_headers":false,"warnings":""}""",
				"getblockhash" when parameters == "[0]" => JsonSerializer.Serialize(GenesisBlockHash),
				"getblockhash" => JsonSerializer.Serialize(BestBlockHash),
				"getsidechaininfo" when sidechainCallIndex > 0 => $$"""{"pegged_asset":"{{FeeAsset}}","fee_asset":"{{FeeAsset}}"}""",
				"getsidechaininfo" => $$"""{"fedpegscript":"51","pegged_asset":"{{FeeAsset}}","parent_blockhash":"0000000000000000000000000000000000000000000000000000000000000000","pegin_confirmation_depth":8,"enforce_pak":false}""",
				"getrawtransaction" when parameters == $"[\"{transactionId}\",false]" => JsonSerializer.Serialize("010203"),
				_ => throw new InvalidOperationException($"Unexpected RPC method '{method}'."),
			};
			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent($"{{\"result\":{result},\"error\":null,\"id\":\"{id}\"}}", Encoding.UTF8, "application/json"),
				RequestMessage = request,
			};
			return response;
		}
	}

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
