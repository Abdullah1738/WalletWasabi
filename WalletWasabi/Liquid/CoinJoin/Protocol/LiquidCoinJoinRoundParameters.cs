using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.CoinJoin.Protocol;

internal sealed record LiquidCoinJoinRoundParameters
{
	public LiquidCoinJoinRoundParameters(
		string roundId,
		ElementsPublicNetworkManifest networkManifest,
		string genesisHash,
		string peggedAssetId,
		long fee,
		string issuerRole,
		int ownerCount)
	{
		if (String.IsNullOrWhiteSpace(roundId)) throw new ArgumentException("A round id is required.", nameof(roundId));
		ArgumentNullException.ThrowIfNull(networkManifest);
		if (!StringComparer.Ordinal.Equals(networkManifest.GenesisBlockHash, genesisHash))
		{
			throw new ArgumentException("The round genesis does not match the network manifest.", nameof(genesisHash));
		}
		if (!StringComparer.Ordinal.Equals(networkManifest.PeggedAssetId, peggedAssetId))
		{
			throw new ArgumentException("The round asset does not match the network manifest.", nameof(peggedAssetId));
		}
		if (fee <= 0 || fee > long.MaxValue / 2)
		{
			throw new ArgumentOutOfRangeException(nameof(fee));
		}
		if (String.IsNullOrWhiteSpace(issuerRole) || ownerCount != 2)
		{
			throw new ArgumentException("A two-owner round requires a non-empty issuer role.");
		}

		RoundId = roundId;
		NetworkManifestId = networkManifest.ManifestId;
		GenesisHash = genesisHash;
		PeggedAssetId = peggedAssetId;
		Fee = fee;
		IssuerRole = issuerRole;
		OwnerCount = ownerCount;
	}

	public string NetworkManifestId { get; }

	// Native local rounds may use a synthetic genesis. This constructor does not
	// admit that identity to the public-network manifest catalog or wallet APIs.
	internal LiquidCoinJoinRoundParameters(string roundId, string networkIdentity, byte[] genesis, byte[] asset, long fee, string issuerRole)
	{
		if (string.IsNullOrWhiteSpace(roundId) || string.IsNullOrWhiteSpace(networkIdentity) || genesis.Length != 32 || asset.Length != 32 || fee <= 0 || string.IsNullOrWhiteSpace(issuerRole))
			throw new ArgumentException("Invalid native local round identity.");
		RoundId = roundId;
		NetworkManifestId = networkIdentity;
		GenesisHash = Convert.ToHexString(genesis);
		PeggedAssetId = Convert.ToHexString(asset);
		Fee = fee;
		IssuerRole = issuerRole;
		OwnerCount = 2;
	}
	public string RoundId { get; }
	public string GenesisHash { get; }
	public string PeggedAssetId { get; }
	public long Fee { get; }
	public string IssuerRole { get; }
	public int OwnerCount { get; }
}
