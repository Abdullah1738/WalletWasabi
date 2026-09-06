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
	public string RoundId { get; }
	public string GenesisHash { get; }
	public string PeggedAssetId { get; }
	public long Fee { get; }
	public string IssuerRole { get; }
	public int OwnerCount { get; }
}
