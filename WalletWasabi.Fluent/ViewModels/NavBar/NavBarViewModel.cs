using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Liquid;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.NavBar;

/// <summary>
/// The ViewModel that represents the structure of the sidebar.
/// </summary>
[AppLifetime]
public partial class NavBarViewModel : ViewModelBase, IWalletSelector
{
	[AutoNotify] private WalletPageViewModel? _selectedWallet;
	[AutoNotify] private LiquidWalletPageViewModel? _selectedLiquidWallet;

	public NavBarViewModel(UiContext uiContext) : base(uiContext)
	{
		BottomItems = new ObservableCollection<NavBarItemViewModel>();

		Wallets = LiquidProductMode.Enabled
			? new ReadOnlyObservableCollection<WalletPageViewModel>(new ObservableCollection<WalletPageViewModel>())
			: ObserveBtcWallets();

		// The parallel Liquid wallet list: one NavBar item per registered
		// Liquid managed wallet, populated by the application lifetime layer
		// when a Liquid wallet is opened. Strictly additive beside the BTC
		// pipeline above; it never touches WalletRepository or WalletManager.
		UiContext.LiquidWalletRepository
			.Wallets
			.Connect()
			.Transform(newWallet => new LiquidWalletPageViewModel(UiContext, newWallet))
			.SortAndBind(
				out var liquidWallets,
				SortExpressionComparer<LiquidWalletPageViewModel>
					.Ascending(x => x.WalletModel.Name)
			)
			.Subscribe();

		LiquidWallets = liquidWallets;
	}

	public ObservableCollection<NavBarItemViewModel> BottomItems { get; }

	public ReadOnlyObservableCollection<WalletPageViewModel> Wallets { get; }

	public ReadOnlyObservableCollection<LiquidWalletPageViewModel> LiquidWallets { get; }

	private ReadOnlyObservableCollection<WalletPageViewModel> ObserveBtcWallets()
	{
		UiContext.WalletRepository
			.Wallets
			.Connect()
			.Transform(newWallet => new WalletPageViewModel(UiContext, newWallet))
			.AutoRefresh(x => x.IsLoggedIn)
			.SortAndBind(
				out var wallets,
				SortExpressionComparer<WalletPageViewModel>
					.Descending(i => i.IsLoggedIn)
					.ThenByAscending(x => x.WalletModel.Name)
			)
			.Subscribe();

		return wallets;
	}

	// AutoInterfaces (such as IWalletModel) cannot be seen by AutoNotifyGenerator.
	public IWalletModel? SelectedWalletModel
	{
		get;
		set => this.RaiseAndSetIfChanged(ref field, value);
	}

	IWalletViewModel? IWalletSelector.SelectedWallet => SelectedWallet?.WalletViewModel;

	public void Activate()
	{
		this.WhenAnyValue(x => x.SelectedWallet)
			.Buffer(2, 1)
			.Select(buffer => (OldValue: buffer[0], NewValue: buffer[1]))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Do(x =>
			{
				if (x.OldValue is { } a)
				{
					a.IsSelected = false;
				}

				if (x.NewValue is { } b)
				{
					b.IsSelected = true;
					UiContext.WalletRepository.StoreLastSelectedWallet(b.WalletModel);
				}
			})
			.Subscribe();

		this.WhenAnyValue(x => x.SelectedWallet!.WalletModel)
			.BindTo(this, x => x.SelectedWalletModel);

		// The Liquid selection mirrors the BTC one: toggling IsSelected on the
		// outgoing/incoming item. Selecting a Liquid wallet clears the BTC
		// selection and vice versa so exactly one wallet page is shown.
		this.WhenAnyValue(x => x.SelectedLiquidWallet)
			.Buffer(2, 1)
			.Select(buffer => (OldValue: buffer[0], NewValue: buffer[1]))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Do(x =>
			{
				if (x.OldValue is { } a)
				{
					a.IsSelected = false;
				}

				if (x.NewValue is { } b)
				{
					b.IsSelected = true;
					SelectedWallet = null;
				}
			})
			.Subscribe();

		// Selecting a BTC wallet clears any Liquid selection.
		this.WhenAnyValue(x => x.SelectedWallet)
			.WhereNotNull()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Do(_ => SelectedLiquidWallet = null)
			.Subscribe();

		SelectedWallet = Wallets.FirstOrDefault(x => x.WalletModel.Name == UiContext.WalletRepository.DefaultWalletName) ?? Wallets.FirstOrDefault();
	}

	/// <summary>
	/// Selects the NavBar item for a Liquid wallet and navigates the HomeScreen
	/// to its <see cref="LiquidWalletViewModel"/>. Navigation is orchestrated
	/// here (the NavBar owns the registered navigation state); the item's
	/// IsSelected flag is only the NavBar highlight.
	/// </summary>
	public void SelectLiquidWallet(string walletName)
	{
		if (LiquidWallets.FirstOrDefault(x => x.WalletModel.Name == walletName) is not { } item)
		{
			Logger.LogWarning($"Liquid wallet '{walletName}' is not yet present in the NavBar list; selection skipped (navigation is handled by the caller).");
			return;
		}

		SelectedLiquidWallet = item;

		// Navigation is only available once the application has registered its
		// navigation state (a bare NavBar in a unit test has none); in that case
		// the selection still takes effect and navigation is skipped.
		try
		{
			UiContext.Navigate(NavigationTarget.HomeScreen)
				.To(item.CreateWalletViewModel(), NavigationMode.Clear);
		}
		catch (InvalidOperationException ex)
		{
			Logger.LogWarning(ex, "Liquid wallet HomeScreen navigation skipped.");
		}
	}

	public async Task InitialiseAsync()
	{
		var bottomItems = NavigationManager.MetaData.Where(x => x.NavBarPosition == NavBarPosition.Bottom);

		foreach (var item in bottomItems)
		{
			var viewModel = await NavigationManager.MaterializeViewModelAsync(item);

			if (viewModel is INavBarItem navBarItem)
			{
				BottomItems.Add(new NavBarItemViewModel(UiContext, navBarItem));
			}
		}
	}

	IWalletViewModel? IWalletNavigation.To(IWalletModel wallet)
	{
		SelectedWallet = Wallets.First(x => x.WalletModel.Name == wallet.Name);
		return SelectedWallet.WalletViewModel;
	}
}
