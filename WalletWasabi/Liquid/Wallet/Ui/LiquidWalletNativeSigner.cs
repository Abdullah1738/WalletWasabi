using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-SIGN-FFI-001: the production native signing binding that makes the landed
/// <see cref="ILiquidWalletSigner"/> seam real. It wraps the caller-supplied key owner (the
/// Fluent-layer component that actually holds the keys and can produce public keys and digest
/// signatures) and drives the native <c>wln_wlpq_sign_finalize_v1</c> export through
/// <see cref="LiquidWalletNativeSigningBinding"/>. The binding never sees, stores, or logs a
/// secret key: it forwards the natively computed per-input sighash-with-rangeproof digest to
/// <see cref="ILiquidWalletSigner.SignDigestHex"/> and the outpoint to
/// <see cref="ILiquidWalletSigner.GetPublicKeyHex"/>. Only compressed public keys and digest
/// signatures cross the callback boundary; the native side validates every public key against
/// the exact previous-output script, rejects high-S signatures, and verifies every signature
/// before finalization. The public spend descriptor, its highest derived index, and the SLIP-77
/// master blinding key are caller-owned public/blinding material supplied at construction — no
/// spend secret crosses the ABI. The 32-byte entropy seed is a fresh
/// <see cref="RandomNumberGenerator"/> fill per call, pinned for the call and zeroed after. On
/// any nonzero native status the result is a fail-closed <see langword="false"/> with a
/// <see langword="null"/> signed transaction — no partial result, no retry, no fallback, no
/// caching. This binding performs no node contact, no RPC, no broadcast, and no sighash
/// computation in managed code.
/// </summary>
public sealed class LiquidWalletNativeSigner : ILiquidWalletSigner
{
	private readonly ILiquidWalletSigner _keyOwner;
	private readonly byte[] _descriptor;
	private readonly ulong _lastIndex;
	private readonly byte[] _slip77MasterKey;
	private readonly Func<byte[]>? _entropyOverride;

	private LiquidWalletNativeSigner(
		ILiquidWalletSigner keyOwner,
		byte[] descriptor,
		ulong lastIndex,
		byte[] slip77MasterKey,
		Func<byte[]>? entropyOverride)
	{
		_keyOwner = keyOwner;
		_descriptor = descriptor;
		_lastIndex = lastIndex;
		_slip77MasterKey = slip77MasterKey;
		_entropyOverride = entropyOverride;
	}

	/// <summary>
	/// Wraps the caller-supplied key owner plus the caller-owned public spend descriptor, its
	/// highest derived index, and the 32-byte SLIP-77 master blinding key. The descriptor text is
	/// UTF-8 encoded once and retained; the SLIP-77 master is blinding material, not a spend key.
	/// Fail-closed: a null key owner, an empty descriptor, or a non-32-byte SLIP-77 master throws.
	/// </summary>
	public static LiquidWalletNativeSigner Create(
		ILiquidWalletSigner keyOwner,
		string publicSpendDescriptor,
		ulong lastIndex,
		ReadOnlySpan<byte> slip77MasterKey)
	{
		ArgumentNullException.ThrowIfNull(keyOwner);
		ArgumentNullException.ThrowIfNull(publicSpendDescriptor);
		if (publicSpendDescriptor.Length == 0)
		{
			throw new ArgumentException("A non-empty public spend descriptor is required.", nameof(publicSpendDescriptor));
		}
		if (slip77MasterKey.Length != LiquidWalletNativeSigningBinding.Slip77MasterKeyLength)
		{
			throw new ArgumentException(
				"The SLIP-77 master blinding key must be exactly 32 bytes.", nameof(slip77MasterKey));
		}

		return new LiquidWalletNativeSigner(
			keyOwner,
			Encoding.UTF8.GetBytes(publicSpendDescriptor),
			lastIndex,
			slip77MasterKey.ToArray(),
			entropyOverride: null);
	}

	/// <summary>
	/// The internal entropy-injectable composition point, used only by the test matrix to pin a
	/// deterministic entropy seed so the produced transaction is byte-identical to the
	/// native-computed ground truth (txid/wtxid asserted). Production callers use
	/// <see cref="Create"/>, which supplies a fresh <see cref="RandomNumberGenerator"/> fill per
	/// call. The <paramref name="entropyOverride"/> must return exactly 32 bytes.
	/// </summary>
	internal static LiquidWalletNativeSigner CreateForTesting(
		ILiquidWalletSigner keyOwner,
		string publicSpendDescriptor,
		ulong lastIndex,
		ReadOnlySpan<byte> slip77MasterKey,
		Func<byte[]> entropyOverride)
	{
		ArgumentNullException.ThrowIfNull(keyOwner);
		ArgumentNullException.ThrowIfNull(publicSpendDescriptor);
		ArgumentNullException.ThrowIfNull(entropyOverride);
		if (publicSpendDescriptor.Length == 0)
		{
			throw new ArgumentException("A non-empty public spend descriptor is required.", nameof(publicSpendDescriptor));
		}
		if (slip77MasterKey.Length != LiquidWalletNativeSigningBinding.Slip77MasterKeyLength)
		{
			throw new ArgumentException(
				"The SLIP-77 master blinding key must be exactly 32 bytes.", nameof(slip77MasterKey));
		}

		return new LiquidWalletNativeSigner(
			keyOwner,
			Encoding.UTF8.GetBytes(publicSpendDescriptor),
			lastIndex,
			slip77MasterKey.ToArray(),
			entropyOverride);
	}

	/// <summary>
	/// The production entry point: decodes the request's wire frame and source epoch from hex,
	/// verifies the pinned native artifact, pins the buffers, fills a fresh 32-byte entropy seed
	/// via <see cref="RandomNumberGenerator.Fill(Span{byte})"/>, and calls the native
	/// sign-and-finalize export. The two managed callbacks translate the native outpoint into the
	/// seam's <c>OutPointHex</c> and forward to the key owner. On native status
	/// <c>0</c> the finalized transaction bytes are projected to a
	/// <see cref="LiquidWalletUiSignedTransaction"/>; on any nonzero status the result is a
	/// fail-closed <see langword="false"/> with a <see langword="null"/> signed transaction.
	/// </summary>
	public unsafe bool TrySignAndFinalize(
		LiquidWalletUiSignRequest request,
		out LiquidWalletUiSignedTransaction? signedTransaction)
	{
		signedTransaction = null;
		if (request is null)
		{
			return false;
		}

		byte[] frame;
		byte[] epoch;
		try
		{
			frame = Convert.FromHexString(request.WireFrameHex);
			epoch = Convert.FromHexString(request.SourceEpochHex);
		}
		catch (FormatException)
		{
			return false;
		}

		if (epoch.Length != LiquidWalletNativeSigningBinding.SourceEpochLength)
		{
			return false;
		}

		var entropy = new byte[LiquidWalletNativeSigningBinding.EntropyLength];
		try
		{
			LiquidWalletNativeSigningBinding.EnsurePinnedNativeArtifact();

			// The caller-supplied-entropy model: a fresh 32-byte CSPRNG seed per call, generated
			// immediately before the call, never reused, never derived from the frame/descriptor/
			// epoch/keys. The native side expands it through an approved-primitive DRBG. The
			// test-only override pins a deterministic seed for the ground-truth cross-check.
			FillEntropy(entropy);

			GCHandle contextHandle = GCHandle.Alloc(this, GCHandleType.Normal);
			try
			{
				IntPtr signerContext = GCHandle.ToIntPtr(contextHandle);
				var outTransaction = new byte[LiquidWalletNativeSigningBinding.InitialOutputCapacity];
				try
				{
					int status = LiquidWalletNativeSigningBinding.SignFinalize(
						frame,
						epoch,
						signerContext,
						&PublicKeyCallback,
						&SignDigestCallback,
						outTransaction,
						out ulong outTransactionLength,
						_descriptor,
						_lastIndex,
						_slip77MasterKey,
						entropy);

					if (status == LiquidWalletNativeSigningBinding.StatusOutputCapacityV1 &&
						outTransactionLength > 0 &&
						outTransactionLength <= LiquidWalletNativeSigningBinding.MaxFrameBytesV1)
					{
						// The output buffer was too small; the native side reported the required
						// length. Retry once against a correctly sized buffer with a fresh seed.
						CryptographicOperations.ZeroMemory(outTransaction);
						outTransaction = new byte[(int)outTransactionLength];
						FillEntropy(entropy);
						status = LiquidWalletNativeSigningBinding.SignFinalize(
							frame,
							epoch,
							signerContext,
							&PublicKeyCallback,
							&SignDigestCallback,
							outTransaction,
							out outTransactionLength,
							_descriptor,
							_lastIndex,
							_slip77MasterKey,
							entropy);
					}

					if (status != LiquidWalletNativeSigningBinding.StatusOkV1)
					{
						return false;
					}

					string signedTransactionHex = Convert.ToHexString(
						outTransaction.AsSpan(0, (int)outTransactionLength)).ToLowerInvariant();

					// The native export reports only the serialized transaction; the transaction
					// id is the double-SHA256 of the non-witness serialization. The container
					// carries the empty id when the signer does not report one; this binding does
					// not re-implement the consensus serializer to recover it.
					signedTransaction = LiquidWalletUiSignedTransaction.Create(
						request.NetworkManifestId,
						request.SourceRevision,
						signedTransactionHex,
						string.Empty);
					return true;
				}
				finally
				{
					CryptographicOperations.ZeroMemory(outTransaction);
				}
			}
			finally
			{
				contextHandle.Free();
			}
		}
		catch (PlatformNotSupportedException)
		{
			return false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(entropy);
		}
	}

	/// <summary>
	/// Fills the 32-byte entropy buffer: a fresh <see cref="RandomNumberGenerator"/> fill in
	/// production, or the test-only override when one is supplied. The override must return
	/// exactly 32 bytes; any other length is a fail-closed <see cref="InvalidOperationException"/>.
	/// </summary>
	private void FillEntropy(byte[] entropy)
	{
		if (_entropyOverride is null)
		{
			RandomNumberGenerator.Fill(entropy);
			return;
		}
		byte[] supplied = _entropyOverride();
		if (supplied.Length != entropy.Length)
		{
			throw new InvalidOperationException("The entropy seed must be exactly 32 bytes.");
		}
		supplied.CopyTo(entropy, 0);
	}

	/// <summary>
	/// Explicit seam implementation: delegates to the wrapped key owner so a
	/// <see cref="LiquidWalletNativeSigner"/> can itself be driven by the landed
	/// <see cref="LiquidWalletUiSigner.TrySign"/> seam driver unchanged.
	/// </summary>
	string? ILiquidWalletSigner.GetPublicKeyHex(string outPointHex) =>
		_keyOwner.GetPublicKeyHex(outPointHex);

	/// <summary>
	/// Explicit seam implementation: delegates to the wrapped key owner so a
	/// <see cref="LiquidWalletNativeSigner"/> can itself be driven by the landed
	/// <see cref="LiquidWalletUiSigner.TrySign"/> seam driver unchanged.
	/// </summary>
	string? ILiquidWalletSigner.SignDigestHex(string outPointHex, string digestHex) =>
		_keyOwner.SignDigestHex(outPointHex, digestHex);

	/// <summary>
	/// The native public-key callback: translates the borrowed 36-byte consensus outpoint into
	/// the seam's 72-char lowercase <c>OutPointHex</c>, forwards to the key owner, and writes the
	/// 33-byte compressed public key. Returns <c>0</c> on success; any refusal or malformed key is
	/// a fail-closed nonzero return surfaced natively as <c>-10</c>.
	/// </summary>
	[UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
	private static unsafe int PublicKeyCallback(
		byte* context,
		byte* outpoint,
		ulong outpointLength,
		byte* outPublicKey,
		ulong publicKeyCapacity)
	{
		if (context is null || outpoint is null || outPublicKey is null ||
			outpointLength != LiquidWalletNativeSigningBinding.OutPointBytesV1 ||
			publicKeyCapacity < LiquidWalletNativeSigningBinding.PublicKeyBytesV1)
		{
			return -1;
		}

		try
		{
			LiquidWalletNativeSigner self = (LiquidWalletNativeSigner)GCHandle.FromIntPtr((IntPtr)context).Target!;
			string outPointHex = Convert.ToHexStringLower(new ReadOnlySpan<byte>(outpoint, (int)outpointLength));
			string? publicKeyHex = self._keyOwner.GetPublicKeyHex(outPointHex);
			if (publicKeyHex is null || publicKeyHex.Length != LiquidWalletNativeSigningBinding.PublicKeyBytesV1 * 2)
			{
				return -1;
			}
			byte[] publicKey = Convert.FromHexString(publicKeyHex);
			new ReadOnlySpan<byte>(publicKey).CopyTo(new Span<byte>(outPublicKey, (int)publicKeyCapacity));
			CryptographicOperations.ZeroMemory(publicKey);
			return 0;
		}
		catch
		{
			return -1;
		}
	}

	/// <summary>
	/// The native digest-signature callback: translates the borrowed outpoint and the natively
	/// computed 32-byte digest into hex, forwards to the key owner, and writes the strict-DER
	/// low-S signature including the trailing sighash byte. Returns <c>0</c> on success; any
	/// refusal or malformed signature is a fail-closed nonzero return surfaced natively as
	/// <c>-10</c>.
	/// </summary>
	[UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
	private static unsafe int SignDigestCallback(
		byte* context,
		byte* outpoint,
		ulong outpointLength,
		byte* digest,
		byte* outSignature,
		ulong signatureCapacity)
	{
		if (context is null || outpoint is null || digest is null || outSignature is null ||
			outpointLength != LiquidWalletNativeSigningBinding.OutPointBytesV1 ||
			signatureCapacity < LiquidWalletNativeSigningBinding.SignatureCapacityV1)
		{
			return -1;
		}

		try
		{
			LiquidWalletNativeSigner self = (LiquidWalletNativeSigner)GCHandle.FromIntPtr((IntPtr)context).Target!;
			string outPointHex = Convert.ToHexStringLower(new ReadOnlySpan<byte>(outpoint, (int)outpointLength));
			string digestHex = Convert.ToHexStringLower(new ReadOnlySpan<byte>(digest, 32));
			string? signatureHex = self._keyOwner.SignDigestHex(outPointHex, digestHex);
			if (signatureHex is null || signatureHex.Length == 0 || signatureHex.Length % 2 != 0)
			{
				return -1;
			}
			byte[] signature = Convert.FromHexString(signatureHex);
			if (signature.Length == 0 || (ulong)signature.Length > signatureCapacity)
			{
				CryptographicOperations.ZeroMemory(signature);
				return -1;
			}
			new ReadOnlySpan<byte>(signature).CopyTo(new Span<byte>(outSignature, (int)signatureCapacity));
			CryptographicOperations.ZeroMemory(signature);
			return 0;
		}
		catch
		{
			return -1;
		}
	}
}
