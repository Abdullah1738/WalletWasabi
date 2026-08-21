using System;

namespace WalletWasabi.Liquid.Wallet.Ui;

public sealed class LiquidWalletUiBootstrapSnapshot
{
	public LiquidWalletUiBootstrapSnapshot(string canonicalWalletId, string networkManifestId, long sourceRevision)
	{
		CanonicalWalletId = canonicalWalletId ?? throw new ArgumentNullException(nameof(canonicalWalletId));
		NetworkManifestId = networkManifestId ?? throw new ArgumentNullException(nameof(networkManifestId));
		SourceRevision = sourceRevision;
	}

	public string CanonicalWalletId { get; }
	public string NetworkManifestId { get; }
	public long SourceRevision { get; }
}

public sealed class LiquidWalletRuntimeHandoff
{
	public LiquidWalletRuntimeHandoff(
		string canonicalWalletId,
		string networkManifestId,
		LiquidWalletUiBootstrapSnapshot bootstrapSnapshot)
	{
		CanonicalWalletId = canonicalWalletId ?? throw new ArgumentNullException(nameof(canonicalWalletId));
		NetworkManifestId = networkManifestId ?? throw new ArgumentNullException(nameof(networkManifestId));
		BootstrapSnapshot = bootstrapSnapshot ?? throw new ArgumentNullException(nameof(bootstrapSnapshot));
	}

	public string CanonicalWalletId { get; }
	public string NetworkManifestId { get; }
	public LiquidWalletUiBootstrapSnapshot BootstrapSnapshot { get; }
}
