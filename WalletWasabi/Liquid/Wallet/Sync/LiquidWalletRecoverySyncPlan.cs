using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The pure confirmation-reconciliation planner for a recovery sync. For each
/// confirmation retained by a caller-supplied
/// <see cref="LiquidWalletReplaySnapshot"/> (canonical ascending-txid order, as
/// the snapshot already orders them), the SYNC-001 tip-binding rule is applied
/// to the recorded <see cref="LiquidConfirmation"/>: a recorded confirmation
/// whose height is above the observed tip, or that sits at the observed tip
/// height under a different best block hash, is no longer bound to the observed
/// tip and yields one
/// <see cref="LiquidWalletSyncConfirmationKind.Unconfirm"/> row carrying the
/// recorded confirmation as the expected prior confirmation, exactly as
/// <see cref="LiquidWalletState.Unconfirm"/> requires. A recorded confirmation
/// that is bound to the observed tip yields no row: the restored state already
/// holds it and re-confirming would trip the confirmation-replacement guard.
/// <see cref="Reconcile"/> performs no state transition, no RPC, and no
/// mutation; it never emits
/// <see cref="LiquidWalletSyncConfirmationKind.Confirm"/> rows, because new
/// confirmations arrive only as caller-supplied observations through the
/// SYNC-001 path. It carries no chain, confirmation-source, currentness, or
/// broadcast authority.
/// </summary>
internal static class LiquidWalletRecoverySyncPlan
{
	public static LiquidWalletSyncConfirmation[] Reconcile(
		LiquidWalletReplaySnapshot snapshot,
		ElementsExpectationBoundNodeObservation observation)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(observation);

		uint observedBlocks = checked((uint)observation.Generation.Blocks);
		string observedBestBlockHash = observation.Generation.BestBlockHash;

		IReadOnlyList<LiquidWalletReplayConfirmation> recorded = snapshot.GetConfirmations();
		var rows = new List<LiquidWalletSyncConfirmation>(recorded.Count);
		for (int index = 0; index < recorded.Count; index++)
		{
			LiquidWalletReplayConfirmation entry = recorded[index];
			LiquidConfirmation confirmation = entry.Confirmation;
			bool boundToObservedTip =
				confirmation.Height <= observedBlocks &&
				(confirmation.Height != observedBlocks ||
					StringComparer.Ordinal.Equals(
						confirmation.CanonicalBlockHash,
						observedBestBlockHash));
			if (boundToObservedTip)
			{
				continue;
			}

			rows.Add(LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				entry.TransactionId,
				confirmation));
		}

		return rows.ToArray();
	}
}
