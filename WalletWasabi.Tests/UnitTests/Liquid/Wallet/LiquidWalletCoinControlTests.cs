using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
		const int OutputCount = 119_832;
		// The v4 canonical encoding appends a 4-byte receive-label count (empty
		// here), so the label-free byte model is OutputCount*140 + 568; with the
		// label field it is 4 bytes larger. Seal then appends the two 8-byte
		// high-waters before padding to the 4 KiB bucket, so this count is the
		// largest that still seals under the 16 MiB padded ceiling.
		Assert.Equal(
			16_777_052,
			48 + (13 * 40) + (OutputCount * (118 + 22)) + 4);
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
