using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.CoinJoin.Native;

internal static unsafe class LiquidCoinJoinNativeBinding
{
	internal const string NativeCommit = "228431f2cc15e2b90dc3d44962b332ba7a06062d";
	private const string MacSha256 = "511c48e09b2c1e543c62301643c72c1c58a197846b976759436c7a09ac24b7d3";

	internal static string ResolveLibraryPath()
	{
		return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "NativeCoinJoin", "libwasabi_liquid_coinjoin_v1.dylib"));
	}

	internal static void EnsurePinnedArtifact()
	{
		string path = ResolveLibraryPath();
		if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			throw new PlatformNotSupportedException("The pinned CoinJoin native artifact is unavailable.");
		byte[] bytes = File.ReadAllBytes(path);
		try
		{
			if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), MacSha256))
				throw new PlatformNotSupportedException("The pinned CoinJoin native artifact hash does not match.");
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	internal static int Execute(ReadOnlySpan<byte> request, Span<byte> response, out ulong responseLength)
	{
		EnsurePinnedArtifact();
		byte[] requestBytes = request.ToArray();
		byte[] responseBytes = new byte[Math.Max(response.Length, 1)];
		try
		{
			nint handle = NativeLibrary.Load(ResolveLibraryPath());
			nint export = NativeLibrary.GetExport(handle, "wlcj_execute_v1");
			var entry = (delegate* unmanaged[Cdecl]<byte*, ulong, byte*, ulong, ulong*, int>)export;
			ulong length = 0;
			fixed (byte* requestPtr = requestBytes)
			fixed (byte* responsePtr = responseBytes)
			{
				int status = entry(requestPtr, (ulong)requestBytes.Length, responsePtr, (ulong)response.Length, &length);
				responseLength = length;
				if (status == 0 && responseLength <= (ulong)response.Length)
					responseBytes.AsSpan(0, (int)responseLength).CopyTo(response);
				else if (status != -8)
					responseLength = 0;
				return status;
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(requestBytes);
			CryptographicOperations.ZeroMemory(responseBytes);
		}
	}
}
