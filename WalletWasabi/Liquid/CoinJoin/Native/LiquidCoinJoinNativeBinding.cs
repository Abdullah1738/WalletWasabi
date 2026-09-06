using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.CoinJoin.Native;

internal static unsafe class LiquidCoinJoinNativeBinding
{
	private const string ExportName = "wlcj_execute_v1";
	private const string MacSha256 = "2818f4066ab2d6fb90252ba3105f0249f37c907673d13cfc1a158d649b3818c8";
	internal const string NativeCommit = "4c7f7b5756ff221cf1e52772a6579a92e2656771";
	private static nint? _handle;

	internal static string ResolveLibraryPath()
	{
		if (!OperatingSystem.IsMacOS() || !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			throw new PlatformNotSupportedException("The initial CoinJoin native binding supports macOS only.");
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
			nint handle = LoadHandle();
			nint export = NativeLibrary.GetExport(handle, ExportName);
			var entry = (delegate* unmanaged[Cdecl]<byte*, ulong, byte*, ulong, ulong*, int>)export;
			ulong length = 0;
			int status;
			fixed (byte* requestPtr = requestBytes)
			fixed (byte* responsePtr = responseBytes)
			{
				status = entry(requestPtr, (ulong)requestBytes.Length, responsePtr, (ulong)response.Length, &length);
			}
			responseLength = length;
			if (status == 0 && length <= (ulong)response.Length)
				responseBytes.AsSpan(0, (int)length).CopyTo(response);
			else if (status != -8)
				responseLength = 0;
			return status;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(requestBytes);
			CryptographicOperations.ZeroMemory(responseBytes);
		}
	}

	private static nint LoadHandle()
	{
		if (_handle is { } cached)
			return cached;
		lock (typeof(LiquidCoinJoinNativeBinding))
		{
			if (_handle is { } existing)
				return existing;
			EnsurePinnedArtifact();
			_handle = NativeLibrary.Load(ResolveLibraryPath());
			return _handle.Value;
		}
	}
}
