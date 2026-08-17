using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Sync;

internal enum LiquidWalletSyncConfirmationKind
{
	Confirm = 0,
	Unconfirm = 1,
}

/// <summary>
/// One caller-supplied confirmation transition for a wallet sync session. An
/// unconfirm row carries the expected prior confirmation exactly as
/// <see cref="LiquidWalletState.Unconfirm"/> requires. Construction validates
/// shape only and carries no chain, currentness, reservation, or broadcast
/// authority.
/// </summary>
internal sealed record LiquidWalletSyncConfirmation
{
	private LiquidWalletSyncConfirmation(
		LiquidWalletSyncConfirmationKind kind,
		LiquidTransactionId transactionId,
		LiquidConfirmation confirmation)
	{
		Kind = kind;
		TransactionId = transactionId;
		Confirmation = confirmation;
	}

	public LiquidWalletSyncConfirmationKind Kind { get; }
	public LiquidTransactionId TransactionId { get; }
	public LiquidConfirmation Confirmation { get; }

	public static LiquidWalletSyncConfirmation Create(
		LiquidWalletSyncConfirmationKind kind,
		LiquidTransactionId transactionId,
		LiquidConfirmation confirmation)
	{
		if (!Enum.IsDefined(kind))
		{
			throw new ArgumentOutOfRangeException(
				nameof(kind),
				"A supported Liquid wallet sync confirmation kind is required.");
		}

		ArgumentNullException.ThrowIfNull(transactionId);
		ArgumentNullException.ThrowIfNull(confirmation);
		if (transactionId.IsZero)
		{
			throw new ArgumentException(
				"A nonzero Liquid transaction identifier is required.",
				nameof(transactionId));
		}

		return new LiquidWalletSyncConfirmation(kind, transactionId, confirmation);
	}

	public override string ToString() => nameof(LiquidWalletSyncConfirmation);
}
