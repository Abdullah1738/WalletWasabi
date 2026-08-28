using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.Rpc;

/// <summary>
/// One node-self-reported input row of a selected refresh candidate. Exactly one of
/// <see cref="IsCoinbase"/> or a canonical nonzero previous transaction identifier is present;
/// never both and never neither. The bytes are not parsed and no ownership or consensus claim is made.
/// </summary>
internal sealed class ElementsWalletRefreshInput
{
	internal ElementsWalletRefreshInput(string? previousTransactionId)
	{
		IsCoinbase = previousTransactionId is null;
		PreviousTransactionId = previousTransactionId;
	}

	public bool IsCoinbase { get; }
	public string? PreviousTransactionId { get; }

	public override string ToString() => nameof(ElementsWalletRefreshInput);
}

/// <summary>
/// Immutable typed metadata for one selected refresh candidate: its canonical node-reported
/// transaction identifier, optional discovered recent-block binding, and the complete node-reported
/// input row. The ordinal-distinct previous transaction row retains first node-reported input order.
/// </summary>
internal sealed class ElementsWalletRefreshCandidate
{
	private readonly string[] _previousTransactionIds;
	private readonly ElementsWalletRefreshInput[] _inputs;

	internal ElementsWalletRefreshCandidate(
		string transactionId,
		string? blockHash,
		uint? blockHeight,
		ElementsWalletRefreshInput[] inputs,
		string[] previousTransactionIds)
	{
		TransactionId = transactionId;
		BlockHash = blockHash;
		BlockHeight = blockHeight;
		_inputs = inputs;
		_previousTransactionIds = previousTransactionIds;
	}

	public string TransactionId { get; }
	public string? BlockHash { get; }
	public uint? BlockHeight { get; }
	public IReadOnlyList<ElementsWalletRefreshInput> Inputs =>
		new ReadOnlyCollection<ElementsWalletRefreshInput>(_inputs);
	public IReadOnlyList<string> PreviousTransactionIds =>
		new ReadOnlyCollection<string>(_previousTransactionIds);

	public override string ToString() => nameof(ElementsWalletRefreshCandidate);
}

/// <summary>
/// Owns one private copy of node-returned raw transaction bytes. Disposal zeroes the owned buffer.
/// The bytes remain opaque; no transaction identity, block membership, or consensus claim is made.
/// </summary>
internal sealed class ElementsWalletRefreshRawTransaction : IDisposable
{
	private byte[]? _transactionBytes;

	internal ElementsWalletRefreshRawTransaction(string transactionId, byte[] transactionBytes)
	{
		TransactionId = transactionId;
		_transactionBytes = transactionBytes.ToArray();
	}

	public string TransactionId { get; }
	public int ByteLength => _transactionBytes?.Length ?? 0;

	public byte[] GetTransactionBytes()
	{
		ObjectDisposedException.ThrowIf(_transactionBytes is null, this);
		return _transactionBytes.ToArray();
	}

	public void Dispose()
	{
		if (_transactionBytes is { } bytes)
		{
			CryptographicOperations.ZeroMemory(bytes);
			_transactionBytes = null;
		}
	}

	public override string ToString() => nameof(ElementsWalletRefreshRawTransaction);
}

/// <summary>
/// Carries the complete generation-fenced refresh acquisition: the existing expectation-bound node
/// observation, the selected candidate metadata in deterministic order, and the raw candidate and
/// dependency bytes. This is a strict bounded projection of node self-report; it grants no
/// transaction-id validation, block-membership, currentness, or consensus authority.
/// </summary>
internal sealed class ElementsWalletRefreshObservation : IDisposable
{
	private readonly ElementsWalletRefreshCandidate[] _candidates;
	private readonly ElementsWalletRefreshRawTransaction[] _rawTransactions;
	private bool _disposed;

	internal ElementsWalletRefreshObservation(
		ElementsExpectationBoundNodeObservation nodeObservation,
		ElementsWalletRefreshCandidate[] candidates,
		ElementsWalletRefreshRawTransaction[] rawTransactions)
	{
		NodeObservation = nodeObservation;
		_candidates = candidates;
		_rawTransactions = rawTransactions;
	}

	public ElementsExpectationBoundNodeObservation NodeObservation { get; }
	public IReadOnlyList<ElementsWalletRefreshCandidate> Candidates =>
		new ReadOnlyCollection<ElementsWalletRefreshCandidate>(_candidates);
	public IReadOnlyList<ElementsWalletRefreshRawTransaction> RawTransactions =>
		new ReadOnlyCollection<ElementsWalletRefreshRawTransaction>(_rawTransactions);

	public bool HasExactGenerationFenceObservation => true;
	public bool HasEffectiveFeeAssetObservation => true;
	public bool HasTransactionIdValidation => false;
	public bool HasBlockMembershipAuthority => false;
	public bool HasCurrentnessAuthority => false;
	public bool HasConsensusClaim => false;

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (ElementsWalletRefreshRawTransaction rawTransaction in _rawTransactions)
		{
			rawTransaction.Dispose();
		}
	}

	public override string ToString() => nameof(ElementsWalletRefreshObservation);
}
