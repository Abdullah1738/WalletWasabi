using System.Collections.ObjectModel;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// One immutable retained Liquid transaction-effect history row. The
/// <see cref="TransactionReference"/> is display-only and collision-tolerant:
/// exactly the first eight lowercase hex characters, one Unicode ellipsis
/// (U+2026), and the last eight lowercase hex characters of the landed
/// canonical transaction id. The full transaction id is never stored or
/// returned, and the canonical block hash of a landed confirmation is
/// deliberately dropped rather than abbreviated or retained.
/// <see cref="TransactionReference"/> is forbidden as a dictionary/cache
/// key, equality identity, selection identity, lookup input, transaction
/// action target, or deduplication key: two effects with the same redacted
/// reference remain two rows. No full transaction id or block hash is
/// retained in any private field, closure, tag, command parameter,
/// automation property, tooltip, or accessibility string. Asset changes are
/// copied in the landed canonical ascending asset-id order; a valid effect
/// with zero asset changes remains one row with
/// <see cref="HasBalanceChange"/> <see langword="false"/>.
/// </summary>
public sealed class LiquidWalletUiHistoryRow
{
	private LiquidWalletUiHistoryRow(
		string transactionReference,
		bool isConfirmed,
		uint? confirmationHeight,
		IReadOnlyList<LiquidWalletUiHistoryAssetChange> assetChanges)
	{
		TransactionReference = transactionReference;
		IsConfirmed = isConfirmed;
		ConfirmationHeight = confirmationHeight;
		AssetChanges = assetChanges;
	}

	public string TransactionReference { get; }
	public bool IsConfirmed { get; }
	public uint? ConfirmationHeight { get; }
	public IReadOnlyList<LiquidWalletUiHistoryAssetChange> AssetChanges { get; }
	public bool HasBalanceChange => AssetChanges.Count != 0;

	internal static LiquidWalletUiHistoryRow FromEffect(
		LiquidWalletTransactionEffect effect)
	{
		ArgumentNullException.ThrowIfNull(effect);

		string canonicalId = effect.TransactionId.CanonicalRpcHex;
		string reference = string.Concat(
			canonicalId.Substring(0, 8),
			"…",
			canonicalId.Substring(canonicalId.Length - 8, 8));

		LiquidConfirmation? confirmation = effect.Confirmation;
		IReadOnlyList<LiquidWalletAssetNetChange> changes = effect.GetAssetNetChanges();
		var projected = new LiquidWalletUiHistoryAssetChange[changes.Count];
		for (int index = 0; index < changes.Count; index++)
		{
			projected[index] = LiquidWalletUiHistoryAssetChange.FromChange(changes[index]);
		}

		return new LiquidWalletUiHistoryRow(
			reference,
			confirmation is not null,
			confirmation?.Height,
			new ReadOnlyCollection<LiquidWalletUiHistoryAssetChange>(projected));
	}
}
