using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Amounts;

internal sealed class LiquidAssetBalanceMap
{
	private readonly SortedDictionary<string, LiquidAssetAmount> _amountsByAsset;

	private LiquidAssetBalanceMap(
		LiquidAssetId peggedAssetId,
		SortedDictionary<string, LiquidAssetAmount> amountsByAsset)
	{
		PeggedAssetId = peggedAssetId;
		_amountsByAsset = amountsByAsset;
	}

	public LiquidAssetId PeggedAssetId { get; }
	public int AssetCount => _amountsByAsset.Count;
	public bool IsEmpty => _amountsByAsset.Count == 0;

	public static LiquidAssetBalanceMap Empty(LiquidAssetId peggedAssetId)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		return new LiquidAssetBalanceMap(peggedAssetId, CreateStorage());
	}

	public static LiquidAssetBalanceMap FromAmounts(
		LiquidAssetId peggedAssetId,
		IEnumerable<LiquidAssetAmount> amounts)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		ArgumentNullException.ThrowIfNull(amounts);

		SortedDictionary<string, LiquidAssetAmount> storage = CreateStorage();
		foreach (LiquidAssetAmount amount in amounts)
		{
			EnsureSameContext(peggedAssetId, amount);
			AddToStorage(storage, amount);
		}

		return new LiquidAssetBalanceMap(peggedAssetId, storage);
	}

	public LiquidAssetBalanceMap Add(LiquidAssetAmount amount)
	{
		EnsureSameContext(amount);
		var updated = CloneStorage();
		AddToStorage(updated, amount);
		return new LiquidAssetBalanceMap(PeggedAssetId, updated);
	}

	public LiquidAssetBalanceMap Subtract(LiquidAssetAmount amount)
	{
		EnsureSameContext(amount);
		var updated = CloneStorage();
		if (amount.IsZero)
		{
			return new LiquidAssetBalanceMap(PeggedAssetId, updated);
		}

		string key = amount.AssetId.CanonicalRpcHex;
		if (!updated.TryGetValue(key, out LiquidAssetAmount? current))
		{
			throw new OverflowException("Liquid asset balance subtraction cannot produce a negative result.");
		}

		LiquidAssetAmount remaining = current.Subtract(amount);
		if (remaining.IsZero)
		{
			updated.Remove(key);
		}
		else
		{
			updated[key] = remaining;
		}

		return new LiquidAssetBalanceMap(PeggedAssetId, updated);
	}

	public LiquidAssetAmount GetAmountOrZero(LiquidAssetId assetId)
	{
		ArgumentNullException.ThrowIfNull(assetId);
		return _amountsByAsset.TryGetValue(assetId.CanonicalRpcHex, out LiquidAssetAmount? amount)
			? amount
			: LiquidAssetAmount.Zero(assetId, PeggedAssetId);
	}

	public bool TryGetAmount(LiquidAssetId assetId, out LiquidAssetAmount? amount)
	{
		ArgumentNullException.ThrowIfNull(assetId);
		return _amountsByAsset.TryGetValue(assetId.CanonicalRpcHex, out amount);
	}

	public IReadOnlyList<LiquidAssetAmount> GetAmounts() =>
		new ReadOnlyCollection<LiquidAssetAmount>(_amountsByAsset.Values.ToArray());

	public override string ToString() => nameof(LiquidAssetBalanceMap);

	private static SortedDictionary<string, LiquidAssetAmount> CreateStorage() =>
		new(StringComparer.Ordinal);

	private static void AddToStorage(
		SortedDictionary<string, LiquidAssetAmount> storage,
		LiquidAssetAmount amount)
	{
		if (amount.IsZero)
		{
			return;
		}

		string key = amount.AssetId.CanonicalRpcHex;
		if (storage.TryGetValue(key, out LiquidAssetAmount? current))
		{
			storage[key] = current.Add(amount);
		}
		else
		{
			storage.Add(key, amount);
		}
	}

	private SortedDictionary<string, LiquidAssetAmount> CloneStorage() =>
		new(_amountsByAsset, StringComparer.Ordinal);

	private void EnsureSameContext(LiquidAssetAmount amount)
	{
		EnsureSameContext(PeggedAssetId, amount);
	}

	private static void EnsureSameContext(LiquidAssetId peggedAssetId, LiquidAssetAmount amount)
	{
		ArgumentNullException.ThrowIfNull(amount);
		if (amount.PeggedAssetId != peggedAssetId)
		{
			throw new InvalidOperationException(
				"Liquid asset balances require an identical pegged-asset context.");
		}
	}
}
