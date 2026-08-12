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
	public void ExactQueryMissesAndValidatesInFailClosedOrder()
	{
		LiquidWalletState empty = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletTransactionEffectSnapshot emptyMiss =
			empty.QueryTransactionEffect(0, Tx('a'));

		Assert.Equal(PeggedAsset, emptyMiss.PeggedAssetId);
		Assert.Equal(0ul, emptyMiss.Revision);
		Assert.Empty(emptyMiss.GetEffects());

		LiquidTransactionId appliedId = Tx('b');
		LiquidWalletState applied = empty.Apply(
			0,
			Delta(appliedId, [], [Output(appliedId, 0, PeggedAsset, 1)]));
		LiquidTransactionId zeroId = Tx('0');
		Exception staleNull = Assert.Throws<InvalidOperationException>(() =>
			applied.QueryTransactionEffect(0, null!));
		Exception staleZero = Assert.Throws<InvalidOperationException>(() =>
			applied.QueryTransactionEffect(0, zeroId));
		Assert.Throws<ArgumentNullException>(() =>
			applied.QueryTransactionEffect(1, null!));
		Exception zero = Assert.Throws<ArgumentException>(() =>
			applied.QueryTransactionEffect(1, zeroId));

		foreach (string text in new[] { staleNull.Message, staleZero.Message, zero.Message })
		{
			Assert.DoesNotContain(appliedId.CanonicalRpcHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(zeroId.CanonicalRpcHex, text, StringComparison.Ordinal);
			Assert.DoesNotContain(PeggedAssetHex, text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ExactQueryReturnsCompleteMultiassetEffectAndDistinguishesZeroNetHit()
	{
		LiquidWalletState state = CreateMultiassetState();
		LiquidTransactionId spendId = Tx('b');
		LiquidWalletTransactionEffectSnapshot queried =
			state.QueryTransactionEffect(state.Revision, spendId);
		LiquidWalletTransactionEffect expected = state
			.GetTransactionEffectSnapshot()
			.GetEffects()
			.Single(effect => effect.TransactionId == spendId);
		LiquidWalletTransactionEffect actual = Assert.Single(queried.GetEffects());

		Assert.Equal(state.Revision, queried.Revision);
		Assert.Equal(PeggedAsset, queried.PeggedAssetId);
		AssertEffectEquivalent(expected, actual);
		Assert.Equal(LiquidConfirmation.Create(BlockHash, 42), actual.Confirmation);
		Assert.Empty(state.QueryTransactionEffect(state.Revision, Tx('f')).GetEffects());

		LiquidTransactionId initialId = Tx('c');
		LiquidOwnedOutput initial = Output(initialId, 0, IssuedAsset, 7);
		LiquidTransactionId zeroId = Tx('d');
		LiquidOwnedOutput replacement = Output(zeroId, 0, IssuedAsset, 7);
		LiquidWalletState zeroNet = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(initialId, [], [initial]))
			.Apply(1, Delta(zeroId, [initial.OutPoint], [replacement]));
		LiquidWalletTransactionEffect zeroEffect = Assert.Single(
			zeroNet.QueryTransactionEffect(2, zeroId).GetEffects());

		Assert.Equal(zeroId, zeroEffect.TransactionId);
		Assert.Empty(zeroEffect.GetAssetNetChanges());
		Assert.Empty(zeroNet.QueryTransactionEffect(2, Tx('e')).GetEffects());
	}

	[Fact]
	public void ExactQueryTracksImmutableStateTransitionsAndRollback()
	{
		LiquidTransactionId receiveId = Tx('a');
		LiquidOwnedOutput received = Output(receiveId, 0, PeggedAsset, 10);
		LiquidTransactionId spendId = Tx('b');
		LiquidOwnedOutput change = Output(spendId, 0, PeggedAsset, 9);
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, 42);
		LiquidWalletState empty = LiquidWalletState.Empty(PeggedAsset);
		LiquidWalletTransactionEffectSnapshot receiveMissBeforeApply =
			empty.QueryTransactionEffect(0, receiveId);
		LiquidWalletState receivedState = empty.Apply(0, Delta(receiveId, [], [received]));
		LiquidWalletTransactionEffectSnapshot receiveAfterApply =
			receivedState.QueryTransactionEffect(1, receiveId);
		LiquidWalletTransactionEffectSnapshot spendMissBeforeApply =
			receivedState.QueryTransactionEffect(1, spendId);
		LiquidWalletState applied = receivedState.Apply(
			1,
			Delta(spendId, [received.OutPoint], [change]));
		LiquidWalletTransactionEffectSnapshot absent =
			applied.QueryTransactionEffect(2, spendId);
		LiquidWalletState confirmed = applied.Confirm(2, spendId, confirmation);
		LiquidWalletTransactionEffectSnapshot attached =
			confirmed.QueryTransactionEffect(3, spendId);
		LiquidWalletState unconfirmed = confirmed.Unconfirm(3, spendId, confirmation);
		LiquidWalletTransactionEffectSnapshot detached =
			unconfirmed.QueryTransactionEffect(4, spendId);
		LiquidWalletState rolledBack = unconfirmed.RollbackLast(4, spendId);

		Assert.Equal(0ul, receiveMissBeforeApply.Revision);
		Assert.Empty(receiveMissBeforeApply.GetEffects());
		Assert.Equal(1ul, receiveAfterApply.Revision);
		AssertEffect(
			Assert.Single(receiveAfterApply.GetEffects()),
			receiveId,
			null,
			[(PeggedAsset, 10L)]);
		Assert.Equal(1ul, spendMissBeforeApply.Revision);
		Assert.Empty(spendMissBeforeApply.GetEffects());
		Assert.Equal(2ul, absent.Revision);
		Assert.Equal(3ul, attached.Revision);
		Assert.Equal(4ul, detached.Revision);
		Assert.Null(Assert.Single(absent.GetEffects()).Confirmation);
		Assert.Equal(confirmation, Assert.Single(attached.GetEffects()).Confirmation);
		Assert.Null(Assert.Single(detached.GetEffects()).Confirmation);
		AssertEffectEquivalentExceptConfirmation(
			Assert.Single(absent.GetEffects()),
			Assert.Single(attached.GetEffects()));
		AssertEffectEquivalentExceptConfirmation(
			Assert.Single(attached.GetEffects()),
			Assert.Single(detached.GetEffects()));
		Assert.Empty(rolledBack.QueryTransactionEffect(5, spendId).GetEffects());
		Assert.Single(rolledBack.QueryTransactionEffect(5, receiveId).GetEffects());

		Assert.Empty(receiveMissBeforeApply.GetEffects());
		AssertEffect(
			Assert.Single(receiveAfterApply.GetEffects()),
			receiveId,
			null,
			[(PeggedAsset, 10L)]);
		Assert.Empty(spendMissBeforeApply.GetEffects());
		Assert.Null(Assert.Single(absent.GetEffects()).Confirmation);
		Assert.Equal(confirmation, Assert.Single(attached.GetEffects()).Confirmation);
		Assert.Null(Assert.Single(detached.GetEffects()).Confirmation);
	}

	[Fact]
	public void ReplayAndProtectedReplayPreserveExactQueryResults()
	{
		LiquidWalletState initial = CreateMultiassetState();
		LiquidTransactionId zeroNetId = Tx('c');
		LiquidOwnedOutput issuedChange = Output(Tx('b'), 0, IssuedAsset, 150);
		LiquidWalletState state = initial.Apply(
			initial.Revision,
			Delta(
				zeroNetId,
				[issuedChange.OutPoint],
				[Output(zeroNetId, 0, IssuedAsset, 150)]));
		LiquidWalletState replayed =
			LiquidWalletState.RestoreReplaySnapshot(state.ExportReplaySnapshot());

		foreach (LiquidTransactionId transactionId in new[] { Tx('a'), Tx('b'), zeroNetId, Tx('f') })
		{
			AssertEquivalent(
				state.QueryTransactionEffect(state.Revision, transactionId),
				replayed.QueryTransactionEffect(replayed.Revision, transactionId));
		}
		Assert.Empty(Assert.Single(
			replayed.QueryTransactionEffect(replayed.Revision, zeroNetId).GetEffects())
			.GetAssetNetChanges());

		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		byte[]? envelope = null;
		try
		{
			LiquidWalletReplayProtectedPayload protectedPayload =
				LiquidWalletReplayProtectedPayload.Seal(state.ExportReplaySnapshot(), 23, key, context);
			envelope = protectedPayload.GetBytes();
			LiquidWalletState restored = LiquidWalletState.RestoreReplaySnapshot(
				protectedPayload.Open(key, context).Snapshot);

			foreach (LiquidTransactionId transactionId in new[] { Tx('a'), Tx('b'), zeroNetId, Tx('f') })
			{
				AssertEquivalent(
					state.QueryTransactionEffect(state.Revision, transactionId),
					restored.QueryTransactionEffect(restored.Revision, transactionId));
			}
			Assert.Empty(Assert.Single(
				restored.QueryTransactionEffect(restored.Revision, zeroNetId).GetEffects())
				.GetAssetNetChanges());
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
	public void ExactQueryNeverMutatesSourceStateOnSuccessOrFailure()
	{
		LiquidTransactionId initialId = Tx('a');
		LiquidOwnedOutput initialIssued = Output(initialId, 0, IssuedAsset, 7);
		LiquidOwnedOutput initialPegged = Output(initialId, 1, PeggedAsset, 11);
		LiquidTransactionId zeroNetId = Tx('b');
		LiquidOwnedOutput replacementPegged = Output(zeroNetId, 0, PeggedAsset, 11);
		LiquidOwnedOutput replacementIssued = Output(zeroNetId, 1, IssuedAsset, 7);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(initialId, [], [initialIssued, initialPegged]))
			.Apply(1, Delta(
				zeroNetId,
				[initialPegged.OutPoint, initialIssued.OutPoint],
				[replacementPegged, replacementIssued]))
			.Confirm(2, zeroNetId, LiquidConfirmation.Create(BlockHash, 42));

		AssertStateUnchanged(state, () =>
			Assert.Single(state.QueryTransactionEffect(3, initialId).GetEffects()));
		AssertStateUnchanged(state, () =>
			Assert.Single(state.QueryTransactionEffect(3, zeroNetId).GetEffects()));
		AssertStateUnchanged(state, () =>
			Assert.Empty(state.QueryTransactionEffect(3, Tx('f')).GetEffects()));
		AssertStateUnchanged(state, () =>
			Assert.Throws<InvalidOperationException>(() =>
				state.QueryTransactionEffect(2, initialId)));
		AssertStateUnchanged(state, () =>
			Assert.Throws<ArgumentNullException>(() =>
				state.QueryTransactionEffect(3, null!)));
		AssertStateUnchanged(state, () =>
			Assert.Throws<ArgumentException>(() =>
				state.QueryTransactionEffect(3, Tx('0'))));
	}

	[Fact]
	public void ExactQueryResultsAndFailuresRedactWalletValuesAndOwnTheirArray()
	{
		const long SensitiveAmount = 987_654_321;
		const uint SensitiveHeight = 876_543_210;
		LiquidTransactionId transactionId = Tx('a');
		LiquidSpendKeyReference key = ExternalKey;
		byte[] scriptPubKey = key.GetScriptPubKey();
		LiquidOwnedOutput output = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, 19),
			scriptPubKey,
			LiquidAssetAmount.Create(IssuedAsset, PeggedAsset, SensitiveAmount),
			key);
		LiquidConfirmation confirmation = LiquidConfirmation.Create(BlockHash, SensitiveHeight);
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(transactionId, [], [output]))
			.Confirm(1, transactionId, confirmation);
		LiquidWalletTransactionEffectSnapshot queried =
			state.QueryTransactionEffect(2, transactionId);
		IReadOnlyList<LiquidWalletTransactionEffect> first = queried.GetEffects();
		var mutable = Assert.IsAssignableFrom<IList<LiquidWalletTransactionEffect>>(first);
		LiquidWalletTransactionEffect effect = Assert.Single(first);
		LiquidWalletAssetNetChange change = Assert.Single(effect.GetAssetNetChanges());

		Assert.True(mutable.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutable.Clear());
		Assert.NotSame(first, queried.GetEffects());
		AssertEffectEquivalent(
			effect,
			Assert.Single(state.QueryTransactionEffect(2, transactionId).GetEffects()));

		Exception stale = Assert.Throws<InvalidOperationException>(() =>
			state.QueryTransactionEffect(1, transactionId));
		Exception nullId = Assert.Throws<ArgumentNullException>(() =>
			state.QueryTransactionEffect(2, null!));
		Exception zero = Assert.Throws<ArgumentException>(() =>
			state.QueryTransactionEffect(2, Tx('0')));
		string[] canaries =
		[
			transactionId.CanonicalRpcHex,
			IssuedAssetHex,
			PeggedAssetHex,
			SensitiveAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
			SensitiveHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
			BlockHash,
			Convert.ToHexString(scriptPubKey),
			PublicKeyHex,
			"19",
		];
		foreach (string text in new[]
		{
			queried.ToString(),
			effect.ToString(),
			change.ToString(),
			stale.Message,
			nullId.Message,
			zero.Message,
		})
		{
			foreach (string canary in canaries)
			{
				Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase);
			}
		}
	}

	[Fact]
	public void ExactQueryUsesActualHistoryBeyondProtectedCodecCapacity()
	{
		const int ProtectedCodecCapacity = 4_096;
		const int LiveHistoryCount = ProtectedCodecCapacity + 1;
		var deltas = new LiquidWalletTransactionDelta[LiveHistoryCount];
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

		LiquidWalletState replayEncodable = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(
				PeggedAsset,
				ProtectedCodecCapacity,
				deltas.Take(ProtectedCodecCapacity),
				[]));
		LiquidWalletState live = LiquidWalletState.RestoreReplaySnapshot(
			LiquidWalletReplaySnapshot.Create(PeggedAsset, LiveHistoryCount, deltas, []));

		foreach ((LiquidWalletState State, int Count) candidate in new[]
		{
			(replayEncodable, ProtectedCodecCapacity),
			(live, LiveHistoryCount),
		})
		{
			foreach (uint transactionNumber in new[]
			{
				1u,
				(uint)(candidate.Count / 2),
				(uint)candidate.Count,
			})
			{
				LiquidWalletTransactionEffect effect = Assert.Single(candidate.State
					.QueryTransactionEffect((ulong)candidate.Count, Tx(transactionNumber))
					.GetEffects());
				Assert.Equal(Tx(transactionNumber), effect.TransactionId);
			}

			Assert.Empty(candidate.State
				.QueryTransactionEffect((ulong)candidate.Count, Tx(20_000))
				.GetEffects());
		}
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
		AssertEffectEquivalent(
			boundaryEffects[0],
			Assert.Single(boundary.QueryTransactionEffect(2, receiveId).GetEffects()));
		AssertEffectEquivalent(
			boundaryEffects[1],
			Assert.Single(boundary.QueryTransactionEffect(2, spendId).GetEffects()));

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
		AssertEffectEquivalent(
			zeroEffect,
			Assert.Single(exactMaximumZero
				.QueryTransactionEffect(2, exactMaximumZeroId)
				.GetEffects()));
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
		Assert.Equal(
			changes,
			Assert.Single(highCardinality
				.QueryTransactionEffect(1, multiassetId)
				.GetEffects())
				.GetAssetNetChanges());

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
			BindingFlags.NonPublic | BindingFlags.Static) ??
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

		MethodInfo builder = typeof(LiquidWalletState).GetMethod(
			"CreateTransactionEffect",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException("The common transaction-effect builder is unavailable.");
		Assert.Equal(1, directProjectionCalls.Count(method => method == builder));
		int canonicalSortCalls = GetCalledMethods(builder).Count(method =>
			method.DeclaringType == typeof(List<LiquidWalletAssetNetChange>) &&
			method.Name == nameof(List<LiquidWalletAssetNetChange>.Sort));
		Assert.Equal(1, canonicalSortCalls);
	}

	[Fact]
	public void ExactQueryStructurePreservesFullScanAndReadOnlyBuilderBoundary()
	{
		MethodInfo query = typeof(LiquidWalletState).GetMethod(
			nameof(LiquidWalletState.QueryTransactionEffect),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The exact transaction-effect query is unavailable.");
		MethodInfo projection = typeof(LiquidWalletState).GetMethod(
			nameof(LiquidWalletState.GetTransactionEffectSnapshot),
			BindingFlags.Public | BindingFlags.Instance) ??
			throw new InvalidOperationException("The full transaction-effect projection is unavailable.");
		MethodInfo search = typeof(LiquidWalletState).GetMethod(
			"FindAppliedDeltaForTransactionEffectQuery",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The exact transaction-effect search is unavailable.");
		MethodInfo builder = typeof(LiquidWalletState).GetMethod(
			"CreateTransactionEffect",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException("The common transaction-effect builder is unavailable.");
		MethodInfo accumulate = typeof(LiquidWalletState).GetMethod(
			"AccumulateTransactionEffectAmount",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException("The per-output accumulator is unavailable.");
		MethodInfo ownershipTransfer = typeof(LiquidWalletTransactionEffectSnapshot).GetMethod(
			"TakeOwnershipFromState",
			BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException("The state ownership-transfer factory is unavailable.");
		MethodInfo ensureRevision = typeof(LiquidWalletState).GetMethod(
			"EnsureRevision",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The state revision guard is unavailable.");
		MethodInfo retainedCreatedOutputs = typeof(LiquidWalletTransactionDelta).GetMethod(
			"GetRetainedCreatedOutputsForStateProjection",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The retained created-output accessor is unavailable.");
		Type totalsType = GetTransactionEffectTotalsType();
		Type totalsDictionaryType = typeof(Dictionary<,>).MakeGenericType(
			typeof(string),
			totalsType);

		Assert.True(builder.IsStatic);
		Assert.Equal(
			[
				typeof(LiquidTransactionId),
				typeof(ReadOnlySpan<LiquidOwnedOutput>),
				typeof(ReadOnlySpan<LiquidOwnedOutput>),
				typeof(LiquidAssetId),
				typeof(LiquidConfirmation),
			],
			builder.GetParameters().Select(parameter => parameter.ParameterType));
		Assert.True(accumulate.IsStatic);
		Assert.Equal(
			[
				totalsDictionaryType,
				typeof(LiquidAssetAmount),
				typeof(LiquidAssetId),
				typeof(bool),
			],
			accumulate.GetParameters().Select(parameter => parameter.ParameterType));

		IReadOnlyList<(int Offset, MethodBase Method)> queryCallSites =
			GetCalledMethodInstructions(query).ToArray();
		IReadOnlyList<MethodBase> queryCalls = queryCallSites.Select(site => site.Method).ToArray();
		Assert.All(queryCallSites, site => Assert.True(
			IsPermittedExactTransactionEffectQueryCall(
				site.Method,
				ensureRevision,
				search,
				builder,
				retainedCreatedOutputs,
				ownershipTransfer),
			$"Unexpected exact-query call: {site.Method.DeclaringType}.{site.Method.Name}."));
		Assert.Equal(17, queryCallSites.Count);
		int searchOffset = Assert.Single(queryCallSites, site => site.Method == search).Offset;
		int builderOffset = Assert.Single(queryCallSites, site => site.Method == builder).Offset;
		int ownershipOffset = Assert.Single(queryCallSites, site => site.Method == ownershipTransfer).Offset;
		int confirmationOffset = Assert.Single(queryCallSites, site =>
			site.Method.DeclaringType == typeof(Dictionary<LiquidTransactionId, LiquidConfirmation>) &&
			site.Method.Name == nameof(Dictionary<LiquidTransactionId, LiquidConfirmation>.TryGetValue)).Offset;
		Assert.DoesNotContain(projection, queryCalls);
		Assert.Equal(1, CountOpCode(query, OpCodes.Newarr));
		int resultStoreOffset = Assert.Single(
			GetIlInstructions(query),
			instruction => instruction.OpCode == OpCodes.Stelem_Ref).Offset;
		Assert.True(searchOffset < confirmationOffset);
		Assert.True(confirmationOffset < builderOffset);
		Assert.True(builderOffset < resultStoreOffset);
		Assert.True(resultStoreOffset < ownershipOffset);
		(int Offset, int Target, OpCode OpCode) hitGuard = Assert.Single(
			GetBranchEdges(query),
			branch => branch.Offset > searchOffset &&
				branch.Offset < confirmationOffset &&
				branch.Target > resultStoreOffset &&
				branch.Target < ownershipOffset);
		Assert.Equal(FlowControl.Cond_Branch, hitGuard.OpCode.FlowControl);
		Assert.All(
			queryCallSites.Where(site =>
				site.Method == builder ||
				(site.Method.DeclaringType == typeof(Dictionary<LiquidTransactionId, LiquidConfirmation>) &&
				 site.Method.Name == nameof(Dictionary<LiquidTransactionId, LiquidConfirmation>.TryGetValue))),
			site => Assert.InRange(site.Offset, hitGuard.Offset + 1, hitGuard.Target - 1));

		IReadOnlyList<(int Offset, int Target, OpCode OpCode)> searchBranches =
			GetBranchEdges(search).ToArray();
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>
			searchInstructions = GetIlInstructions(search)
				.Where(instruction => instruction.OpCode != OpCodes.Nop)
				.ToArray();
		var backwardBranches = searchBranches
			.Where(branch => branch.Target < branch.Offset)
			.ToArray();
		Assert.Single(backwardBranches);
		IReadOnlyList<(int Offset, MethodBase Method)> searchCallSites =
			GetCalledMethodInstructions(search).ToArray();
		Assert.All(searchCallSites, site => Assert.True(
			IsPermittedTransactionEffectSearchCall(site.Method),
			$"Unexpected exact-query search call: {site.Method.DeclaringType}.{site.Method.Name}."));
		Assert.Equal(5, searchCallSites.Count);
		(int Offset, MethodBase Method) historyItem = Assert.Single(searchCallSites, site =>
			site.Method.DeclaringType is { IsGenericType: true } declaringType &&
			declaringType.GetGenericTypeDefinition() == typeof(List<>) &&
			site.Method.Name == "get_Item");
		(int Offset, MethodBase Method) historyCount = Assert.Single(searchCallSites, site =>
			site.Method.DeclaringType is { IsGenericType: true } declaringType &&
			declaringType.GetGenericTypeDefinition() == typeof(List<>) &&
			site.Method.Name == "get_Count");
		Assert.InRange(historyItem.Offset, backwardBranches[0].Target, historyCount.Offset - 1);
		Assert.InRange(historyCount.Offset, historyItem.Offset + 1, backwardBranches[0].Offset - 1);
		LocalVariableInfo historyIndex = Assert.Single(
			search.GetMethodBody()?.LocalVariables ?? [],
			variable => variable.LocalType == typeof(int));
		var indexStores = searchInstructions
			.Select((instruction, position) => (Instruction: instruction, Position: position))
			.Where(candidate =>
				GetStoredLocalIndex(search, candidate.Instruction) == historyIndex.LocalIndex)
			.ToArray();
		Assert.Equal(2, indexStores.Length);
		Assert.True(indexStores[0].Position >= 1);
		Assert.Equal(OpCodes.Ldc_I4_0, searchInstructions[indexStores[0].Position - 1].OpCode);
		Assert.True(indexStores[1].Position >= 3);
		Assert.Equal(
			historyIndex.LocalIndex,
			GetLoadedLocalIndex(search, searchInstructions[indexStores[1].Position - 3]));
		Assert.Equal(OpCodes.Ldc_I4_1, searchInstructions[indexStores[1].Position - 2].OpCode);
		Assert.Equal(OpCodes.Add, searchInstructions[indexStores[1].Position - 1].OpCode);
		int incrementStartOffset = searchInstructions[indexStores[1].Position - 3].Offset;
		Assert.InRange(incrementStartOffset, historyItem.Offset + 1, historyCount.Offset - 1);
		(int Offset, int Target, OpCode OpCode) matchRejoin = Assert.Single(
			searchBranches,
			branch => branch.Offset > historyItem.Offset &&
				branch.Offset < incrementStartOffset);
		Assert.Equal(FlowControl.Cond_Branch, matchRejoin.OpCode.FlowControl);
		Assert.Equal(
			incrementStartOffset,
			searchInstructions.First(instruction => instruction.Offset >= matchRejoin.Target).Offset);
		(int Offset, int Target, OpCode OpCode) loopEntry = Assert.Single(
			searchBranches,
			branch => branch.Offset < backwardBranches[0].Target);
		Assert.Equal(FlowControl.Branch, loopEntry.OpCode.FlowControl);
		Assert.InRange(loopEntry.Target, incrementStartOffset + 1, historyCount.Offset);
		Assert.All(
			searchBranches.Where(branch =>
				branch != backwardBranches[0] &&
				branch != matchRejoin &&
				branch != loopEntry),
			branch =>
			{
				Assert.Equal(FlowControl.Branch, branch.OpCode.FlowControl);
				Assert.True(branch.Offset > backwardBranches[0].Offset);
				Assert.True(branch.Target > branch.Offset);
			});
		Assert.Equal(1, CountOpCode(search, OpCodes.Ret));
		Assert.All(
			GetIlInstructions(search).Where(instruction => instruction.OpCode == OpCodes.Ret),
			instruction => Assert.True(instruction.Offset > backwardBranches[0].Offset));
		Assert.DoesNotContain(searchInstructions, instruction =>
			instruction.OpCode is var opCode &&
			(opCode == OpCodes.Switch || opCode == OpCodes.Leave ||
			 opCode == OpCodes.Leave_S || opCode == OpCodes.Throw ||
			 opCode == OpCodes.Rethrow));
		Assert.Empty(search.GetMethodBody()?.ExceptionHandlingClauses ?? []);
		Assert.DoesNotContain(
			search.GetMethodBody()?.LocalVariables ?? [],
			variable => variable.LocalType.IsByRef || variable.LocalType.IsPointer);
		Assert.DoesNotContain(searchInstructions, instruction =>
			IsForbiddenBuilderOpcode(instruction.OpCode) ||
			instruction.OpCode == OpCodes.Ldloca ||
			instruction.OpCode == OpCodes.Ldloca_S ||
			instruction.OpCode == OpCodes.Ldarga ||
			instruction.OpCode == OpCodes.Ldarga_S);
		Assert.DoesNotContain(GetCalledMethods(search), method =>
			method.Name is "Find" or "FindIndex" or "First" or "FirstOrDefault" or
				"Single" or "SingleOrDefault" or "Contains" or "IndexOf");
		Assert.Empty(query.GetMethodBody()?.ExceptionHandlingClauses ?? []);
		Assert.Empty(GetStoredFields(query));
		Assert.Empty(GetStoredFields(search));
		Assert.DoesNotContain(
			GetIlInstructions(query),
			instruction => instruction.OpCode != OpCodes.Stelem_Ref &&
				IsForbiddenBuilderOpcode(instruction.OpCode));
		Assert.DoesNotContain(
			query.GetMethodBody()?.LocalVariables ?? [],
			variable => variable.LocalType == typeof(object) ||
				variable.LocalType.IsInterface ||
				typeof(Delegate).IsAssignableFrom(variable.LocalType) ||
				ContainsWritableOwnedOutputStorage(variable.LocalType));
		LocalVariableInfo resultArrayLocal = Assert.Single(
			query.GetMethodBody()?.LocalVariables ?? [],
			variable => variable.LocalType == typeof(LiquidWalletTransactionEffect[]));
		IReadOnlyList<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>
			queryInstructions = GetIlInstructions(query)
				.Where(instruction => instruction.OpCode != OpCodes.Nop)
				.ToArray();
		var resultArrayStores = queryInstructions
			.Select((instruction, position) => (Instruction: instruction, Position: position))
			.Where(candidate =>
				GetStoredLocalIndex(query, candidate.Instruction) == resultArrayLocal.LocalIndex)
			.ToArray();
		var resultArrayLoads = queryInstructions
			.Where(instruction =>
				GetLoadedLocalIndex(query, instruction) == resultArrayLocal.LocalIndex)
			.ToArray();
		Assert.Single(resultArrayStores);
		Assert.True(resultArrayStores[0].Position >= 1);
		Assert.Equal(OpCodes.Newarr, queryInstructions[resultArrayStores[0].Position - 1].OpCode);
		Assert.Equal(2, resultArrayLoads.Length);
		Assert.InRange(resultArrayLoads[0].Offset, hitGuard.Offset + 1, resultStoreOffset - 1);
		Assert.InRange(resultArrayLoads[1].Offset, resultStoreOffset + 1, ownershipOffset - 1);
		Assert.DoesNotContain(
			GetAllNestedTypes(typeof(LiquidWalletState)).SelectMany(type => type.GetFields(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly)),
			field => field.FieldType == typeof(LiquidWalletTransactionEffect[]) ||
				field.FieldType == typeof(LiquidWalletTransactionEffectSnapshot));

		FieldInfo historyField = GetStateField("_history");
		FieldInfo confirmationsField = GetStateField("_confirmations");
		FieldInfo appliedIdsField = GetStateField("_appliedTransactionIds");
		FieldInfo balancesField = GetStateField("_balances");
		FieldInfo unspentField = GetStateField("_unspentOutputs");
		FieldInfo knownField = GetStateField("_knownOutputs");
		Assert.Equal(
			new HashSet<FieldInfo> { historyField },
			GetReferencedFields(search)
				.Where(field => field.DeclaringType == typeof(LiquidWalletState))
				.ToHashSet());
		Assert.Equal(
			new HashSet<FieldInfo> { confirmationsField },
			GetReferencedFields(query)
				.Where(field => field.DeclaringType == typeof(LiquidWalletState))
				.ToHashSet());
		Assert.DoesNotContain(
			new[] { historyField, appliedIdsField, balancesField, unspentField, knownField },
			field => GetReferencedFields(query).Contains(field));

		HashSet<MethodBase> queryGraph = GetStateCallGraph(query);
		HashSet<FieldInfo> storedFields = queryGraph
			.SelectMany(GetStoredFields)
			.Where(field =>
				field.DeclaringType == typeof(LiquidWalletState) ||
				field.DeclaringType == totalsType ||
				field.DeclaringType?.Name is "AppliedDelta" or "ReplayBuilder")
			.ToHashSet();
		Assert.All(storedFields, field => Assert.Equal(totalsType, field.DeclaringType));
		Assert.Equal(
			new HashSet<string>(StringComparer.Ordinal)
			{
				"<AssetId>k__BackingField",
				"<SpentAtomicUnits>k__BackingField",
				"<CreatedAtomicUnits>k__BackingField",
			},
			storedFields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal));

		Assert.DoesNotContain(
			GetReferencedFields(builder),
			field => field.DeclaringType == typeof(LiquidWalletState));
		Assert.Equal(0, CountOpCode(builder, OpCodes.Stelem_Ref));
		Assert.Equal(0, CountOpCode(search, OpCodes.Stelem_Ref));

		Assert.DoesNotContain(
			typeof(LiquidWalletState).GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
			field => ContainsType(field.FieldType, totalsType));

		Type valuesType = totalsDictionaryType.GetProperty(
			"Values",
			BindingFlags.Public | BindingFlags.Instance)?.PropertyType ??
			throw new InvalidOperationException("The accumulator dictionary values type is unavailable.");
		Type enumeratorType = valuesType.GetMethod(
			nameof(IEnumerable<object>.GetEnumerator),
			BindingFlags.Public | BindingFlags.Instance,
			Type.EmptyTypes)?.ReturnType ??
			throw new InvalidOperationException("The accumulator dictionary enumerator type is unavailable.");
		IReadOnlyList<MethodBase> builderCalls = GetCalledMethods(builder).ToArray();
		IReadOnlyList<MethodBase> accumulatorCalls = GetCalledMethods(accumulate).ToArray();
		IReadOnlyList<MethodBase> builderCarrierCalls = builderCalls
			.Where(method => method.DeclaringType is not null &&
				ContainsType(method.DeclaringType, totalsType))
			.ToArray();
		Assert.All(builderCarrierCalls, method => Assert.True(
			(method.DeclaringType == totalsDictionaryType &&
				(method.IsConstructor || method.Name == "get_Values")) ||
			(method.DeclaringType == valuesType && method.Name == "GetEnumerator") ||
			(method.DeclaringType == enumeratorType &&
				method.Name is "get_Current" or "MoveNext" or "Dispose") ||
			(method.DeclaringType == totalsType &&
				method.Name is "get_AssetId" or "get_SpentAtomicUnits" or
					"get_CreatedAtomicUnits"),
			$"Unexpected accumulator carrier call: {method.DeclaringType}.{method.Name}."));
		Assert.Equal(1, builderCarrierCalls.Count(method =>
			method.DeclaringType == totalsDictionaryType && method.IsConstructor));
		Assert.Equal(1, builderCarrierCalls.Count(method =>
			method.DeclaringType == totalsDictionaryType && method.Name == "get_Values"));
		Assert.Equal(1, builderCarrierCalls.Count(method =>
			method.DeclaringType == valuesType && method.Name == "GetEnumerator"));
		Assert.Equal(1, builderCarrierCalls.Count(method =>
			method.DeclaringType == enumeratorType && method.Name == "get_Current"));
		Assert.Equal(1, builderCarrierCalls.Count(method =>
			method.DeclaringType == enumeratorType && method.Name == "MoveNext"));
		Assert.InRange(builderCarrierCalls.Count(method =>
			method.DeclaringType == enumeratorType && method.Name == "Dispose"), 0, 1);
		Assert.Equal(1, builderCarrierCalls.Count(method =>
			method.DeclaringType == totalsType && method.Name == "get_AssetId"));
		Assert.Equal(3, builderCarrierCalls.Count(method =>
			method.DeclaringType == totalsType && method.Name == "get_SpentAtomicUnits"));
		Assert.Equal(3, builderCarrierCalls.Count(method =>
			method.DeclaringType == totalsType && method.Name == "get_CreatedAtomicUnits"));
		Assert.Equal(2, builderCalls.Count(method => method == accumulate));

		IReadOnlyList<MethodBase> accumulatorCarrierCalls = accumulatorCalls
			.Where(method =>
				method.DeclaringType == totalsType ||
				method.DeclaringType == totalsDictionaryType)
			.ToArray();
		Assert.All(accumulatorCarrierCalls, method => Assert.True(
			(method.DeclaringType == totalsDictionaryType &&
				method.Name is nameof(Dictionary<string, object>.TryGetValue) or
					nameof(Dictionary<string, object>.Add)) ||
			(method.DeclaringType == totalsType &&
				(method.IsConstructor || method.Name is "AddSpent" or "AddCreated")),
			$"Unexpected accumulator call: {method.DeclaringType}.{method.Name}."));
		Assert.Equal(1, accumulatorCarrierCalls.Count(method =>
			method.DeclaringType == totalsDictionaryType &&
			method.Name == nameof(Dictionary<string, object>.TryGetValue)));
		Assert.Equal(1, accumulatorCarrierCalls.Count(method =>
			method.DeclaringType == totalsDictionaryType &&
			method.Name == nameof(Dictionary<string, object>.Add)));
		Assert.Equal(1, accumulatorCarrierCalls.Count(method =>
			method.DeclaringType == totalsType && method.IsConstructor));
		Assert.Equal(1, accumulatorCarrierCalls.Count(method =>
			method.DeclaringType == totalsType && method.Name == "AddSpent"));
		Assert.Equal(1, accumulatorCarrierCalls.Count(method =>
			method.DeclaringType == totalsType && method.Name == "AddCreated"));

		IReadOnlyList<Type> allowedBuilderCarrierLocals =
			[totalsDictionaryType, totalsType, valuesType, enumeratorType];
		Assert.All(
			builder.GetMethodBody()?.LocalVariables
				.Where(variable => ContainsType(variable.LocalType, totalsType)) ?? [],
			variable => Assert.Contains(variable.LocalType, allowedBuilderCarrierLocals));
		Assert.Equal(1, builder.GetMethodBody()?.LocalVariables.Count(variable =>
			variable.LocalType == totalsDictionaryType));
		Assert.All(
			accumulate.GetMethodBody()?.LocalVariables
				.Where(variable => ContainsType(variable.LocalType, totalsType)) ?? [],
			variable => Assert.Equal(totalsType, variable.LocalType));

		ConstructorInfo totalsConstructor = Assert.Single(totalsType.GetConstructors(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
		PropertyInfo assetProperty = totalsType.GetProperty("AssetId") ??
			throw new InvalidOperationException("The accumulator asset property is unavailable.");
		PropertyInfo spentProperty = totalsType.GetProperty("SpentAtomicUnits") ??
			throw new InvalidOperationException("The accumulator spent property is unavailable.");
		PropertyInfo createdProperty = totalsType.GetProperty("CreatedAtomicUnits") ??
			throw new InvalidOperationException("The accumulator created property is unavailable.");
		FieldInfo assetField = totalsType.GetField(
			"<AssetId>k__BackingField",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The accumulator asset field is unavailable.");
		FieldInfo spentField = totalsType.GetField(
			"<SpentAtomicUnits>k__BackingField",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The accumulator spent field is unavailable.");
		FieldInfo createdField = totalsType.GetField(
			"<CreatedAtomicUnits>k__BackingField",
			BindingFlags.NonPublic | BindingFlags.Instance) ??
			throw new InvalidOperationException("The accumulator created field is unavailable.");
		MethodInfo spentSetter = spentProperty.GetSetMethod(nonPublic: true) ??
			throw new InvalidOperationException("The accumulator spent setter is unavailable.");
		MethodInfo createdSetter = createdProperty.GetSetMethod(nonPublic: true) ??
			throw new InvalidOperationException("The accumulator created setter is unavailable.");
		Assert.Equal([assetField], GetStoredFields(totalsConstructor));
		Assert.Null(assetProperty.GetSetMethod(nonPublic: true));
		Assert.Equal([spentField], GetStoredFields(spentSetter));
		Assert.Equal([createdField], GetStoredFields(createdSetter));
		IReadOnlyList<MethodBase> totalsWriteOwners = totalsType
			.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly)
			.Cast<MethodBase>()
			.Append(totalsConstructor)
			.Where(method => GetStoredFields(method).Any())
			.ToArray();
		Assert.Equal(3, totalsWriteOwners.Count);
		Assert.Equal(
			new HashSet<FieldInfo> { assetField, spentField, createdField },
			totalsWriteOwners.SelectMany(GetStoredFields).ToHashSet());

		HashSet<MethodBase> closedBuilderGraph = GetStateCallGraph(builder)
			.Where(IsWalletStateImplementationMethod)
			.ToHashSet();
		Assert.Contains(builder, closedBuilderGraph);
		Assert.Contains(accumulate, closedBuilderGraph);
		Assert.All(closedBuilderGraph, method => Assert.True(
			method == builder ||
			method == accumulate ||
			IsPermittedTransactionEffectTotalsMethod(method, totalsType) ||
			IsCanonicalEffectComparisonMethod(method),
			$"Unexpected project helper in the closed builder graph: {method.DeclaringType}.{method.Name}."));
		foreach (MethodBase method in closedBuilderGraph)
		{
			Assert.All(GetCalledMethods(method), called => Assert.True(
				closedBuilderGraph.Contains(called) ||
				IsPermittedEffectBuilderExternalCall(
					called,
					totalsDictionaryType,
					valuesType,
					enumeratorType),
				$"Unexpected call from the closed builder graph: {called.DeclaringType}.{called.Name}."));
			Assert.DoesNotContain(
				method.GetMethodBody()?.LocalVariables ?? [],
				variable => variable.LocalType == typeof(object) ||
					variable.LocalType.IsInterface ||
					ContainsWritableOwnedOutputStorage(variable.LocalType));
			Assert.DoesNotContain(
				GetIlInstructions(method),
				instruction => IsForbiddenBuilderOpcode(instruction.OpCode));
		}
		HashSet<MethodBase> exactQueryOwnedGraph = queryGraph
			.Where(IsWalletStateImplementationMethod)
			.ToHashSet();
		Assert.All(exactQueryOwnedGraph, method => Assert.True(
			closedBuilderGraph.Contains(method) ||
			method == query ||
			method == search ||
			method == ensureRevision ||
			IsPermittedExactQueryStateAccessor(method),
			$"Unexpected project method in the exact-query graph: {method.DeclaringType}.{method.Name}."));
		foreach (MethodBase method in exactQueryOwnedGraph.Where(method =>
			!closedBuilderGraph.Contains(method) && method != query && method != search))
		{
			Assert.All(GetCalledMethods(method), called => Assert.True(
				exactQueryOwnedGraph.Contains(called) ||
				(called.DeclaringType == typeof(InvalidOperationException) &&
				 called.IsConstructor &&
				 HasParameterTypes(called, typeof(string))),
				$"Unexpected transitive exact-query call: {called.DeclaringType}.{called.Name}."));
			Assert.Empty(GetStoredFields(method));
			Assert.Empty(method.GetMethodBody()?.ExceptionHandlingClauses ?? []);
			Assert.DoesNotContain(
				method.GetMethodBody()?.LocalVariables ?? [],
				variable => variable.LocalType == typeof(object) ||
					variable.LocalType.IsInterface ||
					typeof(Delegate).IsAssignableFrom(variable.LocalType));
		}

		IReadOnlyList<(MethodBase Owner, FieldInfo Field)> closedBuilderStores =
			closedBuilderGraph
				.SelectMany(owner => GetStoredFields(owner).Select(field => (owner, field)))
				.ToArray();
		Assert.All(closedBuilderStores, store => Assert.True(
			(store.Owner == totalsConstructor && store.Field == assetField) ||
			(store.Owner == spentSetter && store.Field == spentField) ||
			(store.Owner == createdSetter && store.Field == createdField) ||
			(store.Owner == builder &&
			 store.Field.IsStatic &&
			 store.Field.DeclaringType?.DeclaringType == typeof(LiquidWalletState) &&
			 store.Field.FieldType == typeof(Comparison<LiquidWalletAssetNetChange>)),
			$"Unexpected field store in the closed builder graph: {store.Field.DeclaringType}.{store.Field.Name}."));
		Assert.DoesNotContain(
			closedBuilderStores,
			store => store.Field.FieldType == typeof(object) ||
				store.Field.FieldType.IsInterface ||
				ContainsType(store.Field.FieldType, totalsType) &&
				store.Field.DeclaringType != totalsType);

		IReadOnlyList<Type> stateNestedTypes = typeof(LiquidWalletState).GetNestedTypes(
			BindingFlags.Public | BindingFlags.NonPublic);
		Assert.DoesNotContain(
			stateNestedTypes.Where(type => type != totalsType)
				.SelectMany(type => type.GetFields(
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
					BindingFlags.Static)),
			field => ContainsType(field.FieldType, totalsType));
		foreach (MethodInfo method in typeof(LiquidWalletState).GetMethods(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
			BindingFlags.Static | BindingFlags.DeclaredOnly))
		{
			bool inClosedAccumulatorGraph = method == builder || method == accumulate;
			if (!inClosedAccumulatorGraph)
			{
				Assert.False(ContainsType(method.ReturnType, totalsType));
				Assert.DoesNotContain(
					method.GetParameters(),
					parameter => ContainsType(parameter.ParameterType, totalsType));
				Assert.DoesNotContain(
					method.GetMethodBody()?.LocalVariables ?? [],
					variable => ContainsType(variable.LocalType, totalsType));
			}
		}
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

	private static void AssertEffectEquivalent(
		LiquidWalletTransactionEffect expected,
		LiquidWalletTransactionEffect actual)
	{
		Assert.Equal(expected.TransactionId, actual.TransactionId);
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.Confirmation, actual.Confirmation);
		Assert.Equal(expected.GetAssetNetChanges(), actual.GetAssetNetChanges());
	}

	private static void AssertEffectEquivalentExceptConfirmation(
		LiquidWalletTransactionEffect expected,
		LiquidWalletTransactionEffect actual)
	{
		Assert.Equal(expected.TransactionId, actual.TransactionId);
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.GetAssetNetChanges(), actual.GetAssetNetChanges());
	}

	private static void AssertStateUnchanged(LiquidWalletState state, Action action)
	{
		ulong revision = state.Revision;
		LiquidWalletReplaySnapshot replay = state.ExportReplaySnapshot();
		IReadOnlyList<LiquidAssetAmount> balances = state.GetBalances().GetAmounts();
		LiquidWalletTransactionEffectSnapshot effects = state.GetTransactionEffectSnapshot();
		LiquidWalletCoinControlSnapshot inventory = state.GetCoinControlSnapshot();

		action();

		Assert.Equal(revision, state.Revision);
		AssertReplayEquivalent(replay, state.ExportReplaySnapshot());
		Assert.Equal(balances, state.GetBalances().GetAmounts());
		AssertEquivalent(effects, state.GetTransactionEffectSnapshot());
		AssertCoinControlEquivalent(inventory, state.GetCoinControlSnapshot());
	}

	private static void AssertReplayEquivalent(
		LiquidWalletReplaySnapshot expected,
		LiquidWalletReplaySnapshot actual)
	{
		Assert.Equal(expected.PeggedAssetId, actual.PeggedAssetId);
		Assert.Equal(expected.Revision, actual.Revision);
		IReadOnlyList<LiquidWalletTransactionDelta> expectedDeltas = expected.GetDeltas();
		IReadOnlyList<LiquidWalletTransactionDelta> actualDeltas = actual.GetDeltas();
		Assert.Equal(expectedDeltas.Count, actualDeltas.Count);
		for (int index = 0; index < expectedDeltas.Count; index++)
		{
			Assert.Equal(expectedDeltas[index].TransactionId, actualDeltas[index].TransactionId);
			Assert.Equal(expectedDeltas[index].GetSpentOutPoints(), actualDeltas[index].GetSpentOutPoints());
			Assert.Equal(expectedDeltas[index].GetCreatedOutputs(), actualDeltas[index].GetCreatedOutputs());
		}

		Assert.Equal(expected.GetConfirmations(), actual.GetConfirmations());
	}

	private static void AssertCoinControlEquivalent(
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
			Assert.Equal(expectedEntries[index].PeggedAssetId, actualEntries[index].PeggedAssetId);
			Assert.Equal(expectedEntries[index].Confirmation, actualEntries[index].Confirmation);
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

	private static Type GetTransactionEffectTotalsType() =>
		typeof(LiquidWalletState).GetNestedType(
			"TransactionEffectTotals",
			BindingFlags.NonPublic) ??
		throw new InvalidOperationException("The transaction-effect accumulator type is unavailable.");

	private static FieldInfo GetStateField(string name) =>
		typeof(LiquidWalletState).GetField(
			name,
			BindingFlags.NonPublic | BindingFlags.Instance) ??
		throw new InvalidOperationException("The expected wallet-state field is unavailable.");

	private static int CountOpCode(MethodBase method, OpCode expected) =>
		GetIlInstructions(method).Count(instruction => instruction.OpCode == expected);

	private static int? GetStoredLocalIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction) =>
		GetLocalIndex(method, instruction, isStore: true);

	private static int? GetLoadedLocalIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction) =>
		GetLocalIndex(method, instruction, isStore: false);

	private static int? GetLocalIndex(
		MethodBase method,
		(int Offset, OpCode OpCode, int OperandOffset, int OperandSize) instruction,
		bool isStore)
	{
		OpCode opCode = instruction.OpCode;
		if (isStore)
		{
			if (opCode == OpCodes.Stloc_0) { return 0; }
			if (opCode == OpCodes.Stloc_1) { return 1; }
			if (opCode == OpCodes.Stloc_2) { return 2; }
			if (opCode == OpCodes.Stloc_3) { return 3; }
			if (opCode != OpCodes.Stloc && opCode != OpCodes.Stloc_S) { return null; }
		}
		else
		{
			if (opCode == OpCodes.Ldloc_0) { return 0; }
			if (opCode == OpCodes.Ldloc_1) { return 1; }
			if (opCode == OpCodes.Ldloc_2) { return 2; }
			if (opCode == OpCodes.Ldloc_3) { return 3; }
			if (opCode != OpCodes.Ldloc && opCode != OpCodes.Ldloc_S) { return null; }
		}

		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ??
			throw new InvalidOperationException("The method body is unavailable.");
		return instruction.OperandSize == 1
			? il[instruction.OperandOffset]
			: BitConverter.ToUInt16(il, instruction.OperandOffset);
	}

	private static IEnumerable<(int Offset, int Target, OpCode OpCode)> GetBranchEdges(
		MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		foreach ((int offset, OpCode opCode, int operandOffset, int operandSize) in
			GetIlInstructions(method))
		{
			if (opCode.OperandType == OperandType.ShortInlineBrTarget)
			{
				yield return (
					offset,
					operandOffset + operandSize + unchecked((sbyte)il[operandOffset]),
					opCode);
			}
			else if (opCode.OperandType == OperandType.InlineBrTarget)
			{
				yield return (
					offset,
					operandOffset + operandSize + BitConverter.ToInt32(il, operandOffset),
					opCode);
			}
		}
	}

	private static IEnumerable<FieldInfo> GetReferencedFields(MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		foreach ((_, OpCode opCode, int operandOffset, _) in GetIlInstructions(method))
		{
			if (opCode.OperandType != OperandType.InlineField)
			{
				continue;
			}

			FieldInfo? field = method.Module.ResolveField(
				BitConverter.ToInt32(il, operandOffset),
				method.DeclaringType?.GetGenericArguments(),
				method.IsGenericMethod ? method.GetGenericArguments() : null);
			if (field is not null)
			{
				yield return field;
			}
		}
	}

	private static IEnumerable<FieldInfo> GetStoredFields(MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		foreach ((_, OpCode opCode, int operandOffset, _) in GetIlInstructions(method))
		{
			if (opCode != OpCodes.Stfld && opCode != OpCodes.Stsfld)
			{
				continue;
			}

			FieldInfo? field = method.Module.ResolveField(
				BitConverter.ToInt32(il, operandOffset),
				method.DeclaringType?.GetGenericArguments(),
				method.IsGenericMethod ? method.GetGenericArguments() : null);
			if (field is not null)
			{
				yield return field;
			}
		}
	}

	private static IEnumerable<(int Offset, OpCode OpCode, int OperandOffset, int OperandSize)>
		GetIlInstructions(MethodBase method)
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
			int offset = position;
			short value = il[position++] == 0xfe
				? unchecked((short)(0xfe00 | il[position++]))
				: il[position - 1];
			OpCode opCode = opCodes[value];
			int operandOffset = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
			yield return (offset, opCode, operandOffset, operandSize);
			position += operandSize;
		}
	}

	private static bool ContainsType(Type root, Type expected)
	{
		if (root == expected)
		{
			return true;
		}
		if (root.HasElementType && ContainsType(root.GetElementType()!, expected))
		{
			return true;
		}
		return root.IsGenericType && root.GetGenericArguments().Any(type => ContainsType(type, expected));
	}

	private static bool IsWalletStateImplementationMethod(MethodBase method) =>
		method.DeclaringType == typeof(LiquidWalletState) ||
		method.DeclaringType?.DeclaringType == typeof(LiquidWalletState);

	private static bool IsPermittedExactTransactionEffectQueryCall(
		MethodBase method,
		MethodInfo ensureRevision,
		MethodInfo search,
		MethodInfo builder,
		MethodInfo retainedCreatedOutputs,
		MethodInfo ownershipTransfer)
	{
		if (method == ensureRevision || method == search || method == builder ||
			method == retainedCreatedOutputs || method == ownershipTransfer)
		{
			return true;
		}
		Type? declaringType = method.DeclaringType;
		if (declaringType == typeof(ArgumentNullException))
		{
			return method.Name == nameof(ArgumentNullException.ThrowIfNull) &&
				HasParameterTypes(method, typeof(object), typeof(string));
		}
		if (declaringType == typeof(ArgumentException))
		{
			return method.IsConstructor && HasParameterTypes(method, typeof(string), typeof(string));
		}
		if (declaringType == typeof(LiquidTransactionId))
		{
			return method.Name == "get_IsZero" && method.GetParameters().Length == 0;
		}
		if (declaringType == typeof(LiquidWalletState))
		{
			return method.Name is "get_PeggedAssetId" or "get_Revision" &&
				method.GetParameters().Length == 0;
		}
		if (declaringType?.Name == "AppliedDelta" &&
			declaringType.DeclaringType == typeof(LiquidWalletState))
		{
			return method.Name is "get_Delta" or "get_SpentOutputs" &&
				method.GetParameters().Length == 0;
		}
		if (declaringType == typeof(LiquidWalletTransactionDelta))
		{
			return method.Name == "get_TransactionId" && method.GetParameters().Length == 0;
		}
		if (declaringType == typeof(ReadOnlySpan<LiquidOwnedOutput>))
		{
			return method.Name == "op_Implicit" &&
				HasParameterTypes(method, typeof(LiquidOwnedOutput[]));
		}
		return declaringType == typeof(Dictionary<LiquidTransactionId, LiquidConfirmation>) &&
			method.Name == nameof(Dictionary<LiquidTransactionId, LiquidConfirmation>.TryGetValue) &&
			HasParameterTypes(
				method,
				typeof(LiquidTransactionId),
				typeof(LiquidConfirmation).MakeByRefType());
	}

	private static bool IsPermittedTransactionEffectSearchCall(MethodBase method)
	{
		Type? declaringType = method.DeclaringType;
		if (declaringType is { IsGenericType: true } &&
			declaringType.GetGenericTypeDefinition() == typeof(List<>))
		{
			return method.Name is "get_Item" or "get_Count";
		}
		if (declaringType?.Name == "AppliedDelta" &&
			declaringType.DeclaringType == typeof(LiquidWalletState))
		{
			return method.Name == "get_Delta" && method.GetParameters().Length == 0;
		}
		if (declaringType == typeof(LiquidWalletTransactionDelta))
		{
			return method.Name == "get_TransactionId" && method.GetParameters().Length == 0;
		}
		return declaringType == typeof(LiquidTransactionId) &&
			method.Name == "op_Equality" &&
			HasParameterTypes(method, typeof(LiquidTransactionId), typeof(LiquidTransactionId));
	}

	private static bool IsPermittedExactQueryStateAccessor(MethodBase method)
	{
		Type? declaringType = method.DeclaringType;
		if (declaringType == typeof(LiquidWalletState))
		{
			return method.Name is "get_PeggedAssetId" or "get_Revision" &&
				method.GetParameters().Length == 0;
		}
		return declaringType?.Name == "AppliedDelta" &&
			declaringType.DeclaringType == typeof(LiquidWalletState) &&
			method.Name is "get_Delta" or "get_SpentOutputs" &&
			method.GetParameters().Length == 0;
	}

	private static IEnumerable<Type> GetAllNestedTypes(Type root)
	{
		var pending = new Queue<Type>();
		pending.Enqueue(root);
		while (pending.TryDequeue(out Type? current))
		{
			yield return current;
			foreach (Type nested in current.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
			{
				pending.Enqueue(nested);
			}
		}
	}

	private static bool IsPermittedTransactionEffectTotalsMethod(
		MethodBase method,
		Type totalsType)
	{
		if (method.DeclaringType != totalsType)
		{
			return false;
		}
		if (method.IsConstructor)
		{
			return HasParameterTypes(method, typeof(LiquidAssetId));
		}
		return method is MethodInfo methodInfo && method.Name switch
		{
			"get_AssetId" => methodInfo.ReturnType == typeof(LiquidAssetId) &&
				method.GetParameters().Length == 0,
			"get_SpentAtomicUnits" or "get_CreatedAtomicUnits" =>
				methodInfo.ReturnType == typeof(long) && method.GetParameters().Length == 0,
			"set_SpentAtomicUnits" or "set_CreatedAtomicUnits" =>
				methodInfo.ReturnType == typeof(void) && HasParameterTypes(method, typeof(long)),
			"AddSpent" or "AddCreated" => methodInfo.ReturnType == typeof(void) &&
				HasParameterTypes(method, typeof(LiquidAssetAmount)),
			"CheckedEffectTotal" => methodInfo.ReturnType == typeof(long) &&
				HasParameterTypes(method, typeof(long), typeof(LiquidAssetAmount)),
			_ => false,
		};
	}

	private static bool IsCanonicalEffectComparisonMethod(MethodBase method) =>
		method is MethodInfo methodInfo &&
		methodInfo.ReturnType == typeof(int) &&
		method.DeclaringType?.DeclaringType == typeof(LiquidWalletState) &&
		method.DeclaringType.Name == "<>c" &&
		method.DeclaringType != GetTransactionEffectTotalsType() &&
		method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
			[typeof(LiquidWalletAssetNetChange), typeof(LiquidWalletAssetNetChange)]);

	private static bool IsPermittedEffectBuilderExternalCall(
		MethodBase method,
		Type totalsDictionaryType,
		Type valuesType,
		Type enumeratorType)
	{
		Type? declaringType = method.DeclaringType;
		if (declaringType == totalsDictionaryType)
		{
			return method.IsConstructor && HasParameterTypes(
					method, typeof(IEqualityComparer<string>)) ||
				method.Name is "get_Values" or
					nameof(Dictionary<string, object>.TryGetValue) or
					nameof(Dictionary<string, object>.Add);
		}
		if (declaringType == valuesType)
		{
			return method.Name == "GetEnumerator";
		}
		if (declaringType == enumeratorType)
		{
			return method.Name is "get_Current" or "MoveNext" or "Dispose";
		}
		if (declaringType == typeof(ReadOnlySpan<LiquidOwnedOutput>))
		{
			return method.Name is "get_Item" or "get_Length" or "GetEnumerator";
		}
		if (declaringType == typeof(ReadOnlySpan<LiquidOwnedOutput>.Enumerator))
		{
			return method.Name is "get_Current" or "MoveNext";
		}
		if (declaringType == typeof(List<LiquidWalletAssetNetChange>))
		{
			return method.IsConstructor && method.GetParameters().Length == 0 ||
				method.Name is nameof(List<LiquidWalletAssetNetChange>.Add) or
					nameof(List<LiquidWalletAssetNetChange>.Sort);
		}
		if (declaringType == typeof(Comparison<LiquidWalletAssetNetChange>))
		{
			return method.IsConstructor && HasParameterTypes(method, typeof(object), typeof(nint));
		}
		if (declaringType == typeof(LiquidWalletAssetNetChange))
		{
			return method.Name is nameof(LiquidWalletAssetNetChange.Create) or "get_AssetId";
		}
		if (declaringType == typeof(LiquidWalletTransactionEffect))
		{
			return method.IsConstructor && HasParameterTypes(
				method,
				typeof(LiquidTransactionId),
				typeof(LiquidAssetId),
				typeof(LiquidConfirmation),
				typeof(IReadOnlyList<LiquidWalletAssetNetChange>));
		}
		if (declaringType == typeof(LiquidOwnedOutput))
		{
			return method.Name == "get_Amount";
		}
		if (declaringType == typeof(LiquidAssetAmount))
		{
			return method.Name is "get_AssetId" or "get_PeggedAssetId" or "get_AtomicUnits";
		}
		if (declaringType == typeof(LiquidAssetId))
		{
			return method.Name is "get_CanonicalRpcHex" or "op_Equality" or "op_Inequality";
		}
		if (declaringType == typeof(StringComparer))
		{
			return method.Name is "get_Ordinal" or nameof(StringComparer.Compare);
		}
		if (declaringType == typeof(OverflowException) ||
			declaringType == typeof(InvalidOperationException))
		{
			return method.IsConstructor && HasParameterTypes(method, typeof(string));
		}
		if (declaringType == typeof(IDisposable))
		{
			return method.Name == nameof(IDisposable.Dispose) && method.GetParameters().Length == 0;
		}
		return declaringType == typeof(object) &&
			method.IsConstructor &&
			method.GetParameters().Length == 0;
	}

	private static bool HasParameterTypes(MethodBase method, params Type[] expected) =>
		method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(expected);

	private static bool ContainsWritableOwnedOutputStorage(Type type)
	{
		if (type.IsByRef)
		{
			return type.GetElementType() == typeof(LiquidOwnedOutput);
		}
		if (type.IsArray)
		{
			return type.GetElementType() == typeof(LiquidOwnedOutput);
		}
		return type.IsGenericType &&
			type.GetGenericArguments().Contains(typeof(LiquidOwnedOutput)) &&
			type.GetGenericTypeDefinition() is var definition &&
			(definition == typeof(Span<>) || definition == typeof(Memory<>));
	}

	private static bool IsForbiddenBuilderOpcode(OpCode opCode) =>
		opCode == OpCodes.Box ||
		opCode == OpCodes.Castclass ||
		opCode == OpCodes.Unbox ||
		opCode == OpCodes.Unbox_Any ||
		opCode == OpCodes.Calli ||
		opCode == OpCodes.Localloc ||
		opCode == OpCodes.Mkrefany ||
		opCode == OpCodes.Refanyval ||
		opCode == OpCodes.Starg ||
		opCode == OpCodes.Starg_S ||
		opCode == OpCodes.Stelem ||
		opCode == OpCodes.Stelem_I ||
		opCode == OpCodes.Stelem_I1 ||
		opCode == OpCodes.Stelem_I2 ||
		opCode == OpCodes.Stelem_I4 ||
		opCode == OpCodes.Stelem_I8 ||
		opCode == OpCodes.Stelem_R4 ||
		opCode == OpCodes.Stelem_R8 ||
		opCode == OpCodes.Stelem_Ref ||
		opCode == OpCodes.Stind_I ||
		opCode == OpCodes.Stind_I1 ||
		opCode == OpCodes.Stind_I2 ||
		opCode == OpCodes.Stind_I4 ||
		opCode == OpCodes.Stind_I8 ||
		opCode == OpCodes.Stind_R4 ||
		opCode == OpCodes.Stind_R8 ||
		opCode == OpCodes.Stind_Ref ||
		opCode == OpCodes.Stobj ||
		opCode == OpCodes.Cpobj ||
		opCode == OpCodes.Initobj ||
		opCode == OpCodes.Cpblk ||
		opCode == OpCodes.Initblk;

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

	private static IEnumerable<(int Offset, MethodBase Method)> GetCalledMethodInstructions(
		MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		foreach ((int offset, OpCode opCode, int operandOffset, _) in GetIlInstructions(method))
		{
			if (opCode.OperandType == OperandType.InlineMethod)
			{
				int token = BitConverter.ToInt32(il, operandOffset);
				MethodBase? called = method.Module.ResolveMethod(
					token,
					method.DeclaringType?.GetGenericArguments(),
					method.IsGenericMethod ? method.GetGenericArguments() : null);
				if (called is not null)
				{
					yield return (offset, called);
				}
			}
		}
	}

	private static IEnumerable<MethodBase> GetCalledMethods(MethodBase method) =>
		GetCalledMethodInstructions(method).Select(instruction => instruction.Method);

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
