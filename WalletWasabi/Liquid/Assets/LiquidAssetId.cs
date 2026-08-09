namespace WalletWasabi.Liquid.Assets;

internal sealed record LiquidAssetId
{
	private const int CanonicalHexLength = 64;
	public const int ConsensusByteLength = 32;

	private LiquidAssetId(string canonicalRpcHex)
	{
		CanonicalRpcHex = canonicalRpcHex;
	}

	public string CanonicalRpcHex { get; }

	public static LiquidAssetId ParseRpcHex(string canonicalRpcHex, string? parameterName = null)
	{
		string effectiveParameterName = parameterName ?? nameof(canonicalRpcHex);
		ArgumentNullException.ThrowIfNull(canonicalRpcHex, effectiveParameterName);

		if (canonicalRpcHex.Length != CanonicalHexLength)
		{
			throw new ArgumentException("A canonical 32-byte Liquid asset identifier is required.", effectiveParameterName);
		}

		bool hasNonzeroDigit = false;
		foreach (char character in canonicalRpcHex)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				throw new ArgumentException("A canonical 32-byte Liquid asset identifier is required.", effectiveParameterName);
			}

			hasNonzeroDigit |= character != '0';
		}

		if (!hasNonzeroDigit)
		{
			throw new ArgumentException("A nonzero Liquid asset identifier is required.", effectiveParameterName);
		}

		return new LiquidAssetId(canonicalRpcHex);
	}

	/// <summary>
	/// Parses the byte order used by Elements transaction encoding and
	/// <c>rust-elements::AssetId::to_byte_array</c>. This order is the reverse of
	/// the canonical RPC/display hex representation.
	/// </summary>
	public static LiquidAssetId ParseConsensusBytes(ReadOnlySpan<byte> consensusBytes, string? parameterName = null)
	{
		string effectiveParameterName = parameterName ?? nameof(consensusBytes);
		if (consensusBytes.Length != ConsensusByteLength)
		{
			throw new ArgumentException("An exact nonzero 32-byte Liquid asset identifier is required.", effectiveParameterName);
		}

		bool hasNonzeroByte = false;
		Span<char> canonicalRpcHex = stackalloc char[CanonicalHexLength];
		for (int consensusIndex = 0; consensusIndex < consensusBytes.Length; consensusIndex++)
		{
			byte value = consensusBytes[consensusIndex];
			hasNonzeroByte |= value != 0;

			int rpcHexIndex = (ConsensusByteLength - 1 - consensusIndex) * 2;
			canonicalRpcHex[rpcHexIndex] = EncodeLowerHexNibble(value >> 4);
			canonicalRpcHex[rpcHexIndex + 1] = EncodeLowerHexNibble(value & 0x0f);
		}

		if (!hasNonzeroByte)
		{
			throw new ArgumentException("An exact nonzero 32-byte Liquid asset identifier is required.", effectiveParameterName);
		}

		return new LiquidAssetId(new string(canonicalRpcHex));
	}

	/// <summary>
	/// Returns a new byte array in the order used by Elements transaction
	/// encoding and <c>rust-elements::AssetId::to_byte_array</c>.
	/// </summary>
	public byte[] ToConsensusBytes()
	{
		byte[] consensusBytes = new byte[ConsensusByteLength];
		WriteConsensusBytes(consensusBytes);
		return consensusBytes;
	}

	/// <summary>
	/// Writes the exact 32-byte Elements transaction-encoding representation.
	/// </summary>
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

	private static char EncodeLowerHexNibble(int value) =>
		(char)(value < 10 ? '0' + value : 'a' + value - 10);

	private static int DecodeLowerHexNibble(char value) =>
		value <= '9' ? value - '0' : value - 'a' + 10;

	public override string ToString() => CanonicalRpcHex;
}
