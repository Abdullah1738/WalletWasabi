using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;
using CandidateSource = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource;

namespace WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire;

public class LiquidWalletFactsWireV1StructuralRequestCodecTests
{
	private static readonly byte[] SourceA = Enumerable.Repeat((byte)0x41, 32).ToArray();
	private static readonly byte[] ValidDescriptor = Encoding.ASCII.GetBytes(
		"elwpkh([28b3f14e/84'/1'/0']tpubDC2Q4xK4XH72GM7MowNuajyWVbigRLBWKswyP5T88hpPwu5nGqJWnda8zhJEFt71av73Hm8mUMMFSz9acNVzz8b1UbdSHCDXKTbSv5eEytu/<0;1>/*)#u0khc0kg");

	[Fact]
	public void BuildsAllStructuralRequestBasesFromTheAcceptedCorpus()
	{
		WalletFactsWireV1Corpus.AssertChecksumPacket();
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		IReadOnlyList<string[]> recipes = WalletFactsWireV1Corpus.ReadRows(
			"RECIPES_V1.tsv",
			"recipe_id",
			"recipe_kind",
			"source_epoch_hex",
			"descriptor_network",
			"last_derivation_index",
			"public_descriptor_hex",
			"candidates",
			"transactions",
			"outputs",
			"expected_property");

		AssertBuiltRecipe(recipes.Single(row => row[0] == "empty-accepted-request-source"), frames["request-00-base-empty"]);
		AssertBuiltRecipe(recipes.Single(row => row[0] == "nonempty-accepted-request-source"), frames["request-01-base-nonempty"]);

		byte[] semanticDescriptor = Encoding.ASCII.GetBytes("elwpkh(x)#u0khc0kg");
		byte[] built = Build(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, semanticDescriptor, []);
		try
		{
			CorpusFrame expected = frames["request-02-base-semantic-reject"];
			Assert.Equal(expected.Bytes, built);
			Assert.Equal(expected.Bytes.Length, built.Length);
			Assert.Equal(SHA256.HashData(expected.Bytes), SHA256.HashData(built));
			IReadOnlyList<string[]> cases = WalletFactsWireV1Corpus.ReadRows(
				"CASES_V1.tsv",
				"case_id",
				"frame_id",
				"operation",
				"expected_source_epoch_hex",
				"expected_status",
				"expected_error_code",
				"canonical_reencode");
			Assert.Contains(cases, row => row.SequenceEqual(
				["request-semantic-decode", "request-02-base-semantic-reject", "request-decode", "-", "ok", "0", "yes"]));
			Assert.Contains(cases, row => row.SequenceEqual(
				["request-semantic-prepare", "request-02-base-semantic-reject", "request-prepare", "-", "error", "5", "no"]));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(semanticDescriptor);
			CryptographicOperations.ZeroMemory(built);
		}
	}

	[Fact]
	public void DirectFactoryAcceptsCanonicalRequestsAndRejectsEveryDecoderError()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		foreach (string frameId in new[] { "request-00-base-empty", "request-01-base-nonempty", "request-02-base-semantic-reject" })
		{
			byte[] input = [.. frames[frameId].Bytes];
			byte[] retained = new byte[input.Length];
			LiquidWalletFactsWireV1UnpreparedRequestFrame? owner = null;
			try
			{
				owner = LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(input);
				owner.CopyFrameTo(retained);
				Assert.Equal(input, retained);
				Assert.Equal(input.Length, owner.Length);
			}
			finally
			{
				owner?.Dispose();
				CryptographicOperations.ZeroMemory(input);
				CryptographicOperations.ZeroMemory(retained);
			}
		}

		IReadOnlyList<string[]> cases = WalletFactsWireV1Corpus.ReadRows(
			"CASES_V1.tsv",
			"case_id",
			"frame_id",
			"operation",
			"expected_source_epoch_hex",
			"expected_status",
			"expected_error_code",
			"canonical_reencode");
		string[][] decoderErrors = cases.Where(row => row[2] == "request-decode" && row[4] == "error").ToArray();
		Assert.Equal(28, decoderErrors.Length);
		string? message = null;
		foreach (string[] row in decoderErrors)
		{
			byte[] input = [.. frames[row[1]].Bytes];
			byte[] unchanged = [.. input];
			LiquidWalletFactsWireV1UnpreparedRequestFrame? unexpectedOwner = null;
			try
			{
				InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
					() => unexpectedOwner = LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(input));
				message ??= exception.Message;
				Assert.Equal(message, exception.Message);
				Assert.DoesNotContain(row[0], exception.Message, StringComparison.Ordinal);
				Assert.Equal(unchanged, input);
			}
			finally
			{
				unexpectedOwner?.Dispose();
				CryptographicOperations.ZeroMemory(input);
				CryptographicOperations.ZeroMemory(unchanged);
			}
		}
	}

	[Fact]
	public async Task DirectFactoryConcurrentMutationHasOnlyCanonicalOrFixedRejectedOutcomesAsync()
	{
		byte[] descriptor = Encoding.ASCII.GetBytes("x#qqqqqqqq");
		byte[] payload = new byte[1_048_576];
		payload.AsSpan().Fill(0x31);
		byte[] canonical = Build(
			SourceA,
			LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
			0,
			descriptor,
			[new CandidateSource(payload, [])]);
		byte[] alternate = [.. canonical];
		int payloadOffset = 76 + descriptor.Length + 12;
		alternate.AsSpan(payloadOffset, payload.Length).Fill(0x72);
		byte[] caller = [.. canonical];
		byte[] malformed = [.. canonical];
		malformed[72] = 1;
		string fixedMessage;
		LiquidWalletFactsWireV1UnpreparedRequestFrame? unexpectedOwner = null;
		try
		{
			fixedMessage = Assert.Throws<InvalidOperationException>(
				() => unexpectedOwner = LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(malformed)).Message;
		}
		finally
		{
			unexpectedOwner?.Dispose();
			CryptographicOperations.ZeroMemory(malformed);
		}

		using var stop = new CancellationTokenSource();
		var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Task mutator = Task.Run(() =>
		{
			started.SetResult(true);
			while (!stop.IsCancellationRequested)
			{
				Buffer.BlockCopy(canonical, 0, caller, 0, caller.Length);
				caller[72] = 1;
				Thread.Yield();
				Buffer.BlockCopy(alternate, 0, caller, 0, caller.Length);
			}
		});
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
			for (int iteration = 0; iteration < 16; iteration++)
			{
				LiquidWalletFactsWireV1UnpreparedRequestFrame? owner = null;
				byte[]? captured = null;
				try
				{
					try
					{
						owner = LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(caller);
					}
					catch (InvalidOperationException exception)
					{
						Assert.Equal(fixedMessage, exception.Message);
						continue;
					}

					captured = new byte[owner.Length];
					owner.CopyFrameTo(captured);
					using LiquidWalletFactsWireV1UnpreparedRequestFrame verified =
						LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(captured);
					Assert.Equal(captured.Length, verified.Length);
				}
				finally
				{
					owner?.Dispose();
					if (captured is not null)
					{
						CryptographicOperations.ZeroMemory(captured);
					}
				}
			}
		}
		finally
		{
			stop.Cancel();
			try
			{
				await mutator.WaitAsync(TimeSpan.FromSeconds(10));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(descriptor);
				CryptographicOperations.ZeroMemory(payload);
				CryptographicOperations.ZeroMemory(canonical);
				CryptographicOperations.ZeroMemory(alternate);
				CryptographicOperations.ZeroMemory(caller);
			}
		}

	}

	[Fact]
	public void BuilderPreservesExactLayoutAndBorrowedOrderWithoutRetainingInputs()
	{
		byte[] source = [.. SourceA];
		byte[] descriptor = Encoding.ASCII.GetBytes("x#qqqqqqqq");
		byte[] transactionA = [0x03, 0x02, 0x01];
		byte[] transactionB = [0xff, 0x00];
		byte[] previousA = [0x02, 0x01];
		byte[] previousB = [0x02, 0x01];
		byte[] previousC = [0x80, 0x01, 0x00];
		CandidateSource[] candidates =
		[
			new(transactionA, new ReadOnlyMemory<byte>[] { previousA, previousB }),
			new(transactionB, new ReadOnlyMemory<byte>[] { previousC }),
		];

		byte[] built = Build(source, LiquidWalletFactsWireV1DescriptorNetworkClass.Mainnet, 100_000, descriptor, candidates);
		byte[] stable = [.. built];
		try
		{
			Assert.True(built.AsSpan(0, 4).SequenceEqual("WLFQ"u8));
			Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(4, 2)));
			Assert.Equal((ushort)76, BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(6, 2)));
			Assert.Equal((ulong)built.Length, BinaryPrimitives.ReadUInt64LittleEndian(built.AsSpan(8, 8)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(16, 4)));
			Assert.Equal(0, built[20]);
			Assert.Equal(new byte[3], built.AsSpan(21, 3).ToArray());
			Assert.Equal(100_000u, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(24, 4)));
			Assert.Equal(source, built.AsSpan(28, 32).ToArray());
			Assert.Equal((uint)descriptor.Length, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(60, 4)));
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(64, 4)));
			Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(68, 4)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(72, 4)));
			Assert.True(built.AsSpan(76, descriptor.Length).SequenceEqual(descriptor));

			int cursor = 76 + descriptor.Length;
			AssertCandidate(built, ref cursor, transactionA, previousA, previousB);
			AssertCandidate(built, ref cursor, transactionB, previousC);
			Assert.Equal(built.Length, cursor);

			source.AsSpan().Clear();
			descriptor.AsSpan().Fill((byte)'z');
			transactionA.AsSpan().Clear();
			transactionB.AsSpan().Clear();
			previousA.AsSpan().Clear();
			previousB.AsSpan().Clear();
			previousC.AsSpan().Clear();
			Assert.Equal(stable, built);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(source);
			CryptographicOperations.ZeroMemory(descriptor);
			CryptographicOperations.ZeroMemory(transactionA);
			CryptographicOperations.ZeroMemory(transactionB);
			CryptographicOperations.ZeroMemory(previousA);
			CryptographicOperations.ZeroMemory(previousB);
			CryptographicOperations.ZeroMemory(previousC);
			CryptographicOperations.ZeroMemory(built);
			CryptographicOperations.ZeroMemory(stable);
		}
	}

	[Fact]
	public void ErrorCodesAndCombinedInvalidPrecedenceAreStructuralOnly()
	{
		var throwingCandidates = new ThrowingReadOnlyList<CandidateSource>();
		AssertRejected(new byte[31], (LiquidWalletFactsWireV1DescriptorNetworkClass)2, uint.MaxValue, [], throwingCandidates, LiquidWalletFactsWireErrorCode.InvalidArgument);
		AssertRejected(new byte[32], LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, throwingCandidates, LiquidWalletFactsWireErrorCode.InvalidArgument);
		AssertRejected(SourceA, (LiquidWalletFactsWireV1DescriptorNetworkClass)2, 0, [], null!, LiquidWalletFactsWireErrorCode.InvalidArgument);
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, [], null!, LiquidWalletFactsWireErrorCode.InvalidArgument);
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, uint.MaxValue, [], throwingCandidates, LiquidWalletFactsWireErrorCode.InvalidEncoding);
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, uint.MaxValue, ValidDescriptor, new CountedThrowingList<CandidateSource>(1), LiquidWalletFactsWireErrorCode.LimitExceeded);
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, new CountedThrowingList<CandidateSource>(4_097), LiquidWalletFactsWireErrorCode.LimitExceeded);

		CandidateSource empty = new(ReadOnlyMemory<byte>.Empty, []);
		CandidateSource laterOverflow = new(new byte[] { 1 }, new CountedThrowingList<ReadOnlyMemory<byte>>(16_385));
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, [empty, laterOverflow], LiquidWalletFactsWireErrorCode.InvalidEncoding);

		byte[] oversized = new byte[4_194_305];
		try
		{
			CandidateSource oversizedTransaction = new(oversized, new ThrowingReadOnlyList<ReadOnlyMemory<byte>>());
			AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, [oversizedTransaction], LiquidWalletFactsWireErrorCode.LimitExceeded);
			CandidateSource previousCountOverflow = new(new byte[] { 1 }, new CountedThrowingList<ReadOnlyMemory<byte>>(16_385));
			AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, [previousCountOverflow], LiquidWalletFactsWireErrorCode.LimitExceeded);
			CandidateSource emptyPreviousFirst = new(new byte[] { 1 }, new ReadOnlyMemory<byte>[] { ReadOnlyMemory<byte>.Empty, oversized });
			AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, [emptyPreviousFirst], LiquidWalletFactsWireErrorCode.InvalidEncoding);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(oversized);
		}

		Assert.Throws<ArgumentNullException>(() => new CandidateSource(new byte[] { 1 }, null!));
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, new CandidateSource[] { null! }, LiquidWalletFactsWireErrorCode.InvalidArgument);
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 100_001, ValidDescriptor, [], LiquidWalletFactsWireErrorCode.LimitExceeded);
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, new byte[16_385], [], LiquidWalletFactsWireErrorCode.LimitExceeded);
		AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, Encoding.ASCII.GetBytes("x#QQQQQQQQ"), [], LiquidWalletFactsWireErrorCode.InvalidEncoding);
	}

	[Fact]
	public void CandidatePrecedenceDefersAggregateBytesButNotAggregatePreviousCount()
	{
		byte[] one = [1];
		byte[] maximumTransaction = new byte[4_194_304];
		byte[] oversizedTransaction = new byte[4_194_305];
		maximumTransaction.AsSpan().Fill(0x21);
		oversizedTransaction.AsSpan().Fill(0x22);
		try
		{
			var maximumPrevious = new RepeatedReadOnlyMemoryList(16_384, one);
			var malformedCurrentPrevious = new CountedProbeThrowingList<ReadOnlyMemory<byte>>(1);
			CandidateSource previousCountAtLimit = new(one, maximumPrevious);
			CandidateSource currentPreviousCountOverflow = new(one, malformedCurrentPrevious);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[previousCountAtLimit, currentPreviousCountOverflow],
				LiquidWalletFactsWireErrorCode.LimitExceeded);
			Assert.Equal(0, malformedCurrentPrevious.IndexReads);

			CandidateSource[] aggregateAtLimit = Enumerable.Range(0, 16)
				.Select(_ => new CandidateSource(maximumTransaction, []))
				.ToArray();

			var emptyThenOversized = new ProbedReadOnlyList<ReadOnlyMemory<byte>>(
				[ReadOnlyMemory<byte>.Empty, oversizedTransaction]);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[.. aggregateAtLimit, new CandidateSource(one, emptyThenOversized)],
				LiquidWalletFactsWireErrorCode.InvalidEncoding);
			Assert.Equal(new[] { 0 }, emptyThenOversized.IndexReads);

			var oversizedThenEmpty = new ProbedReadOnlyList<ReadOnlyMemory<byte>>(
				[oversizedTransaction, ReadOnlyMemory<byte>.Empty]);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[.. aggregateAtLimit, new CandidateSource(one, oversizedThenEmpty)],
				LiquidWalletFactsWireErrorCode.LimitExceeded);
			Assert.Equal(new[] { 0 }, oversizedThenEmpty.IndexReads);

			var validThenEmptyThenOversized = new ProbedReadOnlyList<ReadOnlyMemory<byte>>(
				[one, ReadOnlyMemory<byte>.Empty, oversizedTransaction]);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[.. aggregateAtLimit, new CandidateSource(one, validThenEmptyThenOversized)],
				LiquidWalletFactsWireErrorCode.InvalidEncoding);
			Assert.Equal(new[] { 0, 1 }, validThenEmptyThenOversized.IndexReads);

			var aboveInt32ThenEmpty = new RepeatedThenTailReadOnlyMemoryList(
				513,
				maximumTransaction,
				ReadOnlyMemory<byte>.Empty);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[new CandidateSource(one, aboveInt32ThenEmpty)],
				LiquidWalletFactsWireErrorCode.InvalidEncoding);
			Assert.Equal(1, aboveInt32ThenEmpty.TailReads);

			var earlierNull = new EarlierNullLaterThrowingCandidateList();
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				earlierNull,
				LiquidWalletFactsWireErrorCode.InvalidArgument);
			Assert.Equal(new[] { 0 }, earlierNull.IndexReads);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(one);
			CryptographicOperations.ZeroMemory(maximumTransaction);
			CryptographicOperations.ZeroMemory(oversizedTransaction);
		}
	}

	[Fact]
	public void DescriptorStructuralGrammarAndChecksumAlphabetAreExact()
	{
		byte[] alphabet = Encoding.ASCII.GetBytes("qpzry9x8gf2tvdw0s3jn54khce6mua7l");
		foreach (byte value in alphabet)
		{
			byte[] descriptor = [(byte)'x', (byte)'#', value, value, value, value, value, value, value, value];
			byte[] frame = Build(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, descriptor, []);
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(descriptor);
		}

		foreach (byte[] malformed in new[]
		{
			Array.Empty<byte>(),
			Encoding.ASCII.GetBytes("#qqqqqqqq"),
			Encoding.ASCII.GetBytes("xqqqqqqqq"),
			Encoding.ASCII.GetBytes("x##qqqqqqqq"),
			Encoding.ASCII.GetBytes("x#qqqqqqq"),
			Encoding.ASCII.GetBytes("x#qqqqqqqqq"),
			Encoding.ASCII.GetBytes("x #qqqqqqqq"),
			[(byte)'x', 0, (byte)'#', .. Encoding.ASCII.GetBytes("qqqqqqqq")],
			[(byte)'x', 0x80, (byte)'#', .. Encoding.ASCII.GetBytes("qqqqqqqq")],
			Encoding.ASCII.GetBytes("x#bbbbbbbb"),
		})
		{
			AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, malformed, [], LiquidWalletFactsWireErrorCode.InvalidEncoding);
			CryptographicOperations.ZeroMemory(malformed);
		}

		foreach (byte whitespace in new byte[] { 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20 })
		{
			byte[] malformed = [(byte)'x', whitespace, (byte)'#', .. Encoding.ASCII.GetBytes("qqqqqqqq")];
			try
			{
				AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, malformed, [], LiquidWalletFactsWireErrorCode.InvalidEncoding);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(malformed);
			}
		}
	}

	[Fact]
	public void ScopedSourceAndDescriptorCopiesAreStableDuringLaterCollectionInspection()
	{
		byte[] source = [.. SourceA];
		byte[] originalSource = [.. source];
		byte[] descriptor = Encoding.ASCII.GetBytes("x#qqqqqqqq");
		byte[] originalDescriptor = [.. descriptor];
		var candidates = new CountCallbackReadOnlyList<CandidateSource>([], () =>
		{
			source.AsSpan().Clear();
			descriptor.AsSpan().Fill((byte)'z');
		});
		byte[]? built = null;
		try
		{
			built = Build(source, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, descriptor, candidates);
			Assert.Equal(1, candidates.CountReads);
			Assert.True(built.AsSpan(28, 32).SequenceEqual(originalSource));
			Assert.Equal((uint)originalDescriptor.Length, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(60, 4)));
			Assert.True(built.AsSpan(76, originalDescriptor.Length).SequenceEqual(originalDescriptor));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(source);
			CryptographicOperations.ZeroMemory(originalSource);
			CryptographicOperations.ZeroMemory(descriptor);
			CryptographicOperations.ZeroMemory(originalDescriptor);
			if (built is not null)
			{
				CryptographicOperations.ZeroMemory(built);
			}
		}
	}

	[Fact]
	public async Task OpaquePayloadMutationDuringBuildCompletesWithOneStableCanonicalFrameAsync()
	{
		byte[] payload = new byte[1_048_576];
		byte[] descriptor = Encoding.ASCII.GetBytes("x#qqqqqqqq");
		payload.AsSpan().Fill(0x35);
		var candidate = new CandidateSource(payload, []);
		using var stop = new CancellationTokenSource();
		var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Task mutator = Task.Run(() =>
		{
			started.SetResult(true);
			byte value = 0x41;
			while (!stop.IsCancellationRequested)
			{
				payload.AsSpan().Fill(value);
				value = value == 0x41 ? (byte)0x72 : (byte)0x41;
			}
		});
		byte[]? built = null;
		byte[]? stable = null;
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
			built = Build(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, descriptor, [candidate]);
			stop.Cancel();
			await mutator.WaitAsync(TimeSpan.FromSeconds(10));
			stable = [.. built];
			payload.AsSpan().Fill(0xff);
			Assert.Equal(stable, built);
			using LiquidWalletFactsWireV1UnpreparedRequestFrame verified =
				LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(built);
			Assert.Equal(built.Length, verified.Length);
		}
		finally
		{
			stop.Cancel();
			try
			{
				await mutator.WaitAsync(TimeSpan.FromSeconds(10));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(payload);
				CryptographicOperations.ZeroMemory(descriptor);
				if (built is not null)
				{
					CryptographicOperations.ZeroMemory(built);
				}
				if (stable is not null)
				{
					CryptographicOperations.ZeroMemory(stable);
				}
			}
		}

	}

	[Fact]
	public void OutputOwnerIsLinearDefensiveAndZeroizesStorage()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		byte[] input = [.. frames["request-00-base-empty"].Bytes];
		var owner = LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(input);
		FieldInfo frameField = typeof(LiquidWalletFactsWireV1UnpreparedRequestFrame)
			.GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic)!;
		byte[] retained = Assert.IsType<byte[]>(frameField.GetValue(owner));
		byte[] callerCopy = new byte[input.Length];
		byte[] tooShort = Enumerable.Repeat((byte)0xa5, input.Length - 1).ToArray();
		byte[] tooShortBefore = [.. tooShort];
		try
		{
			owner.CopyFrameTo(callerCopy);
			Assert.Equal(input, callerCopy);
			Assert.Throws<ArgumentException>(() => owner.CopyFrameTo(tooShort));
			Assert.Equal(tooShortBefore, tooShort);
			owner.Dispose();
			owner.Dispose();
			Assert.All(retained, value => Assert.Equal(0, value));
			Assert.Equal(input, callerCopy);
			Assert.Equal(nameof(LiquidWalletFactsWireV1UnpreparedRequestFrame), owner.ToString());
			Assert.Throws<ObjectDisposedException>(() => _ = owner.Length);
			Assert.Throws<ObjectDisposedException>(() => owner.CopyFrameTo(tooShort));
			Assert.Equal(tooShortBefore, tooShort);
		}
		finally
		{
			owner.Dispose();
			CryptographicOperations.ZeroMemory(input);
			CryptographicOperations.ZeroMemory(callerCopy);
			CryptographicOperations.ZeroMemory(tooShort);
			CryptographicOperations.ZeroMemory(tooShortBefore);
		}
	}

	[Fact]
	public async Task CopyRacingDisposeReturnsACompleteFrameOrTheDisposedErrorAsync()
	{
		byte[] expected = Build(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, []);
		byte[] destination = Enumerable.Repeat((byte)0xa5, expected.Length).ToArray();
		byte[] unchanged = [.. destination];
		var owner = LiquidWalletFactsWireV1UnpreparedRequestFrame.CreateStructuralUnpreparedCopy(expected);
		using var start = new ManualResetEventSlim(false);
		Exception? copyError = null;
		Task copyTask = Task.Run(() =>
		{
			start.Wait();
			try
			{
				owner.CopyFrameTo(destination);
			}
			catch (Exception exception)
			{
				copyError = exception;
			}
		});
		Task disposeTask = Task.Run(() =>
		{
			start.Wait();
			owner.Dispose();
		});
		try
		{
			start.Set();
			await Task.WhenAll(copyTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(10));
			if (copyError is null)
			{
				Assert.Equal(expected, destination);
			}
			else
			{
				Assert.IsType<ObjectDisposedException>(copyError);
				Assert.Equal(unchanged, destination);
			}

			Assert.Equal(nameof(LiquidWalletFactsWireV1UnpreparedRequestFrame), owner.ToString());
		}
		finally
		{
			start.Set();
			owner.Dispose();
			try
			{
				await Task.WhenAll(copyTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(10));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(expected);
				CryptographicOperations.ZeroMemory(destination);
				CryptographicOperations.ZeroMemory(unchanged);
			}
		}
	}

	[Fact]
	public void PracticalStructuralMaximaAndPlusOneRejectionsAreExact()
	{
		byte[] descriptorAtMaximum = new byte[16_384];
		descriptorAtMaximum.AsSpan().Fill((byte)'x');
		descriptorAtMaximum[16_375] = (byte)'#';
		descriptorAtMaximum.AsSpan(16_376).Fill((byte)'q');
		byte[] descriptorOverMaximum = new byte[16_385];
		byte[] one = [1];
		byte[] transactionAtMaximum = new byte[4_194_304];
		byte[] transactionOverMaximum = new byte[4_194_305];
		transactionAtMaximum.AsSpan().Fill(0x51);
		transactionOverMaximum.AsSpan().Fill(0x52);
		try
		{
			AssertRejected(new byte[31], LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, [], LiquidWalletFactsWireErrorCode.InvalidArgument);
			AssertRejected(new byte[33], LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, [], LiquidWalletFactsWireErrorCode.InvalidArgument);
			AssertRejected(new byte[32], LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, [], LiquidWalletFactsWireErrorCode.InvalidArgument);

			byte[] built = Build(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 100_000, descriptorAtMaximum, []);
			CryptographicOperations.ZeroMemory(built);
			AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 100_001, descriptorAtMaximum, [], LiquidWalletFactsWireErrorCode.LimitExceeded);
			AssertRejected(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, descriptorOverMaximum, [], LiquidWalletFactsWireErrorCode.LimitExceeded);

			var sharedCandidate = new CandidateSource(one, []);
			CandidateSource[] maximumCandidates = Enumerable.Repeat(sharedCandidate, 4_096).ToArray();
			built = Build(SourceA, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 0, ValidDescriptor, maximumCandidates);
			CryptographicOperations.ZeroMemory(built);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				new CountedThrowingList<CandidateSource>(4_097),
				LiquidWalletFactsWireErrorCode.LimitExceeded);

			var maximumPrevious = new RepeatedReadOnlyMemoryList(16_384, one);
			built = Build(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[new CandidateSource(one, maximumPrevious)]);
			CryptographicOperations.ZeroMemory(built);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[new CandidateSource(one, new CountedThrowingList<ReadOnlyMemory<byte>>(16_385))],
				LiquidWalletFactsWireErrorCode.LimitExceeded);

			built = Build(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[new CandidateSource(transactionAtMaximum, [])]);
			CryptographicOperations.ZeroMemory(built);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[new CandidateSource(transactionOverMaximum, [])],
				LiquidWalletFactsWireErrorCode.LimitExceeded);

			built = Build(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[new CandidateSource(one, new ReadOnlyMemory<byte>[] { transactionAtMaximum })]);
			CryptographicOperations.ZeroMemory(built);
			AssertRejected(
				SourceA,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				ValidDescriptor,
				[new CandidateSource(one, new ReadOnlyMemory<byte>[] { transactionOverMaximum })],
				LiquidWalletFactsWireErrorCode.LimitExceeded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(descriptorAtMaximum);
			CryptographicOperations.ZeroMemory(descriptorOverMaximum);
			CryptographicOperations.ZeroMemory(one);
			CryptographicOperations.ZeroMemory(transactionAtMaximum);
			CryptographicOperations.ZeroMemory(transactionOverMaximum);
		}
	}

	[Fact]
	public void PrivateStagingAndSurfaceKeepTheFrozenOwnershipBoundary()
	{
		Type owner = typeof(LiquidWalletFactsWireV1UnpreparedRequestFrame);
		Type codec = typeof(LiquidWalletFactsWireV1StructuralRequestCodec);
		Type candidate = typeof(CandidateSource);
		Type staging = owner.GetNestedType("PrivateStructuralFrameCopy", BindingFlags.NonPublic)!;
		Assert.True(staging.IsNestedPrivate);
		object stagingInstance = RuntimeHelpers.GetUninitializedObject(staging);
		byte[] stagedBytes = Enumerable.Repeat((byte)0x5a, 32).ToArray();
		staging.GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(stagingInstance, stagedBytes);
		((IDisposable)stagingInstance).Dispose();
		Assert.All(stagedBytes, value => Assert.Equal(0, value));

		MethodInfo factory = owner.GetMethod("CreateStructuralUnpreparedCopy", BindingFlags.Static | BindingFlags.NonPublic)!;
		ConstructorInfo frameConstructor = owner.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
		ConstructorInfo stagingConstructor = staging.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
		MethodInfo transfer = staging.GetMethod("Transfer", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Assert.Contains(factory.GetMethodBody()!.ExceptionHandlingClauses, clause => clause.Flags == ExceptionHandlingClauseOptions.Finally);
		Assert.Contains(ReferencedMembers(frameConstructor), member => member == transfer);
		Assert.DoesNotContain(
			ReferencedMembers(factory),
			member => member == transfer);
		IlInstruction[] factoryInstructions = ReadInstructions(factory).ToArray();
		IlInstruction scalarLimit = Assert.Single(
			factoryInstructions,
			instruction => instruction.OpCode == OpCodes.Ldc_I4 && instruction.Int32Operand == 67_240_012);
		IlInstruction stagingAllocation = Assert.Single(
			factoryInstructions,
			instruction => instruction.Member == stagingConstructor);
		IlInstruction scalarGuard = Assert.Single(
			factoryInstructions,
			instruction => instruction.Offset > scalarLimit.Offset &&
				instruction.Offset < stagingAllocation.Offset &&
				instruction.OpCode.FlowControl == FlowControl.Cond_Branch);
		int acceptedTarget = Assert.IsType<int>(scalarGuard.BranchTarget);
		Assert.InRange(acceptedTarget, scalarGuard.Offset + 1, stagingAllocation.Offset);
		Assert.Contains(
			factoryInstructions,
			instruction => instruction.Offset > scalarGuard.Offset &&
				instruction.Offset < acceptedTarget &&
				instruction.OpCode == OpCodes.Throw);
		Assert.DoesNotContain(
			factoryInstructions,
			instruction => instruction.Member == stagingConstructor && instruction.Offset < acceptedTarget);
		MethodInfo validator = owner.GetMethod("IsCanonicalStructuralRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
		Assert.DoesNotContain(validator.GetMethodBody()!.LocalVariables, local => local.LocalType == typeof(byte[]));

		Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(LiquidWalletFactsWireV1DescriptorNetworkClass)));
		Assert.Equal(new[] { "Mainnet", "Test" }, Enum.GetNames<LiquidWalletFactsWireV1DescriptorNetworkClass>());
		Assert.Equal(new byte[] { 0, 1 }, Enum.GetValues<LiquidWalletFactsWireV1DescriptorNetworkClass>().Select(value => (byte)value));
		Assert.True(codec.IsAbstract && codec.IsSealed);
		Assert.True(owner.IsSealed);
		Assert.True(candidate.IsSealed);
		byte[] candidateTransaction = [0xde, 0xad];
		byte[] candidatePrevious = [0xbe, 0xef];
		try
		{
			Assert.Equal(
				nameof(LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource),
				new CandidateSource(candidateTransaction, new ReadOnlyMemory<byte>[] { candidatePrevious }).ToString());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(candidateTransaction);
			CryptographicOperations.ZeroMemory(candidatePrevious);
		}
		Assert.Equal(new[] { typeof(IDisposable) }, owner.GetInterfaces());
		Type candidateStorage = Assert.Single(candidate.GetInterfaces());
		Assert.True(candidateStorage.IsNestedPrivate);
		PropertyInfo[] candidateStorageProperties = candidate.GetProperties(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		Assert.Equal(2, candidateStorageProperties.Length);
		Assert.All(
			candidateStorageProperties,
			property => Assert.True(property.GetMethod!.IsPrivate));
		Assert.All(candidate.GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => Assert.True(field.IsPrivate));
		AssertNonPrivateDeclaredMemberNames(codec, "TryBuildUnpreparedFrame");
		AssertNonPrivateDeclaredMemberNames(candidate, ".ctor", "ToString");
		AssertNonPrivateDeclaredMemberNames(
			owner,
			"CopyFrameTo",
			"CreateStructuralUnpreparedCopy",
			"Dispose",
			"ToString",
			"get_Length");

		Type[] allowed = [codec, owner];
		foreach (Type type in owner.Assembly.GetTypes())
		{
			foreach (FieldInfo field in type.GetFields(AllDeclared))
			{
				if (ContainsType(field.FieldType, owner))
				{
					Assert.Contains(type, allowed);
				}
			}

			foreach (MethodBase method in type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared)))
			{
				bool signatureReference = method.GetParameters().Any(parameter => ContainsType(parameter.ParameterType, owner)) ||
					method is MethodInfo methodInfo && ContainsType(methodInfo.ReturnType, owner);
				if (signatureReference || ReferencedMembers(method).Any(member => member.DeclaringType == owner || member == owner))
				{
					Assert.Contains(type, allowed);
				}
			}
		}

		CryptographicOperations.ZeroMemory(stagedBytes);
	}

	[Fact]
	public void ProductionSliceHasNoExcludedCapabilitiesOrAuthorityTypes()
	{
		Type codec = typeof(LiquidWalletFactsWireV1StructuralRequestCodec);
		Type owner = typeof(LiquidWalletFactsWireV1UnpreparedRequestFrame);
		Type networkClass = typeof(LiquidWalletFactsWireV1DescriptorNetworkClass);
		Type[] sliceTypes = DescendantTypes(codec)
			.Concat(DescendantTypes(owner))
			.Append(networkClass)
			.Distinct()
			.ToArray();
		string[] forbiddenMemberNames =
		[
			"EncodeRequest",
			"ValidatedRequest",
			"PreparedRequest",
			"GetEnumerator",
			"Clone",
			"Serialize",
			"Deserialize",
			"op_Implicit",
			"op_Explicit",
		];

		foreach (Type type in sliceTypes)
		{
			Assert.False(IsExcludedCapabilityType(type), type.FullName);
			Assert.All(
				type.GetInterfaces(),
				interfaceType => Assert.False(IsExcludedCapabilityType(interfaceType), type.FullName));
			Assert.DoesNotContain(type.GetCustomAttributesData(), attribute => IsExcludedCapabilityType(attribute.AttributeType));

			foreach (FieldInfo field in type.GetFields(AllDeclared))
			{
				Assert.False(IsExcludedCapabilityType(field.FieldType), $"{type.FullName}.{field.Name}");
				Assert.DoesNotContain(field.GetCustomAttributesData(), attribute => IsExcludedCapabilityType(attribute.AttributeType));
			}

			foreach (PropertyInfo property in type.GetProperties(AllDeclared))
			{
				Assert.False(IsExcludedCapabilityType(property.PropertyType), $"{type.FullName}.{property.Name}");
				Assert.DoesNotContain(property.GetCustomAttributesData(), attribute => IsExcludedCapabilityType(attribute.AttributeType));
			}

			foreach (EventInfo eventInfo in type.GetEvents(AllDeclared))
			{
				Assert.False(IsExcludedCapabilityType(eventInfo.EventHandlerType!), $"{type.FullName}.{eventInfo.Name}");
			}

			foreach (MethodBase method in type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared)))
			{
				Assert.DoesNotContain(method.Name, forbiddenMemberNames);
				Assert.False((method.Attributes & MethodAttributes.PinvokeImpl) != 0, $"P/Invoke: {type.FullName}.{method.Name}");
				Assert.All(
					method.GetParameters(),
					parameter => Assert.False(IsExcludedCapabilityType(parameter.ParameterType), $"{type.FullName}.{method.Name}"));
				if (method is MethodInfo methodInfo)
				{
					Assert.False(IsExcludedCapabilityType(methodInfo.ReturnType), $"{type.FullName}.{method.Name}");
				}

				Assert.DoesNotContain(method.GetCustomAttributesData(), attribute => IsExcludedCapabilityType(attribute.AttributeType));
				foreach (MemberInfo referenced in ReferencedMembers(method))
				{
					Type? referencedType = referenced as Type ?? referenced.DeclaringType;
					if (referencedType is not null)
					{
						Assert.False(IsExcludedCapabilityType(referencedType), $"{type.FullName}.{method.Name} -> {referencedType.FullName}");
					}
				}
			}
		}
	}

	[Fact]
	public void SymbolicRequestBoundariesRemainExactWithoutGiantFrames()
	{
		IReadOnlyList<string[]> rows = WalletFactsWireV1Corpus.ReadRows(
			"BOUNDARIES_V1.tsv",
			"boundary_id",
			"operation",
			"boundary_kind",
			"production_constant",
			"numeric_domain",
			"formula",
			"expected_status",
			"expected_value",
			"expected_error_code");
		Assert.Contains(rows, row => row.SequenceEqual(
			["batch-bytes-maximum", "request-decode", "component-limit", "max-batch-bytes", "usize64", "67108864", "ok", "67108864", "0"]));
		Assert.Contains(rows, row => row.SequenceEqual(
			["batch-bytes-plus-one", "request-decode", "component-limit", "max-batch-bytes", "usize64", "67108864+1", "rejected", "67108865", "4"]));
		Assert.Contains(rows, row => row.SequenceEqual(
			["reachable-request-bytes", "checked-arithmetic", "reachable-maximum", "max-reachable-request-bytes", "usize64", "76+16384+4096*12+16384*4+67108864", "ok", "67240012", "0"]));
		Assert.Contains(rows, row => row.SequenceEqual(
			["request-outer-ceiling", "request-outer-length-check", "outer-ceiling", "max-request-frame-bytes", "usize64", "268435456", "ok", "268435456", "0"]));
		Assert.Contains(rows, row => row.SequenceEqual(
			["request-outer-plus-one", "request-outer-length-check", "outer-ceiling", "max-request-frame-bytes", "usize64", "268435456+1", "rejected", "268435457", "4"]));
		Assert.Contains(rows, row => row.SequenceEqual(
			["arithmetic-add-overflow", "checked-arithmetic", "arithmetic-rejection", "none", "u64", "18446744073709551615+1", "overflow", "-", "4"]));
		Assert.Contains(rows, row => row.SequenceEqual(
			["arithmetic-multiply-overflow", "checked-arithmetic", "arithmetic-rejection", "none", "u64", "18446744073709551615*2", "overflow", "-", "4"]));

		ulong[] literalValues = typeof(LiquidWalletFactsWireV1StructuralRequestCodec)
			.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
			.Where(field => field.IsLiteral && field.GetRawConstantValue() is not null)
			.Select(field => Convert.ToUInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture))
			.ToArray();
		Assert.All(
			new ulong[] { 76, 12, 4, 16_384, 4_096, 16_384, 4_194_304, 67_108_864, 67_240_012, 268_435_456, 100_000 },
			value => Assert.Contains(value, literalValues));
	}

	private static void AssertBuiltRecipe(string[] recipe, CorpusFrame expected)
	{
		byte[] source = Convert.FromHexString(recipe[2]);
		byte[] descriptor = Convert.FromHexString(recipe[5]);
		CandidateSource[] candidates = ParseCandidates(recipe[6]);
		byte[] built = Build(
			source,
			recipe[3] == "mainnet" ? LiquidWalletFactsWireV1DescriptorNetworkClass.Mainnet : LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
			uint.Parse(recipe[4], CultureInfo.InvariantCulture),
			descriptor,
			candidates);
		try
		{
			Assert.Equal(expected.Bytes, built);
			Assert.Equal(expected.Bytes.Length, built.Length);
			Assert.Equal(SHA256.HashData(expected.Bytes), SHA256.HashData(built));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(source);
			CryptographicOperations.ZeroMemory(descriptor);
			CryptographicOperations.ZeroMemory(built);
		}
	}

	private static CandidateSource[] ParseCandidates(string value)
	{
		if (value == "-")
		{
			return [];
		}

		return value.Split(';').Select(record =>
		{
			string[] parts = record.Split(':');
			Assert.Equal(2, parts.Length);
			byte[] transaction = Convert.FromHexString(parts[0]);
			ReadOnlyMemory<byte>[] previous = parts[1] == "-"
				? []
				: parts[1].Split(',').Select(item => new ReadOnlyMemory<byte>(Convert.FromHexString(item))).ToArray();
			return new CandidateSource(transaction, previous);
		}).ToArray();
	}

	private static byte[] Build(
		ReadOnlySpan<byte> source,
		LiquidWalletFactsWireV1DescriptorNetworkClass network,
		uint derivation,
		ReadOnlySpan<byte> descriptor,
		IReadOnlyList<CandidateSource> candidates)
	{
		LiquidWalletFactsWireV1UnpreparedRequestFrame? frame = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
				source,
				network,
				derivation,
				descriptor,
				candidates,
				out frame,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1UnpreparedRequestFrame owner = Assert.IsType<LiquidWalletFactsWireV1UnpreparedRequestFrame>(frame);
			byte[] copy = new byte[owner.Length];
			owner.CopyFrameTo(copy);
			return copy;
		}
		finally
		{
			frame?.Dispose();
		}
	}

	private static void AssertRejected(
		ReadOnlySpan<byte> source,
		LiquidWalletFactsWireV1DescriptorNetworkClass network,
		uint derivation,
		ReadOnlySpan<byte> descriptor,
		IReadOnlyList<CandidateSource> candidates,
		LiquidWalletFactsWireErrorCode expected)
	{
		LiquidWalletFactsWireV1UnpreparedRequestFrame? frame = null;
		try
		{
			Assert.False(LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
				source,
				network,
				derivation,
				descriptor,
				candidates,
				out frame,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Null(frame);
			Assert.Equal(expected, errorCode);
			Assert.DoesNotContain(errorCode, new[]
			{
				LiquidWalletFactsWireErrorCode.VersionMismatch,
				LiquidWalletFactsWireErrorCode.DescriptorRejected,
				LiquidWalletFactsWireErrorCode.CandidateRejected,
				LiquidWalletFactsWireErrorCode.ObservationRejected,
				LiquidWalletFactsWireErrorCode.SourceBindingMismatch,
			});
		}
		finally
		{
			frame?.Dispose();
		}
	}

	private static void AssertCandidate(
		ReadOnlySpan<byte> frame,
		ref int cursor,
		ReadOnlySpan<byte> transaction,
		params byte[][] previous)
	{
		Assert.Equal((uint)transaction.Length, ReadUInt32(frame, ref cursor));
		Assert.Equal((uint)previous.Length, ReadUInt32(frame, ref cursor));
		Assert.Equal(0u, ReadUInt32(frame, ref cursor));
		Assert.True(Take(frame, ref cursor, transaction.Length).SequenceEqual(transaction));
		foreach (byte[] item in previous)
		{
			Assert.Equal((uint)item.Length, ReadUInt32(frame, ref cursor));
			Assert.True(Take(frame, ref cursor, item.Length).SequenceEqual(item));
		}
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> frame, ref int cursor) =>
		BinaryPrimitives.ReadUInt32LittleEndian(Take(frame, ref cursor, sizeof(uint)));

	private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> frame, ref int cursor, int length)
	{
		ReadOnlySpan<byte> value = frame.Slice(cursor, length);
		cursor = checked(cursor + length);
		return value;
	}

	private static void AssertNonPrivateDeclaredMemberNames(Type type, params string[] expectedNames)
	{
		string[] actual = type.GetMethods(AllDeclared)
			.Cast<MethodBase>()
			.Concat(type.GetConstructors(AllDeclared))
			.Where(member => !member.IsPrivate)
			.Select(member => member.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(expectedNames.Order(StringComparer.Ordinal), actual);
	}

	private static bool ContainsType(Type candidate, Type target)
	{
		if (candidate == target)
		{
			return true;
		}
		if (candidate.HasElementType)
		{
			return ContainsType(candidate.GetElementType()!, target);
		}
		return candidate.IsGenericType && candidate.GetGenericArguments().Any(argument => ContainsType(argument, target));
	}

	private static IEnumerable<Type> DescendantTypes(Type root)
	{
		yield return root;
		foreach (Type nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
		{
			foreach (Type descendant in DescendantTypes(nested))
			{
				yield return descendant;
			}
		}
	}

	private static bool IsExcludedCapabilityType(Type type)
	{
		if (type.IsPointer || type.IsFunctionPointer)
		{
			return true;
		}
		if (type.HasElementType)
		{
			return IsExcludedCapabilityType(type.GetElementType()!);
		}
		if (type.IsGenericType && type.GetGenericArguments().Any(IsExcludedCapabilityType))
		{
			return true;
		}

		string fullName = type.FullName ?? type.Name;
		string[] exactForbiddenNames =
		[
			"LiquidWalletObservationBatch",
			"LiquidWalletTransactionObservation",
			"LiquidOwnedOutputObservation",
			"LiquidWalletState",
			"System.Environment",
			"System.Random",
			"System.DateTime",
			"System.DateTimeOffset",
			"System.TimeProvider",
			"System.Security.Cryptography.RandomNumberGenerator",
		];
		if (exactForbiddenNames.Contains(type.Name, StringComparer.Ordinal) ||
			exactForbiddenNames.Contains(fullName, StringComparer.Ordinal))
		{
			return true;
		}

		string[] forbiddenNamespaces =
		[
			"System.IO",
			"System.Net",
			"System.Reflection",
			"System.Runtime.InteropServices",
			"System.Runtime.Serialization",
			"System.Xml",
			"System.Text.Json",
		];
		if (forbiddenNamespaces.Any(prefix =>
			fullName.Equals(prefix, StringComparison.Ordinal) ||
			fullName.StartsWith(prefix + ".", StringComparison.Ordinal)))
		{
			return true;
		}

		string[] forbiddenNameFragments =
		[
			"BlindingKey",
			"Broadcast",
			"CoinJoin",
			"DllImport",
			"FeeSponsor",
			"FunctionPointer",
			"MarshalAs",
			"Mnemonic",
			"NativeLibrary",
			"NetworkManifest",
			"PrivateKey",
			"Pset",
			"ReplayState",
			"RpcClient",
			"Signer",
			"Signing",
			"StructLayout",
			"UsdtCoinJoin",
		];
		return forbiddenNameFragments.Any(fragment => fullName.Contains(fragment, StringComparison.OrdinalIgnoreCase)) ||
			fullName.Equals("System.Diagnostics.Process", StringComparison.Ordinal) ||
			fullName.StartsWith("System.Diagnostics.Process.", StringComparison.Ordinal);
	}

	private static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
	{
		foreach (IlInstruction instruction in ReadInstructions(method))
		{
			if (instruction.Member is not null)
			{
				yield return instruction.Member;
			}
		}
	}

	private static IEnumerable<IlInstruction> ReadInstructions(MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		int offset = 0;
		while (offset < il.Length)
		{
			int instructionOffset = offset;
			short value = il[offset++] == 0xfe
				? unchecked((short)(0xfe00 | il[offset++]))
				: il[offset - 1];
			OpCode opcode = OpcodeByValue[value];
			int? int32Operand = null;
			int? branchTarget = null;
			MemberInfo? member = null;
			switch (opcode.OperandType)
			{
				case OperandType.InlineField:
				case OperandType.InlineMethod:
				case OperandType.InlineTok:
				case OperandType.InlineType:
					int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, sizeof(int)));
					offset += sizeof(int);
					member = method.Module.ResolveMember(
						token,
						method.DeclaringType?.GetGenericArguments(),
						method.IsGenericMethod ? method.GetGenericArguments() : null);
					break;
				case OperandType.InlineBrTarget:
					int branchDelta = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, sizeof(int)));
					offset += sizeof(int);
					branchTarget = checked(offset + branchDelta);
					break;
				case OperandType.InlineI:
					int32Operand = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, sizeof(int)));
					offset += sizeof(int);
					break;
				case OperandType.InlineSig:
				case OperandType.InlineString:
				case OperandType.ShortInlineR:
					offset += 4;
					break;
				case OperandType.InlineI8:
				case OperandType.InlineR:
					offset += 8;
					break;
				case OperandType.InlineVar:
					offset += 2;
					break;
				case OperandType.ShortInlineBrTarget:
					branchTarget = checked(offset + 1 + unchecked((sbyte)il[offset]));
					offset += 1;
					break;
				case OperandType.ShortInlineI:
					int32Operand = unchecked((sbyte)il[offset]);
					offset += 1;
					break;
				case OperandType.ShortInlineVar:
					offset += 1;
					break;
				case OperandType.InlineSwitch:
					int count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, sizeof(int)));
					offset += sizeof(int) + checked(count * sizeof(int));
					break;
				case OperandType.InlineNone:
					break;
				default:
					throw new InvalidOperationException($"Unsupported IL operand: {opcode.OperandType}");
			}

			yield return new IlInstruction(instructionOffset, opcode, int32Operand, branchTarget, member);
		}
	}

	private sealed record IlInstruction(
		int Offset,
		OpCode OpCode,
		int? Int32Operand,
		int? BranchTarget,
		MemberInfo? Member);

	private static readonly BindingFlags AllDeclared =
		BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private static readonly IReadOnlyDictionary<short, OpCode> OpcodeByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(OpCode))
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(opcode => opcode.Value);

	private sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
	{
		public int Count => throw new InvalidOperationException("collection inspected");

		public T this[int index] => throw new InvalidOperationException("collection inspected");

		public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("collection inspected");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CountedThrowingList<T>(int count) : IReadOnlyList<T>
	{
		public int Count { get; } = count;

		public T this[int index] => throw new InvalidOperationException("collection entry inspected");

		public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("collection enumerated");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CountCallbackReadOnlyList<T>(IReadOnlyList<T> items, Action countCallback) : IReadOnlyList<T>
	{
		public int Count
		{
			get
			{
				CountReads++;
				countCallback();
				return items.Count;
			}
		}

		public int CountReads { get; private set; }

		public T this[int index] => items[index];

		public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class RepeatedReadOnlyMemoryList(int count, ReadOnlyMemory<byte> item) : IReadOnlyList<ReadOnlyMemory<byte>>
	{
		public int Count { get; } = count;

		public ReadOnlyMemory<byte> this[int index]
		{
			get
			{
				if ((uint)index >= (uint)Count)
				{
					throw new ArgumentOutOfRangeException(nameof(index));
				}
				return item;
			}
		}

		public IEnumerator<ReadOnlyMemory<byte>> GetEnumerator() =>
			Enumerable.Repeat(item, Count).GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class RepeatedThenTailReadOnlyMemoryList(
		int repeatedCount,
		ReadOnlyMemory<byte> repeated,
		ReadOnlyMemory<byte> tail) : IReadOnlyList<ReadOnlyMemory<byte>>
	{
		public int Count { get; } = checked(repeatedCount + 1);

		public int TailReads { get; private set; }

		public ReadOnlyMemory<byte> this[int index]
		{
			get
			{
				if ((uint)index >= (uint)Count)
				{
					throw new ArgumentOutOfRangeException(nameof(index));
				}

				if (index == repeatedCount)
				{
					TailReads++;
					return tail;
				}

				return repeated;
			}
		}

		public IEnumerator<ReadOnlyMemory<byte>> GetEnumerator() =>
			Enumerable.Repeat(repeated, repeatedCount).Append(tail).GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CountedProbeThrowingList<T>(int count) : IReadOnlyList<T>
	{
		public int Count { get; } = count;

		public int IndexReads { get; private set; }

		public T this[int index]
		{
			get
			{
				IndexReads++;
				throw new InvalidOperationException("collection entry inspected");
			}
		}

		public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("collection enumerated");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class ProbedReadOnlyList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
	{
		public int Count => items.Count;

		public List<int> IndexReads { get; } = [];

		public T this[int index]
		{
			get
			{
				IndexReads.Add(index);
				return items[index];
			}
		}

		public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class EarlierNullLaterThrowingCandidateList : IReadOnlyList<CandidateSource>
	{
		public int Count => 2;

		public List<int> IndexReads { get; } = [];

		public CandidateSource this[int index]
		{
			get
			{
				IndexReads.Add(index);
				return index == 0
					? null!
					: throw new InvalidOperationException("later candidate inspected");
			}
		}

		public IEnumerator<CandidateSource> GetEnumerator() => throw new InvalidOperationException("collection enumerated");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
