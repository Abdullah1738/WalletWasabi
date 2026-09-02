using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 section 3): the internal factory the application
/// wallet-lifetime layer implements or constructs. For each send call it opens one fresh,
/// disposable <see cref="ILiquidWalletSendExecutionScope"/> whose contents are exactly the
/// per-call secret-bearing and node-bound values the executor needs. The application command
/// service resolves the authenticated session for the named wallet and re-derives every
/// secret-bearing value from the session's retained authenticated master at scope-open time —
/// never re-reading a file, never asking the caller, never a service locator.
/// <see cref="LiquidSendViewModel"/>-side presentation code never implements this factory and
/// never retains the resulting scope.
/// </summary>
internal interface ILiquidWalletSendExecutionScopeFactory
{
	/// <summary>
	/// Opens one fresh per-call execution scope for the named wallet. The returned scope owns
	/// its mutable byte arrays and the call-scoped signer it constructs; it never owns the
	/// shared application RPC client. The caller disposes the scope exactly once in
	/// <c>finally</c>.
	/// </summary>
	ILiquidWalletSendExecutionScope Open(string walletName);
}

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 section 3): one per-call, disposable execution
/// scope. It owns the replay-protection key and external wallet-network context, the fresh
/// 32-byte source epoch, the real caller-owned <see cref="ILiquidWalletSigner"/> key owner,
/// the public spend descriptor and its highest derivation index, the SLIP-77 master, the
/// expectation-bound funding source, and the application state-refresh callback — all as
/// owned mutable byte arrays or owned references. Disposal zeroizes every owned mutable byte
/// array (replay key/context, source epoch, SLIP-77 copy, descriptor copy, and transient
/// buffers) and disposes the call-scoped signer it owns. It never disposes the shared
/// application-owned <see cref="ElementsRpcClient"/>; that ownership is explicit.
/// </summary>
internal interface ILiquidWalletSendExecutionScope : IDisposable
{
	/// <summary>The replay-protection key as an owned mutable byte array (zeroed on dispose).</summary>
	byte[] ReplayProtectionKey { get; }

	/// <summary>The external wallet-network context as an owned mutable byte array (zeroed on dispose).</summary>
	byte[] ExternalWalletNetworkContext { get; }

	/// <summary>The fresh 32-byte source epoch as an owned mutable byte array (zeroed on dispose).</summary>
	byte[] SourceEpoch { get; }

	/// <summary>The real caller-owned key owner (never disposed by this scope).</summary>
	ILiquidWalletSigner KeyOwner { get; }

	/// <summary>
	/// The call-scoped native signer, owned and disposed by this scope (V2 section 3 /
	/// amendment section 6: the signer the scope constructs is the one it owns). Constructed
	/// from <see cref="KeyOwner"/>, <see cref="DescriptorString"/>, <see cref="LastIndex"/>,
	/// and <see cref="Slip77MasterKey"/>; disposed after the owned byte arrays are zeroized.
	/// </summary>
	LiquidWalletNativeSigner Signer { get; }

	/// <summary>The public spend descriptor as an owned mutable byte-array copy (zeroed on dispose).</summary>
	byte[] DescriptorBytes { get; }

	/// <summary>The public spend descriptor text (decoded from <see cref="DescriptorBytes"/>).</summary>
	string DescriptorString { get; }

	/// <summary>The highest derivation index bound to the descriptor.</summary>
	ulong LastIndex { get; }

	/// <summary>The SLIP-77 master blinding key as an owned mutable byte array (zeroed on dispose).</summary>
	byte[] Slip77MasterKey { get; }

	/// <summary>The shared application-owned RPC client (never disposed by this scope).</summary>
	ElementsRpcClient RpcClient { get; }

	/// <summary>The expected effective fee asset (canonical RPC hex).</summary>
	string ExpectedEffectiveFeeAsset { get; }

	/// <summary>
	/// Reserves the wallet-owned branch-1 confidential change address for one send. Reservation
	/// happens lazily on first request within the scope (durable, generation-fenced, never rolled
	/// back; gaps acceptable) and is cached so both facade calls of one send observe the same
	/// reserved address — no double-reservation of branch-1 indexes across the two facade calls.
	/// The returned value is the canonical confidential change address string; no key material is
	/// exposed. Returns <see langword="false"/> when no change address can be reserved.
	/// </summary>
	bool TryReserveChangeDestination(out string? changeAddress);

	/// <summary>
	/// The single source of truth for the wallet's landed state directory, taken from the
	/// authenticated session. The send request carries no directory copy; the executor loads
	/// state from this session-supplied directory.
	/// </summary>
	string WalletDataDirectory { get; }

	/// <summary>
	/// Acquires the expectation-bound funding source for the request's selected inputs under the
	/// same expectation profile, before signing. The scope derives the raw-transaction requests
	/// from the request's selected outpoints and previous-transaction-id dependency rows. The
	/// returned batch is caller-owned.
	/// </summary>
	Task<ElementsExpectationBoundRawTransactionBatch> AcquireFundingSourceAsync(
		LiquidWalletUiSendExecutionRequest request,
		CancellationToken cancellationToken);

	/// <summary>
	/// The application-owned accepted-send state-refresh handoff: records the matching
	/// node-accepted canonical transaction id and invokes the exact shared refresh command once
	/// with <see cref="LiquidWalletUiRefreshTrigger.AcceptedSend"/>. The accepted id is recorded
	/// before cancellation is consulted, so caller cancellation can never erase knowledge of a
	/// proven accepted transaction. It does not claim immediate confirmation.
	/// </summary>
	Task ScheduleRefreshAsync(
		string canonicalTransactionIdHex,
		CancellationToken cancellationToken);

	/// <summary>
	/// The application-owned manual/ambiguity state-refresh handoff: invokes the exact shared
	/// refresh command once with <see cref="LiquidWalletUiRefreshTrigger.Manual"/> and no accepted
	/// id, so bounded node discovery may find a transaction whose submission outcome is ambiguous.
	/// It records no accepted id and never transforms ambiguity into acceptance.
	/// </summary>
	Task ScheduleManualRefreshAsync(CancellationToken cancellationToken);
}
