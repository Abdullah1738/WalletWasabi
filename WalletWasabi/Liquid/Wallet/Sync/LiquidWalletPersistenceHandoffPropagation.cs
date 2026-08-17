namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The immutable output of <see cref="LiquidWalletPersistenceHandoffPlan.Propagate"/>:
/// the persistence decision derived from one SYNC-003
/// <see cref="LiquidWalletReorgPlan"/>. <see cref="RequiresRescan"/> is
/// <see langword="true"/> iff the reorg plan requires a rescan; when
/// <see langword="true"/> the caller must rebuild from chain data before any
/// export and <see cref="Revision"/> is meaningless. Otherwise
/// <see cref="Revision"/> is the revision the caller should bind to the
/// export. Construction is by the planner only; the type carries no chain,
/// confirmation-source, currentness, persistence, or broadcast authority.
/// </summary>
internal sealed class LiquidWalletPersistenceHandoffPropagation
{
	private LiquidWalletPersistenceHandoffPropagation(bool requiresRescan, ulong revision)
	{
		RequiresRescan = requiresRescan;
		Revision = revision;
	}

	public bool RequiresRescan { get; }
	public ulong Revision { get; }

	internal static LiquidWalletPersistenceHandoffPropagation Proceed(ulong revision) =>
		new(requiresRescan: false, revision);

	internal static LiquidWalletPersistenceHandoffPropagation RescanRequired() =>
		new(requiresRescan: true, revision: 0);

	public override string ToString() => nameof(LiquidWalletPersistenceHandoffPropagation);
}
