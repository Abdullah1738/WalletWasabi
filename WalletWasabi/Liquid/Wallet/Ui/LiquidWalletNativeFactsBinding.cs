using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-FACTS-FFI-001: the production managed binding to the native
/// <c>wln_wallet_facts_observe_v1</c> boundary. The declaration matches the checked-in C header
/// <c>crates/wallet-facts-ffi/include/wasabi_liquid_wallet_facts_v1.h</c> in wasabi-liquid-native
/// exactly: one borrowed canonical WLFQ v1 request frame, one borrowed 32-byte source epoch, one
/// borrowed 32-byte SLIP-77 master blinding key, a caller-provided output buffer and length
/// receiver, and the trailing caller-supplied 32-byte CSPRNG entropy seed. The caller retains
/// every input; the native side copies what it needs into native-owned storage and clears those
/// copies on return. A null output with zero capacity is a full capacity query; only
/// <see cref="StatusOkV1"/> and <see cref="StatusOutputCapacityV1"/> publish a nonzero response
/// length. This binding computes no observation, derives no descriptor or SLIP-77 key, blinds or
/// unblinds nothing, and re-implements no consensus logic: the native export runs the full
/// <c>wallet-facts::observe_owned_outputs</c> + <c>wallet-facts-wire::encode_response</c>
/// pipeline. Frozen return statuses: <c>0</c> success; <c>-1..-8</c> the shared wire rejections
/// (numerically <c>-(wire error code)</c>); <c>-9</c> a contained native invariant fault;
/// <c>-10</c> the output capacity was too small (the required length is still reported). This
/// binding performs no node contact, no RPC, no broadcast, no key generation or storage, and no
/// caching.
/// </summary>
internal static unsafe partial class LiquidWalletNativeFactsBinding
{
	/// <summary>The frozen wallet-facts FFI ABI version.</summary>
	internal const uint AbiVersionV1 = 1;

	/// <summary>The WLFQ outer request cap enforced before borrowed memory is read.</summary>
	internal const ulong MaxRequestFrameBytesV1 = 268_435_456;

	/// <summary>The WLFV outer response cap.</summary>
	internal const ulong MaxResponseFrameBytesV1 = 268_435_456;

	/// <summary>The largest response reachable under the frozen WLFV v1 limits.</summary>
	internal const ulong MaxReachableResponseBytesV1 = 80_599_492;

	/// <summary>The exact length of the borrowed source epoch buffer.</summary>
	internal const int SourceEpochLength = 32;

	/// <summary>The exact length of the borrowed SLIP-77 master blinding key.</summary>
	internal const int Slip77MasterKeyLength = 32;

	/// <summary>The exact length of the caller-supplied CSPRNG entropy seed.</summary>
	internal const int EntropyLength = 32;

	/// <summary>
	/// The exact byte length of the smallest canonical WLFV v1 response: the 64-byte header of an
	/// empty response (proven natively by the capacity query on the empty request corpus frame).
	/// </summary>
	internal const int MinimumResponseFrameBytes = 64;

	/// <summary>Successful observation; the complete response was copied.</summary>
	internal const int StatusOkV1 = 0;

	/// <summary>A pointer, length, or capacity shape is invalid.</summary>
	internal const int StatusInvalidArgumentV1 = -1;

	/// <summary>The WLFQ magic, version, or header is unsupported.</summary>
	internal const int StatusVersionMismatchV1 = -2;

	/// <summary>The WLFQ encoding is malformed or noncanonical.</summary>
	internal const int StatusInvalidEncodingV1 = -3;

	/// <summary>A frozen request or response limit was exceeded.</summary>
	internal const int StatusLimitExceededV1 = -4;

	/// <summary>Descriptor derivation rejected the request.</summary>
	internal const int StatusDescriptorRejectedV1 = -5;

	/// <summary>Candidate construction rejected the request.</summary>
	internal const int StatusCandidateRejectedV1 = -6;

	/// <summary>The full native observation rejected the candidate batch.</summary>
	internal const int StatusObservationRejectedV1 = -7;

	/// <summary>The supplied source binding does not match.</summary>
	internal const int StatusSourceBindingMismatchV1 = -8;

	/// <summary>A contained native invariant fault; FFI-boundary-only, never a wire code.</summary>
	internal const int StatusInternalErrorV1 = -9;

	/// <summary>
	/// The output buffer is absent or too small; the required length is still published.
	/// FFI-boundary-only, never a wire code.
	/// </summary>
	internal const int StatusOutputCapacityV1 = -10;

	/// <summary>The full native commit the pinned cdylib was built from.</summary>
	internal const string PinnedNativeCommit = "1e7fe02b52faa9681e31045e2d06cec1de9bbb29";

	/// <summary>
	/// The SHA-256 of the pinned-commit macOS arm64 cdylib
	/// (<c>libwasabi_liquid_wallet_facts_v1.dylib</c>) tracked under the production Native/
	/// directory.
	/// </summary>
	internal const string MacOsLibrarySha256 = "d95b02aa4da9a28fb1acc2cb72fab70f007c9cbe6dc4f7cea3f76af13aa77df9";

	/// <summary>
	/// The dynamic library file name produced by the pinned native build. Only macOS is
	/// currently pinned: the Linux cdylib for this commit has not been rebuilt on a Linux host
	/// yet, so <see cref="EnsurePinnedNativeArtifact"/> fails closed off macOS. The Windows
	/// branch is forward parity only; no Windows cdylib is built or pinned.
	/// </summary>
	internal static string LibraryFileName =>
		OperatingSystem.IsMacOS() ? "libwasabi_liquid_wallet_facts_v1.dylib" :
		OperatingSystem.IsLinux() ? "libwasabi_liquid_wallet_facts_v1.so" :
		"wasabi_liquid_wallet_facts_v1.dll";

	/// <summary>
	/// The production artifact is resolved from this dedicated subdirectory next to the assembly
	/// (not the flat assembly directory) so it never collides with the signing cdylib family or
	/// any test-flat cdylib.
	/// </summary>
	internal const string ArtifactSubdirectory = "NativeWalletFacts";

	/// <summary>The full path of the platform-correct pinned production cdylib.</summary>
	internal static string ResolveLibraryPath() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ArtifactSubdirectory, LibraryFileName));

	/// <summary>
	/// Verifies that the platform-correct pinned-commit cdylib is the exact tracked, hash-pinned
	/// build before any native call is attempted: the file must exist as a regular file (no
	/// reparse point) and its recomputed SHA-256 must equal the production pin for the current
	/// platform. Any platform other than Linux or macOS, a missing file, a reparse point, or a
	/// hash mismatch fails closed with <see cref="PlatformNotSupportedException"/> before the
	/// native boundary is reached.
	/// </summary>
	internal static void EnsurePinnedNativeArtifact()
	{
		string libraryPath = ResolveLibraryPath();
		if (!File.Exists(libraryPath))
		{
			throw new PlatformNotSupportedException(
				$"The pinned native wallet-facts cdylib is missing: {libraryPath}");
		}
		if ((File.GetAttributes(libraryPath) & FileAttributes.ReparsePoint) != 0)
		{
			throw new PlatformNotSupportedException(
				$"The pinned native wallet-facts cdylib reparse point is forbidden: {libraryPath}");
		}

		byte[] libraryBytes = File.ReadAllBytes(libraryPath);
		string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(libraryBytes));
		if (!StringComparer.Ordinal.Equals(actualSha256, PlatformLibraryPin.ExpectedSha256))
		{
			throw new PlatformNotSupportedException(
				"The pinned native wallet-facts cdylib hash does not match the production pin.");
		}
	}

	/// <summary>
	/// Fully observes one canonical WLFQ v1 request frame by a live call into the native
	/// observation boundary and, on <see cref="StatusOkV1"/>, writes the canonical WLFV v1
	/// response. All borrowed buffers are pinned for the duration of the call and stay
	/// caller-owned. An empty <paramref name="outResponse"/> is the capacity query: a genuine
	/// null pointer with zero capacity is passed and the native side publishes the required
	/// length under <see cref="StatusOutputCapacityV1"/>. Returns the native status; only
	/// <see cref="StatusOkV1"/> and <see cref="StatusOutputCapacityV1"/> publish a nonzero
	/// <paramref name="outResponseLength"/>.
	/// </summary>
	internal static int Observe(
		ReadOnlySpan<byte> requestFrame,
		ReadOnlySpan<byte> expectedSourceEpoch,
		ReadOnlySpan<byte> slip77MasterKey,
		Span<byte> outResponse,
		out ulong outResponseLength,
		ReadOnlySpan<byte> entropy)
	{
		outResponseLength = 0;
		if (requestFrame.IsEmpty || (ulong)requestFrame.Length > MaxRequestFrameBytesV1)
		{
			throw new ArgumentException("The request frame length must be in 1..=268435456.", nameof(requestFrame));
		}
		if (expectedSourceEpoch.Length != SourceEpochLength)
		{
			throw new ArgumentException("The source epoch must be exactly 32 bytes.", nameof(expectedSourceEpoch));
		}
		if (slip77MasterKey.Length != Slip77MasterKeyLength)
		{
			throw new ArgumentException("The SLIP-77 master key must be exactly 32 bytes.", nameof(slip77MasterKey));
		}
		if (entropy.Length != EntropyLength)
		{
			throw new ArgumentException("The entropy seed must be exactly 32 bytes.", nameof(entropy));
		}

		fixed (byte* framePointer = requestFrame)
		fixed (byte* epochPointer = expectedSourceEpoch)
		fixed (byte* slip77Pointer = slip77MasterKey)
		fixed (byte* entropyPointer = entropy)
		{
			ulong length = 0;
			int status;
			if (outResponse.IsEmpty)
			{
				// The capacity query must pass a genuine null output pointer with zero capacity
				// rather than whatever fixed produces for an empty span.
				status = ((delegate* unmanaged[Cdecl]<
					byte*, ulong, byte*, byte*, byte*, ulong, ulong*, byte*, ulong, int>)NativeEntryPointAddress.Value)(
					framePointer,
					(ulong)requestFrame.Length,
					epochPointer,
					slip77Pointer,
					null,
					0,
					&length,
					entropyPointer,
					(ulong)entropy.Length);
			}
			else
			{
				fixed (byte* outPointer = outResponse)
				{
					status = ((delegate* unmanaged[Cdecl]<
						byte*, ulong, byte*, byte*, byte*, ulong, ulong*, byte*, ulong, int>)NativeEntryPointAddress.Value)(
						framePointer,
						(ulong)requestFrame.Length,
						epochPointer,
						slip77Pointer,
						outPointer,
						(ulong)outResponse.Length,
						&length,
						entropyPointer,
						(ulong)entropy.Length);
				}
			}
			outResponseLength = length;
			return status;
		}
	}

	/// <summary>
	/// The lazily resolved native library handle. The pinned cdylib is loaded once from its exact
	/// hash-pinned path via <see cref="NativeLibrary.Load(string)"/> after the artifact hash is
	/// verified; the single export below is bound from this handle. The artifact hash is verified
	/// before the load; any failure surfaces as a <see cref="PlatformNotSupportedException"/>.
	/// </summary>
	private static readonly Lazy<IntPtr> NativeLibraryHandle = new(LoadNativeLibraryHandle);

	/// <summary>
	/// The lazily resolved <c>wln_wallet_facts_observe_v1</c> export address, bound from
	/// <see cref="NativeLibraryHandle"/>; the caller casts it to the exact unmanaged
	/// function-pointer type.
	/// </summary>
	private static readonly Lazy<IntPtr> NativeEntryPointAddress = new(LoadEntryPointAddress);

	private static IntPtr LoadNativeLibraryHandle()
	{
		EnsurePinnedNativeArtifact();
		return NativeLibrary.Load(ResolveLibraryPath());
	}

	private static IntPtr LoadEntryPointAddress() =>
		NativeLibrary.GetExport(NativeLibraryHandle.Value, "wln_wallet_facts_observe_v1");

	/// <summary>
	/// Selects the SHA-256 of the platform-correct pinned-commit cdylib. Kept in a nested helper
	/// (no lambdas, no captured state) so the enclosing binding's closure surface stays minimal.
	/// Any platform other than Linux or macOS fails closed before the native boundary is reached.
	/// </summary>
	private static class PlatformLibraryPin
	{
		internal static readonly string ExpectedSha256 = Select();

		private static string Select()
		{
			if (OperatingSystem.IsMacOS())
			{
				return MacOsLibrarySha256;
			}
			// The Linux cdylib for this pinned commit has not been rebuilt on a Linux host yet;
			// fail closed rather than trust a stale artifact from the prior pinned commit.
			throw new PlatformNotSupportedException(
				"The pinned native wallet-facts cdylib is tracked for macOS only on this commit.");
		}
	}
}
