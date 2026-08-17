using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The fail-closed recovery entry point: restores one caller-supplied
/// <see cref="LiquidWalletReplaySnapshot"/> in memory, opens one SYNC-001
/// <see cref="LiquidWalletSyncSession"/> against one caller-supplied
/// expectation-bound node observation, and advances the restored state exactly
/// once through <see cref="LiquidWalletSyncSession.Commit"/>. The method body
/// is exactly the required call order and nothing more: it adds no retry, no
/// fallback, no second RPC, no state inspection beyond what
/// <see cref="LiquidWalletSyncSession.Open"/> and
/// <see cref="LiquidWalletSyncSession.Commit"/> already perform, and no
/// catch-and-rethrow remapping. Every rejection surfaces with the existing
/// exception surface of the failing layer, the restored intermediate state is
/// discarded, and the caller's snapshot is never mutated. The node remains
/// self-reported only: this entry point grants no persistence, chain,
/// confirmation-source, currentness, reservation, broadcast,
/// artifact-source, or runtime authority.
/// </summary>
internal static class LiquidWalletRecoverySync
{
	public static LiquidWalletSyncResult RestoreAndSync(
		LiquidWalletReplaySnapshot snapshot,
		ElementsExpectationBoundNodeObservation observation,
		string peggedAsset,
		LiquidWalletObservationBatch observations,
		IReadOnlyList<LiquidWalletSyncConfirmation> confirmations)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(observation);
		ArgumentNullException.ThrowIfNull(peggedAsset);
		ArgumentNullException.ThrowIfNull(observations);
		ArgumentNullException.ThrowIfNull(confirmations);

		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);
		LiquidWalletSyncSession session = LiquidWalletSyncSession.Open(
			restored,
			observation,
			peggedAsset);
		return session.Commit(observations, confirmations);
	}
}
