using System.Runtime.InteropServices;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

/// <summary>
/// MANAGED-WLPQ-LIVE-CALL-001: minimal test-only managed binding to the native
/// <c>wln_wlpq_validate_v1</c> boundary. The declaration matches the checked-in C header
/// <c>crates/ordinary-wallet-plan-ffi/include/wasabi_liquid_wlpq_v1.h</c> in
/// wasabi-liquid-native exactly:
/// <c>int32_t wln_wlpq_validate_v1(const uint8_t *frame, uint64_t frame_length, const uint8_t *expected_source_epoch)</c>.
/// Both buffers are borrowed and must remain readable and immutable for the call. The native
/// side rejects null pointers and frame lengths above the WLPQ outer cap (268435456) before
/// dereference; this binding adds no authority of its own. Frozen return statuses: 0 accepted
/// and byte-identical after native decode/re-encode; -1..-8 the negated native WLPQ error
/// codes; -9 an internal panic or decode/re-encode mismatch. This is a development binding
/// only: no signer, output-opening provider, PSET construction, node, reservation,
/// currentness, broadcast, CoinJoin, sponsor, USDt, or release authority is introduced, and
/// no managed production type references it.
/// </summary>
internal static unsafe partial class LiquidOrdinaryWalletPlanWireV1NativeValidation
{
	/// <summary>The frozen WLPQ FFI ABI version.</summary>
	internal const uint AbiVersionV1 = 1;

	/// <summary>The WLPQ outer frame cap enforced before borrowed memory is read.</summary>
	internal const ulong MaxFrameBytesV1 = 268_435_456;

	/// <summary>Accepted and byte-identical after native decode/re-encode.</summary>
	internal const int StatusOkV1 = 0;

	/// <summary>The exact length of the borrowed source epoch buffer.</summary>
	internal const int SourceEpochLength = 32;

	/// <summary>The dynamic library file name produced by the pinned native build.</summary>
	internal static string LibraryFileName =>
		OperatingSystem.IsWindows() ? "wasabi_liquid_wlpq_v1.dll" :
		OperatingSystem.IsLinux() ? "libwasabi_liquid_wlpq_v1.so" :
		"libwasabi_liquid_wlpq_v1.dylib";

	/// <summary>
	/// Validates one canonical WLPQ v1 frame against an exact 32-byte source epoch by a live
	/// call into the native validation boundary. The frame and epoch spans are pinned for the
	/// duration of the call and stay caller-owned; the native side copies both into
	/// native-owned storage and clears those copies on return. A return value of
	/// <see cref="StatusOkV1"/> proves the native decoder accepted the frame and re-encoded it
	/// byte-for-byte; it does not prepare, open, sign, finalize, or broadcast anything.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// The frame is empty or longer than <see cref="MaxFrameBytesV1"/>, or the source epoch is
	/// not exactly <see cref="SourceEpochLength"/> bytes. The same rejections are enforced
	/// natively; the managed checks only keep the borrowed-pointer contract fail-closed.
	/// </exception>
	internal static int Validate(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> expectedSourceEpoch)
	{
		if (frame.IsEmpty || (ulong)frame.Length > MaxFrameBytesV1)
		{
			throw new ArgumentException("The frame length must be in 1..=268435456.", nameof(frame));
		}
		if (expectedSourceEpoch.Length != SourceEpochLength)
		{
			throw new ArgumentException("The source epoch must be exactly 32 bytes.", nameof(expectedSourceEpoch));
		}

		fixed (byte* framePointer = frame)
		fixed (byte* epochPointer = expectedSourceEpoch)
		{
			return NativeMethods.wln_wlpq_validate_v1(framePointer, (ulong)frame.Length, epochPointer);
		}
	}

	/// <summary>
	/// The library name passed to the loader. The pinned native build produces
	/// <c>libwasabi_liquid_wlpq_v1.dylib</c> (macOS) and <c>libwasabi_liquid_wlpq_v1.so</c>
	/// (Linux); the runtime resolves the per-host extension from this base name.
	/// </summary>
	private const string LibraryName = "libwasabi_liquid_wlpq_v1";

	private static partial class NativeMethods
	{
		// int32_t wln_wlpq_validate_v1(const uint8_t *frame, uint64_t frame_length, const uint8_t *expected_source_epoch)
		[LibraryImport(LibraryName, EntryPoint = "wln_wlpq_validate_v1")]
		[DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
		internal static partial int wln_wlpq_validate_v1(byte* frame, ulong frameLength, byte* expectedSourceEpoch);
	}
}
