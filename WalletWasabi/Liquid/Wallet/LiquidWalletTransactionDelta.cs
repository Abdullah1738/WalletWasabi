using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletTransactionDelta
{
	private readonly LiquidOutPoint[] _spentOutPoints;
	private readonly LiquidOwnedOutput[] _createdOutputs;

	private LiquidWalletTransactionDelta(
		LiquidTransactionId transactionId,
		LiquidOutPoint[] spentOutPoints,
		LiquidOwnedOutput[] createdOutputs)
	{
		TransactionId = transactionId;
		_spentOutPoints = spentOutPoints;
		_createdOutputs = createdOutputs;
	}

	public LiquidTransactionId TransactionId { get; }

	public static LiquidWalletTransactionDelta Create(
		LiquidTransactionId transactionId,
		IEnumerable<LiquidOutPoint> spentOutPoints,
		IEnumerable<LiquidOwnedOutput> createdOutputs)
	{
		ArgumentNullException.ThrowIfNull(transactionId);
		ArgumentNullException.ThrowIfNull(spentOutPoints);
		ArgumentNullException.ThrowIfNull(createdOutputs);
		if (transactionId.IsZero)
		{
			throw new ArgumentException("A nonzero Liquid transaction identifier is required.", nameof(transactionId));
		}

		LiquidOutPoint[] spent = spentOutPoints.ToArray();
		LiquidOwnedOutput[] created = createdOutputs.ToArray();
		if (spent.Length == 0 && created.Length == 0)
		{
			throw new ArgumentException("A wallet transaction delta must contain a spend or an owned output.");
		}

		var uniqueSpent = new HashSet<LiquidOutPoint>();
		foreach (LiquidOutPoint spentOutPoint in spent)
		{
			ArgumentNullException.ThrowIfNull(spentOutPoint, nameof(spentOutPoints));
			if (!uniqueSpent.Add(spentOutPoint))
			{
				throw new ArgumentException("A wallet transaction delta cannot repeat a spent outpoint.", nameof(spentOutPoints));
			}
		}

		var uniqueCreated = new HashSet<LiquidOutPoint>();
		foreach (LiquidOwnedOutput createdOutput in created)
		{
			ArgumentNullException.ThrowIfNull(createdOutput, nameof(createdOutputs));
			if (createdOutput.OutPoint.TransactionId != transactionId)
			{
				throw new ArgumentException("Every created owned output must belong to the delta transaction.", nameof(createdOutputs));
			}
			if (!uniqueCreated.Add(createdOutput.OutPoint))
			{
				throw new ArgumentException("A wallet transaction delta cannot repeat a created outpoint.", nameof(createdOutputs));
			}
			if (uniqueSpent.Contains(createdOutput.OutPoint))
			{
				throw new ArgumentException("A wallet transaction delta cannot spend and create the same outpoint.");
			}
		}

		return new LiquidWalletTransactionDelta(transactionId, spent, created);
	}

	public IReadOnlyList<LiquidOutPoint> GetSpentOutPoints() =>
		new ReadOnlyCollection<LiquidOutPoint>([.. _spentOutPoints]);

	public IReadOnlyList<LiquidOwnedOutput> GetCreatedOutputs() =>
		new ReadOnlyCollection<LiquidOwnedOutput>([.. _createdOutputs]);

	public override string ToString() => nameof(LiquidWalletTransactionDelta);
}
