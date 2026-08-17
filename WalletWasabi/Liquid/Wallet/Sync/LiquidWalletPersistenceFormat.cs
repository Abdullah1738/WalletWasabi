using System.IO;
using System.Reflection;
using WalletWasabi.Io;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The fail-closed on-disk persistence format entry point for the Liquid
/// managed wallet. <see cref="Save"/> frames one sealed
/// <see cref="LiquidWalletReplayProtectedPayload"/> envelope and writes it
/// atomically via the landed <see cref="SafeFile"/> write-temp-then-move
/// pattern; <see cref="LoadEnvelope"/> reads the framed bytes, strictly
/// validates the framing, and returns the enclosed sealed envelope. This type
/// performs no decryption, no journal replay, no revision fencing, no key
/// management, no canonicalization or confinement of the caller-supplied
/// path, and no catch-and-rethrow remapping: every rejection surfaces with
/// the existing exception surface of the failing layer. The caller owns the
/// choice of directory and the decision of when to save and when to load.
/// </summary>
internal static class LiquidWalletPersistenceFormat
{
	/// <summary>
	/// Frames one sealed envelope and writes it atomically to
	/// <paramref name="filePath"/>. The caller's <paramref name="envelope"/>
	/// is never mutated; <see cref="LiquidWalletReplayProtectedPayload.GetBytes"/>
	/// returns a copy.
	/// </summary>
	public static void Save(
		string filePath,
		LiquidWalletReplayProtectedPayload envelope)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		ArgumentException.ThrowIfNullOrEmpty(filePath);

		byte[] envelopeBytes = envelope.GetBytes();
		byte[] framedBytes = LiquidWalletPersistenceFrame.Encode(envelopeBytes);
		File.SafelyWriteAllBytes(filePath, framedBytes);
	}

	/// <summary>
	/// Reads the framed bytes from <paramref name="filePath"/>, strictly
	/// validates the framing, and returns the enclosed sealed envelope. The
	/// returned payload is exactly the bytes the caller then hands to
	/// <see cref="LiquidWalletPersistenceHandoff.Import"/>.
	/// </summary>
	public static LiquidWalletReplayProtectedPayload LoadEnvelope(string filePath)
	{
		ArgumentException.ThrowIfNullOrEmpty(filePath);

		byte[] framedBytes = File.SafelyReadAllBytes(filePath);
		byte[] envelopeBytes = LiquidWalletPersistenceFrame.Decode(framedBytes);
		return ReconstructEnvelope(envelopeBytes);
	}

	private static LiquidWalletReplayProtectedPayload ReconstructEnvelope(byte[] envelopeBytes)
	{
		// The envelope bytes are validated by the frame decoder; reconstruct the
		// payload object from them without opening (decryption is the caller's
		// job via LiquidWalletPersistenceHandoff.Import). The landed
		// LiquidWalletReplayProtectedPayload constructor is private; reflection
		// is the only reconstruction path that does not modify the landed type.
		ConstructorInfo constructor = typeof(LiquidWalletReplayProtectedPayload)
			.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
			.Single();
		return (LiquidWalletReplayProtectedPayload)constructor.Invoke([envelopeBytes]);
	}
}
