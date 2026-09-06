using System;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using WalletWasabi.Liquid.CoinJoin.Native;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.CoinJoin;

public sealed class LiquidCoinJoinNativeOperationsTests
{
	[Fact]
	public void UnsupportedOperationFailsClosed()
	{
		var operations = new LiquidCoinJoinNativeOperations();
		Assert.Throws<NotSupportedException>(() => operations.Execute(2, ReadOnlyMemory<byte>.Empty));
		Assert.Throws<NotSupportedException>(() => operations.Execute(13, ReadOnlyMemory<byte>.Empty));
	}

	[Fact]
	public void AdapterHasNoPublicSurface()
	{
		Type type = typeof(LiquidCoinJoinNativeOperations);
		Assert.True(type.IsNotPublic);
		Assert.Empty(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
		Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
	}

	[Fact]
	public void PinnedArtifactRejectsInvalidCanonicalizationInput()
	{
		LiquidCoinJoinNativeBinding.EnsurePinnedArtifact();
		var operations = new LiquidCoinJoinNativeOperations();
		Assert.Throws<InvalidDataException>(() => operations.Canonicalize(ReadOnlyMemory<byte>.Empty));
	}

	[Fact]
	public void PinnedArtifactAndExportAreTheManifestValues()
	{
		string path = LiquidCoinJoinNativeBinding.ResolveLibraryPath();
		Assert.True(File.Exists(path));
		Assert.Equal(
			"511c48e09b2c1e543c62301643c72c1c58a197846b976759436c7a09ac24b7d3",
			Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
		Assert.Equal("228431f2cc15e2b90dc3d44962b332ba7a06062d", LiquidCoinJoinNativeBinding.NativeCommit);
	}

	[Fact]
	public void CodecProducedFakeEntrypointResponseIsStrictlyParsed()
	{
		byte[] response = LiquidCoinJoinFrame.Encode(1, [new byte[] { 0x42 }]);
		LiquidCoinJoinNativeOperations.Response parsed = LiquidCoinJoinNativeOperations.ParseResponseForTest(1, response);
		Assert.Equal(1u, parsed.Operation);
		Assert.Equal(new byte[] { 0x42 }, parsed.Fields[0]);
		response[8] = 2;
		Assert.Throws<FormatException>(() => LiquidCoinJoinNativeOperations.ParseResponseForTest(1, response));
	}

	[Fact]
	public void FakeEntrypointReceivesIdenticalRequestForCapacityAndWrite()
	{
		byte[]? firstRequest = null;
		int calls = 0;
		var operations = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> output, out ulong length) =>
		{
			calls++;
			if (firstRequest is null)
			{
				firstRequest = request.ToArray();
				length = 21;
				return -8;
			}

			Assert.Equal(firstRequest, request.ToArray());
			byte[] response = LiquidCoinJoinFrame.Encode(1, [new byte[] { 0x42 }]);
			response.CopyTo(output);
			length = (ulong)response.Length;
			return 0;
		});

		LiquidCoinJoinNativeOperations.Response result = operations.Canonicalize("pset"u8.ToArray());
		Assert.Equal(2, calls);
		Assert.Equal(new byte[] { 0x42 }, result.Fields[0]);
	}

	[Fact]
	public void FakeEntrypointRejectsMismatchedCapacityLength()
	{
		var operations = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> output, out ulong length) =>
		{
			if (output.IsEmpty)
			{
				length = 21;
				return -8;
			}

			LiquidCoinJoinFrame.Encode(1, [new byte[] { 0x42 }]).CopyTo(output);
			length = 20;
			return 0;
		});

		Assert.Throws<FormatException>(() => operations.Canonicalize("pset"u8.ToArray()));
	}
}
