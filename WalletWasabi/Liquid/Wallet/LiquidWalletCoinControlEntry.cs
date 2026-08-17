using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletCoinControlEntry
{
	private LiquidWalletCoinControlEntry(
		LiquidOutPoint outPoint,
		LiquidAssetAmount amount,
		LiquidAssetId peggedAssetId,
		LiquidConfirmation? confirmation)
	{
		OutPoint = outPoint;
		Amount = amount;
		PeggedAssetId = peggedAssetId;
		Confirmation = confirmation;
	}

	public LiquidOutPoint OutPoint { get; }
	public LiquidAssetAmount Amount { get; }
	public LiquidAssetId PeggedAssetId { get; }
	public LiquidConfirmation? Confirmation { get; }

	internal static LiquidWalletCoinControlEntry Create(
		LiquidOutPoint outPoint,
		LiquidAssetAmount amount,
		LiquidAssetId peggedAssetId,
		LiquidConfirmation? confirmation)
	{
		ArgumentNullException.ThrowIfNull(outPoint);
		ArgumentNullException.ThrowIfNull(amount);
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		if (amount.IsZero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(amount),
				"A positive Liquid coin-control amount is required.");
		}
		if (amount.PeggedAssetId != peggedAssetId)
		{
			throw new ArgumentException(
				"A Liquid coin-control entry belongs to a different pegged-asset context.",
				nameof(amount));
		}

		return new LiquidWalletCoinControlEntry(outPoint, amount, peggedAssetId, confirmation);
	}

	public override string ToString() => nameof(LiquidWalletCoinControlEntry);
}
