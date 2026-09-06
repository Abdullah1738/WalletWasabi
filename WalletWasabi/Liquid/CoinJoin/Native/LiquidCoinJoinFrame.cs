using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.IO;

namespace WalletWasabi.Liquid.CoinJoin.Native;

internal static class LiquidCoinJoinFrame
{
	internal const uint Magic = 0x574C434A;
	internal const uint AbiVersion = 1;
	internal const int HeaderBytes = 16;
	internal const int MaxFields = 258;
	internal const int MaxFieldBytes = 2_097_152;
	internal const int MaxFrameBytes = 16_777_216;

	internal static byte[] Encode(uint operation, IReadOnlyList<ReadOnlyMemory<byte>> fields)
	{
		ArgumentNullException.ThrowIfNull(fields);
		using var payload = new MemoryStream();
		byte[] lengthBytes = new byte[4];
		foreach (ReadOnlyMemory<byte> field in fields)
		{
			if (field.Length > MaxFieldBytes)
				throw new ArgumentOutOfRangeException(nameof(fields));
			BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, checked((uint)field.Length));
			payload.Write(lengthBytes);
			payload.Write(field.Span);
		}
		byte[] body = payload.ToArray();
		if (fields.Count > MaxFields || HeaderBytes + body.Length > MaxFrameBytes)
			throw new ArgumentOutOfRangeException(nameof(fields));
		byte[] frame = new byte[HeaderBytes + body.Length];
		BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), Magic);
		BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), AbiVersion);
		BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), operation);
		BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12, 4), checked((uint)body.Length));
		body.CopyTo(frame, HeaderBytes);
		return frame;
	}

	internal static (uint Operation, IReadOnlyList<byte[]> Fields) Decode(ReadOnlySpan<byte> frame)
	{
		if (frame.Length < HeaderBytes || frame.Length > MaxFrameBytes)
			throw new FormatException("Invalid CoinJoin frame length.");
		if (BinaryPrimitives.ReadUInt32BigEndian(frame[..4]) != Magic
			|| BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(4, 4)) != AbiVersion)
			throw new FormatException("Invalid CoinJoin frame header.");
		uint operation = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(8, 4));
		uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(12, 4));
		if (payloadLength != frame.Length - HeaderBytes)
			throw new FormatException("CoinJoin frame payload length does not match the frame.");

		ReadOnlySpan<byte> payload = frame[HeaderBytes..];
		int fieldCount = 0;
		while (!payload.IsEmpty)
		{
			if (fieldCount == MaxFields || payload.Length < 4)
				throw new FormatException("Invalid CoinJoin field sequence.");
			uint length = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
			payload = payload[4..];
			if (length > MaxFieldBytes || length > payload.Length)
				throw new FormatException("Invalid CoinJoin field length.");
			fieldCount++;
			payload = payload[(int)length..];
		}

		var fields = new List<byte[]>(fieldCount);
		payload = frame[HeaderBytes..];
		while (!payload.IsEmpty)
		{
			uint length = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
			payload = payload[4..];
			fields.Add(payload[..(int)length].ToArray());
			payload = payload[(int)length..];
		}
		return (operation, fields);
	}
}
