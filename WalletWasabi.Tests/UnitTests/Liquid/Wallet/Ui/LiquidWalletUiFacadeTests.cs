using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

[Collection("Serial unit tests collection")]
public class LiquidWalletUiFacadeTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlindingKeyHex = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";
	private const string IssuedAssetAHex = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
	private const string IssuedAssetBHex = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
	private const string IssuedAssetCHex = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
	private const string IssuedAssetDHex = "0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAssetA => LiquidAssetId.ParseRpcHex(IssuedAssetAHex);
	private static LiquidAssetId IssuedAssetB => LiquidAssetId.ParseRpcHex(IssuedAssetBHex);
	private static LiquidAssetId IssuedAssetC => LiquidAssetId.ParseRpcHex(IssuedAssetCHex);
	private static LiquidAssetId IssuedAssetD => LiquidAssetId.ParseRpcHex(IssuedAssetDHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);
	private static byte[] BlindingKey => Convert.FromHexString(BlindingKeyHex);
	private static byte[] ReceiveScript => ExternalKey.GetScriptPubKey();

	// Required evidence §1: multiasset balance rendering. A state with a
	// pegged-asset balance and two issued-asset balances captures to exactly
	// three Balances entries: the pegged asset first (IsPeggedAsset == true,
	// AssetIdHex == manifest.PeggedAssetId), then the two issued assets in
	// the landed canonical ascending asset-id-hex order, each with the exact
	// landed AtomicUnits and IsConfidential == true.
	[Fact]
	public void CaptureRendersMultiassetBalancesPeggedFirstThenCanonical()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		LiquidTransactionId txC = Tx('c');
		// Apply out of canonical order to prove the projection orders by the
		// landed canonical ascending asset-id-hex, not insertion order.
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, IssuedAssetB, 2_222)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, PeggedAsset, 1_111)]))
			.Apply(2, Delta(txC, [], [Output(txC, 0, IssuedAssetA, 3_333)]));

		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		Assert.Equal(3, snapshot.Balances.Count);
		Assert.False(snapshot.IsEmpty);

		// Pegged asset (L-BTC) first.
		Assert.True(snapshot.Balances[0].IsPeggedAsset);
		Assert.Equal(Manifest.PeggedAssetId, snapshot.Balances[0].AssetIdHex);
		Assert.Equal(1_111, snapshot.Balances[0].AtomicUnits);
		Assert.True(snapshot.Balances[0].IsConfidential);

		// Issued assets in landed canonical ascending asset-id-hex order
		// (A=0a0a… < B=0b0b…), each with the exact landed amount.
		Assert.False(snapshot.Balances[1].IsPeggedAsset);
		Assert.Equal(IssuedAssetAHex, snapshot.Balances[1].AssetIdHex);
		Assert.Equal(3_333, snapshot.Balances[1].AtomicUnits);
		Assert.True(snapshot.Balances[1].IsConfidential);

		Assert.False(snapshot.Balances[2].IsPeggedAsset);
		Assert.Equal(IssuedAssetBHex, snapshot.Balances[2].AssetIdHex);
		Assert.Equal(2_222, snapshot.Balances[2].AtomicUnits);
		Assert.True(snapshot.Balances[2].IsConfidential);

		// The projection matches the landed GetAmounts() order exactly
		// (pegged-first is the only reordering the projection applies).
		Assert.Equal(
			state.GetBalances().GetAmounts().Select(a => a.AssetId.CanonicalRpcHex).Order(StringComparer.Ordinal),
			snapshot.Balances.Select(b => b.AssetIdHex).Order(StringComparer.Ordinal));
	}

	// Required evidence §1 (second row): an empty state yields IsEmpty ==
	// true and Balances.Count == 0.
	[Fact]
	public void CaptureEmptyStateYieldsEmptyBalances()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("empty", Manifest, state);
		Assert.True(snapshot.IsEmpty);
		Assert.Empty(snapshot.Balances);
		Assert.NotNull(snapshot.Balances);
	}

	// Required evidence §1 (third row): Revision equals state.Revision,
	// WalletName equals the supplied name, NetworkManifestId equals
	// manifest.ManifestId, PeggedAssetIdHex equals manifest.PeggedAssetId.
	[Fact]
	public void CaptureCapturesRevisionNameAndManifest()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 42)]));
		Assert.Equal(1ul, state.Revision);

		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("my-liquid", Manifest, state);
		Assert.Equal("my-liquid", snapshot.WalletName);
		Assert.Equal(Manifest.ManifestId, snapshot.NetworkManifestId);
		Assert.Equal(Manifest.PeggedAssetId, snapshot.PeggedAssetIdHex);
		Assert.Equal(1ul, snapshot.Revision);
	}

	// Required evidence §1 (fourth row): a zero-amount asset is excluded.
	// The landed map already excludes zero amounts; the projection adds no
	// filtering of its own and the count matches the landed GetAmounts()
	// count exactly.
	[Fact]
	public void CaptureExcludesZeroAmountsMatchingLandedCount()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		// Create then fully spend an issued asset so its balance returns to
		// zero (the landed map drops zero entries).
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, IssuedAssetA, 500), Output(txA, 1, PeggedAsset, 700)]))
			.Apply(1, Delta(txB, [OutPoint(txA, 0)], [Output(txB, 0, IssuedAssetB, 900)]));

		IReadOnlyList<LiquidAssetAmount> landed = state.GetBalances().GetAmounts();
		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		// The projection count matches the landed GetAmounts() count exactly
		// (no zero-amount row appears, no extra filtering).
		Assert.Equal(landed.Count, snapshot.Balances.Count);
		Assert.DoesNotContain(snapshot.Balances, b => b.AtomicUnits == 0);
		Assert.Equal(2, snapshot.Balances.Count);
	}

	// Required evidence §2: confidential address generation/display. The
	// facade composes the landed LiquidBlindingPublicKey.Create +
	// LiquidAddress.FromScriptPubKey; the returned projection's
	// ConfidentialAddressText equals the landed GetCanonicalAddressText(),
	// UnconfidentialAddressText equals the landed
	// GetUnconfidentialAddressText(), IsConfidential is true, and
	// NetworkManifestId equals manifest.ManifestId.
	[Fact]
	public void CreateReceiveAddressProjectsLandedConfidentialAddress()
	{
		byte[] script = ReceiveScript;
		byte[] blinding = BlindingKey;

		LiquidWalletUiReceiveAddress projected =
			LiquidWalletUiFacade.CreateReceiveAddress(Manifest, script, blinding);

		LiquidAddress landed = LiquidAddress.FromScriptPubKey(
			Manifest,
			script,
			LiquidBlindingPublicKey.Create(blinding));

		Assert.True(projected.IsConfidential);
		Assert.Equal(landed.GetCanonicalAddressText(), projected.ConfidentialAddressText);
		Assert.Equal(landed.GetUnconfidentialAddressText(), projected.UnconfidentialAddressText);
		Assert.Equal(Manifest.ManifestId, projected.NetworkManifestId);
		// The confidential and unconfidential display forms differ.
		Assert.NotEqual(projected.ConfidentialAddressText, projected.UnconfidentialAddressText);
	}

	// Required evidence §2 (second row): the derived confidential address
	// round-trips through the landed LiquidAddress.Parse to an equal
	// address.
	[Fact]
	public void CreateReceiveAddressRoundTripsThroughParse()
	{
		byte[] script = ReceiveScript;
		byte[] blinding = BlindingKey;

		LiquidWalletUiReceiveAddress projected =
			LiquidWalletUiFacade.CreateReceiveAddress(Manifest, script, blinding);

		LiquidAddress parsed = LiquidAddress.Parse(Manifest, projected.ConfidentialAddressText);
		LiquidAddress landed = LiquidAddress.FromScriptPubKey(
			Manifest,
			script,
			LiquidBlindingPublicKey.Create(blinding));

		Assert.Equal(landed, parsed);
		Assert.True(parsed.IsConfidential);
		Assert.Equal(projected.ConfidentialAddressText, parsed.GetCanonicalAddressText());
	}

	// Required evidence §2 (third row): the derived address satisfies the
	// landed LiquidSuppliedConfidentialDestination.Create confidential-only
	// invariant (it does not throw), proving the receive surface produces
	// destinations a later send slice can consume.
	[Fact]
	public void CreateReceiveAddressSatisfiesConfidentialDestinationInvariant()
	{
		byte[] script = ReceiveScript;
		byte[] blinding = BlindingKey;

		LiquidWalletUiReceiveAddress projected =
			LiquidWalletUiFacade.CreateReceiveAddress(Manifest, script, blinding);
		LiquidAddress address = LiquidAddress.Parse(Manifest, projected.ConfidentialAddressText);

		LiquidAssetId assetId = PeggedAsset;
		LiquidAssetAmount amount = LiquidAssetAmount.Create(assetId, PeggedAsset, 1_000);
		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(
				Manifest,
				address,
				assetId,
				amount,
				LiquidWalletLabelSet.Empty);

		Assert.Equal(Manifest.ManifestId, destination.GetNetworkManifestId());
		Assert.Equal(address, destination.GetAddress());
	}

	// Required evidence §2 (failure rows): an empty scriptPubKey throws
	// ArgumentException; a non-33-byte, an invalid, or an uncompressed
	// blindingPublicKey throws ArgumentException from the landed
	// LiquidBlindingPublicKey.Create; a null manifest throws
	// ArgumentNullException.
	[Fact]
	public void CreateReceiveAddressFailsClosedOnInvalidInputs()
	{
		byte[] script = ReceiveScript;
		byte[] blinding = BlindingKey;

		// Null manifest.
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletUiFacade.CreateReceiveAddress(null!, script, blinding));

		// Empty scriptPubKey.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateReceiveAddress(Manifest, ReadOnlySpan<byte>.Empty, blinding));

		// Non-33-byte blinding key.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateReceiveAddress(Manifest, script, blinding.AsSpan(0, 32)));

		// Invalid blinding key (33 bytes, correct prefix, not a valid point:
		// an x-coordinate at or above the field order has no curve point).
		byte[] invalid = new byte[33];
		invalid[0] = 0x02;
		for (int index = 1; index < 33; index++)
		{
			invalid[index] = 0xFF;
		}
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateReceiveAddress(Manifest, script, invalid));

		// Uncompressed blinding key (65 bytes, 0x04 prefix).
		byte[] uncompressed = new byte[65];
		uncompressed[0] = 0x04;
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateReceiveAddress(Manifest, script, uncompressed));
	}

	// Required evidence §3: per-asset amounts. A state holding only an
	// issued-asset balance yields a single Balances entry with
	// IsPeggedAsset == false and the exact issued-asset AtomicUnits.
	[Fact]
	public void CaptureIssuedAssetOnlyYieldsSingleNonPeggedRow()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, IssuedAssetA, 12_345)]));

		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		LiquidWalletUiAssetBalance row = Assert.Single(snapshot.Balances);
		Assert.False(row.IsPeggedAsset);
		Assert.Equal(IssuedAssetAHex, row.AssetIdHex);
		Assert.Equal(12_345, row.AtomicUnits);
	}

	// Required evidence §3 (second row): a state holding the pegged-asset
	// maximum yields a pegged entry with that exact value (no cap, no
	// rescale, no rounding added by the projection).
	[Fact]
	public void CapturePeggedMaximumYieldsExactValue()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, LiquidAssetAmount.MaxPeggedAssetAtomicUnits)]));

		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		LiquidWalletUiAssetBalance row = Assert.Single(snapshot.Balances);
		Assert.True(row.IsPeggedAsset);
		Assert.Equal(LiquidAssetAmount.MaxPeggedAssetAtomicUnits, row.AtomicUnits);
	}

	// Required evidence §3 (third row): a state whose balances span four
	// assets preserves the landed canonical order and exact amounts across
	// all four rows.
	[Fact]
	public void CaptureFourAssetsPreservesCanonicalOrderAndAmounts()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		LiquidTransactionId txC = Tx('c');
		LiquidTransactionId txD = Tx('d');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txD, [], [Output(txD, 0, IssuedAssetD, 444)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, IssuedAssetB, 222)]))
			.Apply(2, Delta(txA, [], [Output(txA, 0, PeggedAsset, 111)]))
			.Apply(3, Delta(txC, [], [Output(txC, 0, IssuedAssetC, 333)]));

		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		Assert.Equal(4, snapshot.Balances.Count);
		// Pegged first, then issued assets in canonical ascending hex order
		// (B=0b0b < C=0c0c < D=0d0d).
		Assert.Equal(
			[Manifest.PeggedAssetId, IssuedAssetBHex, IssuedAssetCHex, IssuedAssetDHex],
			snapshot.Balances.Select(b => b.AssetIdHex).ToArray());
		Assert.Equal(
			[111L, 222L, 333L, 444L],
			snapshot.Balances.Select(b => b.AtomicUnits).ToArray());
		Assert.Equal(
			[true, false, false, false],
			snapshot.Balances.Select(b => b.IsPeggedAsset).ToArray());
	}

	// Required evidence §4: blinded-value handling. Every
	// LiquidWalletUiAssetBalance row reports IsConfidential == true; the
	// type's shape carries no USD field, no exchange-rate field, and no
	// plaintext-value-beyond-atomic-units field — asserted by a reflection
	// row proving LiquidWalletUiAssetBalance exposes exactly AssetIdHex,
	// IsPeggedAsset, AtomicUnits, IsConfidential and no other public
	// instance property.
	[Fact]
	public void AssetBalanceExposesNoUsdOrExchangeRateMember()
	{
		string[] publicInstanceProperties = typeof(LiquidWalletUiAssetBalance)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(property => property.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(
			["AssetIdHex", "AtomicUnits", "IsConfidential", "IsPeggedAsset"],
			publicInstanceProperties);

		// No member carries a USD, exchange-rate, or decimal-fiat shape.
		foreach (PropertyInfo property in typeof(LiquidWalletUiAssetBalance)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			Assert.DoesNotContain("Usd", property.Name, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("Rate", property.Name, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("Fiat", property.Name, StringComparison.OrdinalIgnoreCase);
			Assert.NotEqual(typeof(decimal), property.PropertyType);
		}

		// Every row reports IsConfidential == true.
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, IssuedAssetA, 5), Output(txA, 1, PeggedAsset, 7)]));
		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);
		Assert.All(snapshot.Balances, balance => Assert.True(balance.IsConfidential));
	}

	// Required evidence §5: fail-closed on locked/unloaded wallet.
	// LoadAndCaptureBalances on a path whose .lwwal file does not exist
	// throws InvalidOperationException (the landed SafeFile "no safe
	// version" surface) — no empty-snapshot substitution, no retry, no
	// fallback.
	[Fact]
	public void LoadAndCaptureFailsClosedOnMissingFile()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			string walletDataDir = GetWorkDir();
			Assert.False(File.Exists(Path.Combine(walletDataDir, "missing.lwwal")));
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances(walletDataDir, "missing", Manifest, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §5 (corrupt frame): a .lwwal file with a flipped
	// frame header byte throws LiquidWalletPersistenceFormatException; a
	// valid frame with a flipped envelope ciphertext byte throws
	// LiquidWalletReplayProtectionException.
	[Fact]
	public void LoadAndCaptureFailsClosedOnCorruptFrame()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			string walletDataDir = GetWorkDir();
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);
			string filePath = Path.Combine(walletDataDir, "wallet.lwwal");
			byte[] framed = File.ReadAllBytes(filePath);
			try
			{
				// Flipped frame header byte (inside the magic).
				byte[] flippedHeader = [.. framed];
				flippedHeader[0] ^= 0xFF;
				File.WriteAllBytes(filePath, flippedHeader);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletUiFacade.LoadAndCaptureBalances(walletDataDir, "wallet", Manifest, key, context));

				// Valid frame, flipped envelope ciphertext byte.
				byte[] flippedCiphertext = [.. framed];
				flippedCiphertext[16 + 48] ^= 0x01;
				File.WriteAllBytes(filePath, flippedCiphertext);
				Assert.Throws<LiquidWalletReplayProtectionException>(() =>
					LiquidWalletUiFacade.LoadAndCaptureBalances(walletDataDir, "wallet", Manifest, key, context));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(framed);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §5 (wrong key / wrong context): a state saved with
	// key K1 and context C1 loaded with K2 throws
	// LiquidWalletReplayProtectionException; loaded with C2 throws
	// LiquidWalletReplayProtectionException. No snapshot escapes.
	[Fact]
	public void LoadAndCaptureFailsClosedOnWrongKeyOrContext()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] wrongKey = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] wrongContext = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			string walletDataDir = GetWorkDir();
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);

			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances(walletDataDir, "wallet", Manifest, wrongKey, context));
			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances(walletDataDir, "wallet", Manifest, key, wrongContext));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(wrongKey);
			CryptographicOperations.ZeroMemory(wrongContext);
		}
	}

	// Required evidence §5 (revision fence): a mismatched
	// expectedBaseRevision throws InvalidOperationException (the landed
	// revision fence). No snapshot escapes.
	[Fact]
	public void LoadAndCaptureFailsClosedOnRevisionFenceMismatch()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			string walletDataDir = GetWorkDir();
			LiquidTransactionId txA = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 100)]));
			Assert.Equal(1ul, state.Revision);
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);

			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances(
					walletDataDir,
					"wallet",
					Manifest,
					key,
					context,
					expectedBaseRevision: 2));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §5 (manifest mismatch): Capture with a
	// state.PeggedAssetId.CanonicalRpcHex not equal to manifest.PeggedAssetId
	// throws ArgumentException and yields no snapshot.
	[Fact]
	public void CaptureFailsClosedOnManifestMismatch()
	{
		// A state bound to the mainnet pegged asset presented against the
		// testnet manifest.
		LiquidAssetId mainnetPegged = LiquidAssetId.ParseRpcHex(
			ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId);
		LiquidWalletState state = LiquidWalletState.Empty(mainnetPegged);

		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSnapshot.Capture("wallet", Manifest, state));
	}

	// Required evidence §5 (null-argument rows): every parameter of
	// Capture, CaptureBalances, CreateReceiveAddress, and
	// LoadAndCaptureBalances.
	[Fact]
	public void NullArgumentRows()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
			byte[] script = ReceiveScript;
			byte[] blinding = BlindingKey;

			// Capture null-argument rows.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSnapshot.Capture(null!, Manifest, state));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSnapshot.Capture("wallet", null!, state));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSnapshot.Capture("wallet", Manifest, null!));

			// CaptureBalances null-argument rows.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CaptureBalances(null!, Manifest, state));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CaptureBalances("wallet", null!, state));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CaptureBalances("wallet", Manifest, null!));

			// CreateReceiveAddress null-argument row.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateReceiveAddress(null!, script, blinding));

			// LoadAndCaptureBalances null-argument rows.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances("dir", "wallet", null!, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances(null!, "wallet", Manifest, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances("", "wallet", Manifest, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances("dir", null!, Manifest, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureBalances("dir", "", Manifest, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §1 (round-trip through the public entry point): a
	// saved non-empty multiasset state loads and captures through
	// LoadAndCaptureBalances to the same multiasset balance set.
	[Fact]
	public void LoadAndCaptureRoundTripsMultiassetBalances()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidTransactionId txB = Tx('b');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]))
				.Apply(1, Delta(txB, [], [Output(txB, 0, IssuedAssetA, 2_000)]));

			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 5, key, context);

			LiquidWalletUiSnapshot snapshot = LiquidWalletUiFacade.LoadAndCaptureBalances(
				walletDataDir,
				"wallet",
				Manifest,
				key,
				context,
				expectedBaseRevision: 2);

			Assert.Equal("wallet", snapshot.WalletName);
			Assert.Equal(Manifest.ManifestId, snapshot.NetworkManifestId);
			Assert.Equal(2ul, snapshot.Revision);
			Assert.Equal(2, snapshot.Balances.Count);
			Assert.True(snapshot.Balances[0].IsPeggedAsset);
			Assert.Equal(1_000, snapshot.Balances[0].AtomicUnits);
			Assert.False(snapshot.Balances[1].IsPeggedAsset);
			Assert.Equal(IssuedAssetAHex, snapshot.Balances[1].AssetIdHex);
			Assert.Equal(2_000, snapshot.Balances[1].AtomicUnits);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// The receive-label model half projects the durable label set bound to one
	// receive derivation index into the public next-receive material the Fluent
	// layer renders. An empty label set projects as an empty (never null) list.
	[Fact]
	public void ReceiveMaterialCarriesNextReceiveLabels()
	{
		byte[] script = ReceiveScript;
		byte[] blinding = BlindingKey;

		LiquidWalletUiReceiveMaterial unlabeled = new(script, blinding);
		Assert.Empty(unlabeled.NextReceiveLabels);
		Assert.NotNull(unlabeled.NextReceiveLabels);

		LiquidWalletUiReceiveMaterial labeled = new(script, blinding, ["savings", "vault"]);
		Assert.Equal(["savings", "vault"], labeled.NextReceiveLabels);
		// The projection is a defensive copy: mutating the caller's array does not leak in.
		string[] source = ["temp"];
		LiquidWalletUiReceiveMaterial copied = new(script, blinding, source);
		source[0] = "mutated";
		Assert.Equal(["temp"], copied.NextReceiveLabels);
	}

	// The facade reads the durable label set bound to one receive derivation
	// index from an already-loaded state (internal: it names the internal
	// state). An absent index projects as an empty list.
	[Fact]
	public void ReadReceiveLabelsProjectsIndexLabelsFromState()
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.SetReceiveLabels(0, LiquidWalletLabelSet.Create(["savings", "vault"]))
			.SetReceiveLabels(4, LiquidWalletLabelSet.Create(["donation"]));

		Assert.Equal(
			["savings", "vault"],
			LiquidWalletUiFacade.ReadReceiveLabels(state, 0));
		Assert.Equal(["donation"], LiquidWalletUiFacade.ReadReceiveLabels(state, 4));
		Assert.Empty(LiquidWalletUiFacade.ReadReceiveLabels(state, 1));
		Assert.Throws<ArgumentNullException>(() => LiquidWalletUiFacade.ReadReceiveLabels(null!, 0));
	}

	private static string GetWorkDir()
	{
		string dir = Common.GetWorkDir();
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidSpendKeyReference Key(LiquidKeyBranch branch, uint index) =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), branch, index);

	private static LiquidOutPoint OutPoint(LiquidTransactionId transactionId, uint outputIndex) =>
		LiquidOutPoint.CreateSpendable(transactionId, outputIndex);

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidSpendKeyReference key = ExternalKey;
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			key.GetScriptPubKey(),
			LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits),
			key);
	}

	private static LiquidWalletTransactionDelta Delta(
		LiquidTransactionId transactionId,
		System.Collections.Generic.IEnumerable<LiquidOutPoint> spent,
		System.Collections.Generic.IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);
}
