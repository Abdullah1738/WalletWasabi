namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The pure, fail-closed persistence propagation planner. <see cref="Propagate"/>
/// converts one SYNC-003 <see cref="LiquidWalletReorgPlan"/> into the
/// persistence decision a caller must apply before calling
/// <see cref="LiquidWalletPersistenceHandoff.Export"/>: when the reorg plan
/// requires a rescan, the caller stops and rebuilds from chain data before any
/// export; otherwise the caller proceeds with the export bound to the supplied
/// current revision. The method body is exactly that propagation and nothing
/// more: it performs no state transition, no RPC, no mutation, and no
/// catch-and-rethrow remapping; it never fabricates a rescan signal and never
/// swallows one. It carries no chain, confirmation-source, currentness,
/// persistence, or broadcast authority.
/// </summary>
internal static class LiquidWalletPersistenceHandoffPlan
{
	public static LiquidWalletPersistenceHandoffPropagation Propagate(
		LiquidWalletReorgPlan reorgPlan,
		ulong currentRevision)
	{
		ArgumentNullException.ThrowIfNull(reorgPlan);

		return reorgPlan.RequiresRescan
			? LiquidWalletPersistenceHandoffPropagation.RescanRequired()
			: LiquidWalletPersistenceHandoffPropagation.Proceed(currentRevision);
	}
}
