using ReactiveUI;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// One recipient row of the Liquid send flow: the confidential destination
/// address, the destination asset id (the asset selector is a dropdown
/// bound from <see cref="Models.Wallets.Liquid.LiquidWalletModel.Balances"/>
/// — the multiasset balance set), and the raw atomic-units amount (no
/// decimal formatting, no USD conversion). Every Liquid managed-wallet
/// destination is confidential by construction, so
/// <see cref="IsConfidential"/> is always <see langword="true"/>.
/// </summary>
public sealed partial class LiquidSendRecipientViewModel : ViewModelBase
{
	[AutoNotify] private string _confidentialAddressText = "";
	[AutoNotify] private string _assetIdHex = "";
	[AutoNotify] private long _atomicUnits;

	public LiquidSendRecipientViewModel(UiContext uiContext)
		: base(uiContext)
	{
	}

	public bool IsConfidential => true;
}
