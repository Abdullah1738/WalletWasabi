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
		IReadOnlyList<IlReference> snapshotOwnershipDirect =
			GetIlReferences(snapshotOwnership).ToArray();
		IReadOnlyList<IlReference> snapshotOwnershipConstructorDirect =
			GetIlReferences(snapshotOwnershipConstructor).ToArray();
		IReadOnlyList<IlReference> ownershipDirect = GetIlReferences(selectionOwnership).ToArray();
		IReadOnlyList<IlReference> ownershipConstructorDirect =
			GetIlReferences(selectionOwnershipConstructor).ToArray();
		IReadOnlyList<IlReference> coreDirect = GetIlReferences(selectionCore).ToArray();
		Assert.Contains(projectionDirect, reference => reference.Member == snapshotOwnership);
		Assert.Contains(selectionDirect, reference => reference.Member == selectionOwnership);
		Assert.Equal(1, CountEntryArrayAllocations(projectionDirect));
		Assert.Equal(1, CountEntryArrayAllocations(selectionDirect));
		Assert.Equal(1, selectionDirect.Count(reference =>
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
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletCoinControlEntry> expectedEntries = expected.GetEntries();
		IReadOnlyList<LiquidWalletCoinControlEntry> actualEntries = actual.GetEntries();
		Assert.Equal(expectedEntries.Count, actualEntries.Count);
		for (int index = 0; index < expectedEntries.Count; index++)
		{
			Assert.Equal(expectedEntries[index].OutPoint, actualEntries[index].OutPoint);
			Assert.Equal(expectedEntries[index].Amount, actualEntries[index].Amount);
			Assert.Equal(expectedEntries[index].Confirmation, actualEntries[index].Confirmation);
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
		Assert.DoesNotContain(PeggedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(IssuedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(OtherPeggedAssetHex, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(BlockHash, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(PublicKeyHex, exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			Convert.ToHexString(ExternalKey.GetScriptPubKey()),
			exception.Message,
			StringComparison.OrdinalIgnoreCase);
		foreach (LiquidWalletCoinControlEntry entry in sensitiveEntries)
		{
			Assert.DoesNotContain(
				entry.OutPoint.TransactionId.CanonicalRpcHex,
				exception.Message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				entry.OutPoint.OutputIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
				exception.Message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				entry.Amount.AtomicUnits.ToString(System.Globalization.CultureInfo.InvariantCulture),
				exception.Message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				entry.Amount.AssetId.CanonicalRpcHex,
				exception.Message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				entry.Amount.PeggedAssetId.CanonicalRpcHex,
				exception.Message,
				StringComparison.Ordinal);
			if (entry.Confirmation is { } confirmation)
			{
				Assert.DoesNotContain(
					confirmation.CanonicalBlockHash,
					exception.Message,
					StringComparison.Ordinal);
				Assert.DoesNotContain(
					confirmation.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
					exception.Message,
					StringComparison.Ordinal);
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
		return name.Contains(".Rpc.", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("System.IO", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Serializer", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Serialization", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("ISerializable", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("PSET", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("PSBT", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Signing", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Broadcast", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("CoinJoin", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Sponsor", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("USDT", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("WalletFacts", StringComparison.OrdinalIgnoreCase);
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
}
