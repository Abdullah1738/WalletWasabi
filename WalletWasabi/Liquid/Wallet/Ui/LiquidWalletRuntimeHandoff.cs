using System;

namespace WalletWasabi.Liquid.Wallet.Ui;

public sealed class LiquidWalletRuntimeHandoff
{
	public LiquidWalletRuntimeHandoff(
		string canonicalWalletId,
		string networkManifestId,
		LiquidWalletUiSnapshot balances,
		LiquidWalletUiSelectableOutputsSnapshot selectableOutputs,
		LiquidWalletUiHistorySnapshot history,
		LiquidWalletUiReceiveMaterial receiveMaterial)
	{
		CanonicalWalletId = canonicalWalletId ?? throw new ArgumentNullException(nameof(canonicalWalletId));
		NetworkManifestId = networkManifestId ?? throw new ArgumentNullException(nameof(networkManifestId));
		Balances = balances ?? throw new ArgumentNullException(nameof(balances));
		SelectableOutputs = selectableOutputs ?? throw new ArgumentNullException(nameof(selectableOutputs));
		History = history ?? throw new ArgumentNullException(nameof(history));
		ReceiveMaterial = receiveMaterial ?? throw new ArgumentNullException(nameof(receiveMaterial));
		if (!StringComparer.Ordinal.Equals(canonicalWalletId, balances.WalletName) ||
			!StringComparer.Ordinal.Equals(canonicalWalletId, selectableOutputs.WalletName) ||
			!StringComparer.Ordinal.Equals(canonicalWalletId, history.WalletName) ||
			!StringComparer.Ordinal.Equals(networkManifestId, balances.NetworkManifestId) ||
			!StringComparer.Ordinal.Equals(networkManifestId, selectableOutputs.NetworkManifestId) ||
			!StringComparer.Ordinal.Equals(networkManifestId, history.NetworkManifestId) ||
			!StringComparer.Ordinal.Equals(balances.PeggedAssetIdHex, selectableOutputs.PeggedAssetIdHex) ||
			!StringComparer.Ordinal.Equals(balances.PeggedAssetIdHex, history.PeggedAssetIdHex) ||
			balances.Revision != selectableOutputs.Revision ||
			balances.Revision != history.Revision)
		{
			throw new ArgumentException("The Liquid runtime handoff snapshots must have one wallet, manifest, pegged asset, and revision.");
		}
	}

	public string CanonicalWalletId { get; }
	public string NetworkManifestId { get; }
	public LiquidWalletUiSnapshot Balances { get; }
	public LiquidWalletUiSelectableOutputsSnapshot SelectableOutputs { get; }
	public LiquidWalletUiHistorySnapshot History { get; }
	public LiquidWalletUiReceiveMaterial ReceiveMaterial { get; }
}
