using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, display-ready projection of one exact Liquid multiasset
/// spend plan at one revision: the wallet name, the manifest binding, the
/// pegged-asset id, the source revision (the state's revision at plan time;
/// not a freshness claim), the selected input count, the confidential
/// output count, one entry per confidential destination in the landed batch
/// order, the explicit fee (denominated in the pegged asset), and the
/// per-asset selected totals — the exact-selection requirement: each
/// asset's selected total equals its requested total plus, for the pegged
/// asset, the explicit fee. The projection copies every value out of the
/// landed internal <see cref="LiquidOrdinaryWalletExactSpendPlan"/>; the
/// internal plan never crosses the assembly boundary and the
/// <paramref name="plan"/> reference is used only for the duration of
/// <see cref="FromPlan"/> and is never stored. No retry, no fallback, no
/// caching, no filtering, and no formatting.
/// </summary>
public sealed class LiquidWalletUiSpendPlan
{
	private LiquidWalletUiSpendPlan(
		string walletName,
		string networkManifestId,
		string peggedAssetIdHex,
		ulong sourceRevision,
		int selectedInputCount,
		int confidentialOutputCount,
		IReadOnlyList<LiquidWalletUiSpendPlanDestination> destinations,
		LiquidWalletUiAssetAmount explicitFee,
		IReadOnlyList<LiquidWalletUiAssetAmount> selectedTotals)
	{
		WalletName = walletName;
		NetworkManifestId = networkManifestId;
		PeggedAssetIdHex = peggedAssetIdHex;
		SourceRevision = sourceRevision;
		SelectedInputCount = selectedInputCount;
		ConfidentialOutputCount = confidentialOutputCount;
		Destinations = destinations;
		ExplicitFee = explicitFee;
		SelectedTotals = selectedTotals;
	}

	public string WalletName { get; }
	public string NetworkManifestId { get; }
	public string PeggedAssetIdHex { get; }
	public ulong SourceRevision { get; }
	public int SelectedInputCount { get; }
	public int ConfidentialOutputCount { get; }
	public IReadOnlyList<LiquidWalletUiSpendPlanDestination> Destinations { get; }
	public LiquidWalletUiAssetAmount ExplicitFee { get; }
	public IReadOnlyList<LiquidWalletUiAssetAmount> SelectedTotals { get; }

	/// <summary>
	/// Every destination of a successfully constructed plan is confidential
	/// by construction; surfaced so the view can render the confidential
	/// nature honestly.
	/// </summary>
	public bool IsConfidential => true;

	internal static LiquidWalletUiSpendPlan FromPlan(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidOrdinaryWalletExactSpendPlan plan,
		string? changeAddressCanonicalText = null)
	{
		ArgumentNullException.ThrowIfNull(walletName);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(plan);

		// The manifest binding: a plan is never presented against the wrong
		// network.
		if (!StringComparer.Ordinal.Equals(
			plan.GetDestinationNetworkManifestId(),
			manifest.ManifestId))
		{
			throw new ArgumentException(
				"The Liquid spend plan is bound to a different network manifest.",
				nameof(plan));
		}
		if (!StringComparer.Ordinal.Equals(
			plan.GetPeggedAssetId().CanonicalRpcHex,
			manifest.PeggedAssetId))
		{
			throw new ArgumentException(
				"The Liquid spend plan is bound to a different network manifest.",
				nameof(plan));
		}

		IReadOnlyList<LiquidSuppliedConfidentialDestination> destinations =
			plan.GetDestinations();
		var projectedDestinations = new LiquidWalletUiSpendPlanDestination[destinations.Count];
		for (int index = 0; index < projectedDestinations.Length; index++)
		{
			projectedDestinations[index] =
				LiquidWalletUiSpendPlanDestination.FromDestination(
					destinations[index],
					changeAddressCanonicalText);
		}

		// The per-asset selected totals, accumulated in the landed canonical
		// ascending asset-id-hex order (the same canonical order the landed
		// LiquidAssetBalanceMap.GetAmounts() projects).
		var totals = new SortedDictionary<string, (LiquidAssetAmount Amount, long AtomicUnits)>(
			StringComparer.Ordinal);
		foreach (LiquidWalletCoinControlEntry entry in plan.GetSelectedEntries())
		{
			LiquidAssetAmount amount = entry.Amount;
			string assetIdHex = amount.AssetId.CanonicalRpcHex;
			if (totals.TryGetValue(assetIdHex, out (LiquidAssetAmount Amount, long AtomicUnits) current))
			{
				totals[assetIdHex] = (current.Amount, checked(current.AtomicUnits + amount.AtomicUnits));
			}
			else
			{
				totals[assetIdHex] = (amount, amount.AtomicUnits);
			}
		}

		var selectedTotals = new LiquidWalletUiAssetAmount[totals.Count];
		int writeIndex = 0;
		foreach ((LiquidAssetAmount amount, long atomicUnits) in totals.Values)
		{
			selectedTotals[writeIndex++] = LiquidWalletUiAssetAmount.FromTotal(
				amount.AssetId.CanonicalRpcHex,
				amount.IsPeggedAsset,
				atomicUnits);
		}

		return new LiquidWalletUiSpendPlan(
			walletName,
			manifest.ManifestId,
			manifest.PeggedAssetId,
			plan.SourceRevision,
			plan.SelectedInputCount,
			plan.ConfidentialOutputCount,
			new ReadOnlyCollection<LiquidWalletUiSpendPlanDestination>(projectedDestinations),
			LiquidWalletUiAssetAmount.FromAmount(plan.GetExplicitFee()),
			new ReadOnlyCollection<LiquidWalletUiAssetAmount>(selectedTotals));
	}
}
