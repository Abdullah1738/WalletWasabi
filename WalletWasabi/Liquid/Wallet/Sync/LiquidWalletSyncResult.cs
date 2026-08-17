using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The immutable outcome of one committed wallet sync session: the advanced
/// wallet state, the bound self-reported node observation, and the applied
/// batch digest inputs (counts only). It carries no raw transaction bytes, no
/// scripts, no keys, and no chain, currentness, reservation, or broadcast
/// authority.
/// </summary>
internal sealed class LiquidWalletSyncResult
{
	internal LiquidWalletSyncResult(
		LiquidWalletState state,
		ElementsExpectationBoundNodeObservation nodeObservation,
		ulong baseRevision,
		ulong resultRevision,
		int appliedTransactionCount,
		int confirmationCount)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(nodeObservation);
		State = state;
		NodeObservation = nodeObservation;
		BaseRevision = baseRevision;
		ResultRevision = resultRevision;
		AppliedTransactionCount = appliedTransactionCount;
		ConfirmationCount = confirmationCount;
	}

	public LiquidWalletState State { get; }
	public ElementsExpectationBoundNodeObservation NodeObservation { get; }
	public ulong BaseRevision { get; }
	public ulong ResultRevision { get; }
	public int AppliedTransactionCount { get; }
	public int ConfirmationCount { get; }

	public override string ToString() => nameof(LiquidWalletSyncResult);
}
