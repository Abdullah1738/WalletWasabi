using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletReplaySnapshotTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherPeggedAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlockHash = "4444444444444444444444444444444444444444444444444444444444444444";
	private const string ReplacementBlockHash = "5555555555555555555555555555555555555555555555555555555555555555";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidAssetId OtherPeggedAsset => LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => LiquidSpendKeyReference.Create(
		Convert.FromHexString(PublicKeyHex),
		LiquidKeyBranch.External,
		0);

	[Fact]
	public void EmptyStateRoundTripsWithoutDerivedCaches()
	{
		LiquidWalletState original = LiquidWalletState.Empty(PeggedAsset);

		LiquidWalletReplaySnapshot snapshot = original.ExportReplaySnapshot();
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);

		Assert.Empty(snapshot.GetDeltas());
		Assert.Empty(snapshot.GetConfirmations());
		AssertEquivalent(original, restored, []);
	}

	[Fact]
	public void ReplaysMultiassetReceiveSpendAndConfirmationHistoryExactly()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput lbtc = Output(receiveId, 0, PeggedAsset, 100);
		LiquidOwnedOutput issued = Output(receiveId, 1, IssuedAsset, 200);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput issuedChange = Output(spendId, 0, IssuedAsset, 150);
		LiquidConfirmation firstConfirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidConfirmation replacement = LiquidConfirmation.Create(ReplacementBlockHash, 43);

		LiquidWalletState original = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [lbtc, issued]))
			.Apply(1, Delta(spendId, [issued.OutPoint], [issuedChange]))
			.Confirm(2, receiveId, firstConfirmation)
			.Unconfirm(3, receiveId, firstConfirmation)
			.Confirm(4, receiveId, replacement)
			.Confirm(5, spendId, firstConfirmation);

		LiquidWalletReplaySnapshot snapshot = original.ExportReplaySnapshot();
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(snapshot);

		Assert.Equal(6ul, snapshot.Revision);
		Assert.Equal([receiveId, spendId], snapshot.GetDeltas().Select(delta => delta.TransactionId));
		AssertEquivalent(original, restored, [receiveId, spendId]);
		Assert.Equal(100, restored.GetBalances().GetAmountOrZero(PeggedAsset).AtomicUnits);
		Assert.Equal(150, restored.GetBalances().GetAmountOrZero(IssuedAsset).AtomicUnits);
	}

	[Fact]
	public void ExportsDeltasInApplyOrderAndConfirmationsInCanonicalRpcTransactionOrder()
	{
		LiquidTransactionId thirdId = Tx('c');
		LiquidTransactionId firstId = Tx('a');
		LiquidTransactionId secondId = Tx('b');
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(thirdId, [], [Output(thirdId, 0, PeggedAsset, 3)]))
			.Apply(1, Delta(firstId, [], [Output(firstId, 0, PeggedAsset, 1)]))
			.Apply(2, Delta(secondId, [], [Output(secondId, 0, IssuedAsset, 2)]))
			.Confirm(3, thirdId, confirmation)
			.Confirm(4, secondId, confirmation)
			.Confirm(5, firstId, confirmation);

		LiquidWalletReplaySnapshot snapshot = state.ExportReplaySnapshot();

		Assert.Equal(
			[thirdId, firstId, secondId],
			snapshot.GetDeltas().Select(delta => delta.TransactionId));
		Assert.Equal(
			[firstId, secondId, thirdId],
			snapshot.GetConfirmations().Select(entry => entry.TransactionId));
		AssertEquivalent(
			state,
			LiquidWalletState.RestoreReplaySnapshot(snapshot),
			[firstId, secondId, thirdId]);
	}

	[Fact]
	public void PreservesReachableRollbackRevisionGaps()
	{
		LiquidTransactionId unconfirmedId = Tx('a');
		LiquidWalletState afterUnconfirmedRollback = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(unconfirmedId, [], [Output(unconfirmedId, 0, PeggedAsset, 1)]))
			.RollbackLast(1, unconfirmedId);
		LiquidWalletState restoredGapTwo = LiquidWalletState.RestoreReplaySnapshot(
			afterUnconfirmedRollback.ExportReplaySnapshot());

		LiquidTransactionId confirmedId = Tx('b');
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState afterConfirmedRollback = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(confirmedId, [], [Output(confirmedId, 0, PeggedAsset, 1)]))
			.Confirm(1, confirmedId, confirmation)
			.RollbackLast(2, confirmedId);
		LiquidWalletState restoredGapThree = LiquidWalletState.RestoreReplaySnapshot(
			afterConfirmedRollback.ExportReplaySnapshot());

		Assert.Equal(2ul, restoredGapTwo.Revision);
		Assert.Equal(3ul, restoredGapThree.Revision);
		Assert.Empty(restoredGapTwo.GetUnspentOutputs());
		Assert.Empty(restoredGapThree.GetUnspentOutputs());
	}

	[Fact]
	public void AcceptsEveryReachableSmallGapAndRejectsTheImpossibleGapOne()
	{
		LiquidWalletState zero = LiquidWalletState.RestoreReplaySnapshot(Snapshot(0, [], []));
		LiquidWalletState two = LiquidWalletState.RestoreReplaySnapshot(Snapshot(2, [], []));
		LiquidWalletState three = LiquidWalletState.RestoreReplaySnapshot(Snapshot(3, [], []));

		Assert.Equal(0ul, zero.Revision);
		Assert.Equal(2ul, two.Revision);
		Assert.Equal(3ul, three.Revision);
		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletState.RestoreReplaySnapshot(Snapshot(1, [], [])));
	}

	[Fact]
	public void RestoredStateSupportsEquivalentRollbackAndFutureTransitions()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 100);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput change = Output(spendId, 0, PeggedAsset, 90);
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState original = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [received]))
			.Apply(1, Delta(spendId, [received.OutPoint], [change]))
			.Confirm(2, spendId, confirmation);
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(
			original.ExportReplaySnapshot());

		LiquidWalletState originalRolledBack = original.RollbackLast(3, spendId);
		LiquidWalletState restoredRolledBack = restored.RollbackLast(3, spendId);
		AssertEquivalent(originalRolledBack, restoredRolledBack, [receiveId, spendId]);

		LiquidTransactionId laterId = Tx('c');
		LiquidWalletTransactionDelta later = Delta(
			laterId,
			[],
			[Output(laterId, 0, IssuedAsset, 200)]);
		LiquidWalletState originalApplied = originalRolledBack.Apply(4, later);
		LiquidWalletState restoredApplied = restoredRolledBack.Apply(4, later);
		AssertEquivalent(originalApplied, restoredApplied, [receiveId, spendId, laterId]);

		LiquidWalletState originalConfirmed = originalApplied.Confirm(5, laterId, confirmation);
		LiquidWalletState restoredConfirmed = restoredApplied.Confirm(5, laterId, confirmation);
		AssertEquivalent(originalConfirmed, restoredConfirmed, [receiveId, spendId, laterId]);

		LiquidWalletState originalUnconfirmed = originalConfirmed.Unconfirm(6, laterId, confirmation);
		LiquidWalletState restoredUnconfirmed = restoredConfirmed.Unconfirm(6, laterId, confirmation);
		AssertEquivalent(originalUnconfirmed, restoredUnconfirmed, [receiveId, spendId, laterId]);
	}

	[Fact]
	public void RestoredMaximumRevisionFailsValidTransitionsAtomically()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidWalletTransactionDelta receive = Delta(
			receiveId,
			[],
			[Output(receiveId, 0, PeggedAsset, 100)]);
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState maximumMinusOne = LiquidWalletState.RestoreReplaySnapshot(
			Snapshot(ulong.MaxValue - 1, [], []));

		LiquidWalletState maximum = maximumMinusOne.Apply(ulong.MaxValue - 1, receive);
		LiquidWalletState expectedMaximum = LiquidWalletState.RestoreReplaySnapshot(
			maximum.ExportReplaySnapshot());
		Assert.Equal(ulong.MaxValue, maximum.Revision);

		LiquidTransactionId laterId = Tx('b');
		LiquidWalletTransactionDelta later = Delta(
			laterId,
			[],
			[Output(laterId, 0, IssuedAsset, 200)]);
		OverflowException applyOverflow = Assert.Throws<OverflowException>(() =>
			maximum.Apply(ulong.MaxValue, later));
		OverflowException confirmOverflow = Assert.Throws<OverflowException>(() =>
			maximum.Confirm(ulong.MaxValue, receiveId, confirmation));
		OverflowException rollbackOverflow = Assert.Throws<OverflowException>(() =>
			maximum.RollbackLast(ulong.MaxValue, receiveId));

		LiquidWalletReplayConfirmation replayConfirmation =
			LiquidWalletReplayConfirmation.Create(receiveId, confirmation);
		LiquidWalletState confirmedMaximum = LiquidWalletState.RestoreReplaySnapshot(
			Snapshot(ulong.MaxValue, [receive], [replayConfirmation]));
		LiquidWalletState expectedConfirmedMaximum = LiquidWalletState.RestoreReplaySnapshot(
			confirmedMaximum.ExportReplaySnapshot());
		OverflowException unconfirmOverflow = Assert.Throws<OverflowException>(() =>
			confirmedMaximum.Unconfirm(ulong.MaxValue, receiveId, confirmation));

		AssertEquivalent(expectedMaximum, maximum, [receiveId, laterId]);
		AssertEquivalent(expectedConfirmedMaximum, confirmedMaximum, [receiveId]);
		foreach (OverflowException exception in new[]
		{
			applyOverflow,
			confirmOverflow,
			rollbackOverflow,
			unconfirmOverflow,
		})
		{
			Assert.DoesNotContain(receiveId.CanonicalRpcHex, exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(laterId.CanonicalRpcHex, exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(PeggedAssetHex, exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("100", exception.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void SnapshotDefensivelyCopiesValuesAndReturnsReadOnlyCollections()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidWalletTransactionDelta delta = Delta(
			transactionId,
			[],
			[Output(transactionId, 0, PeggedAsset, 1)]);
		LiquidWalletReplayConfirmation confirmation = LiquidWalletReplayConfirmation.Create(
			transactionId,
			LiquidConfirmation.Create(BlockHash, 42));
		var sourceDeltas = new List<LiquidWalletTransactionDelta> { delta };
		var sourceConfirmations = new List<LiquidWalletReplayConfirmation> { confirmation };
		LiquidWalletReplaySnapshot snapshot = Snapshot(2, sourceDeltas, sourceConfirmations);
		sourceDeltas.Clear();
		sourceConfirmations.Clear();

		IReadOnlyList<LiquidWalletTransactionDelta> deltas = snapshot.GetDeltas();
		IReadOnlyList<LiquidWalletReplayConfirmation> confirmations = snapshot.GetConfirmations();
		var mutableDeltas = Assert.IsAssignableFrom<IList<LiquidWalletTransactionDelta>>(deltas);
		var mutableConfirmations = Assert.IsAssignableFrom<IList<LiquidWalletReplayConfirmation>>(confirmations);

		Assert.Single(deltas);
		Assert.Single(confirmations);
		Assert.NotSame(delta, deltas[0]);
		Assert.NotSame(deltas[0], snapshot.GetDeltas()[0]);
		Assert.NotSame(confirmation, confirmations[0]);
		Assert.True(mutableDeltas.IsReadOnly);
		Assert.True(mutableConfirmations.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableDeltas.Add(delta));
		Assert.Throws<NotSupportedException>(() => mutableConfirmations.Add(confirmation));
		AssertEquivalent(
			LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, delta)
				.Confirm(1, transactionId, confirmation.Confirmation),
			LiquidWalletState.RestoreReplaySnapshot(snapshot),
			[transactionId]);
	}

	[Fact]
	public void ReplayConfirmationRejectsZeroTransactionIdentifierWithoutExposingIt()
	{
		LiquidTransactionId zero = LiquidTransactionId.ParseRpcHex(new string('0', 64));

		var exception = Assert.Throws<ArgumentException>(() =>
			LiquidWalletReplayConfirmation.Create(
				zero,
				LiquidConfirmation.Create(BlockHash, 42)));

		Assert.DoesNotContain(zero.CanonicalRpcHex, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RestoreRejectsInvalidReplayInputsWithoutReturningPartialState()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 10);
		LiquidWalletTransactionDelta receive = Delta(receiveId, [], [received]);
		LiquidTransactionId spendId = Tx('b');
		LiquidWalletTransactionDelta spend = Delta(spendId, [received.OutPoint], []);
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletReplayConfirmation receiveConfirmation =
			LiquidWalletReplayConfirmation.Create(receiveId, confirmation);

		LiquidWalletTransactionDelta unknownSpend = Delta(
			Tx('c'),
			[LiquidOutPoint.CreateSpendable(Tx('d'), 0)],
			[]);
		LiquidAssetAmount foreignAmount = LiquidAssetAmount.Create(
			IssuedAsset,
			OtherPeggedAsset,
			1);
		LiquidWalletTransactionDelta foreign = Delta(
			Tx('e'),
			[],
			[Output(Tx('e'), 0, foreignAmount)]);

		var failures = new Action[]
		{
			() => LiquidWalletState.RestoreReplaySnapshot(Snapshot(2, [receive, receive], [])),
			() => LiquidWalletState.RestoreReplaySnapshot(Snapshot(1, [unknownSpend], [])),
			() => LiquidWalletState.RestoreReplaySnapshot(Snapshot(1, [foreign], [])),
			() => LiquidWalletState.RestoreReplaySnapshot(Snapshot(0, [receive], [])),
			() => LiquidWalletState.RestoreReplaySnapshot(Snapshot(2, [spend, receive], [])),
			() => LiquidWalletState.RestoreReplaySnapshot(Snapshot(
				1,
				[],
				[receiveConfirmation])),
			() => LiquidWalletState.RestoreReplaySnapshot(Snapshot(
				3,
				[receive],
				[receiveConfirmation, receiveConfirmation])),
		};

		foreach (Action failure in failures)
		{
			Assert.Throws<InvalidOperationException>(failure);
		}
	}

	[Fact]
	public void ReplayStringsAndErrorsDoNotExposeWalletValues()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidWalletTransactionDelta delta = Delta(
			transactionId,
			[],
			[Output(transactionId, 0, IssuedAsset, 987_654_321)]);
		LiquidWalletReplayConfirmation confirmation = LiquidWalletReplayConfirmation.Create(
			transactionId,
			LiquidConfirmation.Create(BlockHash, 42));
		LiquidWalletReplaySnapshot snapshot = Snapshot(3, [delta], [confirmation]);

		Assert.Equal(nameof(LiquidWalletReplaySnapshot), snapshot.ToString());
		Assert.Equal(nameof(LiquidWalletReplayConfirmation), confirmation.ToString());
		var exception = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletState.RestoreReplaySnapshot(Snapshot(2, [delta], [])));

		foreach (string text in new[] { snapshot.ToString(), confirmation.ToString(), exception.Message })
		{
			Assert.DoesNotContain(transactionId.CanonicalRpcHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(IssuedAssetHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain("987654321", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ReplayBoundaryHasNoObservationOrNativeSurface()
	{
		Type observationType = typeof(LiquidOwnedOutputObservation);
		foreach (Type boundaryType in new[]
		{
			typeof(LiquidWalletReplaySnapshot),
			typeof(LiquidWalletReplayConfirmation),
			typeof(LiquidWalletState),
		})
		{
			IEnumerable<Type> signatureTypes = boundaryType
				.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.Select(field => field.FieldType)
				.Concat(boundaryType
					.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
					.Select(property => property.PropertyType))
				.Concat(boundaryType
					.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
					.SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
						.Append(method.ReturnType)));

			Assert.DoesNotContain(signatureTypes, type => ContainsType(type, observationType));
			Assert.DoesNotContain(signatureTypes, type =>
				(type.FullName ?? type.Name).Contains("Native", StringComparison.OrdinalIgnoreCase));
		}

		Assert.DoesNotContain(
			typeof(LiquidWalletReplaySnapshot).Assembly.GetReferencedAssemblies(),
			assembly => (assembly.Name ?? "").Contains("liquid-native", StringComparison.OrdinalIgnoreCase));
	}

	private static LiquidWalletReplaySnapshot Snapshot(
		ulong revision,
		IEnumerable<LiquidWalletTransactionDelta> deltas,
		IEnumerable<LiquidWalletReplayConfirmation> confirmations) =>
		LiquidWalletReplaySnapshot.Create(PeggedAsset, revision, deltas, confirmations);

	private static void AssertEquivalent(
		LiquidWalletState expected,
		LiquidWalletState actual,
		IEnumerable<LiquidTransactionId> transactionIds)
	{
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.Revision, actual.Revision);
		Assert.Equal(expected.AppliedTransactionCount, actual.AppliedTransactionCount);
		Assert.Equal(expected.UnspentOutputCount, actual.UnspentOutputCount);
		Assert.Equal(expected.GetBalances().GetAmounts(), actual.GetBalances().GetAmounts());
		Assert.Equal(expected.GetUnspentOutputs(), actual.GetUnspentOutputs());

		foreach (LiquidTransactionId transactionId in transactionIds)
		{
			bool expectedHasConfirmation = expected.TryGetConfirmation(
				transactionId,
				out LiquidConfirmation? expectedConfirmation);
			bool actualHasConfirmation = actual.TryGetConfirmation(
				transactionId,
				out LiquidConfirmation? actualConfirmation);
			Assert.Equal(expectedHasConfirmation, actualHasConfirmation);
			Assert.Equal(expectedConfirmation, actualConfirmation);
		}

		LiquidWalletReplaySnapshot expectedSnapshot = expected.ExportReplaySnapshot();
		LiquidWalletReplaySnapshot actualSnapshot = actual.ExportReplaySnapshot();
		IReadOnlyList<LiquidWalletTransactionDelta> expectedDeltas = expectedSnapshot.GetDeltas();
		IReadOnlyList<LiquidWalletTransactionDelta> actualDeltas = actualSnapshot.GetDeltas();
		Assert.Equal(expectedDeltas.Count, actualDeltas.Count);
		for (int index = 0; index < expectedDeltas.Count; index++)
		{
			Assert.Equal(expectedDeltas[index].TransactionId, actualDeltas[index].TransactionId);
			Assert.Equal(expectedDeltas[index].GetSpentOutPoints(), actualDeltas[index].GetSpentOutPoints());
			Assert.Equal(expectedDeltas[index].GetCreatedOutputs(), actualDeltas[index].GetCreatedOutputs());
		}
		Assert.Equal(
			expectedSnapshot.GetConfirmations(),
			actualSnapshot.GetConfirmations());
	}

	private static bool ContainsType(Type candidate, Type expected)
	{
		if (candidate == expected)
		{
			return true;
		}
		if (candidate.HasElementType)
		{
			return ContainsType(candidate.GetElementType()!, expected);
		}
		return candidate.IsGenericType && candidate.GetGenericArguments().Any(type => ContainsType(type, expected));
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits) =>
		Output(
			transactionId,
			outputIndex,
			LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits));

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetAmount amount)
	{
		LiquidSpendKeyReference key = ExternalKey;
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			key.GetScriptPubKey(),
			amount,
			key);
	}

	private static LiquidWalletTransactionDelta Delta(
		LiquidTransactionId transactionId,
		IEnumerable<LiquidOutPoint> spent,
		IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);
}
