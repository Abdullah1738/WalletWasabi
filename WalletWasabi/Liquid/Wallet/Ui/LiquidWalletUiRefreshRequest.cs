using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Ui;

public enum LiquidWalletUiRefreshTrigger
{
	Manual,
	AcceptedSend,
}

public sealed class LiquidWalletUiRefreshRequest
{
	private const int MaximumWalletIdLength = 256;

	public LiquidWalletUiRefreshRequest(
		string canonicalWalletId,
		LiquidWalletUiRefreshTrigger trigger,
		string? acceptedTransactionIdHex)
	{
		ArgumentNullException.ThrowIfNull(canonicalWalletId);
		if (canonicalWalletId.Length is < 1 or > MaximumWalletIdLength
			|| !StringComparer.Ordinal.Equals(canonicalWalletId, canonicalWalletId.Trim())
			|| canonicalWalletId.Any(char.IsControl))
		{
			throw new ArgumentException("A bounded canonical wallet identifier is required.", nameof(canonicalWalletId));
		}
		if (!Enum.IsDefined(trigger))
		{
			throw new ArgumentOutOfRangeException(nameof(trigger));
		}

		string? canonicalAcceptedId = null;
		if (acceptedTransactionIdHex is not null)
		{
			LiquidTransactionId parsed = LiquidTransactionId.ParseRpcHex(
				acceptedTransactionIdHex,
				nameof(acceptedTransactionIdHex));
			if (parsed.IsZero)
			{
				throw new ArgumentException("A nonzero accepted transaction identifier is required.", nameof(acceptedTransactionIdHex));
			}
			canonicalAcceptedId = parsed.CanonicalRpcHex;
		}

		if ((trigger == LiquidWalletUiRefreshTrigger.Manual && canonicalAcceptedId is not null)
			|| (trigger == LiquidWalletUiRefreshTrigger.AcceptedSend && canonicalAcceptedId is null))
		{
			throw new ArgumentException("The refresh trigger and accepted transaction identifier do not agree.", nameof(acceptedTransactionIdHex));
		}

		CanonicalWalletId = canonicalWalletId;
		Trigger = trigger;
		AcceptedTransactionIdHex = canonicalAcceptedId;
	}

	public string CanonicalWalletId { get; }
	public LiquidWalletUiRefreshTrigger Trigger { get; }
	public string? AcceptedTransactionIdHex { get; }
}
