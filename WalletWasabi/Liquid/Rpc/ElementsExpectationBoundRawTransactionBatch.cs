using System.Collections.ObjectModel;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Wire;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;
using LiquidOrdinaryWalletPlanFundingRow = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingRow;

namespace WalletWasabi.Liquid.Rpc;

public enum ElementsRawTransactionBindingLevel
{
	SelfReportedExactTupleFeeAndGenerationFencedRawBytesOnly = 0,
}

/// <summary>
/// Names one node-self-reported raw transaction lookup. A block hash constrains the RPC lookup but
/// does not establish block membership or currentness authority.
/// </summary>
public sealed record ElementsRawTransactionRequest(string TransactionId, string? BlockHash)
{
	public override string ToString() => nameof(ElementsRawTransactionRequest);

	internal ElementsRawTransactionRequest Normalize(string parameterName)
	{
		LiquidTransactionId transactionId = LiquidTransactionId.ParseRpcHex(TransactionId, parameterName);
		if (transactionId.IsZero)
		{
			throw new ArgumentException("A nonzero Liquid transaction identifier is required.", parameterName);
		}

		string? normalizedBlockHash = BlockHash is null
			? null
			: ElementsNodeStatus.RequireHex32(BlockHash, parameterName);
		return new ElementsRawTransactionRequest(transactionId.CanonicalRpcHex, normalizedBlockHash);
	}
}

/// <summary>
/// Holds an immutable private copy of one node-returned raw transaction. The bytes are not parsed
/// here and are not claimed to match <see cref="ElementsRawTransactionRequest.TransactionId"/>.
/// </summary>
public sealed class ElementsRawTransactionObservation
{
	private readonly byte[] _transactionBytes;

	internal ElementsRawTransactionObservation(
		ElementsRawTransactionRequest request,
		byte[] transactionBytes)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(transactionBytes);
		Request = request;
		_transactionBytes = transactionBytes;
	}

	public ElementsRawTransactionRequest Request { get; }
	public int TransactionByteLength => _transactionBytes.Length;

	public byte[] GetTransactionBytes() => _transactionBytes.ToArray();

	public override string ToString() => nameof(ElementsRawTransactionObservation);
}

/// <summary>
/// Binds raw node bytes to an exact self-reported node identity, fee asset, and unchanged generation
/// observation. This is not artifact qualification, transaction identity validation, block
/// membership, currentness, reservation, or broadcast authority.
/// </summary>
public sealed class ElementsExpectationBoundRawTransactionBatch
{
	private readonly ElementsRawTransactionObservation[] _transactions;

	internal ElementsExpectationBoundRawTransactionBatch(
		ElementsExpectationBoundNodeObservation nodeObservation,
		ElementsRawTransactionObservation[] transactions)
	{
		ArgumentNullException.ThrowIfNull(nodeObservation);
		ArgumentNullException.ThrowIfNull(transactions);
		NodeObservation = nodeObservation;
		_transactions = transactions;
	}

	public ElementsExpectationBoundNodeObservation NodeObservation { get; }
	public int TransactionCount => _transactions.Length;
	public ElementsRawTransactionBindingLevel BindingLevel =>
		ElementsRawTransactionBindingLevel.SelfReportedExactTupleFeeAndGenerationFencedRawBytesOnly;
	public bool HasExactGenerationFenceObservation => true;
	public bool HasEffectiveFeeAssetObservation => true;
	public bool HasTransactionIdValidation => false;
	public bool HasBlockMembershipAuthority => false;
	public bool HasArtifactSourceAttestation => false;
	public bool HasRuntimeQualification => false;
	public bool HasCurrentnessAuthority => false;
	public bool HasReservationAuthority => false;
	public bool HasBroadcastAuthority => false;

	public IReadOnlyList<ElementsRawTransactionObservation> GetTransactions() =>
		new ReadOnlyCollection<ElementsRawTransactionObservation>([.. _transactions]);

	/// <summary>
	/// Copies this batch into one canonical funding row per selected plan entry. Candidate lookup and
	/// previous-transaction membership use only caller and node self-reported identifiers; the copied
	/// bytes remain unparsed and native preparation retains all transaction, identifier, proof,
	/// ownership, and confidential-value authority.
	/// </summary>
	internal bool TryCreateOrdinaryWalletPlanFundingBatch(
		LiquidOrdinaryWalletExactSpendPlan? plan,
		IReadOnlyList<IReadOnlyList<string>?>? previousTransactionIdsBySelectedInput,
		out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
		out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
	{
		fundingBatch = null;
		errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;
		if (plan is null || previousTransactionIdsBySelectedInput is null)
		{
			return RejectFundingComposition(
				LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
				out errorCode);
		}
		string planPeggedAsset = plan.GetPeggedAssetId().CanonicalRpcHex;
		if ((NodeObservation.Expectation is not null &&
				!StringComparer.Ordinal.Equals(NodeObservation.Expectation.PeggedAsset, planPeggedAsset)) ||
			!StringComparer.Ordinal.Equals(NodeObservation.NodeStatus.PeggedAsset, planPeggedAsset) ||
			!StringComparer.Ordinal.Equals(NodeObservation.EffectiveFeeAsset, planPeggedAsset))
		{
			return RejectFundingComposition(
				LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
				out errorCode);
		}

		ReadOnlySpan<LiquidWalletCoinControlEntry> selectedEntries =
			plan.GetSelectedEntriesForWireEncoding();
		int selectedCount = selectedEntries.Length;
		if (previousTransactionIdsBySelectedInput.Count != selectedCount)
		{
			return RejectFundingComposition(
				LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
				out errorCode);
		}

		var transactionsById = new Dictionary<string, ElementsRawTransactionObservation>(
			_transactions.Length,
			StringComparer.Ordinal);
		for (int index = 0; index < _transactions.Length; index++)
		{
			ElementsRawTransactionObservation transaction = _transactions[index];
			if (!transactionsById.TryAdd(transaction.Request.TransactionId, transaction))
			{
				return RejectFundingComposition(
					LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding,
					out errorCode);
			}
		}

		var normalizedPreviousIds = new string[selectedCount][];
		var usedTransactionIds = new HashSet<string>(StringComparer.Ordinal);
		var previousIdsByCandidateId = new Dictionary<string, string[]>(StringComparer.Ordinal);
		int aggregatePreviousCount = 0;
		long aggregateTransactionLength = 0;
		for (int selectedIndex = 0; selectedIndex < selectedCount; selectedIndex++)
		{
			LiquidWalletCoinControlEntry selectedEntry = selectedEntries[selectedIndex];
			string candidateId = selectedEntry.OutPoint.TransactionId.CanonicalRpcHex;
			LiquidConfirmation? confirmation = selectedEntry.Confirmation;
			uint observedBlocks = checked((uint)NodeObservation.Generation.Blocks);
			if (!transactionsById.TryGetValue(
				candidateId,
				out ElementsRawTransactionObservation? candidateTransaction) ||
				!StringComparer.Ordinal.Equals(
					candidateTransaction.Request.BlockHash,
					confirmation?.CanonicalBlockHash) ||
				confirmation is not null &&
					(confirmation.Height > observedBlocks ||
						confirmation.Height == observedBlocks &&
						!StringComparer.Ordinal.Equals(
							confirmation.CanonicalBlockHash,
							NodeObservation.Generation.BestBlockHash)))
			{
				return RejectFundingComposition(
					LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
					out errorCode);
			}
			if (!TryAccumulateTransactionLength(
				ref aggregateTransactionLength,
				candidateTransaction.TransactionByteLength))
			{
				return RejectFundingComposition(
					LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded,
					out errorCode);
			}
			usedTransactionIds.Add(candidateId);

			IReadOnlyList<string>? sourcePreviousIds =
				previousTransactionIdsBySelectedInput[selectedIndex];
			if (sourcePreviousIds is null)
			{
				return RejectFundingComposition(
					LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
					out errorCode);
			}

			int previousCount = sourcePreviousIds.Count;
			if (previousCount < 0 ||
				aggregatePreviousCount >
					LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount - previousCount)
			{
				return RejectFundingComposition(
					LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded,
					out errorCode);
			}
			aggregatePreviousCount += previousCount;

			var rowPreviousIds = new string[previousCount];
			string? priorId = null;
			for (int previousIndex = 0; previousIndex < previousCount; previousIndex++)
			{
				string? sourceId = sourcePreviousIds[previousIndex];
				LiquidTransactionId previousId;
				try
				{
					previousId = LiquidTransactionId.ParseRpcHex(
						sourceId!,
						nameof(previousTransactionIdsBySelectedInput));
				}
				catch (ArgumentException)
				{
					return RejectFundingComposition(
						LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
						out errorCode);
				}

				string normalizedId = previousId.CanonicalRpcHex;
				if (previousId.IsZero ||
					StringComparer.Ordinal.Equals(normalizedId, candidateId) ||
					(priorId is not null && StringComparer.Ordinal.Compare(priorId, normalizedId) >= 0) ||
					!transactionsById.TryGetValue(
						normalizedId,
						out ElementsRawTransactionObservation? previousTransaction))
				{
					return RejectFundingComposition(
						LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
						out errorCode);
				}
				if (!TryAccumulateTransactionLength(
					ref aggregateTransactionLength,
					previousTransaction.TransactionByteLength))
				{
					return RejectFundingComposition(
						LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded,
						out errorCode);
				}

				rowPreviousIds[previousIndex] = normalizedId;
				usedTransactionIds.Add(normalizedId);
				priorId = normalizedId;
			}
			normalizedPreviousIds[selectedIndex] = rowPreviousIds;
			if (previousIdsByCandidateId.TryGetValue(candidateId, out string[]? priorPreviousIds))
			{
				if (!priorPreviousIds.AsSpan().SequenceEqual(rowPreviousIds))
				{
					return RejectFundingComposition(
						LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
						out errorCode);
				}
			}
			else
			{
				previousIdsByCandidateId.Add(candidateId, rowPreviousIds);
			}
		}

		if (usedTransactionIds.Count != transactionsById.Count)
		{
			return RejectFundingComposition(
				LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument,
				out errorCode);
		}

		var rows = new LiquidOrdinaryWalletPlanFundingRow?[selectedCount];
		try
		{
			for (int selectedIndex = 0; selectedIndex < selectedCount; selectedIndex++)
			{
				string candidateId = selectedEntries[selectedIndex].OutPoint.TransactionId.CanonicalRpcHex;
				byte[]? candidateBytes = null;
				byte[][]? previousBytes = null;
				try
				{
					candidateBytes = transactionsById[candidateId].GetTransactionBytes();
					string[] previousIds = normalizedPreviousIds[selectedIndex];
					previousBytes = new byte[previousIds.Length][];
					for (int previousIndex = 0; previousIndex < previousIds.Length; previousIndex++)
					{
						previousBytes[previousIndex] =
							transactionsById[previousIds[previousIndex]].GetTransactionBytes();
					}
					SortTransactionBytes(previousBytes);
					if (!LiquidOrdinaryWalletPlanFundingRow.TryCreate(
						candidateBytes,
						previousBytes,
						out LiquidOrdinaryWalletPlanFundingRow? row,
						out errorCode))
					{
						return false;
					}
					rows[selectedIndex] = row;
				}
				finally
				{
					ClearTransactionBytes(candidateBytes, previousBytes);
				}
			}

			return LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
				plan,
				rows,
				out fundingBatch,
				out errorCode);
		}
		finally
		{
			for (int index = 0; index < rows.Length; index++)
			{
				rows[index]?.Dispose();
			}
			Array.Clear(rows);
		}
	}

	public override string ToString() => nameof(ElementsExpectationBoundRawTransactionBatch);

	private static void SortTransactionBytes(byte[][] transactions)
	{
		for (int index = 1; index < transactions.Length; index++)
		{
			byte[] current = transactions[index];
			int insertionIndex = index;
			while (insertionIndex > 0 &&
				transactions[insertionIndex - 1].AsSpan().SequenceCompareTo(current) > 0)
			{
				transactions[insertionIndex] = transactions[insertionIndex - 1];
				insertionIndex--;
			}
			transactions[insertionIndex] = current;
		}
	}

	private static bool TryAccumulateTransactionLength(ref long aggregateLength, int transactionLength)
	{
		if (transactionLength is < 1 or > LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength ||
			aggregateLength >
				LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength - transactionLength)
		{
			return false;
		}
		aggregateLength += transactionLength;
		return true;
	}

	private static void ClearTransactionBytes(byte[]? candidate, byte[][]? previous)
	{
		if (candidate is not null)
		{
			CryptographicOperations.ZeroMemory(candidate);
		}
		if (previous is not null)
		{
			for (int index = 0; index < previous.Length; index++)
			{
				if (previous[index] is not null)
				{
					CryptographicOperations.ZeroMemory(previous[index]);
				}
			}
			Array.Clear(previous);
		}
	}

	private static bool RejectFundingComposition(
		LiquidOrdinaryWalletPlanWireErrorCode failure,
		out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
	{
		errorCode = failure;
		return false;
	}
}
