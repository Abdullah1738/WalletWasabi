using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Wallet.Ui;

public sealed class LiquidWalletUiSelectableOutput
{
	private LiquidWalletUiSelectableOutput(
		string selectionId,
		string transactionIdHex,
		uint outputIndex,
		string assetIdHex,
		long atomicUnits,
		bool isPeggedAsset,
		uint? confirmationHeight,
		string? confirmationBlockHash)
	{
		SelectionId = selectionId;
		TransactionIdHex = transactionIdHex;
		OutputIndex = outputIndex;
		AssetIdHex = assetIdHex;
		AtomicUnits = atomicUnits;
		IsPeggedAsset = isPeggedAsset;
		ConfirmationHeight = confirmationHeight;
		ConfirmationBlockHash = confirmationBlockHash;
	}

	public string SelectionId { get; }
	public string TransactionIdHex { get; }
	public uint OutputIndex { get; }
	public string AssetIdHex { get; }
	public long AtomicUnits { get; }
	public bool IsPeggedAsset { get; }
	public uint? ConfirmationHeight { get; }
	public string? ConfirmationBlockHash { get; }

	internal static LiquidWalletUiSelectableOutput FromEntry(LiquidWalletCoinControlEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		return new LiquidWalletUiSelectableOutput(
			Convert.ToHexString(entry.OutPoint.ToConsensusBytes()).ToLowerInvariant(),
			entry.OutPoint.TransactionId.CanonicalRpcHex,
			entry.OutPoint.OutputIndex,
			entry.Amount.AssetId.CanonicalRpcHex,
			entry.Amount.AtomicUnits,
			entry.Amount.IsPeggedAsset,
			entry.Confirmation?.Height,
			entry.Confirmation?.CanonicalBlockHash);
	}
}

public sealed class LiquidWalletUiSelectableOutputsSnapshot
{
	private LiquidWalletUiSelectableOutputsSnapshot(
		string walletName,
		string networkManifestId,
		string peggedAssetIdHex,
		ulong revision,
		IReadOnlyList<LiquidWalletUiSelectableOutput> outputs)
	{
		WalletName = walletName;
		NetworkManifestId = networkManifestId;
		PeggedAssetIdHex = peggedAssetIdHex;
		Revision = revision;
		Outputs = outputs;
	}

	public string WalletName { get; }
	public string NetworkManifestId { get; }
	public string PeggedAssetIdHex { get; }
	public ulong Revision { get; }
	public IReadOnlyList<LiquidWalletUiSelectableOutput> Outputs { get; }

	/// <summary>
	/// An empty selectable set bound to the same wallet, network manifest, and
	/// pegged asset as the supplied balance snapshot, at that snapshot's
	/// revision. The Fluent layer seeds its initial selectable stream with
	/// this when the caller supplies no open-time selectable snapshot: an
	/// empty coin-control list, never a fabricated output. Public because the
	/// Fluent model (a separate assembly) composes its own empty seed; no
	/// internal type is named.
	/// </summary>
	public static LiquidWalletUiSelectableOutputsSnapshot Empty(
		string walletName,
		LiquidWalletUiSnapshot balance)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletName);
		ArgumentNullException.ThrowIfNull(balance);
		if (!StringComparer.Ordinal.Equals(balance.WalletName, walletName))
		{
			throw new ArgumentException("The Liquid balance snapshot is bound to a different wallet.", nameof(balance));
		}

		return new LiquidWalletUiSelectableOutputsSnapshot(
			walletName,
			balance.NetworkManifestId,
			balance.PeggedAssetIdHex,
			balance.Revision,
			new ReadOnlyCollection<LiquidWalletUiSelectableOutput>([]));
	}

	internal static LiquidWalletUiSelectableOutputsSnapshot Capture(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletState state)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletName);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(state);
		if (!StringComparer.Ordinal.Equals(state.PeggedAssetId.CanonicalRpcHex, manifest.PeggedAssetId))
		{
			throw new ArgumentException("The Liquid wallet state is bound to a different network manifest.", nameof(state));
		}

		LiquidWalletCoinControlSnapshot snapshot = state.GetCoinControlSnapshot();
		if (snapshot.Revision != state.Revision || snapshot.PeggedAssetId != state.PeggedAssetId)
		{
			throw new ArgumentException("The Liquid coin-control snapshot does not match the supplied state.", nameof(state));
		}

		IReadOnlyList<LiquidWalletCoinControlEntry> entries = snapshot.GetEntries();
		var outputs = new LiquidWalletUiSelectableOutput[entries.Count];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] = LiquidWalletUiSelectableOutput.FromEntry(entries[index]);
		}

		return new LiquidWalletUiSelectableOutputsSnapshot(
			walletName,
			manifest.ManifestId,
			manifest.PeggedAssetId,
			snapshot.Revision,
			new ReadOnlyCollection<LiquidWalletUiSelectableOutput>(outputs));
	}
}
