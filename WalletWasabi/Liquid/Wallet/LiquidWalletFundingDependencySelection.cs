using System.Collections.ObjectModel;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletFundingDependencySelection
{
	private readonly IReadOnlyList<string> _canonicalSelectedOutPointHexes;
	private readonly IReadOnlyList<IReadOnlyList<string>> _previousTransactionIdsBySelectedInput;

	private LiquidWalletFundingDependencySelection(
		IReadOnlyList<string> canonicalSelectedOutPointHexes,
		IReadOnlyList<IReadOnlyList<string>> previousTransactionIdsBySelectedInput)
	{
		ArgumentNullException.ThrowIfNull(canonicalSelectedOutPointHexes);
		ArgumentNullException.ThrowIfNull(previousTransactionIdsBySelectedInput);
		_canonicalSelectedOutPointHexes = new ReadOnlyCollection<string>([.. canonicalSelectedOutPointHexes]);
		var rows = new IReadOnlyList<string>[previousTransactionIdsBySelectedInput.Count];
		for (int index = 0; index < rows.Length; index++)
		{
			IReadOnlyList<string> row = previousTransactionIdsBySelectedInput[index]
				?? throw new ArgumentException("A dependency row cannot be null.", nameof(previousTransactionIdsBySelectedInput));
			rows[index] = new ReadOnlyCollection<string>([.. row]);
		}
		_previousTransactionIdsBySelectedInput = new ReadOnlyCollection<IReadOnlyList<string>>(rows);
	}

	internal IReadOnlyList<string> CanonicalSelectedOutPointHexes => _canonicalSelectedOutPointHexes;
	internal IReadOnlyList<IReadOnlyList<string>> PreviousTransactionIdsBySelectedInput => _previousTransactionIdsBySelectedInput;

	internal static LiquidWalletFundingDependencySelection Create(
		IReadOnlyList<string> canonicalSelectedOutPointHexes,
		IReadOnlyList<IReadOnlyList<string>> previousTransactionIdsBySelectedInput) =>
		new(canonicalSelectedOutPointHexes, previousTransactionIdsBySelectedInput);

	public override string ToString() => nameof(LiquidWalletFundingDependencySelection);
}
