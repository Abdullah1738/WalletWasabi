using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-SIGN-FFI-001: the production managed binding to the native
/// <c>wln_wlpq_sign_finalize_v1</c> boundary. The declaration matches the checked-in C header
/// <c>crates/ordinary-wallet-plan-ffi/include/wasabi_liquid_wlpq_v1.h</c> in
/// wasabi-liquid-native exactly (the amended fifteen-parameter shape): one borrowed canonical
/// WLPQ v1 frame, one borrowed 32-byte source epoch, an opaque caller-owned signer context
/// threaded unchanged into both callbacks, the two caller-owned signing callbacks (the C ABI
/// projection of the native <c>OrdinaryP2wpkhSigner</c> trait), a caller-provided output buffer
/// and length receiver, the caller-owned public spend descriptor text plus its highest derived
/// index, the borrowed 32-byte SLIP-77 master blinding key, and the trailing caller-supplied
/// 32-byte CSPRNG entropy seed pair. The signer stays caller-owned: only compressed public keys
/// and digest signatures cross the callback boundary and the native side never receives, copies,
/// or stores a secret key. The digest is computed natively (sighash-with-rangeproof,
/// <c>EcdsaSighashType::AllPlusRangeproof</c>); this binding computes no sighash and re-implements
/// no consensus logic. Frozen return statuses: <c>0</c> success; <c>-1..-9</c> the shared
/// decode/prepare rejections; <c>-10</c> a signer callback refused; <c>-11</c> the canonical
/// ordinary-PSET transition rejected a public key or signature; <c>-12</c> the output capacity
/// was too small (the required length is still reported). This binding performs no node contact,
/// no RPC, no broadcast, no key generation or storage, and no caching.
/// </summary>
internal static unsafe partial class LiquidWalletNativeSigningBinding
{
	/// <summary>The frozen WLPQ FFI ABI version.</summary>
	internal const uint AbiVersionV1 = 1;

	/// <summary>The WLPQ outer frame cap enforced before borrowed memory is read.</summary>
	internal const ulong MaxFrameBytesV1 = 268_435_456;

	/// <summary>Successful sign-and-finalize.</summary>
	internal const int StatusOkV1 = 0;

	/// <summary>A signer callback returned the refusal code.</summary>
	internal const int StatusSignerRefusedV1 = -10;

	/// <summary>The canonical ordinary-PSET transition rejected a public key or signature.</summary>
	internal const int StatusSigningRejectedV1 = -11;

	/// <summary>The output buffer capacity was too small; the required length is reported.</summary>
	internal const int StatusOutputCapacityV1 = -12;

	/// <summary>The exact length of the borrowed source epoch buffer.</summary>
	internal const int SourceEpochLength = 32;

	/// <summary>The exact length of the borrowed SLIP-77 master blinding key.</summary>
	internal const int Slip77MasterKeyLength = 32;

	/// <summary>The exact length of the caller-supplied CSPRNG entropy seed.</summary>
	internal const int EntropyLength = 32;

	/// <summary>The exact byte length of one consensus-serialized callback outpoint.</summary>
	internal const int OutPointBytesV1 = 36;

	/// <summary>The exact byte length of one compressed secp256k1 public key.</summary>
	internal const int PublicKeyBytesV1 = 33;

	/// <summary>The caller-provided signature output buffer capacity (strict-DER + sighash byte).</summary>
	internal const int SignatureCapacityV1 = 73;

	/// <summary>
	/// The initial output buffer capacity for the finalized transaction serialization. On
	/// <see cref="StatusOutputCapacityV1"/> the call is retried once against the native-reported
	/// required length, so a confidential transaction larger than this still succeeds.
	/// </summary>
	internal const int InitialOutputCapacity = 1 << 20;

	/// <summary>The full native commit the pinned cdylib was built from.</summary>
	internal const string PinnedNativeCommit = "4486ae3f4b85064096df2ef27b0434bd227b639a";

	/// <summary>
	/// The SHA-256 of the pinned-commit macOS arm64 cdylib
	/// (<c>libwasabi_liquid_wlpq_v1.dylib</c>) tracked under the production Native/ directory.
	/// </summary>
	internal const string MacOsLibrarySha256 = "8c2e5f116f72cf049c873fe8f661723c20862db4b69c458fbb69feffbd2f28f2";

	/// <summary>
	/// The SHA-256 of the pinned-commit Linux x86-64 cdylib
	/// (<c>libwasabi_liquid_wlpq_v1.so</c>) tracked under the production Native/ directory.
	/// </summary>
	internal const string LinuxLibrarySha256 = "5e2a6f2e3aa954036597f11a11e2b09f1b7160780e0c051694bcda0cf599584a";

	/// <summary>
	/// The dynamic library file name produced by the pinned native build.
	/// </summary>
	internal static string LibraryFileName =>
		OperatingSystem.IsWindows() ? "wasabi_liquid_wlpq_v1.dll" :
		OperatingSystem.IsLinux() ? "libwasabi_liquid_wlpq_v1.so" :
		"libwasabi_liquid_wlpq_v1.dylib";

	/// <summary>
	/// The production artifact is resolved from this dedicated subdirectory next to the assembly
	/// (not the flat assembly directory) so it never collides with the test-only validation
	/// cdylib the test assembly links flat under the same file name.
	/// </summary>
	internal const string ArtifactSubdirectory = "NativeSigning";

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
				$"The pinned native signing cdylib is missing: {libraryPath}");
		}
		if ((File.GetAttributes(libraryPath) & FileAttributes.ReparsePoint) != 0)
		{
			throw new PlatformNotSupportedException(
				$"The pinned native signing cdylib reparse point is forbidden: {libraryPath}");
		}

		byte[] libraryBytes = File.ReadAllBytes(libraryPath);
		string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(libraryBytes));
		if (!StringComparer.Ordinal.Equals(actualSha256, PlatformLibraryPin.ExpectedSha256))
		{
			throw new PlatformNotSupportedException(
				"The pinned native signing cdylib hash does not match the production pin.");
		}
	}

	/// <summary>
	/// Signs and finalizes one canonical WLPQ v1 frame by a live call into the native signing
	/// boundary. All borrowed buffers are pinned for the duration of the call and stay
	/// caller-owned; the native side copies the frame, epoch, SLIP-77 master, and entropy seed
	/// into native-owned storage and clears those copies on return. The two unmanaged
	/// function-pointer callbacks are the exact C ABI projection of the native
	/// <c>OrdinaryP2wpkhSigner</c> trait. Returns the native status; on
	/// <see cref="StatusOkV1"/> the finalized transaction bytes are written into
	/// <paramref name="outTransaction"/> and the byte length into
	/// <paramref name="outTransactionLength"/>.
	/// </summary>
	internal static int SignFinalize(
		ReadOnlySpan<byte> frame,
		ReadOnlySpan<byte> expectedSourceEpoch,
		IntPtr signerContext,
		delegate* unmanaged[Cdecl]<byte*, byte*, ulong, byte*, ulong, int> publicKeyCallback,
		delegate* unmanaged[Cdecl]<byte*, byte*, ulong, byte*, byte*, ulong, int> signDigestCallback,
		Span<byte> outTransaction,
		out ulong outTransactionLength,
		ReadOnlySpan<byte> descriptor,
		ulong lastIndex,
		ReadOnlySpan<byte> slip77MasterKey,
		ReadOnlySpan<byte> entropy)
	{
		outTransactionLength = 0;
		if (frame.IsEmpty || (ulong)frame.Length > MaxFrameBytesV1)
		{
			throw new ArgumentException("The frame length must be in 1..=268435456.", nameof(frame));
		}
		if (expectedSourceEpoch.Length != SourceEpochLength)
		{
			throw new ArgumentException("The source epoch must be exactly 32 bytes.", nameof(expectedSourceEpoch));
		}
		if (descriptor.IsEmpty)
		{
			throw new ArgumentException("The public spend descriptor must be non-empty.", nameof(descriptor));
		}
		if (slip77MasterKey.Length != Slip77MasterKeyLength)
		{
			throw new ArgumentException("The SLIP-77 master key must be exactly 32 bytes.", nameof(slip77MasterKey));
		}
		if (entropy.Length != EntropyLength)
		{
			throw new ArgumentException("The entropy seed must be exactly 32 bytes.", nameof(entropy));
		}

		fixed (byte* framePointer = frame)
		fixed (byte* epochPointer = expectedSourceEpoch)
		fixed (byte* outPointer = outTransaction)
		fixed (byte* descriptorPointer = descriptor)
		fixed (byte* slip77Pointer = slip77MasterKey)
		fixed (byte* entropyPointer = entropy)
		{
			ulong length = 0;
			int status = ((delegate* unmanaged[Cdecl]<
				byte*, ulong, byte*, IntPtr,
				delegate* unmanaged[Cdecl]<byte*, byte*, ulong, byte*, ulong, int>,
				delegate* unmanaged[Cdecl]<byte*, byte*, ulong, byte*, byte*, ulong, int>,
				byte*, ulong, ulong*, byte*, ulong, ulong, byte*, byte*, ulong, int>)NativeEntryPointAddress.Value)(
				framePointer,
				(ulong)frame.Length,
				epochPointer,
				signerContext,
				publicKeyCallback,
				signDigestCallback,
				outPointer,
				(ulong)outTransaction.Length,
				&length,
				descriptorPointer,
				(ulong)descriptor.Length,
				lastIndex,
				slip77Pointer,
				entropyPointer,
				(ulong)entropy.Length);
			outTransactionLength = length;
			return status;
		}
	}

	/// <summary>
	/// The lazily resolved native export address. The pinned cdylib is loaded once from its exact
	/// hash-pinned path via <see cref="NativeLibrary.Load(string)"/> and the
	/// <c>wln_wlpq_sign_finalize_v1</c> export address is bound; the caller casts it to the exact
	/// unmanaged function-pointer type. The artifact hash is verified before the load; any failure
	/// surfaces as a <see cref="PlatformNotSupportedException"/>.
	/// </summary>
	private static readonly Lazy<IntPtr> NativeEntryPointAddress = new(LoadEntryPointAddress);

	private static IntPtr LoadEntryPointAddress()
	{
		EnsurePinnedNativeArtifact();
		IntPtr handle = NativeLibrary.Load(ResolveLibraryPath());
		return NativeLibrary.GetExport(handle, "wln_wlpq_sign_finalize_v1");
	}

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
			if (OperatingSystem.IsLinux())
			{
				return LinuxLibrarySha256;
			}
			if (OperatingSystem.IsMacOS())
			{
				return MacOsLibrarySha256;
			}
			throw new PlatformNotSupportedException(
				"The pinned native signing cdylib is tracked for macOS and Linux only.");
		}
	}
}
