using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace WalletWasabi.Liquid.CoinJoin.Native;

internal static unsafe class LiquidCoinJoinNativeBinding
{
	internal const string NativeCommit = "f4fbd27463855fb13b2fef209fdf1a3994e3ae76";
	private const string MacSha256 = "7ef206f60b7ef0a401828da9071a801937f40329d64c8598c9494398fe79ea80";
	private static readonly Lazy<nint> NativeExport = new(LoadExport, LazyThreadSafetyMode.ExecutionAndPublication);

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
		if (!OperatingSystem.IsMacOS() || RuntimeInformation.OSArchitecture != Architecture.Arm64)
			throw new PlatformNotSupportedException("The pinned CoinJoin artifact supports macOS arm64 only.");
		EnsurePinnedArtifact();
		var entry = (delegate* unmanaged[Cdecl]<byte*, ulong, byte*, ulong, ulong*, int>)NativeExport.Value;
		ulong length = 0;
		fixed (byte* requestPtr = request)
		fixed (byte* responsePtr = response)
		{
			int status = entry(requestPtr, (ulong)request.Length, responsePtr, (ulong)response.Length, &length);
			responseLength = length;
			return status;
		}
	}

	private static nint LoadExport()
	{
		try
		{
			nint handle = NativeLibrary.Load(ResolveLibraryPath());
			return NativeLibrary.GetExport(handle, "wlcj_execute_v1");
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			throw new PlatformNotSupportedException("The pinned CoinJoin native export is unavailable.", ex);
		}
	}
}
