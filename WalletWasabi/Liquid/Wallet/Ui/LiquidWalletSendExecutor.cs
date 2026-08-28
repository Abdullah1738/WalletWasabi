using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet.Sync;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 sections 1 and 6): the sole application-owned owner
/// of one ordinary Liquid send composition. It is the only code permitted to call both
/// <see cref="LiquidWalletNativeSigner.TrySignAndFinalize"/> and
/// <see cref="ElementsRpcClient.BroadcastExpectationBoundRawTransactionAsync"/>. Its single
/// operation performs, in the frozen order: validate, open one per-call scope, build the exact
/// spend plan, re-check cancellation, build one sign request, sign/finalize exactly once,
/// require a canonical local transaction id, re-check cancellation, move to the submitting
/// state, broadcast exactly once, cross-check the receipt id, and hand off to scan/refresh.
/// The executor is stateless across calls and retains no request, scope, result, transaction
/// bytes, or exception after completion. Its only configuration is the immutable non-secret
/// <see cref="ElementsPublicNetworkManifest"/> of the wallet-bound network, supplied by the
/// application composition layer at construction. Exactly one broadcast; no retry, no
/// fallback, no loop, no resubmission.
/// </summary>
internal sealed class LiquidWalletSendExecutor
{
	private readonly ElementsPublicNetworkManifest _manifest;

	internal LiquidWalletSendExecutor(ElementsPublicNetworkManifest manifest) =>
		_manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));

	/// <summary>
	/// Executes one ordinary Liquid send as a single fail-closed action per V2 section 6. Every
	/// failure before the submission boundary proves zero broadcast calls; every path at or
	/// after the boundary proves at most one broadcast call and records either a receipt-backed
	/// accepted status or an ambiguity/rejection status. The per-call scope is disposed and
	/// every owned byte array is zeroed in <c>finally</c>.
	/// </summary>
	public async Task<LiquidWalletUiSendExecutionResult> ExecuteAsync(
		LiquidWalletUiSendExecutionRequest request,
		ILiquidWalletSendExecutionScopeFactory scopeFactory,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(scopeFactory);

		// Step 1: validate the public request and cancellation before opening a scope.
		cancellationToken.ThrowIfCancellationRequested();

		// Step 2: open one per-call scope.
		using ILiquidWalletSendExecutionScope scope = scopeFactory.Open(request.WalletName);

		try
		{
			// Step 3: build the exact spend plan through the landed facade using the
			// executor-configured manifest, the scope's replay key/context, and the expected
			// revision. The wallet data directory is the session-supplied single source of
			// truth from the scope (the request carries no directory copy).
			LiquidWalletUiSpendPlan plan = LiquidWalletUiFacade.LoadAndCreateSpendPlan(
				scope.WalletDataDirectory,
				request.WalletName,
				_manifest,
				scope.ReplayProtectionKey,
				scope.ExternalWalletNetworkContext,
				request.SelectedOutPointHexes,
				request.ConfidentialDestinationAddress,
				request.DestinationAssetIdHex,
				request.DestinationAtomicUnits,
				request.ExplicitFeeAtomicUnits,
				request.ExpectedRevision);
			ulong sourceRevision = plan.SourceRevision;

			// Step 4: re-check cancellation.
			if (cancellationToken.IsCancellationRequested)
			{
				return PreSubmit(
					LiquidWalletUiSendExecutionStatus.CancelledBeforeSubmit,
					request,
					sourceRevision,
					"send-cancelled-before-submit");
			}

			// Step 5: build one sign request through the landed facade using the same manifest,
			// same public request, same expected revision, fresh source epoch, and the
			// expectation-bound funding source.
			ElementsExpectationBoundRawTransactionBatch fundingSource;
			try
			{
				fundingSource = await scope.AcquireFundingSourceAsync(request, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return PreSubmit(
					LiquidWalletUiSendExecutionStatus.CancelledBeforeSubmit,
					request,
					sourceRevision,
					"send-cancelled-before-submit");
			}
			catch (Exception)
			{
				// A funding-source acquisition failure is a pre-submit rejection: no frame was
				// encoded, no signer ran, and no broadcast is reachable.
				return PreSubmit(
					LiquidWalletUiSendExecutionStatus.RejectedBeforeSubmit,
					request,
					sourceRevision,
					"send-funding-source-unavailable");
			}

			LiquidWalletUiSignRequest signRequest;
			try
			{
				signRequest = LiquidWalletUiFacade.LoadAndCreateSignRequest(
					scope.WalletDataDirectory,
					request.WalletName,
					_manifest,
					scope.ReplayProtectionKey,
					scope.ExternalWalletNetworkContext,
					request.SelectedOutPointHexes,
					request.ConfidentialDestinationAddress,
					request.DestinationAssetIdHex,
					request.DestinationAtomicUnits,
					request.ExplicitFeeAtomicUnits,
					scope.SourceEpoch,
					fundingSource,
					request.PreviousTransactionIdsBySelectedInput,
					request.ExpectedRevision);
			}
			catch (Exception)
			{
				return PreSubmit(
					LiquidWalletUiSendExecutionStatus.RejectedBeforeSubmit,
					request,
					sourceRevision,
					"send-sign-request-rejected");
			}

			// Step 6: the scope owns the call-scoped native signer; invoke TrySignAndFinalize
			// exactly once. A false return is SigningRejected; no broadcast occurs.
			LiquidWalletNativeSigner signer = scope.Signer;

			if (!signer.TrySignAndFinalize(signRequest, out LiquidWalletUiSignedTransaction? signedTransaction)
				|| signedTransaction is null)
			{
				return PreSubmit(
					LiquidWalletUiSendExecutionStatus.SigningRejected,
					request,
					sourceRevision,
					"send-signing-rejected");
			}

			// Step 7: require a canonical local transaction id: present, exactly 64 lowercase
			// hex characters, and nonzero. The signer populates TransactionIdHex fail-closed;
			// this is the executor-side nonzero/canonical guard before broadcast is reachable.
			string localTransactionIdHex = signedTransaction.TransactionIdHex;
			if (!IsCanonicalNonzeroTransactionId(localTransactionIdHex))
			{
				return PreSubmit(
					LiquidWalletUiSendExecutionStatus.SigningRejected,
					request,
					sourceRevision,
					"send-local-txid-invalid");
			}

			// Step 8: re-check cancellation. Cancellation before this boundary is definitely
			// pre-submit.
			if (cancellationToken.IsCancellationRequested)
			{
				return new LiquidWalletUiSendExecutionResult(
					LiquidWalletUiSendExecutionStatus.CancelledBeforeSubmit,
					request.WalletName,
					_manifest.ManifestId,
					sourceRevision,
					localTransactionIdHex,
					acceptedTransactionIdHex: null,
					broadcastAttempted: false,
					refreshScheduled: false,
					"send-cancelled-before-submit");
			}

			// Step 9/10: submit the signed transaction exactly once under the node-probe lock with
			// the session's RPC client and effective fee asset; the standard
			// BroadcastExpectationBoundRawTransactionAsync performs its own pre-submit
			// fee-asset/generation observation under _probeLock (no fabricated expectation
			// object). The scope factory enforces the one-active-execution-per-wallet fence, and
			// this executor is stateless, so at most one broadcast call is reachable.
			ElementsExpectationBoundBroadcastReceipt receipt;
			try
			{
				receipt = await scope.RpcClient.BroadcastExpectationBoundRawTransactionAsync(
					expectedNodeExpectation: null,
					scope.ExpectedEffectiveFeeAsset,
					signedTransaction.SignedTransactionHex,
					cancellationToken).ConfigureAwait(false);
			}
			catch (Exception broadcastException)
			{
				return await ClassifyBroadcastFailureAsync(
					broadcastException,
					request,
					sourceRevision,
					localTransactionIdHex,
					scope,
					cancellationToken).ConfigureAwait(false);
			}

			// Step 11: cross-check the receipt transaction id against the local id, ordinal
			// equality. A mismatch is a protocol failure and a post-submit ambiguity (the node
			// may have accepted bytes before returning a false id).
			if (!StringComparer.Ordinal.Equals(
				receipt.AcceptedTransactionIdHex,
				localTransactionIdHex))
			{
				await TryScheduleManualRefreshAsync(scope, cancellationToken)
					.ConfigureAwait(false);
				return new LiquidWalletUiSendExecutionResult(
					LiquidWalletUiSendExecutionStatus.SubmissionAmbiguous,
					request.WalletName,
					_manifest.ManifestId,
					sourceRevision,
					localTransactionIdHex,
					receipt.AcceptedTransactionIdHex,
					broadcastAttempted: true,
					refreshScheduled: false,
					"send-submission-ambiguous");
			}

			// Step 12: on a matching receipt, the scope records the validated canonical accepted
			// transaction id (before consulting cancellation) and invokes the exact shared refresh
			// delegate once with Trigger = AcceptedSend. This schedules or performs the landed
			// fetch/sync path; it does not claim immediate confirmation. No scan intent is derived
			// and discarded here — intent derivation occurs exactly once inside the refresh service.
			try
			{
				await scope.ScheduleRefreshAsync(localTransactionIdHex, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception)
			{
				// The node accepted the transaction and the ids matched; a refresh-handoff
				// failure is never downgraded to a pre-submit failure and never retried.
				return new LiquidWalletUiSendExecutionResult(
					LiquidWalletUiSendExecutionStatus.AcceptedButRefreshFailed,
					request.WalletName,
					_manifest.ManifestId,
					sourceRevision,
					localTransactionIdHex,
					receipt.AcceptedTransactionIdHex,
					broadcastAttempted: true,
					refreshScheduled: false,
					"send-accepted-refresh-failed");
			}

			// Step 13: return one immutable public result.
			return new LiquidWalletUiSendExecutionResult(
				LiquidWalletUiSendExecutionStatus.AcceptedAndRefreshScheduled,
				request.WalletName,
				_manifest.ManifestId,
				sourceRevision,
				localTransactionIdHex,
				receipt.AcceptedTransactionIdHex,
				broadcastAttempted: true,
				refreshScheduled: true,
				"send-accepted");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// A cancellation that escaped an inner scope before the broadcast boundary is
			// pre-submit; zero broadcasts occurred.
			return new LiquidWalletUiSendExecutionResult(
				LiquidWalletUiSendExecutionStatus.CancelledBeforeSubmit,
				request.WalletName,
				_manifest.ManifestId,
				sourceRevision: 0,
				localTransactionIdHex: null,
				acceptedTransactionIdHex: null,
				broadcastAttempted: false,
				refreshScheduled: false,
				"send-cancelled-before-submit");
		}
		// Step 14: the using-scope disposes the per-call scope and zeroes all owned byte-array
		// buffers in finally, on every terminal and exceptional path.
	}

	private LiquidWalletUiSendExecutionResult PreSubmit(
		LiquidWalletUiSendExecutionStatus status,
		LiquidWalletUiSendExecutionRequest request,
		ulong sourceRevision,
		string displayMessage) =>
		new(
			status,
			request.WalletName,
			_manifest.ManifestId,
			sourceRevision,
			localTransactionIdHex: null,
			acceptedTransactionIdHex: null,
			broadcastAttempted: false,
			refreshScheduled: false,
			displayMessage);

	private async Task<LiquidWalletUiSendExecutionResult> ClassifyBroadcastFailureAsync(
		Exception broadcastException,
		LiquidWalletUiSendExecutionRequest request,
		ulong sourceRevision,
		string localTransactionIdHex,
		ILiquidWalletSendExecutionScope scope,
		CancellationToken cancellationToken)
	{
		// A pre-submit observation-phase RPC rejection is a pre-submit failure with zero
		// broadcasts. An RPC-kind rejection attributed to sendrawtransaction itself under the
		// fenced generation is SubmissionRejected. Every other failure (transport, timeout,
		// HTTP, protocol, cancellation during/after submission, or any failure whose stage
		// cannot be proven pre-submit) is SubmissionAmbiguous. No retry, loop, or second call
		// follows any outcome.
		if (broadcastException is ElementsBroadcastStageException stageException)
		{
			if (stageException.Stage == ElementsBroadcastStage.PreSubmitObservation)
			{
				return new LiquidWalletUiSendExecutionResult(
					LiquidWalletUiSendExecutionStatus.RejectedBeforeSubmit,
					request.WalletName,
					_manifest.ManifestId,
					sourceRevision,
					localTransactionIdHex,
					acceptedTransactionIdHex: null,
					broadcastAttempted: false,
					refreshScheduled: false,
					"send-rejected-before-submit");
			}

			// Submit stage: an RPC-kind rejection attributed to sendrawtransaction itself.
			return new LiquidWalletUiSendExecutionResult(
				LiquidWalletUiSendExecutionStatus.SubmissionRejected,
				request.WalletName,
				_manifest.ManifestId,
				sourceRevision,
				localTransactionIdHex,
				acceptedTransactionIdHex: null,
				broadcastAttempted: true,
				refreshScheduled: false,
				"send-submission-rejected");
		}

		// Any non-RPC-kind failure, or a failure whose stage cannot be proven pre-submit, is
		// ambiguous. Invoke a best-effort manual discovery refresh so bounded node discovery may
		// find the transaction; it records no accepted id, never transforms ambiguity into
		// acceptance, and never triggers a resubmission.
		await TryScheduleManualRefreshAsync(scope, cancellationToken)
			.ConfigureAwait(false);
		return new LiquidWalletUiSendExecutionResult(
			LiquidWalletUiSendExecutionStatus.SubmissionAmbiguous,
			request.WalletName,
			_manifest.ManifestId,
			sourceRevision,
			localTransactionIdHex,
			acceptedTransactionIdHex: null,
			broadcastAttempted: true,
			refreshScheduled: false,
			"send-submission-ambiguous");
	}

	private static async Task TryScheduleManualRefreshAsync(
		ILiquidWalletSendExecutionScope scope,
		CancellationToken cancellationToken)
	{
		try
		{
			await scope.ScheduleManualRefreshAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Best-effort: the ambiguous outcome is already determined; a handoff failure is
			// swallowed here so the ambiguity result is returned unchanged and no resubmission
			// is ever triggered.
		}
	}

	private static bool IsCanonicalNonzeroTransactionId(string? transactionIdHex)
	{
		if (transactionIdHex is null || transactionIdHex.Length != 64)
		{
			return false;
		}

		bool hasNonzero = false;
		foreach (char character in transactionIdHex)
		{
			bool isDigit = char.IsAsciiDigit(character);
			bool isLowerHex = character is >= 'a' and <= 'f';
			if (!isDigit && !isLowerHex)
			{
				return false;
			}
			hasNonzero |= character != '0';
		}
		return hasNonzero;
	}
}
