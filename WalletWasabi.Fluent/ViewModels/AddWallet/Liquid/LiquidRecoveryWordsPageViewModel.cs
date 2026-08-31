using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Models.Wallets.Liquid;
using WalletWasabi.Fluent.ViewModels.AddWallet.Create;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.AddWallet.Liquid;

/// <summary>
/// The Liquid recovery-words page. For <see cref="LiquidWalletCreationMode.CreateNew"/>
/// it generates a fresh wallet (recovery words + password), displays the 12
/// recovery words for backup, and opens/registers the wallet on continue. For
/// <see cref="LiquidWalletCreationMode.Recover"/> it collects the typed
/// recovery words (the same TagsBox pattern as the BTC recover page) and
/// restores/opens/registers the wallet. Wallet creation and restore reuse the
/// harness <c>KeyManager</c> primitives via the
/// <see cref="LiquidWalletSession"/>.
/// </summary>
[NavigationMetaData(Title = "Recovery Words")]
public partial class LiquidRecoveryWordsPageViewModel : RoutableViewModel
{
	private readonly LiquidWalletCreationMode _mode;
	private readonly string _walletName;

	[AutoNotify] private IEnumerable<string>? _suggestions;
	[AutoNotify] private Mnemonic? _currentMnemonics;
	[AutoNotify] private bool _isMnemonicsValid;

	public LiquidRecoveryWordsPageViewModel(UiContext uiContext, LiquidWalletCreationMode mode, string walletName) : base(uiContext)
	{
		_mode = mode;
		_walletName = walletName;
		IsCreateMode = mode == LiquidWalletCreationMode.CreateNew;

		EnableBack = true;

		if (IsCreateMode)
		{
			// Create path: the words are generated now (the file is written only
			// once the password is chosen) and shown for backup.
			GeneratedMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
			MnemonicWords = CreateWordList(GeneratedMnemonic);
			NextCommand = ReactiveCommand.CreateFromTask(OnCreateNextAsync);
		}
		else
		{
			// Recover path: collect the typed words.
			Suggestions = new Mnemonic(Wordlist.English, WordCount.Twelve).WordList.GetWords();
			Mnemonics.ToObservableChangeSet().ToCollection()
				.Select(x => x.Count is 12 or 15 or 18 or 21 or 24 ? new Mnemonic(string.Join(' ', x).ToLowerInvariant()) : null)
				.Subscribe(x =>
				{
					CurrentMnemonics = x;
					IsMnemonicsValid = x is { IsValidChecksum: true };
				});
			NextCommand = ReactiveCommand.CreateFromTask(
				OnRecoverNextAsync,
				canExecute: this.WhenAnyValue(x => x.IsMnemonicsValid));
		}
	}

	public bool IsCreateMode { get; }

	public Mnemonic? GeneratedMnemonic { get; }

	public List<RecoveryWordViewModel>? MnemonicWords { get; }

	public ObservableCollection<string> Mnemonics { get; } = new();

	private List<RecoveryWordViewModel> CreateWordList(Mnemonic mnemonic)
	{
		var result = new List<RecoveryWordViewModel>();
		for (int i = 0; i < mnemonic.Words.Length; i++)
		{
			result.Add(new RecoveryWordViewModel(UiContext, i + 1, mnemonic.Words[i]));
		}

		return result;
	}

	private async Task OnCreateNextAsync()
	{
		ArgumentNullException.ThrowIfNull(GeneratedMnemonic);

		var password = await Navigate().To()
			.CreatePasswordDialog("Add Passphrase", "Set a password for this Liquid wallet (it encrypts the wallet file).")
			.GetResultAsync();
		if (password is not { })
		{
			return;
		}

		await RunOpenAsync(() =>
		{
			UiContext.LiquidWalletSession.CreateWalletFile(_walletName, GeneratedMnemonic, password);
			return UiContext.LiquidWalletSession.GetWalletFilePath(_walletName);
		}, password, "Wasabi was unable to create the Liquid wallet.");
	}

	private async Task OnRecoverNextAsync()
	{
		if (CurrentMnemonics is not { IsValidChecksum: true } mnemonic)
		{
			return;
		}

		var password = await Navigate().To()
			.CreatePasswordDialog("Add Passphrase", "If you used a passphrase when you created your wallet you must type it below, otherwise leave this empty.")
			.GetResultAsync();
		if (password is not { })
		{
			return;
		}

		await RunOpenAsync(() =>
		{
			UiContext.LiquidWalletSession.RecoverWalletFile(_walletName, mnemonic, password);
			return UiContext.LiquidWalletSession.GetWalletFilePath(_walletName);
		}, password, "Wasabi was unable to recover the Liquid wallet.");
	}

	/// <summary>
	/// Writes the wallet file (create/restore), opens + refreshes it via the
	/// session, registers it into the repository, and selects its NavBar entry.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "The LiquidWalletModel ownership transfers to the app-lifetime LiquidWalletRepository via AddOrUpdate; it is not disposed here.")]
	private async Task RunOpenAsync(Func<string> writeWalletFile, string password, string errorCaption)
	{
		IsBusy = true;
		try
		{
			string walletFile = writeWalletFile();

			var model = await UiContext.LiquidWalletSession.OpenWalletAsync(
				_walletName,
				walletFile,
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

			// Navigate the HomeScreen to the wallet page directly from the model —
			// the NavBar's DynamicData list may not have materialized the new row on
			// this tick, and SelectLiquidWallet is a highlight-only helper, not the
			// navigation mechanism. Clearing the dialog stack first drops the whole
			// create/recover flow off DialogScreen.
			Navigate().Clear();
			await Task.Delay(UiConstants.CloseSuccessDialogMillisecondsDelay);
			var homePage = new WalletWasabi.Fluent.ViewModels.Wallets.Liquid.LiquidWalletViewModel(UiContext, model);
			UiContext.Navigate(NavigationTarget.HomeScreen).To(homePage, NavigationMode.Clear);
			UiContext.MainViewModel?.NavBar.SelectLiquidWallet(model.Name);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			// Clean up a partially-created wallet file so the user can retry
			// with the same name without hitting the overwrite guard.
			try
			{
				string walletFile = UiContext.LiquidWalletSession.GetWalletFilePath(_walletName);
				if (File.Exists(walletFile) && !UiContext.LiquidWalletRepository.Wallets.Lookup(_walletName).HasValue)
				{
					File.Delete(walletFile);
				}
			}
			catch (Exception cleanupEx)
			{
				Logger.LogWarning(cleanupEx, "Failed to clean up partial wallet file.");
			}
			await ShowErrorAsync(Title, ex.ToUserFriendlyString(), errorCaption);
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
