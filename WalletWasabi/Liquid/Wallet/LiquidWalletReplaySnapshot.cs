using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// One immutable confirmation entry retained by an in-memory wallet-state
/// replay snapshot.
/// </summary>
internal sealed record LiquidWalletReplayConfirmation
{
	private LiquidWalletReplayConfirmation(
		LiquidTransactionId transactionId,
		LiquidConfirmation confirmation)
	{
		TransactionId = transactionId;
		Confirmation = confirmation;
	}

	public LiquidTransactionId TransactionId { get; }
	public LiquidConfirmation Confirmation { get; }

	public static LiquidWalletReplayConfirmation Create(
		LiquidTransactionId transactionId,
		LiquidConfirmation confirmation)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		ArgumentNullException.ThrowIfNull(confirmation);
		if (transactionId.IsZero)
		{
			throw new ArgumentException(
				"A nonzero Liquid transaction identifier is required.",
				nameof(transactionId));
		}
		return new LiquidWalletReplayConfirmation(transactionId, confirmation);
	}

	public override string ToString() => nameof(LiquidWalletReplayConfirmation);
}

/// <summary>
/// An in-memory replay description of the journal inputs that derive one
/// Liquid wallet state. It contains no cached balance or output state and
/// carries no persistence, chain, confirmation-source, or UTXO authority.
/// </summary>
internal sealed class LiquidWalletReplaySnapshot
{
	private readonly LiquidWalletTransactionDelta[] _deltas;
	private readonly LiquidWalletReplayConfirmation[] _confirmations;
	private readonly LiquidWalletReceiveLabelEntry[] _receiveLabels;

	private LiquidWalletReplaySnapshot(
		LiquidAssetId peggedAssetId,
		ulong revision,
		LiquidWalletTransactionDelta[] deltas,
		LiquidWalletReplayConfirmation[] confirmations,
		LiquidWalletReceiveLabelEntry[] receiveLabels)
	{
		PeggedAssetId = peggedAssetId;
		Revision = revision;
		_deltas = deltas;
		_confirmations = confirmations;
		_receiveLabels = receiveLabels;
	}

	public LiquidAssetId PeggedAssetId { get; }
	public ulong Revision { get; }
	public int ReceiveLabelCount => _receiveLabels.Length;

	public static LiquidWalletReplaySnapshot Create(
		LiquidAssetId peggedAssetId,
		ulong revision,
		IEnumerable<LiquidWalletTransactionDelta> deltas,
		IEnumerable<LiquidWalletReplayConfirmation> confirmations,
		IEnumerable<LiquidWalletReceiveLabelEntry>? receiveLabels = null)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		ArgumentNullException.ThrowIfNull(deltas);
		ArgumentNullException.ThrowIfNull(confirmations);

		LiquidWalletTransactionDelta[] copiedDeltas = deltas
			.Select(CloneDelta)
			.ToArray();
		LiquidWalletReplayConfirmation[] copiedConfirmations = confirmations
			.Select(CloneConfirmation)
			.OrderBy(
				entry => entry.TransactionId.CanonicalRpcHex,
				StringComparer.Ordinal)
			.ToArray();
		LiquidWalletReceiveLabelEntry[] copiedReceiveLabels = (receiveLabels ?? [])
			.Select(CloneReceiveLabelEntry)
			.OrderBy(entry => entry.Index)
			.ToArray();

		return new LiquidWalletReplaySnapshot(
			peggedAssetId,
			revision,
			copiedDeltas,
			copiedConfirmations,
			copiedReceiveLabels);
	}

	public IReadOnlyList<LiquidWalletTransactionDelta> GetDeltas() =>
		new ReadOnlyCollection<LiquidWalletTransactionDelta>(
			_deltas.Select(CloneDelta).ToArray());

	public IReadOnlyList<LiquidWalletReplayConfirmation> GetConfirmations() =>
		new ReadOnlyCollection<LiquidWalletReplayConfirmation>(
			_confirmations.Select(CloneConfirmation).ToArray());

	public IReadOnlyList<LiquidWalletReceiveLabelEntry> GetReceiveLabels() =>
		new ReadOnlyCollection<LiquidWalletReceiveLabelEntry>(
			_receiveLabels.Select(CloneReceiveLabelEntry).ToArray());

	public bool TryGetReceiveLabels(uint index, out LiquidWalletLabelSet? labels)
	{
		foreach (LiquidWalletReceiveLabelEntry entry in _receiveLabels)
		{
			if (entry.Index == index)
			{
				labels = entry.Labels;
				return true;
			}
		}

		labels = null;
		return false;
	}

	public override string ToString() => nameof(LiquidWalletReplaySnapshot);

	private static LiquidWalletReceiveLabelEntry CloneReceiveLabelEntry(LiquidWalletReceiveLabelEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		return LiquidWalletReceiveLabelEntry.Create(entry.Index, entry.Labels);
	}

	private static LiquidWalletTransactionDelta CloneDelta(LiquidWalletTransactionDelta delta)
	{
		ArgumentNullException.ThrowIfNull(delta);
		return LiquidWalletTransactionDelta.Create(
			delta.TransactionId,
			delta.GetSpentOutPoints(),
			delta.GetCreatedOutputs());
	}

	private static LiquidWalletReplayConfirmation CloneConfirmation(
		LiquidWalletReplayConfirmation confirmation)
	{
		ArgumentNullException.ThrowIfNull(confirmation);
		return LiquidWalletReplayConfirmation.Create(
			confirmation.TransactionId,
			confirmation.Confirmation);
	}
}
