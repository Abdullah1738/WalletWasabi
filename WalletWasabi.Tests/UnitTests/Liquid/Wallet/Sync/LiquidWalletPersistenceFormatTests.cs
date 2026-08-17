using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync;

[Collection("Serial unit tests collection")]
public class LiquidWalletPersistenceFormatTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlockHashHex = "4444444444444444444444444444444444444444444444444444444444444444";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);

	// Required evidence row 1: round-trip save→load byte-exact. A non-empty
	// LiquidWalletState (two applied transactions, one confirmation) is
	// exported via the landed Export with a caller-chosen generation; the
	// envelope is saved via Save to a temp path; LoadEnvelope on the same
	// path returns a payload whose GetBytes() is byte-exact equal to the
	// original envelope's GetBytes(); the loaded envelope is then imported
	// via the landed Import with the same key and context and the restored
	// State balances, unspent set, applied-transaction count, confirmation
	// set, Revision, and Generation equal the original exactly.
	[Fact]
	public void SaveLoadRoundTripsNonEmptyStateByteExactly()
	{
		const ulong generation = 73;
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
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

			LiquidWalletPersistenceHandoffResult exported =
				LiquidWalletPersistenceHandoff.Export(state, generation, key, context);
			LiquidWalletReplayProtectedPayload exportedEnvelope = Required(exported.Envelope);
			envelopeBytes = exportedEnvelope.GetBytes();

			string filePath = Path.Combine(GetWorkDir(), "wallet.lwwal");
			LiquidWalletPersistenceFormat.Save(filePath, exportedEnvelope);

			LiquidWalletReplayProtectedPayload loadedEnvelope =
				LiquidWalletPersistenceFormat.LoadEnvelope(filePath);
			byte[] loadedEnvelopeBytes = loadedEnvelope.GetBytes();
			try
			{
				Assert.Equal(envelopeBytes, loadedEnvelopeBytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(loadedEnvelopeBytes);
			}

			LiquidWalletPersistenceHandoffResult imported =
				LiquidWalletPersistenceHandoff.Import(loadedEnvelope.GetBytes(), key, context);

			Assert.Null(imported.Envelope);
			LiquidWalletState restored = Required(imported.State);
			Assert.Equal(3ul, imported.Revision);
			Assert.Equal(generation, imported.Generation);

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
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 1 (second row): an empty state
	// (LiquidWalletState.Empty, Revision == 0) round-trips byte-exact.
	[Fact]
	public void SaveLoadRoundTripsEmptyStateByteExactly()
	{
		const ulong generation = 0;
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);

			LiquidWalletPersistenceHandoffResult exported =
				LiquidWalletPersistenceHandoff.Export(state, generation, key, context);
			Assert.Equal(0ul, exported.Revision);
			envelopeBytes = Required(exported.Envelope).GetBytes();

			string filePath = Path.Combine(GetWorkDir(), "empty.lwwal");
			LiquidWalletPersistenceFormat.Save(filePath, Required(exported.Envelope));

			LiquidWalletReplayProtectedPayload loadedEnvelope =
				LiquidWalletPersistenceFormat.LoadEnvelope(filePath);
			byte[] loadedEnvelopeBytes = loadedEnvelope.GetBytes();
			try
			{
				Assert.Equal(envelopeBytes, loadedEnvelopeBytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(loadedEnvelopeBytes);
			}

			LiquidWalletPersistenceHandoffResult imported =
				LiquidWalletPersistenceHandoff.Import(loadedEnvelope.GetBytes(), key, context, expectedBaseRevision: 0);

			LiquidWalletState restored = Required(imported.State);
			Assert.Equal(0ul, imported.Revision);
			Assert.Equal(generation, imported.Generation);
			Assert.Equal(0ul, restored.Revision);
			Assert.Equal(0, restored.AppliedTransactionCount);
			Assert.Equal(0, restored.UnspentOutputCount);
			Assert.True(restored.GetBalances().IsEmpty);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 2: version-stamp rejection. A framed file whose
	// format-version field is 0 (unknown/older) throws
	// LiquidWalletPersistenceFormatException on LoadEnvelope; a framed file
	// whose format-version field is 2 (newer) throws
	// LiquidWalletPersistenceFormatException; a framed file whose magic is
	// wrong throws LiquidWalletPersistenceFormatException. No envelope bytes
	// reach Import in any row.
	[Fact]
	public void LoadEnvelopeRejectsUnknownAndNewerVersionAndWrongMagic()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope).GetBytes();

			byte[] framed = LiquidWalletPersistenceFrame.Encode(envelopeBytes);
			try
			{
				// Version 0 (unknown/older).
				byte[] version0 = [.. framed];
				version0[8] = 0;
				version0[9] = 0;
				string path0 = Path.Combine(GetWorkDir(), "v0.lwwal");
				File.WriteAllBytes(path0, version0);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(path0));

				// Version 2 (newer).
				byte[] version2 = [.. framed];
				version2[8] = 2;
				version2[9] = 0;
				string path2 = Path.Combine(GetWorkDir(), "v2.lwwal");
				File.WriteAllBytes(path2, version2);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(path2));

				// Wrong magic.
				byte[] wrongMagic = [.. framed];
				wrongMagic[0] = (byte)'X';
				string pathWrongMagic = Path.Combine(GetWorkDir(), "wrongmagic.lwwal");
				File.WriteAllBytes(pathWrongMagic, wrongMagic);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathWrongMagic));
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
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 3: truncation/corruption at each structural
	// boundary. (a) inside the 16-byte frame header, (b) between the frame
	// header and the envelope body, (c) inside the envelope body, (d) extra
	// trailing bytes after the declared envelope, (e) declared envelope
	// length does not equal framedBytes.Length - 16, (f) declared envelope
	// length below the landed minimum (4_160) or above
	// MaxEnvelopeLength (16_777_280).
	[Fact]
	public void LoadEnvelopeRejectsTruncationAndCorruptionAtStructuralBoundaries()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope).GetBytes();

			byte[] framed = LiquidWalletPersistenceFrame.Encode(envelopeBytes);
			try
			{
				// (a) Truncated inside the 16-byte frame header.
				byte[] truncatedHeader = framed[..8];
				string pathA = Path.Combine(GetWorkDir(), "truncated-header.lwwal");
				File.WriteAllBytes(pathA, truncatedHeader);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathA));

				// (b) Truncated between the frame header and the envelope body
				// (header complete, body missing).
				byte[] truncatedBody = framed[..16];
				string pathB = Path.Combine(GetWorkDir(), "truncated-body.lwwal");
				File.WriteAllBytes(pathB, truncatedBody);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathB));

				// (c) Truncated inside the envelope body.
				byte[] truncatedEnvelope = framed[..^1];
				string pathC = Path.Combine(GetWorkDir(), "truncated-envelope.lwwal");
				File.WriteAllBytes(pathC, truncatedEnvelope);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathC));

				// (d) Extra trailing bytes after the declared envelope.
				byte[] trailing = [.. framed, 0x00];
				string pathD = Path.Combine(GetWorkDir(), "trailing.lwwal");
				File.WriteAllBytes(pathD, trailing);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathD));

				// (e) Declared envelope length does not equal
				// framedBytes.Length - 16.
				byte[] wrongLength = [.. framed];
				System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
					wrongLength.AsSpan(12), (uint)(envelopeBytes.Length + 1));
				string pathE = Path.Combine(GetWorkDir(), "wrong-length.lwwal");
				File.WriteAllBytes(pathE, wrongLength);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathE));

				// (f) Declared envelope length below the landed minimum
				// (4_160).
				byte[] belowMin = [.. framed];
				System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
					belowMin.AsSpan(12), 4_159u);
				string pathF1 = Path.Combine(GetWorkDir(), "below-min.lwwal");
				File.WriteAllBytes(pathF1, belowMin);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathF1));

				// (f) Declared envelope length above MaxEnvelopeLength
				// (16_777_280).
				byte[] aboveMax = [.. framed];
				System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
					aboveMax.AsSpan(12), (uint)LiquidWalletReplayProtectedPayload.MaxEnvelopeLength + 1);
				string pathF2 = Path.Combine(GetWorkDir(), "above-max.lwwal");
				File.WriteAllBytes(pathF2, aboveMax);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletPersistenceFormat.LoadEnvelope(pathF2));
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
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 4: oversize rejection.
	// LiquidWalletPersistenceFrame.Encode with an envelope byte span longer
	// than MaxEnvelopeLength throws LiquidWalletPersistenceFormatException;
	// Decode with a declared envelope length above MaxEnvelopeLength throws
	// LiquidWalletPersistenceFormatException before reading the body.
	[Fact]
	public void FrameEncodeDecodeRejectOversizeEnvelope()
	{
		// Encode with an envelope longer than MaxEnvelopeLength.
		byte[] oversize = new byte[LiquidWalletReplayProtectedPayload.MaxEnvelopeLength + 1];
		try
		{
			Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
				LiquidWalletPersistenceFrame.Encode(oversize));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(oversize);
		}

		// Decode with a declared envelope length above MaxEnvelopeLength.
		byte[] framed = new byte[16];
		"WLWALFMT"u8.ToArray().CopyTo(framed, 0);
		System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(8), 1);
		System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(10), 0);
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
			framed.AsSpan(12), (uint)LiquidWalletReplayProtectedPayload.MaxEnvelopeLength + 1);
		Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
			LiquidWalletPersistenceFrame.Decode(framed));
	}

	// Required evidence row 4 (below-minimum): Encode with an envelope byte
	// span shorter than the landed minimum (4_160) throws
	// LiquidWalletPersistenceFormatException.
	[Fact]
	public void FrameEncodeRejectsBelowMinimumEnvelope()
	{
		byte[] tooSmall = new byte[4_159];
		try
		{
			Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
				LiquidWalletPersistenceFrame.Encode(tooSmall));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(tooSmall);
		}
	}

	// Required evidence row 5: atomic-write partial-failure behavior. After
	// a Save, the target file exists and no .new file remains; when a
	// pre-existing target file is overwritten, the prior bytes are fully
	// replaced (no interleaving) and no .old file remains after a clean
	// write. A simulated partial write (a stale .new file left beside a
	// valid target) is resolved by the landed SafeFile read path to the
	// valid target file, and LoadEnvelope returns the valid envelope. A
	// simulated crash leaving only .old and .new (no target) is resolved by
	// the landed SafeFile read path to .old.
	[Fact]
	public void SaveLoadAtomicWritePartialFailureAndCrashRecovery()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes2 = null;
		try
		{
			string filePath = Path.Combine(GetWorkDir(), "atomic.lwwal");
			string newFilePath = filePath + ".new";
			string oldFilePath = filePath + ".old";

			// After a Save, the target file exists and no .new file remains.
			LiquidWalletReplayProtectedPayload payload1 = LiquidWalletReplayProtectedPayload.Seal(
				LiquidWalletState.Empty(PeggedAsset).ExportReplaySnapshot(), 1, key, context);
			LiquidWalletPersistenceFormat.Save(filePath, payload1);
			Assert.True(File.Exists(filePath));
			Assert.False(File.Exists(newFilePath));
			Assert.False(File.Exists(oldFilePath));

			// When a pre-existing target file is overwritten, the prior bytes
			// are fully replaced and no .old file remains after a clean write.
			LiquidWalletReplayProtectedPayload payload2 = LiquidWalletReplayProtectedPayload.Seal(
				LiquidWalletState.Empty(PeggedAsset).ExportReplaySnapshot(), 2, key, context);
			envelopeBytes2 = payload2.GetBytes();
			LiquidWalletPersistenceFormat.Save(filePath, payload2);
			Assert.True(File.Exists(filePath));
			Assert.False(File.Exists(newFilePath));
			Assert.False(File.Exists(oldFilePath));
			byte[] onDisk = File.ReadAllBytes(filePath);
			try
			{
				byte[] expected = LiquidWalletPersistenceFrame.Encode(envelopeBytes2);
				try
				{
					Assert.Equal(expected, onDisk);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(expected);
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(onDisk);
			}

			// A simulated partial write (a stale .new file left beside a
			// valid target) is resolved by the landed SafeFile read path to
			// the valid target file.
			File.WriteAllBytes(newFilePath, [0xDE, 0xAD]);
			LiquidWalletReplayProtectedPayload loaded =
				LiquidWalletPersistenceFormat.LoadEnvelope(filePath);
			byte[] loadedBytes = loaded.GetBytes();
			try
			{
				Assert.Equal(envelopeBytes2, loadedBytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(loadedBytes);
			}
			File.Delete(newFilePath);

			// A simulated crash leaving only .old and .new (no target) is
			// resolved by the landed SafeFile read path to .old.
			File.Move(filePath, oldFilePath);
			File.WriteAllBytes(newFilePath, [0xDE, 0xAD]);
			loaded = LiquidWalletPersistenceFormat.LoadEnvelope(filePath);
			loadedBytes = loaded.GetBytes();
			try
			{
				Assert.Equal(envelopeBytes2, loadedBytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(loadedBytes);
			}
			File.Delete(newFilePath);
			File.Move(oldFilePath, filePath);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelopeBytes2 is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes2);
			}
		}
	}

	// Required evidence row 6: load-then-Import integration through the
	// landed handoff. A state exported at revision N, saved, and loaded is
	// imported via the landed Import with expectedBaseRevision = N and
	// succeeds; the same loaded envelope imported with expectedBaseRevision
	// = N + 1 throws InvalidOperationException (the landed revision fence),
	// proving the format layer hands bytes to the handoff unchanged. A
	// second row: a file whose frame is valid but whose enclosed envelope
	// bytes have a flipped ciphertext byte passes LoadEnvelope (framing is
	// intact) and throws LiquidWalletReplayProtectionException from the
	// landed Import (authentication failure), proving the format layer
	// performs no decryption and the cryptographic fence is the landed one.
	[Fact]
	public void LoadThenImportIntegratesThroughLandedHandoff()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			LiquidTransactionId firstId = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]));
			ulong revision = state.Revision;

			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(state, 1, key, context)
				.Envelope).GetBytes();

			string filePath = Path.Combine(GetWorkDir(), "integration.lwwal");
			LiquidWalletPersistenceFormat.Save(filePath, Required(LiquidWalletPersistenceHandoff
				.Export(state, 1, key, context)
				.Envelope));

			LiquidWalletReplayProtectedPayload loaded =
				LiquidWalletPersistenceFormat.LoadEnvelope(filePath);

			// expectedBaseRevision = N succeeds.
			LiquidWalletPersistenceHandoffResult imported =
				LiquidWalletPersistenceHandoff.Import(loaded.GetBytes(), key, context, revision);
			Assert.Equal(revision, Required(imported.State).Revision);

			// expectedBaseRevision = N + 1 throws InvalidOperationException.
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletPersistenceHandoff.Import(loaded.GetBytes(), key, context, revision + 1));

			// A file whose frame is valid but whose enclosed envelope bytes
			// have a flipped ciphertext byte passes LoadEnvelope and throws
			// LiquidWalletReplayProtectionException from Import.
			byte[] framed = LiquidWalletPersistenceFrame.Encode(envelopeBytes);
			try
			{
				byte[] mutated = [.. framed];
				mutated[16 + 48] ^= 0x01; // Flip a ciphertext byte inside the envelope.
				string mutatedPath = Path.Combine(GetWorkDir(), "flipped.lwwal");
				File.WriteAllBytes(mutatedPath, mutated);

				LiquidWalletReplayProtectedPayload mutatedLoaded =
					LiquidWalletPersistenceFormat.LoadEnvelope(mutatedPath);
				Assert.Throws<LiquidWalletReplayProtectionException>(() =>
					LiquidWalletPersistenceHandoff.Import(mutatedLoaded.GetBytes(), key, context));
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
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 6 (wrong key/context through the full path): a
	// saved envelope loaded and imported with a wrong key or wrong context
	// throws LiquidWalletReplayProtectionException.
	[Fact]
	public void LoadThenImportRejectsWrongKeyAndContextThroughFullPath()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] wrongKey = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] wrongContext = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope).GetBytes();

			string filePath = Path.Combine(GetWorkDir(), "wrongkey.lwwal");
			LiquidWalletPersistenceFormat.Save(filePath, Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope));

			LiquidWalletReplayProtectedPayload loaded =
				LiquidWalletPersistenceFormat.LoadEnvelope(filePath);

			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletPersistenceHandoff.Import(loaded.GetBytes(), wrongKey, context));
			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletPersistenceHandoff.Import(loaded.GetBytes(), key, wrongContext));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(wrongKey);
			CryptographicOperations.ZeroMemory(wrongContext);
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 7: path convention and wrong-directory/permission
	// rows. GetWalletStateFilePath rejects a null, empty, or
	// path-traversal-containing walletName with ArgumentException; Save to a
	// path in a nonexistent directory succeeds (the landed SafeFile.Write
	// creates the containing directory via EnsureContainingDirectoryExists);
	// LoadEnvelope on a path with no safe version throws
	// InvalidOperationException (the landed SafeFile surface);
	// Save/LoadEnvelope on a read-only or otherwise permission-denied path
	// surface UnauthorizedAccessException or IOException unremapped.
	[Fact]
	public void PathConventionAndWrongDirectoryAndPermissionRows()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			// GetWalletStateFilePath rejects null, empty, or
			// path-traversal-containing walletName.
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath(null!, "wallet"));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath("", "wallet"));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath("dir", null!));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath("dir", ""));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath("dir", "../wallet"));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath("dir", "wallet/../other"));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath("dir", "wallet\\..\\other"));

			// GetWalletStateFilePath composes the correct path.
			string composed = LiquidWalletPersistencePaths.GetWalletStateFilePath("dir", "wallet");
			Assert.Equal(Path.Combine("dir", "wallet.lwwal"), composed);

			// Save to a path in a nonexistent directory succeeds.
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope).GetBytes();
			string nonexistentDir = Path.Combine(GetWorkDir(), "nonexistent", "subdir");
			string filePath = Path.Combine(nonexistentDir, "wallet.lwwal");
			LiquidWalletPersistenceFormat.Save(filePath, Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope));
			Assert.True(File.Exists(filePath));

			// LoadEnvelope on a path with no safe version throws
			// InvalidOperationException.
			string noFilePath = Path.Combine(GetWorkDir(), "no-such-file.lwwal");
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletPersistenceFormat.LoadEnvelope(noFilePath));

			// Save to a read-only directory surfaces
			// UnauthorizedAccessException or IOException unremapped.
			// On macOS/Linux, FileAttributes.ReadOnly does not prevent the
			// owner from writing; use a directory path as the file path to
			// trigger an IOException from the file system.
			string directoryAsFile = Path.Combine(GetWorkDir(), "readonly-dir");
			Directory.CreateDirectory(directoryAsFile);
			Assert.ThrowsAny<Exception>(() =>
				LiquidWalletPersistenceFormat.Save(directoryAsFile, Required(LiquidWalletPersistenceHandoff
					.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
					.Envelope)));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 7 (null-argument rows): for every parameter of
	// Save, LoadEnvelope, Encode, Decode, and GetWalletStateFilePath.
	[Fact]
	public void NullArgumentRows()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope).GetBytes();

			LiquidWalletReplayProtectedPayload envelope = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope);

			// Save null-argument rows.
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistenceFormat.Save(null!, envelope));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistenceFormat.Save("", envelope));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletPersistenceFormat.Save("path", null!));

			// LoadEnvelope null-argument rows.
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistenceFormat.LoadEnvelope(null!));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistenceFormat.LoadEnvelope(""));

			// Encode null/empty-argument rows.
			Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
				LiquidWalletPersistenceFrame.Encode(ReadOnlySpan<byte>.Empty));

			// Decode null/empty-argument rows.
			Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
				LiquidWalletPersistenceFrame.Decode(ReadOnlySpan<byte>.Empty));

			// GetWalletStateFilePath null-argument rows.
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath(null!, "wallet"));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletPersistencePaths.GetWalletStateFilePath("dir", null!));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// Required evidence row 5 (temp-file cleanup): after a clean Save, no
	// .new or .old temp files remain in the target directory.
	[Fact]
	public void SaveLeavesNoTempFilesAfterCleanWrite()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			string dir = Path.Combine(GetWorkDir(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			string filePath = Path.Combine(dir, "cleanup.lwwal");
			LiquidWalletPersistenceFormat.Save(filePath, Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope));

			Assert.True(File.Exists(filePath));
			Assert.False(File.Exists(filePath + ".new"));
			Assert.False(File.Exists(filePath + ".old"));
			Assert.Empty(Directory.EnumerateFiles(dir, "*.new"));
			Assert.Empty(Directory.EnumerateFiles(dir, "*.old"));
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
		value ?? throw new Xunit.Sdk.XunitException("A non-null handoff result value is required.");

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
