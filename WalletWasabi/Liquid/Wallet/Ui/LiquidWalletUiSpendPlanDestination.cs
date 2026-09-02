using WalletWasabi.Liquid.Addresses;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, display-ready projection of one confidential destination
/// of an exact Liquid spend plan: the canonical confidential address text,
/// the unconfidential address text, the canonical 64-character lowercase
/// destination asset-id hex, and the exact requested positive atomic-units
/// amount. Every destination of a successfully constructed plan is
/// confidential by construction (the landed
/// <see cref="LiquidSuppliedConfidentialDestination.Create"/> rejects any
/// non-confidential address), so <see cref="IsConfidential"/> is always
/// <see langword="true"/>. The projection copies every value out of the
/// landed internal destination; the internal type never crosses the
/// assembly boundary. No retry, no fallback, no caching, no filtering, and
/// no formatting beyond the additive pegged-asset marker the presentation
/// layer uses to pick its display convention.
/// </summary>
public sealed class LiquidWalletUiSpendPlanDestination
{
	private LiquidWalletUiSpendPlanDestination(
		string confidentialAddressText,
		string unconfidentialAddressText,
		string assetIdHex,
		bool isPeggedAsset,
		long atomicUnits,
		bool isWalletOwnedChange)
	{
		ConfidentialAddressText = confidentialAddressText;
		UnconfidentialAddressText = unconfidentialAddressText;
		AssetIdHex = assetIdHex;
		IsPeggedAsset = isPeggedAsset;
		AtomicUnits = atomicUnits;
		IsConfidential = true;
		IsWalletOwnedChange = isWalletOwnedChange;
	}

	public string ConfidentialAddressText { get; }
	public string UnconfidentialAddressText { get; }
	public string AssetIdHex { get; }

	/// <summary>
	/// Whether the destination asset is the wallet's pegged asset (L-BTC).
	/// Additive presentation flag: the view uses it to pick the L-BTC
	/// decimal display convention; issued assets stay in atomic units.
	/// </summary>
	public bool IsPeggedAsset { get; }
	public long AtomicUnits { get; }
	public bool IsConfidential { get; }

	/// <summary>
	/// Whether this destination is the wallet-owned change output the facade
	/// appended for a per-asset surplus: true exactly when the destination's
	/// canonical address text matches the change address supplied to the
	/// facade for that plan. When no change destination was supplied (no
	/// surplus) this is false for every destination — the projection shape is
	/// otherwise byte-identical. Additive attribution of ALREADY-composed
	/// change only; it never alters when or how change outputs are appended.
	/// </summary>
	public bool IsWalletOwnedChange { get; }

	internal static LiquidWalletUiSpendPlanDestination FromDestination(
		LiquidSuppliedConfidentialDestination destination,
		string? changeAddressCanonicalText = null)
	{
		ArgumentNullException.ThrowIfNull(destination);

		LiquidAddress address = destination.GetAddress();
		string canonicalAddressText = address.GetCanonicalAddressText();
		// The landed destination Create rejects a null amount and the landed
		// plan Create rejects a null amount again; the null-forgiving
		// operator adds no runtime check and no fallback. The change flag is
		// attribution only: a canonical-text match against the supplied
		// change address; no change destination supplied means no match.
		return new LiquidWalletUiSpendPlanDestination(
			canonicalAddressText,
			address.GetUnconfidentialAddressText(),
			destination.GetAssetId().CanonicalRpcHex,
			destination.GetAmount()!.IsPeggedAsset,
			destination.GetAmount()!.AtomicUnits,
			changeAddressCanonicalText is not null
				&& StringComparer.Ordinal.Equals(canonicalAddressText, changeAddressCanonicalText));
	}
}
