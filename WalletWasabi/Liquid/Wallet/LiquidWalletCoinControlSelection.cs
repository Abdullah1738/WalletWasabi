using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletCoinControlSelection
{
	private sealed class SelectedAssetTotal
	{
		public SelectedAssetTotal(LiquidAssetAmount amount)
		{
			AssetId = amount.AssetId;
			PeggedAssetId = amount.PeggedAssetId;
			AtomicUnits = amount.AtomicUnits;
		}

		public LiquidAssetId AssetId { get; }
		public LiquidAssetId PeggedAssetId { get; }
		public long AtomicUnits { get; private set; }

		public void Add(LiquidAssetAmount amount)
		{
			long updated;
			try
			{
				updated = checked(AtomicUnits + amount.AtomicUnits);
			}
			catch (OverflowException)
			{
				throw new OverflowException(
					"Liquid coin-control selection accumulation exceeded the supported range.");
			}

			if (AssetId == PeggedAssetId &&
				updated > LiquidAssetAmount.MaxPeggedAssetAtomicUnits)
			{
				throw new OverflowException(
					"Liquid coin-control selection accumulation exceeded the supported range.");
			}

			AtomicUnits = updated;
		}

		public LiquidAssetAmount ToAmount() =>
			LiquidAssetAmount.Create(AssetId, PeggedAssetId, AtomicUnits);
	}

	private readonly LiquidWalletCoinControlEntry[] _entries;
	private readonly LiquidAssetBalanceMap _selectedBalances;

	internal LiquidWalletCoinControlSelection(
		LiquidAssetId peggedAssetId,
		ulong sourceRevision,
		IReadOnlyList<LiquidWalletCoinControlEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		ArgumentNullException.ThrowIfNull(entries);

		LiquidWalletCoinControlEntry[] ownedEntries = CopyEntries(entries);
		LiquidAssetBalanceMap selectedBalances = ValidateAndAggregate(peggedAssetId, ownedEntries);

		PeggedAssetId = peggedAssetId;
		SourceRevision = sourceRevision;
		_entries = ownedEntries;
		_selectedBalances = selectedBalances;
	}

	private LiquidWalletCoinControlSelection(
		LiquidAssetId peggedAssetId,
		ulong sourceRevision,
		LiquidWalletCoinControlEntry[] ownedEntries)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		LiquidAssetBalanceMap selectedBalances = ValidateAndAggregate(peggedAssetId, ownedEntries);

		PeggedAssetId = peggedAssetId;
		SourceRevision = sourceRevision;
		_entries = ownedEntries;
		_selectedBalances = selectedBalances;
	}

	public LiquidAssetId PeggedAssetId { get; }
	public ulong SourceRevision { get; }

	public IReadOnlyList<LiquidWalletCoinControlEntry> GetEntries() =>
		new ReadOnlyCollection<LiquidWalletCoinControlEntry>([.. _entries]);

	public LiquidAssetBalanceMap GetSelectedBalances() => _selectedBalances;

	internal static LiquidWalletCoinControlSelection TakeOwnershipFromState(
		LiquidAssetId peggedAssetId,
		ulong sourceRevision,
		LiquidWalletCoinControlEntry[] ownedEntries) =>
		new(peggedAssetId, sourceRevision, ownedEntries);

	public override string ToString() => nameof(LiquidWalletCoinControlSelection);

	private static LiquidWalletCoinControlEntry[] CopyEntries(
		IReadOnlyList<LiquidWalletCoinControlEntry> entries)
	{
		var copiedEntries = new LiquidWalletCoinControlEntry[entries.Count];
		for (int index = 0; index < copiedEntries.Length; index++)
		{
			copiedEntries[index] = entries[index];
		}

		return copiedEntries;
	}

	private static LiquidAssetBalanceMap ValidateAndAggregate(
		LiquidAssetId peggedAssetId,
		LiquidWalletCoinControlEntry[] entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		if (entries.Length == 0)
		{
			throw new ArgumentException(
				"A Liquid coin-control selection requires at least one entry.",
				nameof(entries));
		}

		var totals = new Dictionary<string, SelectedAssetTotal>(StringComparer.Ordinal);
		LiquidWalletCoinControlEntry? previous = null;
		for (int index = 0; index < entries.Length; index++)
		{
			LiquidWalletCoinControlEntry entry = entries[index]
				?? throw new ArgumentException(
					"A Liquid coin-control selection cannot contain a null entry.",
					nameof(entries));
			if (entry.PeggedAssetId != peggedAssetId ||
				entry.Amount.PeggedAssetId != peggedAssetId)
			{
				throw new ArgumentException(
					"A Liquid coin-control selection entry belongs to a different pegged-asset context.",
					nameof(entries));
			}
			if (previous is not null && CompareCanonical(previous, entry) >= 0)
			{
				throw new ArgumentException(
					"Liquid coin-control selection entries must be unique and canonically ordered.",
					nameof(entries));
			}

			string assetKey = entry.Amount.AssetId.CanonicalRpcHex;
			if (totals.TryGetValue(assetKey, out SelectedAssetTotal? total))
			{
				total.Add(entry.Amount);
			}
			else
			{
				totals.Add(assetKey, new SelectedAssetTotal(entry.Amount));
			}

			previous = entry;
		}

		var amounts = new LiquidAssetAmount[totals.Count];
		int amountIndex = 0;
		foreach (SelectedAssetTotal total in totals.Values)
		{
			amounts[amountIndex++] = total.ToAmount();
		}

		return LiquidAssetBalanceMap.FromAmounts(peggedAssetId, amounts);
	}

	private static int CompareCanonical(
		LiquidWalletCoinControlEntry left,
		LiquidWalletCoinControlEntry right)
	{
		int transactionOrder = StringComparer.Ordinal.Compare(
			left.OutPoint.TransactionId.CanonicalRpcHex,
			right.OutPoint.TransactionId.CanonicalRpcHex);
		return transactionOrder != 0
			? transactionOrder
			: left.OutPoint.OutputIndex.CompareTo(right.OutPoint.OutputIndex);
	}
}
