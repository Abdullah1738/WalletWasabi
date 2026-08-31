using System.Globalization;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// One immutable per-asset net-change row of a Liquid transaction-history
/// entry: the canonical lowercase 64-hex asset id, whether the asset is the
/// wallet's pegged asset (L-BTC), the exact signed net effect in atomic
/// units copied from the landed <see cref="LiquidWalletAssetNetChange"/>,
/// and a formatted display string. For the pegged asset the display string
/// is the signed L-BTC decimal form (atomic units scaled by 1e8, trailing
/// fractional zeros trimmed, invariant culture). For a non-pegged asset it
/// is the signed atomic-unit count together with the full canonical asset
/// id. The amount itself is never rounded, absolutized, converted to fiat,
/// or interpreted as a fee or payment. <see cref="IsCredit"/> is exactly
/// <c>NetAtomicUnits &gt; 0</c> and <see cref="IsDebit"/> is exactly
/// <c>NetAtomicUnits &lt; 0</c>; zero is impossible because the landed
/// net-change type rejects it. No internal state reference is retained.
/// </summary>
public sealed class LiquidWalletUiHistoryAssetChange
{
	private const long AtomicUnitsPerPeggedUnit = 100_000_000L;

	private LiquidWalletUiHistoryAssetChange(
		string assetIdHex,
		bool isPeggedAsset,
		long netAtomicUnits)
	{
		AssetIdHex = assetIdHex;
		IsPeggedAsset = isPeggedAsset;
		NetAtomicUnits = netAtomicUnits;
		DisplayAmount = isPeggedAsset
			? FormatPeggedAmount(netAtomicUnits)
			: string.Create(
				CultureInfo.InvariantCulture,
				$"{netAtomicUnits} atomic units of {assetIdHex}");
	}

	public string AssetIdHex { get; }
	public bool IsPeggedAsset { get; }
	public long NetAtomicUnits { get; }
	public bool IsCredit => NetAtomicUnits > 0;
	public bool IsDebit => NetAtomicUnits < 0;

	/// <summary>
	/// The formatted per-asset display string. For the pegged asset (L-BTC)
	/// this is the signed decimal form with up to eight fractional digits;
	/// for any other asset it is the signed atomic-unit count and the full
	/// canonical asset id.
	/// </summary>
	public string DisplayAmount { get; }

	internal static LiquidWalletUiHistoryAssetChange FromChange(
		LiquidWalletAssetNetChange change)
	{
		ArgumentNullException.ThrowIfNull(change);
		return new LiquidWalletUiHistoryAssetChange(
			change.AssetId.CanonicalRpcHex,
			change.AssetId == change.PeggedAssetId,
			change.NetAtomicUnits);
	}

	// Signed L-BTC decimal form: atomic units scaled by 1e8 with an explicit
	// sign, trailing fractional zeros trimmed. The pegged-asset range
	// (<= 21e14 atomic units) is far inside decimal precision, so the scaled
	// value is exact. The landed net-change type rejects long.MinValue, so
	// Math.Abs cannot overflow.
	private static string FormatPeggedAmount(long netAtomicUnits)
	{
		decimal scaled = netAtomicUnits / (decimal)AtomicUnitsPerPeggedUnit;
		return scaled.ToString("0.########", CultureInfo.InvariantCulture);
	}
}
