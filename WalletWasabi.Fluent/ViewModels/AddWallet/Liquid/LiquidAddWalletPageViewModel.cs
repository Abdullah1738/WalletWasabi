using System.IO;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.AddWallet.Liquid;

/// <summary>
/// The Liquid "Add Wallet" hub, parallel to the BTC
/// <c>AddWalletPageViewModel</c> but Liquid-only: it offers creating a fresh
/// Liquid testnet wallet (name + recovery words + password), recovering one
/// from recovery words, and opening an existing harness-written wallet file by
/// password. Each path ends by registering the opened wallet into the
/// <c>LiquidWalletRepository</c> and selecting its NavBar entry.
/// </summary>
[NavigationMetaData(
	Title = "Add Liquid Wallet",
	Caption = "Create, recover, or open a Liquid testnet wallet",
	Order = 2,
	Category = "General",
	Keywords = new[] { "Wallet", "Add", "Create", "New", "Recover", "Liquid" },
	IconName = "nav_add_circle_24_regular",
	IconNameFocused = "nav_add_circle_24_filled",
	NavigationTarget = NavigationTarget.DialogScreen,
	NavBarPosition = NavBarPosition.None,
	Searchable = false)]
public partial class LiquidAddWalletPageViewModel : DialogViewModelBase<System.Reactive.Unit>
{
	public LiquidAddWalletPageViewModel(UiContext uiContext) : base(uiContext)
	{
		CreateWalletCommand = ReactiveCommand.Create(OnCreateWallet);
		RecoverWalletCommand = ReactiveCommand.Create(OnRecoverWallet);
		OpenWalletCommand = ReactiveCommand.CreateFromTask(OnOpenWalletAsync);

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
	}

	public ICommand CreateWalletCommand { get; }

	public ICommand RecoverWalletCommand { get; }

	public ICommand OpenWalletCommand { get; }

	private void OnCreateWallet()
	{
		Navigate().To().LiquidWalletNamePage(LiquidWalletCreationMode.CreateNew);
	}

	private void OnRecoverWallet()
	{
		Navigate().To().LiquidWalletNamePage(LiquidWalletCreationMode.Recover);
	}

	private async Task OnOpenWalletAsync()
	{
		try
		{
			var file = await FileDialogHelper.OpenFileAsync(
				"Open Liquid wallet file",
				["json"],
				UiContext.LiquidWalletSession.LiquidWalletDirectory);

			if (file is null)
			{
				return;
			}

			var filePath = file.Path.LocalPath;
			var walletName = Path.GetFileNameWithoutExtension(filePath);

			var password = await Navigate().To()
				.CreatePasswordDialog("Password", "Type the password for this Liquid wallet.", enableEmpty: true)
				.GetResultAsync();
			if (password is not { })
			{
				return;
			}

			await OpenAndRegisterAsync(walletName, filePath, password);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			await ShowErrorAsync("Open Liquid wallet", ex.ToUserFriendlyString(), "Wasabi was unable to open the Liquid wallet.");
		}
	}

	/// <summary>
	/// Opens the wallet via the application-lifetime Liquid session, registers
	/// the resulting model into the repository, clears the dialog stack, and
	/// selects the wallet's NavBar entry so its home page is shown.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "The LiquidWalletModel ownership transfers to the app-lifetime LiquidWalletRepository via AddOrUpdate; it is not disposed here.")]
	internal async Task OpenAndRegisterAsync(string walletName, string walletFilePath, string password)
	{
		IsBusy = true;
		try
		{
			var model = await UiContext.LiquidWalletSession.OpenWalletAsync(
				walletName,
				walletFilePath,
				password,
				System.Threading.CancellationToken.None);

			UiContext.LiquidWalletRepository.AddOrUpdate(model);

			// The wallet now exists — exit out-of-box so the welcome backdrop
			// (which would otherwise cover the shell as a blank screen) lifts.
			UiContext.ApplicationSettings.Oobe = false;
			if (UiContext.MainViewModel is { } mvm)
			{
				mvm.IsOobeBackgroundVisible = false;
			}

			Navigate().Clear();
			await Task.Delay(UiConstants.CloseSuccessDialogMillisecondsDelay);
			var homePage = new WalletWasabi.Fluent.ViewModels.Wallets.Liquid.LiquidWalletViewModel(UiContext, model);
			UiContext.Navigate(NavigationTarget.HomeScreen).To(homePage, NavigationMode.Clear);
			UiContext.MainViewModel?.NavBar.SelectLiquidWallet(model.Name);
		}
		finally
		{
			IsBusy = false;
		}
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);
		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
	}
}
