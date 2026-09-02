using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// One checkable row of the Liquid coin-control list: the wallet's
/// <see cref="LiquidWalletUiSelectableOutput"/> projected for display plus
/// the <see cref="IsSelected"/> checkbox state that drives the selected
/// outpoint set the plan/sign path consumes. The row carries the exact
/// <see cref="SelectionId"/> (the 72-character consensus-bytes hex the
/// plan/sign path expects) verbatim — the checkbox only chooses which
/// wallet outputs fund the plan; it never fabricates an output, never
/// admits a non-wallet outpoint, and never bypasses the landed exact-plan
/// validation. The amount renders the pegged-aware display form via
/// <see cref="LiquidAmountDisplay"/>: the L-BTC decimal for the pegged
/// asset, the raw atomic-unit count for any issued asset.
/// </summary>
public sealed partial class LiquidSelectableOutputItemViewModel : ViewModelBase
{
	[AutoNotify] private bool _isSelected;

	public LiquidSelectableOutputItemViewModel(UiContext uiContext, LiquidWalletUiSelectableOutput output)
		: base(uiContext)
	{
		ArgumentNullException.ThrowIfNull(output);
		SelectionId = output.SelectionId;
		TransactionIdHex = output.TransactionIdHex;
		OutputIndex = output.OutputIndex;
		AssetIdHex = output.AssetIdHex;
		IsPeggedAsset = output.IsPeggedAsset;
		AtomicUnits = output.AtomicUnits;
		OutPointDisplayText = $"{TruncateHex(TransactionIdHex)}:{OutputIndex}";
		AssetMarkerText = IsPeggedAsset ? "L-BTC" : "issued";
		AmountDisplayText = LiquidAmountDisplay.FormatBalance(IsPeggedAsset, AtomicUnits);
	}

	// The exact 72-character consensus-bytes hex the plan/sign path consumes.
	public string SelectionId { get; }
	public string TransactionIdHex { get; }
	public uint OutputIndex { get; }
	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long AtomicUnits { get; }

	/// <summary>The outpoint coordinate, txid hex truncated for display.</summary>
	public string OutPointDisplayText { get; }

	/// <summary>The asset marker: "L-BTC" for the pegged asset, "issued" otherwise.</summary>
	public string AssetMarkerText { get; }

	/// <summary>The pegged-aware amount display (L-BTC decimal / atomic units).</summary>
	public string AmountDisplayText { get; }

	private static string TruncateHex(string hex) =>
		hex.Length <= 12 ? hex : string.Concat(hex.AsSpan(0, 8), "…", hex.AsSpan(hex.Length - 4));
}
