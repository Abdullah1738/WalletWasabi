using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletTransactionEffect
{
	private readonly LiquidWalletAssetNetChange[] _assetNetChanges;

	internal LiquidWalletTransactionEffect(
		LiquidTransactionId transactionId,
		LiquidAssetId peggedAssetId,
		LiquidConfirmation? confirmation,
		IReadOnlyList<LiquidWalletAssetNetChange> assetNetChanges)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		ArgumentNullException.ThrowIfNull(assetNetChanges);
		if (transactionId.IsZero)
		{
			throw new ArgumentException(
				"A nonzero Liquid transaction identifier is required.",
				nameof(transactionId));
		}

		var copy = new LiquidWalletAssetNetChange[assetNetChanges.Count];
		string? previousAssetId = null;
		for (int index = 0; index < copy.Length; index++)
		{
			LiquidWalletAssetNetChange change = assetNetChanges[index];
			ArgumentNullException.ThrowIfNull(change, nameof(assetNetChanges));
			if (change.PeggedAssetId != peggedAssetId)
			{
				throw new ArgumentException(
					"Every asset net change must use the transaction-effect pegged-asset context.",
					nameof(assetNetChanges));
			}

			string assetId = change.AssetId.CanonicalRpcHex;
			if (previousAssetId is not null &&
				StringComparer.Ordinal.Compare(previousAssetId, assetId) >= 0)
			{
				throw new ArgumentException(
					"Liquid wallet asset net changes must have unique canonical order.",
					nameof(assetNetChanges));
			}

			copy[index] = change;
			previousAssetId = assetId;
		}

		TransactionId = transactionId;
		PeggedAssetId = peggedAssetId;
		Confirmation = confirmation;
		_assetNetChanges = copy;
	}

	public LiquidTransactionId TransactionId { get; }
	public LiquidAssetId PeggedAssetId { get; }
	public LiquidConfirmation? Confirmation { get; }

	public IReadOnlyList<LiquidWalletAssetNetChange> GetAssetNetChanges() =>
		new ReadOnlyCollection<LiquidWalletAssetNetChange>([.. _assetNetChanges]);

	public override string ToString() => nameof(LiquidWalletTransactionEffect);
}
