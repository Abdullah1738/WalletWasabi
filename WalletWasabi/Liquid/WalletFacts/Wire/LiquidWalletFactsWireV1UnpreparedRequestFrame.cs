using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.WalletFacts.Wire;

internal sealed class LiquidWalletFactsWireV1UnpreparedRequestFrame : IDisposable
{
	private const int HeaderLength = 76;
	private const int SourceEpochOffset = 28;
	private const int SourceEpochLength = 32;
	private const int DescriptorLengthOffset = 60;
	private const int CandidateCountOffset = 64;
	private const int PreviousTransactionCountOffset = 68;
	private const int CandidateFixedLength = 12;
	private const int MaximumDescriptorLength = 16_384;
	private const int MaximumCandidateCount = 4_096;
	private const int MaximumPreviousTransactionCount = 16_384;
	private const int MaximumTransactionLength = 4_194_304;
	private const int MaximumAggregateTransactionLength = 67_108_864;
	private const int MaximumReachableFrameLength = 67_240_012;
	private const uint MaximumDerivationIndex = 100_000;
	private const string InvalidStructuralFrameMessage = "Liquid wallet facts wire structural frame is invalid.";
	private const string DisposedMessage = "Liquid wallet facts wire unprepared request frame is disposed.";
	private const string DestinationLengthMessage = "An exact wallet facts wire frame destination is required.";

	private readonly object _gate = new();
	private readonly byte[] _frame;
	private bool _disposed;

	private LiquidWalletFactsWireV1UnpreparedRequestFrame(PrivateStructuralFrameCopy structuralCopy)
	{
		_frame = structuralCopy.Transfer();
	}

	internal static LiquidWalletFactsWireV1UnpreparedRequestFrame CreateStructuralUnpreparedCopy(
		ReadOnlySpan<byte> canonicalFrame)
	{
		if (canonicalFrame.Length > MaximumReachableFrameLength)
		{
			throw new InvalidOperationException(InvalidStructuralFrameMessage);
		}

		using var structuralCopy = new PrivateStructuralFrameCopy(canonicalFrame);
		if (!IsCanonicalStructuralRequest(structuralCopy.Frame))
		{
			throw new InvalidOperationException(InvalidStructuralFrameMessage);
		}

		return new LiquidWalletFactsWireV1UnpreparedRequestFrame(structuralCopy);
	}

	internal int Length
	{
		get
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				return _frame.Length;
			}
		}
	}

	internal void CopyFrameTo(Span<byte> exactDestination)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (exactDestination.Length != _frame.Length)
			{
				throw new ArgumentException(DestinationLengthMessage, nameof(exactDestination));
			}

			_frame.AsSpan().CopyTo(exactDestination);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			CryptographicOperations.ZeroMemory(_frame);
		}
	}

	public override string ToString() => nameof(LiquidWalletFactsWireV1UnpreparedRequestFrame);

	private static bool IsCanonicalStructuralRequest(ReadOnlySpan<byte> frame)
	{
		if (frame.Length < HeaderLength || frame.Length > MaximumReachableFrameLength ||
			!frame[..4].SequenceEqual("WLFQ"u8) ||
			BinaryPrimitives.ReadUInt16LittleEndian(frame[4..6]) != 1 ||
			BinaryPrimitives.ReadUInt16LittleEndian(frame[6..8]) != HeaderLength ||
			BinaryPrimitives.ReadUInt64LittleEndian(frame[8..16]) != (ulong)frame.Length ||
			BinaryPrimitives.ReadUInt32LittleEndian(frame[16..20]) != 0 ||
			frame[20] > 1 ||
			!IsZero(frame[21..24]) ||
			BinaryPrimitives.ReadUInt32LittleEndian(frame[24..28]) > MaximumDerivationIndex ||
			!IsNonzero(frame.Slice(SourceEpochOffset, SourceEpochLength)) ||
			BinaryPrimitives.ReadUInt32LittleEndian(frame[72..76]) != 0)
		{
			return false;
		}

		uint descriptorLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(DescriptorLengthOffset, sizeof(uint)));
		uint candidateCountValue = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(CandidateCountOffset, sizeof(uint)));
		uint expectedPreviousCount = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(PreviousTransactionCountOffset, sizeof(uint)));
		if (descriptorLengthValue is 0 or > MaximumDescriptorLength ||
			candidateCountValue > MaximumCandidateCount ||
			expectedPreviousCount > MaximumPreviousTransactionCount)
		{
			return false;
		}

		int descriptorLength = (int)descriptorLengthValue;
		int candidateCount = (int)candidateCountValue;
		int cursor = HeaderLength;
		if (!TryTake(frame, ref cursor, descriptorLength, out ReadOnlySpan<byte> descriptor) ||
			!IsStructurallyShapedDescriptor(descriptor))
		{
			return false;
		}

		int aggregatePreviousCount = 0;
		int aggregateTransactionLength = 0;
		for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
		{
			if (!TryReadUInt32(frame, ref cursor, out uint transactionLengthValue) ||
				!TryReadUInt32(frame, ref cursor, out uint previousCountValue) ||
				!TryReadUInt32(frame, ref cursor, out uint reserved) ||
				reserved != 0 ||
				transactionLengthValue is 0 or > MaximumTransactionLength ||
				previousCountValue > MaximumPreviousTransactionCount ||
				!TryCheckedAdd(aggregatePreviousCount, (int)previousCountValue, out aggregatePreviousCount) ||
				aggregatePreviousCount > MaximumPreviousTransactionCount ||
				!TryCheckedAdd(aggregateTransactionLength, (int)transactionLengthValue, out aggregateTransactionLength) ||
				aggregateTransactionLength > MaximumAggregateTransactionLength ||
				!TryTake(frame, ref cursor, (int)transactionLengthValue, out _))
			{
				return false;
			}

			int previousCount = (int)previousCountValue;
			for (int previousIndex = 0; previousIndex < previousCount; previousIndex++)
			{
				if (!TryReadUInt32(frame, ref cursor, out uint previousLengthValue) ||
					previousLengthValue is 0 or > MaximumTransactionLength ||
					!TryCheckedAdd(aggregateTransactionLength, (int)previousLengthValue, out aggregateTransactionLength) ||
					aggregateTransactionLength > MaximumAggregateTransactionLength ||
					!TryTake(frame, ref cursor, (int)previousLengthValue, out _))
				{
					return false;
				}
			}
		}

		return aggregatePreviousCount == expectedPreviousCount && cursor == frame.Length;
	}

	private static bool IsStructurallyShapedDescriptor(ReadOnlySpan<byte> descriptor)
	{
		ReadOnlySpan<byte> checksumAlphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l"u8;
		int separator = -1;
		for (int index = 0; index < descriptor.Length; index++)
		{
			byte value = descriptor[index];
			if (value > 0x7f || value == 0 || value is >= 0x09 and <= 0x0d or 0x20)
			{
				return false;
			}

			if (value == (byte)'#')
			{
				if (separator >= 0)
				{
					return false;
				}

				separator = index;
			}
		}

		if (separator <= 0 || descriptor.Length - separator - 1 != 8)
		{
			return false;
		}

		foreach (byte value in descriptor[(separator + 1)..])
		{
			if (checksumAlphabet.IndexOf(value) < 0)
			{
				return false;
			}
		}

		return true;
	}

	private static bool TryReadUInt32(ReadOnlySpan<byte> frame, ref int cursor, out uint value)
	{
		if (!TryTake(frame, ref cursor, sizeof(uint), out ReadOnlySpan<byte> bytes))
		{
			value = 0;
			return false;
		}

		value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
		return true;
	}

	private static bool TryTake(
		ReadOnlySpan<byte> frame,
		ref int cursor,
		int length,
		out ReadOnlySpan<byte> value)
	{
		if (length < 0 || cursor < 0 || length > frame.Length - cursor)
		{
			value = default;
			return false;
		}

		value = frame.Slice(cursor, length);
		cursor += length;
		return true;
	}

	private static bool TryCheckedAdd(int left, int right, out int result)
	{
		long sum = (long)left + right;
		if (sum > int.MaxValue)
		{
			result = 0;
			return false;
		}

		result = (int)sum;
		return true;
	}

	private static bool IsNonzero(ReadOnlySpan<byte> value)
	{
		byte aggregate = 0;
		foreach (byte item in value)
		{
			aggregate |= item;
		}

		return aggregate != 0;
	}

	private static bool IsZero(ReadOnlySpan<byte> value)
	{
		byte aggregate = 0;
		foreach (byte item in value)
		{
			aggregate |= item;
		}

		return aggregate == 0;
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(
				nameof(LiquidWalletFactsWireV1UnpreparedRequestFrame),
				DisposedMessage);
		}
	}

	private sealed class PrivateStructuralFrameCopy : IDisposable
	{
		private byte[] _frame;

		internal PrivateStructuralFrameCopy(ReadOnlySpan<byte> canonicalFrame)
		{
			_frame = canonicalFrame.ToArray();
		}

		internal ReadOnlySpan<byte> Frame => _frame;

		internal byte[] Transfer()
		{
			byte[] frame = _frame;
			_frame = Array.Empty<byte>();
			return frame;
		}

		void IDisposable.Dispose()
		{
			CryptographicOperations.ZeroMemory(_frame);
			_frame = Array.Empty<byte>();
		}
	}
}
