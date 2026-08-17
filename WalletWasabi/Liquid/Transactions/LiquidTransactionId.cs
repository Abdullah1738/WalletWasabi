namespace WalletWasabi.Liquid.Transactions;

internal sealed record LiquidTransactionId
{
	private const int CanonicalHexLength = 64;
	public const int ConsensusByteLength = 32;

	private LiquidTransactionId(string canonicalRpcHex, bool isZero)
	{
		CanonicalRpcHex = canonicalRpcHex;
		IsZero = isZero;
	}

	public string CanonicalRpcHex { get; }
	public bool IsZero { get; }

	public static LiquidTransactionId ParseRpcHex(string canonicalRpcHex, string? parameterName = null)
	{
		string effectiveParameterName = parameterName ?? nameof(canonicalRpcHex);
		ArgumentNullException.ThrowIfNull(canonicalRpcHex, effectiveParameterName);
		if (canonicalRpcHex.Length != CanonicalHexLength)
		{
			throw new ArgumentException("A canonical 32-byte Liquid transaction identifier is required.", effectiveParameterName);
		}

		bool isZero = true;
		foreach (char character in canonicalRpcHex)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				throw new ArgumentException("A canonical 32-byte Liquid transaction identifier is required.", effectiveParameterName);
			}

			isZero &= character == '0';
		}

		return new LiquidTransactionId(canonicalRpcHex, isZero);
	}

	public static LiquidTransactionId ParseConsensusBytes(ReadOnlySpan<byte> consensusBytes, string? parameterName = null)
	{
		string effectiveParameterName = parameterName ?? nameof(consensusBytes);
		if (consensusBytes.Length != ConsensusByteLength)
		{
			throw new ArgumentException("An exact 32-byte Liquid transaction identifier is required.", effectiveParameterName);
		}

		bool isZero = true;
		Span<char> canonicalRpcHex = stackalloc char[CanonicalHexLength];
		for (int consensusIndex = 0; consensusIndex < consensusBytes.Length; consensusIndex++)
		{
			byte value = consensusBytes[consensusIndex];
			isZero &= value == 0;
			int rpcHexIndex = (ConsensusByteLength - 1 - consensusIndex) * 2;
			canonicalRpcHex[rpcHexIndex] = EncodeLowerHexNibble(value >> 4);
			canonicalRpcHex[rpcHexIndex + 1] = EncodeLowerHexNibble(value & 0x0f);
		}

		return new LiquidTransactionId(new string(canonicalRpcHex), isZero);
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
			throw new ArgumentException("An exact 32-byte destination is required.", nameof(destination));
		}

		for (int consensusIndex = 0; consensusIndex < destination.Length; consensusIndex++)
		{
			int rpcHexIndex = (ConsensusByteLength - 1 - consensusIndex) * 2;
			destination[consensusIndex] = (byte)(
				(DecodeLowerHexNibble(CanonicalRpcHex[rpcHexIndex]) << 4) |
				DecodeLowerHexNibble(CanonicalRpcHex[rpcHexIndex + 1]));
		}
	}

	public override string ToString() => nameof(LiquidTransactionId);

	private static char EncodeLowerHexNibble(int value) =>
		(char)(value < 10 ? '0' + value : 'a' + value - 10);

	private static int DecodeLowerHexNibble(char value) =>
		value <= '9' ? value - '0' : value - 'a' + 10;
}
