using System;

namespace WalletWasabi.Liquid.CoinJoin.Native;

internal static unsafe class LiquidCoinJoinNativeBinding
{
	// No production packaged artifact exists for the signer ABI (ops 11/12).
	internal const string NativeCommit = "UNAVAILABLE";

	internal static string ResolveLibraryPath()
	{
		throw new PlatformNotSupportedException("The CoinJoin native artifact is unavailable; this binding is codec-only.");
	}

	internal static void EnsurePinnedArtifact()
	{
		throw new PlatformNotSupportedException("The CoinJoin native artifact is unavailable; this binding is codec-only.");
	}

	internal static int Execute(ReadOnlySpan<byte> request, Span<byte> response, out ulong responseLength)
	{
		responseLength = 0;
		throw new PlatformNotSupportedException("The CoinJoin native artifact is unavailable; this binding is codec-only.");
	}
}
