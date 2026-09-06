using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using WalletWasabi.Liquid.CoinJoin.Native;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.CoinJoin;

public sealed class LiquidCoinJoinFrameTests
{
	[Fact]
	public void EncodeDecodePreservesOperationAndFields()
	{
		byte[] frame = LiquidCoinJoinFrame.Encode(12, ["pset"u8.ToArray(), new byte[] { 1, 2, 3 }]);
		(uint operation, IReadOnlyList<byte[]> fields) = LiquidCoinJoinFrame.Decode(frame);
		Assert.Equal(12u, operation);
		Assert.Equal("pset"u8.ToArray(), fields[0]);
		Assert.Equal(new byte[] { 1, 2, 3 }, fields[1]);
	}

	[Fact]
	public void DecodeRejectsHeaderLengthAndFieldMutations()
	{
		byte[] frame = LiquidCoinJoinFrame.Encode(1, [new byte[] { 0x42 }]);
		foreach (byte[] invalid in new[]
		{
			frame[..15],
			[..frame, 0],
			Mutate(frame, 0, 0),
			Mutate(frame, 12, 0xFF)
		})
		{
			Assert.Throws<FormatException>(() => LiquidCoinJoinFrame.Decode(invalid));
		}
	}

	private static byte[] Mutate(byte[] source, int offset, byte value)
	{
		byte[] copy = [.. source];
		copy[offset] = value;
		return copy;
	}
}
