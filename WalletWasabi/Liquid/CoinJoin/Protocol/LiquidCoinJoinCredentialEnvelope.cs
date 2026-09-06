using WabiSabi.CredentialRequesting;

namespace WalletWasabi.Liquid.CoinJoin.Protocol;

internal sealed record LiquidCoinJoinCredentialRequestEnvelope(
	string RoundId,
	string IssuerRole,
	string NetworkManifestId,
	string GenesisHash,
	string PeggedAssetId,
	string ParticipantId,
	long Amount,
	long Delta,
	ICredentialsRequest Request);

internal sealed record LiquidCoinJoinCredentialResponseEnvelope(
	string RoundId,
	string IssuerRole,
	string NetworkManifestId,
	string GenesisHash,
	string PeggedAssetId,
	CredentialsResponse Response);
