using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.WalletFacts.Wire;

internal enum LiquidWalletFactsWireV1DescriptorNetworkClass : byte
{
	Mainnet = 0,
	Test = 1,
}

internal static class LiquidWalletFactsWireV1StructuralRequestCodec
{
	private const int SourceEpochLength = 32;
	private const int HeaderLength = 76;
	private const int CandidateFixedLength = 12;
	private const int PreviousLengthPrefix = 4;
	private const int MaximumDescriptorLength = 16_384;
	// One candidate per refresh-selected row; sized to ElementsRpcClient.MaxRefreshSelectedCandidates
	// (8_192) so a full bounded rescan window passes and the selection cap is the only gate.
	private const int MaximumCandidateCount = 8_192;
	private const int MaximumPreviousTransactionCount = 16_384;
	private const int MaximumTransactionLength = 4_194_304;
	private const int MaximumAggregateTransactionLength = 67_108_864;
	private const int MaximumReachableFrameLength = 67_240_012;
	private const int MaximumFrameLength = 268_435_456;
	private const uint MaximumDerivationIndex = 100_000;

	internal static bool TryBuildUnpreparedFrame(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidWalletFactsWireV1DescriptorNetworkClass descriptorNetworkClass,
		uint lastDerivationIndex,
		ReadOnlySpan<byte> descriptorAscii,
		IReadOnlyList<LiquidWalletFactsWireV1StructuralCandidateSource> candidates,
		out LiquidWalletFactsWireV1UnpreparedRequestFrame? frame,
		out LiquidWalletFactsWireErrorCode errorCode)
	{
		frame = null;
		errorCode = LiquidWalletFactsWireErrorCode.None;
		Span<byte> sourceEpochScratch = stackalloc byte[SourceEpochLength];
		byte[]? descriptorScratch = null;
		CandidateSnapshot?[]? candidateSnapshots = null;
		byte[]? temporaryFrame = null;

		try
		{
			if (sourceEpoch.Length != SourceEpochLength)
			{
				return Reject(LiquidWalletFactsWireErrorCode.InvalidArgument, out errorCode);
			}

			sourceEpoch.CopyTo(sourceEpochScratch);
			if (!IsNonzero(sourceEpochScratch))
			{
				return Reject(LiquidWalletFactsWireErrorCode.InvalidArgument, out errorCode);
			}

			if (descriptorNetworkClass is not LiquidWalletFactsWireV1DescriptorNetworkClass.Mainnet and
				not LiquidWalletFactsWireV1DescriptorNetworkClass.Test)
			{
				return Reject(LiquidWalletFactsWireErrorCode.InvalidArgument, out errorCode);
			}

			if (candidates is null)
			{
				return Reject(LiquidWalletFactsWireErrorCode.InvalidArgument, out errorCode);
			}

			if (descriptorAscii.IsEmpty)
			{
				return Reject(LiquidWalletFactsWireErrorCode.InvalidEncoding, out errorCode);
			}

			if (descriptorAscii.Length > MaximumDescriptorLength)
			{
				return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
			}

			descriptorScratch = descriptorAscii.ToArray();
			if (!IsStructurallyShapedDescriptor(descriptorScratch))
			{
				return Reject(LiquidWalletFactsWireErrorCode.InvalidEncoding, out errorCode);
			}

			int candidateCount = candidates.Count;
			if (lastDerivationIndex > MaximumDerivationIndex || candidateCount > MaximumCandidateCount)
			{
				return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
			}

			candidateSnapshots = new CandidateSnapshot?[candidateCount];
			int aggregatePreviousCount = 0;
			ulong aggregateTransactionLength = 0;
			int previousPrefixLength = 0;
			for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
			{
				LiquidWalletFactsWireV1StructuralCandidateSource? candidate = candidates[candidateIndex];
				if (candidate is null)
				{
					return Reject(LiquidWalletFactsWireErrorCode.InvalidArgument, out errorCode);
				}

				ICandidateSourceStorage candidateStorage = candidate;
				ReadOnlyMemory<byte> transaction = candidateStorage.TransactionBytes;
				int transactionLength = transaction.Length;
				if (transactionLength == 0)
				{
					return Reject(LiquidWalletFactsWireErrorCode.InvalidEncoding, out errorCode);
				}

				if (transactionLength > MaximumTransactionLength)
				{
					return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
				}

				IReadOnlyList<ReadOnlyMemory<byte>> previousTransactions = candidateStorage.PreviousTransactionBytes;
				int previousCount = previousTransactions.Count;
				if (!TryCheckedAdd(aggregatePreviousCount, previousCount, out aggregatePreviousCount) ||
					aggregatePreviousCount > MaximumPreviousTransactionCount)
				{
					return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
				}

				if (!TryCheckedAdd(aggregateTransactionLength, transactionLength, out aggregateTransactionLength))
				{
					return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
				}

				var previousSnapshots = new PayloadSnapshot[previousCount];
				var candidateSnapshot = new CandidateSnapshot(
					new PayloadSnapshot(transaction, transactionLength),
					previousSnapshots);
				candidateSnapshots[candidateIndex] = candidateSnapshot;
				for (int previousIndex = 0; previousIndex < previousCount; previousIndex++)
				{
					ReadOnlyMemory<byte> previous = previousTransactions[previousIndex];
					int previousLength = previous.Length;
					if (previousLength == 0)
					{
						return Reject(LiquidWalletFactsWireErrorCode.InvalidEncoding, out errorCode);
					}

					if (previousLength > MaximumTransactionLength)
					{
						return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
					}

					if (!TryCheckedAdd(aggregateTransactionLength, previousLength, out aggregateTransactionLength) ||
						!TryCheckedAdd(previousPrefixLength, PreviousLengthPrefix, out previousPrefixLength))
					{
						return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
					}

					previousSnapshots[previousIndex] = new PayloadSnapshot(previous, previousLength);
				}

				if (aggregateTransactionLength > (ulong)MaximumAggregateTransactionLength)
				{
					return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
				}
			}

			if (aggregateTransactionLength > int.MaxValue)
			{
				return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
			}

			if (!TryCheckedMultiply(candidateCount, CandidateFixedLength, out int candidateHeaderLength) ||
				!TryCheckedAdd(HeaderLength, descriptorScratch.Length, out int exactLength) ||
				!TryCheckedAdd(exactLength, candidateHeaderLength, out exactLength) ||
				!TryCheckedAdd(exactLength, previousPrefixLength, out exactLength) ||
				!TryCheckedAdd(exactLength, (int)aggregateTransactionLength, out exactLength) ||
				exactLength > MaximumReachableFrameLength || exactLength > MaximumFrameLength)
			{
				return Reject(LiquidWalletFactsWireErrorCode.LimitExceeded, out errorCode);
			}

			temporaryFrame = new byte[exactLength];
			int cursor = 0;
			Write("WLFQ"u8, temporaryFrame, ref cursor);
			WriteUInt16(1, temporaryFrame, ref cursor);
			WriteUInt16(HeaderLength, temporaryFrame, ref cursor);
			WriteUInt64((ulong)exactLength, temporaryFrame, ref cursor);
			WriteUInt32(0, temporaryFrame, ref cursor);
			WriteByte((byte)descriptorNetworkClass, temporaryFrame, ref cursor);
			WriteZeros(3, temporaryFrame, ref cursor);
			WriteUInt32(lastDerivationIndex, temporaryFrame, ref cursor);
			Write(sourceEpochScratch, temporaryFrame, ref cursor);
			WriteUInt32((uint)descriptorScratch.Length, temporaryFrame, ref cursor);
			WriteUInt32((uint)candidateCount, temporaryFrame, ref cursor);
			WriteUInt32((uint)aggregatePreviousCount, temporaryFrame, ref cursor);
			WriteUInt32(0, temporaryFrame, ref cursor);
			Write(descriptorScratch, temporaryFrame, ref cursor);
			foreach (CandidateSnapshot? candidateItem in candidateSnapshots)
			{
				CandidateSnapshot candidate = candidateItem ??
					throw new InvalidOperationException("Liquid wallet facts wire structural frame assembly failed.");
				WriteUInt32((uint)candidate._transaction.Length, temporaryFrame, ref cursor);
				WriteUInt32((uint)candidate._previous.Length, temporaryFrame, ref cursor);
				WriteUInt32(0, temporaryFrame, ref cursor);
				Write(candidate._transaction.Memory.Span, temporaryFrame, ref cursor);
				foreach (PayloadSnapshot previous in candidate._previous)
				{
					WriteUInt32((uint)previous.Length, temporaryFrame, ref cursor);
					Write(previous.Memory.Span, temporaryFrame, ref cursor);
				}
			}

			if (cursor != exactLength)
			{
				throw new InvalidOperationException("Liquid wallet facts wire structural frame assembly failed.");
			}

			LiquidWalletFactsWireV1UnpreparedRequestFrame ownedFrame =
				LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(temporaryFrame);
			frame = ownedFrame;
			errorCode = LiquidWalletFactsWireErrorCode.None;
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceEpochScratch);
			if (descriptorScratch is not null)
			{
				CryptographicOperations.ZeroMemory(descriptorScratch);
			}

			if (candidateSnapshots is not null)
			{
				foreach (CandidateSnapshot? candidate in candidateSnapshots)
				{
					candidate?.Clear();
				}

				Array.Clear(candidateSnapshots);
			}

			if (temporaryFrame is not null)
			{
				CryptographicOperations.ZeroMemory(temporaryFrame);
			}
		}
	}

	private static bool Reject(
		LiquidWalletFactsWireErrorCode failure,
		out LiquidWalletFactsWireErrorCode errorCode)
	{
		errorCode = failure;
		return false;
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

	private static bool IsNonzero(ReadOnlySpan<byte> value)
	{
		byte aggregate = 0;
		foreach (byte item in value)
		{
			aggregate |= item;
		}

		return aggregate != 0;
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

	private static bool TryCheckedAdd(ulong left, int right, out ulong result)
	{
		if (right < 0 || left > ulong.MaxValue - (uint)right)
		{
			result = 0;
			return false;
		}

		result = left + (uint)right;
		return true;
	}

	private static bool TryCheckedMultiply(int left, int right, out int result)
	{
		long product = (long)left * right;
		if (product > int.MaxValue)
		{
			result = 0;
			return false;
		}

		result = (int)product;
		return true;
	}

	private static void Write(ReadOnlySpan<byte> value, Span<byte> destination, ref int cursor)
	{
		value.CopyTo(destination[cursor..]);
		cursor += value.Length;
	}

	private static void WriteByte(byte value, Span<byte> destination, ref int cursor)
	{
		destination[cursor] = value;
		cursor++;
	}

	private static void WriteZeros(int length, Span<byte> destination, ref int cursor)
	{
		destination.Slice(cursor, length).Clear();
		cursor += length;
	}

	private static void WriteUInt16(int value, Span<byte> destination, ref int cursor)
	{
		BinaryPrimitives.WriteUInt16LittleEndian(destination[cursor..], checked((ushort)value));
		cursor += sizeof(ushort);
	}

	private static void WriteUInt32(uint value, Span<byte> destination, ref int cursor)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(destination[cursor..], value);
		cursor += sizeof(uint);
	}

	private static void WriteUInt64(ulong value, Span<byte> destination, ref int cursor)
	{
		BinaryPrimitives.WriteUInt64LittleEndian(destination[cursor..], value);
		cursor += sizeof(ulong);
	}

	private readonly struct PayloadSnapshot
	{
		internal PayloadSnapshot(ReadOnlyMemory<byte> memory, int length)
		{
			Memory = memory;
			Length = length;
		}

		internal ReadOnlyMemory<byte> Memory { get; }

		internal int Length { get; }
	}

	private sealed class CandidateSnapshot
	{
		internal PayloadSnapshot _transaction;
		internal readonly PayloadSnapshot[] _previous;

		internal CandidateSnapshot(PayloadSnapshot transaction, PayloadSnapshot[] previous)
		{
			_transaction = transaction;
			_previous = previous;
		}

		internal void Clear()
		{
			_transaction = default;
			Array.Clear(_previous);
		}
	}

	private interface ICandidateSourceStorage
	{
		ReadOnlyMemory<byte> TransactionBytes { get; }

		IReadOnlyList<ReadOnlyMemory<byte>> PreviousTransactionBytes { get; }
	}

	internal sealed class LiquidWalletFactsWireV1StructuralCandidateSource : ICandidateSourceStorage
	{
		private readonly ReadOnlyMemory<byte> _transactionBytes;
		private readonly IReadOnlyList<ReadOnlyMemory<byte>> _previousTransactionBytes;

		internal LiquidWalletFactsWireV1StructuralCandidateSource(
			ReadOnlyMemory<byte> transactionBytes,
			IReadOnlyList<ReadOnlyMemory<byte>> previousTransactionBytes)
		{
			_previousTransactionBytes = previousTransactionBytes ??
				throw new ArgumentNullException(
					nameof(previousTransactionBytes),
					"A previous-transaction collection is required.");
			_transactionBytes = transactionBytes;
		}

		ReadOnlyMemory<byte> ICandidateSourceStorage.TransactionBytes => _transactionBytes;

		IReadOnlyList<ReadOnlyMemory<byte>> ICandidateSourceStorage.PreviousTransactionBytes =>
			_previousTransactionBytes;

		public override string ToString() => nameof(LiquidWalletFactsWireV1StructuralCandidateSource);
	}
}
