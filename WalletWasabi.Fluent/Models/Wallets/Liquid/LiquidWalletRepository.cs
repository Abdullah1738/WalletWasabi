using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using DynamicData;
using ReactiveUI;

namespace WalletWasabi.Fluent.Models.Wallets.Liquid;

/// <summary>
/// The parallel wallet-list owner for Liquid managed wallets, beside the
/// BTC <see cref="WalletRepository"/> (which stays BTC-only and untouched).
/// Owns an <see cref="IObservableCache{LiquidWalletModel, String}"/> keyed
/// by wallet name, populated by the application lifetime layer when a
/// Liquid wallet is opened via
/// <see cref="WalletWasabi.Liquid.Wallet.Ui.LiquidWalletUiFacade.LoadAndCaptureBalances"/>.
/// Performs no <c>WalletManager</c> interaction, no <c>KeyManager</c>
/// interaction, and no <c>WalletId</c> minting: a Liquid managed wallet is
/// identified by its name and its manifest binding, not by a BTC
/// <c>WalletId</c>.
/// </summary>
public sealed class LiquidWalletRepository : ReactiveObject, IDisposable
{
	private readonly CompositeDisposable _disposable = new();
	private readonly SourceCache<LiquidWalletModel, string> _wallets;

	public LiquidWalletRepository()
	{
		_wallets = new SourceCache<LiquidWalletModel, string>(model => model.Name);
		Wallets = _wallets
			.Connect()
			.AsObservableCache()
			.DisposeWith(_disposable);
	}

	public IObservableCache<LiquidWalletModel, string> Wallets { get; }

	/// <summary>
	/// Registers a freshly opened Liquid wallet. The wallet is identified
	/// by its name; registering the same name twice replaces the entry.
	/// </summary>
	public void AddOrUpdate(LiquidWalletModel wallet)
	{
		ArgumentNullException.ThrowIfNull(wallet);
		_wallets.AddOrUpdate(wallet);
	}

	/// <summary>
	/// Removes a closed Liquid wallet by name.
	/// </summary>
	public void Remove(string walletName)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletName);
		_wallets.Remove(walletName);
	}

	public void Dispose()
	{
		_wallets.Dispose();
		_disposable.Dispose();
	}
}
