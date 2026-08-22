using System.Collections.ObjectModel;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 section 2; 2026-08-21 amendment): the public,
/// immutable, non-secret request for one ordinary Liquid send execution. It carries only
/// presentation-safe values: the wallet name, the selected outpoint hex strings, the
/// confidential destination, the destination asset id and atomic units, the explicit
/// pegged-asset fee in atomic units, the expected wallet revision (the caller's freshness
/// fence), and the previous-transaction-id dependency rows the landed funding composition
/// requires. It carries no key/context bytes, source epoch, descriptor, derivation bound,
/// SLIP-77 material, RPC client, credentials, node expectation, manifest, signer, funding
/// transaction bytes, wallet data directory, or cancellation source. The wallet data directory
/// is single-sourced from the authenticated session (the command service resolves the session
/// by <see cref="WalletName"/> and supplies its directory); the request carries no copy.
/// Collections are defensively copied and exposed read-only. No secret ever crosses this
/// boundary.
/// </summary>
public sealed class LiquidWalletUiSendExecutionRequest
{
	public LiquidWalletUiSendExecutionRequest(
		string walletName,
		IReadOnlyList<string> selectedOutPointHexes,
		string confidentialDestinationAddress,
		string destinationAssetIdHex,
		long destinationAtomicUnits,
		long explicitFeeAtomicUnits,
		ulong expectedRevision,
		IReadOnlyList<IReadOnlyList<string>?> previousTransactionIdsBySelectedInput)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletName);
		ArgumentNullException.ThrowIfNull(selectedOutPointHexes);
		ArgumentException.ThrowIfNullOrEmpty(confidentialDestinationAddress);
		ArgumentException.ThrowIfNullOrEmpty(destinationAssetIdHex);
		ArgumentNullException.ThrowIfNull(previousTransactionIdsBySelectedInput);
		if (selectedOutPointHexes.Count == 0)
		{
			throw new ArgumentException(
				"A Liquid send execution requires at least one selected outpoint.",
				nameof(selectedOutPointHexes));
		}
		if (selectedOutPointHexes.Count != previousTransactionIdsBySelectedInput.Count)
		{
			throw new ArgumentException(
				"The previous-transaction-id dependency rows must align one-to-one with the selected outpoints.",
				nameof(previousTransactionIdsBySelectedInput));
		}
		if (destinationAtomicUnits <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(destinationAtomicUnits),
				"A positive Liquid destination amount is required.");
		}
		if (explicitFeeAtomicUnits <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(explicitFeeAtomicUnits),
				"A positive Liquid explicit fee is required.");
		}

		WalletName = walletName;
		SelectedOutPointHexes = new ReadOnlyCollection<string>([.. selectedOutPointHexes]);
		ConfidentialDestinationAddress = confidentialDestinationAddress;
		DestinationAssetIdHex = destinationAssetIdHex;
		DestinationAtomicUnits = destinationAtomicUnits;
		ExplicitFeeAtomicUnits = explicitFeeAtomicUnits;
		ExpectedRevision = expectedRevision;

		var rows = new IReadOnlyList<string>?[previousTransactionIdsBySelectedInput.Count];
		for (int index = 0; index < rows.Length; index++)
		{
			IReadOnlyList<string>? row = previousTransactionIdsBySelectedInput[index];
			rows[index] = row is null ? null : new ReadOnlyCollection<string>([.. row]);
		}
		PreviousTransactionIdsBySelectedInput =
			new ReadOnlyCollection<IReadOnlyList<string>?>(rows);
	}

	public string WalletName { get; }
	public IReadOnlyList<string> SelectedOutPointHexes { get; }
	public string ConfidentialDestinationAddress { get; }
	public string DestinationAssetIdHex { get; }
	public long DestinationAtomicUnits { get; }
	public long ExplicitFeeAtomicUnits { get; }
	public ulong ExpectedRevision { get; }

	/// <summary>
	/// One nullable row per selected outpoint, in the same order: the previous-transaction-id
	/// dependency list the landed funding composition requires for that input. A
	/// <see langword="null"/> row is passed through to the landed funding composition, which
	/// rejects it fail-closed when a row is required. The rows carry transaction-id hex strings
	/// only; no transaction bytes cross this request.
	/// </summary>
	public IReadOnlyList<IReadOnlyList<string>?> PreviousTransactionIdsBySelectedInput { get; }
}
