using System.Collections.Generic;
using WalletWasabi.Liquid.Assets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Assets;

public class LiquidAssetIdTests
{
	private const string MainnetLbtc = "6f0279e9ed041c3d710a9f57d0c02928416460c4b722ae3457a11eec381c526d";
	private const string TestnetLbtc = "144c654344aa716d6f3abcc1ca90e5641e4e2a7f633bc09fe3baf64585819a49";
	private const string AsymmetricRpcHex = "44332211ffffffff4433221100000000100f0e0d0c0b0a090807060504030201";
	private const string AsymmetricConsensusHex = "0102030405060708090a0b0c0d0e0f100000000011223344ffffffff11223344";

	[Theory]
	[InlineData(MainnetLbtc)]
	[InlineData(TestnetLbtc)]
	public void ParsesAndPreservesCanonicalRpcHex(string canonicalRpcHex)
	{
		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(canonicalRpcHex);

		Assert.Equal(canonicalRpcHex, assetId.CanonicalRpcHex);
		Assert.Equal(canonicalRpcHex, assetId.ToString());
	}

	[Fact]
	public void UsesCanonicalIdentityForEquality()
	{
		LiquidAssetId first = LiquidAssetId.ParseRpcHex(MainnetLbtc);
		LiquidAssetId same = LiquidAssetId.ParseRpcHex(MainnetLbtc);
		LiquidAssetId different = LiquidAssetId.ParseRpcHex(TestnetLbtc);

		Assert.Equal(first, same);
		Assert.NotEqual(first, different);
	}

	[Fact]
	public void ConvertsBetweenRpcAndConsensusByteOrder()
	{
		byte[] consensusBytes = Convert.FromHexString(AsymmetricConsensusHex);

		LiquidAssetId fromRpc = LiquidAssetId.ParseRpcHex(AsymmetricRpcHex);
		LiquidAssetId fromConsensus = LiquidAssetId.ParseConsensusBytes(consensusBytes);

		Assert.Equal(fromRpc, fromConsensus);
		Assert.Equal(fromRpc.GetHashCode(), fromConsensus.GetHashCode());
		Assert.Equal(AsymmetricRpcHex, fromConsensus.CanonicalRpcHex);
		Assert.Equal(consensusBytes, fromRpc.ToConsensusBytes());
		Assert.Equal("matched", new Dictionary<LiquidAssetId, string> { [fromRpc] = "matched" }[fromConsensus]);

		Span<byte> written = stackalloc byte[LiquidAssetId.ConsensusByteLength];
		fromRpc.WriteConsensusBytes(written);
		Assert.True(consensusBytes.AsSpan().SequenceEqual(written));
	}

	[Theory]
	[InlineData(MainnetLbtc, "6d521c38ec1ea15734ae22b7c46064412829c0d0579f0a713d1c04ede979026f")]
	[InlineData(TestnetLbtc, "499a818545f6bae39fc03b637f2a4e1e64e590cac1bc3a6f6d71aa4443654c14")]
	public void MatchesPinnedRustElementsConstants(string canonicalRpcHex, string consensusHex)
	{
		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(canonicalRpcHex);

		Assert.Equal(Convert.FromHexString(consensusHex), assetId.ToConsensusBytes());
		Assert.Equal(assetId, LiquidAssetId.ParseConsensusBytes(Convert.FromHexString(consensusHex)));
	}

	[Fact]
	public void ConsensusConversionsDoNotRetainCallerOwnedStorage()
	{
		byte[] source = Convert.FromHexString(AsymmetricConsensusHex);
		LiquidAssetId parsed = LiquidAssetId.ParseConsensusBytes(source);
		source.AsSpan().Clear();

		Assert.Equal(AsymmetricRpcHex, parsed.CanonicalRpcHex);

		byte[] exported = parsed.ToConsensusBytes();
		exported.AsSpan().Clear();

		Assert.Equal(Convert.FromHexString(AsymmetricConsensusHex), parsed.ToConsensusBytes());
		Assert.NotSame(parsed.ToConsensusBytes(), parsed.ToConsensusBytes());
	}

	[Fact]
	public void RejectsInvalidConsensusByteInputsAndDestinationLengths()
	{
		byte[][] invalidValues =
		[
			[],
			CreateFilledBytes(LiquidAssetId.ConsensusByteLength - 1, 0xa5),
			new byte[LiquidAssetId.ConsensusByteLength],
			CreateFilledBytes(LiquidAssetId.ConsensusByteLength + 1, 0xa5),
		];

		foreach (byte[] invalidValue in invalidValues)
		{
			var exception = Assert.Throws<ArgumentException>(() => LiquidAssetId.ParseConsensusBytes(invalidValue));
			Assert.Equal("consensusBytes", exception.ParamName);
			if (invalidValue.Length > 0)
			{
				Assert.DoesNotContain(Convert.ToHexString(invalidValue), exception.Message, StringComparison.OrdinalIgnoreCase);
			}
		}

		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(MainnetLbtc);
		foreach (int invalidLength in new[] { 0, LiquidAssetId.ConsensusByteLength - 1, LiquidAssetId.ConsensusByteLength + 1 })
		{
			byte[] destination = CreateFilledBytes(invalidLength, 0x5a);
			byte[] expected = [.. destination];
			var exception = Assert.Throws<ArgumentException>(() => assetId.WriteConsensusBytes(destination));
			Assert.Equal("destination", exception.ParamName);
			Assert.Equal(expected, destination);
		}
	}

	[Fact]
	public void PreservesParameterNameAndSpanBoundaries()
	{
		var exception = Assert.Throws<ArgumentException>(() =>
			LiquidAssetId.ParseConsensusBytes(new byte[LiquidAssetId.ConsensusByteLength], "assetBytes"));
		Assert.Equal("assetBytes", exception.ParamName);

		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(AsymmetricRpcHex);
		byte[] carrier = CreateFilledBytes(LiquidAssetId.ConsensusByteLength + 2, 0x5a);
		assetId.WriteConsensusBytes(carrier.AsSpan(1, LiquidAssetId.ConsensusByteLength));

		Assert.Equal(0x5a, carrier[0]);
		Assert.Equal(0x5a, carrier[^1]);
		Assert.Equal(Convert.FromHexString(AsymmetricConsensusHex), carrier[1..^1]);
	}

	[Fact]
	public void ReversesWholeBytesAtBothEndpoints()
	{
		byte[] consensusBytes = new byte[LiquidAssetId.ConsensusByteLength];
		consensusBytes[0] = 0xa1;
		consensusBytes[^1] = 0xb2;

		LiquidAssetId assetId = LiquidAssetId.ParseConsensusBytes(consensusBytes);

		Assert.StartsWith("b2", assetId.CanonicalRpcHex, StringComparison.Ordinal);
		Assert.EndsWith("a1", assetId.CanonicalRpcHex, StringComparison.Ordinal);
		Assert.Equal(consensusBytes, assetId.ToConsensusBytes());
	}

	[Fact]
	public void WritesConsensusBytesWithoutManagedAllocation()
	{
		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(AsymmetricRpcHex);
		Span<byte> destination = stackalloc byte[LiquidAssetId.ConsensusByteLength];
		assetId.WriteConsensusBytes(destination);

		long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		for (int iteration = 0; iteration < 100; iteration++)
		{
			assetId.WriteConsensusBytes(destination);
		}
		long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

		Assert.Equal(allocatedBefore, allocatedAfter);
		Assert.True(Convert.FromHexString(AsymmetricConsensusHex).AsSpan().SequenceEqual(destination));
	}

	[Fact]
	public void RejectsNoncanonicalOrZeroValuesWithoutNormalization()
	{
		Assert.Throws<ArgumentNullException>(() => LiquidAssetId.ParseRpcHex(null!));

		string[] invalidValues =
		[
			"",
			new string('a', 63),
			new string('a', 65),
			MainnetLbtc.ToUpperInvariant(),
			$"0x{MainnetLbtc}",
			$" {MainnetLbtc}",
			$"{MainnetLbtc} ",
			$"{MainnetLbtc[..^1]}g",
			new string('0', 64),
		];

		foreach (string invalidValue in invalidValues)
		{
			var exception = Assert.Throws<ArgumentException>(() => LiquidAssetId.ParseRpcHex(invalidValue));
			if (invalidValue.Length > 0)
			{
				Assert.DoesNotContain(invalidValue, exception.Message, StringComparison.Ordinal);
			}
		}
	}

	private static byte[] CreateFilledBytes(int length, byte value)
	{
		byte[] bytes = new byte[length];
		bytes.AsSpan().Fill(value);
		return bytes;
	}
}
