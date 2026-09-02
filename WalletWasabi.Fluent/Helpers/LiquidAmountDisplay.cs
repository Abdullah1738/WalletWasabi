using WalletWasabi.Fluent.Extensions;

namespace WalletWasabi.Fluent.Helpers;

/// <summary>
/// The Liquid amount display conventions. The pegged asset (L-BTC) has a
/// fixed precision of 1e8 atomic units per unit, set by the Liquid protocol,
/// so a pegged amount renders as the L-BTC decimal form with Wasabi's
/// conventional BTC fraction digits (the same
/// <see cref="CurrencyExtensions.FormattedBtcFixedFractional"/> convention
/// the BTC wallet uses). An issued asset has no known precision — no asset
/// metadata exists for an arbitrary Liquid issuance — so its amount is never
/// scaled and stays in atomic units.
/// </summary>
internal static class LiquidAmountDisplay
{
	// The Liquid protocol constant: exactly 1e8 atomic units per L-BTC.
	private const long AtomicUnitsPerPeggedUnit = 100_000_000L;

	/// <summary>
	/// The L-BTC decimal form of a pegged amount: the atomic units scaled by
	/// 1e8 and rendered with Wasabi's conventional fixed eight fraction
	/// digits (e.g. 100_000 atomic units renders "0.00 100 000"). The pegged
	/// range is far inside decimal precision, so the scaled value is exact.
	/// The magnitude only — the caller appends the unit label.
	/// </summary>
	public static string FormatPeggedAmount(long atomicUnits)
	{
		decimal scaled = atomicUnits / (decimal)AtomicUnitsPerPeggedUnit;
		return scaled.FormattedBtcFixedFractional();
	}

	/// <summary>
	/// The balance display string for one held asset: the L-BTC decimal form
	/// with the unit label for the pegged asset, the raw atomic-unit count
	/// with the honest "atomic units" label for any issued asset (unknown
	/// precision — never scaled).
	/// </summary>
	public static string FormatBalance(bool isPeggedAsset, long atomicUnits) =>
		isPeggedAsset
			? $"{FormatPeggedAmount(atomicUnits)} L-BTC"
			: $"{atomicUnits} atomic units";
}
