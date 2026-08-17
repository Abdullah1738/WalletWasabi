using System.Collections.ObjectModel;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The immutable output of <see cref="LiquidWalletScanIntentDeriver.Derive"/>:
/// the deterministic, ordered, deduplicated, bounded set of
/// <see cref="LiquidWalletSyncBatchPlanner.FetchIntent"/> values the caller
/// hands to <see cref="LiquidWalletSyncBatchPlanner.CreateRequests"/>.
/// <see cref="Intents"/> is a deep-cloned read-only view in canonical
/// ascending transaction-id order (ordinal on
/// <see cref="Transactions.LiquidTransactionId.CanonicalRpcHex"/>).
/// <see cref="IsEmpty"/> is <see langword="true"/> iff the derivation produced
/// zero intents — the caller skips the fetch step entirely (the
/// empty-descriptor / no-candidate edge). Construction is by the deriver only;
/// the type carries no chain, scanning, currentness, or broadcast authority.
/// </summary>
internal sealed class LiquidWalletScanIntentDerivation
{
	private readonly LiquidWalletSyncBatchPlanner.FetchIntent[] _intents;

	private LiquidWalletScanIntentDerivation(LiquidWalletSyncBatchPlanner.FetchIntent[] intents)
	{
		_intents = intents;
	}

	public IReadOnlyList<LiquidWalletSyncBatchPlanner.FetchIntent> Intents =>
		new ReadOnlyCollection<LiquidWalletSyncBatchPlanner.FetchIntent>([.. _intents]);

	public bool IsEmpty => _intents.Length == 0;

	internal static LiquidWalletScanIntentDerivation Create(
		LiquidWalletSyncBatchPlanner.FetchIntent[] intents)
	{
		ArgumentNullException.ThrowIfNull(intents);
		return new LiquidWalletScanIntentDerivation([.. intents]);
	}

	public override string ToString() => nameof(LiquidWalletScanIntentDerivation);
}
