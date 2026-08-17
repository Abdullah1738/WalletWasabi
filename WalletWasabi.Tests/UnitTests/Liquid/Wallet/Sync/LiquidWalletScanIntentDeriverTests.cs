using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync;

[Collection("Serial unit tests collection")]
public class LiquidWalletScanIntentDeriverTests
{
	private const string BlockHashHex = "4444444444444444444444444444444444444444444444444444444444444444";
	private const string OtherBlockHashHex = "0202020202020202020202020202020202020202020202020202020202020202";
	private const string LetterBlockHashHex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string OtherPublicKeyHex = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";

	// Required evidence row 1: deterministic ordered derivation happy path.
	// The caller assembles the watched set from the reviewed descriptor
	// surface (two LiquidSpendKeyReference rows — one External, one Internal —
	// and one LiquidAddress row) and supplies the candidate rows in
	// non-sorted order; the derivation emits Intents in canonical ascending
	// transaction-id order (ordinal on CanonicalRpcHex), each
	// FetchIntent.TransactionId equal to the row's CanonicalRpcHex and each
	// FetchIntent.BlockHash equal to the row's BlockHash (null preserved).
	[Fact]
	public void DeriveProducesCanonicalAscendingOrderIndependentOfInputRowOrder()
	{
		// The watched set the caller assembles from the reviewed descriptor
		// surface: two spend keys (External, Internal) and one address. The
		// deriver consumes the resulting candidate rows, not the scripts
		// directly; the script-to-transaction association is the caller's
		// scanning policy and stays outside this layer.
		LiquidSpendKeyReference externalKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString(PublicKeyHex), LiquidKeyBranch.External, 0);
		LiquidSpendKeyReference internalKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString(OtherPublicKeyHex), LiquidKeyBranch.Internal, 1);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(
			ElementsPublicNetworkManifest.LiquidTestnet,
			new PubKey(Convert.FromHexString(PublicKeyHex)).WitHash.ScriptPubKey.ToBytes());
		Assert.Equal(22, externalKey.GetScriptPubKey().Length);
		Assert.Equal(22, internalKey.GetScriptPubKey().Length);
		Assert.Equal(22, address.GetScriptPubKey().Length);

		LiquidWalletScanIntent[] candidates =
		[
			Intent(Txid(3), BlockHashHex),
			Intent(Txid(1), null),
			Intent(Txid(2), OtherBlockHashHex),
		];
		LiquidWalletScanIntent[] shuffled =
		[
			Intent(Txid(2), OtherBlockHashHex),
			Intent(Txid(3), BlockHashHex),
			Intent(Txid(1), null),
		];

		LiquidWalletScanIntentDerivation derivation = LiquidWalletScanIntentDeriver.Derive(candidates);
		LiquidWalletScanIntentDerivation shuffledDerivation = LiquidWalletScanIntentDeriver.Derive(shuffled);

		Assert.False(derivation.IsEmpty);
		Assert.Equal(3, derivation.Intents.Count);
		Assert.Equal(
			candidates.Select(row => row.TransactionId.CanonicalRpcHex).Order(StringComparer.Ordinal),
			derivation.Intents.Select(intent => intent.TransactionId));
		Assert.Equal(
			derivation.Intents.Select(intent => (intent.TransactionId, intent.BlockHash)),
			shuffledDerivation.Intents.Select(intent => (intent.TransactionId, intent.BlockHash)));
		Assert.Equal(Txid(1), derivation.Intents[0].TransactionId);
		Assert.Null(derivation.Intents[0].BlockHash);
		Assert.Equal(Txid(2), derivation.Intents[1].TransactionId);
		Assert.Equal(OtherBlockHashHex, derivation.Intents[1].BlockHash);
		Assert.Equal(Txid(3), derivation.Intents[2].TransactionId);
		Assert.Equal(BlockHashHex, derivation.Intents[2].BlockHash);
	}

	// Required evidence row 2: dedup rules. Two candidate rows with the same
	// normalized transaction id, one with a null BlockHash and one with a
	// non-null BlockHash, dedup to a single intent carrying the non-null
	// BlockHash — in either input order; two rows with the same transaction
	// id and the same non-null BlockHash dedup to one; two rows with the same
	// transaction id and different non-null BlockHash values throw
	// ArgumentException (conflicting hint) and no intent escapes.
	[Fact]
	public void DeriveDeduplicatesByCanonicalRpcHexKeepingTheNonNullBlockHashHint()
	{
		LiquidTransactionId id = Tx(Txid(7));
		LiquidWalletScanIntent[] nullAndHint =
		[
			LiquidWalletScanIntent.Create(id, null),
			LiquidWalletScanIntent.Create(id, BlockHashHex),
		];
		LiquidWalletScanIntent[] hintAndNull =
		[
			LiquidWalletScanIntent.Create(id, BlockHashHex),
			LiquidWalletScanIntent.Create(id, null),
		];

		LiquidWalletScanIntentDerivation nullFirst = LiquidWalletScanIntentDeriver.Derive(nullAndHint);
		LiquidWalletScanIntentDerivation hintFirst = LiquidWalletScanIntentDeriver.Derive(hintAndNull);
		LiquidWalletSyncBatchPlanner.FetchIntent single = Assert.Single(nullFirst.Intents);
		Assert.Equal(id.CanonicalRpcHex, single.TransactionId);
		Assert.Equal(BlockHashHex, single.BlockHash);
		Assert.Equal(
			nullFirst.Intents.Select(intent => (intent.TransactionId, intent.BlockHash)),
			hintFirst.Intents.Select(intent => (intent.TransactionId, intent.BlockHash)));

		LiquidWalletScanIntentDerivation sameHint = LiquidWalletScanIntentDeriver.Derive(
			[
				LiquidWalletScanIntent.Create(id, BlockHashHex),
				LiquidWalletScanIntent.Create(id, BlockHashHex),
			]);
		LiquidWalletSyncBatchPlanner.FetchIntent deduplicated = Assert.Single(sameHint.Intents);
		Assert.Equal(BlockHashHex, deduplicated.BlockHash);
	}

	[Fact]
	public void DeriveRejectsConflictingBlockHashHintsForOneTransaction()
	{
		LiquidTransactionId id = Tx(Txid(7));

		ArgumentException failure = Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntentDeriver.Derive(
				[
					LiquidWalletScanIntent.Create(id, BlockHashHex),
					LiquidWalletScanIntent.Create(id, OtherBlockHashHex),
				]));
		Assert.Equal("candidateIntents", failure.ParamName);
		// Symmetric in input row order.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntentDeriver.Derive(
				[
					LiquidWalletScanIntent.Create(id, OtherBlockHashHex),
					LiquidWalletScanIntent.Create(id, BlockHashHex),
				]));
	}

	// Required evidence row 3: empty-descriptor edge. Derive([]) (no
	// candidates, no reorg plan) returns a derivation with IsEmpty == true
	// and an empty Intents; the caller skips the fetch step, so
	// CreateRequests is never reached with an empty list from this path (the
	// planner's < 1 rejection would fire if it were).
	[Fact]
	public void DeriveEmptyCandidatesReturnsIsEmptyAndSkipsCreateRequests()
	{
		LiquidWalletScanIntentDerivation derivation = LiquidWalletScanIntentDeriver.Derive([]);

		Assert.True(derivation.IsEmpty);
		Assert.Empty(derivation.Intents);
		// The Required call order skips CreateRequests on IsEmpty; the
		// planner's own < 1 fence would reject an empty list if reached.
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests(derivation.Intents));
	}

	// Required evidence row 4: bounding vs MaximumRequestCount. Exactly 100
	// unique transaction ids succeeds; 101 throws ArgumentOutOfRangeException
	// and produces no intent. The bound is enforced by the deriver before
	// CreateRequests is reached, and CreateRequests remains the final fence.
	[Fact]
	public void DeriveAcceptsExactCapAndRejectsOneHundredOneUniqueIntents()
	{
		LiquidWalletScanIntent[] atCap = Enumerable
			.Range(1, LiquidWalletSyncBatchPlanner.MaximumRequestCount)
			.Select(index => Intent(Txid(index), null))
			.ToArray();

		LiquidWalletScanIntentDerivation derivation = LiquidWalletScanIntentDeriver.Derive(atCap);

		Assert.False(derivation.IsEmpty);
		Assert.Equal(LiquidWalletSyncBatchPlanner.MaximumRequestCount, derivation.Intents.Count);
		Assert.Equal(
			atCap.Select(row => row.TransactionId.CanonicalRpcHex).Order(StringComparer.Ordinal),
			derivation.Intents.Select(intent => intent.TransactionId));

		LiquidWalletScanIntent[] overCap = [.. atCap, Intent(Txid(LiquidWalletSyncBatchPlanner.MaximumRequestCount + 1), null)];
		ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletScanIntentDeriver.Derive(overCap));
		Assert.Equal("candidateIntents", failure.ParamName);
	}

	// Required evidence row 4, final-fence row: the deriver never emits a set
	// CreateRequests would reject — the derived exact-cap set passes the
	// landed planner unchanged (count bound, uniqueness, normalization all
	// hold by construction), and the derived order survives CreateRequests
	// row-for-row.
	[Fact]
	public void DerivedIntentsPassCreateRequestsAsTheFinalFence()
	{
		LiquidWalletScanIntentDerivation derivation = LiquidWalletScanIntentDeriver.Derive(
			[
				Intent(Txid(2), BlockHashHex),
				Intent(Txid(1), null),
			]);

		ElementsRawTransactionRequest[] requests = LiquidWalletSyncBatchPlanner.CreateRequests(derivation.Intents);

		Assert.Equal(2, requests.Length);
		Assert.Equal(derivation.Intents.Select(intent => intent.TransactionId), requests.Select(request => request.TransactionId));
		Assert.Equal(derivation.Intents.Select(intent => intent.BlockHash), requests.Select(request => request.BlockHash));
	}

	// Required evidence row 5: composition with a non-rescan SYNC-003 reorg
	// plan. The replacement set unions into the same deterministic ordered
	// dedup; a replacement row that duplicates a candidate row dedups by the
	// row-2 rules (here: the replacement row's non-null hint wins over the
	// candidate's null).
	[Fact]
	public void DeriveUnionsNonRescanReplacementSetIntoTheSameOrderedDedup()
	{
		LiquidWalletReorgPlan plan = LiquidWalletReorgPlan.Create([], []);
		LiquidWalletScanIntent[] candidates =
		[
			Intent(Txid(3), BlockHashHex),
			Intent(Txid(1), null),
		];
		LiquidWalletScanIntent[] replacements =
		[
			Intent(Txid(2), OtherBlockHashHex),
			Intent(Txid(1), BlockHashHex),
		];

		LiquidWalletScanIntentDerivation derivation = LiquidWalletScanIntentDeriver.Derive(
			candidates,
			plan,
			replacements);

		Assert.False(derivation.IsEmpty);
		Assert.Equal(3, derivation.Intents.Count);
		Assert.Equal(
			[Txid(1), Txid(2), Txid(3)],
			derivation.Intents.Select(intent => intent.TransactionId));
		// The duplicate txid-1 row carries the replacement row's non-null hint.
		Assert.Equal(BlockHashHex, derivation.Intents[0].BlockHash);
		Assert.Equal(OtherBlockHashHex, derivation.Intents[1].BlockHash);
		Assert.Equal(BlockHashHex, derivation.Intents[2].BlockHash);

		// A non-rescan plan with an empty replacement set is legitimate (a
		// reorg that invalidates nothing).
		LiquidWalletScanIntentDerivation emptyReplacement = LiquidWalletScanIntentDeriver.Derive(
			candidates,
			plan,
			[]);
		Assert.Equal(2, emptyReplacement.Intents.Count);
	}

	// Required evidence row 5: a RequiresRescan == true plan throws
	// InvalidOperationException and produces no intent (the caller must
	// rebuild from chain data; there is no well-defined replacement set).
	[Fact]
	public void DeriveRejectsRescanRequiredPlan()
	{
		LiquidWalletReorgPlan rescan = LiquidWalletReorgPlan.RescanRequired();

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletScanIntentDeriver.Derive([Intent(Txid(1), null)], rescan, []));
		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletScanIntentDeriver.Derive([], rescan, []));
	}

	// Required evidence row 5: a non-rescan plan supplied with a null
	// replacementIntents throws ArgumentException; a null plan supplied with
	// a non-null replacementIntents throws ArgumentException (a replacement
	// set with no reorg plan is a caller bug).
	[Fact]
	public void DeriveRequiresPlanAndReplacementSetToBeSuppliedTogether()
	{
		LiquidWalletReorgPlan plan = LiquidWalletReorgPlan.Create([], []);
		LiquidWalletScanIntent[] candidates = [Intent(Txid(1), null)];
		LiquidWalletScanIntent[] replacements = [Intent(Txid(2), null)];

		ArgumentException missingReplacement = Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntentDeriver.Derive(candidates, plan, null));
		Assert.Equal("replacementIntents", missingReplacement.ParamName);

		ArgumentException missingPlan = Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntentDeriver.Derive(candidates, null, replacements));
		Assert.Equal("replacementIntents", missingPlan.ParamName);
	}

	// Required evidence row 6: fail-closed on malformed input.
	// LiquidWalletScanIntent.Create rejects a null transaction id, a zero
	// transaction id, and a malformed BlockHash (wrong length, uppercase,
	// non-hex, or all-zero) with ArgumentException.
	[Fact]
	public void CreateRejectsNullZeroAndMalformedRows()
	{
		LiquidTransactionId id = Tx(Txid(1));
		LiquidTransactionId zeroId = LiquidTransactionId.ParseRpcHex(new string('0', 64));

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletScanIntent.Create(null!, null));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntent.Create(zeroId, null));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntent.Create(id, new string('0', 63)));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntent.Create(id, LetterBlockHashHex.ToUpperInvariant()));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntent.Create(id, new string('g', 64)));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntent.Create(id, new string('0', 64)));

		LiquidWalletScanIntent row = LiquidWalletScanIntent.Create(id, BlockHashHex);
		Assert.Equal(id, row.TransactionId);
		Assert.Equal(BlockHashHex, row.BlockHash);
		Assert.Equal(nameof(LiquidWalletScanIntent), row.ToString());
	}

	// Required evidence row 6: Derive rejects a null candidateIntents, a null
	// candidate element, and a null replacement element; every rejection
	// produces no intent.
	[Fact]
	public void DeriveRejectsNullArgumentsAndNullElements()
	{
		LiquidWalletReorgPlan plan = LiquidWalletReorgPlan.Create([], []);

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletScanIntentDeriver.Derive(null!));

		ArgumentException nullCandidate = Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntentDeriver.Derive([Intent(Txid(1), null), null!]));
		Assert.Equal("candidateIntents", nullCandidate.ParamName);

		ArgumentException nullReplacement = Assert.Throws<ArgumentException>(() =>
			LiquidWalletScanIntentDeriver.Derive(
				[Intent(Txid(1), null)],
				plan,
				[Intent(Txid(2), null), null!]));
		Assert.Equal("replacementIntents", nullReplacement.ParamName);
	}

	private static LiquidWalletScanIntent Intent(string transactionIdHex, string? blockHash) =>
		LiquidWalletScanIntent.Create(Tx(transactionIdHex), blockHash);

	private static LiquidTransactionId Tx(string canonicalRpcHex) =>
		LiquidTransactionId.ParseRpcHex(canonicalRpcHex);

	private static string Txid(int ordinal)
	{
		// Digits-only 64-character hex so every row is a valid canonical id.
		string digits = string.Create(64, ordinal, static (span, value) =>
		{
			span.Fill('0');
			int position = span.Length - 1;
			while (value > 0)
			{
				span[position--] = (char)('0' + value % 10);
				value /= 10;
			}
		});
		return digits;
	}
}
