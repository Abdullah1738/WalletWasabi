using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidOrdinaryWalletExactSpendPlan
{
	public const int MaximumSelectedInputCount = 100;
	public const int MaximumConfidentialOutputCount = 255;
	public const long MaximumAtomicUnits = 2_100_000_000_000_000;

	private const string RejectionMessage =
		"The exact Liquid ordinary-wallet spend plan could not be accepted.";

	private readonly string _destinationNetworkManifestId;
	private readonly LiquidAssetId _peggedAssetId;
	private readonly LiquidWalletCoinControlEntry[] _selectedEntries;
	private readonly LiquidSuppliedConfidentialDestination[] _destinations;
	private readonly LiquidAssetAmount _explicitFee;

	private LiquidOrdinaryWalletExactSpendPlan(
		ulong sourceRevision,
		string destinationNetworkManifestId,
		LiquidAssetId peggedAssetId,
		LiquidWalletCoinControlEntry[] selectedEntries,
		LiquidSuppliedConfidentialDestination[] destinations,
		LiquidAssetAmount explicitFee)
	{
		SourceRevision = sourceRevision;
		_destinationNetworkManifestId = destinationNetworkManifestId;
		_peggedAssetId = peggedAssetId;
		_selectedEntries = selectedEntries;
		_destinations = destinations;
		_explicitFee = explicitFee;
	}

	public ulong SourceRevision { get; }
	public int SelectedInputCount => _selectedEntries.Length;
	public int ConfidentialOutputCount => _destinations.Length;

	public string GetDestinationNetworkManifestId() => _destinationNetworkManifestId;

	public LiquidAssetId GetPeggedAssetId() => _peggedAssetId;

	public IReadOnlyList<LiquidWalletCoinControlEntry> GetSelectedEntries() =>
		new ReadOnlyCollection<LiquidWalletCoinControlEntry>([.. _selectedEntries]);

	public IReadOnlyList<LiquidSuppliedConfidentialDestination> GetDestinations() =>
		new ReadOnlyCollection<LiquidSuppliedConfidentialDestination>([.. _destinations]);

	public LiquidAssetAmount GetExplicitFee() => _explicitFee;

	internal static LiquidOrdinaryWalletExactSpendPlan Create(
		LiquidWalletCoinControlSelection selection,
		LiquidSuppliedConfidentialDestinationBatch destinations,
		LiquidAssetAmount explicitFee)
	{
		ArgumentNullException.ThrowIfNull(selection);
		ArgumentNullException.ThrowIfNull(destinations);
		ArgumentNullException.ThrowIfNull(explicitFee);

		int destinationCount = destinations.Count;
		if (destinationCount is < 1 or > MaximumConfidentialOutputCount)
		{
			throw new ArgumentOutOfRangeException(nameof(destinations), RejectionMessage);
		}

		IReadOnlyList<LiquidWalletCoinControlEntry> selectedSource = selection.GetEntries();
		var selectedEntries = new LiquidWalletCoinControlEntry[selectedSource.Count];
		for (int index = 0; index < selectedEntries.Length; index++)
		{
			selectedEntries[index] = selectedSource[index];
		}

		IReadOnlyList<LiquidSuppliedConfidentialDestination> destinationSource =
			destinations.GetDestinations();
		var ownedDestinations = new LiquidSuppliedConfidentialDestination[destinationCount];
		for (int index = 0; index < ownedDestinations.Length; index++)
		{
			ownedDestinations[index] = destinationSource[index];
		}

		if (selectedEntries.Length is < 1 or > MaximumSelectedInputCount)
		{
			throw new ArgumentOutOfRangeException(nameof(selection), RejectionMessage);
		}

		LiquidAssetId peggedAssetId = selection.PeggedAssetId;
		if (destinations.GetPeggedAssetId() != peggedAssetId)
		{
			throw new ArgumentException(RejectionMessage);
		}

		var selectedTotals = new Dictionary<LiquidAssetId, long>();
		for (int index = 0; index < selectedEntries.Length; index++)
		{
			LiquidWalletCoinControlEntry entry = selectedEntries[index];
			LiquidAssetAmount amount = entry.Amount;
			if (entry.PeggedAssetId != peggedAssetId ||
				amount.PeggedAssetId != peggedAssetId ||
				amount.AtomicUnits is < 1 or > MaximumAtomicUnits)
			{
				throw new ArgumentException(RejectionMessage);
			}

			AddTotal(selectedTotals, amount.AssetId, amount.AtomicUnits);
		}

		var requiredTotals = new Dictionary<LiquidAssetId, long>();
		for (int index = 0; index < ownedDestinations.Length; index++)
		{
			LiquidSuppliedConfidentialDestination destination = ownedDestinations[index];
			LiquidAssetAmount? amount = destination.GetAmount();
			if (destination.GetPeggedAssetId() != peggedAssetId ||
				amount is null ||
				amount.AssetId != destination.GetAssetId() ||
				amount.PeggedAssetId != peggedAssetId ||
				amount.AtomicUnits is < 1 or > MaximumAtomicUnits)
			{
				throw new ArgumentException(RejectionMessage);
			}

			AddTotal(requiredTotals, amount.AssetId, amount.AtomicUnits);
		}

		if (explicitFee.AssetId != peggedAssetId ||
			explicitFee.PeggedAssetId != peggedAssetId ||
			explicitFee.AtomicUnits is < 1 or > MaximumAtomicUnits)
		{
			throw new ArgumentException(RejectionMessage);
		}

		AddTotal(requiredTotals, explicitFee.AssetId, explicitFee.AtomicUnits);
		if (selectedTotals.Count != requiredTotals.Count)
		{
			throw new ArgumentException(RejectionMessage);
		}
		foreach ((LiquidAssetId assetId, long selectedTotal) in selectedTotals)
		{
			if (!requiredTotals.TryGetValue(assetId, out long requiredTotal) ||
				selectedTotal != requiredTotal)
			{
				throw new ArgumentException(RejectionMessage);
			}
		}

		return new LiquidOrdinaryWalletExactSpendPlan(
			selection.SourceRevision,
			new string(destinations.GetNetworkManifestId().AsSpan()),
			peggedAssetId,
			selectedEntries,
			ownedDestinations,
			explicitFee);
	}

	public override string ToString() => nameof(LiquidOrdinaryWalletExactSpendPlan);

	private static void AddTotal(
		Dictionary<LiquidAssetId, long> totals,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		try
		{
			totals[assetId] = totals.TryGetValue(assetId, out long current)
				? checked(current + atomicUnits)
				: atomicUnits;
		}
		catch (OverflowException)
		{
			throw new ArgumentException(RejectionMessage);
		}
	}
}
