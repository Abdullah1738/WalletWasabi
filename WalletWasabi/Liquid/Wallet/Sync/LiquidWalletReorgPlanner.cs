using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The pure, fail-closed multi-batch reorg planner. <see cref="Plan"/> derives
/// the complete unwind plan for a reorg spanning one or more previously
/// confirmed batches from one caller-supplied
/// <see cref="LiquidWalletReplaySnapshot"/> and one caller-supplied
/// expectation-bound node observation, and nothing more: it performs no state
/// transition, no RPC, no mutation, and no catch-and-rethrow remapping. The
/// unconfirm set is delegated unchanged to the landed
/// <see cref="LiquidWalletRecoverySyncPlan.Reconcile"/> tip-binding rule. The
/// invalidated-transaction set is seeded by every retained delta whose recorded
/// confirmation the observed tip no longer binds and expanded by the
/// dependent-spend cascade (a delta is invalidated iff it spends an outpoint
/// created by an invalidated delta), iterated to fixpoint. The rollback order
/// is the invalidated deltas in reverse snapshot order — the exact reverse
/// application order <see cref="LiquidWalletState.RollbackLast"/> demands. The
/// derivation rejects with <see cref="LiquidWalletReorgPlan.RequiresRescan"/>
/// (both lists empty) when the invalidated set is not a suffix of the
/// snapshot's delta list: a reorg that invalidates an earlier transaction while
/// a later, independent transaction stays valid cannot be satisfied by the
/// retained history without a chain rescan, and the planner never fabricates a
/// rollback order <see cref="LiquidWalletState.RollbackLast"/> would reject.
/// It never emits <see cref="LiquidWalletSyncConfirmationKind.Confirm"/> rows
/// and carries no chain, confirmation-source, currentness, or broadcast
/// authority.
/// </summary>
internal static class LiquidWalletReorgPlanner
{
	public static LiquidWalletReorgPlan Plan(
		LiquidWalletReplaySnapshot snapshot,
		ElementsExpectationBoundNodeObservation observation)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(observation);

		// The unconfirm set is the landed SYNC-002 reconcile rule, reused
		// unchanged: one Unconfirm row per recorded confirmation the observed
		// tip no longer binds, in canonical ascending-txid order.
		LiquidWalletSyncConfirmation[] unconfirmations =
			LiquidWalletRecoverySyncPlan.Reconcile(snapshot, observation);

		IReadOnlyList<LiquidWalletTransactionDelta> deltas = snapshot.GetDeltas();
		int deltaCount = deltas.Count;

		// Seed: a retained delta is invalidated iff its txid appears in the
		// unconfirm set (its recorded confirmation is no longer tip-bound — the
		// reorg moved its block).
		var unconfirmedTransactionIds = new HashSet<LiquidTransactionId>();
		foreach (LiquidWalletSyncConfirmation row in unconfirmations)
		{
			unconfirmedTransactionIds.Add(row.TransactionId);
		}

		var invalidated = new bool[deltaCount];
		for (int index = 0; index < deltaCount; index++)
		{
			invalidated[index] = unconfirmedTransactionIds.Contains(deltas[index].TransactionId);
		}

		// Dependent-spend cascade to fixpoint: a retained delta is invalidated
		// iff it spends an outpoint created by an invalidated delta.
		bool expanded = true;
		while (expanded)
		{
			expanded = false;
			var invalidatedCreatedOutPoints = new HashSet<LiquidOutPoint>();
			for (int index = 0; index < deltaCount; index++)
			{
				if (!invalidated[index])
				{
					continue;
				}

				IReadOnlyList<LiquidOwnedOutput> createdOutputs = deltas[index].GetCreatedOutputs();
				for (int outputIndex = 0; outputIndex < createdOutputs.Count; outputIndex++)
				{
					invalidatedCreatedOutPoints.Add(createdOutputs[outputIndex].OutPoint);
				}
			}

			for (int index = 0; index < deltaCount; index++)
			{
				if (invalidated[index])
				{
					continue;
				}

				IReadOnlyList<LiquidOutPoint> spentOutPoints = deltas[index].GetSpentOutPoints();
				bool spendsInvalidatedOutput = false;
				for (int spentIndex = 0; spentIndex < spentOutPoints.Count; spentIndex++)
				{
					if (invalidatedCreatedOutPoints.Contains(spentOutPoints[spentIndex]))
					{
						spendsInvalidatedOutput = true;
						break;
					}
				}

				if (spendsInvalidatedOutput)
				{
					invalidated[index] = true;
					expanded = true;
				}
			}
		}

		// Suffix check: RollbackLast can only unwind the still-applied history
		// tail in exact reverse order, so the invalidated set must be a suffix
		// of the snapshot's delta list. A non-suffix invalidation means the
		// reorg is deeper than the retained history can satisfy.
		int firstInvalidatedIndex = -1;
		int invalidatedCount = 0;
		for (int index = 0; index < deltaCount; index++)
		{
			if (!invalidated[index])
			{
				continue;
			}

			if (firstInvalidatedIndex < 0)
			{
				firstInvalidatedIndex = index;
			}

			invalidatedCount++;
		}

		if (invalidatedCount > 0 && firstInvalidatedIndex + invalidatedCount != deltaCount)
		{
			return LiquidWalletReorgPlan.RescanRequired();
		}

		// The snapshot's delta list order is the application order, so the
		// rollback order is the invalidated deltas in reverse snapshot order.
		var rollbackTransactionIds = new LiquidTransactionId[invalidatedCount];
		for (int index = 0; index < invalidatedCount; index++)
		{
			rollbackTransactionIds[index] = deltas[deltaCount - 1 - index].TransactionId;
		}

		return LiquidWalletReorgPlan.Create(unconfirmations, rollbackTransactionIds);
	}
}
