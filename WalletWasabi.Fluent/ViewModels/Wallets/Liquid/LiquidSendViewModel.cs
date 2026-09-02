using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Fluent.Models.Wallets.Liquid;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The Liquid send view model, parallel to the BTC <c>SendViewModel</c>
/// (not a subtype): its primary content is the exact multiasset spend plan
/// — a summary of the selected inputs, the confidential destinations, the
/// per-asset amounts, and the explicit fee, bound from a
/// <see cref="LiquidWalletUiSpendPlan"/> produced by
/// <see cref="LiquidWalletModel.CreateSpendPlan"/>. The "Sign &amp; broadcast"
/// action executes one ordinary send through the caller-supplied
/// <see cref="ExecuteSendCommand"/> delegate (the application session layer's
/// narrow non-secret surface) and renders the returned status, display
/// message, and transaction ids; there is deliberately no transaction
/// preview, no fee-rate slider, no coin list, no CoinJoin status, no music
/// box, and no history table — a Liquid managed wallet has no CoinJoin. The
/// explicit fee is a caller-supplied atomic-units input denominated in the
/// pegged asset; there is no fee-rate estimation and no fee-market data
/// source. Fail-closed: any rejection from the landed load, spend-plan, or
/// send-execution surface surfaces as-is — no retry, no fallback, no
/// cached-plan substitution, and no fabricated success.
/// </summary>
[NavigationMetaData(
	Title = "Send",
	Caption = "Build a Liquid wallet spend plan",
	IconName = "wallet_action_send",
	Order = 7,
	Category = "Wallet",
	Keywords = new[] { "Wallet", "Send", "Action", },
	NavBarPosition = NavBarPosition.None,
	NavigationTarget = NavigationTarget.DialogScreen,
	Searchable = false)]
public partial class LiquidSendViewModel : RoutableViewModel
{
	private readonly ObservableAsPropertyHelper<IReadOnlyList<LiquidSelectableOutputItemViewModel>> _selectableOutputs;

	[AutoNotify] private LiquidSpendPlanItemViewModel? _spendPlan;
	[AutoNotify] private long _explicitFeeAtomicUnits;
	[AutoNotify] private LiquidWalletUiSendExecutionResult? _executionResult;
	[AutoNotify] private string? _executionErrorText;

	public LiquidSendViewModel(
		UiContext uiContext,
		LiquidWalletModel walletModel,
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>? executeSendCommand = null,
		string? preSelectedAssetIdHex = null)
		: base(uiContext)
	{
		WalletModel = walletModel;
		ExecuteSendCommand = executeSendCommand;
		Recipient = new LiquidSendRecipientViewModel(uiContext, walletModel);

		// The per-balance-row Send affordance carries the row's asset id so the
		// picker opens holding that asset; null is the plain top-level Send path
		// (the landed pegged-first default). Presentation-only: the id only
		// chooses which option the picker holds.
		if (preSelectedAssetIdHex is not null)
		{
			Recipient.PreSelectAsset(preSelectedAssetIdHex);
		}

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
		EnableBack = true;

		// The coin-control list binds from the wallet's selectable-output
		// stream: each emission replaces the row set wholesale with a fresh
		// projection of the immutable snapshot. Every projected row starts
		// selected — this preserves the landed empty-field semantics exactly
		// (an empty SelectedOutPointHexesText meant every spendable outpoint
		// funded the plan, because the coin-control selection is drawn from
		// the spendable set). A refresh re-selects the fresh rows
		// deterministically. Seed with an empty row set so SelectableOutputs
		// is never null before the first emission: an empty selectable set
		// means an empty list (no fabricated outputs).
		_selectableOutputs = walletModel.SelectableOutputs
			.ObserveOn(RxApp.MainThreadScheduler)
			.Select(snapshot => (IReadOnlyList<LiquidSelectableOutputItemViewModel>)snapshot.Outputs
				.Select(output =>
				{
					var row = new LiquidSelectableOutputItemViewModel(uiContext, output)
					{
						IsSelected = true,
					};
					// A checkbox toggle rebuilds the selected-outpoint set.
					row.WhenAnyValue(x => x.IsSelected)
						.Subscribe(_ => RebuildSelectedOutPointHexes(null));
					return row;
				})
				.ToArray())
			.ToProperty(
				this,
				x => x.SelectableOutputs,
				initialValue: Array.Empty<LiquidSelectableOutputItemViewModel>());

		// Rebuild the selected-outpoint set after the bound list has applied
		// the new row set: when SelectableOutputs changes, a bound
		// ItemsControl rebuilds its containers, so deferring to the
		// dispatcher lets the fresh all-selected rows settle before the set
		// is recomputed.
		this.WhenAnyValue(x => x.SelectableOutputs)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(rows => RxApp.MainThreadScheduler.Schedule(
				rows,
				(_, current) =>
				{
					RebuildSelectedOutPointHexes(current);
					return System.Reactive.Disposables.Disposable.Empty;
				}));

		// Builds the plan from the current recipient/asset/amount/fee
		// inputs. The wallet data directory and the key/context spans are
		// caller-supplied at the command call site (key management is
		// outside this layer); this view model never holds them.
		BuildPlanCommand = new BuildSpendPlanCommand(this);

		// Executes one ordinary send through the caller-supplied session
		// delegate and renders the returned result. Key management stays in
		// the session layer: this command carries only the public,
		// non-secret request values. Fail-closed: any rejection surfaces
		// as-is — no fabricated success, no retry, no fallback.
		SendExecution = ReactiveCommand.CreateFromTask(ExecuteSendPlanAsync);
	}

	public LiquidWalletModel WalletModel { get; }

	/// <summary>
	/// The single narrow non-secret send-execution command surface (MANAGED-WALLET-UI-SEND-EXECUTE-001,
	/// V2 section 8): the application composition layer supplies one
	/// <see cref="Func{T1,T2,TResult}"/> over the public request/result types at construction. This
	/// view model never receives an executor scope factory, a secret-bearing parameter, or any
	/// key/context. It is null only when no Liquid runtime composition is wired (no session source);
	/// it is never a fabricated no-op.
	/// </summary>
	public Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>? ExecuteSendCommand { get; }

	public LiquidSendRecipientViewModel Recipient { get; }

	/// <summary>
	/// The coin-control rows bound from the wallet's selectable-output
	/// snapshot: one checkable row per spendable wallet output, every row
	/// selected by default (the landed empty-field semantics — an empty
	/// selected-outpoint text meant every spendable outpoint funded the
	/// plan). Never null; an empty selectable set means an empty list.
	/// </summary>
	public IReadOnlyList<LiquidSelectableOutputItemViewModel> SelectableOutputs => _selectableOutputs.Value;

	/// <summary>
	/// The exact selected-outpoint hex set the plan/sign path consumes: the
	/// 72-character consensus-bytes hex of every checked row, in list order.
	/// Coin control only chooses which wallet outputs fund the plan; it never
	/// fabricates an output, never admits a non-wallet outpoint, and never
	/// bypasses the landed exact-plan validation.
	/// </summary>
	public IReadOnlyList<string> SelectedOutPointHexes { get; private set; } = [];

	public ICommand BuildPlanCommand { get; }

	/// <summary>
	/// The "Sign &amp; broadcast" action: builds the non-secret send-execution
	/// request from the current inputs and invokes <see cref="ExecuteSendCommand"/>,
	/// binding the returned status, message, and transaction ids as the flow's
	/// terminal content. Enabled only when the session layer supplied an
	/// executor at construction.
	/// </summary>
	public ICommand ExecuteSendPlanCommand => SendExecution;

	// The typed reactive command behind ExecuteSendPlanCommand: one ordinary
	// send per invocation, awaited by the caller.
	public ReactiveCommand<Unit, Unit> SendExecution { get; }

	/// <summary>True when the session layer wired a send-execution delegate.</summary>
	public bool IsSendExecutionAvailable => ExecuteSendCommand is not null;

	private async Task ExecuteSendPlanAsync(CancellationToken cancellationToken)
	{
		ExecutionResult = null;
		ExecutionErrorText = null;

		if (ExecuteSendCommand is not { } executeSend)
		{
			ExecutionErrorText = "The Liquid send execution surface is not wired for this wallet session.";
			return;
		}

		string[] selectedOutPointHexes = [.. SelectedOutPointHexes];

		try
		{
			// One null row per selected outpoint: the session executor replaces
			// them with the previous-transaction-id dependency rows derived from
			// the open session's current selectable outputs (the same source the
			// harness send phase uses). The expected revision is the freshness
			// fence — the snapshot revision this view last rendered.
			var request = new LiquidWalletUiSendExecutionRequest(
				WalletModel.Name,
				selectedOutPointHexes,
				Recipient.ConfidentialAddressText,
				Recipient.AssetIdHex,
				Recipient.AtomicUnits,
				ExplicitFeeAtomicUnits,
				Snapshot?.Revision ?? 0,
				new IReadOnlyList<string>?[selectedOutPointHexes.Length]);

			ExecutionResult = await executeSend(request, cancellationToken).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Fail-closed: the landed rejection surfaces as-is; no success is
			// fabricated and the plan is left untouched.
			ExecutionErrorText = ex.Message;
		}
	}

	// Builds the exact spend plan from the current inputs and binds it as
	// the send flow's primary content. The wallet data directory and the
	// caller's key/context spans are supplied by the application lifetime
	// layer at the call site (key management is outside this layer); this
	// view model never holds them.
	private void BuildPlan(
		string walletDataDir,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		string[] selectedOutPointHexes = [.. SelectedOutPointHexes];

		SpendPlan = new LiquidSpendPlanItemViewModel(UiContext, WalletModel.CreateSpendPlan(
			walletDataDir,
			key,
			externalWalletNetworkContext,
			selectedOutPointHexes,
			Recipient.ConfidentialAddressText,
			Recipient.AssetIdHex,
			Recipient.AtomicUnits,
			ExplicitFeeAtomicUnits,
			Snapshot?.Revision));
	}

	// The snapshot revision the UI last rendered — the caller's freshness
	// fence wired to the landed plan-time revision fence.
	private LiquidWalletUiSnapshot? Snapshot => WalletModel.Snapshot;

	// Recomputes the exact selected-outpoint hex set from the checked rows,
	// in list order. Called on every checkbox toggle (null → read the current
	// row set) and (deferred) after a snapshot refresh reseeds the row set.
	private void RebuildSelectedOutPointHexes(IReadOnlyList<LiquidSelectableOutputItemViewModel>? rows)
	{
		rows ??= _selectableOutputs?.Value;
		SelectedOutPointHexes = rows is null
			? []
			: [.. rows.Where(row => row.IsSelected).Select(row => row.SelectionId)];
	}

	// The build-plan command: the application lifetime layer invokes it
	// with the wallet data directory and the caller's key/context spans
	// (key management is outside this layer). Fail-closed: any rejection
	// from the landed load or spend-plan surface surfaces as-is.
	private sealed class BuildSpendPlanCommand(LiquidSendViewModel owner) : ICommand
	{
		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter)
		{
			if (parameter is not BuildSpendPlanParameters parameters)
			{
				throw new ArgumentException(
					"The Liquid build-plan command requires the wallet data directory and the caller's key/context spans.",
					nameof(parameter));
			}

			owner.BuildPlan(
				parameters.WalletDataDir,
				parameters.Key.Span,
				parameters.ExternalWalletNetworkContext.Span);
		}

		public void RaiseCanExecuteChanged() =>
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}

	// The command parameters: the wallet data directory plus the caller's
	// key/context spans. The caller owns the spans' provenance and
	// lifetime; the view model never retains them.
	public sealed record BuildSpendPlanParameters(
		string WalletDataDir,
		ReadOnlyMemory<byte> Key,
		ReadOnlyMemory<byte> ExternalWalletNetworkContext);
}
