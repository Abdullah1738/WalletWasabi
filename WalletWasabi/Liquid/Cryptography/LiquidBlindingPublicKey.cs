using NBitcoin;

namespace WalletWasabi.Liquid.Cryptography;

internal sealed class LiquidBlindingPublicKey : IEquatable<LiquidBlindingPublicKey>
{
	public const int CompressedByteLength = 33;

	private readonly byte[] _compressedPublicKey;

	private LiquidBlindingPublicKey(byte[] compressedPublicKey)
	{
		_compressedPublicKey = compressedPublicKey;
	}

	public static LiquidBlindingPublicKey Create(ReadOnlySpan<byte> compressedPublicKey)
	{
		if (compressedPublicKey.Length != CompressedByteLength || compressedPublicKey[0] is not (0x02 or 0x03))
		{
			throw new ArgumentException(
				"An exact compressed secp256k1 blinding public key is required.",
				nameof(compressedPublicKey));
		}

		byte[] publicKeyBytes = compressedPublicKey.ToArray();
		PubKey publicKey;
		try
		{
			publicKey = new PubKey(publicKeyBytes);
		}
		catch (Exception exception) when (exception is ArgumentException or FormatException)
		{
			throw new ArgumentException(
				"A valid compressed secp256k1 blinding public key is required.",
				nameof(compressedPublicKey));
		}

		if (!publicKey.IsCompressed)
		{
			throw new ArgumentException(
				"A compressed secp256k1 blinding public key is required.",
				nameof(compressedPublicKey));
		}

		return new LiquidBlindingPublicKey(publicKeyBytes);
	}

	public byte[] GetCompressedPublicKey() => [.. _compressedPublicKey];

	public bool Equals(LiquidBlindingPublicKey? other) =>
		other is not null && _compressedPublicKey.AsSpan().SequenceEqual(other._compressedPublicKey);

	public override bool Equals(object? obj) => Equals(obj as LiquidBlindingPublicKey);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (byte value in _compressedPublicKey)
		{
			hash.Add(value);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidBlindingPublicKey);
}
