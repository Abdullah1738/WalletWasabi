using System.Buffers.Binary;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The pure, fail-closed framing codec for the on-disk Liquid wallet
/// persistence format. <see cref="Encode"/> wraps one sealed
/// <see cref="LiquidWalletReplayProtectedPayload"/> envelope in a versioned
/// 16-byte plaintext header; <see cref="Decode"/> strictly parses and
/// validates that framing and returns the enclosed envelope bytes. The frame
/// adds only structural framing (magic, version, length) so that truncation,
/// trailing data, version skew, and oversize are rejected before any bytes
/// reach the cryptographic layer; integrity is provided end-to-end by the
/// landed AES-GCM tag inside the envelope, verified by
/// <see cref="LiquidWalletReplayProtectedPayload.Open"/> at
/// <see cref="LiquidWalletPersistenceHandoff.Import"/> time. This type
/// performs no decryption, no AES-GCM, no journal replay, and no canonicality
/// check.
/// </summary>
internal static class LiquidWalletPersistenceFrame
{
	private const int HeaderLength = 16;
	private const int MagicLength = 8;
	private const ushort FormatVersion = 1;
	private const int MinimumEnvelopeLength = 48 + LiquidWalletReplayProtectedPayload.PaddingBucketLength + LiquidWalletReplayProtectedPayload.TagLength;
	private static readonly byte[] Magic = "WLWALFMT"u8.ToArray();

	/// <summary>
	/// Frames one sealed envelope in the versioned on-disk layout. Throws
	/// <see cref="LiquidWalletPersistenceFormatException"/> on any bound
	/// violation.
	/// </summary>
	public static byte[] Encode(ReadOnlySpan<byte> envelopeBytes)
	{
		if (envelopeBytes.IsEmpty)
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		if (envelopeBytes.Length < MinimumEnvelopeLength ||
			envelopeBytes.Length > LiquidWalletReplayProtectedPayload.MaxEnvelopeLength)
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		byte[] framed = new byte[HeaderLength + envelopeBytes.Length];
		Magic.CopyTo(framed, 0);
		BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(8), FormatVersion);
		BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(10), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(12), (uint)envelopeBytes.Length);
		envelopeBytes.CopyTo(framed.AsSpan(HeaderLength));
		return framed;
	}

	/// <summary>
	/// Strictly parses and validates the framed on-disk layout and returns the
	/// enclosed envelope bytes. Throws
	/// <see cref="LiquidWalletPersistenceFormatException"/> on any violation:
	/// wrong magic, unknown or newer format version, truncation, trailing data,
	/// or an envelope length outside the landed structural limits.
	/// </summary>
	public static byte[] Decode(ReadOnlySpan<byte> framedBytes)
	{
		if (framedBytes.Length < HeaderLength)
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		if (!framedBytes[..MagicLength].SequenceEqual(Magic))
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		ushort version = BinaryPrimitives.ReadUInt16LittleEndian(framedBytes[8..]);
		if (version != FormatVersion)
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(framedBytes[10..]);
		if (reserved != 0)
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		uint envelopeLength = BinaryPrimitives.ReadUInt32LittleEndian(framedBytes[12..]);
		if (envelopeLength < MinimumEnvelopeLength ||
			envelopeLength > LiquidWalletReplayProtectedPayload.MaxEnvelopeLength)
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		if (framedBytes.Length - HeaderLength != envelopeLength)
		{
			throw new LiquidWalletPersistenceFormatException();
		}

		return framedBytes[HeaderLength..].ToArray();
	}
}
