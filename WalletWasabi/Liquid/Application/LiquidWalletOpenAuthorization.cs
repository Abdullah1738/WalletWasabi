using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace WalletWasabi.Liquid.Application;

public sealed class LiquidWalletOpenAuthorization : IDisposable
{
	private char[]? _buffer;

	internal LiquidWalletOpenAuthorization(char[] buffer) => _buffer = buffer;

	internal char[] TakeBuffer() =>
		Interlocked.Exchange(ref _buffer, null)
		?? throw new InvalidOperationException("The authorization is already consumed or disposed.");

	internal static void ZeroBuffer(char[] buffer) =>
		CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));

	public void Dispose()
	{
		char[]? buffer = Interlocked.Exchange(ref _buffer, null);
		if (buffer is not null)
		{
			ZeroBuffer(buffer);
		}
	}
}
