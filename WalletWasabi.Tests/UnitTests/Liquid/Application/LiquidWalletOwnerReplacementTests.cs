using System;
using System.IO;
using System.Linq;
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
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletOwnerReplacementTests
{
	[Fact]
	public void ReplacementPreservesReceiveMaterialAndReprojectsCommittedState()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		PersistedStateSeed seed = SeedPersistedState(walletDirectory, walletFile, "TestPassword", "alpha", manifest);
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
		LiquidAuthenticatedWalletStateOwner owner = LiquidAuthenticatedWalletStateOwner.Open(
			identity, manifest, bound, walletDirectory, master, adapter, rpcClient);

		LiquidAuthenticatedWalletStateOwner generationOnlyReplacement = owner.CreateReplacement(
			owner.State,
			owner.PersistenceGeneration + 1);
		Assert.Same(owner.State, generationOnlyReplacement.State);
		Assert.Equal(owner.StateRevision, generationOnlyReplacement.StateRevision);
		Assert.Equal(owner.PersistenceGeneration + 1, generationOnlyReplacement.PersistenceGeneration);
		Assert.Equal(owner.ExternalIndexHighWater, generationOnlyReplacement.ExternalIndexHighWater);
		Assert.Throws<InvalidOperationException>(() => owner.CreateReplacement(
			LiquidWalletState.Empty(LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId)),
			owner.PersistenceGeneration + 1));

		// Commit one more owned output on top of the seeded base state.
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidSpendKeyReference externalKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
			LiquidKeyBranch.External,
			0);
		LiquidOwnedOutput received = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(LiquidTransactionId.ParseRpcHex(new string('b', 64)), 0),
			externalKey.GetScriptPubKey(),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 7_777),
			externalKey);
		LiquidWalletState committedState = seed.BaseState.Apply(
			seed.BaseState.Revision,
			LiquidWalletTransactionDelta.Create(LiquidTransactionId.ParseRpcHex(new string('b', 64)), [], [received]));
		ulong nextGeneration = owner.PersistenceGeneration + 1;

		LiquidAuthenticatedWalletStateOwner replacement = owner.CreateReplacement(committedState, nextGeneration);

		// The captured owner is never mutated.
		Assert.Equal(seed.BaseState.Revision, owner.StateRevision);
		Assert.Equal(seed.BaseState.Revision, owner.Balances.Revision);

		// Revision/generation advance; high-water, descriptor, last-index, and receive material are preserved.
		Assert.Equal(committedState.Revision, replacement.StateRevision);
		Assert.Equal(nextGeneration, replacement.PersistenceGeneration);
		Assert.Equal(owner.Descriptor, replacement.Descriptor);
		Assert.Equal(owner.LastIndex, replacement.LastIndex);
		// The receive script/blinding keys are preserved; the published receive material is
		// rebound (a new instance) so its NextReceiveLabels track the committed state's
		// durable label map for the next-receive index (empty here, since none is set).
		Assert.Equal(owner.ReceiveMaterial.NextReceiveScriptPubKey, replacement.ReceiveMaterial.NextReceiveScriptPubKey);
		Assert.Equal(owner.ReceiveMaterial.NextReceiveBlindingPublicKey, replacement.ReceiveMaterial.NextReceiveBlindingPublicKey);
		Assert.Empty(replacement.ReceiveMaterial.NextReceiveLabels);
		Assert.Same(owner.NodeExpectation, replacement.NodeExpectation);

		// Balances/selectable/history are re-projected from the committed state consistently.
		Assert.Equal(committedState.Revision, replacement.Balances.Revision);
		Assert.Equal(committedState.Revision, replacement.SelectableOutputs.Revision);
		Assert.Equal(committedState.Revision, replacement.History.Revision);
		LiquidWalletUiAssetBalance peggedBalance = Assert.Single(replacement.Balances.Balances);
		Assert.Equal(12_345 + 7_777, peggedBalance.AtomicUnits);
		Assert.Equal(2, replacement.SelectableOutputs.Outputs.Count);
		Assert.Equal(2, replacement.History.Rows.Count);

		// The replacement exposes no key or RPC authority beyond the captured surface.
		Assert.Null(typeof(LiquidAuthenticatedWalletStateOwner).GetProperties()
			.FirstOrDefault(property => property.Name.Contains("Key", StringComparison.Ordinal) && property.PropertyType != typeof(LiquidWalletUiReceiveMaterial)));
	}

	private sealed record PersistedStateSeed(LiquidWalletState BaseState);

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }

		public void Dispose()
		{
			try
			{
				if (Directory.Exists(Path))
				{
					Directory.Delete(Path, recursive: true);
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

	private static PersistedStateSeed SeedPersistedState(
		string walletDirectory,
		string walletFile,
		string password,
		string walletName,
		ElementsPublicNetworkManifest manifest)
	{
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey(password);
		ExtKey replayChild = master.Derive(new KeyPath(1108790945U | 0x80000000U));
		byte[] childMaterial = replayChild.PrivateKey.ToBytes();
		byte[] saltInput = System.Text.Encoding.UTF8.GetBytes(manifest.ManifestId + walletName);
		byte[] salt = System.Security.Cryptography.SHA256.HashData(saltInput);
		byte[] replayKey = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/replay");
		byte[] context = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/context");
		try
		{
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
			return new PersistedStateSeed(state);
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
}
