namespace WalletWasabi.Liquid.Assets;

/// <summary>Immutable display metadata. The caller, not the labels, establishes asset trust.</summary>
public sealed class LiquidAssetMetadata
{
	public LiquidAssetMetadata(string assetIdHex, string ticker, string name, int precision)
	{
		AssetIdHex = LiquidAssetId.ParseRpcHex(assetIdHex).CanonicalRpcHex;
		ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (ticker.Length > 16 || name.Length > 128 || ticker.Any(char.IsControl) || name.Any(char.IsControl))
			throw new ArgumentException("Asset display labels must be bounded single-line text.");
		if (precision is < 0 or > 18) throw new ArgumentOutOfRangeException(nameof(precision));
		Ticker = ticker;
		Name = name;
		Precision = precision;
	}

	public string AssetIdHex { get; }
	public string Ticker { get; }
	public string Name { get; }
	public int Precision { get; }
}
