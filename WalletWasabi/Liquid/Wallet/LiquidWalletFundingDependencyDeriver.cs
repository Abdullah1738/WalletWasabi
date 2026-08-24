using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

internal static class LiquidWalletFundingDependencyDeriver
{
	internal static LiquidWalletFundingDependencySelection Derive(
		LiquidWalletState state,
		IReadOnlyList<string> selectedOutPointHexes,
		ulong expectedRevision)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(selectedOutPointHexes);
		if (state.Revision != expectedRevision)
		{
			throw new InvalidOperationException("The Liquid wallet state revision does not match the expected revision.");
		}
		if (selectedOutPointHexes.Count == 0)
		{
			throw new ArgumentException("At least one selected outpoint is required.", nameof(selectedOutPointHexes));
		}

		var parsed = new LiquidOutPoint[selectedOutPointHexes.Count];
		var seen = new HashSet<LiquidOutPoint>();
		for (int index = 0; index < parsed.Length; index++)
		{
			string hex = selectedOutPointHexes[index]
				?? throw new ArgumentException("A selected outpoint cannot be null.", nameof(selectedOutPointHexes));
			byte[] bytes;
			try
			{
				bytes = Convert.FromHexString(hex);
			}
			catch (FormatException exception)
			{
				throw new ArgumentException("A selected outpoint must be valid hexadecimal.", nameof(selectedOutPointHexes), exception);
			}
			try
			{
				parsed[index] = LiquidOutPoint.ParseSpendableConsensusBytes(bytes, nameof(selectedOutPointHexes));
			}
			catch (ArgumentException exception)
			{
				throw new ArgumentException("A valid spendable selected outpoint is required.", nameof(selectedOutPointHexes), exception);
			}
			if (!seen.Add(parsed[index]))
			{
				throw new ArgumentException("Selected outpoints must be unique.", nameof(selectedOutPointHexes));
			}
		}

		LiquidWalletCoinControlSelection selection = state.CreateCoinControlSelection(expectedRevision, parsed);
		IReadOnlyList<LiquidWalletCoinControlEntry> entries = selection.GetEntries();
		LiquidWalletReplaySnapshot snapshot = state.ExportReplaySnapshot();
		if (snapshot.Revision != expectedRevision)
		{
			throw new InvalidOperationException("The retained replay snapshot revision does not match the expected revision.");
		}
		IReadOnlyList<LiquidWalletTransactionDelta> deltas = snapshot.GetDeltas();
		var byId = new Dictionary<string, LiquidWalletTransactionDelta>(StringComparer.Ordinal);
		foreach (LiquidWalletTransactionDelta delta in deltas)
		{
			if (delta is null || delta.TransactionId is null || delta.TransactionId.IsZero ||
			!StringComparer.Ordinal.Equals(delta.TransactionId.CanonicalRpcHex, delta.TransactionId.CanonicalRpcHex.ToLowerInvariant()))
			{
				throw new InvalidOperationException("The retained replay contains an invalid transaction identifier.");
			}
			if (!byId.TryAdd(delta.TransactionId.CanonicalRpcHex, delta))
			{
				throw new InvalidOperationException("The retained replay contains duplicate transaction identifiers.");
			}
		}

		var canonicalSelected = new string[entries.Count];
		var rows = new IReadOnlyList<string>[entries.Count];
		for (int index = 0; index < entries.Count; index++)
		{
			LiquidOutPoint candidate = entries[index].OutPoint;
			canonicalSelected[index] = Convert.ToHexString(candidate.ToConsensusBytes()).ToLowerInvariant();
			string candidateId = candidate.TransactionId.CanonicalRpcHex;
			if (!byId.TryGetValue(candidateId, out LiquidWalletTransactionDelta? delta))
			{
				throw new InvalidOperationException("The selected candidate is absent from retained replay.");
			}
			IReadOnlyList<LiquidOutPoint> spent = delta.GetSpentOutPoints();
			var dependencies = new HashSet<string>(StringComparer.Ordinal);
			foreach (LiquidOutPoint predecessor in spent)
			{
				if (predecessor is null || predecessor.TransactionId is null || predecessor.TransactionId.IsZero ||
					predecessor.OutputIndex > LiquidOutPoint.MaxSpendableOutputIndex)
				{
					throw new InvalidOperationException("The retained replay contains an invalid spent outpoint.");
				}
				string predecessorId = predecessor.TransactionId.CanonicalRpcHex;
				if (StringComparer.Ordinal.Equals(predecessorId, candidateId) || !byId.ContainsKey(predecessorId))
				{
					throw new InvalidOperationException("The retained replay predecessor set is incomplete.");
				}
				dependencies.Add(predecessorId);
			}
			string[] ordered = [.. dependencies.OrderBy(id => id, StringComparer.Ordinal)];
			rows[index] = new ReadOnlyCollection<string>(ordered);
		}
		return LiquidWalletFundingDependencySelection.Create(
			new ReadOnlyCollection<string>(canonicalSelected),
			new ReadOnlyCollection<IReadOnlyList<string>>(rows));
	}
}
