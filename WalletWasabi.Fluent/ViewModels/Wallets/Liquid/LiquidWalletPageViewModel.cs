using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets.Liquid;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The Liquid per-wallet NavBar item, parallel to the BTC
/// <c>WalletPageViewModel</c> but Liquid-only: it carries the
/// <see cref="LiquidWalletModel"/> and the selection highlight state shown in
/// the NavBar. Navigation to the wallet's <see cref="LiquidWalletViewModel"/>
/// is orchestrated by the <c>NavBarViewModel</c> (which owns the registered
/// navigation state) when an item is selected — this item deliberately has no
/// login page, no loading page, no CoinJoin status, and no <c>WalletManager</c>
/// interaction, because a Liquid managed wallet is already authenticated and
/// loaded by the time it is registered into the
/// <see cref="LiquidWalletRepository"/>.
/// </summary>
[AppLifetime]
public partial class LiquidWalletPageViewModel : ViewModelBase, INavBarItem
{
	[AutoNotify] private bool _isSelected;
	[AutoNotify] private string _title;
	[AutoNotify] private string? _iconName;
	[AutoNotify] private string? _iconNameFocused;

	public LiquidWalletPageViewModel(UiContext uiContext, LiquidWalletModel walletModel) : base(uiContext)
	{
		WalletModel = walletModel;
		_title = walletModel.Name;

		SetIcon();
	}

	public LiquidWalletModel WalletModel { get; }

	/// <summary>
	/// Builds this wallet's Liquid home view model. The <c>NavBarViewModel</c>
	/// navigates to this when the item is selected.
	/// </summary>
	public LiquidWalletViewModel CreateWalletViewModel() => new(UiContext, WalletModel);

	private void SetIcon()
	{
		IconName = "nav_wallet_24_regular";
		IconNameFocused = "nav_wallet_24_filled";
	}
}
