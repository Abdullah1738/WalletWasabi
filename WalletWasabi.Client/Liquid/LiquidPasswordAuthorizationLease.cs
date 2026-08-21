using System;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidPasswordAuthorizationLease : IDisposable
{
	private const int MaxPasswordLength = 1024;
	private char[]? _password;

	private LiquidPasswordAuthorizationLease(char[] password) => _password = password;

	internal static LiquidPasswordAuthorizationLease Create(ReadOnlySpan<char> password)
	{
		if (password.IsEmpty || password.Length > MaxPasswordLength)
		{
			throw new ArgumentException("A password between 1 and 1024 characters is required.", nameof(password));
		}

		return new(password.ToArray());
	}

	internal ReadOnlySpan<char> Password => _password ?? throw new ObjectDisposedException(nameof(LiquidPasswordAuthorizationLease));

	internal bool IsDisposed => _password is null;

	public void Dispose()
	{
		char[]? password = Interlocked.Exchange(ref _password, null);
		if (password is null)
		{
			return;
		}

		CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
	}
}
