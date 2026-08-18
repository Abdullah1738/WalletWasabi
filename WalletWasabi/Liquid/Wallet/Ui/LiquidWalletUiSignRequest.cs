using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, caller-signable package for one exact Liquid spend plan
/// at one revision: the wallet name, the manifest binding, the pegged-asset
/// id, the source revision (the state's revision at plan time; not a
/// freshness claim), the canonical WLPQ v1 wire frame as lowercase hex (the
/// exact bytes the landed <c>LiquidOrdinaryWalletPlanEncoder.TryEncode</c>
/// produced and the native <c>wln_wlpq_validate_v1</c> accepts), the 32-byte
/// source epoch as lowercase hex, one entry per selected input in the landed
/// plan order, the confidential output count, and the explicit fee
/// (denominated in the pegged asset). The hex projection keeps the public
/// surface span/string-only, mirroring the landed facade pattern; the raw
/// frame and epoch never escape as retained <see cref="byte"/> arrays — the
/// spans are used only for the duration of <see cref="FromPlanAndFrame"/>
/// and are never stored. The internal plan and frame types never cross the
/// assembly boundary. No retry, no fallback, no caching, no signing, and no
/// formatting beyond the hex projection.
/// </summary>
public sealed class LiquidWalletUiSignRequest
{
	private LiquidWalletUiSignRequest(
		string walletName,
		string networkManifestId,
		string peggedAssetIdHex,
		ulong sourceRevision,
		string wireFrameHex,
		string sourceEpochHex,
		IReadOnlyList<LiquidWalletUiSignRequestInput> inputs,
		int confidentialOutputCount,
		long explicitFeeAtomicUnits)
	{
		WalletName = walletName;
		NetworkManifestId = networkManifestId;
		PeggedAssetIdHex = peggedAssetIdHex;
		SourceRevision = sourceRevision;
		WireFrameHex = wireFrameHex;
		SourceEpochHex = sourceEpochHex;
		Inputs = inputs;
		ConfidentialOutputCount = confidentialOutputCount;
		ExplicitFeeAtomicUnits = explicitFeeAtomicUnits;
	}

	public string WalletName { get; }
	public string NetworkManifestId { get; }
	public string PeggedAssetIdHex { get; }
	public ulong SourceRevision { get; }
	public string WireFrameHex { get; }
	public string SourceEpochHex { get; }
	public IReadOnlyList<LiquidWalletUiSignRequestInput> Inputs { get; }
	public int ConfidentialOutputCount { get; }
	public long ExplicitFeeAtomicUnits { get; }

	/// <summary>
	/// Every destination of a successfully constructed plan is confidential
	/// by construction; surfaced so the caller can render the confidential
	/// nature honestly.
	/// </summary>
	public bool IsConfidential => true;

	internal static LiquidWalletUiSignRequest FromPlanAndFrame(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidOrdinaryWalletExactSpendPlan plan,
		ReadOnlySpan<byte> wireFrame,
		ReadOnlySpan<byte> sourceEpoch)
	{
		ArgumentNullException.ThrowIfNull(walletName);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(plan);

		// The manifest binding: a package is never presented against the
		// wrong network. Mirrors the landed LiquidWalletUiSpendPlan.FromPlan
		// binding exactly.
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

		IReadOnlyList<LiquidWalletCoinControlEntry> selectedEntries =
			plan.GetSelectedEntries();
		var inputs = new LiquidWalletUiSignRequestInput[selectedEntries.Count];
		for (int index = 0; index < inputs.Length; index++)
		{
			inputs[index] = LiquidWalletUiSignRequestInput.FromEntry(selectedEntries[index]);
		}

		// The frame and epoch spans are projected to lowercase hex and only
		// the hex is retained; the spans are never stored.
		return new LiquidWalletUiSignRequest(
			walletName,
			manifest.ManifestId,
			manifest.PeggedAssetId,
			plan.SourceRevision,
			Convert.ToHexString(wireFrame).ToLowerInvariant(),
			Convert.ToHexString(sourceEpoch).ToLowerInvariant(),
			new ReadOnlyCollection<LiquidWalletUiSignRequestInput>(inputs),
			plan.ConfidentialOutputCount,
			plan.GetExplicitFee().AtomicUnits);
	}
}
