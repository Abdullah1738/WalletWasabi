using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Wire;
using Xunit;
using LiquidOrdinaryWalletPlanEncodedFrame = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanEncodedFrame;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;
using LiquidOrdinaryWalletPlanFundingRow = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingRow;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

public class LiquidOrdinaryWalletPlanWireTests
{
	private const string ExpectedDebugWireSurfaceSha256 = "fc58193325d4e920020d9b24e8f3caf9ca8a6da2275b7680a56d463e4e7e9de6";
	private const string ExpectedReleaseWireSurfaceSha256 = "09609a26c51dc66d9b870f842cd98eac42843d28a31dca53dc9ed93e8e470fec";
	private const string ExpectedDebugWireClosureSha256 = "014cf01f4bda42f8c36a7dd41cae78e5d464ad716f598256d0ff5642493a2c54";
	private const string ExpectedReleaseWireClosureSha256 = "b097fda46b50cf066de1844b689936682b6b3eb154a2b1526b0c6d7a66947c73";
	private const string ExpectedDebugRuntimeDispatchAuthoritySha256 = "486ddbf38f33d2eb2b6f12c09d6acc3244c486c7f3278e930a08a90b56392e38";
	private const string ExpectedReleaseRuntimeDispatchAuthoritySha256 = "c888e75576f7d298160c957d797c593346e79670d2ae5ff8e517a4a0d968b0f2";
	private const string ExpectedDebugAmbientRuntimeDispatchAuthoritySha256 = "b30f09d21d2b3a2f38e3fdc52925906f64d5c325fdd66de80113858ce18edb7e";
	private const string ExpectedReleaseAmbientRuntimeDispatchAuthoritySha256 = "9114556617725c4f2d52936d0b2e58d245408577439cb6a8a1d566959e90d9da";
	private const string ExpectedDebugModuleInitializerBodySha256 = "23d1ae5ddc95da66864101267cfbd2d82a7942762a4cee19ebb85013b7dcd3c3";
	private const string ExpectedReleaseModuleInitializerBodySha256 = "23d1ae5ddc95da66864101267cfbd2d82a7942762a4cee19ebb85013b7dcd3c3";
	private const string ExpectedDebugAmbientClosureSha256 = "7cbab0e3ce7f01621fea42595285e374a7139f7e0e307e1ff95a95f5f7dc6ba6";
	private const string ExpectedReleaseAmbientClosureSha256 = "190c3955f0e3a75fcf1497e92604b514ce93769c31d601e66c54862da397deee";
	private const string ExpectedDebugGeneratedSourcesSha256 = "5f9abe4582b34b708d20504a398880e6f8e1922d52f8f8ab3c98d933b9e3c6e8";
	private const string ExpectedReleaseGeneratedSourcesSha256 = "5f9abe4582b34b708d20504a398880e6f8e1922d52f8f8ab3c98d933b9e3c6e8";
	private const string ExpectedDebugImportClosureSha256 = "932584d307786452fe44a5582afb6c5eba174aa4500fd0ba7b3bc2e0ad6c3601";
	private const string ExpectedReleaseImportClosureSha256 = "932584d307786452fe44a5582afb6c5eba174aa4500fd0ba7b3bc2e0ad6c3601";
	private const string ExpectedDebugReferenceAuthoritySha256 = "ff62b53abb82dfe960380727580f4b800389ce3eed5a64ee8c6a80196c34b2eb";
	private const string ExpectedReleaseReferenceAuthoritySha256 = "ff62b53abb82dfe960380727580f4b800389ce3eed5a64ee8c6a80196c34b2eb";
	private const string ExpectedDebugCompilerInputAuthoritySha256 = "62ac04eeaf0a813c3ba77f7544250d222cb9ac387b899e82269d74356e600dfa";
	private const string ExpectedReleaseCompilerInputAuthoritySha256 = "5e84372882fab8c2203eafab592644f935c59ae4aec7dce00dd454d6d9d31bee";
	private const string ExpectedToolchainDependencyAuthoritySha256 = "76752a17554778c4a90942141dbc1f4ed23b5382d7dd54843767bd1df67ae08e";
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
	public void ProductionSourceInventorySurfaceAndAuthorityAreFailClosed()
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
		var buildAuthority = GetEvaluatedProductionBuildAuthority(productionRoot);
		AssertExactBuildAuthority(
			buildAuthority.Properties,
			buildAuthority.DotnetRoot,
			productionRoot,
			buildAuthority.GeneratedRoot);
		(string FullPath, string RelativePath, string Source)[] evaluatedCompileInputs =
			buildAuthority.CompileInputs;
		AssertExactImplementationCompileInputs(expectedImplementationPaths, productionRoot, evaluatedCompileInputs);
		AssertExactAmbientCompileAuthority(evaluatedCompileInputs);
		AssertExactAnalyzerAuthority(
			buildAuthority.Analyzers,
			buildAuthority.DotnetRoot,
			buildAuthority.PackageAuthority);
		AssertExactGeneratedSourceAuthority(buildAuthority.GeneratedSources);

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
		string surfaceManifest = string.Join(
			'\n',
			exactTypes.SelectMany(GetTypeSurfaceManifest).Order(StringComparer.Ordinal)) + "\n";
#if DEBUG
		string expectedSurfaceSha256 = ExpectedDebugWireSurfaceSha256;
#else
		string expectedSurfaceSha256 = ExpectedReleaseWireSurfaceSha256;
#endif
		string actualSurfaceSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(surfaceManifest))).ToLowerInvariant();
		Assert.True(
			StringComparer.Ordinal.Equals(expectedSurfaceSha256, actualSurfaceSha256),
			actualSurfaceSha256);

		MethodInfo[] exactPlanEntryPoints = GetExactPlanWireEntryPoints(exactTypes);
		Assert.Equal(
			new[]
			{
				"GetDestinationNetworkManifestId",
				"GetDestinationsForWireEncoding",
				"GetExplicitFee",
				"GetPeggedAssetId",
				"GetSelectedEntriesForWireEncoding",
				"get_SelectedInputCount",
				"get_SourceRevision",
			},
			exactPlanEntryPoints.Select(method => method.Name));
		MethodBase[] wireRoots = exactTypes
			.SelectMany(GetDeclaredMethods)
			.Concat(exactPlanEntryPoints)
			.Distinct()
			.OrderBy(MethodIdentity, StringComparer.Ordinal)
			.ToArray();
		MethodBase[] wireClosure = AssertWireMethodClosureSafe(wireRoots);
		Assert.All(wireRoots, root => Assert.Contains(root, wireClosure));
		string wireClosureManifest = BuildMethodClosureManifest(wireClosure);
#if DEBUG
		string expectedWireClosureSha256 = ExpectedDebugWireClosureSha256;
#else
		string expectedWireClosureSha256 = ExpectedReleaseWireClosureSha256;
#endif
		string actualWireClosureSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(wireClosureManifest))).ToLowerInvariant();
		Assert.True(
			StringComparer.Ordinal.Equals(
				expectedWireClosureSha256,
				actualWireClosureSha256),
			actualWireClosureSha256);
		AssertPeModuleInitializerAndAmbientClosureAuthority(
			typeof(LiquidOrdinaryWalletPlanEncoder).Assembly);

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
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertApprovedDotnetHost(
				Path.Combine(Path.GetTempPath(), "fake-dotnet"),
				buildAuthority.DotnetRoot));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactBuildAuthority(
				MutateBuildProperty(buildAuthority.Properties, "Configuration", "Unexpected"),
				buildAuthority.DotnetRoot,
				productionRoot,
				buildAuthority.GeneratedRoot));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactBuildAuthority(
				MutateBuildProperty(buildAuthority.Properties, "TargetFramework", "net9.0"),
				buildAuthority.DotnetRoot,
				productionRoot,
				buildAuthority.GeneratedRoot));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactBuildAuthority(
				MutateBuildProperty(buildAuthority.Properties, "Platform", "x64"),
				buildAuthority.DotnetRoot,
				productionRoot,
				buildAuthority.GeneratedRoot));
		foreach ((string property, string value) in new[]
		{
			("DirectoryBuildTargetsPath", "/wlpq/injected-directory-build.targets"),
			("CustomBeforeMicrosoftCommonTargets", "/wlpq/injected-analyzer.targets"),
			("CscToolPath", "/wlpq/unreviewed-compiler"),
		})
		{
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertExactChildGlobalProperties(
					MutateBuildProperty(buildAuthority.GlobalProperties, property, value),
					buildAuthority.GlobalProperties));
		}
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactChildEnvironment(
				MutateBuildProperty(buildAuthority.ChildEnvironment, "NUGET_PACKAGES", "/wlpq/unreviewed-packages"),
				buildAuthority.ChildEnvironment));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactInvocationArguments(
				buildAuthority.InvocationArguments.Append("@/wlpq/injected-response.rsp").ToArray(),
				buildAuthority.InvocationArguments));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertConfiguredAuthorityHashes(
				buildAuthority.ImportClosureManifest + "IMPORT_EVENT|DUPLICATE\n",
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest,
				buildAuthority.ToolchainAuthorityManifest));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertConfiguredAuthorityHashes(
				string.Join('\n', buildAuthority.ImportClosureManifest.Split('\n').Reverse()),
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest,
				buildAuthority.ToolchainAuthorityManifest));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertConfiguredAuthorityHashes(
				buildAuthority.ImportClosureManifest.Replace(
					"project.assets.json|",
					"project.assets.json|MUTATED-BYTE|",
					StringComparison.Ordinal),
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest,
				buildAuthority.ToolchainAuthorityManifest));

		string packageMutationRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-package-authority-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(packageMutationRoot);
			string primaryPackageRoot = Path.Combine(packageMutationRoot, "packages");
			string fallbackPackageRoot = Path.Combine(packageMutationRoot, "fallback");
			string nestedPackageRoot = Path.Combine(primaryPackageRoot, "nested");
			string undeclaredPackageRoot = Path.Combine(packageMutationRoot, "unapproved/.nuget/packages");
			Directory.CreateDirectory(primaryPackageRoot);
			Directory.CreateDirectory(fallbackPackageRoot);
			Directory.CreateDirectory(nestedPackageRoot);
			Directory.CreateDirectory(undeclaredPackageRoot);
			string syntheticAssets = Path.Combine(packageMutationRoot, "project.assets.json");

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(fallbackPackageRoot, true));
			(string PrimaryRoot, string[] OrderedRoots) multiRoot =
				GetPinnedPackageAuthority(syntheticAssets);
			Assert.Equal(primaryPackageRoot, multiRoot.PrimaryRoot);
			Assert.Equal([primaryPackageRoot, fallbackPackageRoot], multiRoot.OrderedRoots);

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true));
			(string PrimaryRoot, string[] OrderedRoots) singleRoot =
				GetPinnedPackageAuthority(syntheticAssets);
			Assert.Equal(primaryPackageRoot, singleRoot.PrimaryRoot);
			Assert.Equal([primaryPackageRoot], singleRoot.OrderedRoots);

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(fallbackPackageRoot, true),
				(primaryPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(primaryPackageRoot + Path.DirectorySeparatorChar, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(nestedPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				Path.Combine(primaryPackageRoot, "..", "fallback"),
				(fallbackPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				"relative/packages",
				(primaryPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(syntheticAssets, primaryPackageRoot);
			AssertPackageAuthorityRejected(syntheticAssets);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, false));
			AssertPackageAuthorityRejected(syntheticAssets);
			string missingPackageRoot = Path.Combine(packageMutationRoot, "missing");
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				missingPackageRoot,
				(missingPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);
			File.WriteAllText(
				syntheticAssets,
				"{\"project\":{\"restore\":{}},\"packageFolders\":{}}",
				Encoding.UTF8);
			AssertPackageAuthorityRejected(syntheticAssets);

			string linkedPackageRoot = Path.Combine(packageMutationRoot, "linked-packages");
			Directory.CreateSymbolicLink(linkedPackageRoot, fallbackPackageRoot);
			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				linkedPackageRoot,
				(linkedPackageRoot, true));
			AssertPackageAuthorityRejected(syntheticAssets);

			WritePackageAssetsAuthorityFixture(
				syntheticAssets,
				primaryPackageRoot,
				(primaryPackageRoot, true),
				(fallbackPackageRoot, true));
			multiRoot = GetPinnedPackageAuthority(syntheticAssets);
			string relativePackageFile = "example.package/1.2.3/lib/net10.0/Example.dll";
			string primaryPackageFile = Path.Combine(
				primaryPackageRoot,
				relativePackageFile.Replace('/', Path.DirectorySeparatorChar));
			string fallbackPackageFile = Path.Combine(
				fallbackPackageRoot,
				relativePackageFile.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(primaryPackageFile)!);
			Directory.CreateDirectory(Path.GetDirectoryName(fallbackPackageFile)!);
			File.WriteAllBytes(primaryPackageFile, [1, 2, 3, 4]);
			File.WriteAllBytes(fallbackPackageFile, [1, 2, 3, 4]);
			string expectedPackageIdentity = $"NUGET|{relativePackageFile}";
			Assert.Equal(
				expectedPackageIdentity,
				NormalizeAuthorityPath(
					primaryPackageFile,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					multiRoot));
			Assert.Equal(
				expectedPackageIdentity,
				NormalizeAuthorityPath(
					fallbackPackageFile,
					buildAuthority.RepositoryRoot,
					buildAuthority.DotnetRoot,
					multiRoot));
			Assert.Equal(
				$"/reference:{{NUGET}}/{relativePackageFile}",
				NormalizeAuthorityStringWithPackages(
					$"/reference:{fallbackPackageFile}",
					multiRoot));
			string adjacentRootLookalike = NormalizeAuthorityStringWithPackages(
				$"/reference:{fallbackPackageRoot}-undeclared/{relativePackageFile}",
				multiRoot);
			Assert.DoesNotContain("{NUGET}", adjacentRootLookalike, StringComparison.Ordinal);

			File.WriteAllBytes(fallbackPackageFile, [1, 2, 3, 5]);
			AssertPackagePathRejected(
				primaryPackageFile,
				buildAuthority.RepositoryRoot,
				buildAuthority.DotnetRoot,
				multiRoot);

			string undeclaredPackageFile = Path.Combine(
				undeclaredPackageRoot,
				relativePackageFile.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(undeclaredPackageFile)!);
			File.WriteAllBytes(undeclaredPackageFile, [1, 2, 3, 4]);
			AssertPackagePathRejected(
				undeclaredPackageFile,
				buildAuthority.RepositoryRoot,
				buildAuthority.DotnetRoot,
				multiRoot);
		}
		finally
		{
			if (Directory.Exists(packageMutationRoot))
			{
				Directory.Delete(packageMutationRoot, recursive: true);
			}
		}

		string symlinkMutationRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-symlink-mutation-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(symlinkMutationRoot);
			string targetDirectory = Path.Combine(symlinkMutationRoot, "target");
			string link = Path.Combine(symlinkMutationRoot, "linked-directory");
			Directory.CreateDirectory(targetDirectory);
			string target = Path.Combine(targetDirectory, "regular.props");
			File.WriteAllText(target, "<Project />", Encoding.UTF8);
			Directory.CreateSymbolicLink(link, targetDirectory);
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertRegularAuthorityFile(
					Path.Combine(link, "regular.props"),
					"symbolic-link ancestor mutation"));
		}
		finally
		{
			if (Directory.Exists(symlinkMutationRoot))
			{
				Directory.Delete(symlinkMutationRoot, recursive: true);
			}
		}

		string linkedFullPath = Path.GetFullPath(Path.Combine(
			productionRoot,
			"../linked/LiquidOrdinaryWalletPlanEncoder.Linked.cs"));
		var linkedExplicitInclude = (
			FullPath: linkedFullPath,
			RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, linkedFullPath)),
			Source: "namespace WalletWasabi.Liquid.Wallet.Wire; internal static partial class LiquidOrdinaryWalletPlanEncoder { }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactImplementationCompileInputs(
				expectedImplementationPaths,
				productionRoot,
				evaluatedCompileInputs.Append(linkedExplicitInclude).ToArray()));

		string generatedFullPath = Path.GetFullPath(Path.Combine(
			productionRoot,
			"obj/Debug/net10.0/LiquidOrdinaryWalletPlanEncoder.Generated.cs"));
		var generatedCompileItem = (
			FullPath: generatedFullPath,
			RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, generatedFullPath)),
			Source: "namespace WalletWasabi.Liquid.Wallet.Wire; internal static partial class LiquidOrdinaryWalletPlanEncoder { }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactImplementationCompileInputs(
				expectedImplementationPaths,
				productionRoot,
				evaluatedCompileInputs.Append(generatedCompileItem).ToArray()));

		string nestedFullPath = Path.GetFullPath(Path.Combine(
			productionRoot,
			"Liquid/Wallet/Wire/Nested/AdditionalWireAuthority.cs"));
		var nestedPartialContribution = (
			FullPath: nestedFullPath,
			RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, nestedFullPath)),
			Source: "namespace WalletWasabi.Liquid.Wallet.Wire.Nested; internal static partial class AdditionalWireAuthority { }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactImplementationCompileInputs(
				expectedImplementationPaths,
				productionRoot,
				evaluatedCompileInputs.Append(nestedPartialContribution).ToArray()));

		foreach (string condition in new[] { "Configuration", "TargetFramework", "Platform" })
		{
			string conditionalFullPath = Path.GetFullPath(Path.Combine(
				productionRoot,
				$"obj/authority-mutation/{condition}/LiquidOrdinaryWalletPlanEncoder.Conditional.cs"));
			var conditionalContributor = (
				FullPath: conditionalFullPath,
				RelativePath: NormalizeRelativePath(Path.GetRelativePath(productionRoot, conditionalFullPath)),
				Source: "namespace WalletWasabi.Liquid.Wallet.Wire; internal static partial class LiquidOrdinaryWalletPlanEncoder { }");
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertExactImplementationCompileInputs(
					expectedImplementationPaths,
					productionRoot,
					evaluatedCompileInputs.Append(conditionalContributor).ToArray()));
		}

		var ambientModuleInitializer = (
			FullPath: Path.GetFullPath(Path.Combine(productionRoot, "AmbientModuleInitializer.cs")),
			RelativePath: "AmbientModuleInitializer.cs",
			Source: "using System.Runtime.CompilerServices; internal static class Added { [ModuleInitializer] internal static void Initialize() { } }");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactAmbientCompileAuthority(evaluatedCompileInputs.Append(ambientModuleInitializer)));
		var ambientAssemblyAttribute = (
			FullPath: Path.GetFullPath(Path.Combine(productionRoot, "AmbientAssemblyAttribute.cs")),
			RelativePath: "AmbientAssemblyAttribute.cs",
			Source: "[assembly: System.CLSCompliant(true)]");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactAmbientCompileAuthority(evaluatedCompileInputs.Append(ambientAssemblyAttribute)));
		var ambientGlobalAlias = (
			FullPath: Path.GetFullPath(Path.Combine(productionRoot, "AmbientGlobalAlias.cs")),
			RelativePath: "AmbientGlobalAlias.cs",
			Source: "global using WireAlias = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder;");
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactAmbientCompileAuthority(evaluatedCompileInputs.Append(ambientGlobalAlias)));
		Assert.Contains(
			"BeforeTargets=\"CoreCompile\"",
			buildAuthority.InjectedAnalyzerTargetContent,
			StringComparison.Ordinal);
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertConfiguredAuthorityHashes(
				buildAuthority.ImportClosureManifest,
				buildAuthority.ReferenceAuthorityManifest,
				buildAuthority.CompilerInputAuthorityManifest +
					"CSC_INPUT|INJECTED|Analyzers|/wlpq/injected-analyzer.dll\n" +
					"CSC_ARG|INJECTED|/analyzer:/wlpq/injected-analyzer.dll\n",
				buildAuthority.ToolchainAuthorityManifest));
		Assert.Contains(
			"<Analyzer Include=\"/wlpq/injected-analyzer.dll\" />",
			buildAuthority.InjectedAnalyzerTargetContent,
			StringComparison.Ordinal);
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactGeneratedSourceAuthority(buildAuthority.GeneratedSources.Append((
				new GeneratedBuildFile(
					"FakeGenerator/Fake.Generated.cs",
					"namespace WalletWasabi.Liquid.Wallet.Wire; internal static class GeneratedAuthority { }",
					new string('0', 64))))));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactPlanWireAccessorSource(
				File.ReadAllText(Path.Combine(productionRoot, "Liquid/Wallet/LiquidOrdinaryWalletExactSpendPlan.cs"))
					.Replace("public int SelectedInputCount => _selectedEntries.Length;", "public int SelectedInputCount => 0;", StringComparison.Ordinal)));

		foreach (MethodInfo forbiddenClosureMutation in CreateForbiddenClosureMutations())
		{
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertWireMethodClosureSafe([forbiddenClosureMutation]));
		}
		Assert.True(IsProductionWireNamespace("WalletWasabi.Liquid.Wallet.Wire.Nested"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactWireTypeNames(
				exactTypes.Select(type => type.FullName!),
				exactTypes.Select(type => type.FullName!)
					.Append("WalletWasabi.Liquid.Wallet.Wire.Nested.Added")));
	}

	[Fact]
	public void RestoreArtifactAuthorityIsPortableAndMutationClosed()
	{
		string currentRevision = new('a', 40);
		Assert.Equal(
			"1.2.3+release",
			RemoveSdkSourceRevisionSuffix("1.2.3+release", "", currentRevision));
		Assert.Equal(
			"1.2.3+release",
			RemoveSdkSourceRevisionSuffix($"1.2.3+release.{currentRevision}", "", currentRevision));
		Assert.Equal(
			$"1.2.3+{currentRevision}",
			RemoveSdkSourceRevisionSuffix($"1.2.3+{currentRevision}", "", null));
		Assert.Equal(
			$"1.2.3+{currentRevision}",
			RemoveSdkSourceRevisionSuffix($"1.2.3+{currentRevision}", "", new string('b', 40)));
		Assert.Equal(
			$"1.2.3+{currentRevision}",
			RemoveSdkSourceRevisionSuffix($"1.2.3+{currentRevision}", currentRevision, null));
		Assert.Equal(
			"1.2.3+release",
			RemoveSdkSourceRevisionSuffix(
				$"1.2.3+release.{currentRevision}",
				currentRevision,
				currentRevision));
		Assert.True(IsValidGitReferenceName("refs/heads/release-é@candidate"));
		Assert.False(IsValidGitReferenceName("refs/heads/../escape"));
		Assert.False(IsValidGitReferenceName("refs/heads/control\u0001name"));

		string fixtureRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-restore-artifact-{Guid.NewGuid():N}");
		try
		{
			string firstRoot = Path.Combine(fixtureRoot, "first");
			string secondRoot = Path.Combine(fixtureRoot, "second");
			string firstRepository = Path.Combine(firstRoot, "repo");
			string secondRepository = Path.Combine(secondRoot, "repo");
			string firstDotnet = Path.Combine(firstRoot, "dotnet");
			string secondDotnet = Path.Combine(secondRoot, "dotnet");
			string firstPrimary = Path.Combine(firstRoot, "packages");
			string secondPrimary = Path.Combine(secondRoot, "packages");
			string secondFallback = Path.Combine(secondRoot, "fallback");
			Directory.CreateDirectory(firstRepository);
			Directory.CreateDirectory(secondRepository);
			Directory.CreateDirectory(firstDotnet);
			Directory.CreateDirectory(secondDotnet);
			Directory.CreateDirectory(firstPrimary);
			Directory.CreateDirectory(secondPrimary);
			Directory.CreateDirectory(secondFallback);

			string detachedRepository = Path.Combine(fixtureRoot, "git-detached");
			string detachedGitDirectory = Path.Combine(detachedRepository, ".git");
			Directory.CreateDirectory(detachedGitDirectory);
			File.WriteAllText(Path.Combine(detachedGitDirectory, "HEAD"), currentRevision, Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(detachedRepository));

			string looseRepository = Path.Combine(fixtureRoot, "git-loose");
			string looseGitDirectory = Path.Combine(looseRepository, ".git");
			string looseReference = "refs/heads/release-é@candidate";
			Directory.CreateDirectory(Path.Combine(looseGitDirectory, "refs/heads"));
			File.WriteAllText(Path.Combine(looseGitDirectory, "HEAD"), $"ref: {looseReference}\n", Encoding.UTF8);
			File.WriteAllText(Path.Combine(looseGitDirectory, looseReference), currentRevision, Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(looseRepository));

			string packedRepository = Path.Combine(fixtureRoot, "git-packed");
			string packedGitDirectory = Path.Combine(packedRepository, ".git");
			string packedReference = "refs/heads/packed@candidate";
			Directory.CreateDirectory(packedGitDirectory);
			File.WriteAllText(Path.Combine(packedGitDirectory, "HEAD"), $"ref: {packedReference}\n", Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(packedGitDirectory, "packed-refs"),
				$"# pack-refs with: peeled fully-peeled sorted\n{currentRevision} {packedReference}\n",
				Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(packedRepository));

			string linkedRepository = Path.Combine(fixtureRoot, "git-linked");
			string commonGitDirectory = Path.Combine(fixtureRoot, "git-common");
			string linkedGitDirectory = Path.Combine(commonGitDirectory, "worktrees/linked");
			string linkedReference = "refs/heads/linked-é@candidate";
			Directory.CreateDirectory(linkedRepository);
			Directory.CreateDirectory(Path.Combine(commonGitDirectory, "refs/heads"));
			Directory.CreateDirectory(linkedGitDirectory);
			File.WriteAllText(
				Path.Combine(linkedRepository, ".git"),
				$"gitdir: {linkedGitDirectory}\n",
				Encoding.UTF8);
			File.WriteAllText(Path.Combine(linkedGitDirectory, "commondir"), "../..\n", Encoding.UTF8);
			File.WriteAllText(Path.Combine(linkedGitDirectory, "HEAD"), $"ref: {linkedReference}\n", Encoding.UTF8);
			File.WriteAllText(Path.Combine(commonGitDirectory, linkedReference), currentRevision, Encoding.UTF8);
			Assert.Equal(currentRevision, TryReadRepositoryRevision(linkedRepository));

			string firstImport = CreateSemanticRestorePackageImport(firstPrimary, [1, 2, 3, 4]);
			string secondImport = CreateSemanticRestorePackageImport(secondFallback, [1, 2, 3, 4]);
			string firstAssets = WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			string secondAssets = WriteSemanticRestoreFixture(
				secondRepository,
				secondDotnet,
				secondPrimary,
				[secondPrimary, secondFallback],
				secondImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			string secondOfflineSource = Path.Combine(secondRoot, "source");
			Directory.CreateDirectory(secondOfflineSource);
			File.WriteAllText(
				secondAssets,
				File.ReadAllText(secondAssets).Replace(
					JsonSerializer.Serialize("https://api.nuget.org/v3/index.json") + ":{}",
					JsonSerializer.Serialize(secondOfflineSource) + ":{}",
					StringComparison.Ordinal),
				Encoding.UTF8);
			(string PrimaryRoot, string[] OrderedRoots) firstAuthority = GetPinnedPackageAuthority(firstAssets);
			(string PrimaryRoot, string[] OrderedRoots) secondAuthority = GetPinnedPackageAuthority(secondAssets);
			string firstManifest = BuildSemanticRestoreFixtureManifest(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);
			string secondManifest = BuildSemanticRestoreFixtureManifest(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);
			Assert.Equal(firstManifest, secondManifest);

			string secondFallbackProperty =
				$"\"fallbackFolders\":[{JsonSerializer.Serialize(secondFallback)}],";
			string secondAssetsText = File.ReadAllText(secondAssets);
			Assert.Contains(secondFallbackProperty, secondAssetsText, StringComparison.Ordinal);
			File.WriteAllText(
				secondAssets,
				secondAssetsText.Replace(secondFallbackProperty, "", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				secondAssets,
				secondRepository,
				secondDotnet,
				secondAuthority);

			const string RestoreSourceProperty =
				"\"sources\":{\"https://api.nuget.org/v3/index.json\":{}},";
			string firstAssetsText = File.ReadAllText(firstAssets);
			Assert.Contains(RestoreSourceProperty, firstAssetsText, StringComparison.Ordinal);
			File.WriteAllText(
				firstAssets,
				firstAssetsText.Replace(
					RestoreSourceProperty,
					RestoreSourceProperty + "\"fallbackFolders\":[],",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				firstAuthority);

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.4",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.4");
			Assert.NotEqual(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					GetPinnedPackageAuthority(firstAssets)));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			File.WriteAllText(
				firstAssets,
				File.ReadAllText(firstAssets).Replace(
					"https://api.nuget.org/v3/index.json",
					"https://unapproved.invalid/v3/index.json",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));
			string generatedProps =
				Path.Combine(firstRepository, "WalletWasabi/obj/WalletWasabi.csproj.nuget.g.props");
			string sourceRootDeclaration =
				$"<SourceRoot Include=\"{System.Security.SecurityElement.Escape(firstPrimary + Path.DirectorySeparatorChar)}\" />";
			string generatedPropsText;

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			File.WriteAllText(
				generatedProps,
				generatedPropsText.Replace(
					sourceRootDeclaration,
					$"<SourceRoot Include=\"{System.Security.SecurityElement.Escape(firstPrimary)}\" />",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			File.WriteAllText(
				generatedProps,
				generatedPropsText.Replace(
					sourceRootDeclaration,
					$"<SourceRoot Include=\"{System.Security.SecurityElement.Escape(firstPrimary + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar)}\" />",
					StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			Assert.Contains(sourceRootDeclaration, generatedPropsText, StringComparison.Ordinal);
			File.WriteAllText(
				generatedProps,
				generatedPropsText.Replace(sourceRootDeclaration, "", StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			generatedPropsText = File.ReadAllText(generatedProps);
			Assert.Contains("NuGetPackageRoot", generatedPropsText, StringComparison.Ordinal);
			File.WriteAllText(
				generatedProps,
				generatedPropsText
					.Replace(
						"<NuGetPackageRoot>",
						"<VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY>",
						StringComparison.Ordinal)
					.Replace(
						"</NuGetPackageRoot>",
						"</VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY>",
						StringComparison.Ordinal),
				Encoding.UTF8);
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(8),
				"example.package/1.2.3");
			Assert.NotEqual(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					GetPinnedPackageAuthority(firstAssets)));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"../example.package/1.2.3");
			AssertSemanticRestoreFixtureRejected(
				firstAssets,
				firstRepository,
				firstDotnet,
				GetPinnedPackageAuthority(firstAssets));

			WriteSemanticRestoreFixture(
				firstRepository,
				firstDotnet,
				firstPrimary,
				[firstPrimary],
				firstImport,
				"1.2.3",
				CreateSemanticRestoreContentHash(7),
				"example.package/1.2.3");
			string alternateImport = CreateSemanticRestorePackageImport(
				firstPrimary,
				[1, 2, 3, 4],
				"alternate.props");
			WriteSemanticNuGetPropsFixture(
				Path.Combine(firstRepository, "WalletWasabi/obj/WalletWasabi.csproj.nuget.g.props"),
				[firstPrimary],
				alternateImport);
			Assert.NotEqual(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					GetPinnedPackageAuthority(firstAssets)));

			WriteSemanticNuGetPropsFixture(
				Path.Combine(firstRepository, "WalletWasabi/obj/WalletWasabi.csproj.nuget.g.props"),
				[firstPrimary],
				firstImport);
			File.WriteAllBytes(firstImport, [1, 2, 3, 5]);
			Assert.NotEqual(
				firstManifest,
				BuildSemanticRestoreFixtureManifest(
					firstAssets,
					firstRepository,
					firstDotnet,
					GetPinnedPackageAuthority(firstAssets)));
		}
		finally
		{
			if (Directory.Exists(fixtureRoot))
			{
				Directory.Delete(fixtureRoot, recursive: true);
			}
		}
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

	private sealed record EvaluatedBuildItem(
		string Identity,
		string FullPath,
		string DefiningProjectFullPath,
		IReadOnlyDictionary<string, string> Metadata);

	private sealed record GeneratedBuildFile(string RelativePath, string Source, string Sha256);
	private sealed record BinaryBuildTrace(
		string[] CommandLineArgs,
		IReadOnlyDictionary<string, string[]> TaskInputs,
		string[] ImportedProjects,
		string ImportManifest,
		string CscManifest);
	private readonly record struct BuildContextKey(
		int NodeId,
		int ProjectContextId,
		int TargetId,
		int TaskId,
		int SubmissionId,
		int ProjectInstanceId,
		int EvaluationId);

	private sealed record ProductionBuildAuthority(
		IReadOnlyDictionary<string, string> Properties,
		IReadOnlyDictionary<string, string> GlobalProperties,
		IReadOnlyDictionary<string, string> ChildEnvironment,
		string[] InvocationArguments,
		(string FullPath, string RelativePath, string Source)[] CompileInputs,
		(string FullPath, string DefiningProjectFullPath)[] Analyzers,
		EvaluatedBuildItem[] ReferencePaths,
		EvaluatedBuildItem[] AdditionalFiles,
		EvaluatedBuildItem[] EditorConfigFiles,
		EvaluatedBuildItem[] EmbeddedFiles,
		string[] CscCommandLineArgs,
		GeneratedBuildFile[] GeneratedSources,
		string[] ImportedProjects,
		string ImportClosureManifest,
		string ReferenceAuthorityManifest,
		string CompilerInputAuthorityManifest,
		string ToolchainAuthorityManifest,
		string OutputAssemblySha256,
		string DotnetHost,
		string DotnetRoot,
		string RepositoryRoot,
		(string PrimaryRoot, string[] OrderedRoots) PackageAuthority,
		string AuthorityRoot,
		string GeneratedRoot,
		string InjectedAnalyzerTargetContent);

	private static ProductionBuildAuthority GetEvaluatedProductionBuildAuthority(
		string expectedProductionRoot)
	{
		string projectPath = Path.GetFullPath(Path.Combine(expectedProductionRoot, "WalletWasabi.csproj"));
		string repositoryRoot = Path.GetFullPath(Path.GetDirectoryName(expectedProductionRoot)!);
		string projectAssetsFile = Path.GetFullPath(Path.Combine(expectedProductionRoot, "obj/project.assets.json"));
		string projectExtensionsPath = Path.GetFullPath(Path.Combine(expectedProductionRoot, "obj")) +
			Path.DirectorySeparatorChar;
		(string dotnetHost, string dotnetRoot) = GetApprovedDotnetHost();
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority = GetPinnedPackageAuthority(projectAssetsFile);
		string packageRoot = packageAuthority.PrimaryRoot;
		string authorityRoot = Path.Combine(
			Path.GetTempPath(),
			$"walletwasabi-wlpq-authority-{Guid.NewGuid():N}");
		string baseIntermediateOutputPath = Path.Combine(authorityRoot, "obj") + Path.DirectorySeparatorChar;
		string intermediateOutputPath = Path.Combine(authorityRoot, "obj/net10.0") + Path.DirectorySeparatorChar;
		string baseOutputPath = Path.Combine(authorityRoot, "base-bin") + Path.DirectorySeparatorChar;
		string outputPath = Path.Combine(authorityRoot, "bin") + Path.DirectorySeparatorChar;
		string generatedRoot = Path.Combine(authorityRoot, "generated");
		string disabledImportsRoot = Path.Combine(authorityRoot, "disabled-imports");
		string childHome = Path.Combine(authorityRoot, "home");
		string childTemp = Path.Combine(authorityRoot, "temp");
		string injectedAnalyzerTarget = Path.Combine(authorityRoot, "injected-analyzer.targets");
		string automaticResponseFile = Path.Combine(authorityRoot, "MSBuild.rsp");
		string diagnosticLog = Path.Combine(authorityRoot, "build.diagnostic.log");
		string binaryLog = Path.Combine(authorityRoot, "build.binlog");
		const string InjectedAnalyzerTargetContent =
			"<Project><Target Name=\"InjectAnalyzer\" BeforeTargets=\"CoreCompile\"><ItemGroup>" +
			"<Analyzer Include=\"/wlpq/injected-analyzer.dll\" />" +
			"</ItemGroup></Target></Project>";
		Directory.CreateDirectory(authorityRoot);
		Directory.CreateDirectory(generatedRoot);
		Directory.CreateDirectory(disabledImportsRoot);
		Directory.CreateDirectory(childHome);
		Directory.CreateDirectory(childTemp);
		File.WriteAllText(
			injectedAnalyzerTarget,
			InjectedAnalyzerTargetContent,
			Encoding.UTF8);
		File.WriteAllText(
			automaticResponseFile,
			"-property:CscToolPath=/wlpq/automatic-response-file-must-be-ignored\n",
			Encoding.UTF8);

		try
		{
#if DEBUG
			const string configuration = "Debug";
#else
			const string configuration = "Release";
#endif
			(string productVersion, string commitHash) = GetLoadedProductBuildIdentity();
			string sdkRoot = Path.Combine(dotnetRoot, "sdk/10.0.100");
			string roslynRoot = Path.Combine(sdkRoot, "Roslyn");
			var globalProperties = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["Configuration"] = configuration,
				["Version"] = productVersion,
				["CommitHash"] = commitHash,
				["TargetFramework"] = "net10.0",
				["Platform"] = "AnyCPU",
				["BaseIntermediateOutputPath"] = baseIntermediateOutputPath,
				["IntermediateOutputPath"] = intermediateOutputPath,
				["BaseOutputPath"] = baseOutputPath,
				["OutputPath"] = outputPath,
				["PathMap"] = $"{generatedRoot}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{configuration}/net10.0/," +
					$"{intermediateOutputPath}=WalletWasabi/obj/{configuration}/net10.0/," +
					$"{expectedProductionRoot}{Path.DirectorySeparatorChar}=WalletWasabi",
				["DefaultExcludesInProjectFolder"] = "bin/**;obj/**;**/.*/**",
				["MSBuildProjectExtensionsPath"] = projectExtensionsPath,
				["ProjectAssetsFile"] = projectAssetsFile,
				["BuildProjectReferences"] = "false",
				["UseSharedCompilation"] = "false",
				["UseHostCompilerIfAvailable"] = "false",
				["ProvideCommandLineArgs"] = "true",
				["EmitCompilerGeneratedFiles"] = "true",
				["CompilerGeneratedFilesOutputPath"] = generatedRoot,
				["RestoreDuringBuild"] = "false",
				["RestorePackagesPath"] = packageRoot,
				["NuGetPackageRoot"] = packageRoot + Path.DirectorySeparatorChar,
				["DisableImplicitNuGetFallbackFolder"] = "true",
				["ImportDirectoryBuildProps"] = "true",
				["DirectoryBuildPropsPath"] = Path.Combine(repositoryRoot, "Directory.Build.props"),
				["ImportDirectoryBuildTargets"] = "false",
				["DirectoryBuildTargetsPath"] = "",
				["CustomBeforeDirectoryBuildProps"] = "",
				["CustomAfterDirectoryBuildProps"] = "",
				["CustomBeforeDirectoryBuildTargets"] = "",
				["CustomAfterDirectoryBuildTargets"] = "",
				["ImportProjectExtensionProps"] = "true",
				["ImportProjectExtensionTargets"] = "true",
				["ImportByWildcardBeforeMicrosoftCommonProps"] = "false",
				["ImportByWildcardAfterMicrosoftCommonProps"] = "false",
				["ImportUserLocationsByWildcardBeforeMicrosoftCommonProps"] = "false",
				["ImportUserLocationsByWildcardAfterMicrosoftCommonProps"] = "false",
				["ImportByWildcardBeforeMicrosoftCommonTargets"] = "false",
				["ImportByWildcardAfterMicrosoftCommonTargets"] = "false",
				["ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets"] = "false",
				["ImportUserLocationsByWildcardAfterMicrosoftCommonTargets"] = "false",
				["ImportByWildcardBeforeMicrosoftCSharpTargets"] = "false",
				["ImportByWildcardAfterMicrosoftCSharpTargets"] = "false",
				["ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets"] = "false",
				["ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets"] = "false",
				["CustomBeforeMicrosoftCommonProps"] = "",
				["CustomAfterMicrosoftCommonProps"] = "",
				["CustomBeforeMicrosoftCommonTargets"] = "",
				["CustomAfterMicrosoftCommonTargets"] = "",
				["CustomBeforeMicrosoftCSharpTargets"] = "",
				["CustomAfterMicrosoftCSharpTargets"] = "",
				["MSBuildUserExtensionsPath"] = disabledImportsRoot,
				["MSBuildSDKsPath"] = Path.Combine(sdkRoot, "Sdks"),
				["RoslynTargetsPath"] = roslynRoot,
				["CSharpCoreTargetsPath"] = Path.Combine(roslynRoot, "Microsoft.CSharp.Core.targets"),
				["CscToolPath"] = "",
				["CscToolExe"] = "",
				["MSBuildDisableAllAutoResponseFiles"] = "true",
			};
			var childEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["DOTNET_ROOT"] = dotnetRoot,
				["DOTNET_MULTILEVEL_LOOKUP"] = "0",
				["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
				["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
				["DOTNET_NOLOGO"] = "1",
				["MSBUILDDISABLENODEREUSE"] = "1",
				["HOME"] = childHome,
				["TMPDIR"] = childTemp,
			};
			var startInfo = new ProcessStartInfo
			{
				FileName = dotnetHost,
				WorkingDirectory = authorityRoot,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			startInfo.Environment.Clear();
			foreach ((string name, string value) in childEnvironment)
			{
				startInfo.Environment.Add(name, value);
			}
			startInfo.ArgumentList.Add("msbuild");
			startInfo.ArgumentList.Add(projectPath);
			startInfo.ArgumentList.Add("-target:Rebuild");
			startInfo.ArgumentList.Add("-noAutoResponse");
			string[] queriedProperties = new[]
			{
				"MSBuildProjectDirectory", "TargetFrameworkIdentifier", "TargetFrameworkVersion",
				"TargetFrameworks", "RuntimeIdentifier", "RuntimeIdentifiers", "NETCoreSdkVersion",
				"MSBuildVersion", "LangVersion", "DefineConstants", "AllowUnsafeBlocks",
				"MSBuildToolsPath", "CompileDependsOn", "CoreCompileDependsOn", "TargetsTriggeredByCompilation",
			}.Concat(globalProperties.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			startInfo.ArgumentList.Add("-getProperty:" + string.Join(',', queriedProperties));
			startInfo.ArgumentList.Add(
				"-getItem:Compile,Analyzer,ReferencePathWithRefAssemblies,AdditionalFiles," +
				"EditorConfigFiles,EmbeddedFiles,CscCommandLineArgs");
			startInfo.ArgumentList.Add($"-binaryLogger:{binaryLog};ProjectImports=None");
			startInfo.ArgumentList.Add("-fileLogger");
			startInfo.ArgumentList.Add(
				$"-fileLoggerParameters:LogFile={diagnosticLog};Verbosity=diagnostic;Encoding=UTF-8");
			foreach ((string name, string value) in globalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			{
				startInfo.ArgumentList.Add($"-property:{name}={EscapeMsbuildPropertyValue(value)}");
			}
			startInfo.ArgumentList.Add("-nologo");
			startInfo.ArgumentList.Add("-verbosity:quiet");
			string[] invocationArguments = startInfo.ArgumentList.ToArray();
			AssertExactChildGlobalProperties(globalProperties, CreateExpectedGlobalProperties(
				configuration,
				repositoryRoot,
				expectedProductionRoot,
				dotnetRoot,
				packageRoot,
				authorityRoot,
				productVersion,
				commitHash));
			AssertExactChildEnvironment(
				startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.Ordinal),
				CreateExpectedChildEnvironment(dotnetRoot, childHome, childTemp));
			AssertExactInvocationArguments(
				invocationArguments,
				CreateExpectedInvocationArguments(
					projectPath,
					queriedProperties,
					globalProperties,
					binaryLog,
					diagnosticLog));

			using var process = new Process { StartInfo = startInfo };
			Assert.True(process.Start(), "The bound MSBuild Rebuild authority process did not start.");
			Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorTask = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(TimeSpan.FromMinutes(4)))
			{
				process.Kill(entireProcessTree: true);
				throw new Xunit.Sdk.XunitException("The bound MSBuild Rebuild authority process timed out.");
			}

			string output = outputTask.GetAwaiter().GetResult();
			string error = errorTask.GetAwaiter().GetResult();
			Assert.True(
				process.ExitCode == 0,
				$"Bound MSBuild Rebuild authority failed with exit code {process.ExitCode}: {error}\n{output}");
			using JsonDocument document = JsonDocument.Parse(output);
			var properties = document.RootElement
				.GetProperty("Properties")
				.EnumerateObject()
				.ToDictionary(
					property => property.Name,
					property => property.Value.GetString() ?? "",
					StringComparer.Ordinal);
			string evaluatedProjectRoot = Path.GetFullPath(properties["MSBuildProjectDirectory"]);
			Assert.Equal(Path.GetFullPath(expectedProductionRoot), evaluatedProjectRoot);
			Assert.Equal("", properties["CscToolPath"]);

			EvaluatedBuildItem[] compileItems = ReadEvaluatedItems(document, "Compile", requireFile: true);
			Assert.DoesNotContain(compileItems, item =>
				IsPathWithin(item.FullPath, Path.Combine(evaluatedProjectRoot, "obj")) ||
				IsPathWithin(item.FullPath, Path.Combine(evaluatedProjectRoot, "bin")));
			var inputs = compileItems.Select(item => (
				item.FullPath,
				NormalizeRelativePath(Path.GetRelativePath(evaluatedProjectRoot, item.FullPath)),
				File.ReadAllText(item.FullPath))).ToArray();
			EvaluatedBuildItem[] analyzerItems = ReadEvaluatedItems(document, "Analyzer", requireFile: true);
			var analyzers = analyzerItems.Select(item =>
			{
				Assert.False(string.IsNullOrWhiteSpace(item.DefiningProjectFullPath));
				Assert.True(File.Exists(item.DefiningProjectFullPath));
				return (item.FullPath, item.DefiningProjectFullPath);
			}).ToArray();
			EvaluatedBuildItem[] referencePaths =
				ReadEvaluatedItems(document, "ReferencePathWithRefAssemblies", requireFile: true);
			EvaluatedBuildItem[] additionalFiles =
				ReadEvaluatedItems(document, "AdditionalFiles", requireFile: true);
			EvaluatedBuildItem[] editorConfigFiles =
				ReadEvaluatedItems(document, "EditorConfigFiles", requireFile: true);
			EvaluatedBuildItem[] embeddedFiles =
				ReadEvaluatedItems(document, "EmbeddedFiles", requireFile: true);
			string[] cscCommandLineArgs = ReadEvaluatedItems(
				document,
				"CscCommandLineArgs",
				requireFile: false).Select(item => item.Identity).ToArray();
			Assert.NotEmpty(cscCommandLineArgs);
			AssertCompilerArgumentsCoverInputs(
				cscCommandLineArgs,
				evaluatedProjectRoot,
				compileItems,
				analyzerItems,
				referencePaths,
				additionalFiles,
				editorConfigFiles,
				embeddedFiles);

			GeneratedBuildFile[] generatedSources = Directory
				.EnumerateFiles(generatedRoot, "*", SearchOption.AllDirectories)
				.Select(path => new GeneratedBuildFile(
					NormalizeRelativePath(Path.GetRelativePath(generatedRoot, Path.GetFullPath(path))),
					Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
						? File.ReadAllText(path)
						: "",
					Sha256File(path)))
				.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
				.ToArray();
			Assert.NotEmpty(generatedSources);
			Assert.Contains(
				generatedSources,
				generated => generated.RelativePath.Contains(
					"System.Text.RegularExpressions.Generator",
					StringComparison.Ordinal));

			Assert.True(File.Exists(binaryLog), "The single Rebuild did not produce its binary evaluation trace.");
			Assert.True(new FileInfo(binaryLog).Length > 0, "The binary evaluation trace is empty.");
			Assert.True(File.Exists(diagnosticLog), "The single Rebuild did not produce its diagnostic trace.");
			string diagnosticCscManifest = AssertCscDiagnosticAuthority(
				File.ReadAllText(diagnosticLog),
				dotnetRoot,
				generatedRoot,
				intermediateOutputPath);
			BinaryBuildTrace binaryTrace = ReadAndAssertBinaryBuildTrace(
				binaryLog,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				authorityRoot,
				projectPath,
				projectAssetsFile);
			Assert.Equal(cscCommandLineArgs, binaryTrace.CommandLineArgs);
			AssertCscTaskInputsMatchArguments(binaryTrace, evaluatedProjectRoot);
			string[] importedProjects = binaryTrace.ImportedProjects;
			string importClosureManifest = binaryTrace.ImportManifest;
			string cscTraceManifest = diagnosticCscManifest + binaryTrace.CscManifest;
			string referenceAuthorityManifest = BuildReferenceAuthorityManifest(
				referencePaths,
				repositoryRoot,
				dotnetRoot,
				packageAuthority);
			string compilerInputAuthorityManifest = BuildCompilerInputAuthorityManifest(
				cscCommandLineArgs,
				compileItems,
				analyzerItems,
				referencePaths,
				additionalFiles,
				editorConfigFiles,
				embeddedFiles,
				evaluatedProjectRoot,
				repositoryRoot,
				dotnetRoot,
				packageAuthority,
				authorityRoot);
			compilerInputAuthorityManifest += cscTraceManifest;
			string toolchainAuthorityManifest = BuildToolchainAuthorityManifest(dotnetHost, dotnetRoot);
			AssertConfiguredAuthorityHashes(
				importClosureManifest,
				referenceAuthorityManifest,
				compilerInputAuthorityManifest,
				toolchainAuthorityManifest);

			string rebuiltAssembly = Path.Combine(outputPath, "WalletWasabi.dll");
			string loadedAssembly = Path.GetFullPath(typeof(LiquidOrdinaryWalletPlanEncoder).Assembly.Location);
			Assert.True(File.Exists(rebuiltAssembly), $"Isolated Rebuild output is absent: {rebuiltAssembly}");
			byte[] loadedAssemblyBytes = File.ReadAllBytes(loadedAssembly);
			byte[] rebuiltAssemblyBytes = File.ReadAllBytes(rebuiltAssembly);
			AssertExactArtifactBytes(loadedAssemblyBytes, rebuiltAssemblyBytes);
			byte[] swappedAssemblyBytes = rebuiltAssemblyBytes.ToArray();
			swappedAssemblyBytes[^1] ^= 1;
			Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
				AssertExactArtifactBytes(loadedAssemblyBytes, swappedAssemblyBytes));
			string rebuiltPdb = Path.Combine(outputPath, "WalletWasabi.pdb");
			string loadedPdb = Path.ChangeExtension(loadedAssembly, ".pdb");
			Assert.True(File.Exists(rebuiltPdb), $"Isolated Rebuild PDB is absent: {rebuiltPdb}");
			Assert.True(File.Exists(loadedPdb), $"Loaded audited PDB is absent: {loadedPdb}");
			AssertExactArtifactBytes(File.ReadAllBytes(loadedPdb), File.ReadAllBytes(rebuiltPdb));
			string outputAssemblySha256 = Sha256File(rebuiltAssembly);

			return new ProductionBuildAuthority(
				properties,
				globalProperties,
				childEnvironment,
				invocationArguments,
				inputs,
				analyzers,
				referencePaths,
				additionalFiles,
				editorConfigFiles,
				embeddedFiles,
				cscCommandLineArgs,
				generatedSources,
				importedProjects,
				importClosureManifest,
				referenceAuthorityManifest,
				compilerInputAuthorityManifest,
				toolchainAuthorityManifest,
				outputAssemblySha256,
				dotnetHost,
				dotnetRoot,
				repositoryRoot,
				packageAuthority,
				authorityRoot,
				generatedRoot,
				InjectedAnalyzerTargetContent);
		}
		finally
		{
			Directory.Delete(authorityRoot, recursive: true);
		}
	}

	private static (string PrimaryRoot, string[] OrderedRoots) GetPinnedPackageAuthority(string projectAssetsFile)
	{
		AssertRegularAuthorityFile(projectAssetsFile, "project assets authority");
		using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(projectAssetsFile));
		JsonElement root = assets.RootElement;
		Assert.Equal(JsonValueKind.Object, root.ValueKind);
		Assert.True(root.TryGetProperty("project", out JsonElement project));
		Assert.Equal(JsonValueKind.Object, project.ValueKind);
		Assert.True(project.TryGetProperty("restore", out JsonElement restore));
		Assert.Equal(JsonValueKind.Object, restore.ValueKind);
		Assert.True(restore.TryGetProperty("packagesPath", out JsonElement packagesPath));
		Assert.Equal(JsonValueKind.String, packagesPath.ValueKind);
		string primaryRoot = ParseCanonicalPackageRoot(
			Assert.IsType<string>(packagesPath.GetString()),
			"primary package root");
		Assert.True(root.TryGetProperty("packageFolders", out JsonElement packageFolders));
		Assert.Equal(JsonValueKind.Object, packageFolders.ValueKind);
		var orderedRoots = new List<string>();
		foreach (JsonProperty property in packageFolders.EnumerateObject())
		{
			Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
			Assert.Empty(property.Value.EnumerateObject());
			orderedRoots.Add(ParseCanonicalPackageRoot(property.Name, "declared package root"));
		}
		Assert.NotEmpty(orderedRoots);
		Assert.Equal(primaryRoot, orderedRoots[0]);
		var uniqueRoots = new HashSet<string>(PackagePathComparer);
		foreach (string packageRoot in orderedRoots)
		{
			Assert.True(uniqueRoots.Add(packageRoot), $"Duplicate declared package root: {packageRoot}");
		}
		for (int first = 0; first < orderedRoots.Count; first++)
		{
			for (int second = first + 1; second < orderedRoots.Count; second++)
			{
				Assert.False(
					IsPathWithin(orderedRoots[first], orderedRoots[second]) ||
					IsPathWithin(orderedRoots[second], orderedRoots[first]),
					$"Declared package roots overlap: {orderedRoots[first]} and {orderedRoots[second]}");
			}
		}
		return (primaryRoot, orderedRoots.ToArray());
	}

	private static IReadOnlyDictionary<string, string> CreateExpectedGlobalProperties(
		string configuration,
		string repositoryRoot,
		string productionRoot,
		string dotnetRoot,
		string packageRoot,
		string authorityRoot,
		string productVersion,
		string commitHash)
	{
		string sdkRoot = Path.Combine(dotnetRoot, "sdk/10.0.100");
		string roslynRoot = Path.Combine(sdkRoot, "Roslyn");
		return new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["Configuration"] = configuration,
			["Version"] = productVersion,
			["CommitHash"] = commitHash,
			["TargetFramework"] = "net10.0",
			["Platform"] = "AnyCPU",
			["BaseIntermediateOutputPath"] = Path.Combine(authorityRoot, "obj") + Path.DirectorySeparatorChar,
			["IntermediateOutputPath"] = Path.Combine(authorityRoot, "obj/net10.0") + Path.DirectorySeparatorChar,
			["BaseOutputPath"] = Path.Combine(authorityRoot, "base-bin") + Path.DirectorySeparatorChar,
			["OutputPath"] = Path.Combine(authorityRoot, "bin") + Path.DirectorySeparatorChar,
			["PathMap"] = $"{Path.Combine(authorityRoot, "generated")}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{configuration}/net10.0/," +
				$"{Path.Combine(authorityRoot, "obj/net10.0")}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{configuration}/net10.0/," +
				$"{productionRoot}{Path.DirectorySeparatorChar}=WalletWasabi",
			["DefaultExcludesInProjectFolder"] = "bin/**;obj/**;**/.*/**",
			["MSBuildProjectExtensionsPath"] = Path.Combine(productionRoot, "obj") + Path.DirectorySeparatorChar,
			["ProjectAssetsFile"] = Path.Combine(productionRoot, "obj/project.assets.json"),
			["BuildProjectReferences"] = "false",
			["UseSharedCompilation"] = "false",
			["UseHostCompilerIfAvailable"] = "false",
			["ProvideCommandLineArgs"] = "true",
			["EmitCompilerGeneratedFiles"] = "true",
			["CompilerGeneratedFilesOutputPath"] = Path.Combine(authorityRoot, "generated"),
			["RestoreDuringBuild"] = "false",
			["RestorePackagesPath"] = packageRoot,
			["NuGetPackageRoot"] = packageRoot + Path.DirectorySeparatorChar,
			["DisableImplicitNuGetFallbackFolder"] = "true",
			["ImportDirectoryBuildProps"] = "true",
			["DirectoryBuildPropsPath"] = Path.Combine(repositoryRoot, "Directory.Build.props"),
			["ImportDirectoryBuildTargets"] = "false",
			["DirectoryBuildTargetsPath"] = "",
			["CustomBeforeDirectoryBuildProps"] = "",
			["CustomAfterDirectoryBuildProps"] = "",
			["CustomBeforeDirectoryBuildTargets"] = "",
			["CustomAfterDirectoryBuildTargets"] = "",
			["ImportProjectExtensionProps"] = "true",
			["ImportProjectExtensionTargets"] = "true",
			["ImportByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["CustomBeforeMicrosoftCommonProps"] = "",
			["CustomAfterMicrosoftCommonProps"] = "",
			["CustomBeforeMicrosoftCommonTargets"] = "",
			["CustomAfterMicrosoftCommonTargets"] = "",
			["CustomBeforeMicrosoftCSharpTargets"] = "",
			["CustomAfterMicrosoftCSharpTargets"] = "",
			["MSBuildUserExtensionsPath"] = Path.Combine(authorityRoot, "disabled-imports"),
			["MSBuildSDKsPath"] = Path.Combine(sdkRoot, "Sdks"),
			["RoslynTargetsPath"] = roslynRoot,
			["CSharpCoreTargetsPath"] = Path.Combine(roslynRoot, "Microsoft.CSharp.Core.targets"),
			["CscToolPath"] = "",
			["CscToolExe"] = "",
			["MSBuildDisableAllAutoResponseFiles"] = "true",
		};
	}

	private static IReadOnlyDictionary<string, string> CreateExpectedChildEnvironment(
		string dotnetRoot,
		string childHome,
		string childTemp) =>
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["DOTNET_ROOT"] = dotnetRoot,
			["DOTNET_MULTILEVEL_LOOKUP"] = "0",
			["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
			["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
			["DOTNET_NOLOGO"] = "1",
			["MSBUILDDISABLENODEREUSE"] = "1",
			["HOME"] = childHome,
			["TMPDIR"] = childTemp,
		};

	private static string[] CreateExpectedInvocationArguments(
		string projectPath,
		IReadOnlyList<string> queriedProperties,
		IReadOnlyDictionary<string, string> globalProperties,
		string binaryLog,
		string diagnosticLog)
	{
		var result = new List<string>
		{
			"msbuild",
			projectPath,
			"-target:Rebuild",
			"-noAutoResponse",
			"-getProperty:" + string.Join(',', queriedProperties),
			"-getItem:Compile,Analyzer,ReferencePathWithRefAssemblies,AdditionalFiles," +
				"EditorConfigFiles,EmbeddedFiles,CscCommandLineArgs",
			$"-binaryLogger:{binaryLog};ProjectImports=None",
			"-fileLogger",
			$"-fileLoggerParameters:LogFile={diagnosticLog};Verbosity=diagnostic;Encoding=UTF-8",
		};
		result.AddRange(globalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => $"-property:{pair.Key}={EscapeMsbuildPropertyValue(pair.Value)}"));
		result.Add("-nologo");
		result.Add("-verbosity:quiet");
		return result.ToArray();
	}

	private static EvaluatedBuildItem[] ReadEvaluatedItems(
		JsonDocument document,
		string itemName,
		bool requireFile)
	{
		var result = new List<EvaluatedBuildItem>();
		foreach (JsonElement item in document.RootElement
			.GetProperty("Items")
			.GetProperty(itemName)
			.EnumerateArray())
		{
			string identity = item.GetProperty("Identity").GetString() ?? "";
			Assert.False(string.IsNullOrWhiteSpace(identity), $"{itemName} has an empty identity.");
			var metadata = item.EnumerateObject()
				.Where(property => property.Name != "Identity")
				.ToDictionary(
					property => property.Name,
					property => property.Value.GetString() ?? property.Value.ToString(),
					StringComparer.Ordinal);
			string fullPath = metadata.TryGetValue("FullPath", out string? capturedFullPath) &&
				!string.IsNullOrWhiteSpace(capturedFullPath)
					? Path.GetFullPath(capturedFullPath)
					: "";
			if (requireFile)
			{
				Assert.False(string.IsNullOrWhiteSpace(fullPath), $"{itemName} has no FullPath: {identity}");
				Assert.True(File.Exists(fullPath), $"{itemName} input does not exist: {fullPath}");
			}
			string definingProject = metadata.TryGetValue(
				"DefiningProjectFullPath",
				out string? capturedDefiningProject) && !string.IsNullOrWhiteSpace(capturedDefiningProject)
					? Path.GetFullPath(capturedDefiningProject)
					: "";
			result.Add(new EvaluatedBuildItem(identity, fullPath, definingProject, metadata));
		}

		return result.ToArray();
	}

	private static void AssertCompilerArgumentsCoverInputs(
		IReadOnlyList<string> arguments,
		string projectRoot,
		params EvaluatedBuildItem[][] inventories)
	{
		Assert.NotEmpty(arguments);
		Assert.DoesNotContain(arguments, argument => argument.StartsWith('@'));
		Assert.Equal(
			inventories[0].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "source").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[1].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/analyzer:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[2].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/reference:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[3].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/additionalfile:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		Assert.Equal(
			inventories[4].Select(item => NormalizeMacTemporaryAlias(item.FullPath)).Order(StringComparer.Ordinal),
			GetCompilerArgumentPaths(arguments, projectRoot, "/analyzerconfig:").Select(NormalizeMacTemporaryAlias).Order(StringComparer.Ordinal));
		string[] embeddedArguments = GetCompilerArgumentPaths(arguments, projectRoot, "/embed:");
		Assert.All(inventories[5], item =>
		{
			string expected = NormalizeMacTemporaryAlias(item.FullPath);
			string[] actual = embeddedArguments.Select(NormalizeMacTemporaryAlias).ToArray();
			Assert.True(
				actual.Contains(expected, StringComparer.Ordinal),
				$"Expected embed {Convert.ToHexString(Encoding.UTF8.GetBytes(expected))}; actual " +
				string.Join(',', actual.Select(value => Convert.ToHexString(Encoding.UTF8.GetBytes(value)))));
		});
	}

	private static string[] GetCompilerArgumentPaths(
		IEnumerable<string> arguments,
		string projectRoot,
		string category)
	{
		var result = new List<string>();
		foreach (string raw in arguments)
		{
			string argument = raw.Trim().Trim('"');
			string[] values;
			if (category == "source")
			{
				if (!argument.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
					argument.StartsWith("/reference:", StringComparison.Ordinal) ||
					argument.StartsWith("/analyzer:", StringComparison.Ordinal) ||
					argument.StartsWith("/additionalfile:", StringComparison.Ordinal) ||
					argument.StartsWith("/analyzerconfig:", StringComparison.Ordinal) ||
					argument.StartsWith("/embed:", StringComparison.Ordinal))
				{
					continue;
				}
				values = [argument];
			}
			else
			{
				if (!argument.StartsWith(category, StringComparison.Ordinal))
				{
					continue;
				}
				string valueList = argument[category.Length..];
				values = category is "/reference:" or "/analyzer:"
					? valueList.Split(',', StringSplitOptions.RemoveEmptyEntries)
					: [valueList];
			}

			foreach (string rawValue in values)
			{
				string value = rawValue.Trim().Trim('"');
				if (category == "/reference:" && value.Contains('='))
				{
					value = value[(value.IndexOf('=') + 1)..];
				}
				result.Add(Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectRoot, value)));
			}
		}
		return result.ToArray();
	}

	private static string NormalizeMacTemporaryAlias(string path) =>
		OperatingSystem.IsMacOS() && path.StartsWith("/private/var/", StringComparison.Ordinal)
			? path[8..]
			: path;

	private static string AssertCscDiagnosticAuthority(
		string diagnostic,
		string dotnetRoot,
		string generatedRoot,
		string intermediateOutputPath)
	{
		Match[] taskAssemblies = Regex.Matches(
			diagnostic,
			"Using \"Csc\" task from assembly \"(?<path>[^\"]+)\"\\.")
			.Cast<Match>()
			.ToArray();
		Match taskAssemblyMatch = Assert.Single(taskAssemblies);
		string taskAssembly = Path.GetFullPath(taskAssemblyMatch.Groups["path"].Value);
		Assert.Equal(
			Path.Combine(dotnetRoot, "sdk/10.0.100/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll"),
			taskAssembly);
		Match[] starts = Regex.Matches(diagnostic, "Task \"Csc\" \\(TaskId:(?<id>[0-9]+)\\)")
			.Cast<Match>()
			.ToArray();
		string taskId = Assert.Single(starts).Groups["id"].Value;
		Assert.Single(Regex.Matches(
			diagnostic,
			$"Done executing task \"Csc\"\\. \\(TaskId:{taskId}\\)").Cast<Match>());
		string csc = Path.Combine(dotnetRoot, "sdk/10.0.100/Roslyn/bincore/csc");
		string[] requiredParameters =
		[
			$"Task Parameter:GeneratedFilesOutputPath={generatedRoot} (TaskId:{taskId})",
			$"Task Parameter:UseSharedCompilation=False (TaskId:{taskId})",
			$"Task Parameter:ProvideCommandLineArgs=True (TaskId:{taskId})",
			$"Task Parameter:UseHostCompilerIfAvailable=False (TaskId:{taskId})",
			$"Task Parameter:OutputAssembly={Path.Combine(intermediateOutputPath, "WalletWasabi.dll")} (TaskId:{taskId})",
			$"Setting DOTNET_ROOT to '{dotnetRoot}' (TaskId:{taskId})",
			$"CompilerServer: tool - using command line tool by design '{csc}' - WalletWasabi (net10.0) (TaskId:{taskId})",
		];
		Assert.All(requiredParameters, expected => Assert.Contains(expected, diagnostic, StringComparison.Ordinal));
		Assert.DoesNotContain("NUGET_PACKAGES=", diagnostic, StringComparison.OrdinalIgnoreCase);
		Assert.Single(diagnostic.Split('\n'), line =>
			line.TrimStart().StartsWith(csc + " /noconfig ", StringComparison.Ordinal) &&
			line.Contains($"(TaskId:{taskId})", StringComparison.Ordinal));
		return ($"TASK|{NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, taskAssembly))}|{Sha256File(taskAssembly)}\n" +
			$"COMPILER|{NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, csc))}|{Sha256File(csc)}\n" +
			string.Join('\n', requiredParameters.Select(parameter => NormalizeAuthorityString(
				parameter,
				("{DOTNET}", dotnetRoot),
				("{GENERATED}", generatedRoot),
				("{INTERMEDIATE}", intermediateOutputPath)))) + "\n")
			.Replace($"TaskId:{taskId}", "TaskId:{TASK}", StringComparison.Ordinal);
	}

	private static BinaryBuildTrace ReadAndAssertBinaryBuildTrace(
		string binaryLog,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string authorityRoot,
		string projectPath,
		string projectAssetsFile)
	{
		Assert.True(File.Exists(binaryLog), "The Rebuild binary trace is absent.");
		var starts = new Dictionary<BuildContextKey, TaskStartedEventArgs>();
		var finishes = new Dictionary<BuildContextKey, TaskFinishedEventArgs>();
		var parameters = new Dictionary<BuildContextKey, List<TaskParameterEventArgs>>();
		var imports = new List<ProjectImportedEventArgs>();
		var errors = new List<string>();
		using var compressedLog = new GZipStream(File.OpenRead(binaryLog), CompressionMode.Decompress);
		using var binaryReader = new BinaryReader(compressedLog, Encoding.UTF8, leaveOpen: false);
		using BuildEventArgsReader reader = BinaryLogReplayEventSource.OpenBuildEventsReader(
			binaryReader,
			closeInput: true,
			allowForwardCompatibility: false);
		reader.SkipUnknownEvents = false;
		reader.SkipUnknownEventParts = false;
		reader.RecoverableReadError += error => errors.Add(error.ToString() ?? "Unknown recoverable binlog error.");
		while (reader.Read() is BuildEventArgs buildEvent)
		{
			switch (buildEvent)
			{
				case ProjectImportedEventArgs imported:
					imports.Add(imported);
					break;
				case TaskStartedEventArgs started when StringComparer.Ordinal.Equals(started.TaskName, "Csc"):
					starts.Add(GetBuildContext(started), started);
					break;
				case TaskParameterEventArgs parameter:
					BuildContextKey parameterContext = GetBuildContext(parameter);
					if (!parameters.TryGetValue(parameterContext, out List<TaskParameterEventArgs>? values))
					{
						values = [];
						parameters.Add(parameterContext, values);
					}
					values.Add(parameter);
					break;
				case TaskFinishedEventArgs finished when StringComparer.Ordinal.Equals(finished.TaskName, "Csc"):
					finishes.Add(GetBuildContext(finished), finished);
					break;
			}
		}
		Assert.Empty(errors);
		BuildContextKey cscContext = Assert.Single(starts.Keys);
		TaskStartedEventArgs cscStart = starts[cscContext];
		TaskFinishedEventArgs cscFinish = Assert.Single(finishes, pair => pair.Key == cscContext).Value;
		Assert.True(cscFinish.Succeeded, "The exact Csc task captured in the Rebuild trace did not succeed.");
		string expectedTaskAssembly = Path.Combine(
			dotnetRoot,
			"sdk/10.0.100/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll");
		Assert.Equal(expectedTaskAssembly, Path.GetFullPath(cscStart.TaskAssemblyLocation));
		Assert.Equal(Path.GetFullPath(projectPath), Path.GetFullPath(cscStart.ProjectFile));
		TaskParameterEventArgs[] cscParameters = parameters.GetValueOrDefault(cscContext, []).ToArray();
		TaskParameterEventArgs commandLine = Assert.Single(cscParameters, parameter =>
			parameter.Kind == TaskParameterMessageKind.TaskOutput &&
			StringComparer.Ordinal.Equals(parameter.ParameterName, "CommandLineArgs") &&
			StringComparer.Ordinal.Equals(parameter.ItemType, "CscCommandLineArgs"));
		string[] orderedArgs = commandLine.Items.Cast<object>().Select(GetBuildItemSpec).ToArray();
		Assert.NotEmpty(orderedArgs);
		TaskParameterEventArgs[] inputs = cscParameters
			.Where(parameter => parameter.Kind == TaskParameterMessageKind.TaskInput)
			.ToArray();
		Assert.NotEmpty(inputs);
		var taskInputs = inputs
			.Where(input => input.Items.Cast<object>().Any())
			.GroupBy(input => input.ParameterName ?? input.PropertyName ?? input.ItemType ?? "", StringComparer.Ordinal)
			.ToDictionary(
				group => group.Key,
				group => Assert.Single(group).Items.Cast<object>().Select(GetBuildItemSpec).ToArray(),
				StringComparer.Ordinal);
		Assert.Contains("Sources", taskInputs.Keys);
		Assert.Contains("Analyzers", taskInputs.Keys);
		Assert.Contains("References", taskInputs.Keys);

		var paths = new List<string>();
		var rows = new List<string>();
		for (int index = 0; index < imports.Count; index++)
		{
			ProjectImportedEventArgs imported = imports[index];
			string importedPath = imported.ImportedProjectFile ?? "";
			string rowPrefix = $"IMPORT_EVENT|{index:D3}|IGNORED|{imported.ImportIgnored}|UNEXPANDED|{imported.UnexpandedProject}|" +
				$"SOURCE|{NormalizeOptionalAuthorityPath(imported.ProjectFile, repositoryRoot, dotnetRoot, packageAuthority)}|" +
				$"LOCATION|{imported.LineNumber}:{imported.ColumnNumber}";
			if (string.IsNullOrWhiteSpace(importedPath))
			{
				rows.Add(rowPrefix + "|RESOLVED|EMPTY");
				continue;
			}
			string path = Path.GetFullPath(importedPath);
			AssertRegularAuthorityFile(path, "captured import");
			paths.Add(path);
			rows.Add(rowPrefix + $"|RESOLVED|{NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority)}|SHA256|" +
				GetBuildAuthorityFileSha256(
					path,
					projectAssetsFile,
					repositoryRoot,
					dotnetRoot,
					packageAuthority));
		}
		Assert.NotEmpty(imports);

		string[] independentlyPinned =
		[
			Path.Combine(repositoryRoot, "global.json"),
			Path.Combine(repositoryRoot, "Directory.Build.props"),
			Path.Combine(repositoryRoot, "Directory.Packages.props"),
			projectPath,
			Path.Combine(Path.GetDirectoryName(projectPath)!, "packages.lock.json"),
			projectAssetsFile,
			Path.Combine(Path.GetDirectoryName(projectAssetsFile)!, "WalletWasabi.csproj.nuget.g.props"),
			Path.Combine(Path.GetDirectoryName(projectAssetsFile)!, "WalletWasabi.csproj.nuget.g.targets"),
		];
		foreach (string path in independentlyPinned)
		{
			AssertRegularAuthorityFile(path, "pinned build-authority file");
			rows.Add($"PIN|{NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority)}|" +
				GetBuildAuthorityFileSha256(
					path,
					projectAssetsFile,
					repositoryRoot,
					dotnetRoot,
					packageAuthority));
		}
		string[] cscInputRows = inputs
			.OrderBy(input => input.ParameterName ?? "", StringComparer.Ordinal)
			.ThenBy(input => input.PropertyName ?? "", StringComparer.Ordinal)
			.ThenBy(input => input.ItemType ?? "", StringComparer.Ordinal)
			.Select((input, index) =>
			$"CSC_INPUT|{index:D3}|{input.ParameterName}|{input.PropertyName}|{input.ItemType}|" +
			string.Join('|', input.Items.Cast<object>().Select(item => NormalizeAuthorityStringWithPackages(
				GetBuildItemSpec(item),
				packageAuthority,
				("{REPO}", repositoryRoot),
				("{DOTNET}", dotnetRoot),
				("{AUTHORITY}", authorityRoot))))).ToArray();
		string[] cscArgumentRows = orderedArgs.Select((argument, index) =>
			$"CSC_ARG|{index:D4}|" + NormalizeAuthorityStringWithPackages(
				argument,
				packageAuthority,
				("{REPO}", repositoryRoot),
				("{DOTNET}", dotnetRoot),
				("{AUTHORITY}", authorityRoot))).ToArray();
		string cscManifest = $"CSC_START|{NormalizeAuthorityPath(cscStart.TaskAssemblyLocation, repositoryRoot, dotnetRoot, packageAuthority)}|" +
			$"{Sha256File(cscStart.TaskAssemblyLocation)}\n" + string.Join('\n', cscInputRows) + "\n" +
			string.Join('\n', cscArgumentRows) + "\n";
		return new BinaryBuildTrace(
			orderedArgs,
			taskInputs,
			paths.ToArray(),
			string.Join('\n', rows) + "\n",
			cscManifest);
	}

	private static string GetBuildItemSpec(object item)
	{
		if (item is ITaskItem taskItem)
		{
			return taskItem.ItemSpec;
		}
		PropertyInfo? itemSpec = item.GetType().GetProperty("ItemSpec", BindingFlags.Public | BindingFlags.Instance);
		return itemSpec?.GetValue(item)?.ToString() ??
			throw new Xunit.Sdk.XunitException($"Build trace item exposes no ItemSpec: {item.GetType().FullName}");
	}

	private static void AssertCscTaskInputsMatchArguments(BinaryBuildTrace trace, string projectRoot)
	{
		foreach ((string parameter, string category) in new[]
		{
			("Sources", "source"),
			("Analyzers", "/analyzer:"),
			("References", "/reference:"),
			("AdditionalFiles", "/additionalfile:"),
			("AnalyzerConfigFiles", "/analyzerconfig:"),
			("EmbeddedFiles", "/embed:"),
		})
		{
			string[] expected = trace.TaskInputs.TryGetValue(parameter, out string[]? items)
				? items.Select(item => Path.GetFullPath(
					Path.IsPathRooted(item.Trim('"'))
						? item.Trim('"')
						: Path.Combine(projectRoot, item.Trim('"')))).ToArray()
				: [];
			Assert.Equal(
				expected.Order(StringComparer.Ordinal),
				GetCompilerArgumentPaths(trace.CommandLineArgs, projectRoot, category).Order(StringComparer.Ordinal));
		}
	}

	private static BuildContextKey GetBuildContext(BuildEventArgs buildEvent)
	{
		BuildEventContext context = buildEvent.BuildEventContext ??
			throw new Xunit.Sdk.XunitException("Build event has no context.");
		return new BuildContextKey(
			context.NodeId,
			context.ProjectContextId,
			context.TargetId,
			context.TaskId,
			context.SubmissionId,
			context.ProjectInstanceId,
			context.EvaluationId);
	}

	private static string NormalizeOptionalAuthorityPath(
		string? path,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority) =>
		string.IsNullOrWhiteSpace(path)
			? "EMPTY"
			: NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority);

	private static string BuildReferenceAuthorityManifest(
		IEnumerable<EvaluatedBuildItem> references,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority) =>
		string.Join(
			'\n',
			references.Select((reference, index) =>
			{
				string provenance = string.IsNullOrEmpty(reference.DefiningProjectFullPath)
					? "NONE"
					: NormalizeAuthorityPath(
						reference.DefiningProjectFullPath,
						repositoryRoot,
						dotnetRoot,
						packageAuthority);
				return $"REFERENCE|{index:D3}|{NormalizeAuthorityPath(reference.FullPath, repositoryRoot, dotnetRoot, packageAuthority)}|" +
					$"{Sha256File(reference.FullPath)}|PROVENANCE|{provenance}|ALIASES|" +
					(reference.Metadata.TryGetValue("Aliases", out string? aliases) ? aliases : "");
			})) + "\n";

	private static string BuildCompilerInputAuthorityManifest(
		IReadOnlyList<string> arguments,
		EvaluatedBuildItem[] compile,
		EvaluatedBuildItem[] analyzers,
		EvaluatedBuildItem[] references,
		EvaluatedBuildItem[] additionalFiles,
		EvaluatedBuildItem[] editorConfigs,
		EvaluatedBuildItem[] embeddedFiles,
		string projectRoot,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string authorityRoot)
	{
		var rows = arguments.Select((argument, index) =>
			$"ARG|{index:D4}|{NormalizeAuthorityStringWithPackages(argument, packageAuthority, ("{REPO}", repositoryRoot), ("{DOTNET}", dotnetRoot), ("{AUTHORITY}", authorityRoot))}")
			.ToList();
		foreach ((string category, EvaluatedBuildItem[] items) in new[]
		{
			("SOURCE", compile),
			("ANALYZER", analyzers),
			("REFERENCE", references),
			("ADDITIONAL", additionalFiles),
			("ANALYZERCONFIG", editorConfigs),
			("EMBED", embeddedFiles),
		})
		{
			rows.AddRange(items.Select((item, index) =>
				$"{category}|{index:D4}|{NormalizeAuthorityPath(item.FullPath, repositoryRoot, dotnetRoot, packageAuthority, authorityRoot)}|" +
				GetCompilerInputAuthoritySha256(item.FullPath, authorityRoot)));
		}

		foreach (string analyzerDirectory in analyzers
			.Select(item => Path.GetDirectoryName(item.FullPath)!)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal))
		{
			rows.AddRange(Directory.EnumerateFiles(analyzerDirectory, "*.dll", SearchOption.TopDirectoryOnly)
				.Order(StringComparer.Ordinal)
				.Select(path => $"ANALYZER_DEP|{NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority)}|{Sha256File(path)}"));
		}

		string[] auxiliaryPrefixes =
		[
			"/ruleset:", "/appconfig:", "/keyfile:", "/win32icon:", "/win32res:",
			"/win32manifest:", "/sourcelink:", "/resource:", "/linkresource:", "/addmodule:",
		];
		foreach (string argument in arguments)
		{
			string? prefix = auxiliaryPrefixes.FirstOrDefault(candidate => argument.StartsWith(candidate, StringComparison.Ordinal));
			if (prefix is null)
			{
				continue;
			}
			string value = argument[prefix.Length..].Trim('"').Split(',')[0];
			string path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectRoot, value));
			Assert.True(File.Exists(path), $"Compiler auxiliary input is absent: {path}");
			rows.Add($"AUX|{prefix}|{NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority, authorityRoot)}|{Sha256File(path)}");
		}
		return string.Join('\n', rows) + "\n";
	}

	private static string GetCompilerInputAuthoritySha256(string path, string authorityRoot)
	{
		string fullPath = Path.GetFullPath(path);
		string relativePath = NormalizeRelativePath(Path.GetRelativePath(authorityRoot, fullPath));
		if (!StringComparer.Ordinal.Equals(relativePath, "obj/net10.0/WalletWasabi.AssemblyInfo.cs"))
		{
			return Sha256File(fullPath);
		}

		AssertRegularAuthorityFile(fullPath, "generated product assembly identity");
		Assembly productAssembly = typeof(LiquidOrdinaryWalletPlanEncoder).Assembly;
		string assemblyVersion = productAssembly.GetName().Version?.ToString() ??
			throw new Xunit.Sdk.XunitException("The loaded product assembly version is absent.");
		string fileVersion = Assert.Single(
			productAssembly.GetCustomAttributes<AssemblyFileVersionAttribute>()).Version;
		string informationalVersion = Assert.Single(
			productAssembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>()).InformationalVersion;
		(string _, string commitHash) = GetLoadedProductBuildIdentity();
		string canonical = File.ReadAllText(fullPath);
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyFileVersionAttribute(\"{fileVersion}\")",
			"System.Reflection.AssemblyFileVersionAttribute(\"{FILE_VERSION}\")");
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyInformationalVersionAttribute(\"{informationalVersion}\")",
			"System.Reflection.AssemblyInformationalVersionAttribute(\"{INFORMATIONAL_VERSION}\")");
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyVersionAttribute(\"{assemblyVersion}\")",
			"System.Reflection.AssemblyVersionAttribute(\"{ASSEMBLY_VERSION}\")");
		canonical = ReplaceExactGeneratedAssemblyIdentity(
			canonical,
			$"System.Reflection.AssemblyMetadata(\"CommitHash\", \"{commitHash}\")",
			"System.Reflection.AssemblyMetadata(\"CommitHash\", \"{COMMIT_HASH}\")");
		return Sha256Text(canonical);
	}

	private static string ReplaceExactGeneratedAssemblyIdentity(
		string source,
		string expected,
		string replacement)
	{
		Assert.Equal(2, source.Split(expected, StringSplitOptions.None).Length);
		return source.Replace(expected, replacement, StringComparison.Ordinal);
	}

	private static string BuildToolchainAuthorityManifest(string dotnetHost, string dotnetRoot)
	{
		string sdkRoot = Path.Combine(dotnetRoot, "sdk/10.0.100");
		string hostFxrRoot = Path.Combine(dotnetRoot, "host/fxr/10.0.0");
		string sharedRuntimeRoot = Path.Combine(dotnetRoot, "shared/Microsoft.NETCore.App/10.0.0");
		AssertExactArtifactBytes(
			File.ReadAllBytes(Path.Combine(sdkRoot, "Microsoft.Build.dll")),
			File.ReadAllBytes(typeof(BinaryLogReplayEventSource).Assembly.Location));
		AssertExactArtifactBytes(
			File.ReadAllBytes(Path.Combine(sdkRoot, "Microsoft.Build.Framework.dll")),
			File.ReadAllBytes(typeof(BuildEventArgs).Assembly.Location));
		string[] files = new[] { dotnetHost }.Concat(new[] { sdkRoot, hostFxrRoot, sharedRuntimeRoot }
			.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)))
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		return string.Join('\n', files.Select(path =>
		{
			Assert.True(File.Exists(path), $"Pinned toolchain dependency is absent: {path}");
			return $"TOOL|{NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, path))}|{Sha256File(path)}";
		})) + "\n";
	}

	private static void AssertConfiguredAuthorityHashes(
		string importManifest,
		string referenceManifest,
		string compilerManifest,
		string toolchainManifest)
	{
#if DEBUG
		string expectedImport = ExpectedDebugImportClosureSha256;
		string expectedReferences = ExpectedDebugReferenceAuthoritySha256;
		string expectedCompiler = ExpectedDebugCompilerInputAuthoritySha256;
#else
		string expectedImport = ExpectedReleaseImportClosureSha256;
		string expectedReferences = ExpectedReleaseReferenceAuthoritySha256;
		string expectedCompiler = ExpectedReleaseCompilerInputAuthoritySha256;
#endif
		AssertExactSha256(expectedImport, importManifest);
		AssertExactSha256(expectedReferences, referenceManifest);
		AssertExactSha256(expectedCompiler, compilerManifest);
		AssertExactSha256(ExpectedToolchainDependencyAuthoritySha256, toolchainManifest);
	}

	private static string NormalizeAuthorityPath(
		string path,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		string? authorityRoot = null)
	{
		string fullPath = Path.GetFullPath(path);
		if (TryNormalizePackageAuthorityPath(fullPath, packageAuthority, out string normalizedPackagePath))
		{
			return normalizedPackagePath;
		}
		foreach ((string token, string root) in new[]
		{
			("REPO", repositoryRoot),
			("DOTNET", dotnetRoot),
			("AUTHORITY", authorityRoot ?? Path.Combine(Path.GetTempPath(), "authority-not-present")),
		})
		{
			if (IsPathWithin(fullPath, root) || StringComparer.Ordinal.Equals(fullPath, Path.GetFullPath(root)))
			{
				return $"{token}|{NormalizeRelativePath(Path.GetRelativePath(root, fullPath))}";
			}
		}
		throw new Xunit.Sdk.XunitException($"Authority path is outside all pinned roots: {fullPath}");
	}

	private static string NormalizeAuthorityString(
		string value,
		params (string Token, string Root)[] roots)
	{
		string normalized = value.Replace('\\', '/');
		foreach ((string token, string root) in roots.OrderByDescending(pair => pair.Root.Length))
		{
			normalized = ReplaceAuthorityRoot(normalized, root, token);
		}
		return normalized;
	}

	private static string ReplaceAuthorityRoot(string value, string root, string token)
	{
		string normalizedRoot = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
		StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var result = new StringBuilder(value.Length);
		int copied = 0;
		int search = 0;
		while (search < value.Length)
		{
			int match = value.IndexOf(normalizedRoot, search, comparison);
			if (match < 0)
			{
				break;
			}
			int end = match + normalizedRoot.Length;
			bool validStart = match == 0 || IsAuthorityValueBoundary(value[match - 1]);
			bool validEnd = end == value.Length || value[end] == '/' || IsAuthorityValueBoundary(value[end]);
			if (!validStart || !validEnd)
			{
				search = match + 1;
				continue;
			}
			result.Append(value, copied, match - copied);
			result.Append(token);
			copied = end;
			search = end;
		}
		result.Append(value, copied, value.Length - copied);
		return result.ToString();
	}

	private static bool IsAuthorityValueBoundary(char value) =>
		char.IsWhiteSpace(value) || value is '"' or '\'' or '=' or ':' or ';' or ',' or '(' or ')' or '[' or ']';

	private static void AssertExactArtifactBytes(byte[] inspectedAssembly, byte[] rebuiltAssembly)
	{
		Assert.NotEmpty(inspectedAssembly);
		Assert.Equal(inspectedAssembly, rebuiltAssembly);
	}

	private static void AssertExactChildGlobalProperties(
		IReadOnlyDictionary<string, string> actual,
		IReadOnlyDictionary<string, string> expected)
	{
		Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
		Assert.Equal("false", actual["ImportDirectoryBuildTargets"]);
		Assert.Equal("", actual["DirectoryBuildTargetsPath"]);
		Assert.Equal("", actual["CustomBeforeMicrosoftCommonTargets"]);
		Assert.Equal("", actual["CustomAfterMicrosoftCommonTargets"]);
		Assert.Equal("", actual["CustomBeforeMicrosoftCSharpTargets"]);
		Assert.Equal("", actual["CustomAfterMicrosoftCSharpTargets"]);
		Assert.Equal("false", actual["UseSharedCompilation"]);
		Assert.Equal("true", actual["ProvideCommandLineArgs"]);
		Assert.Equal("true", actual["EmitCompilerGeneratedFiles"]);
		Assert.Equal("true", actual["MSBuildDisableAllAutoResponseFiles"]);
	}

	private static void AssertExactChildEnvironment(
		IReadOnlyDictionary<string, string> actual,
		IReadOnlyDictionary<string, string> expected)
	{
		Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
		Assert.DoesNotContain(actual.Keys, name =>
			name.Equals("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("CscToolPath", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("DirectoryBuildTargetsPath", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("MSBuildProject", StringComparison.OrdinalIgnoreCase));
		Assert.All(actual.Keys, name => Assert.DoesNotContain('=', name));
	}

	private static void AssertExactInvocationArguments(
		IReadOnlyList<string> actual,
		IReadOnlyList<string> expected)
	{
		Assert.Equal(expected, actual);
		Assert.Single(actual, argument => argument == "-target:Rebuild");
		Assert.Single(actual, argument => argument == "-noAutoResponse");
		Assert.DoesNotContain(actual, argument => argument.StartsWith('@'));
		Assert.DoesNotContain(actual, argument => argument.Contains("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase));
	}

	private static string Sha256File(string path)
	{
		AssertRegularAuthorityFile(path, "hashed authority file");
		return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
	}

	private static string GetBuildAuthorityFileSha256(
		string path,
		string projectAssetsFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string fullPath = Path.GetFullPath(path);
		string fullAssetsPath = Path.GetFullPath(projectAssetsFile);
		if (PackagePathComparer.Equals(fullPath, fullAssetsPath))
		{
			return Sha256Text(BuildProjectAssetsSemanticManifest(
				fullPath,
				repositoryRoot,
				dotnetRoot,
				packageAuthority));
		}

		string fileName = Path.GetFileName(fullPath);
		if (PackagePathComparer.Equals(Path.GetDirectoryName(fullPath), Path.GetDirectoryName(fullAssetsPath)) &&
			(fileName.EndsWith(".nuget.g.props", StringComparison.Ordinal) ||
			 fileName.EndsWith(".nuget.g.targets", StringComparison.Ordinal)))
		{
			return Sha256Text(BuildGeneratedNuGetSemanticManifest(
				fullPath,
				repositoryRoot,
				dotnetRoot,
				packageAuthority));
		}

		return Sha256File(fullPath);
	}

	private static string BuildProjectAssetsSemanticManifest(
		string projectAssetsFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		AssertRegularAuthorityFile(projectAssetsFile, "semantic project assets authority");
		using JsonDocument document = JsonDocument.Parse(
			File.ReadAllText(projectAssetsFile),
			new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 });
		JsonElement root = document.RootElement;
		Assert.Equal(JsonValueKind.Object, root.ValueKind);
		AssertExactJsonProperties(
			root,
			["version", "targets", "libraries", "projectFileDependencyGroups", "packageFolders", "project"]);
		Assert.Equal(3, root.GetProperty("version").GetInt32());
		AssertProjectAssetsDependencyAuthority(root);
		AssertProjectAssetsPackageTopology(root, packageAuthority);
		AssertProjectAssetsFallbackFolderTopology(root, packageAuthority);

		var manifest = new StringBuilder();
		AppendCanonicalProjectAssetsJson(
			manifest,
			root,
			"$",
			repositoryRoot,
			dotnetRoot,
			packageAuthority);
		return manifest.ToString();
	}

	private static void AssertProjectAssetsDependencyAuthority(JsonElement root)
	{
		JsonElement libraries = root.GetProperty("libraries");
		Assert.Equal(JsonValueKind.Object, libraries.ValueKind);
		var identities = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty library in libraries.EnumerateObject())
		{
			Assert.True(identities.Add(library.Name), $"Duplicate project-assets library identity: {library.Name}");
			int separator = library.Name.LastIndexOf('/');
			Assert.True(separator > 0 && separator < library.Name.Length - 1, $"Invalid library identity: {library.Name}");
			Assert.Equal(JsonValueKind.Object, library.Value.ValueKind);
			Assert.Equal("package", library.Value.GetProperty("type").GetString());
			string contentHash = Assert.IsType<string>(library.Value.GetProperty("sha512").GetString());
			Assert.Equal(64, Convert.FromBase64String(contentHash).Length);
			string packagePath = Assert.IsType<string>(library.Value.GetProperty("path").GetString());
			AssertSafePackageRelativePath(packagePath);
			Assert.Equal(library.Name.ToLowerInvariant(), packagePath);
			JsonElement files = library.Value.GetProperty("files");
			Assert.Equal(JsonValueKind.Array, files.ValueKind);
			var fileIdentities = new HashSet<string>(StringComparer.Ordinal);
			foreach (JsonElement file in files.EnumerateArray())
			{
				Assert.Equal(JsonValueKind.String, file.ValueKind);
				string relativeFile = Assert.IsType<string>(file.GetString());
				AssertSafePackageRelativePath(relativeFile);
				Assert.True(fileIdentities.Add(relativeFile), $"Duplicate package file identity: {library.Name}/{relativeFile}");
			}
			Assert.NotEmpty(fileIdentities);
		}
		Assert.NotEmpty(identities);

		JsonElement targets = root.GetProperty("targets");
		Assert.Equal(JsonValueKind.Object, targets.ValueKind);
		Assert.NotEmpty(targets.EnumerateObject());
		foreach (JsonProperty target in targets.EnumerateObject())
		{
			Assert.Equal(JsonValueKind.Object, target.Value.ValueKind);
			foreach (JsonProperty dependency in target.Value.EnumerateObject())
			{
				Assert.Contains(dependency.Name, identities);
				Assert.Equal(JsonValueKind.Object, dependency.Value.ValueKind);
				Assert.Equal("package", dependency.Value.GetProperty("type").GetString());
			}
		}
	}

	private static void AssertProjectAssetsPackageTopology(
		JsonElement root,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		JsonElement packageFolders = root.GetProperty("packageFolders");
		Assert.Equal(JsonValueKind.Object, packageFolders.ValueKind);
		int index = 0;
		foreach (JsonProperty folder in packageFolders.EnumerateObject())
		{
			Assert.True(index < packageAuthority.OrderedRoots.Length);
			Assert.True(PackagePathComparer.Equals(
				ParseCanonicalPackageRoot(folder.Name, "semantic project-assets package root"),
				packageAuthority.OrderedRoots[index]));
			Assert.Equal(JsonValueKind.Object, folder.Value.ValueKind);
			Assert.Empty(folder.Value.EnumerateObject());
			index++;
		}
		Assert.Equal(packageAuthority.OrderedRoots.Length, index);
	}

	private static void AppendCanonicalProjectAssetsJson(
		StringBuilder manifest,
		JsonElement value,
		string jsonPath,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		switch (value.ValueKind)
		{
			case JsonValueKind.Object:
				manifest.Append('{');
				JsonProperty[] properties = value.EnumerateObject().ToArray();
				SortJsonProperties(properties);
				var names = new HashSet<string>(StringComparer.Ordinal);
				bool first = true;
				foreach (JsonProperty property in properties)
				{
					Assert.True(names.Add(property.Name), $"Duplicate JSON property at {jsonPath}: {property.Name}");
					string childPath = jsonPath + "." + property.Name;
					if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.fallbackFolders"))
					{
						AssertProjectAssetsFallbackFolders(property.Value, packageAuthority);
						continue;
					}
					if (!first)
					{
						manifest.Append(',');
					}
					first = false;
					manifest.Append(JsonSerializer.Serialize(property.Name));
					manifest.Append(':');
					if (StringComparer.Ordinal.Equals(childPath, "$.packageFolders"))
					{
						manifest.Append(JsonSerializer.Serialize("{VALIDATED_PACKAGE_ROOT_TOPOLOGY}"));
					}
					else if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.configFilePaths"))
					{
						AssertProjectAssetsConfigFileTopology(property.Value, repositoryRoot);
						manifest.Append(JsonSerializer.Serialize("{VALIDATED_CONFIG_FILE_TOPOLOGY}"));
					}
					else if (StringComparer.Ordinal.Equals(childPath, "$.project.restore.sources"))
					{
						AssertProjectAssetsRestoreSources(property.Value, packageAuthority);
						manifest.Append(JsonSerializer.Serialize("{VALIDATED_RESTORE_SOURCE}"));
					}
					else
					{
						AppendCanonicalProjectAssetsJson(
							manifest,
							property.Value,
							childPath,
							repositoryRoot,
							dotnetRoot,
							packageAuthority);
					}
				}
				manifest.Append('}');
				break;
			case JsonValueKind.Array:
				manifest.Append('[');
				int index = 0;
				foreach (JsonElement item in value.EnumerateArray())
				{
					if (index != 0)
					{
						manifest.Append(',');
					}
					AppendCanonicalProjectAssetsJson(
						manifest,
						item,
						jsonPath + "[]",
						repositoryRoot,
						dotnetRoot,
						packageAuthority);
					index++;
				}
				manifest.Append(']');
				break;
			case JsonValueKind.String:
				manifest.Append(JsonSerializer.Serialize(NormalizeProjectAssetsString(
					Assert.IsType<string>(value.GetString()),
					jsonPath,
					repositoryRoot,
					dotnetRoot,
					packageAuthority)));
				break;
			case JsonValueKind.Number:
				manifest.Append(value.GetRawText());
				break;
			case JsonValueKind.True:
				manifest.Append("true");
				break;
			case JsonValueKind.False:
				manifest.Append("false");
				break;
			case JsonValueKind.Null:
				manifest.Append("null");
				break;
			default:
				throw new Xunit.Sdk.XunitException($"Unsupported JSON value at {jsonPath}: {value.ValueKind}");
		}
		}

	private static void AssertProjectAssetsConfigFileTopology(JsonElement configFiles, string repositoryRoot)
	{
		Assert.Equal(JsonValueKind.Array, configFiles.ValueKind);
		string[] paths = configFiles.EnumerateArray().Select(item =>
		{
			Assert.Equal(JsonValueKind.String, item.ValueKind);
			return Path.GetFullPath(Assert.IsType<string>(item.GetString()));
		}).ToArray();
		Assert.InRange(paths.Length, 1, 2);
		Assert.True(PackagePathComparer.Equals(
			paths[0],
			Path.GetFullPath(Path.Combine(repositoryRoot, "NuGet.Config"))));
		if (paths.Length == 2)
		{
			Assert.EndsWith(
				"/.nuget/NuGet/NuGet.Config",
				paths[1].Replace('\\', '/'),
				StringComparison.OrdinalIgnoreCase);
		}
	}

	private static void AssertProjectAssetsRestoreSources(
		JsonElement sources,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		Assert.Equal(JsonValueKind.Object, sources.ValueKind);
		JsonProperty source = Assert.Single(sources.EnumerateObject());
		Assert.Equal(JsonValueKind.Object, source.Value.ValueKind);
		Assert.Empty(source.Value.EnumerateObject());
		if (StringComparer.Ordinal.Equals(source.Name, "https://api.nuget.org/v3/index.json"))
		{
			return;
		}

		string primaryParent = Directory.GetParent(packageAuthority.PrimaryRoot)?.FullName ??
			throw new Xunit.Sdk.XunitException("The primary package root has no parent.");
		string expectedOfflineSource = Path.GetFullPath(Path.Combine(primaryParent, "source"));
		Assert.True(PackagePathComparer.Equals(Path.GetFullPath(source.Name), expectedOfflineSource));
		AssertRegularAuthorityDirectory(expectedOfflineSource, "offline restore source");
	}

	private static string NormalizeProjectAssetsString(
		string value,
		string jsonPath,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.packagesPath"))
		{
			Assert.True(PackagePathComparer.Equals(
				value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				packageAuthority.PrimaryRoot));
			return "{NUGET_PRIMARY}";
		}
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.projectUniqueName") ||
			StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.projectPath") ||
			StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.outputPath"))
		{
			return NormalizeAuthorityPath(value, repositoryRoot, dotnetRoot, packageAuthority);
		}
		if (StringComparer.Ordinal.Equals(jsonPath, "$.project.restore.configFilePaths[]"))
		{
			string normalized = Path.GetFullPath(value).Replace('\\', '/');
			const string UserConfigSuffix = "/.nuget/NuGet/NuGet.Config";
			if (normalized.EndsWith(UserConfigSuffix, StringComparison.OrdinalIgnoreCase))
			{
				return "{HOME}" + UserConfigSuffix;
			}
			return NormalizeAuthorityPath(value, repositoryRoot, dotnetRoot, packageAuthority);
		}
		if (jsonPath.StartsWith("$.project.frameworks.", StringComparison.Ordinal) &&
			jsonPath.EndsWith(".runtimeIdentifierGraphPath", StringComparison.Ordinal))
		{
			string fullPath = Path.GetFullPath(value);
			Assert.True(IsPathWithin(fullPath, dotnetRoot));
			return $"DOTNET|{NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, fullPath))}";
		}
		return value;
	}

	private static void AssertProjectAssetsFallbackFolders(
		JsonElement fallbackFolders,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		Assert.Equal(JsonValueKind.Array, fallbackFolders.ValueKind);
		int index = 1;
		foreach (JsonElement fallback in fallbackFolders.EnumerateArray())
		{
			Assert.Equal(JsonValueKind.String, fallback.ValueKind);
			Assert.True(index < packageAuthority.OrderedRoots.Length);
			Assert.True(PackagePathComparer.Equals(
				ParseCanonicalPackageRoot(Assert.IsType<string>(fallback.GetString()), "project-assets fallback root"),
				packageAuthority.OrderedRoots[index]));
			index++;
		}
		Assert.Equal(packageAuthority.OrderedRoots.Length, index);
	}

	private static string BuildGeneratedNuGetSemanticManifest(
		string generatedProjectFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		AssertRegularAuthorityFile(generatedProjectFile, "generated NuGet authority");
		var settings = new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit,
			IgnoreComments = false,
			IgnoreProcessingInstructions = false,
			XmlResolver = null,
		};
		using XmlReader reader = XmlReader.Create(generatedProjectFile, settings);
		XDocument document = XDocument.Load(reader, LoadOptions.None);
		XElement root = Assert.IsType<XElement>(document.Root);
		Assert.All(
			document.Nodes().Where(node => !ReferenceEquals(node, root)),
			node => Assert.True(node is XText text && string.IsNullOrWhiteSpace(text.Value)));
		XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
		Assert.Equal(msbuild + "Project", root.Name);
		bool requiresSourceRoot = Path.GetFileName(generatedProjectFile)
			.EndsWith(".nuget.g.props", StringComparison.Ordinal);
		AssertGeneratedNuGetSourceRootTopology(root, msbuild, packageAuthority, requiresSourceRoot);
		var manifest = new StringBuilder();
		manifest.Append("NUGET_GENERATED|");
		manifest.Append(Path.GetFileName(generatedProjectFile));
		manifest.Append('|');
		AppendCanonicalGeneratedNuGetXml(
			manifest,
			root,
			msbuild,
			repositoryRoot,
			dotnetRoot,
			packageAuthority);
		return manifest.ToString();
	}

	private static void AssertGeneratedNuGetSourceRootTopology(
		XElement root,
		XNamespace msbuild,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		bool required)
	{
		XElement[] sourceRoots = root.Descendants(msbuild + "SourceRoot").ToArray();
		if (sourceRoots.Length == 0)
		{
			Assert.False(required, "Generated NuGet props must declare the validated SourceRoot topology.");
			return;
		}
		Assert.Equal(packageAuthority.OrderedRoots.Length, sourceRoots.Length);
		XElement sourceRootParent = Assert.IsType<XElement>(sourceRoots[0].Parent);
		Assert.Equal(msbuild + "ItemGroup", sourceRootParent.Name);
		Assert.Same(root, sourceRootParent.Parent);
		Assert.Equal(sourceRoots.Length, sourceRootParent.Elements().Count());
		for (int index = 0; index < sourceRoots.Length; index++)
		{
			XElement sourceRoot = sourceRoots[index];
			Assert.Same(sourceRootParent, sourceRoot.Parent);
			Assert.Same(sourceRoot, sourceRootParent.Elements().ElementAt(index));
			Assert.Empty(sourceRoot.Elements());
			Assert.True(string.IsNullOrWhiteSpace(sourceRoot.Value));
			XAttribute include = Assert.Single(sourceRoot.Attributes());
			Assert.Equal("Include", include.Name.LocalName);
			Assert.True(PackagePathComparer.Equals(
				include.Value,
				packageAuthority.OrderedRoots[index] + Path.DirectorySeparatorChar));
		}
	}

	private static void AppendCanonicalGeneratedNuGetXml(
		StringBuilder manifest,
		XElement element,
		XNamespace msbuild,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		Assert.Equal(msbuild.NamespaceName, element.Name.NamespaceName);
		Assert.NotEqual(msbuild + "VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY", element.Name);
		if (element.Name == msbuild + "SourceRoot")
		{
			if (element.ElementsBeforeSelf().Any())
			{
				return;
			}
			manifest.Append("<VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY></VALIDATED_PACKAGE_SOURCE_ROOT_TOPOLOGY>");
			return;
		}
		manifest.Append('<');
		manifest.Append(element.Name.LocalName);
		XAttribute[] attributes = element.Attributes().ToArray();
		SortXmlAttributes(attributes);
		string? importIdentity = null;
		string? importBytes = null;
		if (element.Name == msbuild + "Import")
		{
			XAttribute project = Assert.Single(attributes, attribute => attribute.Name.LocalName == "Project");
			(importIdentity, importBytes) = GetGeneratedNuGetImportAuthority(project.Value, packageAuthority);
		}
		foreach (XAttribute attribute in attributes)
		{
			if (attribute.IsNamespaceDeclaration)
			{
				continue;
			}
			Assert.True(string.IsNullOrEmpty(attribute.Name.NamespaceName));
			manifest.Append('|');
			manifest.Append(attribute.Name.LocalName);
			manifest.Append('=');
			string attributeValue = attribute.Value;
			if (element.Name == msbuild + "Import" && attribute.Name.LocalName == "Project")
			{
				attributeValue = Assert.IsType<string>(importIdentity);
			}
			else if (element.Name == msbuild + "Import" && attribute.Name.LocalName == "Condition")
			{
				XAttribute project = Assert.Single(attributes, candidate => candidate.Name.LocalName == "Project");
				Assert.Equal($"Exists('{project.Value}')", attributeValue);
				attributeValue = "Exists('{NUGET_IMPORT}')";
			}
			else
			{
				attributeValue = AssertGeneratedNuGetStableValue(
					attributeValue,
					repositoryRoot,
					dotnetRoot,
					packageAuthority);
			}
			manifest.Append(JsonSerializer.Serialize(attributeValue));
		}
		if (importBytes is not null)
		{
			manifest.Append("|SELECTED_SHA256=");
			manifest.Append(importBytes);
		}
		manifest.Append('>');

		XNode[] nodes = element.Nodes().ToArray();
		bool hasElements = element.HasElements;
		foreach (XNode node in nodes)
		{
			if (node is XElement child)
			{
				AppendCanonicalGeneratedNuGetXml(
					manifest,
					child,
					msbuild,
					repositoryRoot,
					dotnetRoot,
					packageAuthority);
			}
			else if (node is XText text)
			{
				Assert.True(string.IsNullOrWhiteSpace(text.Value) || !hasElements);
			}
			else
			{
				throw new Xunit.Sdk.XunitException($"Unsupported generated NuGet XML node: {node.NodeType}");
			}
		}
		if (!hasElements)
		{
			string semanticValue = element.Value;
			if (element.Name == msbuild + "NuGetPackageRoot")
			{
				Assert.True(PackagePathComparer.Equals(
					semanticValue.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
					packageAuthority.PrimaryRoot));
				semanticValue = "{NUGET_PRIMARY}";
			}
			else if (element.Name == msbuild + "NuGetPackageFolders")
			{
				AssertGeneratedNuGetPackageFolders(semanticValue, packageAuthority);
				semanticValue = "{VALIDATED_PACKAGE_ROOT_TOPOLOGY}";
			}
			else if (element.Name.LocalName.StartsWith("Pkg", StringComparison.Ordinal))
			{
				semanticValue = NormalizePackageDirectoryIdentity(semanticValue, packageAuthority);
			}
			else
			{
				semanticValue = AssertGeneratedNuGetStableValue(
					semanticValue,
					repositoryRoot,
					dotnetRoot,
					packageAuthority);
			}
			manifest.Append(JsonSerializer.Serialize(semanticValue));
		}
		manifest.Append("</");
		manifest.Append(element.Name.LocalName);
		manifest.Append('>');
	}

	private static (string Identity, string Sha256) GetGeneratedNuGetImportAuthority(
		string project,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		const string NuGetPackageRootPrefix = "$(NuGetPackageRoot)/";
		string relativePath;
		string? selectedPath = null;
		string normalizedProject = project.Replace('\\', '/');
		if (normalizedProject.StartsWith(NuGetPackageRootPrefix, StringComparison.Ordinal))
		{
			relativePath = normalizedProject[NuGetPackageRootPrefix.Length..];
		}
		else
		{
			string fullPath = Path.GetFullPath(project);
			string? selectedRoot = GetContainingPackageRoot(fullPath, packageAuthority);
			Assert.NotNull(selectedRoot);
			relativePath = NormalizeRelativePath(Path.GetRelativePath(selectedRoot, fullPath));
			selectedPath = fullPath;
		}
		AssertSafePackageRelativePath(relativePath);
		if (selectedPath is null)
		{
			foreach (string packageRoot in packageAuthority.OrderedRoots)
			{
				string candidate = Path.GetFullPath(Path.Combine(
					packageRoot,
					relativePath.Replace('/', Path.DirectorySeparatorChar)));
				if (File.Exists(candidate))
				{
					selectedPath = candidate;
					break;
				}
			}
		}
		Assert.NotNull(selectedPath);
		AssertPackageShadowConsistency(selectedPath, relativePath, packageAuthority);
		return ($"NUGET|{relativePath}", Sha256File(selectedPath));
	}

	private static string NormalizePackageDirectoryIdentity(
		string path,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string fullPath = Path.GetFullPath(path);
		string? packageRoot = GetContainingPackageRoot(fullPath, packageAuthority);
		Assert.NotNull(packageRoot);
		AssertRegularAuthorityDirectory(fullPath, "generated NuGet package directory");
		string relativePath = NormalizeRelativePath(Path.GetRelativePath(packageRoot, fullPath));
		AssertSafePackageRelativePath(relativePath);
		return $"NUGET|{relativePath}";
	}

	private static string? GetContainingPackageRoot(
		string path,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string fullPath = Path.GetFullPath(path);
		string? result = null;
		foreach (string packageRoot in packageAuthority.OrderedRoots)
		{
			if (!IsPathWithin(fullPath, packageRoot))
			{
				continue;
			}
			Assert.Null(result);
			result = packageRoot;
		}
		return result;
	}

	private static void AssertGeneratedNuGetPackageFolders(
		string value,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string[] folders = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(packageAuthority.OrderedRoots.Length, folders.Length);
		for (int index = 0; index < folders.Length; index++)
		{
			Assert.True(PackagePathComparer.Equals(
				folders[index].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				packageAuthority.OrderedRoots[index]));
		}
	}

	private static string AssertGeneratedNuGetStableValue(
		string value,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string normalizedValue = value.Replace('\\', '/');
		foreach (string packageRoot in packageAuthority.OrderedRoots)
		{
			Assert.DoesNotContain(
				Path.GetFullPath(packageRoot).Replace('\\', '/').TrimEnd('/'),
				normalizedValue,
				StringComparison.Ordinal);
		}
		Assert.DoesNotContain(
			Path.GetFullPath(repositoryRoot).Replace('\\', '/').TrimEnd('/'),
			normalizedValue,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			Path.GetFullPath(dotnetRoot).Replace('\\', '/').TrimEnd('/'),
			normalizedValue,
			StringComparison.Ordinal);
		return value;
	}

	private static void AssertSafePackageRelativePath(string value)
	{
		Assert.False(string.IsNullOrWhiteSpace(value));
		Assert.DoesNotContain('\\', value);
		Assert.False(Path.IsPathFullyQualified(value));
		string normalized = NormalizeRelativePath(value);
		Assert.Equal(value, normalized);
		Assert.All(value.Split('/'), component =>
			Assert.False(component is "" or "." or ".."));
	}

	private static void AssertExactJsonProperties(JsonElement value, string[] expected)
	{
		Assert.Equal(JsonValueKind.Object, value.ValueKind);
		var actual = new List<string>();
		var unique = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty property in value.EnumerateObject())
		{
			Assert.True(unique.Add(property.Name), $"Duplicate JSON property: {property.Name}");
			actual.Add(property.Name);
		}
		Assert.Equal(expected, actual);
	}

	private static void SortJsonProperties(JsonProperty[] properties)
	{
		for (int index = 1; index < properties.Length; index++)
		{
			JsonProperty current = properties[index];
			int insertion = index;
			while (insertion > 0 && StringComparer.Ordinal.Compare(properties[insertion - 1].Name, current.Name) > 0)
			{
				properties[insertion] = properties[insertion - 1];
				insertion--;
			}
			properties[insertion] = current;
		}
	}

	private static void SortXmlAttributes(XAttribute[] attributes)
	{
		for (int index = 1; index < attributes.Length; index++)
		{
			XAttribute current = attributes[index];
			int insertion = index;
			while (insertion > 0 && StringComparer.Ordinal.Compare(
				attributes[insertion - 1].Name.ToString(), current.Name.ToString()) > 0)
			{
				attributes[insertion] = attributes[insertion - 1];
				insertion--;
			}
			attributes[insertion] = current;
		}
	}

	private static string Sha256Text(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private static void AssertRegularAuthorityFile(string path, string description)
	{
		Assert.True(File.Exists(path), $"The {description} is absent: {path}");
		AssertAuthorityPathHasNoSymbolicLinks(path, description);
	}

	private static string EscapeMsbuildPropertyValue(string value)
	{
		string escaped = value.Replace("%", "%25", StringComparison.Ordinal);
		return escaped.IndexOfAny([';', ',']) >= 0 ? $"\"{escaped}\"" : escaped;
	}

	private static (string DotnetHost, string DotnetRoot) GetApprovedDotnetHost()
	{
		string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";
		DirectoryInfo? dotnetRootDirectory = new DirectoryInfo(runtimeDirectory).Parent?.Parent?.Parent;
		Assert.NotNull(dotnetRootDirectory);
		string dotnetRoot = Path.GetFullPath(dotnetRootDirectory.FullName);
		string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
		string dotnetHost = Path.GetFullPath(Path.Combine(dotnetRoot, executableName));
		AssertApprovedDotnetHost(dotnetHost, dotnetRoot);
		return (dotnetHost, dotnetRoot);
	}

	private static (string ProductVersion, string CommitHash) GetLoadedProductBuildIdentity()
	{
		Assembly productAssembly = typeof(LiquidOrdinaryWalletPlanEncoder).Assembly;
		AssemblyInformationalVersionAttribute informationalVersion = Assert.Single(
			productAssembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>());
		string value = informationalVersion.InformationalVersion;
		string? commitHash = null;
		foreach (AssemblyMetadataAttribute metadata in productAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
		{
			if (!StringComparer.Ordinal.Equals(metadata.Key, "CommitHash"))
			{
				continue;
			}
			Assert.Null(commitHash);
			commitHash = metadata.Value;
		}
		Assert.NotNull(commitHash);
		Assert.True(
			commitHash.Length == 0 ||
			Regex.IsMatch(commitHash, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant),
			"The loaded CommitHash metadata is not empty or a full lowercase Git identity.");
		string productVersion = RemoveSdkSourceRevisionSuffix(
			value,
			commitHash,
			TryReadCurrentRepositoryRevision());
		Assert.Matches(
			"^[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*(?:\\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
			productVersion);
		return (productVersion, commitHash);
	}

	private static string RemoveSdkSourceRevisionSuffix(
		string informationalVersion,
		string commitHash,
		string? currentRepositoryRevision)
	{
		Assert.True(
			currentRepositoryRevision is null ||
			Regex.IsMatch(currentRepositoryRevision, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant),
			"The current repository revision evidence is not a full lowercase Git identity.");
		int metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
		if (metadataSeparator < 0)
		{
			return informationalVersion;
		}
		string metadata = informationalVersion[(metadataSeparator + 1)..];
		int revisionSeparator = metadata.LastIndexOf('.');
		string revision = revisionSeparator < 0 ? metadata : metadata[(revisionSeparator + 1)..];
		if (!Regex.IsMatch(revision, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
		{
			return informationalVersion;
		}
		if (Regex.IsMatch(commitHash, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
		{
			Assert.Equal(commitHash, revision);
		}
		if (!StringComparer.Ordinal.Equals(currentRepositoryRevision, revision))
		{
			return informationalVersion;
		}
		return revisionSeparator < 0
			? informationalVersion[..metadataSeparator]
			: informationalVersion[..(metadataSeparator + 1 + revisionSeparator)];
	}

	private static void AssertApprovedDotnetHost(string candidate, string dotnetRoot)
	{
		string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
		string expected = Path.GetFullPath(Path.Combine(dotnetRoot, executableName));
		Assert.Equal(expected, Path.GetFullPath(candidate));
		Assert.True(File.Exists(expected), $"The running runtime's canonical dotnet host is absent: {expected}");
		Assert.True(File.Exists(Path.Combine(dotnetRoot, "sdk/10.0.100/MSBuild.dll")));
		Assert.True(File.Exists(Path.Combine(dotnetRoot, "sdk/10.0.100/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props")));
	}

	private static void AssertExactBuildAuthority(
		IReadOnlyDictionary<string, string> properties,
		string dotnetRoot,
		string productionRoot,
		string generatedRoot)
	{
#if DEBUG
		const string ExpectedConfiguration = "Debug";
		const string ExpectedDefineConstants =
			"TRACE;DEBUG;NET;NET10_0;NETCOREAPP;NET5_0_OR_GREATER;NET6_0_OR_GREATER;" +
			"NET7_0_OR_GREATER;NET8_0_OR_GREATER;NET9_0_OR_GREATER;NET10_0_OR_GREATER;" +
			"NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;" +
			"NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;" +
			"NETCOREAPP3_1_OR_GREATER";
#else
		const string ExpectedConfiguration = "Release";
		const string ExpectedDefineConstants =
			"TRACE;RELEASE;NET;NET10_0;NETCOREAPP;NET5_0_OR_GREATER;NET6_0_OR_GREATER;" +
			"NET7_0_OR_GREATER;NET8_0_OR_GREATER;NET9_0_OR_GREATER;NET10_0_OR_GREATER;" +
			"NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;" +
			"NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;" +
			"NETCOREAPP3_1_OR_GREATER";
#endif
		string repositoryRoot = Path.GetDirectoryName(Path.GetFullPath(productionRoot))!;
		string authorityRoot = Path.GetDirectoryName(Path.GetFullPath(generatedRoot))!;
		string projectAssetsFile = Path.GetFullPath(Path.Combine(productionRoot, "obj/project.assets.json"));
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority = GetPinnedPackageAuthority(projectAssetsFile);
		string packageRoot = packageAuthority.PrimaryRoot;
		string sdkRoot = Path.Combine(dotnetRoot, "sdk/10.0.100");
		string roslynRoot = Path.Combine(sdkRoot, "Roslyn");
		string outputPath = Path.Combine(authorityRoot, "bin") + Path.DirectorySeparatorChar;
		string intermediateOutputPath = Path.Combine(authorityRoot, "obj/net10.0") + Path.DirectorySeparatorChar;
		string baseOutputPath = Path.Combine(authorityRoot, "base-bin") + Path.DirectorySeparatorChar;
		string baseIntermediateOutputPath = Path.Combine(authorityRoot, "obj") + Path.DirectorySeparatorChar;
		(string productVersion, string commitHash) = GetLoadedProductBuildIdentity();
		var expected = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["MSBuildProjectDirectory"] = Path.GetFullPath(productionRoot),
			["Configuration"] = ExpectedConfiguration,
			["Version"] = productVersion,
			["CommitHash"] = commitHash,
			["Platform"] = "AnyCPU",
			["TargetFramework"] = "net10.0",
			["TargetFrameworkIdentifier"] = ".NETCoreApp",
			["TargetFrameworkVersion"] = "v10.0",
			["TargetFrameworks"] = "",
			["RuntimeIdentifier"] = "",
			["RuntimeIdentifiers"] = "",
			["NETCoreSdkVersion"] = "10.0.100",
			["MSBuildVersion"] = "18.0.2",
			["LangVersion"] = "14",
			["DefineConstants"] = ExpectedDefineConstants,
			["AllowUnsafeBlocks"] = "true",
			["BaseIntermediateOutputPath"] = baseIntermediateOutputPath,
			["IntermediateOutputPath"] = intermediateOutputPath,
			["BaseOutputPath"] = baseOutputPath,
			["OutputPath"] = outputPath,
			["PathMap"] = $"{generatedRoot}{Path.DirectorySeparatorChar}=WalletWasabi/obj/{ExpectedConfiguration}/net10.0/," +
				$"{intermediateOutputPath}=WalletWasabi/obj/{ExpectedConfiguration}/net10.0/," +
				$"{productionRoot}{Path.DirectorySeparatorChar}=WalletWasabi",
			["DefaultExcludesInProjectFolder"] = "bin/**;obj/**;**/.*/**",
			["MSBuildProjectExtensionsPath"] = Path.GetFullPath(Path.Combine(productionRoot, "obj")) +
				Path.DirectorySeparatorChar,
			["EmitCompilerGeneratedFiles"] = "true",
			["CompilerGeneratedFilesOutputPath"] = Path.GetFullPath(generatedRoot),
			["ProjectAssetsFile"] = projectAssetsFile,
			["BuildProjectReferences"] = "false",
			["UseSharedCompilation"] = "false",
			["UseHostCompilerIfAvailable"] = "false",
			["ProvideCommandLineArgs"] = "true",
			["RestoreDuringBuild"] = "false",
			["RestorePackagesPath"] = packageRoot,
			["NuGetPackageRoot"] = packageRoot + Path.DirectorySeparatorChar,
			["DisableImplicitNuGetFallbackFolder"] = "true",
			["ImportDirectoryBuildProps"] = "true",
			["DirectoryBuildPropsPath"] = Path.Combine(repositoryRoot, "Directory.Build.props"),
			["ImportDirectoryBuildTargets"] = "false",
			["DirectoryBuildTargetsPath"] = "",
			["CustomBeforeDirectoryBuildProps"] = "",
			["CustomAfterDirectoryBuildProps"] = "",
			["CustomBeforeDirectoryBuildTargets"] = "",
			["CustomAfterDirectoryBuildTargets"] = "",
			["ImportProjectExtensionProps"] = "true",
			["ImportProjectExtensionTargets"] = "true",
			["ImportByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonProps"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonProps"] = "false",
			["ImportByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCommonTargets"] = "false",
			["ImportByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets"] = "false",
			["ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets"] = "false",
			["CustomBeforeMicrosoftCommonProps"] = "",
			["CustomAfterMicrosoftCommonProps"] = "",
			["CustomBeforeMicrosoftCommonTargets"] = "",
			["CustomAfterMicrosoftCommonTargets"] = "",
			["CustomBeforeMicrosoftCSharpTargets"] = "",
			["CustomAfterMicrosoftCSharpTargets"] = "",
			["MSBuildUserExtensionsPath"] = Path.Combine(authorityRoot, "disabled-imports"),
			["MSBuildToolsPath"] = Path.GetFullPath(sdkRoot),
			["MSBuildSDKsPath"] = Path.GetFullPath(Path.Combine(sdkRoot, "Sdks")),
			["RoslynTargetsPath"] = Path.GetFullPath(roslynRoot),
			["CSharpCoreTargetsPath"] = Path.GetFullPath(Path.Combine(roslynRoot, "Microsoft.CSharp.Core.targets")),
			["CscToolPath"] = "",
			["CscToolExe"] = "",
			["MSBuildDisableAllAutoResponseFiles"] = "true",
		};
		string[] targetListProperties = ["CompileDependsOn", "CoreCompileDependsOn", "TargetsTriggeredByCompilation"];
		Assert.Equal(
			expected.OrderBy(pair => pair.Key),
			properties.Where(pair => !targetListProperties.Contains(pair.Key, StringComparer.Ordinal))
				.OrderBy(pair => pair.Key));
		Assert.Equal(
			new[]
			{
				"ResolveReferences", "ResolveKeySource", "SetWin32ManifestProperties",
				"_SetPreferNativeArm64Win32ManifestProperties", "FindReferenceAssembliesForReferences",
				"_GenerateCompileInputs", "BeforeCompile", "_TimeStampBeforeCompile",
				"_GenerateCompileDependencyCache", "CoreCompile", "_TimeStampAfterCompile",
				"AfterCompile", "_CreateAppHost", "_CreateComHost", "_GetIjwHostPaths",
			},
			SplitTargetList(properties["CompileDependsOn"]));
		Assert.Equal(
			new[] { "_ComputeNonExistentFileProperty", "ResolveCodeAnalysisRuleSet" },
			SplitTargetList(properties["CoreCompileDependsOn"]));
		Assert.Empty(SplitTargetList(properties["TargetsTriggeredByCompilation"]));

		Assembly productionAssembly = typeof(LiquidOrdinaryWalletPlanEncoder).Assembly;
		AssemblyConfigurationAttribute configuration = Assert.Single(
			productionAssembly.GetCustomAttributes<AssemblyConfigurationAttribute>());
		TargetFrameworkAttribute framework = Assert.Single(
			productionAssembly.GetCustomAttributes<TargetFrameworkAttribute>());
		Assert.Equal(ExpectedConfiguration, configuration.Configuration);
		Assert.Equal(".NETCoreApp,Version=v10.0", framework.FrameworkName);
		Assert.Equal(10, Environment.Version.Major);
	}

	private static string[] SplitTargetList(string value) =>
		value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private static IReadOnlyDictionary<string, string> MutateBuildProperty(
		IReadOnlyDictionary<string, string> properties,
		string name,
		string value)
	{
		var mutated = new Dictionary<string, string>(properties, StringComparer.Ordinal)
		{
			[name] = value,
		};
		return mutated;
	}

	private static void AssertExactAmbientCompileAuthority(
		IEnumerable<(string FullPath, string RelativePath, string Source)> compileInputs)
	{
		string[] actual = GetAmbientCompileAuthority(compileInputs);

		string[] expected =
		[
			"ATTRIBUTE|AssemblyInfo.cs|assembly|InternalsVisibleTo",
			"ATTRIBUTE|obj/{configuration}/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs|assembly|global::System.Runtime.Versioning.TargetFrameworkAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyCompanyAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyConfigurationAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyFileVersionAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyInformationalVersionAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyMetadata",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyProductAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyTitleAttribute",
			"ATTRIBUTE|obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs|assembly|System.Reflection.AssemblyVersionAttribute",
			"GLOBAL_USING|GlobalUsings.cs|global using System;",
			"GLOBAL_USING|GlobalUsings.cs|global using static WalletWasabi.Models.Height;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WabiSabi.Crypto.ZeroKnowledge;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.Blockchain.TransactionOutputs;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.Helpers;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.Logging;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Models;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Rounds;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Models.MultipartyTransaction;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Client/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Models;",
			"GLOBAL_USING|WabiSabi/Client/CoinJoin/Manager/GlobalUsings.cs|global using WalletWasabi.Wallets;",
			"GLOBAL_USING|WabiSabi/Client/GlobalUsings.cs|global using WalletWasabi.Blockchain.Keys;",
			"GLOBAL_USING|WabiSabi/Client/GlobalUsings.cs|global using WalletWasabi.Extensions;",
			"GLOBAL_USING|WabiSabi/Coordinator/GlobalUsings.cs|global using WalletWasabi.Logging;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using NBitcoin;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Collections.Generic;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Collections.Immutable;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Linq;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Threading.Tasks;",
			"GLOBAL_USING|WabiSabi/GlobalUsings.cs|global using System.Threading;",
			"GLOBAL_USING|WabiSabi/Models/GlobalUsings.cs|global using WabiSabi.CredentialRequesting;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.Crypto;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.Extensions;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.Helpers;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Models;",
			"GLOBAL_USING|WabiSabi/Models/MultipartyTransaction/GlobalUsings.cs|global using WalletWasabi.WabiSabi.Coordinator.Rounds;",
			"MODULE_INITIALIZER|ModuleInitializer.cs|ModuleInitializer.PatchTestNet",
		];
		Assert.True(
			expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)),
			string.Join('\n', actual.Order(StringComparer.Ordinal)));
	}

	private static string[] GetAmbientCompileAuthority(
		IEnumerable<(string FullPath, string RelativePath, string Source)> compileInputs)
	{
		var actual = new List<string>();
		foreach ((string _, string relativePath, string source) in compileInputs)
		{
			string normalizedPath = NormalizeAmbientPath(relativePath);
			CompilationUnitSyntax root = Assert.IsType<CompilationUnitSyntax>(
				CSharpSyntaxTree.ParseText(source).GetRoot());
			foreach (AttributeListSyntax list in root.AttributeLists)
			{
				string? target = list.Target?.Identifier.ValueText;
				if (target is "assembly" or "module")
				{
					actual.AddRange(list.Attributes.Select(attribute =>
						$"ATTRIBUTE|{normalizedPath}|{target}|{attribute.Name}"));
				}
			}
			foreach (UsingDirectiveSyntax directive in root.Usings.Where(usingDirective =>
				usingDirective.GlobalKeyword.RawKind != (int)SyntaxKind.None))
			{
				actual.Add($"GLOBAL_USING|{normalizedPath}|{NormalizeSyntax(directive.ToString())}");
			}
			foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
			{
				if (method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
					attribute.Name.ToString() is "ModuleInitializer" or "ModuleInitializerAttribute" or
					"System.Runtime.CompilerServices.ModuleInitializer" or
					"System.Runtime.CompilerServices.ModuleInitializerAttribute"))
				{
					string declaringType = method.Ancestors().OfType<BaseTypeDeclarationSyntax>()
						.First().Identifier.ValueText;
					actual.Add($"MODULE_INITIALIZER|{normalizedPath}|{declaringType}.{method.Identifier.ValueText}");
				}
			}
		}

		return actual.ToArray();
	}

	private static string NormalizeAmbientPath(string relativePath)
	{
		string normalized = NormalizeRelativePath(relativePath);
		if (normalized.EndsWith(
			"/obj/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs",
			StringComparison.Ordinal))
		{
			return "obj/{configuration}/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs";
		}
		if (normalized.EndsWith(
			"/obj/net10.0/WalletWasabi.AssemblyInfo.cs",
			StringComparison.Ordinal))
		{
			return "obj/{configuration}/net10.0/WalletWasabi.AssemblyInfo.cs";
		}
		return normalized
			.Replace("obj/Debug/", "obj/{configuration}/", StringComparison.Ordinal)
			.Replace("obj/Release/", "obj/{configuration}/", StringComparison.Ordinal);
	}

	private static void AssertExactAnalyzerAuthority(
		IEnumerable<(string FullPath, string DefiningProjectFullPath)> analyzers,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string[] expected =
		[
			"DOTNET|packs/Microsoft.NETCore.App.Ref/10.0.0/analyzers/dotnet/cs/Microsoft.Interop.ComInterfaceGenerator.dll|SHA256|051a9f8bfee1842ec53d40e329ea068d498de8a60da2be29cceb2dac65e19561|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"DOTNET|packs/Microsoft.NETCore.App.Ref/10.0.0/analyzers/dotnet/cs/Microsoft.Interop.JavaScript.JSImportGenerator.dll|SHA256|f7096596857e0d473488436131de7ff1bd401244ca7f8d1e8b5c856438e2b409|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"DOTNET|packs/Microsoft.NETCore.App.Ref/10.0.0/analyzers/dotnet/cs/Microsoft.Interop.LibraryImportGenerator.dll|SHA256|cd136ba1cbed48e1b3252ab608b3cc8bd8392ac2187cadfe1b610bde751ab5ee|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"DOTNET|packs/Microsoft.NETCore.App.Ref/10.0.0/analyzers/dotnet/cs/Microsoft.Interop.SourceGeneration.dll|SHA256|f183345ed20cd0416c2ab8bd439e2938bc2ccf0b0688be91f5b474a29bdd42a5|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"DOTNET|packs/Microsoft.NETCore.App.Ref/10.0.0/analyzers/dotnet/cs/System.Text.Json.SourceGeneration.dll|SHA256|94372eebcff48adff1272f295d0bd0a8f2186ea6f94003cbdde4c2aaa26a1a31|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"DOTNET|packs/Microsoft.NETCore.App.Ref/10.0.0/analyzers/dotnet/cs/System.Text.RegularExpressions.Generator.dll|SHA256|87382d87a6f801acde7176a21d6e58b0fe395f4fc8b420624dea538a537358f2|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/analyzers/Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll|SHA256|53046380d99a25e32cc436bc6e89678813e5108511836a2b488699a0910d9e12|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/analyzers/Microsoft.CodeAnalysis.NetAnalyzers.dll|SHA256|04736be5b1476c9ef2e08f0530e02672cdb8d3891c418a0ac2b48decec81cab1|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"NUGET|microsoft.codeanalysis.bannedapianalyzers/4.14.0/analyzers/dotnet/cs/Microsoft.CodeAnalysis.BannedApiAnalyzers.dll|SHA256|1d2972c0ee11dcc950f84cd97f1d9ada503d07ec93379232a46129dc723df5ad|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"NUGET|microsoft.codeanalysis.bannedapianalyzers/4.14.0/analyzers/dotnet/cs/Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll|SHA256|350d4f564d49d8a0fb8d3189d15aa7fd086618974de182e89423afd828c11150|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"NUGET|microsoft.extensions.logging.abstractions/10.0.2/analyzers/dotnet/roslyn4.4/cs/Microsoft.Extensions.Logging.Generators.dll|SHA256|88139b918cc7fab679feef0c4fd4a25ba3773082d2ae1c6cf485f9cc92cdc6f6|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
			"NUGET|microsoft.extensions.options/10.0.2/analyzers/dotnet/roslyn4.4/cs/Microsoft.Extensions.Options.SourceGeneration.dll|SHA256|c0f9fc70a18c42637f8801a6ee7af722943fb7093731c95e85269f29198c7608|PROVENANCE|DOTNET|sdk/10.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.ConflictResolution.targets",
		];
		string[] actual = analyzers.Select(analyzer =>
		{
			string definingProject = Path.GetFullPath(analyzer.DefiningProjectFullPath);
			Assert.True(IsPathWithin(definingProject, dotnetRoot));
			Assert.True(File.Exists(definingProject), $"Analyzer provenance does not exist: {definingProject}");
			string provenance = "|PROVENANCE|DOTNET|" +
				NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, definingProject));
			string fullPath = Path.GetFullPath(analyzer.FullPath);
			Assert.True(File.Exists(fullPath), $"Analyzer does not exist: {fullPath}");
			string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
			if (IsPathWithin(fullPath, dotnetRoot))
			{
				return "DOTNET|" + NormalizeRelativePath(Path.GetRelativePath(dotnetRoot, fullPath)) +
					"|SHA256|" + sha256 + provenance;
			}

			Assert.True(
				TryNormalizePackageAuthorityPath(fullPath, packageAuthority, out string normalizedPackagePath),
				$"Analyzer is outside approved SDK and package roots: {fullPath}");
			return normalizedPackagePath +
				"|SHA256|" + sha256 + provenance;
		}).Order(StringComparer.Ordinal).ToArray();
		string[] orderedExpected = expected.Order(StringComparer.Ordinal).ToArray();
		Assert.Equal(orderedExpected.Length, actual.Length);
		for (int index = 0; index < actual.Length; index++)
		{
			Assert.True(
				StringComparer.Ordinal.Equals(orderedExpected[index], actual[index]),
				$"Analyzer authority mismatch at {index}.\nEXPECTED HEX {Convert.ToHexString(Encoding.UTF8.GetBytes(orderedExpected[index]))}\n" +
				$"ACTUAL HEX {Convert.ToHexString(Encoding.UTF8.GetBytes(actual[index]))}");
		}
	}

	private static bool IsPathWithin(string candidate, string root)
	{
		string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
		return !Path.IsPathRooted(relative) &&
			relative != ".." &&
			!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static void AssertExactGeneratedSourceAuthority(
		IEnumerable<GeneratedBuildFile> generatedSources)
	{
		var sources = generatedSources.OrderBy(source => source.RelativePath, StringComparer.Ordinal).ToArray();
		Assert.NotEmpty(sources);
		foreach (GeneratedBuildFile generated in sources)
		{
			string relativePath = generated.RelativePath;
			string source = generated.Source;
			Assert.Equal(NormalizeRelativePath(relativePath), relativePath);
			Assert.Matches("^[0-9a-f]{64}$", generated.Sha256);
			Assert.False(string.IsNullOrEmpty(source), $"Generated authority is not C# source: {relativePath}");
			Assert.False(IsImplementationContributor(source), $"Generated source contributes WLPQ authority: {relativePath}");
			Assert.Empty(GetAmbientCompileAuthority(
				[(Path.GetFullPath(Path.Combine(Path.GetTempPath(), relativePath)), relativePath, source)]));
		}
		string manifest = string.Join(
			'\n',
			sources.Select(source => $"{source.RelativePath}|{source.Sha256}")) + "\n";
#if DEBUG
		string expectedSha256 = ExpectedDebugGeneratedSourcesSha256;
#else
		string expectedSha256 = ExpectedReleaseGeneratedSourcesSha256;
#endif
		string actualSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedSha256, actualSha256), actualSha256);
	}

	private static void AssertExactImplementationCompileInputs(
		IEnumerable<string> expectedRelativePaths,
		string productionRoot,
		IEnumerable<(string FullPath, string RelativePath, string Source)> evaluatedCompileInputs)
	{
		string[] expectedRelative = expectedRelativePaths
			.Select(NormalizeRelativePath)
			.Order(StringComparer.Ordinal)
			.ToArray();
		string[] expectedFull = expectedRelative
			.Select(path => Path.GetFullPath(Path.Combine(productionRoot, path)))
			.Order(StringComparer.Ordinal)
			.ToArray();
		var evaluated = evaluatedCompileInputs.ToArray();
		Assert.All(evaluated, input =>
		{
			Assert.Equal(Path.GetFullPath(input.FullPath), input.FullPath);
			Assert.Equal(
				NormalizeRelativePath(Path.GetRelativePath(productionRoot, input.FullPath)),
				input.RelativePath);
		});
		var implementation = evaluated
			.Where(input => IsImplementationContributor(input.Source))
			.ToArray();
		Assert.Equal(
			expectedRelative,
			implementation.Select(input => input.RelativePath).Order(StringComparer.Ordinal));
		Assert.Equal(
			expectedFull,
			implementation.Select(input => input.FullPath).Order(StringComparer.Ordinal));
		Assert.Equal(expectedRelative.Length, implementation.Select(input => input.FullPath).Distinct().Count());
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

	private static MethodBase[] AssertWireMethodClosureSafe(
		IEnumerable<MethodBase> roots,
		string? expectedRuntimeDispatchAuthoritySha256 = null)
	{
		MethodBase[] rootMethods = roots.OrderBy(MethodIdentity, StringComparer.Ordinal).ToArray();
		Assert.NotEmpty(rootMethods);
		Assembly assembly = rootMethods[0].Module.Assembly;
		Assert.All(rootMethods, method => Assert.Equal(assembly, method.Module.Assembly));
		var pending = new Stack<(MethodBase Method, bool StrictDispatch)>(
			rootMethods.Reverse().Select(method => (method, true)));
		var closure = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
		var strictMethods = new HashSet<string>(StringComparer.Ordinal);
		var unresolvedDispatches = new List<string>();
		var reviewedDispatches = new List<string>();

		void EnqueueTypeInitializer(Type? type, bool strictDispatch)
		{
			if (type?.Assembly == assembly && type.TypeInitializer is { } initializer)
			{
				pending.Push((initializer, strictDispatch));
			}
		}

		while (pending.Count > 0)
		{
			(MethodBase method, bool strictDispatch) = pending.Pop();
			string identity = MethodIdentity(method);
			bool newlyStrict = strictDispatch && strictMethods.Add(identity);
			if (!closure.TryAdd(identity, method) && !newlyStrict)
			{
				continue;
			}

			Assert.False(IsForbiddenWireMember(method), $"forbidden closure method {identity}");
			MethodBody body = method.GetMethodBody() ??
				throw new Xunit.Sdk.XunitException($"Closure method has no managed body: {identity}");
			Assert.DoesNotContain(body.ExceptionHandlingClauses, clause =>
				clause.Flags == ExceptionHandlingClauseOptions.Clause && IsForbiddenWireType(clause.CatchType));
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				Assert.False(
					IsForbiddenWireType(local.LocalType),
					$"forbidden closure local {identity} -> {TypeIdentity(local.LocalType)}");
			}
			EnqueueTypeInitializer(method.DeclaringType, strictDispatch);
			foreach ((int instructionOffset, OpCode opCode, MemberInfo? reference) in
				GetIlInstructionsWithOffsets(method))
			{
				Assert.NotEqual(OpCodes.Calli, opCode);
				Assert.NotEqual(OpCodes.Ldftn, opCode);
				Assert.NotEqual(OpCodes.Ldvirtftn, opCode);
				if (reference is null)
				{
					continue;
				}
				Assert.False(
					IsForbiddenWireMember(reference),
					$"forbidden closure reference {identity} -> {MemberIdentity(reference)}");
				if (strictDispatch)
				{
					if (IsUnresolvedRuntimeDispatch(
						method,
						instructionOffset,
						opCode,
						reference,
						out string? reviewedDispatch))
					{
						unresolvedDispatches.Add(RuntimeDispatchSite(
							method,
							instructionOffset,
							opCode,
							reference));
					}
					else if (reviewedDispatch is not null)
					{
						reviewedDispatches.Add(reviewedDispatch);
					}
				}
				Type? touchedType = reference switch
				{
					Type referencedType => referencedType,
					_ => reference.DeclaringType,
				};
				EnqueueTypeInitializer(touchedType, strictDispatch);
				if (reference is MethodBase called && called.Module.Assembly == assembly)
				{
					pending.Push((called, strictDispatch));
				}
			}
		}

		Assert.True(unresolvedDispatches.Count == 0, string.Join('\n', unresolvedDispatches));
		string reviewedDispatchManifest = string.Join(
			'\n',
			reviewedDispatches.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "\n";
		if (expectedRuntimeDispatchAuthoritySha256 is null)
		{
#if DEBUG
			expectedRuntimeDispatchAuthoritySha256 = ExpectedDebugRuntimeDispatchAuthoritySha256;
#else
			expectedRuntimeDispatchAuthoritySha256 = ExpectedReleaseRuntimeDispatchAuthoritySha256;
#endif
		}
		AssertExactSha256(expectedRuntimeDispatchAuthoritySha256, reviewedDispatchManifest);
		return closure.Values.OrderBy(MethodIdentity, StringComparer.Ordinal).ToArray();
	}

	private static bool IsUnresolvedRuntimeDispatch(
		MethodBase caller,
		int instructionOffset,
		OpCode opCode,
		MemberInfo reference,
		out string? reviewedDispatch)
	{
		reviewedDispatch = null;
		if (opCode is var instruction && (instruction == OpCodes.Ldftn || instruction == OpCodes.Ldvirtftn))
		{
			return true;
		}
		if (reference is not MethodBase method)
		{
			return false;
		}

		Type? declaringType = method.DeclaringType;
		if (declaringType is not null && typeof(Delegate).IsAssignableFrom(declaringType) &&
			method.Name == "Invoke")
		{
			return true;
		}
		if (opCode == OpCodes.Callvirt &&
			(declaringType?.IsInterface is true || method is MethodInfo { IsVirtual: true, IsFinal: false }))
		{
			string? receiverProvenance = ClassifyReviewedRuntimeReceiver(caller, instructionOffset, method);
			if (receiverProvenance is null)
			{
				return true;
			}

			reviewedDispatch = $"{RuntimeDispatchSite(caller, instructionOffset, opCode, method)}|" +
				$"RECEIVER|{receiverProvenance}";
			return false;
		}

		string identity = MemberIdentity(reference);
		return identity.Contains("System.Reflection", StringComparison.Ordinal) ||
			identity.Contains("System.Runtime.CompilerServices.CallSite", StringComparison.Ordinal) ||
			identity.Contains("Microsoft.CSharp.RuntimeBinder", StringComparison.Ordinal) ||
			identity.Contains("::DynamicInvoke(", StringComparison.Ordinal) ||
			identity.Contains("System.Activator", StringComparison.Ordinal) ||
			identity.Contains("System.Type", StringComparison.Ordinal) &&
				(method.Name.StartsWith("GetMethod", StringComparison.Ordinal) ||
					method.Name.StartsWith("GetField", StringComparison.Ordinal) ||
					method.Name.StartsWith("GetProperty", StringComparison.Ordinal) ||
					method.Name.StartsWith("GetMember", StringComparison.Ordinal) ||
					method.Name == "InvokeMember");
	}

	private static string? ClassifyReviewedRuntimeReceiver(
		MethodBase caller,
		int instructionOffset,
		MethodBase callee)
	{
		Type? declaringType = callee.DeclaringType;
		if (caller.DeclaringType?.FullName == "WalletWasabi.ModuleInitializer" &&
			caller.Name == "PatchTestNet" && declaringType == typeof(Type) &&
			callee.Name is "get_TypeHandle" or nameof(Type.GetField))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(Type) && producer.Name == nameof(Type.GetTypeFromHandle),
				"exact RuntimeType returned by Type.GetTypeFromHandle in pinned PatchTestNet IL");
		}
		if (caller.DeclaringType?.FullName == "WalletWasabi.ModuleInitializer" &&
			caller.Name == "PatchTestNet" && declaringType == typeof(FieldInfo) &&
			callee.Name == nameof(FieldInfo.GetValue))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(Type) && producer.Name == nameof(Type.GetField),
				"exact FieldInfo returned by Type.GetField in pinned PatchTestNet IL");
		}
		if (declaringType == typeof(StringComparer) &&
			callee.Name is nameof(StringComparer.Equals) or nameof(StringComparer.Compare))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(StringComparer) && producer.Name == "get_Ordinal",
				"StringComparer.Ordinal singleton");
		}
		if (declaringType == typeof(Encoding) && callee.Name == nameof(Encoding.GetBytes))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == typeof(Encoding) && producer.Name == "get_ASCII",
				"Encoding.ASCII singleton");
		}
		if (declaringType?.IsGenericType is true &&
			declaringType.GetGenericTypeDefinition() == typeof(EqualityComparer<>) &&
			callee.Name == nameof(EqualityComparer<object>.Equals))
		{
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => instruction.Member is MethodBase producer &&
					producer.DeclaringType == declaringType && producer.Name == "get_Default",
				$"EqualityComparer<{TypeIdentity(declaringType.GetGenericArguments()[0])}>.Default singleton");
		}
		if (declaringType?.IsGenericType is true &&
			(declaringType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>) ||
				declaringType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) &&
			callee.Name is "get_Count" or "get_Item")
		{
			ParameterInfo parameter = Assert.Single(
				caller.GetParameters(),
				candidate => candidate.ParameterType.IsInterface &&
					declaringType.IsAssignableFrom(candidate.ParameterType));
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => GetLoadedArgumentIndex(caller, instruction) ==
					parameter.Position + (caller.IsStatic ? 0 : 1),
				$"parameter {parameter.Position}:{parameter.Name}:{TypeIdentity(parameter.ParameterType)}");
		}
		if (declaringType == typeof(object) && callee.Name == nameof(ToString) &&
			caller.DeclaringType == typeof(LiquidAddressCodec) &&
			((caller.Name == "EncodeBase58" && instructionOffset is 0x10b or 0xdb) ||
				(caller.Name == "EncodeWitnessAddress" && instructionOffset is 0x162 or 0x13c)))
		{
			LocalVariableInfo local = Assert.Single(
				caller.GetMethodBody()?.LocalVariables ?? [],
				local => local.LocalType == typeof(StringBuilder));
			Assert.True(typeof(StringBuilder).IsSealed);
			return BuildReviewedReceiverProvenance(
				caller,
				instructionOffset,
				instruction => GetLoadedLocalIndex(caller, instruction) == local.LocalIndex,
				$"sealed System.Text.StringBuilder local {local.LocalIndex}");
		}

		return null;
	}

	private static string BuildReviewedReceiverProvenance(
		MethodBase caller,
		int callOffset,
		Func<(int Offset, OpCode OpCode, MemberInfo? Member), bool> isProducer,
		string sourceIdentity)
	{
		var instructions = GetIlInstructionsWithOffsets(caller).ToArray();
		int callIndex = Array.FindIndex(instructions, instruction => instruction.Offset == callOffset);
		Assert.True(callIndex >= 0, $"Reviewed dispatch offset is absent: {MethodIdentity(caller)} IL_{callOffset:x4}");
		int producerIndex = -1;
		for (int index = callIndex - 1; index >= 0; index--)
		{
			if (isProducer(instructions[index]))
			{
				producerIndex = index;
				break;
			}
		}
		Assert.True(
			producerIndex >= 0,
			$"Reviewed receiver producer is absent: {MethodIdentity(caller)} IL_{callOffset:x4} {sourceIdentity}\n" +
			string.Join('\n', instructions.Take(callIndex).TakeLast(20).Select(instruction =>
				$"IL_{instruction.Offset:x4}|{instruction.OpCode.Name}|" +
				(instruction.Member is null ? "NONE" : MemberIdentity(instruction.Member)))));
		var producer = instructions[producerIndex];
		byte[] il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
		int endOffset = callIndex + 1 < instructions.Length ? instructions[callIndex + 1].Offset : il.Length;
		string windowSha256 = Convert.ToHexString(SHA256.HashData(
			il.AsSpan(producer.Offset, endOffset - producer.Offset))).ToLowerInvariant();
		return $"{sourceIdentity}|PRODUCER|IL_{producer.Offset:x4}|{producer.OpCode.Name}|" +
			$"{(producer.Member is null ? "NONE" : MemberIdentity(producer.Member))}|WINDOW_SHA256|{windowSha256}";
	}

	private static int? GetLoadedArgumentIndex(
		MethodBase caller,
		(int Offset, OpCode OpCode, MemberInfo? Member) instruction)
	{
		if (instruction.OpCode == OpCodes.Ldarg_0)
		{
			return 0;
		}
		if (instruction.OpCode == OpCodes.Ldarg_1)
		{
			return 1;
		}
		if (instruction.OpCode == OpCodes.Ldarg_2)
		{
			return 2;
		}
		if (instruction.OpCode == OpCodes.Ldarg_3)
		{
			return 3;
		}

		return ReadVariableOperand(caller, instruction, OpCodes.Ldarg_S, OpCodes.Ldarg);
	}

	private static int? GetLoadedLocalIndex(
		MethodBase caller,
		(int Offset, OpCode OpCode, MemberInfo? Member) instruction)
	{
		if (instruction.OpCode == OpCodes.Ldloc_0)
		{
			return 0;
		}
		if (instruction.OpCode == OpCodes.Ldloc_1)
		{
			return 1;
		}
		if (instruction.OpCode == OpCodes.Ldloc_2)
		{
			return 2;
		}
		if (instruction.OpCode == OpCodes.Ldloc_3)
		{
			return 3;
		}

		return ReadVariableOperand(caller, instruction, OpCodes.Ldloc_S, OpCodes.Ldloc);
	}

	private static int? ReadVariableOperand(
		MethodBase caller,
		(int Offset, OpCode OpCode, MemberInfo? Member) instruction,
		OpCode shortForm,
		OpCode longForm)
	{
		byte[] il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
		int operandOffset = instruction.Offset + instruction.OpCode.Size;
		if (instruction.OpCode == shortForm)
		{
			Assert.InRange(operandOffset, 0, il.Length - 1);
			return il[operandOffset];
		}
		if (instruction.OpCode == longForm)
		{
			Assert.InRange(operandOffset, 0, il.Length - sizeof(ushort));
			return BitConverter.ToUInt16(il, operandOffset);
		}

		return null;
	}

	private static string RuntimeDispatchSite(
		MethodBase caller,
		int instructionOffset,
		OpCode opCode,
		MemberInfo callee) =>
		$"{MethodIdentity(caller)}|IL_{instructionOffset:x4}|{opCode.Name}|{MemberIdentity(callee)}";

	private static string BuildMethodClosureManifest(IEnumerable<MethodBase> methods)
	{
		var rows = new List<string>();
		foreach (MethodBase method in methods.OrderBy(MethodIdentity, StringComparer.Ordinal))
		{
			MethodBody body = method.GetMethodBody() ??
				throw new Xunit.Sdk.XunitException($"Closure method has no managed body: {MethodIdentity(method)}");
			rows.Add($"METHOD|{MethodIdentity(method)}|{(int)method.Attributes}|" +
				$"{(int)method.GetMethodImplementationFlags()}|{(int)method.CallingConvention}");
			if (method is MethodInfo methodInfo)
			{
				rows.Add($"RETURN|{TypeIdentity(methodInfo.ReturnType)}|" +
					AttributeIdentity(methodInfo.ReturnParameter.GetCustomAttributesData()));
			}
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				rows.Add($"PARAM|{parameter.Position}|{parameter.Name}|{TypeIdentity(parameter.ParameterType)}|" +
					$"{(int)parameter.Attributes}|{AttributeIdentity(parameter.GetCustomAttributesData())}");
			}
			rows.Add($"BODY|{body.InitLocals}|{body.MaxStackSize}|" +
				Convert.ToHexString(body.GetILAsByteArray() ?? []).ToLowerInvariant());
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				rows.Add($"LOCAL|{local.LocalIndex}|{TypeIdentity(local.LocalType)}|{local.IsPinned}");
			}
			foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
			{
				rows.Add($"EH|{(int)clause.Flags}|{clause.TryOffset}|{clause.TryLength}|" +
					$"{clause.HandlerOffset}|{clause.HandlerLength}|" +
					TypeIdentity(clause.Flags == ExceptionHandlingClauseOptions.Clause ? clause.CatchType : null));
			}
			foreach (MemberInfo reference in GetIlReferences(method))
			{
				rows.Add($"REF|{MemberIdentity(reference)}");
			}
		}

		return string.Join('\n', rows) + "\n";
	}

	private static void AssertPeModuleInitializerAndAmbientClosureAuthority(Assembly assembly)
	{
		string assemblyPath = Path.GetFullPath(assembly.Location);
		using FileStream stream = File.OpenRead(assemblyPath);
		using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
		MetadataReader metadata = peReader.GetMetadataReader();
		TypeDefinition moduleType = metadata.GetTypeDefinition(
			Assert.Single(metadata.TypeDefinitions, handle =>
				metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "<Module>"));
		MethodDefinitionHandle moduleCctorHandle = Assert.Single(
			moduleType.GetMethods(),
			handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == ".cctor");
		MethodDefinition moduleCctor = metadata.GetMethodDefinition(moduleCctorHandle);
		MethodBodyBlock body = peReader.GetMethodBody(moduleCctor.RelativeVirtualAddress);
		string peBodyManifest = $"TOKEN|{MetadataTokens.GetToken(moduleCctorHandle):x8}\n" +
			$"MAXSTACK|{body.MaxStack}\nLOCALS|{MetadataTokens.GetToken(body.LocalSignature):x8}\n" +
			$"IL|{Convert.ToHexString(body.GetILBytes() ?? []).ToLowerInvariant()}\n" +
			string.Join('\n', body.ExceptionRegions.Select(region =>
				$"EH|{region.Kind}|{region.TryOffset}|{region.TryLength}|{region.HandlerOffset}|" +
				$"{region.HandlerLength}|{region.FilterOffset}|{MetadataTokens.GetToken(region.CatchType):x8}")) + "\n";
		string peBodySha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(peBodyManifest))).ToLowerInvariant();
#if DEBUG
		string expectedPeBodySha256 = ExpectedDebugModuleInitializerBodySha256;
		string expectedAmbientSha256 = ExpectedDebugAmbientClosureSha256;
		string expectedAmbientDispatchSha256 = ExpectedDebugAmbientRuntimeDispatchAuthoritySha256;
#else
		string expectedPeBodySha256 = ExpectedReleaseModuleInitializerBodySha256;
		string expectedAmbientSha256 = ExpectedReleaseAmbientClosureSha256;
		string expectedAmbientDispatchSha256 = ExpectedReleaseAmbientRuntimeDispatchAuthoritySha256;
#endif
		Assert.True(StringComparer.Ordinal.Equals(expectedPeBodySha256, peBodySha256), peBodySha256);

		MethodBase reflectionModuleCctor = Assert.IsAssignableFrom<MethodBase>(
			assembly.ManifestModule.ResolveMethod(MetadataTokens.GetToken(moduleCctorHandle)));
		Assert.Equal(".cctor", reflectionModuleCctor.Name);
		Type moduleInitializerType = assembly.GetType("WalletWasabi.ModuleInitializer", throwOnError: true)!;
		MethodInfo patchTestNet = Assert.Single(
			moduleInitializerType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
			method => method.Name == "PatchTestNet");
		MethodBase[] ambientClosure = AssertWireMethodClosureSafe(
			[reflectionModuleCctor, patchTestNet],
			expectedAmbientDispatchSha256);
		Assert.Contains(reflectionModuleCctor, ambientClosure);
		Assert.Contains(patchTestNet, ambientClosure);
		string ambientManifest = BuildMethodClosureManifest(ambientClosure);
		string ambientSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(ambientManifest))).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedAmbientSha256, ambientSha256), ambientSha256);
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactSha256(expectedPeBodySha256, peBodyManifest + "MODULE_INITIALIZER_MUTATION"));
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			AssertExactSha256(expectedAmbientSha256, ambientManifest + "PATCH_TESTNET_MUTATION"));
	}

	private static MethodBase[] GetSameAssemblyAmbientClosure(
		Assembly assembly,
		IEnumerable<MethodBase> roots)
	{
		var pending = new Stack<MethodBase>(roots.Reverse());
		var closure = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
		while (pending.TryPop(out MethodBase? method))
		{
			if (!closure.TryAdd(MethodIdentity(method), method))
			{
				continue;
			}
			MethodBody body = method.GetMethodBody() ??
				throw new Xunit.Sdk.XunitException($"Ambient closure method has no managed body: {MethodIdentity(method)}");
			if (method.DeclaringType?.Assembly == assembly && method.DeclaringType.TypeInitializer is { } typeInitializer)
			{
				pending.Push(typeInitializer);
			}
			foreach ((_, _, MemberInfo? reference) in GetIlInstructionsWithOffsets(method))
			{
				if (reference?.DeclaringType?.Assembly == assembly &&
					reference.DeclaringType.TypeInitializer is { } referencedInitializer)
				{
					pending.Push(referencedInitializer);
				}
				if (reference is MethodBase called && called.Module.Assembly == assembly &&
					called.GetMethodBody() is not null)
				{
					pending.Push(called);
				}
			}
			_ = body;
		}
		return closure.Values.OrderBy(MethodIdentity, StringComparer.Ordinal).ToArray();
	}

	private static void AssertExactSha256(string expectedSha256, string manifest)
	{
		string actualSha256 = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedSha256, actualSha256), actualSha256);
	}

	private static MethodInfo[] CreateForbiddenClosureMutations()
	{
		AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName("WlpqClosureMutation"),
			AssemblyBuilderAccess.Run);
		ModuleBuilder module = assembly.DefineDynamicModule("WlpqClosureMutation");
		TypeBuilder type = module.DefineType(
			"WlpqClosureMutation.PlanAccessors",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodInfo fileExists = typeof(File).GetMethod(nameof(File.Exists), [typeof(string)])!;

		MethodBuilder direct = type.DefineMethod(
			"ReadSelected",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator directIl = direct.GetILGenerator();
		directIl.Emit(OpCodes.Ldnull);
		directIl.Emit(OpCodes.Call, fileExists);
		directIl.Emit(OpCodes.Pop);
		directIl.Emit(OpCodes.Ret);

		MethodBuilder wrapper = type.DefineMethod(
			"Forward",
			MethodAttributes.Private | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator wrapperIl = wrapper.GetILGenerator();
		wrapperIl.Emit(OpCodes.Ldnull);
		wrapperIl.Emit(OpCodes.Call, fileExists);
		wrapperIl.Emit(OpCodes.Pop);
		wrapperIl.Emit(OpCodes.Ret);

		MethodBuilder transitive = type.DefineMethod(
			"ReadDestinations",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator transitiveIl = transitive.GetILGenerator();
		transitiveIl.Emit(OpCodes.Call, wrapper);
		transitiveIl.Emit(OpCodes.Ret);

		Type created = type.CreateType()!;

		TypeBuilder reflectionType = module.DefineType(
			"WlpqClosureMutation.ReflectionDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder reflection = reflectionType.DefineMethod(
			"Reflect",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator reflectionIl = reflection.GetILGenerator();
		reflectionIl.Emit(OpCodes.Ldtoken, typeof(string));
		reflectionIl.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
		reflectionIl.Emit(OpCodes.Ldstr, nameof(string.ToString));
		reflectionIl.Emit(OpCodes.Callvirt, typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string)])!);
		reflectionIl.Emit(OpCodes.Pop);
		reflectionIl.Emit(OpCodes.Ret);
		Type createdReflection = reflectionType.CreateType()!;

		TypeBuilder delegateType = module.DefineType(
			"WlpqClosureMutation.DelegateDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder delegateTarget = delegateType.DefineMethod(
			"Target",
			MethodAttributes.Private | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		delegateTarget.GetILGenerator().Emit(OpCodes.Ret);
		MethodBuilder delegateDispatch = delegateType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator delegateIl = delegateDispatch.GetILGenerator();
		delegateIl.Emit(OpCodes.Ldnull);
		delegateIl.Emit(OpCodes.Ldftn, delegateTarget);
		delegateIl.Emit(OpCodes.Newobj, typeof(Action).GetConstructor([typeof(object), typeof(IntPtr)])!);
		delegateIl.Emit(OpCodes.Callvirt, typeof(Action).GetMethod(nameof(Action.Invoke))!);
		delegateIl.Emit(OpCodes.Ret);
		Type createdDelegate = delegateType.CreateType()!;

		TypeBuilder dynamicType = module.DefineType(
			"WlpqClosureMutation.DynamicDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder dynamicDispatch = dynamicType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator dynamicIl = dynamicDispatch.GetILGenerator();
		Type closedCallSite = typeof(CallSite<>).MakeGenericType(typeof(Action));
		dynamicIl.Emit(OpCodes.Ldnull);
		dynamicIl.Emit(
			OpCodes.Call,
			closedCallSite.GetMethod(nameof(CallSite<Action>.Create), [typeof(CallSiteBinder)])!);
		dynamicIl.Emit(OpCodes.Pop);
		dynamicIl.Emit(OpCodes.Ret);
		Type createdDynamic = dynamicType.CreateType()!;

		TypeBuilder reviewedCalleeType = module.DefineType(
			"WlpqClosureMutation.ReviewedCalleeAtNewSite",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder reviewedCalleeDispatch = reviewedCalleeType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(bool),
			[typeof(string), typeof(string)]);
		ILGenerator reviewedCalleeIl = reviewedCalleeDispatch.GetILGenerator();
		reviewedCalleeIl.Emit(
			OpCodes.Call,
			typeof(StringComparer).GetProperty(nameof(StringComparer.Ordinal))!.GetMethod!);
		reviewedCalleeIl.Emit(OpCodes.Ldarg_0);
		reviewedCalleeIl.Emit(OpCodes.Ldarg_1);
		reviewedCalleeIl.Emit(
			OpCodes.Callvirt,
			typeof(StringComparer).GetMethod(
				nameof(StringComparer.Equals),
				[typeof(string), typeof(string)])!);
		reviewedCalleeIl.Emit(OpCodes.Ret);
		Type createdReviewedCallee = reviewedCalleeType.CreateType()!;

		TypeBuilder localOverrideType = module.DefineType(
			"WlpqClosureMutation.LocalOverrideDispatch",
			TypeAttributes.Public | TypeAttributes.Sealed);
		MethodBuilder localToString = localOverrideType.DefineMethod(
			nameof(ToString),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
			typeof(string),
			Type.EmptyTypes);
		ILGenerator localToStringIl = localToString.GetILGenerator();
		localToStringIl.Emit(OpCodes.Ldstr, "local override");
		localToStringIl.Emit(OpCodes.Ret);
		localOverrideType.DefineMethodOverride(localToString, typeof(object).GetMethod(nameof(ToString))!);
		MethodBuilder localOverrideDispatch = localOverrideType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(string),
			[localOverrideType]);
		ILGenerator localOverrideIl = localOverrideDispatch.GetILGenerator();
		localOverrideIl.Emit(OpCodes.Ldarg_0);
		localOverrideIl.Emit(OpCodes.Callvirt, typeof(object).GetMethod(nameof(ToString))!);
		localOverrideIl.Emit(OpCodes.Ret);
		Type createdLocalOverride = localOverrideType.CreateType()!;

		TypeBuilder calliType = module.DefineType(
			"WlpqClosureMutation.CalliDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder calliDispatch = calliType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator calliIl = calliDispatch.GetILGenerator();
		calliIl.Emit(OpCodes.Ldc_I4_0);
		calliIl.Emit(OpCodes.Conv_I);
		calliIl.EmitCalli(
			OpCodes.Calli,
			CallingConvention.Cdecl,
			typeof(void),
			Type.EmptyTypes);
		calliIl.Emit(OpCodes.Ret);
		Type createdCalli = calliType.CreateType()!;

		TypeBuilder interfaceBuilder = module.DefineType(
			"WlpqClosureMutation.IUnknownDispatch",
			TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
		interfaceBuilder.DefineMethod(
			"Run",
			MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual,
			typeof(void),
			Type.EmptyTypes);
		Type interfaceType = interfaceBuilder.CreateType()!;
		TypeBuilder virtualType = module.DefineType(
			"WlpqClosureMutation.VirtualDispatch",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		MethodBuilder virtualDispatch = virtualType.DefineMethod(
			"Dispatch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			[interfaceType]);
		ILGenerator virtualIl = virtualDispatch.GetILGenerator();
		virtualIl.Emit(OpCodes.Ldarg_0);
		virtualIl.Emit(OpCodes.Callvirt, interfaceType.GetMethod("Run")!);
		virtualIl.Emit(OpCodes.Ret);
		Type createdVirtual = virtualType.CreateType()!;

		TypeBuilder cctorType = module.DefineType(
			"WlpqClosureMutation.TouchedTypeInitializer",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		FieldBuilder cctorField = cctorType.DefineField(
			"Value",
			typeof(int),
			FieldAttributes.Private | FieldAttributes.Static);
		ConstructorBuilder typeInitializer = cctorType.DefineTypeInitializer();
		ILGenerator cctorIl = typeInitializer.GetILGenerator();
		cctorIl.Emit(OpCodes.Ldnull);
		cctorIl.Emit(OpCodes.Call, fileExists);
		cctorIl.Emit(OpCodes.Pop);
		cctorIl.Emit(OpCodes.Ret);
		MethodBuilder touch = cctorType.DefineMethod(
			"Touch",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator touchIl = touch.GetILGenerator();
		touchIl.Emit(OpCodes.Ldsfld, cctorField);
		touchIl.Emit(OpCodes.Pop);
		touchIl.Emit(OpCodes.Ret);
		Type createdCctor = cctorType.CreateType()!;

		TypeBuilder propertyType = module.DefineType(
			"WlpqClosureMutation.PropertyAccessor",
			TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
		PropertyBuilder property = propertyType.DefineProperty(
			"Value",
			PropertyAttributes.None,
			typeof(int),
			null);
		MethodBuilder getter = propertyType.DefineMethod(
			"get_Value",
			MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName,
			typeof(int),
			Type.EmptyTypes);
		ILGenerator getterIl = getter.GetILGenerator();
		getterIl.Emit(OpCodes.Ldnull);
		getterIl.Emit(OpCodes.Call, fileExists);
		getterIl.Emit(OpCodes.Pop);
		getterIl.Emit(OpCodes.Ldc_I4_0);
		getterIl.Emit(OpCodes.Ret);
		property.SetGetMethod(getter);
		Type createdProperty = propertyType.CreateType()!;

		return
		[
			created.GetMethod("ReadSelected", BindingFlags.Public | BindingFlags.Static)!,
			created.GetMethod("ReadDestinations", BindingFlags.Public | BindingFlags.Static)!,
			createdReflection.GetMethod("Reflect", BindingFlags.Public | BindingFlags.Static)!,
			createdDelegate.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdDynamic.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdReviewedCallee.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdLocalOverride.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdCalli.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdVirtual.GetMethod("Dispatch", BindingFlags.Public | BindingFlags.Static)!,
			createdCctor.GetMethod("Touch", BindingFlags.Public | BindingFlags.Static)!,
			createdProperty.GetMethod("get_Value", BindingFlags.Public | BindingFlags.Static)!,
		];
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

	private static StringComparer PackagePathComparer =>
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;

	private static string ParseCanonicalPackageRoot(string value, string description)
	{
		Assert.False(string.IsNullOrWhiteSpace(value), $"The {description} is blank.");
		Assert.True(Path.IsPathFullyQualified(value), $"The {description} is not absolute: {value}");
		string provided = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string canonical = Path.GetFullPath(value)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		Assert.False(string.IsNullOrEmpty(canonical), $"The {description} is a filesystem root.");
		Assert.True(
			PackagePathComparer.Equals(provided, canonical),
			$"The {description} is not canonical: {value}");
		string filesystemRoot = (Path.GetPathRoot(Path.GetFullPath(value)) ?? "")
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		Assert.False(
			PackagePathComparer.Equals(canonical, filesystemRoot),
			$"The {description} is a filesystem root: {value}");
		AssertRegularAuthorityDirectory(canonical, description);
		return canonical;
	}

	private static bool TryNormalizePackageAuthorityPath(
		string path,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		out string normalizedPath)
	{
		string fullPath = Path.GetFullPath(path);
		string? packageRoot = null;
		foreach (string candidateRoot in packageAuthority.OrderedRoots)
		{
			if (!IsPathWithin(fullPath, candidateRoot))
			{
				continue;
			}
			Assert.Null(packageRoot);
			packageRoot = candidateRoot;
		}
		if (packageRoot is null)
		{
			normalizedPath = "";
			return false;
		}
		string relativePath = NormalizeRelativePath(Path.GetRelativePath(packageRoot, fullPath));
		Assert.NotEqual(".", relativePath);
		AssertPackageShadowConsistency(fullPath, relativePath, packageAuthority);
		normalizedPath = $"NUGET|{relativePath}";
		return true;
	}

	private static void AssertPackageShadowConsistency(
		string selectedPath,
		string relativePath,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		AssertRegularAuthorityFile(selectedPath, "selected package authority file");
		byte[] selectedBytes = File.ReadAllBytes(selectedPath);
		foreach (string packageRoot in packageAuthority.OrderedRoots)
		{
			string candidate = Path.GetFullPath(Path.Combine(
				packageRoot,
				relativePath.Replace('/', Path.DirectorySeparatorChar)));
			if (PackagePathComparer.Equals(candidate, Path.GetFullPath(selectedPath)))
			{
				continue;
			}
			Assert.False(
				Directory.Exists(candidate),
				$"A package authority file is shadowed by a directory: {candidate}");
			if (!File.Exists(candidate))
			{
				continue;
			}
			AssertRegularAuthorityFile(candidate, "shadow package authority file");
			Assert.Equal(selectedBytes, File.ReadAllBytes(candidate));
		}
	}

	private static string NormalizeAuthorityStringWithPackages(
		string value,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority,
		params (string Token, string Root)[] roots)
	{
		string normalized = value.Replace('\\', '/');
		string[] packageRoots = packageAuthority.OrderedRoots.ToArray();
		for (int index = 1; index < packageRoots.Length; index++)
		{
			string current = packageRoots[index];
			int insertion = index;
			while (insertion > 0 && packageRoots[insertion - 1].Length < current.Length)
			{
				packageRoots[insertion] = packageRoots[insertion - 1];
				insertion--;
			}
			packageRoots[insertion] = current;
		}
		foreach (string packageRoot in packageRoots)
		{
			normalized = ReplaceAuthorityRoot(normalized, packageRoot, "{NUGET}");
		}
		return NormalizeAuthorityString(normalized, roots);
	}

	private static void WritePackageAssetsAuthorityFixture(
		string path,
		string primaryRoot,
		params (string Root, bool EmptyObject)[] orderedRoots)
	{
		var json = new StringBuilder();
		json.Append("{\"project\":{\"restore\":{\"packagesPath\":");
		json.Append(JsonSerializer.Serialize(primaryRoot));
		json.Append("}},\"packageFolders\":{");
		for (int index = 0; index < orderedRoots.Length; index++)
		{
			if (index != 0)
			{
				json.Append(',');
			}
			json.Append(JsonSerializer.Serialize(orderedRoots[index].Root));
			json.Append(orderedRoots[index].EmptyObject ? ":{}" : ":{\"unexpected\":true}");
		}
		json.Append("}}");
		File.WriteAllText(path, json.ToString(), Encoding.UTF8);
	}

	private static string WriteSemanticRestoreFixture(
		string repositoryRoot,
		string dotnetRoot,
		string primaryPackageRoot,
		string[] orderedPackageRoots,
		string importedPackageFile,
		string dependencyVersion,
		string contentHash,
		string libraryPath)
	{
		string projectRoot = Path.Combine(repositoryRoot, "WalletWasabi");
		string generatedRoot = Path.Combine(projectRoot, "obj");
		Directory.CreateDirectory(generatedRoot);
		string projectPath = Path.Combine(projectRoot, "WalletWasabi.csproj");
		string assetsPath = Path.Combine(generatedRoot, "project.assets.json");
		string propsPath = Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.props");
		string targetsPath = Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.targets");
		string dependencyIdentity = $"Example.Package/{dependencyVersion}";
		var json = new StringBuilder();
		json.Append("{\"version\":3,\"targets\":{\"net10.0\":{");
		json.Append(JsonSerializer.Serialize(dependencyIdentity));
		json.Append(":{\"type\":\"package\",\"compile\":{\"lib/net10.0/Example.Package.dll\":{}}}}},");
		json.Append("\"libraries\":{");
		json.Append(JsonSerializer.Serialize(dependencyIdentity));
		json.Append(":{\"sha512\":");
		json.Append(JsonSerializer.Serialize(contentHash));
		json.Append(",\"type\":\"package\",\"path\":");
		json.Append(JsonSerializer.Serialize(libraryPath));
		json.Append(",\"files\":[\"lib/net10.0/Example.Package.dll\",\"build/example.props\"]}},");
		json.Append("\"projectFileDependencyGroups\":{\"net10.0\":[");
		json.Append(JsonSerializer.Serialize($"Example.Package >= {dependencyVersion}"));
		json.Append("]},\"packageFolders\":{");
		for (int index = 0; index < orderedPackageRoots.Length; index++)
		{
			if (index != 0)
			{
				json.Append(',');
			}
			json.Append(JsonSerializer.Serialize(orderedPackageRoots[index]));
			json.Append(":{}");
		}
		json.Append("},\"project\":{\"version\":\"1.0.0\",\"restore\":{");
		json.Append("\"projectUniqueName\":");
		json.Append(JsonSerializer.Serialize(projectPath));
		json.Append(",\"projectName\":\"WalletWasabi\",\"projectPath\":");
		json.Append(JsonSerializer.Serialize(projectPath));
		json.Append(",\"packagesPath\":");
		json.Append(JsonSerializer.Serialize(primaryPackageRoot));
		json.Append(",\"outputPath\":");
		json.Append(JsonSerializer.Serialize(generatedRoot + Path.DirectorySeparatorChar));
		json.Append(",\"projectStyle\":\"PackageReference\",\"configFilePaths\":[");
		json.Append(JsonSerializer.Serialize(Path.Combine(repositoryRoot, "NuGet.Config")));
		json.Append(',');
		json.Append(JsonSerializer.Serialize(Path.Combine(repositoryRoot, "home/.nuget/NuGet/NuGet.Config")));
		json.Append("],\"originalTargetFrameworks\":[\"net10.0\"],\"sources\":{\"https://api.nuget.org/v3/index.json\":{}},");
		if (orderedPackageRoots.Length > 1)
		{
			json.Append("\"fallbackFolders\":[");
			for (int index = 1; index < orderedPackageRoots.Length; index++)
			{
				if (index != 1)
				{
					json.Append(',');
				}
				json.Append(JsonSerializer.Serialize(orderedPackageRoots[index]));
			}
			json.Append("],");
		}
		json.Append("\"frameworks\":{\"net10.0\":{\"targetAlias\":\"net10.0\",\"projectReferences\":{}}}},");
		json.Append("\"frameworks\":{\"net10.0\":{\"targetAlias\":\"net10.0\",\"dependencies\":{");
		json.Append("\"Example.Package\":{\"target\":\"Package\",\"version\":");
		json.Append(JsonSerializer.Serialize($"[{dependencyVersion}, )"));
		json.Append("}},\"runtimeIdentifierGraphPath\":");
		json.Append(JsonSerializer.Serialize(Path.Combine(dotnetRoot, "sdk/10.0.100/PortableRuntimeIdentifierGraph.json")));
		json.Append("}}}}");
		File.WriteAllText(assetsPath, json.ToString(), Encoding.UTF8);
		WriteSemanticNuGetPropsFixture(propsPath, orderedPackageRoots, importedPackageFile);
		File.WriteAllText(
			targetsPath,
			"<Project ToolsVersion=\"14.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />",
			Encoding.UTF8);
		return assetsPath;
	}

	private static void WriteSemanticNuGetPropsFixture(
		string path,
		string[] orderedPackageRoots,
		string importedPackageFile)
	{
		var xml = new StringBuilder();
		xml.Append("<Project ToolsVersion=\"14.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
		xml.Append("<PropertyGroup Condition=\" '$(ExcludeRestorePackageImports)' != 'true' \">");
		xml.Append("<NuGetPackageRoot>");
		xml.Append(System.Security.SecurityElement.Escape(orderedPackageRoots[0]));
		xml.Append("</NuGetPackageRoot><NuGetPackageFolders>");
		xml.Append(System.Security.SecurityElement.Escape(string.Join(';', orderedPackageRoots)));
		xml.Append("</NuGetPackageFolders><PkgExample_Package>");
		string packageDirectory = Path.GetDirectoryName(Path.GetDirectoryName(importedPackageFile)!)!;
		xml.Append(System.Security.SecurityElement.Escape(packageDirectory));
		xml.Append("</PkgExample_Package></PropertyGroup><ItemGroup>");
		foreach (string packageRoot in orderedPackageRoots)
		{
			xml.Append("<SourceRoot Include=\"");
			xml.Append(System.Security.SecurityElement.Escape(packageRoot + Path.DirectorySeparatorChar));
			xml.Append("\" />");
		}
		xml.Append("</ItemGroup><ImportGroup><Import Project=\"");
		xml.Append(System.Security.SecurityElement.Escape(importedPackageFile));
		xml.Append("\" Condition=\"Exists('");
		xml.Append(System.Security.SecurityElement.Escape(importedPackageFile));
		xml.Append("')\" /></ImportGroup></Project>");
		File.WriteAllText(path, xml.ToString(), Encoding.UTF8);
	}

	private static string CreateSemanticRestorePackageImport(
		string packageRoot,
		byte[] content,
		string fileName = "example.props")
	{
		string path = Path.Combine(packageRoot, "example.package/1.2.3/build", fileName);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, content);
		return path;
	}

	private static string CreateSemanticRestoreContentHash(byte value)
	{
		byte[] bytes = new byte[64];
		Array.Fill(bytes, value);
		return Convert.ToBase64String(bytes);
	}

	private static string BuildSemanticRestoreFixtureManifest(
		string projectAssetsFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		string generatedRoot = Path.GetDirectoryName(projectAssetsFile)!;
		return GetBuildAuthorityFileSha256(
			projectAssetsFile,
			projectAssetsFile,
			repositoryRoot,
			dotnetRoot,
			packageAuthority) + "|" +
			GetBuildAuthorityFileSha256(
				Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.props"),
				projectAssetsFile,
				repositoryRoot,
				dotnetRoot,
				packageAuthority) + "|" +
			GetBuildAuthorityFileSha256(
				Path.Combine(generatedRoot, "WalletWasabi.csproj.nuget.g.targets"),
				projectAssetsFile,
				repositoryRoot,
				dotnetRoot,
				packageAuthority);
	}

	private static void AssertSemanticRestoreFixtureRejected(
		string projectAssetsFile,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		bool rejected = false;
		try
		{
			_ = BuildSemanticRestoreFixtureManifest(
				projectAssetsFile,
				repositoryRoot,
				dotnetRoot,
				packageAuthority);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid semantic restore authority was accepted.");
	}

	private static void AssertPackageAuthorityRejected(string projectAssetsFile)
	{
		bool rejected = false;
		try
		{
			_ = GetPinnedPackageAuthority(projectAssetsFile);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid package authority was accepted.");
	}

	private static void AssertPackagePathRejected(
		string path,
		string repositoryRoot,
		string dotnetRoot,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		bool rejected = false;
		try
		{
			_ = NormalizeAuthorityPath(path, repositoryRoot, dotnetRoot, packageAuthority);
		}
		catch (Xunit.Sdk.XunitException)
		{
			rejected = true;
		}
		Assert.True(rejected, "Invalid package authority path was accepted.");
	}

	private static void AssertRegularAuthorityDirectory(string path, string description)
	{
		Assert.True(Directory.Exists(path), $"The {description} is absent: {path}");
		AssertAuthorityPathHasNoSymbolicLinks(path, description);
	}

	private static void AssertAuthorityPathHasNoSymbolicLinks(string path, string description)
	{
		string fullPath = Path.GetFullPath(path);
		if (OperatingSystem.IsMacOS() && fullPath.StartsWith("/var/", StringComparison.Ordinal))
		{
			fullPath = "/private" + fullPath;
		}
		string? current = Path.GetPathRoot(fullPath);
		foreach (string component in fullPath[(current?.Length ?? 0)..].Split(
			Path.DirectorySeparatorChar,
			StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current ?? "", component);
			Assert.False(
				new FileInfo(current).LinkTarget is not null ||
				new DirectoryInfo(current).LinkTarget is not null,
				$"The {description} reaches a symbolic link at: {current}");
		}
	}

	private static void AssertProjectAssetsFallbackFolderTopology(
		JsonElement root,
		(string PrimaryRoot, string[] OrderedRoots) packageAuthority)
	{
		JsonElement project = root.GetProperty("project");
		Assert.Equal(JsonValueKind.Object, project.ValueKind);
		JsonElement restore = project.GetProperty("restore");
		Assert.Equal(JsonValueKind.Object, restore.ValueKind);
		bool hasFallbackFolders = restore.TryGetProperty("fallbackFolders", out JsonElement fallbackFolders);
		if (packageAuthority.OrderedRoots.Length == 1)
		{
			Assert.False(hasFallbackFolders, "A single package root must not declare restore fallbackFolders.");
			return;
		}

		Assert.True(hasFallbackFolders, "Multiple package roots require restore fallbackFolders.");
		AssertProjectAssetsFallbackFolders(fallbackFolders, packageAuthority);
	}

	private static string? TryReadCurrentRepositoryRevision()
	{
		DirectoryInfo? repository = Directory.GetParent(GetProductionRoot());
		return repository is null ? null : TryReadRepositoryRevision(repository.FullName);
	}

	private static string? TryReadRepositoryRevision(string repositoryRoot)
	{
		string canonicalRepositoryRoot = Path.GetFullPath(repositoryRoot);
		string gitEntry = Path.Combine(canonicalRepositoryRoot, ".git");
		string gitDirectory;
		if (Directory.Exists(gitEntry))
		{
			AssertRegularAuthorityDirectory(gitEntry, "current Git authority directory");
			gitDirectory = Path.GetFullPath(gitEntry);
		}
		else if (File.Exists(gitEntry))
		{
			AssertRegularAuthorityFile(gitEntry, "current Git authority indirection");
			string indirection = File.ReadAllText(gitEntry).Trim();
			const string GitDirectoryPrefix = "gitdir: ";
			if (!indirection.StartsWith(GitDirectoryPrefix, StringComparison.Ordinal))
			{
				return null;
			}
			string declaredDirectory = indirection[GitDirectoryPrefix.Length..];
			gitDirectory = Path.GetFullPath(Path.Combine(canonicalRepositoryRoot, declaredDirectory));
			AssertRegularAuthorityDirectory(gitDirectory, "current linked-worktree Git authority directory");
		}
		else
		{
			return null;
		}

		string commonGitDirectory = gitDirectory;
		string commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
		if (File.Exists(commonDirectoryPath))
		{
			AssertRegularAuthorityFile(commonDirectoryPath, "current Git common-directory authority");
			string declaredCommonDirectory = File.ReadAllText(commonDirectoryPath).Trim();
			if (string.IsNullOrWhiteSpace(declaredCommonDirectory))
			{
				return null;
			}
			commonGitDirectory = Path.GetFullPath(Path.Combine(gitDirectory, declaredCommonDirectory));
			AssertRegularAuthorityDirectory(commonGitDirectory, "current Git common authority directory");
		}

		string headPath = Path.Combine(gitDirectory, "HEAD");
		if (!File.Exists(headPath))
		{
			return null;
		}
		AssertRegularAuthorityFile(headPath, "current Git HEAD authority");
		string head = File.ReadAllText(headPath).Trim();
		if (Regex.IsMatch(head, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
		{
			return head;
		}

		const string RefPrefix = "ref: ";
		if (!head.StartsWith(RefPrefix, StringComparison.Ordinal))
		{
			return null;
		}
		string referenceName = head[RefPrefix.Length..];
		if (!IsValidGitReferenceName(referenceName))
		{
			return null;
		}
		string referencePath = Path.GetFullPath(Path.Combine(commonGitDirectory, referenceName));
		if (!IsPathWithin(referencePath, commonGitDirectory))
		{
			return null;
		}
		if (File.Exists(referencePath))
		{
			AssertRegularAuthorityFile(referencePath, "current Git reference authority");
			string revision = File.ReadAllText(referencePath).Trim();
			return Regex.IsMatch(revision, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
				? revision
				: null;
		}

		string packedReferencesPath = Path.Combine(commonGitDirectory, "packed-refs");
		if (!File.Exists(packedReferencesPath))
		{
			return null;
		}
		AssertRegularAuthorityFile(packedReferencesPath, "current packed Git reference authority");
		string? packedRevision = null;
		foreach (string line in File.ReadAllLines(packedReferencesPath))
		{
			int separator = line.IndexOf(' ');
			if (separator <= 0 || !StringComparer.Ordinal.Equals(line[(separator + 1)..], referenceName))
			{
				continue;
			}
			Assert.Null(packedRevision);
			string candidate = line[..separator];
			if (!Regex.IsMatch(candidate, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
			{
				return null;
			}
			packedRevision = candidate;
		}
		return packedRevision;
	}

	private static bool IsValidGitReferenceName(string referenceName)
	{
		if (!referenceName.StartsWith("refs/", StringComparison.Ordinal) ||
			referenceName.EndsWith('/') ||
			referenceName.EndsWith('.') ||
			referenceName.Contains("..", StringComparison.Ordinal) ||
			referenceName.Contains("//", StringComparison.Ordinal) ||
			referenceName.Contains("@{", StringComparison.Ordinal))
		{
			return false;
		}

		foreach (string component in referenceName.Split('/'))
		{
			if (component.Length == 0 ||
				component.StartsWith('.') ||
				component.EndsWith(".lock", StringComparison.Ordinal))
			{
				return false;
			}
		}

		foreach (char character in referenceName)
		{
			if (character <= ' ' || character == '\u007f' ||
				character is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
			{
				return false;
			}
		}
		return true;
	}
}
