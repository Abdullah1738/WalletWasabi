namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable container for one caller-signed Liquid transaction: the
/// finalized confidential transaction bytes as lowercase hex (the analogue
/// of the native <c>FinalizedOrdinaryTransaction.serialize_for_broadcast()</c>
/// output), the non-witness transaction id as canonical 64-character
/// lowercase hex when the signer reports it (the empty string when it does
/// not), the manifest binding, and the source revision. This type is a
/// container only: it asserts that a caller-supplied signer returned bytes,
/// not that the bytes are valid, signed correctly, or broadcast-acceptable.
/// No validation of the transaction is performed by this slice. No retry, no
/// fallback, no caching, and no formatting beyond the fail-closed hex shape
/// check.
/// </summary>
public sealed class LiquidWalletUiSignedTransaction
{
	private LiquidWalletUiSignedTransaction(
		string signedTransactionHex,
		string transactionIdHex,
		string networkManifestId,
		ulong sourceRevision)
	{
		SignedTransactionHex = signedTransactionHex;
		TransactionIdHex = transactionIdHex;
		NetworkManifestId = networkManifestId;
		SourceRevision = sourceRevision;
	}

	public string SignedTransactionHex { get; }
	public string TransactionIdHex { get; }
	public string NetworkManifestId { get; }
	public ulong SourceRevision { get; }

	public static LiquidWalletUiSignedTransaction Create(
		string networkManifestId,
		ulong sourceRevision,
		string signedTransactionHex,
		string transactionIdHex)
	{
		ArgumentNullException.ThrowIfNull(networkManifestId);
		ArgumentNullException.ThrowIfNull(signedTransactionHex);
		ArgumentNullException.ThrowIfNull(transactionIdHex);

		if (!IsWellFormedHex(signedTransactionHex) || signedTransactionHex.Length == 0)
		{
			throw new ArgumentException(
				"A non-empty, even-length, lowercase-hex signed Liquid transaction is required.",
				nameof(signedTransactionHex));
		}
		if (transactionIdHex.Length != 0 &&
			(transactionIdHex.Length != 64 || !IsWellFormedHex(transactionIdHex)))
		{
			throw new ArgumentException(
				"A Liquid transaction id must be empty or exactly 64 lowercase hexadecimal characters.",
				nameof(transactionIdHex));
		}

		return new LiquidWalletUiSignedTransaction(
			signedTransactionHex,
			transactionIdHex,
			networkManifestId,
			sourceRevision);
	}

	private static bool IsWellFormedHex(string value)
	{
		if (value.Length % 2 != 0)
		{
			return false;
		}
		foreach (char character in value)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				return false;
			}
		}
		return true;
	}
}
