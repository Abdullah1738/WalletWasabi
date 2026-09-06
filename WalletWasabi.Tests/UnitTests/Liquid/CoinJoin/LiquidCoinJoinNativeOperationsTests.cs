using System;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using WalletWasabi.Liquid.CoinJoin.Native;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.CoinJoin;

public sealed class LiquidCoinJoinNativeOperationsTests
{
	[Theory]
	[InlineData(0u)]
	[InlineData(uint.MaxValue)]
	public void UnsupportedOperationFailsClosed(uint operation)
	{
		var operations = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> output, out ulong length) =>
			throw new InvalidOperationException("Unsupported operations must not reach native code."));
		Assert.Throws<NotSupportedException>(() => operations.Execute(operation, ReadOnlyMemory<byte>.Empty));
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
	public void PinnedArtifactRejectsMalformedCanonicalizationFields()
	{
		LiquidCoinJoinNativeBinding.EnsurePinnedArtifact();
		var operations = new LiquidCoinJoinNativeOperations();
		Assert.Throws<FormatException>(() => operations.Canonicalize(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty));
	}

	[Fact]
	public void PinnedArtifactAndExportAreTheManifestValues()
	{
		string path = LiquidCoinJoinNativeBinding.ResolveLibraryPath();
		Assert.True(File.Exists(path));
		Assert.Equal(
			"7ef206f60b7ef0a401828da9071a801937f40329d64c8598c9494398fe79ea80",
			Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
		Assert.Equal("1d8e9f079d2485477cbfa51838d33ca25d454509", LiquidCoinJoinNativeBinding.NativeCommit);
	}

	[Fact]
	public void CodecProducedFakeEntrypointResponseIsStrictlyParsed()
	{
		byte[] response = LiquidCoinJoinFrame.Encode(1, [new byte[] { 0x42 }, new byte[32]]);
		LiquidCoinJoinNativeOperations.Response parsed = LiquidCoinJoinNativeOperations.ParseResponseForTest(1, response);
		Assert.Equal(1u, parsed.Operation);
		Assert.Equal(new byte[] { 0x42 }, parsed.Fields[0]);
		response[8] = 2;
		Assert.Throws<FormatException>(() => LiquidCoinJoinNativeOperations.ParseResponseForTest(1, response));
	}

	[Theory]
	[InlineData(1u, 2)]
	[InlineData(2u, 4)]
	[InlineData(3u, 6)]
	[InlineData(4u, 4)]
	[InlineData(5u, 5)]
	[InlineData(6u, 2)]
	[InlineData(7u, 3)]
	[InlineData(8u, 7)]
	[InlineData(9u, 9)]
	[InlineData(10u, 4)]
	[InlineData(11u, 5)]
	[InlineData(12u, 5)]
	public void FakeEntrypointReceivesExactOrderedFieldsForCapacityAndWrite(uint operation, int fieldCount)
	{
		ReadOnlyMemory<byte>[] inputs = Enumerable.Range(1, fieldCount)
			.Select(i => new ReadOnlyMemory<byte>(Enumerable.Repeat((byte)i, i).ToArray())).ToArray();
		ReadOnlyMemory<byte>[] responseFields = ResponseFields(operation);
		byte[] response = LiquidCoinJoinFrame.Encode(operation, responseFields);
		byte[]? firstRequest = null;
		int calls = 0;
		var operations = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> output, out ulong length) =>
		{
			calls++;
			var decoded = LiquidCoinJoinFrame.Decode(request);
			Assert.Equal(operation, decoded.Operation);
			Assert.Equal(fieldCount, decoded.Fields.Count);
			for (int i = 0; i < fieldCount; i++)
				Assert.Equal(inputs[i].ToArray(), decoded.Fields[i]);
			length = (ulong)response.Length;
			if (firstRequest is null)
			{
				Assert.True(output.IsEmpty);
				firstRequest = request.ToArray();
				return -8;
			}

			Assert.Equal(firstRequest, request.ToArray());
			Assert.Equal(response.Length, output.Length);
			response.CopyTo(output);
			return 0;
		});

		LiquidCoinJoinNativeOperations.Response result = operation switch
		{
			1 => operations.Canonicalize(inputs[0], inputs[1]),
			2 => operations.VerifyInputRegistration(inputs[0], inputs[1], inputs[2], inputs[3]),
			3 => operations.VerifyOutputRegistration(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4], inputs[5]),
			4 => operations.BlindNonLast(inputs[0], inputs[1], inputs[2], inputs[3]),
			5 => operations.BlindLast(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4]),
			6 => operations.FinalView(inputs[0], inputs[1]),
			7 => operations.VerifyPartialBalance(inputs[0], inputs[1], inputs[2]),
			8 => operations.EqualityProof(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4], inputs[5], inputs[6]),
			9 => operations.EqualityProofOutput(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4], inputs[5], inputs[6], inputs[7], inputs[8]),
			10 => operations.BalanceProof(inputs[0], inputs[1], inputs[2], inputs[3]),
			11 => operations.OwnedDigests(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4]),
			12 => operations.Assembly(inputs[0], inputs[1], inputs[2], inputs[3], inputs[4]),
			_ => throw new ArgumentOutOfRangeException(nameof(operation))
		};
		Assert.Equal(2, calls);
		Assert.Equal(operation, result.Operation);
		Assert.Equal(responseFields.Length, result.Fields.Count);
		for (int i = 0; i < responseFields.Length; i++)
			Assert.Equal(responseFields[i].ToArray(), result.Fields[i]);
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

		Assert.Throws<FormatException>(() => operations.Canonicalize("pset"u8.ToArray(), "context"u8.ToArray()));
	}

	[Theory]
	[InlineData(1u)]
	[InlineData(6u)]
	public void FakeEntrypointRejectsMissingOrExtraResponseFields(uint operation)
	{
		ReadOnlyMemory<byte>[] fields = ResponseFields(operation);
		foreach (ReadOnlyMemory<byte>[] malformed in new[] { fields[..1], fields.Append(ReadOnlyMemory<byte>.Empty).ToArray() })
		{
			byte[] response = LiquidCoinJoinFrame.Encode(operation, malformed);
			var operations = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> output, out ulong length) =>
			{
				length = (ulong)response.Length;
				if (output.IsEmpty)
					return -8;
				response.CopyTo(output);
				return 0;
			});
			Assert.Throws<FormatException>(() => operations.Execute(operation, "pset"u8.ToArray(), "context"u8.ToArray()));
		}
	}

	[Theory]
	[InlineData(1u, 1, 32)]
	[InlineData(6u, 1, 32)]
	[InlineData(8u, 0, 162)]
	[InlineData(9u, 0, 162)]
	[InlineData(10u, 0, 65)]
	[InlineData(11u, 0, 32)]
	[InlineData(11u, 1, 32)]
	[InlineData(12u, 0, 32)]
	[InlineData(12u, 1, 32)]
	[InlineData(12u, 3, 32)]
	public void ResponseFixedFieldsRejectWrongLengths(uint operation, int fieldIndex, int expectedLength)
	{
		foreach (int length in new[] { 0, expectedLength - 1, expectedLength + 1 })
		{
			ReadOnlyMemory<byte>[] fields = ResponseFields(operation);
			fields[fieldIndex] = new byte[length];
			byte[] response = LiquidCoinJoinFrame.Encode(operation, fields);
			Assert.Throws<FormatException>(() => LiquidCoinJoinNativeOperations.ParseResponseForTest(operation, response));
		}
	}

	[Theory]
	[InlineData(0UL)]
	[InlineData(15UL)]
	[InlineData(16777217UL)]
	[InlineData(ulong.MaxValue)]
	public void FakeEntrypointRejectsUnboundedCapacity(ulong capacity)
	{
		int calls = 0;
		var operations = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> output, out ulong length) =>
		{
			calls++;
			length = capacity;
			return -8;
		});
		Assert.Throws<InvalidDataException>(() => operations.Canonicalize("pset"u8.ToArray(), "context"u8.ToArray()));
		Assert.Equal(1, calls);
	}

	[Theory]
	[InlineData(-1, typeof(FormatException))]
	[InlineData(-2, typeof(NotSupportedException))]
	[InlineData(-3, typeof(NotSupportedException))]
	[InlineData(-4, typeof(ArgumentOutOfRangeException))]
	[InlineData(-5, typeof(InvalidDataException))]
	[InlineData(-6, typeof(InvalidDataException))]
	[InlineData(-7, typeof(InvalidOperationException))]
	[InlineData(0, typeof(InvalidOperationException))]
	[InlineData(42, typeof(InvalidOperationException))]
	public void FakeEntrypointPreservesStatusMapping(int status, Type exceptionType)
	{
		var operations = new LiquidCoinJoinNativeOperations((ReadOnlySpan<byte> request, Span<byte> output, out ulong length) =>
		{
			length = 0;
			return status;
		});
		Assert.Throws(exceptionType, () => operations.Canonicalize("pset"u8.ToArray(), "context"u8.ToArray()));
	}

	private static ReadOnlyMemory<byte>[] ResponseFields(uint operation) => operation switch
	{
		1 => ["projection"u8.ToArray(), Enumerable.Repeat((byte)0x42, 32).ToArray()],
		6 => [new byte[] { 1 }, Enumerable.Repeat((byte)0x43, 32).ToArray()],
		8 or 9 => [new byte[162]],
		10 => [new byte[65]],
		11 => [new byte[32], new byte[32], new byte[] { 0x44 }],
		12 => [new byte[32], new byte[32], new byte[] { 0x45 }, new byte[32]],
		_ => [new byte[] { 1 }]
	};
}
