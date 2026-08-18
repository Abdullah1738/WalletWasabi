using WalletWasabi.Liquid.Amounts;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, display-ready projection of one asset's balance inside a
/// loaded Liquid managed wallet. Carries the asset id's canonical hex, the
/// exact landed atomic-unit amount, the pegged-asset marker for the L-BTC
/// row, and the confidential marker (always <see langword="true"/>: every
/// Liquid managed-wallet owned output's value is blinded on chain, so the
/// view renders the confidential nature honestly rather than implying a
/// plaintext on-chain amount). Carries no USD rate, no exchange rate, no
/// decimal formatting, and no asset metadata beyond the id — none exists
/// for an arbitrary Liquid issued asset.
/// </summary>
public sealed class LiquidWalletUiAssetBalance
{
	private LiquidWalletUiAssetBalance(
		string assetIdHex,
		bool isPeggedAsset,
		long atomicUnits)
	{
		AssetIdHex = assetIdHex;
		IsPeggedAsset = isPeggedAsset;
		AtomicUnits = atomicUnits;
		IsConfidential = true;
	}

	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long AtomicUnits { get; }
	public bool IsConfidential { get; }

	internal static LiquidWalletUiAssetBalance FromAmount(LiquidAssetAmount amount)
	{
		ArgumentNullException.ThrowIfNull(amount);
		return new LiquidWalletUiAssetBalance(
			amount.AssetId.CanonicalRpcHex,
			amount.IsPeggedAsset,
			amount.AtomicUnits);
	}
}
