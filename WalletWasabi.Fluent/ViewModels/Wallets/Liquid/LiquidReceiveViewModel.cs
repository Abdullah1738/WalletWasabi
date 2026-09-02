using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Fluent.Models.Wallets.Liquid;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The Liquid receive surface, parallel to the BTC <c>ReceiveViewModel</c>
/// but confidential-address native: on activation it derives the wallet's
/// next confidential receive address via
/// <see cref="LiquidWalletModel.CreateNextReceiveAddress"/> (from the
/// caller-supplied next-receive script and blinding key captured at open
/// time) and displays the returned
/// <see cref="LiquidWalletUiReceiveAddress.ConfidentialAddressText"/> as the
/// primary address, with <see cref="LiquidWalletUiReceiveAddress.UnconfidentialAddressText"/>
/// shown alongside it and a QR binding of the confidential form. It carries
/// no script-type picker (a Liquid managed wallet's receive scripts are
/// caller-derived, not user-selected between SegWit/Taproot), no HD
/// gap-limit surface, and no hardware-wallet show-on-device command in this
/// slice.
/// </summary>
[NavigationMetaData(
	Title = "Receive",
	Caption = "Display Liquid wallet receive dialog",
	IconName = "wallet_action_receive",
	Order = 6,
	Category = "Wallet",
	Keywords = new[] { "Wallet", "Receive", "Action", },
	NavBarPosition = NavBarPosition.None,
	NavigationTarget = NavigationTarget.DialogScreen,
	Searchable = false)]
public partial class LiquidReceiveViewModel : RoutableViewModel
{
	private readonly LiquidWalletModel _wallet;

	[AutoNotify] private string _confidentialAddressText = "";
	[AutoNotify] private string _unconfidentialAddressText = "";
	[AutoNotify] private string _labelText = "";
	[AutoNotify] private string? _labelSaveErrorText;

	public LiquidReceiveViewModel(UiContext uiContext, LiquidWalletModel wallet)
		: base(uiContext)
	{
		_wallet = wallet;

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
		EnableBack = true;

		CopyAddressCommand = ReactiveCommand.CreateFromTask(() =>
			UiContext.Clipboard.SetTextAsync(ConfidentialAddressText));

		// ObserveOn the main-thread scheduler so the CanExecute change that
		// completes the save reaches the bound Save button on the UI thread;
		// the landed receive view renders it and an off-thread completion
		// would touch Avalonia state from a background thread.
		SaveLabel = ReactiveCommand.CreateFromTask(SaveLabelAsync, outputScheduler: RxApp.MainThreadScheduler);

		NextCommand = CancelCommand;
	}

	public ICommand CopyAddressCommand { get; }

	/// <summary>
	/// The Save/Confirm action for the receive label: persists the comma-separated
	/// label set for the current next-receive index through the model's durable
	/// write path; an empty field clears the label. Fail-closed: any landed
	/// rejection surfaces as-is.
	/// </summary>
	public ReactiveCommand<Unit, Unit> SaveLabel { get; }

	public IObservable<bool[,]>? QrCode { get; private set; }

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// Derive the confidential receive address on activation from the
		// caller-supplied next-receive script and blinding key. Fail-closed:
		// any rejection from the landed derivation surfaces as-is.
		LiquidWalletUiReceiveAddress address = _wallet.CreateNextReceiveAddress();

		ConfidentialAddressText = address.ConfidentialAddressText;
		UnconfidentialAddressText = address.UnconfidentialAddressText;
		QrCode = UiContext.QrCodeGenerator.Generate(address.ConfidentialAddressText.ToUpperInvariant());

		// Surface the existing durable label (if any) bound to this next
		// receive index, joined as Wasabi's comma-separated label convention.
		LabelText = string.Join(", ", _wallet.NextReceiveLabels);
	}

	private async System.Threading.Tasks.Task SaveLabelAsync(System.Threading.CancellationToken cancellationToken)
	{
		LabelSaveErrorText = null;

		// Wasabi's label convention: comma-separated suggestion labels, each
		// trimmed; empty entries dropped. An empty field yields an empty set,
		// which clears the label.
		string[] labels = LabelText
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		try
		{
			await _wallet.SetNextReceiveLabelsAsync(labels, cancellationToken).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Fail-closed: the landed rejection surfaces as-is; no success is
			// fabricated and the durable label set is left untouched.
			LabelSaveErrorText = ex.Message;
		}
	}
}
