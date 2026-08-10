using System.Buffers.Binary;
using System.Security.Cryptography;
using NBitcoin;

namespace WalletWasabi.Liquid.WalletFacts.Wire;

internal static class LiquidWalletFactsWireV1ResponseCodec
{
	private const int SourceEpochLength = 32;
	private const int HeaderLength = 64;
	private const int TransactionFixedLength = 72;
	private const int InputLength = 36;
	private const int OwnedOutputLength = 144;
	private const int NativeP2WpkhScriptLength = 22;
	private const int MaximumFrameLength = 268_435_456;
	private const int MaximumReachableFrameLength = 80_599_492;
	private const int MaximumTransactionCount = 4_096;
	private const int MaximumAggregateInputCount = 1_636_801;
	private const int MaximumAggregateOwnedOutputCount = 148_470;
	private const int MaximumInputCountPerTransaction = 102_298;
	private const int MaximumOwnedOutputCountPerTransaction = 9_279;
	private const uint MaximumSpendableOutputIndex = 0x3fff_ffff;
	private const uint MaximumDerivationIndex = 100_000;
	private const ulong MaximumOwnedOutputValue = 0x7fff_ffff_ffff_ffff;

	private static ReadOnlySpan<byte> ResponseMagic => "WLFV"u8;

	internal static bool TryDecodeResponse(
		ReadOnlySpan<byte> frame,
		ReadOnlySpan<byte> expectedSourceEpoch,
		out LiquidWalletFactsWireV1Response? response,
		out LiquidWalletFactsWireErrorCode errorCode)
	{
		response = null;
		errorCode = LiquidWalletFactsWireErrorCode.None;
		Span<byte> expectedSourceEpochScratch = stackalloc byte[SourceEpochLength];
		Span<byte> headerScratch = stackalloc byte[HeaderLength];
		byte[]? ownedFrame = null;
		int[]? transactionOffsets = null;
		bool ownershipTransferred = false;

		try
		{
			if (expectedSourceEpoch.Length != SourceEpochLength)
			{
				errorCode = LiquidWalletFactsWireErrorCode.InvalidArgument;
				return false;
			}

			expectedSourceEpoch.CopyTo(expectedSourceEpochScratch);
			if (!IsNonzero(expectedSourceEpochScratch))
			{
				errorCode = LiquidWalletFactsWireErrorCode.InvalidArgument;
				return false;
			}

			if (frame.Length > MaximumFrameLength)
			{
				errorCode = LiquidWalletFactsWireErrorCode.LimitExceeded;
				return false;
			}

			int copiedHeaderLength = Math.Min(frame.Length, HeaderLength);
			frame[..copiedHeaderLength].CopyTo(headerScratch);
			if (!TryValidateHeader(
				headerScratch[..copiedHeaderLength],
				frame.Length,
				expectedSourceEpochScratch,
				out ResponseHeader header,
				out errorCode))
			{
				return false;
			}

			if (frame.Length > MaximumReachableFrameLength)
			{
				errorCode = LiquidWalletFactsWireErrorCode.LimitExceeded;
				return false;
			}

			ownedFrame = frame.ToArray();
			headerScratch.Clear();
			ownedFrame.AsSpan(0, HeaderLength).CopyTo(headerScratch);
			if (!TryValidateHeader(
				headerScratch,
				ownedFrame.Length,
				expectedSourceEpochScratch,
				out header,
				out errorCode))
			{
				return false;
			}

			if (!TryValidateLayout(ownedFrame, header, out errorCode) ||
				!TryValidateInputUniqueness(ownedFrame, header, out errorCode))
			{
				return false;
			}

			transactionOffsets = BuildTransactionOffsets(ownedFrame, header.TransactionCount);
			response = new LiquidWalletFactsWireV1Response(ownedFrame, transactionOffsets);
			ownershipTransferred = true;
			errorCode = LiquidWalletFactsWireErrorCode.None;
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(expectedSourceEpochScratch);
			CryptographicOperations.ZeroMemory(headerScratch);
			if (!ownershipTransferred)
			{
				if (ownedFrame is not null)
				{
					CryptographicOperations.ZeroMemory(ownedFrame);
				}

				if (transactionOffsets is not null)
				{
					Array.Clear(transactionOffsets);
				}
			}
		}
	}

	private static bool TryValidateHeader(
		ReadOnlySpan<byte> header,
		int frameLength,
		ReadOnlySpan<byte> expectedSourceEpoch,
		out ResponseHeader responseHeader,
		out LiquidWalletFactsWireErrorCode errorCode)
	{
		responseHeader = default;
		if (header.Length < 4)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (!header[..4].SequenceEqual(ResponseMagic))
		{
			errorCode = LiquidWalletFactsWireErrorCode.VersionMismatch;
			return false;
		}

		if (header.Length < 6)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]) != 1)
		{
			errorCode = LiquidWalletFactsWireErrorCode.VersionMismatch;
			return false;
		}

		if (header.Length < 8)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]) != HeaderLength)
		{
			errorCode = LiquidWalletFactsWireErrorCode.VersionMismatch;
			return false;
		}

		if (header.Length < 16)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		ulong declaredLength = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
		if (declaredLength != (ulong)frameLength)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (header.Length < 20)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]) != 0)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (header.Length < 24)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		uint transactionCount = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
		if (header.Length < 28)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		uint ownedOutputCount = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
		if (header.Length < 32)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]) != 0)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (header.Length < HeaderLength)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		ReadOnlySpan<byte> sourceEpoch = header[32..HeaderLength];
		if (!IsNonzero(sourceEpoch))
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		if (!sourceEpoch.SequenceEqual(expectedSourceEpoch))
		{
			errorCode = LiquidWalletFactsWireErrorCode.SourceBindingMismatch;
			return false;
		}

		if (transactionCount > MaximumTransactionCount ||
			ownedOutputCount > MaximumAggregateOwnedOutputCount)
		{
			errorCode = LiquidWalletFactsWireErrorCode.LimitExceeded;
			return false;
		}

		responseHeader = new ResponseHeader((int)transactionCount, (int)ownedOutputCount);
		errorCode = LiquidWalletFactsWireErrorCode.None;
		return true;
	}

	private static bool TryValidateLayout(
		ReadOnlySpan<byte> frame,
		ResponseHeader header,
		out LiquidWalletFactsWireErrorCode errorCode)
	{
		var reader = new WireReader(frame, HeaderLength);
		Span<byte> previousTransactionId = stackalloc byte[32];
		bool hasPreviousTransactionId = false;
		int totalInputCount = 0;
		int totalOwnedOutputCount = 0;

		try
		{
			for (int transactionIndex = 0; transactionIndex < header.TransactionCount; transactionIndex++)
			{
				if (!reader.TryTake(32, out ReadOnlySpan<byte> transactionId) ||
					!reader.TryTake(32, out _) ||
					!reader.TryReadUInt32(out uint inputCountValue) ||
					!reader.TryReadUInt32(out uint ownedOutputCountValue))
				{
					errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
					return false;
				}

				if (!IsNonzero(transactionId) ||
					(hasPreviousTransactionId && previousTransactionId.SequenceCompareTo(transactionId) >= 0) ||
					inputCountValue == 0)
				{
					errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
					return false;
				}

				if (inputCountValue > MaximumInputCountPerTransaction ||
					ownedOutputCountValue > MaximumOwnedOutputCountPerTransaction)
				{
					errorCode = LiquidWalletFactsWireErrorCode.LimitExceeded;
					return false;
				}

				int inputCount = (int)inputCountValue;
				int ownedOutputCount = (int)ownedOutputCountValue;
				if (inputCount > MaximumAggregateInputCount - totalInputCount ||
					ownedOutputCount > MaximumAggregateOwnedOutputCount - totalOwnedOutputCount)
				{
					errorCode = LiquidWalletFactsWireErrorCode.LimitExceeded;
					return false;
				}

				totalInputCount += inputCount;
				totalOwnedOutputCount += ownedOutputCount;
				transactionId.CopyTo(previousTransactionId);
				hasPreviousTransactionId = true;

				for (int inputIndex = 0; inputIndex < inputCount; inputIndex++)
				{
					if (!reader.TryTake(32, out ReadOnlySpan<byte> previousTransactionIdValue) ||
						!reader.TryReadUInt32(out uint previousOutputIndex))
					{
						errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
						return false;
					}

					if (!IsNonzero(previousTransactionIdValue) || previousOutputIndex > MaximumSpendableOutputIndex)
					{
						errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
						return false;
					}
				}

				uint previousOutputIndexValue = 0;
				bool hasPreviousOutputIndex = false;
				for (int outputIndex = 0; outputIndex < ownedOutputCount; outputIndex++)
				{
					if (!reader.TryReadUInt32(out uint observedOutputIndex) ||
						!reader.TryReadUInt32(out uint scriptLength) ||
						!reader.TryTake(33, out ReadOnlySpan<byte> spendPublicKey) ||
						!reader.TryTake(33, out ReadOnlySpan<byte> blindingPublicKey) ||
						!reader.TryReadByte(out byte branch) ||
						!reader.TryTake(3, out ReadOnlySpan<byte> reserved) ||
						!reader.TryReadUInt32(out uint derivationIndex) ||
						!reader.TryTake(32, out ReadOnlySpan<byte> assetId) ||
						!reader.TryReadUInt64(out ulong value))
					{
						errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
						return false;
					}

					if (scriptLength != NativeP2WpkhScriptLength ||
						!reader.TryTake(NativeP2WpkhScriptLength, out ReadOnlySpan<byte> scriptPubKey))
					{
						errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
						return false;
					}

					if (observedOutputIndex > MaximumSpendableOutputIndex ||
						(hasPreviousOutputIndex && previousOutputIndexValue >= observedOutputIndex) ||
						branch > 1 ||
						!IsAllZero(reserved) ||
						derivationIndex > MaximumDerivationIndex ||
						!IsNonzero(assetId) ||
						value == 0 ||
						value > MaximumOwnedOutputValue ||
						!ValidatesObservedPublicOutput(scriptPubKey, spendPublicKey, blindingPublicKey))
					{
						errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
						return false;
					}

					previousOutputIndexValue = observedOutputIndex;
					hasPreviousOutputIndex = true;
				}
			}

			if (totalOwnedOutputCount != header.OwnedOutputCount || !reader.IsAtEnd)
			{
				errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
				return false;
			}

			errorCode = LiquidWalletFactsWireErrorCode.None;
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(previousTransactionId);
		}
	}

	private static bool TryValidateInputUniqueness(
		ReadOnlySpan<byte> frame,
		ResponseHeader header,
		out LiquidWalletFactsWireErrorCode errorCode)
	{
		var reader = new WireReader(frame, HeaderLength);
		for (int transactionIndex = 0; transactionIndex < header.TransactionCount; transactionIndex++)
		{
			if (!reader.TryTake(64, out _) ||
				!reader.TryReadUInt32(out uint inputCountValue) ||
				!reader.TryReadUInt32(out uint ownedOutputCountValue))
			{
				errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
				return false;
			}

			int inputCount = (int)inputCountValue;
			byte[] scratch = GC.AllocateUninitializedArray<byte>(checked(inputCount * InputLength));
			try
			{
				for (int inputIndex = 0; inputIndex < inputCount; inputIndex++)
				{
					if (!reader.TryTake(InputLength, out ReadOnlySpan<byte> outPoint))
					{
						errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
						return false;
					}

					outPoint.CopyTo(scratch.AsSpan(inputIndex * InputLength, InputLength));
				}

				SortOutPoints(scratch, inputCount);
				for (int inputIndex = 1; inputIndex < inputCount; inputIndex++)
				{
					if (scratch.AsSpan((inputIndex - 1) * InputLength, InputLength)
						.SequenceEqual(scratch.AsSpan(inputIndex * InputLength, InputLength)))
					{
						errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
						return false;
					}
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(scratch);
			}

			int ownedOutputBytes = checked((int)ownedOutputCountValue * OwnedOutputLength);
			if (!reader.TryTake(ownedOutputBytes, out _))
			{
				errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
				return false;
			}
		}

		if (!reader.IsAtEnd)
		{
			errorCode = LiquidWalletFactsWireErrorCode.InvalidEncoding;
			return false;
		}

		errorCode = LiquidWalletFactsWireErrorCode.None;
		return true;
	}

	private static int[] BuildTransactionOffsets(ReadOnlySpan<byte> frame, int transactionCount)
	{
		var reader = new WireReader(frame, HeaderLength);
		int[] offsets = new int[transactionCount];
		try
		{
			for (int transactionIndex = 0; transactionIndex < transactionCount; transactionIndex++)
			{
				offsets[transactionIndex] = reader.Offset;
				if (!reader.TryTake(64, out _) ||
					!reader.TryReadUInt32(out uint inputCount) ||
					!reader.TryReadUInt32(out uint ownedOutputCount) ||
					!reader.TryTake(checked((int)inputCount * InputLength), out _) ||
					!reader.TryTake(checked((int)ownedOutputCount * OwnedOutputLength), out _))
				{
					throw new InvalidOperationException("Validated wallet facts wire layout is unavailable.");
				}
			}

			if (!reader.IsAtEnd)
			{
				throw new InvalidOperationException("Validated wallet facts wire layout is unavailable.");
			}

			return offsets;
		}
		catch
		{
			Array.Clear(offsets);
			throw;
		}
	}

	private static bool ValidatesObservedPublicOutput(
		ReadOnlySpan<byte> scriptPubKey,
		ReadOnlySpan<byte> spendPublicKey,
		ReadOnlySpan<byte> blindingPublicKey)
	{
		byte[] spendPublicKeyBytes = spendPublicKey.ToArray();
		byte[] blindingPublicKeyBytes = blindingPublicKey.ToArray();
		byte[]? expectedScriptPubKey = null;
		try
		{
			var spendKey = new PubKey(spendPublicKeyBytes);
			var blindingKey = new PubKey(blindingPublicKeyBytes);
			if (!spendKey.IsCompressed || !blindingKey.IsCompressed)
			{
				return false;
			}

			expectedScriptPubKey = spendKey.WitHash.ScriptPubKey.ToBytes();
			return scriptPubKey.SequenceEqual(expectedScriptPubKey);
		}
		catch (Exception exception) when (exception is ArgumentException or FormatException)
		{
			return false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(spendPublicKeyBytes);
			CryptographicOperations.ZeroMemory(blindingPublicKeyBytes);
			if (expectedScriptPubKey is not null)
			{
				CryptographicOperations.ZeroMemory(expectedScriptPubKey);
			}
		}
	}

	private static void SortOutPoints(Span<byte> scratch, int count)
	{
		for (int root = (count / 2) - 1; root >= 0; root--)
		{
			SiftDown(scratch, root, count);
		}

		for (int end = count - 1; end > 0; end--)
		{
			SwapOutPoints(scratch, 0, end);
			SiftDown(scratch, 0, end);
		}
	}

	private static void SiftDown(Span<byte> scratch, int root, int count)
	{
		while (true)
		{
			int child = (root * 2) + 1;
			if (child >= count)
			{
				return;
			}

			if (child + 1 < count && CompareOutPoints(scratch, child, child + 1) < 0)
			{
				child++;
			}

			if (CompareOutPoints(scratch, root, child) >= 0)
			{
				return;
			}

			SwapOutPoints(scratch, root, child);
			root = child;
		}
	}

	private static int CompareOutPoints(ReadOnlySpan<byte> scratch, int left, int right) =>
		scratch.Slice(left * InputLength, InputLength)
			.SequenceCompareTo(scratch.Slice(right * InputLength, InputLength));

	private static void SwapOutPoints(Span<byte> scratch, int left, int right)
	{
		if (left == right)
		{
			return;
		}

		int leftOffset = left * InputLength;
		int rightOffset = right * InputLength;
		for (int index = 0; index < InputLength; index++)
		{
			(scratch[leftOffset + index], scratch[rightOffset + index]) =
				(scratch[rightOffset + index], scratch[leftOffset + index]);
		}
	}

	private static bool IsNonzero(ReadOnlySpan<byte> value) => value.ContainsAnyExcept((byte)0);

	private static bool IsAllZero(ReadOnlySpan<byte> value) => !IsNonzero(value);

	private readonly struct ResponseHeader
	{
		public ResponseHeader(int transactionCount, int ownedOutputCount)
		{
			TransactionCount = transactionCount;
			OwnedOutputCount = ownedOutputCount;
		}

		public int TransactionCount { get; }
		public int OwnedOutputCount { get; }
	}

	private ref struct WireReader
	{
		private readonly ReadOnlySpan<byte> _frame;
		private int _offset;

		public WireReader(ReadOnlySpan<byte> frame, int offset)
		{
			_frame = frame;
			_offset = offset;
		}

		public readonly int Offset => _offset;
		public readonly bool IsAtEnd => _offset == _frame.Length;

		public bool TryReadByte(out byte value)
		{
			if (!TryTake(sizeof(byte), out ReadOnlySpan<byte> bytes))
			{
				value = 0;
				return false;
			}

			value = bytes[0];
			return true;
		}

		public bool TryReadUInt32(out uint value)
		{
			if (!TryTake(sizeof(uint), out ReadOnlySpan<byte> bytes))
			{
				value = 0;
				return false;
			}

			value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
			return true;
		}

		public bool TryReadUInt64(out ulong value)
		{
			if (!TryTake(sizeof(ulong), out ReadOnlySpan<byte> bytes))
			{
				value = 0;
				return false;
			}

			value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
			return true;
		}

		public bool TryTake(int length, out ReadOnlySpan<byte> value)
		{
			if (length < 0 || length > _frame.Length - _offset)
			{
				value = default;
				return false;
			}

			value = _frame.Slice(_offset, length);
			_offset += length;
			return true;
		}
	}
}
