using System.Reactive.Disposables;
using System.Reactive.Linq;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.ViewModels.AddWallet.Liquid;

/// <summary>
/// The Liquid wallet-name entry page, parallel to the BTC
/// <c>WalletNamePageViewModel</c> but validating against the Liquid wallet
/// directory (a name is taken when a wallet file already exists there) instead
/// of the BTC <c>WalletRepository</c>. On continue it routes to the recovery
/// words page for either the create or the recover path.
/// </summary>
[NavigationMetaData(Title = "Liquid Wallet Name")]
public partial class LiquidWalletNamePageViewModel : RoutableViewModel
{
	private readonly LiquidWalletCreationMode _mode;
	[AutoNotify] private string _walletName = "";

	public LiquidWalletNamePageViewModel(UiContext uiContext, LiquidWalletCreationMode mode) : base(uiContext)
	{
		_mode = mode;

		EnableBack = true;

		var nextCommandCanExecute =
			this.WhenAnyValue(x => x.WalletName)
				.Select(_ => !Validations.Any && !string.IsNullOrWhiteSpace(WalletName));

		NextCommand = ReactiveCommand.Create(OnNext, nextCommandCanExecute);

		this.ValidateProperty(x => x.WalletName, ValidateWalletName);
	}

	private void OnNext()
	{
		Navigate().To().LiquidRecoveryWordsPage(_mode, WalletName.Trim());
	}

	private void ValidateWalletName(IValidationErrors errors)
	{
		if (string.IsNullOrWhiteSpace(WalletName))
		{
			return;
		}

		string candidate = WalletName.Trim();
		if (UiContext.LiquidWalletSession.WalletExists(candidate))
		{
			errors.Add(ErrorSeverity.Error, $"A Liquid wallet named '{candidate}' already exists.");
		}
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);
		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
	}
}
