using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.CoinJoin.Native;

/// <summary>Typed managed boundary for the pinned CoinJoin native artifact.</summary>
internal sealed class LiquidCoinJoinNativeOperations
{
	private const int StatusOk = 0;
	private const int StatusOutputCapacity = -8;

	internal sealed record Response(uint Operation, IReadOnlyList<byte[]> Fields);
	internal delegate int NativeExecutor(ReadOnlySpan<byte> request, Span<byte> response, out ulong responseLength);

	private readonly NativeExecutor _executeNative;

	internal LiquidCoinJoinNativeOperations()
		: this(LiquidCoinJoinNativeBinding.Execute)
	{
	}

	internal LiquidCoinJoinNativeOperations(NativeExecutor executeNative)
	{
		_executeNative = executeNative ?? throw new ArgumentNullException(nameof(executeNative));
	}

	internal Response Canonicalize(ReadOnlyMemory<byte> pset) => Execute(1, pset);
	internal Response BlindNonLast(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> witnesses) => Execute(4, pset, context, witnesses);
	internal Response BlindLast(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> witnesses) => Execute(5, pset, context, witnesses);
	internal Response FinalView(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context) => Execute(6, pset, context);
	internal Response EqualityProof(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> witness, ReadOnlyMemory<byte> entropy) => Execute(8, pset, context, witness, entropy);
	internal Response EqualityProofOutput(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> witness, ReadOnlyMemory<byte> entropy, ReadOnlyMemory<byte> valueProof, ReadOnlyMemory<byte> assetProof) => Execute(9, pset, context, witness, entropy, valueProof, assetProof);
	internal Response BalanceProof(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> residual, ReadOnlyMemory<byte> entropy) => Execute(10, pset, context, residual, entropy);
	internal Response OwnedDigests(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> authorization, ReadOnlyMemory<byte> owned) => Execute(11, pset, context, digest, authorization, owned);
	internal Response Assembly(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> authorization, ReadOnlyMemory<byte> signatures) => Execute(12, pset, context, digest, authorization, signatures);

	internal Response Execute(uint operation, params ReadOnlyMemory<byte>[] fields)
	{
		if (operation is not (1 or 4 or 5 or 6 or 8 or 9 or 10 or 11 or 12))
			throw new NotSupportedException($"CoinJoin operation {operation} is not supported by this adapter.");

		byte[] request = LiquidCoinJoinFrame.Encode(operation, fields);
		try
		{
			int status = _executeNative(request, Span<byte>.Empty, out ulong required);
			if (status != StatusOutputCapacity)
				ThrowStatus(status);
			if (required < LiquidCoinJoinFrame.HeaderBytes || required > LiquidCoinJoinFrame.MaxFrameBytes || required > int.MaxValue)
				throw new InvalidDataException("Native response length exceeds the managed response bound.");

			byte[] response = new byte[(int)required];
			try
			{
				status = _executeNative(request, response, out ulong written);
				if (status != StatusOk)
					ThrowStatus(status);
				if (written != required)
					throw new FormatException("Native response length does not match the capacity query.");
				return ParseResponse(operation, response, checked((int)written));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(response);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(request);
		}
	}

	private static Response ParseResponse(uint operation, byte[] response, int length)
	{
		if (length != response.Length)
			throw new FormatException("Native response length is inconsistent.");
		(uint responseOperation, IReadOnlyList<byte[]> fields) = LiquidCoinJoinFrame.Decode(response.AsSpan(0, length));
		int expectedFields = operation switch
		{
			11 => 3,
			12 => 4,
			_ => 1
		};
		if (responseOperation != operation || fields.Count != expectedFields)
			throw new FormatException("Native response header or fields are invalid.");
		return new Response(responseOperation, fields);
	}

	internal static Response ParseResponseForTest(uint operation, byte[] response) => ParseResponse(operation, response, response.Length);

	private static void ThrowStatus(int status) => throw status switch
	{
		-1 => new FormatException("Native CoinJoin frame is invalid."),
		-2 or -3 => new NotSupportedException("Native CoinJoin ABI or operation is unsupported."),
		-4 => new ArgumentOutOfRangeException("Native CoinJoin payload exceeds its bound."),
		-5 or -6 => new InvalidDataException("Native CoinJoin validation failed."),
		-7 => new InvalidOperationException("Native CoinJoin operation failed."),
		-8 => new InvalidOperationException("Native CoinJoin output capacity was not returned."),
		_ => new InvalidOperationException($"Native CoinJoin returned status {status}.")
	};
}
