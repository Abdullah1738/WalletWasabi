using System.Collections.ObjectModel;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// Immutable source-only container for one ordered set of managed Liquid
/// transaction observations. Construction preserves only observation order
/// and aggregate bounds; it carries no native-validation, wallet-state,
/// persistence, signing, broadcast, or chain authority.
/// </summary>
internal sealed class LiquidWalletObservationBatch : IEquatable<LiquidWalletObservationBatch>
{
	// One observation per refresh-selected candidate; sized to ElementsRpcClient.MaxRefreshSelectedCandidates
	// (8_192) so a full bounded rescan window passes and the selection cap is the only gate.
	private const int MaxTransactionCount = 8_192;
	private const int MaxAggregateInputCount = 1_636_801;
	private const int MaxAggregateOwnedOutputCount = 148_470;

	private readonly LiquidWalletTransactionObservation[] _transactions;

	private LiquidWalletObservationBatch(LiquidWalletTransactionObservation[] transactions, int ownedOutputCount)
	{
		_transactions = transactions;
		OwnedOutputCount = ownedOutputCount;
	}

	public int TransactionCount => _transactions.Length;
	public int OwnedOutputCount { get; }
	public bool IsEmpty => TransactionCount == 0;

	public static LiquidWalletObservationBatch Create(IReadOnlyList<LiquidWalletTransactionObservation> transactions)
	{
		ArgumentNullException.ThrowIfNull(transactions);

		int transactionCount = transactions.Count;
		if (transactionCount < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(transactions),
				"A nonnegative wallet observation transaction count is required.");
		}
		if (transactionCount > MaxTransactionCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(transactions),
				"The wallet observation transaction limit was exceeded.");
		}

		var copiedTransactions = new LiquidWalletTransactionObservation[transactionCount];
		int aggregateInputCount = 0;
		int aggregateOwnedOutputCount = 0;
		byte[]? previousTransactionId = null;
		for (int index = 0; index < transactionCount; index++)
		{
			LiquidWalletTransactionObservation transaction = transactions[index];
			ArgumentNullException.ThrowIfNull(transaction, nameof(transactions));
			copiedTransactions[index] = transaction;

			aggregateInputCount = checked(aggregateInputCount + transaction.InputCount);
			if (aggregateInputCount > MaxAggregateInputCount)
			{
				throw new ArgumentOutOfRangeException(
					nameof(transactions),
					"The wallet observation aggregate input limit was exceeded.");
			}

			aggregateOwnedOutputCount = checked(
				aggregateOwnedOutputCount + transaction.OwnedOutputCount);
			if (aggregateOwnedOutputCount > MaxAggregateOwnedOutputCount)
			{
				throw new ArgumentOutOfRangeException(
					nameof(transactions),
					"The wallet observation aggregate owned-output limit was exceeded.");
			}

			byte[] transactionId = transaction.GetTransactionIdConsensusBytes();
			if (previousTransactionId is not null)
			{
				int comparison = 0;
				for (int byteIndex = 0; byteIndex < transactionId.Length; byteIndex++)
				{
					if (previousTransactionId[byteIndex] < transactionId[byteIndex])
					{
						comparison = -1;
						break;
					}
					if (previousTransactionId[byteIndex] > transactionId[byteIndex])
					{
						comparison = 1;
						break;
					}
				}
				if (comparison >= 0)
				{
					throw new ArgumentException(
						"Wallet observation transactions must have unique, strictly ascending consensus identifiers.",
						nameof(transactions));
				}
			}
			previousTransactionId = transactionId;
		}

		return new LiquidWalletObservationBatch(copiedTransactions, aggregateOwnedOutputCount);
	}

	public IReadOnlyList<LiquidWalletTransactionObservation> GetTransactions()
	{
		LiquidWalletTransactionObservation[] source = _transactions;
		var copy = new LiquidWalletTransactionObservation[source.Length];
		Array.Copy(source, copy, source.Length);
		return new ReadOnlyCollection<LiquidWalletTransactionObservation>(copy);
	}

	public bool Equals(LiquidWalletObservationBatch? other)
	{
		if (other is null)
		{
			return false;
		}

		LiquidWalletTransactionObservation[] left = _transactions;
		LiquidWalletTransactionObservation[] right = other._transactions;
		if (left.Length != right.Length)
		{
			return false;
		}

		for (int index = 0; index < left.Length; index++)
		{
			if (!left[index].Equals(right[index]))
			{
				return false;
			}
		}

		return true;
	}

	public override bool Equals(object? obj) => Equals(obj as LiquidWalletObservationBatch);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		LiquidWalletTransactionObservation[] transactions = _transactions;
		for (int index = 0; index < transactions.Length; index++)
		{
			hash.Add(transactions[index]);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidWalletObservationBatch);
}
