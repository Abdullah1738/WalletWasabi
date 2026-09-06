namespace WalletWasabi.Liquid.Assets;

/// <summary>
/// Reviewed display-only snapshot for Liquid testnet, not an issuance or chain-inclusion proof.
/// The source bytes and metadata are checked offline in tests. Contract/issuance linkage was
/// reviewed read-only, but asset-ID derivation is not independently verified here: the managed
/// dependencies expose no Elements derivation or SHA-256 midstate API. No chain inclusion check
/// was performed. Neither source attribution nor labels authenticate an issuer.
/// </summary>
internal static class LiquidTestnetAssetSnapshot
{
	internal const string Source = "Blockstream/asset_registry_testnet_db";
	internal const string Revision = "e07ca133ed964a5978cd57b836f2eacb342df588";
	internal const string SourcePath = "38/38fca2d939696061a8f76d4e6b5eecd54e3b4221c846f24a6b279e79952850a5.json";
	internal const string SourceSha256 = "8c4f129205a2ea6a290826eb7121dc103bf5bb94dc9b3ebe210257b00f63dde2";
	internal const string SourceJson = """
		{"asset_id":"38fca2d939696061a8f76d4e6b5eecd54e3b4221c846f24a6b279e79952850a5","contract":{"entity":{"domain":"liquidtestnet.com"},"issuer_pubkey":"035d0f7b0207d9cc68870abfef621692bce082084ed3ca0c1ae432dd12d889be01","name":"Testnet Asset","precision":3,"ticker":"TEST","version":0},"issuance_txin":{"txid":"8be4123cca8e77ff960f9ff64e66cae40d05105b86c49ccc1a9d7f4475f5f793","vin":0},"issuance_prevout":{"txid":"0e19e938c74378ae83b549213a12be88ede6e32e1407bfdf50c4ec3f927408ec","vout":0},"version":0,"issuer_pubkey":"035d0f7b0207d9cc68870abfef621692bce082084ed3ca0c1ae432dd12d889be01","name":"Testnet Asset","ticker":"TEST","precision":3,"entity":{"domain":"liquidtestnet.com"}}
		""";

	internal static LiquidAssetMetadata Metadata { get; } = new(
		"38fca2d939696061a8f76d4e6b5eecd54e3b4221c846f24a6b279e79952850a5", "TEST", "Testnet Asset", 3);
}
