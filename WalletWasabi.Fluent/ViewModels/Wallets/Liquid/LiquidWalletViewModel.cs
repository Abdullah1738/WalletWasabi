using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using DynamicData;
using ReactiveUI;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets.Liquid;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The Liquid wallet home view model, parallel to the BTC
/// <c>WalletViewModel</c>. Its primary content is the multiasset balance
/// set: one <see cref="LiquidAssetBalanceItemViewModel"/> row per asset in
/// <see cref="LiquidWalletModel.Balances"/>, the pegged asset (L-BTC) first,
/// then the issued assets in the landed canonical ascending asset-id-hex
/// order. The receive action navigates to the confidential-address-native
/// <see cref="LiquidReceiveViewModel"/>. There is deliberately no send
/// command, no coin list, no CoinJoin status, no music box, and no history
/// table — the send flow and the history presentation are later slices.
/// </summary>
[AppLifetime]
public partial class LiquidWalletViewModel : RoutableViewModel
{
	private ReadOnlyObservableCollection<LiquidAssetBalanceItemViewModel>? _balanceRows;

	public LiquidWalletViewModel(UiContext uiContext, LiquidWalletModel walletModel)
		: base(uiContext)
	{
		WalletModel = walletModel;
		Title = walletModel.Name;

		// The multiasset balance set is the primary content: each emission
		// of the model's balance stream replaces the row set wholesale with
		// a fresh projection of the immutable snapshot.
		walletModel.Balances
			.ObserveOn(RxApp.MainThreadScheduler)
			.Select(snapshot => snapshot.Balances
				.Select(balance => new LiquidAssetBalanceItemViewModel(uiContext, balance))
				.ToArray())
			.ToObservableChangeSet(row => row.AssetIdHex)
			.Bind(out _balanceRows)
			.Subscribe();

		ReceiveCommand = ReactiveCommand.Create(() =>
			UiContext.Navigate(NavigationTarget.DialogScreen)
				.To(new LiquidReceiveViewModel(uiContext, walletModel)));

		SetupCancel(enableCancel: false, enableCancelOnEscape: false, enableCancelOnPressed: false);
		EnableBack = true;
	}

	public LiquidWalletModel WalletModel { get; }

	public ReadOnlyObservableCollection<LiquidAssetBalanceItemViewModel>? BalanceRows => _balanceRows;

	public ICommand ReceiveCommand { get; }

	public override string Title { get; protected set; }
}
