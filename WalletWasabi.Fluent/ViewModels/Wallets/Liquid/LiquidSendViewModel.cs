using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
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
/// <see cref="LiquidWalletModel.CreateSpendPlan"/>. There is deliberately
/// no sign command, no broadcast command, no transaction preview, no
/// fee-rate slider, no coin list, no CoinJoin status, no music box, and no
/// history table: signing and broadcast are a later slice, and a Liquid
/// managed wallet has no CoinJoin. The explicit fee is a caller-supplied
/// atomic-units input denominated in the pegged asset; there is no fee-rate
/// estimation and no fee-market data source. Fail-closed: any rejection
/// from the landed load or spend-plan surface surfaces as-is — no retry,
/// no fallback, no cached-plan substitution.
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
	[AutoNotify] private LiquidWalletUiSpendPlan? _spendPlan;
	[AutoNotify] private string _selectedOutPointHexesText = "";
	[AutoNotify] private long _explicitFeeAtomicUnits;

	public LiquidSendViewModel(
		UiContext uiContext,
		LiquidWalletModel walletModel,
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>? executeSendCommand = null)
		: base(uiContext)
	{
		WalletModel = walletModel;
		ExecuteSendCommand = executeSendCommand;
		Recipient = new LiquidSendRecipientViewModel(uiContext);

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
		EnableBack = true;

		// Builds the plan from the current recipient/asset/amount/fee
		// inputs. The wallet data directory and the key/context spans are
		// caller-supplied at the command call site (key management is
		// outside this layer); this view model never holds them.
		BuildPlanCommand = new BuildSpendPlanCommand(this);
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

	public ICommand BuildPlanCommand { get; }

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
		string[] selectedOutPointHexes = SelectedOutPointHexesText.Split(
			['\r', '\n', ' ', ',', ';'],
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		SpendPlan = WalletModel.CreateSpendPlan(
			walletDataDir,
			key,
			externalWalletNetworkContext,
			selectedOutPointHexes,
			Recipient.ConfidentialAddressText,
			Recipient.AssetIdHex,
			Recipient.AtomicUnits,
			ExplicitFeeAtomicUnits,
			Snapshot?.Revision);
	}

	// The snapshot revision the UI last rendered — the caller's freshness
	// fence wired to the landed plan-time revision fence.
	private LiquidWalletUiSnapshot? Snapshot => WalletModel.Snapshot;

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
