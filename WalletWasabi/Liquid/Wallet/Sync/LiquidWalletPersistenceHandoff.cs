namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The fail-closed, in-memory persistence handoff entry point connecting the
/// landed sync engine to a caller-owned durable storage writer and reader.
/// <see cref="Export"/> hands the caller a sealed, in-memory
/// <see cref="LiquidWalletReplayProtectedPayload"/> ready for the caller's
/// storage writer to persist; <see cref="Import"/> takes one caller-supplied
/// stored envelope (already read into memory by the caller) and hands back the
/// restored <see cref="LiquidWalletState"/> plus the envelope's caller-defined
/// <see cref="LiquidWalletReplayOpenResult.Generation"/> metadata. Each method
/// body is exactly the required call order and nothing more: no retry, no
/// fallback, no second export or open, no state inspection beyond what
/// <see cref="LiquidWalletState.ExportReplaySnapshot"/>,
/// <see cref="LiquidWalletReplayProtectedPayload.Seal"/>,
/// <see cref="LiquidWalletReplayProtectedPayload.Open"/>, and
/// <see cref="LiquidWalletState.RestoreReplaySnapshot"/> already perform, and
/// no catch-and-rethrow remapping — every rejection surfaces with the existing
/// exception surface of the failing layer. This entry point performs no file
/// I/O, no serialization-to-disk, no encryption-key management beyond the
/// caller-supplied key and context spans, and no freshness or anti-rollback
/// check beyond the caller-supplied <c>expectedBaseRevision</c> fence; it
/// grants no persistence, chain, confirmation-source, currentness,
/// reservation, broadcast, artifact-source, or runtime authority.
/// </summary>
internal static class LiquidWalletPersistenceHandoff
{
	public static LiquidWalletPersistenceHandoffResult Export(
		LiquidWalletState state,
		ulong generation,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		ArgumentNullException.ThrowIfNull(state);

		LiquidWalletReplaySnapshot snapshot = state.ExportReplaySnapshot();
		LiquidWalletReplayProtectedPayload envelope = LiquidWalletReplayProtectedPayload.Seal(
			snapshot,
			generation,
			key,
			externalWalletNetworkContext);
		return LiquidWalletPersistenceHandoffResult.Create(envelope, snapshot.Revision, generation);
	}

	public static LiquidWalletPersistenceHandoffResult Import(
		ReadOnlySpan<byte> envelope,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		ulong? expectedBaseRevision = null)
	{
		LiquidWalletReplayOpenResult openResult = LiquidWalletReplayProtectedPayload.Open(
			envelope,
			key,
			externalWalletNetworkContext);
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(openResult.Snapshot);
		if (expectedBaseRevision is ulong expected && restored.Revision != expected)
		{
			throw new InvalidOperationException(
				"The Liquid wallet persistence handoff snapshot revision does not match the expected base revision.");
		}

		return LiquidWalletPersistenceHandoffResult.Create(
			envelope: null,
			restored.Revision,
			openResult.Generation,
			restored);
	}
}
