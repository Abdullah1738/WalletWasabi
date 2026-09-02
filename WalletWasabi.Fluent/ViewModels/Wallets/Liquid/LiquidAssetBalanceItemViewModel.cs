using System.Windows.Input;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// One Liquid multiasset balance row: the asset id's canonical hex, the
/// pegged-asset marker for the L-BTC row, the exact atomic-unit amount, and
/// the confidential marker. Projected from one
/// <see cref="LiquidWalletUiAssetBalance"/>. No USD conversion (no per-asset
/// rate exists for an arbitrary Liquid issued asset). The display string is
/// the pegged-aware form: the L-BTC decimal for the pegged asset, the raw
/// atomic-unit count for any issued asset (unknown precision — never
/// scaled); the exact atomic units stay on <see cref="AtomicUnits"/>.
/// </summary>
public sealed class LiquidAssetBalanceItemViewModel : ViewModelBase
{
	public LiquidAssetBalanceItemViewModel(UiContext uiContext, LiquidWalletUiAssetBalance balance)
		: base(uiContext)
	{
		ArgumentNullException.ThrowIfNull(balance);
		AssetIdHex = balance.AssetIdHex;
		IsPeggedAsset = balance.IsPeggedAsset;
		AtomicUnits = balance.AtomicUnits;
		IsConfidential = balance.IsConfidential;
		BalanceDisplayText = LiquidAmountDisplay.FormatBalance(IsPeggedAsset, AtomicUnits);
	}

	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long AtomicUnits { get; }
	public bool IsConfidential { get; }

	/// <summary>
	/// The per-row Send affordance: navigates to the Liquid send flow with
	/// this row's asset pre-selected in the asset picker. Wired by
	/// <see cref="LiquidWalletViewModel"/> at projection (it owns the wallet
	/// model and the navigation); null for rows projected by the send flow's
	/// own picker options, which carry no send affordance.
	/// </summary>
	public ICommand? SendCommand { get; internal set; }

	/// <summary>
	/// The pegged-aware display amount: the L-BTC decimal form (e.g.
	/// "0.00 100 000 L-BTC") for the pegged asset, the raw atomic-unit count
	/// with the honest "atomic units" label for any issued asset.
	/// </summary>
	public string BalanceDisplayText { get; }
}
