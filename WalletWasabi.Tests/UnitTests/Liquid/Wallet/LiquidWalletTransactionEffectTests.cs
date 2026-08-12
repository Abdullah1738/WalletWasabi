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

public class LiquidWalletTransactionEffectTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string IssuedAssetHex = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherPeggedAssetHex = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlockHash = "4444444444444444444444444444444444444444444444444444444444444444";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidAssetId OtherPeggedAsset => LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);
	private static LiquidSpendKeyReference ExternalKey => LiquidSpendKeyReference.Create(
		Convert.FromHexString(PublicKeyHex),
		LiquidKeyBranch.External,
		0);

	[Fact]
	public void EmptyStateProducesRevisionBoundReadOnlySnapshot()
	{
		LiquidWalletTransactionEffectSnapshot snapshot =
			LiquidWalletState.Empty(PeggedAsset).GetTransactionEffectSnapshot();
		IReadOnlyList<LiquidWalletTransactionEffect> effects = snapshot.GetEffects();
		var mutableView = Assert.IsAssignableFrom<IList<LiquidWalletTransactionEffect>>(effects);

		Assert.Equal(PeggedAsset, snapshot.PeggedAssetId);
		Assert.Equal(0ul, snapshot.Revision);
		Assert.Empty(effects);
		Assert.True(mutableView.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableView.Add(null!));
		Assert.Equal(nameof(LiquidWalletTransactionEffectSnapshot), snapshot.ToString());
	}

	[Fact]
	public void ProjectsPeggedReceiveAndSpendWithOwnedChange()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 100);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput change = Output(spendId, 0, PeggedAsset, 60);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [received]))
			.Apply(1, Delta(spendId, [received.OutPoint], [change]));

		IReadOnlyList<LiquidWalletTransactionEffect> effects =
			state.GetTransactionEffectSnapshot().GetEffects();

		Assert.Equal(2, effects.Count);
		AssertEffect(effects[0], receiveId, null, [(PeggedAsset, 100L)]);
		AssertEffect(effects[1], spendId, null, [(PeggedAsset, -40L)]);
	}

	[Fact]
	public void KeepsMultiassetChangesIndependentAndCanonicallyOrdered()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput issued = Output(receiveId, 0, IssuedAsset, 200);
		LiquidOwnedOutput pegged = Output(receiveId, 1, PeggedAsset, 100);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput issuedChange = Output(spendId, 0, IssuedAsset, 150);
		LiquidOwnedOutput peggedChange = Output(spendId, 1, PeggedAsset, 90);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [issued, pegged]))
			.Apply(1, Delta(
				spendId,
				[issued.OutPoint, pegged.OutPoint],
				[issuedChange, peggedChange]));

		IReadOnlyList<LiquidWalletTransactionEffect> effects =
			state.GetTransactionEffectSnapshot().GetEffects();

		AssertEffect(effects[0], receiveId, null, [(PeggedAsset, 100L), (IssuedAsset, 200L)]);
		AssertEffect(effects[1], spendId, null, [(PeggedAsset, -10L), (IssuedAsset, -50L)]);
	}

	[Fact]
	public void CoversPositiveNegativeAndZeroSameAssetEffects()
	{
		LiquidTransactionId initialId = Tx('a');
		LiquidOwnedOutput initial = Output(initialId, 0, IssuedAsset, 10);
		LiquidTransactionId creditId = Tx('b');
		LiquidOwnedOutput credited = Output(creditId, 0, IssuedAsset, 15);
		LiquidTransactionId debitId = Tx('c');
		LiquidOwnedOutput debited = Output(debitId, 0, IssuedAsset, 10);
		LiquidTransactionId zeroId = Tx('d');
		LiquidOwnedOutput zero = Output(zeroId, 0, IssuedAsset, 10);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(initialId, [], [initial]))
			.Apply(1, Delta(creditId, [initial.OutPoint], [credited]))
			.Apply(2, Delta(debitId, [credited.OutPoint], [debited]))
			.Apply(3, Delta(zeroId, [debited.OutPoint], [zero]));

		IReadOnlyList<LiquidWalletTransactionEffect> effects =
			state.GetTransactionEffectSnapshot().GetEffects();

		AssertEffect(effects[1], creditId, null, [(IssuedAsset, 5L)]);
		AssertEffect(effects[2], debitId, null, [(IssuedAsset, -5L)]);
		Assert.Equal(zeroId, effects[3].TransactionId);
		Assert.Empty(effects[3].GetAssetNetChanges());
	}

	[Fact]
	public void CanonicalAssetOrderIgnoresSpentAndCreatedOrder()
	{
		LiquidAssetId firstAsset = Asset(1);
		LiquidAssetId secondAsset = Asset(2);
		LiquidAssetId thirdAsset = Asset(3);
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput first = Output(receiveId, 0, firstAsset, 10);
		LiquidOwnedOutput second = Output(receiveId, 1, secondAsset, 10);
		LiquidOwnedOutput third = Output(receiveId, 2, thirdAsset, 10);
		LiquidTransactionId spendId = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [third, second, first]))
			.Apply(1, Delta(
				spendId,
				[third.OutPoint, first.OutPoint, second.OutPoint],
				[
					Output(spendId, 2, thirdAsset, 7),
					Output(spendId, 1, secondAsset, 8),
					Output(spendId, 0, firstAsset, 9),
				]));

		IReadOnlyList<LiquidWalletAssetNetChange> receiveChanges = state
			.GetTransactionEffectSnapshot()
			.GetEffects()[0]
			.GetAssetNetChanges();
		IReadOnlyList<LiquidWalletAssetNetChange> spendChanges = state
			.GetTransactionEffectSnapshot()
			.GetEffects()[1]
			.GetAssetNetChanges();

		Assert.Equal([firstAsset, secondAsset, thirdAsset], receiveChanges.Select(change => change.AssetId));
		Assert.Equal([firstAsset, secondAsset, thirdAsset], spendChanges.Select(change => change.AssetId));
		Assert.Equal([-1L, -2L, -3L], spendChanges.Select(change => change.NetAtomicUnits));
	}

	[Fact]
	public void EffectOrderIsApplicationOrderRatherThanTransactionIdOrder()
	{
		LiquidTransactionId thirdId = Tx('c');
		LiquidTransactionId firstId = Tx('a');
		LiquidTransactionId secondId = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(thirdId, [], [Output(thirdId, 0, PeggedAsset, 1)]))
			.Apply(1, Delta(firstId, [], [Output(firstId, 0, IssuedAsset, 2)]))
			.Apply(2, Delta(secondId, [], [Output(secondId, 0, Asset(1), 3)]));

		Assert.Equal(
			[thirdId, firstId, secondId],
			state.GetTransactionEffectSnapshot().GetEffects().Select(effect => effect.TransactionId));
	}

	[Fact]
	public void ConfirmAndUnconfirmCreateDistinctImmutableSnapshots()
	{
		LiquidTransactionId transactionId = Tx('a');
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState applied = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [Output(transactionId, 0, PeggedAsset, 1)]));
		LiquidWalletTransactionEffectSnapshot absent = applied.GetTransactionEffectSnapshot();
		LiquidWalletState confirmedState = applied.Confirm(1, transactionId, confirmation);
		LiquidWalletTransactionEffectSnapshot attached = confirmedState.GetTransactionEffectSnapshot();
		LiquidWalletState unconfirmedState = confirmedState.Unconfirm(2, transactionId, confirmation);
		LiquidWalletTransactionEffectSnapshot detached = unconfirmedState.GetTransactionEffectSnapshot();

		Assert.Equal(1ul, absent.Revision);
		Assert.Equal(2ul, attached.Revision);
		Assert.Equal(3ul, detached.Revision);
		Assert.Null(Assert.Single(absent.GetEffects()).Confirmation);
		Assert.Equal(confirmation, Assert.Single(attached.GetEffects()).Confirmation);
		Assert.Null(Assert.Single(detached.GetEffects()).Confirmation);
		Assert.Equal(confirmation, Assert.Single(attached.GetEffects()).Confirmation);
		Assert.Equal(
			Assert.Single(absent.GetEffects()).GetAssetNetChanges(),
			Assert.Single(attached.GetEffects()).GetAssetNetChanges());
		Assert.Equal(
			Assert.Single(attached.GetEffects()).GetAssetNetChanges(),
			Assert.Single(detached.GetEffects()).GetAssetNetChanges());
	}

	[Fact]
	public void RollbackRemovesOnlyLatestEffect()
	{
		LiquidTransactionId firstId = Tx('a');
		LiquidOwnedOutput first = Output(firstId, 0, PeggedAsset, 10);
		LiquidTransactionId secondId = Tx('b');
		LiquidOwnedOutput second = Output(secondId, 0, PeggedAsset, 9);
		LiquidWalletState applied = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(firstId, [], [first]))
			.Apply(1, Delta(secondId, [first.OutPoint], [second]));
		LiquidWalletState rolledBack = applied.RollbackLast(2, secondId);

		Assert.Equal([firstId, secondId], applied
			.GetTransactionEffectSnapshot().GetEffects().Select(effect => effect.TransactionId));
		Assert.Equal([firstId], rolledBack
			.GetTransactionEffectSnapshot().GetEffects().Select(effect => effect.TransactionId));
		Assert.Equal(3ul, rolledBack.GetTransactionEffectSnapshot().Revision);
	}

	[Fact]
	public void ReplayExportAndRestorePreserveEffectsExactly()
	{
		LiquidWalletState state = CreateMultiassetState();
		LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(state.ExportReplaySnapshot());

		AssertEquivalent(
			state.GetTransactionEffectSnapshot(),
			restored.GetTransactionEffectSnapshot());
	}

	[Fact]
	public void ProtectedReplayOpenAndRestorePreserveEffectsExactly()
	{
		LiquidWalletState state = CreateMultiassetState();
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(state.ExportReplaySnapshot(), 17, key, context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletReplayOpenResult opened = protectedPayload.Open(key, context);
			LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(opened.Snapshot);

			Assert.Equal(17ul, opened.Generation);
			AssertEquivalent(
				state.GetTransactionEffectSnapshot(),
				restored.GetTransactionEffectSnapshot());
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
	public void NetChangeFactoryEnforcesExactSignedAssetBounds()
	{
		long cap = LiquidAssetAmount.MaxPeggedAssetAtomicUnits;
		foreach (long value in new[] { cap, -cap, cap - 1, -(cap - 1) })
		{
			LiquidWalletAssetNetChange change =
				LiquidWalletAssetNetChange.Create(PeggedAsset, PeggedAsset, value);
			Assert.Equal(value, change.NetAtomicUnits);
			Assert.Equal(value > 0, change.IsCredit);
			Assert.Equal(value < 0, change.IsDebit);
		}

		foreach (long value in new[] { long.MaxValue, -long.MaxValue })
		{
			LiquidWalletAssetNetChange change =
				LiquidWalletAssetNetChange.Create(IssuedAsset, PeggedAsset, value);
			Assert.Equal(value, change.NetAtomicUnits);
		}

		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletAssetNetChange.Create(null!, PeggedAsset, 1));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletAssetNetChange.Create(IssuedAsset, null!, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletAssetNetChange.Create(IssuedAsset, PeggedAsset, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletAssetNetChange.Create(IssuedAsset, PeggedAsset, long.MinValue));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletAssetNetChange.Create(PeggedAsset, PeggedAsset, cap + 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletAssetNetChange.Create(PeggedAsset, PeggedAsset, -(cap + 1)));
	}

	[Fact]
	public void ProjectsExactMaximumCreditsDebitsAndMixedSideDifferences()
	{
		long cap = LiquidAssetAmount.MaxPeggedAssetAtomicUnits;
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput pegged = Output(receiveId, 0, PeggedAsset, cap);
		LiquidOwnedOutput issued = Output(receiveId, 1, IssuedAsset, long.MaxValue);
		LiquidTransactionId spendId = Tx('b');
		LiquidWalletState boundary = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(receiveId, [], [pegged, issued]))
			.Apply(1, Delta(spendId, [pegged.OutPoint, issued.OutPoint], []));
		IReadOnlyList<LiquidWalletTransactionEffect> boundaryEffects =
			boundary.GetTransactionEffectSnapshot().GetEffects();

		AssertEffect(
			boundaryEffects[0],
			receiveId,
			null,
			[(PeggedAsset, cap), (IssuedAsset, long.MaxValue)]);
		AssertEffect(
			boundaryEffects[1],
			spendId,
			null,
			[(PeggedAsset, -cap), (IssuedAsset, -long.MaxValue)]);

		LiquidTransactionId initialId = Tx('c');
		LiquidOwnedOutput initial = Output(initialId, 0, IssuedAsset, long.MaxValue - 1);
		LiquidTransactionId plusOneId = Tx('d');
		LiquidOwnedOutput maximum = Output(plusOneId, 0, IssuedAsset, long.MaxValue);
		LiquidTransactionId minusOneId = Tx('e');
		LiquidOwnedOutput belowMaximum = Output(minusOneId, 0, IssuedAsset, long.MaxValue - 1);
		LiquidTransactionId zeroId = Tx('f');
		LiquidOwnedOutput same = Output(zeroId, 0, IssuedAsset, long.MaxValue - 1);
		LiquidWalletState mixed = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(initialId, [], [initial]))
			.Apply(1, Delta(plusOneId, [initial.OutPoint], [maximum]))
			.Apply(2, Delta(minusOneId, [maximum.OutPoint], [belowMaximum]))
			.Apply(3, Delta(zeroId, [belowMaximum.OutPoint], [same]));
		IReadOnlyList<LiquidWalletTransactionEffect> mixedEffects =
			mixed.GetTransactionEffectSnapshot().GetEffects();

		AssertEffect(mixedEffects[1], plusOneId, null, [(IssuedAsset, 1L)]);
		AssertEffect(mixedEffects[2], minusOneId, null, [(IssuedAsset, -1L)]);
		Assert.Empty(mixedEffects[3].GetAssetNetChanges());

		LiquidTransactionId exactMaximumReceiveId = Tx(101);
		LiquidOwnedOutput exactMaximum = Output(
			exactMaximumReceiveId,
			0,
			IssuedAsset,
			long.MaxValue);
		LiquidTransactionId exactMaximumZeroId = Tx(102);
		LiquidOwnedOutput exactMaximumReplacement = Output(
			exactMaximumZeroId,
			0,
			IssuedAsset,
			long.MaxValue);
		LiquidWalletState exactMaximumZero = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(exactMaximumReceiveId, [], [exactMaximum]))
			.Apply(1, Delta(
				exactMaximumZeroId,
				[exactMaximum.OutPoint],
				[exactMaximumReplacement]));
		LiquidWalletTransactionEffect zeroEffect = exactMaximumZero
			.GetTransactionEffectSnapshot()
			.GetEffects()[1];

		Assert.Equal(exactMaximumZeroId, zeroEffect.TransactionId);
		Assert.Equal(PeggedAsset, zeroEffect.PeggedAssetId);
		Assert.Empty(zeroEffect.GetAssetNetChanges());
	}

	[Fact]
	public void EffectAndSnapshotOwnDefensiveReadOnlyCopies()
	{
		LiquidWalletAssetNetChange change =
			LiquidWalletAssetNetChange.Create(IssuedAsset, PeggedAsset, 7);
		var sourceChanges = new List<LiquidWalletAssetNetChange> { change };
		var effect = new LiquidWalletTransactionEffect(Tx('a'), PeggedAsset, null, sourceChanges);
		sourceChanges.Clear();
		IReadOnlyList<LiquidWalletAssetNetChange> firstChanges = effect.GetAssetNetChanges();
		var mutableChanges = Assert.IsAssignableFrom<IList<LiquidWalletAssetNetChange>>(firstChanges);

		Assert.Single(firstChanges);
		Assert.True(mutableChanges.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableChanges.Add(change));
		Assert.NotSame(firstChanges, effect.GetAssetNetChanges());
		Assert.Equal(PeggedAsset, effect.PeggedAssetId);

		var sourceEffects = new List<LiquidWalletTransactionEffect> { effect };
		var snapshot = new LiquidWalletTransactionEffectSnapshot(PeggedAsset, 9, sourceEffects);
		sourceEffects.Clear();
		IReadOnlyList<LiquidWalletTransactionEffect> firstEffects = snapshot.GetEffects();
		var mutableEffects = Assert.IsAssignableFrom<IList<LiquidWalletTransactionEffect>>(firstEffects);

		Assert.Single(firstEffects);
		Assert.True(mutableEffects.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableEffects.Add(effect));
		Assert.NotSame(firstEffects, snapshot.GetEffects());

		LiquidTransactionId deltaId = Tx('f');
		LiquidOwnedOutput deltaOutput = Output(deltaId, 0, IssuedAsset, 1);
		var sourceOutputs = new List<LiquidOwnedOutput> { deltaOutput };
		LiquidWalletTransactionDelta delta = Delta(deltaId, [], sourceOutputs);
		sourceOutputs.Clear();
		IReadOnlyList<LiquidOwnedOutput> firstCreatedOutputs = delta.GetCreatedOutputs();
		var mutableOutputs = Assert.IsAssignableFrom<IList<LiquidOwnedOutput>>(firstCreatedOutputs);
		Assert.Single(firstCreatedOutputs);
		Assert.True(mutableOutputs.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableOutputs.Add(deltaOutput));
		Assert.NotSame(firstCreatedOutputs, delta.GetCreatedOutputs());

		Assert.Throws<ArgumentException>(() => new LiquidWalletTransactionEffect(
			Tx('b'),
			PeggedAsset,
			null,
			[
				LiquidWalletAssetNetChange.Create(Asset(2), PeggedAsset, 1),
				LiquidWalletAssetNetChange.Create(Asset(1), PeggedAsset, 1),
			]));
		LiquidWalletTransactionEffect foreign = new(
			Tx('c'),
			OtherPeggedAsset,
			null,
			[LiquidWalletAssetNetChange.Create(IssuedAsset, OtherPeggedAsset, 1)]);
		Assert.Throws<ArgumentException>(() =>
			new LiquidWalletTransactionEffectSnapshot(PeggedAsset, 0, [foreign]));

		LiquidWalletTransactionEffect foreignEmpty = new(
			Tx('d'),
			OtherPeggedAsset,
			null,
			[]);
		Assert.Throws<ArgumentException>(() =>
			new LiquidWalletTransactionEffectSnapshot(PeggedAsset, 0, [foreignEmpty]));
		Assert.Throws<ArgumentException>(() => new LiquidWalletTransactionEffect(
			Tx('e'),
			PeggedAsset,
			null,
			[LiquidWalletAssetNetChange.Create(IssuedAsset, OtherPeggedAsset, 1)]));
		Assert.Throws<ArgumentNullException>(() => new LiquidWalletTransactionEffect(
			Tx('e'),
			null!,
			null,
			[]));
	}

	[Fact]
	public void StringsAndFailuresDoNotExposeWalletValues()
	{
		LiquidWalletAssetNetChange change =
			LiquidWalletAssetNetChange.Create(IssuedAsset, PeggedAsset, 987_654_321);
		var effect = new LiquidWalletTransactionEffect(
			Tx('a'),
			PeggedAsset,
			LiquidConfirmation.Create(BlockHash, 42),
			[change]);
		var snapshot = new LiquidWalletTransactionEffectSnapshot(PeggedAsset, 1, [effect]);
		long cap = LiquidAssetAmount.MaxPeggedAssetAtomicUnits;
		Exception invalid = Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletAssetNetChange.Create(PeggedAsset, PeggedAsset, cap + 1));
		var foreignEffect = new LiquidWalletTransactionEffect(
			Tx('b'),
			OtherPeggedAsset,
			null,
			[LiquidWalletAssetNetChange.Create(IssuedAsset, OtherPeggedAsset, 1)]);
		Exception foreign = Assert.Throws<ArgumentException>(() =>
			new LiquidWalletTransactionEffectSnapshot(PeggedAsset, 0, [foreignEffect]));

		foreach (string text in new[]
		{
			change.ToString(),
			effect.ToString(),
			snapshot.ToString(),
			invalid.Message,
			foreign.Message,
		})
		{
			Assert.DoesNotContain(Tx('a').CanonicalRpcHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(IssuedAssetHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(PeggedAssetHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(BlockHash, text, StringComparison.Ordinal);
			Assert.DoesNotContain("987654321", text, StringComparison.Ordinal);
			Assert.DoesNotContain(
				cap.ToString(System.Globalization.CultureInfo.InvariantCulture),
				text,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ProjectsMaximumHistoryAndHighCardinalityTransaction()
	{
		const int HistoryCount = 4_096;
		var deltas = new LiquidWalletTransactionDelta[HistoryCount];
		LiquidOwnedOutput? previous = null;
		for (int index = 0; index < deltas.Length; index++)
		{
			LiquidTransactionId transactionId = Tx((uint)index + 1);
			LiquidOwnedOutput created = Output(transactionId, 0, PeggedAsset, 1);
			deltas[index] = Delta(
				transactionId,
				previous is null ? [] : [previous.OutPoint],
				[created]);
			previous = created;
		}
		LiquidWalletState longHistory = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(PeggedAsset, HistoryCount, deltas, []));
		LiquidWalletTransactionEffectSnapshot historyEffects =
			longHistory.GetTransactionEffectSnapshot();

		Assert.Equal((ulong)HistoryCount, historyEffects.Revision);
		Assert.Equal(HistoryCount, historyEffects.GetEffects().Count);
		Assert.Single(historyEffects.GetEffects()[0].GetAssetNetChanges());
		Assert.All(historyEffects.GetEffects().Skip(1), effect =>
			Assert.Empty(effect.GetAssetNetChanges()));

		const int AssetCount = 1_500;
		LiquidTransactionId multiassetId = Tx((uint)HistoryCount + 1);
		LiquidSpendKeyReference key = ExternalKey;
		byte[] scriptPubKey = key.GetScriptPubKey();
		var outputs = new LiquidOwnedOutput[AssetCount];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] = LiquidOwnedOutput.Create(
				LiquidOutPoint.CreateSpendable(multiassetId, (uint)index),
				scriptPubKey,
				LiquidAssetAmount.Create(Asset((uint)index + 1), PeggedAsset, 1),
				key);
		}
		LiquidWalletState highCardinality = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				1,
				[Delta(multiassetId, [], outputs)],
				[]));
		IReadOnlyList<LiquidWalletAssetNetChange> changes = highCardinality
			.GetTransactionEffectSnapshot()
			.GetEffects()[0]
			.GetAssetNetChanges();

		Assert.Equal(AssetCount, changes.Count);
		Assert.Equal(Asset(1), changes[0].AssetId);
		Assert.Equal(Asset(AssetCount), changes[^1].AssetId);

		const int HighOutputCount = 9_279;
		LiquidTransactionId highOutputId = Tx(20_000);
		var sameAssetOutputs = new LiquidOwnedOutput[HighOutputCount];
		for (int index = 0; index < sameAssetOutputs.Length; index++)
		{
			sameAssetOutputs[index] = LiquidOwnedOutput.Create(
				LiquidOutPoint.CreateSpendable(highOutputId, (uint)index),
				scriptPubKey,
				LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, 1),
				key);
		}
		LiquidWalletState highOutput = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				1,
				[Delta(highOutputId, [], sameAssetOutputs)],
				[]));
		IReadOnlyList<LiquidWalletAssetNetChange> highOutputChanges = highOutput
			.GetTransactionEffectSnapshot()
			.GetEffects()[0]
			.GetAssetNetChanges();

		LiquidWalletAssetNetChange highOutputChange = Assert.Single(highOutputChanges);
		Assert.Equal(IssuedAsset, highOutputChange.AssetId);
		Assert.Equal(HighOutputCount, highOutputChange.NetAtomicUnits);
	}

	[Fact]
	public void ProjectionCallGraphKeepsPerOutputWorkBounded()
	{
		MethodInfo projection = typeof(LiquidWalletState).GetMethod(
			nameof(LiquidWalletState.GetTransactionEffectSnapshot),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The transaction-effect projection is unavailable.");
		HashSet<MethodBase> callGraph = GetStateCallGraph(projection);
		MethodInfo balanceAdd = typeof(LiquidAssetBalanceMap).GetMethod(
			nameof(LiquidAssetBalanceMap.Add),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The immutable balance add method is unavailable.");
		MethodInfo balanceSubtract = typeof(LiquidAssetBalanceMap).GetMethod(
			nameof(LiquidAssetBalanceMap.Subtract),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The immutable balance subtract method is unavailable.");

		Assert.DoesNotContain(balanceAdd, callGraph);
		Assert.DoesNotContain(balanceSubtract, callGraph);
		Assert.Contains(callGraph, method =>
			method.DeclaringType is { IsGenericType: true } declaringType &&
			declaringType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
			method.IsConstructor);

		MethodInfo accumulate = typeof(LiquidWalletState).GetMethod(
			"AccumulateTransactionEffectAmount",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The per-output accumulator is unavailable.");
		HashSet<MethodBase> perOutputGraph = GetStateCallGraph(accumulate);
		Assert.DoesNotContain(perOutputGraph, IsCollectionCopyOrMaterializer);

		MethodInfo retainedCreatedOutputs = typeof(LiquidWalletTransactionDelta).GetMethod(
			"GetRetainedCreatedOutputsForStateProjection",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The retained created-output accessor is unavailable.");
		Assert.Equal(typeof(ReadOnlySpan<LiquidOwnedOutput>), retainedCreatedOutputs.ReturnType);
		Assert.Contains(retainedCreatedOutputs, callGraph);
		Assert.DoesNotContain(
			GetCalledMethods(retainedCreatedOutputs),
			IsCollectionCopyOrMaterializer);
		Assert.DoesNotContain(
			GetCalledMethods(retainedCreatedOutputs),
			method => method.DeclaringType == typeof(Array) && method.Name == nameof(Array.Copy));

		MethodInfo ownershipTransfer = typeof(LiquidWalletTransactionEffectSnapshot).GetMethod(
			"TakeOwnershipFromState",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException("The state ownership-transfer factory is unavailable.");
		IReadOnlyList<MethodBase> directProjectionCalls = GetCalledMethods(projection).ToArray();
		Assert.Contains(ownershipTransfer, directProjectionCalls);
		Assert.False(ownershipTransfer.IsPublic);
		Assert.DoesNotContain(directProjectionCalls, method =>
			method.IsConstructor &&
			method.DeclaringType == typeof(LiquidWalletTransactionEffectSnapshot));
		Assert.DoesNotContain(
			typeof(LiquidWalletState).GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
			field => field.FieldType == typeof(LiquidWalletTransactionEffect[]) ||
				field.FieldType == typeof(LiquidWalletTransactionEffectSnapshot));

		int canonicalSortCalls = GetCalledMethods(projection).Count(method =>
			method.DeclaringType == typeof(List<LiquidWalletAssetNetChange>) &&
			method.Name == nameof(List<LiquidWalletAssetNetChange>.Sort));
		Assert.Equal(1, canonicalSortCalls);
	}

	[Fact]
	public void EffectBoundaryHasNoExternalExecutionSurface()
	{
		foreach (Type boundaryType in new[]
		{
			typeof(LiquidWalletAssetNetChange),
			typeof(LiquidWalletTransactionEffect),
			typeof(LiquidWalletTransactionEffectSnapshot),
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
					.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
					.SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)))
				.Concat(boundaryType
					.GetMethods(
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
						BindingFlags.Static | BindingFlags.DeclaredOnly)
					.SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
						.Append(method.ReturnType)));

			Assert.DoesNotContain(signatureTypes, ContainsForbiddenExecutionType);
		}

		Assert.DoesNotContain(
			typeof(LiquidWalletTransactionEffectSnapshot).Assembly.GetReferencedAssemblies(),
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

	private static void AssertEffect(
		LiquidWalletTransactionEffect effect,
		LiquidTransactionId transactionId,
		LiquidConfirmation? confirmation,
		IReadOnlyList<(LiquidAssetId AssetId, long NetAtomicUnits)> expected)
	{
		Assert.Equal(transactionId, effect.TransactionId);
		Assert.Equal(PeggedAsset, effect.PeggedAssetId);
		Assert.Equal(confirmation, effect.Confirmation);
		IReadOnlyList<LiquidWalletAssetNetChange> actual = effect.GetAssetNetChanges();
		Assert.Equal(expected.Count, actual.Count);
		for (int index = 0; index < expected.Count; index++)
		{
			Assert.Equal(expected[index].AssetId, actual[index].AssetId);
			Assert.Equal(PeggedAsset, actual[index].PeggedAssetId);
			Assert.Equal(expected[index].NetAtomicUnits, actual[index].NetAtomicUnits);
			Assert.Equal(expected[index].NetAtomicUnits > 0, actual[index].IsCredit);
			Assert.Equal(expected[index].NetAtomicUnits < 0, actual[index].IsDebit);
		}
	}

	private static void AssertEquivalent(
		LiquidWalletTransactionEffectSnapshot expected,
		LiquidWalletTransactionEffectSnapshot actual)
	{
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletTransactionEffect> expectedEffects = expected.GetEffects();
		IReadOnlyList<LiquidWalletTransactionEffect> actualEffects = actual.GetEffects();
		Assert.Equal(expectedEffects.Count, actualEffects.Count);
		for (int effectIndex = 0; effectIndex < expectedEffects.Count; effectIndex++)
		{
			Assert.Equal(expectedEffects[effectIndex].TransactionId, actualEffects[effectIndex].TransactionId);
			Assert.Equal(expectedEffects[effectIndex].Confirmation, actualEffects[effectIndex].Confirmation);
			Assert.Equal(
				expectedEffects[effectIndex].GetAssetNetChanges(),
				actualEffects[effectIndex].GetAssetNetChanges());
		}
	}

	private static HashSet<MethodBase> GetStateCallGraph(MethodInfo root)
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
			method.Name is nameof(Enumerable.ToArray) or nameof(Enumerable.ToDictionary) or
				nameof(Enumerable.ToHashSet) or nameof(Enumerable.ToList))
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

	private static bool ContainsForbiddenExecutionType(Type type)
	{
		if (type.HasElementType)
		{
			return ContainsForbiddenExecutionType(type.GetElementType()!);
		}
		if (type.IsGenericType && type.GetGenericArguments().Any(ContainsForbiddenExecutionType))
		{
			return true;
		}

		string name = type.FullName ?? type.Name;
		return name.Contains(".Rpc.", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("System.IO", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("PSET", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("PSBT", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Signing", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Broadcast", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("CoinJoin", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Sponsor", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("USDT", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("WalletFacts", StringComparison.OrdinalIgnoreCase);
	}

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
}
