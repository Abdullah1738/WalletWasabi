using System.Security.Cryptography;
using NBitcoin.Secp256k1;

namespace WalletWasabi.Liquid.Cryptography;

/// <summary>Derives the compressed SLIP-77 blinding public key for one script.</summary>
public static class LiquidSlip77PublicKey
{
	public const int MasterLength = 32;

	public static byte[] Derive(ReadOnlySpan<byte> slip77Master, ReadOnlySpan<byte> scriptPubKey)
	{
		if (slip77Master.Length != MasterLength)
		{
			throw new ArgumentException("An exact 32-byte SLIP-77 master is required.", nameof(slip77Master));
		}
		if (scriptPubKey.IsEmpty)
		{
			throw new ArgumentException("A non-empty scriptPubKey is required.", nameof(scriptPubKey));
		}

		byte[] masterCopy = slip77Master.ToArray();
		byte[] scalar = Array.Empty<byte>();
		try
		{
			scalar = System.Security.Cryptography.HMACSHA256.HashData(masterCopy, scriptPubKey);
			byte[] compressed;
			try
			{
				using ECPrivKey privateKey = ECPrivKey.Create(scalar);
				compressed = privateKey.CreatePubKey().ToBytes();
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
			{
				throw new ArgumentException("The script-derived SLIP-77 scalar is not a valid nonzero secp256k1 scalar.", nameof(scriptPubKey), exception);
			}

			LiquidBlindingPublicKey validated = LiquidBlindingPublicKey.Create(compressed);
			return validated.GetCompressedPublicKey();
		}
		finally
		{
			CryptographicOperations.ZeroMemory(scalar);
			CryptographicOperations.ZeroMemory(masterCopy);
		}
	}
}
