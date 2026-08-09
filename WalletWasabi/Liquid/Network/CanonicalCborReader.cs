using System.Buffers.Binary;
using System.Text;

namespace WalletWasabi.Liquid.Network;

internal ref struct CanonicalCborReader
{
	private const int MaxArrayItems = 64;
	private const int MaxByteStringBytes = 64 * 1024;
	private const int MaxTextBytes = 128;
	private readonly ReadOnlySpan<byte> _input;
	private int _offset;

	public CanonicalCborReader(ReadOnlySpan<byte> input)
	{
		_input = input;
	}

	public void ReadArray(int expectedItems, string field)
	{
		ulong count = ReadLength(4, field);
		if (count > MaxArrayItems || count != (ulong)expectedItems)
		{
			throw Invalid(field);
		}
	}

	public ulong ReadUnsigned(string field) => ReadLength(0, field);

	public byte[] ReadByteString(int expectedBytes, string field)
	{
		ulong length = ReadLength(2, field);
		if (length > MaxByteStringBytes || length != (ulong)expectedBytes || length > (ulong)(_input.Length - _offset))
		{
			throw Invalid(field);
		}

		byte[] result = _input.Slice(_offset, expectedBytes).ToArray();
		_offset += expectedBytes;
		return result;
	}

	public string ReadText(string field)
	{
		ulong length = ReadLength(3, field);
		if (length > MaxTextBytes || length > (ulong)(_input.Length - _offset))
		{
			throw Invalid(field);
		}

		ReadOnlySpan<byte> text = _input.Slice(_offset, checked((int)length));
		foreach (byte value in text)
		{
			if (value > 0x7f)
			{
				throw Invalid(field);
			}
		}

		_offset += text.Length;
		return Encoding.ASCII.GetString(text);
	}

	public bool ReadBoolean(string field)
	{
		byte value = ReadByte(field);
		return value switch
		{
			0xf4 => false,
			0xf5 => true,
			_ => throw Invalid(field),
		};
	}

	public void EnsureFinished()
	{
		if (_offset != _input.Length)
		{
			throw Invalid("trailing data");
		}
	}

	private ulong ReadLength(byte expectedMajorType, string field)
	{
		byte initial = ReadByte(field);
		byte majorType = (byte)(initial >> 5);
		byte additionalInformation = (byte)(initial & 0x1f);
		if (majorType != expectedMajorType || additionalInformation >= 28)
		{
			throw Invalid(field);
		}

		return additionalInformation switch
		{
			< 24 => additionalInformation,
			24 => ReadCanonicalByte(field),
			25 => ReadCanonicalUInt16(field),
			26 => ReadCanonicalUInt32(field),
			27 => ReadCanonicalUInt64(field),
			_ => throw Invalid(field),
		};
	}

	private byte ReadCanonicalByte(string field)
	{
		byte value = ReadByte(field);
		if (value < 24)
		{
			throw Invalid(field);
		}
		return value;
	}

	private ushort ReadCanonicalUInt16(string field)
	{
		ReadOnlySpan<byte> valueBytes = ReadBytes(sizeof(ushort), field);
		ushort value = BinaryPrimitives.ReadUInt16BigEndian(valueBytes);
		if (value <= byte.MaxValue)
		{
			throw Invalid(field);
		}
		return value;
	}

	private uint ReadCanonicalUInt32(string field)
	{
		ReadOnlySpan<byte> valueBytes = ReadBytes(sizeof(uint), field);
		uint value = BinaryPrimitives.ReadUInt32BigEndian(valueBytes);
		if (value <= ushort.MaxValue)
		{
			throw Invalid(field);
		}
		return value;
	}

	private ulong ReadCanonicalUInt64(string field)
	{
		ReadOnlySpan<byte> valueBytes = ReadBytes(sizeof(ulong), field);
		ulong value = BinaryPrimitives.ReadUInt64BigEndian(valueBytes);
		if (value <= uint.MaxValue)
		{
			throw Invalid(field);
		}
		return value;
	}

	private byte ReadByte(string field)
	{
		if (_offset >= _input.Length)
		{
			throw Invalid(field);
		}
		return _input[_offset++];
	}

	private ReadOnlySpan<byte> ReadBytes(int length, string field)
	{
		if (length > _input.Length - _offset)
		{
			throw Invalid(field);
		}

		ReadOnlySpan<byte> result = _input.Slice(_offset, length);
		_offset += length;
		return result;
	}

	private static ElementsNetworkManifestException Invalid(string field) =>
		new($"The reviewed Elements network manifest has invalid canonical CBOR at '{field}'.");
}
