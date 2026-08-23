using System;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidAuthenticatedWalletStateOwner
{
	private const uint ReplayContextBranchIndex = 1108790945;
	private const string ReplayKeyInfo = "WalletWasabi/Liquid/v1/replay";
	private const string ContextKeyInfo = "WalletWasabi/Liquid/v1/context";
	private const string Slip77Info = "WalletWasabi/Liquid/v1/slip77";

	private readonly LiquidWalletExternalIndexAllocation _allocation;

	private LiquidAuthenticatedWalletStateOwner(
		LiquidWalletExternalIndexAllocation allocation,
		LiquidWalletReceiveDerivation receiveDerivation,
		byte[] blindingPublicKey,
		string walletName,
		ElementsPublicNetworkManifest manifest)
	{
		_allocation = allocation ?? throw new ArgumentNullException(nameof(allocation));
		StateRevision = allocation.StateRevision;
		PersistenceGeneration = allocation.PersistedGeneration;
		Descriptor = receiveDerivation.Descriptor;
		LastIndex = receiveDerivation.LastIndex;
		ReceiveMaterial = new LiquidWalletUiReceiveMaterial(receiveDerivation.ScriptPubKey, blindingPublicKey);
		Balances = LiquidWalletUiFacade.CaptureAllocationBalances(walletName, manifest, allocation);
		SelectableOutputs = LiquidWalletUiFacade.CaptureSelectableOutputs(walletName, manifest, allocation);
		History = LiquidWalletUiFacade.CaptureAllocationHistory(walletName, manifest, allocation);
	}

	internal LiquidWalletExternalIndexAllocation Allocation => _allocation;
	internal ulong StateRevision { get; }
	internal ulong PersistenceGeneration { get; }
	internal string Descriptor { get; }
	internal ulong LastIndex { get; }
	internal LiquidWalletUiReceiveMaterial ReceiveMaterial { get; }
	internal LiquidWalletUiSnapshot Balances { get; }
	internal LiquidWalletUiSelectableOutputsSnapshot SelectableOutputs { get; }
	internal LiquidWalletUiHistorySnapshot History { get; }

	internal static LiquidAuthenticatedWalletStateOwner Open(
		LiquidWalletIdentity identity,
		ElementsPublicNetworkManifest manifest,
		string walletDataDirectory,
		ExtKey authenticatedMaster,
		LiquidWalletSignerKeyAdapter signerKeyAdapter,
		ElementsRpcClient rpcClient)
	{
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentException.ThrowIfNullOrEmpty(walletDataDirectory);
		ArgumentNullException.ThrowIfNull(authenticatedMaster);
		ArgumentNullException.ThrowIfNull(signerKeyAdapter);
		ArgumentNullException.ThrowIfNull(rpcClient);
		if (!StringComparer.Ordinal.Equals(identity.NetworkManifestId, manifest.ManifestId))
		{
			throw new InvalidOperationException("The Liquid wallet state owner manifest does not match the wallet identity.");
		}

		byte[] masterPrivateKey = authenticatedMaster.PrivateKey.ToBytes();
		byte[] slip77Master = Array.Empty<byte>();
		byte[] replayChildMaterial = Array.Empty<byte>();
		byte[] saltInput = Array.Empty<byte>();
		byte[] salt = Array.Empty<byte>();
		byte[] replayKey = Array.Empty<byte>();
		byte[] context = Array.Empty<byte>();
		try
		{
			slip77Master = LiquidKeyDomain.DeriveHkdf(masterPrivateKey, [], Slip77Info);
			ExtKey replayContextChild = authenticatedMaster.Derive(new KeyPath(ReplayContextBranchIndex | 0x80000000U));
			replayChildMaterial = replayContextChild.PrivateKey.ToBytes();
			(byte[] network, byte[] wallet) = (Encoding.UTF8.GetBytes(identity.NetworkManifestId), Encoding.UTF8.GetBytes(identity.CanonicalWalletId));
			saltInput = new byte[network.Length + wallet.Length];
			network.CopyTo(saltInput, 0);
			wallet.CopyTo(saltInput, network.Length);
			salt = SHA256.HashData(saltInput);
			replayKey = LiquidKeyDomain.DeriveHkdf(replayChildMaterial, salt, ReplayKeyInfo);
			context = LiquidKeyDomain.DeriveHkdf(replayChildMaterial, salt, ContextKeyInfo);

			LiquidWalletExternalIndexAllocation allocation = LiquidWalletExternalIndexAllocator.Allocate(
				walletDataDirectory,
				identity.CanonicalWalletId,
				replayKey,
				context);
			Network descriptorNetwork = ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidMainnet)
				? Network.Main
				: Network.TestNet;
			LiquidWalletReceiveDerivation receive = LiquidWalletReceiveDerivation.Create(
				authenticatedMaster,
				descriptorNetwork,
				account: 0,
				allocation.Index);
			byte[] blindingPublicKey = LiquidSlip77PublicKey.Derive(slip77Master, receive.ScriptPubKey);
			return new LiquidAuthenticatedWalletStateOwner(
				allocation,
				receive,
				blindingPublicKey,
				identity.CanonicalWalletId,
				manifest);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(replayKey);
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(saltInput);
			CryptographicOperations.ZeroMemory(replayChildMaterial);
			CryptographicOperations.ZeroMemory(slip77Master);
			CryptographicOperations.ZeroMemory(masterPrivateKey);
		}
	}
}
