using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using ReactiveUI;
using WalletWasabi.Fluent.Models.Wallets.Liquid;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// One recipient row of the Liquid send flow: the confidential destination
/// address, the destination asset (picked from a dropdown bound from
/// <see cref="LiquidWalletModel.Balances"/> — the multiasset balance set:
/// the pegged asset first, then the issued assets in canonical order), and
/// the raw atomic-units amount (no decimal formatting, no USD conversion).
/// The selected asset keeps <see cref="AssetIdHex"/> in sync — the property
/// the plan/sign path already consumes. Every Liquid managed-wallet
/// destination is confidential by construction, so
/// <see cref="IsConfidential"/> is always <see langword="true"/>.
/// </summary>
public sealed partial class LiquidSendRecipientViewModel : ViewModelBase
{
	private readonly LiquidWalletModel _walletModel;
	private readonly ObservableAsPropertyHelper<IReadOnlyList<LiquidAssetBalanceItemViewModel>> _assetOptions;

	[AutoNotify] private string _confidentialAddressText = "";
	[AutoNotify] private string _assetIdHex = "";
	[AutoNotify] private long _atomicUnits;
	[AutoNotify] private LiquidAssetBalanceItemViewModel? _selectedAsset;

	public LiquidSendRecipientViewModel(UiContext uiContext, LiquidWalletModel walletModel)
		: base(uiContext)
	{
		ArgumentNullException.ThrowIfNull(walletModel);
		_walletModel = walletModel;

		// The asset selector's options follow the wallet's balance stream:
		// each emission replaces the option set wholesale with a fresh
		// projection of the immutable snapshot. The selection follows the
		// default (the pegged asset when present, else the first asset) until
		// the user picks an asset, and reseeds when a refresh drops the held
		// selection or none is held — an empty balance set means an empty
		// dropdown and no fabricated asset.
		// Seed with an empty option set so AssetOptions is never null before the
		// first balance emission: an empty balance set means an empty dropdown.
		_assetOptions = walletModel.Balances
			.ObserveOn(RxApp.MainThreadScheduler)
			.Select(snapshot => (IReadOnlyList<LiquidAssetBalanceItemViewModel>)snapshot.Balances
				.Select(balance => new LiquidAssetBalanceItemViewModel(uiContext, balance))
				.ToArray())
			.ToProperty(
				this,
				x => x.AssetOptions,
				initialValue: Array.Empty<LiquidAssetBalanceItemViewModel>());

		// Reseed after the bound dropdown has applied the new option set. When
		// AssetOptions changes, a bound selector clears its SelectedItem before
		// the new items arrive; deferring the reseed to the dispatcher lets that
		// clear settle first so the default (pegged-first) selection sticks
		// instead of being overwritten by the clear.
		this.WhenAnyValue(x => x.AssetOptions)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(options => RxApp.MainThreadScheduler.Schedule(
				options,
				(_, current) =>
				{
					ReseedSelection(current);
					return System.Reactive.Disposables.Disposable.Empty;
				}));

		// The selected option drives the asset id the plan/sign path consumes;
		// a cleared selection clears the id (the landed fail-closed validation
		// surfaces as-is).
		this.WhenAnyValue(x => x.SelectedAsset)
			.Subscribe(selected => AssetIdHex = selected?.AssetIdHex ?? "");
	}

	public IReadOnlyList<LiquidAssetBalanceItemViewModel> AssetOptions => _assetOptions.Value;

	public bool IsConfidential => true;

	/// <summary>
	/// Holds the asset id the per-balance-row Send affordance wants pre-selected.
	/// The initial option emission is scheduler-deferred, so the option set may be
	/// empty when the affordance fires; holding the id lets the deferred reseed
	/// consume it once the matching option arrives.
	/// </summary>
	private string? _preSelectedAssetIdHex;

	/// <summary>
	/// Pre-selects the option whose <see cref="LiquidAssetBalanceItemViewModel.AssetIdHex"/>
	/// matches <paramref name="assetIdHex"/> (the per-balance-row Send affordance
	/// opens the send flow with the row's asset held). The id is held and consumed
	/// by the deferred reseed once the matching option is present; it never
	/// fabricates an option (an empty balance set still yields an empty dropdown).
	/// The default reseed semantics for the no-preselection path are unchanged.
	/// </summary>
	public void PreSelectAsset(string assetIdHex)
	{
		ArgumentNullException.ThrowIfNull(assetIdHex);
		_preSelectedAssetIdHex = assetIdHex;
		SelectedAsset = AssetOptions.FirstOrDefault(option => option.AssetIdHex == assetIdHex);
	}

	private void ReseedSelection(IReadOnlyList<LiquidAssetBalanceItemViewModel>? options)
	{
		if (options is null || options.Count == 0)
		{
			// Empty balance set: no fabricated asset.
			SelectedAsset = null;
			return;
		}

		// Consume the held pre-selection once its option is present. The reseed
		// then honors the held selection on later refreshes via the branch below.
		if (_preSelectedAssetIdHex is { } wanted &&
			options.FirstOrDefault(option => option.AssetIdHex == wanted) is { } preSelected)
		{
			_preSelectedAssetIdHex = null;
			SelectedAsset = preSelected;
			return;
		}

		if (SelectedAsset is { } held && options.Any(option => option.AssetIdHex == held.AssetIdHex))
		{
			return;
		}

		SelectedAsset = options.FirstOrDefault(option => option.IsPeggedAsset) ?? options.FirstOrDefault();
	}
}
