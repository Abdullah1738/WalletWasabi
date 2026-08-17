using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletAssetNetChange : IEquatable<LiquidWalletAssetNetChange>
{
	private LiquidWalletAssetNetChange(
		LiquidAssetId assetId,
		LiquidAssetId peggedAssetId,
		long netAtomicUnits)
	{
		AssetId = assetId;
		PeggedAssetId = peggedAssetId;
		NetAtomicUnits = netAtomicUnits;
	}

	public LiquidAssetId AssetId { get; }
	public LiquidAssetId PeggedAssetId { get; }
	public long NetAtomicUnits { get; }
	public bool IsCredit => NetAtomicUnits > 0;
	public bool IsDebit => NetAtomicUnits < 0;

	public static LiquidWalletAssetNetChange Create(
		LiquidAssetId assetId,
		LiquidAssetId peggedAssetId,
		long netAtomicUnits)
	{
		ArgumentNullException.ThrowIfNull(assetId);
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		if (netAtomicUnits == 0 || netAtomicUnits == long.MinValue)
		{
			throw new ArgumentOutOfRangeException(
				nameof(netAtomicUnits),
				"A supported nonzero Liquid wallet asset net change is required.");
		}

		long peggedLimit = LiquidAssetAmount.MaxPeggedAssetAtomicUnits;
		if (assetId == peggedAssetId &&
			(netAtomicUnits > peggedLimit || netAtomicUnits < -peggedLimit))
		{
			throw new ArgumentOutOfRangeException(
				nameof(netAtomicUnits),
				"The pegged-asset net change exceeds the supported range.");
		}

		return new LiquidWalletAssetNetChange(assetId, peggedAssetId, netAtomicUnits);
	}

	public bool Equals(LiquidWalletAssetNetChange? other) =>
		other is not null &&
		AssetId == other.AssetId &&
		PeggedAssetId == other.PeggedAssetId &&
		NetAtomicUnits == other.NetAtomicUnits;

	public override bool Equals(object? obj) => Equals(obj as LiquidWalletAssetNetChange);

	public override int GetHashCode() => HashCode.Combine(AssetId, PeggedAssetId, NetAtomicUnits);

	public override string ToString() => nameof(LiquidWalletAssetNetChange);
}
