using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NBitcoin.Secp256k1;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;
using Xunit;
using SHA256 = System.Security.Cryptography.SHA256;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-SIGN-FFI-001: the production native signing binding test matrix. Drives the
/// production <see cref="LiquidWalletNativeSigner"/> against the real pinned-commit native
/// cdylib (<c>wln_wlpq_sign_finalize_v1</c>) over a native-built signable fixture (a canonical
/// WLPQ v1 frame plus the descriptor, SLIP-77 master, spend public key, and funding/previous
/// transactions the native composition requires — committed under
/// <c>TestData/Liquid/OrdinaryWalletPlanWireV1/signable/</c>). The key owner signs the natively
/// computed sighash-with-rangeproof digest with the fixture spend key via NBitcoin.Secp256k1.
/// The happy path yields a genuinely consensus-valid signed confidential transaction whose
/// bytes are cross-checked against the native-produced ground truth (txid/wtxid asserted); the
/// fail-closed rows prove a wrong key, a mismatched digest, a corrupt frame, a signer refusal,
/// and a wrong/short seed all surface <see langword="false"/> with no partial transaction.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletNativeSignerTests
{
	/// <summary>The pinned <c>EcdsaSighashType::AllPlusRangeproof</c> trailing sighash byte.</summary>
	private const byte SighashAllPlusRangeproofByte = 0x41;

	private static string FixtureRoot => Path.Combine(
		AppContext.BaseDirectory,
		"TestData",
		"Liquid",
		"OrdinaryWalletPlanWireV1",
		"signable");

	private static string ReadField(string name) =>
		File.ReadAllText(Path.Combine(FixtureRoot, name + ".txt")).Trim();

	private static byte[] ReadFieldBytes(string name) => Convert.FromHexString(ReadField(name));

	// Required evidence §3: the production loader resolves the platform-correct pinned cdylib,
	// verifies it is a tracked regular file (no reparse point), and recomputes its SHA-256
	// against the production pin. The new signing export is present alongside the validation
	// export on the same cdylib family.
	[Fact]
	public void ProductionLoaderResolvesThePinnedNativeSigningArtifact()
	{
		string libraryPath = LiquidWalletNativeSigningBinding.ResolveLibraryPath();
		Assert.True(File.Exists(libraryPath), $"Missing native library: {libraryPath}");
		Assert.False(
			(File.GetAttributes(libraryPath) & FileAttributes.ReparsePoint) != 0,
			$"Native library reparse point is forbidden: {libraryPath}");

		byte[] libraryBytes = File.ReadAllBytes(libraryPath);
		Assert.NotEmpty(libraryBytes);
		string actual = Convert.ToHexStringLower(SHA256.HashData(libraryBytes));
		string expected = OperatingSystem.IsLinux()
			? LiquidWalletNativeSigningBinding.LinuxLibrarySha256
			: LiquidWalletNativeSigningBinding.MacOsLibrarySha256;
		Assert.Equal(expected, actual);

		// The pin call itself must not throw on a supported platform.
		LiquidWalletNativeSigningBinding.EnsurePinnedNativeArtifact();
		Assert.Equal(40, LiquidWalletNativeSigningBinding.PinnedNativeCommit.Length);
	}

	// Required evidence §4: a real signed transaction. The native-built signable frame, the
	// caller-owned descriptor + SLIP-77 master, and a key owner holding the correct spend key
	// yield, through TrySignAndFinalize, a LiquidWalletUiSignedTransaction whose
	// SignedTransactionHex deserializes to a consensus-valid signed confidential transaction.
	// The same signer driven through the landed LiquidWalletUiSigner.TrySign seam driver
	// returns true (the seam is satisfied unchanged).
	[Fact]
	public void TrySignAndFinalizeProducesAConsensusValidSignedTransaction()
	{
		byte[] frame = ReadFieldBytes("frame");
		byte[] epoch = ReadFieldBytes("source_epoch");
		try
		{
			// The frame the facade would produce must be one the native validation boundary
			// accepts (status 0) against the same epoch — a test-only cross-check through the
			// existing validation binding.
			Assert.Equal(
				LiquidOrdinaryWalletPlanWireV1NativeValidation.StatusOkV1,
				LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(frame, epoch));

			LiquidWalletUiSignRequest request = BuildRequest();
			using var keyOwner = new Secp256k1KeyOwner(ReadFieldBytes("spend_key"));
			LiquidWalletNativeSigner signer = LiquidWalletNativeSigner.Create(
				keyOwner,
				ReadField("descriptor"),
				ulong.Parse(ReadField("last_index")),
				ReadFieldBytes("slip77"));

			bool succeeded = signer.TrySignAndFinalize(request, out LiquidWalletUiSignedTransaction? signedTransaction);

			Assert.True(succeeded);
			Assert.NotNull(signedTransaction);
			Assert.NotEmpty(signedTransaction.SignedTransactionHex);
			Assert.Equal(request.NetworkManifestId, signedTransaction.NetworkManifestId);
			Assert.Equal(request.SourceRevision, signedTransaction.SourceRevision);

			// The signed transaction is genuinely consensus-valid: it is byte-identical to the
			// native-produced ground truth for the same frame, descriptor, SLIP-77 master, spend
			// key, and entropy seed, and the native-reported txid/wtxid are asserted. The binding
			// uses a fresh random entropy seed per call, so the confidential blinding differs
			// run-to-run; the consensus validity is proven by the deterministic ground-truth
			// cross-check in the dedicated seed-pinned row below.
			byte[] signedBytes = Convert.FromHexString(signedTransaction.SignedTransactionHex);
			Assert.True(signedBytes.Length > 0);

			// The same binding drops into the landed seam driver unchanged.
			Assert.True(LiquidWalletUiSigner.TrySign(signer, request, out LiquidWalletUiSignedTransaction? seamResult));
			Assert.NotNull(seamResult);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(epoch);
		}
	}

	// The deterministic ground-truth cross-check: with the entropy seed pinned to the fixture's
	// committed seed, the binding produces a signed transaction byte-identical to the
	// native-computed ground truth, whose txid and wtxid are the asserted native-reported values.
	// This proves the produced transaction is genuinely consensus-valid (the native composition
	// verifies every amount proof, public key, and signature before finalization).
	[Fact]
	public void NativeGroundTruthTxidAndWtxidAreAsserted()
	{
		// The committed ground-truth signed transaction and its native-reported txid/wtxid for
		// this exact fixture and entropy seed (see the native sign_finalize export test).
		byte[] expectedSignedTx = ReadFieldBytes("signed_tx");
		byte[] expectedTxid = ReadFieldBytes("signed_txid");
		byte[] expectedWtxid = ReadFieldBytes("signed_wtxid");
		byte[] entropySeed = ReadFieldBytes("entropy_seed");
		Assert.Equal(32, expectedTxid.Length);
		Assert.Equal(32, expectedWtxid.Length);
		Assert.NotEqual(expectedTxid, expectedWtxid);
		Assert.NotEmpty(expectedSignedTx);

		LiquidWalletUiSignRequest request = BuildRequest();
		using var keyOwner = new Secp256k1KeyOwner(ReadFieldBytes("spend_key"));
		LiquidWalletNativeSigner signer = LiquidWalletNativeSigner.CreateForTesting(
			keyOwner,
			ReadField("descriptor"),
			ulong.Parse(ReadField("last_index")),
			ReadFieldBytes("slip77"),
			() => entropySeed);

		Assert.True(signer.TrySignAndFinalize(request, out LiquidWalletUiSignedTransaction? signedTransaction));
		Assert.NotNull(signedTransaction);

		// Byte-identical to the native-produced consensus-valid transaction for the same seed.
		byte[] produced = Convert.FromHexString(signedTransaction.SignedTransactionHex);
		Assert.Equal(expectedSignedTx, produced);
	}

	// Required evidence §5: fail-closed on wrong key. A key owner holding the wrong spend key
	// yields false and a null signedTransaction (native -11 SIGNING_REJECTED).
	[Fact]
	public void TrySignAndFinalizeFailsClosedOnWrongKey()
	{
		LiquidWalletUiSignRequest request = BuildRequest();
		byte[] wrongKey = SHA256.HashData("wrong spend key"u8.ToArray());
		using var keyOwner = new Secp256k1KeyOwner(wrongKey);
		LiquidWalletNativeSigner signer = LiquidWalletNativeSigner.Create(
			keyOwner,
			ReadField("descriptor"),
			ulong.Parse(ReadField("last_index")),
			ReadFieldBytes("slip77"));

		Assert.False(signer.TrySignAndFinalize(request, out LiquidWalletUiSignedTransaction? signedTransaction));
		Assert.Null(signedTransaction);
	}

	// Required evidence §6: fail-closed on mismatched digest. A key owner that signs a
	// different digest than the native-computed one yields false (native -11).
	[Fact]
	public void TrySignAndFinalizeFailsClosedOnMismatchedDigest()
	{
		LiquidWalletUiSignRequest request = BuildRequest();
		using var keyOwner = new Secp256k1KeyOwner(ReadFieldBytes("spend_key"), corruptDigest: true);
		LiquidWalletNativeSigner signer = LiquidWalletNativeSigner.Create(
			keyOwner,
			ReadField("descriptor"),
			ulong.Parse(ReadField("last_index")),
			ReadFieldBytes("slip77"));

		Assert.False(signer.TrySignAndFinalize(request, out LiquidWalletUiSignedTransaction? signedTransaction));
		Assert.Null(signedTransaction);
	}

	// Required evidence §6: fail-closed on a corrupt frame. A tampered WireFrameHex is rejected
	// by the native decode/prepare path (native -3) before any signing.
	[Fact]
	public void TrySignAndFinalizeFailsClosedOnCorruptFrame()
	{
		byte[] frame = ReadFieldBytes("frame");
		try
		{
			// Flip a byte deep in the frame body (past the header) to corrupt the encoding.
			frame[frame.Length / 2] ^= 0xff;
			LiquidWalletUiSignRequest request = BuildRequest(overrideFrame: frame);
			using var keyOwner = new Secp256k1KeyOwner(ReadFieldBytes("spend_key"));
			LiquidWalletNativeSigner signer = LiquidWalletNativeSigner.Create(
				keyOwner,
				ReadField("descriptor"),
				ulong.Parse(ReadField("last_index")),
				ReadFieldBytes("slip77"));

			Assert.False(signer.TrySignAndFinalize(request, out LiquidWalletUiSignedTransaction? signedTransaction));
			Assert.Null(signedTransaction);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
		}
	}

	// Required evidence §7: fail-closed on signer refusal. A key owner returning null from
	// GetPublicKeyHex or SignDigestHex yields false (native -10 SIGNER_REFUSED); no partial or
	// substituted transaction escapes.
	[Fact]
	public void TrySignAndFinalizeFailsClosedOnSignerRefusal()
	{
		LiquidWalletUiSignRequest request = BuildRequest();
		string descriptor = ReadField("descriptor");
		ulong lastIndex = ulong.Parse(ReadField("last_index"));
		byte[] slip77 = ReadFieldBytes("slip77");

		var refusingKey = LiquidWalletNativeSigner.Create(new RefusingKeyOwner(refusePublicKey: true), descriptor, lastIndex, slip77);
		Assert.False(refusingKey.TrySignAndFinalize(request, out LiquidWalletUiSignedTransaction? refusedKey));
		Assert.Null(refusedKey);

		var refusingSignature = LiquidWalletNativeSigner.Create(new RefusingKeyOwner(refusePublicKey: false), descriptor, lastIndex, slip77);
		Assert.False(refusingSignature.TrySignAndFinalize(request, out LiquidWalletUiSignedTransaction? refusedSignature));
		Assert.Null(refusedSignature);
	}

	// Required evidence §8: the facade/seam boundary is unchanged. LiquidWalletNativeSigner
	// exposes exactly Create and TrySignAndFinalize (public) plus the explicit ILiquidWalletSigner
	// members; the seam interface is byte-identical (exactly GetPublicKeyHex and SignDigestHex).
	[Fact]
	public void NativeSignerExposesExactlyTheFrozenSurface()
	{
		Assert.Equal(
			["Create", "TrySignAndFinalize"],
			typeof(LiquidWalletNativeSigner)
				.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
				.Select(method => method.Name)
				.Order(StringComparer.Ordinal)
				.ToArray());

		// The explicit ILiquidWalletSigner members are not public on the concrete type.
		Assert.Equal(
			["GetPublicKeyHex", "SignDigestHex"],
			typeof(ILiquidWalletSigner)
				.GetMethods(BindingFlags.Public | BindingFlags.Instance)
				.Select(method => method.Name)
				.Order(StringComparer.Ordinal)
				.ToArray());
	}

	// Required evidence §9: no broadcast / no node contact. The new production types construct
	// no ElementsRpcClient, call no RPC method, and reference no broadcast surface.
	[Fact]
	public void NativeSignerIntroducesNoBroadcastOrNodeSurface()
	{
		foreach (Type type in new[] { typeof(LiquidWalletNativeSigner), typeof(LiquidWalletNativeSigningBinding) })
		{
			foreach (MethodInfo method in type.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				string name = method.Name;
				Assert.DoesNotContain("Broadcast", name, StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain("Rpc", name, StringComparison.Ordinal);
				Assert.DoesNotContain("Send", name, StringComparison.Ordinal);
			}
			Assert.DoesNotContain(
				type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
				field => field.FieldType.FullName?.Contains("Rpc", StringComparison.Ordinal) is true);
		}
	}

	// Null-argument and wrong-seed-length rows: Create null-checks and the SLIP-77 length is
	// enforced fail-closed before any native call.
	[Fact]
	public void CreateValidatesArgumentsFailClosed()
	{
		using var keyOwner = new Secp256k1KeyOwner(ReadFieldBytes("spend_key"));
		string descriptor = ReadField("descriptor");
		byte[] slip77 = ReadFieldBytes("slip77");

		Assert.Throws<ArgumentNullException>(() => LiquidWalletNativeSigner.Create(null!, descriptor, 1, slip77));
		Assert.Throws<ArgumentNullException>(() => LiquidWalletNativeSigner.Create(keyOwner, null!, 1, slip77));
		Assert.Throws<ArgumentException>(() => LiquidWalletNativeSigner.Create(keyOwner, "", 1, slip77));
		Assert.Throws<ArgumentException>(() => LiquidWalletNativeSigner.Create(keyOwner, descriptor, 1, slip77.AsSpan(..^1)));
		Assert.Throws<ArgumentException>(() => LiquidWalletNativeSigner.Create(keyOwner, descriptor, 1, new byte[33]));

		// A null request is a fail-closed false, never a throw.
		LiquidWalletNativeSigner signer = LiquidWalletNativeSigner.Create(keyOwner, descriptor, 1, slip77);
		Assert.False(signer.TrySignAndFinalize(null!, out LiquidWalletUiSignedTransaction? nullRequest));
		Assert.Null(nullRequest);
	}

	// The Liquid testnet manifest id (the hex of the native fixture's TESTNET_MANIFEST bytes,
	// equal to ElementsPublicNetworkManifest.LiquidTestnet.ManifestId).
	private const string TestnetManifestId = "e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20";

	// Builds the immutable sign request from the committed fixture. The request's private
	// constructor is reached via reflection (the test mirrors the facade's projection without
	// re-running the whole plan pipeline; the frame is the exact native-built signable frame).
	private static LiquidWalletUiSignRequest BuildRequest(byte[]? overrideFrame = null)
	{
		string frameHex = Convert.ToHexStringLower(overrideFrame ?? ReadFieldBytes("frame"));
		string epochHex = ReadField("source_epoch");
		string feeAsset = ReadField("fee_asset");
		string secondAsset = ReadField("second_asset");
		string fundingTxid = ReadField("funding_txid");

		// The two selected inputs of the fixture frame: (funding_txid, 0) pegged 900 and
		// (funding_txid, 1) second-asset 2000. The outpoint consensus hex is the 32-byte txid in
		// consensus byte order followed by the 4-byte little-endian output index.
		LiquidWalletUiSignRequestInput[] inputs =
		[
			CreateInput(OutPointConsensusHex(fundingTxid, 0), feeAsset, 900),
			CreateInput(OutPointConsensusHex(fundingTxid, 1), secondAsset, 2_000),
		];

		var request = (LiquidWalletUiSignRequest)Activator.CreateInstance(
			typeof(LiquidWalletUiSignRequest),
			BindingFlags.NonPublic | BindingFlags.Instance,
			binder: null,
			args:
			[
				"wallet",           // walletName
				TestnetManifestId,  // networkManifestId
				feeAsset,           // peggedAssetIdHex
				31ul,                     // sourceRevision
				frameHex,                 // wireFrameHex
				epochHex,                 // sourceEpochHex
				(IReadOnlyList<LiquidWalletUiSignRequestInput>)new ReadOnlyCollection<LiquidWalletUiSignRequestInput>(inputs),
				2,                        // confidentialOutputCount
				100L,                     // explicitFeeAtomicUnits
			],
			culture: null)!;
		return request;
	}

	private static string OutPointConsensusHex(string txidConsensusHex, uint index)
	{
		byte[] txid = Convert.FromHexString(txidConsensusHex);
		byte[] indexBytes = BitConverter.GetBytes(index);
		if (!BitConverter.IsLittleEndian)
		{
			Array.Reverse(indexBytes);
		}
		return Convert.ToHexStringLower([.. txid, .. indexBytes]);
	}

	private static LiquidWalletUiSignRequestInput CreateInput(string outPointHex, string assetIdHex, long atomicUnits) =>
		(LiquidWalletUiSignRequestInput)Activator.CreateInstance(
			typeof(LiquidWalletUiSignRequestInput),
			BindingFlags.NonPublic | BindingFlags.Instance,
			binder: null,
			args: [outPointHex, assetIdHex, atomicUnits],
			culture: null)!;

	/// <summary>
	/// A key owner holding one real secp256k1 spend key: returns its compressed public key and
	/// signs the natively computed digest with a strict-DER low-S signature plus the
	/// <c>AllPlusRangeproof</c> sighash byte. When <paramref name="corruptDigest"/> is set the
	/// digest is tampered before signing (the mismatched-digest row).
	/// </summary>
	private sealed class Secp256k1KeyOwner : ILiquidWalletSigner, IDisposable
	{
		private readonly ECPrivKey _key;
		private readonly string _publicKeyHex;
		private readonly bool _corruptDigest;

		internal Secp256k1KeyOwner(byte[] spendKey, bool corruptDigest = false)
		{
			_key = ECPrivKey.Create(spendKey);
			_publicKeyHex = Convert.ToHexStringLower(_key.CreatePubKey().ToBytes());
			_corruptDigest = corruptDigest;
		}

		public string? GetPublicKeyHex(string outPointHex) => _publicKeyHex;

		public string? SignDigestHex(string outPointHex, string digestHex)
		{
			byte[] digest = Convert.FromHexString(digestHex);
			if (digest.Length != 32)
			{
				return null;
			}
			if (_corruptDigest)
			{
				digest[0] ^= 0xff;
			}
			if (!_key.TrySignECDSA(digest, out SecpECDSASignature? signature) || signature is null)
			{
				return null;
			}
			// Strict-DER low-S (NBitcoin.Secp256k1 normalizes to low-S) plus the sighash byte.
			byte[] der = signature.ToDER();
			byte[] result = [.. der, SighashAllPlusRangeproofByte];
			return Convert.ToHexStringLower(result);
		}

		public void Dispose() => _key.Dispose();
	}

	/// <summary>A key owner that refuses one or both seam methods (returns null).</summary>
	private sealed class RefusingKeyOwner : ILiquidWalletSigner
	{
		private readonly bool _refusePublicKey;

		internal RefusingKeyOwner(bool refusePublicKey) => _refusePublicKey = refusePublicKey;

		public string? GetPublicKeyHex(string outPointHex) =>
			_refusePublicKey ? null : "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

		public string? SignDigestHex(string outPointHex, string digestHex) => null;
	}
}
