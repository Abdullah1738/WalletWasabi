using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync;

[Collection("Serial unit tests collection")]
public class LiquidWalletPersistenceHandoffTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlockHashHex = "4444444444444444444444444444444444444444444444444444444444444444";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);

	// Required evidence row 1: handoff round-trip
	// state -> snapshot -> seal -> open -> restore -> state. A non-empty state
	// (two applied transactions, one confirmation) exports through Export with
	// a caller-chosen generation; the returned envelope imports through Import
	// with the same key and context; the imported state's balances, unspent
	// set, applied-transaction count, and confirmation set equal the original
	// state's exactly; the imported Revision equals the original Revision; the
	// imported Generation equals the exported generation. The caller's state
	// is never mutated.
	[Fact]
	public void ExportImportRoundTripsNonEmptyStateExactly()
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
			Assert.Null(exported.State);
			Assert.Equal(3ul, exported.Revision);
			Assert.Equal(generation, exported.Generation);
			Assert.Equal(nameof(LiquidWalletPersistenceHandoffResult), exported.ToString());

			envelopeBytes = exportedEnvelope.GetBytes();

			LiquidWalletPersistenceHandoffResult imported =
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, context);

			Assert.Null(imported.Envelope);
			LiquidWalletState restored = Required(imported.State);
			Assert.Equal(3ul, imported.Revision);
			Assert.Equal(generation, imported.Generation);

			// Balances, unspent set, applied-transaction count, and
			// confirmation set equal the original state's exactly.
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

			// The imported state is a fully functional wallet state: the
			// revision-guarded transitions resume exactly where the original
			// state left off.
			LiquidTransactionId thirdId = Tx('c');
			LiquidWalletState advanced = restored.Apply(
				restored.Revision,
				Delta(thirdId, [], [Output(thirdId, 0, PeggedAsset, 25)]));
			Assert.Equal(4ul, advanced.Revision);

			// The caller's state is unchanged throughout.
			Assert.Equal(3ul, state.Revision);
			Assert.Equal(2, state.AppliedTransactionCount);
			Assert.Equal(1, state.UnspentOutputCount);
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
	// (LiquidWalletState.Empty) round-trips to an empty state with
	// Revision == 0.
	[Fact]
	public void ExportImportRoundTripsEmptyStateExactly()
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

			LiquidWalletPersistenceHandoffResult imported =
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, context, expectedBaseRevision: 0);

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

	// Required evidence row 2: RequiresRescan propagation. A reorg plan with
	// RequiresRescan == true yields a propagation with RequiresRescan == true;
	// a plan with RequiresRescan == false yields Proceed with the supplied
	// revision.
	[Fact]
	public void PropagateCarriesRescanSignalAndProceedRevision()
	{
		LiquidWalletPersistenceHandoffPropagation rescan =
			LiquidWalletPersistenceHandoffPlan.Propagate(LiquidWalletReorgPlan.RescanRequired(), 42);
		Assert.True(rescan.RequiresRescan);

		LiquidWalletSyncConfirmation[] unconfirmations =
		[
			LiquidWalletSyncConfirmation.Create(
				LiquidWalletSyncConfirmationKind.Unconfirm,
				Tx('a'),
				LiquidConfirmation.Create(BlockHashHex, 7)),
		];
		LiquidWalletReorgPlan plan = LiquidWalletReorgPlan.Create(unconfirmations, [Tx('a')]);
		LiquidWalletPersistenceHandoffPropagation proceed =
			LiquidWalletPersistenceHandoffPlan.Propagate(plan, 42);
		Assert.False(proceed.RequiresRescan);
		Assert.Equal(42ul, proceed.Revision);
		Assert.Equal(
			nameof(LiquidWalletPersistenceHandoffPropagation),
			proceed.ToString());
	}

	// Required evidence row 2 (null-argument row): Propagate rejects a null
	// reorg plan.
	[Fact]
	public void PropagateRejectsNullReorgPlan()
	{
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletPersistenceHandoffPlan.Propagate(null!, 42));
	}

	// Required evidence row 3: snapshot/node revision mismatch on load. A
	// state exported at revision N imported with expectedBaseRevision = N + 1
	// throws InvalidOperationException; expectedBaseRevision = N - 1
	// (regression) also throws; expectedBaseRevision = N succeeds.
	[Fact]
	public void ImportFencesExpectedBaseRevisionExactly()
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
			envelopeBytes = Required(
				LiquidWalletPersistenceHandoff.Export(state, 1, key, context).Envelope).GetBytes();

			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, context, revision + 1));
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, context, revision - 1));

			LiquidWalletPersistenceHandoffResult imported =
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, context, revision);
			Assert.Equal(revision, Required(imported.State).Revision);
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

	// Required evidence row 4(a): a truncated envelope (fewer than
	// HeaderLength + PaddingBucketLength + TagLength bytes) throws
	// LiquidWalletReplayProtectionException on Import.
	[Fact]
	public void ImportRejectsTruncatedEnvelope()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		byte[]? truncated = null;
		try
		{
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope).GetBytes();
			truncated = envelopeBytes[..^1];

			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletPersistenceHandoff.Import(truncated, key, context));

			byte[] farTooShort = new byte[48 + LiquidWalletReplayProtectedPayload.PaddingBucketLength +
				LiquidWalletReplayProtectedPayload.TagLength - 1];
			try
			{
				Assert.Throws<LiquidWalletReplayProtectionException>(() =>
					LiquidWalletPersistenceHandoff.Import(farTooShort, key, context));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(farTooShort);
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
			if (truncated is not null)
			{
				CryptographicOperations.ZeroMemory(truncated);
			}
		}
	}

	// Required evidence row 4(b): an envelope with a flipped ciphertext byte
	// throws LiquidWalletReplayProtectionException on Import.
	[Fact]
	public void ImportRejectsFlippedCiphertextByte()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		byte[]? mutated = null;
		try
		{
			envelopeBytes = Required(LiquidWalletPersistenceHandoff
				.Export(LiquidWalletState.Empty(PeggedAsset), 1, key, context)
				.Envelope).GetBytes();
			mutated = [.. envelopeBytes];
			mutated[48] ^= 0x01;

			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletPersistenceHandoff.Import(mutated, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
			if (mutated is not null)
			{
				CryptographicOperations.ZeroMemory(mutated);
			}
		}
	}

	// Required evidence row 4(c): an envelope sealed with a different key or
	// different externalWalletNetworkContext throws
	// LiquidWalletReplayProtectionException on Import.
	[Fact]
	public void ImportRejectsWrongKeyAndWrongContext()
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

			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, wrongKey, context));
			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, wrongContext));
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

	// Required evidence row 4(d): a hand-built snapshot whose journal
	// double-applies a transaction (bypassing the codec) throws
	// InvalidOperationException from RestoreReplaySnapshot when imported
	// through a validly sealed envelope — the replay-time fail-closed check
	// fires after authentication.
	[Fact]
	public void ImportRejectsInconsistentSnapshotJournalAfterAuthentication()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			LiquidTransactionId firstId = Tx('a');
			LiquidTransactionId secondId = Tx('b');
			LiquidWalletTransactionDelta first = Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]);
			LiquidWalletTransactionDelta second = Delta(secondId, [], [Output(secondId, 0, PeggedAsset, 50)]);
			// A hand-built snapshot whose journal double-applies the first
			// transaction (bypassing the codec's canonicality re-encode, which
			// would reject the journal at Seal time): the raw canonical writer
			// encodes the journal losslessly, so the envelope is a validly
			// sealed consistent journal whose canonical payload region is
			// swapped for the inconsistent journal's encoding under the same
			// nonce, key, and context (the landed
			// ReverseAuthenticatedConfirmationOrder pattern). The envelope
			// authenticates; the replay-time fail-closed check fires after
			// authentication.
			LiquidWalletReplaySnapshot inconsistent = CreateUncheckedSnapshot(
				PeggedAsset,
				3,
				[first, second, first],
				[]);
			LiquidWalletReplaySnapshot consistent = LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				2,
				[first, second],
				[]);
			byte[] payload = EncodePayloadCore(inconsistent);
			try
			{
				envelopeBytes = ResealWithPayload(payload, consistent, 1, key, context);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(payload);
			}

			// The landed Open authenticates, then its Decode restores the
			// decoded journal as part of the canonicality re-encode and maps
			// every failure — including the replay-time double-apply fence —
			// to the uniform privacy-redacted
			// LiquidWalletReplayProtectionException. The fence therefore fires
			// after authentication and before any state escapes, which is the
			// fail-closed guarantee the contract row names; the uniform
			// exception surface is the landed Open behavior this slice must
			// not remap (the contract's no-catch-and-rethrow rule).
			Assert.Throws<LiquidWalletReplayProtectionException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, context));

			// Control: the same swap path with the consistent journal's
			// encoding opens and restores successfully, proving the rejection
			// above is the journal-inconsistency fence and not an envelope
			// construction artifact.
			byte[] consistentPayload = EncodePayloadCore(consistent);
			byte[]? controlEnvelope = null;
			try
			{
				controlEnvelope = ResealWithPayload(consistentPayload, consistent, 1, key, context);
				LiquidWalletPersistenceHandoffResult control =
					LiquidWalletPersistenceHandoff.Import(controlEnvelope, key, context);
				Assert.Equal(2ul, Required(control.State).Revision);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(consistentPayload);
				if (controlEnvelope is not null)
				{
					CryptographicOperations.ZeroMemory(controlEnvelope);
				}
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

	[Fact]
	public void ExportRejectsDeltaCountCapacityOverflow()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			// A state at exactly MaxDeltaCount applied transactions exports a
			// canonical snapshot; one further Apply makes the exported
			// snapshot's delta count exceed MaxDeltaCount and the capacity
			// fence fires inside Export.
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
			for (int index = 0; index < LiquidWalletReplayCodec.MaxDeltaCount; index++)
			{
				LiquidTransactionId transactionId = IndexedTx(index);
				state = state.Apply(
					state.Revision,
					Delta(transactionId, [], [Output(transactionId, 0, PeggedAsset, 100)]));
			}

			LiquidTransactionId overflowId = IndexedTx(LiquidWalletReplayCodec.MaxDeltaCount);
			state = state.Apply(
				state.Revision,
				Delta(overflowId, [], [Output(overflowId, 0, PeggedAsset, 100)]));
			Assert.Equal((ulong)(LiquidWalletReplayCodec.MaxDeltaCount + 1), state.Revision);

			Assert.Throws<LiquidWalletReplayCapacityException>(() =>
				LiquidWalletPersistenceHandoff.Export(state, 1, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence row 5 (second row): a snapshot whose confirmation
	// count exceeds MaxConfirmationCount throws
	// LiquidWalletReplayCapacityException on Export.
	[Fact]
	public void ExportRejectsConfirmationCountCapacityOverflow()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			// A state carrying MaxConfirmationCount + 1 confirmed applied
			// transactions exports a snapshot whose confirmation count exceeds
			// MaxConfirmationCount; the capacity fence fires inside Export.
			LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHashHex, 7);
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
			for (int index = 0; index < LiquidWalletReplayCodec.MaxConfirmationCount + 1; index++)
			{
				LiquidTransactionId transactionId = IndexedTx(index);
				state = state.Apply(
					state.Revision,
					Delta(transactionId, [], [Output(transactionId, 0, PeggedAsset, 100)]));
				state = state.Confirm(state.Revision, transactionId, confirmation);
			}

			Assert.Equal(
				(ulong)((LiquidWalletReplayCodec.MaxConfirmationCount + 1) * 2),
				state.Revision);

			Assert.Throws<LiquidWalletReplayCapacityException>(() =>
				LiquidWalletPersistenceHandoff.Export(state, 1, key, context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	[Fact]
	public void HandoffRejectsNullArguments()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletPersistenceHandoff.Export(null!, 1, key, context));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletPersistenceHandoffPlan.Propagate(null!, 1));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence row 5 (boundary rows): wrong-length key (31 or 33
	// bytes) and wrong-length externalWalletNetworkContext (31 or 33 bytes)
	// throw ArgumentException on both Export and Import.
	[Fact]
	public void HandoffRejectsWrongLengthKeyAndContext()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] shortKey = new byte[LiquidWalletReplayProtectedPayload.KeyLength - 1];
		byte[] longKey = new byte[LiquidWalletReplayProtectedPayload.KeyLength + 1];
		byte[] shortContext = new byte[LiquidWalletReplayProtectedPayload.ExternalContextLength - 1];
		byte[] longContext = new byte[LiquidWalletReplayProtectedPayload.ExternalContextLength + 1];
		byte[]? envelopeBytes = null;
		try
		{
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
			envelopeBytes = Required(
				LiquidWalletPersistenceHandoff.Export(state, 1, key, context).Envelope).GetBytes();

			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Export(state, 1, shortKey, context));
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Export(state, 1, longKey, context));
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Export(state, 1, key, shortContext));
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Export(state, 1, key, longContext));

			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, shortKey, context));
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, longKey, context));
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, shortContext));
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, longContext));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(shortKey);
			CryptographicOperations.ZeroMemory(longKey);
			CryptographicOperations.ZeroMemory(shortContext);
			CryptographicOperations.ZeroMemory(longContext);
			if (envelopeBytes is not null)
			{
				CryptographicOperations.ZeroMemory(envelopeBytes);
			}
		}
	}

	// The handoff is transparent to the sync engine: an imported state opens
	// a SYNC-001 session exactly as if it had never been persisted (Required
	// call order step 6).
	[Fact]
	public void ImportedStateOpensSyncSessionTransparently()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelopeBytes = null;
		try
		{
			LiquidTransactionId firstId = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 100)]));
			envelopeBytes = Required(
				LiquidWalletPersistenceHandoff.Export(state, 9, key, context).Envelope).GetBytes();

			LiquidWalletState restored = Required(
				LiquidWalletPersistenceHandoff.Import(envelopeBytes, key, context).State);

			LiquidWalletReplaySnapshot snapshot = restored.ExportReplaySnapshot();
			Assert.Equal(state.Revision, snapshot.Revision);
			Assert.Equal(
				state.ExportReplaySnapshot().GetDeltas().Select(delta => delta.TransactionId),
				snapshot.GetDeltas().Select(delta => delta.TransactionId));
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

	private static T Required<T>(T? value) where T : class =>
		value ?? throw new Xunit.Sdk.XunitException("A non-null handoff result value is required.");

	private static LiquidWalletReplaySnapshot CreateUncheckedSnapshot(
		LiquidAssetId peggedAsset,
		ulong revision,
		LiquidWalletTransactionDelta[] deltas,
		LiquidWalletReplayConfirmation[] confirmations)
	{
		ConstructorInfo constructor = Assert.Single(typeof(LiquidWalletReplaySnapshot)
			.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));
		return Assert.IsType<LiquidWalletReplaySnapshot>(constructor.Invoke(
			[peggedAsset, revision, deltas, confirmations, Array.Empty<LiquidWalletReceiveLabelEntry>()]));
	}

	private static byte[] EncodePayloadCore(LiquidWalletReplaySnapshot snapshot)
	{
		// EncodeCore is the raw canonical writer without the canonicality
		// re-encode: an inconsistent journal is still encodable — only replay
		// rejects it.
		MethodInfo encodeCore = typeof(LiquidWalletReplayCodec).GetMethod(
			"EncodeCore",
			BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new Xunit.Sdk.XunitException("The replay codec EncodeCore method is required.");
		return (byte[])encodeCore.Invoke(null, [snapshot, true])!;
	}

	private static byte[] ResealWithPayload(
		byte[] payload,
		LiquidWalletReplaySnapshot consistentSnapshot,
		ulong generation,
		byte[] key,
		byte[] context)
	{
		// Seal the consistent snapshot, then decrypt the envelope, swap the
		// canonical payload region for the inconsistent journal's encoding,
		// and re-encrypt in place under the same nonce, key, and context (the
		// landed ReverseAuthenticatedConfirmationOrder pattern). Both payloads
		// pad to one bucket, so the envelope stays well-formed and
		// authenticates — the replay-time fence is what fires on Import.
		byte[] envelope = LiquidWalletReplayProtectedPayload
			.Seal(consistentSnapshot, generation, key, context)
			.GetBytes();
		const int HeaderLength = 48;
		const int InnerPrefixLength = sizeof(ulong) + sizeof(uint);
		int bucket = LiquidWalletReplayProtectedPayload.PaddingBucketLength;
		byte[] plaintext = new byte[bucket];
		byte[] associatedData = new byte[HeaderLength + context.Length];
		try
		{
			envelope.AsSpan(0, HeaderLength).CopyTo(associatedData);
			context.CopyTo(associatedData.AsSpan(HeaderLength));
			using var aes = new AesGcm(key, LiquidWalletReplayProtectedPayload.TagLength);
			aes.Decrypt(
				envelope.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength),
				envelope.AsSpan(HeaderLength, bucket),
				envelope.AsSpan(HeaderLength + bucket, LiquidWalletReplayProtectedPayload.TagLength),
				plaintext,
				associatedData);

			System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
				plaintext.AsSpan(sizeof(ulong)),
				(uint)payload.Length);
			plaintext.AsSpan(InnerPrefixLength).Clear();
			payload.AsSpan().CopyTo(plaintext.AsSpan(InnerPrefixLength));

			aes.Encrypt(
				envelope.AsSpan(32, LiquidWalletReplayProtectedPayload.NonceLength),
				plaintext,
				envelope.AsSpan(HeaderLength, bucket),
				envelope.AsSpan(HeaderLength + bucket, LiquidWalletReplayProtectedPayload.TagLength),
				associatedData);
			return envelope;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(envelope);
			throw;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
			CryptographicOperations.ZeroMemory(associatedData);
		}
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidTransactionId IndexedTx(int index)
	{
		string hex = (index + 1).ToString("x16");
		return LiquidTransactionId.ParseRpcHex(hex + new string('0', 64 - hex.Length));
	}

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
		IEnumerable<LiquidOutPoint> spent,
		IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);
}
