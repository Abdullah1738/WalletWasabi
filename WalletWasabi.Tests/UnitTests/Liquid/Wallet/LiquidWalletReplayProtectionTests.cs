using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletReplayProtectionTests
{
	private const int HeaderLength = 48;
	private const int ConfirmationLength = 68;
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherPeggedAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlockHash = "4444444444444444444444444444444444444444444444444444444444444444";
	private const string OtherBlockHash = "5555555555555555555555555555555555555555555555555555555555555555";
	private const string UniformFailure = "The protected Liquid wallet replay cache is invalid.";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidAssetId OtherPeggedAsset => LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);

	[Fact]
	public void CodecRoundTripsCanonicalMultiassetReplayState()
	{
		LiquidWalletReplaySnapshot expected = CreateReplaySnapshot();
		byte[] encoded = LiquidWalletReplayCodec.Encode(expected);
		byte[]? reencoded = null;
		try
		{
			Assert.Equal(1_000_000, LiquidWalletReplayCodec.MaxReplayWorkUnits);
			LiquidWalletReplaySnapshot decoded = LiquidWalletReplayCodec.Decode(encoded);
			reencoded = LiquidWalletReplayCodec.Encode(decoded);

			Assert.Equal(encoded, reencoded);
			Assert.Equal(expected.Revision, decoded.Revision);
			Assert.Equal(
				expected.GetDeltas().Select(delta => delta.TransactionId),
				decoded.GetDeltas().Select(delta => delta.TransactionId));
			Assert.Equal(expected.GetConfirmations(), decoded.GetConfirmations());
			LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(decoded);
			Assert.Equal(100, restored.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits);
			Assert.Equal(150, restored.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(encoded);
			if (reencoded is not null)
			{
				CryptographicOperations.ZeroMemory(reencoded);
			}
		}
	}

	[Fact]
	public void ProtectedPayloadRoundTripsWithGenerationAndBucketPadding()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(CreateReplaySnapshot(), 73, key, context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletReplayOpenResult opened = protectedPayload.Open(key, context);

			Assert.Equal(73ul, opened.Generation);
			Assert.Equal(4ul, opened.Snapshot.Revision);
			Assert.Equal(0, (envelope.Length - HeaderLength - LiquidWalletReplayProtectedPayload.TagLength) %
				LiquidWalletReplayProtectedPayload.PaddingBucketLength);
			Assert.Equal(nameof(LiquidWalletReplayProtectedPayload), protectedPayload.ToString());
			Assert.Equal(nameof(LiquidWalletReplayOpenResult), opened.ToString());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelope is not null)
			{
				CryptographicOperations.ZeroMemory(envelope);
			}
		}
	}

	[Fact]
	public void SealUsesFreshNonceAndReturnsDefensiveEnvelopeCopies()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? first = null;
		byte[]? second = null;
		byte[]? defensiveCopy = null;
		try
		{
			LiquidWalletReplayProtectedPayload firstPayload =
				LiquidWalletReplayProtectedPayload.Seal(CreateReplaySnapshot(), 1, key, context);
			LiquidWalletReplayProtectedPayload secondPayload =
				LiquidWalletReplayProtectedPayload.Seal(CreateReplaySnapshot(), 1, key, context);
			first = firstPayload.GetBytes();
			second = secondPayload.GetBytes();
			defensiveCopy = firstPayload.GetBytes();

			Assert.Equal(first.Length, second.Length);
			Assert.False(first.AsSpan().SequenceEqual(second));
			Assert.False(first
				.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength)
				.SequenceEqual(second.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength)));
			first[0] ^= 0xff;
			Assert.NotEqual(first[0], defensiveCopy[0]);
			Assert.Equal(1ul, firstPayload.Open(key, context).Generation);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			Zero(first);
			Zero(second);
			Zero(defensiveCopy);
		}
	}

	[Fact]
	public void SealedEnvelopeDoesNotExposeFixtureWalletMetadata()
	{
		const ulong generation = 0x8877665544332211;
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] envelope = LiquidWalletReplayProtectedPayload
			.Seal(CreateReplaySnapshot(), generation, key, context)
			.GetBytes();
		byte[] scriptPubKey = Output(Tx('a'), 0, PeggedAsset, 100).GetScriptPubKey();
		byte[] generationBytes = UInt64Bytes(generation);
		var forbidden = new List<byte[]>
		{
			Encoding.ASCII.GetBytes(Tx('a').CanonicalRpcHex),
			Encoding.ASCII.GetBytes(Tx('b').CanonicalRpcHex),
			Tx('a').ToConsensusBytes(),
			Tx('b').ToConsensusBytes(),
			Encoding.ASCII.GetBytes(PeggedAssetHex),
			Encoding.ASCII.GetBytes(IssuedAssetHex),
			PeggedAsset.ToConsensusBytes(),
			IssuedAsset.ToConsensusBytes(),
			Encoding.ASCII.GetBytes(BlockHash),
			Encoding.ASCII.GetBytes(OtherBlockHash),
			Convert.FromHexString(BlockHash),
			Convert.FromHexString(OtherBlockHash),
			scriptPubKey,
			Convert.FromHexString(PublicKeyHex),
			AmountBytes(100),
			AmountBytes(150),
			AmountBytes(200),
			generationBytes,
		};
		try
		{
			Assert.Equal(-1, envelope.AsSpan(0, HeaderLength).IndexOf(generationBytes));
			foreach (byte[] bytes in forbidden)
			{
				Assert.Equal(-1, envelope.AsSpan().IndexOf(bytes));
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(envelope);
			foreach (byte[] bytes in forbidden)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}
		}
	}

	[Fact]
	public void WrongKeyContextTamperAndHeaderMutationsHaveOnePublicFailure()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] wrongKey = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] wrongContext = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] envelope = LiquidWalletReplayProtectedPayload
			.Seal(CreateReplaySnapshot(), 7, key, context)
			.GetBytes();
		try
		{
			AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open(envelope, wrongKey, context));
			AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open(envelope, key, wrongContext));

			foreach (int offset in new[] { 0, 8, 10, 12, 14, 16, 20, 24, 32, 44, 48, envelope.Length - 1 })
			{
				byte[] mutated = [.. envelope];
				try
				{
					mutated[offset] ^= 0x01;
					AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open(mutated, key, context));
				}
				finally
				{
					CryptographicOperations.ZeroMemory(mutated);
				}
			}

			byte[] truncated = envelope[..^1];
			byte[] appended = [.. envelope, 0];
			try
			{
				AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open(truncated, key, context));
				AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open(appended, key, context));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(truncated);
				CryptographicOperations.ZeroMemory(appended);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(wrongKey);
			CryptographicOperations.ZeroMemory(wrongContext);
			CryptographicOperations.ZeroMemory(envelope);
		}
	}

	[Fact]
	public void AuthenticatedNoncanonicalConfirmationOrderIsRejectedUniformly()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] envelope = LiquidWalletReplayProtectedPayload
			.Seal(CreateReplaySnapshot(), 9, key, context)
			.GetBytes();
		byte[]? noncanonical = null;
		try
		{
			noncanonical = ReverseAuthenticatedConfirmationOrder(envelope, key, context);
			AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open(noncanonical, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(envelope);
			Zero(noncanonical);
		}
	}

	[Fact]
	public void EncodeAndSealRefuseUnreachableOrForeignConfirmationSnapshots()
	{
		LiquidWalletReplaySnapshot unreachable = LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			1,
			[],
			[]);
		LiquidTransactionId transactionId = Tx('a');
		LiquidWalletReplaySnapshot foreignConfirmation = LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			2,
			[Delta(transactionId, [], [Output(transactionId, 0, PeggedAsset, 1)])],
			[LiquidWalletReplayConfirmation.Create(
				Tx('b'),
				LiquidConfirmation.Create(BlockHash, 42))]);
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			foreach (LiquidWalletReplaySnapshot invalid in new[] { unreachable, foreignConfirmation })
			{
				Assert.Throws<InvalidOperationException>(() => LiquidWalletReplayCodec.Encode(invalid));
				Assert.Throws<InvalidOperationException>(() =>
					LiquidWalletReplayProtectedPayload.Seal(invalid, 1, key, context));
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	[Fact]
	public void CodecRejectsTrailingPartialAndOverLimitCounts()
	{
		byte[] encoded = LiquidWalletReplayCodec.Encode(CreateReplaySnapshot());
		byte[] empty = LiquidWalletReplayCodec.Encode(LiquidWalletState.Empty(PeggedAsset).ExportReplaySnapshot());
		byte[] trailing = [.. encoded, 0];
		byte[] partial = encoded[..^1];
		byte[] overLimitDeltas = [.. empty];
		byte[] overLimitConfirmations = [.. empty];
		byte[] oversizedCanonical = new byte[LiquidWalletReplayCodec.MaxCanonicalLength + 1];
		try
		{
			Assert.Equal(4_096, LiquidWalletReplayCodec.MaxDeltaCount);
			Assert.Equal(4_096, LiquidWalletReplayCodec.MaxConfirmationCount);
			Assert.Equal(16_777_204, LiquidWalletReplayCodec.MaxCanonicalLength);
			Assert.Equal(
				LiquidWalletReplayProtectedPayload.MaxPaddedPlaintextLength -
					LiquidWalletReplayProtectedPayload.InnerPrefixLength,
				LiquidWalletReplayCodec.MaxCanonicalLength);
			BinaryPrimitives.WriteUInt32LittleEndian(overLimitDeltas.AsSpan(40),
				LiquidWalletReplayCodec.MaxDeltaCount + 1u);
			BinaryPrimitives.WriteUInt32LittleEndian(overLimitConfirmations.AsSpan(44),
				LiquidWalletReplayCodec.MaxConfirmationCount + 1u);
			Assert.Throws<InvalidDataException>(() => LiquidWalletReplayCodec.Decode(trailing));
			Assert.Throws<InvalidDataException>(() => LiquidWalletReplayCodec.Decode(partial));
			Assert.Throws<InvalidDataException>(() => LiquidWalletReplayCodec.Decode(overLimitDeltas));
			Assert.Throws<InvalidDataException>(() => LiquidWalletReplayCodec.Decode(overLimitConfirmations));
			Assert.Throws<InvalidDataException>(() => LiquidWalletReplayCodec.Decode(oversizedCanonical));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(encoded);
			CryptographicOperations.ZeroMemory(empty);
			CryptographicOperations.ZeroMemory(trailing);
			CryptographicOperations.ZeroMemory(partial);
			CryptographicOperations.ZeroMemory(overLimitDeltas);
			CryptographicOperations.ZeroMemory(overLimitConfirmations);
			CryptographicOperations.ZeroMemory(oversizedCanonical);
		}
	}

	[Fact]
	public void ReplayWorkBudgetAccepts707AndRejects708SmallReceivesBeforeRestore()
	{
		LiquidWalletReplaySnapshot accepted = CreateManyReceiveSnapshot(707);
		LiquidWalletReplaySnapshot rejected = CreateManyReceiveSnapshot(708);
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? acceptedCanonical = null;
		byte[]? canonical = null;
		byte[]? envelope = null;
		try
		{
			acceptedCanonical = LiquidWalletReplayCodec.Encode(accepted);
			Assert.Equal(707, LiquidWalletReplayCodec.Decode(acceptedCanonical).GetDeltas().Count);
			Assert.Throws<LiquidWalletReplayCapacityException>(() =>
				LiquidWalletReplayCodec.Encode(rejected));

			canonical = EncodeCoreForTest(rejected);
			Assert.Throws<InvalidDataException>(() => LiquidWalletReplayCodec.Decode(canonical));
			envelope = ProtectCanonicalForTest(canonical, 5, key, context);
			AssertUniformFailure(() =>
				LiquidWalletReplayProtectedPayload.Open(envelope, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			Zero(acceptedCanonical);
			Zero(canonical);
			Zero(envelope);
		}
	}

	[Fact]
	public void ReplayWorkBudgetRejectsFrontLoadedOutputsAndLaterConfirmations()
	{
		const int initialOutputCount = 500;
		const int laterDeltaCount = 400;
		const int confirmationCount = 200;
		LiquidTransactionId initialId = Tx(1u);
		LiquidOwnedOutput[] initialOutputs = Enumerable.Range(0, initialOutputCount)
			.Select(index => Output(initialId, (uint)index, PeggedAsset, 1))
			.ToArray();
		var deltas = new List<LiquidWalletTransactionDelta>
		{
			Delta(initialId, [], initialOutputs),
		};
		for (int index = 0; index < laterDeltaCount; index++)
		{
			LiquidTransactionId transactionId = Tx((uint)index + 2);
			deltas.Add(Delta(
				transactionId,
				[initialOutputs[index].OutPoint],
				[Output(transactionId, 0, PeggedAsset, 1)]));
		}
		LiquidWalletReplayConfirmation[] confirmations = deltas
			.Take(confirmationCount)
			.Select(delta => LiquidWalletReplayConfirmation.Create(
				delta.TransactionId,
				LiquidConfirmation.Create(BlockHash, 42)))
			.ToArray();
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			(ulong)(deltas.Count + confirmations.Length),
			deltas,
			confirmations);

		Assert.Throws<LiquidWalletReplayCapacityException>(() =>
			LiquidWalletReplayCodec.Encode(snapshot));
	}

	[Fact]
	public void ReplayWorkBudgetChargesEveryDistinctAssetBalanceClone()
	{
		const int distinctAssetCount = 1_500;
		LiquidTransactionId transactionId = Tx(1u);
		LiquidOwnedOutput[] outputs = Enumerable.Range(1, distinctAssetCount)
			.Select(index => Output(
				transactionId,
				(uint)(index - 1),
				Asset((uint)index),
				1))
			.ToArray();
		LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			1,
			[Delta(transactionId, [], outputs)],
			[]);

		Assert.Throws<LiquidWalletReplayCapacityException>(() =>
			LiquidWalletReplayCodec.Encode(snapshot));
	}

	[Fact]
	public void EncodeAndSealRequireRescanBeyondTemporaryHistoryCapacity()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidWalletTransactionDelta delta = Delta(
			transactionId,
			[],
			[Output(transactionId, 0, PeggedAsset, 1)]);
		LiquidWalletTransactionDelta[] deltas = Enumerable
			.Repeat(delta, LiquidWalletReplayCodec.MaxDeltaCount + 1)
			.ToArray();
		ConstructorInfo constructor = Assert.Single(typeof(LiquidWalletReplaySnapshot)
			.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));
		var snapshot = Assert.IsType<LiquidWalletReplaySnapshot>(constructor.Invoke(
			[
				PeggedAsset,
				(ulong)deltas.Length,
				deltas,
				Array.Empty<LiquidWalletReplayConfirmation>(),
			]));
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidWalletReplayCapacityException encodeFailure =
				Assert.Throws<LiquidWalletReplayCapacityException>(() =>
					LiquidWalletReplayCodec.Encode(snapshot));
			LiquidWalletReplayCapacityException sealFailure =
				Assert.Throws<LiquidWalletReplayCapacityException>(() =>
					LiquidWalletReplayProtectedPayload.Seal(snapshot, 1, key, context));

			const string expected =
				"The Liquid wallet replay cache exceeded its temporary capacity; a chain rescan is required.";
			Assert.Equal(expected, encodeFailure.Message);
			Assert.Equal(expected, sealFailure.Message);
			Assert.Null(encodeFailure.InnerException);
			Assert.Null(sealFailure.InnerException);
			Assert.DoesNotContain(transactionId.CanonicalRpcHex, encodeFailure.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(PeggedAssetHex, encodeFailure.Message, StringComparison.Ordinal);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	[Fact]
	public void OversizedEnvelopeIsRejectedBeforePlaintextAllocationOrAuthentication()
	{
		Assert.Equal(16_777_216, LiquidWalletReplayProtectedPayload.MaxPaddedPlaintextLength);
		Assert.Equal(
			HeaderLength + 16_777_216 + LiquidWalletReplayProtectedPayload.TagLength,
			LiquidWalletReplayProtectedPayload.MaxEnvelopeLength);

		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] oversized = new byte[LiquidWalletReplayProtectedPayload.MaxEnvelopeLength + 1];
		try
		{
			// Warm exception/assertion paths before measuring the rejection itself.
			AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open([], key, context));
			long before = GC.GetAllocatedBytesForCurrentThread();
			AssertUniformFailure(() => LiquidWalletReplayProtectedPayload.Open(oversized, key, context));
			long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

			Assert.True(
				allocated < 1_048_576,
				$"Oversized envelope rejection allocated {allocated} bytes.");
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(oversized);
		}
	}

	[Fact]
	public void CodecRefusesAnOutOfRangeSpendKeyEvenIfAnInvalidObjectIsInjected()
	{
		byte[] publicKey = Convert.FromHexString(PublicKeyHex);
		LiquidSpendKeyReference valid = LiquidSpendKeyReference.Create(
			publicKey,
			LiquidKeyBranch.External,
			LiquidSpendKeyReference.MaximumIndex);
		byte[] scriptPubKey = valid.GetScriptPubKey();
		try
		{
			ConstructorInfo constructor = Assert.Single(typeof(LiquidSpendKeyReference)
				.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));
			var invalid = Assert.IsType<LiquidSpendKeyReference>(constructor.Invoke(
				[publicKey, scriptPubKey, LiquidKeyBranch.External, LiquidSpendKeyReference.MaximumIndex + 1]));
			LiquidTransactionId transactionId = Tx('a');
			LiquidOwnedOutput output = LiquidOwnedOutput.Create(
				LiquidOutPoint.CreateSpendable(transactionId, 0),
				scriptPubKey,
				LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 1),
				invalid);
			LiquidWalletReplaySnapshot snapshot = LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				1,
				[Delta(transactionId, [], [output])],
				[]);

			Assert.Throws<ArgumentException>(() => LiquidWalletReplayCodec.Encode(snapshot));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(publicKey);
			CryptographicOperations.ZeroMemory(scriptPubKey);
		}
	}

	[Fact]
	public void ProtectionBoundaryHasNoFilesystemRuntimeOrKeyManagerSurface()
	{
		foreach (Type boundaryType in new[]
		{
			typeof(LiquidWalletReplayCodec),
			typeof(LiquidWalletReplayProtectedPayload),
			typeof(LiquidWalletReplayOpenResult),
		})
		{
			IEnumerable<Type> signatureTypes = boundaryType
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Select(field => field.FieldType)
				.Concat(boundaryType
					.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
					.Select(property => property.PropertyType))
				.Concat(boundaryType
					.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
					.SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
						.Append(method.ReturnType)));

			Assert.DoesNotContain(signatureTypes, type =>
			{
				string name = type.FullName ?? type.Name;
				return name.Contains("System.IO", StringComparison.Ordinal) ||
					name.Contains("KeyManager", StringComparison.OrdinalIgnoreCase) ||
					name.Contains("Rpc", StringComparison.OrdinalIgnoreCase) ||
					name.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
					name.Contains("WalletManager", StringComparison.OrdinalIgnoreCase);
			});
		}

		Assert.Null(typeof(LiquidWalletReplayProtectedPayload).GetProperty("Generation"));
	}

	private static byte[] ReverseAuthenticatedConfirmationOrder(
		byte[] envelope,
		byte[] key,
		byte[] context)
	{
		byte[] result = [.. envelope];
		byte[] plaintext = new byte[BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(20))];
		byte[] associatedData = new byte[HeaderLength + context.Length];
		Span<byte> temporary = stackalloc byte[ConfirmationLength];
		try
		{
			result.AsSpan(0, HeaderLength).CopyTo(associatedData);
			context.CopyTo(associatedData.AsSpan(HeaderLength));
			int ciphertextLength = plaintext.Length;
			using var aes = new AesGcm(key, LiquidWalletReplayProtectedPayload.TagLength);
			aes.Decrypt(
				result.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength),
				result.AsSpan(HeaderLength, ciphertextLength),
				result.AsSpan(HeaderLength + ciphertextLength, LiquidWalletReplayProtectedPayload.TagLength),
				plaintext,
				associatedData);

			int canonicalLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext.AsSpan(sizeof(ulong)));
			Span<byte> canonical = plaintext.AsSpan(sizeof(ulong) + sizeof(uint), canonicalLength);
			int confirmationOffset = FindConfirmationOffset(canonical);
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(canonical[confirmationOffset..]));
			Span<byte> first = canonical.Slice(confirmationOffset + sizeof(uint), ConfirmationLength);
			Span<byte> second = canonical.Slice(
				confirmationOffset + sizeof(uint) + ConfirmationLength,
				ConfirmationLength);
			first.CopyTo(temporary);
			second.CopyTo(first);
			temporary.CopyTo(second);

			aes.Encrypt(
				result.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength),
				plaintext,
				result.AsSpan(HeaderLength, ciphertextLength),
				result.AsSpan(HeaderLength + ciphertextLength, LiquidWalletReplayProtectedPayload.TagLength),
				associatedData);
			return result;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(result);
			throw;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
			CryptographicOperations.ZeroMemory(associatedData);
			CryptographicOperations.ZeroMemory(temporary);
		}
	}

	private static byte[] EncodeCoreForTest(LiquidWalletReplaySnapshot snapshot)
	{
		MethodInfo method = typeof(LiquidWalletReplayCodec).GetMethod(
			"EncodeCore",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException("The replay codec core encoder is unavailable.");
		return Assert.IsType<byte[]>(method.Invoke(null, [snapshot]));
	}

	private static byte[] ProtectCanonicalForTest(
		ReadOnlySpan<byte> canonical,
		ulong generation,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> context)
	{
		int innerLength = LiquidWalletReplayProtectedPayload.InnerPrefixLength + canonical.Length;
		int paddedLength = checked(
			((innerLength + LiquidWalletReplayProtectedPayload.PaddingBucketLength - 1) /
				LiquidWalletReplayProtectedPayload.PaddingBucketLength) *
			LiquidWalletReplayProtectedPayload.PaddingBucketLength);
		byte[] plaintext = new byte[paddedLength];
		byte[] associatedData = new byte[HeaderLength + context.Length];
		byte[] envelope = new byte[HeaderLength + paddedLength + LiquidWalletReplayProtectedPayload.TagLength];
		try
		{
			BinaryPrimitives.WriteUInt64LittleEndian(plaintext, generation);
			BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(sizeof(ulong)), (uint)canonical.Length);
			canonical.CopyTo(plaintext.AsSpan(LiquidWalletReplayProtectedPayload.InnerPrefixLength));
			RandomNumberGenerator.Fill(plaintext.AsSpan(innerLength));

			Span<byte> header = envelope.AsSpan(0, HeaderLength);
			Encoding.ASCII.GetBytes("WLRPENV1").CopyTo(header);
			BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 1);
			BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 1);
			BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
			BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)paddedLength);
			BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)paddedLength);
			RandomNumberGenerator.Fill(header.Slice(32, LiquidWalletReplayProtectedPayload.NonceLength));
			header.CopyTo(associatedData);
			context.CopyTo(associatedData.AsSpan(HeaderLength));

			using var aes = new AesGcm(key, LiquidWalletReplayProtectedPayload.TagLength);
			aes.Encrypt(
				header.Slice(32, LiquidWalletReplayProtectedPayload.NonceLength),
				plaintext,
				envelope.AsSpan(HeaderLength, paddedLength),
				envelope.AsSpan(HeaderLength + paddedLength, LiquidWalletReplayProtectedPayload.TagLength),
				associatedData);
			return envelope;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(envelope);
			throw;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
			CryptographicOperations.ZeroMemory(associatedData);
		}
	}

	private static int FindConfirmationOffset(ReadOnlySpan<byte> canonical)
	{
		int position = LiquidAssetId.ConsensusByteLength + sizeof(ulong);
		uint deltaCount = ReadUInt32(canonical, ref position);
		for (uint deltaIndex = 0; deltaIndex < deltaCount; deltaIndex++)
		{
			position += LiquidTransactionId.ConsensusByteLength;
			uint spentCount = ReadUInt32(canonical, ref position);
			position += checked((int)spentCount * LiquidOutPoint.ConsensusByteLength);
			uint createdCount = ReadUInt32(canonical, ref position);
			for (uint createdIndex = 0; createdIndex < createdCount; createdIndex++)
			{
				position += LiquidOutPoint.ConsensusByteLength;
				uint scriptLength = ReadUInt32(canonical, ref position);
				position += checked((int)scriptLength) + LiquidAssetId.ConsensusByteLength +
					sizeof(long) + sizeof(byte) + sizeof(uint) + 33;
			}
		}
		return position;
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int position)
	{
		uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
		position += sizeof(uint);
		return value;
	}

	private static byte[] AmountBytes(long atomicUnits)
	{
		byte[] bytes = new byte[sizeof(long)];
		BinaryPrimitives.WriteInt64LittleEndian(bytes, atomicUnits);
		return bytes;
	}

	private static byte[] UInt64Bytes(ulong value)
	{
		byte[] bytes = new byte[sizeof(ulong)];
		BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
		return bytes;
	}

	private static LiquidWalletReplaySnapshot CreateReplaySnapshot()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput lbtc = Output(receiveId, 0, PeggedAsset, 100);
		LiquidOwnedOutput issued = Output(receiveId, 1, IssuedAsset, 200);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput change = Output(spendId, 0, IssuedAsset, 150);
		return LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [lbtc, issued]))
			.Apply(1, Delta(spendId, [issued.OutPoint], [change]))
			.Confirm(2, spendId, LiquidConfirmation.Create(OtherBlockHash, 43))
			.Confirm(3, receiveId, LiquidConfirmation.Create(BlockHash, 42))
			.ExportReplaySnapshot();
	}

	private static LiquidWalletReplaySnapshot CreateManyReceiveSnapshot(int count)
	{
		LiquidWalletTransactionDelta[] deltas = Enumerable.Range(1, count)
			.Select(index =>
			{
				LiquidTransactionId transactionId = Tx((uint)index);
				return Delta(
					transactionId,
					[],
					[Output(transactionId, 0, PeggedAsset, 1)]);
			})
			.ToArray();
		return LiquidWalletReplaySnapshot.Create(PeggedAsset, (ulong)deltas.Length, deltas, []);
	}

	private static void AssertUniformFailure(Action action)
	{
		LiquidWalletReplayProtectionException exception =
			Assert.Throws<LiquidWalletReplayProtectionException>(action);
		Assert.Equal(UniformFailure, exception.Message);
		Assert.Null(exception.InnerException);
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidTransactionId Tx(uint value) =>
		LiquidTransactionId.ParseRpcHex(value.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));

	private static LiquidAssetId Asset(uint value) =>
		LiquidAssetId.ParseRpcHex(value.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		byte[] publicKey = Convert.FromHexString(PublicKeyHex);
		byte[]? scriptPubKey = null;
		try
		{
			LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
				publicKey,
				LiquidKeyBranch.External,
				outputIndex);
			scriptPubKey = spendKey.GetScriptPubKey();
			return LiquidOwnedOutput.Create(
				LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
				scriptPubKey,
				LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits),
				spendKey);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(publicKey);
			Zero(scriptPubKey);
		}
	}

	private static LiquidWalletTransactionDelta Delta(
		LiquidTransactionId transactionId,
		IEnumerable<LiquidOutPoint> spent,
		IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);

	private static void Zero(byte[]? bytes)
	{
		if (bytes is not null)
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}
}
