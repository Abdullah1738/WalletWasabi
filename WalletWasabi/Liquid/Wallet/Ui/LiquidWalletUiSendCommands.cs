using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (composition-root amendment V2; 2026-08-21 amendment
/// section 4): the public, non-secret command bundle the Fluent Liquid send surface receives.
/// It exposes exactly two get-only delegates — plan and execute — over the public immutable
/// V2 plan/request/result types. Every parameter and return type visible through those
/// delegates is a public immutable type plus <see cref="CancellationToken"/>/<see cref="Task"/>;
/// no internal type, key/context memory, signer, descriptor, SLIP-77 value, source epoch,
/// funding bytes, expectation, RPC client, scope/factory, refresh service, endpoint, or
/// credentials cross the public signature. The composition root constructs this once over the
/// WalletWasabi-resident command surface; Fluent never fabricates or replaces either delegate.
/// </summary>
public sealed class LiquidWalletUiSendCommands
{
	internal LiquidWalletUiSendCommands(
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> executeAsync)
	{
		ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
	}

	/// <summary>
	/// Executes one ordinary Liquid send under the application-layer one-active-execution-per-wallet
	/// fence. The delegate body runs entirely inside the WalletWasabi assembly: it resolves the
	/// authenticated session, opens one internal per-call scope, runs the internal executor, and
	/// returns the public immutable result. Exactly one broadcast; no retry, no fallback.
	/// </summary>
	public Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> ExecuteAsync { get; }
}
