namespace WalletWasabi.Liquid.Wallet.Ui;

public enum LiquidWalletUiRefreshStatus
{
	Committed,
	NoCandidates,
}

public sealed class LiquidWalletUiRefreshResult
{
	public LiquidWalletUiRefreshResult(
		LiquidWalletUiRefreshStatus status,
		string canonicalWalletId,
		LiquidWalletUiRefreshTrigger trigger,
		string? acceptedTransactionIdHex,
		int candidateCount,
		int appliedTransactionCount,
		ulong resultRevision,
		ulong resultGeneration,
		bool isPostSubmit,
		bool handoffPublished)
	{
		if (!Enum.IsDefined(status))
		{
			throw new ArgumentOutOfRangeException(nameof(status));
		}
		LiquidWalletUiRefreshRequest validatedRequest = new(canonicalWalletId, trigger, acceptedTransactionIdHex);
		if (candidateCount < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(candidateCount));
		}
		if (appliedTransactionCount < 0 || appliedTransactionCount > candidateCount)
		{
			throw new ArgumentOutOfRangeException(nameof(appliedTransactionCount));
		}
		if (isPostSubmit != (trigger == LiquidWalletUiRefreshTrigger.AcceptedSend))
		{
			throw new ArgumentException("Post-submit status must match the refresh trigger.", nameof(isPostSubmit));
		}
		if (status == LiquidWalletUiRefreshStatus.NoCandidates
			&& (candidateCount != 0 || appliedTransactionCount != 0 || handoffPublished))
		{
			throw new ArgumentException("A no-candidate result cannot report applied or published state.", nameof(status));
		}

		Status = status;
		CanonicalWalletId = validatedRequest.CanonicalWalletId;
		Trigger = validatedRequest.Trigger;
		AcceptedTransactionIdHex = validatedRequest.AcceptedTransactionIdHex;
		CandidateCount = candidateCount;
		AppliedTransactionCount = appliedTransactionCount;
		ResultRevision = resultRevision;
		ResultGeneration = resultGeneration;
		IsPostSubmit = isPostSubmit;
		HandoffPublished = handoffPublished;
	}

	public LiquidWalletUiRefreshStatus Status { get; }
	public string CanonicalWalletId { get; }
	public LiquidWalletUiRefreshTrigger Trigger { get; }
	public string? AcceptedTransactionIdHex { get; }
	public int CandidateCount { get; }
	public int AppliedTransactionCount { get; }
	public ulong ResultRevision { get; }
	public ulong ResultGeneration { get; }
	public bool IsPostSubmit { get; }
	public bool HandoffPublished { get; }
}
