using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, display-ready projection of one loaded Liquid managed
/// wallet at one revision: the wallet name, the manifest binding, the
/// pegged-asset id, the captured revision, and the multiasset balance set —
/// one entry per asset with a nonzero balance, the pegged asset (L-BTC)
/// first when present, then the issued assets in the landed canonical
/// ascending asset-id-hex order of
/// <see cref="LiquidAssetBalanceMap.GetAmounts()"/>. The projection copies
/// every value out of the internal state; the
/// <see cref="LiquidWalletState"/> reference is used only for the duration
/// of <see cref="Capture"/> and is never stored. No retry, no fallback, no
/// caching, no filtering beyond the landed zero-amount exclusion, and no
/// formatting.
/// </summary>
public sealed class LiquidWalletUiSnapshot
{
	private LiquidWalletUiSnapshot(
		string walletName,
		string networkManifestId,
		string peggedAssetIdHex,
		ulong revision,
		IReadOnlyList<LiquidWalletUiAssetBalance> balances)
	{
		WalletName = walletName;
		NetworkManifestId = networkManifestId;
		PeggedAssetIdHex = peggedAssetIdHex;
		Revision = revision;
		Balances = balances;
	}

	public string WalletName { get; }
	public string NetworkManifestId { get; }
	public string PeggedAssetIdHex { get; }
	public ulong Revision { get; }
	public IReadOnlyList<LiquidWalletUiAssetBalance> Balances { get; }
	public bool IsEmpty => Balances.Count == 0;

	internal static LiquidWalletUiSnapshot Capture(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletState state)
	{
		ArgumentNullException.ThrowIfNull(walletName);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(state);

		if (!StringComparer.Ordinal.Equals(
			state.PeggedAssetId.CanonicalRpcHex,
			manifest.PeggedAssetId))
		{
			throw new ArgumentException(
				"The Liquid wallet state is bound to a different network manifest.",
				nameof(state));
		}

		LiquidAssetBalanceMap balances = state.GetBalances();
		IReadOnlyList<LiquidAssetAmount> amounts = balances.GetAmounts();

		var projected = new LiquidWalletUiAssetBalance[amounts.Count];
		int peggedIndex = -1;
		for (int index = 0; index < amounts.Count; index++)
		{
			projected[index] = LiquidWalletUiAssetBalance.FromAmount(amounts[index]);
			if (amounts[index].IsPeggedAsset)
			{
				peggedIndex = index;
			}
		}

		LiquidWalletUiAssetBalance[] ordered;
		if (peggedIndex < 0)
		{
			ordered = projected;
		}
		else
		{
			// Pegged asset (L-BTC) first, then the issued assets in the
			// landed canonical ascending asset-id-hex order.
			ordered = new LiquidWalletUiAssetBalance[projected.Length];
			ordered[0] = projected[peggedIndex];
			int writeIndex = 1;
			for (int index = 0; index < projected.Length; index++)
			{
				if (index != peggedIndex)
				{
					ordered[writeIndex++] = projected[index];
				}
			}
		}

		return new LiquidWalletUiSnapshot(
			walletName,
			manifest.ManifestId,
			manifest.PeggedAssetId,
			state.Revision,
			new ReadOnlyCollection<LiquidWalletUiAssetBalance>(ordered));
	}
}
