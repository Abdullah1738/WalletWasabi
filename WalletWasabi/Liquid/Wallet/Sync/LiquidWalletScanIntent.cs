using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// One validated caller-supplied candidate transaction-association row: a
/// nonzero <see cref="LiquidTransactionId"/> plus an optional confirming
/// block-hash hint. The caller produces one row per (watched script,
/// transaction) association its own scanning policy determined relevant; how
/// the caller determined relevance (a block-filter match, an address-index
/// hit, a mempool hint) lives entirely outside this layer. A non-null
/// <see cref="BlockHash"/> is a caller hint passed through to
/// <see cref="LiquidWalletSyncBatchPlanner.FetchIntent.BlockHash"/>, not a
/// block-membership, currentness, or broadcast proof. Construction validates
/// shape only and carries no chain, currentness, or broadcast authority.
/// </summary>
internal sealed record LiquidWalletScanIntent
{
	private const int CanonicalHashLength = 64;

	private LiquidWalletScanIntent(LiquidTransactionId transactionId, string? blockHash)
	{
		TransactionId = transactionId;
		BlockHash = blockHash;
	}

	public LiquidTransactionId TransactionId { get; }
	public string? BlockHash { get; }

	public static LiquidWalletScanIntent Create(LiquidTransactionId transactionId, string? blockHash)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		if (transactionId.IsZero)
		{
			throw new ArgumentException(
				"A nonzero Liquid transaction identifier is required.",
				nameof(transactionId));
		}

		if (blockHash is not null)
		{
			// The LiquidConfirmation.Create block-hash shape rules, reused
			// unchanged: exactly 64 ASCII lowercase hex characters with at
			// least one nonzero digit.
			if (blockHash.Length != CanonicalHashLength)
			{
				throw new ArgumentException(
					"A canonical nonzero 32-byte Liquid block hash is required.",
					nameof(blockHash));
			}

			bool hasNonzeroDigit = false;
			foreach (char character in blockHash)
			{
				if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
				{
					throw new ArgumentException(
						"A canonical nonzero 32-byte Liquid block hash is required.",
						nameof(blockHash));
				}

				hasNonzeroDigit |= character != '0';
			}

			if (!hasNonzeroDigit)
			{
				throw new ArgumentException(
					"A nonzero Liquid block hash is required.",
					nameof(blockHash));
			}
		}

		return new LiquidWalletScanIntent(transactionId, blockHash);
	}

	public override string ToString() => nameof(LiquidWalletScanIntent);
}
