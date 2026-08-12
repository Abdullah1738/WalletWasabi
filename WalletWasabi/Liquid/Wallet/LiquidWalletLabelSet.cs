using System.Collections.Generic;
using System.Text;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletLabelSet : IEquatable<LiquidWalletLabelSet>
{
	public const int MaximumLabelCount = 32;
	public const int MaximumRawLabelUtf16CodeUnitCount = 128;
	public const int MaximumLabelUtf8ByteCount = 128;
	public const int MaximumTotalUtf8ByteCount = 2_048;

	private readonly string[] _labels;

	private LiquidWalletLabelSet(string[] labels)
	{
		_labels = labels;
	}

	public static LiquidWalletLabelSet Empty { get; } = new([]);

	public int Count => _labels.Length;
	public bool IsEmpty => Count == 0;

	public static LiquidWalletLabelSet Create(IReadOnlyList<string> labels)
	{
		ArgumentNullException.ThrowIfNull(labels);

		int count = labels.Count;
		if (count < 0 || count > MaximumLabelCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(labels),
				"The Liquid wallet label set could not be accepted.");
		}

		if (count == 0)
		{
			return Empty;
		}

		var snapshot = new string[count];
		for (int index = 0; index < count; index++)
		{
			snapshot[index] = labels[index];
		}

		for (int index = 0; index < snapshot.Length; index++)
		{
			if (snapshot[index] is null)
			{
				throw new ArgumentException(
					"The Liquid wallet label set could not be accepted.",
					nameof(labels));
			}
		}

		for (int index = 0; index < snapshot.Length; index++)
		{
			if (snapshot[index].Length > MaximumRawLabelUtf16CodeUnitCount)
			{
				throw new ArgumentOutOfRangeException(
					nameof(labels),
					"The Liquid wallet label set could not be accepted.");
			}
		}

		var strictUtf8 = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true);
		for (int index = 0; index < snapshot.Length; index++)
		{
			try
			{
				_ = strictUtf8.GetByteCount(snapshot[index]);
			}
			catch (EncoderFallbackException)
			{
				throw new ArgumentException(
					"The Liquid wallet label set could not be accepted.",
					nameof(labels));
			}
		}

		for (int index = 0; index < snapshot.Length; index++)
		{
			foreach (Rune rune in snapshot[index].EnumerateRunes())
			{
				if (IsDeniedScalar(rune.Value))
				{
					throw new ArgumentException(
						"The Liquid wallet label set could not be accepted.",
						nameof(labels));
				}
			}
		}

		for (int index = 0; index < snapshot.Length; index++)
		{
			string label = snapshot[index];
			int start = 0;
			while (start < label.Length && label[start] == ' ')
			{
				start++;
			}

			int end = label.Length;
			while (end > start && label[end - 1] == ' ')
			{
				end--;
			}

			if (start == end)
			{
				throw new ArgumentException(
					"The Liquid wallet label set could not be accepted.",
					nameof(labels));
			}

			if (start != 0 || end != label.Length)
			{
				snapshot[index] = label[start..end];
			}
		}

		var byteCounts = new int[snapshot.Length];
		for (int index = 0; index < snapshot.Length; index++)
		{
			int byteCount = strictUtf8.GetByteCount(snapshot[index]);
			if (byteCount > MaximumLabelUtf8ByteCount)
			{
				throw new ArgumentOutOfRangeException(
					nameof(labels),
					"The Liquid wallet label set could not be accepted.");
			}
			byteCounts[index] = byteCount;
		}

		var uniqueLabels = new HashSet<string>(StringComparer.Ordinal);
		int totalByteCount = 0;
		for (int index = 0; index < snapshot.Length; index++)
		{
			if (!uniqueLabels.Add(snapshot[index]))
			{
				continue;
			}

			totalByteCount += byteCounts[index];
			if (totalByteCount > MaximumTotalUtf8ByteCount)
			{
				throw new ArgumentOutOfRangeException(
					nameof(labels),
					"The Liquid wallet label set could not be accepted.");
			}
		}

		var canonicalLabels = new string[uniqueLabels.Count];
		uniqueLabels.CopyTo(canonicalLabels);
		Array.Sort(canonicalLabels, StringComparer.Ordinal);
		return new LiquidWalletLabelSet(canonicalLabels);
	}

	public IReadOnlyList<string> GetLabels()
	{
		var snapshot = new string[_labels.Length];
		Array.Copy(_labels, snapshot, _labels.Length);
		return Array.AsReadOnly(snapshot);
	}

	public bool Equals(LiquidWalletLabelSet? other)
	{
		if (ReferenceEquals(this, other))
		{
			return true;
		}

		if (other is null || _labels.Length != other._labels.Length)
		{
			return false;
		}

		for (int index = 0; index < _labels.Length; index++)
		{
			if (!StringComparer.Ordinal.Equals(_labels[index], other._labels[index]))
			{
				return false;
			}
		}

		return true;
	}

	public override bool Equals(object? obj) => Equals(obj as LiquidWalletLabelSet);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		for (int index = 0; index < _labels.Length; index++)
		{
			hash.Add(_labels[index], StringComparer.Ordinal);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidWalletLabelSet);

	public static bool operator ==(
		LiquidWalletLabelSet? left,
		LiquidWalletLabelSet? right) =>
		ReferenceEquals(left, right) || (left is not null && left.Equals(right));

	public static bool operator !=(
		LiquidWalletLabelSet? left,
		LiquidWalletLabelSet? right) =>
		!(left == right);

	private static bool IsDeniedScalar(int value) =>
		value is >= 0x0000 and <= 0x001F or
		>= 0x007F and <= 0x009F or
		0x00A0 or
		0x1680 or
		>= 0x2000 and <= 0x200A or
		>= 0x2028 and <= 0x2029 or
		0x202F or
		0x205F or
		0x3000 or
		0x00AD or
		>= 0x0600 and <= 0x0605 or
		0x061C or
		0x06DD or
		0x070F or
		>= 0x0890 and <= 0x0891 or
		0x08E2 or
		0x180E or
		>= 0x200B and <= 0x200F or
		>= 0x202A and <= 0x202E or
		>= 0x2060 and <= 0x206F or
		0xFEFF or
		>= 0xFFF9 and <= 0xFFFB or
		0x110BD or
		0x110CD or
		>= 0x13430 and <= 0x1343F or
		>= 0x1BCA0 and <= 0x1BCA3 or
		>= 0x1D173 and <= 0x1D17A or
		0xE0001 or
		>= 0xE0020 and <= 0xE007F or
		0x034F or
		>= 0x115F and <= 0x1160 or
		>= 0x17B4 and <= 0x17B5 or
		>= 0x180B and <= 0x180F or
		0x3164 or
		>= 0xFE00 and <= 0xFE0F or
		0xFFA0 or
		>= 0xFFF0 and <= 0xFFF8 or
		>= 0xE0000 and <= 0xE0FFF or
		>= 0xFDD0 and <= 0xFDEF ||
		(value & 0xFFFF) is 0xFFFE or 0xFFFF;
}
