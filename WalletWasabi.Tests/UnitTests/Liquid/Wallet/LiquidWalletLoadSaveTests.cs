using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NBitcoin;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

[Collection("Serial unit tests collection")]
public class LiquidWalletLoadSaveTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlockHashHex = "4444444444444444444444444444444444444444444444444444444444444444";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);

	// Required evidence row 1: round-trip save→load through the wiring. A
	// non-empty LiquidWalletState (two applied transactions, one
	// confirmation) is saved via LiquidWalletLoadSave.Save; Load on the same
	// directory returns a result whose State balances, unspent set,
	// applied-transaction count, confirmation set, Revision, and Generation
	// equal the original exactly.
	[Fact]
	public void SaveLoadRoundTripsNonEmptyStateThroughWiring()
	{
		const ulong generation = 73;
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId firstId = Tx('a');
			LiquidTransactionId secondId = Tx('b');
			LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHashHex, 7);
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]))
				.Confirm(1, firstId, confirmation)
				.Apply(2, Delta(secondId, [OutPoint(firstId, 0)], [Output(secondId, 0, IssuedAsset, 150)]));
			Assert.Equal(3ul, state.Revision);

			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSaveResult saved = LiquidWalletLoadSave.Save(
				walletDataDir,
				"wallet",
				state,
				generation,
				key,
				context);
			Assert.Null(saved.State);
			Assert.Equal(3ul, saved.Revision);
			Assert.Equal(generation, saved.Generation);

			LiquidWalletLoadSaveResult loaded = LiquidWalletLoadSave.Load(
				walletDataDir,
				"wallet",
				key,
				context);
			LiquidWalletState restored = Required(loaded.State);
			Assert.Equal(3ul, loaded.Revision);
			Assert.Equal(generation, loaded.Generation);
			Assert.Equal(3ul, restored.Revision);

			Assert.Equal(
				state.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits,
				restored.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits);
			Assert.Equal(
				state.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits,
				restored.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
			Assert.Equal(150, restored.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
			Assert.Equal(
				state.GetUnspentOutputs().Select(output => output.OutPoint),
				restored.GetUnspentOutputs().Select(output => output.OutPoint));
			Assert.Equal(state.UnspentOutputCount, restored.UnspentOutputCount);
			Assert.Equal(state.AppliedTransactionCount, restored.AppliedTransactionCount);
			Assert.Equal(2, restored.AppliedTransactionCount);
			Assert.True(restored.TryGetConfirmation(firstId, out LiquidConfirmation? restoredConfirmation));
			Assert.Equal(confirmation, restoredConfirmation);
			Assert.False(restored.TryGetConfirmation(secondId, out _));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence row 1 (second row): an empty state
	// (LiquidWalletState.Empty, Revision == 0) round-trips through the
	// wiring.
	[Fact]
	public void SaveLoadRoundTripsEmptyStateThroughWiring()
	{
		const ulong generation = 0;
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);

			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSaveResult saved = LiquidWalletLoadSave.Save(
				walletDataDir,
				"empty",
				state,
				generation,
				key,
				context);
			Assert.Null(saved.State);
			Assert.Equal(0ul, saved.Revision);
			Assert.Equal(generation, saved.Generation);

			LiquidWalletLoadSaveResult loaded = LiquidWalletLoadSave.Load(
				walletDataDir,
				"empty",
				key,
				context,
				expectedBaseRevision: 0);
			LiquidWalletState restored = Required(loaded.State);
			Assert.Equal(0ul, loaded.Revision);
			Assert.Equal(generation, loaded.Generation);
			Assert.Equal(0ul, restored.Revision);
			Assert.Equal(0, restored.AppliedTransactionCount);
			Assert.Equal(0, restored.UnspentOutputCount);
			Assert.True(restored.GetBalances().IsEmpty);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence row 1 (third row): the on-disk file's bytes after
	// Save are byte-exact the landed framed format (magic WLWALFMT, version
	// 1, reserved 0, declared envelope length equal to the enclosed envelope
	// byte count, total length 16 + envelope length), and the enclosed
	// envelope bytes re-import through the landed handoff to exactly the
	// saved state — proving the wiring composes the landed format unchanged
	// and adds no framing of its own. (The landed Seal draws a fresh random
	// nonce per call, so byte-exactness is asserted against the frame
	// contract and the landed re-import, not against a second Seal.)
	[Fact]
	public void SaveWritesByteExactLandedFramedFormat()
	{
		const ulong generation = 41;
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? onDisk = null;
		try
		{
			LiquidTransactionId firstId = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]));

			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, generation, key, context);

			string filePath = Path.Combine(walletDataDir, "wallet.lwwal");
			onDisk = File.ReadAllBytes(filePath);
			Assert.Equal("WLWALFMT"u8.ToArray(), onDisk[..8]);
			Assert.Equal(
				1,
				System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(onDisk.AsSpan(8)));
			Assert.Equal(
				0,
				System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(onDisk.AsSpan(10)));
			uint envelopeLength =
				System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(onDisk.AsSpan(12));
			Assert.Equal((uint)(onDisk.Length - 16), envelopeLength);
			Assert.True(envelopeLength >= 4_160u);
			Assert.True(envelopeLength <= (uint)LiquidWalletReplayProtectedPayload.MaxEnvelopeLength);

			// The enclosed envelope bytes re-import through the landed
			// handoff to exactly the saved state.
			LiquidWalletPersistenceHandoffResult imported =
				LiquidWalletPersistenceHandoff.Import(
					onDisk.AsSpan(16),
					key,
					context,
					expectedBaseRevision: 1);
			Assert.Equal(1ul, imported.Revision);
			Assert.Equal(generation, imported.Generation);
			LiquidWalletState restored = Required(imported.State);
			Assert.Equal(
				state.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits,
				restored.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits);
			Assert.Equal(state.UnspentOutputCount, restored.UnspentOutputCount);
			Assert.Equal(state.AppliedTransactionCount, restored.AppliedTransactionCount);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (onDisk is not null)
			{
				CryptographicOperations.ZeroMemory(onDisk);
			}
		}
	}

	// Required evidence row 2: fail-closed on missing file. Load on a path
	// whose .lwwal file does not exist throws InvalidOperationException (the
	// landed SafeFile "no safe version" surface) — no silent empty-state
	// substitution, no retry, no fallback.
	[Fact]
	public void LoadFailsClosedOnMissingFile()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			string walletDataDir = GetWorkDir();
			Assert.False(File.Exists(Path.Combine(walletDataDir, "missing.lwwal")));
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletLoadSave.Load(walletDataDir, "missing", key, context));
			Assert.False(File.Exists(Path.Combine(walletDataDir, "missing.lwwal")));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence row 3: fail-closed on corrupt file. A .lwwal file
	// with a flipped frame header byte throws
	// LiquidWalletPersistenceFormatException on Load; a .lwwal file with a
	// valid frame but a flipped envelope ciphertext byte passes the frame
	// check and throws LiquidWalletReplayProtectionException from the landed
	// Import — proving the wiring performs no decryption and the
	// cryptographic fence is the landed one. A truncated file and a file
	// with trailing data each throw LiquidWalletPersistenceFormatException.
	[Fact]
	public void LoadFailsClosedOnCorruptFile()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			string walletDataDir = GetWorkDir();
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);
			string filePath = Path.Combine(walletDataDir, "wallet.lwwal");
			byte[] framed = File.ReadAllBytes(filePath);
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(state, 1, key, context)
				.Envelope).GetBytes();
			try
			{
				// Flipped frame header byte (inside the magic).
				byte[] flippedHeader = [.. framed];
				flippedHeader[0] ^= 0xFF;
				File.WriteAllBytes(filePath, flippedHeader);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletLoadSave.Load(walletDataDir, "wallet", key, context));

				// Valid frame, flipped envelope ciphertext byte: passes the
				// frame check and fails at the landed Open.
				byte[] flippedCiphertext = [.. framed];
				flippedCiphertext[16 + 48] ^= 0x01;
				File.WriteAllBytes(filePath, flippedCiphertext);
				Assert.Throws<LiquidWalletReplayProtectionException>(() =>
					LiquidWalletLoadSave.Load(walletDataDir, "wallet", key, context));

				// Truncated file.
				File.WriteAllBytes(filePath, framed[..^1]);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletLoadSave.Load(walletDataDir, "wallet", key, context));

				// Trailing data.
				File.WriteAllBytes(filePath, [.. framed, 0x00]);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletLoadSave.Load(walletDataDir, "wallet", key, context));

				// Restoring the valid bytes loads cleanly again (no state
				// escaped from any failure row).
				File.WriteAllBytes(filePath, framed);
				LiquidWalletLoadSaveResult loaded =
					LiquidWalletLoadSave.Load(walletDataDir, "wallet", key, context);
				Assert.Equal(0ul, loaded.Revision);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(framed);
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence row 4: wrong key / wrong context rejection. A state
	// saved with key K1 and context C1 loaded with key K2 (K2 != K1) throws
	// LiquidWalletReplayProtectionException; loaded with context C2 (C2 !=
	// C1) throws LiquidWalletReplayProtectionException; loaded with the
	// correct key and context and a mismatched expectedBaseRevision throws
	// InvalidOperationException (the landed revision fence). No state
	// escapes in any failure row.
	[Fact]
	public void LoadRejectsWrongKeyWrongContextAndRevisionFenceMismatch()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] wrongKey = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] wrongContext = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId firstId = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]));
			Assert.Equal(1ul, state.Revision);

			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 9, key, context);

			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletLoadSave.Load(walletDataDir, "wallet", wrongKey, context));
			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletLoadSave.Load(walletDataDir, "wallet", key, wrongContext));
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletLoadSave.Load(walletDataDir, "wallet", key, context, expectedBaseRevision: 2));

			LiquidWalletLoadSaveResult loaded = LiquidWalletLoadSave.Load(
				walletDataDir,
				"wallet",
				key,
				context,
				expectedBaseRevision: 1);
			Assert.Equal(1ul, loaded.Revision);
			Assert.Equal(9ul, loaded.Generation);
			Assert.Equal(1ul, Required(loaded.State).Revision);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(wrongKey);
			CryptographicOperations.ZeroMemory(wrongContext);
		}
	}

	// Required evidence row 5: path convention.
	// LiquidWalletDataDir.GetLiquidWalletDataDir rejects a null or empty
	// walletsWorkDir with ArgumentException and returns
	// Path.Combine(walletsWorkDir, "Liquid"); the resolved Liquid wallet
	// state file path is Path.Combine(liquidWalletDataDir, walletName +
	// ".lwwal") and is not enumerated by
	// WalletDirectories.EnumerateWalletFiles() (a Liquid/ sibling
	// subdirectory under the work dir is outside the BTC Wallets/ JSON
	// scan), proving the .lwwal file is never mistaken for a BTC wallet.
	[Fact]
	public void LiquidDataDirConventionAndBtcEnumerationSeparation()
	{
		Assert.ThrowsAny<ArgumentException>(() =>
			LiquidWalletDataDir.GetLiquidWalletDataDir(null!));
		Assert.ThrowsAny<ArgumentException>(() =>
			LiquidWalletDataDir.GetLiquidWalletDataDir(""));

		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			string walletsWorkDir = Path.Combine(GetWorkDir(), "wasabi");
			string liquidWalletDataDir = LiquidWalletDataDir.GetLiquidWalletDataDir(walletsWorkDir);
			Assert.Equal(Path.Combine(walletsWorkDir, "Liquid"), liquidWalletDataDir);

			LiquidWalletLoadSave.Save(
				liquidWalletDataDir,
				"wallet",
				LiquidWalletState.Empty(PeggedAsset),
				1,
				key,
				context);
			string filePath = Path.Combine(liquidWalletDataDir, "wallet.lwwal");
			Assert.True(File.Exists(filePath));

			// The .lwwal file under the Liquid/ sibling subdirectory is never
			// enumerated as a BTC wallet.
			WalletDirectories directories = new(NBitcoin.Network.RegTest, walletsWorkDir);
			Assert.DoesNotContain(
				directories.EnumerateWalletFiles().Select(file => file.FullName),
				enumerated => enumerated == filePath);

			// A same-named BTC JSON wallet beside it is enumerated; the
			// .lwwal file still is not.
			File.WriteAllBytes(
				Path.Combine(directories.WalletsDir, "wallet.json"),
				[0x7B, 0x7D]);
			Assert.Equal(
				["wallet.json"],
				directories.EnumerateWalletFiles().Select(file => file.Name).ToArray());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence row 5 (null-argument rows): every parameter of
	// Load, Save, and GetLiquidWalletDataDir; a path-traversal walletName is
	// rejected by the landed GetWalletStateFilePath with ArgumentException.
	[Fact]
	public void NullArgumentAndPathTraversalRows()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);

			// Load null-argument rows.
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Load(null!, "wallet", key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Load("", "wallet", key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Load("dir", null!, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Load("dir", "", key, context));

			// Save null-argument rows.
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Save(null!, "wallet", state, 1, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Save("", "wallet", state, 1, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Save("dir", null!, state, 1, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Save("dir", "", state, 1, key, context));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletLoadSave.Save("dir", "wallet", null!, 1, key, context));

			// GetLiquidWalletDataDir null-argument rows.
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletDataDir.GetLiquidWalletDataDir(null!));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletDataDir.GetLiquidWalletDataDir(""));

			// Path-traversal walletName rows (rejected by the landed
			// GetWalletStateFilePath).
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Load("dir", "..", key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Load("dir", "../wallet", key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Load("dir", "a/b", key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Save("dir", "..", state, 1, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Save("dir", "../wallet", state, 1, key, context));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletLoadSave.Save("dir", "a/b", state, 1, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	private static string GetWorkDir()
	{
		string dir = Common.GetWorkDir();
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static T Required<T>(T? value) where T : class =>
		value ?? throw new Xunit.Sdk.XunitException("A non-null load/save result value is required.");

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
