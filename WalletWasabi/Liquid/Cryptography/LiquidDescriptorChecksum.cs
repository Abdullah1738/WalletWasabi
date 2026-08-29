using System;
using System.Text;

namespace WalletWasabi.Liquid.Cryptography;

/// <summary>
/// Computes the canonical 8-character Bitcoin output descriptor checksum appended after '#'
/// as specified by Bitcoin Core (doc/descriptors.md). The algorithm is a BCH code over a
/// dedicated input charset; this implementation mirrors the reference vectors published there.
/// </summary>
internal static class LiquidDescriptorChecksum
{
	private const string InputCharset =
		"0123456789()[],'/*abcdefgh@:$%{}IJKLMNOPQRSTUVWXYZ&+-.;<=>?!^_|~ijklmnopqrstuvwxyzABCDEFGH`#\"\\ ";
	private const string ChecksumCharset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

	private static readonly ulong[] Generator =
	[
		0xf5dee51989UL,
		0xa9fdca3312UL,
		0x1bab10e32dUL,
		0x3706b1677aUL,
		0x644d626ffdUL,
	];

	/// <summary>
	/// Returns <paramref name="body"/> followed by '#' and its canonical 8-character checksum.
	/// The input must be the descriptor body only: no '#', no whitespace, printable ASCII.
	/// </summary>
	internal static string AppendChecksum(string body)
	{
		ArgumentNullException.ThrowIfNull(body);
		ulong checksum = ComputeChecksum(body);
		var result = new StringBuilder(body.Length + 9);
		result.Append(body);
		result.Append('#');
		for (int position = 0; position < 8; position++)
		{
			result.Append(ChecksumCharset[(int)((checksum >> (5 * (7 - position))) & 31UL)]);
		}

		return result.ToString();
	}

	private static ulong ComputeChecksum(string body)
	{
		// Symbol expansion: each input character contributes its low 5 bits directly, while the
		// high bits accumulate in groups of three and collapse into one extra symbol.
		ulong c = 1;
		Span<int> groups = stackalloc int[3];
		int groupCount = 0;
		foreach (char ch in body)
		{
			int value = InputCharset.IndexOf(ch);
			if (value < 0)
			{
				throw new ArgumentException(
					$"The character '{ch}' is not allowed inside an output descriptor body.",
					nameof(body));
			}

			c = PolyMod(c, (ulong)(value & 31));
			groups[groupCount++] = value >> 5;
			if (groupCount == 3)
			{
				c = PolyMod(c, (ulong)(groups[0] * 9 + groups[1] * 3 + groups[2]));
				groupCount = 0;
			}
		}

		if (groupCount == 1)
		{
			c = PolyMod(c, (ulong)groups[0]);
		}
		else if (groupCount == 2)
		{
			c = PolyMod(c, (ulong)(groups[0] * 3 + groups[1]));
		}

		for (int index = 0; index < 8; index++)
		{
			c = PolyMod(c, 0);
		}

		return c ^ 1;
	}

	private static ulong PolyMod(ulong c, ulong value)
	{
		byte top = (byte)(c >> 35);
		c = ((c & 0x7ffffffffUL) << 5) ^ value;
		for (int index = 0; index < Generator.Length; index++)
		{
			if (((top >> index) & 1) != 0)
			{
				c ^= Generator[index];
			}
		}

		return c;
	}
}
