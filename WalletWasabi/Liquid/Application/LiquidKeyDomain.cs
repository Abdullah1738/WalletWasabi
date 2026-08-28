using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WalletWasabi.Liquid.Application;

internal static class LiquidKeyDomain
{
	internal static uint Index(string label)
	{
		ArgumentException.ThrowIfNullOrEmpty(label);
		Span<byte> digest = stackalloc byte[32];
		SHA256.HashData(Encoding.UTF8.GetBytes(label), digest);
		return BinaryPrimitives.ReadUInt32BigEndian(digest) & 0x7fffffffU;
	}

	internal static byte[] DeriveHkdf(ReadOnlySpan<byte> keyMaterial, ReadOnlySpan<byte> salt, string info)
	{
		ArgumentNullException.ThrowIfNull(info);
		byte[] result = HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial.ToArray(), 32, salt.ToArray(), Encoding.UTF8.GetBytes(info));
		return result;
	}
}
