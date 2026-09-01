using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NBitcoin;
using NBitcoin.Secp256k1;
using WalletWasabi.Liquid.Application;
using Xunit;
using SHA256 = System.Security.Cryptography.SHA256;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

/// <summary>
/// LIQUID-SEND-SIGNATURE-FORMAT-001: the production <see cref="LiquidWalletSignerKeyAdapter"/>
/// must return each callback signature as strict-DER low-S plus the trailing
/// <c>EcdsaSighashType::AllPlusRangeproof</c> sighash byte <c>0x41</c> — the exact byte layout
/// the pinned native ordinary-PSET signer requires
/// (<c>ordinary-pset/src/signing.rs</c> appends <c>ORDINARY_SIGHASH_TYPE</c> to the DER
/// serialization, and its finalization check splits the last byte off as the sighash byte and
/// re-parses the remainder as strict DER). The live testnet send returned SigningRejected when
/// the adapter crossed the seam with DER only. The fail-closed contract is pinned alongside:
/// malformed digests and unknown outpoints still refuse with <see langword="null"/>.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletSignerKeyAdapterTests
{
	/// <summary>The pinned <c>EcdsaSighashType::AllPlusRangeproof</c> trailing sighash byte.</summary>
	private const byte SighashAllPlusRangeproofByte = 0x41;

	/// <summary>Outpoint hex form the signing seam hands over; opaque to the adapter.</summary>
	private const string OutPointHex =
		"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa00000000";

	private static string FixtureRoot => Path.Combine(
		AppContext.BaseDirectory,
		"TestData",
		"Liquid",
		"OrdinaryWalletPlanWireV1",
		"signable");

	// The signature the adapter returns must be the strict-DER low-S encoding plus exactly one
	// trailing 0x41 sighash byte: the DER prefix must re-serialize to itself (strict), already
	// be low-S (the native signer rejects non-canonical high-S), and cryptographically verify
	// against the adapter's own public key and the caller's digest.
	[Fact]
	public void SignDigestHexAppendsAllPlusRangeproofSighashByteToStrictDer()
	{
		byte[] spendKeyBytes = Convert.FromHexString(
			File.ReadAllText(Path.Combine(FixtureRoot, "spend_key.txt")).Trim());
		ExtKey masterKey = ExtKey.CreateFromSeed(spendKeyBytes);
		using LiquidWalletSignerKeyAdapter adapter = new(
			masterKey,
			_ => (0, 0, 0),
			NBitcoin.Network.Main);

		byte[] digest = SHA256.HashData(new byte[] { 0x01, 0x02, 0x03, 0x04 });
		string digestHex = Convert.ToHexStringLower(digest);

		string? signatureHex = adapter.SignDigestHex(OutPointHex, digestHex);
		Assert.NotNull(signatureHex);

		byte[] signature = Convert.FromHexString(signatureHex);

		// Even-length hex, DER (70-72 bytes) plus exactly one trailing sighash byte.
		Assert.Equal(signature.Length * 2, signatureHex.Length);
		Assert.InRange(signature.Length, 71, 73);
		Assert.Equal(SighashAllPlusRangeproofByte, signature[^1]);

		// Removing the trailing sighash byte leaves the strict-DER low-S signature.
		byte[] der = signature[..^1];
		Assert.True(SecpECDSASignature.TryCreateFromDer(der, out SecpECDSASignature? parsed));
		Assert.NotNull(parsed);
		byte[] reserialized = parsed.ToDER();
		Assert.Equal(der, reserialized);

		// Already low-S: the native signer rejects a signature whose S is not normalized, and
		// TryNormalize returns false when no normalization was needed.
		Assert.False(parsed.TryNormalize(out SecpECDSASignature? _));

		// The DER prefix is a genuine signature by the adapter's own spend public key over
		// the caller's digest.
		string? publicKeyHex = adapter.GetPublicKeyHex(OutPointHex);
		Assert.NotNull(publicKeyHex);
		Assert.True(ECPubKey.TryCreate(
			Convert.FromHexString(publicKeyHex),
			Context.Instance,
			out bool _,
			out ECPubKey? publicKey));
		Assert.NotNull(publicKey);

		// The native ordinary-PSET signer (ordinary-pset/src/signing.rs) builds its secp256k1
		// Message from the exact 32 digest bytes it passes across the callback and verifies the
		// returned signature against that same message. SigVerify's 32-byte message argument is
		// fed to libsecp256k1 verbatim — the same raw-byte convention — so the signature must
		// verify against the ORIGINAL digest bytes with no byte-order transformation.
		Assert.True(publicKey.SigVerify(parsed, digest));
	}

	// The fail-closed contract is unchanged by the format fix: a non-hex digest, a wrong-length
	// digest, an unknown outpoint, and a disposed adapter all refuse with null.
	[Fact]
	public void SignDigestHexStillRefusesMalformedDigestsAndUnknownOutpoints()
	{
		ExtKey masterKey = new ExtKey();
		using LiquidWalletSignerKeyAdapter adapter = new(
			masterKey,
			outpoint => outpoint == OutPointHex ? (0, 0, 0) : null,
			NBitcoin.Network.Main);

		string digestHex = new string('0', 64);

		Assert.Null(adapter.SignDigestHex(OutPointHex, "not-hex"));
		Assert.Null(adapter.SignDigestHex(OutPointHex, new string('0', 62)));
		Assert.Null(adapter.SignDigestHex(OutPointHex, new string('0', 66)));
		Assert.Null(adapter.SignDigestHex(
			"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb00000000",
			digestHex));

		adapter.Dispose();
		Assert.Null(adapter.SignDigestHex(OutPointHex, digestHex));
	}
}
