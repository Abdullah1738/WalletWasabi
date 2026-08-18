using WalletWasabi.Liquid.Addresses;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, display-ready projection of one freshly derived
/// confidential Liquid receive address. The canonical confidential form is
/// the primary display form; the unconfidential form is available alongside
/// it. Construction refuses a non-confidential address — the Liquid-native
/// receive surface is confidential-only, matching the landed
/// <see cref="LiquidSuppliedConfidentialDestination.Create"/> invariant.
/// </summary>
public sealed class LiquidWalletUiReceiveAddress
{
	private LiquidWalletUiReceiveAddress(
		string confidentialAddressText,
		string unconfidentialAddressText,
		string networkManifestId)
	{
		ConfidentialAddressText = confidentialAddressText;
		UnconfidentialAddressText = unconfidentialAddressText;
		IsConfidential = true;
		NetworkManifestId = networkManifestId;
	}

	public string ConfidentialAddressText { get; }
	public string UnconfidentialAddressText { get; }
	public bool IsConfidential { get; }
	public string NetworkManifestId { get; }

	internal static LiquidWalletUiReceiveAddress FromAddress(LiquidAddress address)
	{
		ArgumentNullException.ThrowIfNull(address);
		if (!address.IsConfidential)
		{
			throw new ArgumentException(
				"A confidential Liquid receive address is required.",
				nameof(address));
		}

		return new LiquidWalletUiReceiveAddress(
			address.GetCanonicalAddressText(),
			address.GetUnconfidentialAddressText(),
			address.NetworkManifestId);
	}
}
