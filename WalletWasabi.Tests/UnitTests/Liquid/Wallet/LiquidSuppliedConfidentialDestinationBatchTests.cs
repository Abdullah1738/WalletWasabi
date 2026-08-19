using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidSuppliedConfidentialDestinationBatchTests
{
	private const string BlindingPublicKeyHex =
		"0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string OpaqueAssetHex =
		"2222222222222222222222222222222222222222222222222222222222222222";
	private const string OtherAssetHex =
		"3333333333333333333333333333333333333333333333333333333333333333";
	private const string OtherPeggedAssetHex =
		"4444444444444444444444444444444444444444444444444444444444444444";
	private const string ScriptHex = "00140102030405060708090a0b0c0d0e0f1011121314";
	private const string OtherScriptHex = "001415161718191a1b1c1d1e1f202122232425262728";
	private const string PrivateLabel = "private-batch-label-canary-519407";

#if DEBUG
	private const string ExpectedImplementationManifestSha256 =
		"abe2929150196807f50734f8d5fbfdef7f720282e46c9c2bef9ca4af8b8abaa1";
#else
	private const string ExpectedImplementationManifestSha256 =
		"09bd94baa78c655627735792eb971eae65f3152b054cf7107a079a37c5ddbe04";
#endif

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CreatesOrderedMultiassetBatchWithExactIndependentTotals(bool testnet)
	{
		ElementsPublicNetworkManifest manifest = testnet
			? ElementsPublicNetworkManifest.LiquidTestnet
			: ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidSuppliedConfidentialDestination first = Destination(
			manifest, ScriptHex, OpaqueAssetHex, 7, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination policy = Destination(
			manifest, OtherScriptHex, manifest.PeggedAssetId, 11, ["policy"]);
		LiquidSuppliedConfidentialDestination repeated = Destination(
			manifest, OtherScriptHex, OpaqueAssetHex, 13, ["repeated"]);

		LiquidSuppliedConfidentialDestinationBatch batch =
			LiquidSuppliedConfidentialDestinationBatch.Create([first, policy, repeated]);

		Assert.Equal(3, batch.Count);
		Assert.Equal(manifest.ManifestId, batch.GetNetworkManifestId());
		Assert.Equal(PeggedAsset(manifest), batch.GetPeggedAssetId());
		Assert.Equal([first, policy, repeated], batch.GetDestinations());
		LiquidAssetBalanceMap requested = batch.GetRequestedAmounts();
		Assert.Equal(2, requested.AssetCount);
		Assert.Equal(20, Amount(requested, OpaqueAssetHex));
		Assert.Equal(11, Amount(requested, manifest.PeggedAssetId));
		Assert.Equal(
			new[] { OpaqueAssetHex, manifest.PeggedAssetId }
				.OrderBy(value => value, StringComparer.Ordinal),
			requested.GetAmounts().Select(amount => amount.AssetId.CanonicalRpcHex));
		Assert.Equal(nameof(LiquidSuppliedConfidentialDestinationBatch), batch.ToString());
	}

	[Fact]
	public void EnforcesExactBoundsAndUsesIndexedSnapshotOnce()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidSuppliedConfidentialDestination value = Destination(
			manifest, ScriptHex, OpaqueAssetHex, 1, [PrivateLabel]);
		var one = new IndexedList([value]);

		LiquidSuppliedConfidentialDestinationBatch accepted =
			LiquidSuppliedConfidentialDestinationBatch.Create(one);

		Assert.Equal(1, accepted.Count);
		Assert.Equal(1, one.CountReads);
		Assert.Equal(1, one.IndexReads);
		Assert.Equal(0, one.EnumerationRequests);

		var maximum = new IndexedList(
			Enumerable.Repeat(value, LiquidSuppliedConfidentialDestinationBatch.MaximumDestinationCount)
				.ToArray());
		Assert.Equal(
			LiquidSuppliedConfidentialDestinationBatch.MaximumDestinationCount,
			LiquidSuppliedConfidentialDestinationBatch.Create(maximum).Count);
		Assert.Equal(1, maximum.CountReads);
		Assert.Equal(LiquidSuppliedConfidentialDestinationBatch.MaximumDestinationCount, maximum.IndexReads);
		Assert.Equal(0, maximum.EnumerationRequests);

		ArgumentOutOfRangeException emptyFailure = AssertArgumentFailure<ArgumentOutOfRangeException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create([]));
		ArgumentOutOfRangeException largeFailure = AssertArgumentFailure<ArgumentOutOfRangeException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(
				Enumerable.Repeat(value, LiquidSuppliedConfidentialDestinationBatch.MaximumDestinationCount + 1)
					.ToArray()));
		Assert.Null(emptyFailure.ActualValue);
		Assert.Null(largeFailure.ActualValue);
	}

	[Fact]
	public void RejectsNullIncompleteAndMixedContextInputsInExactPrecedence()
	{
		ElementsPublicNetworkManifest mainnet = ElementsPublicNetworkManifest.LiquidMainnet;
		ElementsPublicNetworkManifest testnet = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidSuppliedConfidentialDestination valid = Destination(
			mainnet, ScriptHex, OpaqueAssetHex, 1, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination incomplete = LiquidSuppliedConfidentialDestination.Create(
			mainnet,
			ConfidentialAddress(mainnet, OtherScriptHex),
			LiquidAssetId.ParseRpcHex(OtherAssetHex),
			null,
			LiquidWalletLabelSet.Empty);
		LiquidSuppliedConfidentialDestination otherManifest = Destination(
			testnet, ScriptHex, OpaqueAssetHex, 1, ["other-network"]);
		LiquidSuppliedConfidentialDestination otherContext = CreateUncheckedDestination(
			mainnet.ManifestId,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			ConfidentialAddress(mainnet, OtherScriptHex),
			LiquidAssetId.ParseRpcHex(OtherAssetHex),
			LiquidAssetAmount.Create(
				LiquidAssetId.ParseRpcHex(OtherAssetHex),
				LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
				1),
			LiquidWalletLabelSet.Empty);

		AssertNullFailure(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(null!));
		AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create([valid, null!, incomplete]));
		AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create([valid, incomplete, otherManifest]));
		AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create([valid, otherManifest]));
		AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create([valid, otherContext]));
	}

	[Fact]
	public void AggregationOverflowIsCheckedAndAtomic()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidSuppliedConfidentialDestination issuedMaximum = Destination(
			manifest, ScriptHex, OpaqueAssetHex, long.MaxValue, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination issuedOne = Destination(
			manifest, OtherScriptHex, OpaqueAssetHex, 1, ["one"]);
		LiquidSuppliedConfidentialDestination peggedMaximum = Destination(
			manifest,
			ScriptHex,
			manifest.PeggedAssetId,
			LiquidAssetAmount.MaxPeggedAssetAtomicUnits,
			[PrivateLabel]);
		LiquidSuppliedConfidentialDestination peggedOne = Destination(
			manifest, OtherScriptHex, manifest.PeggedAssetId, 1, ["one"]);

		Assert.Throws<OverflowException>(() =>
			LiquidSuppliedConfidentialDestinationBatch.Create([issuedMaximum, issuedOne]));
		Assert.Throws<OverflowException>(() =>
			LiquidSuppliedConfidentialDestinationBatch.Create([peggedMaximum, peggedOne]));
	}

	[Fact]
	public void OwnsContextSnapshotAndEveryRequestedTotalDisclosure()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidSuppliedConfidentialDestination first = Destination(
			manifest, ScriptHex, OpaqueAssetHex, 9, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination repeated = Destination(
			manifest, OtherScriptHex, OpaqueAssetHex, 4, ["second"]);
		LiquidSuppliedConfidentialDestination singleton = Destination(
			manifest, ScriptHex, OtherAssetHex, 6, ["singleton"]);

		LiquidSuppliedConfidentialDestinationBatch batch =
			LiquidSuppliedConfidentialDestinationBatch.Create([first, repeated, singleton]);
		LiquidSuppliedConfidentialDestinationBatch equalBatch =
			LiquidSuppliedConfidentialDestinationBatch.Create([first, repeated, singleton]);

		Assert.Equal(first.GetNetworkManifestId(), batch.GetNetworkManifestId());
		Assert.NotSame(first.GetNetworkManifestId(), batch.GetNetworkManifestId());
		Assert.NotSame(repeated.GetNetworkManifestId(), batch.GetNetworkManifestId());
		Assert.NotSame(singleton.GetNetworkManifestId(), batch.GetNetworkManifestId());
		Assert.NotSame(batch.GetNetworkManifestId(), equalBatch.GetNetworkManifestId());
		Assert.Equal(first.GetPeggedAssetId(), batch.GetPeggedAssetId());
		Assert.NotSame(first.GetPeggedAssetId(), batch.GetPeggedAssetId());
		Assert.NotSame(repeated.GetPeggedAssetId(), batch.GetPeggedAssetId());
		Assert.NotSame(singleton.GetPeggedAssetId(), batch.GetPeggedAssetId());
		Assert.NotSame(batch.GetPeggedAssetId(), equalBatch.GetPeggedAssetId());

		LiquidAssetBalanceMap firstDisclosure = batch.GetRequestedAmounts();
		LiquidAssetBalanceMap secondDisclosure = batch.GetRequestedAmounts();
		Assert.NotSame(firstDisclosure, secondDisclosure);
		Assert.NotSame(firstDisclosure.PeggedAssetId, secondDisclosure.PeggedAssetId);
		Assert.NotSame(firstDisclosure.PeggedAssetId, batch.GetPeggedAssetId());
		Assert.NotSame(firstDisclosure.PeggedAssetId, first.GetPeggedAssetId());
		Assert.NotSame(firstDisclosure.PeggedAssetId, singleton.GetPeggedAssetId());
		Dictionary<string, LiquidAssetAmount> firstTotals = firstDisclosure.GetAmounts()
			.ToDictionary(amount => amount.AssetId.CanonicalRpcHex, StringComparer.Ordinal);
		Dictionary<string, LiquidAssetAmount> secondTotals = secondDisclosure.GetAmounts()
			.ToDictionary(amount => amount.AssetId.CanonicalRpcHex, StringComparer.Ordinal);
		Assert.Equal(2, firstTotals.Count);
		Assert.Equal(13, firstTotals[OpaqueAssetHex].AtomicUnits);
		Assert.Equal(6, firstTotals[OtherAssetHex].AtomicUnits);
		LiquidAssetAmount[] sourceAmounts =
		[
			first.GetAmount()!,
			repeated.GetAmount()!,
			singleton.GetAmount()!,
		];
		foreach ((string assetHex, LiquidAssetAmount firstTotal) in firstTotals)
		{
			LiquidAssetAmount secondTotal = secondTotals[assetHex];
			Assert.Equal(firstTotal, secondTotal);
			Assert.NotSame(firstTotal, secondTotal);
			Assert.NotSame(firstTotal.AssetId, secondTotal.AssetId);
			Assert.NotSame(firstTotal.PeggedAssetId, secondTotal.PeggedAssetId);
			foreach (LiquidAssetAmount sourceAmount in sourceAmounts)
			{
				Assert.NotSame(firstTotal, sourceAmount);
				Assert.NotSame(firstTotal.AssetId, sourceAmount.AssetId);
				Assert.NotSame(firstTotal.PeggedAssetId, sourceAmount.PeggedAssetId);
				Assert.NotSame(secondTotal, sourceAmount);
				Assert.NotSame(secondTotal.AssetId, sourceAmount.AssetId);
				Assert.NotSame(secondTotal.PeggedAssetId, sourceAmount.PeggedAssetId);
			}
		}
	}

	[Fact]
	public void PreservesOrderMultiplicityAndDefendsCollectionOwnership()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidSuppliedConfidentialDestination first = Destination(
			manifest, ScriptHex, OpaqueAssetHex, 2, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination second = Destination(
			manifest, OtherScriptHex, OtherAssetHex, 3, ["second"]);
		LiquidSuppliedConfidentialDestination[] source = [first, second, first];

		LiquidSuppliedConfidentialDestinationBatch batch =
			LiquidSuppliedConfidentialDestinationBatch.Create(source);
		source[0] = second;
		IReadOnlyList<LiquidSuppliedConfidentialDestination> disclosed = batch.GetDestinations();
		Assert.Equal([first, second, first], disclosed);
		Assert.Same(first, disclosed[0]);
		Assert.Same(first, disclosed[2]);
		Assert.Throws<NotSupportedException>(() => ((IList)disclosed)[0] = second);
		Assert.Equal([first, second, first], batch.GetDestinations());
		Assert.NotSame(disclosed, batch.GetDestinations());
		Assert.Equal(4, Amount(batch.GetRequestedAmounts(), OpaqueAssetHex));
		Assert.Equal(3, Amount(batch.GetRequestedAmounts(), OtherAssetHex));
	}

	[Fact]
	public void EqualityAndHashingIncludeContextOrderMultiplicityAndDestinationValues()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidSuppliedConfidentialDestination first = Destination(
			manifest, ScriptHex, OpaqueAssetHex, 2, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination second = Destination(
			manifest, OtherScriptHex, OtherAssetHex, 3, ["second"]);
		LiquidSuppliedConfidentialDestination equalFirst = Destination(
			manifest, ScriptHex, OpaqueAssetHex, 2, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination equalSecond = Destination(
			manifest, OtherScriptHex, OtherAssetHex, 3, ["second"]);
		LiquidSuppliedConfidentialDestinationBatch value =
			LiquidSuppliedConfidentialDestinationBatch.Create([first, second]);
		LiquidSuppliedConfidentialDestinationBatch equal =
			LiquidSuppliedConfidentialDestinationBatch.Create([equalFirst, equalSecond]);

		Assert.True(value.Equals(value));
		Assert.True(value.Equals(equal));
		Assert.True(((object)value).Equals(equal));
		Assert.Equal(value.GetHashCode(), equal.GetHashCode());
		Assert.False(value.Equals(null));
		Assert.False(value.Equals(new object()));
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([second, first]));
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([first, second, second]));
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([
			Destination(manifest, OtherScriptHex, OpaqueAssetHex, 2, [PrivateLabel]), second]));
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([
			Destination(manifest, ScriptHex, OtherAssetHex, 2, [PrivateLabel]), second]));
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([
			Destination(manifest, ScriptHex, OpaqueAssetHex, 4, [PrivateLabel]), second]));
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([
			Destination(manifest, ScriptHex, OpaqueAssetHex, 2, ["different"]), second]));

		LiquidSuppliedConfidentialDestination differentManifest = CreateUncheckedDestination(
			"different-manifest",
			first.GetPeggedAssetId(),
			first.GetAddress(),
			first.GetAssetId(),
			first.GetAmount(),
			first.GetLabels());
		LiquidSuppliedConfidentialDestination differentManifestSecond = CreateUncheckedDestination(
			"different-manifest",
			second.GetPeggedAssetId(),
			second.GetAddress(),
			second.GetAssetId(),
			second.GetAmount(),
			second.GetLabels());
		LiquidSuppliedConfidentialDestination differentContext = CreateUncheckedDestination(
			manifest.ManifestId,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			first.GetAddress(),
			first.GetAssetId(),
			LiquidAssetAmount.Create(
				first.GetAssetId(),
				LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
				2),
			first.GetLabels());
		LiquidSuppliedConfidentialDestination differentContextSecond = CreateUncheckedDestination(
			manifest.ManifestId,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			second.GetAddress(),
			second.GetAssetId(),
			LiquidAssetAmount.Create(
				second.GetAssetId(),
				LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
				3),
			second.GetLabels());
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([
			differentManifest, differentManifestSecond]));
		Assert.NotEqual(value, LiquidSuppliedConfidentialDestinationBatch.Create([
			differentContext, differentContextSecond]));
	}

	[Fact]
	public void RenderingAndOwnedFailuresDoNotDiscloseOrRetainInputs()
	{
		ElementsPublicNetworkManifest mainnet = ElementsPublicNetworkManifest.LiquidMainnet;
		ElementsPublicNetworkManifest testnet = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidSuppliedConfidentialDestination first = Destination(
			mainnet, ScriptHex, OpaqueAssetHex, 91_827_364, [PrivateLabel]);
		LiquidSuppliedConfidentialDestination otherNetwork = Destination(
			testnet, OtherScriptHex, OtherAssetHex, 72_615_493, ["other-private-label"]);
		LiquidSuppliedConfidentialDestination incomplete = LiquidSuppliedConfidentialDestination.Create(
			mainnet,
			ConfidentialAddress(mainnet, OtherScriptHex),
			LiquidAssetId.ParseRpcHex(OtherAssetHex),
			null,
			LiquidWalletLabelSet.Create(["incomplete-private-label"]));
		LiquidAssetId otherPeggedAsset = LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);
		LiquidAssetId otherContextAsset = LiquidAssetId.ParseRpcHex(OtherAssetHex);
		LiquidSuppliedConfidentialDestination otherContext = CreateUncheckedDestination(
			mainnet.ManifestId,
			otherPeggedAsset,
			ConfidentialAddress(mainnet, OtherScriptHex),
			otherContextAsset,
			LiquidAssetAmount.Create(otherContextAsset, otherPeggedAsset, 63_504_281),
			LiquidWalletLabelSet.Create(["context-private-label"]));
		LiquidSuppliedConfidentialDestination issuedMaximum = Destination(
			mainnet, ScriptHex, OpaqueAssetHex, long.MaxValue, ["issued-maximum-label"]);
		LiquidSuppliedConfidentialDestination issuedOne = Destination(
			mainnet, OtherScriptHex, OpaqueAssetHex, 1, ["issued-one-label"]);
		LiquidSuppliedConfidentialDestination peggedMaximum = Destination(
			mainnet,
			ScriptHex,
			mainnet.PeggedAssetId,
			LiquidAssetAmount.MaxPeggedAssetAtomicUnits,
			["pegged-maximum-label"]);
		LiquidSuppliedConfidentialDestination peggedOne = Destination(
			mainnet, OtherScriptHex, mainnet.PeggedAssetId, 1, ["pegged-one-label"]);

		LiquidSuppliedConfidentialDestination[] emptyInput = [];
		LiquidSuppliedConfidentialDestination[] largeInput = Enumerable.Repeat(
			first,
			LiquidSuppliedConfidentialDestinationBatch.MaximumDestinationCount + 1).ToArray();
		LiquidSuppliedConfidentialDestination[] nullEntryInput = [first, null!];
		LiquidSuppliedConfidentialDestination[] incompleteInput = [first, incomplete];
		LiquidSuppliedConfidentialDestination[] mixedManifestInput = [first, otherNetwork];
		LiquidSuppliedConfidentialDestination[] mixedContextInput = [first, otherContext];
		LiquidSuppliedConfidentialDestination[] issuedOverflowInput = [issuedMaximum, issuedOne];
		LiquidSuppliedConfidentialDestination[] peggedOverflowInput = [peggedMaximum, peggedOne];
		string[] allCanaries = SensitiveCanaries(
			[
				first,
				otherNetwork,
				incomplete,
				otherContext,
				issuedMaximum,
				issuedOne,
				peggedMaximum,
				peggedOne,
			]);

		LiquidSuppliedConfidentialDestinationBatch accepted =
			LiquidSuppliedConfidentialDestinationBatch.Create([first]);
		AssertRedacted(accepted.ToString(), allCanaries);

		ArgumentNullException nullFailure = AssertNullFailure(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(null!));
		AssertOwnedFailurePrivacy(nullFailure, [], allCanaries);

		ArgumentOutOfRangeException emptyFailure = AssertArgumentFailure<ArgumentOutOfRangeException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(emptyInput));
		AssertOwnedFailurePrivacy(
			emptyFailure,
			SuppliedComponents(emptyInput),
			allCanaries);

		ArgumentOutOfRangeException largeFailure = AssertArgumentFailure<ArgumentOutOfRangeException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(largeInput));
		AssertOwnedFailurePrivacy(
			largeFailure,
			SuppliedComponents(largeInput),
			SensitiveCanaries(largeInput));

		ArgumentException nullEntryFailure = AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(nullEntryInput));
		AssertOwnedFailurePrivacy(
			nullEntryFailure,
			SuppliedComponents(nullEntryInput),
			SensitiveCanaries(nullEntryInput));

		ArgumentException incompleteFailure = AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(incompleteInput));
		AssertOwnedFailurePrivacy(
			incompleteFailure,
			SuppliedComponents(incompleteInput),
			SensitiveCanaries(incompleteInput));

		ArgumentException manifestFailure = AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(mixedManifestInput));
		AssertOwnedFailurePrivacy(
			manifestFailure,
			SuppliedComponents(mixedManifestInput),
			SensitiveCanaries(mixedManifestInput));

		ArgumentException contextFailure = AssertArgumentFailure<ArgumentException>(
			"destinations",
			() => LiquidSuppliedConfidentialDestinationBatch.Create(mixedContextInput));
		AssertOwnedFailurePrivacy(
			contextFailure,
			SuppliedComponents(mixedContextInput),
			SensitiveCanaries(mixedContextInput));

		OverflowException issuedOverflowFailure = Assert.Throws<OverflowException>(() =>
			LiquidSuppliedConfidentialDestinationBatch.Create(issuedOverflowInput));
		AssertOwnedFailurePrivacy(
			issuedOverflowFailure,
			SuppliedComponents(issuedOverflowInput),
			SensitiveCanaries(issuedOverflowInput));

		OverflowException peggedOverflowFailure = Assert.Throws<OverflowException>(() =>
			LiquidSuppliedConfidentialDestinationBatch.Create(peggedOverflowInput));
		AssertOwnedFailurePrivacy(
			peggedOverflowFailure,
			SuppliedComponents(peggedOverflowInput),
			SensitiveCanaries(peggedOverflowInput));
	}

	[Fact]
	public void DirectDependencySourcesRemainExact()
	{
		string root = FindRepositoryRoot();
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Wallet/LiquidSuppliedConfidentialDestination.cs",
			"ce73126abb53838790e9254658552641f908fd48ce0504c2bb3fbc7e9fbd65f5");
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Amounts/LiquidAssetBalanceMap.cs",
			"c95631f4f642002dd95cc684e549fdc567540d3c2f3ca4b0e5cdfb3f89522acb");
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Amounts/LiquidAssetAmount.cs",
			"8c3b2a403b8139f1e7bcc0689c8ca3e45499dfdd1364283d739cd40e93e249e4");
		AssertSourceSha256(
			root,
			"WalletWasabi/Liquid/Assets/LiquidAssetId.cs",
			"806fd6bb70d9b326385eae70f1ec99882aba04d4e0a31f38c6fc6a150266ba2b");
	}

	[Fact]
	public void ExactSurfaceIsFrozenAndContainsNoImplicitDisclosureMember()
	{
		Type type = typeof(LiquidSuppliedConfidentialDestinationBatch);
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		Assert.True(type.IsClass);
		Assert.True(type.IsSealed);
		Assert.False(type.IsAbstract);
		Assert.False(type.IsPublic);
		Assert.False(type.IsNested);
		Assert.Equal(typeof(object), type.BaseType);
		Assert.Equal([typeof(IEquatable<LiquidSuppliedConfidentialDestinationBatch>)], type.GetInterfaces());
		Assert.Empty(type.GetNestedTypes(Declared));
		Assert.Empty(type.GetEvents(Declared));
		Assert.Equal(["Count:System.Int32:public-get"], type.GetProperties(Declared)
			.Select(property =>
				$"{property.Name}:{property.PropertyType.FullName}:" +
				$"{(property.GetMethod?.IsPublic == true ? "public-get" : "nonpublic-get")}"));

		FieldInfo constant = Assert.Single(type.GetFields(Declared), field => field.IsLiteral);
		Assert.Equal(nameof(LiquidSuppliedConfidentialDestinationBatch.MaximumDestinationCount), constant.Name);
		Assert.Equal(typeof(int), constant.FieldType);
		Assert.True(constant.IsPublic);
		Assert.True(constant.IsStatic);
		Assert.Equal(256, constant.GetRawConstantValue());
		Assert.Equal(
			[
				"_destinations:WalletWasabi.Liquid.Wallet.LiquidSuppliedConfidentialDestination[]:readonly",
				"_networkManifestId:System.String:readonly",
				"_peggedAssetId:WalletWasabi.Liquid.Assets.LiquidAssetId:readonly",
				"_requestedAmounts:WalletWasabi.Liquid.Amounts.LiquidAssetBalanceMap:readonly",
			],
			type.GetFields(Declared)
				.Where(field => !field.IsStatic)
				.Select(field => $"{field.Name}:{field.FieldType.FullName}:{(field.IsInitOnly ? "readonly" : "mutable")}")
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.All(type.GetFields(Declared).Where(field => !field.IsStatic), field =>
		{
			Assert.True(field.IsPrivate);
			Assert.True(field.IsInitOnly);
		});

		ConstructorInfo constructor = Assert.Single(type.GetConstructors(Declared));
		Assert.True(constructor.IsPrivate);
		Assert.Equal(
			[
				typeof(string),
				typeof(LiquidAssetId),
				typeof(LiquidSuppliedConfidentialDestination[]),
				typeof(LiquidAssetBalanceMap),
			],
			constructor.GetParameters().Select(parameter => parameter.ParameterType));
		Assert.Equal(
			[
				"Create(System.Collections.Generic.IReadOnlyList`1[WalletWasabi.Liquid.Wallet.LiquidSuppliedConfidentialDestination])->WalletWasabi.Liquid.Wallet.LiquidSuppliedConfidentialDestinationBatch:static",
				"Equals(System.Object)->System.Boolean:instance",
				"Equals(WalletWasabi.Liquid.Wallet.LiquidSuppliedConfidentialDestinationBatch)->System.Boolean:instance",
				"GetDestinations()->System.Collections.Generic.IReadOnlyList`1[WalletWasabi.Liquid.Wallet.LiquidSuppliedConfidentialDestination]:instance",
				"GetHashCode()->System.Int32:instance",
				"GetNetworkManifestId()->System.String:instance",
				"GetPeggedAssetId()->WalletWasabi.Liquid.Assets.LiquidAssetId:instance",
				"GetRequestedAmounts()->WalletWasabi.Liquid.Amounts.LiquidAssetBalanceMap:instance",
				"ToString()->System.String:instance",
				"get_Count()->System.Int32:instance",
			],
			type.GetMethods(Declared)
				.Where(method => method.IsPublic)
				.Select(MethodSignature)
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.DoesNotContain(type.GetMethods(Declared), method => method.IsFamily || method.IsAssembly);
		Assert.DoesNotContain(type.CustomAttributes, IsForbiddenAttribute);
		Assert.DoesNotContain(type.GetFields(Declared).SelectMany(field => field.CustomAttributes), IsForbiddenAttribute);
		Assert.DoesNotContain(type.GetProperties(Declared).SelectMany(property => property.CustomAttributes), IsForbiddenAttribute);
		Assert.DoesNotContain(type.GetMethods(Declared).SelectMany(method => method.CustomAttributes), IsForbiddenAttribute);
	}

	[Fact]
	public void CompleteOwnedImplementationGraphIsFrozenAndContainsNoForbiddenSurface()
	{
		Type type = typeof(LiquidSuppliedConfidentialDestinationBatch);
		string manifest = BuildImplementationManifest(type);

		foreach (MethodBase method in OwnedMethods(type))
		{
			Assert.Empty(GetIlSignatures(method));
			Assert.DoesNotContain(
				GetIlOpCodes(method),
				opCode => opCode is var candidate &&
					(candidate == OpCodes.Calli || candidate == OpCodes.Ldftn ||
					 candidate == OpCodes.Ldvirtftn || candidate == OpCodes.Localloc));
			Assert.DoesNotContain(
				method.GetMethodBody()?.LocalVariables ?? [],
				local => ContainsForbiddenType(local.LocalType));
			Assert.DoesNotContain(
				method.GetMethodBody()?.ExceptionHandlingClauses ?? [],
				clause => ContainsForbiddenType(
					clause.Flags == ExceptionHandlingClauseOptions.Clause ? clause.CatchType : null));
			foreach (MemberInfo reference in GetIlReferences(method))
			{
				Assert.False(
					IsForbiddenMember(reference),
					$"{MethodBaseIdentity(method)} -> {ResolvedMemberIdentity(reference)}");
			}
		}

		string actual = Sha256Utf8(manifest);
		Assert.True(
			StringComparer.Ordinal.Equals(ExpectedImplementationManifestSha256, actual),
			actual);
	}

	private static LiquidSuppliedConfidentialDestination Destination(
		ElementsPublicNetworkManifest manifest,
		string scriptHex,
		string assetHex,
		long atomicUnits,
		IReadOnlyList<string> labels)
	{
		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(assetHex);
		LiquidAssetAmount amount = LiquidAssetAmount.Create(assetId, PeggedAsset(manifest), atomicUnits);
		return LiquidSuppliedConfidentialDestination.Create(
			manifest,
			ConfidentialAddress(manifest, scriptHex),
			assetId,
			amount,
			LiquidWalletLabelSet.Create(labels));
	}

	private static LiquidSuppliedConfidentialDestination CreateUncheckedDestination(
		string networkManifestId,
		LiquidAssetId peggedAssetId,
		LiquidAddress address,
		LiquidAssetId assetId,
		LiquidAssetAmount? amount,
		LiquidWalletLabelSet labels)
	{
		ConstructorInfo constructor = Assert.Single(
			typeof(LiquidSuppliedConfidentialDestination).GetConstructors(
				BindingFlags.NonPublic | BindingFlags.Instance));
		return Assert.IsType<LiquidSuppliedConfidentialDestination>(constructor.Invoke(
			[networkManifestId, peggedAssetId, address, assetId, amount, labels]));
	}

	private static LiquidAddress ConfidentialAddress(
		ElementsPublicNetworkManifest manifest,
		string scriptHex) =>
		LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString(scriptHex),
			LiquidBlindingPublicKey.Create(Convert.FromHexString(BlindingPublicKeyHex)));

	private static LiquidAssetId PeggedAsset(ElementsPublicNetworkManifest manifest) =>
		LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);

	private static long Amount(LiquidAssetBalanceMap map, string assetHex) =>
		map.GetAmountOrZero(LiquidAssetId.ParseRpcHex(assetHex)).AtomicUnits;

	private static ArgumentNullException AssertNullFailure(string expectedParameter, Action action)
	{
		ArgumentNullException failure = Assert.Throws<ArgumentNullException>(action);
		Assert.Equal(expectedParameter, failure.ParamName);
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
		return failure;
	}

	private static T AssertArgumentFailure<T>(string expectedParameter, Action action)
		where T : ArgumentException
	{
		T failure = Assert.Throws<T>(action);
		Assert.Equal(expectedParameter, failure.ParamName);
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
		return failure;
	}

	private static void AssertRedacted(string rendered, IEnumerable<string> canaries)
	{
		foreach (string canary in canaries.Where(value => value.Length >= 8))
		{
			Assert.DoesNotContain(canary, rendered, StringComparison.OrdinalIgnoreCase);
		}
	}

	private static string[] SensitiveCanaries(
		IReadOnlyList<LiquidSuppliedConfidentialDestination> destinations)
	{
		List<string> values = [];
		foreach (LiquidSuppliedConfidentialDestination? destination in destinations)
		{
			if (destination is null)
			{
				continue;
			}

			values.Add(destination.GetNetworkManifestId());
			values.Add(destination.GetPeggedAssetId().CanonicalRpcHex);
			values.Add(destination.GetAddress().GetCanonicalAddressText());
			values.Add(destination.GetAddress().GetUnconfidentialAddressText());
			values.Add(Convert.ToHexString(destination.GetAddress().GetScriptPubKey()));
			values.Add(Convert.ToHexString(destination.GetAddress().GetBlindingPublicKey() ?? []));
			values.Add(destination.GetAssetId().CanonicalRpcHex);
			LiquidAssetAmount? amount = destination.GetAmount();
			if (amount is not null)
			{
				values.Add(amount.AssetId.CanonicalRpcHex);
				values.Add(amount.PeggedAssetId.CanonicalRpcHex);
				values.Add(amount.AtomicUnits.ToString(CultureInfo.InvariantCulture));
			}

			values.AddRange(destination.GetLabels().GetLabels());
		}

		return values.Distinct(StringComparer.Ordinal).ToArray();
	}

	private static object[] SuppliedComponents(
		IReadOnlyList<LiquidSuppliedConfidentialDestination> destinations)
	{
		List<object> values = [destinations];
		foreach (LiquidSuppliedConfidentialDestination? destination in destinations)
		{
			if (destination is null)
			{
				continue;
			}

			values.Add(destination);
			values.Add(destination.GetNetworkManifestId());
			values.Add(destination.GetPeggedAssetId());
			values.Add(destination.GetAddress());
			values.Add(destination.GetAssetId());
			values.Add(destination.GetLabels());
			LiquidAssetAmount? amount = destination.GetAmount();
			if (amount is not null)
			{
				values.Add(amount);
				values.Add(amount.AssetId);
				values.Add(amount.PeggedAssetId);
			}
		}

		return values.ToArray();
	}

	private static void AssertOwnedFailurePrivacy(
		Exception failure,
		IReadOnlyList<object> suppliedComponents,
		IReadOnlyList<string> canaries)
	{
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
		AssertRedacted(failure.Message, canaries);
		AssertRedacted(failure.ToString(), canaries);
		if (failure is ArgumentOutOfRangeException rangeFailure)
		{
			Assert.Null(rangeFailure.ActualValue);
		}

		foreach (object? retained in GetDirectExceptionValues(failure))
		{
			Assert.DoesNotContain(suppliedComponents, supplied => ReferenceEquals(supplied, retained));
		}
	}

	private static IEnumerable<object?> GetDirectExceptionValues(Exception failure)
	{
		for (Type? type = failure.GetType(); type is not null; type = type.BaseType)
		{
			foreach (FieldInfo field in type.GetFields(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				yield return field.GetValue(failure);
			}
		}

		foreach (PropertyInfo property in failure.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
		{
			if (property.GetMethod is not null && property.GetIndexParameters().Length == 0)
			{
				yield return property.GetValue(failure);
			}
		}
	}

	private static string FindRepositoryRoot()
	{
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
			directory is not null;
			directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "WalletWasabi.slnx")))
			{
				return directory.FullName;
			}
		}

		throw new DirectoryNotFoundException("The repository root could not be located.");
	}

	private static void AssertSourceSha256(string root, string relativePath, string expected)
	{
		string actual = Convert.ToHexString(
			SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relativePath))))
			.ToLowerInvariant();
		Assert.Equal(expected, actual);
	}

	private static IEnumerable<MethodBase> OwnedMethods(Type type)
	{
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		return type.GetConstructors(Declared).Cast<MethodBase>().Concat(type.GetMethods(Declared));
	}

	private static string BuildImplementationManifest(Type type)
	{
		var rows = new List<string>
		{
			$"TYPE|{type.FullName}|{(int)type.Attributes}|{CustomAttributeManifest(type.CustomAttributes)}",
		};
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		foreach (FieldInfo field in type.GetFields(Declared).OrderBy(field => field.Name, StringComparer.Ordinal))
		{
			rows.Add(
				$"FIELD|{field.Name}|{TypeIdentity(field.FieldType)}|{(int)field.Attributes}|" +
				$"{ModifierManifest(field.GetRequiredCustomModifiers())}|" +
				$"{ModifierManifest(field.GetOptionalCustomModifiers())}|" +
				CustomAttributeManifest(field.CustomAttributes));
		}
		foreach (PropertyInfo property in type.GetProperties(Declared).OrderBy(property => property.Name, StringComparer.Ordinal))
		{
			rows.Add(
				$"PROPERTY|{property.Name}|{TypeIdentity(property.PropertyType)}|{(int)property.Attributes}|" +
				$"{ModifierManifest(property.GetRequiredCustomModifiers())}|" +
				$"{ModifierManifest(property.GetOptionalCustomModifiers())}|" +
				$"{CustomAttributeManifest(property.CustomAttributes)}|" +
				$"{property.GetMethod?.Name}|{property.SetMethod?.Name}");
		}

		foreach (MethodBase method in OwnedMethods(type).OrderBy(MethodBaseIdentity, StringComparer.Ordinal))
		{
			MethodBody? body = method.GetMethodBody();
			rows.Add(
				$"METHOD|{MethodBaseIdentity(method)}|{(int)method.Attributes}|" +
				$"{(int)method.GetMethodImplementationFlags()}|{(int)method.CallingConvention}|" +
				CustomAttributeManifest(method.CustomAttributes));
			if (method is MethodInfo methodInfo)
			{
				rows.Add(
					$"RETURN|{TypeIdentity(methodInfo.ReturnType)}|" +
					$"{ModifierManifest(methodInfo.ReturnParameter.GetRequiredCustomModifiers())}|" +
					$"{ModifierManifest(methodInfo.ReturnParameter.GetOptionalCustomModifiers())}|" +
					CustomAttributeManifest(methodInfo.ReturnParameter.CustomAttributes));
			}
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				rows.Add(
					$"PARAM|{parameter.Position}|{parameter.Name}|{TypeIdentity(parameter.ParameterType)}|" +
					$"{(int)parameter.Attributes}|{ModifierManifest(parameter.GetRequiredCustomModifiers())}|" +
					$"{ModifierManifest(parameter.GetOptionalCustomModifiers())}|" +
					CustomAttributeManifest(parameter.CustomAttributes));
			}
			if (body is null)
			{
				rows.Add("BODY|null");
				continue;
			}

			rows.Add(
				$"BODY|{body.InitLocals}|{body.MaxStackSize}|" +
				Convert.ToHexString(body.GetILAsByteArray() ?? []).ToLowerInvariant());
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				rows.Add($"LOCAL|{local.LocalIndex}|{TypeIdentity(local.LocalType)}|{local.IsPinned}");
			}
			foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
			{
				int filterOffset = clause.Flags == ExceptionHandlingClauseOptions.Filter
					? clause.FilterOffset
					: -1;
				Type? catchType = clause.Flags == ExceptionHandlingClauseOptions.Clause
					? clause.CatchType
					: null;
				rows.Add(
					$"EH|{(int)clause.Flags}|{clause.TryOffset}|{clause.TryLength}|" +
					$"{clause.HandlerOffset}|{clause.HandlerLength}|{filterOffset}|" +
					TypeIdentity(catchType));
			}
			foreach (MemberInfo reference in GetIlReferences(method))
			{
				rows.Add($"REF|{ResolvedMemberIdentity(reference)}");
			}
			foreach (string literal in GetIlStringLiterals(method))
			{
				rows.Add($"STRING|{StringLiteralIdentity(literal)}");
			}
			foreach (byte[] signature in GetIlSignatures(method))
			{
				rows.Add($"SIGNATURE|{Convert.ToHexString(signature).ToLowerInvariant()}");
			}
		}
		return string.Join('\n', rows) + "\n";
	}

	private static string Sha256Utf8(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private static string CustomAttributeManifest(IEnumerable<CustomAttributeData> attributes) =>
		string.Join(",", attributes
			.Select(attribute =>
				$"{TypeIdentity(attribute.AttributeType)}({string.Join(";", attribute.ConstructorArguments.Select(CustomAttributeValue))})" +
				$"[{string.Join(";", attribute.NamedArguments.Select(argument => $"{argument.MemberName}={CustomAttributeValue(argument.TypedValue)}"))}]")
			.OrderBy(value => value, StringComparer.Ordinal));

	private static string CustomAttributeValue(CustomAttributeTypedArgument argument)
	{
		if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> values)
		{
			return $"[{string.Join(",", values.Select(CustomAttributeValue))}]";
		}
		return $"{TypeIdentity(argument.ArgumentType)}:{argument.Value}";
	}

	private static string ModifierManifest(IEnumerable<Type> modifiers) =>
		string.Join(",", modifiers.Select(TypeIdentity));

	private static string TypeIdentity(Type? type) =>
		LiquidWalletStateTests.NormalizeProductAssemblyVersion(type?.AssemblyQualifiedName ?? "null");

	private static string MethodBaseIdentity(MethodBase method)
	{
		string parameters = string.Join(",", method.GetParameters()
			.Select(parameter => TypeIdentity(parameter.ParameterType)));
		string genericArguments = method.IsGenericMethod
			? $"<{string.Join(",", method.GetGenericArguments().Select(TypeIdentity))}>"
			: "";
		string returnType = method is MethodInfo info ? TypeIdentity(info.ReturnType) : "void";
		return $"{TypeIdentity(method.DeclaringType)}::{method.Name}{genericArguments}({parameters})->{returnType}";
	}

	private static string ResolvedMemberIdentity(MemberInfo member) => member switch
	{
		MethodBase method => MethodBaseIdentity(method),
		FieldInfo field => $"{TypeIdentity(field.DeclaringType)}::{field.Name}:{TypeIdentity(field.FieldType)}",
		Type type => TypeIdentity(type),
		_ => $"{TypeIdentity(member.DeclaringType)}::{member.Name}",
	};

	private static string StringLiteralIdentity(string value)
	{
		var identity = new StringBuilder(value.Length * 4);
		foreach (char codeUnit in value)
		{
			identity.Append(((int)codeUnit).ToString("X4", CultureInfo.InvariantCulture));
		}
		return $"{value.Length}:{identity}";
	}

	private static string MethodSignature(MethodInfo method)
	{
		string parameters = string.Join(",", method.GetParameters()
			.Select(parameter => parameter.ParameterType.ToString()));
		return $"{method.Name}({parameters})->{method.ReturnType}:" +
			$"{(method.IsStatic ? "static" : "instance")}";
	}

	private static bool IsForbiddenMember(MemberInfo member)
	{
		if (IsForbiddenIdentity(MemberIdentity(member)))
		{
			return true;
		}
		if (member is MethodInfo methodInfo && ContainsForbiddenType(methodInfo.ReturnType))
		{
			return true;
		}
		if (member is MethodBase methodBase &&
			methodBase.GetParameters().Any(parameter => ContainsForbiddenType(parameter.ParameterType)))
		{
			return true;
		}
		if (member is FieldInfo field && ContainsForbiddenType(field.FieldType))
		{
			return true;
		}
		if (member is Type type && ContainsForbiddenType(type))
		{
			return true;
		}
		return member is not Type && member.GetCustomAttributesData().Any(IsForbiddenAttribute);
	}

	private static bool ContainsForbiddenType(Type? type) =>
		ContainsForbiddenType(type, new HashSet<Type>());

	private static bool ContainsForbiddenType(Type? type, HashSet<Type> visited)
	{
		if (type is null || !visited.Add(type))
		{
			return false;
		}
		if (type.IsPointer || type.IsFunctionPointer || typeof(Delegate).IsAssignableFrom(type))
		{
			return true;
		}
		if (IsForbiddenIdentity(type.AssemblyQualifiedName ?? type.FullName ?? type.Name))
		{
			return true;
		}
		if (type.HasElementType && ContainsForbiddenType(type.GetElementType(), visited))
		{
			return true;
		}
		return type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsForbiddenType(argument, visited));
	}

	private static bool IsForbiddenIdentity(string identity) =>
		ForbiddenIdentityFragments.Any(fragment =>
			identity.Contains(fragment, StringComparison.OrdinalIgnoreCase));

	private static bool IsForbiddenAttribute(CustomAttributeData attribute) =>
		new[]
		{
			"Debugger", "Serializable", "OnSerializ", "OnDeserializ", "DataContract",
			"DataMember", "Json", "Xml", "Yaml", "MessagePack", "Proto",
		}
			.Any(fragment =>
				(attribute.AttributeType.FullName ?? attribute.AttributeType.Name)
					.Contains(fragment, StringComparison.OrdinalIgnoreCase));

	private static string MemberIdentity(MemberInfo member) =>
		$"{member.Module.Assembly.GetName().Name}|{member.DeclaringType?.FullName}|{member.Name}";

	private static readonly string[] ForbiddenIdentityFragments =
	[
		"WalletWasabi.Blockchain.Analysis.Clustering.LabelsArray",
		"WalletWasabi.Liquid.Native",
		"WalletWasabi.Liquid.Rpc",
		"WalletWasabi.Liquid.Transactions",
		"WalletWasabi.Liquid.Wallet.LiquidWalletState",
		"WalletWasabi.Liquid.Wallet.LiquidOwnedOutput",
		"WalletWasabi.Liquid.Wallet.LiquidWalletCoinControl",
		"Pset",
		"Signing",
		"Persistence",
		"System.IO.",
		"System.Net.",
		"System.Diagnostics.",
		"System.Console",
		"System.Runtime.InteropServices.",
		"System.Reflection.",
		"System.Dynamic.",
		"System.Linq.Expressions.",
		"System.Delegate",
		"System.MulticastDelegate",
		"System.Activator",
		"System.Type",
		"System.Text.Json",
		"WalletWasabi.Logging",
		"Microsoft.Extensions.Logging",
		"OpenTelemetry",
		"Newtonsoft.Json",
		"Serilog",
		"NLog",
	];

	private static IEnumerable<MemberInfo> GetIlReferences(MethodBase method)
	{
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		Type[]? typeArguments = method.DeclaringType?.GetGenericArguments();
		Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			int operandPosition = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandPosition);
			if (opCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
				OperandType.InlineTok or OperandType.InlineType)
			{
				int token = BitConverter.ToInt32(il, operandPosition);
				MemberInfo? member = method.Module.ResolveMember(token, typeArguments, methodArguments);
				if (member is not null)
				{
					yield return member;
				}
			}
			position += operandSize;
		}
	}

	private static IReadOnlyList<OpCode> GetIlOpCodes(MethodBase method)
	{
		var opCodes = new List<OpCode>();
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			opCodes.Add(opCode);
			position += GetOperandSize(opCode.OperandType, il, position);
		}
		return opCodes;
	}

	private static IReadOnlyList<string> GetIlStringLiterals(MethodBase method)
	{
		var literals = new List<string>();
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			int operandPosition = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandPosition);
			if (opCode.OperandType == OperandType.InlineString)
			{
				literals.Add(method.Module.ResolveString(BitConverter.ToInt32(il, operandPosition)));
			}
			position += operandSize;
		}
		return literals;
	}

	private static IReadOnlyList<byte[]> GetIlSignatures(MethodBase method)
	{
		var signatures = new List<byte[]>();
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			int operandPosition = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandPosition);
			if (opCode.OperandType == OperandType.InlineSig)
			{
				signatures.Add(method.Module.ResolveSignature(BitConverter.ToInt32(il, operandPosition)));
			}
			position += operandSize;
		}
		return signatures;
	}

	private static OpCode ReadOpCode(byte[] il, ref int position)
	{
		byte first = il[position++];
		short value = first == 0xfe
			? (short)(0xfe00 | il[position++])
			: first;
		return OpCodeByValue[value];
	}

	private static int GetOperandSize(OperandType operandType, byte[] il, int position) =>
		operandType switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
				OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
				OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => sizeof(int) +
				(BitConverter.ToInt32(il, position) * sizeof(int)),
			_ => throw new InvalidOperationException($"Unsupported IL operand type {operandType}."),
		};

	private static readonly IReadOnlyDictionary<short, OpCode> OpCodeByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(OpCode))
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(opCode => opCode.Value);

	private sealed class IndexedList : IReadOnlyList<LiquidSuppliedConfidentialDestination>
	{
		private readonly LiquidSuppliedConfidentialDestination[] _values;

		public IndexedList(LiquidSuppliedConfidentialDestination[] values)
		{
			_values = values;
		}

		public int CountReads { get; private set; }
		public int IndexReads { get; private set; }
		public int EnumerationRequests { get; private set; }

		public int Count
		{
			get
			{
				CountReads++;
				return _values.Length;
			}
		}

		public LiquidSuppliedConfidentialDestination this[int index]
		{
			get
			{
				IndexReads++;
				return _values[index];
			}
		}

		public IEnumerator<LiquidSuppliedConfidentialDestination> GetEnumerator()
		{
			EnumerationRequests++;
			return ((IEnumerable<LiquidSuppliedConfidentialDestination>)_values).GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
