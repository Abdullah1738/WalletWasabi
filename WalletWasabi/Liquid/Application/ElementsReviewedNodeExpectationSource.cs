using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Application;

internal sealed record ElementsReviewedNodeExpectationDescriptor(
	string ManifestId,
	string Network,
	string FedpegScript,
	int PeginConfirmationDepth);

internal static class ElementsReviewedNodeExpectationSource
{
	private static readonly ElementsReviewedNodeExpectationDescriptor[] ReviewedCatalog =
	[
		new(
			"b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b",
			"liquidv1",
			"745c87635b21020e0338c96a8870479f2396c373cc7696ba124e8635d41b0ea581112b678172612102675333a4e4b8fb51d9d4e22fa5a8eaced3fdac8a8cbf9be8c030f75712e6af992102896807d54bc55c24981f24a453c60ad3e8993d693732288068a23df3d9f50d4821029e51a5ef5db3137051de8323b001749932f2ff0d34c82e96a2c2461de96ae56c2102a4e1a9638d46923272c266631d94d36bdb03a64ee0e14c7518e49d2f29bc40102102f8a00b269f8c5e59c67d36db3cdc11b11b21f64b4bffb2815e9100d9aa8daf072103079e252e85abffd3c401a69b087e590a9b86f33f574f08129ccbd3521ecf516b2103111cf405b627e22135b3b3733a4a34aa5723fb0f58379a16d32861bf576b0ec2210318f331b3e5d38156da6633b31929c5b220349859cc9ca3d33fb4e68aa08401742103230dae6b4ac93480aeab26d000841298e3b8f6157028e47b0897c1e025165de121035abff4281ff00660f99ab27bb53e6b33689c2cd8dcd364bc3c90ca5aea0d71a62103bd45cddfacf2083b14310ae4a84e25de61e451637346325222747b157446614c2103cc297026b06c71cbfa52089149157b5ff23de027ac5ab781800a578192d175462103d3bde5d63bdb3a6379b461be64dad45eabff42f758543a9645afd42f6d4248282103ed1e8d5109c9ed66f7941bc53cc71137baa76d50d274bda8d5e8ffbd6e61fe9a5f6702c00fb275522103aab896d53a8e7d6433137bbba940f9c521e085dd07e60994579b64a6d992cf79210291b7d0b1b692f8f524516ed950872e5da10fb1b808b5a526dedc6fed1cf29807210386aa9372fbab374593466bc5451dc59954e90787f08060964d95c87ef34ca5bb5368ae",
			100),
		new(
			"e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20",
			"liquidtestnet",
			"51",
			8),
	];

	internal static ElementsNodeExpectation Bind(
		ElementsPublicNetworkManifest manifest,
		LiquidRpcProfile profile)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(profile);
		AssertCatalogShape();
		ElementsReviewedNodeExpectationDescriptor[] matches = ReviewedCatalog
			.Where(row => StringComparer.Ordinal.Equals(row.ManifestId, manifest.ManifestId))
			.ToArray();
		if (matches.Length != 1)
		{
			throw new InvalidOperationException("The reviewed node-expectation catalog must select exactly one ordinal descriptor_manifest row.");
		}
		return ValidateDescriptor(matches[0], manifest, profile);
	}

	internal static ElementsNodeExpectation ValidateDescriptor(
		ElementsReviewedNodeExpectationDescriptor descriptor,
		ElementsPublicNetworkManifest manifest,
		LiquidRpcProfile profile)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(profile);
		RequireProfile(StringComparer.Ordinal.Equals(profile.Manifest, manifest.ManifestId), "profile_manifest");
		RequireProfile(StringComparer.Ordinal.Equals(profile.Network, manifest.ChainRpcName), "profile_network");
		RequireInvariant(StringComparer.Ordinal.Equals(descriptor.ManifestId, manifest.ManifestId), "descriptor_manifest");
		RequireInvariant(StringComparer.Ordinal.Equals(descriptor.Network, manifest.ChainRpcName), "descriptor_network");

		var candidate = new ElementsNodeExpectation(
			manifest.ChainRpcName,
			manifest.GenesisBlockHash,
			descriptor.FedpegScript,
			manifest.PeggedAssetId,
			manifest.ParentGenesisHash,
			descriptor.PeginConfirmationDepth,
			manifest.EnforcePak,
			manifest.ElementsNumericVersion,
			manifest.ElementsProtocolVersion,
			manifest.ExpectedSubversion);
		ElementsNodeExpectation normalized = candidate.Normalize();
		RequireInvariant(normalized == candidate, "normalization");
		ValidateExpectation(normalized, manifest, descriptor);
		return normalized;
	}

	internal static void AssertCatalogShape()
	{
		ElementsPublicNetworkManifest[] admittedManifests =
		[
			ElementsPublicNetworkManifest.LiquidMainnet,
			ElementsPublicNetworkManifest.LiquidTestnet,
		];
		RequireInvariant(ReviewedCatalog.Length == admittedManifests.Length, "catalog_count");
		RequireInvariant(ReviewedCatalog.Select(row => row.ManifestId).Distinct(StringComparer.Ordinal).Count() == ReviewedCatalog.Length, "duplicate_descriptor_manifest");
		foreach (ElementsPublicNetworkManifest manifest in admittedManifests)
		{
			ElementsReviewedNodeExpectationDescriptor[] rows = ReviewedCatalog
				.Where(row => StringComparer.Ordinal.Equals(row.ManifestId, manifest.ManifestId))
				.ToArray();
			RequireInvariant(rows.Length == 1, "catalog_manifest_cardinality");
			RequireInvariant(StringComparer.Ordinal.Equals(rows[0].Network, manifest.ChainRpcName), "descriptor_network");
		}
		RequireInvariant(ReviewedCatalog.All(row => admittedManifests.Any(manifest => StringComparer.Ordinal.Equals(row.ManifestId, manifest.ManifestId))), "extra_descriptor_manifest");
	}

	internal static void ValidateOwnerExpectation(
		LiquidWalletIdentity identity,
		ElementsPublicNetworkManifest manifest,
		ElementsNodeExpectation expectation)
	{
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(expectation);
		AssertCatalogShape();
		RequireInvariant(StringComparer.Ordinal.Equals(identity.NetworkManifestId, manifest.ManifestId), "descriptor_manifest");
		ElementsReviewedNodeExpectationDescriptor[] rows = ReviewedCatalog
			.Where(row => StringComparer.Ordinal.Equals(row.ManifestId, identity.NetworkManifestId))
			.ToArray();
		RequireInvariant(rows.Length == 1, "catalog_manifest_cardinality");
		ElementsReviewedNodeExpectationDescriptor reviewedDescriptor = rows[0];
		RequireInvariant(StringComparer.Ordinal.Equals(reviewedDescriptor.ManifestId, manifest.ManifestId), "descriptor_manifest");
		RequireInvariant(StringComparer.Ordinal.Equals(reviewedDescriptor.Network, manifest.ChainRpcName), "descriptor_network");
		ElementsNodeExpectation normalized = expectation.Normalize();
		RequireInvariant(normalized == expectation, "normalization");
		ValidateExpectation(expectation, manifest, reviewedDescriptor);
	}

	private static void ValidateExpectation(
		ElementsNodeExpectation expectation,
		ElementsPublicNetworkManifest manifest,
		ElementsReviewedNodeExpectationDescriptor descriptor)
	{
		RequireInvariant(StringComparer.Ordinal.Equals(expectation.Chain, manifest.ChainRpcName), "chain");
		RequireInvariant(StringComparer.Ordinal.Equals(expectation.GenesisBlockHash, manifest.GenesisBlockHash), "genesis_block_hash");
		RequireInvariant(StringComparer.Ordinal.Equals(expectation.PeggedAsset, manifest.PeggedAssetId), "pegged_asset");
		RequireInvariant(StringComparer.Ordinal.Equals(expectation.ParentGenesisBlockHash, manifest.ParentGenesisHash), "parent_genesis_block_hash");
		RequireInvariant(expectation.EnforcePak == manifest.EnforcePak, "enforce_pak");
		RequireInvariant(expectation.Version == manifest.ElementsNumericVersion, "version");
		RequireInvariant(expectation.ProtocolVersion == manifest.ElementsProtocolVersion, "protocol_version");
		RequireInvariant(StringComparer.Ordinal.Equals(expectation.Subversion, manifest.ExpectedSubversion), "subversion");
		string scriptHash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(expectation.FedpegScript))).ToLower(CultureInfo.InvariantCulture);
		RequireInvariant(StringComparer.Ordinal.Equals(scriptHash, manifest.FedpegScriptSha256), "fedpeg_script_sha256");
		RequireInvariant(expectation.PeginConfirmationDepth == descriptor.PeginConfirmationDepth, "pegin_confirmation_depth");
	}

	private static void RequireProfile(bool condition, string field)
	{
		if (!condition)
		{
			throw new InvalidDataException($"The reviewed node-expectation profile violates '{field}'.");
		}
	}

	private static void RequireInvariant(bool condition, string field)
	{
		if (!condition)
		{
			throw new InvalidOperationException($"The reviewed node-expectation invariant violates '{field}'.");
		}
	}
}
