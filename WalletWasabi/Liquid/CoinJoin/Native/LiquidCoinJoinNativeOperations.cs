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

	internal Response Canonicalize(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> canonicalContext) => Execute(1, pset, canonicalContext);
	internal Response VerifyInputRegistration(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> registrationContext, ReadOnlyMemory<byte> proof, ReadOnlyMemory<byte> Ma) => Execute(2, pset, registrationContext, proof, Ma);
	internal Response VerifyOutputRegistration(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> registrationContext, ReadOnlyMemory<byte> proof, ReadOnlyMemory<byte> Ma, ReadOnlyMemory<byte> valueProof, ReadOnlyMemory<byte> assetProof) => Execute(3, pset, registrationContext, proof, Ma, valueProof, assetProof);
	internal Response BlindNonLast(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> roleMap, ReadOnlyMemory<byte> secretRecords, ReadOnlyMemory<byte> entropy) => Execute(4, pset, roleMap, secretRecords, entropy);
	internal Response BlindLast(ReadOnlyMemory<byte> originalPset, ReadOnlyMemory<byte> roleMap, ReadOnlyMemory<byte> intermediatePset, ReadOnlyMemory<byte> secretRecords, ReadOnlyMemory<byte> entropy) => Execute(5, originalPset, roleMap, intermediatePset, secretRecords, entropy);
	internal Response FinalView(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> canonicalContext) => Execute(6, pset, canonicalContext);
	internal Response VerifyPartialBalance(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> balanceContext, ReadOnlyMemory<byte> proof) => Execute(7, pset, balanceContext, proof);
	internal Response EqualityProof(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> registrationContext, ReadOnlyMemory<byte> Ma, ReadOnlyMemory<byte> valueBE8, ReadOnlyMemory<byte> r1, ReadOnlyMemory<byte> r2, ReadOnlyMemory<byte> entropy) => Execute(8, pset, registrationContext, Ma, valueBE8, r1, r2, entropy);
	internal Response EqualityProofOutput(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> registrationContext, ReadOnlyMemory<byte> Ma, ReadOnlyMemory<byte> valueBE8, ReadOnlyMemory<byte> r1, ReadOnlyMemory<byte> r2, ReadOnlyMemory<byte> entropy, ReadOnlyMemory<byte> valueProof, ReadOnlyMemory<byte> assetProof) => Execute(9, pset, registrationContext, Ma, valueBE8, r1, r2, entropy, valueProof, assetProof);
	internal Response BalanceProof(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> balanceContext, ReadOnlyMemory<byte> residual, ReadOnlyMemory<byte> entropy) => Execute(10, pset, balanceContext, residual, entropy);
	internal Response OwnedDigests(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> authorization, ReadOnlyMemory<byte> owned) => Execute(11, pset, context, digest, authorization, owned);
	internal Response Assembly(ReadOnlyMemory<byte> pset, ReadOnlyMemory<byte> context, ReadOnlyMemory<byte> digest, ReadOnlyMemory<byte> authorization, ReadOnlyMemory<byte> signatures) => Execute(12, pset, context, digest, authorization, signatures);

	internal Response Execute(uint operation, params ReadOnlyMemory<byte>[] fields)
	{
		if (operation is < 1 or > 12)
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
		try
		{
			int expectedFields = operation switch
			{
				1 or 6 => 2,
				11 => 3,
				12 => 4,
				_ => 1
			};
			if (responseOperation != operation || fields.Count != expectedFields)
				throw new FormatException("Native response header or fields are invalid.");
			bool validLengths = operation switch
			{
				1 or 6 => fields[1].Length == 32,
				8 or 9 => fields[0].Length == 162,
				10 => fields[0].Length == 65,
				11 => fields[0].Length == 32 && fields[1].Length == 32,
				12 => fields[0].Length == 32 && fields[1].Length == 32 && fields[3].Length == 32,
				_ => true
			};
			if (!validLengths)
				throw new FormatException("Native response fixed-size fields are invalid.");
			return new Response(responseOperation, fields);
		}
		catch
		{
			// Rejected decoded fields may contain witness-class intermediate PSET data.
			foreach (byte[] field in fields)
				CryptographicOperations.ZeroMemory(field);
			throw;
		}
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
