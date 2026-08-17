using System.Buffers.Binary;

namespace WalletWasabi.Liquid.Transactions;

internal sealed record LiquidOutPoint
{
	public const int ConsensusByteLength = LiquidTransactionId.ConsensusByteLength + sizeof(uint);
	public const uint MaxSpendableOutputIndex = (1u << 30) - 1;

	private LiquidOutPoint(LiquidTransactionId transactionId, uint outputIndex)
	{
		TransactionId = transactionId;
		OutputIndex = outputIndex;
	}

	public LiquidTransactionId TransactionId { get; }
	public uint OutputIndex { get; }

	public static LiquidOutPoint CreateSpendable(LiquidTransactionId transactionId, uint outputIndex)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		if (transactionId.IsZero)
		{
			throw new ArgumentException("A non-coinbase Liquid transaction identifier is required.", nameof(transactionId));
		}
		if (outputIndex > MaxSpendableOutputIndex)
		{
			throw new ArgumentOutOfRangeException(
				nameof(outputIndex),
				"A Liquid output index without input flag bits is required.");
		}

		return new LiquidOutPoint(transactionId, outputIndex);
	}

	public static LiquidOutPoint ParseSpendableConsensusBytes(ReadOnlySpan<byte> consensusBytes, string? parameterName = null)
	{
		string effectiveParameterName = parameterName ?? nameof(consensusBytes);
		if (consensusBytes.Length != ConsensusByteLength)
		{
			throw new ArgumentException("An exact 36-byte spendable Liquid outpoint is required.", effectiveParameterName);
		}

		LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(
			consensusBytes[..LiquidTransactionId.ConsensusByteLength],
			effectiveParameterName);
		uint outputIndex = BinaryPrimitives.ReadUInt32LittleEndian(
			consensusBytes[LiquidTransactionId.ConsensusByteLength..]);

		try
		{
			return CreateSpendable(transactionId, outputIndex);
		}
		catch (ArgumentException exception)
		{
			throw new ArgumentException("A valid spendable Liquid outpoint is required.", effectiveParameterName, exception);
		}
	}

	public byte[] ToConsensusBytes()
	{
		byte[] consensusBytes = new byte[ConsensusByteLength];
		WriteConsensusBytes(consensusBytes);
		return consensusBytes;
	}

	public void WriteConsensusBytes(Span<byte> destination)
	{
		if (destination.Length != ConsensusByteLength)
		{
			throw new ArgumentException("An exact 36-byte destination is required.", nameof(destination));
		}

		TransactionId.WriteConsensusBytes(destination[..LiquidTransactionId.ConsensusByteLength]);
		BinaryPrimitives.WriteUInt32LittleEndian(
			destination[LiquidTransactionId.ConsensusByteLength..],
			OutputIndex);
	}

	public override string ToString() => nameof(LiquidOutPoint);
}
