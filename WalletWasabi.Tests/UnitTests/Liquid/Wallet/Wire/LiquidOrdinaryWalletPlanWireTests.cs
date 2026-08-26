using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Wire;
using Xunit;
using LiquidOrdinaryWalletPlanEncodedFrame = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanEncodedFrame;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;
using LiquidOrdinaryWalletPlanFundingRow = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingRow;
using LockedPackageAuthority = (string Type, string? Requested, string ResolvedVersion, string? ContentHash, System.Collections.Generic.IReadOnlyDictionary<string, string> Dependencies);

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

public class LiquidOrdinaryWalletPlanWireTests
{
	private const string IssuedAssetHex =
		"2222222222222222222222222222222222222222222222222222222222222222";
	private const string PublicKeyHex =
		"0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string FirstScriptHex = "00140102030405060708090a0b0c0d0e0f1011121314";
	private const string SecondScriptHex = "001415161718191a1b1c1d1e1f202122232425262728";
	private static readonly byte[] SourceEpoch = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();


	[Fact]
	public void ErrorCodesAndMessagesAreFrozenAndPrivacyRedacted()
	{
		Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(LiquidOrdinaryWalletPlanWireErrorCode)));
		Assert.Equal(
			[
				"None",
				"InvalidArgument",
				"VersionMismatch",
				"InvalidEncoding",
				"LimitExceeded",
				"SourceBindingMismatch",
				"ContextRejected",
				"PlanRejected",
				"FundingRejected",
			],
			Enum.GetNames<LiquidOrdinaryWalletPlanWireErrorCode>());
		Assert.Equal(
			Enumerable.Range(0, 9).Select(value => (uint)value),
			Enum.GetValues<LiquidOrdinaryWalletPlanWireErrorCode>().Select(value => (uint)value));

		AssertExactErrorMessageMapping(code => code.GetMessage());
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactErrorMessageMapping(code =>
				code == LiquidOrdinaryWalletPlanWireErrorCode.FundingRejected
					? "ordinary wallet plan wire funding was rejecteD"
					: code.GetMessage()));

		string[] messages = Enumerable.Range(1, 8)
			.Select(value => ((LiquidOrdinaryWalletPlanWireErrorCode)value).GetMessage())
			.ToArray();
		Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
		Assert.All(messages, message =>
		{
			Assert.Equal(message.ToLowerInvariant(), message);
			Assert.DoesNotContain(IssuedAssetHex, message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("transaction", message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("address", message, StringComparison.OrdinalIgnoreCase);
		});
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidOrdinaryWalletPlanWireErrorCode.None.GetMessage());
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			((LiquidOrdinaryWalletPlanWireErrorCode)9).GetMessage());
	}

	[Fact]
	public void FundingRowNullPrecedenceIsAllocationFreeAndExhaustive()
	{
		var hostile = new ThrowingPayloadList();
		AssertRowRejected(
			null,
			hostile,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		Assert.Equal(0, hostile.CountReads);

		AssertRowRejected(
			[1],
			null,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertRowRejected(
			[],
			new byte[]?[] { [2], null, [1] },
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertRowRejected(
			new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength + 1],
			new byte[]?[] { null },
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
	}

	[Fact]
	public void FundingRowEnforcesEveryLengthCountAndOrderingBoundary()
	{
		AssertRowRejected([], [], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		AssertRowRejected([1], [[]], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		AssertRowRejected(
			[1],
			new NegativeCountList<byte[]?>(),
			LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);

		byte[] oversized = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength + 1];
		try
		{
			AssertRowRejected(oversized, [], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
			AssertRowRejected([1], [oversized], LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(oversized);
		}

		byte[] same = [1, 2];
		AssertRowRejected([1], [same, same], LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding);
		AssertRowRejected([1], [[2], [1]], LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding);

		byte[] maximumPayload = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength];
		try
		{
			AssertRowRejected(
				[1],
				Enumerable.Repeat<byte[]?>(maximumPayload, 16).ToArray(),
				LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(maximumPayload);
		}

		byte[] shared = [1];
		var overCount = Enumerable.Repeat<byte[]?>(
			shared,
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount + 1).ToArray();
		AssertRowRejected([1], overCount, LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);

		byte[] maximumCandidate = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength];
		maximumCandidate[^1] = 1;
		try
		{
			using LiquidOrdinaryWalletPlanFundingRow maximum = CreateRow(maximumCandidate);
			Assert.Equal(
				LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength,
				GetField<byte[]>(maximum, "_candidateTransaction").Length);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(maximumCandidate);
		}

		byte[]?[] maximumPrevious = Enumerable.Range(
			0,
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount)
			.Select(index => (byte[]?)[(byte)(index >> 8), (byte)index])
			.ToArray();
		using LiquidOrdinaryWalletPlanFundingRow maximumPreviousRow = CreateRow([1], maximumPrevious);
		Assert.Equal(
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount,
			GetField<byte[][]>(maximumPreviousRow, "_previousTransactions").Length);
	}

	[Fact]
	public void FundingRowRejectsHostileOversizedCountBeforeSnapshotAllocationOrPayloadCopy()
	{
		byte[] payload = [0x7a];
		var oversized = new RepeatedValueList<byte[]?>(
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount + 1,
			payload);
		AssertRowRejected([1], oversized, LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		long before = GC.GetAllocatedBytesForCurrentThread();
		AssertRowRejected([1], oversized, LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(allocated < 1_024, $"Oversized funding-row rejection allocated {allocated} bytes.");
		Assert.Equal(4 * oversized.Count, oversized.ReadCount);

		var nullLast = new RepeatedValueList<byte[]?>(
			LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount + 1,
			payload,
			nullAt: LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount);
		AssertRowRejected(
			new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength + 1],
			nullLast,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
	}

	[Fact]
	public void FundingRowDefensivelyCopiesAndClearsEveryOwnedPayload()
	{
		byte[] candidate = [0xaa, 0xbb];
		byte[] previousA = [0x01];
		byte[] previousB = [0x02, 0x00];
		var source = new byte[]?[] { previousA, previousB };
		LiquidOrdinaryWalletPlanFundingRow row = CreateRow(candidate, source);
		byte[] retainedCandidate = GetField<byte[]>(row, "_candidateTransaction");
		byte[][] retainedPrevious = GetField<byte[][]>(row, "_previousTransactions");
		byte[] retainedPreviousA = retainedPrevious[0];
		byte[] retainedPreviousB = retainedPrevious[1];

		candidate.AsSpan().Fill(0xff);
		previousA.AsSpan().Fill(0xff);
		previousB.AsSpan().Fill(0xff);
		source[0] = [0xee];
		Assert.Equal(new byte[] { 0xaa, 0xbb }, retainedCandidate);
		Assert.Equal(new byte[] { 0x01 }, retainedPrevious[0]);
		Assert.Equal(new byte[] { 0x02, 0x00 }, retainedPrevious[1]);

		row.Dispose();
		row.Dispose();
		Assert.All(retainedCandidate, value => Assert.Equal(0, value));
		Assert.All(retainedPreviousA, value => Assert.Equal(0, value));
		Assert.All(retainedPreviousB, value => Assert.Equal(0, value));
		Assert.True(GetField<bool>(row, "_disposed"));
		Assert.Equal(nameof(LiquidOrdinaryWalletPlanFundingRow), row.ToString());
	}

	[Fact]
	public async Task FundingRowSnapshotsStatefulAndConcurrentlyMutatedSourcesAfterNullPreflightAsync()
	{
		var stableSnapshot = new StatefulPayloadList(
			firstReads: [[0xf1], [0xf2]],
			snapshotReads: [[0x01], [0x02]]);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([0xaa], stableSnapshot);
		Assert.Equal(2, GetField<byte[][]>(row, "_previousTransactions").Length);
		Assert.Equal([3, 3], stableSnapshot.ReadCounts);

		var nullOnSnapshot = new StatefulPayloadList(
			firstReads: [[1]],
			snapshotReads: [null]);
		AssertRowRejected(
			[],
			nullOnSnapshot,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		Assert.Equal([2], nullOnSnapshot.ReadCounts);

		using var firstRead = new ManualResetEventSlim();
		using var mutationComplete = new ManualResetEventSlim();
		byte[]? concurrentValue = [1];
		var concurrent = new CoordinatedSingleItemList<byte[]?>(
			() => concurrentValue,
			firstRead,
			mutationComplete);
		Task mutation = Task.Run(() =>
		{
			firstRead.Wait();
			concurrentValue = null;
			mutationComplete.Set();
		});
		AssertRowRejected(
			[],
			concurrent,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		await mutation;
	}

	[Fact]
	public void FundingBatchNullLifecycleAndCountPrecedenceIsFrozen()
	{
		LiquidOrdinaryWalletExactSpendPlan oneInput = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			100);
		LiquidOrdinaryWalletExactSpendPlan twoInputs = CreateTwoAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet).Plan;
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([2]);

		AssertBatchRejected(null, null, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(oneInput, null, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(oneInput, [null], LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(oneInput, [], LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertBatchRejected(
			oneInput,
			new NegativeCountList<LiquidOrdinaryWalletPlanFundingRow?>(),
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		first.Dispose();
		AssertBatchRejected(
			twoInputs,
			[first, null],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		ObjectDisposedException disposedBeforeCount = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
				oneInput,
				[first, second],
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding row is disposed.",
			disposedBeforeCount.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public void FundingBatchEnforcesExpandedPreviousCountBeforeCopying()
	{
		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		byte[]?[] previous = Enumerable.Range(0, 8_193)
			.Select(index => (byte[]?)[(byte)(index >> 8), (byte)index])
			.ToArray();
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([1], previous);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([2], previous);

		AssertBatchRejected(
			fixture.Plan,
			[first, second],
			LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
	}

	[Fact]
	public void FundingBatchRejectsHostileOversizedCountBeforeSnapshotAllocationOrRowCopy()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			149);
		using LiquidOrdinaryWalletPlanFundingRow live = CreateRow([0x7a]);
		var oversized = new RepeatedValueList<LiquidOrdinaryWalletPlanFundingRow?>(
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1,
			live);
		AssertBatchRejected(plan, oversized, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		long before = GC.GetAllocatedBytesForCurrentThread();
		AssertBatchRejected(plan, oversized, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(allocated < 1_024, $"Oversized funding-batch rejection allocated {allocated} bytes.");
		Assert.Equal(4 * oversized.Count, oversized.ReadCount);

		using LiquidOrdinaryWalletPlanFundingRow disposed = CreateRow([0x7b]);
		disposed.Dispose();
		var nullLast = new RepeatedValueList<LiquidOrdinaryWalletPlanFundingRow?>(
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1,
			disposed,
			nullAt: LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount);
		AssertBatchRejected(plan, nullLast, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		var disposedOversized = new RepeatedValueList<LiquidOrdinaryWalletPlanFundingRow?>(
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1,
			disposed);
		ObjectDisposedException lifecycleBeforeCount = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
				plan,
				disposedOversized,
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding row is disposed.",
			lifecycleBeforeCount.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public async Task FundingBatchSnapshotsStatefulAndConcurrentlyMutatedRowsAfterNullPreflightAsync()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			150);
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([2]);
		var stableSnapshot = new StatefulRowList(
			firstReads: [second],
			snapshotReads: [first]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, stableSnapshot);
		Assert.Equal([3], stableSnapshot.ReadCounts);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(plan, batch, SourceEpoch);
		byte[] encoded = Copy(frame);
		try
		{
			Assert.Equal(1, encoded[240]);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(encoded);
		}

		var nullOnSnapshot = new StatefulRowList(
			firstReads: [first],
			snapshotReads: [null]);
		AssertBatchRejected(
			plan,
			nullOnSnapshot,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		using var firstRead = new ManualResetEventSlim();
		using var mutationComplete = new ManualResetEventSlim();
		LiquidOrdinaryWalletPlanFundingRow? concurrentValue = first;
		var concurrent = new CoordinatedSingleItemList<LiquidOrdinaryWalletPlanFundingRow?>(
			() => concurrentValue,
			firstRead,
			mutationComplete);
		Task mutation = Task.Run(() =>
		{
			firstRead.Wait();
			concurrentValue = null;
			mutationComplete.Set();
		});
		AssertBatchRejected(
			plan,
			concurrent,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		await mutation;
	}

	[Fact]
	public void EncoderWritesEveryCanonicalFieldInFrozenOrder()
	{
		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow(
			[0xaa],
			[0x01],
			[0x02, 0x00]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([0xbb, 0xcc]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(fixture.Plan, first, second);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(fixture.Plan, batch, SourceEpoch);
		byte[] encoded = Copy(frame);
		try
		{
			Assert.Equal("WLPQ"u8.ToArray(), encoded[..4]);
			Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(4, 2)));
			Assert.Equal(152, BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(6, 2)));
			Assert.Equal((ulong)encoded.Length, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(8, 8)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(16, 4)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(20, 4)));
			Assert.Equal(SourceEpoch, encoded[24..56]);
			Assert.Equal(fixture.Plan.SourceRevision, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(56, 8)));
			Assert.Equal(Convert.FromHexString(fixture.Manifest.ManifestId), encoded[64..96]);
			Assert.Equal(fixture.PeggedAsset.ToConsensusBytes(), encoded[96..128]);
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(128, 4)));
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(132, 4)));
			Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(136, 4)));
			Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(140, 4)));
			Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(144, 8)));

			int cursor = 152;
			AssertSelectedRow(
				encoded,
				ref cursor,
				fixture.FirstSelected,
				[0xaa],
				[[0x01], [0x02, 0x00]]);
			AssertSelectedRow(
				encoded,
				ref cursor,
				fixture.SecondSelected,
				[0xbb, 0xcc],
				[]);
			AssertDestination(encoded, ref cursor, fixture.FirstDestination);
			AssertDestination(encoded, ref cursor, fixture.SecondDestination);
			Assert.Equal(encoded.Length, cursor);
			Assert.Equal(
				152 + 2 * 88 + 2 * 48 + 2 * 4 + 6 +
				fixture.FirstDestination.GetAddress().GetCanonicalAddressText().Length +
				fixture.SecondDestination.GetAddress().GetCanonicalAddressText().Length,
				encoded.Length);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(encoded);
		}
	}

	[Fact]
	public void WireLimitsAndReachableLengthArithmeticAreFrozen()
	{
		Assert.Equal(32, LiquidOrdinaryWalletPlanWireLimits.SourceEpochLength);
		Assert.Equal(152, LiquidOrdinaryWalletPlanWireLimits.HeaderLength);
		Assert.Equal(88, LiquidOrdinaryWalletPlanWireLimits.SelectedFixedLength);
		Assert.Equal(48, LiquidOrdinaryWalletPlanWireLimits.DestinationFixedLength);
		Assert.Equal(4, LiquidOrdinaryWalletPlanWireLimits.PreviousLengthPrefix);
		Assert.Equal(256, LiquidOrdinaryWalletPlanWireLimits.MaximumAddressLength);
		Assert.Equal(4_194_304, LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength);
		Assert.Equal(16_384, LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount);
		Assert.Equal(67_108_864, LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength);
		Assert.Equal(
			LiquidOrdinaryWalletPlanWireLimits.HeaderLength +
				LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount *
				LiquidOrdinaryWalletPlanWireLimits.SelectedFixedLength +
				LiquidOrdinaryWalletExactSpendPlan.MaximumConfidentialOutputCount *
				LiquidOrdinaryWalletPlanWireLimits.DestinationFixedLength +
				LiquidOrdinaryWalletExactSpendPlan.MaximumConfidentialOutputCount *
				LiquidOrdinaryWalletPlanWireLimits.MaximumAddressLength +
				LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount *
				LiquidOrdinaryWalletPlanWireLimits.PreviousLengthPrefix +
				LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength,
			LiquidOrdinaryWalletPlanWireLimits.MaximumReachableFrameLength);
	}

	[Fact]
	public void EncoderSupportsBothReviewedContextsAndIsDeterministic()
	{
		foreach (ElementsPublicNetworkManifest manifest in new[]
		{
			ElementsPublicNetworkManifest.LiquidMainnet,
			ElementsPublicNetworkManifest.LiquidTestnet,
		})
		{
			LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(manifest, 200);
#pragma warning disable CA2000 // Each disposable owner is immediately declared with using.
			using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([0x01]);
			using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, row);
			using LiquidOrdinaryWalletPlanEncodedFrame first = Encode(plan, batch, SourceEpoch);
			using LiquidOrdinaryWalletPlanEncodedFrame second = Encode(plan, batch, SourceEpoch);
#pragma warning restore CA2000
			byte[] firstBytes = Copy(first);
			byte[] secondBytes = Copy(second);
			try
			{
				Assert.Equal(firstBytes, secondBytes);
				Assert.Equal(Convert.FromHexString(manifest.ManifestId), firstBytes[64..96]);
				Assert.Equal(
					LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId).ToConsensusBytes(),
					firstBytes[96..128]);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(firstBytes);
				CryptographicOperations.ZeroMemory(secondBytes);
			}
		}
	}

	[Fact]
	public void EncoderInvalidArgumentAndDisposedLifecyclePrecedenceIsFrozen()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			300);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, row);

		AssertEncodeRejected([], plan, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(new byte[31], plan, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(new byte[32], plan, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(SourceEpoch, null, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertEncodeRejected(SourceEpoch, plan, null, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		batch.Dispose();
		AssertEncodeRejected(SourceEpoch, null, batch, LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		ObjectDisposedException disposed = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanEncoder.TryEncode(
				new byte[31],
				plan,
				batch,
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding batch is disposed.",
			disposed.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public void BatchBindingMismatchIsInvalidArgumentWithFrozenCombinedPrecedence()
	{
		LiquidOrdinaryWalletExactSpendPlan firstPlan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			401);
		LiquidOrdinaryWalletExactSpendPlan secondPlan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidMainnet,
			402);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(firstPlan, row);

		AssertEncodeRejected(
			new byte[31],
			secondPlan,
			batch,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		LiquidOrdinaryWalletPlanEncodedFrame? frame = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanEncoder.TryEncode(
				SourceEpoch,
				secondPlan,
				batch,
				out frame,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(frame);
			Assert.Equal(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, errorCode);
			string message = errorCode.GetMessage();
			Assert.DoesNotContain("401", message, StringComparison.Ordinal);
			Assert.DoesNotContain("402", message, StringComparison.Ordinal);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidTestnet.ManifestId,
				message,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidMainnet.ManifestId,
				message,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				message,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId,
				message,
				StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			frame?.Dispose();
		}

		batch.Dispose();
		ObjectDisposedException disposed = Assert.Throws<ObjectDisposedException>(() =>
			LiquidOrdinaryWalletPlanEncoder.TryEncode(
				new byte[31],
				secondPlan,
				batch,
				out _,
				out _));
		Assert.Equal(
			"Liquid ordinary-wallet plan funding batch is disposed.",
			disposed.Message.Split(Environment.NewLine)[0]);
	}

	[Fact]
	public void FundingAndFrameOwnershipIsIndependentAndDeterministicallyCleared()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			500);
		byte[] candidate = [0xaa, 0xbb];
		byte[] previous = [0x01];
		byte[] epoch = SourceEpoch.ToArray();
		LiquidOrdinaryWalletPlanFundingRow sourceRow = CreateRow(candidate, previous);
		LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, sourceRow);
		LiquidOrdinaryWalletPlanFundingRow[] retainedRows =
			GetField<LiquidOrdinaryWalletPlanFundingRow[]>(batch, "_rows");
		byte[] retainedCandidate = GetField<byte[]>(retainedRows[0], "_candidateTransaction");
		byte[][] retainedPrevious = GetField<byte[][]>(retainedRows[0], "_previousTransactions");
		byte[] retainedPreviousPayload = retainedPrevious[0];

		sourceRow.Dispose();
		candidate.AsSpan().Fill(0xff);
		previous.AsSpan().Fill(0xff);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(plan, batch, epoch);
		byte[] firstCopy = Copy(frame);
		byte[] secondCopy = Copy(frame);
		byte[] retainedFrame = GetField<byte[]>(frame, "_frame");
		try
		{
			epoch.AsSpan().Fill(0xff);
			firstCopy.AsSpan().Fill(0xee);
			Assert.NotEqual(firstCopy, secondCopy);
			Assert.Contains((byte)0xaa, secondCopy);
			Assert.Equal(SourceEpoch, secondCopy[24..56]);
			Assert.Equal(new byte[] { 0xaa, 0xbb }, retainedCandidate);
			Assert.Equal(new byte[] { 0x01 }, retainedPrevious[0]);
			ArgumentException wrongLength = Assert.Throws<ArgumentException>(() =>
				frame.CopyFrameTo(new byte[frame.Length - 1]));
			Assert.Equal(
				"An exact Liquid ordinary-wallet plan wire frame destination is required. (Parameter 'exactDestination')",
				wrongLength.Message);

			frame.Dispose();
			frame.Dispose();
			Assert.All(retainedFrame, value => Assert.Equal(0, value));
			Assert.Throws<ObjectDisposedException>(() => _ = frame.Length);
			Assert.Throws<ObjectDisposedException>(() => frame.CopyFrameTo(secondCopy));

			batch.Dispose();
			batch.Dispose();
			Assert.All(retainedCandidate, value => Assert.Equal(0, value));
			Assert.All(retainedPreviousPayload, value => Assert.Equal(0, value));
			Assert.All(retainedRows, Assert.Null);
			Assert.All(candidate, value => Assert.Equal(0xff, value));
			Assert.All(previous, value => Assert.Equal(0xff, value));
			Assert.All(epoch, value => Assert.Equal(0xff, value));
			Assert.Equal(nameof(LiquidOrdinaryWalletPlanFundingBatch), batch.ToString());
			Assert.Equal(nameof(LiquidOrdinaryWalletPlanEncodedFrame), frame.ToString());
		}
		finally
		{
			batch.Dispose();
			CryptographicOperations.ZeroMemory(firstCopy);
			CryptographicOperations.ZeroMemory(secondCopy);
			CryptographicOperations.ZeroMemory(epoch);
		}
	}

	[Fact]
	public void SurfaceIsInternalOwnedAndContainsNoExcludedAuthority()
	{
		Type[] ownerTypes =
		[
			typeof(LiquidOrdinaryWalletPlanFundingRow),
			typeof(LiquidOrdinaryWalletPlanFundingBatch),
			typeof(LiquidOrdinaryWalletPlanEncodedFrame),
		];
		foreach (Type type in ownerTypes)
		{
			Assert.False(type.IsVisible);
			Assert.Equal(typeof(LiquidOrdinaryWalletPlanEncoder), type.DeclaringType);
			Assert.True(type.IsSealed);
			Assert.Equal([typeof(IDisposable)], type.GetInterfaces());
			Assert.All(
				type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
				constructor => Assert.True(constructor.IsPrivate));
			Assert.DoesNotContain(type.GetCustomAttributesData(), attribute =>
				attribute.AttributeType.Name.Contains("Serializable", StringComparison.OrdinalIgnoreCase) ||
				attribute.AttributeType.Name.Contains("Debugger", StringComparison.OrdinalIgnoreCase));
		}

		Type encoder = typeof(LiquidOrdinaryWalletPlanEncoder);
		Assert.True(encoder.IsNotPublic);
		Assert.True(encoder.IsAbstract && encoder.IsSealed);
		Assert.Equal(
			["TryEncode"],
			encoder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
				.Where(method => !method.IsPrivate)
				.Select(method => method.Name)
				.Distinct(StringComparer.Ordinal));

		string[] forbidden =
		[
			"Decode", "Native", "PInvoke", "DllImport", "Provider", "Signer", "Pset",
			"Rpc", "Node", "File", "Directory", "Process", "Socket", "Http",
			"Broadcast", "CoinJoin", "Sponsor", "Usdt", "Regtest", "Fault", "Probe", "TestHook",
		];
		Type[] wireTypes = GetExactProductionWireTypes();
		Assert.All(wireTypes, type => Assert.False(type.IsVisible));
		Assert.DoesNotContain(wireTypes, type => forbidden.Any(fragment =>
			type.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
		Assert.DoesNotContain(
			wireTypes.SelectMany(type => type.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly)),
			method => method.GetCustomAttribute<DllImportAttribute>() is not null ||
				forbidden.Any(fragment => method.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
		Assert.DoesNotContain(
			encoder.Assembly.GetReferencedAssemblies(),
			assembly => (assembly.Name ?? "").Contains("liquid-native", StringComparison.OrdinalIgnoreCase));

		FieldInfo capability = Assert.Single(
			encoder.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			field => field.Name == "CooperationCapability");
		Assert.Equal("CooperationCapability", capability.Name);
		Assert.Equal(typeof(object), capability.FieldType);
		Assert.True(capability.IsPrivate && capability.IsInitOnly);
		MethodInfo ensureCooperation = Assert.Single(
			encoder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "EnsureCooperation" &&
				method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(object)]));
		Assert.True(ensureCooperation.IsPrivate);
		AssertCapabilityGuarded(
			typeof(LiquidOrdinaryWalletPlanEncodedFrame),
			ensureCooperation,
			"TakeOwnership");
		AssertCapabilityGuarded(
			typeof(LiquidOrdinaryWalletPlanFundingBatch),
			ensureCooperation,
			"TryEncode");
		AssertCapabilityGuarded(
			typeof(LiquidOrdinaryWalletPlanFundingRow),
			ensureCooperation,
			"CreateOwnedCopy", "EnsureNotDisposed", "GetEncodingShape", "WritePayloads");

		MethodInfo lockedEncode = Assert.Single(
			encoder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "TryEncodeLocked");
		Assert.True(lockedEncode.IsPrivate);
		MethodBase[] lockedCallers = GetExactProductionWireTypes()
			.SelectMany(GetDeclaredMethods)
			.Where(method => GetIlReferences(method).Contains(lockedEncode))
			.ToArray();
		MethodInfo batchEncode = Assert.Single(
			typeof(LiquidOrdinaryWalletPlanFundingBatch).GetMethods(
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "TryEncode");
		Assert.Equal([batchEncode], lockedCallers);
	}

	[Fact]
	public void CooperationCapabilityRejectsDirectTypeStateBypassesBeforeOwnershipTransfer()
	{
		byte[] arbitraryBytes = [0x57, 0x4c, 0x50, 0x51];
		byte[]? callerStorage = arbitraryBytes;
		Assert.Throws<InvalidOperationException>(() =>
			LiquidOrdinaryWalletPlanEncodedFrame.TakeOwnership(null, ref callerStorage));
		Assert.Same(arbitraryBytes, callerStorage);
		Assert.Equal(new byte[] { 0x57, 0x4c, 0x50, 0x51 }, arbitraryBytes);

		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			699);
		using LiquidOrdinaryWalletPlanFundingRow row = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, row);
		Assert.Throws<InvalidOperationException>(() => row.EnsureNotDisposed(null));
		Assert.Throws<InvalidOperationException>(() => row.GetEncodingShape(null));
		Assert.Throws<InvalidOperationException>(() => row.CreateOwnedCopy(null));
		Assert.Throws<InvalidOperationException>(() =>
		{
			int cursor = 0;
			row.WritePayloads(null, new byte[1], ref cursor);
		});
		Assert.Throws<InvalidOperationException>(() => batch.TryEncode(
			null,
			SourceEpoch,
			plan,
			out _,
			out _));
	}

	[Fact]
	public void ProductionSourceInventoryIsExactAndSafe()
	{
		string[] expectedImplementationPaths =
		[
			"Liquid/Wallet/LiquidOrdinaryWalletExactSpendPlan.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanEncodedFrame.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanEncoder.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanFundingBatch.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanFundingRow.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanWireErrorCode.cs",
			"Liquid/Wallet/Wire/LiquidOrdinaryWalletPlanWireLimits.cs",
		];
		string productionRoot = GetProductionRoot();
		string wireRoot = GetWireProductionRoot();

		string[] actualImplementationPaths = Directory
			.GetFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
			.Select(path => NormalizeRelativePath(Path.GetRelativePath(productionRoot, path)))
			.Where(path => path.StartsWith("Liquid/Wallet/", StringComparison.Ordinal))
			.Where(path => IsImplementationContributor(File.ReadAllText(Path.Combine(productionRoot, path))))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(
			expectedImplementationPaths.Order(StringComparer.Ordinal),
			actualImplementationPaths);

		var declaredTypes = new List<string>();
		foreach (string sourcePath in expectedImplementationPaths)
		{
			string source = File.ReadAllText(Path.Combine(productionRoot, sourcePath));
			Assert.True(IsSafeWireSource(source));
			CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
				CSharpSyntaxTree.ParseText(source).GetRoot());
			Assert.DoesNotContain(root.DescendantTrivia(descendIntoTrivia: true),
				trivia => trivia.GetStructure() is DirectiveTriviaSyntax);
			Assert.DoesNotContain(
				root.DescendantTokens(),
				token => token.RawKind is (int)SyntaxKind.UnsafeKeyword or (int)SyntaxKind.ExternKeyword);
			Assert.DoesNotContain(
				root.DescendantNodes(),
				node => node is PointerTypeSyntax or FunctionPointerTypeSyntax or
					ImplicitStackAllocArrayCreationExpressionSyntax or FixedStatementSyntax);
			declaredTypes.AddRange(root.DescendantNodes()
				.OfType<BaseTypeDeclarationSyntax>()
				.Select(declaration => declaration.Identifier.ValueText));
		}

		Assert.Equal(
			new[]
			{
				"EncodingShape",
				"LiquidOrdinaryWalletExactSpendPlan",
				"LiquidOrdinaryWalletPlanEncodedFrame",
				"LiquidOrdinaryWalletPlanEncoder",
				"LiquidOrdinaryWalletPlanFundingBatch",
				"LiquidOrdinaryWalletPlanFundingRow",
				"LiquidOrdinaryWalletPlanWireErrorCode",
				"LiquidOrdinaryWalletPlanWireErrorCodeExtensions",
				"LiquidOrdinaryWalletPlanWireLimits",
			},
			declaredTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
		AssertExactPlanWireAccessorSource(File.ReadAllText(Path.Combine(
			productionRoot,
			"Liquid/Wallet/LiquidOrdinaryWalletExactSpendPlan.cs")));

		string encoderSource = File.ReadAllText(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanEncoder.cs"));
		Assert.Contains("fresh unpredictable epoch", encoderSource, StringComparison.Ordinal);
		Assert.Contains("never reuse", encoderSource, StringComparison.Ordinal);
		Assert.Contains("plaintext", encoderSource, StringComparison.Ordinal);
		Assert.Contains("not a secret", encoderSource, StringComparison.Ordinal);
		Assert.Contains("anti-replay", encoderSource, StringComparison.Ordinal);
		Assert.Contains("variable-time", encoderSource, StringComparison.Ordinal);
		Assert.Contains("linkable", encoderSource, StringComparison.Ordinal);
		Assert.Contains("actual confidential selected assets or values", encoderSource, StringComparison.Ordinal);
		Assert.Contains(
			"caller must clear every destination copy separately",
			File.ReadAllText(Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanEncodedFrame.cs")),
			StringComparison.Ordinal);

		Type[] exactTypes = GetExactProductionWireTypes();
		AssertExactWireTypeNames(
			new[]
			{
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanEncodedFrame",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingBatch",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingRow",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder+LiquidOrdinaryWalletPlanFundingRow+EncodingShape",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireErrorCode",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireErrorCodeExtensions",
				"WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireLimits",
			},
			exactTypes.Select(type => type.FullName!));
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanEncodedFrame),
			"CopyFrameTo", "Dispose", "TakeOwnership", "ToString", "get_Length");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanEncoder),
			"TryEncode");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanFundingBatch),
			"Dispose", "ToString", "TryCreate", "TryEncode");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanFundingRow),
			"CreateOwnedCopy", "Dispose", "EnsureNotDisposed", "GetEncodingShape", "ToString",
			"TryCreate", "WritePayloads");
		AssertNonPrivateMethodNames(
			typeof(LiquidOrdinaryWalletPlanWireErrorCodeExtensions),
			"GetMessage");

		foreach (Type type in exactTypes)
		{
			Assert.False(IsForbiddenWireIdentity(type.FullName ?? type.Name));
			foreach (MemberInfo member in type.GetMembers(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				Assert.False(IsForbiddenWireMember(member), $"forbidden member {type.FullName}.{member.Name}");
			}
			foreach (MethodBase method in GetDeclaredMethods(type))
			{
				Assert.DoesNotContain(
					method.GetMethodBody()?.ExceptionHandlingClauses ?? [],
					clause => clause.Flags == ExceptionHandlingClauseOptions.Clause &&
						IsForbiddenWireType(clause.CatchType));
				Assert.DoesNotContain(
					method.GetMethodBody()?.LocalVariables ?? [],
					local => IsForbiddenWireType(local.LocalType));
				Assert.DoesNotContain(GetIlReferences(method), IsForbiddenWireMember);
			}
		}

		Assert.True(IsForbiddenWireMember(typeof(WalletWasabi.Logging.Logger)
			.GetMethods().First(method => method.Name == "LogInfo")));
		Assert.True(IsForbiddenWireType(typeof(IServiceProvider)));
		Assert.True(IsForbiddenWireType(typeof(WalletWasabi.Liquid.Rpc.ElementsNodeStatus)));
		Assert.True(IsForbiddenWireType(typeof(FileStream)));
		Assert.True(IsForbiddenWireType(typeof(System.Net.Http.HttpClient)));
		Assert.True(IsForbiddenWireType(typeof(Thread)));
		Assert.True(IsForbiddenWireType(typeof(RandomNumberGenerator)));
		Assert.True(IsForbiddenWireType(typeof(NativeLibrary)));
		Assert.True(IsForbiddenWireIdentity("GetRawFrame"));
		Assert.False(IsSafeWireSource("#if DEBUG\ninternal static class Added { }\n#endif"));
		Assert.False(IsSafeWireSource("internal unsafe static class Added { }"));
		Assert.False(IsSafeWireSource("internal static class Added { internal static extern void Call(); }"));
		Assert.False(IsSafeWireSource("internal static class FaultProbe { }"));
		Assert.False(IsSafeWireSource("internal static class Added { private static void TestHook() { } }"));
		Assert.True(IsProductionWireNamespace("WalletWasabi.Liquid.Wallet.Wire.Nested"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
		AssertExactWireTypeNames(
			exactTypes.Select(type => type.FullName!),
			exactTypes.Select(type => type.FullName!)
				.Append("WalletWasabi.Liquid.Wallet.Wire.Nested.Added")));
	}


	[Fact]
	public void EncoderDefenseInDepthRechecksEveryMutableAcceptedTypeState()
	{
		ElementsPublicNetworkManifest testnet = ElementsPublicNetworkManifest.LiquidTestnet;
		ElementsPublicNetworkManifest mainnet = ElementsPublicNetworkManifest.LiquidMainnet;

		LiquidOrdinaryWalletExactSpendPlan contextPlan = CreateSingleAssetPlan(testnet, 601);
		using LiquidOrdinaryWalletPlanFundingRow contextRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch contextBatch = CreateBatch(contextPlan, contextRow);
		SetField(contextPlan, "_destinationNetworkManifestId", new string('0', 64));
		AssertFixedInvariant(contextPlan, contextBatch);

		LiquidOrdinaryWalletExactSpendPlan countPlan = CreateSingleAssetPlan(testnet, 602);
		using LiquidOrdinaryWalletPlanFundingRow countRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch countBatch = CreateBatch(countPlan, countRow);
		SetField(countPlan, "_destinations", Array.Empty<LiquidSuppliedConfidentialDestination>());
		AssertFixedInvariant(countPlan, countBatch);

		PlanFixture orderFixture = CreateTwoAssetPlan(testnet);
		using LiquidOrdinaryWalletPlanFundingRow firstOrderRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow secondOrderRow = CreateRow([2]);
		using LiquidOrdinaryWalletPlanFundingBatch orderBatch = CreateBatch(
			orderFixture.Plan,
			firstOrderRow,
			secondOrderRow);
		LiquidWalletCoinControlEntry[] selected =
			GetField<LiquidWalletCoinControlEntry[]>(orderFixture.Plan, "_selectedEntries");
		selected[1] = selected[0];
		AssertFixedInvariant(orderFixture.Plan, orderBatch);

		LiquidOrdinaryWalletExactSpendPlan destinationPlan = CreateSingleAssetPlan(testnet, 603);
		using LiquidOrdinaryWalletPlanFundingRow destinationRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch destinationBatch = CreateBatch(
			destinationPlan,
			destinationRow);
		LiquidSuppliedConfidentialDestination[] destinations =
			GetField<LiquidSuppliedConfidentialDestination[]>(destinationPlan, "_destinations");
		LiquidAssetId mainnetPegged = LiquidAssetId.ParseRpcHex(mainnet.PeggedAssetId);
		destinations[0] = Destination(mainnet, FirstScriptHex, mainnetPegged, 9);
		AssertFixedInvariant(destinationPlan, destinationBatch);

		LiquidOrdinaryWalletExactSpendPlan conservationPlan = CreateSingleAssetPlan(testnet, 604);
		using LiquidOrdinaryWalletPlanFundingRow conservationRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch conservationBatch = CreateBatch(
			conservationPlan,
			conservationRow);
		LiquidAssetId testnetPegged = LiquidAssetId.ParseRpcHex(testnet.PeggedAssetId);
		GetField<LiquidSuppliedConfidentialDestination[]>(conservationPlan, "_destinations")[0] =
			Destination(testnet, FirstScriptHex, testnetPegged, 8);
		AssertFixedInvariant(conservationPlan, conservationBatch);

		LiquidOrdinaryWalletExactSpendPlan feePlan = CreateSingleAssetPlan(testnet, 605);
		using LiquidOrdinaryWalletPlanFundingRow feeRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch feeBatch = CreateBatch(feePlan, feeRow);
		SetField(feePlan, "_explicitFee", LiquidAssetAmount.Zero(testnetPegged, testnetPegged));
		AssertFixedInvariant(feePlan, feeBatch);

		LiquidOrdinaryWalletExactSpendPlan candidatePlan = CreateSingleAssetPlan(testnet, 606);
		using LiquidOrdinaryWalletPlanFundingRow candidateRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch candidateBatch = CreateBatch(candidatePlan, candidateRow);
		LiquidOrdinaryWalletPlanFundingRow ownedCandidateRow =
			GetField<LiquidOrdinaryWalletPlanFundingRow[]>(candidateBatch, "_rows")[0];
		SetField(ownedCandidateRow, "_candidateTransaction", Array.Empty<byte>());
		AssertFixedInvariant(candidatePlan, candidateBatch);

		LiquidOrdinaryWalletExactSpendPlan previousPlan = CreateSingleAssetPlan(testnet, 607);
		using LiquidOrdinaryWalletPlanFundingRow previousRow = CreateRow([1], [1], [2]);
		using LiquidOrdinaryWalletPlanFundingBatch previousBatch = CreateBatch(previousPlan, previousRow);
		LiquidOrdinaryWalletPlanFundingRow ownedPreviousRow =
			GetField<LiquidOrdinaryWalletPlanFundingRow[]>(previousBatch, "_rows")[0];
		byte[][] previous = GetField<byte[][]>(ownedPreviousRow, "_previousTransactions");
		(previous[0], previous[1]) = (previous[1], previous[0]);
		AssertFixedInvariant(previousPlan, previousBatch);

		LiquidOrdinaryWalletExactSpendPlan malformedAddressPlan = CreateSingleAssetPlan(testnet, 608);
		using LiquidOrdinaryWalletPlanFundingRow malformedAddressRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch malformedAddressBatch = CreateBatch(
			malformedAddressPlan,
			malformedAddressRow);
		LiquidAddress malformedAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(malformedAddressPlan, "_destinations")[0]
				.GetAddress();
		SetField(malformedAddress, "_canonicalAddressText", "malformed-address");
		AssertFixedInvariant(malformedAddressPlan, malformedAddressBatch);

		LiquidOrdinaryWalletExactSpendPlan noncanonicalAddressPlan = CreateSingleAssetPlan(testnet, 609);
		using LiquidOrdinaryWalletPlanFundingRow noncanonicalAddressRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch noncanonicalAddressBatch = CreateBatch(
			noncanonicalAddressPlan,
			noncanonicalAddressRow);
		LiquidAddress noncanonicalAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(noncanonicalAddressPlan, "_destinations")[0]
				.GetAddress();
		SetField(
			noncanonicalAddress,
			"_canonicalAddressText",
			noncanonicalAddress.GetCanonicalAddressText().ToUpperInvariant());
		AssertFixedInvariant(noncanonicalAddressPlan, noncanonicalAddressBatch);

		LiquidOrdinaryWalletExactSpendPlan scriptPlan = CreateSingleAssetPlan(testnet, 610);
		using LiquidOrdinaryWalletPlanFundingRow scriptRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch scriptBatch = CreateBatch(scriptPlan, scriptRow);
		LiquidAddress scriptAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(scriptPlan, "_destinations")[0]
				.GetAddress();
		SetField(scriptAddress, "_scriptPubKey", Convert.FromHexString(SecondScriptHex));
		AssertFixedInvariant(scriptPlan, scriptBatch);

		LiquidOrdinaryWalletExactSpendPlan blindingPlan = CreateSingleAssetPlan(testnet, 611);
		using LiquidOrdinaryWalletPlanFundingRow blindingRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch blindingBatch = CreateBatch(blindingPlan, blindingRow);
		LiquidAddress blindingAddress =
			GetField<LiquidSuppliedConfidentialDestination[]>(blindingPlan, "_destinations")[0]
				.GetAddress();
		SetField(
			blindingAddress,
			"_blindingPublicKey",
			LiquidBlindingPublicKey.Create(Convert.FromHexString(
				"0379be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798")));
		AssertFixedInvariant(blindingPlan, blindingBatch);

		LiquidOrdinaryWalletExactSpendPlan nonhexTransactionPlan = CreateSingleAssetPlan(testnet, 612);
		using LiquidOrdinaryWalletPlanFundingRow nonhexTransactionRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch nonhexTransactionBatch = CreateBatch(
			nonhexTransactionPlan,
			nonhexTransactionRow);
		LiquidTransactionId nonhexTransactionId = GetField<LiquidWalletCoinControlEntry[]>(
			nonhexTransactionPlan,
			"_selectedEntries")[0].OutPoint.TransactionId;
		SetField(nonhexTransactionId, "<CanonicalRpcHex>k__BackingField", new string('g', 64));
		AssertFixedInvariant(nonhexTransactionPlan, nonhexTransactionBatch);

		LiquidOrdinaryWalletExactSpendPlan staleZeroTransactionPlan = CreateSingleAssetPlan(testnet, 613);
		using LiquidOrdinaryWalletPlanFundingRow staleZeroTransactionRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingBatch staleZeroTransactionBatch = CreateBatch(
			staleZeroTransactionPlan,
			staleZeroTransactionRow);
		LiquidTransactionId staleZeroTransactionId = GetField<LiquidWalletCoinControlEntry[]>(
			staleZeroTransactionPlan,
			"_selectedEntries")[0].OutPoint.TransactionId;
		Assert.False(staleZeroTransactionId.IsZero);
		SetField(staleZeroTransactionId, "<CanonicalRpcHex>k__BackingField", new string('0', 64));
		Assert.False(staleZeroTransactionId.IsZero);
		AssertFixedInvariant(staleZeroTransactionPlan, staleZeroTransactionBatch);

		PlanFixture nonhexIssuedFixture = CreateTwoAssetPlan(testnet);
		using LiquidOrdinaryWalletPlanFundingRow nonhexIssuedFirstRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow nonhexIssuedSecondRow = CreateRow([2]);
		using LiquidOrdinaryWalletPlanFundingBatch nonhexIssuedBatch = CreateBatch(
			nonhexIssuedFixture.Plan,
			nonhexIssuedFirstRow,
			nonhexIssuedSecondRow);
		LiquidAssetId nonhexIssuedAsset = AssertSharedIssuedAsset(nonhexIssuedFixture.Plan);
		SetField(
			nonhexIssuedAsset,
			"<CanonicalRpcHex>k__BackingField",
			"g" + IssuedAssetHex[1..]);
		AssertFixedInvariant(nonhexIssuedFixture.Plan, nonhexIssuedBatch);

		PlanFixture zeroIssuedFixture = CreateTwoAssetPlan(testnet);
		using LiquidOrdinaryWalletPlanFundingRow zeroIssuedFirstRow = CreateRow([1]);
		using LiquidOrdinaryWalletPlanFundingRow zeroIssuedSecondRow = CreateRow([2]);
		using LiquidOrdinaryWalletPlanFundingBatch zeroIssuedBatch = CreateBatch(
			zeroIssuedFixture.Plan,
			zeroIssuedFirstRow,
			zeroIssuedSecondRow);
		LiquidAssetId zeroIssuedAsset = AssertSharedIssuedAsset(zeroIssuedFixture.Plan);
		SetField(zeroIssuedAsset, "<CanonicalRpcHex>k__BackingField", new string('0', 64));
		AssertFixedInvariant(zeroIssuedFixture.Plan, zeroIssuedBatch);
	}

	[Fact]
	public void CleanupProofIsTestOnlyAndSuccessStorageHasNoInstrumentationAlias()
	{
		string wireRoot = GetWireProductionRoot();
		AssertOwnedCleanupRegion(
			Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanFundingRow.cs"),
			["ownedCandidate = candidateTransaction.ToArray()", "ownedPrevious[index] = sourcePrevious[index]!.ToArray()"],
			["cleanupOwner?.Dispose()", "Clear(ownedCandidate, ownedPrevious)"]);
		AssertFundingRowOwnershipTransfer(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanFundingRow.cs"));
		AssertOwnedCleanupRegion(
			Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanFundingBatch.cs"),
			["ownedRows[copiedCount++] = copiedRow"],
			["cleanupOwner?.Dispose()", "ownedRows[index].Dispose()"]);
		AssertFundingBatchOwnershipTransfer(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanFundingBatch.cs"));
		AssertOwnedCleanupRegion(
			Path.Combine(wireRoot, "LiquidOrdinaryWalletPlanEncoder.cs"),
			[
				"temporaryFrame = new byte[checked((int)exactLength)]",
				"ownedFrame = LiquidOrdinaryWalletPlanEncodedFrame.TakeOwnership( CooperationCapability, ref temporaryFrame)",
			],
			["ownedFrame?.Dispose()", "CryptographicOperations.ZeroMemory(temporaryFrame)"]);
		AssertOwnershipTransferOrder(Path.Combine(
			wireRoot,
			"LiquidOrdinaryWalletPlanEncodedFrame.cs"));

		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		using LiquidOrdinaryWalletPlanFundingRow first = CreateRow([0xaa], [0x01]);
		using LiquidOrdinaryWalletPlanFundingRow second = CreateRow([0xbb], [0x02]);
		using LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(fixture.Plan, first, second);
		using LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(fixture.Plan, batch, SourceEpoch);
		byte[] expected = Copy(frame);
		byte[] afterCollection = new byte[frame.Length];
		try
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			frame.CopyFrameTo(afterCollection);
			Assert.Equal(expected, afterCollection);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(expected);
			CryptographicOperations.ZeroMemory(afterCollection);
		}
	}

	[Fact]
	public async Task OwnerRacesReturnOnlyCompleteSuccessOrFixedLifecycleOutcomeAsync()
	{
		for (int iteration = 0; iteration < 64; iteration++)
		{
			LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
				ElementsPublicNetworkManifest.LiquidTestnet,
				(uint)(700 + iteration));
			LiquidOrdinaryWalletPlanFundingRow sourceRow = CreateRow([0xaa], [0x01]);
			LiquidOrdinaryWalletPlanFundingBatch batch = CreateBatch(plan, sourceRow);
			LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(plan, batch, SourceEpoch);
			byte[] expected = Copy(frame);
			byte[] destination = Enumerable.Repeat((byte)0xee, frame.Length).ToArray();
			LiquidOrdinaryWalletPlanEncodedFrame? encoded = null;
			LiquidOrdinaryWalletPlanFundingBatch? copiedBatch = null;
			using var start = new ManualResetEventSlim();
			try
			{
				Exception? copyFailure = null;
				Task copy = Task.Run(() =>
				{
					start.Wait();
					try
					{
						frame.CopyFrameTo(destination);
					}
					catch (Exception exception)
					{
						copyFailure = exception;
					}
				});
				Task frameDispose = Task.Run(() =>
				{
					start.Wait();
					frame.Dispose();
				});
				start.Set();
				await Task.WhenAll(copy, frameDispose);
				if (copyFailure is null)
				{
					Assert.Equal(expected, destination);
				}
				else
				{
					AssertFixedDisposed(copyFailure, nameof(LiquidOrdinaryWalletPlanEncodedFrame));
					Assert.All(destination, value => Assert.Equal(0xee, value));
				}

				Exception? encodeFailure = null;
				using var encodeStart = new ManualResetEventSlim();
				Task encode = Task.Run(() =>
				{
					encodeStart.Wait();
					try
					{
						LiquidOrdinaryWalletPlanEncoder.TryEncode(
							SourceEpoch,
							plan,
							batch,
							out encoded,
							out _);
					}
					catch (Exception exception)
					{
						encodeFailure = exception;
					}
				});
				Task batchDispose = Task.Run(() =>
				{
					encodeStart.Wait();
					batch.Dispose();
				});
				encodeStart.Set();
				await Task.WhenAll(encode, batchDispose);
				if (encodeFailure is null)
				{
					Assert.NotNull(encoded);
					byte[] racedFrame = Copy(encoded);
					try
					{
						Assert.Equal(expected, racedFrame);
					}
					finally
					{
						CryptographicOperations.ZeroMemory(racedFrame);
					}
				}
				else
				{
					Assert.Null(encoded);
					AssertFixedDisposed(encodeFailure, nameof(LiquidOrdinaryWalletPlanFundingBatch));
				}

				Exception? rowCopyFailure = null;
				using var rowStart = new ManualResetEventSlim();
				Task rowCopy = Task.Run(() =>
				{
					rowStart.Wait();
					try
					{
						LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
							plan,
							new LiquidOrdinaryWalletPlanFundingRow?[] { sourceRow },
							out copiedBatch,
							out _);
					}
					catch (Exception exception)
					{
						rowCopyFailure = exception;
					}
				});
				Task rowDispose = Task.Run(() =>
				{
					rowStart.Wait();
					sourceRow.Dispose();
				});
				rowStart.Set();
				await Task.WhenAll(rowCopy, rowDispose);
				if (rowCopyFailure is null)
				{
					Assert.NotNull(copiedBatch);
					using LiquidOrdinaryWalletPlanEncodedFrame copiedFrame = Encode(
						plan,
						copiedBatch,
						SourceEpoch);
					byte[] copiedBytes = Copy(copiedFrame);
					try
					{
						Assert.Equal(expected, copiedBytes);
					}
					finally
					{
						CryptographicOperations.ZeroMemory(copiedBytes);
					}
				}
				else
				{
					Assert.Null(copiedBatch);
					AssertFixedDisposed(rowCopyFailure, nameof(LiquidOrdinaryWalletPlanFundingRow));
				}
			}
			finally
			{
				copiedBatch?.Dispose();
				encoded?.Dispose();
				frame.Dispose();
				batch.Dispose();
				sourceRow.Dispose();
				CryptographicOperations.ZeroMemory(expected);
				CryptographicOperations.ZeroMemory(destination);
			}
		}
	}

	private static void AssertSelectedRow(
		byte[] encoded,
		ref int cursor,
		LiquidWalletCoinControlEntry selected,
		byte[] candidate,
		byte[][] previous)
	{
		Assert.Equal(selected.OutPoint.TransactionId.ToConsensusBytes(), encoded[cursor..(cursor + 32)]);
		cursor += 32;
		Assert.Equal(selected.OutPoint.OutputIndex, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(selected.Amount.AssetId.ToConsensusBytes(), encoded[cursor..(cursor + 32)]);
		cursor += 32;
		Assert.Equal((ulong)selected.Amount.AtomicUnits, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(cursor, 8)));
		cursor += 8;
		Assert.Equal((uint)candidate.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal((uint)previous.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(candidate, encoded[cursor..(cursor + candidate.Length)]);
		cursor += candidate.Length;
		foreach (byte[] payload in previous)
		{
			Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
			cursor += 4;
			Assert.Equal(payload, encoded[cursor..(cursor + payload.Length)]);
			cursor += payload.Length;
		}
	}

	private static void AssertDestination(
		byte[] encoded,
		ref int cursor,
		LiquidSuppliedConfidentialDestination destination)
	{
		string address = destination.GetAddress().GetCanonicalAddressText();
		Assert.Equal(destination.GetAssetId().ToConsensusBytes(), encoded[cursor..(cursor + 32)]);
		cursor += 32;
		Assert.Equal((ulong)destination.GetAmount()!.AtomicUnits, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(cursor, 8)));
		cursor += 8;
		Assert.Equal((uint)address.Length, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(cursor, 4)));
		cursor += 4;
		Assert.Equal(address, System.Text.Encoding.ASCII.GetString(encoded, cursor, address.Length));
		cursor += address.Length;
	}

	private static void AssertRowRejected(
		byte[]? candidate,
		IReadOnlyList<byte[]?>? previous,
		LiquidOrdinaryWalletPlanWireErrorCode expected)
	{
		LiquidOrdinaryWalletPlanFundingRow? row = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanFundingRow.TryCreate(
				candidate,
				previous,
				out row,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(row);
			Assert.Equal(expected, errorCode);
		}
		finally
		{
			row?.Dispose();
		}
	}

	private static void AssertBatchRejected(
		LiquidOrdinaryWalletExactSpendPlan? plan,
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?>? rows,
		LiquidOrdinaryWalletPlanWireErrorCode expected)
	{
		LiquidOrdinaryWalletPlanFundingBatch? batch = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
				plan,
				rows,
				out batch,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(batch);
			Assert.Equal(expected, errorCode);
		}
		finally
		{
			batch?.Dispose();
		}
	}

	private static void AssertEncodeRejected(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidOrdinaryWalletExactSpendPlan? plan,
		LiquidOrdinaryWalletPlanFundingBatch? batch,
		LiquidOrdinaryWalletPlanWireErrorCode expected)
	{
		LiquidOrdinaryWalletPlanEncodedFrame? frame = null;
		try
		{
			Assert.False(LiquidOrdinaryWalletPlanEncoder.TryEncode(
				sourceEpoch,
				plan,
				batch,
				out frame,
				out LiquidOrdinaryWalletPlanWireErrorCode errorCode));
			Assert.Null(frame);
			Assert.Equal(expected, errorCode);
		}
		finally
		{
			frame?.Dispose();
		}
	}

	private static LiquidOrdinaryWalletPlanFundingRow CreateRow(
		byte[] candidate,
		params byte[]?[] previous)
		=> CreateRow(candidate, (IReadOnlyList<byte[]?>)previous);

	private static LiquidOrdinaryWalletPlanFundingRow CreateRow(
		byte[] candidate,
		IReadOnlyList<byte[]?> previous)
	{
		bool succeeded = LiquidOrdinaryWalletPlanFundingRow.TryCreate(
			candidate,
			previous,
			out LiquidOrdinaryWalletPlanFundingRow? row,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		return row ?? throw new InvalidOperationException("Funding row creation returned no owner.");
	}

	private static LiquidOrdinaryWalletPlanFundingBatch CreateBatch(
		LiquidOrdinaryWalletExactSpendPlan plan,
		params LiquidOrdinaryWalletPlanFundingRow?[] rows)
		=> CreateBatch(plan, (IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?>)rows);

	private static LiquidOrdinaryWalletPlanFundingBatch CreateBatch(
		LiquidOrdinaryWalletExactSpendPlan plan,
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?> rows)
	{
		bool succeeded = LiquidOrdinaryWalletPlanFundingBatch.TryCreate(
			plan,
			rows,
			out LiquidOrdinaryWalletPlanFundingBatch? batch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		return batch ?? throw new InvalidOperationException("Funding batch creation returned no owner.");
	}

	private static LiquidOrdinaryWalletPlanEncodedFrame Encode(
		LiquidOrdinaryWalletExactSpendPlan plan,
		LiquidOrdinaryWalletPlanFundingBatch batch,
		ReadOnlySpan<byte> sourceEpoch)
	{
		bool succeeded = LiquidOrdinaryWalletPlanEncoder.TryEncode(
			sourceEpoch,
			plan,
			batch,
			out LiquidOrdinaryWalletPlanEncodedFrame? frame,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		return frame ?? throw new InvalidOperationException("Encoding returned no frame owner.");
	}

	private static byte[] Copy(LiquidOrdinaryWalletPlanEncodedFrame frame)
	{
		byte[] bytes = new byte[frame.Length];
		frame.CopyFrameTo(bytes);
		return bytes;
	}

	private static PlanFixture CreateTwoAssetPlan(ElementsPublicNetworkManifest manifest)
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidAssetId issuedAsset = LiquidAssetId.ParseRpcHex(IssuedAssetHex);
		LiquidTransactionId secondId = Tx(2);
		LiquidOwnedOutput second = Output(secondId, 0, issuedAsset, peggedAsset, 7);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(secondId, [], [second]));
		LiquidTransactionId firstId = Tx(1);
		LiquidOwnedOutput first = Output(firstId, 0, peggedAsset, peggedAsset, 4);
		state = state.Apply(
			state.Revision,
			LiquidWalletTransactionDelta.Create(firstId, [], [first]));
		LiquidSuppliedConfidentialDestination firstDestination = Destination(
			manifest,
			SecondScriptHex,
			issuedAsset,
			7);
		LiquidSuppliedConfidentialDestination secondDestination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			3);
		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[second.OutPoint, first.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([firstDestination, secondDestination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
		IReadOnlyList<LiquidWalletCoinControlEntry> selected = plan.GetSelectedEntries();
		return new PlanFixture(
			manifest,
			peggedAsset,
			plan,
			selected[0],
			selected[1],
			firstDestination,
			secondDestination);
	}

	private static LiquidOrdinaryWalletExactSpendPlan CreateSingleAssetPlan(
		ElementsPublicNetworkManifest manifest,
		uint transactionValue)
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidTransactionId transactionId = Tx(transactionValue);
		LiquidOwnedOutput output = Output(transactionId, 0, peggedAsset, peggedAsset, 10);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], [output]));
		LiquidSuppliedConfidentialDestination destination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			9);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[output.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
	}

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		LiquidAssetId peggedAssetId,
		long atomicUnits)
	{
		LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString(PublicKeyHex),
			LiquidKeyBranch.External,
			outputIndex);
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			spendKey.GetScriptPubKey(),
			LiquidAssetAmount.Create(assetId, peggedAssetId, atomicUnits),
			spendKey);
	}

	private static LiquidSuppliedConfidentialDestination Destination(
		ElementsPublicNetworkManifest manifest,
		string scriptHex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString(scriptHex),
			LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex)));
		return LiquidSuppliedConfidentialDestination.Create(
			manifest,
			address,
			assetId,
			LiquidAssetAmount.Create(assetId, peggedAsset, atomicUnits),
			LiquidWalletLabelSet.Create(["wire-test"]));
	}


	private static LiquidTransactionId Tx(uint value) =>
		LiquidTransactionId.ParseRpcHex(value.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));

	private static T GetField<T>(object owner, string fieldName) =>
		Assert.IsType<T>(owner.GetType()
			.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(owner));

	private static void SetField<T>(object owner, string fieldName, T value) =>
		owner.GetType()
			.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(owner, value);

	private static void AssertFixedInvariant(
		LiquidOrdinaryWalletExactSpendPlan plan,
		LiquidOrdinaryWalletPlanFundingBatch batch)
	{
		LiquidOrdinaryWalletPlanEncodedFrame? frame = null;
		try
		{
			LiquidOrdinaryWalletPlanEncoder.TryEncode(SourceEpoch, plan, batch, out frame, out _);
		}
		catch (InvalidOperationException exception)
		{
			Assert.Null(frame);
			Assert.Equal(LiquidOrdinaryWalletPlanEncoder.InvariantMessage, exception.Message);
			return;
		}
		finally
		{
			frame?.Dispose();
		}

		throw new Xunit.Sdk.XunitException("A mutated accepted type state was encoded.");
	}

	private static LiquidAssetId AssertSharedIssuedAsset(
		LiquidOrdinaryWalletExactSpendPlan plan)
	{
		LiquidWalletCoinControlEntry selected = Assert.Single(
			plan.GetSelectedEntries(),
			entry => StringComparer.Ordinal.Equals(entry.Amount.AssetId.CanonicalRpcHex, IssuedAssetHex));
		LiquidSuppliedConfidentialDestination destination = Assert.Single(
			plan.GetDestinations(),
			item => StringComparer.Ordinal.Equals(item.GetAssetId().CanonicalRpcHex, IssuedAssetHex));
		Assert.Same(selected.Amount.AssetId, destination.GetAssetId());
		Assert.Same(destination.GetAssetId(), destination.GetAmount()!.AssetId);
		return destination.GetAssetId();
	}

	private static void AssertFixedDisposed(Exception exception, string objectName)
	{
		ObjectDisposedException disposed = Assert.IsType<ObjectDisposedException>(exception);
		Assert.Equal(objectName, disposed.ObjectName);
		Assert.DoesNotContain("System.Byte", disposed.ToString(), StringComparison.Ordinal);
	}

	private static string FailureMessage(LiquidOrdinaryWalletPlanWireErrorCode errorCode) =>
		errorCode == LiquidOrdinaryWalletPlanWireErrorCode.None
			? "The operation returned false without an error code."
			: errorCode.GetMessage();

	private static void AssertExactErrorMessageMapping(
		Func<LiquidOrdinaryWalletPlanWireErrorCode, string> getMessage)
	{
		string[] expected =
		[
			"ordinary wallet plan wire argument is invalid",
			"ordinary wallet plan wire version is unsupported",
			"ordinary wallet plan wire encoding is invalid",
			"ordinary wallet plan wire limit exceeded",
			"ordinary wallet plan wire source binding does not match",
			"ordinary wallet plan wire context was rejected",
			"ordinary wallet plan wire plan was rejected",
			"ordinary wallet plan wire funding was rejected",
		];
		string[] actual = Enumerable.Range(1, 8)
			.Select(value => getMessage((LiquidOrdinaryWalletPlanWireErrorCode)value))
			.ToArray();
		Assert.Equal(expected, actual);
	}

	private static string GetProductionRoot([CallerFilePath] string testFilePath = "") =>
		Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(testFilePath)!,
			"../../../../../WalletWasabi"));

	private static string GetWireProductionRoot([CallerFilePath] string testFilePath = "") =>
		Path.Combine(GetProductionRoot(testFilePath), "Liquid/Wallet/Wire");

	private static Type[] GetExactProductionWireTypes() =>
		typeof(LiquidOrdinaryWalletPlanEncoder).Assembly.GetTypes()
			.Where(type => IsProductionWireNamespace(type.Namespace))
			.OrderBy(type => type.FullName, StringComparer.Ordinal)
			.ToArray();

	private static bool IsProductionWireNamespace(string? candidate)
	{
		string expected = typeof(LiquidOrdinaryWalletPlanEncoder).Namespace!;
		return StringComparer.Ordinal.Equals(candidate, expected) ||
			candidate?.StartsWith(expected + ".", StringComparison.Ordinal) is true;
	}

	private static void AssertExactWireTypeNames(
		IEnumerable<string> expected,
		IEnumerable<string> actual) =>
		Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

	private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

	private static void AssertExactPlanWireAccessorSource(string source)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(source).GetRoot());
		string[] methods = root.DescendantNodes()
			.OfType<MethodDeclarationSyntax>()
			.Where(method => method.Identifier.ValueText is
				"GetDestinationNetworkManifestId" or "GetPeggedAssetId" or "GetExplicitFee" or
				"GetSelectedEntriesForWireEncoding" or "GetDestinationsForWireEncoding")
			.Select(method => NormalizeSyntax(method.ToString()))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(
			new[]
			{
				"public LiquidAssetAmount GetExplicitFee() => _explicitFee;",
				"public LiquidAssetId GetPeggedAssetId() => _peggedAssetId;",
				"public string GetDestinationNetworkManifestId() => _destinationNetworkManifestId;",
				"internal ReadOnlySpan<LiquidSuppliedConfidentialDestination> GetDestinationsForWireEncoding() => _destinations;",
				"internal ReadOnlySpan<LiquidWalletCoinControlEntry> GetSelectedEntriesForWireEncoding() => _selectedEntries;",
			}.Order(StringComparer.Ordinal),
			methods);

		string[] properties = root.DescendantNodes()
			.OfType<PropertyDeclarationSyntax>()
			.Where(property => property.Identifier.ValueText is "SourceRevision" or "SelectedInputCount")
			.Select(property => NormalizeSyntax(property.ToString()))
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(
			new[]
			{
				"public int SelectedInputCount => _selectedEntries.Length;",
				"public ulong SourceRevision { get; }",
			}.Order(StringComparer.Ordinal),
			properties);
	}

	private static bool IsSafeWireSource(string source)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(source).GetRoot());
		return !root.DescendantTrivia(descendIntoTrivia: true)
			.Any(trivia => trivia.GetStructure() is DirectiveTriviaSyntax) &&
			!root.DescendantTokens().Any(token =>
				token.RawKind is (int)SyntaxKind.UnsafeKeyword or (int)SyntaxKind.ExternKeyword ||
				token.RawKind == (int)SyntaxKind.IdentifierToken &&
					(token.ValueText.Contains("Fault", StringComparison.OrdinalIgnoreCase) ||
						token.ValueText.Contains("Probe", StringComparison.OrdinalIgnoreCase) ||
						token.ValueText.Contains("TestHook", StringComparison.OrdinalIgnoreCase))) &&
			!root.DescendantNodes().Any(node => node is PointerTypeSyntax or
				FunctionPointerTypeSyntax or ImplicitStackAllocArrayCreationExpressionSyntax or
				FixedStatementSyntax);
	}

	private static void AssertNonPrivateMethodNames(Type type, params string[] expected)
	{
		string[] actual = GetDeclaredMethods(type)
			.Where(method => !method.IsPrivate)
			.Select(method => method.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
	}

	private static void AssertCapabilityGuarded(
		Type type,
		MethodInfo ensureCooperation,
		params string[] methodNames)
	{
		foreach (string methodName in methodNames)
		{
			MethodInfo method = Assert.Single(
				type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
					BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
				candidate => candidate.Name == methodName);
			Assert.Equal(typeof(object), Assert.Single(
				method.GetParameters(),
				parameter => parameter.Position == 0).ParameterType);
			Assert.Equal(ensureCooperation, GetIlReferences(method).First());
		}
	}

	private static void AssertOwnedCleanupRegion(
		string sourcePath,
		IReadOnlyList<string> stagingStatements,
		IReadOnlyList<string> cleanupStatements)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		foreach (string staging in stagingStatements)
		{
			string normalizedStaging = NormalizeSyntax(staging);
			StatementSyntax statement = Assert.Single(
				root.DescendantNodes().OfType<StatementSyntax>(),
				node => NormalizeSyntax(node.ToString()).Contains(normalizedStaging, StringComparison.Ordinal) &&
					!node.DescendantNodes().OfType<StatementSyntax>()
						.Any(child => NormalizeSyntax(child.ToString()).Contains(normalizedStaging, StringComparison.Ordinal)));
			TryStatementSyntax guarded = Assert.Single(
				statement.Ancestors().OfType<TryStatementSyntax>(),
				candidate => candidate.Finally is not null);
			string cleanup = NormalizeSyntax(guarded.Finally!.ToString());
			Assert.All(cleanupStatements, expected =>
				Assert.Contains(NormalizeSyntax(expected), cleanup, StringComparison.Ordinal));
		}
	}

	private static void AssertOwnershipTransferOrder(string sourcePath)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		MethodDeclarationSyntax transfer = Assert.Single(
			root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			method => method.Identifier.ValueText == "TakeOwnership");
		BlockSyntax transferBody = transfer.Body ??
			throw new Xunit.Sdk.XunitException("TakeOwnership must have a block body.");
		string body = NormalizeSyntax(transferBody.ToString());
		int construct = body.IndexOf(
			"var owner = new LiquidOrdinaryWalletPlanEncodedFrame(ownedFrame);",
			StringComparison.Ordinal);
		int releaseCaller = body.IndexOf("frame = null;", StringComparison.Ordinal);
		int returnOwner = body.IndexOf("return owner;", StringComparison.Ordinal);
		Assert.True(construct >= 0 && construct < releaseCaller && releaseCaller < returnOwner, body);
	}

	private static void AssertFundingBatchOwnershipTransfer(string sourcePath)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		MethodDeclarationSyntax tryCreate = Assert.Single(
			root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			IsTryCreateMethod);
		TryStatementSyntax guarded = Assert.Single(
			tryCreate.DescendantNodes().OfType<TryStatementSyntax>(),
			HasFinally);
		StatementSyntax[] successStatements = guarded.Block.Statements.ToArray();

		AssignmentExpressionSyntax[] assignments = guarded.Block.DescendantNodes()
			.OfType<AssignmentExpressionSyntax>()
			.ToArray();
		VariableDeclaratorSyntax guard = Assert.Single(
			tryCreate.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
			IsCleanupOwnerDeclaration);
		Assert.Equal((int)SyntaxKind.NullLiteralExpression, guard.Initializer?.Value.RawKind);
		ObjectCreationExpressionSyntax ownerConstruction = Assert.Single(
			tryCreate.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
			IsFundingBatchOwnerCreation);
		AssignmentExpressionSyntax construct = Assert.Single(assignments, IsFundingBatchGuardConstruction);
		Assert.Same(ownerConstruction, construct.Right);
		AssignmentExpressionSyntax releaseRows = Assert.Single(assignments, IsOwnedRowsNullAssignment);
		AssignmentExpressionSyntax publish = Assert.Single(assignments, IsBatchPublication);
		AssignmentExpressionSyntax releaseOwner = Assert.Single(assignments, IsCleanupOwnerNullAssignment);

		int constructIndex = Array.IndexOf(successStatements, construct.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseRowsIndex = Array.IndexOf(successStatements, releaseRows.FirstAncestorOrSelf<StatementSyntax>()!);
		int publishIndex = Array.IndexOf(successStatements, publish.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseOwnerIndex = Array.IndexOf(successStatements, releaseOwner.FirstAncestorOrSelf<StatementSyntax>()!);
		Assert.True(
			constructIndex >= 0 && constructIndex < releaseRowsIndex && releaseRowsIndex < publishIndex &&
			publishIndex + 1 == releaseOwnerIndex,
			NormalizeSyntax(guarded.Block.ToString()));
		Assert.IsType<ReturnStatementSyntax>(successStatements[releaseOwnerIndex + 1]);
		Assert.Equal(releaseOwnerIndex + 2, successStatements.Length);

		Assert.DoesNotContain(
			assignments,
			IsOwnedRowsCollectionAssignment);
		Assert.Equal(
			2,
			tryCreate.DescendantNodes().OfType<AssignmentExpressionSyntax>().Count(IsBatchAssignment));
		Assert.Equal(2, assignments.Count(IsCleanupOwnerAssignment));
		string cleanup = NormalizeSyntax(guarded.Finally!.ToString());
		Assert.Contains("cleanupOwner?.Dispose();", cleanup, StringComparison.Ordinal);
		Assert.Contains("ownedRows[index].Dispose();", cleanup, StringComparison.Ordinal);
	}

	private static void AssertFundingRowOwnershipTransfer(string sourcePath)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot());
		MethodDeclarationSyntax tryCreate = Assert.Single(
			root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			IsTryCreateMethod);
		TryStatementSyntax guarded = Assert.Single(
			tryCreate.DescendantNodes().OfType<TryStatementSyntax>(),
			HasFinally);
		StatementSyntax[] successStatements = guarded.Block.Statements.ToArray();

		AssignmentExpressionSyntax[] assignments = guarded.Block.DescendantNodes()
			.OfType<AssignmentExpressionSyntax>()
			.ToArray();
		VariableDeclaratorSyntax guard = Assert.Single(
			tryCreate.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
			IsCleanupOwnerDeclaration);
		Assert.Equal((int)SyntaxKind.NullLiteralExpression, guard.Initializer?.Value.RawKind);
		ObjectCreationExpressionSyntax ownerConstruction = Assert.Single(
			tryCreate.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
			IsFundingRowOwnerCreation);
		AssignmentExpressionSyntax construct = Assert.Single(assignments, IsFundingRowGuardConstruction);
		Assert.Same(ownerConstruction, construct.Right);
		AssignmentExpressionSyntax releaseCandidate = Assert.Single(assignments, IsOwnedCandidateNullAssignment);
		AssignmentExpressionSyntax releasePrevious = Assert.Single(assignments, IsOwnedPreviousNullAssignment);
		AssignmentExpressionSyntax publish = Assert.Single(assignments, IsRowPublication);
		AssignmentExpressionSyntax releaseOwner = Assert.Single(assignments, IsCleanupOwnerNullAssignment);

		int constructIndex = Array.IndexOf(successStatements, construct.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseCandidateIndex = Array.IndexOf(
			successStatements,
			releaseCandidate.FirstAncestorOrSelf<StatementSyntax>()!);
		int releasePreviousIndex = Array.IndexOf(
			successStatements,
			releasePrevious.FirstAncestorOrSelf<StatementSyntax>()!);
		int publishIndex = Array.IndexOf(successStatements, publish.FirstAncestorOrSelf<StatementSyntax>()!);
		int releaseOwnerIndex = Array.IndexOf(successStatements, releaseOwner.FirstAncestorOrSelf<StatementSyntax>()!);
		Assert.True(
			constructIndex >= 0 && constructIndex < releaseCandidateIndex &&
			releaseCandidateIndex < releasePreviousIndex && releasePreviousIndex < publishIndex &&
			publishIndex + 1 == releaseOwnerIndex,
			NormalizeSyntax(guarded.Block.ToString()));
		Assert.IsType<ReturnStatementSyntax>(successStatements[releaseOwnerIndex + 1]);
		Assert.Equal(releaseOwnerIndex + 2, successStatements.Length);
		Assert.Equal(
			2,
			tryCreate.DescendantNodes().OfType<AssignmentExpressionSyntax>().Count(IsRowAssignment));
		Assert.Equal(2, assignments.Count(IsCleanupOwnerAssignment));
		string cleanup = NormalizeSyntax(guarded.Finally!.ToString());
		Assert.Contains("cleanupOwner?.Dispose();", cleanup, StringComparison.Ordinal);
		Assert.Contains("Clear(ownedCandidate, ownedPrevious);", cleanup, StringComparison.Ordinal);
	}

	private static bool IsTryCreateMethod(MethodDeclarationSyntax method) =>
		method.Identifier.ValueText == "TryCreate";

	private static bool HasFinally(TryStatementSyntax statement) => statement.Finally is not null;

	private static bool IsCleanupOwnerDeclaration(VariableDeclaratorSyntax declaration) =>
		declaration.Identifier.ValueText == "cleanupOwner";

	private static bool IsFundingBatchOwnerCreation(ObjectCreationExpressionSyntax creation) =>
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingBatch";

	private static bool IsFundingRowOwnerCreation(ObjectCreationExpressionSyntax creation) =>
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingRow";

	private static bool IsFundingBatchGuardConstruction(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "cleanupOwner" &&
		assignment.Right is ObjectCreationExpressionSyntax creation &&
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingBatch";

	private static bool IsFundingRowGuardConstruction(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "cleanupOwner" &&
		assignment.Right is ObjectCreationExpressionSyntax creation &&
		creation.Type.ToString() == "LiquidOrdinaryWalletPlanFundingRow";

	private static bool IsOwnedRowsNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "ownedRows");

	private static bool IsOwnedCandidateNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "ownedCandidate");

	private static bool IsOwnedPreviousNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "ownedPrevious");

	private static bool IsCleanupOwnerNullAssignment(AssignmentExpressionSyntax assignment) =>
		IsNullAssignment(assignment, "cleanupOwner");

	private static bool IsNullAssignment(AssignmentExpressionSyntax assignment, string left) =>
		assignment.Left.ToString() == left &&
		assignment.Right.RawKind == (int)SyntaxKind.NullLiteralExpression;

	private static bool IsBatchPublication(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "batch" && assignment.Right.ToString() == "cleanupOwner";

	private static bool IsRowPublication(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "row" && assignment.Right.ToString() == "cleanupOwner";

	private static bool IsOwnedRowsCollectionAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "ownedRows" && assignment.Right is CollectionExpressionSyntax;

	private static bool IsBatchAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "batch";

	private static bool IsRowAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "row";

	private static bool IsCleanupOwnerAssignment(AssignmentExpressionSyntax assignment) =>
		assignment.Left.ToString() == "cleanupOwner";

	private static string NormalizeSyntax(string value) =>
		string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	private static IEnumerable<string> GetTypeSurfaceManifest(Type type)
	{
		yield return $"TYPE|{TypeIdentity(type)}|{(int)type.Attributes}|{TypeIdentity(type.BaseType)}|" +
			string.Join(",", type.GetInterfaces().Select(TypeIdentity).Order(StringComparer.Ordinal)) + "|" +
			AttributeIdentity(type.GetCustomAttributesData());
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		foreach (FieldInfo field in type.GetFields(Declared).OrderBy(field => field.Name, StringComparer.Ordinal))
		{
			yield return $"FIELD|{TypeIdentity(type)}|{field.Name}|{TypeIdentity(field.FieldType)}|" +
				$"{(int)field.Attributes}|{AttributeIdentity(field.GetCustomAttributesData())}";
		}
		foreach (PropertyInfo property in type.GetProperties(Declared).OrderBy(property => property.Name, StringComparer.Ordinal))
		{
			yield return $"PROPERTY|{TypeIdentity(type)}|{property.Name}|{TypeIdentity(property.PropertyType)}|" +
				$"{(int)property.Attributes}|{property.GetMethod?.Name}|{property.SetMethod?.Name}|" +
				AttributeIdentity(property.GetCustomAttributesData());
		}
		foreach (MethodBase method in GetDeclaredMethods(type).OrderBy(MethodIdentity, StringComparer.Ordinal))
		{
			MethodBody? body = method.GetMethodBody();
			yield return $"METHOD|{MethodIdentity(method)}|{(int)method.Attributes}|" +
				$"{(int)method.GetMethodImplementationFlags()}|{(int)method.CallingConvention}|" +
				AttributeIdentity(method.GetCustomAttributesData());
			if (method is MethodInfo methodInfo)
			{
				yield return $"RETURN|{MethodIdentity(method)}|{TypeIdentity(methodInfo.ReturnType)}|" +
					AttributeIdentity(methodInfo.ReturnParameter.GetCustomAttributesData());
			}
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				yield return $"PARAM|{MethodIdentity(method)}|{parameter.Position}|{parameter.Name}|" +
					$"{TypeIdentity(parameter.ParameterType)}|{(int)parameter.Attributes}|" +
					AttributeIdentity(parameter.GetCustomAttributesData());
			}
			if (body is null)
			{
				yield return $"BODY|{MethodIdentity(method)}|null";
				continue;
			}

			yield return $"BODY|{MethodIdentity(method)}|{body.InitLocals}|{body.MaxStackSize}|" +
				Convert.ToHexString(body.GetILAsByteArray() ?? []).ToLowerInvariant();
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				yield return $"LOCAL|{MethodIdentity(method)}|{local.LocalIndex}|" +
					$"{TypeIdentity(local.LocalType)}|{local.IsPinned}";
			}
			foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
			{
				yield return $"EH|{MethodIdentity(method)}|{(int)clause.Flags}|{clause.TryOffset}|" +
					$"{clause.TryLength}|{clause.HandlerOffset}|{clause.HandlerLength}|" +
					TypeIdentity(clause.Flags == ExceptionHandlingClauseOptions.Clause ? clause.CatchType : null);
			}
			foreach (MemberInfo reference in GetIlReferences(method))
			{
				yield return $"REF|{MethodIdentity(method)}|{MemberIdentity(reference)}";
			}
		}
	}

	private static IEnumerable<MethodBase> GetDeclaredMethods(Type type)
	{
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		return type.GetConstructors(Declared).Cast<MethodBase>().Concat(type.GetMethods(Declared));
	}

	private static string TypeIdentity(Type? type) =>
		WalletWasabi.Tests.UnitTests.Liquid.Wallet.LiquidWalletStateTests.NormalizeProductAssemblyVersion(
			type?.AssemblyQualifiedName ?? "null");

	private static string MethodIdentity(MethodBase method) =>
		$"{TypeIdentity(method.DeclaringType)}::{method.Name}" +
		$"`{(method.IsGenericMethod ? method.GetGenericArguments().Length : 0)}" +
		$"<{(method.IsGenericMethod ? string.Join(",", method.GetGenericArguments().Select(TypeIdentity)) : "")}>" +
		"(" +
		$"{string.Join(",", method.GetParameters().Select(parameter => TypeIdentity(parameter.ParameterType)))})" +
		$"->{(method is MethodInfo methodInfo ? TypeIdentity(methodInfo.ReturnType) : "void")}";

	private static string MemberIdentity(MemberInfo member) => member switch
	{
		MethodBase method => MethodIdentity(method),
		FieldInfo field => $"{TypeIdentity(field.DeclaringType)}::{field.Name}:{TypeIdentity(field.FieldType)}",
		Type memberType => TypeIdentity(memberType),
		_ => $"{TypeIdentity(member.DeclaringType)}::{member.Name}",
	};

	private static string AttributeIdentity(IEnumerable<CustomAttributeData> attributes) =>
		string.Join(",", attributes.Select(attribute => TypeIdentity(attribute.AttributeType))
			.Order(StringComparer.Ordinal));

	private static bool IsForbiddenWireMember(MemberInfo member)
	{
		if (member is MethodBase { DeclaringType: { } declaringType } monitorMethod &&
			declaringType == typeof(Monitor) &&
			monitorMethod.Name is nameof(Monitor.Enter) or nameof(Monitor.Exit))
		{
			return false;
		}

		if (IsForbiddenWireIdentity(MemberIdentity(member)) || IsForbiddenWireType(member.DeclaringType))
		{
			return true;
		}
		if (member is MethodInfo method && IsForbiddenWireType(method.ReturnType))
		{
			return true;
		}
		if (member is MethodBase methodBase && methodBase.GetParameters().Any(parameter =>
			IsForbiddenWireType(parameter.ParameterType)))
		{
			return true;
		}
		if (member is FieldInfo field && IsForbiddenWireType(field.FieldType))
		{
			return true;
		}
		if (member is PropertyInfo property && IsForbiddenWireType(property.PropertyType))
		{
			return true;
		}

		return false;
	}

	private static bool IsForbiddenWireType(Type? type)
	{
		if (type is null)
		{
			return false;
		}
		if (IsForbiddenWireIdentity(type.FullName ?? type.Name) ||
			IsForbiddenWireIdentity(type.Assembly.FullName ?? ""))
		{
			return true;
		}
		if (type.HasElementType)
		{
			return IsForbiddenWireType(type.GetElementType());
		}

		return type.IsGenericType && type.GetGenericArguments().Any(IsForbiddenWireType);
	}

	private static bool IsForbiddenWireIdentity(string identity) =>
		identity.Contains("WalletWasabi.Logging", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Serilog", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("NLog", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Logger", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Runtime.InteropServices", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains(".Interop.", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("PInvoke", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("DllImport", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("WalletWasabi.Liquid.Rpc", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.IO", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Net", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Threading", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("HttpClient", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("RandomNumberGenerator", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Random", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Randomness", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("TimeProvider", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Environment", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("ElementsNode", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("RawFrame", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("GetFrame", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Pset", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Psbt", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Signer", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Broadcast", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("CoinJoin", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Sponsor", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains(".Fault", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Probe", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("TestHook", StringComparison.OrdinalIgnoreCase);

	private static IEnumerable<MemberInfo> GetIlReferences(MethodBase method)
		=> GetIlInstructions(method)
			.Where(instruction => instruction.Member is not null)
			.Select(instruction => instruction.Member!);

	private static IEnumerable<(OpCode OpCode, MemberInfo? Member)> GetIlInstructions(MethodBase method)
		=> GetIlInstructionsWithOffsets(method)
			.Select(instruction => (instruction.OpCode, instruction.Member));

	private static IEnumerable<(int Offset, OpCode OpCode, MemberInfo? Member)> GetIlInstructionsWithOffsets(
		MethodBase method)
	{
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		Type[]? typeArguments = method.DeclaringType?.IsGenericType == true
			? method.DeclaringType.GetGenericArguments()
			: null;
		Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
		for (int offset = 0; offset < il.Length;)
		{
			int instructionOffset = offset;
			OpCode opCode = ReadOpCode(il, ref offset);
			MemberInfo? resolvedMember = null;
			if (opCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
				OperandType.InlineTok or OperandType.InlineType)
			{
				int token = BitConverter.ToInt32(il, offset);
				resolvedMember = method.Module.ResolveMember(token, typeArguments, methodArguments);
			}

			yield return (instructionOffset, opCode, resolvedMember);
			offset += OperandSize(opCode.OperandType, il, offset);
		}
	}

	private static OpCode ReadOpCode(byte[] il, ref int offset)
	{
		short value = il[offset++];
		if (value == 0xfe)
		{
			value = (short)(0xfe00 | il[offset++]);
		}

		return typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
			.Select(field => Assert.IsType<OpCode>(field.GetValue(null)))
			.First(opCode => opCode.Value == value);
	}

	private static int OperandSize(OperandType operandType, byte[] il, int operandOffset) =>
		operandType switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
				OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
				OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
				OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, operandOffset),
			_ => throw new Xunit.Sdk.XunitException($"Unsupported operand type {operandType}."),
		};













































	private static void AssertExactArtifactBytes(byte[] inspectedAssembly, byte[] rebuiltAssembly)
	{
		Assert.NotEmpty(inspectedAssembly);
		Assert.Equal(inspectedAssembly, rebuiltAssembly);
	}

























































	private static bool IsImplementationContributor(string source)
	{
		CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(source).GetRoot());
		foreach (BaseTypeDeclarationSyntax declaration in root.DescendantNodes()
			.OfType<BaseTypeDeclarationSyntax>())
		{
			string declaredNamespace = GetDeclaredNamespace(declaration);
			if (IsProductionWireNamespace(declaredNamespace))
			{
				return true;
			}
			if (StringComparer.Ordinal.Equals(declaredNamespace, "WalletWasabi.Liquid.Wallet") &&
				declaration.Identifier.ValueText == nameof(LiquidOrdinaryWalletExactSpendPlan) &&
				!declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any())
			{
				return true;
			}
		}

		return false;
	}

	private static string GetDeclaredNamespace(BaseTypeDeclarationSyntax declaration) =>
		string.Join(
			'.',
			declaration.Ancestors()
				.OfType<BaseNamespaceDeclarationSyntax>()
				.Reverse()
				.Select(namespaceDeclaration => namespaceDeclaration.Name.ToString()));

	private static MethodInfo[] GetExactPlanWireEntryPoints(IEnumerable<Type> exactWireTypes) =>
		exactWireTypes
			.SelectMany(GetDeclaredMethods)
			.SelectMany(GetIlReferences)
			.OfType<MethodInfo>()
			.Where(method => method.DeclaringType == typeof(LiquidOrdinaryWalletExactSpendPlan))
			.Distinct()
			.OrderBy(method => method.Name, StringComparer.Ordinal)
			.ThenBy(MethodIdentity, StringComparer.Ordinal)
			.ToArray();











	private static void AssertExactSha256(string expectedSha256, string manifest)
	{
		string actualSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedSha256, actualSha256), actualSha256);
	}


	private sealed record PlanFixture(
		ElementsPublicNetworkManifest Manifest,
		LiquidAssetId PeggedAsset,
		LiquidOrdinaryWalletExactSpendPlan Plan,
		LiquidWalletCoinControlEntry FirstSelected,
		LiquidWalletCoinControlEntry SecondSelected,
		LiquidSuppliedConfidentialDestination FirstDestination,
		LiquidSuppliedConfidentialDestination SecondDestination);

	private sealed class ThrowingPayloadList : IReadOnlyList<byte[]?>
	{
		public int Count
		{
			get
			{
				CountReads++;
				throw new InvalidOperationException("The payload collection must not be inspected.");
			}
		}

		public int CountReads { get; private set; }

		public byte[]? this[int index] =>
			throw new InvalidOperationException("The payload collection must not be inspected.");

		public IEnumerator<byte[]?> GetEnumerator() =>
			throw new InvalidOperationException("The payload collection must not be enumerated.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class NegativeCountList<T> : IReadOnlyList<T>
	{
		public int Count => -1;

		public T this[int index] => throw new InvalidOperationException("A negative-count list has no elements.");

		public IEnumerator<T> GetEnumerator() =>
			throw new InvalidOperationException("A negative-count list has no elements.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class RepeatedValueList<T>(int count, T value, int nullAt = -1) : IReadOnlyList<T>
	{
		public int Count => count;

		public int ReadCount { get; private set; }

		public T this[int index]
		{
			get
			{
				if ((uint)index >= (uint)count)
				{
					throw new ArgumentOutOfRangeException(nameof(index));
				}

				ReadCount++;
				return index == nullAt ? default! : value;
			}
		}

		public IEnumerator<T> GetEnumerator() =>
			throw new InvalidOperationException("The repeated-value list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class StatefulPayloadList(
		IReadOnlyList<byte[]?> firstReads,
		IReadOnlyList<byte[]?> snapshotReads) : IReadOnlyList<byte[]?>
	{
		private readonly int[] _readCounts = new int[firstReads.Count];

		public int Count => firstReads.Count;

		public IReadOnlyList<int> ReadCounts => _readCounts;

		public byte[]? this[int index]
		{
			get
			{
				int read = Interlocked.Increment(ref _readCounts[index]);
				return read switch
				{
					1 => firstReads[index],
					2 or 3 => snapshotReads[index],
					_ => throw new InvalidOperationException("The payload list was read after snapshotting."),
				};
			}
		}

		public IEnumerator<byte[]?> GetEnumerator() =>
			throw new InvalidOperationException("The payload list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class StatefulRowList(
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?> firstReads,
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?> snapshotReads) :
		IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?>
	{
		private readonly int[] _readCounts = new int[firstReads.Count];

		public int Count => firstReads.Count;

		public IReadOnlyList<int> ReadCounts => _readCounts;

		public LiquidOrdinaryWalletPlanFundingRow? this[int index]
		{
			get
			{
				int read = Interlocked.Increment(ref _readCounts[index]);
				return read switch
				{
					1 => firstReads[index],
					2 or 3 => snapshotReads[index],
					_ => throw new InvalidOperationException("The row list was read after snapshotting."),
				};
			}
		}

		public IEnumerator<LiquidOrdinaryWalletPlanFundingRow?> GetEnumerator() =>
			throw new InvalidOperationException("The row list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CoordinatedSingleItemList<T>(
		Func<T> readCurrent,
		ManualResetEventSlim firstRead,
		ManualResetEventSlim mutationComplete) : IReadOnlyList<T>
	{
		private int _readCount;

		public int Count => 1;

		public T this[int index]
		{
			get
			{
				Assert.Equal(0, index);
				if (Interlocked.Increment(ref _readCount) == 1)
				{
					T first = readCurrent();
					firstRead.Set();
					return first;
				}

				Assert.True(mutationComplete.Wait(TimeSpan.FromSeconds(10)));
				return readCurrent();
			}
		}

		public IEnumerator<T> GetEnumerator() =>
			throw new InvalidOperationException("The coordinated list must be accessed by index.");

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}













































































































	[Fact]
	public void GenerationFencedRawTransactionsCreateCanonicalClosedFundingRows()
	{
		PlanFixture fixture = CreateTwoAssetPlan(ElementsPublicNetworkManifest.LiquidTestnet);
		string firstCandidateId = fixture.FirstSelected.OutPoint.TransactionId.CanonicalRpcHex;
		string secondCandidateId = fixture.SecondSelected.OutPoint.TransactionId.CanonicalRpcHex;
		string firstPreviousId = Tx(3).CanonicalRpcHex;
		string secondPreviousId = Tx(4).CanonicalRpcHex;
		string thirdPreviousId = Tx(5).CanonicalRpcHex;
		byte[] firstCandidate = [0xa1];
		byte[] secondCandidate = [0xa2];
		byte[] firstPrevious = [0x30];
		byte[] secondPrevious = [0x10];
		byte[] thirdPrevious = [0x20];
		ElementsExpectationBoundRawTransactionBatch rawTransactions = CreateRawTransactionBatch(
			(secondCandidateId, secondCandidate),
			(secondPreviousId, secondPrevious),
			(firstCandidateId, firstCandidate),
			(thirdPreviousId, thirdPrevious),
			(firstPreviousId, firstPrevious));
		IReadOnlyList<string>?[] previousIdsBySelectedInput =
		[
			new[] { firstPreviousId, secondPreviousId },
			new[] { secondPreviousId, thirdPreviousId },
		];

		bool succeeded = rawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
			fixture.Plan,
			previousIdsBySelectedInput,
			out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		Assert.True(succeeded, FailureMessage(errorCode));
		Assert.NotNull(fundingBatch);
		Assert.False(rawTransactions.HasTransactionIdValidation);
		Assert.False(rawTransactions.HasBlockMembershipAuthority);
		Assert.False(rawTransactions.HasCurrentnessAuthority);
		using (fundingBatch)
		using (LiquidOrdinaryWalletPlanEncodedFrame frame = Encode(
			fixture.Plan,
			fundingBatch,
			SourceEpoch))
		{
			byte[] encoded = Copy(frame);
			try
			{
				int cursor = 152;
				AssertSelectedRow(
					encoded,
					ref cursor,
					fixture.FirstSelected,
					firstCandidate,
					[secondPrevious, firstPrevious]);
				AssertSelectedRow(
					encoded,
					ref cursor,
					fixture.SecondSelected,
					secondCandidate,
					[secondPrevious, thirdPrevious]);
				AssertDestination(encoded, ref cursor, fixture.FirstDestination);
				AssertDestination(encoded, ref cursor, fixture.SecondDestination);
				Assert.Equal(encoded.Length, cursor);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(encoded);
			}
		}

		LiquidOrdinaryWalletExactSpendPlan confirmedPlan = CreateConfirmedSingleTransactionPlan();
		LiquidWalletCoinControlEntry confirmedEntry = confirmedPlan.GetSelectedEntries()[0];
		ElementsExpectationBoundRawTransactionBatch confirmedRawTransactions =
			CreateRawTransactionBatchFromRequests(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				(new ElementsRawTransactionRequest(
					confirmedEntry.OutPoint.TransactionId.CanonicalRpcHex,
					confirmedEntry.Confirmation!.CanonicalBlockHash),
				[0xee]));
		bool confirmedSucceeded = confirmedRawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
			confirmedPlan,
			[Array.Empty<string>()],
			out LiquidOrdinaryWalletPlanFundingBatch? confirmedFundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode confirmedErrorCode);
		try
		{
			Assert.True(confirmedSucceeded, FailureMessage(confirmedErrorCode));
			Assert.NotNull(confirmedFundingBatch);
		}
		finally
		{
			confirmedFundingBatch?.Dispose();
		}
	}

	[Fact]
	public void GenerationFencedFundingCompositionRejectsEveryOpenOrAmbiguousMapping()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateSingleAssetPlan(
			ElementsPublicNetworkManifest.LiquidTestnet,
			71);
		string candidateId = Tx(71).CanonicalRpcHex;
		string firstPreviousId = Tx(0xab).CanonicalRpcHex;
		string secondPreviousId = Tx(0xac).CanonicalRpcHex;
		string missingPreviousId = Tx(0xad).CanonicalRpcHex;
		ElementsExpectationBoundRawTransactionBatch exact = CreateRawTransactionBatch(
			(candidateId, [0xaa]),
			(firstPreviousId, [0xbb]));

		AssertFundingCompositionRejected(
			exact,
			null,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			null,
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			new IReadOnlyList<string>?[] { null },
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((firstPreviousId, [0xbb])),
			plan,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xcc])),
			plan,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[new[] { firstPreviousId.ToUpperInvariant() }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[new[] { new string('0', 64) }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xcc])),
			plan,
			[new[] { secondPreviousId, firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			exact,
			plan,
			[new[] { firstPreviousId, firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((candidateId, [0xaa])),
			plan,
			[new[] { candidateId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((candidateId, [0xaa])),
			plan,
			[new[] { missingPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xbb])),
			plan,
			[new[] { firstPreviousId, secondPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding);

		LiquidOrdinaryWalletExactSpendPlan sharedCandidatePlan = CreateSameTransactionPlan();
		string sharedCandidateId = sharedCandidatePlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch(
				(sharedCandidateId, [0xdd]),
				(firstPreviousId, [0xbb]),
				(secondPreviousId, [0xcc])),
			sharedCandidatePlan,
			[new[] { firstPreviousId }, new[] { secondPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		LiquidOrdinaryWalletExactSpendPlan confirmedPlan = CreateConfirmedSingleTransactionPlan();
		string confirmedCandidateId = confirmedPlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatch((confirmedCandidateId, [0xee])),
			confirmedPlan,
			[Array.Empty<string>()],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		LiquidOrdinaryWalletExactSpendPlan futureConfirmationPlan =
			CreateConfirmedSingleTransactionPlan(new string('d', 64), 2);
		string futureCandidateId = futureConfirmationPlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatchFromRequests(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				(new ElementsRawTransactionRequest(futureCandidateId, new string('d', 64)), [0xef])),
			futureConfirmationPlan,
			[Array.Empty<string>()],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		LiquidOrdinaryWalletExactSpendPlan wrongTipPlan =
			CreateConfirmedSingleTransactionPlan(new string('d', 64), 1);
		string wrongTipCandidateId = wrongTipPlan.GetSelectedEntries()[0]
			.OutPoint.TransactionId.CanonicalRpcHex;
		AssertFundingCompositionRejected(
			CreateRawTransactionBatchFromRequests(
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				(new ElementsRawTransactionRequest(wrongTipCandidateId, new string('d', 64)), [0xf0])),
			wrongTipPlan,
			[Array.Empty<string>()],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);
		AssertFundingCompositionRejected(
			CreateRawTransactionBatchWithEffectiveFeeAsset(
				IssuedAssetHex,
				(candidateId, [0xaa]),
				(firstPreviousId, [0xbb])),
			plan,
			[new[] { firstPreviousId }],
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument);

		byte[] maximumPayload = new byte[LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength];
		try
		{
			var expandedTransactions =
				new (string TransactionId, byte[] Bytes)[10];
			expandedTransactions[0] = (sharedCandidateId, [0xdd]);
			var expandedPreviousIds = new string[9];
			for (int index = 0; index < expandedPreviousIds.Length; index++)
			{
				string previousId = Tx(checked((uint)(200 + index))).CanonicalRpcHex;
				expandedPreviousIds[index] = previousId;
				expandedTransactions[index + 1] = (previousId, maximumPayload);
			}
			AssertFundingCompositionRejected(
				CreateRawTransactionBatch(expandedTransactions),
				sharedCandidatePlan,
				[expandedPreviousIds, expandedPreviousIds.ToArray()],
				LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(maximumPayload);
		}
	}

	private static LiquidOrdinaryWalletExactSpendPlan CreateSameTransactionPlan()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidTransactionId transactionId = Tx(91);
		LiquidOwnedOutput first = Output(transactionId, 0, peggedAsset, peggedAsset, 4);
		LiquidOwnedOutput second = Output(transactionId, 1, peggedAsset, peggedAsset, 6);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], [first, second]));
		LiquidSuppliedConfidentialDestination destination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			9);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[first.OutPoint, second.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
	}

	private static LiquidOrdinaryWalletExactSpendPlan CreateConfirmedSingleTransactionPlan(
		string? blockHash = null,
		uint height = 1)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidTransactionId transactionId = Tx(92);
		LiquidOwnedOutput output = Output(transactionId, 0, peggedAsset, peggedAsset, 10);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], [output]));
		state = state.Confirm(
			state.Revision,
			transactionId,
			LiquidConfirmation.Create(blockHash ?? new string('b', 64), height));
		LiquidSuppliedConfidentialDestination destination = Destination(
			manifest,
			FirstScriptHex,
			peggedAsset,
			9);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[output.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
	}

	private static ElementsExpectationBoundRawTransactionBatch CreateRawTransactionBatch(
		params (string TransactionId, byte[] Bytes)[] transactions) =>
		CreateRawTransactionBatchWithEffectiveFeeAsset(
			ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
			transactions);

	private static ElementsExpectationBoundRawTransactionBatch CreateRawTransactionBatchWithEffectiveFeeAsset(
		string effectiveFeeAsset,
		params (string TransactionId, byte[] Bytes)[] transactions)
	{
		var requests = new (ElementsRawTransactionRequest Request, byte[] Bytes)[transactions.Length];
		for (int index = 0; index < transactions.Length; index++)
		{
			(string transactionId, byte[] bytes) = transactions[index];
			requests[index] = (new ElementsRawTransactionRequest(transactionId, null), bytes);
		}
		return CreateRawTransactionBatchFromRequests(effectiveFeeAsset, requests);
	}

	private static ElementsExpectationBoundRawTransactionBatch CreateRawTransactionBatchFromRequests(
		string effectiveFeeAsset,
		params (ElementsRawTransactionRequest Request, byte[] Bytes)[] transactions)
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		string genesisBlockHash = new('a', 64);
		string bestBlockHash = new('b', 64);
		string startupId = new('c', 64);
		var expectation = new ElementsNodeExpectation(
			manifest.ChainRpcName,
			genesisBlockHash,
			"51",
			manifest.PeggedAssetId,
			new string('0', 64),
			2,
			false,
			1,
			1,
			"/wire-test:1/");
		var status = new ElementsNodeStatus(
			expectation.Chain,
			1,
			1,
			bestBlockHash,
			expectation.GenesisBlockHash,
			false,
			false,
			false,
			false,
			true,
			true,
			false,
			expectation.FedpegScript,
			expectation.PeggedAsset,
			expectation.ParentGenesisBlockHash,
			expectation.PeginConfirmationDepth,
			expectation.EnforcePak,
			expectation.Version,
			expectation.ProtocolVersion,
			expectation.Subversion);
		var generation = new ElementsNodeGenerationObservation(
			startupId,
			1,
			status.Blocks,
			status.BestBlockHash);
		var nodeObservation = new ElementsExpectationBoundNodeObservation(
			expectation,
			effectiveFeeAsset,
			status,
			generation);
		var observations = new ElementsRawTransactionObservation[transactions.Length];
		for (int index = 0; index < transactions.Length; index++)
		{
			(ElementsRawTransactionRequest request, byte[] bytes) = transactions[index];
			observations[index] = new ElementsRawTransactionObservation(
				request,
				bytes);
		}

		return new ElementsExpectationBoundRawTransactionBatch(nodeObservation, observations);
	}

	private static void AssertFundingCompositionRejected(
		ElementsExpectationBoundRawTransactionBatch rawTransactions,
		LiquidOrdinaryWalletExactSpendPlan? plan,
		IReadOnlyList<IReadOnlyList<string>?>? previousTransactionIdsBySelectedInput,
		LiquidOrdinaryWalletPlanWireErrorCode expectedErrorCode)
	{
		bool succeeded = rawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
			plan,
			previousTransactionIdsBySelectedInput,
			out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode);
		try
		{
			Assert.False(succeeded);
			Assert.Null(fundingBatch);
			Assert.Equal(expectedErrorCode, errorCode);
		}
		finally
		{
			fundingBatch?.Dispose();
		}
	}
}
