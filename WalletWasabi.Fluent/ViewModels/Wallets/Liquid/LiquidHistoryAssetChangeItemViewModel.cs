using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The immutable Fluent projection of one public Liquid history asset
/// change: the exact signed atomic units, the formatted display amount, the
/// full canonical asset id, explicit <c>Credit</c>/<c>Debit</c> text, and
/// <c>L-BTC</c> for the pegged asset. For the pegged asset the display
/// amount is the signed L-BTC decimal form; for any other asset it is the
/// signed atomic-unit count together with the full canonical asset id.
/// Performs no fiat amount conversion, no rounding, no absolute-value
/// conversion, and no fee/payment interpretation. Status is text, never
/// color/icon alone.
/// </summary>
public sealed class LiquidHistoryAssetChangeItemViewModel : ViewModelBase
{
	public LiquidHistoryAssetChangeItemViewModel(
		UiContext uiContext,
		LiquidWalletUiHistoryAssetChange change)
		: base(uiContext)
	{
		ArgumentNullException.ThrowIfNull(change);
		NetAtomicUnits = change.NetAtomicUnits;
		IsPeggedAsset = change.IsPeggedAsset;
		IsCredit = change.IsCredit;
		IsDebit = change.IsDebit;
		DisplayAmount = change.DisplayAmount;
		AssetIdHex = change.AssetIdHex;
		DirectionText = change.IsCredit ? "Credit" : "Debit";
		AssetDisplayReference = change.IsPeggedAsset
			? "L-BTC"
			: change.AssetIdHex;
	}

	public long NetAtomicUnits { get; }
	public bool IsPeggedAsset { get; }
	public bool IsCredit { get; }
	public bool IsDebit { get; }
	public string DirectionText { get; }

	/// <summary>
	/// The formatted per-asset display amount. For the pegged asset (L-BTC)
	/// this is the signed decimal form; for any other asset it is the signed
	/// atomic-unit count and the full canonical asset id.
	/// </summary>
	public string DisplayAmount { get; }

	/// <summary>The full canonical lowercase 64-hex asset id.</summary>
	public string AssetIdHex { get; }

	/// <summary>
	/// The per-asset display label: <c>L-BTC</c> for the pegged asset,
	/// otherwise the full canonical asset id.
	/// </summary>
	public string AssetDisplayReference { get; }
}
