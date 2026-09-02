using System.IO;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Wallet.Sync;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>The durable allocation result for one external receive index.</summary>
public sealed class LiquidWalletExternalIndexAllocation
{
	private readonly LiquidWalletState _state;

	internal LiquidWalletExternalIndexAllocation(
		ulong index,
		ulong stateRevision,
		ulong persistedGeneration,
		ulong persistedExternalIndexHighWater,
		ulong persistedInternalIndexHighWater,
		LiquidWalletState state)
	{
		Index = index;
		StateRevision = stateRevision;
		PersistedGeneration = persistedGeneration;
		PersistedExternalIndexHighWater = persistedExternalIndexHighWater;
		PersistedInternalIndexHighWater = persistedInternalIndexHighWater;
		_state = state;
	}

	public ulong Index { get; }
	public ulong StateRevision { get; }
	public ulong PersistedGeneration { get; }
	public ulong PersistedExternalIndexHighWater { get; }
	public ulong PersistedInternalIndexHighWater { get; }
	internal LiquidWalletState State => _state;
}

/// <summary>
/// Allocates an external index by advancing the sealed wallet generation while preserving and
/// retaining the exact loaded state. Address issuance is durable even when no output is observed.
/// </summary>
public static class LiquidWalletExternalIndexAllocator
{

	public static LiquidWalletExternalIndexAllocation Allocate(
		string walletDataDirectory,
		string walletName,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		lock (LiquidWalletLoadSave.GenerationFence)
		{
			LiquidWalletLoadSaveResult loaded = LiquidWalletLoadSave.Load(
				walletDataDirectory,
				walletName,
				key,
				externalWalletNetworkContext);
			LiquidWalletState loadedState = loaded.State
				?? throw new InvalidOperationException("The Liquid wallet state load returned no state.");
			if (loaded.ExternalIndexHighWater > 0x7fffffffUL)
			{
				throw new InvalidOperationException("The Liquid external receive-index space is exhausted.");
			}

			ulong allocatedIndex = loaded.ExternalIndexHighWater;
			ulong nextExternalIndexHighWater = checked(allocatedIndex + 1);
			ulong nextGeneration = checked(loaded.Generation + 1);
			LiquidWalletLoadSaveResult saved = LiquidWalletLoadSave.SaveWithExternalIndexHighWater(
				walletDataDirectory,
				walletName,
				loadedState,
				nextGeneration,
				nextExternalIndexHighWater,
				loaded.Generation,
				key,
				externalWalletNetworkContext);
			return new LiquidWalletExternalIndexAllocation(
				allocatedIndex,
				saved.Revision,
				saved.Generation,
				saved.ExternalIndexHighWater,
				saved.InternalIndexHighWater,
				loadedState);
		}
	}

	/// <summary>
	/// The production-supported first-open seam for a genuinely absent wallet: when no safe
	/// version of the wallet state file exists at all (no <c>.lwwal</c>, no <c>.new</c>, no
	/// <c>.old</c>), seals <see cref="LiquidWalletState.Empty"/> under the caller-supplied
	/// key and network context at generation 0 through the normal atomic write path, then
	/// performs the ordinary allocation above. Any present on-disk state — corrupt,
	/// undecryptable under this key/context, or a <c>.new</c>/<c>.old</c> conflict — is
	/// never converted to empty: this seam runs only on exact absence, inside the same
	/// <see cref="LiquidWalletLoadSave.GenerationFence"/> as the allocation, so a
	/// concurrent initializer cannot interleave and every other failure keeps the landed
	/// fail-closed exception surface.
	/// </summary>
	internal static LiquidWalletExternalIndexAllocation AllocateWithFirstOpenInitialization(
		string walletDataDirectory,
		string walletName,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		LiquidAssetId peggedAssetId)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		lock (LiquidWalletLoadSave.GenerationFence)
		{
			string filePath = LiquidWalletPersistencePaths.GetWalletStateFilePath(walletDataDirectory, walletName);
			if (!File.Exists(filePath) && !File.Exists(filePath + ".new") && !File.Exists(filePath + ".old"))
			{
				LiquidWalletLoadSave.Save(
					walletDataDirectory,
					walletName,
					LiquidWalletState.Empty(peggedAssetId),
					generation: 0,
					key,
					externalWalletNetworkContext);
			}

			return Allocate(walletDataDirectory, walletName, key, externalWalletNetworkContext);
		}
	}
}
