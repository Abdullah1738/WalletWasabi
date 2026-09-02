using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The Fluent display wrapper for one <see cref="LiquidWalletUiAssetAmount"/>
/// of an exact Liquid spend plan (the explicit fee or one per-asset
/// selected total): the projection's exact values plus the pegged-aware
/// display amount. For the pegged asset the display is the L-BTC decimal
/// form (the Liquid protocol fixes the pegged precision at 1e8); for any
/// issued asset it is the raw atomic-unit count with the honest
/// "atomic units" label — an issued asset has no known precision and is
/// never scaled. The conversion lives in the single
/// <see cref="LiquidAmountDisplay"/> helper, so the string is unit-testable
/// off the view.
/// </summary>
public sealed class LiquidSpendPlanAssetAmountItemViewModel : ViewModelBase
{
	public LiquidSpendPlanAssetAmountItemViewModel(UiContext uiContext, LiquidWalletUiAssetAmount amount)
		: base(uiContext)
	{
		ArgumentNullException.ThrowIfNull(amount);
		AssetIdHex = amount.AssetIdHex;
		IsPeggedAsset = amount.IsPeggedAsset;
		AtomicUnits = amount.AtomicUnits;
		AmountDisplayText = LiquidAmountDisplay.FormatBalance(IsPeggedAsset, AtomicUnits);
	}

	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long AtomicUnits { get; }

	/// <summary>
	/// The pegged-aware display amount: the L-BTC decimal form with the unit
	/// label for the pegged asset, the raw atomic-unit count with the honest
	/// "atomic units" label for any issued asset.
	/// </summary>
	public string AmountDisplayText { get; }
}
