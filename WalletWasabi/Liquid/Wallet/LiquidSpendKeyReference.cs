using NBitcoin;

namespace WalletWasabi.Liquid.Wallet;

internal enum LiquidKeyBranch : byte
{
	External = 0,
	Internal = 1,
}

internal sealed class LiquidSpendKeyReference : IEquatable<LiquidSpendKeyReference>
{
	internal const uint MaximumIndex = 100_000;

	private readonly byte[] _compressedPublicKey;
	private readonly byte[] _scriptPubKey;

	private LiquidSpendKeyReference(
		byte[] compressedPublicKey,
		byte[] scriptPubKey,
		LiquidKeyBranch branch,
		uint index)
	{
		_compressedPublicKey = compressedPublicKey;
		_scriptPubKey = scriptPubKey;
		Branch = branch;
		Index = index;
	}

	public LiquidKeyBranch Branch { get; }
	public uint Index { get; }

	public static LiquidSpendKeyReference Create(
		ReadOnlySpan<byte> compressedPublicKey,
		LiquidKeyBranch branch,
		uint index)
	{
		if (!Enum.IsDefined(branch))
		{
			throw new ArgumentOutOfRangeException(nameof(branch), "A supported Liquid key branch is required.");
		}
		if (index > MaximumIndex)
		{
			throw new ArgumentOutOfRangeException(
				nameof(index),
				"A supported normal descriptor derivation index is required.");
		}
		if (compressedPublicKey.Length != 33 || compressedPublicKey[0] is not (0x02 or 0x03))
		{
			throw new ArgumentException("An exact compressed secp256k1 public key is required.", nameof(compressedPublicKey));
		}

		byte[] publicKeyBytes = compressedPublicKey.ToArray();
		PubKey publicKey;
		try
		{
			publicKey = new PubKey(publicKeyBytes);
		}
		catch (Exception exception) when (exception is ArgumentException or FormatException)
		{
			throw new ArgumentException("A valid compressed secp256k1 public key is required.", nameof(compressedPublicKey), exception);
		}

		if (!publicKey.IsCompressed)
		{
			throw new ArgumentException("A compressed secp256k1 public key is required.", nameof(compressedPublicKey));
		}

		return new LiquidSpendKeyReference(
			publicKeyBytes,
			publicKey.WitHash.ScriptPubKey.ToBytes(),
			branch,
			index);
	}

	public byte[] GetCompressedPublicKey() => [.. _compressedPublicKey];

	public byte[] GetScriptPubKey() => [.. _scriptPubKey];

	public bool MatchesScriptPubKey(ReadOnlySpan<byte> scriptPubKey) =>
		scriptPubKey.SequenceEqual(_scriptPubKey);

	public bool Equals(LiquidSpendKeyReference? other) =>
		other is not null &&
		Branch == other.Branch &&
		Index == other.Index &&
		_compressedPublicKey.AsSpan().SequenceEqual(other._compressedPublicKey);

	public override bool Equals(object? obj) => Equals(obj as LiquidSpendKeyReference);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Branch);
		hash.Add(Index);
		foreach (byte value in _compressedPublicKey)
		{
			hash.Add(value);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidSpendKeyReference);
}
