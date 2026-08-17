using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletCoinControlSnapshot
{
	private readonly LiquidWalletCoinControlEntry[] _entries;

	internal LiquidWalletCoinControlSnapshot(
		LiquidAssetId peggedAssetId,
		ulong revision,
		IReadOnlyList<LiquidWalletCoinControlEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		ArgumentNullException.ThrowIfNull(entries);

		LiquidWalletCoinControlEntry[] ownedEntries = CopyEntries(entries);
		ValidateEntries(peggedAssetId, ownedEntries);

		PeggedAssetId = peggedAssetId;
		Revision = revision;
		_entries = ownedEntries;
	}

	private LiquidWalletCoinControlSnapshot(
		LiquidAssetId peggedAssetId,
		ulong revision,
		LiquidWalletCoinControlEntry[] ownedEntries)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		ValidateEntries(peggedAssetId, ownedEntries);

		PeggedAssetId = peggedAssetId;
		Revision = revision;
		_entries = ownedEntries;
	}

	public LiquidAssetId PeggedAssetId { get; }
	public ulong Revision { get; }

	public IReadOnlyList<LiquidWalletCoinControlEntry> GetEntries() =>
		new ReadOnlyCollection<LiquidWalletCoinControlEntry>([.. _entries]);

	internal static LiquidWalletCoinControlSnapshot TakeOwnershipFromState(
		LiquidAssetId peggedAssetId,
		ulong revision,
		LiquidWalletCoinControlEntry[] ownedEntries)
	{
		ArgumentNullException.ThrowIfNull(ownedEntries);
		return new LiquidWalletCoinControlSnapshot(peggedAssetId, revision, ownedEntries);
	}

	public override string ToString() => nameof(LiquidWalletCoinControlSnapshot);

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

	private static void ValidateEntries(
		LiquidAssetId peggedAssetId,
		LiquidWalletCoinControlEntry[] entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		LiquidWalletCoinControlEntry? previous = null;
		for (int index = 0; index < entries.Length; index++)
		{
			LiquidWalletCoinControlEntry entry = entries[index]
				?? throw new ArgumentException(
					"A Liquid coin-control snapshot cannot contain a null entry.",
					nameof(entries));
			if (entry.PeggedAssetId != peggedAssetId ||
				entry.Amount.PeggedAssetId != peggedAssetId)
			{
				throw new ArgumentException(
					"A Liquid coin-control snapshot entry belongs to a different pegged-asset context.",
					nameof(entries));
			}
			if (previous is not null && CompareCanonical(previous, entry) >= 0)
			{
				throw new ArgumentException(
					"Liquid coin-control snapshot entries must be unique and canonically ordered.",
					nameof(entries));
			}

			previous = entry;
		}
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
