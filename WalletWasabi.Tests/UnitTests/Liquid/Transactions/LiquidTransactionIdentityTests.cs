using System.Buffers.Binary;
using System.Linq;
using WalletWasabi.Liquid.Transactions;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Transactions;

public class LiquidTransactionIdentityTests
{
	private const string AsymmetricRpcHex = "44332211ffffffff4433221100000000100f0e0d0c0b0a090807060504030201";
	private const string AsymmetricConsensusHex = "0102030405060708090a0b0c0d0e0f100000000011223344ffffffff11223344";

	[Fact]
	public void ConvertsTransactionIdBetweenRpcAndConsensusByteOrder()
	{
		byte[] consensusBytes = Convert.FromHexString(AsymmetricConsensusHex);
		LiquidTransactionId fromRpc = LiquidTransactionId.ParseRpcHex(AsymmetricRpcHex);
		LiquidTransactionId fromConsensus = LiquidTransactionId.ParseConsensusBytes(consensusBytes);

		Assert.Equal(fromRpc, fromConsensus);
		Assert.Equal(fromRpc.GetHashCode(), fromConsensus.GetHashCode());
		Assert.False(fromRpc.IsZero);
		Assert.Equal(consensusBytes, fromRpc.ToConsensusBytes());
		Assert.Equal(nameof(LiquidTransactionId), fromRpc.ToString());
	}

	[Fact]
	public void PreservesZeroTransactionIdentityButRejectsItAsSpendableOutpoint()
	{
		LiquidTransactionId zeroFromRpc = LiquidTransactionId.ParseRpcHex(new string('0', 64));
		LiquidTransactionId zeroFromConsensus = LiquidTransactionId.ParseConsensusBytes(new byte[32]);

		Assert.True(zeroFromRpc.IsZero);
		Assert.Equal(zeroFromRpc, zeroFromConsensus);
		Assert.Throws<ArgumentException>(() => LiquidOutPoint.CreateSpendable(zeroFromRpc, 0));
	}

	[Fact]
	public void EncodesSpendableOutpointWithLittleEndianOutputIndex()
	{
		LiquidTransactionId transactionId = LiquidTransactionId.ParseRpcHex(AsymmetricRpcHex);
		LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(transactionId, 0x01020304);
		byte[] expected = new byte[LiquidOutPoint.ConsensusByteLength];
		Convert.FromHexString(AsymmetricConsensusHex).CopyTo(expected, 0);
		BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(32), 0x01020304);

		byte[] encoded = outPoint.ToConsensusBytes();
		LiquidOutPoint parsed = LiquidOutPoint.ParseSpendableConsensusBytes(encoded);

		Assert.Equal(expected, encoded);
		Assert.Equal(outPoint, parsed);
		Assert.Equal(outPoint.GetHashCode(), parsed.GetHashCode());
		Assert.Equal(nameof(LiquidOutPoint), outPoint.ToString());
	}

	[Theory]
	[InlineData(0u)]
	[InlineData(LiquidOutPoint.MaxSpendableOutputIndex)]
	public void AcceptsSpendableOutputIndexBoundaries(uint outputIndex)
	{
		LiquidTransactionId transactionId = LiquidTransactionId.ParseRpcHex(AsymmetricRpcHex);
		LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(transactionId, outputIndex);

		Assert.Equal(outputIndex, outPoint.OutputIndex);
		Assert.Equal(outPoint, LiquidOutPoint.ParseSpendableConsensusBytes(outPoint.ToConsensusBytes()));
	}

	[Theory]
	[InlineData(1u << 30)]
	[InlineData(1u << 31)]
	[InlineData(uint.MaxValue)]
	public void RejectsOutputIndicesContainingInputFlagBits(uint outputIndex)
	{
		LiquidTransactionId transactionId = LiquidTransactionId.ParseRpcHex(AsymmetricRpcHex);

		var exception = Assert.Throws<ArgumentOutOfRangeException>(
			() => LiquidOutPoint.CreateSpendable(transactionId, outputIndex));

		Assert.DoesNotContain(outputIndex.ToString(), exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(AsymmetricRpcHex, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ConsensusWritersPreserveSliceSentinelsAndCallerStorage()
	{
		byte[] source = Convert.FromHexString(AsymmetricConsensusHex);
		LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(source);
		source.AsSpan().Clear();
		LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(transactionId, 7);

		byte[] carrier = Enumerable.Repeat((byte)0x5a, LiquidOutPoint.ConsensusByteLength + 2).ToArray();
		outPoint.WriteConsensusBytes(carrier.AsSpan(1, LiquidOutPoint.ConsensusByteLength));

		Assert.Equal(0x5a, carrier[0]);
		Assert.Equal(0x5a, carrier[^1]);
		Assert.Equal(outPoint.ToConsensusBytes(), carrier[1..^1]);
		Assert.Equal(Convert.FromHexString(AsymmetricConsensusHex), transactionId.ToConsensusBytes());

		byte[] exported = outPoint.ToConsensusBytes();
		exported.AsSpan().Clear();
		Assert.NotEqual(exported, outPoint.ToConsensusBytes());
	}

	[Fact]
	public void RejectsInvalidIdentityAndOutpointEncodingsAtomically()
	{
		foreach (string invalid in new[]
		{
			"",
			new string('a', 63),
			new string('a', 65),
			AsymmetricRpcHex.ToUpperInvariant(),
			$"0x{AsymmetricRpcHex}",
			$"{AsymmetricRpcHex[..^1]}g",
		})
		{
			var exception = Assert.Throws<ArgumentException>(() => LiquidTransactionId.ParseRpcHex(invalid));
			if (invalid.Length > 0)
			{
				Assert.DoesNotContain(invalid, exception.Message, StringComparison.Ordinal);
			}
		}

		LiquidTransactionId transactionId = LiquidTransactionId.ParseRpcHex(AsymmetricRpcHex);
		LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(transactionId, 1);
		foreach (int invalidLength in new[] { 0, 31, 33 })
		{
			byte[] destination = Enumerable.Repeat((byte)0xa5, invalidLength).ToArray();
			byte[] expected = [.. destination];
			Assert.Throws<ArgumentException>(() => transactionId.WriteConsensusBytes(destination));
			Assert.Equal(expected, destination);
		}
		foreach (int invalidLength in new[] { 0, 35, 37 })
		{
			byte[] destination = Enumerable.Repeat((byte)0xa5, invalidLength).ToArray();
			byte[] expected = [.. destination];
			Assert.Throws<ArgumentException>(() => outPoint.WriteConsensusBytes(destination));
			Assert.Equal(expected, destination);
			Assert.Throws<ArgumentException>(() => LiquidOutPoint.ParseSpendableConsensusBytes(destination));
		}
	}

	[Fact]
	public void DirectConsensusWritersAllocateNoManagedStorageAfterWarmup()
	{
		LiquidTransactionId transactionId = LiquidTransactionId.ParseRpcHex(AsymmetricRpcHex);
		LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(transactionId, 7);
		Span<byte> transactionDestination = stackalloc byte[LiquidTransactionId.ConsensusByteLength];
		Span<byte> outPointDestination = stackalloc byte[LiquidOutPoint.ConsensusByteLength];
		transactionId.WriteConsensusBytes(transactionDestination);
		outPoint.WriteConsensusBytes(outPointDestination);

		long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		for (int iteration = 0; iteration < 100; iteration++)
		{
			transactionId.WriteConsensusBytes(transactionDestination);
			outPoint.WriteConsensusBytes(outPointDestination);
		}
		long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

		Assert.Equal(allocatedBefore, allocatedAfter);
	}
}
