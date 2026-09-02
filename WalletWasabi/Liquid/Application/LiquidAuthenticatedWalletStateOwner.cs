using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Liquid.Application;

internal sealed class LiquidAuthenticatedWalletStateOwner
{
	private const uint ReplayContextBranchIndex = 1108790945;
	private const string ReplayKeyInfo = "WalletWasabi/Liquid/v1/replay";
	private const string ContextKeyInfo = "WalletWasabi/Liquid/v1/context";
	private const string Slip77Info = "WalletWasabi/Liquid/v1/slip77";

	private readonly LiquidWalletExternalIndexAllocation _allocation;
	private readonly string _walletName;
	private readonly ElementsPublicNetworkManifest _manifest;

	private LiquidAuthenticatedWalletStateOwner(
		LiquidWalletExternalIndexAllocation allocation,
		LiquidWalletReceiveDerivation receiveDerivation,
		byte[] blindingPublicKey,
		string walletName,
		ElementsPublicNetworkManifest manifest,
		ElementsNodeExpectation nodeExpectation)
		: this(
			allocation,
			receiveDerivation?.Descriptor ?? throw new ArgumentNullException(nameof(receiveDerivation)),
			receiveDerivation.LastIndex,
			new LiquidWalletUiReceiveMaterial(
				receiveDerivation.ScriptPubKey,
				blindingPublicKey,
				allocation.State.GetReceiveLabels(checked((uint)receiveDerivation.LastIndex))?.GetLabels()),
			walletName,
			manifest,
			nodeExpectation)
	{
	}

	private LiquidAuthenticatedWalletStateOwner(
		LiquidWalletExternalIndexAllocation allocation,
		string descriptor,
		ulong lastIndex,
		LiquidWalletUiReceiveMaterial receiveMaterial,
		string walletName,
		ElementsPublicNetworkManifest manifest,
		ElementsNodeExpectation nodeExpectation)
	{
		_allocation = allocation ?? throw new ArgumentNullException(nameof(allocation));
		_walletName = walletName ?? throw new ArgumentNullException(nameof(walletName));
		_manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
		StateRevision = allocation.StateRevision;
		PersistenceGeneration = allocation.PersistedGeneration;
		Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		LastIndex = lastIndex;
		ReceiveMaterial = receiveMaterial ?? throw new ArgumentNullException(nameof(receiveMaterial));
		Balances = LiquidWalletUiFacade.CaptureAllocationBalances(walletName, manifest, allocation);
		SelectableOutputs = LiquidWalletUiFacade.CaptureSelectableOutputs(walletName, manifest, allocation);
		History = LiquidWalletUiFacade.CaptureAllocationHistory(walletName, manifest, allocation);
		NodeExpectation = nodeExpectation ?? throw new ArgumentNullException(nameof(nodeExpectation));
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
	internal ElementsNodeExpectation NodeExpectation { get; }

	/// <summary>The immutable wallet state this owner projects from (internal-only).</summary>
	internal LiquidWalletState State => _allocation.State;

	/// <summary>
	/// The confirmed-history high-water of the projected wallet state (internal-only): the lowest
	/// confirmed block height the wallet already knows, or <see langword="null"/> when it holds no
	/// confirmation. This is the refresh deep-rescan trigger anchor.
	/// </summary>
	internal uint? MinConfirmedHeight => _allocation.State.MinConfirmedHeight;

	/// <summary>The persisted external receive-index high-water carried by this owner.</summary>
	internal ulong ExternalIndexHighWater => _allocation.PersistedExternalIndexHighWater;

	/// <summary>The persisted internal change-index high-water carried by this owner.</summary>
	internal ulong InternalIndexHighWater => _allocation.PersistedInternalIndexHighWater;

	/// <summary>
	/// Purely projects a complete replacement owner from a committed
	/// <paramref name="committedState"/> and its persisted <paramref name="nextGeneration"/>.
	/// The captured owner is never mutated; receive derivation material, descriptor, last-index,
	/// and the external-index high-water are preserved (refresh allocates no receive index).
	/// All projection/validation happens here, before any persistence. No key or RPC authority
	/// is consulted or exposed.
	/// </summary>
	internal LiquidAuthenticatedWalletStateOwner CreateReplacement(
		LiquidWalletState committedState,
		ulong nextGeneration)
	{
		ArgumentNullException.ThrowIfNull(committedState);
		if (committedState.Revision < StateRevision)
		{
			throw new InvalidOperationException("A replacement owner cannot regress the committed state revision.");
		}
		if (nextGeneration <= PersistenceGeneration)
		{
			throw new InvalidOperationException("A replacement owner requires a persistence generation that advances.");
		}
		if (!StringComparer.Ordinal.Equals(
			committedState.PeggedAssetId.CanonicalRpcHex,
			_manifest.PeggedAssetId))
		{
			throw new InvalidOperationException("A replacement owner requires a committed state bound to the owner's pegged asset.");
		}

		// Re-project through a replacement allocation that preserves the allocated receive
		// index and both persisted index high-waters while binding the committed
		// state, its revision, and the next persistence generation.
		var replacementAllocation = new LiquidWalletExternalIndexAllocation(
			_allocation.Index,
			committedState.Revision,
			nextGeneration,
			_allocation.PersistedExternalIndexHighWater,
			_allocation.PersistedInternalIndexHighWater,
			committedState);
		// Rebind the durable label set for the current next-receive derivation index
		// from the committed state. The label map is keyed by the branch-0 index, which
		// is the same value LastIndex exposes for the next receive address; an unlabeled
		// index projects an empty label list. Without this the published NextReceiveLabels
		// would always be empty regardless of any persisted label write.
		LiquidWalletLabelSet? nextLabels = committedState.GetReceiveLabels(checked((uint)LastIndex));
		LiquidWalletUiReceiveMaterial reboundReceiveMaterial = new(
			ReceiveMaterial.NextReceiveScriptPubKey,
			ReceiveMaterial.NextReceiveBlindingPublicKey,
			nextLabels?.GetLabels());

		return new LiquidAuthenticatedWalletStateOwner(
			replacementAllocation,
			Descriptor,
			LastIndex,
			reboundReceiveMaterial,
			_walletName,
			_manifest,
			NodeExpectation);
	}

	internal Task<ElementsExpectationBoundRawTransactionBatch> GetPreRefreshRawTransactionsAsync(
		ElementsPublicNetworkManifest manifest,
		ElementsRpcClient rpcClient,
		IReadOnlyList<ElementsRawTransactionRequest> requests,
		CancellationToken cancellationToken,
		Func<ElementsNodeExpectation, string, IReadOnlyList<ElementsRawTransactionRequest>, CancellationToken, Task<ElementsExpectationBoundRawTransactionBatch>>? rawFetch = null)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(rpcClient);
		ArgumentNullException.ThrowIfNull(requests);
		if (rawFetch is null)
		{
			return rpcClient.GetExpectationBoundRawTransactionsAsync(
				NodeExpectation,
				manifest.RequiredFeeAssetId,
				requests,
				manifest,
				cancellationToken);
		}

		return rawFetch(NodeExpectation, manifest.RequiredFeeAssetId, requests, cancellationToken);
	}

	internal static LiquidAuthenticatedWalletStateOwner Open(
		LiquidWalletIdentity identity,
		ElementsPublicNetworkManifest manifest,
		ElementsNodeExpectation nodeExpectation,
		string walletDataDirectory,
		ExtKey authenticatedMaster,
		LiquidWalletSignerKeyAdapter signerKeyAdapter,
		ElementsRpcClient rpcClient)
	{
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(nodeExpectation);
		ArgumentException.ThrowIfNullOrEmpty(walletDataDirectory);
		ArgumentNullException.ThrowIfNull(authenticatedMaster);
		ArgumentNullException.ThrowIfNull(signerKeyAdapter);
		ArgumentNullException.ThrowIfNull(rpcClient);
		ElementsReviewedNodeExpectationSource.ValidateOwnerExpectation(identity, manifest, nodeExpectation);

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

			LiquidWalletExternalIndexAllocation allocation = LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
				walletDataDirectory,
				identity.CanonicalWalletId,
				replayKey,
				context,
				LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId));
			NBitcoin.Network descriptorNetwork = ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidMainnet)
				? NBitcoin.Network.Main
				: NBitcoin.Network.TestNet;
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
				manifest,
				nodeExpectation);
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
