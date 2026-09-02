namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The wallet-owned branch-1 confidential change address for one Liquid send, supplied to the
/// facade as a public-safe value. The executor reserves it once from the scope's reserved change
/// surface (lazily, cached for both facade calls of one send) and passes it through; the facade
/// applies it to each asset whose selected total exceeds destination-plus-fee, assigning the
/// per-asset surplus as the change amount. No key material or internal state crosses this
/// boundary. Used only to append change outputs so the exact plan validator balances per asset;
/// never a user-facing destination.
/// </summary>
public sealed class LiquidWalletUiChangeDestination
{
	public LiquidWalletUiChangeDestination(string confidentialAddress)
	{
		ArgumentException.ThrowIfNullOrEmpty(confidentialAddress);
		ConfidentialAddress = confidentialAddress;
	}

	public string ConfidentialAddress { get; }
}
