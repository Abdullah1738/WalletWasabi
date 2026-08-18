using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// One Liquid multiasset balance row: the asset id's canonical hex, the
/// pegged-asset marker for the L-BTC row, the exact atomic-unit amount, and
/// the confidential marker. Projected from one
/// <see cref="LiquidWalletUiAssetBalance"/>. No USD conversion (no per-asset
/// rate exists for an arbitrary Liquid issued asset) and no formatting
/// beyond the raw amount and the asset id — display formatting conventions
/// are the view's, not the model's.
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
	}

	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long AtomicUnits { get; }
	public bool IsConfidential { get; }
}
