using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Transactions;

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

	public override string ToString() => nameof(ElementsExpectationBoundRawTransactionBatch);
}
