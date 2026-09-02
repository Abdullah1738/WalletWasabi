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
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The Liquid wallet home view model, parallel to the BTC
/// <c>WalletViewModel</c>. Its primary content is the multiasset balance
/// set: one <see cref="LiquidAssetBalanceItemViewModel"/> row per asset in
/// <see cref="LiquidWalletModel.Balances"/>, the pegged asset (L-BTC) first,
/// then the issued assets in the landed canonical ascending asset-id-hex
/// order. The receive action navigates to the confidential-address-native
/// <see cref="LiquidReceiveViewModel"/>; the send action navigates to the
/// exact-spend-plan <see cref="LiquidSendViewModel"/>, wired with the
/// session's narrow send-execution delegate (key management stays in the
/// session layer). There is deliberately no coin list, no CoinJoin status,
/// and no music box — a Liquid managed wallet has no CoinJoin.
/// </summary>
[AppLifetime]
public partial class LiquidWalletViewModel : RoutableViewModel
{
	private ReadOnlyObservableCollection<LiquidAssetBalanceItemViewModel>? _balanceRows;
	private LiquidAssetBalanceItemViewModel? _peggedBalanceRow;
	private readonly ObservableCollection<LiquidHistoryItemViewModel> _historyRows = new();
	private readonly ReadOnlyObservableCollection<LiquidHistoryItemViewModel> _historyRowsReadOnly;
	private bool _isHistoryLoaded;
	private bool _isHistoryEmpty;
	private int _selectedHistoryIndex = -1;

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
				.Select(balance => CreateBalanceRow(balance))
				.ToArray())
			.Do(rows => PeggedBalanceRow = rows.FirstOrDefault(row => row.IsPeggedAsset))
			.ToObservableChangeSet(row => row.AssetIdHex)
			.Bind(out _balanceRows)
			.Subscribe();

		// The retained transaction history is a complete revision-scoped
		// replacement: every valid emission replaces the whole visible row
		// set in snapshot order. The TransactionId is never
		// used as a DynamicData key, equality identity, or deduplication
		// input — two rows from the same transaction remain two rows. When history
		// is unloaded (revision-pair fence) the collection exposes no stale
		// rows.
		_historyRowsReadOnly = new ReadOnlyObservableCollection<LiquidHistoryItemViewModel>(_historyRows);
		walletModel.History
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(snapshot =>
			{
				_historyRows.Clear();
				foreach (var row in snapshot.Rows)
				{
					_historyRows.Add(new LiquidHistoryItemViewModel(uiContext, row));
				}

				IsHistoryEmpty = _historyRows.Count == 0;
			});
		walletModel.HistoryLoaded
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(loaded =>
			{
				IsHistoryLoaded = loaded;
				if (!loaded)
				{
					_historyRows.Clear();
					IsHistoryEmpty = false;
				}
			});

		ReceiveCommand = ReactiveCommand.Create(() =>
			UiContext.Navigate(NavigationTarget.DialogScreen)
				.To(new LiquidReceiveViewModel(uiContext, walletModel)));

		// The send action navigates to the exact-spend-plan
		// LiquidSendViewModel, wired with the session's narrow send-execution
		// delegate: the session owns the single application client and the
		// open authenticated session (keys never leave that layer); this view
		// model only forwards the delegate.
		SendCommand = ReactiveCommand.Create(() =>
			UiContext.Navigate(NavigationTarget.DialogScreen)
				.To(new LiquidSendViewModel(
					uiContext,
					walletModel,
					uiContext.LiquidWalletSession.ExecuteSendAsync)));

		SetupCancel(enableCancel: false, enableCancelOnEscape: false, enableCancelOnPressed: false);
		EnableBack = true;
	}

	public LiquidWalletModel WalletModel { get; }

	public ReadOnlyObservableCollection<LiquidAssetBalanceItemViewModel>? BalanceRows => _balanceRows;

	/// <summary>
	/// Presentation-only mirror of the pegged (L-BTC) row in
	/// <see cref="BalanceRows"/> for the balance tile; the tile is the same
	/// row shown prominently, not an additional balance. Null until a balance
	/// snapshot carries the pegged asset.
	/// </summary>
	public LiquidAssetBalanceItemViewModel? PeggedBalanceRow
	{
		get => _peggedBalanceRow;
		private set => this.RaiseAndSetIfChanged(ref _peggedBalanceRow, value);
	}

	public ReadOnlyObservableCollection<LiquidHistoryItemViewModel> HistoryRows => _historyRowsReadOnly;

	public bool IsHistoryLoaded
	{
		get => _isHistoryLoaded;
		private set => this.RaiseAndSetIfChanged(ref _isHistoryLoaded, value);
	}

	public bool IsHistoryEmpty
	{
		get => _isHistoryEmpty;
		private set => this.RaiseAndSetIfChanged(ref _isHistoryEmpty, value);
	}

	/// <summary>
	/// The keyboard-driven selected history row index (one tab stop,
	/// Up/Down traversal). Never keyed by the redacted transaction
	/// reference.
	/// </summary>
	public int SelectedHistoryIndex
	{
		get => _selectedHistoryIndex;
		set => this.RaiseAndSetIfChanged(ref _selectedHistoryIndex, value);
	}

	public ICommand ReceiveCommand { get; }

	public ICommand SendCommand { get; }

	public override string Title { get; protected set; }

	// Projects one balance row, wiring the per-row Send affordance: the row's
	// Send navigates exactly like the top-level SendCommand (DialogScreen →
	// LiquidSendViewModel over the session's send executor) but pre-selects
	// the row's asset in the picker. The top-level SendCommand keeps the
	// default (no pre-selection) path.
	private LiquidAssetBalanceItemViewModel CreateBalanceRow(LiquidWalletUiAssetBalance balance)
	{
		var row = new LiquidAssetBalanceItemViewModel(UiContext, balance);
		string assetIdHex = balance.AssetIdHex;
		row.SendCommand = ReactiveCommand.Create(() =>
			UiContext.Navigate(NavigationTarget.DialogScreen)
				.To(new LiquidSendViewModel(
					UiContext,
					WalletModel,
					UiContext.LiquidWalletSession.ExecuteSendAsync,
					preSelectedAssetIdHex: assetIdHex)));
		return row;
	}
}
