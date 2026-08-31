using System.Collections.ObjectModel;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// One immutable retained Liquid transaction-effect history row. The
/// <see cref="TransactionId"/> is the full canonical lowercase 64-hex
/// transaction id of the landed effect, shown in full (Wasabi shows
/// everything the retained history has). <see cref="TransactionId"/> is
/// display-only: it is forbidden as a dictionary/cache key, equality
/// identity, selection identity, lookup input, transaction action target, or
/// deduplication key — two effects remain two rows. The canonical block hash
/// of a landed confirmation is deliberately dropped rather than retained
/// (the retained history carries the confirmation height only). Asset changes
/// are copied in the landed canonical ascending asset-id order; a valid
/// effect with zero asset changes remains one row with
/// <see cref="HasBalanceChange"/> <see langword="false"/>.
/// </summary>
public sealed class LiquidWalletUiHistoryRow
{
	private LiquidWalletUiHistoryRow(
		string transactionId,
		bool isConfirmed,
		uint? confirmationHeight,
		IReadOnlyList<LiquidWalletUiHistoryAssetChange> assetChanges)
	{
		TransactionId = transactionId;
		IsConfirmed = isConfirmed;
		ConfirmationHeight = confirmationHeight;
		AssetChanges = assetChanges;
	}

	public string TransactionId { get; }
	public bool IsConfirmed { get; }
	public uint? ConfirmationHeight { get; }
	public IReadOnlyList<LiquidWalletUiHistoryAssetChange> AssetChanges { get; }
	public bool HasBalanceChange => AssetChanges.Count != 0;

	internal static LiquidWalletUiHistoryRow FromEffect(
		LiquidWalletTransactionEffect effect)
	{
		ArgumentNullException.ThrowIfNull(effect);

		string canonicalId = effect.TransactionId.CanonicalRpcHex;

		LiquidConfirmation? confirmation = effect.Confirmation;
		IReadOnlyList<LiquidWalletAssetNetChange> changes = effect.GetAssetNetChanges();
		var projected = new LiquidWalletUiHistoryAssetChange[changes.Count];
		for (int index = 0; index < changes.Count; index++)
		{
			projected[index] = LiquidWalletUiHistoryAssetChange.FromChange(changes[index]);
		}

		return new LiquidWalletUiHistoryRow(
			canonicalId,
			confirmation is not null,
			confirmation?.Height,
			new ReadOnlyCollection<LiquidWalletUiHistoryAssetChange>(projected));
	}
}
