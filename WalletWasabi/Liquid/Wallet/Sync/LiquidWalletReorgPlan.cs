using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The immutable output of the fail-closed multi-batch reorg derivation.
/// <see cref="Unconfirmations"/> carries every recorded confirmation the
/// observed tip no longer binds, in canonical ascending-txid order exactly as
/// <see cref="LiquidWalletRecoverySyncPlan.Reconcile"/> emits them, every row
/// <see cref="LiquidWalletSyncConfirmationKind.Unconfirm"/>.
/// <see cref="RollbackTransactionIds"/> carries the retained transaction deltas
/// the reorg invalidated, in exact reverse application order — the order a
/// caller must feed them to <see cref="LiquidWalletState.RollbackLast"/>.
/// <see cref="RequiresRescan"/> is <see langword="true"/> iff the derivation
/// determined the reorg is deeper than the retained history can satisfy; when
/// <see langword="true"/>, both lists are empty and the caller must rebuild
/// from chain data rather than execute any unwind. Construction is by the
/// planner only; the type carries no chain, confirmation-source, currentness,
/// or broadcast authority.
/// </summary>
internal sealed class LiquidWalletReorgPlan
{
	private readonly LiquidWalletSyncConfirmation[] _unconfirmations;
	private readonly LiquidTransactionId[] _rollbackTransactionIds;

	private LiquidWalletReorgPlan(
		LiquidWalletSyncConfirmation[] unconfirmations,
		LiquidTransactionId[] rollbackTransactionIds,
		bool requiresRescan)
	{
		_unconfirmations = unconfirmations;
		_rollbackTransactionIds = rollbackTransactionIds;
		RequiresRescan = requiresRescan;
	}

	public IReadOnlyList<LiquidWalletSyncConfirmation> Unconfirmations =>
		new ReadOnlyCollection<LiquidWalletSyncConfirmation>([.. _unconfirmations]);

	public IReadOnlyList<LiquidTransactionId> RollbackTransactionIds =>
		new ReadOnlyCollection<LiquidTransactionId>([.. _rollbackTransactionIds]);

	public bool RequiresRescan { get; }

	internal static LiquidWalletReorgPlan Create(
		LiquidWalletSyncConfirmation[] unconfirmations,
		LiquidTransactionId[] rollbackTransactionIds)
	{
		ArgumentNullException.ThrowIfNull(unconfirmations);
		ArgumentNullException.ThrowIfNull(rollbackTransactionIds);
		return new LiquidWalletReorgPlan(
			[.. unconfirmations],
			[.. rollbackTransactionIds],
			requiresRescan: false);
	}

	internal static LiquidWalletReorgPlan RescanRequired() =>
		new([], [], requiresRescan: true);

	public override string ToString() => nameof(LiquidWalletReorgPlan);
}
