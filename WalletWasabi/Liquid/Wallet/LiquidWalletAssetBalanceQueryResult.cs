using System.Collections;
using WalletWasabi.Liquid.Amounts;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletAssetBalanceQueryResult : IReadOnlyList<LiquidAssetAmount>
{
	private readonly LiquidAssetAmount[] _amounts;

	internal LiquidWalletAssetBalanceQueryResult(LiquidAssetAmount[] amounts)
	{
		ArgumentNullException.ThrowIfNull(amounts);

		var ownedAmounts = new LiquidAssetAmount[amounts.Length];
		for (int index = 0; index < ownedAmounts.Length; index++)
		{
			ownedAmounts[index] = amounts[index] ??
				throw new ArgumentException(
					"The Liquid asset balance query result could not be accepted.",
					nameof(amounts));
		}

		_amounts = ownedAmounts;
	}

	public int Count => _amounts.Length;

	public LiquidAssetAmount this[int index] => _amounts[index];

	public IEnumerator<LiquidAssetAmount> GetEnumerator() =>
		((IEnumerable<LiquidAssetAmount>)_amounts).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public override string ToString() => nameof(LiquidWalletAssetBalanceQueryResult);
}
