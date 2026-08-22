namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 section 7): the closed set of terminal outcomes of
/// one ordinary Liquid send execution. The set is exhaustive and frozen; no other status is
/// representable.
/// </summary>
public enum LiquidWalletUiSendExecutionStatus
{
	/// <summary>
	/// Exactly one receipt was obtained, the node-accepted id matched the locally computed id,
	/// and the scan/state-refresh handoff was scheduled successfully.
	/// </summary>
	AcceptedAndRefreshScheduled,

	/// <summary>A rejection occurred before any broadcast was issued; zero broadcasts occurred.</summary>
	RejectedBeforeSubmit,

	/// <summary>The signer refused or the local transaction id was absent/malformed; no broadcast occurred.</summary>
	SigningRejected,

	/// <summary>Cancellation was observed before the submission boundary; zero broadcasts occurred.</summary>
	CancelledBeforeSubmit,

	/// <summary>
	/// An RPC-kind rejection attributed to <c>sendrawtransaction</c> itself under a fenced
	/// unchanged generation. Exactly one submission; no retry follows.
	/// </summary>
	SubmissionRejected,

	/// <summary>
	/// The outcome cannot be proven: a transport/timeout/protocol failure, a cancellation during
	/// or after submission, a malformed acceptance, a local/receipt id mismatch, a post-submit
	/// generation change, or any failure whose stage cannot be proven pre-submit. No automatic
	/// resubmission follows.
	/// </summary>
	SubmissionAmbiguous,

	/// <summary>
	/// Node acceptance and the id cross-check succeeded but the refresh handoff failed. Never
	/// downgraded to a pre-submit failure; no broadcast retry follows.
	/// </summary>
	AcceptedButRefreshFailed,
}

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 section 7): the public, immutable, non-secret result
/// of one ordinary Liquid send execution. It carries the status, the wallet name, the network
/// manifest id, the source revision, the locally computed transaction id when signing
/// completed, the node-accepted transaction id only when a receipt exists, the
/// <see cref="BroadcastAttempted"/> and <see cref="RefreshScheduled"/> flags, and a
/// privacy-redacted display message/code. The display message/code contains no transaction
/// bytes, endpoint, credentials, descriptor, key identifiers, source epoch, outpoints, or
/// exception text (and no RPC exception <c>Message</c>, which may embed method names and node
/// detail). No secret ever crosses this boundary.
/// </summary>
public sealed class LiquidWalletUiSendExecutionResult
{
	internal LiquidWalletUiSendExecutionResult(
		LiquidWalletUiSendExecutionStatus status,
		string walletName,
		string networkManifestId,
		ulong sourceRevision,
		string? localTransactionIdHex,
		string? acceptedTransactionIdHex,
		bool broadcastAttempted,
		bool refreshScheduled,
		string displayMessage)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletName);
		ArgumentException.ThrowIfNullOrEmpty(networkManifestId);
		ArgumentNullException.ThrowIfNull(displayMessage);

		Status = status;
		WalletName = walletName;
		NetworkManifestId = networkManifestId;
		SourceRevision = sourceRevision;
		LocalTransactionIdHex = localTransactionIdHex;
		AcceptedTransactionIdHex = acceptedTransactionIdHex;
		BroadcastAttempted = broadcastAttempted;
		RefreshScheduled = refreshScheduled;
		DisplayMessage = displayMessage;
	}

	public LiquidWalletUiSendExecutionStatus Status { get; }
	public string WalletName { get; }
	public string NetworkManifestId { get; }
	public ulong SourceRevision { get; }

	/// <summary>The locally computed canonical transaction id, present only once signing completed.</summary>
	public string? LocalTransactionIdHex { get; }

	/// <summary>The node-accepted canonical transaction id, present only when a receipt exists.</summary>
	public string? AcceptedTransactionIdHex { get; }

	public bool BroadcastAttempted { get; }
	public bool RefreshScheduled { get; }

	/// <summary>
	/// A privacy-redacted, display-safe message/code. Carries no transaction bytes, endpoint,
	/// credentials, descriptor, key identifiers, source epoch, outpoints, or exception text.
	/// </summary>
	public string DisplayMessage { get; }
}
