using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The immutable Fluent projection of one public Liquid history asset
/// change: the exact signed atomic units, explicit <c>Credit</c>/
/// <c>Debit</c> text, <c>L-BTC</c> for the pegged asset, and an abbreviated
/// issued-asset display reference (first eight + U+2026 + last eight
/// lowercase hex characters, the same redaction shape as the transaction
/// reference). Performs no BTC/fiat amount conversion, no rescale, no
/// rounding, no absolute-value conversion, and no fee/payment
/// interpretation. Status is text, never color/icon alone.
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
		DirectionText = change.IsCredit ? "Credit" : "Debit";
		AssetDisplayReference = change.IsPeggedAsset
			? "L-BTC"
			: Abbreviate(change.AssetIdHex);
	}

	public long NetAtomicUnits { get; }
	public bool IsPeggedAsset { get; }
	public bool IsCredit { get; }
	public bool IsDebit { get; }
	public string DirectionText { get; }
	public string AssetDisplayReference { get; }

	private static string Abbreviate(string canonicalHex) =>
		string.Concat(
			canonicalHex.Substring(0, 8),
			"…",
			canonicalHex.Substring(canonicalHex.Length - 8, 8));
}
