using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletCoinControlTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherPeggedAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string BlockHash = "4444444444444444444444444444444444444444444444444444444444444444";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const int MaximumCreatedOutputsPerDelta = 9_279;

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidAssetId OtherPeggedAsset => LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => LiquidSpendKeyReference.Create(
		Convert.FromHexString(PublicKeyHex),
		LiquidKeyBranch.External,
		0);

	[Fact]
	public void EmptyStateProducesRevisionBoundDefensiveSnapshot()
	{
		LiquidWalletCoinControlSnapshot snapshot =
			LiquidWalletState.Empty(PeggedAsset).GetCoinControlSnapshot();
		IReadOnlyList<LiquidWalletCoinControlEntry> entries = snapshot.GetEntries();
		var mutableView = Assert.IsAssignableFrom<IList<LiquidWalletCoinControlEntry>>(entries);

		Assert.Equal(PeggedAsset, snapshot.PeggedAssetId);
		Assert.Equal(0ul, snapshot.Revision);
		Assert.Empty(entries);
		Assert.True(mutableView.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableView.Add(null!));
		Assert.Equal(nameof(LiquidWalletCoinControlSnapshot), snapshot.ToString());
	}

	[Fact]
	public void MultiassetInventoryIsCanonicalAndPrivacyMinimized()
	{
		LiquidTransactionId laterId = Tx('c');
		LiquidTransactionId earlierId = Tx('a');
		LiquidOwnedOutput issued = Output(laterId, 1, IssuedAsset, 20);
		LiquidOwnedOutput peggedSecond = Output(earlierId, 1, PeggedAsset, 30);
		LiquidOwnedOutput peggedFirst = Output(earlierId, 0, PeggedAsset, 10);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(laterId, [], [issued]))
			.Apply(1, Delta(earlierId, [], [peggedSecond, peggedFirst]));

		IReadOnlyList<LiquidWalletCoinControlEntry> entries =
			state.GetCoinControlSnapshot().GetEntries();

		Assert.Equal(
			[peggedFirst.OutPoint, peggedSecond.OutPoint, issued.OutPoint],
			entries.Select(entry => entry.OutPoint));
		Assert.Equal([10L, 30L, 20L], entries.Select(entry => entry.Amount.AtomicUnits));
		Assert.All(entries, entry => Assert.Equal(PeggedAsset, entry.PeggedAssetId));
		Assert.All(entries, entry => Assert.Null(entry.Confirmation));

		string[] forbiddenMemberNames =
		[
			"Script", "SpendKey", "Derivation", "Branch", "Index", "Blinding",
			"Label", "Metadata", "Registry", "PublicKey",
		];
		IEnumerable<string> memberNames = typeof(LiquidWalletCoinControlEntry)
			.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Select(member => member.Name);
		Assert.DoesNotContain(memberNames, name =>
			forbiddenMemberNames.Any(forbidden =>
				name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void ConfirmationTransitionsPreserveOldSnapshotsAndOnlyChangeAttachment()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState applied = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [Output(transactionId, 0, PeggedAsset, 1)]));
		LiquidWalletCoinControlSnapshot absent = applied.GetCoinControlSnapshot();
		LiquidWalletState confirmed = applied.Confirm(1, transactionId, confirmation);
		LiquidWalletCoinControlSnapshot attached = confirmed.GetCoinControlSnapshot();
		LiquidWalletState unconfirmed = confirmed.Unconfirm(2, transactionId, confirmation);
		LiquidWalletCoinControlSnapshot detached = unconfirmed.GetCoinControlSnapshot();

		Assert.Equal([1ul, 2ul, 3ul], new[] { absent.Revision, attached.Revision, detached.Revision });
		Assert.Null(Assert.Single(absent.GetEntries()).Confirmation);
		Assert.Equal(confirmation, Assert.Single(attached.GetEntries()).Confirmation);
		Assert.Null(Assert.Single(detached.GetEntries()).Confirmation);
		Assert.Null(Assert.Single(absent.GetEntries()).Confirmation);
		Assert.Equal(confirmation, Assert.Single(attached.GetEntries()).Confirmation);
	}

	[Fact]
	public void ExactRevisionSelectionCanonicalizesAndLeavesStateUnchanged()
	{
		LiquidWalletState state = CreateMultiassetState();
		LiquidWalletCoinControlSnapshot before = state.GetCoinControlSnapshot();
		IReadOnlyList<LiquidWalletCoinControlEntry> inventory = before.GetEntries();
		LiquidWalletCoinControlSelection selection = state.CreateCoinControlSelection(
			state.Revision,
			[inventory[^1].OutPoint, inventory[0].OutPoint]);

		Assert.Equal(PeggedAsset, selection.PeggedAssetId);
		Assert.Equal(state.Revision, selection.SourceRevision);
		Assert.Equal(
			[inventory[0].OutPoint, inventory[^1].OutPoint],
			selection.GetEntries().Select(entry => entry.OutPoint));
		AssertEquivalent(before, state.GetCoinControlSnapshot());
		Assert.Equal(nameof(LiquidWalletCoinControlSelection), selection.ToString());
	}

	[Fact]
	public void SelectionValidationUsesExactPrecedenceAndRedactedFailures()
	{
		LiquidTransactionId receiveId = Tx('a');
		const uint FirstIndex = 101_003;
		const long FirstAmount = 41_234_567;
		LiquidOwnedOutput first = Output(receiveId, FirstIndex, PeggedAsset, FirstAmount);
		LiquidOwnedOutput second = Output(receiveId, 202_009, IssuedAsset, 52_345_678);
		LiquidOwnedOutput third = Output(receiveId, 303_017, Asset(1), 63_456_789);
		LiquidWalletCoinControlEntry firstSensitive =
			Entry(receiveId, FirstIndex, PeggedAsset, FirstAmount);
		LiquidWalletState twoOutputs = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [first, second, third]));
		LiquidWalletCoinControlEntry unknownEntry = Entry(
			Tx('f'),
			LiquidOutPoint.MaxSpendableOutputIndex - 1,
			IssuedAsset,
			987_654_321);

		Exception staleFailure = Assert.Throws<InvalidOperationException>(() =>
			twoOutputs.CreateCoinControlSelection(0, new ThrowingSelectionList()));
		Assert.Equal(
			"The Liquid wallet state revision changed before the requested transition.",
			staleFailure.Message);
		AssertRedacted(staleFailure);

		AssertRedactedFailure<ArgumentNullException>(() =>
			twoOutputs.CreateCoinControlSelection(1, null!));
		AssertRedactedFailure<ArgumentException>(() =>
			twoOutputs.CreateCoinControlSelection(1, []));
		Exception overCountFailure = AssertRedactedFailure<ArgumentException>(() =>
			twoOutputs.CreateCoinControlSelection(1, new ThrowingElementSelectionList(4)));
		Assert.Contains("exceeds", overCountFailure.Message, StringComparison.OrdinalIgnoreCase);

		Exception nullFailure = AssertRedactedFailure<ArgumentException>(() =>
			twoOutputs.CreateCoinControlSelection(1, new LiquidOutPoint[] { null! }));
		Exception duplicateFailure = AssertRedactedFailure<ArgumentException>(() =>
			twoOutputs.CreateCoinControlSelection(1, [first.OutPoint, first.OutPoint]), firstSensitive);
		Exception unknownFailure = AssertRedactedFailure<InvalidOperationException>(() =>
			twoOutputs.CreateCoinControlSelection(1, [unknownEntry.OutPoint]), unknownEntry);
		Assert.Contains("null outpoint", nullFailure.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("duplicate", duplicateFailure.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("not currently retained", unknownFailure.Message, StringComparison.OrdinalIgnoreCase);

		Exception nullBeforeDuplicate = AssertRedactedFailure<ArgumentException>(() =>
			twoOutputs.CreateCoinControlSelection(
				1,
				new LiquidOutPoint[] { first.OutPoint, null!, first.OutPoint }),
			firstSensitive);
		Exception duplicateBeforeUnknown = AssertRedactedFailure<ArgumentException>(() =>
			twoOutputs.CreateCoinControlSelection(
				1,
				[first.OutPoint, first.OutPoint, unknownEntry.OutPoint]),
			firstSensitive,
			unknownEntry);
		Exception unknownBeforeNull = AssertRedactedFailure<InvalidOperationException>(() =>
			twoOutputs.CreateCoinControlSelection(
				1,
				new LiquidOutPoint[] { unknownEntry.OutPoint, null! }),
			unknownEntry);
		Assert.Contains("null outpoint", nullBeforeDuplicate.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("duplicate", duplicateBeforeUnknown.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("not currently retained", unknownBeforeNull.Message, StringComparison.OrdinalIgnoreCase);

		var countOnce = new CountOnceSelectionList(first.OutPoint);
		LiquidWalletCoinControlSelection selected =
			twoOutputs.CreateCoinControlSelection(1, countOnce);
		Assert.Equal(first.OutPoint, Assert.Single(selected.GetEntries()).OutPoint);
		Assert.Equal(1, countOnce.CountReads);

		LiquidWalletState spentState = twoOutputs.Apply(
			1,
			Delta(Tx('b'), [first.OutPoint], []));
		Exception spentFailure = AssertRedactedFailure<InvalidOperationException>(() =>
			spentState.CreateCoinControlSelection(2, [first.OutPoint]), firstSensitive);
		Assert.Contains("not currently retained", spentFailure.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void MultiassetTotalsRemainIndependentAndChecked()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidOwnedOutput pegged = Output(
			transactionId,
			0,
			PeggedAsset,
			LiquidAssetAmount.MaxPeggedAssetAtomicUnits);
		LiquidOwnedOutput issuedMaximum = Output(
			transactionId,
			1,
			IssuedAsset,
			long.MaxValue);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [issuedMaximum, pegged]));
		LiquidWalletCoinControlSelection selection = state.CreateCoinControlSelection(
			1,
			[issuedMaximum.OutPoint, pegged.OutPoint]);
		LiquidAssetBalanceMap balances = selection.GetSelectedBalances();

		Assert.Equal(2, balances.AssetCount);
		Assert.Equal(
			LiquidAssetAmount.MaxPeggedAssetAtomicUnits,
			balances.GetAmountOrZero(PeggedAsset).AtomicUnits);
		Assert.Equal(long.MaxValue, balances.GetAmountOrZero(IssuedAsset).AtomicUnits);

		LiquidWalletCoinControlEntry issuedMaxEntry =
			Entry(Tx('b'), 707_059, IssuedAsset, long.MaxValue);
		LiquidWalletCoinControlEntry issuedAdditionalEntry =
			Entry(Tx('c'), 707_061, IssuedAsset, 17_345_679);
		AssertRedactedFailure<OverflowException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(
				PeggedAsset,
				0,
				[issuedMaxEntry, issuedAdditionalEntry]),
			issuedMaxEntry,
			issuedAdditionalEntry);
	}

	[Fact]
	public void InventoryTracksApplySpendRollbackConfirmAndUnconfirm()
	{
		LiquidTransactionId firstId = Tx('a');
		LiquidOwnedOutput first = Output(firstId, 0, PeggedAsset, 10);
		LiquidTransactionId secondId = Tx('b');
		LiquidOwnedOutput second = Output(secondId, 0, PeggedAsset, 9);
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState received = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(firstId, [], [first]));
		LiquidWalletState spent = received.Apply(1, Delta(secondId, [first.OutPoint], [second]));
		LiquidWalletState confirmed = spent.Confirm(2, secondId, confirmation);
		LiquidWalletState unconfirmed = confirmed.Unconfirm(3, secondId, confirmation);
		LiquidWalletState rolledBack = unconfirmed.RollbackLast(4, secondId);

		Assert.Equal(first.OutPoint, Assert.Single(received.GetCoinControlSnapshot().GetEntries()).OutPoint);
		Assert.Equal(second.OutPoint, Assert.Single(spent.GetCoinControlSnapshot().GetEntries()).OutPoint);
		Assert.Equal(confirmation, Assert.Single(confirmed.GetCoinControlSnapshot().GetEntries()).Confirmation);
		Assert.Null(Assert.Single(unconfirmed.GetCoinControlSnapshot().GetEntries()).Confirmation);
		Assert.Equal(first.OutPoint, Assert.Single(rolledBack.GetCoinControlSnapshot().GetEntries()).OutPoint);
		Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul], new[]
		{
			received.Revision, spent.Revision, confirmed.Revision, unconfirmed.Revision, rolledBack.Revision,
		});
	}

	[Fact]
	public void ReplayAndProtectedReplayPreserveInventoryAndSelection()
	{
		LiquidWalletState state = CreateMultiassetState();
		LiquidWalletState replayRestored =
			LiquidWalletState.RestoreReplaySnapshot(state.ExportReplaySnapshot());
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(state.ExportReplaySnapshot(), 17, key, context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletReplayOpenResult opened = protectedPayload.Open(key, context);
			LiquidWalletState protectedRestored =
				LiquidWalletState.RestoreReplaySnapshot(opened.Snapshot);

			Assert.Equal(17ul, opened.Generation);
			AssertEquivalent(state.GetCoinControlSnapshot(), replayRestored.GetCoinControlSnapshot());
			AssertEquivalent(state.GetCoinControlSnapshot(), protectedRestored.GetCoinControlSnapshot());
			AssertSelectionEquivalent(state, replayRestored);
			AssertSelectionEquivalent(state, protectedRestored);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelope is not null)
			{
				CryptographicOperations.ZeroMemory(envelope);
			}
		}
	}

	[Fact]
	public void OrdinaryConstructionAndStateFactoriesEnforceAllInvariants()
	{
		LiquidWalletCoinControlEntry first = Entry(Tx('a'), 101_003, PeggedAsset, 41_234_567);
		LiquidWalletCoinControlEntry second = Entry(Tx('b'), 202_009, IssuedAsset, 52_345_678);
		LiquidWalletCoinControlEntry foreign = LiquidWalletCoinControlEntry.Create(
			LiquidOutPoint.CreateSpendable(Tx('c'), 303_017),
			LiquidAssetAmount.Create(IssuedAsset, OtherPeggedAsset, 63_456_789),
			OtherPeggedAsset,
			null);
		LiquidWalletCoinControlEntry confirmed = LiquidWalletCoinControlEntry.Create(
			LiquidOutPoint.CreateSpendable(Tx('d'), 404_029),
			LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 74_567_891),
			PeggedAsset,
			LiquidConfirmation.Create(BlockHash, 4_242));
		LiquidWalletCoinControlEntry lateTransaction =
			Entry(Tx('f'), 505_043, IssuedAsset, 85_678_913);
		LiquidWalletCoinControlEntry middleTransaction =
			Entry(Tx('c'), 303_017, IssuedAsset, 63_456_789);
		LiquidWalletCoinControlEntry earliestTransaction =
			Entry(Tx(1), 606_049, IssuedAsset, 96_789_131);
		LiquidTransactionId sharedTransactionId = Tx('e');
		LiquidWalletCoinControlEntry lowOutputIndex =
			Entry(sharedTransactionId, 0, PeggedAsset, 107_891_337);
		LiquidWalletCoinControlEntry highOutputIndex = Entry(
			sharedTransactionId,
			LiquidOutPoint.MaxSpendableOutputIndex,
			IssuedAsset,
			118_913_579);
		LiquidWalletCoinControlEntry earlyHighIndex = Entry(
			Tx('a'),
			LiquidOutPoint.MaxSpendableOutputIndex,
			IssuedAsset,
			129_135_791);
		LiquidWalletCoinControlEntry lateLowIndex =
			Entry(Tx('c'), 0, PeggedAsset, 130_357_913);
		Assert.True(StringComparer.Ordinal.Compare(
			first.OutPoint.TransactionId.CanonicalRpcHex,
			middleTransaction.OutPoint.TransactionId.CanonicalRpcHex) < -1);
		Assert.True(StringComparer.Ordinal.Compare(
			middleTransaction.OutPoint.TransactionId.CanonicalRpcHex,
			first.OutPoint.TransactionId.CanonicalRpcHex) > 1);
		Assert.True(StringComparer.Ordinal.Compare(
			earliestTransaction.OutPoint.TransactionId.CanonicalRpcHex,
			lateTransaction.OutPoint.TransactionId.CanonicalRpcHex) < -1);
		Assert.True(StringComparer.Ordinal.Compare(
			lateTransaction.OutPoint.TransactionId.CanonicalRpcHex,
			earliestTransaction.OutPoint.TransactionId.CanonicalRpcHex) > 1);

		ArgumentNullException nullOutPoint = AssertRedactedFailure<ArgumentNullException>(() =>
			LiquidWalletCoinControlEntry.Create(null!, null!, null!, null), first, foreign);
		ArgumentNullException nullAmount = AssertRedactedFailure<ArgumentNullException>(() =>
			LiquidWalletCoinControlEntry.Create(first.OutPoint, null!, null!, null), first, foreign);
		ArgumentNullException nullPeggedContext = AssertRedactedFailure<ArgumentNullException>(() =>
			LiquidWalletCoinControlEntry.Create(first.OutPoint, first.Amount, null!, null), first, foreign);
		Assert.Equal("outPoint", nullOutPoint.ParamName);
		Assert.Equal("amount", nullAmount.ParamName);
		Assert.Equal("peggedAssetId", nullPeggedContext.ParamName);
		AssertRedactedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletCoinControlEntry.Create(
				first.OutPoint,
				LiquidAssetAmount.Zero(PeggedAsset, PeggedAsset),
				PeggedAsset,
				null),
			first,
			foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlEntry.Create(first.OutPoint, foreign.Amount, PeggedAsset, null),
			first,
			foreign);

		ArgumentNullException snapshotContextPrecedence = AssertRedactedFailure<ArgumentNullException>(() =>
			new LiquidWalletCoinControlSnapshot(null!, 0, null!), first, foreign);
		Assert.Equal("peggedAssetId", snapshotContextPrecedence.ParamName);
		AssertRedactedFailure<ArgumentNullException>(() =>
			new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, null!), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSnapshot(
				PeggedAsset,
				0,
				new LiquidWalletCoinControlEntry[] { null! }), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, [foreign]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, [first, first]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, [second, first]), first, second, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, [confirmed, confirmed]), confirmed);
		AssertSnapshotOrderingAccepted(first, middleTransaction);
		AssertSnapshotOrderingRejected(middleTransaction, first);
		AssertSnapshotOrderingAccepted(earliestTransaction, lateTransaction);
		AssertSnapshotOrderingRejected(lateTransaction, earliestTransaction);
		AssertSnapshotOrderingAccepted(lowOutputIndex, highOutputIndex);
		AssertSnapshotOrderingRejected(highOutputIndex, lowOutputIndex);
		AssertSnapshotOrderingRejected(lowOutputIndex, lowOutputIndex);
		AssertSnapshotOrderingAccepted(earlyHighIndex, lateLowIndex);
		AssertSnapshotOrderingRejected(lateLowIndex, earlyHighIndex);
		AssertSnapshotOrderingRejected(first, middleTransaction, second);

		AssertRedactedFailure<ArgumentNullException>(() =>
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(null!, 0, [first]), first, foreign);
		AssertRedactedFailure<ArgumentNullException>(() =>
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(PeggedAsset, 0, null!), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(
				PeggedAsset,
				0,
				new LiquidWalletCoinControlEntry[] { null! }), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(PeggedAsset, 0, [foreign]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(PeggedAsset, 0, [first, first]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(PeggedAsset, 0, [second, first]), first, second, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(
				PeggedAsset,
				0,
				[confirmed, confirmed]), confirmed);

		ArgumentNullException selectionContextPrecedence = AssertRedactedFailure<ArgumentNullException>(() =>
			new LiquidWalletCoinControlSelection(null!, 0, null!), first, foreign);
		Assert.Equal("peggedAssetId", selectionContextPrecedence.ParamName);
		AssertRedactedFailure<ArgumentNullException>(() =>
			new LiquidWalletCoinControlSelection(PeggedAsset, 0, null!), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSelection(PeggedAsset, 0, []), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSelection(
				PeggedAsset,
				0,
				new LiquidWalletCoinControlEntry[] { null! }), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSelection(PeggedAsset, 0, [foreign]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSelection(PeggedAsset, 0, [first, first]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSelection(PeggedAsset, 0, [second, first]), first, second, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			new LiquidWalletCoinControlSelection(PeggedAsset, 0, [confirmed, confirmed]), confirmed);

		AssertRedactedFailure<ArgumentNullException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(null!, 0, [first]), first, foreign);
		AssertRedactedFailure<ArgumentNullException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(PeggedAsset, 0, null!), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(PeggedAsset, 0, []), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(
				PeggedAsset,
				0,
				new LiquidWalletCoinControlEntry[] { null! }), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(PeggedAsset, 0, [foreign]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(PeggedAsset, 0, [first, first]), first, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(PeggedAsset, 0, [second, first]), first, second, foreign);
		AssertRedactedFailure<ArgumentException>(() =>
			LiquidWalletCoinControlSelection.TakeOwnershipFromState(
				PeggedAsset,
				0,
				[confirmed, confirmed]), confirmed);

		var source = new[] { first, second };
		var snapshot = new LiquidWalletCoinControlSnapshot(PeggedAsset, 7, source);
		var selection = new LiquidWalletCoinControlSelection(PeggedAsset, 7, source);
		source[0] = Entry(Tx('e'), 505_043, PeggedAsset, 85_678_913);
		Assert.Equal(first, snapshot.GetEntries()[0]);
		Assert.Equal(first, selection.GetEntries()[0]);
		var snapshotView = Assert.IsAssignableFrom<IList<LiquidWalletCoinControlEntry>>(snapshot.GetEntries());
		var selectionView = Assert.IsAssignableFrom<IList<LiquidWalletCoinControlEntry>>(selection.GetEntries());
		Assert.Throws<NotSupportedException>(() => snapshotView[0] = second);
		Assert.Throws<NotSupportedException>(() => selectionView[0] = second);
	}

	[Fact]
	public void HighCardinalityInventoriesAndSelectionsCompleteExactly()
	{
		AssertHighCardinalitySelection(9_279, useDistinctAssets: false);
		AssertHighCardinalitySelection(1_500, useDistinctAssets: true);
	}

	[Fact]
	public void ExactZeroConfirmationProtectedReplayBoundaryProjectsAndSelectsAllOutputs()
	{
		const int OutputCount = 119_833;
		Assert.Equal(
			16_777_188,
			48 + (13 * 40) + (OutputCount * (118 + 22)));
		Assert.Equal(16_777_204, LiquidWalletReplayCodec.MaxCanonicalLength);

		LiquidWalletReplaySnapshot source = CreateHighOutputReplaySnapshot(OutputCount, withConfirmation: false);
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(source, 119_833, key, context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletReplayOpenResult opened = protectedPayload.Open(key, context);
			LiquidWalletState state = LiquidWalletState.RestoreReplaySnapshot(opened.Snapshot);
			LiquidWalletCoinControlSnapshot inventory = state.GetCoinControlSnapshot();
			IReadOnlyList<LiquidWalletCoinControlEntry> entries = inventory.GetEntries();
			var selectedOutPoints = new LiquidOutPoint[entries.Count];
			for (int index = 0; index < entries.Count; index++)
			{
				selectedOutPoints[index] = entries[index].OutPoint;
			}
			LiquidWalletCoinControlSelection selection =
				state.CreateCoinControlSelection(state.Revision, selectedOutPoints);

			Assert.Equal(119_833ul, opened.Generation);
			Assert.Equal(13ul, state.Revision);
			Assert.Equal(OutputCount, entries.Count);
			Assert.Equal(OutputCount, selection.GetEntries().Count);
			Assert.All(entries, entry => Assert.Null(entry.Confirmation));
			Assert.Equal(
				OutputCount,
				selection.GetSelectedBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
			AssertCanonical(entries);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelope is not null)
			{
				CryptographicOperations.ZeroMemory(envelope);
			}
		}
	}

	[Fact]
	public void ConfirmationAndNextOutputCrossProtectedReplayByteCeiling()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			Assert.Equal(
				16_777_256,
				48 + (13 * 40) + (119_833 * (118 + 22)) + 68);
			Assert.Equal(
				16_777_328,
				48 + (13 * 40) + (119_834 * (118 + 22)));
			Assert.Throws<LiquidWalletReplayCapacityException>(() =>
				LiquidWalletReplayProtectedPayload.Seal(
					CreateHighOutputReplaySnapshot(119_833, withConfirmation: true),
					1,
					key,
					context));
			Assert.Throws<LiquidWalletReplayCapacityException>(() =>
				LiquidWalletReplayProtectedPayload.Seal(
					CreateHighOutputReplaySnapshot(119_834, withConfirmation: false),
					1,
					key,
					context));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	[Fact]
	public void CoinControlCallGraphsKeepBoundedOwnershipAndAggregation()
	{
		MethodInfo projection = RequiredMethod(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.GetCoinControlSnapshot),
			BindingFlags.Public | BindingFlags.Instance);
		MethodInfo selection = RequiredMethod(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.CreateCoinControlSelection),
			BindingFlags.Public | BindingFlags.Instance);
		MethodInfo stateSelectionCore = RequiredMethod(
			typeof(LiquidWalletState),
			"CreateCoinControlSelectionCore",
			BindingFlags.NonPublic | BindingFlags.Instance,
			typeof(ulong),
			typeof(IReadOnlyList<LiquidOutPoint>),
			typeof(int?));
		MethodInfo selectionCore = RequiredMethod(
			typeof(LiquidWalletCoinControlSelection),
			"ValidateAndAggregate",
			BindingFlags.NonPublic | BindingFlags.Static);
		MethodInfo snapshotOwnership = RequiredMethod(
			typeof(LiquidWalletCoinControlSnapshot),
			"TakeOwnershipFromState",
			BindingFlags.NonPublic | BindingFlags.Static);
		ConstructorInfo snapshotOwnershipConstructor = Assert.Single(
			typeof(LiquidWalletCoinControlSnapshot)
				.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance),
			constructor => constructor.GetParameters() is var parameters &&
				parameters.Length == 3 &&
				parameters[2].ParameterType == typeof(LiquidWalletCoinControlEntry[]));
		MethodInfo snapshotValidator = RequiredMethod(
			typeof(LiquidWalletCoinControlSnapshot),
			"ValidateEntries",
			BindingFlags.NonPublic | BindingFlags.Static);
		MethodInfo selectionOwnership = RequiredMethod(
			typeof(LiquidWalletCoinControlSelection),
			"TakeOwnershipFromState",
			BindingFlags.NonPublic | BindingFlags.Static);
		ConstructorInfo selectionOwnershipConstructor = Assert.Single(
			typeof(LiquidWalletCoinControlSelection)
				.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance),
			constructor => constructor.GetParameters() is var parameters &&
					parameters.Length == 3 &&
					parameters[2].ParameterType == typeof(LiquidWalletCoinControlEntry[]));
		MethodInfo balanceAdd = RequiredMethod(
			typeof(LiquidAssetBalanceMap),
			nameof(LiquidAssetBalanceMap.Add),
			BindingFlags.Public | BindingFlags.Instance);
		MethodInfo balanceSubtract = RequiredMethod(
			typeof(LiquidAssetBalanceMap),
			nameof(LiquidAssetBalanceMap.Subtract),
			BindingFlags.Public | BindingFlags.Instance);

		HashSet<MemberInfo> projectionGraph = GetCallGraph(projection, typeof(LiquidWalletState));
		HashSet<MemberInfo> selectionGraph = GetCallGraph(selection, typeof(LiquidWalletState));
		HashSet<MemberInfo> coreGraph = GetCallGraph(selectionCore, typeof(LiquidWalletCoinControlSelection));
		HashSet<MemberInfo> snapshotOwnershipGraph = GetCallGraph(
			snapshotOwnership,
			typeof(LiquidWalletCoinControlSnapshot));
		HashSet<MemberInfo> ownershipGraph = GetCallGraph(
			selectionOwnership,
			typeof(LiquidWalletCoinControlSelection));
		FieldInfo history = typeof(LiquidWalletState).GetField(
			"_history",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The retained replay history is unavailable.");

		Assert.DoesNotContain(history, projectionGraph);
		Assert.DoesNotContain(history, selectionGraph);
		Assert.DoesNotContain(balanceAdd, projectionGraph);
		Assert.DoesNotContain(balanceSubtract, projectionGraph);
		Assert.DoesNotContain(balanceAdd, selectionGraph);
		Assert.DoesNotContain(balanceSubtract, selectionGraph);
		Assert.DoesNotContain(balanceAdd, coreGraph);
		Assert.DoesNotContain(balanceSubtract, coreGraph);
		Assert.DoesNotContain(balanceAdd, ownershipGraph);
		Assert.DoesNotContain(balanceSubtract, ownershipGraph);
		foreach (HashSet<MemberInfo> graph in new[]
		{
			projectionGraph, selectionGraph, coreGraph, snapshotOwnershipGraph, ownershipGraph,
		})
		{
			Assert.DoesNotContain(graph.OfType<MethodBase>(), IsCollectionCopyOrMaterializer);
			Assert.DoesNotContain(graph.OfType<MethodBase>(), IsArrayCopyOrClone);
		}

		IReadOnlyList<IlReference> projectionDirect = GetIlReferences(projection).ToArray();
		IReadOnlyList<IlReference> selectionDirect = GetIlReferences(selection).ToArray();
		IReadOnlyList<IlReference> stateSelectionCoreDirect =
			GetIlReferences(stateSelectionCore).ToArray();
		IReadOnlyList<IlReference> snapshotOwnershipDirect =
			GetIlReferences(snapshotOwnership).ToArray();
		IReadOnlyList<IlReference> snapshotOwnershipConstructorDirect =
			GetIlReferences(snapshotOwnershipConstructor).ToArray();
		IReadOnlyList<IlReference> ownershipDirect = GetIlReferences(selectionOwnership).ToArray();
		IReadOnlyList<IlReference> ownershipConstructorDirect =
			GetIlReferences(selectionOwnershipConstructor).ToArray();
		IReadOnlyList<IlReference> coreDirect = GetIlReferences(selectionCore).ToArray();
		Assert.Contains(projectionDirect, reference => reference.Member == snapshotOwnership);
		Assert.Contains(selectionDirect, reference => reference.Member == stateSelectionCore);
		Assert.Contains(stateSelectionCoreDirect, reference => reference.Member == selectionOwnership);
		Assert.Equal(1, CountEntryArrayAllocations(projectionDirect));
		Assert.Equal(0, CountEntryArrayAllocations(selectionDirect));
		Assert.Equal(1, CountEntryArrayAllocations(stateSelectionCoreDirect));
		Assert.Equal(1, stateSelectionCoreDirect.Count(reference =>
			reference.Member is MethodBase method &&
			method.DeclaringType == typeof(Array) &&
			method.Name == nameof(Array.Sort)));
		Assert.Equal(1, snapshotOwnershipDirect.Count(reference =>
			reference.OpCode == OpCodes.Newobj &&
			reference.Member == snapshotOwnershipConstructor));
		Assert.Contains(snapshotOwnershipConstructor, snapshotOwnershipGraph);
		Assert.Contains(snapshotValidator, snapshotOwnershipGraph);
		Assert.Equal(1, snapshotOwnershipConstructorDirect.Count(reference =>
			reference.Member == snapshotValidator));
		Assert.Equal(1, ownershipDirect.Count(reference =>
			reference.OpCode == OpCodes.Newobj &&
			reference.Member == selectionOwnershipConstructor));
		Assert.Contains(selectionOwnershipConstructor, ownershipGraph);
		Assert.Contains(selectionCore, ownershipGraph);
		Assert.Equal(1, ownershipConstructorDirect.Count(reference =>
			reference.Member == selectionCore));
		Assert.Equal(1, coreDirect.Count(reference =>
			reference.Member is MethodInfo method &&
			method.DeclaringType == typeof(LiquidAssetBalanceMap) &&
			method.Name == nameof(LiquidAssetBalanceMap.FromAmounts)));
		Assert.Equal(0, CountEntryArrayAllocations(
			snapshotOwnershipGraph.OfType<MethodBase>().SelectMany(GetIlReferences)));
		Assert.Equal(0, CountEntryArrayAllocations(
			ownershipGraph.OfType<MethodBase>().SelectMany(GetIlReferences)));
		Assert.DoesNotContain(
			typeof(LiquidWalletState).GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
			field => field.FieldType == typeof(LiquidWalletCoinControlEntry[]) ||
				field.FieldType == typeof(LiquidWalletCoinControlSnapshot) ||
				field.FieldType == typeof(LiquidWalletCoinControlSelection));
	}

	[Fact]
	public void CoinControlBoundaryHasNoExternalExecutionSurfaceAndStringsAreRedacted()
	{
		foreach (Type boundaryType in new[]
		{
			typeof(LiquidWalletCoinControlEntry),
			typeof(LiquidWalletCoinControlSnapshot),
			typeof(LiquidWalletCoinControlSelection),
		})
		{
			IEnumerable<Type> signatureTypes = boundaryType
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Select(field => field.FieldType)
				.Concat(boundaryType
					.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
					.Select(property => property.PropertyType))
				.Concat(boundaryType
					.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
					.SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)))
				.Concat(boundaryType
					.GetMethods(
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
						BindingFlags.Static | BindingFlags.DeclaredOnly)
					.SelectMany(method => method.GetParameters()
						.Select(parameter => parameter.ParameterType)
						.Append(method.ReturnType)));

			Assert.DoesNotContain(signatureTypes, ContainsForbiddenExecutionType);
		}

		LiquidWalletCoinControlEntry entry = LiquidWalletCoinControlEntry.Create(
			LiquidOutPoint.CreateSpendable(Tx('a'), 606_049),
			LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 123_456_789),
			PeggedAsset,
			LiquidConfirmation.Create(BlockHash, 4_242));
		var snapshot = new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, [entry]);
		var selection = new LiquidWalletCoinControlSelection(PeggedAsset, 0, [entry]);
		foreach (string value in new[] { entry.ToString(), snapshot.ToString(), selection.ToString() })
		{
			Assert.DoesNotContain(entry.OutPoint.TransactionId.CanonicalRpcHex, value, StringComparison.Ordinal);
			Assert.DoesNotContain("606049", value, StringComparison.Ordinal);
			Assert.DoesNotContain(IssuedAssetHex, value, StringComparison.Ordinal);
			Assert.DoesNotContain("123456789", value, StringComparison.Ordinal);
			Assert.DoesNotContain(BlockHash, value, StringComparison.Ordinal);
			Assert.DoesNotContain("4242", value, StringComparison.Ordinal);
			Assert.DoesNotContain(PublicKeyHex, value, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				Convert.ToHexString(ExternalKey.GetScriptPubKey()),
				value,
				StringComparison.OrdinalIgnoreCase);
		}
		Assert.DoesNotContain(
			typeof(LiquidWalletCoinControlEntry).Assembly.GetReferencedAssemblies(),
			assembly => (assembly.Name ?? "").Contains("liquid-native", StringComparison.OrdinalIgnoreCase));
	}

	private static LiquidWalletState CreateMultiassetState()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput pegged = Output(receiveId, 0, PeggedAsset, 100);
		LiquidOwnedOutput issued = Output(receiveId, 1, IssuedAsset, 200);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput issuedChange = Output(spendId, 0, IssuedAsset, 150);
		return LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [pegged, issued]))
			.Apply(1, Delta(spendId, [issued.OutPoint], [issuedChange]))
			.Confirm(2, spendId, LiquidConfirmation.Create(BlockHash, 42));
	}

	private static void AssertHighCardinalitySelection(int count, bool useDistinctAssets)
	{
		LiquidTransactionId transactionId = Tx(useDistinctAssets ? 30_000u : 20_000u);
		LiquidSpendKeyReference key = ExternalKey;
		byte[] scriptPubKey = key.GetScriptPubKey();
		var outputs = new LiquidOwnedOutput[count];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] = LiquidOwnedOutput.Create(
				LiquidOutPoint.CreateSpendable(transactionId, (uint)index),
				scriptPubKey,
				LiquidAssetAmount.Create(
					useDistinctAssets ? Asset((uint)index + 1) : IssuedAsset,
					PeggedAsset,
					1),
				key);
		}
		LiquidWalletState state = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				1,
				[Delta(transactionId, [], outputs)],
				[]));
		IReadOnlyList<LiquidWalletCoinControlEntry> inventory =
			state.GetCoinControlSnapshot().GetEntries();
		LiquidWalletCoinControlSelection selection = state.CreateCoinControlSelection(
			1,
			inventory.Select(entry => entry.OutPoint).Reverse().ToArray());

		Assert.Equal(count, inventory.Count);
		Assert.Equal(count, selection.GetEntries().Count);
		Assert.Equal(useDistinctAssets ? count : 1, selection.GetSelectedBalances().AssetCount);
		if (useDistinctAssets)
		{
			IReadOnlyList<LiquidAssetAmount> amounts = selection.GetSelectedBalances().GetAmounts();
			Assert.Equal(Asset(1), amounts[0].AssetId);
			Assert.Equal(Asset((uint)count), amounts[^1].AssetId);
		}
		else
		{
			Assert.Equal(count, selection.GetSelectedBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
		}
	}

	private static LiquidWalletReplaySnapshot CreateHighOutputReplaySnapshot(
		int outputCount,
		bool withConfirmation)
	{
		int deltaCount = (outputCount + MaximumCreatedOutputsPerDelta - 1) /
			MaximumCreatedOutputsPerDelta;
		var deltas = new LiquidWalletTransactionDelta[deltaCount];
		LiquidSpendKeyReference key = ExternalKey;
		byte[] scriptPubKey = key.GetScriptPubKey();
		int remaining = outputCount;
		for (int deltaIndex = 0; deltaIndex < deltas.Length; deltaIndex++)
		{
			LiquidTransactionId transactionId = Tx((uint)deltaIndex + 100_000);
			int createdCount = Math.Min(MaximumCreatedOutputsPerDelta, remaining);
			var outputs = new LiquidOwnedOutput[createdCount];
			for (int outputIndex = 0; outputIndex < outputs.Length; outputIndex++)
			{
				outputs[outputIndex] = LiquidOwnedOutput.Create(
					LiquidOutPoint.CreateSpendable(transactionId, (uint)outputIndex),
					scriptPubKey,
					LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 1),
					key);
			}
			deltas[deltaIndex] = Delta(transactionId, [], outputs);
			remaining -= createdCount;
		}

		LiquidWalletReplayConfirmation[] confirmations = withConfirmation
			? [LiquidWalletReplayConfirmation.Create(
				deltas[0].TransactionId,
				LiquidConfirmation.Create(BlockHash, 42))]
			: [];
		return LiquidWalletReplaySnapshot.Create(
			PeggedAsset,
			(ulong)(deltaCount + confirmations.Length),
			deltas,
			confirmations);
	}

	private static void AssertEquivalent(
		LiquidWalletCoinControlSnapshot expected,
		LiquidWalletCoinControlSnapshot actual)
	{
		AssertSensitive(
			expected.PeggedAssetId == actual.PeggedAssetId,
			"The coin-control snapshot pegged context did not match.");
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletCoinControlEntry> expectedEntries = expected.GetEntries();
		IReadOnlyList<LiquidWalletCoinControlEntry> actualEntries = actual.GetEntries();
		Assert.Equal(expectedEntries.Count, actualEntries.Count);
		for (int index = 0; index < expectedEntries.Count; index++)
		{
			AssertEntryEquivalent(expectedEntries[index], actualEntries[index]);
		}
	}

	private static void AssertSelectionEquivalent(LiquidWalletState expected, LiquidWalletState actual)
	{
		LiquidOutPoint[] selected = expected.GetCoinControlSnapshot()
			.GetEntries()
			.Select(entry => entry.OutPoint)
			.ToArray();
		LiquidWalletCoinControlSelection expectedSelection =
			expected.CreateCoinControlSelection(expected.Revision, selected);
		LiquidWalletCoinControlSelection actualSelection =
			actual.CreateCoinControlSelection(actual.Revision, selected);
		Assert.Equal(expectedSelection.GetEntries().Select(entry => entry.OutPoint),
			actualSelection.GetEntries().Select(entry => entry.OutPoint));
		Assert.Equal(expectedSelection.GetSelectedBalances().GetAmounts(),
			actualSelection.GetSelectedBalances().GetAmounts());
	}

	private static void AssertCanonical(IReadOnlyList<LiquidWalletCoinControlEntry> entries)
	{
		for (int index = 1; index < entries.Count; index++)
		{
			LiquidWalletCoinControlEntry previous = entries[index - 1];
			LiquidWalletCoinControlEntry current = entries[index];
			int transactionOrder = StringComparer.Ordinal.Compare(
				previous.OutPoint.TransactionId.CanonicalRpcHex,
				current.OutPoint.TransactionId.CanonicalRpcHex);
			Assert.True(transactionOrder < 0 ||
				(transactionOrder == 0 && previous.OutPoint.OutputIndex < current.OutPoint.OutputIndex));
		}
	}

	private static TException AssertRedactedFailure<TException>(
		Action action,
		params LiquidWalletCoinControlEntry[] sensitiveEntries)
		where TException : Exception
	{
		TException exception = Assert.Throws<TException>(action);
		AssertRedacted(exception, sensitiveEntries);
		return exception;
	}

	private static void AssertRedacted(
		Exception exception,
		params LiquidWalletCoinControlEntry[] sensitiveEntries)
	{
		AssertRedactedText(exception.Message, PeggedAssetHex);
		AssertRedactedText(exception.Message, IssuedAssetHex);
		AssertRedactedText(exception.Message, OtherPeggedAssetHex);
		AssertRedactedText(exception.Message, BlockHash);
		AssertRedactedText(exception.Message, PublicKeyHex);
		AssertRedactedText(exception.Message, Convert.ToHexString(ExternalKey.GetScriptPubKey()));
		foreach (LiquidWalletCoinControlEntry entry in sensitiveEntries)
		{
			AssertRedactedText(exception.Message, entry.OutPoint.TransactionId.CanonicalRpcHex);
			AssertRedactedText(
				exception.Message,
				entry.OutPoint.OutputIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
			AssertRedactedText(
				exception.Message,
				entry.Amount.AtomicUnits.ToString(System.Globalization.CultureInfo.InvariantCulture));
			AssertRedactedText(exception.Message, entry.Amount.AssetId.CanonicalRpcHex);
			AssertRedactedText(exception.Message, entry.Amount.PeggedAssetId.CanonicalRpcHex);
			if (entry.Confirmation is { } confirmation)
			{
				AssertRedactedText(exception.Message, confirmation.CanonicalBlockHash);
				AssertRedactedText(
					exception.Message,
					confirmation.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
			}
		}
	}

	private static MethodInfo RequiredMethod(Type type, string name, BindingFlags bindingFlags) =>
		type.GetMethod(name, bindingFlags) ??
		throw new InvalidOperationException($"The required {type.Name} method is unavailable.");

	private static HashSet<MemberInfo> GetCallGraph(MethodBase root, Type owningType)
	{
		var discovered = new HashSet<MemberInfo> { root };
		var pending = new Queue<MethodBase>();
		pending.Enqueue(root);
		while (pending.TryDequeue(out MethodBase? current))
		{
			foreach (IlReference reference in GetIlReferences(current))
			{
				MemberInfo member = reference.Member;
				if (!discovered.Add(member))
				{
					continue;
				}
				if (member is MethodBase called &&
					(called.DeclaringType == owningType || called.DeclaringType?.DeclaringType == owningType))
				{
					pending.Enqueue(called);
				}
			}
		}
		return discovered;
	}

	private static IEnumerable<IlReference> GetIlReferences(MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		Dictionary<short, OpCode> opCodes = typeof(OpCodes)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.FieldType == typeof(OpCode))
			.Select(field => (OpCode)field.GetValue(null)!)
			.ToDictionary(opCode => opCode.Value);
		int position = 0;
		while (position < il.Length)
		{
			short value = il[position++] == 0xfe
				? unchecked((short)(0xfe00 | il[position++]))
				: il[position - 1];
			OpCode opCode = opCodes[value];
			if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or
				OperandType.InlineType or OperandType.InlineTok)
			{
				int token = BitConverter.ToInt32(il, position);
				MemberInfo? member = method.Module.ResolveMember(
					token,
					method.DeclaringType?.GetGenericArguments(),
					method.IsGenericMethod ? method.GetGenericArguments() : null);
				if (member is not null)
				{
					yield return new IlReference(opCode, member);
				}
			}
			position += GetOperandSize(opCode.OperandType, il, position);
		}
	}

	private static int CountEntryArrayAllocations(IEnumerable<IlReference> references) =>
		references.Count(reference =>
			reference.OpCode.Equals(OpCodes.Newarr) &&
			reference.Member == typeof(LiquidWalletCoinControlEntry));

	private static bool IsArrayCopyOrClone(MethodBase method) =>
		(method.DeclaringType == typeof(Array) && method.Name == nameof(Array.Copy)) ||
		(method.Name == nameof(Array.Clone) &&
			(method.DeclaringType == typeof(Array) || method.DeclaringType?.IsArray == true));

	private static bool IsCollectionCopyOrMaterializer(MethodBase method)
	{
		if (method.DeclaringType == typeof(Enumerable) &&
			method.Name is nameof(Enumerable.ToArray) or nameof(Enumerable.ToDictionary) or
				nameof(Enumerable.ToHashSet) or nameof(Enumerable.ToList) or
				nameof(Enumerable.OrderBy) or nameof(Enumerable.ThenBy))
		{
			return true;
		}

		if (!method.IsConstructor || method.DeclaringType is null ||
			!method.DeclaringType.IsGenericType)
		{
			return false;
		}

		Type genericType = method.DeclaringType.GetGenericTypeDefinition();
		if (genericType != typeof(Dictionary<,>) &&
			genericType != typeof(HashSet<>) &&
			genericType != typeof(List<>) &&
			genericType != typeof(SortedDictionary<,>))
		{
			return false;
		}

		return method.GetParameters().Any(parameter =>
		{
			Type parameterType = parameter.ParameterType;
			return parameterType.IsGenericType &&
				parameterType.GetGenericTypeDefinition() is var definition &&
				(definition == typeof(IEnumerable<>) ||
				 definition == typeof(ICollection<>) ||
				 definition == typeof(IDictionary<,>) ||
				 definition == typeof(IReadOnlyDictionary<,>));
		});
	}

	private static int GetOperandSize(OperandType operandType, byte[] il, int position) =>
		operandType switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
				OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
				OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => sizeof(int) +
				(BitConverter.ToInt32(il, position) * sizeof(int)),
			_ => throw new InvalidOperationException("An unsupported IL operand type was encountered."),
		};

	private static bool ContainsForbiddenExecutionType(Type type) =>
		ContainsForbiddenExecutionType(type, new HashSet<Type>());

	private static bool ContainsForbiddenExecutionType(Type type, HashSet<Type> visited)
	{
		if (!visited.Add(type))
		{
			return false;
		}
		if (type.HasElementType)
		{
			return ContainsForbiddenExecutionType(type.GetElementType()!, visited);
		}
		if (type.IsGenericType && type.GetGenericArguments().Any(argument =>
			ContainsForbiddenExecutionType(argument, visited)))
		{
			return true;
		}
		if (type.GetInterfaces().Any(@interface =>
			ContainsForbiddenExecutionType(@interface, visited)))
		{
			return true;
		}
		if (type.BaseType is not null &&
			ContainsForbiddenExecutionType(type.BaseType, visited))
		{
			return true;
		}

		string name = type.FullName ?? type.Name;
		return name.Contains("ISerializable", StringComparison.OrdinalIgnoreCase) ||
			IsForbiddenExecutionIdentity(name);
	}

	private sealed record IlReference(OpCode OpCode, MemberInfo Member);

	private static LiquidWalletCoinControlEntry Entry(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits) =>
		LiquidWalletCoinControlEntry.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits),
			PeggedAsset,
			null);

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidTransactionId Tx(uint value) =>
		LiquidTransactionId.ParseRpcHex(value.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));

	private static LiquidAssetId Asset(uint value) =>
		LiquidAssetId.ParseRpcHex(value.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));

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

	private sealed class ThrowingSelectionList : IReadOnlyList<LiquidOutPoint>
	{
		public int Count => throw new SelectionInspectedException();

		public LiquidOutPoint this[int index] =>
			throw new SelectionInspectedException();

		public IEnumerator<LiquidOutPoint> GetEnumerator() =>
			throw new SelectionInspectedException();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class ThrowingElementSelectionList(int count) : IReadOnlyList<LiquidOutPoint>
	{
		public int Count { get; } = count;

		public LiquidOutPoint this[int index] => throw new SelectionInspectedException();

		public IEnumerator<LiquidOutPoint> GetEnumerator() => throw new SelectionInspectedException();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CountOnceSelectionList(LiquidOutPoint outPoint) : IReadOnlyList<LiquidOutPoint>
	{
		private readonly LiquidOutPoint _outPoint = outPoint;

		public int Count
		{
			get
			{
				CountReads++;
				if (CountReads != 1)
				{
					throw new SelectionInspectedException();
				}

				return 1;
			}
		}

		public int CountReads { get; private set; }

		public LiquidOutPoint this[int index] => index == 0
			? _outPoint
			: throw new ArgumentOutOfRangeException(nameof(index));

		public IEnumerator<LiquidOutPoint> GetEnumerator()
		{
			yield return _outPoint;
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class SelectionInspectedException : Exception
	{
	}

	[Fact]
	public void ExactUnspentQueryReturnsContextBoundZeroOrOneEntries()
	{
		LiquidOutPoint unknown = LiquidOutPoint.CreateSpendable(Tx('f'), 909_091);
		LiquidWalletCoinControlSnapshot empty = LiquidWalletState.Empty(PeggedAsset)
			.QueryUnspentCoinControlEntry(0, unknown);
		AssertQuerySnapshot(empty, 0, expectedEntry: null);

		LiquidWalletState state = CreateMultiassetState();
		IReadOnlyList<LiquidWalletCoinControlEntry> inventory =
			state.GetCoinControlSnapshot().GetEntries();
		LiquidWalletCoinControlEntry pegged = Assert.Single(
			inventory,
			entry => entry.Amount.AssetId == PeggedAsset);
		LiquidWalletCoinControlEntry issued = Assert.Single(
			inventory,
			entry => entry.Amount.AssetId == IssuedAsset);

		AssertQuerySnapshot(
			state.QueryUnspentCoinControlEntry(state.Revision, pegged.OutPoint),
			state.Revision,
			pegged);
		AssertQuerySnapshot(
			state.QueryUnspentCoinControlEntry(state.Revision, issued.OutPoint),
			state.Revision,
			issued);
		AssertQuerySnapshot(
			state.QueryUnspentCoinControlEntry(state.Revision, unknown),
			state.Revision,
			expectedEntry: null);
	}

	[Fact]
	public void ExactUnspentQueryValidatesRevisionBeforeNullAndRedactsFailures()
	{
		LiquidWalletState state = CreateMultiassetState();
		Exception stale;
		try
		{
			state.QueryUnspentCoinControlEntry(state.Revision - 1, null!);
			throw new Xunit.Sdk.XunitException("The stale exact query unexpectedly succeeded.");
		}
		catch (Exception exception)
		{
			stale = Assert.IsType<InvalidOperationException>(exception);
		}
		Assert.Equal(
			"The Liquid wallet state revision changed before the requested transition.",
			stale.Message);
		AssertRedacted(stale);

		ArgumentNullException nullOutPoint;
		try
		{
			state.QueryUnspentCoinControlEntry(state.Revision, null!);
			throw new Xunit.Sdk.XunitException("The null exact query unexpectedly succeeded.");
		}
		catch (Exception exception)
		{
			nullOutPoint = Assert.IsType<ArgumentNullException>(exception);
			AssertRedacted(nullOutPoint);
		}
		Assert.Equal("outPoint", nullOutPoint.ParamName);
	}

	[Fact]
	public void ExactUnspentQueryTracksApplySpendAndRollbackWithoutChangingOldSnapshots()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput receivedOutput = Output(receiveId, 101_003, PeggedAsset, 41_234_567);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput change = Output(spendId, 202_009, IssuedAsset, 52_345_678);
		LiquidWalletState emptyState = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletCoinControlSnapshot preApplyMiss =
			emptyState.QueryUnspentCoinControlEntry(0, receivedOutput.OutPoint);
		LiquidWalletState receivedState = emptyState.Apply(
			0,
			Delta(receiveId, [], [receivedOutput]));
		LiquidWalletCoinControlSnapshot postCreateHit = receivedState
			.QueryUnspentCoinControlEntry(1, receivedOutput.OutPoint);
		LiquidWalletCoinControlEntry expectedReceived = Assert.Single(
			receivedState.GetCoinControlSnapshot().GetEntries());
		AssertQuerySnapshot(preApplyMiss, 0, expectedEntry: null);
		AssertQuerySnapshot(postCreateHit, 1, expectedReceived);

		LiquidWalletState spentState = receivedState.Apply(
			1,
			Delta(spendId, [receivedOutput.OutPoint], [change]));
		LiquidWalletCoinControlSnapshot postSpendMiss = spentState
			.QueryUnspentCoinControlEntry(2, receivedOutput.OutPoint);
		AssertQuerySnapshot(preApplyMiss, 0, expectedEntry: null);
		AssertQuerySnapshot(postCreateHit, 1, expectedReceived);
		AssertQuerySnapshot(postSpendMiss, 2, expectedEntry: null);

		LiquidWalletState rolledBackState = spentState.RollbackLast(2, spendId);
		LiquidWalletCoinControlSnapshot restoredHit = rolledBackState
			.QueryUnspentCoinControlEntry(3, receivedOutput.OutPoint);
		LiquidWalletCoinControlSnapshot rolledBackSpendCreateMiss = rolledBackState
			.QueryUnspentCoinControlEntry(3, change.OutPoint);
		AssertQuerySnapshot(preApplyMiss, 0, expectedEntry: null);
		AssertQuerySnapshot(postCreateHit, 1, expectedReceived);
		AssertQuerySnapshot(postSpendMiss, 2, expectedEntry: null);
		AssertQuerySnapshot(restoredHit, 3, expectedReceived);
		AssertQuerySnapshot(rolledBackSpendCreateMiss, 3, expectedEntry: null);

		LiquidWalletState rolledBackCreateState = rolledBackState.RollbackLast(3, receiveId);
		LiquidWalletCoinControlSnapshot rolledBackOriginalCreateMiss = rolledBackCreateState
			.QueryUnspentCoinControlEntry(4, receivedOutput.OutPoint);
		AssertQuerySnapshot(preApplyMiss, 0, expectedEntry: null);
		AssertQuerySnapshot(postCreateHit, 1, expectedReceived);
		AssertQuerySnapshot(postSpendMiss, 2, expectedEntry: null);
		AssertQuerySnapshot(restoredHit, 3, expectedReceived);
		AssertQuerySnapshot(rolledBackOriginalCreateMiss, 4, expectedEntry: null);
	}

	[Fact]
	public void ExactUnspentQueryConfirmationTransitionsOnlyChangeRevisionAndAttachment()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidOwnedOutput output = Output(transactionId, 303_017, IssuedAsset, 63_456_789);
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 4_242);
		LiquidWalletState applied = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [output]));
		LiquidWalletCoinControlSnapshot absent =
			applied.QueryUnspentCoinControlEntry(1, output.OutPoint);
		LiquidWalletCoinControlEntry absentEntry = Assert.Single(
			applied.GetCoinControlSnapshot().GetEntries());
		AssertQuerySnapshot(absent, 1, absentEntry);
		Assert.Null(absentEntry.Confirmation);

		LiquidWalletState confirmedState = applied.Confirm(1, transactionId, confirmation);
		LiquidWalletCoinControlSnapshot attached =
			confirmedState.QueryUnspentCoinControlEntry(2, output.OutPoint);
		LiquidWalletCoinControlEntry attachedEntry = Assert.Single(
			confirmedState.GetCoinControlSnapshot().GetEntries());
		AssertQuerySnapshot(absent, 1, absentEntry);
		AssertQuerySnapshot(attached, 2, attachedEntry);
		AssertEntryEquivalentExceptConfirmation(absentEntry, attachedEntry);
		AssertSensitive(
			attachedEntry.Confirmation == confirmation,
			"The exact query confirmation attachment did not match.");

		LiquidWalletState unconfirmedState = confirmedState.Unconfirm(2, transactionId, confirmation);
		LiquidWalletCoinControlSnapshot detached =
			unconfirmedState.QueryUnspentCoinControlEntry(3, output.OutPoint);
		LiquidWalletCoinControlEntry detachedEntry = Assert.Single(
			unconfirmedState.GetCoinControlSnapshot().GetEntries());
		AssertQuerySnapshot(absent, 1, absentEntry);
		AssertQuerySnapshot(attached, 2, attachedEntry);
		AssertQuerySnapshot(detached, 3, detachedEntry);
		AssertEntryEquivalentExceptConfirmation(attachedEntry, detachedEntry);
		Assert.Null(detachedEntry.Confirmation);
	}

	[Fact]
	public void ExactUnspentQuerySurvivesReplayAndProtectedReplayForHitsAndMisses()
	{
		LiquidWalletState state = CreateMultiassetState();
		LiquidOutPoint hit = state.GetCoinControlSnapshot().GetEntries()[0].OutPoint;
		LiquidOutPoint miss = LiquidOutPoint.CreateSpendable(Tx('f'), 404_029);
		LiquidWalletState replayRestored =
			LiquidWalletState.RestoreReplaySnapshot(state.ExportReplaySnapshot());
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(
			LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(state.ExportReplaySnapshot(), 23, key, context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletReplayOpenResult opened = protectedPayload.Open(key, context);
			LiquidWalletState protectedRestored =
				LiquidWalletState.RestoreReplaySnapshot(opened.Snapshot);

			Assert.Equal(23ul, opened.Generation);
			foreach (LiquidOutPoint outPoint in new[] { hit, miss })
			{
				AssertEquivalent(
					state.QueryUnspentCoinControlEntry(state.Revision, outPoint),
					replayRestored.QueryUnspentCoinControlEntry(replayRestored.Revision, outPoint));
				AssertEquivalent(
					state.QueryUnspentCoinControlEntry(state.Revision, outPoint),
					protectedRestored.QueryUnspentCoinControlEntry(protectedRestored.Revision, outPoint));
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
			if (envelope is not null)
			{
				CryptographicOperations.ZeroMemory(envelope);
			}
		}
	}

	[Fact]
	public void ExactUnspentQueryFindsFirstMiddleAndLastInLargeMultiassetInventory()
	{
		const int OutputCount = 1_500;
		LiquidWalletState state = CreateLargeMultiassetState(OutputCount);
		IReadOnlyList<LiquidWalletCoinControlEntry> inventory =
			state.GetCoinControlSnapshot().GetEntries();
		Assert.Equal(OutputCount, inventory.Count);

		foreach (int index in new[] { 0, OutputCount / 2, OutputCount - 1 })
		{
			LiquidWalletCoinControlEntry expected = inventory[index];
			AssertQuerySnapshot(
				state.QueryUnspentCoinControlEntry(state.Revision, expected.OutPoint),
				state.Revision,
				expected);
		}

		AssertQuerySnapshot(
			state.QueryUnspentCoinControlEntry(
				state.Revision,
				LiquidOutPoint.CreateSpendable(Tx(99_999), 1_499)),
			state.Revision,
			expectedEntry: null);
	}

	[Fact]
	public void ExactUnspentQueryEveryPathLeavesAllStateViewsEquivalent()
	{
		LiquidWalletState state = CreateMultiassetState();
		LiquidOutPoint hit = state.GetCoinControlSnapshot().GetEntries()[0].OutPoint;
		LiquidOutPoint miss = LiquidOutPoint.CreateSpendable(Tx('f'), 505_031);

		AssertStateUnchanged(state, 0, hit, miss);
		AssertStateUnchanged(state, 1, hit, miss);
		AssertStateUnchanged(state, 2, hit, miss);
		AssertStateUnchanged(state, 3, hit, miss);
	}

	[Fact]
	public void ExactUnspentQueryResultsAreDefensiveAndSensitiveValuesRemainRedacted()
	{
		const uint OutputIndex = 606_049;
		const long AtomicUnits = 123_456_789;
		const int DerivationIndex = 73_421;
		const int Height = 4_242;
		LiquidTransactionId transactionId = Tx('e');
		LiquidSpendKeyReference internalKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString(PublicKeyHex),
			LiquidKeyBranch.Internal,
			DerivationIndex);
		byte[] scriptPubKey = internalKey.GetScriptPubKey();
		LiquidOwnedOutput output = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, OutputIndex),
			scriptPubKey,
			LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, AtomicUnits),
			internalKey);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [output]))
			.Confirm(1, transactionId, LiquidConfirmation.Create(BlockHash, Height));
		LiquidWalletCoinControlSnapshot hit =
			state.QueryUnspentCoinControlEntry(2, output.OutPoint);
		LiquidWalletCoinControlSnapshot secondHit =
			state.QueryUnspentCoinControlEntry(2, output.OutPoint);
		LiquidOutPoint unknown = LiquidOutPoint.CreateSpendable(Tx('f'), 707_059);
		LiquidWalletCoinControlSnapshot miss = state.QueryUnspentCoinControlEntry(2, unknown);
		LiquidWalletCoinControlSnapshot secondMiss = state.QueryUnspentCoinControlEntry(2, unknown);
		var hitView = Assert.IsAssignableFrom<IList<LiquidWalletCoinControlEntry>>(hit.GetEntries());
		var missView = Assert.IsAssignableFrom<IList<LiquidWalletCoinControlEntry>>(miss.GetEntries());
		LiquidWalletCoinControlEntry hitEntry = Assert.Single(hitView);

		Assert.NotSame(hit.GetEntries(), secondHit.GetEntries());
		Assert.NotSame(miss.GetEntries(), secondMiss.GetEntries());
		Assert.True(hitView.IsReadOnly);
		Assert.True(missView.IsReadOnly);
		try
		{
			hitView[0] = Entry(Tx('d'), 1, PeggedAsset, 1);
			throw new Xunit.Sdk.XunitException("The exact hit view unexpectedly accepted mutation.");
		}
		catch (Exception exception)
		{
			Assert.IsType<NotSupportedException>(exception);
		}
		try
		{
			missView.Add(hitEntry);
			throw new Xunit.Sdk.XunitException("The exact miss view unexpectedly accepted mutation.");
		}
		catch (Exception exception)
		{
			Assert.IsType<NotSupportedException>(exception);
		}
		AssertQuerySnapshot(secondHit, 2, hitEntry);
		AssertQuerySnapshot(secondMiss, 2, expectedEntry: null);
		AssertQuerySnapshot(state.QueryUnspentCoinControlEntry(2, output.OutPoint), 2, hitEntry);

		Exception stale;
		try
		{
			state.QueryUnspentCoinControlEntry(1, output.OutPoint);
			throw new Xunit.Sdk.XunitException("The stale exact query unexpectedly succeeded.");
		}
		catch (Exception exception)
		{
			stale = Assert.IsType<InvalidOperationException>(exception);
		}
		ArgumentNullException nullOutPoint;
		try
		{
			state.QueryUnspentCoinControlEntry(2, null!);
			throw new Xunit.Sdk.XunitException("The null exact query unexpectedly succeeded.");
		}
		catch (Exception exception)
		{
			nullOutPoint = Assert.IsType<ArgumentNullException>(exception);
		}
		Assert.Equal("outPoint", nullOutPoint.ParamName);
		string text = string.Join(
			"|",
			new string?[]
			{
				hit.ToString(),
				hitEntry.ToString(),
				miss.ToString(),
				stale.Message,
				nullOutPoint.Message,
				nullOutPoint.ParamName,
			});
		Exception? redactedMismatch = null;
		try
		{
			AssertEntryEquivalent(hitEntry, Entry(Tx('d'), 1, PeggedAsset, 1));
		}
		catch (Exception exception)
		{
			redactedMismatch = exception;
		}
		Assert.NotNull(redactedMismatch);
		text = string.Join("|", text, redactedMismatch.Message);
		Exception? redactionAssertion = null;
		try
		{
			AssertRedacted(new InvalidOperationException(PeggedAssetHex));
		}
		catch (Exception exception)
		{
			redactionAssertion = exception;
		}
		Assert.NotNull(redactionAssertion);
		text = string.Join("|", text, redactionAssertion.Message);
		foreach (string canary in new[]
		{
			transactionId.CanonicalRpcHex,
			OutputIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
			IssuedAssetHex,
			PeggedAssetHex,
			AtomicUnits.ToString(System.Globalization.CultureInfo.InvariantCulture),
			Convert.ToHexString(scriptPubKey),
			PublicKeyHex,
			nameof(LiquidKeyBranch.Internal),
			DerivationIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
			BlockHash,
			Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
		})
		{
			AssertSensitive(
				!text.Contains(canary, StringComparison.OrdinalIgnoreCase),
				"Sensitive exact-query data appeared in failure or diagnostic text.");
		}
	}

	[Fact]
	public void ExactUnspentQueryHasOneLookupBoundedOwnershipAndFrozenPublicSurface()
	{
		MethodInfo query = RequiredMethod(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.QueryUnspentCoinControlEntry),
			BindingFlags.Public | BindingFlags.Instance);
		MethodInfo ensureRevision = RequiredMethod(
			typeof(LiquidWalletState),
			"EnsureRevision",
			BindingFlags.NonPublic | BindingFlags.Instance);
		MethodInfo builder = RequiredMethod(
			typeof(LiquidWalletState),
			"CreateCoinControlEntry",
			BindingFlags.NonPublic | BindingFlags.Instance);
		MethodInfo entryFactory = RequiredMethod(
			typeof(LiquidWalletCoinControlEntry),
			nameof(LiquidWalletCoinControlEntry.Create),
			BindingFlags.NonPublic | BindingFlags.Static);
		ConstructorInfo entryConstructor = Assert.Single(
			typeof(LiquidWalletCoinControlEntry)
				.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));
		MethodInfo ownership = RequiredMethod(
			typeof(LiquidWalletCoinControlSnapshot),
			"TakeOwnershipFromState",
			BindingFlags.NonPublic | BindingFlags.Static);
		ConstructorInfo ownershipConstructor = Assert.Single(
			typeof(LiquidWalletCoinControlSnapshot)
				.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance),
			constructor => constructor.GetParameters() is var parameters &&
				parameters.Length == 3 &&
				parameters[2].ParameterType == typeof(LiquidWalletCoinControlEntry[]));
		MethodInfo validator = RequiredMethod(
			typeof(LiquidWalletCoinControlSnapshot),
			"ValidateEntries",
			BindingFlags.NonPublic | BindingFlags.Static);
		MethodInfo comparator = RequiredMethod(
			typeof(LiquidWalletCoinControlSnapshot),
			"CompareCanonical",
			BindingFlags.NonPublic | BindingFlags.Static);
		FieldInfo unspent = RequiredField(typeof(LiquidWalletState), "_unspentOutputs");
		FieldInfo confirmations = RequiredField(typeof(LiquidWalletState), "_confirmations");
		MethodInfo peggedAssetGetter = RequiredPropertyGetter(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.PeggedAssetId));
		MethodInfo revisionGetter = RequiredPropertyGetter(
			typeof(LiquidWalletState),
			nameof(LiquidWalletState.Revision));

		MethodInfo reflectedQuery = Assert.Single(
			typeof(LiquidWalletState).GetMethods(
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
					BindingFlags.DeclaredOnly),
			method => method.Name == nameof(LiquidWalletState.QueryUnspentCoinControlEntry));
		Assert.Equal(query, reflectedQuery);
		Assert.Equal(typeof(LiquidWalletCoinControlSnapshot), query.ReturnType);
		Assert.Equal(
			[typeof(ulong), typeof(LiquidOutPoint)],
			query.GetParameters().Select(parameter => parameter.ParameterType));
		var expectedManifest = new HashSet<string>(StringComparer.Ordinal)
			{
				"public instance GetCoinControlSnapshot/0 () -> LiquidWalletCoinControlSnapshot",
				"public instance CreateCoinControlSelection/0 (value ulong, value IReadOnlyList<LiquidOutPoint>) -> LiquidWalletCoinControlSelection",
				"public instance CreateExactOrdinaryWalletSpendPlan/0 (value ulong, value IReadOnlyList<LiquidOutPoint>, value LiquidSuppliedConfidentialDestinationBatch, value LiquidAssetAmount) -> LiquidOrdinaryWalletExactSpendPlan",
				"public instance ContainsUnspent/0 (value LiquidOutPoint) -> bool",
				"public instance QueryUnspentCoinControlEntry/0 (value ulong, value LiquidOutPoint) -> LiquidWalletCoinControlSnapshot",
			};
		HashSet<string> actualManifest = GetSelectedCoinControlStateManifest();
		Assert.Equal(expectedManifest.Count, actualManifest.Count);
		Assert.True(expectedManifest.SetEquals(actualManifest));

		IReadOnlyList<(int Offset, MethodBase Method)> directCalls =
			GetCalledMethodInstructions(query).ToArray();
		foreach (var site in directCalls)
		{
			Assert.True(
				IsPermittedExactUnspentQueryCall(site.Method, ensureRevision, builder, ownership),
				$"Unexpected exact-unspent-query call: {site.Method.DeclaringType}.{site.Method.Name}.");
		}
		Assert.Equal(1, CountCallSites(directCalls, ensureRevision));
		Assert.Equal(1, directCalls.Count(site =>
			site.Method.DeclaringType == typeof(ArgumentNullException) &&
			site.Method.Name == nameof(ArgumentNullException.ThrowIfNull)));
		Assert.Equal(1, directCalls.Count(site =>
			site.Method.DeclaringType == typeof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>) &&
			site.Method.Name == nameof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>.TryGetValue)));
		Assert.Equal(1, CountCallSites(directCalls, builder));
		Assert.Equal(1, CountCallSites(directCalls, ownership));
		AssertExactCallMultiset(
			query,
			ensureRevision,
			RequiredMethod(
				typeof(ArgumentNullException),
				nameof(ArgumentNullException.ThrowIfNull),
				BindingFlags.Public | BindingFlags.Static,
				typeof(object),
				typeof(string)),
			RequiredMethod(
				typeof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>),
				nameof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>.TryGetValue),
				BindingFlags.Public | BindingFlags.Instance,
				typeof(LiquidOutPoint),
				typeof(LiquidOwnedOutput).MakeByRefType()),
			builder,
			peggedAssetGetter,
			revisionGetter,
			ownership);

		int revisionOffset = GetSingleCallOffset(directCalls, ensureRevision);
		int nullOffset = Assert.Single(directCalls, site =>
			site.Method.DeclaringType == typeof(ArgumentNullException) &&
			site.Method.Name == nameof(ArgumentNullException.ThrowIfNull)).Offset;
		int lookupOffset = Assert.Single(directCalls, site =>
			site.Method.DeclaringType == typeof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>) &&
			site.Method.Name == nameof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>.TryGetValue)).Offset;
		int builderOffset = GetSingleCallOffset(directCalls, builder);
		int ownershipOffset = GetSingleCallOffset(directCalls, ownership);
		int storeOffset = Assert.Single(
			GetIlInstructions(query),
			instruction => instruction.OpCode == OpCodes.Stelem_Ref).Offset;
		Assert.True(revisionOffset < nullOffset);
		Assert.True(nullOffset < lookupOffset);
		Assert.True(lookupOffset < builderOffset);
		Assert.True(builderOffset < storeOffset);
		Assert.True(storeOffset < ownershipOffset);
		(int Offset, int Target, OpCode OpCode)? hitGuardMatch = null;
		foreach (var branch in GetBranchEdges(query))
		{
			if (branch.Offset > lookupOffset && branch.Offset < builderOffset &&
				branch.Target > storeOffset && branch.Target < ownershipOffset)
			{
				Assert.Null(hitGuardMatch);
				hitGuardMatch = branch;
			}
		}
		var hitGuard = Assert.IsType<(int Offset, int Target, OpCode OpCode)>(hitGuardMatch);
		Assert.Equal(FlowControl.Cond_Branch, hitGuard.OpCode.FlowControl);
		Assert.InRange(builderOffset, hitGuard.Offset + 1, hitGuard.Target - 1);
		Assert.InRange(storeOffset, hitGuard.Offset + 1, hitGuard.Target - 1);

		var queryFlow = CreateValidControlFlow(query);
		Assert.Empty(query.GetMethodBody()?.ExceptionHandlingClauses ?? []);
		foreach (var instruction in queryFlow.Instructions)
		{
			Assert.Contains(instruction.Offset, queryFlow.ReachableOffsets);
		}
		AssertDominates(queryFlow, revisionOffset, nullOffset);
		AssertDominates(queryFlow, nullOffset, lookupOffset);
		int newArrayOffset = GetSingleInstructionOffset(
			query,
			queryFlow.Instructions,
			OpCodes.Newarr,
			typeof(LiquidWalletCoinControlEntry));
		AssertDominates(queryFlow, lookupOffset, newArrayOffset);
		var queryReturn = Assert.Single(
			queryFlow.Instructions,
			instruction => instruction.OpCode == OpCodes.Ret);
		Assert.DoesNotContain(
			queryFlow.Instructions,
			instruction => instruction.OpCode is var opCode &&
				(opCode == OpCodes.Throw || opCode == OpCodes.Rethrow ||
				 opCode == OpCodes.Leave || opCode == OpCodes.Leave_S ||
				 opCode == OpCodes.Switch));
		AssertDominates(queryFlow, newArrayOffset, ownershipOffset);
		AssertDominates(queryFlow, ownershipOffset, queryReturn.Offset);
		AssertExactRevisionGuardControlFlow(ensureRevision);
		AssertExactQueryArrayDataFlow(
			query,
			queryFlow,
			lookupOffset,
			newArrayOffset,
			builderOffset,
			storeOffset,
			ownershipOffset,
			unspent,
			peggedAssetGetter,
			revisionGetter);

		IReadOnlyList<(int Offset, MethodBase Method)> builderCalls =
			GetCalledMethodInstructions(builder).ToArray();
		Assert.Equal(1, builderCalls.Count(site =>
			site.Method.DeclaringType == typeof(Dictionary<LiquidTransactionId, LiquidConfirmation>) &&
			site.Method.Name == nameof(Dictionary<LiquidTransactionId, LiquidConfirmation>.TryGetValue)));
		Assert.Equal(1, builderCalls.Count(site =>
			site.Method.DeclaringType == typeof(LiquidWalletCoinControlEntry) &&
			site.Method.Name == nameof(LiquidWalletCoinControlEntry.Create)));
		Assert.DoesNotContain(directCalls, site =>
			site.Method.DeclaringType == typeof(Dictionary<LiquidTransactionId, LiquidConfirmation>));
		MethodInfo ownedOutputOutPoint = RequiredPropertyGetter(
			typeof(LiquidOwnedOutput),
			nameof(LiquidOwnedOutput.OutPoint));
		AssertExactCallMultiset(
			builder,
			RequiredMethod(
				typeof(Dictionary<LiquidTransactionId, LiquidConfirmation>),
				nameof(Dictionary<LiquidTransactionId, LiquidConfirmation>.TryGetValue),
				BindingFlags.Public | BindingFlags.Instance,
				typeof(LiquidTransactionId),
				typeof(LiquidConfirmation).MakeByRefType()),
			ownedOutputOutPoint,
			RequiredPropertyGetter(typeof(LiquidOutPoint), nameof(LiquidOutPoint.TransactionId)),
			ownedOutputOutPoint,
			RequiredPropertyGetter(typeof(LiquidOwnedOutput), nameof(LiquidOwnedOutput.Amount)),
			RequiredPropertyGetter(typeof(LiquidWalletState), nameof(LiquidWalletState.PeggedAssetId)),
			entryFactory);

		Assert.Equal([unspent], GetReferencedFields(query));
		Assert.Equal([confirmations], GetReferencedFields(builder));
		Assert.Empty(GetStoredFields(query));
		Assert.Empty(GetStoredFields(builder));
		Assert.Equal(1, CountEntryArrayAllocations(GetIlReferences(query)));
		Assert.Equal(1, GetIlInstructions(validator).Count(instruction =>
			instruction.OpCode == OpCodes.Ldlen));
		Assert.DoesNotContain(
			GetIlInstructions(query),
			instruction => instruction.OpCode is var opCode &&
				(opCode == OpCodes.Ldc_I4_2 || opCode == OpCodes.Ldc_I4_3 ||
				 opCode == OpCodes.Ldc_I4_4 || opCode == OpCodes.Ldc_I4_5 ||
				 opCode == OpCodes.Ldc_I4_6 || opCode == OpCodes.Ldc_I4_7 ||
				 opCode == OpCodes.Ldc_I4_8 || opCode == OpCodes.Ldc_I4_M1 ||
				 opCode == OpCodes.Ldc_I4 || opCode == OpCodes.Ldc_I4_S));

		HashSet<MethodBase> stateGraph = GetOwnedMethodGraph(query, typeof(LiquidWalletState));
		HashSet<MethodBase> entryGraph = GetOwnedMethodGraph(
			entryFactory,
			typeof(LiquidWalletCoinControlEntry));
		HashSet<MethodBase> snapshotGraph = GetOwnedMethodGraph(
			ownership,
			typeof(LiquidWalletCoinControlSnapshot));
		Assert.True(
			new HashSet<MethodBase>
			{
				query,
				ensureRevision,
				builder,
				peggedAssetGetter,
				revisionGetter,
			}.SetEquals(stateGraph));
		Assert.Contains(ownership, directCalls.Select(site => site.Method));
		Assert.Contains(ownershipConstructor, snapshotGraph);
		Assert.Contains(validator, snapshotGraph);
		Assert.Contains(comparator, snapshotGraph);
		Assert.True(
			new HashSet<MethodBase> { entryFactory, entryConstructor }.SetEquals(entryGraph));
		Assert.True(
			new HashSet<MethodBase> { ownership, ownershipConstructor, validator, comparator }
				.SetEquals(snapshotGraph));
		AssertExactBackingFieldGetter(
			peggedAssetGetter,
			RequiredField(typeof(LiquidWalletState), "<PeggedAssetId>k__BackingField"));
		AssertExactBackingFieldGetter(
			revisionGetter,
			RequiredField(typeof(LiquidWalletState), "<Revision>k__BackingField"));

		AssertExactCoinControlEntryFactoryGraph(entryFactory, entryConstructor);
		AssertExactSnapshotOwnershipGraph(
			ownership,
			ownershipConstructor,
			validator,
			comparator);

		MethodBase[] completeOwnedGraph = stateGraph
			.Concat(entryGraph)
			.Concat(snapshotGraph)
			.Distinct()
			.ToArray();
		var graphFlows = new Dictionary<MethodBase, (
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets)>();
		foreach (MethodBase method in completeOwnedGraph)
		{
			Assert.Empty(method.GetMethodBody()?.ExceptionHandlingClauses ?? []);
			var methodFlow = CreateValidControlFlow(method);
			graphFlows.Add(method, methodFlow);
			foreach (var instruction in methodFlow.Instructions)
			{
				Assert.Contains(instruction.Offset, methodFlow.ReachableOffsets);
			}
			Assert.DoesNotContain(GetCalledMethods(method), IsCollectionCopyOrMaterializer);
			Assert.DoesNotContain(GetCalledMethods(method), IsArrayCopyOrClone);
			Assert.DoesNotContain(
				method.GetMethodBody()?.LocalVariables ?? [],
				local => local.LocalType == typeof(object) ||
					local.LocalType.IsInterface ||
					typeof(Delegate).IsAssignableFrom(local.LocalType) ||
					local.LocalType.IsByRef ||
					local.LocalType.IsPointer ||
					IsWritableSpan(local.LocalType) ||
					ContainsForbiddenGraphType(local.LocalType));
			Assert.DoesNotContain(
				GetIlInstructions(method),
				instruction => IsForbiddenExactQueryOpcode(instruction.OpCode));
			Assert.DoesNotContain(GetCalledMethods(method), IsCapacityMutationOrCallback);
		}
		AssertExactOwnedGraphCycles(graphFlows, validator, comparator);
		AssertGraphHasNoForbiddenSurface(completeOwnedGraph);
		AssertExactOwnershipReferenceFlow(
			query,
			ownership,
			ownershipConstructor,
			validator,
			newArrayOffset,
			ownershipOffset,
			peggedAssetGetter,
			revisionGetter);
		AssertExactOwnedGraphAllocationsAndMutations(
			completeOwnedGraph,
			query,
			ensureRevision,
			entryFactory,
			entryConstructor,
			ownershipConstructor,
			validator,
			comparator);

		HashSet<FieldInfo> queryFields = GetReferencedFields(query).ToHashSet();
		foreach (FieldInfo forbiddenField in new[]
		{
			RequiredField(typeof(LiquidWalletState), "_knownOutputs"),
			RequiredField(typeof(LiquidWalletState), "_history"),
			RequiredField(typeof(LiquidWalletState), "_appliedTransactionIds"),
			RequiredField(typeof(LiquidWalletState), "_balances"),
		})
		{
			Assert.DoesNotContain(forbiddenField, queryFields);
		}
		Assert.DoesNotContain(
			typeof(LiquidWalletState).GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
			field => field.FieldType == typeof(LiquidWalletCoinControlEntry[]) ||
				field.FieldType == typeof(LiquidWalletCoinControlSnapshot));
		Assert.DoesNotContain(
			query.DeclaringType?.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public) ?? [],
			type => type.Name.Contains(nameof(LiquidWalletState.QueryUnspentCoinControlEntry),
				StringComparison.Ordinal));
		AssertExactAssemblyTypeManifestsMatchBase();

		IEnumerable<Type> exposedTypes = query.GetParameters()
			.Select(parameter => parameter.ParameterType)
			.Append(query.ReturnType)
			.Concat(typeof(LiquidWalletCoinControlSnapshot)
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
				.Select(field => field.FieldType))
			.Concat(typeof(LiquidWalletCoinControlEntry)
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Select(property => property.PropertyType));
		Assert.DoesNotContain(exposedTypes, ContainsForbiddenExecutionType);
	}

	private static LiquidWalletState CreateLargeMultiassetState(int outputCount)
	{
		LiquidTransactionId transactionId = Tx(80_000);
		LiquidSpendKeyReference key = ExternalKey;
		byte[] scriptPubKey = key.GetScriptPubKey();
		var outputs = new LiquidOwnedOutput[outputCount];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] = LiquidOwnedOutput.Create(
				LiquidOutPoint.CreateSpendable(transactionId, (uint)index),
				scriptPubKey,
				LiquidAssetAmount.Create(
					index % 2 == 0 ? PeggedAsset : IssuedAsset,
					PeggedAsset,
					index + 1),
				key);
		}

		return LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				1,
				[Delta(transactionId, [], outputs)],
				[]));
	}

	private static void AssertQuerySnapshot(
		LiquidWalletCoinControlSnapshot snapshot,
		ulong expectedRevision,
		LiquidWalletCoinControlEntry? expectedEntry)
	{
		AssertSensitive(
			snapshot.PeggedAssetId == PeggedAsset,
			"The exact query pegged context did not match.");
		Assert.Equal(expectedRevision, snapshot.Revision);
		IReadOnlyList<LiquidWalletCoinControlEntry> entries = snapshot.GetEntries();
		if (expectedEntry is null)
		{
			Assert.Empty(entries);
			return;
		}

		AssertEntryEquivalent(expectedEntry, Assert.Single(entries));
	}

	private static void AssertEntryEquivalent(
		LiquidWalletCoinControlEntry expected,
		LiquidWalletCoinControlEntry actual)
	{
		AssertSensitive(
			expected.OutPoint == actual.OutPoint,
			"The exact query outpoint did not match.");
		AssertSensitive(
			expected.Amount == actual.Amount,
			"The exact query amount did not match.");
		AssertSensitive(
			expected.PeggedAssetId == actual.PeggedAssetId,
			"The exact query pegged context did not match.");
		AssertSensitive(
			expected.Confirmation == actual.Confirmation,
			"The exact query confirmation did not match.");
	}

	private static void AssertEntryEquivalentExceptConfirmation(
		LiquidWalletCoinControlEntry expected,
		LiquidWalletCoinControlEntry actual)
	{
		AssertSensitive(
			expected.OutPoint == actual.OutPoint,
			"The exact query outpoint changed across confirmation transition.");
		AssertSensitive(
			expected.Amount == actual.Amount,
			"The exact query amount changed across confirmation transition.");
		AssertSensitive(
			expected.PeggedAssetId == actual.PeggedAssetId,
			"The exact query pegged context changed across confirmation transition.");
	}

	private static void AssertStateUnchanged(
		LiquidWalletState state,
		int path,
		LiquidOutPoint hit,
		LiquidOutPoint miss)
	{
		ulong revision = state.Revision;
		LiquidWalletReplaySnapshot replay = state.ExportReplaySnapshot();
		IReadOnlyList<LiquidAssetAmount> balances = state.GetBalances().GetAmounts();
		LiquidWalletCoinControlSnapshot inventory = state.GetCoinControlSnapshot();
		LiquidWalletTransactionEffectSnapshot effects = state.GetTransactionEffectSnapshot();
		IReadOnlyList<LiquidOwnedOutput> unspentOutputs = state.GetUnspentOutputs();
		LiquidTransactionId unknownTransactionId = Tx('f');
		LiquidTransactionId[] queriedTransactionIds = replay.GetDeltas()
			.Select(delta => delta.TransactionId)
			.Append(unknownTransactionId)
			.ToArray();
		var exactEffects = new LiquidWalletTransactionEffectSnapshot[queriedTransactionIds.Length];
		for (int index = 0; index < exactEffects.Length; index++)
		{
			exactEffects[index] = state.QueryTransactionEffect(revision, queriedTransactionIds[index]);
		}
		LiquidOutPoint[] selectedOutPoints = inventory.GetEntries()
			.Select(entry => entry.OutPoint)
			.ToArray();
		LiquidWalletCoinControlSelection selection =
			state.CreateCoinControlSelection(revision, selectedOutPoints);

		if (path == 0)
		{
			Assert.Single(state.QueryUnspentCoinControlEntry(state.Revision, hit).GetEntries());
		}
		else if (path == 1)
		{
			Assert.Empty(state.QueryUnspentCoinControlEntry(state.Revision, miss).GetEntries());
		}
		else
		{
			Exception failure;
			try
			{
				state.QueryUnspentCoinControlEntry(
					path == 2 ? state.Revision - 1 : state.Revision,
					path == 2 ? hit : null!);
				throw new Xunit.Sdk.XunitException("The invalid exact query unexpectedly succeeded.");
			}
			catch (Exception exception)
			{
				failure = exception;
			}
			if (path == 2)
			{
				Assert.IsType<InvalidOperationException>(failure);
			}
			else
			{
				Assert.Equal(3, path);
				Assert.IsType<ArgumentNullException>(failure);
			}
		}

		Assert.Equal(revision, state.Revision);
		AssertReplayEquivalent(replay, state.ExportReplaySnapshot());
		AssertSensitive(
			balances.SequenceEqual(state.GetBalances().GetAmounts()),
			"The wallet balances changed during an exact query.");
		AssertEquivalent(inventory, state.GetCoinControlSnapshot());
		AssertEffectEquivalent(effects, state.GetTransactionEffectSnapshot());
		AssertSensitive(
			unspentOutputs.SequenceEqual(state.GetUnspentOutputs()),
			"The retained unspent-output values changed during an exact query.");
		AssertSelectionEquivalent(
			selection,
			state.CreateCoinControlSelection(state.Revision, selectedOutPoints));
		for (int index = 0; index < queriedTransactionIds.Length; index++)
		{
			AssertEffectEquivalent(
				exactEffects[index],
				state.QueryTransactionEffect(state.Revision, queriedTransactionIds[index]));
		}
	}

	private static void AssertReplayEquivalent(
		LiquidWalletReplaySnapshot expected,
		LiquidWalletReplaySnapshot actual)
	{
		AssertSensitive(
			expected.PeggedAssetId == actual.PeggedAssetId,
			"The replay pegged context changed during an exact query.");
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletTransactionDelta> expectedDeltas = expected.GetDeltas();
		IReadOnlyList<LiquidWalletTransactionDelta> actualDeltas = actual.GetDeltas();
		Assert.Equal(expectedDeltas.Count, actualDeltas.Count);
		for (int index = 0; index < expectedDeltas.Count; index++)
		{
			AssertSensitive(
				expectedDeltas[index].TransactionId == actualDeltas[index].TransactionId,
				"A replay transaction identifier changed during an exact query.");
			AssertSensitive(
				expectedDeltas[index].GetSpentOutPoints()
					.SequenceEqual(actualDeltas[index].GetSpentOutPoints()),
				"Replay spent outpoints changed during an exact query.");
			AssertSensitive(
				expectedDeltas[index].GetCreatedOutputs()
					.SequenceEqual(actualDeltas[index].GetCreatedOutputs()),
				"Replay created outputs changed during an exact query.");
		}
		AssertSensitive(
			expected.GetConfirmations().SequenceEqual(actual.GetConfirmations()),
			"Replay confirmations changed during an exact query.");
	}

	private static void AssertEffectEquivalent(
		LiquidWalletTransactionEffectSnapshot expected,
		LiquidWalletTransactionEffectSnapshot actual)
	{
		AssertSensitive(
			expected.PeggedAssetId == actual.PeggedAssetId,
			"The transaction-effect pegged context changed during an exact query.");
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletTransactionEffect> expectedEffects = expected.GetEffects();
		IReadOnlyList<LiquidWalletTransactionEffect> actualEffects = actual.GetEffects();
		Assert.Equal(expectedEffects.Count, actualEffects.Count);
		for (int index = 0; index < expectedEffects.Count; index++)
		{
			AssertSensitive(
				expectedEffects[index].TransactionId == actualEffects[index].TransactionId,
				"A transaction-effect identifier changed during an exact query.");
			AssertSensitive(
				expectedEffects[index].PeggedAssetId == actualEffects[index].PeggedAssetId,
				"A transaction-effect pegged context changed during an exact query.");
			AssertSensitive(
				expectedEffects[index].Confirmation == actualEffects[index].Confirmation,
				"A transaction-effect confirmation changed during an exact query.");
			AssertSensitive(
				expectedEffects[index].GetAssetNetChanges()
					.SequenceEqual(actualEffects[index].GetAssetNetChanges()),
				"Transaction-effect asset changes changed during an exact query.");
		}
	}

	private static void AssertSelectionEquivalent(
		LiquidWalletCoinControlSelection expected,
		LiquidWalletCoinControlSelection actual)
	{
		AssertSensitive(
			expected.PeggedAssetId == actual.PeggedAssetId,
			"The selection pegged context changed during an exact query.");
		Assert.Equal(expected.SourceRevision, actual.SourceRevision);
		AssertSensitive(
			expected.GetSelectedBalances().GetAmounts()
				.SequenceEqual(actual.GetSelectedBalances().GetAmounts()),
			"Selection balances changed during an exact query.");
		IReadOnlyList<LiquidWalletCoinControlEntry> expectedEntries = expected.GetEntries();
		IReadOnlyList<LiquidWalletCoinControlEntry> actualEntries = actual.GetEntries();
		Assert.Equal(expectedEntries.Count, actualEntries.Count);
		for (int index = 0; index < expectedEntries.Count; index++)
		{
			AssertEntryEquivalent(expectedEntries[index], actualEntries[index]);
		}
	}

	private static void AssertSensitive(bool condition, string redactedFailureMessage) =>
		Assert.True(condition, redactedFailureMessage);

	private static void AssertRedactedText(string text, string sensitiveCanary) =>
		AssertSensitive(
			!text.Contains(sensitiveCanary, StringComparison.OrdinalIgnoreCase),
			"Sensitive data appeared in an exception message.");

	private static MethodInfo RequiredMethod(
		Type type,
		string name,
		BindingFlags bindingFlags,
		params Type[] parameterTypes) =>
		type.GetMethod(name, bindingFlags, parameterTypes) ??
		throw new InvalidOperationException($"The required {type.Name} method is unavailable.");

	private static ConstructorInfo RequiredConstructor(Type type, params Type[] parameterTypes) =>
		type.GetConstructor(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
			parameterTypes) ??
		throw new InvalidOperationException($"The required {type.Name} constructor is unavailable.");

	private static MethodInfo RequiredPropertyGetter(Type type, string name) =>
		type.GetProperty(
			name,
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static)?.GetMethod ??
		throw new InvalidOperationException($"The required {type.Name} property getter is unavailable.");

	private static FieldInfo RequiredField(Type type, string name) =>
		type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ??
		throw new InvalidOperationException($"The required {type.Name} field is unavailable.");

	private static void AssertExactCallMultiset(MethodBase owner, params MethodBase[] expected)
	{
		var remaining = GetCalledMethods(owner).ToList();
		foreach (MethodBase expectedCall in expected)
		{
			int matchIndex = -1;
			for (int index = 0; index < remaining.Count; index++)
			{
				if (remaining[index].Equals(expectedCall))
				{
					matchIndex = index;
					break;
				}
			}
			Assert.True(
				matchIndex >= 0,
				$"The closed IL graph is missing the expected call {expectedCall.DeclaringType?.Name}.{expectedCall.Name}.");
			remaining.RemoveAt(matchIndex);
		}
		Assert.True(
			remaining.Count == 0,
			remaining.Count == 0
				? "The closed IL call multiset matched."
				: $"Unexpected closed IL call {remaining[0].DeclaringType?.Name}.{remaining[0].Name}.");
	}

	private static void AssertExactStoredFieldMultiset(
		MethodBase owner,
		params FieldInfo[] expected)
	{
		var remaining = GetStoredFields(owner).ToList();
		foreach (FieldInfo expectedField in expected)
		{
			int matchIndex = -1;
			for (int index = 0; index < remaining.Count; index++)
			{
				if (remaining[index].Equals(expectedField))
				{
					matchIndex = index;
					break;
				}
			}
			Assert.True(
				matchIndex >= 0,
				$"The closed IL graph is missing the expected field store {expectedField.DeclaringType?.Name}.{expectedField.Name}.");
			remaining.RemoveAt(matchIndex);
		}
		Assert.True(
			remaining.Count == 0,
			remaining.Count == 0
				? "The closed IL field-store multiset matched."
				: $"Unexpected closed IL field store {remaining[0].DeclaringType?.Name}.{remaining[0].Name}.");
	}

	private static void AssertExactBackingFieldGetter(MethodInfo getter, FieldInfo backingField)
	{
		Assert.Empty(GetCalledMethods(getter));
		Assert.Empty(GetStoredFields(getter));
		Assert.Equal([backingField], GetReferencedFields(getter));
		var instructions = GetIlInstructions(getter)
			.Where(instruction => instruction.OpCode != OpCodes.Nop)
			.ToArray();
		Assert.Equal(3, instructions.Length);
		Assert.Equal(0, GetLoadedArgumentIndex(getter, instructions[0]));
		Assert.Equal(OpCodes.Ldfld, instructions[1].OpCode);
		Assert.Equal(backingField, ResolveInstructionMember(getter, instructions[1]));
		Assert.Equal(OpCodes.Ret, instructions[2].OpCode);
	}

	private static void AssertExactCoinControlEntryFactoryGraph(
		MethodInfo factory,
		ConstructorInfo constructor)
	{
		MethodInfo throwIfNull = RequiredMethod(
			typeof(ArgumentNullException),
			nameof(ArgumentNullException.ThrowIfNull),
			BindingFlags.Public | BindingFlags.Static,
			typeof(object),
			typeof(string));
		AssertExactCallMultiset(
			factory,
			throwIfNull,
			throwIfNull,
			throwIfNull,
			RequiredPropertyGetter(typeof(LiquidAssetAmount), nameof(LiquidAssetAmount.IsZero)),
			RequiredConstructor(
				typeof(ArgumentOutOfRangeException),
				typeof(string),
				typeof(string)),
			RequiredPropertyGetter(
				typeof(LiquidAssetAmount),
				nameof(LiquidAssetAmount.PeggedAssetId)),
			RequiredMethod(
				typeof(LiquidAssetId),
				"op_Inequality",
				BindingFlags.Public | BindingFlags.Static,
				typeof(LiquidAssetId),
				typeof(LiquidAssetId)),
			RequiredConstructor(typeof(ArgumentException), typeof(string), typeof(string)),
			constructor);
		Assert.Empty(GetStoredFields(factory));
		AssertExactStoredFieldMultiset(
			constructor,
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<OutPoint>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<Amount>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<PeggedAssetId>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<Confirmation>k__BackingField"));
		AssertExactCallMultiset(constructor, RequiredConstructor(typeof(object)));
	}

	private static void AssertExactSnapshotOwnershipGraph(
		MethodInfo ownership,
		ConstructorInfo ownershipConstructor,
		MethodInfo validator,
		MethodInfo comparator)
	{
		MethodInfo throwIfNull = RequiredMethod(
			typeof(ArgumentNullException),
			nameof(ArgumentNullException.ThrowIfNull),
			BindingFlags.Public | BindingFlags.Static,
			typeof(object),
			typeof(string));
		ConstructorInfo argumentException = RequiredConstructor(
			typeof(ArgumentException),
			typeof(string),
			typeof(string));
		MethodInfo assetInequality = RequiredMethod(
			typeof(LiquidAssetId),
			"op_Inequality",
			BindingFlags.Public | BindingFlags.Static,
			typeof(LiquidAssetId),
			typeof(LiquidAssetId));

		AssertExactCallMultiset(ownership, throwIfNull, ownershipConstructor);
		Assert.Empty(GetStoredFields(ownership));
		AssertExactCallMultiset(
			ownershipConstructor,
			RequiredConstructor(typeof(object)),
			throwIfNull,
			validator);
		AssertExactStoredFieldMultiset(
			ownershipConstructor,
			RequiredField(typeof(LiquidWalletCoinControlSnapshot), "<PeggedAssetId>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlSnapshot), "<Revision>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlSnapshot), "_entries"));

		AssertExactCallMultiset(
			validator,
			throwIfNull,
			argumentException,
			RequiredPropertyGetter(
				typeof(LiquidWalletCoinControlEntry),
				nameof(LiquidWalletCoinControlEntry.PeggedAssetId)),
			assetInequality,
			RequiredPropertyGetter(
				typeof(LiquidWalletCoinControlEntry),
				nameof(LiquidWalletCoinControlEntry.Amount)),
			RequiredPropertyGetter(
				typeof(LiquidAssetAmount),
				nameof(LiquidAssetAmount.PeggedAssetId)),
			assetInequality,
			argumentException,
			comparator,
			argumentException);
		Assert.Empty(GetStoredFields(validator));

		MethodInfo outPointGetter = RequiredPropertyGetter(
			typeof(LiquidWalletCoinControlEntry),
			nameof(LiquidWalletCoinControlEntry.OutPoint));
		MethodInfo transactionIdGetter = RequiredPropertyGetter(
			typeof(LiquidOutPoint),
			nameof(LiquidOutPoint.TransactionId));
		MethodInfo canonicalIdGetter = RequiredPropertyGetter(
			typeof(LiquidTransactionId),
			nameof(LiquidTransactionId.CanonicalRpcHex));
		MethodInfo outputIndexGetter = RequiredPropertyGetter(
			typeof(LiquidOutPoint),
			nameof(LiquidOutPoint.OutputIndex));
		AssertExactCallMultiset(
			comparator,
			RequiredPropertyGetter(typeof(StringComparer), nameof(StringComparer.Ordinal)),
			outPointGetter,
			transactionIdGetter,
			canonicalIdGetter,
			outPointGetter,
			transactionIdGetter,
			canonicalIdGetter,
			RequiredMethod(
				typeof(StringComparer),
				nameof(StringComparer.Compare),
				BindingFlags.Public | BindingFlags.Instance,
				typeof(string),
				typeof(string)),
			outPointGetter,
			outputIndexGetter,
			outPointGetter,
			outputIndexGetter,
			RequiredMethod(
				typeof(uint),
				nameof(uint.CompareTo),
				BindingFlags.Public | BindingFlags.Instance,
				typeof(uint)));
		Assert.Empty(GetStoredFields(comparator));
		AssertExactComparatorResultProvenance(comparator);
	}

	private static void AssertExactComparatorResultProvenance(MethodInfo comparator)
	{
		MethodInfo ordinalGetter = RequiredPropertyGetter(
			typeof(StringComparer),
			nameof(StringComparer.Ordinal));
		MethodInfo outPointGetter = RequiredPropertyGetter(
			typeof(LiquidWalletCoinControlEntry),
			nameof(LiquidWalletCoinControlEntry.OutPoint));
		MethodInfo transactionIdGetter = RequiredPropertyGetter(
			typeof(LiquidOutPoint),
			nameof(LiquidOutPoint.TransactionId));
		MethodInfo canonicalIdGetter = RequiredPropertyGetter(
			typeof(LiquidTransactionId),
			nameof(LiquidTransactionId.CanonicalRpcHex));
		MethodInfo ordinalCompare = RequiredMethod(
			typeof(StringComparer),
			nameof(StringComparer.Compare),
			BindingFlags.Public | BindingFlags.Instance,
			typeof(string),
			typeof(string));
		MethodInfo outputIndexGetter = RequiredPropertyGetter(
			typeof(LiquidOutPoint),
			nameof(LiquidOutPoint.OutputIndex));
		MethodInfo outputIndexCompare = RequiredMethod(
			typeof(uint),
			nameof(uint.CompareTo),
			BindingFlags.Public | BindingFlags.Instance,
			typeof(uint));
		var flow = CreateValidControlFlow(comparator);
		var instructions = flow.Instructions
			.Where(instruction => instruction.OpCode != OpCodes.Nop)
			.ToArray();
		Assert.True(instructions.Length is 25 or 28);
		Assert.Equal(ordinalGetter, ResolveInstructionMember(comparator, instructions[0]));
		Assert.Equal(0, GetLoadedArgumentIndex(comparator, instructions[1]));
		Assert.Equal(outPointGetter, ResolveInstructionMember(comparator, instructions[2]));
		Assert.Equal(transactionIdGetter, ResolveInstructionMember(comparator, instructions[3]));
		Assert.Equal(canonicalIdGetter, ResolveInstructionMember(comparator, instructions[4]));
		Assert.Equal(1, GetLoadedArgumentIndex(comparator, instructions[5]));
		Assert.Equal(outPointGetter, ResolveInstructionMember(comparator, instructions[6]));
		Assert.Equal(transactionIdGetter, ResolveInstructionMember(comparator, instructions[7]));
		Assert.Equal(canonicalIdGetter, ResolveInstructionMember(comparator, instructions[8]));
		Assert.Equal(ordinalCompare, ResolveInstructionMember(comparator, instructions[9]));
		int transactionOrderLocal = Assert.IsType<int>(
			GetStoredLocalIndex(comparator, instructions[10]));
		Assert.Equal(
			typeof(int),
			comparator.GetMethodBody()?.LocalVariables[transactionOrderLocal].LocalType);
		Assert.Equal(transactionOrderLocal, GetLoadedLocalIndex(comparator, instructions[11]));
		var transactionGuard = instructions[12];
		Assert.True(IsBooleanConditionalBranch(transactionGuard.OpCode));
		Assert.True(BranchesOnTrue(transactionGuard.OpCode));
		AssertExactLinearNopPath(flow, instructions[9].Offset, instructions[10].Offset);
		AssertExactLinearNopPath(flow, instructions[10].Offset, instructions[11].Offset);
		AssertExactLinearNopPath(flow, instructions[11].Offset, transactionGuard.Offset);

		Assert.Equal(0, GetLoadedArgumentIndex(comparator, instructions[13]));
		Assert.Equal(outPointGetter, ResolveInstructionMember(comparator, instructions[14]));
		Assert.Equal(outputIndexGetter, ResolveInstructionMember(comparator, instructions[15]));
		int leftOutputIndexLocal = Assert.IsType<int>(
			GetStoredLocalIndex(comparator, instructions[16]));
		Assert.Equal(
			typeof(uint),
			comparator.GetMethodBody()?.LocalVariables[leftOutputIndexLocal].LocalType);
		Assert.Equal(leftOutputIndexLocal, GetAddressedLocalIndex(comparator, instructions[17]));
		Assert.Equal(1, GetLoadedArgumentIndex(comparator, instructions[18]));
		Assert.Equal(outPointGetter, ResolveInstructionMember(comparator, instructions[19]));
		Assert.Equal(outputIndexGetter, ResolveInstructionMember(comparator, instructions[20]));
		Assert.Equal(outputIndexCompare, ResolveInstructionMember(comparator, instructions[21]));
		Assert.Equal(1, CountLocalAccess(comparator, instructions, leftOutputIndexLocal, 1));
		Assert.Equal(0, CountLocalAccess(comparator, instructions, leftOutputIndexLocal, 0));
		Assert.Equal(1, CountLocalAccess(comparator, instructions, leftOutputIndexLocal, 2));
		int transactionGuardPosition = FindInstructionPosition(
			flow.Instructions,
			transactionGuard.Offset);
		Assert.Equal(
			instructions[13].Offset,
			flow.Instructions[transactionGuardPosition + 1].Offset);
		for (int position = 13; position < 21; position++)
		{
			AssertExactLinearNopPath(
				flow,
				instructions[position].Offset,
				instructions[position + 1].Offset);
		}

		byte[] comparatorIl = comparator.GetMethodBody()?.GetILAsByteArray() ?? [];
		int transactionResultOffset = Assert.Single(
			GetBranchTargets(transactionGuard, comparatorIl));
		Assert.Equal(instructions[23].Offset, transactionResultOffset);
		Assert.Equal(transactionOrderLocal, GetLoadedLocalIndex(comparator, instructions[23]));
		Assert.Equal(1, CountLocalAccess(comparator, instructions, transactionOrderLocal, 1));
		Assert.Equal(2, CountLocalAccess(comparator, instructions, transactionOrderLocal, 0));
		Assert.Equal(0, CountLocalAccess(comparator, instructions, transactionOrderLocal, 2));
		Assert.True(flow.Successors[transactionGuard.Offset].SetEquals(
			new[] { instructions[13].Offset, transactionResultOffset }));
		if (instructions.Length == 25)
		{
			Assert.Equal(OpCodes.Ret, instructions[22].OpCode);
			Assert.Equal(OpCodes.Ret, instructions[24].OpCode);
			AssertExactLinearNopPath(flow, instructions[21].Offset, instructions[22].Offset);
			AssertExactLinearNopPath(flow, instructions[23].Offset, instructions[24].Offset);
		}
		else
		{
			Assert.Equal(FlowControl.Branch, instructions[22].OpCode.FlowControl);
			Assert.Equal(FlowControl.Branch, instructions[25].OpCode.FlowControl);
			int resultStoreOffset = Assert.Single(
				GetBranchTargets(instructions[22], comparatorIl));
			Assert.Equal(instructions[24].Offset, resultStoreOffset);
			int resultLocal = Assert.IsType<int>(
				GetStoredLocalIndex(comparator, instructions[24]));
			Assert.Equal(
				typeof(int),
				comparator.GetMethodBody()?.LocalVariables[resultLocal].LocalType);
			Assert.Equal(resultLocal, GetLoadedLocalIndex(comparator, instructions[26]));
			Assert.Equal(OpCodes.Ret, instructions[27].OpCode);
			Assert.Equal(
				instructions[26].Offset,
				Assert.Single(GetBranchTargets(instructions[25], comparatorIl)));
			Assert.Equal(instructions[24].Offset, Assert.Single(flow.Successors[instructions[23].Offset]));
			Assert.Equal(1, CountLocalAccess(comparator, instructions, resultLocal, 1));
			Assert.Equal(1, CountLocalAccess(comparator, instructions, resultLocal, 0));
			Assert.Equal(0, CountLocalAccess(comparator, instructions, resultLocal, 2));
			AssertExactLinearNopPath(flow, instructions[21].Offset, instructions[22].Offset);
			AssertExactLinearNopPath(flow, instructions[24].Offset, instructions[25].Offset);
			AssertExactLinearNopPath(flow, instructions[26].Offset, instructions[27].Offset);
		}
	}

	private static HashSet<string> GetSelectedCoinControlStateManifest()
	{
		Type[] boundaryTypes =
		new Type[]
		{
			typeof(LiquidOutPoint),
			typeof(LiquidWalletCoinControlEntry),
			typeof(LiquidWalletCoinControlSnapshot),
			typeof(LiquidWalletCoinControlSelection),
		};
		var manifest = new HashSet<string>(StringComparer.Ordinal);
		foreach (MethodInfo method in typeof(LiquidWalletState).GetMethods(
			BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
				BindingFlags.DeclaredOnly))
		{
			bool selected = ContainsSelectedManifestType(
				method.ReturnType,
				boundaryTypes,
				new HashSet<Type>());
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				selected |= ContainsSelectedManifestType(
					parameter.ParameterType,
					boundaryTypes,
					new HashSet<Type>());
			}
			if (selected)
			{
				manifest.Add(CanonicalizeManifestMethod(method));
			}
		}
		return manifest;
	}

	private static bool ContainsSelectedManifestType(
		Type type,
		IReadOnlyCollection<Type> boundaryTypes,
		HashSet<Type> visited)
	{
		if (boundaryTypes.Contains(type))
		{
			return true;
		}
		if (!visited.Add(type))
		{
			return false;
		}
		if (type.HasElementType)
		{
			return ContainsSelectedManifestType(type.GetElementType()!, boundaryTypes, visited);
		}
		if (!type.IsConstructedGenericType)
		{
			return false;
		}
		foreach (Type argument in type.GetGenericArguments())
		{
			if (ContainsSelectedManifestType(argument, boundaryTypes, visited))
			{
				return true;
			}
		}
		return false;
	}

	private static string CanonicalizeManifestMethod(MethodInfo method)
	{
		string parameters = string.Join(
			", ",
			method.GetParameters().Select(parameter =>
			{
				string kind = parameter.IsOut
					? "out"
					: parameter.ParameterType.IsByRef
						? parameter.IsIn ? "in" : "ref"
						: "value";
				Type parameterType = parameter.ParameterType.IsByRef
					? parameter.ParameterType.GetElementType()!
					: parameter.ParameterType;
				return $"{kind} {CanonicalTypeName(parameterType)}";
			}));
		string visibility = method.IsPublic ? "public" :
			method.IsFamily ? "protected" :
			method.IsAssembly ? "internal" : "private";
		string dispatch = method.IsStatic ? "static" : "instance";
		return $"{visibility} {dispatch} {method.Name}/{method.GetGenericArguments().Length} " +
			$"({parameters}) -> {CanonicalTypeName(method.ReturnType)}";
	}

	private static string CanonicalTypeName(Type type)
	{
		if (type.IsArray)
		{
			return $"{CanonicalTypeName(type.GetElementType()!)}[]";
		}
		if (type.IsPointer)
		{
			return $"{CanonicalTypeName(type.GetElementType()!)}*";
		}
		if (!type.IsGenericType)
		{
			return type == typeof(bool) ? "bool" :
				type == typeof(ulong) ? "ulong" : type.Name;
		}

		string name = type.Name[..type.Name.IndexOf('`')];
		return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(CanonicalTypeName))}>";
	}

	private static HashSet<MethodBase> GetOwnedMethodGraph(MethodBase root, Type owningType)
	{
		var discovered = new HashSet<MethodBase> { root };
		var pending = new Queue<MethodBase>();
		pending.Enqueue(root);
		while (pending.TryDequeue(out MethodBase? current))
		{
			foreach (MethodBase called in GetCalledMethods(current))
			{
				if ((called.DeclaringType == owningType || called.DeclaringType?.DeclaringType == owningType) &&
					discovered.Add(called))
				{
					pending.Enqueue(called);
				}
			}
		}
		return discovered;
	}

	private static IEnumerable<MethodBase> GetCalledMethods(MethodBase method) =>
		GetIlReferences(method)
			.Where(reference => reference.OpCode.OperandType == OperandType.InlineMethod)
			.Select(reference => reference.Member)
			.OfType<MethodBase>();

	private static IReadOnlyList<(int Offset, MethodBase Method)> GetCalledMethodInstructions(
		MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			return [];
		}

		var calls = new List<(int Offset, MethodBase Method)>();
		foreach ((int offset, OpCode opCode, int operandOffset, _) in GetIlInstructions(method))
		{
			if (opCode.OperandType != OperandType.InlineMethod)
			{
				continue;
			}
			MemberInfo? member = method.Module.ResolveMember(
				BitConverter.ToInt32(il, operandOffset),
				method.DeclaringType?.GetGenericArguments(),
				method.IsGenericMethod ? method.GetGenericArguments() : null);
			if (member is MethodBase called)
			{
				calls.Add((offset, called));
			}
		}
		return calls;
	}

	private static IReadOnlyList<(int Offset, int Target, OpCode OpCode)> GetBranchEdges(
		MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			return [];
		}

		var branches = new List<(int Offset, int Target, OpCode OpCode)>();
		foreach ((int offset, OpCode opCode, int operandOffset, int operandSize) in
			GetIlInstructions(method))
		{
			if (opCode.OperandType == OperandType.ShortInlineBrTarget)
			{
				branches.Add((
					offset,
					operandOffset + operandSize + unchecked((sbyte)il[operandOffset]),
					opCode));
			}
			else if (opCode.OperandType == OperandType.InlineBrTarget)
			{
				branches.Add((
					offset,
					operandOffset + operandSize + BitConverter.ToInt32(il, operandOffset),
					opCode));
			}
		}
		return branches;
	}

	private static IEnumerable<FieldInfo> GetReferencedFields(MethodBase method) =>
		GetIlReferences(method)
			.Where(reference => reference.OpCode.OperandType == OperandType.InlineField)
			.Select(reference => reference.Member)
			.OfType<FieldInfo>();

	private static IEnumerable<FieldInfo> GetStoredFields(MethodBase method) =>
		GetIlReferences(method)
			.Where(reference => reference.OpCode == OpCodes.Stfld || reference.OpCode == OpCodes.Stsfld)
			.Select(reference => reference.Member)
			.OfType<FieldInfo>();

	private static IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>
		GetIlInstructions(MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			return [];
		}

		Dictionary<short, OpCode> opCodes = typeof(OpCodes)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.FieldType == typeof(OpCode))
			.Select(field => (OpCode)field.GetValue(null)!)
			.ToDictionary(opCode => opCode.Value);
		var instructions = new List<(
			int Offset,
			OpCode OpCode,
			int OperandOffset,
			int OperandSize)>();
		int position = 0;
		while (position < il.Length)
		{
			int offset = position;
			short value = il[position++] == 0xfe
				? unchecked((short)(0xfe00 | il[position++]))
				: il[position - 1];
			OpCode opCode = opCodes[value];
			int operandOffset = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
			instructions.Add((offset, opCode, operandOffset, operandSize));
			position += operandSize;
		}
		return instructions;
	}

	private static bool IsPermittedExactUnspentQueryCall(
		MethodBase method,
		MethodInfo ensureRevision,
		MethodInfo builder,
		MethodInfo ownership)
	{
		if (method == ensureRevision || method == builder || method == ownership)
		{
			return true;
		}
		if (method.DeclaringType == typeof(ArgumentNullException))
		{
			return method.Name == nameof(ArgumentNullException.ThrowIfNull) &&
				HasParameterTypes(method, typeof(object), typeof(string));
		}
		if (method.DeclaringType == typeof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>))
		{
			return method.Name == nameof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>.TryGetValue) &&
				HasParameterTypes(
					method,
					typeof(LiquidOutPoint),
					typeof(LiquidOwnedOutput).MakeByRefType());
		}
		return method.DeclaringType == typeof(LiquidWalletState) &&
			method.Name is "get_PeggedAssetId" or "get_Revision" &&
			method.GetParameters().Length == 0;
	}

	private static bool HasParameterTypes(MethodBase method, params Type[] expected) =>
		method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(expected);

	private static int CountCallSites(
		IReadOnlyList<(int Offset, MethodBase Method)> calls,
		MethodBase expected)
	{
		int count = 0;
		foreach (var call in calls)
		{
			if (call.Method == expected)
			{
				count++;
			}
		}
		return count;
	}

	private static int GetSingleCallOffset(
		IReadOnlyList<(int Offset, MethodBase Method)> calls,
		MethodBase expected)
	{
		int? offset = null;
		foreach (var call in calls)
		{
			if (call.Method == expected)
			{
				Assert.Null(offset);
				offset = call.Offset;
			}
		}
		return Assert.IsType<int>(offset);
	}

	private static int GetSingleInstructionOffset(
		MethodBase owner,
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions,
		OpCode expectedOpCode,
		MemberInfo expectedMember)
	{
		int? offset = null;
		foreach (var instruction in instructions)
		{
			if (instruction.OpCode == expectedOpCode &&
				ResolveInstructionMember(owner, instruction) == expectedMember)
			{
				Assert.Null(offset);
				offset = instruction.Offset;
			}
		}
		return Assert.IsType<int>(offset);
	}

	private static (int Offset, OpCode OpCode, int OperandOffset, int OperandSize)
		GetSingleInstruction(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions,
			OpCode expectedOpCode)
	{
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)? match = null;
		foreach (var instruction in instructions)
		{
			if (instruction.OpCode == expectedOpCode)
			{
				Assert.Null(match);
				match = instruction;
			}
		}
		return Assert.IsType<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>(match);
	}

	private static (int Offset, OpCode OpCode, int OperandOffset, int OperandSize)
		GetSingleConditionalBranch(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions)
	{
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)? match = null;
		foreach (var instruction in instructions)
		{
			if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
			{
				Assert.Null(match);
				match = instruction;
			}
		}
		return Assert.IsType<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>(match);
	}

	private static int FindInstructionPosition(
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions,
		int expectedOffset)
	{
		for (int index = 0; index < instructions.Count; index++)
		{
			if (instructions[index].Offset == expectedOffset)
			{
				return index;
			}
		}
		return -1;
	}

	private static int CountLocalAccess(
		MethodBase method,
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions,
		int localIndex,
		int access)
	{
		int count = 0;
		foreach (var instruction in instructions)
		{
			int? actual = access == 0
				? GetLoadedLocalIndex(method, instruction)
				: access == 1
					? GetStoredLocalIndex(method, instruction)
					: GetAddressedLocalIndex(method, instruction);
			if (actual == localIndex)
			{
				count++;
			}
		}
		return count;
	}

	private static (int Offset, OpCode OpCode, int OperandOffset, int OperandSize)
		GetSingleLocalAccess(
			MethodBase method,
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions,
			int localIndex,
			int access)
	{
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)? match = null;
		foreach (var instruction in instructions)
		{
			int? actual = access == 0
				? GetLoadedLocalIndex(method, instruction)
				: access == 1
					? GetStoredLocalIndex(method, instruction)
					: GetAddressedLocalIndex(method, instruction);
			if (actual == localIndex)
			{
				Assert.Null(match);
				match = instruction;
			}
		}
		return Assert.IsType<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>(match);
	}

	private static int CountOpCode(
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions,
		OpCode expected)
	{
		int count = 0;
		foreach (var instruction in instructions)
		{
			if (instruction.OpCode == expected)
			{
				count++;
			}
		}
		return count;
	}

	private static LocalVariableInfo GetSingleLocalByType(
		MethodBase method,
		Type expectedType)
	{
		LocalVariableInfo? match = null;
		foreach (LocalVariableInfo local in method.GetMethodBody()?.LocalVariables ?? [])
		{
			if (local.LocalType == expectedType)
			{
				Assert.Null(match);
				match = local;
			}
		}
		return Assert.IsAssignableFrom<LocalVariableInfo>(match);
	}

	private static (
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
		IReadOnlyDictionary<int, HashSet<int>> Successors,
		IReadOnlySet<int> ReachableOffsets) CreateValidControlFlow(MethodBase method)
	{
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ??
			throw new InvalidOperationException("The required IL method body is unavailable.");
		var instructions = GetIlInstructions(method).ToArray();
		Assert.NotEmpty(instructions);
		var instructionOffsets = instructions.Select(instruction => instruction.Offset).ToHashSet();
		var successors = instructions.ToDictionary(
			instruction => instruction.Offset,
			_ => new HashSet<int>());
		for (int index = 0; index < instructions.Length; index++)
		{
			var instruction = instructions[index];
			int? fallthrough = index + 1 < instructions.Length
				? instructions[index + 1].Offset
				: null;
			IReadOnlyList<int> branchTargets = GetBranchTargets(instruction, il);
			foreach (int target in branchTargets)
			{
				Assert.Contains(target, instructionOffsets);
			}

			if (instruction.OpCode.OperandType == OperandType.InlineSwitch)
			{
				Assert.NotNull(fallthrough);
				successors[instruction.Offset].UnionWith(branchTargets);
				successors[instruction.Offset].Add(fallthrough.Value);
			}
			else if (instruction.OpCode.FlowControl == FlowControl.Branch)
			{
				Assert.Single(branchTargets);
				successors[instruction.Offset].Add(branchTargets[0]);
			}
			else if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
			{
				Assert.Single(branchTargets);
				Assert.NotNull(fallthrough);
				successors[instruction.Offset].Add(branchTargets[0]);
				successors[instruction.Offset].Add(fallthrough.Value);
			}
			else if (instruction.OpCode.FlowControl is not FlowControl.Return and not FlowControl.Throw)
			{
				Assert.NotNull(fallthrough);
				successors[instruction.Offset].Add(fallthrough.Value);
			}
			else
			{
				Assert.Empty(branchTargets);
			}
		}

		var reachable = new HashSet<int>();
		var pending = new Queue<int>();
		pending.Enqueue(instructions[0].Offset);
		while (pending.TryDequeue(out int current))
		{
			if (!reachable.Add(current))
			{
				continue;
			}
			foreach (int successor in successors[current])
			{
				pending.Enqueue(successor);
			}
		}

		Assert.Equal(instructions.Length, reachable.Count);
		return (instructions, successors, reachable);
	}

	private static IReadOnlyList<int> GetBranchTargets(
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction,
		byte[] il)
	{
		if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
		{
			return
			new int[]
			{
				instruction.OperandOffset + instruction.OperandSize +
					unchecked((sbyte)il[instruction.OperandOffset]),
			};
		}
		if (instruction.OpCode.OperandType == OperandType.InlineBrTarget)
		{
			return
			new int[]
			{
				instruction.OperandOffset + instruction.OperandSize +
					BitConverter.ToInt32(il, instruction.OperandOffset),
			};
		}
		if (instruction.OpCode.OperandType != OperandType.InlineSwitch)
		{
			return [];
		}

		int count = BitConverter.ToInt32(il, instruction.OperandOffset);
		int targetBase = instruction.OperandOffset + instruction.OperandSize;
		var targets = new int[count];
		for (int index = 0; index < targets.Length; index++)
		{
			targets[index] = targetBase + BitConverter.ToInt32(
				il,
				instruction.OperandOffset + sizeof(int) + (index * sizeof(int)));
		}
		return targets;
	}

	private static void AssertDominates(
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow,
		int dominator,
		int dominated)
	{
		Assert.Contains(dominator, flow.ReachableOffsets);
		Assert.Contains(dominated, flow.ReachableOffsets);
		Dictionary<int, HashSet<int>> predecessors = flow.Successors.Keys.ToDictionary(
			offset => offset,
			_ => new HashSet<int>());
		foreach ((int source, HashSet<int> targets) in flow.Successors)
		{
			foreach (int target in targets)
			{
				predecessors[target].Add(source);
			}
		}

		int entry = flow.Instructions[0].Offset;
		var dominators = new Dictionary<int, HashSet<int>>();
		foreach (int offset in flow.ReachableOffsets)
		{
			dominators.Add(
				offset,
				offset == entry
					? new HashSet<int> { entry }
					: new HashSet<int>(flow.ReachableOffsets));
		}
		bool changed;
		do
		{
			changed = false;
			foreach (int offset in flow.ReachableOffsets)
			{
				if (offset == entry)
				{
					continue;
				}
				var incoming = new List<HashSet<int>>();
				foreach (int predecessor in predecessors[offset])
				{
					if (flow.ReachableOffsets.Contains(predecessor))
					{
						incoming.Add(dominators[predecessor]);
					}
				}
				Assert.NotEmpty(incoming);
				var updated = new HashSet<int>(incoming[0]);
				foreach (HashSet<int> incomingSet in incoming.Skip(1))
				{
					updated.IntersectWith(incomingSet);
				}
				updated.Add(offset);
				if (!updated.SetEquals(dominators[offset]))
				{
					dominators[offset] = updated;
					changed = true;
				}
			}
		}
		while (changed);

		Assert.Contains(dominator, dominators[dominated]);
	}

	private static bool CanReach(
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow,
		int source,
		int target)
	{
		var visited = new HashSet<int>();
		var pending = new Queue<int>();
		pending.Enqueue(source);
		while (pending.TryDequeue(out int current))
		{
			if (!visited.Add(current))
			{
				continue;
			}
			if (current == target)
			{
				return true;
			}
			foreach (int successor in flow.Successors[current])
			{
				pending.Enqueue(successor);
			}
		}
		return false;
	}

	private static bool CanReachAvoiding(
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow,
		int source,
		int target,
		IReadOnlySet<int> excluded)
	{
		if (excluded.Contains(source) || excluded.Contains(target))
		{
			return false;
		}

		var visited = new HashSet<int>();
		var pending = new Queue<int>();
		pending.Enqueue(source);
		while (pending.TryDequeue(out int current))
		{
			if (!visited.Add(current))
			{
				continue;
			}
			if (current == target)
			{
				return true;
			}
			foreach (int successor in flow.Successors[current])
			{
				if (!excluded.Contains(successor))
				{
					pending.Enqueue(successor);
				}
			}
		}
		return false;
	}

	private static IReadOnlySet<int> GetPredecessors(
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow,
		int target)
	{
		var predecessors = new HashSet<int>();
		foreach ((int source, HashSet<int> successors) in flow.Successors)
		{
			if (successors.Contains(target))
			{
				predecessors.Add(source);
			}
		}
		return predecessors;
	}

	private static void AssertExactRevisionGuardControlFlow(MethodInfo ensureRevision)
	{
		Assert.Empty(ensureRevision.GetMethodBody()?.ExceptionHandlingClauses ?? []);
		var flow = CreateValidControlFlow(ensureRevision);
		var thrown = GetSingleInstruction(flow.Instructions, OpCodes.Throw);
		var returned = GetSingleInstruction(flow.Instructions, OpCodes.Ret);
		var guard = GetSingleConditionalBranch(flow.Instructions);
		bool hasThrowOnlySuccessor = false;
		bool hasReturnOnlySuccessor = false;
		foreach (int successor in flow.Successors[guard.Offset])
		{
			hasThrowOnlySuccessor |= CanReach(flow, successor, thrown.Offset) &&
				!CanReach(flow, successor, returned.Offset);
			hasReturnOnlySuccessor |= CanReach(flow, successor, returned.Offset) &&
				!CanReach(flow, successor, thrown.Offset);
		}
		Assert.True(hasThrowOnlySuccessor);
		Assert.True(hasReturnOnlySuccessor);
		Assert.Empty(flow.Successors[thrown.Offset]);
		Assert.Empty(flow.Successors[returned.Offset]);
		AssertExactCallMultiset(
			ensureRevision,
			RequiredPropertyGetter(typeof(LiquidWalletState), nameof(LiquidWalletState.Revision)),
			RequiredConstructor(typeof(InvalidOperationException), typeof(string)));
		Assert.Empty(GetStoredFields(ensureRevision));
	}

	private static void AssertExactQueryArrayDataFlow(
		MethodInfo query,
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow,
		int lookupOffset,
		int newArrayOffset,
		int builderOffset,
		int storeOffset,
		int ownershipOffset,
		FieldInfo unspentField,
		MethodInfo peggedAssetGetter,
		MethodInfo revisionGetter)
	{
		var instructions = flow.Instructions
			.Where(instruction => instruction.OpCode != OpCodes.Nop)
			.ToArray();
		int lookupPosition = FindInstructionPosition(instructions, lookupOffset);
		int newArrayPosition = FindInstructionPosition(instructions, newArrayOffset);
		Assert.True(lookupPosition >= 4 && newArrayPosition > lookupPosition);
		Assert.Equal(0, GetLoadedArgumentIndex(query, instructions[lookupPosition - 4]));
		Assert.Equal(OpCodes.Ldfld, instructions[lookupPosition - 3].OpCode);
		Assert.Equal(unspentField, ResolveInstructionMember(query, instructions[lookupPosition - 3]));
		Assert.Equal(2, GetLoadedArgumentIndex(query, instructions[lookupPosition - 2]));
		int outputLocal = Assert.IsType<int>(
			GetAddressedLocalIndex(query, instructions[lookupPosition - 1]));
		Assert.Equal(
			typeof(LiquidOwnedOutput),
			query.GetMethodBody()?.LocalVariables[outputLocal].LocalType);
		Assert.Equal(1, CountLocalAccess(query, instructions, outputLocal, 2));
		var outputLoad = GetSingleLocalAccess(query, instructions, outputLocal, 0);
		int builderPosition = FindInstructionPosition(instructions, builderOffset);
		Assert.Equal(outputLoad.Offset, instructions[builderPosition - 1].Offset);
		Assert.Equal(0, GetLoadedArgumentIndex(query, instructions[builderPosition - 2]));

		var lengthShape = instructions[(lookupPosition + 1)..newArrayPosition];
		int? foundLocal = GetStoredLocalIndex(query, lengthShape[0]);
		if (lengthShape.Length == 6)
		{
			Assert.NotNull(foundLocal);
			Assert.Equal(typeof(bool), query.GetMethodBody()?.LocalVariables[foundLocal.Value].LocalType);
			Assert.Equal(foundLocal, GetLoadedLocalIndex(query, lengthShape[1]));
			Assert.Equal(FlowControl.Cond_Branch, lengthShape[2].OpCode.FlowControl);
			int? fallthroughLength = GetInt32Constant(query, lengthShape[3]);
			Assert.Equal(FlowControl.Branch, lengthShape[4].OpCode.FlowControl);
			int? branchLength = GetInt32Constant(query, lengthShape[5]);
			Assert.True(
				new HashSet<int?> { fallthroughLength, branchLength }.SetEquals([0, 1]));
			byte[] queryIl = query.GetMethodBody()?.GetILAsByteArray() ?? [];
			Assert.Equal(
				lengthShape[5].Offset,
				Assert.Single(GetBranchTargets(lengthShape[2], queryIl)));
			Assert.Equal(
				newArrayOffset,
				Assert.Single(GetBranchTargets(lengthShape[4], queryIl)));
			Assert.True(IsBooleanConditionalBranch(lengthShape[2].OpCode));
			int trueLength = BranchesOnTrue(lengthShape[2].OpCode)
				? branchLength!.Value
				: fallthroughLength!.Value;
			int falseLength = BranchesOnTrue(lengthShape[2].OpCode)
				? fallthroughLength!.Value
				: branchLength!.Value;
			Assert.Equal(1, trueLength);
			Assert.Equal(0, falseLength);
			Assert.Equal(1, CountLocalAccess(query, instructions, foundLocal.Value, 1));
			Assert.Equal(2, CountLocalAccess(query, instructions, foundLocal.Value, 0));
			Assert.Equal(0, CountLocalAccess(query, instructions, foundLocal.Value, 2));
			foreach (int successor in flow.Successors[lengthShape[2].Offset])
			{
				Assert.True(CanReach(flow, successor, newArrayOffset));
			}
		}
		else
		{
			Assert.Equal(3, lengthShape.Length);
			Assert.Null(foundLocal);
			Assert.Equal(OpCodes.Dup, lengthShape[0].OpCode);
			Assert.Equal(0, GetInt32Constant(query, lengthShape[1]));
			Assert.Equal(OpCodes.Cgt_Un, lengthShape[2].OpCode);
		}

		LocalVariableInfo? resultArrayMatch = null;
		foreach (LocalVariableInfo local in query.GetMethodBody()?.LocalVariables ?? [])
		{
			if (local.LocalType == typeof(LiquidWalletCoinControlEntry[]))
			{
				Assert.Null(resultArrayMatch);
				resultArrayMatch = local;
			}
		}
		LocalVariableInfo resultArray = Assert.IsAssignableFrom<LocalVariableInfo>(resultArrayMatch);
		var arrayStore = instructions[newArrayPosition + 1];
		Assert.Equal(resultArray.LocalIndex, GetStoredLocalIndex(query, arrayStore));
		Assert.Equal(1, CountLocalAccess(query, instructions, resultArray.LocalIndex, 1));
		var arrayLoads = new List<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>();
		foreach (var instruction in instructions)
		{
			if (GetLoadedLocalIndex(query, instruction) == resultArray.LocalIndex)
			{
				arrayLoads.Add(instruction);
			}
		}
		Assert.Equal(2, arrayLoads.Count);
		Assert.Equal(0, CountLocalAccess(query, instructions, resultArray.LocalIndex, 2));
		Assert.Equal(foundLocal is null ? 1 : 0, CountOpCode(instructions, OpCodes.Dup));
		Assert.DoesNotContain(instructions, instruction =>
			instruction.OpCode == OpCodes.Starg || instruction.OpCode == OpCodes.Starg_S);

		int firstArrayLoadPosition = FindInstructionPosition(instructions, arrayLoads[0].Offset);
		Assert.Equal(0, GetInt32Constant(query, instructions[firstArrayLoadPosition + 1]));
		Assert.Equal(0, GetLoadedArgumentIndex(query, instructions[firstArrayLoadPosition + 2]));
		Assert.Equal(outputLoad.Offset, instructions[firstArrayLoadPosition + 3].Offset);
		Assert.Equal(builderOffset, instructions[firstArrayLoadPosition + 4].Offset);
		Assert.Equal(storeOffset, instructions[firstArrayLoadPosition + 5].Offset);
		Assert.Equal(1, CountOpCode(instructions, OpCodes.Stelem_Ref));
		Assert.DoesNotContain(instructions, instruction =>
			IsArrayElementStore(instruction.OpCode) && instruction.OpCode != OpCodes.Stelem_Ref);

		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) hitGuard;
		if (foundLocal is int foundLocalIndex)
		{
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)? foundHitLoadMatch = null;
			foreach (var instruction in instructions)
			{
				if (GetLoadedLocalIndex(query, instruction) == foundLocalIndex &&
					instruction.Offset > newArrayOffset)
				{
					Assert.Null(foundHitLoadMatch);
					foundHitLoadMatch = instruction;
				}
			}
			var foundHitLoad = Assert.IsType<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>(foundHitLoadMatch);
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)? hitGuardMatch = null;
			foreach (var instruction in instructions)
			{
				if (instruction.Offset > foundHitLoad.Offset &&
					instruction.Offset < builderOffset &&
					instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
				{
					Assert.Null(hitGuardMatch);
					hitGuardMatch = instruction;
				}
			}
			hitGuard = Assert.IsType<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>(hitGuardMatch);
			int foundHitPosition = FindInstructionPosition(instructions, foundHitLoad.Offset);
			int hitGuardPosition = FindInstructionPosition(instructions, hitGuard.Offset);
			var hitTransfer =
				instructions[(foundHitPosition + 1)..hitGuardPosition];
			if (hitTransfer.Length != 0)
			{
				Assert.Equal(2, hitTransfer.Length);
				int hitCopyLocal = Assert.IsType<int>(GetStoredLocalIndex(query, hitTransfer[0]));
				Assert.Equal(typeof(bool),
					query.GetMethodBody()?.LocalVariables[hitCopyLocal].LocalType);
				Assert.Equal(hitCopyLocal, GetLoadedLocalIndex(query, hitTransfer[1]));
				Assert.Equal(1, CountLocalAccess(query, instructions, hitCopyLocal, 1));
				Assert.Equal(1, CountLocalAccess(query, instructions, hitCopyLocal, 0));
				Assert.Equal(0, CountLocalAccess(query, instructions, hitCopyLocal, 2));
			}
		}
		else
		{
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)? hitGuardMatch = null;
			foreach (var instruction in instructions)
			{
				if (instruction.Offset > newArrayOffset && instruction.Offset < builderOffset &&
					instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
				{
					Assert.Null(hitGuardMatch);
					hitGuardMatch = instruction;
				}
			}
			hitGuard = Assert.IsType<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>(hitGuardMatch);
			Assert.Equal(arrayStore.Offset, instructions[newArrayPosition + 1].Offset);
			Assert.Equal(hitGuard.Offset, instructions[newArrayPosition + 2].Offset);
		}
		Assert.Equal(FlowControl.Cond_Branch, hitGuard.OpCode.FlowControl);
		Assert.True(IsBooleanConditionalBranch(hitGuard.OpCode));
		HashSet<int> hitSuccessors = flow.Successors[hitGuard.Offset];
		Assert.Equal(2, hitSuccessors.Count);
		int hitGuardPositionInFlow = FindInstructionPosition(flow.Instructions, hitGuard.Offset);
		int fallthrough = flow.Instructions[hitGuardPositionInFlow + 1].Offset;
		int? branchTargetMatch = null;
		foreach (int successor in hitSuccessors)
		{
			if (successor != fallthrough)
			{
				Assert.Null(branchTargetMatch);
				branchTargetMatch = successor;
			}
		}
		int branchTarget = Assert.IsType<int>(branchTargetMatch);
		int trueSuccessor = BranchesOnTrue(hitGuard.OpCode) ? branchTarget : fallthrough;
		int falseSuccessor = BranchesOnTrue(hitGuard.OpCode) ? fallthrough : branchTarget;
		Assert.True(
			CanReach(flow, trueSuccessor, builderOffset) &&
			CanReach(flow, trueSuccessor, storeOffset));
		Assert.True(
			!CanReach(flow, falseSuccessor, builderOffset) &&
			!CanReach(flow, falseSuccessor, storeOffset));
		int hitPathCount = 0;
		int missPathCount = 0;
		foreach (int successor in hitSuccessors)
		{
			hitPathCount += CanReach(flow, successor, builderOffset) &&
				CanReach(flow, successor, storeOffset) ? 1 : 0;
			missPathCount += !CanReach(flow, successor, builderOffset) &&
				!CanReach(flow, successor, storeOffset) ? 1 : 0;
			Assert.True(CanReach(flow, successor, ownershipOffset));
		}
		Assert.Equal(1, hitPathCount);
		Assert.Equal(1, missPathCount);

		int secondArrayLoadPosition = FindInstructionPosition(instructions, arrayLoads[1].Offset);
		Assert.Equal(0, GetLoadedArgumentIndex(query, instructions[secondArrayLoadPosition - 4]));
		Assert.Equal(peggedAssetGetter,
			ResolveInstructionMember(query, instructions[secondArrayLoadPosition - 3]));
		Assert.Equal(0, GetLoadedArgumentIndex(query, instructions[secondArrayLoadPosition - 2]));
		Assert.Equal(revisionGetter,
			ResolveInstructionMember(query, instructions[secondArrayLoadPosition - 1]));
		Assert.Equal(ownershipOffset, instructions[secondArrayLoadPosition + 1].Offset);
		Assert.Equal(1, CountOpCode(instructions, OpCodes.Newarr));
		Assert.Empty(query.GetMethodBody()?.ExceptionHandlingClauses ?? []);
	}

	private static MemberInfo? ResolveInstructionMember(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction)
	{
		if (instruction.OpCode.OperandType is not (
			OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or
			OperandType.InlineTok))
		{
			return null;
		}
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		return method.Module.ResolveMember(
			BitConverter.ToInt32(il, instruction.OperandOffset),
			method.DeclaringType?.GetGenericArguments(),
			method.IsGenericMethod ? method.GetGenericArguments() : null);
	}

	private static string? ResolveInstructionString(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction)
	{
		if (instruction.OpCode != OpCodes.Ldstr)
		{
			return null;
		}
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		return method.Module.ResolveString(BitConverter.ToInt32(il, instruction.OperandOffset));
	}

	private static int? GetStoredLocalIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction) =>
		GetLocalIndex(method, instruction, 1);

	private static int? GetLoadedLocalIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction) =>
		GetLocalIndex(method, instruction, 0);

	private static int? GetAddressedLocalIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction) =>
		GetLocalIndex(method, instruction, 2);

	private static int? GetLoadedArgumentIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction)
	{
		if (instruction.OpCode == OpCodes.Ldarg_0) { return 0; }
		if (instruction.OpCode == OpCodes.Ldarg_1) { return 1; }
		if (instruction.OpCode == OpCodes.Ldarg_2) { return 2; }
		if (instruction.OpCode == OpCodes.Ldarg_3) { return 3; }
		if (instruction.OpCode != OpCodes.Ldarg && instruction.OpCode != OpCodes.Ldarg_S)
		{
			return null;
		}

		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		return instruction.OperandSize == 1
			? il[instruction.OperandOffset]
			: BitConverter.ToUInt16(il, instruction.OperandOffset);
	}

	private static int? GetAddressedArgumentIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction)
	{
		if (instruction.OpCode != OpCodes.Ldarga && instruction.OpCode != OpCodes.Ldarga_S)
		{
			return null;
		}
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		return instruction.OperandSize == 1
			? il[instruction.OperandOffset]
			: BitConverter.ToUInt16(il, instruction.OperandOffset);
	}

	private static int? GetLocalIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction,
		int access)
	{
		OpCode opCode = instruction.OpCode;
		if (access == 1)
		{
			if (opCode == OpCodes.Stloc_0) { return 0; }
			if (opCode == OpCodes.Stloc_1) { return 1; }
			if (opCode == OpCodes.Stloc_2) { return 2; }
			if (opCode == OpCodes.Stloc_3) { return 3; }
			if (opCode != OpCodes.Stloc && opCode != OpCodes.Stloc_S) { return null; }
		}
		else if (access == 0)
		{
			if (opCode == OpCodes.Ldloc_0) { return 0; }
			if (opCode == OpCodes.Ldloc_1) { return 1; }
			if (opCode == OpCodes.Ldloc_2) { return 2; }
			if (opCode == OpCodes.Ldloc_3) { return 3; }
			if (opCode != OpCodes.Ldloc && opCode != OpCodes.Ldloc_S) { return null; }
		}
		else if (opCode != OpCodes.Ldloca && opCode != OpCodes.Ldloca_S)
		{
			return null;
		}

		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		return instruction.OperandSize == 1
			? il[instruction.OperandOffset]
			: BitConverter.ToUInt16(il, instruction.OperandOffset);
	}

	private static int? GetInt32Constant(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction)
	{
		if (instruction.OpCode == OpCodes.Ldc_I4_M1) { return -1; }
		if (instruction.OpCode == OpCodes.Ldc_I4_0) { return 0; }
		if (instruction.OpCode == OpCodes.Ldc_I4_1) { return 1; }
		if (instruction.OpCode == OpCodes.Ldc_I4_2) { return 2; }
		if (instruction.OpCode == OpCodes.Ldc_I4_3) { return 3; }
		if (instruction.OpCode == OpCodes.Ldc_I4_4) { return 4; }
		if (instruction.OpCode == OpCodes.Ldc_I4_5) { return 5; }
		if (instruction.OpCode == OpCodes.Ldc_I4_6) { return 6; }
		if (instruction.OpCode == OpCodes.Ldc_I4_7) { return 7; }
		if (instruction.OpCode == OpCodes.Ldc_I4_8) { return 8; }
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		if (instruction.OpCode == OpCodes.Ldc_I4_S)
		{
			return unchecked((sbyte)il[instruction.OperandOffset]);
		}
		return instruction.OpCode == OpCodes.Ldc_I4
			? BitConverter.ToInt32(il, instruction.OperandOffset)
			: null;
	}

	private static bool IsBooleanConditionalBranch(OpCode opCode) =>
		opCode == OpCodes.Brtrue || opCode == OpCodes.Brtrue_S ||
		opCode == OpCodes.Brfalse || opCode == OpCodes.Brfalse_S;

	private static bool BranchesOnTrue(OpCode opCode)
	{
		Assert.True(IsBooleanConditionalBranch(opCode));
		return opCode == OpCodes.Brtrue || opCode == OpCodes.Brtrue_S;
	}

	private static bool IsArrayElementStore(OpCode opCode) =>
		opCode == OpCodes.Stelem || opCode == OpCodes.Stelem_I ||
		opCode == OpCodes.Stelem_I1 || opCode == OpCodes.Stelem_I2 ||
		opCode == OpCodes.Stelem_I4 || opCode == OpCodes.Stelem_I8 ||
		opCode == OpCodes.Stelem_R4 || opCode == OpCodes.Stelem_R8 ||
		opCode == OpCodes.Stelem_Ref;

	private static void AssertExactOwnedGraphCycles(
		IReadOnlyDictionary<MethodBase, (
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets)> graphFlows,
		MethodInfo validator,
		MethodInfo comparator)
	{
		var cyclicMethods = new List<MethodBase>();
		foreach ((MethodBase method, var flow) in graphFlows)
		{
			var cyclicComponents = new List<HashSet<int>>();
			foreach (HashSet<int> component in GetStronglyConnectedComponents(flow))
			{
				if (component.Count > 1 ||
					(component.Count == 1 && flow.Successors[component.Single()].Contains(component.Single())))
				{
					cyclicComponents.Add(component);
				}
			}
			if (cyclicComponents.Count == 0)
			{
				continue;
			}

			Assert.Equal(validator, method);
			Assert.Single(cyclicComponents);
			cyclicMethods.Add(method);
			(int Source, int Target)? backEdgeMatch = null;
			foreach ((int source, HashSet<int> targets) in flow.Successors)
			{
				foreach (int target in targets)
				{
					if (target < source)
					{
						Assert.Null(backEdgeMatch);
						backEdgeMatch = (source, target);
					}
				}
			}
			var backEdge = Assert.IsType<(int Source, int Target)>(backEdgeMatch);
			HashSet<int> loop = cyclicComponents[0];
			Assert.Contains(backEdge.Source, loop);
			Assert.Contains(backEdge.Target, loop);
			var length = GetSingleInstruction(flow.Instructions, OpCodes.Ldlen);
			Assert.Contains(length.Offset, loop);
			AssertValidatorLoopBoundedByEntriesLength(
				validator,
				comparator,
				flow,
				loop,
				backEdge,
				length);
		}

		Assert.Equal([validator], cyclicMethods);
	}

	private static void AssertValidatorLoopBoundedByEntriesLength(
		MethodInfo validator,
		MethodInfo comparator,
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow,
		IReadOnlySet<int> loop,
		(int Source, int Target) backEdge,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) length)
	{
		var instructions = flow.Instructions
			.Where(instruction => instruction.OpCode != OpCodes.Nop)
			.ToArray();
		LocalVariableInfo indexVariable = GetSingleLocalByType(validator, typeof(int));
		int indexLocal = indexVariable.LocalIndex;
		var elementLoad = GetSingleInstruction(instructions, OpCodes.Ldelem_Ref);
		int elementLoadPosition = FindInstructionPosition(instructions, elementLoad.Offset);
		Assert.Equal(1, GetLoadedArgumentIndex(validator, instructions[elementLoadPosition - 2]));
		Assert.Equal(indexLocal, GetLoadedLocalIndex(validator, instructions[elementLoadPosition - 1]));
		Assert.True(CanReach(flow, backEdge.Target, instructions[elementLoadPosition - 2].Offset));
		foreach (var instruction in flow.Instructions)
		{
			if (instruction.Offset >= backEdge.Target &&
				instruction.Offset < instructions[elementLoadPosition - 2].Offset)
			{
				Assert.Equal(OpCodes.Nop, instruction.OpCode);
			}
		}
		Assert.Contains(instructions[elementLoadPosition - 2].Offset, loop);
		int firstBodyOffset = instructions[elementLoadPosition - 2].Offset;
		var returned = GetSingleInstruction(instructions, OpCodes.Ret);

		var indexStores = new List<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>();
		foreach (var instruction in instructions)
		{
			if (GetStoredLocalIndex(validator, instruction) == indexLocal)
			{
				indexStores.Add(instruction);
			}
		}
		Assert.Equal(2, indexStores.Count);
		int initializationPosition = FindInstructionPosition(instructions, indexStores[0].Offset);
		Assert.Equal(0, GetInt32Constant(validator, instructions[initializationPosition - 1]));
		Assert.DoesNotContain(indexStores[0].Offset, loop);
		AssertDominates(flow, indexStores[0].Offset, firstBodyOffset);
		AssertDominates(flow, indexStores[0].Offset, backEdge.Source);
		Assert.Equal(
			[indexStores[0].Offset],
			flow.Successors[instructions[initializationPosition - 1].Offset]);
		Assert.Equal(
			[instructions[initializationPosition - 1].Offset],
			GetPredecessors(flow, indexStores[0].Offset));
		int incrementPosition = FindInstructionPosition(instructions, indexStores[1].Offset);
		Assert.True(incrementPosition >= 3);
		Assert.Equal(indexLocal, GetLoadedLocalIndex(validator, instructions[incrementPosition - 3]));
		Assert.Equal(1, GetInt32Constant(validator, instructions[incrementPosition - 2]));
		Assert.Equal(OpCodes.Add, instructions[incrementPosition - 1].OpCode);
		Assert.Contains(indexStores[1].Offset, loop);
		var incrementStart = instructions[incrementPosition - 3];
		var incrementConstant = instructions[incrementPosition - 2];
		var incrementAdd = instructions[incrementPosition - 1];
		var incrementStore = indexStores[1];

		(int EntryLoadOffset, int CompletedStoreOffset, int EntryLocal, int PreviousLocal)?
			completedCopyMatch = null;
		for (int position = 0; position + 1 < instructions.Length; position++)
		{
			int? loadedLocal = GetLoadedLocalIndex(validator, instructions[position]);
			int? storedLocal = GetStoredLocalIndex(validator, instructions[position + 1]);
			if (loadedLocal is null || storedLocal is null || loadedLocal == storedLocal ||
				validator.GetMethodBody()?.LocalVariables[loadedLocal.Value].LocalType !=
					typeof(LiquidWalletCoinControlEntry) ||
				validator.GetMethodBody()?.LocalVariables[storedLocal.Value].LocalType !=
					typeof(LiquidWalletCoinControlEntry))
			{
				continue;
			}

			Assert.Null(completedCopyMatch);
			completedCopyMatch = (
				instructions[position].Offset,
				instructions[position + 1].Offset,
				loadedLocal.Value,
				storedLocal.Value);
		}
		var completedCopy = Assert.IsType<(
			int EntryLoadOffset,
			int CompletedStoreOffset,
			int EntryLocal,
			int PreviousLocal)>(completedCopyMatch);
		Assert.NotEqual(completedCopy.EntryLocal, completedCopy.PreviousLocal);
		Assert.Contains(completedCopy.EntryLoadOffset, loop);
		Assert.Contains(completedCopy.CompletedStoreOffset, loop);
		Assert.True(elementLoad.Offset < completedCopy.EntryLoadOffset);
		Assert.True(completedCopy.CompletedStoreOffset < incrementStart.Offset);

		var previousStores = new List<(
			int Offset,
			OpCode OpCode,
			int OperandOffset,
			int OperandSize)>();
		var entryStores = new List<(
			int Offset,
			OpCode OpCode,
			int OperandOffset,
			int OperandSize)>();
		foreach (var instruction in instructions)
		{
			int? storedLocal = GetStoredLocalIndex(validator, instruction);
			if (storedLocal == completedCopy.PreviousLocal)
			{
				previousStores.Add(instruction);
			}
			if (storedLocal == completedCopy.EntryLocal)
			{
				entryStores.Add(instruction);
			}
		}
		Assert.Equal(2, previousStores.Count);
		Assert.Single(entryStores);
		var entryStore = entryStores[0];
		Assert.True(previousStores[0].Offset < firstBodyOffset);
		Assert.DoesNotContain(previousStores[0].Offset, loop);
		Assert.Equal(completedCopy.CompletedStoreOffset, previousStores[1].Offset);
		Assert.Contains(entryStore.Offset, loop);
		Assert.True(elementLoad.Offset < entryStore.Offset);
		Assert.True(entryStore.Offset < completedCopy.EntryLoadOffset);
		int previousInitializationPosition = FindInstructionPosition(
			instructions,
			previousStores[0].Offset);
		Assert.True(previousInitializationPosition > 0);
		Assert.Equal(OpCodes.Ldnull, instructions[previousInitializationPosition - 1].OpCode);
		Assert.Equal(
			0,
			CountLocalAccess(
				validator,
				instructions,
				completedCopy.PreviousLocal,
				2));
		Assert.Equal(
			0,
			CountLocalAccess(
				validator,
				instructions,
				completedCopy.EntryLocal,
				2));

		AssertExactLinearNopPath(
			flow,
			instructions[previousInitializationPosition - 1].Offset,
			previousStores[0].Offset);
		AssertExactLinearNopPath(
			flow,
			instructions[elementLoadPosition - 2].Offset,
			instructions[elementLoadPosition - 1].Offset);
		AssertExactLinearNopPath(
			flow,
			instructions[elementLoadPosition - 1].Offset,
			elementLoad.Offset);
		var elementDuplicate = instructions[elementLoadPosition + 1];
		var nullEntryGuard = instructions[elementLoadPosition + 2];
		Assert.Equal(OpCodes.Dup, elementDuplicate.OpCode);
		Assert.True(IsBooleanConditionalBranch(nullEntryGuard.OpCode));
		Assert.True(BranchesOnTrue(nullEntryGuard.OpCode));
		AssertExactLinearNopPath(flow, elementLoad.Offset, elementDuplicate.Offset);
		AssertExactLinearNopPath(flow, elementDuplicate.Offset, nullEntryGuard.Offset);
		byte[] validatorIl = validator.GetMethodBody()?.GetILAsByteArray() ?? [];
		Assert.Equal(
			entryStore.Offset,
			Assert.Single(GetBranchTargets(nullEntryGuard, validatorIl)));
		Assert.Equal(nullEntryGuard.Offset, Assert.Single(GetPredecessors(flow, entryStore.Offset)));
		Assert.Equal(OpCodes.Pop, instructions[elementLoadPosition + 3].OpCode);

		AssertExactLinearNopPath(
			flow,
			completedCopy.EntryLoadOffset,
			completedCopy.CompletedStoreOffset);
		AssertExactLinearNopPath(
			flow,
			completedCopy.CompletedStoreOffset,
			incrementStart.Offset);
		AssertExactLinearNopPath(flow, incrementStart.Offset, incrementConstant.Offset);
		AssertExactLinearNopPath(flow, incrementConstant.Offset, incrementAdd.Offset);
		AssertExactLinearNopPath(flow, incrementAdd.Offset, incrementStore.Offset);
		Assert.Equal(0, CountLocalAccess(validator, instructions, indexLocal, 2));

		int previousNullGuardOffset = -1;
		int previousNullGuardCount = 0;
		int comparatorCallOffset = -1;
		int comparatorCallCount = 0;
		foreach (var call in GetCalledMethodInstructions(validator))
		{
			Assert.True(call.Offset < completedCopy.CompletedStoreOffset);
			if (call.Method == comparator)
			{
				comparatorCallCount++;
				comparatorCallOffset = call.Offset;
			}
		}
		Assert.Equal(1, comparatorCallCount);
		int comparatorPosition = FindInstructionPosition(instructions, comparatorCallOffset);
		Assert.True(comparatorPosition >= 2);
		Assert.Equal(
			completedCopy.PreviousLocal,
			GetLoadedLocalIndex(validator, instructions[comparatorPosition - 2]));
		Assert.Equal(
			completedCopy.EntryLocal,
			GetLoadedLocalIndex(validator, instructions[comparatorPosition - 1]));
		Assert.True(firstBodyOffset < comparatorCallOffset);
		Assert.True(comparatorCallOffset < completedCopy.EntryLoadOffset);

		const string CanonicalOrderMessage =
			"Liquid coin-control snapshot entries must be unique and canonically ordered.";
		const string EntriesParameterName = "entries";
		ConstructorInfo argumentExceptionConstructor = RequiredConstructor(
			typeof(ArgumentException),
			typeof(string),
			typeof(string));
		(int MessageOffset, int ParameterOffset, int AllocationOffset, int ThrowOffset)?
			canonicalFailureMatch = null;
		for (int position = 0; position + 3 < instructions.Length; position++)
		{
			if (ResolveInstructionString(validator, instructions[position]) != CanonicalOrderMessage ||
				ResolveInstructionString(validator, instructions[position + 1]) != EntriesParameterName ||
				instructions[position + 2].OpCode != OpCodes.Newobj ||
				ResolveInstructionMember(validator, instructions[position + 2]) !=
					argumentExceptionConstructor ||
				instructions[position + 3].OpCode != OpCodes.Throw)
			{
				continue;
			}

			Assert.Null(canonicalFailureMatch);
			canonicalFailureMatch = (
				instructions[position].Offset,
				instructions[position + 1].Offset,
				instructions[position + 2].Offset,
				instructions[position + 3].Offset);
		}
		var canonicalFailure = Assert.IsType<(
			int MessageOffset,
			int ParameterOffset,
			int AllocationOffset,
			int ThrowOffset)>(canonicalFailureMatch);
		AssertExactLinearNopPath(
			flow,
			canonicalFailure.MessageOffset,
			canonicalFailure.ParameterOffset);
		AssertExactLinearNopPath(
			flow,
			canonicalFailure.ParameterOffset,
			canonicalFailure.AllocationOffset);
		AssertExactLinearNopPath(
			flow,
			canonicalFailure.AllocationOffset,
			canonicalFailure.ThrowOffset);
		Assert.Equal(
			canonicalFailure.AllocationOffset,
			Assert.Single(GetPredecessors(flow, canonicalFailure.ThrowOffset)));
		Assert.Empty(flow.Successors[canonicalFailure.ThrowOffset]);

		var comparatorZero = instructions[comparatorPosition + 1];
		Assert.Equal(0, GetInt32Constant(validator, comparatorZero));
		AssertExactLinearNopPath(flow, comparatorCallOffset, comparatorZero.Offset);
		int comparatorFailureGuardOffset;
		int noPreviousCompletionPathOffset;
		var signedComparison = instructions[comparatorPosition + 2];
		if (signedComparison.OpCode == OpCodes.Blt || signedComparison.OpCode == OpCodes.Blt_S)
		{
			AssertExactLinearNopPath(flow, comparatorZero.Offset, signedComparison.Offset);
			Assert.Equal(
				completedCopy.EntryLoadOffset,
				Assert.Single(GetBranchTargets(signedComparison, validatorIl)));
			int signedComparisonPosition = FindInstructionPosition(
				instructions,
				signedComparison.Offset);
			Assert.Equal(
				canonicalFailure.MessageOffset,
				instructions[signedComparisonPosition + 1].Offset);
			comparatorFailureGuardOffset = signedComparison.Offset;
			noPreviousCompletionPathOffset = completedCopy.EntryLoadOffset;
		}
		else
		{
			Assert.Equal(OpCodes.Clt, signedComparison.OpCode);
			var inversionZero = instructions[comparatorPosition + 3];
			var inversion = instructions[comparatorPosition + 4];
			var predicateTransfer = instructions[comparatorPosition + 5];
			Assert.Equal(0, GetInt32Constant(validator, inversionZero));
			Assert.Equal(OpCodes.Ceq, inversion.OpCode);
			Assert.Equal(FlowControl.Branch, predicateTransfer.OpCode.FlowControl);
			AssertExactLinearNopPath(flow, comparatorZero.Offset, signedComparison.Offset);
			AssertExactLinearNopPath(flow, signedComparison.Offset, inversionZero.Offset);
			AssertExactLinearNopPath(flow, inversionZero.Offset, inversion.Offset);
			AssertExactLinearNopPath(flow, inversion.Offset, predicateTransfer.Offset);
			int predicateStoreOffset = Assert.Single(
				GetBranchTargets(predicateTransfer, validatorIl));
			int predicateStorePosition = FindInstructionPosition(
				instructions,
				predicateStoreOffset);
			Assert.True(predicateStorePosition > 0);
			int predicateLocal = Assert.IsType<int>(
				GetStoredLocalIndex(validator, instructions[predicateStorePosition]));
			Assert.Equal(
				typeof(bool),
				validator.GetMethodBody()?.LocalVariables[predicateLocal].LocalType);
			Assert.Equal(0, GetInt32Constant(
				validator,
				instructions[predicateStorePosition - 1]));
			Assert.Equal(1, CountLocalAccess(validator, instructions, predicateLocal, 1));
			Assert.Equal(1, CountLocalAccess(validator, instructions, predicateLocal, 0));
			Assert.Equal(0, CountLocalAccess(validator, instructions, predicateLocal, 2));
			var predicateLoad = instructions[predicateStorePosition + 1];
			var predicateGuard = instructions[predicateStorePosition + 2];
			Assert.Equal(predicateLocal, GetLoadedLocalIndex(validator, predicateLoad));
			Assert.True(IsBooleanConditionalBranch(predicateGuard.OpCode));
			Assert.False(BranchesOnTrue(predicateGuard.OpCode));
			AssertExactLinearNopPath(flow, predicateStoreOffset, predicateLoad.Offset);
			AssertExactLinearNopPath(flow, predicateLoad.Offset, predicateGuard.Offset);
			Assert.Equal(
				completedCopy.EntryLoadOffset,
				Assert.Single(GetBranchTargets(predicateGuard, validatorIl)));
			int predicateGuardPosition = FindInstructionPosition(
				instructions,
				predicateGuard.Offset);
			Assert.Equal(
				canonicalFailure.MessageOffset,
				instructions[predicateGuardPosition + 1].Offset);
			comparatorFailureGuardOffset = predicateGuard.Offset;
			noPreviousCompletionPathOffset = instructions[predicateStorePosition - 1].Offset;
			Assert.True(GetPredecessors(flow, completedCopy.EntryLoadOffset).SetEquals(
				new[] { predicateGuard.Offset }));
			Assert.True(GetPredecessors(flow, predicateStoreOffset).SetEquals(
				new[] { predicateTransfer.Offset, noPreviousCompletionPathOffset }));
			Assert.Equal(
				predicateStoreOffset,
				Assert.Single(flow.Successors[noPreviousCompletionPathOffset]));
		}
		int? comparatorFailureEdgeMatch = null;
		foreach (int successor in flow.Successors[comparatorFailureGuardOffset])
		{
			if (successor != completedCopy.EntryLoadOffset)
			{
				Assert.Null(comparatorFailureEdgeMatch);
				comparatorFailureEdgeMatch = successor;
			}
		}
		int comparatorFailureEdge = Assert.IsType<int>(comparatorFailureEdgeMatch);
		Assert.True(flow.Successors[comparatorFailureGuardOffset].SetEquals(
			new[] { completedCopy.EntryLoadOffset, comparatorFailureEdge }));
		if (comparatorFailureEdge != canonicalFailure.MessageOffset)
		{
			AssertExactLinearNopPath(
				flow,
				comparatorFailureEdge,
				canonicalFailure.MessageOffset);
		}
		Assert.True(CanReach(
			flow,
			canonicalFailure.MessageOffset,
			canonicalFailure.AllocationOffset));
		Assert.True(CanReach(
			flow,
			canonicalFailure.MessageOffset,
			canonicalFailure.ThrowOffset));
		Assert.False(CanReach(
			flow,
			canonicalFailure.MessageOffset,
			completedCopy.CompletedStoreOffset));
		Assert.False(CanReach(
			flow,
			canonicalFailure.MessageOffset,
			incrementStart.Offset));
		Assert.False(CanReach(
			flow,
			canonicalFailure.MessageOffset,
			returned.Offset));
		var currentIterationExclusion = new HashSet<int> { backEdge.Source };
		Assert.False(CanReachAvoiding(
			flow,
			completedCopy.EntryLoadOffset,
			canonicalFailure.AllocationOffset,
			currentIterationExclusion));
		Assert.False(CanReachAvoiding(
			flow,
			completedCopy.EntryLoadOffset,
			canonicalFailure.ThrowOffset,
			currentIterationExclusion));
		Assert.True(CanReach(
			flow,
			completedCopy.EntryLoadOffset,
			completedCopy.CompletedStoreOffset));
		for (int position = 0; position + 1 < instructions.Length; position++)
		{
			if (GetLoadedLocalIndex(validator, instructions[position]) ==
					completedCopy.PreviousLocal &&
				instructions[position + 1].OpCode.FlowControl == FlowControl.Cond_Branch)
			{
				previousNullGuardCount++;
				previousNullGuardOffset = instructions[position + 1].Offset;
			}
		}
		Assert.Equal(1, previousNullGuardCount);
		Assert.True(entryStore.Offset < previousNullGuardOffset);
		Assert.True(previousNullGuardOffset < comparatorCallOffset);
		var previousNullGuard = instructions[
			FindInstructionPosition(instructions, previousNullGuardOffset)];
		Assert.True(IsBooleanConditionalBranch(previousNullGuard.OpCode));
		Assert.False(BranchesOnTrue(previousNullGuard.OpCode));
		Assert.Equal(
			noPreviousCompletionPathOffset,
			Assert.Single(GetBranchTargets(previousNullGuard, validatorIl)));
		Assert.True(flow.Successors[previousNullGuard.Offset].SetEquals(
			new[]
			{
				instructions[comparatorPosition - 2].Offset,
				noPreviousCompletionPathOffset,
			}));
		Assert.True(CanReach(
			flow,
			noPreviousCompletionPathOffset,
			completedCopy.CompletedStoreOffset));
		if (noPreviousCompletionPathOffset == completedCopy.EntryLoadOffset)
		{
			Assert.False(CanReachAvoiding(
				flow,
				noPreviousCompletionPathOffset,
				canonicalFailure.ThrowOffset,
				currentIterationExclusion));
			Assert.True(GetPredecessors(flow, completedCopy.EntryLoadOffset).SetEquals(
				new[] { previousNullGuard.Offset, comparatorFailureGuardOffset }));
		}
		Assert.Equal(
			2,
			CountLocalAccess(
				validator,
				instructions,
				completedCopy.PreviousLocal,
				0));

		Assert.True(CanReach(flow, firstBodyOffset, completedCopy.CompletedStoreOffset));
		Assert.True(CanReach(flow, elementLoad.Offset, completedCopy.CompletedStoreOffset));
		Assert.True(CanReach(flow, completedCopy.CompletedStoreOffset, incrementStart.Offset));
		Assert.True(CanReach(flow, incrementStart.Offset, backEdge.Source));
		var completedStoreExclusion = new HashSet<int> { completedCopy.CompletedStoreOffset };
		Assert.False(CanReachAvoiding(
			flow,
			firstBodyOffset,
			incrementStart.Offset,
			completedStoreExclusion));
		Assert.False(CanReachAvoiding(
			flow,
			elementLoad.Offset,
			backEdge.Source,
			completedStoreExclusion));
		Assert.False(CanReachAvoiding(
			flow,
			firstBodyOffset,
			backEdge.Source,
			completedStoreExclusion));
		Assert.False(CanReachAvoiding(
			flow,
			firstBodyOffset,
			returned.Offset,
			completedStoreExclusion));
		var incrementStoreExclusion = new HashSet<int> { incrementStore.Offset };
		Assert.False(CanReachAvoiding(
			flow,
			completedCopy.CompletedStoreOffset,
			backEdge.Source,
			incrementStoreExclusion));
		Assert.False(CanReachAvoiding(
			flow,
			firstBodyOffset,
			backEdge.Source,
			incrementStoreExclusion));
		IReadOnlySet<int> conditionExclusion = currentIterationExclusion;
		Assert.False(CanReachAvoiding(
			flow,
			firstBodyOffset,
			returned.Offset,
			conditionExclusion));

		int throwTerminalCount = 0;
		foreach (var instruction in instructions)
		{
			if (instruction.OpCode != OpCodes.Throw && instruction.OpCode != OpCodes.Rethrow)
			{
				continue;
			}

			throwTerminalCount++;
			Assert.True(CanReach(flow, firstBodyOffset, instruction.Offset));
			Assert.True(CanReach(flow, elementLoad.Offset, instruction.Offset));
			Assert.False(CanReachAvoiding(
				flow,
				completedCopy.CompletedStoreOffset,
				instruction.Offset,
				conditionExclusion));
			Assert.True(instruction.Offset < completedCopy.CompletedStoreOffset);
		}
		Assert.True(throwTerminalCount > 0);
		int lengthPosition = FindInstructionPosition(instructions, length.Offset);
		Assert.True(lengthPosition >= 2);
		Assert.Equal(1, GetLoadedArgumentIndex(validator, instructions[lengthPosition - 1]));
		Assert.Equal(indexLocal, GetLoadedLocalIndex(validator, instructions[lengthPosition - 2]));
		Assert.Contains(instructions[lengthPosition - 2].Offset, loop);
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)? conditionMatch = null;
		foreach (var instruction in instructions)
		{
			if (instruction.Offset >= length.Offset && instruction.Offset <= backEdge.Source &&
				instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
			{
				Assert.Null(conditionMatch);
				conditionMatch = instruction;
			}
		}
		Assert.Equal(backEdge.Source, Assert.IsType<(
			int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>(conditionMatch).Offset);

		var comparison = instructions[(lengthPosition + 1)..(
			FindInstructionPosition(instructions, backEdge.Source) + 1)];
		Assert.Equal(OpCodes.Conv_I4, comparison[0].OpCode);
		if (comparison.Length == 2)
		{
			Assert.True(
				comparison[1].OpCode == OpCodes.Blt || comparison[1].OpCode == OpCodes.Blt_S);
		}
		else
		{
			Assert.Equal(5, comparison.Length);
			Assert.Equal(OpCodes.Clt, comparison[1].OpCode);
			int conditionLocal = Assert.IsType<int>(GetStoredLocalIndex(validator, comparison[2]));
			Assert.Equal(typeof(bool),
				validator.GetMethodBody()?.LocalVariables[conditionLocal].LocalType);
			Assert.Equal(conditionLocal, GetLoadedLocalIndex(validator, comparison[3]));
			Assert.True(BranchesOnTrue(comparison[4].OpCode));
		}
		int? backwardTarget = null;
		foreach (int target in flow.Successors[backEdge.Source])
		{
			if (target < backEdge.Source)
			{
				Assert.Null(backwardTarget);
				backwardTarget = target;
			}
		}
		Assert.Equal(backEdge.Target, Assert.IsType<int>(backwardTarget));
		Assert.Equal(3, CountLocalAccess(validator, instructions, indexLocal, 0));
		int branchPosition = FindInstructionPosition(instructions, backEdge.Source);
		Assert.Equal(2, flow.Successors[backEdge.Source].Count);
		int? falseSuccessorMatch = null;
		foreach (int successor in flow.Successors[backEdge.Source])
		{
			if (successor != backEdge.Target)
			{
				Assert.Null(falseSuccessorMatch);
				falseSuccessorMatch = successor;
			}
		}
		int falseSuccessor = Assert.IsType<int>(falseSuccessorMatch);
		Assert.Equal(instructions[branchPosition + 1].Offset, falseSuccessor);
		Assert.True(CanReach(flow, backEdge.Target, firstBodyOffset));
		Assert.True(CanReach(flow, falseSuccessor, returned.Offset));
		Assert.False(CanReach(flow, falseSuccessor, backEdge.Target));
		foreach (var terminal in instructions)
		{
			if (flow.Successors[terminal.Offset].Count == 0 &&
				CanReach(flow, falseSuccessor, terminal.Offset))
			{
				Assert.Equal(returned.Offset, terminal.Offset);
			}
		}
		AssertValidatorArrayArgumentUse(validator, instructions, elementLoad, length);
	}

	private static void AssertExactLinearNopPath(
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow,
		int sourceOffset,
		int targetOffset)
	{
		int sourcePosition = FindInstructionPosition(flow.Instructions, sourceOffset);
		int targetPosition = FindInstructionPosition(flow.Instructions, targetOffset);
		Assert.True(sourcePosition >= 0 && targetPosition > sourcePosition);
		for (int position = sourcePosition; position < targetPosition; position++)
		{
			if (position > sourcePosition)
			{
				Assert.Equal(OpCodes.Nop, flow.Instructions[position].OpCode);
			}

			int currentOffset = flow.Instructions[position].Offset;
			int nextOffset = flow.Instructions[position + 1].Offset;
			Assert.Equal(nextOffset, Assert.Single(flow.Successors[currentOffset]));
			Assert.Equal(currentOffset, Assert.Single(GetPredecessors(flow, nextOffset)));
		}
	}

	private static void AssertValidatorArrayArgumentUse(
		MethodInfo validator,
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> instructions,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) elementLoad,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) length)
	{
		Assert.Equal(typeof(LiquidWalletCoinControlEntry[]), validator.GetParameters()[1].ParameterType);
		int arrayParameterCount = 0;
		foreach (ParameterInfo parameter in validator.GetParameters())
		{
			arrayParameterCount += parameter.ParameterType.IsArray ? 1 : 0;
		}
		Assert.Equal(1, arrayParameterCount);
		foreach (LocalVariableInfo local in validator.GetMethodBody()?.LocalVariables ?? [])
		{
			Assert.False(local.LocalType.IsArray);
		}
		foreach (FieldInfo field in GetReferencedFields(validator))
		{
			Assert.False(field.FieldType.IsArray);
		}
		foreach (var instruction in instructions)
		{
			Assert.NotEqual(1, GetAddressedArgumentIndex(validator, instruction));
			Assert.True(instruction.OpCode != OpCodes.Starg && instruction.OpCode != OpCodes.Starg_S);
		}

		int? nullGuardCallMatch = null;
		foreach (var call in GetCalledMethodInstructions(validator))
		{
			if (call.Method.DeclaringType == typeof(ArgumentNullException) &&
				call.Method.Name == nameof(ArgumentNullException.ThrowIfNull))
			{
				Assert.Null(nullGuardCallMatch);
				nullGuardCallMatch = call.Offset;
			}
		}
		int nullGuardCall = Assert.IsType<int>(nullGuardCallMatch);
		int nullGuardPosition = FindInstructionPosition(instructions, nullGuardCall);
		Assert.Equal(1, GetLoadedArgumentIndex(validator, instructions[nullGuardPosition - 2]));
		Assert.Equal(OpCodes.Ldstr, instructions[nullGuardPosition - 1].OpCode);
		var allowedLoads = new HashSet<int>
		{
			instructions[nullGuardPosition - 2].Offset,
			instructions[FindInstructionPosition(instructions, elementLoad.Offset) - 2].Offset,
			instructions[FindInstructionPosition(instructions, length.Offset) - 1].Offset,
		};
		var actualLoads = new HashSet<int>();
		foreach (var instruction in instructions)
		{
			if (GetLoadedArgumentIndex(validator, instruction) == 1)
			{
				actualLoads.Add(instruction.Offset);
			}
		}
		Assert.Equal(allowedLoads, actualLoads);
		Assert.Equal(1, CountOpCode(instructions, OpCodes.Ldlen));
		Assert.Equal(1, CountOpCode(instructions, OpCodes.Ldelem_Ref));
		foreach (var instruction in instructions)
		{
			Assert.True(instruction.OpCode != OpCodes.Box &&
				instruction.OpCode != OpCodes.Castclass &&
				instruction.OpCode != OpCodes.Isinst &&
				!IsArrayElementStore(instruction.OpCode));
		}
	}

	private static IReadOnlyList<HashSet<int>> GetStronglyConnectedComponents(
		(
			IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)> Instructions,
			IReadOnlyDictionary<int, HashSet<int>> Successors,
			IReadOnlySet<int> ReachableOffsets) flow)
	{
		var components = new List<HashSet<int>>();
		var assigned = new HashSet<int>();
		foreach (int offset in flow.ReachableOffsets)
		{
			if (assigned.Contains(offset))
			{
				continue;
			}
			var component = new HashSet<int>();
			foreach (int candidate in flow.ReachableOffsets)
			{
				if (CanReach(flow, offset, candidate) && CanReach(flow, candidate, offset))
				{
					component.Add(candidate);
				}
			}
			assigned.UnionWith(component);
			components.Add(component);
		}
		return components;
	}

	private static void AssertExactOwnershipReferenceFlow(
		MethodInfo query,
		MethodInfo ownership,
		ConstructorInfo ownershipConstructor,
		MethodInfo validator,
		int newArrayOffset,
		int ownershipOffset,
		MethodInfo peggedAssetGetter,
		MethodInfo revisionGetter)
	{
		var queryInstructions = GetNonNopInstructions(query);
		int ownershipPosition = FindInstructionPosition(queryInstructions, ownershipOffset);
		int resultLocal = Assert.IsType<int>(
			GetLoadedLocalIndex(query, queryInstructions[ownershipPosition - 1]));
		Assert.Equal(revisionGetter,
			ResolveInstructionMember(query, queryInstructions[ownershipPosition - 2]));
		Assert.Equal(0, GetLoadedArgumentIndex(query, queryInstructions[ownershipPosition - 3]));
		Assert.Equal(peggedAssetGetter,
			ResolveInstructionMember(query, queryInstructions[ownershipPosition - 4]));
		Assert.Equal(0, GetLoadedArgumentIndex(query, queryInstructions[ownershipPosition - 5]));
		Assert.Equal(2, CountLocalAccess(query, queryInstructions, resultLocal, 0));
		Assert.Equal(1, CountLocalAccess(query, queryInstructions, resultLocal, 1));
		Assert.Equal(0, CountLocalAccess(query, queryInstructions, resultLocal, 2));
		var resultStore = GetSingleLocalAccess(query, queryInstructions, resultLocal, 1);
		Assert.Equal(newArrayOffset, queryInstructions[
			FindInstructionPosition(queryInstructions, resultStore.Offset) - 1].Offset);

		var ownershipInstructions = GetNonNopInstructions(ownership);
		int constructorPosition = FindInstructionPosition(
			ownershipInstructions,
			GetSingleInstructionOffset(
				ownership,
				ownershipInstructions,
				OpCodes.Newobj,
				ownershipConstructor));
		Assert.True(constructorPosition >= 3);
		Assert.Equal(0, GetLoadedArgumentIndex(ownership, ownershipInstructions[constructorPosition - 3]));
		Assert.Equal(1, GetLoadedArgumentIndex(ownership, ownershipInstructions[constructorPosition - 2]));
		Assert.Equal(2, GetLoadedArgumentIndex(ownership, ownershipInstructions[constructorPosition - 1]));
		int ownershipThirdArgumentLoads = 0;
		foreach (var instruction in ownershipInstructions)
		{
			if (GetLoadedArgumentIndex(ownership, instruction) == 2)
			{
				ownershipThirdArgumentLoads++;
			}
			Assert.Null(GetAddressedArgumentIndex(ownership, instruction));
		}
		Assert.Equal(2, ownershipThirdArgumentLoads);

		var constructorInstructions = GetNonNopInstructions(ownershipConstructor);
		int validationPosition = FindInstructionPosition(
			constructorInstructions,
			GetSingleInstructionOffset(
				ownershipConstructor,
				constructorInstructions,
				OpCodes.Call,
				validator));
		FieldInfo entriesField = RequiredField(typeof(LiquidWalletCoinControlSnapshot), "_entries");
		int entriesStorePosition = FindInstructionPosition(
			constructorInstructions,
			GetSingleInstructionOffset(
				ownershipConstructor,
				constructorInstructions,
				OpCodes.Stfld,
				entriesField));
		Assert.True(validationPosition >= 2 && entriesStorePosition > validationPosition);
		Assert.Equal(1, GetLoadedArgumentIndex(
			ownershipConstructor,
			constructorInstructions[validationPosition - 2]));
		Assert.Equal(3, GetLoadedArgumentIndex(
			ownershipConstructor,
			constructorInstructions[validationPosition - 1]));
		Assert.Equal(0, GetLoadedArgumentIndex(
			ownershipConstructor,
			constructorInstructions[entriesStorePosition - 2]));
		Assert.Equal(3, GetLoadedArgumentIndex(
			ownershipConstructor,
			constructorInstructions[entriesStorePosition - 1]));
		int constructorFourthArgumentLoads = 0;
		foreach (var instruction in constructorInstructions)
		{
			if (GetLoadedArgumentIndex(ownershipConstructor, instruction) == 3)
			{
				constructorFourthArgumentLoads++;
			}
			Assert.Null(GetAddressedArgumentIndex(ownershipConstructor, instruction));
		}
		Assert.Equal(2, constructorFourthArgumentLoads);
	}

	private static void AssertExactOwnedGraphAllocationsAndMutations(
		IReadOnlyCollection<MethodBase> graph,
		MethodInfo query,
		MethodInfo ensureRevision,
		MethodInfo entryFactory,
		ConstructorInfo entryConstructor,
		ConstructorInfo snapshotConstructor,
		MethodInfo validator,
		MethodInfo comparator)
	{
		var arrayAllocations = new List<(
			MethodBase Method,
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)>();
		foreach (MethodBase method in graph)
		{
			foreach (var instruction in GetIlInstructions(method))
			{
				if (instruction.OpCode == OpCodes.Newarr)
				{
					arrayAllocations.Add((method, instruction));
				}
			}
		}
		var arrayAllocation = Assert.Single(arrayAllocations);
		Assert.Equal(query, arrayAllocation.Method);
		Assert.Equal(
			typeof(LiquidWalletCoinControlEntry),
			ResolveInstructionMember(query, arrayAllocation.Instruction));

		var objectAllocations = new List<(MethodBase Method, ConstructorInfo Constructor)>();
		foreach (MethodBase method in graph)
		{
			foreach (IlReference reference in GetIlReferences(method))
			{
				if (reference.OpCode == OpCodes.Newobj)
				{
					objectAllocations.Add((
						method,
						Assert.IsAssignableFrom<ConstructorInfo>(reference.Member)));
				}
			}
		}
		AssertExactAllocation(objectAllocations, ensureRevision,
			RequiredConstructor(typeof(InvalidOperationException), typeof(string)));
		AssertExactAllocation(objectAllocations, entryFactory,
			RequiredConstructor(typeof(ArgumentOutOfRangeException), typeof(string), typeof(string)));
		AssertExactAllocation(objectAllocations, entryFactory,
			RequiredConstructor(typeof(ArgumentException), typeof(string), typeof(string)));
		AssertExactAllocation(objectAllocations, entryFactory, entryConstructor);
		AssertExactAllocation(objectAllocations,
			RequiredMethod(typeof(LiquidWalletCoinControlSnapshot), "TakeOwnershipFromState",
				BindingFlags.NonPublic | BindingFlags.Static),
			snapshotConstructor);
		for (int index = 0; index < 3; index++)
		{
			AssertExactAllocation(objectAllocations, validator,
				RequiredConstructor(typeof(ArgumentException), typeof(string), typeof(string)));
		}
		Assert.Empty(objectAllocations);

		var elementStores = new List<(MethodBase Method, OpCode OpCode)>();
		foreach (MethodBase method in graph)
		{
			foreach (var instruction in GetIlInstructions(method))
			{
				if (IsArrayElementStore(instruction.OpCode))
				{
					elementStores.Add((method, instruction.OpCode));
				}
			}
		}
		var elementStore = Assert.Single(elementStores);
		Assert.Equal(query, elementStore.Method);
		Assert.Equal(OpCodes.Stelem_Ref, elementStore.OpCode);

		FieldInfo[] expectedEntryStores =
		new FieldInfo[]
		{
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<OutPoint>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<Amount>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<PeggedAssetId>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlEntry), "<Confirmation>k__BackingField"),
		};
		FieldInfo[] expectedSnapshotStores =
		new FieldInfo[]
		{
			RequiredField(typeof(LiquidWalletCoinControlSnapshot), "<PeggedAssetId>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlSnapshot), "<Revision>k__BackingField"),
			RequiredField(typeof(LiquidWalletCoinControlSnapshot), "_entries"),
		};
		Assert.Equal(expectedEntryStores, GetStoredFields(entryConstructor));
		Assert.Equal(expectedSnapshotStores, GetStoredFields(snapshotConstructor));
		int storedFieldCount = 0;
		foreach (MethodBase method in graph)
		{
			foreach (FieldInfo field in GetStoredFields(method))
			{
				storedFieldCount++;
			}
		}
		Assert.Equal(7, storedFieldCount);

		var addressedLocals = new List<(
			MethodBase Method,
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)>();
		foreach (MethodBase method in graph)
		{
			foreach (var instruction in GetNonNopInstructions(method))
			{
				if (GetAddressedLocalIndex(method, instruction) is not null)
				{
					addressedLocals.Add((method, instruction));
				}
			}
		}
		Assert.Equal(3, addressedLocals.Count);
		(MethodBase Method, (int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)?
			queryAddress = null;
		(MethodBase Method, (int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)?
			builderAddress = null;
		(MethodBase Method, (int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)?
			comparatorAddress = null;
		foreach (var addressedLocal in addressedLocals)
		{
			if (addressedLocal.Method == query)
			{
				Assert.Null(queryAddress);
				queryAddress = addressedLocal;
			}
			if (addressedLocal.Method.Name == "CreateCoinControlEntry")
			{
				Assert.Null(builderAddress);
				builderAddress = addressedLocal;
			}
			if (addressedLocal.Method == comparator)
			{
				Assert.Null(comparatorAddress);
				comparatorAddress = addressedLocal;
			}
		}
		var exactQueryAddress = Assert.IsType<(
			MethodBase Method,
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)>(queryAddress);
		var exactBuilderAddress = Assert.IsType<(
			MethodBase Method,
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)>(builderAddress);
		var exactComparatorAddress = Assert.IsType<(
			MethodBase Method,
			(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) Instruction)>(comparatorAddress);
		AssertAddressImmediatelyFeedsCall(
			query,
			exactQueryAddress.Instruction,
			RequiredMethod(
				typeof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>),
				nameof(Dictionary<LiquidOutPoint, LiquidOwnedOutput>.TryGetValue),
				BindingFlags.Public | BindingFlags.Instance,
				typeof(LiquidOutPoint),
				typeof(LiquidOwnedOutput).MakeByRefType()));
		AssertAddressImmediatelyFeedsCall(
			exactBuilderAddress.Method,
			exactBuilderAddress.Instruction,
			RequiredMethod(
				typeof(Dictionary<LiquidTransactionId, LiquidConfirmation>),
				nameof(Dictionary<LiquidTransactionId, LiquidConfirmation>.TryGetValue),
				BindingFlags.Public | BindingFlags.Instance,
				typeof(LiquidTransactionId),
				typeof(LiquidConfirmation).MakeByRefType()));
		AssertComparatorAddressFeedsReadOnlyCompare(comparator, exactComparatorAddress.Instruction);
		foreach (MethodBase method in graph)
		{
			foreach (var instruction in GetNonNopInstructions(method))
			{
				Assert.Null(GetAddressedArgumentIndex(method, instruction));
			}
		}
	}

	private static void AssertAddressImmediatelyFeedsCall(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) address,
		MethodBase expectedCall)
	{
		var instructions = GetNonNopInstructions(method);
		int position = FindInstructionPosition(instructions, address.Offset);
		Assert.Equal(expectedCall, ResolveInstructionMember(method, instructions[position + 1]));
	}

	private static void AssertComparatorAddressFeedsReadOnlyCompare(
		MethodInfo comparator,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) address)
	{
		int localIndex = Assert.IsType<int>(GetAddressedLocalIndex(comparator, address));
		Assert.Equal(typeof(uint), comparator.GetMethodBody()?.LocalVariables[localIndex].LocalType);
		var instructions = GetNonNopInstructions(comparator);
		Assert.Equal(1, CountLocalAccess(comparator, instructions, localIndex, 1));
		Assert.Equal(1, CountLocalAccess(comparator, instructions, localIndex, 2));
		Assert.Equal(0, CountLocalAccess(comparator, instructions, localIndex, 0));
		int position = FindInstructionPosition(instructions, address.Offset);
		Assert.Equal(1, GetLoadedArgumentIndex(comparator, instructions[position + 1]));
		Assert.Equal(
			RequiredPropertyGetter(typeof(LiquidWalletCoinControlEntry), nameof(LiquidWalletCoinControlEntry.OutPoint)),
			ResolveInstructionMember(comparator, instructions[position + 2]));
		Assert.Equal(
			RequiredPropertyGetter(typeof(LiquidOutPoint), nameof(LiquidOutPoint.OutputIndex)),
			ResolveInstructionMember(comparator, instructions[position + 3]));
		Assert.Equal(
			RequiredMethod(typeof(uint), nameof(uint.CompareTo), BindingFlags.Public | BindingFlags.Instance, typeof(uint)),
			ResolveInstructionMember(comparator, instructions[position + 4]));
	}

	private static void AssertExactAllocation(
		IList<(MethodBase Method, ConstructorInfo Constructor)> allocations,
		MethodBase expectedMethod,
		ConstructorInfo expectedConstructor)
	{
		int index = -1;
		for (int candidate = 0; candidate < allocations.Count; candidate++)
		{
			if (allocations[candidate].Method == expectedMethod &&
				allocations[candidate].Constructor == expectedConstructor)
			{
				index = candidate;
				break;
			}
		}
		Assert.True(index >= 0, "The exact-query graph allocation manifest changed.");
		allocations.RemoveAt(index);
	}

	private static (int Offset, OpCode OpCode, int OperandOffset, int OperandSize)[]
		GetNonNopInstructions(MethodBase method) =>
		GetIlInstructions(method)
			.Where(instruction => instruction.OpCode != OpCodes.Nop)
			.ToArray();

	private static void AssertGraphHasNoForbiddenSurface(IEnumerable<MethodBase> ownedGraph)
	{
		foreach (MethodBase owner in ownedGraph)
		{
			Assert.False(
				ContainsForbiddenExecutionSurface(owner),
				"A forbidden owner appears in the exact-query graph.");
			Assert.DoesNotContain(
				owner.GetMethodBody()?.LocalVariables ?? [],
				local => ContainsForbiddenGraphType(local.LocalType));
			IEnumerable<MemberInfo> referencedMembers = GetIlReferences(owner)
				.Select(reference => reference.Member)
				.Append(owner);
			foreach (MemberInfo member in referencedMembers)
			{
				Assert.False(
					ContainsForbiddenExecutionSurface(member),
					$"Forbidden execution surface in exact-query graph: {member.DeclaringType?.Name}.{member.Name}.");
			}
		}
	}

	private static void AssertExactAssemblyTypeManifestsMatchBase()
	{
		string[] permittedAddedTestTypes =
		[
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<BindsExactExpectationAndFeeInsideUnchangedGenerationAsync>d__86",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsGenerationOrStatusDriftBeforeExpectationAndFeeMismatchAsync>d__87",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsIdentityOrFeeMismatchOnlyAfterStableFenceAsync>d__88",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<ValidatesExpectationBoundInputsBeforeTransportAsync>d__89",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<FetchesExpectationBoundRawTransactionsInsideExactFenceAsync>d__90",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsMalformedOrDriftingRawTransactionsWithoutPartialAuthorityAsync>d__91",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<EncodesOneExpectationBoundPlanFromCanonicalAcquiredTransactionsAsync>d__117",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<RejectsInvalidPlanCompositionBeforeRpcAndInvalidFundingWithoutPartialFrameAsync>d__118",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<AssertPlanEncodingArgumentRejectedAsync>d__119",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass89_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_1",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_2",
			"WalletWasabi.Tests.UnitTests.Liquid.Rpc.ElementsRpcClientTests+<>c__DisplayClass91_3",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1CorpusTests",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanWireV1CorpusTests+<>c__DisplayClass3_0",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.OrdinaryWalletPlanWireV1Corpus",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.OrdinaryWalletPlanWireV1Corpus+<>c",
			"WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire.OrdinaryWalletPlanWireV1Corpus+CorpusTree",
		];
#if DEBUG
		AssertExactAssemblyTypeManifest(
			typeof(LiquidWalletState).Assembly,
			"WalletWasabi",
			1_729,
			"4c610e08438673d546165d34380b483caf365de61ed588f8d9e1714094f414ea");
		AssertExactAssemblyTypeManifest(
			typeof(LiquidWalletCoinControlTests).Assembly,
			"WalletWasabi.Tests",
			1_838,
			"0aa945588554736d9c1c679cdabcb9a7056fb9ee0941ace565fcb221954efa0e",
			permittedAddedTestTypes);
#else
		AssertExactAssemblyTypeManifest(
			typeof(LiquidWalletState).Assembly,
			"WalletWasabi",
			1_726,
			"5cb77829575afe28a78467df8e8895d7af125affc19b3fd5fe2217238a38edc7");
		AssertExactAssemblyTypeManifest(
			typeof(LiquidWalletCoinControlTests).Assembly,
			"WalletWasabi.Tests",
			1_833,
			"ade8113e7f1081371aa0fda520ce27e49671759436191df22afcba687440b0ce",
			permittedAddedTestTypes);
#endif
	}

	private static void AssertExactAssemblyTypeManifest(
		Assembly assembly,
		string expectedSimpleName,
		int expectedCount,
		string expectedSha256,
		IReadOnlyCollection<string>? permittedAddedTypes = null)
	{
		Assert.Equal(expectedSimpleName, assembly.GetName().Name);
		Assert.Same(
			System.Runtime.Loader.AssemblyLoadContext.Default,
			System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly));
		var rows = new HashSet<string>(StringComparer.Ordinal);
		foreach (Type type in assembly.GetTypes())
		{
			Assert.True(rows.Add(Assert.IsType<string>(type.FullName)));
		}
		foreach (string addedType in permittedAddedTypes ?? [])
		{
			Assert.True(rows.Remove(addedType), $"Missing permitted added type: {addedType}");
		}
		Assert.Equal(expectedCount, rows.Count);
		string[] orderedRows = rows.Order(StringComparer.Ordinal).ToArray();
		byte[] manifest = System.Text.Encoding.UTF8.GetBytes(
			expectedSimpleName + "\0" + string.Concat(orderedRows.Select(row => row + "\0")));
		string actualSha256 = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
		Assert.True(StringComparer.Ordinal.Equals(expectedSha256, actualSha256), actualSha256);
	}

	private static bool ContainsForbiddenExecutionSurface(MemberInfo member)
	{
		var visited = new HashSet<Type>();
		if (IsForbiddenExecutionIdentity(member.Module.Assembly.FullName ?? "") ||
			ContainsForbiddenGraphType(member.DeclaringType, visited))
		{
			return true;
		}
		if (member is MethodInfo method &&
			(ContainsForbiddenGraphType(method.ReturnType, visited) ||
			 ContainsForbiddenParameter(method.ReturnParameter, visited) ||
			 (method.IsGenericMethod &&
				 ContainsForbiddenGraphType(method.GetGenericMethodDefinition().DeclaringType, visited))))
		{
			return true;
		}
		if (member is MethodInfo genericMethod)
		{
			foreach (Type genericArgument in genericMethod.GetGenericArguments())
			{
				if (ContainsForbiddenGraphType(genericArgument, visited))
				{
					return true;
				}
			}
		}
		if (member is MethodBase methodBase)
		{
			foreach (ParameterInfo parameter in methodBase.GetParameters())
			{
				if (ContainsForbiddenParameter(parameter, visited))
				{
					return true;
				}
			}
		}
		if (member is FieldInfo field &&
			(ContainsForbiddenGraphType(field.FieldType, visited) ||
			 ContainsForbiddenModifiers(field.GetRequiredCustomModifiers(), visited) ||
			 ContainsForbiddenModifiers(field.GetOptionalCustomModifiers(), visited)))
		{
			return true;
		}
		if (member is PropertyInfo property &&
			(ContainsForbiddenGraphType(property.PropertyType, visited) ||
			 ContainsForbiddenModifiers(property.GetRequiredCustomModifiers(), visited) ||
			 ContainsForbiddenModifiers(property.GetOptionalCustomModifiers(), visited)))
		{
			return true;
		}
		if (member is PropertyInfo indexedProperty)
		{
			foreach (ParameterInfo parameter in indexedProperty.GetIndexParameters())
			{
				if (ContainsForbiddenParameter(parameter, visited))
				{
					return true;
				}
			}
		}
		if (member is EventInfo @event && ContainsForbiddenGraphType(@event.EventHandlerType, visited))
		{
			return true;
		}

		string identity = $"{member.DeclaringType?.FullName}.{member.Name}";
		return IsForbiddenExecutionIdentity(identity);
	}

	private static bool ContainsForbiddenParameter(
		ParameterInfo parameter,
		HashSet<Type> visited) =>
		ContainsForbiddenGraphType(parameter.ParameterType, visited) ||
		ContainsForbiddenModifiers(parameter.GetRequiredCustomModifiers(), visited) ||
		ContainsForbiddenModifiers(parameter.GetOptionalCustomModifiers(), visited);

	private static bool ContainsForbiddenModifiers(
		IEnumerable<Type> modifiers,
		HashSet<Type> visited)
	{
		foreach (Type modifier in modifiers)
		{
			if (ContainsForbiddenGraphType(modifier, visited))
			{
				return true;
			}
		}
		return false;
	}

	private static bool ContainsForbiddenGraphType(Type? type) =>
		ContainsForbiddenGraphType(type, new HashSet<Type>());

	private static bool ContainsForbiddenGraphType(Type? type, HashSet<Type> visited)
	{
		if (type is null || !visited.Add(type))
		{
			return false;
		}
		if (IsForbiddenExecutionIdentity(type.FullName ?? type.Name) ||
			IsForbiddenExecutionIdentity(type.Assembly.FullName ?? ""))
		{
			return true;
		}
		if (type.HasElementType)
		{
			return ContainsForbiddenGraphType(type.GetElementType(), visited);
		}
		if (type.IsFunctionPointer)
		{
			if (ContainsForbiddenGraphType(type.GetFunctionPointerReturnType(), visited))
			{
				return true;
			}
			foreach (Type parameter in type.GetFunctionPointerParameterTypes())
			{
				if (ContainsForbiddenGraphType(parameter, visited))
				{
					return true;
				}
			}
			foreach (Type convention in type.GetFunctionPointerCallingConventions())
			{
				if (ContainsForbiddenGraphType(convention, visited))
				{
					return true;
				}
			}
			return false;
		}
		if (ContainsForbiddenGraphType(type.DeclaringType, visited))
		{
			return true;
		}
		foreach (Type @interface in type.GetInterfaces())
		{
			if (!IsInheritedSerializationMarker(@interface) &&
				ContainsForbiddenGraphType(@interface, visited))
			{
				return true;
			}
		}
		if (ContainsForbiddenGraphType(type.BaseType, visited))
		{
			return true;
		}
		if (type.IsGenericParameter)
		{
			foreach (Type constraint in type.GetGenericParameterConstraints())
			{
				if (ContainsForbiddenGraphType(constraint, visited))
				{
					return true;
				}
			}
		}
		if (!type.IsGenericType)
		{
			return false;
		}
		if (type.IsConstructedGenericType &&
			ContainsForbiddenGraphType(type.GetGenericTypeDefinition(), visited))
		{
			return true;
		}
		foreach (Type argument in type.GetGenericArguments())
		{
			if (ContainsForbiddenGraphType(argument, visited))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsInheritedSerializationMarker(Type type) =>
		type.FullName is "System.Runtime.Serialization.ISerializable"
			or "System.Runtime.Serialization.IDeserializationCallback";

	private static bool IsForbiddenExecutionIdentity(string identity) =>
		identity.Contains(".Rpc", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Grpc", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("JsonRpc", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("PInvoke", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Interop", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("DllImport", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Unmanaged", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.IO", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("FileSystem", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Net", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("HttpClient", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Serializer", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Serialization", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Persistence", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Avalonia", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("WalletWasabi.Fluent", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains(".Views", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains(".ViewModels", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains(".Controls", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Microsoft.UI", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Windows", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("UserInterface", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("ViewModel", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Serilog", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("NLog", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("log4net", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Logging", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Logger", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("ApplicationInsights", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Diagnostics.Activity", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Diagnostics.DiagnosticSource", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Diagnostics.Tracing", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("System.Diagnostics.Metrics", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Metric", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("ActivitySource", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("DiagnosticSource", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("EventSource", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Instrument", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Process", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Ipc", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("MessageBus", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Kafka", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Publish", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Producer", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Fee", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Payment", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("PSET", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("PSBT", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Signing", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Signer", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Broadcast", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("CoinJoin", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("Sponsor", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("USDT", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("USDt", StringComparison.OrdinalIgnoreCase) ||
		identity.Contains("WalletFacts", StringComparison.OrdinalIgnoreCase);

	private static bool IsForbiddenExactQueryOpcode(OpCode opCode) =>
		opCode == OpCodes.Calli || opCode == OpCodes.Cpblk || opCode == OpCodes.Cpobj ||
		opCode == OpCodes.Initblk || opCode == OpCodes.Initobj ||
		opCode == OpCodes.Localloc || opCode == OpCodes.Mkrefany ||
		opCode == OpCodes.Refanyval || opCode == OpCodes.Stind_I || opCode == OpCodes.Stind_I1 ||
		opCode == OpCodes.Stind_I2 || opCode == OpCodes.Stind_I4 || opCode == OpCodes.Stind_I8 ||
		opCode == OpCodes.Stind_R4 || opCode == OpCodes.Stind_R8 || opCode == OpCodes.Stind_Ref ||
		opCode == OpCodes.Stobj || opCode == OpCodes.Starg || opCode == OpCodes.Starg_S ||
		opCode == OpCodes.Ldflda || opCode == OpCodes.Ldsflda || opCode == OpCodes.Ldelema ||
		opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn;

	private static bool IsWritableSpan(Type type) =>
		type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Span<>);

	private static bool IsCapacityMutationOrCallback(MethodBase method) =>
		method.Name is "EnsureCapacity" or "TrimExcess" or "set_Capacity" ||
		typeof(Delegate).IsAssignableFrom(method.DeclaringType);

	private static void AssertSnapshotOrderingAccepted(
		params LiquidWalletCoinControlEntry[] entries)
	{
		var ordinary = new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, entries);
		LiquidWalletCoinControlSnapshot owned =
			LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(
				PeggedAsset,
				0,
				entries.ToArray());
		LiquidOutPoint[] expected = entries.Select(entry => entry.OutPoint).ToArray();
		Assert.Equal(expected, ordinary.GetEntries().Select(entry => entry.OutPoint));
		Assert.Equal(expected, owned.GetEntries().Select(entry => entry.OutPoint));
	}

	private static void AssertSnapshotOrderingRejected(
		params LiquidWalletCoinControlEntry[] entries)
	{
		for (int path = 0; path < 2; path++)
		{
			Exception? failure = null;
			try
			{
				if (path == 0)
				{
					_ = new LiquidWalletCoinControlSnapshot(PeggedAsset, 0, entries);
				}
				else
				{
					_ = LiquidWalletCoinControlSnapshot.TakeOwnershipFromState(
						PeggedAsset,
						0,
						entries.ToArray());
				}
			}
			catch (Exception exception)
			{
				failure = exception;
			}

			ArgumentException argumentFailure = Assert.IsType<ArgumentException>(failure);
			Assert.Equal("entries", argumentFailure.ParamName);
			Assert.Contains(
				"unique and canonically ordered",
				argumentFailure.Message,
				StringComparison.Ordinal);
			AssertRedacted(argumentFailure, entries);
		}
	}
}
