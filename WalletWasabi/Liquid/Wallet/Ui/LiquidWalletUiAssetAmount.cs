using WalletWasabi.Liquid.Amounts;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, display-ready projection of one asset's amount inside an
/// exact Liquid spend plan: the canonical 64-character lowercase asset-id
/// hex, whether the asset is the wallet's pegged asset (L-BTC), and the
/// exact nonnegative atomic-units value. The projection copies every value
/// out of the landed internal <see cref="LiquidAssetAmount"/>; the internal
/// record never crosses the assembly boundary. No retry, no fallback, no
/// caching, no filtering, and no formatting.
/// </summary>
public sealed class LiquidWalletUiAssetAmount
{
	private LiquidWalletUiAssetAmount(
		string assetIdHex,
		bool isPeggedAsset,
		long atomicUnits)
	{
		AssetIdHex = assetIdHex;
		IsPeggedAsset = isPeggedAsset;
		AtomicUnits = atomicUnits;
	}

	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long AtomicUnits { get; }

	internal static LiquidWalletUiAssetAmount FromAmount(LiquidAssetAmount amount)
	{
		ArgumentNullException.ThrowIfNull(amount);
		return new LiquidWalletUiAssetAmount(
			amount.AssetId.CanonicalRpcHex,
			amount.IsPeggedAsset,
			amount.AtomicUnits);
	}

	// The per-asset selected-total projection: the accumulated total of one
	// asset across the plan's selected entries (already validated by the
	// landed plan construction).
	internal static LiquidWalletUiAssetAmount FromTotal(
		string assetIdHex,
		bool isPeggedAsset,
		long atomicUnits)
	{
		ArgumentNullException.ThrowIfNull(assetIdHex);
		return new LiquidWalletUiAssetAmount(assetIdHex, isPeggedAsset, atomicUnits);
	}
}
