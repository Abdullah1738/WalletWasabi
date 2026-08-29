using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// Thin adapter mapping caller-named raw-transaction fetch intents into
/// bounded, normalized <see cref="ElementsRawTransactionRequest"/> rows for
/// <see cref="ElementsRpcClient.GetExpectationBoundRawTransactionsAsync"/> and
/// pre-parse verification of the returned batch. It performs no transaction
/// parsing and grants no transaction-identity or block-membership authority.
/// </summary>
internal static class LiquidWalletSyncBatchPlanner
{
	// One request per refresh-selected candidate; sized to ElementsRpcClient.MaxRefreshSelectedCandidates
	// (8_192) so a full bounded rescan window passes and the selection cap is the only gate.
	internal const int MaximumRequestCount = 8_192;

	/// <summary>
	/// One caller-named fetch intent: a transaction identifier plus an optional
	/// block hash constraining the node lookup. A block hash does not establish
	/// block membership or currentness authority.
	/// </summary>
	internal sealed record FetchIntent(string TransactionId, string? BlockHash);

	public static ElementsRawTransactionRequest[] CreateRequests(IReadOnlyList<FetchIntent> intents)
	{
		ArgumentNullException.ThrowIfNull(intents);
		int count = intents.Count;
		if (count is < 1 or > MaximumRequestCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(intents),
				$"Between one and {MaximumRequestCount} raw transaction fetch intents are required.");
		}

		var requests = new ElementsRawTransactionRequest[count];
		var transactionIds = new HashSet<string>(StringComparer.Ordinal);
		for (int index = 0; index < count; index++)
		{
			FetchIntent intent = intents[index]
				?? throw new ArgumentException(
					"Every raw transaction fetch intent is required.",
					nameof(intents));
			ElementsRawTransactionRequest request = new ElementsRawTransactionRequest(
					intent.TransactionId,
					intent.BlockHash)
				.Normalize(nameof(intents));
			if (!transactionIds.Add(request.TransactionId))
			{
				throw new ArgumentException(
					"Raw transaction fetch intent identifiers must be unique.",
					nameof(intents));
			}

			requests[index] = request;
		}

		return requests;
	}

	/// <summary>
	/// Verifies exact request-set equality, unique transaction identifiers, and
	/// per-row transaction-identifier/byte consistency before the raw bytes are
	/// handed to the caller's observation builder. No parsing is performed.
	/// </summary>
	public static void Verify(
		ElementsExpectationBoundRawTransactionBatch batch,
		IReadOnlyList<ElementsRawTransactionRequest> requests)
	{
		ArgumentNullException.ThrowIfNull(batch);
		ArgumentNullException.ThrowIfNull(requests);

		var expected = new HashSet<string>(StringComparer.Ordinal);
		for (int index = 0; index < requests.Count; index++)
		{
			ElementsRawTransactionRequest request = requests[index]
				?? throw new ArgumentException(
					"Every raw transaction request is required.",
					nameof(requests));
			if (!expected.Add(request.TransactionId))
			{
				throw new ArgumentException(
					"Raw transaction request identifiers must be unique.",
					nameof(requests));
			}
		}

		IReadOnlyList<ElementsRawTransactionObservation> transactions = batch.GetTransactions();
		if (transactions.Count != expected.Count)
		{
			throw new InvalidOperationException(
				"The expectation-bound raw transaction batch does not match the requested transaction set.");
		}

		var observed = new HashSet<string>(StringComparer.Ordinal);
		for (int index = 0; index < transactions.Count; index++)
		{
			ElementsRawTransactionObservation transaction = transactions[index];
			string transactionId = transaction.Request.TransactionId;
			if (!observed.Add(transactionId) ||
				!expected.Contains(transactionId) ||
				transaction.TransactionByteLength < 1 ||
				transaction.GetTransactionBytes().Length != transaction.TransactionByteLength)
			{
				throw new InvalidOperationException(
					"The expectation-bound raw transaction batch does not match the requested transaction set.");
			}
		}
	}
}
