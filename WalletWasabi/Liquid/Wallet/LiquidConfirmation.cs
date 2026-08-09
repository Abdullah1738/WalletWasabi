namespace WalletWasabi.Liquid.Wallet;

internal sealed record LiquidConfirmation
{
	private const int CanonicalHashLength = 64;

	private LiquidConfirmation(string canonicalBlockHash, uint height)
	{
		CanonicalBlockHash = canonicalBlockHash;
		Height = height;
	}

	public string CanonicalBlockHash { get; }
	public uint Height { get; }

	public static LiquidConfirmation Create(string canonicalBlockHash, uint height)
	{
		ArgumentNullException.ThrowIfNull(canonicalBlockHash);
		if (canonicalBlockHash.Length != CanonicalHashLength)
		{
			throw new ArgumentException("A canonical nonzero 32-byte Liquid block hash is required.", nameof(canonicalBlockHash));
		}

		bool hasNonzeroDigit = false;
		foreach (char character in canonicalBlockHash)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				throw new ArgumentException("A canonical nonzero 32-byte Liquid block hash is required.", nameof(canonicalBlockHash));
			}

			hasNonzeroDigit |= character != '0';
		}

		if (!hasNonzeroDigit)
		{
			throw new ArgumentException("A nonzero Liquid block hash is required.", nameof(canonicalBlockHash));
		}

		return new LiquidConfirmation(canonicalBlockHash, height);
	}

	public override string ToString() => nameof(LiquidConfirmation);
}
