using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Wallet.Wire;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

/// <summary>
/// MANAGED-WLPQ-LIVE-CALL-001: the live managed-to-native WLPQ validation call slice. The
/// managed wallet-side test harness loads the native <c>wln_wlpq_validate_v1</c> cdylib built
/// locally with cargo from the exact pinned native commit (wasabi-liquid-native @ d8595a4,
/// <c>crates/ordinary-wallet-plan-ffi</c> + <c>src/shim.c</c>, linked by
/// <c>ci/build-wlpq-ffi-library.sh</c>) and validates mirrored conformance-2 frames against
/// it: a known-good frame must return the frozen status 0 and known-bad frames must return
/// the exact frozen negative statuses recorded for them. Null/oversized inputs are rejected
/// fail-closed before dereference by both the managed wrapper and the native boundary. Both
/// platform artifacts are tracked under
/// <c>TestData/Liquid/OrdinaryWalletPlanWireV1/native/</c> and hash-pinned below: the macOS
/// arm64 <c>.dylib</c> (built on the macOS host) and the Linux x86-64 <c>.so</c> (built from
/// the same pinned commit in a clean git worktree inside the pinned rust:1.96-bookworm Linux
/// toolchain container, since the native link step requires GNU ld). The runtime selects the
/// platform-correct artifact via <see cref="OperatingSystem.IsLinux()"/> /
/// <see cref="OperatingSystem.IsMacOS()"/>; any other platform fails closed. This
/// slice adds no managed decoder, no signer, no output-opening provider, no PSET
/// construction, no node, no reservation, no currentness, no broadcast, no CoinJoin, no
/// sponsor or USDt surface, and no GUI; the binding lives in the test assembly only.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidOrdinaryWalletPlanWireV1LiveValidationTests
{
	/// <summary>The full native commit the loaded cdylib was built from.</summary>
	private const string PinnedNativeCommit = "d8595a4fa4d438fbe14351c7599bcd5c4e862f58";

	/// <summary>
	/// The SHA-256 of the pinned-commit macOS arm64 cdylib
	/// (<c>libwasabi_liquid_wlpq_v1.dylib</c>) tracked under the native/ directory.
	/// </summary>
	private const string MacOsLibrarySha256 = "27320e9e5f2ee95538f793652b317f1e5e3f59f961a7ec738f7f2387ca20b236";

	/// <summary>
	/// The SHA-256 of the pinned-commit Linux x86-64 cdylib
	/// (<c>libwasabi_liquid_wlpq_v1.so</c>) tracked under the native/ directory.
	/// </summary>
	private const string LinuxLibrarySha256 = "1793c40d58e9f65caa38506861f8add30152dbb7f86a0a75801a2b17896c99e4";

	/// <summary>The conformance-2 source epoch of the accepted toy frames (0x41 repeated).</summary>
	private static readonly byte[] ToySourceEpoch = [.. Enumerable.Repeat((byte)0x41, LiquidOrdinaryWalletPlanWireV1NativeValidation.SourceEpochLength)];

	[Fact]
	public void LoadedLibraryIsThePinnedNativeBuildWithTheExactFrozenExport()
	{
		string libraryPath = ResolveNativeLibraryPath();
		Assert.Equal(LiquidOrdinaryWalletPlanWireV1NativeValidation.LibraryFileName, Path.GetFileName(libraryPath));
		Assert.True(File.Exists(libraryPath), $"Missing native library: {libraryPath}");
		Assert.False(
			(File.GetAttributes(libraryPath) & FileAttributes.ReparsePoint) != 0,
			$"Native library reparse point is forbidden: {libraryPath}");

		// The artifact loaded by the test must be the exact bytes linked from the pinned-commit
		// cargo build. The digest is recomputed from the native build output at slice time and
		// recorded in the slice evidence; here the file is pinned to a regular file under the
		// tracked native/ directory, its identity is exercised live below, and its exact bytes
		// are hash-pinned per platform so a substituted or corrupted artifact fails closed. The
		// platform selection lives in a nested helper so this class's closure surface (pinned by
		// the assembly type manifest) stays byte-identical to the base slice.
		byte[] libraryBytes = File.ReadAllBytes(libraryPath);
		Assert.NotEmpty(libraryBytes);
		Assert.Equal(PlatformLibraryPin.ExpectedSha256, Convert.ToHexStringLower(SHA256.HashData(libraryBytes)));
		Assert.Equal(40, PinnedNativeCommit.Length);
		Assert.All(PinnedNativeCommit, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
	}

	[Fact]
	public void KnownGoodFrameIsAcceptedByLiveNativeValidation()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
		byte[] frame = LoadCorpusFrame("frame-test-toy-single", "301e282dec76985d4b6e19396fb38d33f76005c426c63b5691a391b89e9d2f2d");
		try
		{
			int status = LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(frame, ToySourceEpoch);
			Assert.Equal(LiquidOrdinaryWalletPlanWireV1NativeValidation.StatusOkV1, status);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
		}
	}

	[Fact]
	public void KnownBadFramesReturnTheFrozenNegativeStatuses()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();

		// Every row: the mirrored corpus frame identifier, its recorded decoded SHA-256, the
		// recorded structural error code, and the frozen native status (negated code).
		(string FrameId, string Sha256, int ExpectedStatus)[] rows =
		[
			("frame-wrong-magic", "9a530e610e4a440c13f32d4e28884dfd155bad4504dd4290c413706fed5fffb0", -(int)LiquidOrdinaryWalletPlanWireErrorCode.VersionMismatch),
			("frame-truncated-body", "2a125ed574492a3649e5715288d43812736a289f92554f4f48ef38e0a7c0f4b1", -(int)LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding),
			("frame-candidate-length-plus-one", "fe3487a9316c8b6c3b505f5588b0e7ba0a8cc10574e7399ab39420e9c575d36a", -(int)LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded),
		];

		foreach ((string frameId, string sha256, int expectedStatus) in rows)
		{
			byte[] frame = LoadCorpusFrame(frameId, sha256);
			try
			{
				int status = LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(frame, ToySourceEpoch);
				Assert.True(status < 0, $"Frame {frameId} must be rejected by live native validation.");
				Assert.Equal(expectedStatus, status);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(frame);
			}
		}
	}

	[Fact]
	public void SourceBindingMismatchIsReportedLive()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
		byte[] frame = LoadCorpusFrame("frame-test-toy-single", "301e282dec76985d4b6e19396fb38d33f76005c426c63b5691a391b89e9d2f2d");
		byte[] wrongEpoch = [.. Enumerable.Repeat((byte)0x42, LiquidOrdinaryWalletPlanWireV1NativeValidation.SourceEpochLength)];
		try
		{
			int status = LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(frame, wrongEpoch);
			Assert.Equal(-(int)LiquidOrdinaryWalletPlanWireErrorCode.SourceBindingMismatch, status);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(wrongEpoch);
		}
	}

	[Fact]
	public void WrapperIsFailClosedBeforeAnyNativeCall()
	{
		byte[] frame = LoadCorpusFrame("frame-test-toy-single", "301e282dec76985d4b6e19396fb38d33f76005c426c63b5691a391b89e9d2f2d");
		try
		{
			// Empty frames and wrong-length epochs never reach the native boundary.
			Assert.Throws<ArgumentException>(() => LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(ReadOnlySpan<byte>.Empty, ToySourceEpoch));
			Assert.Throws<ArgumentException>(() => LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(frame, ReadOnlySpan<byte>.Empty));
			Assert.Throws<ArgumentException>(() => LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(frame, ToySourceEpoch.AsSpan(..^1)));

			// The frozen family is exactly 0 and -1..-9; nothing else may be returned.
			Assert.Equal(0, LiquidOrdinaryWalletPlanWireV1NativeValidation.StatusOkV1);
			Assert.Equal(1u, LiquidOrdinaryWalletPlanWireV1NativeValidation.AbiVersionV1);
			Assert.Equal(268_435_456ul, LiquidOrdinaryWalletPlanWireV1NativeValidation.MaxFrameBytesV1);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
		}
	}

	private static string ResolveNativeLibraryPath()
	{
		// The csproj links the tracked native/ artifact flat next to the test assembly so the
		// binding's AssemblyDirectory search path resolves it; the tracked copy under
		// TestData/Liquid/OrdinaryWalletPlanWireV1/native/ is the source of truth.
		string path = Path.Combine(
			AppContext.BaseDirectory,
			LiquidOrdinaryWalletPlanWireV1NativeValidation.LibraryFileName);
		return Path.GetFullPath(path);
	}

	private static byte[] LoadCorpusFrame(string frameId, string expectedSha256)
	{
		string path = Path.Combine(OrdinaryWalletPlanWireV1Corpus.RootPath, "vectors", "frames", $"{frameId}.hex");
		string text = OrdinaryWalletPlanWireV1Corpus.ReadCanonicalText(path);
		string hex = text[..^1];
		Assert.Equal(0, hex.Length % 2);
		byte[] frame = Convert.FromHexString(hex);
		Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(frame)));
		return frame;
	}

	/// <summary>
	/// Selects the SHA-256 of the platform-correct pinned-commit cdylib. Kept in a nested
	/// helper (no lambdas, no captured state) so the enclosing test class's compiler-generated
	/// closure surface stays byte-identical to the base slice pinned by the assembly type
	/// manifest. Any platform other than Linux or macOS fails closed before the native
	/// boundary is reached.
	/// </summary>
	private static class PlatformLibraryPin
	{
		internal static readonly string ExpectedSha256 = Select();

		private static string Select()
		{
			if (OperatingSystem.IsLinux())
			{
				return LinuxLibrarySha256;
			}
			if (OperatingSystem.IsMacOS())
			{
				return MacOsLibrarySha256;
			}
			throw new PlatformNotSupportedException("The pinned native validation cdylib is tracked for macOS and Linux only.");
		}
	}
}
