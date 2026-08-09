namespace WalletWasabi.Liquid.Assets;

internal sealed record LiquidAssetId
{
	private const int CanonicalHexLength = 64;

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

	public override string ToString() => CanonicalRpcHex;
}
