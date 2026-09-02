using WalletWasabi.Liquid.Wallet.Sync;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>The durable result of one receive-label write (internal: it names the internal label set).</summary>
internal sealed class LiquidWalletReceiveLabelAllocation
{
	private readonly LiquidWalletState _state;

	internal LiquidWalletReceiveLabelAllocation(
		uint index,
		LiquidWalletLabelSet labels,
		ulong stateRevision,
		ulong persistedGeneration,
		LiquidWalletState state)
	{
		Index = index;
		Labels = labels;
		StateRevision = stateRevision;
		PersistedGeneration = persistedGeneration;
		_state = state;
	}

	public uint Index { get; }
	public LiquidWalletLabelSet Labels { get; }
	public ulong StateRevision { get; }
	public ulong PersistedGeneration { get; }
	internal LiquidWalletState State => _state;
}

/// <summary>
/// Persists a durable label set bound to one receive (branch-0) derivation index by advancing the
/// sealed wallet generation, exactly as the index allocators persist a high-water. The write is
/// generation-fenced and fail-closed: it loads the current state, applies the label set (or removes
/// the entry when the set is empty), and saves under the exact captured generation. A concurrent
/// generation change rejects the write (no stale label persistence). The allocator holds no
/// process-local state and performs no key derivation, address generation, send, native, RPC, or UI
/// behavior; the label set is validated by <see cref="LiquidWalletLabelSet.Create"/> before any
/// write. It is internal because the durable result names the internal
/// <see cref="LiquidWalletLabelSet"/>; the public UI-facing surface is the session command
/// service.
/// </summary>
internal static class LiquidWalletReceiveLabelAllocator
{
	public static LiquidWalletReceiveLabelAllocation SetLabels(
		string walletDataDirectory,
		string walletName,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		uint index,
		IReadOnlyList<string> labels)
	{
		// Validate the label set before touching the persistence fence or the file.
		LiquidWalletLabelSet labelSet = LiquidWalletLabelSet.Create(labels);
		if (index > 0x7fffffffU)
		{
			throw new InvalidOperationException("The Liquid external receive-index space is exhausted.");
		}

		lock (LiquidWalletLoadSave.GenerationFence)
		{
			LiquidWalletLoadSaveResult loaded = LiquidWalletLoadSave.Load(
				walletDataDirectory,
				walletName,
				key,
				externalWalletNetworkContext);
			LiquidWalletState loadedState = loaded.State
				?? throw new InvalidOperationException("The Liquid wallet state load returned no state.");

			LiquidWalletState labeledState = loadedState.SetReceiveLabels(index, labelSet);
			ulong nextGeneration = checked(loaded.Generation + 1);
			LiquidWalletLoadSaveResult saved = LiquidWalletLoadSave.SaveWithExpectedGeneration(
				walletDataDirectory,
				walletName,
				labeledState,
				nextGeneration,
				loaded.Generation,
				key,
				externalWalletNetworkContext);
			return new LiquidWalletReceiveLabelAllocation(
				index,
				labelSet,
				saved.Revision,
				saved.Generation,
				labeledState);
		}
	}
}
