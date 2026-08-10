namespace WalletWasabi.Liquid.Transactions;

/// <summary>
/// A caller-asserted opaque value intended to carry the single SHA-256 binding over
/// exact witness-inclusive Liquid transaction bytes.
/// Construction only validates the length and copies the value; it does not hash,
/// parse, or verify transaction bytes. This is not a transaction identifier or wtxid.
/// </summary>
internal sealed class LiquidTransactionWitnessBinding : IEquatable<LiquidTransactionWitnessBinding>
{
	public const int ByteLength = 32;

	private readonly byte[] _bytes;

	private LiquidTransactionWitnessBinding(byte[] bytes)
	{
		_bytes = bytes;
	}

	public static LiquidTransactionWitnessBinding Create(ReadOnlySpan<byte> callerAssertedBinding)
	{
		if (callerAssertedBinding.Length != ByteLength)
		{
			throw new ArgumentException(
				"An exact 32-byte full-witness transaction binding is required.",
				nameof(callerAssertedBinding));
		}

		return new LiquidTransactionWitnessBinding(callerAssertedBinding.ToArray());
	}

	public byte[] GetBytes() => [.. _bytes];

	public bool Equals(LiquidTransactionWitnessBinding? other) =>
		other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

	public override bool Equals(object? obj) => Equals(obj as LiquidTransactionWitnessBinding);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (byte value in _bytes)
		{
			hash.Add(value);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidTransactionWitnessBinding);
}
