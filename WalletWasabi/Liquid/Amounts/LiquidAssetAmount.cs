using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Amounts;

internal sealed record LiquidAssetAmount
{
	internal const long MaxPeggedAssetAtomicUnits = 2_100_000_000_000_000;

	private LiquidAssetAmount(
		LiquidAssetId assetId,
		LiquidAssetId peggedAssetId,
		long atomicUnits)
	{
		AssetId = assetId;
		PeggedAssetId = peggedAssetId;
		AtomicUnits = atomicUnits;
	}

	public LiquidAssetId AssetId { get; }
	public LiquidAssetId PeggedAssetId { get; }
	public long AtomicUnits { get; }
	public bool IsPeggedAsset => AssetId == PeggedAssetId;
	public bool IsZero => AtomicUnits == 0;

	public static LiquidAssetAmount Create(
		LiquidAssetId assetId,
		LiquidAssetId peggedAssetId,
		long atomicUnits)
	{
		ArgumentNullException.ThrowIfNull(assetId);
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		if (atomicUnits < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(atomicUnits),
				"A nonnegative Liquid asset amount is required.");
		}
		if (assetId == peggedAssetId && atomicUnits > MaxPeggedAssetAtomicUnits)
		{
			throw new ArgumentOutOfRangeException(
				nameof(atomicUnits),
				"The pegged asset amount exceeds the supported range.");
		}

		return new LiquidAssetAmount(assetId, peggedAssetId, atomicUnits);
	}

	public static LiquidAssetAmount Zero(LiquidAssetId assetId, LiquidAssetId peggedAssetId) =>
		Create(assetId, peggedAssetId, 0);

	public LiquidAssetAmount Add(LiquidAssetAmount other)
	{
		EnsureSameContext(other);
		long result;
		try
		{
			result = checked(AtomicUnits + other.AtomicUnits);
		}
		catch (OverflowException)
		{
			throw new OverflowException("Liquid asset amount addition exceeded the supported range.");
		}

		if (IsPeggedAsset && result > MaxPeggedAssetAtomicUnits)
		{
			throw new OverflowException("Liquid asset amount addition exceeded the supported range.");
		}

		return new LiquidAssetAmount(AssetId, PeggedAssetId, result);
	}

	public LiquidAssetAmount Subtract(LiquidAssetAmount other)
	{
		EnsureSameContext(other);
		if (other.AtomicUnits > AtomicUnits)
		{
			throw new OverflowException("Liquid asset amount subtraction cannot produce a negative result.");
		}

		return new LiquidAssetAmount(AssetId, PeggedAssetId, AtomicUnits - other.AtomicUnits);
	}

	public override string ToString() => nameof(LiquidAssetAmount);

	private void EnsureSameContext(LiquidAssetAmount other)
	{
		ArgumentNullException.ThrowIfNull(other);
		if (AssetId != other.AssetId || PeggedAssetId != other.PeggedAssetId)
		{
			throw new InvalidOperationException(
				"Liquid asset arithmetic requires an identical asset and pegged-asset context.");
		}
	}
}
