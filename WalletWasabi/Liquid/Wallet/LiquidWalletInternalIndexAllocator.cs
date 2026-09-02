using WalletWasabi.Liquid.Wallet.Sync;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>The durable allocation result for one internal (change) index.</summary>
public sealed class LiquidWalletInternalIndexAllocation
{
	private readonly LiquidWalletState _state;

	internal LiquidWalletInternalIndexAllocation(
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
/// Allocates an internal (change) index by advancing the sealed wallet generation while
/// preserving and retaining the exact loaded state. Index reservation is durable even when no
/// output is observed: the current high-water is allocated and the persisted high-water is
/// advanced by one, so no index is reused across reopen. The allocator holds no process-local
/// state and mutates no caller state. The external receive-index high-water is carried forward
/// unchanged. This is index reservation only: it performs no key derivation, no address
/// generation, and no send logic.
/// </summary>
public static class LiquidWalletInternalIndexAllocator
{
	public static LiquidWalletInternalIndexAllocation Allocate(
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
			if (loaded.InternalIndexHighWater > 0x7fffffffUL)
			{
				throw new InvalidOperationException("The Liquid internal change-index space is exhausted.");
			}

			ulong allocatedIndex = loaded.InternalIndexHighWater;
			ulong nextInternalIndexHighWater = checked(allocatedIndex + 1);
			ulong nextGeneration = checked(loaded.Generation + 1);
			LiquidWalletLoadSaveResult saved = LiquidWalletLoadSave.SaveWithIndexHighWaters(
				walletDataDirectory,
				walletName,
				loadedState,
				nextGeneration,
				loaded.ExternalIndexHighWater,
				nextInternalIndexHighWater,
				loaded.Generation,
				key,
				externalWalletNetworkContext);
			return new LiquidWalletInternalIndexAllocation(
				allocatedIndex,
				saved.Revision,
				saved.Generation,
				saved.ExternalIndexHighWater,
				saved.InternalIndexHighWater,
				loadedState);
		}
	}
}
