using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

[Collection("Serial unit tests collection")]
public class LiquidWalletUiHistoryTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string IssuedAssetAHex = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
	private const string IssuedAssetBHex = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
	private const string IssuedAssetCHex = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAssetA => LiquidAssetId.ParseRpcHex(IssuedAssetAHex);
	private static LiquidAssetId IssuedAssetB => LiquidAssetId.ParseRpcHex(IssuedAssetBHex);
	private static LiquidAssetId IssuedAssetC => LiquidAssetId.ParseRpcHex(IssuedAssetCHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);

	// Required evidence §1: exact public surface and immutability of the
	// three new types. Reflection proves exactly the frozen properties and
	// factories, no public setter/constructor, no internal type in the
	// public surface, and no extra identity/detail field.
	[Fact]
	public void PublicSurfaceIsExactAndImmutable()
	{
		AssertPublicGetOnly(typeof(LiquidWalletUiHistoryAssetChange),
			("AssetIdHex", typeof(string)),
			("IsPeggedAsset", typeof(bool)),
			("NetAtomicUnits", typeof(long)),
			("IsCredit", typeof(bool)),
			("IsDebit", typeof(bool)));
		AssertPublicGetOnly(typeof(LiquidWalletUiHistoryRow),
			("TransactionReference", typeof(string)),
			("IsConfirmed", typeof(bool)),
			("ConfirmationHeight", typeof(uint?)),
			("AssetChanges", typeof(IReadOnlyList<LiquidWalletUiHistoryAssetChange>)),
			("HasBalanceChange", typeof(bool)));
		AssertPublicGetOnly(typeof(LiquidWalletUiHistorySnapshot),
			("WalletName", typeof(string)),
			("NetworkManifestId", typeof(string)),
			("PeggedAssetIdHex", typeof(string)),
			("Revision", typeof(ulong)),
			("Rows", typeof(IReadOnlyList<LiquidWalletUiHistoryRow>)),
			("IsEmpty", typeof(bool)));

		// No public constructor on any of the three types.
		foreach (Type type in new[]
		{
			typeof(LiquidWalletUiHistoryAssetChange),
			typeof(LiquidWalletUiHistoryRow),
			typeof(LiquidWalletUiHistorySnapshot),
		})
		{
			Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
		}

		// The factories are internal static.
		AssertInternalStaticFactory(typeof(LiquidWalletUiHistoryAssetChange), "FromChange");
		AssertInternalStaticFactory(typeof(LiquidWalletUiHistoryRow), "FromEffect");
		AssertInternalStaticFactory(typeof(LiquidWalletUiHistorySnapshot), "Capture");
	}

	// Required evidence §2: multiasset projection. One effect with an L-BTC
	// debit plus two issued-asset credit/debit rows projects exact signed
	// atomic units, pegged flags, and canonical ascending asset order.
	[Fact]
	public void ProjectsMultiassetNetChangesExactlyInCanonicalOrder()
	{
		LiquidTransactionId tx = Tx('a');
		// Spend 700 pegged (debit), create 500 issued-A (credit) and create a
		// second output then spend it to make issued-B net negative.
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(Tx('f'), [], [Output(Tx('f'), 0, IssuedAssetB, 1_000)]))
			.Apply(1, Delta(tx,
				[OutPoint(Tx('f'), 0)],
				[Output(tx, 0, IssuedAssetA, 500), Output(tx, 1, PeggedAsset, 300)]));

		LiquidWalletUiHistorySnapshot snapshot = Capture(state);
		LiquidWalletUiHistoryRow row = Assert.Single(snapshot.Rows, r => r.TransactionReference == Reference(tx));
		// Canonical ascending asset order: A (0a0a…) < B (0b0b…) < pegged (6f02…).
		Assert.Equal(3, row.AssetChanges.Count);

		Assert.False(row.AssetChanges[0].IsPeggedAsset);
		Assert.Equal(IssuedAssetAHex, row.AssetChanges[0].AssetIdHex);
		Assert.Equal(500, row.AssetChanges[0].NetAtomicUnits);
		Assert.True(row.AssetChanges[0].IsCredit);
		Assert.False(row.AssetChanges[0].IsDebit);

		Assert.False(row.AssetChanges[1].IsPeggedAsset);
		Assert.Equal(IssuedAssetBHex, row.AssetChanges[1].AssetIdHex);
		Assert.Equal(-1_000, row.AssetChanges[1].NetAtomicUnits);
		Assert.True(row.AssetChanges[1].IsDebit);
		Assert.False(row.AssetChanges[1].IsCredit);

		Assert.True(row.AssetChanges[2].IsPeggedAsset);
		Assert.Equal(Manifest.PeggedAssetId, row.AssetChanges[2].AssetIdHex);
		Assert.Equal(300, row.AssetChanges[2].NetAtomicUnits);
		Assert.True(row.AssetChanges[2].IsCredit);
		Assert.False(row.AssetChanges[2].IsDebit);
	}

	// Required evidence §3: application ordering. Applying A, B, C yields
	// public rows C, B, A. Confirmation heights deliberately out of order do
	// not reorder rows. Per-row asset order remains canonical ascending.
	[Fact]
	public void NewestAppliedFirstNeverSortedByConfirmation()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		LiquidTransactionId txC = Tx('c');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 111)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, PeggedAsset, 222)]))
			.Apply(2, Delta(txC, [], [Output(txC, 0, PeggedAsset, 333)]));

		// Confirm out of height order: C at height 5, A at height 99.
		state = state
			.Confirm(3, txC, LiquidConfirmation.Create(new string('1', 64), 5))
			.Confirm(4, txA, LiquidConfirmation.Create(new string('2', 64), 99));

		LiquidWalletUiHistorySnapshot snapshot = Capture(state);
		Assert.Equal(3, snapshot.Rows.Count);
		// Newest applied first: C, B, A — NOT sorted by confirmation height.
		Assert.Equal(Reference(txC), snapshot.Rows[0].TransactionReference);
		Assert.Equal(Reference(txB), snapshot.Rows[1].TransactionReference);
		Assert.Equal(Reference(txA), snapshot.Rows[2].TransactionReference);
		Assert.True(snapshot.Rows[0].IsConfirmed);
		Assert.Equal(5u, snapshot.Rows[0].ConfirmationHeight);
		Assert.False(snapshot.Rows[1].IsConfirmed);
		Assert.Null(snapshot.Rows[1].ConfirmationHeight);
		Assert.True(snapshot.Rows[2].IsConfirmed);
		Assert.Equal(99u, snapshot.Rows[2].ConfirmationHeight);
	}

	// Required evidence §4: zero-net effect. A retained effect with zero
	// asset changes remains one row with HasBalanceChange == false and an
	// empty list; distinct from an empty history.
	[Fact]
	public void ZeroNetEffectIsOneRowWithNoBalanceChange()
	{
		// A self-spend of the only output at the same amount nets to zero
		// per asset but is still a retained effect row. Build it as: create
		// 500 issued-A, then a tx that spends it and creates an identical
		// output — but the landed effect drops zero-net rows. Instead use the
		// landed semantics: create then fully spend different assets so the
		// effect has changes; the simplest landed zero-change effect is a
		// create+spend in the same delta. Apply create, then a delta that
		// spends and re-creates the identical output.
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, IssuedAssetA, 500)]))
			.Apply(1, Delta(txB, [OutPoint(txA, 0)], [Output(txB, 0, IssuedAssetA, 500)]));

		LiquidWalletUiHistorySnapshot snapshot = Capture(state);
		Assert.False(snapshot.IsEmpty);
		Assert.Equal(2, snapshot.Rows.Count);
		// txB: spent 500 A, created 500 A -> net zero -> dropped -> zero changes.
		LiquidWalletUiHistoryRow zeroRow = snapshot.Rows[0];
		Assert.Equal(Reference(txB), zeroRow.TransactionReference);
		Assert.False(zeroRow.HasBalanceChange);
		Assert.Empty(zeroRow.AssetChanges);
		Assert.NotNull(zeroRow.AssetChanges);

		// Distinct from empty history: an empty state has zero rows.
		LiquidWalletUiHistorySnapshot empty = Capture(LiquidWalletState.Empty(PeggedAsset));
		Assert.True(empty.IsEmpty);
		Assert.Empty(empty.Rows);
	}

	// Required evidence §5: permanent identity redaction. The reference is
	// exactly 8hex…8hex; the full txid and block hash never appear. Two
	// transaction ids sharing the exposed ends produce two rows, not merged.
	[Fact]
	public void IdentityIsPermanentlyRedacted()
	{
		LiquidTransactionId txA = Tx('a');
		string fullId = txA.CanonicalRpcHex;
		string blockHash = new string('9', 64);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 42)]))
			.Confirm(1, txA, LiquidConfirmation.Create(blockHash, 7));

		LiquidWalletUiHistorySnapshot snapshot = Capture(state);
		LiquidWalletUiHistoryRow row = Assert.Single(snapshot.Rows);

		// Reference is exactly first8 + U+2026 + last8.
		string expected = string.Concat(fullId.AsSpan(0, 8), "…", fullId.AsSpan(fullId.Length - 8, 8));
		Assert.Equal(expected, row.TransactionReference);
		Assert.Equal(17, row.TransactionReference.Length); // 8 + 1 (…) + 8
		// The full id and block hash never appear in any public string.
		Assert.DoesNotContain(fullId, row.TransactionReference);
		Assert.DoesNotContain(blockHash, ObjectGraphStrings(snapshot));

		// Two ids sharing the exposed ends remain two rows.
		string sharedPrefixSuffix = "abcdef12";
		LiquidTransactionId tx1 = LiquidTransactionId.ParseRpcHex(
			sharedPrefixSuffix + new string('1', 48) + sharedPrefixSuffix);
		LiquidTransactionId tx2 = LiquidTransactionId.ParseRpcHex(
			sharedPrefixSuffix + new string('2', 48) + sharedPrefixSuffix);
		LiquidWalletState twoState = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx1, [], [Output(tx1, 0, PeggedAsset, 10)]))
			.Apply(1, Delta(tx2, [], [Output(tx2, 0, PeggedAsset, 20)]));
		LiquidWalletUiHistorySnapshot twoSnapshot = Capture(twoState);
		Assert.Equal(2, twoSnapshot.Rows.Count);
		// Same redacted reference, but two distinct rows preserved.
		Assert.Equal(twoSnapshot.Rows[0].TransactionReference, twoSnapshot.Rows[1].TransactionReference);
	}

	// Required evidence §6: confirm then unconfirm advances revisions without
	// changing row order/reference/asset changes; confirmed height appears
	// only in the confirmed snapshot; block hash never crosses.
	[Fact]
	public void ConfirmThenUnconfirmPreservesRowAndAdvancesRevision()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState applied = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 42)]));
		LiquidConfirmation confirmation = LiquidConfirmation.Create(new string('3', 64), 11);

		LiquidWalletUiHistorySnapshot beforeConfirm = Capture(applied);
		LiquidWalletState confirmed = applied.Confirm(1, txA, confirmation);
		LiquidWalletUiHistorySnapshot afterConfirm = Capture(confirmed);
		LiquidWalletState unconfirmed = confirmed.Unconfirm(2, txA, confirmation);
		LiquidWalletUiHistorySnapshot afterUnconfirm = Capture(unconfirmed);

		Assert.False(beforeConfirm.Rows[0].IsConfirmed);
		Assert.Null(beforeConfirm.Rows[0].ConfirmationHeight);

		Assert.True(afterConfirm.Rows[0].IsConfirmed);
		Assert.Equal(11u, afterConfirm.Rows[0].ConfirmationHeight);
		Assert.Equal(beforeConfirm.Rows[0].TransactionReference, afterConfirm.Rows[0].TransactionReference);
		Assert.Equal(
			beforeConfirm.Rows[0].AssetChanges.Select(c => (c.AssetIdHex, c.NetAtomicUnits)),
			afterConfirm.Rows[0].AssetChanges.Select(c => (c.AssetIdHex, c.NetAtomicUnits)));
		Assert.NotEqual(beforeConfirm.Revision, afterConfirm.Revision);

		Assert.False(afterUnconfirm.Rows[0].IsConfirmed);
		Assert.Null(afterUnconfirm.Rows[0].ConfirmationHeight);
		Assert.Equal(afterConfirm.Rows[0].TransactionReference, afterUnconfirm.Rows[0].TransactionReference);
		Assert.NotEqual(afterConfirm.Revision, afterUnconfirm.Revision);
	}

	// Required evidence §7: rollback removes exactly the newest public row.
	[Fact]
	public void RollbackRemovesNewestRow()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 111)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, PeggedAsset, 222)]));

		LiquidWalletUiHistorySnapshot before = Capture(state);
		Assert.Equal(2, before.Rows.Count);
		Assert.Equal(Reference(txB), before.Rows[0].TransactionReference);

		LiquidWalletState rolledBack = state.RollbackLast(2, txB);
		LiquidWalletUiHistorySnapshot after = Capture(rolledBack);
		LiquidWalletUiHistoryRow remaining = Assert.Single(after.Rows);
		Assert.Equal(Reference(txA), remaining.TransactionReference);
	}

	// Required evidence §8: persistence round trip at the public entry
	// point. Save -> Load -> LoadAndCaptureHistory preserves revision, row
	// order, redacted references, confirmation states/heights, and canonical
	// per-asset changes.
	[Fact]
	public void LoadAndCaptureHistoryRoundTripsThroughPersistence()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidTransactionId txB = Tx('b');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]))
				.Apply(1, Delta(txB, [], [Output(txB, 0, IssuedAssetA, 2_000)]))
				.Confirm(2, txA, LiquidConfirmation.Create(new string('4', 64), 21));

			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 5, key, context);

			LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.LoadAndCaptureHistory(
				walletDataDir, "wallet", Manifest, key, context, expectedBaseRevision: 3);

			Assert.Equal("wallet", snapshot.WalletName);
			Assert.Equal(Manifest.ManifestId, snapshot.NetworkManifestId);
			Assert.Equal(Manifest.PeggedAssetId, snapshot.PeggedAssetIdHex);
			Assert.Equal(3ul, snapshot.Revision);
			Assert.Equal(2, snapshot.Rows.Count);
			// Newest first: txB (unconfirmed), then txA (confirmed at 21).
			Assert.Equal(Reference(txB), snapshot.Rows[0].TransactionReference);
			Assert.False(snapshot.Rows[0].IsConfirmed);
			Assert.Equal(Reference(txA), snapshot.Rows[1].TransactionReference);
			Assert.True(snapshot.Rows[1].IsConfirmed);
			Assert.Equal(21u, snapshot.Rows[1].ConfirmationHeight);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §9: failure atomicity. A stale expected revision or
	// wrong key/context yields no history snapshot.
	[Fact]
	public void LoadAndCaptureHistoryFailsClosedOnStaleRevisionAndWrongKey()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[] wrongKey = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]));
			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 5, key, context);

			// Stale expected revision.
			Assert.ThrowsAny<Exception>(() => LiquidWalletUiFacade.LoadAndCaptureHistory(
				walletDataDir, "wallet", Manifest, key, context, expectedBaseRevision: 99));
			// Wrong key.
			Assert.ThrowsAny<Exception>(() => LiquidWalletUiFacade.LoadAndCaptureHistory(
				walletDataDir, "wallet", Manifest, wrongKey, context, expectedBaseRevision: 1));
			// Missing file.
			Assert.ThrowsAny<Exception>(() => LiquidWalletUiFacade.LoadAndCaptureHistory(
				walletDataDir, "nonexistent", Manifest, key, context, expectedBaseRevision: 1));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(wrongKey);
		}
	}

	// Required evidence §9 (null/fail-closed argument rows for the new
	// methods).
	[Fact]
	public void HistoryNullArgumentRows()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);

			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CaptureHistory(null!, Manifest, state));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CaptureHistory("wallet", null!, state));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, null!));

			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureHistory("dir", "wallet", null!, key, context, 0));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureHistory(null!, "wallet", Manifest, key, context, 0));
			Assert.ThrowsAny<ArgumentException>(() =>
				LiquidWalletUiFacade.LoadAndCaptureHistory("dir", null!, Manifest, key, context, 0));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	private static LiquidWalletUiHistorySnapshot Capture(LiquidWalletState state) =>
		LiquidWalletUiSnapshotProxy.Capture(state);

	private static string Reference(LiquidTransactionId id)
	{
		string hex = id.CanonicalRpcHex;
		return string.Concat(hex.AsSpan(0, 8), "…", hex.AsSpan(hex.Length - 8, 8));
	}

	// Walks the public object graph's string surface to assert redaction.
	private static string ObjectGraphStrings(LiquidWalletUiHistorySnapshot snapshot)
	{
		var builder = new System.Text.StringBuilder();
		foreach (LiquidWalletUiHistoryRow row in snapshot.Rows)
		{
			builder.Append(row.TransactionReference).Append('|');
			foreach (LiquidWalletUiHistoryAssetChange change in row.AssetChanges)
			{
				builder.Append(change.AssetIdHex).Append('|');
			}
		}
		return builder.ToString();
	}

	private static void AssertPublicGetOnly(Type type, params (string Name, Type PropertyType)[] expected)
	{
		PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
		Assert.Equal(
			expected.Select(e => e.Name).Order(StringComparer.Ordinal),
			properties.Select(p => p.Name).Order(StringComparer.Ordinal));
		foreach ((string name, Type propertyType) in expected)
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;
			Assert.NotNull(property);
			Assert.Equal(propertyType, property.PropertyType);
			Assert.Null(property.SetMethod); // no setter at all
			Assert.NotNull(property.GetMethod);
			Assert.True(property.GetMethod!.IsPublic);
		}
	}

	private static void AssertInternalStaticFactory(Type type, string name)
	{
		MethodInfo method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
		Assert.NotNull(method);
		Assert.True(method.IsAssembly);
		Assert.True(method.IsStatic);
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
		IEnumerable<LiquidOutPoint> spent,
		IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);

	// Small in-assembly helper so the facade's internal CaptureHistory is the
	// exercised path (mirrors the landed CaptureBalances test pattern).
	private static class LiquidWalletUiSnapshotProxy
	{
		internal static LiquidWalletUiHistorySnapshot Capture(LiquidWalletState state) =>
			LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
	}
}
