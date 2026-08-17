using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
	public void MutableRestorePreservesSpentOutputOrderForRollbackHistory()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput first = Output(receiveId, 0, PeggedAsset, 40);
		LiquidOwnedOutput second = Output(receiveId, 1, IssuedAsset, 60);
		LiquidTransactionId spendId = Tx('b');
		LiquidWalletState original = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [first, second]))
			.Apply(1, Delta(spendId, [second.OutPoint, first.OutPoint], []));
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(
			original.ExportReplaySnapshot());

		FieldInfo historyField = typeof(LiquidWalletState).GetField(
			"_history",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The replay history field is unavailable.");
		var history = Assert.IsAssignableFrom<System.Collections.IList>(historyField.GetValue(restored));
		object? appliedValue = history[history.Count - 1];
		Assert.NotNull(appliedValue);
		object applied = appliedValue;
		PropertyInfo spentOutputsProperty = applied.GetType().GetProperty(
			"SpentOutputs",
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The spent-output history is unavailable.");
		LiquidOwnedOutput[] spentOutputs = Assert.IsType<LiquidOwnedOutput[]>(
			spentOutputsProperty.GetValue(applied));

		Assert.Equal([second, first], spentOutputs);
		AssertEquivalent(
			original.RollbackLast(2, spendId),
			restored.RollbackLast(2, spendId),
			[receiveId, spendId]);
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
	public void MutableRestorePreservesValidationPrecedence()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 10);
		LiquidWalletTransactionDelta receive = Delta(receiveId, [], [received]);
		LiquidOutPoint unknown = LiquidOutPoint.CreateSpendable(Tx('f'), 0);
		LiquidWalletTransactionDelta duplicateWithUnknownSpend = Delta(
			receiveId,
			[unknown],
			[]);

		InvalidOperationException duplicate = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletState.RestoreReplaySnapshot(Snapshot(
				2,
				[receive, duplicateWithUnknownSpend],
				[])));
		Assert.Equal("A Liquid wallet transaction cannot be applied more than once.", duplicate.Message);

		LiquidTransactionId laterId = Tx('b');
		LiquidAssetAmount foreignAmount = LiquidAssetAmount.Create(
			IssuedAsset,
			OtherPeggedAsset,
			1);
		LiquidWalletTransactionDelta unknownSpendWithForeignOutput = Delta(
			laterId,
			[unknown],
			[Output(laterId, 0, foreignAmount)]);
		InvalidOperationException unavailable = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletState.RestoreReplaySnapshot(Snapshot(
				2,
				[receive, unknownSpendWithForeignOutput],
				[])));
		Assert.Equal(
			"A Liquid wallet transaction attempted to spend an unavailable owned output.",
			unavailable.Message);
	}

	[Fact]
	public void RestoreCallGraphUsesOnlyTheMutableBuilderPath()
	{
		MethodInfo restore = typeof(LiquidWalletState).GetMethod(
			nameof(LiquidWalletState.RestoreReplaySnapshot),
			BindingFlags.Public | BindingFlags.Static) ??
			throw new InvalidOperationException("The replay restore method is unavailable.");
		HashSet<MethodBase> callGraph = GetStateRestoreCallGraph(restore);
		MethodInfo immutableApply = typeof(LiquidWalletState).GetMethod(
			nameof(LiquidWalletState.Apply),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The immutable apply method is unavailable.");
		MethodInfo immutableConfirm = typeof(LiquidWalletState).GetMethod(
			nameof(LiquidWalletState.Confirm),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The immutable confirm method is unavailable.");
		MethodInfo balanceAdd = typeof(LiquidAssetBalanceMap).GetMethod(
			nameof(LiquidAssetBalanceMap.Add),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The immutable balance add method is unavailable.");
		MethodInfo balanceSubtract = typeof(LiquidAssetBalanceMap).GetMethod(
			nameof(LiquidAssetBalanceMap.Subtract),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The immutable balance subtract method is unavailable.");

		Assert.DoesNotContain(immutableApply, callGraph);
		Assert.DoesNotContain(immutableConfirm, callGraph);
		Assert.DoesNotContain(balanceAdd, callGraph);
		Assert.DoesNotContain(balanceSubtract, callGraph);
		Assert.Contains(callGraph, method =>
			method.DeclaringType?.Name.Contains("ReplayBuilder", StringComparison.Ordinal) == true &&
			method.Name == nameof(LiquidWalletState.Apply));
		Assert.Contains(callGraph, method =>
			method.DeclaringType?.Name.Contains("ReplayBuilder", StringComparison.Ordinal) == true &&
			method.Name == nameof(LiquidWalletState.Confirm));

		MethodInfo builderApply = Assert.Single(callGraph.OfType<MethodInfo>(), method =>
			method.DeclaringType?.Name.Contains("ReplayBuilder", StringComparison.Ordinal) == true &&
			method.Name == nameof(LiquidWalletState.Apply));
		MethodInfo builderConfirm = Assert.Single(callGraph.OfType<MethodInfo>(), method =>
			method.DeclaringType?.Name.Contains("ReplayBuilder", StringComparison.Ordinal) == true &&
			method.Name == nameof(LiquidWalletState.Confirm));
		HashSet<MethodBase> perStepGraph = GetStateRestoreCallGraph(builderApply);
		perStepGraph.UnionWith(GetStateRestoreCallGraph(builderConfirm));
		Assert.DoesNotContain(perStepGraph, IsCollectionCopyOrMaterializer);
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

	private static HashSet<MethodBase> GetStateRestoreCallGraph(MethodInfo root)
	{
		var discovered = new HashSet<MethodBase> { root };
		var pending = new Queue<MethodBase>();
		pending.Enqueue(root);
		while (pending.TryDequeue(out MethodBase? current))
		{
			foreach (MethodBase called in GetCalledMethods(current))
			{
				if (!discovered.Add(called))
				{
					continue;
				}
				Type? declaringType = called.DeclaringType;
				if (declaringType is not null &&
					(declaringType == typeof(LiquidWalletState) ||
					 declaringType.DeclaringType == typeof(LiquidWalletState)))
				{
					pending.Enqueue(called);
				}
			}
		}
		return discovered;
	}

	private static bool IsCollectionCopyOrMaterializer(MethodBase method)
	{
		if (method.DeclaringType == typeof(Enumerable) &&
			method.Name is nameof(Enumerable.ToDictionary) or nameof(Enumerable.ToList) or nameof(Enumerable.ToHashSet))
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

	private static IEnumerable<MethodBase> GetCalledMethods(MethodBase method)
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
			if (opCode.OperandType == OperandType.InlineMethod)
			{
				int token = BitConverter.ToInt32(il, position);
				MethodBase? called = method.Module.ResolveMethod(
					token,
					method.DeclaringType?.GetGenericArguments(),
					method.IsGenericMethod ? method.GetGenericArguments() : null);
				if (called is not null)
				{
					yield return called;
				}
			}
			position += GetOperandSize(opCode.OperandType, il, position);
		}
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
