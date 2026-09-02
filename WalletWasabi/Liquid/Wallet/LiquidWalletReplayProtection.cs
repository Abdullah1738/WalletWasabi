using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// A uniform, privacy-redacted failure to authenticate or reconstruct a
/// protected Liquid wallet replay cache.
/// </summary>
internal sealed class LiquidWalletReplayProtectionException : Exception
{
	public LiquidWalletReplayProtectionException()
		: base("The protected Liquid wallet replay cache is invalid.")
	{
	}
}

/// <summary>
/// A privacy-redacted signal that the temporary bounded replay cache cannot
/// represent the wallet history and the caller must rebuild from chain data.
/// </summary>
internal sealed class LiquidWalletReplayCapacityException : Exception
{
	public LiquidWalletReplayCapacityException()
		: base("The Liquid wallet replay cache exceeded its temporary capacity; a chain rescan is required.")
	{
	}
}

/// <summary>
/// The authenticated result of opening a protected Liquid wallet replay
/// cache. Generation is caller-defined metadata and is not an anti-rollback
/// or freshness claim.
/// </summary>
internal sealed class LiquidWalletReplayOpenResult
{
	public LiquidWalletReplayOpenResult(
		ulong generation,
		ulong externalIndexHighWater,
		ulong internalIndexHighWater,
		LiquidWalletReplaySnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Generation = generation;
		ExternalIndexHighWater = externalIndexHighWater;
		InternalIndexHighWater = internalIndexHighWater;
		Snapshot = snapshot;
	}

	public ulong Generation { get; }
	public ulong ExternalIndexHighWater { get; }
	public ulong InternalIndexHighWater { get; }
	public LiquidWalletReplaySnapshot Snapshot { get; }

	/// <summary>The number of durable receive-label bindings carried by the payload.</summary>
	public int ReceiveLabelCount => Snapshot.ReceiveLabelCount;

	/// <summary>The durable label set bound to a receive derivation index, or null when absent.</summary>
	public bool TryGetReceiveLabels(uint index, out LiquidWalletLabelSet? labels) =>
		Snapshot.TryGetReceiveLabels(index, out labels);

	public override string ToString() => nameof(LiquidWalletReplayOpenResult);
}

/// <summary>
/// Strict wallet-owned binary codec for the envelope-owned payload bytes needed
/// to reconstruct a Liquid wallet replay cache. Versioning and authentication
/// belong to <see cref="LiquidWalletReplayProtectedPayload"/>; these bytes are
/// not a standalone persistence format. They contain private wallet metadata
/// in plaintext, must remain in memory, be cleared by the caller, and must
/// never be persisted or logged.
/// </summary>
internal static class LiquidWalletReplayCodec
{
	internal const int MaxCanonicalLength = LiquidWalletReplayProtectedPayload.MaxCanonicalLength;
	// This is a wallet-lifetime replay-cache ceiling, not an observation-batch limit.
	internal const int MaxDeltaCount = 4_096;
	internal const int MaxConfirmationCount = 4_096;
	internal const int MaxSpentPerDelta = 102_298;
	internal const int MaxCreatedPerDelta = 9_279;
	internal const int MaxAggregateSpent = 1_636_801;
	internal const int MaxAggregateCreated = 148_470;
	internal const int MaxScriptLength = 10_000;
	internal const int MaxAggregateScriptLength = 16_777_216;
	// The receive-label map is capacity-bounded like every other collection:
	// an entry-count ceiling and an aggregate label-byte ceiling, consistent
	// with the LiquidWalletLabelSet per-set limits.
	internal const int MaxReceiveLabelEntryCount = 4_096;
	internal const int MaxAggregateReceiveLabelUtf8Bytes = 262_144;

	private const int AssetIdLength = LiquidAssetId.ConsensusByteLength;
	private const int TransactionIdLength = LiquidTransactionId.ConsensusByteLength;
	private const int OutPointLength = LiquidOutPoint.ConsensusByteLength;
	private const int CompressedPublicKeyLength = 33;
	private const int BlockHashLength = 32;
	private const int CreatedOutputFixedLength =
		OutPointLength + sizeof(uint) + AssetIdLength + sizeof(long) + sizeof(byte) +
		sizeof(uint) + CompressedPublicKeyLength;
	private const int ConfirmationLength = TransactionIdLength + BlockHashLength + sizeof(uint);
	private const int MinimumDeltaLength = TransactionIdLength + sizeof(uint) + sizeof(uint);

	public static byte[] Encode(LiquidWalletReplaySnapshot snapshot) =>
		Encode(snapshot, includeReceiveLabels: true);

	// The legacy (v1/v2/v3) canonical encoding carries no receive-label map;
	// the v4 encoding appends it. Only the protected-payload seal uses the
	// label-carrying form; the legacy test envelope builder uses the
	// label-free form so a labeled snapshot still yields legacy canonical
	// bytes.
	internal static byte[] Encode(LiquidWalletReplaySnapshot snapshot, bool includeReceiveLabels)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		byte[] encoded = EncodeCore(snapshot, includeReceiveLabels);
		byte[]? canonical = null;
		try
		{
			LiquidWalletReplaySnapshot reconstructed = LiquidWalletState
				.RestoreReplaySnapshot(snapshot)
				.ExportReplaySnapshot();
			canonical = EncodeCore(reconstructed, includeReceiveLabels);
			if (!CryptographicOperations.FixedTimeEquals(encoded, canonical))
			{
				throw new InvalidOperationException("The replay cache snapshot is not canonical.");
			}
			return encoded;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(encoded);
			throw;
		}
		finally
		{
			if (canonical is not null)
			{
				CryptographicOperations.ZeroMemory(canonical);
			}
		}
	}

	private static byte[] EncodeCore(LiquidWalletReplaySnapshot snapshot, bool includeReceiveLabels)
	{

		IReadOnlyList<LiquidWalletTransactionDelta> deltas = snapshot.GetDeltas();
		IReadOnlyList<LiquidWalletReplayConfirmation> confirmations = snapshot.GetConfirmations();
		IReadOnlyList<LiquidWalletReceiveLabelEntry> receiveLabels = includeReceiveLabels
			? snapshot.GetReceiveLabels()
			: [];
		ValidateCapacity(deltas.Count, MaxDeltaCount);
		ValidateCapacity(confirmations.Count, MaxConfirmationCount);
		ValidateCapacity(receiveLabels.Count, MaxReceiveLabelEntryCount);
		if (confirmations.Count > deltas.Count)
		{
			throw new ArgumentException(
				"A replay cache cannot contain more confirmations than transaction deltas.",
				nameof(snapshot));
		}

		long encodedLength = AssetIdLength + sizeof(ulong) + sizeof(uint) + sizeof(uint);
		long aggregateSpent = 0;
		long aggregateCreated = 0;
		long aggregateScriptLength = 0;
		foreach (LiquidWalletTransactionDelta delta in deltas)
		{
			ArgumentNullException.ThrowIfNull(delta, nameof(snapshot));
			IReadOnlyList<LiquidOutPoint> spent = delta.GetSpentOutPoints();
			IReadOnlyList<LiquidOwnedOutput> created = delta.GetCreatedOutputs();
			ValidateCapacity(spent.Count, MaxSpentPerDelta);
			ValidateCapacity(created.Count, MaxCreatedPerDelta);

			aggregateSpent = checked(aggregateSpent + spent.Count);
			aggregateCreated = checked(aggregateCreated + created.Count);
			if (aggregateSpent > MaxAggregateSpent || aggregateCreated > MaxAggregateCreated)
			{
				throw new LiquidWalletReplayCapacityException();
			}

			encodedLength = checked(encodedLength + MinimumDeltaLength + ((long)spent.Count * OutPointLength));
			foreach (LiquidOwnedOutput output in created)
			{
				ArgumentNullException.ThrowIfNull(output, nameof(snapshot));
				if (output.SpendKey.Index > LiquidSpendKeyReference.MaximumIndex)
				{
					throw new ArgumentException(
						"The replay cache contains an unsupported derivation index.",
						nameof(snapshot));
				}
				byte[] scriptPubKey = output.GetScriptPubKey();
				try
				{
					ValidateCapacity(scriptPubKey.Length, MaxScriptLength);
					aggregateScriptLength = checked(aggregateScriptLength + scriptPubKey.Length);
					if (aggregateScriptLength > MaxAggregateScriptLength)
					{
						throw new LiquidWalletReplayCapacityException();
					}
					encodedLength = checked(encodedLength + CreatedOutputFixedLength + scriptPubKey.Length);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(scriptPubKey);
				}
			}
		}

		encodedLength = checked(encodedLength + ((long)confirmations.Count * ConfirmationLength));

		var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
		long aggregateReceiveLabelUtf8Bytes = 0;
		if (includeReceiveLabels)
		{
			encodedLength = checked(encodedLength + sizeof(uint));
			foreach (LiquidWalletReceiveLabelEntry entry in receiveLabels)
			{
				ArgumentNullException.ThrowIfNull(entry, nameof(snapshot));
				IReadOnlyList<string> entryLabels = entry.Labels.GetLabels();
				encodedLength = checked(encodedLength + sizeof(uint) + sizeof(uint));
				foreach (string label in entryLabels)
				{
					int labelLength = strictUtf8.GetByteCount(label);
					aggregateReceiveLabelUtf8Bytes = checked(aggregateReceiveLabelUtf8Bytes + labelLength);
					if (aggregateReceiveLabelUtf8Bytes > MaxAggregateReceiveLabelUtf8Bytes)
					{
						throw new LiquidWalletReplayCapacityException();
					}
					encodedLength = checked(encodedLength + sizeof(uint) + labelLength);
				}
			}
		}

		if (encodedLength > MaxCanonicalLength || encodedLength > int.MaxValue)
		{
			throw new LiquidWalletReplayCapacityException();
		}

		byte[] encoded = new byte[(int)encodedLength];
		try
		{
			EncodeInto(encoded, snapshot, deltas, confirmations, receiveLabels, includeReceiveLabels, strictUtf8);
			return encoded;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(encoded);
			throw;
		}
	}

	private static void EncodeInto(
		Span<byte> encoded,
		LiquidWalletReplaySnapshot snapshot,
		IReadOnlyList<LiquidWalletTransactionDelta> deltas,
		IReadOnlyList<LiquidWalletReplayConfirmation> confirmations,
		IReadOnlyList<LiquidWalletReceiveLabelEntry> receiveLabels,
		bool includeReceiveLabels,
		UTF8Encoding strictUtf8)
	{
		var writer = new ReplayWriter(encoded);
		WriteAssetId(ref writer, snapshot.PeggedAssetId);
		writer.WriteUInt64(snapshot.Revision);
		writer.WriteUInt32((uint)deltas.Count);
		foreach (LiquidWalletTransactionDelta delta in deltas)
		{
			WriteTransactionId(ref writer, delta.TransactionId);
			IReadOnlyList<LiquidOutPoint> spent = delta.GetSpentOutPoints();
			writer.WriteUInt32((uint)spent.Count);
			foreach (LiquidOutPoint outPoint in spent)
			{
				WriteOutPoint(ref writer, outPoint);
			}

			IReadOnlyList<LiquidOwnedOutput> created = delta.GetCreatedOutputs();
			writer.WriteUInt32((uint)created.Count);
			foreach (LiquidOwnedOutput output in created)
			{
				byte[] scriptPubKey = output.GetScriptPubKey();
				byte[] compressedPublicKey = output.SpendKey.GetCompressedPublicKey();
				try
				{
					WriteOutPoint(ref writer, output.OutPoint);
					writer.WriteUInt32((uint)scriptPubKey.Length);
					writer.WriteBytes(scriptPubKey);
					WriteAssetId(ref writer, output.Amount.AssetId);
					writer.WriteInt64(output.Amount.AtomicUnits);
					writer.WriteByte((byte)output.SpendKey.Branch);
					writer.WriteUInt32(output.SpendKey.Index);
					writer.WriteBytes(compressedPublicKey);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(scriptPubKey);
					CryptographicOperations.ZeroMemory(compressedPublicKey);
				}
			}
		}

		writer.WriteUInt32((uint)confirmations.Count);
		foreach (LiquidWalletReplayConfirmation confirmation in confirmations)
		{
			WriteTransactionId(ref writer, confirmation.TransactionId);
			byte[] consensusBlockHash = ParseCanonicalHash(
				confirmation.Confirmation.CanonicalBlockHash);
			try
			{
				// RPC hashes are display-ordered; this codec stores the reversed
				// 32-byte consensus representation, matching transaction identifiers.
				writer.WriteBytes(consensusBlockHash);
				writer.WriteUInt32(confirmation.Confirmation.Height);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(consensusBlockHash);
			}
		}

		if (includeReceiveLabels)
		{
			writer.WriteUInt32((uint)receiveLabels.Count);
			foreach (LiquidWalletReceiveLabelEntry entry in receiveLabels)
			{
				writer.WriteUInt32(entry.Index);
				IReadOnlyList<string> entryLabels = entry.Labels.GetLabels();
				writer.WriteUInt32((uint)entryLabels.Count);
				foreach (string label in entryLabels)
				{
					byte[] labelBytes = strictUtf8.GetBytes(label);
					writer.WriteUInt32((uint)labelBytes.Length);
					writer.WriteBytes(labelBytes);
				}
			}
		}
		writer.EnsureComplete();
	}

	public static LiquidWalletReplaySnapshot Decode(ReadOnlySpan<byte> encoded) =>
		Decode(encoded, includeReceiveLabels: true);

	internal static LiquidWalletReplaySnapshot Decode(ReadOnlySpan<byte> encoded, bool includeReceiveLabels)
	{
		if (encoded.Length > MaxCanonicalLength)
		{
			throw new InvalidDataException("The replay cache exceeds its canonical payload limit.");
		}

		var reader = new ReplayReader(encoded);
		LiquidAssetId peggedAsset = LiquidAssetId.ParseConsensusBytes(reader.ReadBytes(AssetIdLength));
		ulong revision = reader.ReadUInt64();
		int deltaCount = reader.ReadBoundedCount(MaxDeltaCount, MinimumDeltaLength);
		var deltas = new List<LiquidWalletTransactionDelta>(deltaCount);
		long aggregateSpent = 0;
		long aggregateCreated = 0;
		long aggregateScriptLength = 0;
		for (int deltaIndex = 0; deltaIndex < deltaCount; deltaIndex++)
		{
			LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(
				reader.ReadBytes(TransactionIdLength));
			int spentCount = reader.ReadBoundedCount(MaxSpentPerDelta, OutPointLength);
			aggregateSpent = checked(aggregateSpent + spentCount);
			if (aggregateSpent > MaxAggregateSpent)
			{
				throw new InvalidDataException("The replay cache exceeds its aggregate spent-output limit.");
			}
			var spent = new List<LiquidOutPoint>(spentCount);
			for (int spentIndex = 0; spentIndex < spentCount; spentIndex++)
			{
				spent.Add(LiquidOutPoint.ParseSpendableConsensusBytes(reader.ReadBytes(OutPointLength)));
			}

			int createdCount = reader.ReadBoundedCount(MaxCreatedPerDelta, CreatedOutputFixedLength);
			aggregateCreated = checked(aggregateCreated + createdCount);
			if (aggregateCreated > MaxAggregateCreated)
			{
				throw new InvalidDataException("The replay cache exceeds its aggregate created-output limit.");
			}
			var created = new List<LiquidOwnedOutput>(createdCount);
			for (int createdIndex = 0; createdIndex < createdCount; createdIndex++)
			{
				LiquidOutPoint outPoint = LiquidOutPoint.ParseSpendableConsensusBytes(
					reader.ReadBytes(OutPointLength));
				int scriptLength = reader.ReadBoundedCount(MaxScriptLength, sizeof(byte));
				aggregateScriptLength = checked(aggregateScriptLength + scriptLength);
				if (aggregateScriptLength > MaxAggregateScriptLength)
				{
					throw new InvalidDataException("The replay cache exceeds its aggregate script limit.");
				}
				ReadOnlySpan<byte> scriptPubKey = reader.ReadBytes(scriptLength);
				LiquidAssetId assetId = LiquidAssetId.ParseConsensusBytes(reader.ReadBytes(AssetIdLength));
				long atomicUnits = reader.ReadInt64();
				LiquidKeyBranch branch = (LiquidKeyBranch)reader.ReadByte();
				uint keyIndex = reader.ReadUInt32();
				ReadOnlySpan<byte> compressedPublicKey = reader.ReadBytes(CompressedPublicKeyLength);
				LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
					compressedPublicKey,
					branch,
					keyIndex);
				LiquidAssetAmount amount = LiquidAssetAmount.Create(assetId, peggedAsset, atomicUnits);
				created.Add(LiquidOwnedOutput.Create(outPoint, scriptPubKey, amount, spendKey));
			}

			deltas.Add(LiquidWalletTransactionDelta.Create(transactionId, spent, created));
		}

		int confirmationCount = reader.ReadBoundedCount(MaxConfirmationCount, ConfirmationLength);
		if (confirmationCount > deltaCount)
		{
			throw new InvalidDataException("The replay cache has more confirmations than transaction deltas.");
		}
		var confirmations = new List<LiquidWalletReplayConfirmation>(confirmationCount);
		for (int confirmationIndex = 0; confirmationIndex < confirmationCount; confirmationIndex++)
		{
			LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(
				reader.ReadBytes(TransactionIdLength));
			string blockHash = FormatCanonicalHash(reader.ReadBytes(BlockHashLength));
			uint height = reader.ReadUInt32();
			confirmations.Add(LiquidWalletReplayConfirmation.Create(
				transactionId,
				LiquidConfirmation.Create(blockHash, height)));
		}

		var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
		var receiveLabels = new List<LiquidWalletReceiveLabelEntry>();
		if (includeReceiveLabels)
		{
			int receiveLabelCount = reader.ReadBoundedCount(MaxReceiveLabelEntryCount, sizeof(uint) + sizeof(uint));
			long aggregateReceiveLabelUtf8Bytes = 0;
			uint previousIndex = 0;
			for (int entryIndex = 0; entryIndex < receiveLabelCount; entryIndex++)
			{
				uint labelIndex = reader.ReadUInt32();
				if (entryIndex > 0 && labelIndex <= previousIndex)
				{
					throw new InvalidDataException("The replay cache receive-label indices are not strictly increasing.");
				}
				previousIndex = labelIndex;

				int labelCount = reader.ReadBoundedCount(LiquidWalletLabelSet.MaximumLabelCount, sizeof(uint));
				var labels = new string[labelCount];
				for (int labelEntryIndex = 0; labelEntryIndex < labelCount; labelEntryIndex++)
				{
					int labelLength = reader.ReadBoundedCount(LiquidWalletLabelSet.MaximumLabelUtf8ByteCount, sizeof(byte));
					aggregateReceiveLabelUtf8Bytes = checked(aggregateReceiveLabelUtf8Bytes + labelLength);
					if (aggregateReceiveLabelUtf8Bytes > MaxAggregateReceiveLabelUtf8Bytes)
					{
						throw new InvalidDataException("The replay cache exceeds its aggregate receive-label limit.");
					}
					labels[labelEntryIndex] = strictUtf8.GetString(reader.ReadBytes(labelLength));
				}

				receiveLabels.Add(LiquidWalletReceiveLabelEntry.Create(
					labelIndex,
					LiquidWalletLabelSet.Create(labels)));
			}
		}
		reader.EnsureComplete();

		LiquidWalletReplaySnapshot decoded = LiquidWalletReplaySnapshot.Create(
			peggedAsset,
			revision,
			deltas,
			confirmations,
			receiveLabels);
		LiquidWalletReplaySnapshot reconstructed = LiquidWalletState
			.RestoreReplaySnapshot(decoded)
			.ExportReplaySnapshot();
		byte[] canonical = EncodeCore(reconstructed, includeReceiveLabels);
		try
		{
			if (!CryptographicOperations.FixedTimeEquals(encoded, canonical))
			{
				throw new InvalidDataException("The replay cache encoding is not canonical.");
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(canonical);
		}

		return reconstructed;
	}

	private static void ValidateCapacity(int count, int maximum)
	{
		if (count < 0 || count > maximum)
		{
			throw new LiquidWalletReplayCapacityException();
		}
	}

	private static void WriteAssetId(ref ReplayWriter writer, LiquidAssetId assetId)
	{
		Span<byte> consensusBytes = stackalloc byte[AssetIdLength];
		try
		{
			assetId.WriteConsensusBytes(consensusBytes);
			writer.WriteBytes(consensusBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(consensusBytes);
		}
	}

	private static void WriteTransactionId(
		ref ReplayWriter writer,
		LiquidTransactionId transactionId)
	{
		Span<byte> consensusBytes = stackalloc byte[TransactionIdLength];
		try
		{
			transactionId.WriteConsensusBytes(consensusBytes);
			writer.WriteBytes(consensusBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(consensusBytes);
		}
	}

	private static void WriteOutPoint(ref ReplayWriter writer, LiquidOutPoint outPoint)
	{
		Span<byte> consensusBytes = stackalloc byte[OutPointLength];
		try
		{
			outPoint.WriteConsensusBytes(consensusBytes);
			writer.WriteBytes(consensusBytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(consensusBytes);
		}
	}

	private static byte[] ParseCanonicalHash(string canonicalHash)
	{
		byte[] result = Convert.FromHexString(canonicalHash);
		if (result.Length != BlockHashLength)
		{
			CryptographicOperations.ZeroMemory(result);
			throw new ArgumentException("A canonical block hash is required.", nameof(canonicalHash));
		}
		Array.Reverse(result);
		return result;
	}

	private static string FormatCanonicalHash(ReadOnlySpan<byte> consensusHash)
	{
		Span<byte> canonical = stackalloc byte[BlockHashLength];
		try
		{
			consensusHash.CopyTo(canonical);
			canonical.Reverse();
			return Convert.ToHexString(canonical).ToLowerInvariant();
		}
		finally
		{
			CryptographicOperations.ZeroMemory(canonical);
		}
	}

	private ref struct ReplayWriter
	{
		private readonly Span<byte> _destination;
		private int _position;

		public ReplayWriter(Span<byte> destination)
		{
			_destination = destination;
			_position = 0;
		}

		public void WriteByte(byte value) => Take(sizeof(byte))[0] = value;

		public void WriteUInt32(uint value) =>
			BinaryPrimitives.WriteUInt32LittleEndian(Take(sizeof(uint)), value);

		public void WriteInt64(long value) =>
			BinaryPrimitives.WriteInt64LittleEndian(Take(sizeof(long)), value);

		public void WriteUInt64(ulong value) =>
			BinaryPrimitives.WriteUInt64LittleEndian(Take(sizeof(ulong)), value);

		public void WriteBytes(scoped ReadOnlySpan<byte> bytes) => bytes.CopyTo(Take(bytes.Length));

		public void EnsureComplete()
		{
			if (_position != _destination.Length)
			{
				throw new InvalidOperationException("The replay cache encoder length is inconsistent.");
			}
		}

		private Span<byte> Take(int length)
		{
			Span<byte> result = _destination.Slice(_position, length);
			_position += length;
			return result;
		}
	}

	private ref struct ReplayReader
	{
		private readonly ReadOnlySpan<byte> _source;
		private int _position;

		public ReplayReader(ReadOnlySpan<byte> source)
		{
			_source = source;
			_position = 0;
		}

		private int Remaining => _source.Length - _position;

		public byte ReadByte() => ReadBytes(sizeof(byte))[0];

		public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));

		public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(sizeof(long)));

		public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong)));

		public int ReadBoundedCount(int maximum, int minimumItemLength)
		{
			uint count = ReadUInt32();
			if (count > maximum || ((ulong)count * (uint)minimumItemLength) > (ulong)Remaining)
			{
				throw new InvalidDataException("The replay cache count is invalid.");
			}
			return (int)count;
		}

		public ReadOnlySpan<byte> ReadBytes(int length)
		{
			if (length < 0 || length > Remaining)
			{
				throw new InvalidDataException("The replay cache is truncated.");
			}
			ReadOnlySpan<byte> result = _source.Slice(_position, length);
			_position += length;
			return result;
		}

		public void EnsureComplete()
		{
			if (Remaining != 0)
			{
				throw new InvalidDataException("The replay cache has trailing data.");
			}
		}
	}
}

/// <summary>
/// An authenticated, padded, in-memory envelope for a Liquid wallet replay
/// cache. It reconstructs cached wallet state only. It carries no spending or
/// blinding key, chain, UTXO, confirmation-source, freshness, or anti-rollback
/// authority and has no persistence or runtime behavior. Callers own the key
/// and external context buffers and are responsible for clearing them.
/// </summary>
internal sealed class LiquidWalletReplayProtectedPayload
{
	internal const int KeyLength = 32;
	internal const int ExternalContextLength = 32;
	internal const int NonceLength = 12;
	internal const int TagLength = 16;
	internal const int PaddingBucketLength = 4_096;

	private const ushort EnvelopeVersion = 1;
	private const ushort LegacyPayloadVersionV1 = 1;
	private const ushort LegacyPayloadVersionV2 = 2;
	private const ushort LegacyPayloadVersionV3 = 3;
	private const ushort PayloadVersion = 4;
	private const ushort Aes256GcmAlgorithm = 1;
	private const int HeaderLength = 48;
	private const int InnerGenerationLength = sizeof(ulong);
	private const int InnerLengthFieldLength = sizeof(uint);
	private const int InnerExternalIndexHighWaterLength = sizeof(ulong);
	private const int InnerInternalIndexHighWaterLength = sizeof(ulong);
	internal const int InnerPrefixLength = InnerGenerationLength + InnerLengthFieldLength;
	// This exact 16 MiB ceiling bounds allocation before payload authentication.
	internal const int MaxPaddedPlaintextLength = 16_777_216;
	internal const int MaxCanonicalLength = MaxPaddedPlaintextLength - InnerPrefixLength;
	internal const int MaxEnvelopeLength = HeaderLength + MaxPaddedPlaintextLength + TagLength;
	private static readonly byte[] Magic = "WLRPENV1"u8.ToArray();

	private readonly byte[] _envelope;

	private LiquidWalletReplayProtectedPayload(byte[] envelope)
	{
		_envelope = envelope;
	}

	public static LiquidWalletReplayProtectedPayload Seal(
		LiquidWalletReplaySnapshot snapshot,
		ulong generation,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		ulong externalIndexHighWater = 0,
		ulong internalIndexHighWater = 0)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ValidateKeyAndContext(key, externalWalletNetworkContext);

		byte[] canonical = LiquidWalletReplayCodec.Encode(snapshot);
		byte[]? plaintext = null;
		byte[]? associatedData = null;
		try
		{
			int innerLength = checked(InnerPrefixLength + canonical.Length +
				InnerExternalIndexHighWaterLength + InnerInternalIndexHighWaterLength);
			int paddedLength = RoundUpToBucket(innerLength);
			if (paddedLength > MaxPaddedPlaintextLength)
			{
				throw new LiquidWalletReplayCapacityException();
			}

			plaintext = new byte[paddedLength];
			BinaryPrimitives.WriteUInt64LittleEndian(plaintext, generation);
			BinaryPrimitives.WriteUInt32LittleEndian(
				plaintext.AsSpan(InnerGenerationLength),
				(uint)canonical.Length);
			canonical.CopyTo(plaintext.AsSpan(InnerPrefixLength));
			BinaryPrimitives.WriteUInt64LittleEndian(
				plaintext.AsSpan(InnerPrefixLength + canonical.Length),
				externalIndexHighWater);
			BinaryPrimitives.WriteUInt64LittleEndian(
				plaintext.AsSpan(InnerPrefixLength + canonical.Length + InnerExternalIndexHighWaterLength),
				internalIndexHighWater);
			RandomNumberGenerator.Fill(plaintext.AsSpan(innerLength));

			byte[] envelope = new byte[checked(HeaderLength + paddedLength + TagLength)];
			Span<byte> header = envelope.AsSpan(0, HeaderLength);
			WriteHeader(header, paddedLength);
			RandomNumberGenerator.Fill(header.Slice(32, NonceLength));
			associatedData = BuildAssociatedData(header, externalWalletNetworkContext);

			Span<byte> ciphertext = envelope.AsSpan(HeaderLength, paddedLength);
			Span<byte> tag = envelope.AsSpan(HeaderLength + paddedLength, TagLength);
			using var aes = new AesGcm(key, TagLength);
			aes.Encrypt(
				header.Slice(32, NonceLength),
				plaintext,
				ciphertext,
				tag,
				associatedData);
			return new LiquidWalletReplayProtectedPayload(envelope);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(canonical);
			if (plaintext is not null)
			{
				CryptographicOperations.ZeroMemory(plaintext);
			}
			if (associatedData is not null)
			{
				CryptographicOperations.ZeroMemory(associatedData);
			}
		}
	}

	public LiquidWalletReplayOpenResult Open(
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext) =>
		Open(_envelope, key, externalWalletNetworkContext);

	public static LiquidWalletReplayOpenResult Open(
		ReadOnlySpan<byte> envelope,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		ValidateKeyAndContext(key, externalWalletNetworkContext);
		byte[]? plaintext = null;
		byte[]? associatedData = null;
		try
		{
			if (envelope.Length < HeaderLength + PaddingBucketLength + TagLength ||
				envelope.Length > MaxEnvelopeLength)
			{
				throw new InvalidDataException();
			}

			ReadOnlySpan<byte> header = envelope[..HeaderLength];
			HeaderValues values = ReadHeader(header, envelope.Length);
			associatedData = BuildAssociatedData(header, externalWalletNetworkContext);
			plaintext = new byte[values.PlaintextLength];
			using (var aes = new AesGcm(key, TagLength))
			{
				aes.Decrypt(
					header.Slice(32, NonceLength),
					envelope.Slice(HeaderLength, values.CiphertextLength),
					envelope.Slice(HeaderLength + values.CiphertextLength, TagLength),
					plaintext,
					associatedData);
			}

			if (plaintext.Length < InnerPrefixLength)
			{
				throw new InvalidDataException();
			}
			ulong generation = BinaryPrimitives.ReadUInt64LittleEndian(plaintext);
			uint canonicalLength = BinaryPrimitives.ReadUInt32LittleEndian(
				plaintext.AsSpan(InnerGenerationLength));
			if (canonicalLength == 0 || canonicalLength > plaintext.Length - InnerPrefixLength)
			{
				throw new InvalidDataException();
			}
			// v4 carries the receive-label map in the canonical payload; v1/v2/v3 import it as empty.
			bool includeReceiveLabels = values.PayloadVersion == PayloadVersion;
			LiquidWalletReplaySnapshot snapshot = LiquidWalletReplayCodec.Decode(
				plaintext.AsSpan(InnerPrefixLength, (int)canonicalLength),
				includeReceiveLabels);
			// v1 carries no high-water; v2 carries only the external high-water; v3/v4 carry both.
			bool hasExternal = values.PayloadVersion is LegacyPayloadVersionV2 or LegacyPayloadVersionV3 or PayloadVersion;
			bool hasInternal = values.PayloadVersion is LegacyPayloadVersionV3 or PayloadVersion;
			ulong externalIndexHighWater = hasExternal
				? BinaryPrimitives.ReadUInt64LittleEndian(
					plaintext.AsSpan(checked(InnerPrefixLength + (int)canonicalLength), InnerExternalIndexHighWaterLength))
				: 0;
			ulong internalIndexHighWater = hasInternal
				? BinaryPrimitives.ReadUInt64LittleEndian(
					plaintext.AsSpan(checked(InnerPrefixLength + (int)canonicalLength + InnerExternalIndexHighWaterLength), InnerInternalIndexHighWaterLength))
				: 0;

			return new LiquidWalletReplayOpenResult(generation, externalIndexHighWater, internalIndexHighWater, snapshot);
		}
		catch (Exception exception) when (
			exception is ArgumentException or
			CryptographicException or
			FormatException or
			InvalidDataException or
			InvalidOperationException or
			LiquidWalletReplayCapacityException or
			OverflowException)
		{
			throw new LiquidWalletReplayProtectionException();
		}
		finally
		{
			if (plaintext is not null)
			{
				CryptographicOperations.ZeroMemory(plaintext);
			}
			if (associatedData is not null)
			{
				CryptographicOperations.ZeroMemory(associatedData);
			}
		}
	}

	public byte[] GetBytes() => [.. _envelope];

	public override string ToString() => nameof(LiquidWalletReplayProtectedPayload);

	private static void ValidateKeyAndContext(
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		if (key.Length != KeyLength)
		{
			throw new ArgumentException("An exact 32-byte replay protection key is required.", nameof(key));
		}
		if (externalWalletNetworkContext.Length != ExternalContextLength)
		{
			throw new ArgumentException(
				"An exact 32-byte external wallet and network context is required.",
				nameof(externalWalletNetworkContext));
		}
	}

	private static int RoundUpToBucket(int length)
	{
		int buckets = checked((length + PaddingBucketLength - 1) / PaddingBucketLength);
		return checked(buckets * PaddingBucketLength);
	}

	private static void WriteHeader(Span<byte> header, int paddedLength)
	{
		header.Clear();
		Magic.CopyTo(header);
		BinaryPrimitives.WriteUInt16LittleEndian(header[8..], EnvelopeVersion);
		BinaryPrimitives.WriteUInt16LittleEndian(header[10..], PayloadVersion);
		BinaryPrimitives.WriteUInt16LittleEndian(header[12..], Aes256GcmAlgorithm);
		BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 0);
		BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)paddedLength);
		BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)paddedLength);
		BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0);
	}

	private static HeaderValues ReadHeader(ReadOnlySpan<byte> header, int envelopeLength)
	{
		ushort payloadVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
		if (!header[..Magic.Length].SequenceEqual(Magic) ||
			BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != EnvelopeVersion ||
			payloadVersion is not (LegacyPayloadVersionV1 or LegacyPayloadVersionV2 or LegacyPayloadVersionV3 or PayloadVersion) ||
			BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) != Aes256GcmAlgorithm ||
			BinaryPrimitives.ReadUInt16LittleEndian(header[14..]) != 0 ||
			BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) != 0 ||
			BinaryPrimitives.ReadUInt32LittleEndian(header[44..]) != 0)
		{
			throw new InvalidDataException();
		}

		uint ciphertextLength = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
		uint plaintextLength = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
		if (ciphertextLength != plaintextLength ||
			plaintextLength == 0 ||
			plaintextLength > MaxPaddedPlaintextLength ||
			plaintextLength % PaddingBucketLength != 0 ||
			ciphertextLength > int.MaxValue ||
			checked(HeaderLength + (int)ciphertextLength + TagLength) != envelopeLength)
		{
			throw new InvalidDataException();
		}

		return new HeaderValues((int)ciphertextLength, (int)plaintextLength, payloadVersion);
	}

	private static byte[] BuildAssociatedData(
		ReadOnlySpan<byte> header,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		byte[] associatedData = new byte[HeaderLength + ExternalContextLength];
		header.CopyTo(associatedData);
		externalWalletNetworkContext.CopyTo(associatedData.AsSpan(HeaderLength));
		return associatedData;
	}

	private readonly record struct HeaderValues(
		int CiphertextLength,
		int PlaintextLength,
		ushort PayloadVersion);
}
