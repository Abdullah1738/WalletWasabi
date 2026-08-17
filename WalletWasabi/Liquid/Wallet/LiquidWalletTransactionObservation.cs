using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// Immutable public facts shaped like one independently validated Liquid
/// transaction observation and any owned confidential-output observations
/// grouped with it. Construction checks only the normalized identity, binding,
/// ordering, and bounds represented here; it does not prove transaction
/// encoding, amount proofs, ownership, or native validation and carries no
/// chain, wallet-state, balance-credit, persistence, signing, or broadcast
/// authority.
/// </summary>
internal sealed class LiquidWalletTransactionObservation : IEquatable<LiquidWalletTransactionObservation>
{
	private const int MaxInputCount = 102_298;
	private const int MaxOwnedOutputCount = 9_279;

	private readonly LiquidTransactionId _transactionId;
	private readonly LiquidTransactionWitnessBinding _transactionWitnessBinding;
	private readonly LiquidOutPoint[] _inputs;
	private readonly LiquidOwnedOutputObservation[] _ownedOutputs;

	private LiquidWalletTransactionObservation(
		LiquidTransactionId transactionId,
		LiquidTransactionWitnessBinding transactionWitnessBinding,
		LiquidOutPoint[] inputs,
		LiquidOwnedOutputObservation[] ownedOutputs)
	{
		_transactionId = transactionId;
		_transactionWitnessBinding = transactionWitnessBinding;
		_inputs = inputs;
		_ownedOutputs = ownedOutputs;
	}

	public int InputCount => _inputs.Length;
	public int OwnedOutputCount => _ownedOutputs.Length;

	public static LiquidWalletTransactionObservation Create(
		ReadOnlySpan<byte> transactionIdConsensusBytes,
		ReadOnlySpan<byte> transactionWitnessBinding,
		IReadOnlyList<LiquidOutPoint> inputs,
		IReadOnlyList<LiquidOwnedOutputObservation> ownedOutputs)
	{
		ArgumentNullException.ThrowIfNull(inputs);
		ArgumentNullException.ThrowIfNull(ownedOutputs);

		int inputCount = inputs.Count;
		int ownedOutputCount = ownedOutputs.Count;
		if (inputCount == 0)
		{
			throw new ArgumentException(
				"A transaction observation requires at least one input.",
				nameof(inputs));
		}
		if (inputCount > MaxInputCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(inputs),
				"The transaction observation input limit was exceeded.");
		}
		if (ownedOutputCount > MaxOwnedOutputCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(ownedOutputs),
				"The transaction observation owned-output limit was exceeded.");
		}

		LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(
			transactionIdConsensusBytes,
			nameof(transactionIdConsensusBytes));
		if (transactionId.IsZero)
		{
			throw new ArgumentException(
				"A nonzero Liquid transaction identifier is required.",
				nameof(transactionIdConsensusBytes));
		}

		LiquidTransactionWitnessBinding witnessBinding =
			LiquidTransactionWitnessBinding.Create(transactionWitnessBinding);
		var copiedInputs = new LiquidOutPoint[inputCount];
		var uniqueInputs = new HashSet<LiquidOutPoint>();
		for (int index = 0; index < inputCount; index++)
		{
			LiquidOutPoint input = inputs[index];
			ArgumentNullException.ThrowIfNull(input, nameof(inputs));
			if (!uniqueInputs.Add(input))
			{
				throw new ArgumentException(
					"A transaction observation cannot repeat an input outpoint.",
					nameof(inputs));
			}

			copiedInputs[index] = input;
		}

		var copiedOwnedOutputs = new LiquidOwnedOutputObservation[ownedOutputCount];
		uint previousOutputIndex = 0;
		for (int index = 0; index < ownedOutputCount; index++)
		{
			LiquidOwnedOutputObservation ownedOutput = ownedOutputs[index];
			ArgumentNullException.ThrowIfNull(ownedOutput, nameof(ownedOutputs));
			if (!ownedOutput.MatchesTransactionId(transactionId))
			{
				throw new ArgumentException(
					"Every owned output must match the observed transaction identifier.",
					nameof(ownedOutputs));
			}
			if (!ownedOutput.MatchesTransactionWitnessBinding(witnessBinding))
			{
				throw new ArgumentException(
					"Every owned output must match the observed transaction binding.",
					nameof(ownedOutputs));
			}
			if (index != 0 && ownedOutput.OutputIndex <= previousOutputIndex)
			{
				throw new ArgumentException(
					"Owned outputs must have unique, strictly ascending output indices.",
					nameof(ownedOutputs));
			}

			previousOutputIndex = ownedOutput.OutputIndex;
			copiedOwnedOutputs[index] = ownedOutput;
		}

		return new LiquidWalletTransactionObservation(
			transactionId,
			witnessBinding,
			copiedInputs,
			copiedOwnedOutputs);
	}

	public byte[] GetTransactionIdConsensusBytes() => _transactionId.ToConsensusBytes();

	public byte[] GetTransactionWitnessBinding() => _transactionWitnessBinding.GetBytes();

	public IReadOnlyList<LiquidOutPoint> GetInputs() =>
		new ReadOnlyCollection<LiquidOutPoint>([.. _inputs]);

	public IReadOnlyList<LiquidOwnedOutputObservation> GetOwnedOutputs() =>
		new ReadOnlyCollection<LiquidOwnedOutputObservation>([.. _ownedOutputs]);

	public bool Equals(LiquidWalletTransactionObservation? other) =>
		other is not null &&
		_transactionId == other._transactionId &&
		_transactionWitnessBinding.Equals(other._transactionWitnessBinding) &&
		_inputs.AsSpan().SequenceEqual(other._inputs) &&
		_ownedOutputs.AsSpan().SequenceEqual(other._ownedOutputs);

	public override bool Equals(object? obj) => Equals(obj as LiquidWalletTransactionObservation);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(_transactionId);
		hash.Add(_transactionWitnessBinding);
		foreach (LiquidOutPoint input in _inputs)
		{
			hash.Add(input);
		}
		foreach (LiquidOwnedOutputObservation ownedOutput in _ownedOutputs)
		{
			hash.Add(ownedOutput);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidWalletTransactionObservation);
}
