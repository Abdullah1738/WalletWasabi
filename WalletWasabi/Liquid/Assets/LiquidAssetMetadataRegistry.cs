using System.Collections.Frozen;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Assets;

/// <summary>Network-bound, canonical asset-ID lookup for display and exact UI input conversion only.</summary>
public sealed class LiquidAssetMetadataRegistry
{
	private readonly FrozenDictionary<string, LiquidAssetMetadata> _entries;

	/// <summary>
	/// Copies explicitly trusted static entries for the supplied snapshot network.
	/// The caller must review both the entries and their network attribution; this
	/// constructor does not authenticate remote registry data or establish issuance identity.
	/// </summary>
	public LiquidAssetMetadataRegistry(
		ElementsPublicNetworkManifest manifest,
		string trustedSnapshotNetworkManifestId,
		IEnumerable<LiquidAssetMetadata> trustedSnapshotEntries)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(trustedSnapshotEntries);
		if (!StringComparer.Ordinal.Equals(manifest.ManifestId, trustedSnapshotNetworkManifestId))
		{
			throw new ArgumentException("The trusted asset snapshot belongs to a different network manifest.", nameof(trustedSnapshotNetworkManifestId));
		}

		NetworkManifestId = manifest.ManifestId;
		PeggedAssetId = manifest.PeggedAssetId;
		var entries = new Dictionary<string, LiquidAssetMetadata>(StringComparer.Ordinal)
		{
			[PeggedAssetId] = new(PeggedAssetId, "L-BTC", "Liquid Bitcoin", 8)
		};
		foreach (var entry in trustedSnapshotEntries)
		{
			ArgumentNullException.ThrowIfNull(entry);
			// Reject all duplicate IDs, including attempts to override the pegged asset.
			entries.Add(entry.AssetIdHex, entry);
		}
		_entries = entries.ToFrozenDictionary(StringComparer.Ordinal);
	}

	public string NetworkManifestId { get; }
	public string PeggedAssetId { get; }

	// Noncanonical input never aliases an entry. Missing metadata has no inferred name or precision.
	public bool TryGet(string assetIdHex, out LiquidAssetMetadata metadata) => _entries.TryGetValue(assetIdHex, out metadata!);

	public static LiquidAssetMetadataRegistry ForManifest(ElementsPublicNetworkManifest manifest)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		if (StringComparer.Ordinal.Equals(manifest.ManifestId, ElementsPublicNetworkManifest.LiquidTestnet.ManifestId))
		{
			return new(manifest, ElementsPublicNetworkManifest.LiquidTestnet.ManifestId,
				[LiquidTestnetAssetSnapshot.Metadata]);
		}
		return new(manifest, manifest.ManifestId, []);
	}
}
