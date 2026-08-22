namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// One immutable per-asset net-change row of a Liquid transaction-history
/// entry: the canonical lowercase 64-hex asset id, whether the asset is the
/// wallet's pegged asset (L-BTC), and the exact signed net effect in atomic
/// units copied from the landed
/// <see cref="LiquidWalletAssetNetChange"/>. The amount is never rescaled,
/// rounded, absolutized, decimal-formatted, converted to fiat, or
/// interpreted as a fee or payment. <see cref="IsCredit"/> is exactly
/// <c>NetAtomicUnits &gt; 0</c> and <see cref="IsDebit"/> is exactly
/// <c>NetAtomicUnits &lt; 0</c>; zero is impossible because the landed
/// net-change type rejects it. No internal state reference is retained.
/// </summary>
public sealed class LiquidWalletUiHistoryAssetChange
{
	private LiquidWalletUiHistoryAssetChange(
		string assetIdHex,
		bool isPeggedAsset,
		long netAtomicUnits)
	{
		AssetIdHex = assetIdHex;
		IsPeggedAsset = isPeggedAsset;
		NetAtomicUnits = netAtomicUnits;
	}

	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long NetAtomicUnits { get; }
	public bool IsCredit => NetAtomicUnits > 0;
	public bool IsDebit => NetAtomicUnits < 0;

	internal static LiquidWalletUiHistoryAssetChange FromChange(
		LiquidWalletAssetNetChange change)
	{
		ArgumentNullException.ThrowIfNull(change);
		return new LiquidWalletUiHistoryAssetChange(
			change.AssetId.CanonicalRpcHex,
			change.AssetId == change.PeggedAssetId,
			change.NetAtomicUnits);
	}
}
