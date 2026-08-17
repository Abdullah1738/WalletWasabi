using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidSuppliedConfidentialDestination :
	IEquatable<LiquidSuppliedConfidentialDestination>
{
	private readonly string _networkManifestId;
	private readonly LiquidAssetId _peggedAssetId;
	private readonly LiquidAddress _address;
	private readonly LiquidAssetId _assetId;
	private readonly LiquidAssetAmount? _amount;
	private readonly LiquidWalletLabelSet _labels;

	private LiquidSuppliedConfidentialDestination(
		string networkManifestId,
		LiquidAssetId peggedAssetId,
		LiquidAddress address,
		LiquidAssetId assetId,
		LiquidAssetAmount? amount,
		LiquidWalletLabelSet labels)
	{
		_networkManifestId = networkManifestId;
		_peggedAssetId = peggedAssetId;
		_address = address;
		_assetId = assetId;
		_amount = amount;
		_labels = labels;
	}

	public static LiquidSuppliedConfidentialDestination Create(
		ElementsPublicNetworkManifest manifest,
		LiquidAddress address,
		LiquidAssetId assetId,
		LiquidAssetAmount? amount,
		LiquidWalletLabelSet labels)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(address);
		ArgumentNullException.ThrowIfNull(assetId);
		ArgumentNullException.ThrowIfNull(labels);

		if (!StringComparer.Ordinal.Equals(address.NetworkManifestId, manifest.ManifestId))
		{
			throw new ArgumentException(
				"The supplied Liquid address could not be accepted.",
				nameof(address));
		}
		if (!address.IsConfidential)
		{
			throw new ArgumentException(
				"The supplied Liquid address could not be accepted.",
				nameof(address));
		}

		if (amount is not null)
		{
			if (amount.IsZero)
			{
				throw new ArgumentOutOfRangeException(
					nameof(amount),
					"The supplied Liquid amount could not be accepted.");
			}
			if (amount.AssetId != assetId)
			{
				throw new ArgumentException(
					"The supplied Liquid amount could not be accepted.",
					nameof(amount));
			}
			if (!StringComparer.Ordinal.Equals(
				amount.PeggedAssetId.CanonicalRpcHex,
				manifest.PeggedAssetId))
			{
				throw new ArgumentException(
					"The supplied Liquid amount could not be accepted.",
					nameof(amount));
			}
		}

		LiquidAssetId peggedAssetId = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		return new LiquidSuppliedConfidentialDestination(
			manifest.ManifestId,
			peggedAssetId,
			address,
			assetId,
			amount,
			labels);
	}

	public string GetNetworkManifestId() => _networkManifestId;

	public LiquidAssetId GetPeggedAssetId() => _peggedAssetId;

	public LiquidAddress GetAddress() => _address;

	public LiquidAssetId GetAssetId() => _assetId;

	public LiquidAssetAmount? GetAmount() => _amount;

	public LiquidWalletLabelSet GetLabels() => _labels;

	public bool Equals(LiquidSuppliedConfidentialDestination? other) =>
		other is not null &&
		StringComparer.Ordinal.Equals(_networkManifestId, other._networkManifestId) &&
		_peggedAssetId == other._peggedAssetId &&
		_address.Equals(other._address) &&
		_assetId == other._assetId &&
		_amount == other._amount &&
		_labels == other._labels;

	public override bool Equals(object? obj) =>
		Equals(obj as LiquidSuppliedConfidentialDestination);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(_networkManifestId, StringComparer.Ordinal);
		hash.Add(_peggedAssetId);
		hash.Add(_address);
		hash.Add(_assetId);
		hash.Add(_amount);
		hash.Add(_labels);
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidSuppliedConfidentialDestination);
}
