using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// One immutable, complete-at-revision Liquid transaction-history
/// projection: the wallet name, the manifest binding, the pegged-asset id,
/// the captured revision, and every retained transaction effect of the
/// supplied state, in newest-applied-first order. If the landed effects are
/// <c>[A, B, C]</c> in application order, <see cref="Rows"/> is
/// <c>[C, B, A]</c>: the projector walks the landed retained application
/// order once from last to first and never sorts by confirmation height,
/// confirmation state, asset, signed amount, transaction reference,
/// wall-clock time, or UI locale. Each row's asset changes remain in the
/// landed canonical ascending asset-id order. Empty effects yield a
/// non-null empty read-only list. Every list is defensively owned and
/// read-only; no internal state, effect, or net-change reference crosses
/// the assembly boundary. There is no row cap, paging, filtering, grouping,
/// or truncation: a valid 4,097-effect live state produces 4,097 rows —
/// presentation virtualization is a Fluent concern and cannot change this
/// snapshot's exact cardinality.
/// </summary>
public sealed class LiquidWalletUiHistorySnapshot
{
	private LiquidWalletUiHistorySnapshot(
		string walletName,
		string networkManifestId,
		string peggedAssetIdHex,
		ulong revision,
		IReadOnlyList<LiquidWalletUiHistoryRow> rows)
	{
		WalletName = walletName;
		NetworkManifestId = networkManifestId;
		PeggedAssetIdHex = peggedAssetIdHex;
		Revision = revision;
		Rows = rows;
	}

	public string WalletName { get; }
	public string NetworkManifestId { get; }
	public string PeggedAssetIdHex { get; }
	public ulong Revision { get; }
	public IReadOnlyList<LiquidWalletUiHistoryRow> Rows { get; }
	public bool IsEmpty => Rows.Count == 0;

	internal static LiquidWalletUiHistorySnapshot Capture(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletState state)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletName);
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

		LiquidWalletTransactionEffectSnapshot effectSnapshot =
			state.GetTransactionEffectSnapshot();

		if (effectSnapshot.Revision != state.Revision)
		{
			throw new ArgumentException(
				"The Liquid transaction-effect snapshot revision does not match the supplied state.",
				nameof(state));
		}

		if (effectSnapshot.PeggedAssetId != state.PeggedAssetId)
		{
			throw new ArgumentException(
				"The Liquid transaction-effect snapshot is bound to a different pegged-asset context.",
				nameof(state));
		}

		IReadOnlyList<LiquidWalletTransactionEffect> effects = effectSnapshot.GetEffects();
		var projected = new LiquidWalletUiHistoryRow[effects.Count];
		for (int index = 0; index < effects.Count; index++)
		{
			// Newest applied first: walk the retained application order once
			// from last to first; never sort.
			projected[index] = LiquidWalletUiHistoryRow.FromEffect(
				effects[effects.Count - 1 - index]);
		}

		return new LiquidWalletUiHistorySnapshot(
			walletName,
			manifest.ManifestId,
			manifest.PeggedAssetId,
			effectSnapshot.Revision,
			new ReadOnlyCollection<LiquidWalletUiHistoryRow>(projected));
	}
}
