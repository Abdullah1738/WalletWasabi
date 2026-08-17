using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The pure, fail-closed scan-intent deriver. <see cref="Derive"/> maps one
/// caller-supplied set of validated candidate transaction-association rows
/// (and, optionally, the SYNC-003 replacement-transaction set named alongside
/// a non-rescan <see cref="LiquidWalletReorgPlan"/>) into the deterministic,
/// ordered, deduplicated, bounded
/// <see cref="LiquidWalletSyncBatchPlanner.FetchIntent"/> set the landed
/// SYNC-001 planner consumes. The method body is exactly that mapping and
/// nothing more: it performs no state transition, no RPC, no node contact, no
/// mutation, no block-filter scanning, and no catch-and-rethrow remapping. It
/// never asks "what is mine" — the caller names every candidate transaction —
/// and it never fabricates an intent
/// <see cref="LiquidWalletSyncBatchPlanner.CreateRequests"/> would reject (the
/// 1..<see cref="LiquidWalletSyncBatchPlanner.MaximumRequestCount"/> bound,
/// uniqueness, and normalization all hold by construction; CreateRequests
/// remains the final fence downstream). It carries no chain,
/// confirmation-source, currentness, or broadcast authority.
/// </summary>
internal static class LiquidWalletScanIntentDeriver
{
	public static LiquidWalletScanIntentDerivation Derive(
		IReadOnlyList<LiquidWalletScanIntent> candidateIntents,
		LiquidWalletReorgPlan? reorgPlan = null,
		IReadOnlyList<LiquidWalletScanIntent>? replacementIntents = null)
	{
		ArgumentNullException.ThrowIfNull(candidateIntents);
		for (int index = 0; index < candidateIntents.Count; index++)
		{
			_ = candidateIntents[index]
				?? throw new ArgumentException(
					"Every candidate scan intent is required.",
					nameof(candidateIntents));
		}

		if (reorgPlan is not null)
		{
			if (reorgPlan.RequiresRescan)
			{
				throw new InvalidOperationException(
					"A reorg deeper than the retained history has no well-defined replacement set; the caller must rebuild from chain data.");
			}

			if (replacementIntents is null)
			{
				throw new ArgumentException(
					"A non-rescan reorg plan requires its replacement scan intent set.",
					nameof(replacementIntents));
			}

			for (int index = 0; index < replacementIntents.Count; index++)
			{
				_ = replacementIntents[index]
					?? throw new ArgumentException(
						"Every replacement scan intent is required.",
						nameof(replacementIntents));
			}
		}
		else if (replacementIntents is not null)
		{
			throw new ArgumentException(
				"A replacement scan intent set requires its reorg plan.",
				nameof(replacementIntents));
		}

		// Union of the candidate and (when present) replacement rows,
		// deduplicated by normalized transaction id (ordinal on
		// CanonicalRpcHex). On a duplicate transaction id the row carrying a
		// non-null block-hash hint wins (a confirming hint is strictly more
		// useful than none); two different non-null hints for one transaction
		// are a caller inconsistency the deriver must not silently resolve.
		var deduplicated = new Dictionary<string, LiquidWalletScanIntent>(StringComparer.Ordinal);
		Accumulate(candidateIntents);
		if (replacementIntents is not null)
		{
			Accumulate(replacementIntents);
		}

		if (deduplicated.Count > LiquidWalletSyncBatchPlanner.MaximumRequestCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(candidateIntents),
				$"At most {LiquidWalletSyncBatchPlanner.MaximumRequestCount} deduplicated scan intents are supported; the caller must split the scan into multiple bounded batches.");
		}

		// Canonical ascending transaction-id order (ordinal on
		// CanonicalRpcHex), so the output is deterministic and independent of
		// input row order. A count of zero is not an error: the derivation
		// returns IsEmpty == true and the caller skips the fetch step.
		LiquidWalletScanIntent[] ordered = [.. deduplicated.Values];
		Array.Sort(ordered, static (left, right) =>
			StringComparer.Ordinal.Compare(
				left.TransactionId.CanonicalRpcHex,
				right.TransactionId.CanonicalRpcHex));

		var intents = new LiquidWalletSyncBatchPlanner.FetchIntent[ordered.Length];
		for (int index = 0; index < ordered.Length; index++)
		{
			intents[index] = new LiquidWalletSyncBatchPlanner.FetchIntent(
				ordered[index].TransactionId.CanonicalRpcHex,
				ordered[index].BlockHash);
		}

		return LiquidWalletScanIntentDerivation.Create(intents);

		void Accumulate(IReadOnlyList<LiquidWalletScanIntent> rows)
		{
			for (int index = 0; index < rows.Count; index++)
			{
				LiquidWalletScanIntent row = rows[index];
				string transactionId = row.TransactionId.CanonicalRpcHex;
				if (deduplicated.TryGetValue(transactionId, out LiquidWalletScanIntent? existing))
				{
					if (existing.BlockHash is null)
					{
						if (row.BlockHash is not null)
						{
							deduplicated[transactionId] = row;
						}
					}
					else if (row.BlockHash is not null &&
						!StringComparer.Ordinal.Equals(existing.BlockHash, row.BlockHash))
					{
						throw new ArgumentException(
							"Conflicting block-hash hints for one transaction are a caller inconsistency.",
							nameof(candidateIntents));
					}
				}
				else
				{
					deduplicated.Add(transactionId, row);
				}
			}
		}
	}
}
