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
		LiquidWalletState state)
	{
		Index = index;
		StateRevision = stateRevision;
		PersistedGeneration = persistedGeneration;
		PersistedExternalIndexHighWater = persistedExternalIndexHighWater;
		_state = state;
	}

	public ulong Index { get; }
	public ulong StateRevision { get; }
	public ulong PersistedGeneration { get; }
	public ulong PersistedExternalIndexHighWater { get; }
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
				loadedState);
		}
	}
}
