namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, signing-ready projection of one selected input of an
/// exact Liquid spend plan: the 72-character consensus outpoint hex (the
/// 32-byte transaction id followed by the 4-byte little-endian output
/// index), the canonical 64-character lowercase asset-id hex, and the exact
/// positive atomic-units value. The projection copies every value out of
/// the landed internal <see cref="LiquidWalletCoinControlEntry"/>; the
/// internal type never crosses the assembly boundary and the
/// <paramref name="entry"/> reference is used only for the duration of
/// <see cref="FromEntry"/> and is never stored. No retry, no fallback, no
/// caching, no filtering, and no formatting.
/// </summary>
public sealed class LiquidWalletUiSignRequestInput
{
	private LiquidWalletUiSignRequestInput(
		string outPointHex,
		string assetIdHex,
		long atomicUnits)
	{
		OutPointHex = outPointHex;
		AssetIdHex = assetIdHex;
		AtomicUnits = atomicUnits;
	}

	public string OutPointHex { get; }
	public string AssetIdHex { get; }
	public long AtomicUnits { get; }

	internal static LiquidWalletUiSignRequestInput FromEntry(
		LiquidWalletCoinControlEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		return new LiquidWalletUiSignRequestInput(
			Convert.ToHexString(entry.OutPoint.ToConsensusBytes()).ToLowerInvariant(),
			entry.Amount.AssetId.CanonicalRpcHex,
			entry.Amount.AtomicUnits);
	}
}
