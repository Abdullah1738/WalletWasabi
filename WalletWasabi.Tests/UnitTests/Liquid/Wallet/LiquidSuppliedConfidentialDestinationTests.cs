using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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

public class LiquidSuppliedConfidentialDestinationTests
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
	private const string PrivateLabel = "private-customer-canary-739251";

#if DEBUG
	private const string ExpectedImplementationManifestSha256 =
		"4ef1e916023e22aff666b01ac7de26c00cd122795ce4b4976e3e075272c194ae";
#else
	private const string ExpectedImplementationManifestSha256 =
		"82baef511aeab70ad4068bdb3a57b7d4b680b6c5ab01fd3039ac102898cfb57b";
#endif

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CreatesExactConfidentialDestinationAndRequiresNamedDisclosure(bool testnet)
	{
		ElementsPublicNetworkManifest manifest = testnet
			? ElementsPublicNetworkManifest.LiquidTestnet
			: ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidAddress address = ConfidentialAddress(manifest, ScriptHex);
		LiquidAssetId peggedAsset = PeggedAsset(manifest);
		LiquidAssetId asset = LiquidAssetId.ParseRpcHex(OpaqueAssetHex);
		LiquidAssetAmount amount = LiquidAssetAmount.Create(asset, peggedAsset, 987_654_321);
		LiquidWalletLabelSet labels = LiquidWalletLabelSet.Create([PrivateLabel, "cold"]);

		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(manifest, address, asset, amount, labels);

		Assert.Equal(manifest.ManifestId, destination.GetNetworkManifestId());
		Assert.Equal(peggedAsset, destination.GetPeggedAssetId());
		Assert.Same(address, destination.GetAddress());
		Assert.Same(asset, destination.GetAssetId());
		Assert.Same(amount, destination.GetAmount());
		Assert.Same(labels, destination.GetLabels());
		Assert.Equal(nameof(LiquidSuppliedConfidentialDestination), destination.ToString());
	}

	[Fact]
	public void AcceptsPolicyAssetNullAmountAndEmptyLabels()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidAddress address = ConfidentialAddress(manifest, ScriptHex);
		LiquidAssetId policyAsset = PeggedAsset(manifest);

		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(
				manifest,
				address,
				policyAsset,
				null,
				LiquidWalletLabelSet.Empty);

		Assert.Same(policyAsset, destination.GetAssetId());
		Assert.Equal(policyAsset, destination.GetPeggedAssetId());
		Assert.Null(destination.GetAmount());
		Assert.Same(LiquidWalletLabelSet.Empty, destination.GetLabels());
	}

	[Fact]
	public void RejectsNullInputsInExactOrder()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidAddress address = ConfidentialAddress(manifest, ScriptHex);
		LiquidAssetId asset = LiquidAssetId.ParseRpcHex(OpaqueAssetHex);

		AssertNullFailure(
			"manifest",
			() => LiquidSuppliedConfidentialDestination.Create(null!, null!, null!, null, null!));
		AssertNullFailure(
			"address",
			() => LiquidSuppliedConfidentialDestination.Create(manifest, null!, null!, null, null!));
		AssertNullFailure(
			"assetId",
			() => LiquidSuppliedConfidentialDestination.Create(manifest, address, null!, null, null!));
		AssertNullFailure(
			"labels",
			() => LiquidSuppliedConfidentialDestination.Create(manifest, address, asset, null, null!));
	}

	[Fact]
	public void RejectsSemanticFailuresInExactOrder()
	{
		ElementsPublicNetworkManifest mainnet = ElementsPublicNetworkManifest.LiquidMainnet;
		ElementsPublicNetworkManifest testnet = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAssetId selectedAsset = LiquidAssetId.ParseRpcHex(OpaqueAssetHex);
		LiquidAssetId otherAsset = LiquidAssetId.ParseRpcHex(OtherAssetHex);
		LiquidAssetId mainnetPeggedAsset = PeggedAsset(mainnet);
		LiquidAssetAmount zeroMismatch = LiquidAssetAmount.Zero(otherAsset, LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex));

		AssertArgumentFailure<ArgumentException>(
			"address",
			() => LiquidSuppliedConfidentialDestination.Create(
				mainnet,
				ConfidentialAddress(testnet, ScriptHex),
				selectedAsset,
				zeroMismatch,
				LiquidWalletLabelSet.Empty));
		AssertArgumentFailure<ArgumentException>(
			"address",
			() => LiquidSuppliedConfidentialDestination.Create(
				mainnet,
				UnconfidentialAddress(mainnet, ScriptHex),
				selectedAsset,
				zeroMismatch,
				LiquidWalletLabelSet.Empty));
		ArgumentOutOfRangeException zeroFailure = AssertArgumentFailure<ArgumentOutOfRangeException>(
			"amount",
			() => LiquidSuppliedConfidentialDestination.Create(
				mainnet,
				ConfidentialAddress(mainnet, ScriptHex),
				selectedAsset,
				zeroMismatch,
				LiquidWalletLabelSet.Empty));
		Assert.Null(zeroFailure.ActualValue);

		LiquidAssetAmount assetMismatch = LiquidAssetAmount.Create(otherAsset, LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex), 1);
		AssertArgumentFailure<ArgumentException>(
			"amount",
			() => LiquidSuppliedConfidentialDestination.Create(
				mainnet,
				ConfidentialAddress(mainnet, ScriptHex),
				selectedAsset,
				assetMismatch,
				LiquidWalletLabelSet.Empty));

		LiquidAssetAmount peggedMismatch = LiquidAssetAmount.Create(
			selectedAsset,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			1);
		AssertArgumentFailure<ArgumentException>(
			"amount",
			() => LiquidSuppliedConfidentialDestination.Create(
				mainnet,
				ConfidentialAddress(mainnet, ScriptHex),
				selectedAsset,
				peggedMismatch,
				LiquidWalletLabelSet.Empty));

		LiquidAssetAmount accepted = LiquidAssetAmount.Create(selectedAsset, mainnetPeggedAsset, 1);
		Assert.NotNull(LiquidSuppliedConfidentialDestination.Create(
			mainnet,
			ConfidentialAddress(mainnet, ScriptHex),
			selectedAsset,
			accepted,
			LiquidWalletLabelSet.Empty));
	}

	[Fact]
	public void EqualityAndHashingIncludeEveryRetainedField()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidAddress address = ConfidentialAddress(manifest, ScriptHex);
		LiquidAssetId peggedAsset = PeggedAsset(manifest);
		LiquidAssetId asset = LiquidAssetId.ParseRpcHex(OpaqueAssetHex);
		LiquidAssetAmount amount = LiquidAssetAmount.Create(asset, peggedAsset, 42);
		LiquidWalletLabelSet labels = LiquidWalletLabelSet.Create(["one", "two"]);
		LiquidSuppliedConfidentialDestination value = CreateUnchecked(
			manifest.ManifestId,
			peggedAsset,
			address,
			asset,
			amount,
			labels);
		LiquidSuppliedConfidentialDestination equal = CreateUnchecked(
			new string(manifest.ManifestId.ToCharArray()),
			LiquidAssetId.ParseRpcHex(peggedAsset.CanonicalRpcHex),
			LiquidAddress.Parse(manifest, address.GetCanonicalAddressText()),
			LiquidAssetId.ParseRpcHex(OpaqueAssetHex),
			LiquidAssetAmount.Create(LiquidAssetId.ParseRpcHex(OpaqueAssetHex), PeggedAsset(manifest), 42),
			LiquidWalletLabelSet.Create(["two", "one"]));

		Assert.True(value.Equals(value));
		Assert.True(value.Equals(equal));
		Assert.True(((object)value).Equals(equal));
		Assert.Equal(value.GetHashCode(), equal.GetHashCode());
		Assert.False(value.Equals(null));
		Assert.False(value.Equals(new object()));

		Assert.NotEqual(value, CreateUnchecked(
			"different-manifest",
			peggedAsset,
			address,
			asset,
			amount,
			labels));
		Assert.NotEqual(value, CreateUnchecked(
			manifest.ManifestId,
			LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex),
			address,
			asset,
			amount,
			labels));
		Assert.NotEqual(value, CreateUnchecked(
			manifest.ManifestId,
			peggedAsset,
			ConfidentialAddress(manifest, OtherScriptHex),
			asset,
			amount,
			labels));
		Assert.NotEqual(value, CreateUnchecked(
			manifest.ManifestId,
			peggedAsset,
			address,
			LiquidAssetId.ParseRpcHex(OtherAssetHex),
			amount,
			labels));
		Assert.NotEqual(value, CreateUnchecked(
			manifest.ManifestId,
			peggedAsset,
			address,
			asset,
			null,
			labels));
		Assert.NotEqual(value, CreateUnchecked(
			manifest.ManifestId,
			peggedAsset,
			address,
			asset,
			LiquidAssetAmount.Create(asset, peggedAsset, 43),
			labels));
		Assert.NotEqual(value, CreateUnchecked(
			manifest.ManifestId,
			peggedAsset,
			address,
			asset,
			amount,
			LiquidWalletLabelSet.Create(["other"])));
	}

	[Fact]
	public void RetainedComponentsRemainImmutableAcrossSourceAndReturnedSnapshotMutation()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		byte[] script = Convert.FromHexString(ScriptHex);
		byte[] expectedScript = [.. script];
		byte[] publicKey = Convert.FromHexString(BlindingPublicKeyHex);
		byte[] expectedPublicKey = [.. publicKey];
		string[] labelSource = [PrivateLabel, "secondary"];
		LiquidBlindingPublicKey blindingKey = LiquidBlindingPublicKey.Create(publicKey);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(manifest, script, blindingKey);
		LiquidWalletLabelSet labels = LiquidWalletLabelSet.Create(labelSource);
		LiquidSuppliedConfidentialDestination destination = LiquidSuppliedConfidentialDestination.Create(
			manifest,
			address,
			LiquidAssetId.ParseRpcHex(OpaqueAssetHex),
			null,
			labels);

		script.AsSpan().Fill(0xff);
		publicKey.AsSpan().Fill(0xff);
		labelSource[0] = "mutated";
		byte[] returnedScript = destination.GetAddress().GetScriptPubKey();
		byte[] returnedPublicKey = Assert.IsType<byte[]>(destination.GetAddress().GetBlindingPublicKey());
		returnedScript.AsSpan().Fill(0xee);
		returnedPublicKey.AsSpan().Fill(0xee);
		IReadOnlyList<string> returnedLabels = destination.GetLabels().GetLabels();
		Assert.Throws<NotSupportedException>(() => ((IList)returnedLabels)[0] = "mutated");

		Assert.Equal(expectedScript, destination.GetAddress().GetScriptPubKey());
		Assert.Equal(expectedPublicKey, destination.GetAddress().GetBlindingPublicKey());
		Assert.Equal([PrivateLabel, "secondary"], destination.GetLabels().GetLabels());
	}

	[Fact]
	public void RenderingAndOwnedFailuresDoNotDiscloseOrRetainComponents()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		ElementsPublicNetworkManifest otherManifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAddress confidential = ConfidentialAddress(manifest, ScriptHex);
		LiquidAddress otherNetworkConfidential = ConfidentialAddress(otherManifest, ScriptHex);
		LiquidAddress unconfidential = UnconfidentialAddress(manifest, ScriptHex);
		LiquidAssetId asset = LiquidAssetId.ParseRpcHex(OpaqueAssetHex);
		LiquidAssetId otherAsset = LiquidAssetId.ParseRpcHex(OtherAssetHex);
		LiquidAssetId peggedAsset = PeggedAsset(manifest);
		LiquidAssetId otherPeggedAsset = LiquidAssetId.ParseRpcHex(OtherPeggedAssetHex);
		LiquidAssetAmount amount = LiquidAssetAmount.Zero(asset, peggedAsset);
		LiquidAssetAmount assetMismatchAmount = LiquidAssetAmount.Create(
			otherAsset,
			peggedAsset,
			87_654_321);
		LiquidAssetAmount peggedMismatchAmount = LiquidAssetAmount.Create(
			asset,
			otherPeggedAsset,
			98_765_432);
		LiquidWalletLabelSet labels = LiquidWalletLabelSet.Create([PrivateLabel]);
		object[] commonSupplied = [manifest, confidential, asset, peggedAsset, labels];
		string[] zeroCanaries = SensitiveCanaries(manifest, confidential, amount, labels);

		LiquidSuppliedConfidentialDestination destination = LiquidSuppliedConfidentialDestination.Create(
			manifest,
			confidential,
			asset,
			null,
			labels);
		AssertRedacted(destination.ToString(), zeroCanaries);

		ArgumentException crossManifestFailure = AssertArgumentFailure<ArgumentException>(
			"address",
			() => LiquidSuppliedConfidentialDestination.Create(
				manifest,
				otherNetworkConfidential,
				asset,
				assetMismatchAmount,
				labels));
		AssertOwnedFailurePrivacy(
			crossManifestFailure,
			[.. commonSupplied, otherManifest, otherNetworkConfidential, otherAsset, assetMismatchAmount],
			[
				.. SensitiveCanaries(otherManifest, otherNetworkConfidential, assetMismatchAmount, labels),
				manifest.ManifestId,
				asset.CanonicalRpcHex
			]);

		ArgumentException addressFailure = AssertArgumentFailure<ArgumentException>(
			"address",
			() => LiquidSuppliedConfidentialDestination.Create(
				manifest,
				unconfidential,
				asset,
				assetMismatchAmount,
				labels));
		AssertOwnedFailurePrivacy(
			addressFailure,
			[.. commonSupplied, unconfidential, otherAsset, assetMismatchAmount],
			[
				.. SensitiveCanaries(manifest, unconfidential, assetMismatchAmount, labels),
				asset.CanonicalRpcHex
			]);

		ArgumentOutOfRangeException amountFailure = AssertArgumentFailure<ArgumentOutOfRangeException>(
			"amount",
			() => LiquidSuppliedConfidentialDestination.Create(
				manifest,
				confidential,
				asset,
				amount,
				labels));
		Assert.Null(amountFailure.ActualValue);
		AssertOwnedFailurePrivacy(
			amountFailure,
			[.. commonSupplied, amount],
			zeroCanaries);

		ArgumentException assetMismatchFailure = AssertArgumentFailure<ArgumentException>(
			"amount",
			() => LiquidSuppliedConfidentialDestination.Create(
				manifest,
				confidential,
				asset,
				assetMismatchAmount,
				labels));
		AssertOwnedFailurePrivacy(
			assetMismatchFailure,
			[.. commonSupplied, otherAsset, assetMismatchAmount],
			[
				.. SensitiveCanaries(manifest, confidential, assetMismatchAmount, labels),
				asset.CanonicalRpcHex
			]);

		ArgumentException peggedMismatchFailure = AssertArgumentFailure<ArgumentException>(
			"amount",
			() => LiquidSuppliedConfidentialDestination.Create(
				manifest,
				confidential,
				asset,
				peggedMismatchAmount,
				labels));
		AssertOwnedFailurePrivacy(
			peggedMismatchFailure,
			[.. commonSupplied, otherPeggedAsset, peggedMismatchAmount],
			SensitiveCanaries(manifest, confidential, peggedMismatchAmount, labels));
	}

	[Fact]
	public void ExactSurfaceIsFrozenAndHasNoImplicitDisclosureMember()
	{
		Type type = typeof(LiquidSuppliedConfidentialDestination);
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		Assert.True(type.IsClass);
		Assert.True(type.IsSealed);
		Assert.False(type.IsAbstract);
		Assert.False(type.IsPublic);
		Assert.False(type.IsNested);
		Assert.Equal(typeof(object), type.BaseType);
		Assert.Equal([typeof(IEquatable<LiquidSuppliedConfidentialDestination>)], type.GetInterfaces());
		Assert.Empty(type.GetNestedTypes(Declared));
		Assert.Empty(type.GetEvents(Declared));
		Assert.Empty(type.GetProperties(Declared));

		ConstructorInfo constructor = Assert.Single(type.GetConstructors(Declared));
		Assert.True(constructor.IsPrivate);
		Assert.Equal(
			[
				typeof(string),
				typeof(LiquidAssetId),
				typeof(LiquidAddress),
				typeof(LiquidAssetId),
				typeof(LiquidAssetAmount),
				typeof(LiquidWalletLabelSet),
			],
			constructor.GetParameters().Select(parameter => parameter.ParameterType));

		Assert.Equal(
			[
				"_address:WalletWasabi.Liquid.Addresses.LiquidAddress:readonly",
				"_amount:WalletWasabi.Liquid.Amounts.LiquidAssetAmount:readonly",
				"_assetId:WalletWasabi.Liquid.Assets.LiquidAssetId:readonly",
				"_labels:WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet:readonly",
				"_networkManifestId:System.String:readonly",
				"_peggedAssetId:WalletWasabi.Liquid.Assets.LiquidAssetId:readonly",
			],
			type.GetFields(Declared)
				.Select(field => $"{field.Name}:{field.FieldType.FullName}:{(field.IsInitOnly ? "readonly" : "mutable")}")
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.All(type.GetFields(Declared), field =>
		{
			Assert.True(field.IsPrivate);
			Assert.False(field.IsStatic);
			Assert.True(field.IsInitOnly);
		});

		Assert.Equal(
			[
				"Create(WalletWasabi.Liquid.Network.ElementsPublicNetworkManifest,WalletWasabi.Liquid.Addresses.LiquidAddress,WalletWasabi.Liquid.Assets.LiquidAssetId,WalletWasabi.Liquid.Amounts.LiquidAssetAmount,WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet)->WalletWasabi.Liquid.Wallet.LiquidSuppliedConfidentialDestination:static",
				"Equals(System.Object)->System.Boolean:instance",
				"Equals(WalletWasabi.Liquid.Wallet.LiquidSuppliedConfidentialDestination)->System.Boolean:instance",
				"GetAddress()->WalletWasabi.Liquid.Addresses.LiquidAddress:instance",
				"GetAmount()->WalletWasabi.Liquid.Amounts.LiquidAssetAmount:instance",
				"GetAssetId()->WalletWasabi.Liquid.Assets.LiquidAssetId:instance",
				"GetHashCode()->System.Int32:instance",
				"GetLabels()->WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet:instance",
				"GetNetworkManifestId()->System.String:instance",
				"GetPeggedAssetId()->WalletWasabi.Liquid.Assets.LiquidAssetId:instance",
				"ToString()->System.String:instance",
			],
			type.GetMethods(Declared)
				.Where(method => method.IsPublic)
				.Select(MethodSignature)
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.DoesNotContain(type.GetMethods(Declared), method => method.IsFamily || method.IsAssembly);
		Assert.DoesNotContain(type.CustomAttributes, IsForbiddenAttribute);
		Assert.DoesNotContain(type.GetFields(Declared).SelectMany(field => field.CustomAttributes), IsForbiddenAttribute);
		Assert.DoesNotContain(type.GetMethods(Declared).SelectMany(method => method.CustomAttributes), IsForbiddenAttribute);
	}

	[Fact]
	public void CompleteOwnedImplementationGraphIsFrozenAndContainsNoForbiddenSurface()
	{
		Type type = typeof(LiquidSuppliedConfidentialDestination);
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
			Assert.DoesNotContain(GetIlReferences(method), IsForbiddenMember);
		}

		string actual = Sha256Utf8(manifest);
		Assert.True(
			StringComparer.Ordinal.Equals(ExpectedImplementationManifestSha256, actual),
			actual);
	}

	private static LiquidSuppliedConfidentialDestination CreateUnchecked(
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

	private static LiquidAddress UnconfidentialAddress(
		ElementsPublicNetworkManifest manifest,
		string scriptHex) =>
		LiquidAddress.FromScriptPubKey(manifest, Convert.FromHexString(scriptHex));

	private static LiquidAssetId PeggedAsset(ElementsPublicNetworkManifest manifest) =>
		LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);

	private static void AssertNullFailure(string expectedParameter, Action action)
	{
		ArgumentNullException failure = Assert.Throws<ArgumentNullException>(action);
		Assert.Equal(expectedParameter, failure.ParamName);
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
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

	private static string[] SensitiveCanaries(
		ElementsPublicNetworkManifest manifest,
		LiquidAddress address,
		LiquidAssetAmount amount,
		LiquidWalletLabelSet labels) =>
		[
			manifest.ManifestId,
			manifest.PeggedAssetId,
			address.GetCanonicalAddressText(),
			address.GetUnconfidentialAddressText(),
			Convert.ToHexString(address.GetScriptPubKey()),
			Convert.ToHexString(address.GetBlindingPublicKey() ?? []),
			amount.AssetId.CanonicalRpcHex,
			amount.PeggedAssetId.CanonicalRpcHex,
			amount.AtomicUnits.ToString(CultureInfo.InvariantCulture),
			.. labels.GetLabels(),
		];

	private static void AssertRedacted(string rendered, IEnumerable<string> canaries)
	{
		foreach (string canary in canaries.Where(value => value.Length >= 8))
		{
			Assert.DoesNotContain(canary, rendered, StringComparison.OrdinalIgnoreCase);
		}
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

	private static string TypeIdentity(Type? type) => type?.AssemblyQualifiedName ?? "null";

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
		return member.GetCustomAttributesData().Any(IsForbiddenAttribute);
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
}
