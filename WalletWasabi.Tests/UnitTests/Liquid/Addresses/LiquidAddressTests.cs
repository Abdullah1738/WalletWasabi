using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using Xunit;
using Xunit.Sdk;

namespace WalletWasabi.Tests.UnitTests.Liquid.Addresses;

public class LiquidAddressTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

	[Theory]
	[MemberData(nameof(PositiveRowIds))]
	public void LiteralPositiveCorpusParsesAndRoundTrips(string rowId)
	{
		var (
			manifest,
			expectedKind,
			expectedConfidential,
			expectedWitnessVersion,
			scriptHex,
			blindingKeyHex,
			canonicalAddress,
			unconfidentialAddress,
			uppercaseInput) = PositiveCase(rowId);
		LiquidAddress parsed = LiquidAddress.Parse(manifest, canonicalAddress);

		string expectedManifestId = ExpectedManifestId(manifest);
		Assert.Equal(expectedManifestId, manifest.ManifestId);
		Assert.Equal(expectedManifestId, parsed.NetworkManifestId);
		Assert.Equal(expectedKind, parsed.Kind);
		Assert.Equal(expectedConfidential, parsed.IsConfidential);
		Assert.Equal(expectedWitnessVersion, parsed.WitnessVersion);
		AssertProtectedText(canonicalAddress, parsed.GetCanonicalAddressText());
		AssertProtectedText(unconfidentialAddress, parsed.GetUnconfidentialAddressText());
		AssertProtectedText(scriptHex, Convert.ToHexStringLower(parsed.GetScriptPubKey()));
		AssertProtectedText(
			blindingKeyHex,
			parsed.GetBlindingPublicKey() is { } key
				? Convert.ToHexStringLower(key)
				: "");
		Assert.Equal(nameof(LiquidAddress), parsed.ToString());

		LiquidAddress reparsed = LiquidAddress.Parse(manifest, canonicalAddress);
		Assert.Equal(parsed, reparsed);
		Assert.Equal(parsed.GetHashCode(), reparsed.GetHashCode());
		LiquidBlindingPublicKey? blindingKey = blindingKeyHex.Length == 0
			? null
			: LiquidBlindingPublicKey.Create(Convert.FromHexString(blindingKeyHex));
		LiquidAddress fromScript = LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString(scriptHex),
			blindingKey);
		Assert.Equal(parsed, fromScript);
		AssertProtectedText(canonicalAddress, fromScript.GetCanonicalAddressText());

		if (uppercaseInput.Length > 0)
		{
			LiquidAddress uppercase = LiquidAddress.Parse(manifest, uppercaseInput);
			Assert.Equal(parsed, uppercase);
			AssertProtectedText(canonicalAddress, uppercase.GetCanonicalAddressText());
		}

		AssertFailure(
			OtherManifest(manifest),
			canonicalAddress,
			LiquidAddressParseFailure.NetworkMismatch);
	}

	[Fact]
	public void FrozenCorpusCardinalitiesAreExact()
	{
		Assert.Equal(40, PositiveRowIds().Count());
		Assert.Equal(6, InvalidPointRowIds().Count());
		Assert.Equal(2, ExactNinetyOneRowIds().Count());
		Assert.Equal(64, ShortProgramDefectRowIds().Count());
		Assert.Equal(36, SupplementalInvalidAddressRowIds().Count());
		Assert.Equal(52, MalformedScriptRowIds().Count());
	}

	[Theory]
	[MemberData(nameof(InvalidPointRowIds))]
	public void InvalidCompressedPointsAreStrictlyRejected(string rowId)
	{
		(ElementsPublicNetworkManifest manifest, string malformedAddress) = InvalidPointCase(rowId);
		AssertFailure(manifest, malformedAddress, LiquidAddressParseFailure.InvalidEncoding);
		AssertFailure(OtherManifest(manifest), malformedAddress, LiquidAddressParseFailure.InvalidEncoding);
	}

	[Theory]
	[MemberData(nameof(ExactNinetyOneRowIds))]
	public void ExactNinetyOneCharacterBech32BoundaryIsRejected(string rowId)
	{
		string malformedAddress = ExactNinetyOneCase(rowId);
		Assert.Equal(91, malformedAddress.Length);
		AssertFailure(
			ElementsPublicNetworkManifest.LiquidTestnet,
			malformedAddress,
			LiquidAddressParseFailure.InvalidEncoding);
	}

	[Fact]
	public void Bech32LengthEnvelopeRunsBeforeBitConversion()
	{
		MethodInfo parser = RequiredCodecMethod("TryParseWitness");
		MethodInfo envelope = RequiredCodecMethod("IsWitnessTextEnvelopeValid");
		MethodInfo conversion = RequiredCodecMethod("TryConvertBits");
		IReadOnlyList<(int Offset, MethodBase Method)> calls = GetCalledMethods(parser);
		int envelopeOffset = Assert.Single(calls, call => call.Method == envelope).Offset;
		int conversionOffset = Assert.Single(calls, call => call.Method == conversion).Offset;

		Assert.True(envelopeOffset < conversionOffset);
		Assert.True(Assert.IsType<bool>(envelope.Invoke(null, [new string('q', 90), false])));
		Assert.False(Assert.IsType<bool>(envelope.Invoke(null, [new string('q', 91), false])));
		Assert.True(Assert.IsType<bool>(envelope.Invoke(null, [new string('q', 91), true])));
	}

	[Theory]
	[MemberData(nameof(ShortProgramDefectRowIds))]
	public void ValidKeyBlech32mShortProgramReferenceDefectIsRejected(int rowIndex)
	{
		string malformedAddress = ShortProgramAddresses[rowIndex];
		ElementsPublicNetworkManifest manifest = malformedAddress.StartsWith("tlq", StringComparison.Ordinal)
			? ElementsPublicNetworkManifest.LiquidTestnet
			: ElementsPublicNetworkManifest.LiquidMainnet;
		AssertFailure(manifest, malformedAddress, LiquidAddressParseFailure.InvalidEncoding);
		AssertFailure(OtherManifest(manifest), malformedAddress, LiquidAddressParseFailure.InvalidEncoding);
	}

	[Theory]
	[MemberData(nameof(SupplementalInvalidAddressRowIds))]
	public void SupplementalLiteralNegativeCorpusIsRejected(string rowId)
	{
		string malformedAddress = SupplementalInvalidAddressCase(rowId);
		AssertFailure(
			ElementsPublicNetworkManifest.LiquidMainnet,
			malformedAddress,
			LiquidAddressParseFailure.InvalidEncoding);
		AssertFailure(
			ElementsPublicNetworkManifest.LiquidTestnet,
			malformedAddress,
			LiquidAddressParseFailure.InvalidEncoding);
	}

	[Fact]
	public void CommonMalformedInputsAreRejectedWithoutNormalizationOrFallback()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		string[] malformed =
		[
			"",
			"1",
			"0",
			" ",
			"\0",
			"é",
			new('q', 257),
			"ex",
			"1qqqqqq",
			"ex1",
			"e1x1pfeesuuklaq",
			"ex1pfeesuuklaq1",
			"ex1pfees!uklaq",
			"ex1pfees",
			"ex1pFEEsuuklaq",
			"ex1pfeesuuklar",
			"ex1pfeesuuklaq ",
			" ert1pfeesuuklaq",
			"unknown1qqqqqq",
			"lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesnnn7jykyxh9aknb",
		];

		foreach (string value in malformed)
		{
			AssertFailure(manifest, value, LiquidAddressParseFailure.InvalidEncoding);
			AssertFailure(
				ElementsPublicNetworkManifest.LiquidTestnet,
				value,
				LiquidAddressParseFailure.InvalidEncoding);
		}
	}

	[Theory]
	[MemberData(nameof(MalformedScriptRowIds))]
	public void ScriptRecognitionRejectsNoncanonicalAndMalformedScripts(string rowId)
	{
		string scriptHex = MalformedScriptCase(rowId);
		LiquidBlindingPublicKey key = LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex));
		ArgumentException exception = CaptureExpectedFailure<ArgumentException>(
			() => LiquidAddress.FromScriptPubKey(
				ElementsPublicNetworkManifest.LiquidMainnet,
				Convert.FromHexString(scriptHex),
				key));
		AssertOpaqueDiagnostic(scriptHex, exception.ToString());
		AssertOpaqueDiagnostic(PublicKeyHex, exception.ToString());
		Assert.Equal("scriptPubKey", exception.ParamName);
		Assert.True(
			exception.Message.StartsWith(
				"The script cannot be represented by the reviewed Liquid address domain.",
				StringComparison.Ordinal),
			"The malformed-script failure message changed.");
	}

	[Fact]
	public void ReturnedBytesAndSourceTextAreNotRetained()
	{
		const string uppercase = "LQ1PQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESNNN7JYKYXH9AKNA";
		LiquidAddress parsed = LiquidAddress.Parse(
			ElementsPublicNetworkManifest.LiquidMainnet,
			uppercase);
		LiquidAddress same = LiquidAddress.Parse(
			ElementsPublicNetworkManifest.LiquidMainnet,
			parsed.GetCanonicalAddressText());
		int hash = parsed.GetHashCode();
		byte[] script = parsed.GetScriptPubKey();
		byte[] key = Assert.IsType<byte[]>(parsed.GetBlindingPublicKey());

		script.AsSpan().Fill(0xff);
		key.AsSpan().Fill(0xff);

		Assert.Equal(same, parsed);
		Assert.Equal(hash, parsed.GetHashCode());
		AssertProtectedText("51024e73", Convert.ToHexStringLower(parsed.GetScriptPubKey()));
		AssertProtectedText(PublicKeyHex, Convert.ToHexStringLower(parsed.GetBlindingPublicKey()!));
		Assert.DoesNotContain(
			typeof(LiquidAddress).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
			field => field.FieldType == typeof(string) &&
				StringComparer.Ordinal.Equals((string?)field.GetValue(parsed), uppercase));
	}

	[Fact]
	public void EqualityIncludesConfidentialityAndNetworkIdentity()
	{
		const string unconfidentialText = "ex1pfeesuuklaq";
		const string confidentialText = "lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesnnn7jykyxh9akna";
		LiquidAddress unconfidential = LiquidAddress.Parse(
			ElementsPublicNetworkManifest.LiquidMainnet,
			unconfidentialText);
		LiquidAddress confidential = LiquidAddress.Parse(
			ElementsPublicNetworkManifest.LiquidMainnet,
			confidentialText);
		AssertProtectedBytes(unconfidential.GetScriptPubKey(), confidential.GetScriptPubKey());
		Assert.NotEqual(unconfidential, confidential);

		byte[] script = Convert.FromHexString("51024e73");
		LiquidAddress mainnet = LiquidAddress.FromScriptPubKey(
			ElementsPublicNetworkManifest.LiquidMainnet,
			script);
		LiquidAddress testnet = LiquidAddress.FromScriptPubKey(
			ElementsPublicNetworkManifest.LiquidTestnet,
			script);
		AssertProtectedBytes(mainnet.GetScriptPubKey(), testnet.GetScriptPubKey());
		Assert.NotEqual(mainnet, testnet);
		LiquidAddress differentScript = LiquidAddress.FromScriptPubKey(
			ElementsPublicNetworkManifest.LiquidMainnet,
			Convert.FromHexString("60021516"));
		Assert.NotEqual(mainnet, differentScript);

		LiquidBlindingPublicKey firstKey = LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex));
		LiquidBlindingPublicKey secondKey = LiquidBlindingPublicKey.Create(Convert.FromHexString(
			"0379be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
		LiquidAddress firstConfidential = LiquidAddress.FromScriptPubKey(
			ElementsPublicNetworkManifest.LiquidMainnet,
			script,
			firstKey);
		LiquidAddress secondConfidential = LiquidAddress.FromScriptPubKey(
			ElementsPublicNetworkManifest.LiquidMainnet,
			script,
			secondKey);
		AssertProtectedBytes(firstConfidential.GetScriptPubKey(), secondConfidential.GetScriptPubKey());
		Assert.NotEqual(firstConfidential, secondConfidential);
		Assert.False(mainnet.Equals(null));
	}

	[Fact]
	public void NullValidationPrecedenceIsFrozen()
	{
		ArgumentNullException nullManifest = Assert.Throws<ArgumentNullException>(
			() => LiquidAddress.Parse(null!, null!));
		Assert.Equal("manifest", nullManifest.ParamName);

		ArgumentNullException nullAddress = Assert.Throws<ArgumentNullException>(
			() => LiquidAddress.Parse(ElementsPublicNetworkManifest.LiquidMainnet, null!));
		Assert.Equal("encodedAddress", nullAddress.ParamName);

		ArgumentNullException scriptManifest = Assert.Throws<ArgumentNullException>(
			() => LiquidAddress.FromScriptPubKey(null!, []));
		Assert.Equal("manifest", scriptManifest.ParamName);
	}

	[Fact]
	public void FailuresAndObjectStringsAreRedacted()
	{
		const string addressCanary = "canary-address-secret";
		AssertFailure(
			ElementsPublicNetworkManifest.LiquidMainnet,
			addressCanary,
			LiquidAddressParseFailure.InvalidEncoding);

		const string scriptCanary = "6a04736563726574";
		ArgumentException script = CaptureExpectedFailure<ArgumentException>(
			() => LiquidAddress.FromScriptPubKey(
				ElementsPublicNetworkManifest.LiquidMainnet,
				Convert.FromHexString(scriptCanary)));
		AssertProtectedAbsent(scriptCanary, script.ToString());
		AssertProtectedAbsent(PublicKeyHex, script.ToString());
	}

	[Fact]
	public void RedactionAssertionDiagnosticsAreOpaque()
	{
		const string canary = "canary-protected-address";
		var messageLeak = new InvalidOperationException($"failure contains {canary}");
		XunitException messageDiagnostic = CaptureAssignableFailure<XunitException>(
			() => AssertOpaqueAddressFailureText(canary, messageLeak));
		var diagnosticLeak = new InvalidOperationException(
			"The Liquid address could not be accepted.",
			new InvalidOperationException(canary));
		XunitException toStringDiagnostic = CaptureAssignableFailure<XunitException>(
			() => AssertOpaqueAddressFailureText(canary, diagnosticLeak));

		AssertProtectedAbsent(canary, messageDiagnostic.ToString());
		AssertProtectedAbsent(canary, toStringDiagnostic.ToString());
	}

	[Fact]
	public void ControlledRegtestWitnessRoundTrips()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidControlledRegtest;
		byte[] script = Convert.FromHexString("0014a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3");
		LiquidAddress unconfidential = LiquidAddress.FromScriptPubKey(manifest, script);
		Assert.Equal("71115e296e89e5f9161a74649f3a16fa2bb7ed9cf59d42ec203750b8a54350da", unconfidential.NetworkManifestId);
		Assert.Equal(LiquidAddressKind.WitnessV0KeyHash, unconfidential.Kind);
		Assert.False(unconfidential.IsConfidential);
		Assert.StartsWith("ert1", unconfidential.GetCanonicalAddressText(), StringComparison.Ordinal);
		Assert.Equal(unconfidential, LiquidAddress.Parse(manifest, unconfidential.GetCanonicalAddressText()));
		AssertProtectedBytes(script, unconfidential.GetScriptPubKey());

		LiquidBlindingPublicKey key = LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex));
		LiquidAddress confidential = LiquidAddress.FromScriptPubKey(manifest, script, key);
		Assert.True(confidential.IsConfidential);
		Assert.StartsWith("el1", confidential.GetCanonicalAddressText(), StringComparison.Ordinal);
		Assert.Equal(confidential, LiquidAddress.Parse(manifest, confidential.GetCanonicalAddressText()));
		Assert.Equal(unconfidential, LiquidAddress.Parse(manifest, confidential.GetUnconfidentialAddressText()));

		AssertFailure(
			ElementsPublicNetworkManifest.LiquidMainnet,
			unconfidential.GetCanonicalAddressText(),
			LiquidAddressParseFailure.NetworkMismatch);
		AssertFailure(
			ElementsPublicNetworkManifest.LiquidTestnet,
			confidential.GetCanonicalAddressText(),
			LiquidAddressParseFailure.NetworkMismatch);
		AssertFailure(
			manifest,
			"ex1q2pg4y56524t9wkzetfd4ch27tasxzcnrvarl93",
			LiquidAddressParseFailure.NetworkMismatch);
		AssertFailure(
			manifest,
			"tex1qjzge9yu5jktf0xyen2dee8v7n7s2rg4rttpweg",
			LiquidAddressParseFailure.NetworkMismatch);
	}

	[Fact]
	public void ControlledRegtestLegacyRoundTrips()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidControlledRegtest;
		byte[] script = Convert.FromHexString("76a914101112131415161718191a1b1c1d1e1f2021222388ac");
		LiquidBlindingPublicKey key = LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex));
		LiquidAddress unconfidential = LiquidAddress.FromScriptPubKey(manifest, script);
		Assert.Equal(LiquidAddressKind.PayToPubKeyHash, unconfidential.Kind);
		Assert.Equal(unconfidential, LiquidAddress.Parse(manifest, unconfidential.GetCanonicalAddressText()));

		LiquidAddress confidential = LiquidAddress.FromScriptPubKey(manifest, script, key);
		Assert.True(confidential.IsConfidential);
		Assert.Equal(confidential, LiquidAddress.Parse(manifest, confidential.GetCanonicalAddressText()));
		Assert.Equal(unconfidential, LiquidAddress.Parse(manifest, confidential.GetUnconfidentialAddressText()));

		AssertFailure(
			ElementsPublicNetworkManifest.LiquidMainnet,
			unconfidential.GetCanonicalAddressText(),
			LiquidAddressParseFailure.NetworkMismatch);
		AssertFailure(
			ElementsPublicNetworkManifest.LiquidTestnet,
			confidential.GetCanonicalAddressText(),
			LiquidAddressParseFailure.NetworkMismatch);
		AssertFailure(manifest, "PxjLPM1vfXHj9HvzfHfcrE9RtT7oFfUdLW", LiquidAddressParseFailure.NetworkMismatch);
		AssertFailure(manifest, "FpCrMc7Q5TZ6Nd5DZkGG3TFxe169znJfNS", LiquidAddressParseFailure.NetworkMismatch);
	}

	[Fact]
	public void ControlledRegtestFailuresDoNotLeakAddressText()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidControlledRegtest;
		string regtestAddress = LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString("0014a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3")).GetCanonicalAddressText();
		AssertFailure(
			ElementsPublicNetworkManifest.LiquidMainnet,
			regtestAddress,
			LiquidAddressParseFailure.NetworkMismatch);
	}

	[Fact]
	public async Task ConcurrentParsingAndEncodingHaveNoSharedMutableState()
	{
		const string address = "tlq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesgppyg3jgffxyu5zj23t9skjutesxyerxdp4xcmnswf68v7r603lgpq5ys6yg4rywl5nh72lxlpva";
		byte[] script = Convert.FromHexString("6028202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4041424344454647");
		LiquidBlindingPublicKey key = LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex));
		Task<LiquidAddress>[] parseTasks = Enumerable.Range(0, 256)
			.Select(_ => Task.Run(() => LiquidAddress.Parse(
				ElementsPublicNetworkManifest.LiquidTestnet,
				address)))
			.ToArray();
		Task<LiquidAddress>[] encodeTasks = Enumerable.Range(0, 256)
			.Select(_ => Task.Run(() => LiquidAddress.FromScriptPubKey(
				ElementsPublicNetworkManifest.LiquidTestnet,
				script,
				key)))
			.ToArray();

		LiquidAddress[] parsed = await Task.WhenAll(parseTasks);
		LiquidAddress[] encoded = await Task.WhenAll(encodeTasks);
		Assert.All(parsed, result => Assert.Equal(parsed[0], result));
		Assert.All(encoded, result => Assert.Equal(encoded[0], result));
		Assert.All(encoded, result => AssertProtectedText(address, result.GetCanonicalAddressText()));
		Assert.Equal(parsed[0], encoded[0]);
		Assert.DoesNotContain(
			typeof(LiquidAddressCodec).GetFields(BindingFlags.Static | BindingFlags.NonPublic),
			field => !field.IsLiteral);
	}

	[Fact]
	public void InternalSurfaceAndChecksumConstantsAreFrozen()
	{
		Type addressType = typeof(LiquidAddress);
		Assert.Equal(
			["LiquidAddress", "LiquidAddressCodec", "LiquidAddressFormatException",
			 "LiquidAddressKind", "LiquidAddressParseFailure"],
			addressType.Assembly.GetTypes()
				.Where(type => type.Namespace == typeof(LiquidAddress).Namespace)
				.Select(type => type.Name)
				.Order(StringComparer.Ordinal));
		Assert.False(addressType.IsPublic);
		Assert.True(addressType.IsSealed);
		Assert.False(addressType.IsAbstract);
		Assert.Equal(
			["PayToPubKeyHash", "PayToScriptHash", "WitnessV0KeyHash", "WitnessV0ScriptHash",
			 "WitnessV1Taproot", "WitnessUnknown"],
			Enum.GetNames<LiquidAddressKind>());
		Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<LiquidAddressKind>().Select(value => (int)value));
		Assert.Equal(
			["InvalidEncoding", "NetworkMismatch"],
			Enum.GetNames<LiquidAddressParseFailure>());
		Assert.Equal([0, 1], Enum.GetValues<LiquidAddressParseFailure>().Select(value => (int)value));

		PropertyInfo[] properties = addressType.GetProperties(
			BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		Assert.Equal(4, properties.Length);
		AssertProperty(properties, "NetworkManifestId", typeof(string));
		AssertProperty(properties, "Kind", typeof(LiquidAddressKind));
		AssertProperty(properties, "IsConfidential", typeof(bool));
		AssertProperty(properties, "WitnessVersion", typeof(byte?));

		ConstructorInfo constructor = Assert.Single(addressType.GetConstructors(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
		Assert.True(constructor.IsPrivate);
		ParameterInfo constructorParameter = Assert.Single(constructor.GetParameters());
		Assert.Equal(
			typeof((string, LiquidAddressKind, byte?, string, string, byte[], LiquidBlindingPublicKey)),
			constructorParameter.ParameterType);

		var expectedFields = new Dictionary<string, Type>(StringComparer.Ordinal)
		{
			["<Kind>k__BackingField"] = typeof(LiquidAddressKind),
			["<NetworkManifestId>k__BackingField"] = typeof(string),
			["<WitnessVersion>k__BackingField"] = typeof(byte?),
			["_blindingPublicKey"] = typeof(LiquidBlindingPublicKey),
			["_canonicalAddressText"] = typeof(string),
			["_scriptPubKey"] = typeof(byte[]),
			["_unconfidentialAddressText"] = typeof(string),
		};
		FieldInfo[] fields = addressType.GetFields(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		Assert.Equal(expectedFields.Count, fields.Length);
		foreach (FieldInfo field in fields)
		{
			Assert.True(field.IsPrivate);
			Assert.True(field.IsInitOnly);
			Assert.Equal(expectedFields[field.Name], field.FieldType);
		}

		MethodInfo[] methods = addressType.GetMethods(
			BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Where(method => !method.IsSpecialName)
			.ToArray();
		Assert.Equal(10, methods.Length);
		AssertMethod(methods, "Parse", true, typeof(LiquidAddress), typeof(ElementsPublicNetworkManifest), typeof(string));
		AssertMethod(methods, "FromScriptPubKey", true, typeof(LiquidAddress), typeof(ElementsPublicNetworkManifest), typeof(ReadOnlySpan<byte>), typeof(LiquidBlindingPublicKey));
		AssertMethod(methods, "GetCanonicalAddressText", false, typeof(string));
		AssertMethod(methods, "GetUnconfidentialAddressText", false, typeof(string));
		AssertMethod(methods, "GetScriptPubKey", false, typeof(byte[]));
		AssertMethod(methods, "GetBlindingPublicKey", false, typeof(byte[]));
		AssertMethod(methods, nameof(LiquidAddress.Equals), false, typeof(bool), typeof(LiquidAddress));
		AssertMethod(methods, nameof(LiquidAddress.Equals), false, typeof(bool), typeof(object));
		AssertMethod(methods, nameof(LiquidAddress.GetHashCode), false, typeof(int));
		AssertMethod(methods, nameof(LiquidAddress.ToString), false, typeof(string));

		Assert.False(typeof(LiquidAddressFormatException).IsPublic);
		Assert.True(typeof(LiquidAddressFormatException).IsSealed);
		ConstructorInfo exceptionConstructor = Assert.Single(typeof(LiquidAddressFormatException).GetConstructors(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
		Assert.True(exceptionConstructor.IsAssembly);
		Assert.Equal(typeof(LiquidAddressParseFailure), Assert.Single(exceptionConstructor.GetParameters()).ParameterType);
		AssertProperty(
			typeof(LiquidAddressFormatException).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
			"Failure",
			typeof(LiquidAddressParseFailure));
		Assert.False(typeof(LiquidAddressCodec).IsPublic);
		Assert.True(typeof(LiquidAddressCodec).IsAbstract);
		Assert.True(typeof(LiquidAddressCodec).IsSealed);

		Assert.Equal(90, Constant<int>("MaximumBech32Length"));
		Assert.Equal(256, Constant<int>("MaximumAddressLength"));
		Assert.Equal(1u, Constant<uint>("Bech32Constant"));
		Assert.Equal(0x2bc830a3u, Constant<uint>("Bech32mConstant"));
		Assert.Equal(1ul, Constant<ulong>("Blech32Constant"));
		Assert.Equal(0x455972a3350f7a1ul, Constant<ulong>("Blech32mConstant"));
		Assert.Equal(0x7ffffffffffffful, Constant<ulong>("Blech32LowMask"));
		Assert.Equal(
			[0x3b6a57b2u, 0x26508e6du, 0x1ea119fau, 0x3d4233ddu, 0x2a1462b3u],
			Enumerable.Range(0, 5).Select(index => Constant<uint>($"Bech32Generator{index}")));
		Assert.Equal(
			[0x7d52fba40bd886ul, 0x5e8dbf1a03950cul, 0x1c3a3c74072a18ul,
			 0x385d72fa0e5139ul, 0x7093e5a608865bul],
			Enumerable.Range(0, 5).Select(index => Constant<ulong>($"Blech32Generator{index}")));

		IEnumerable<Type> signatureTypes = typeof(LiquidAddress)
			.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
			.SelectMany(MemberSignatureTypes)
			.Concat(typeof(LiquidAddressCodec)
				.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
				.SelectMany(MemberSignatureTypes));
		Assert.DoesNotContain(
			signatureTypes,
			IsForbiddenSignatureType);
	}

	[Fact]
	public void UpstreamBlech32ChecksumOnlyVectorIsFrozen()
	{
		MethodInfo step = RequiredCodecMethod("Blech32Step");
		byte[] converted = [0, 28, 1, 0, 6, 1, 0, 5, 0, 24, 3, 16, 16, 2, 15, 10, 15, 15, 10, 17, 0];
		Assert.Equal(
			[22, 13, 13, 5, 4, 4, 23, 7, 28, 21, 30, 12],
			ComputeBlech32ChecksumViaStep(step, converted, 1));
		Assert.Equal(
			[30, 24, 1, 18, 1, 12, 14, 18, 29, 8, 3, 12],
			ComputeBlech32ChecksumViaStep(step, converted, 0x455972a3350f7a1));
	}

	[Fact]
	public void ProductionMethodBodiesDoNotReferenceForbiddenSurfaces()
	{
		Type[] productionTypes =
		[
			typeof(LiquidAddress),
			typeof(LiquidAddressFormatException),
			typeof(LiquidAddressCodec),
		];
		MethodBase[] methods = productionTypes
			.SelectMany(type => type.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
				BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
				.Concat(type.GetConstructors(
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
					BindingFlags.Instance | BindingFlags.DeclaredOnly)))
			.ToArray();
		IEnumerable<MemberInfo> references = methods
			.SelectMany(GetReferencedMembers)
			.Concat(productionTypes.SelectMany(type => type.GetFields(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
				BindingFlags.Instance | BindingFlags.DeclaredOnly)))
			.Concat(productionTypes.SelectMany(type => type.GetProperties(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
				BindingFlags.Instance | BindingFlags.DeclaredOnly)));

		Assert.DoesNotContain(references, IsForbiddenMember);
	}

	public static IEnumerable<object[]> PositiveRowIds()
	{
		yield return ["P001"];
		yield return ["P002"];
		yield return ["P003"];
		yield return ["P004"];
		yield return ["P005"];
		yield return ["P006"];
		yield return ["P007"];
		yield return ["P008"];
		yield return ["P009"];
		yield return ["P010"];
		yield return ["P011"];
		yield return ["P012"];
		yield return ["P013"];
		yield return ["P014"];
		yield return ["P015"];
		yield return ["P016"];
		yield return ["P017"];
		yield return ["P018"];
		yield return ["P019"];
		yield return ["P020"];
		yield return ["P021"];
		yield return ["P022"];
		yield return ["P023"];
		yield return ["P024"];
		yield return ["P025"];
		yield return ["P026"];
		yield return ["P027"];
		yield return ["P028"];
		yield return ["P029"];
		yield return ["P030"];
		yield return ["P031"];
		yield return ["P032"];
		yield return ["P033"];
		yield return ["P034"];
		yield return ["P035"];
		yield return ["P036"];
		yield return ["P037"];
		yield return ["P038"];
		yield return ["P039"];
		yield return ["P040"];
	}

	private static (
		ElementsPublicNetworkManifest Manifest,
		LiquidAddressKind Kind,
		bool Confidential,
		byte? WitnessVersion,
		string ScriptHex,
		string BlindingKeyHex,
		string CanonicalAddress,
		string UnconfidentialAddress,
		string UppercaseInput) PositiveCase(string rowId) => rowId switch
		{
			"P001" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.PayToPubKeyHash, false, null, "76a914101112131415161718191a1b1c1d1e1f2021222388ac", "", "PxjLPM1vfXHj9HvzfHfcrE9RtT7oFfUdLW", "PxjLPM1vfXHj9HvzfHfcrE9RtT7oFfUdLW", ""),
			"P002" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.PayToPubKeyHash, true, null, "76a914101112131415161718191a1b1c1d1e1f2021222388ac", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "VTpwKsrwasw7VnNf4GHMmcjNY3MR2Q81GaxDv7EyhVS8rzivSeE5iyDR7hQRF4dfJyk4Y3NXNWH8Q1Ka", "PxjLPM1vfXHj9HvzfHfcrE9RtT7oFfUdLW", ""),
			"P003" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.PayToScriptHash, false, null, "a914303132333435363738393a3b3c3d3e3f4041424387", "", "GmaLmeYVc4capjGSd7bwMHDcPPsr6xoZGU", "GmaLmeYVc4capjGSd7bwMHDcPPsr6xoZGU", ""),
			"P004" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.PayToScriptHash, true, null, "a914303132333435363738393a3b3c3d3e3f4041424387", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "VJL8r24A8tovW2f1hmFsHNXPTqBU1rp77hFp7wwj6pkkEbo6hynRUfDEPqkvYHwwMJxP4Jn4zAdFhzsv", "GmaLmeYVc4capjGSd7bwMHDcPPsr6xoZGU", ""),
			"P005" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessV0KeyHash, false, (byte?)0, "0014505152535455565758595a5b5c5d5e5f60616263", "", "ex1q2pg4y56524t9wkzetfd4ch27tasxzcnrvarl93", "ex1q2pg4y56524t9wkzetfd4ch27tasxzcnrvarl93", "EX1Q2PG4Y56524T9WKZETFD4CH27TASXZCNRVARL93"),
			"P006" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessV0KeyHash, true, (byte?)0, "0014505152535455565758595a5b5c5d5e5f60616263", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1qqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes5z32ff4g42k2av9jkjmt3w4uhmqv93xx73dy59hdyan5", "ex1q2pg4y56524t9wkzetfd4ch27tasxzcnrvarl93", "LQ1QQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTES5Z32FF4G42K2AV9JKJMT3W4UHMQV93XX73DY59HDYAN5"),
			"P007" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessV0ScriptHash, false, (byte?)0, "0020707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f", "", "ex1qwpchyum5w4m8w7re0fahclt707qgrq5rsjzcdpug3x9ghryd368szewa7v", "ex1qwpchyum5w4m8w7re0fahclt707qgrq5rsjzcdpug3x9ghryd368szewa7v", "EX1QWPCHYUM5W4M8W7RE0FAHCLT707QGRQ5RSJZCDPUG3X9GHRYD368SZEWA7V"),
			"P008" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessV0ScriptHash, true, (byte?)0, "0020707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1qqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesur3wfehgatkwau8j7nm037huluqsxpg8py9s6rc3zv23wxgmr50s2qufj4z5t3j", "ex1qwpchyum5w4m8w7re0fahclt707qgrq5rsjzcdpug3x9ghryd368szewa7v", "LQ1QQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESUR3WFEHGATKWAU8J7NM037HULUQSXPG8PY9S6RC3ZV23WXGMR50S2QUFJ4Z5T3J"),
			"P009" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessV1Taproot, false, (byte?)1, "5120a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf", "", "ex1p5zs69gay5kn2029f4246etdw47ctrv4nkj6mddachxath09ah6ls2gmggc", "ex1p5zs69gay5kn2029f4246etdw47ctrv4nkj6mddachxath09ah6ls2gmggc", "EX1P5ZS69GAY5KN2029F4246ETDW47CTRV4NKJ6MDDACHXATH09AH6LS2GMGGC"),
			"P010" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessV1Taproot, true, (byte?)1, "5120a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqte3g9p5236ffdx5752n24t4jk6ataskxet8d94k6mm3wd6hw7tm04lfd7hcvp3zyj3", "ex1p5zs69gay5kn2029f4246etdw47ctrv4nkj6mddachxath09ah6ls2gmggc", "LQ1PQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTE3G9P5236FFDX5752N24T4JK6ATASKXET8D94K6MM3WD6HW7TM04LFD7HCVP3ZYJ3"),
			"P011" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, false, (byte?)1, "51024e73", "", "ex1pfeesuuklaq", "ex1pfeesuuklaq", "EX1PFEESUUKLAQ"),
			"P012" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, true, (byte?)1, "51024e73", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesnnn7jykyxh9akna", "ex1pfeesuuklaq", "LQ1PQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESNNN7JYKYXH9AKNA"),
			"P013" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, false, (byte?)1, "5128c0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7", "", "ex1pcrqu9s7ychrv0jxfet9uenwwelgdr5kn6n2ad47cm8ddhhxamm07pc0zu0jwteh8ksf8ww", "ex1pcrqu9s7ychrv0jxfet9uenwwelgdr5kn6n2ad47cm8ddhhxamm07pc0zu0jwteh8ksf8ww", "EX1PCRQU9S7YCHRV0JXFET9UENWWELGDR5KN6N2AD47CM8DDHHXAMM07PC0ZU0JWTEH8KSF8WW"),
			"P014" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, true, (byte?)1, "5128c0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqte3sxpctpuf3wxclyvnjktenxuan7s68fd84x46mta3kw6m0wdmhklurs79clyuhnwwrxzyv67whl08", "ex1pcrqu9s7ychrv0jxfet9uenwwelgdr5kn6n2ad47cm8ddhhxamm07pc0zu0jwteh8ksf8ww", "LQ1PQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTE3SXPCTPUF3WXCLYVNJKTENXUAN7S68FD84X46MTA3KW6M0WDMHKLURS79CLYUHNWWRXZYV67WHL08"),
			"P015" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, false, (byte?)2, "52140102030405060708090a0b0c0d0e0f1011121314", "", "ex1zqypqxpq9qcrsszg2pvxq6rs0zqg3yyc5qcchs9", "ex1zqypqxpq9qcrsszg2pvxq6rs0zqg3yyc5qcchs9", "EX1ZQYPQXPQ9QCRSSZG2PVXQ6RS0ZQG3YYC5QCCHS9"),
			"P016" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, true, (byte?)2, "52140102030405060708090a0b0c0d0e0f1011121314", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1zqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqgzqvzq2ps8pqys5zcvp58q7yq3zgf3g362yevxf7lnh", "ex1zqypqxpq9qcrsszg2pvxq6rs0zqg3yyc5qcchs9", "LQ1ZQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESQGZQVZQ2PS8PQYS5ZCVP58Q7YQ3ZGF3G362YEVXF7LNH"),
			"P017" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, false, (byte?)16, "60021516", "", "ex1sz5tqzlz5td", "ex1sz5tqzlz5td", "EX1SZ5TQZLZ5TD"),
			"P018" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, true, (byte?)16, "60021516", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes9gkceq9926p9gep", "ex1sz5tqzlz5td", "LQ1SQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTES9GKCEQ9926P9GEP"),
			"P019" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, false, (byte?)16, "6028202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4041424344454647", "", "ex1syqsjygeyy5nzw2pf9g4jctfw9ucrzv3nxs6nvdec8yark0pa8cl5qs2zgdzy23j88k869f", "ex1syqsjygeyy5nzw2pf9g4jctfw9ucrzv3nxs6nvdec8yark0pa8cl5qs2zgdzy23j88k869f", "EX1SYQSJYGEYY5NZW2PF9G4JCTFW9UCRZV3NXS6NVDEC8YARK0PA8CL5QS2ZGDZY23J88K869F"),
			"P020" => (ElementsPublicNetworkManifest.LiquidMainnet, LiquidAddressKind.WitnessUnknown, true, (byte?)16, "6028202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4041424344454647", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "lq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesgppyg3jgffxyu5zj23t9skjutesxyerxdp4xcmnswf68v7r603lgpq5ys6yg4ryw2gfa6dte553q", "ex1syqsjygeyy5nzw2pf9g4jctfw9ucrzv3nxs6nvdec8yark0pa8cl5qs2zgdzy23j88k869f", "LQ1SQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESGPPYG3JGFFXYU5ZJ23T9SKJUTESXYERXDP4XCMNSWF68V7R603LGPQ5YS6YG4RYW2GFA6DTE553Q"),
			"P021" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.PayToPubKeyHash, false, null, "76a914d0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e388ac", "", "FpCrMc7Q5TZ6Nd5DZkGG3TFxe169znJfNS", "FpCrMc7Q5TZ6Nd5DZkGG3TFxe169znJfNS", ""),
			"P022" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.PayToPubKeyHash, true, null, "76a914d0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e388ac", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "vtS9oGwj6XuPE6vGfR7EcKNSkWsjyDeoBeyNfShtjNn6y3jhP4RwcTjpkSTecKD6KkDW7vzA9rpUKEvQ", "FpCrMc7Q5TZ6Nd5DZkGG3TFxe169znJfNS", ""),
			"P023" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.PayToScriptHash, false, null, "a914b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c387", "", "8vXjWW9kHc1a51rjkEfna1fF7QK6GwzQ1g", "8vXjWW9kHc1a51rjkEfna1fF7QK6GwzQ1g", ""),
			"P024" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.PayToScriptHash, true, null, "a914b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c387", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "vjTx85MD4YjNWSrPGqFVbRHa56VTTi5nG9UskBUpcQv1i1QzGb2RyeW4c8gVh5DtwPrp7WyuQYNH4kgU", "8vXjWW9kHc1a51rjkEfna1fF7QK6GwzQ1g", ""),
			"P025" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessV0KeyHash, false, (byte?)0, "0014909192939495969798999a9b9c9d9e9fa0a1a2a3", "", "tex1qjzge9yu5jktf0xyen2dee8v7n7s2rg4rttpweg", "tex1qjzge9yu5jktf0xyen2dee8v7n7s2rg4rttpweg", "TEX1QJZGE9YU5JKTF0XYEN2DEE8V7N7S2RG4RTTPWEG"),
			"P026" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessV0KeyHash, true, (byte?)0, "0014909192939495969798999a9b9c9d9e9fa0a1a2a3", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1qqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqte3yy3j2fef9vkj7vfnx5mnjwea8aq5x32xzzdzl2wxkjrw", "tex1qjzge9yu5jktf0xyen2dee8v7n7s2rg4rttpweg", "TLQ1QQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTE3YY3J2FEF9VKJ7VFNX5MNJWEA8AQ5X32XZZDZL2WXKJRW"),
			"P027" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessV0ScriptHash, false, (byte?)0, "0020404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f", "", "tex1qgpq5ys6yg4rywjzfff95cn2wfag9z5jn2324v46ct9d9khzate0s9lt0w2", "tex1qgpq5ys6yg4rywjzfff95cn2wfag9z5jn2324v46ct9d9khzate0s9lt0w2", "TEX1QGPQ5YS6YG4RYWJZFFF95CN2WFAG9Z5JN2324V46CT9D9KHZATE0S9LT0W2"),
			"P028" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessV0ScriptHash, true, (byte?)0, "0020404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1qqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesszpgfp5g32xgayyjjjtf3x5un6s29f9x4z42et4sk26tdw96hjlsyk2s4vf4n7f", "tex1qgpq5ys6yg4rywjzfff95cn2wfag9z5jn2324v46ct9d9khzate0s9lt0w2", "TLQ1QQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESSZPGFP5G32XGAYYJJJTF3X5UN6S29F9X4Z42ET4SK26TDW96HJLSYK2S4VF4N7F"),
			"P029" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessV1Taproot, false, (byte?)1, "5120606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f", "", "tex1pvpskycmyv4nxw6rfdf4kcmtwdac8zunnw36hvamc09a8klra0els0d8rx7", "tex1pvpskycmyv4nxw6rfdf4kcmtwdac8zunnw36hvamc09a8klra0els0d8rx7", "TEX1PVPSKYCMYV4NXW6RFDF4KCMTWDAC8ZUNNW36HVAMC09A8KLRA0ELS0D8RX7"),
			"P030" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessV1Taproot, true, (byte?)1, "5120606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtescrpvf3kgetxva5xj6ntd3kkummsw9e8xar4wemhs7t60d786lnl9lt9zlx0z9hv", "tex1pvpskycmyv4nxw6rfdf4kcmtwdac8zunnw36hvamc09a8klra0els0d8rx7", "TLQ1PQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESCRPVF3KGETXVA5XJ6NTD3KKUMMSW9E8XAR4WEMHS7T60D786LNL9LT9ZLX0Z9HV"),
			"P031" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, false, (byte?)1, "51024e73", "", "tex1pfeesnm2z0n", "tex1pfeesnm2z0n", "TEX1PFEESNM2Z0N"),
			"P032" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, true, (byte?)1, "51024e73", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesnnnk9j6enl7l75h", "tex1pfeesnm2z0n", "TLQ1PQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESNNNK9J6ENL7L75H"),
			"P033" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, false, (byte?)1, "5128808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7", "", "tex1pszqc9quyskrg0zyf329cervw37gfry5njj2ed9ucnxdfh8yan606pgdz5wj2tf48s93knt", "tex1pszqc9quyskrg0zyf329cervw37gfry5njj2ed9ucnxdfh8yan606pgdz5wj2tf48s93knt", "TEX1PSZQC9QUYSKRG0ZYF329CERVW37GFRY5NJJ2ED9UCNXDFH8YAN606PGDZ5WJ2TF48S93KNT"),
			"P034" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, true, (byte?)1, "5128808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqte3qyps2pcfpvxs7ygnz5t3jxcarusjxff89y4j6te3xv6nwwfm85l5zs69gay5kn2wvf05rekxzrw6", "tex1pszqc9quyskrg0zyf329cervw37gfry5njj2ed9ucnxdfh8yan606pgdz5wj2tf48s93knt", "TLQ1PQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTE3QYPS2PCFPVXS7YGNZ5T3JXCARUSJXFF89Y4J6TE3XV6NWWFM85L5ZS69GAY5KN2WVF05REKXZRW6"),
			"P035" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, false, (byte?)2, "5214e0e1e2e3e4e5e6e7e8e9eaebecedeeeff0f1f2f3", "", "tex1zurs79clyuhnw068fat47em0walc0ruhnkdja8t", "tex1zurs79clyuhnw068fat47em0walc0ruhnkdja8t", "TEX1ZURS79CLYUHNW068FAT47EM0WALC0RUHNKDJA8T"),
			"P036" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, true, (byte?)2, "5214e0e1e2e3e4e5e6e7e8e9eaebecedeeeff0f1f2f3", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1zqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqte3c8put37fe0xul5wn6htank7amls78e0xl462jfw4kqzr", "tex1zurs79clyuhnw068fat47em0walc0ruhnkdja8t", "TLQ1ZQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTE3C8PUT37FE0XUL5WN6HTANK7AMLS78E0XL462JFW4KQZR"),
			"P037" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, false, (byte?)16, "6002f4f5", "", "tex1s7n6sxzkf4t", "tex1s7n6sxzkf4t", "TEX1S7N6SXZKF4T"),
			"P038" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, true, (byte?)16, "6002f4f5", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqte3a84cppt65c6xkjr", "tex1s7n6sxzkf4t", "TLQ1SQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTE3A84CPPT65C6XKJR"),
			"P039" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, false, (byte?)16, "602808090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f", "", "tex1spqys5zcvp58q7yq3zgf3g9gkzuvpjxsmrsw3u8eqyy3zxfp9ycnjs2f29vkz6t30dqn5sk", "tex1spqys5zcvp58q7yq3zgf3g9gkzuvpjxsmrsw3u8eqyy3zxfp9ycnjs2f29vkz6t30dqn5sk", "TEX1SPQYS5ZCVP58Q7YQ3ZGF3G9GKZUVPJXSMRSW3U8EQYY3ZXFP9YCNJS2F29VKZ6T30DQN5SK"),
			"P040" => (ElementsPublicNetworkManifest.LiquidTestnet, LiquidAddressKind.WitnessUnknown, true, (byte?)16, "602808090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "tlq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f389q5j52ev95hz7damas3tj06ux", "tex1spqys5zcvp58q7yq3zgf3g9gkzuvpjxsmrsw3u8eqyy3zxfp9ycnjs2f29vkz6t30dqn5sk", "TLQ1SQFUMUEN7L8WTHTZ45P3FTN58PVRS9XLUMVKUU2XET8EGZKCKLQTESZQFPG9SCRGWPUGPZYSNZS23V9CCRYDPK8QARC0JQGFZYVJZ2F389Q5J52EV95HZ7DAMAS3TJ06UX"),
			_ => throw new InvalidOperationException("An unknown positive address row ID was requested."),
		};

	public static IEnumerable<object[]> InvalidPointRowIds()
	{
		yield return ["I001"];
		yield return ["I002"];
		yield return ["I003"];
		yield return ["I004"];
		yield return ["I005"];
		yield return ["I006"];
	}

	private static (ElementsPublicNetworkManifest Manifest, string Address) InvalidPointCase(string rowId) => rowId switch
	{
		"I001" => (ElementsPublicNetworkManifest.LiquidMainnet, "VTpt2eoB5rosXRebdDb5xVkurtSDqkWWFuXJsWAXxoLhTBhJcyTwGSMLyDsEYWt1LdinKMcyKJS7SA6x"),
		"I002" => (ElementsPublicNetworkManifest.LiquidMainnet, "lq1qqgqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq5z32ff4g42k2av9jkjmt3w4uhmqv93xx4jvv205wn4rd"),
		"I003" => (ElementsPublicNetworkManifest.LiquidMainnet, "lq1pqgqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqnnn56knjrexugtx"),
		"I004" => (ElementsPublicNetworkManifest.LiquidTestnet, "vtS6W3sxbWn9FkCDENQxoCPz5MxYna3JAyYTcqdSzggfZEi5ZPfo9vskbxvTumTSMQCDuFEc6f6WJo11"),
		"I005" => (ElementsPublicNetworkManifest.LiquidTestnet, "tlq1qqgqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqpyy3j2fef9vkj7vfnx5mnjwea8aq5x32xfpv2pqd9p6nh"),
		"I006" => (ElementsPublicNetworkManifest.LiquidTestnet, "tlq1pqgqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqnnnudql0k3a7qvv"),
		_ => throw new InvalidOperationException("An unknown invalid-point row ID was requested."),
	};

	public static IEnumerable<object[]> ExactNinetyOneRowIds()
	{
		yield return ["L001"];
		yield return ["L002"];
	}

	private static string ExactNinetyOneCase(string rowId) => rowId switch
	{
		"L001" => "tex1qqqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f389q5j52ev95hz7vp3a4nrqw",
		"L002" => "tex1pqqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f389q5j52ev95hz7vp3w8qdlr",
		_ => throw new InvalidOperationException("An unknown length row ID was requested."),
	};

	public static IEnumerable<object[]> ShortProgramDefectRowIds() =>
		Enumerable.Range(0, ShortProgramAddresses.Length).Select(index => new object[] { index });

	public static IEnumerable<object[]> SupplementalInvalidAddressRowIds()
	{
		yield return ["N001"];
		yield return ["N002"];
		yield return ["N003"];
		yield return ["N004"];
		yield return ["N005"];
		yield return ["N006"];
		yield return ["N007"];
		yield return ["N008"];
		yield return ["N009"];
		yield return ["N010"];
		yield return ["N011"];
		yield return ["N012"];
		yield return ["N013"];
		yield return ["N014"];
		yield return ["N015"];
		yield return ["N016"];
		yield return ["N017"];
		yield return ["N018"];
		yield return ["N019"];
		yield return ["N020"];
		yield return ["N021"];
		yield return ["N022"];
		yield return ["N023"];
		yield return ["N024"];
		yield return ["N025"];
		yield return ["N026"];
		yield return ["N027"];
		yield return ["N028"];
		yield return ["N029"];
		yield return ["N030"];
		yield return ["N031"];
		yield return ["N032"];
		yield return ["N033"];
		yield return ["N034"];
		yield return ["N035"];
		yield return ["N036"];
	}

	private static string SupplementalInvalidAddressCase(string rowId) => rowId switch
	{
		"N001" => "PxjLPM1vfXHj9HvzfHfcrE9RtT7oFfUdL1",
		"N002" => "PxjLPM1vfXHj9HvzfHfcrE9RtT7oFf",
		"N003" => "1PxjLPM1vfXHj9HvzfHfcrE9RtT7oFfUdLW",
		"N004" => "6CimGV6eXvzFzAGrmULdDWiLEUDSaTJmj",
		"N005" => "2kLyrZdM5s2gq1bjKVzZTEH2Cr6GzvPPPST7",
		"N006" => "grvfjuY2V6kXVVndguf1DVaUKdwR655CGw",
		"N007" => "4z4FrK5uqGBygumSs4p6Qzh4FgNFcYKJcHT3CfncPveRXJ9Mm1iSP6Qd26himpwU26gCzANvYXKrzJsoG",
		"N008" => "Vqz97Yk6xqtZ9siVYmLqaBt13CRJ3fB6xfaWmpGZm3giKvDxfCqtP7uMMVWoBsqZj17hvDgyXhDMFzWh",
		"N009" => "VGkLS3RMttxPfjhjp1kcsLA4HTpUWmbTRYe5VFjBfiJr8XyQLMvBPtsSzoMEGf2i6y4EqxDJHuqFsgk3",
		"N010" => "7T1tjLgQhJRwwZ2jcBG2zEXu1Eg4dZsyhgs81FZAg5dWRTuqPQqKMjkj8dc76VfLWrRYx47wCxRW6vF",
		"N011" => "3AdQanKVRvXoqg3GayQTCfvi241P73B3vBnrk1QYhFiVwhfmvSgM7AdmPSHAtbDgDmuDxF9vwHscWjsKqc",
		"N012" => "VTq7trsoTLuHczKQe7CxbQvwGdPQZfYCXsRxtonpMfxQpebEe6gHWDDHLw1hCLhp93vy1sXeQ2aGQBF8",
		"N013" => "3AdQanKVRvXoqg3GayQTCfvi241P73B3vBnrk1QYhFiVwhfn2tBJjrF7oSZLN152p6MT4c1U13uvBviXK7",
		"N014" => "ex1q2pg4y56524t9wkzetfd4ch27tasxzcnrepnnqn",
		"N015" => "ex1pfeesfqxncz",
		"N016" => "tex1pfeesuuklaq",
		"N017" => "ex1ppycs6ku",
		"N018" => "ex1pppyaw80s",
		"N019" => "ex13pqsz8r44",
		"N020" => "ex1phr8e97",
		"N021" => "ex1pqyeuhwk7",
		"N022" => "ex1pqqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f389qzle5gp",
		"N023" => "ex1qqqqsgmx3qf",
		"N024" => "ex1qqqqsyqcyq5rqwzqfpg9scrgwpugpzys7ndqk4",
		"N025" => "ex1qqqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs2dexhx",
		"N026" => "ex1qqqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarckc2wwp",
		"N027" => "ex1qqqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jq9fk8c5",
		"N028" => "ex1qqqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f38zwv5rj",
		"N029" => "lq1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq9u80hdgzld9g",
		"N030" => "lq1qqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtest444sfrgsf6a",
		"N031" => "lq1q2pg4y56524t9wkzetfd4ch27tasxzcnr0dcsehs9yq3n",
		"N032" => "ex1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqqqqqqqqqqqqq7xtvjj",
		"N033" => "lq1qqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes5z32ff4g42k2av9jkjmt3w4uhmqv93xxkypn3dwc9qw5",
		"N034" => "lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesnnnk8gppwwsutwa",
		"N035" => "tlq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesnnn7jykyxh9akna",
		"N036" => "ert1pfees5fana7",
		_ => throw new InvalidOperationException("An unknown negative address row ID was requested."),
	};

	public static IEnumerable<object[]> MalformedScriptRowIds()
	{
		yield return ["S001"];
		yield return ["S002"];
		yield return ["S003"];
		yield return ["S004"];
		yield return ["S005"];
		yield return ["S006"];
		yield return ["S007"];
		yield return ["S008"];
		yield return ["S009"];
		yield return ["S010"];
		yield return ["S011"];
		yield return ["S012"];
		yield return ["S013"];
		yield return ["S014"];
		yield return ["S015"];
		yield return ["S016"];
		yield return ["S017"];
		yield return ["S018"];
		yield return ["S019"];
		yield return ["S020"];
		yield return ["S021"];
		yield return ["S022"];
		yield return ["S023"];
		yield return ["S024"];
		yield return ["S025"];
		yield return ["S026"];
		yield return ["S027"];
		yield return ["S028"];
		yield return ["S029"];
		yield return ["S030"];
		yield return ["S031"];
		yield return ["S032"];
		yield return ["S033"];
		yield return ["S034"];
		yield return ["S035"];
		yield return ["S036"];
		yield return ["S037"];
		yield return ["S038"];
		yield return ["S039"];
		yield return ["S040"];
		yield return ["S041"];
		yield return ["S042"];
		yield return ["S043"];
		yield return ["S044"];
		yield return ["S045"];
		yield return ["S046"];
		yield return ["S047"];
		yield return ["S048"];
		yield return ["S049"];
		yield return ["S050"];
		yield return ["S051"];
		yield return ["S052"];
	}

	private static string MalformedScriptCase(string rowId) => rowId switch
	{
		"S001" => "",
		"S002" => "210279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798ac",
		"S003" => "51210279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f8179851ae",
		"S004" => "6a",
		"S005" => "6a024e73",
		"S006" => "ac",
		"S007" => "76",
		"S008" => "76a9",
		"S009" => "76a914",
		"S010" => "76a914101112131415161718191a1b1c1d1e1f202122",
		"S011" => "76a914101112131415161718191a1b1c1d1e1f20212223",
		"S012" => "76a914101112131415161718191a1b1c1d1e1f2021222388",
		"S013" => "76a914101112131415161718191a1b1c1d1e1f2021222388ac00",
		"S014" => "a9",
		"S015" => "a914",
		"S016" => "a914101112131415161718191a1b1c1d1e1f202122",
		"S017" => "a914101112131415161718191a1b1c1d1e1f20212223",
		"S018" => "a914101112131415161718191a1b1c1d1e1f202122238700",
		"S019" => "00",
		"S020" => "0014",
		"S021" => "0014101112131415161718191a1b1c1d1e1f202122",
		"S022" => "0014101112131415161718191a1b1c1d1e1f2021222300",
		"S023" => "00",
		"S024" => "0020",
		"S025" => "0020707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e",
		"S026" => "0020707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f00",
		"S027" => "51",
		"S028" => "5102",
		"S029" => "51024e",
		"S030" => "51024e7300",
		"S031" => "60",
		"S032" => "6028",
		"S033" => "6028202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40414243444546",
		"S034" => "6028202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464700",
		"S035" => "004c14101112131415161718191a1b1c1d1e1f20212223",
		"S036" => "514c024e73",
		"S037" => "0000",
		"S038" => "000100",
		"S039" => "00020001",
		"S040" => "0013000102030405060708090a0b0c0d0e0f101112",
		"S041" => "0015000102030405060708090a0b0c0d0e0f1011121314",
		"S042" => "001f000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e",
		"S043" => "0021000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20",
		"S044" => "0028000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2021222324252627",
		"S045" => "5100",
		"S046" => "510100",
		"S047" => "5129000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728",
		"S048" => "6000",
		"S049" => "600100",
		"S050" => "6029000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728",
		"S051" => "61024e73",
		"S052" => "50024e73",
		_ => throw new InvalidOperationException("An unknown malformed script row ID was requested."),
	};

	private static readonly string[] ShortProgramAddresses =
	[
		// Literal sealed rows follow; the implementation under test does not construct them.
		"lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtest27vckph2y79",
		"lq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqgjr4huqyu7deg",
		"lq1zqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesn5h70x9fwauy",
		"lq1zqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqsf8ez4ahgqcru",
		"lq1rqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesm7ssz37r4d9u",
		"lq1rqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqcquakjkpy2vjs",
		"lq1yqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes2p9ng0duxxcx",
		"lq1yqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespqf5m3ceh7ga4z",
		"lq19qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesztza9ckkakp7",
		"lq19qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespgq0l9ljpjzfyw",
		"lq1xqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes64t0jgjge0rl",
		"lq1xqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespsmtnsk0jxuu76",
		"lq18qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesjlvpllfzzl68",
		"lq18qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespcjshy3yy2kg0k",
		"lq1gqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes3zgfxaalkesz",
		"lq1gqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszqkq9waxcv93mg",
		"lq1fqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteseg08t2x4dff6",
		"lq1fqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszglmp66dwq092y",
		"lq12qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespkx4u6ztfstm",
		"lq12qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszsyld0nsa53sss",
		"lq1tqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesfupm3depjqjr",
		"lq1tqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszcdyfm5mtcmypu",
		"lq1vqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtescr5cmn27pt0e",
		"lq1vqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrqyv0u75aze4xw",
		"lq1dqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtessfnkky356mkp",
		"lq1dqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrgdhtgeltwnphz",
		"lq1wqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesgh6yp5427z5q",
		"lq1wqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrskn8aszc6d5dk",
		"lq10qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqaa2vrwq9jdc",
		"lq10qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrclgrfhfwk8qu6",
		"lq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteswyj56s5elwq2",
		"lq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesyqppseh3xplf8u",
		"tlq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes39cherjf4ehf",
		"tlq1pqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqg3t2l04rt9lzr",
		"tlq1zqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesfm39wnkh3q4g",
		"tlq1zqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqs20x2xgslm2ch",
		"tlq1rqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesp3ktryda2svs",
		"tlq1rqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesqcr5z7prxn37fm",
		"tlq1yqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesswrgf67zem32",
		"tlq1yqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespq2uyetvsfn0wf",
		"tlq19qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtescyyxyd9gztgj",
		"tlq19qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespgr8qdv8x9eml9",
		"tlq1xqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesq6d5napkxj2n",
		"tlq1xqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespscrvc96438w93",
		"tlq18qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesgs26726uaznt",
		"tlq18qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtespc3cgvz3rad65a",
		"tlq1gqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtestdwj8gwpfyew",
		"tlq1gqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszq4g6xwnlm7rqr",
		"tlq1fqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesr8fu2l4tj5qk",
		"tlq1fqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszgun7jfcfh5h30",
		"tlq12qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesmeqwa034kdzh",
		"tlq12qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszs8hj8q96r2ztm",
		"tlq1tqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesnn8qsc2ldam0",
		"tlq1tqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszcwvkn8wv0qk6h",
		"tlq1vqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqteszvjr6xeq7kx4",
		"tlq1vqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrq8ys5dp64z8a9",
		"tlq1dqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes2x4dh3z29xld",
		"tlq1dqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrgwl5q22vegnvf",
		"tlq1wqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesjculqpx5plav",
		"tlq1wqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrs4mc4rhldkxka",
		"tlq10qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes6jm3dka760y5",
		"tlq10qfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesrcuqupyufpuj83",
		"tlq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtes5t50m988qnfx",
		"tlq1sqfumuen7l8wthtz45p3ftn58pvrs9xlumvkuu2xet8egzkcklqtesyqzf03yypkymuh",
	];

	private static T Constant<T>(string name) =>
		Assert.IsType<T>(typeof(LiquidAddressCodec)
			.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!
			.GetRawConstantValue());

	private static MethodInfo RequiredCodecMethod(string name) =>
		Assert.IsAssignableFrom<MethodInfo>(typeof(LiquidAddressCodec).GetMethod(
			name,
			BindingFlags.Static | BindingFlags.NonPublic));

	private static ulong InvokeBlech32Step(MethodInfo step, ulong checksum, int value) =>
		Assert.IsType<ulong>(step.Invoke(null, [checksum, value]));

	private static int[] ComputeBlech32ChecksumViaStep(
		MethodInfo step,
		ReadOnlySpan<byte> converted,
		ulong constant)
	{
		ulong checksum = 1;
		foreach (char character in "lq")
		{
			checksum = InvokeBlech32Step(step, checksum, character >> 5);
		}
		checksum = InvokeBlech32Step(step, checksum, 0);
		foreach (char character in "lq")
		{
			checksum = InvokeBlech32Step(step, checksum, character & 31);
		}
		foreach (byte value in converted)
		{
			checksum = InvokeBlech32Step(step, checksum, value);
		}
		for (int index = 0; index < 12; index++)
		{
			checksum = InvokeBlech32Step(step, checksum, 0);
		}
		checksum ^= constant;
		return Enumerable.Range(0, 12)
			.Select(index => (int)((checksum >> (5 * (11 - index))) & 31))
			.ToArray();
	}

	private static void AssertProperty(
		IEnumerable<PropertyInfo> properties,
		string name,
		Type propertyType)
	{
		PropertyInfo property = Assert.Single(properties, candidate => candidate.Name == name);
		Assert.Equal(propertyType, property.PropertyType);
		MethodInfo getter = Assert.IsAssignableFrom<MethodInfo>(property.GetMethod);
		Assert.True(getter.IsPublic);
		Assert.False(getter.IsStatic);
		Assert.Null(property.SetMethod);
	}

	private static void AssertMethod(
		IEnumerable<MethodInfo> methods,
		string name,
		bool isStatic,
		Type returnType,
		params Type[] parameterTypes)
	{
		MethodInfo method = Assert.Single(methods, candidate =>
			candidate.Name == name &&
			candidate.IsStatic == isStatic &&
			candidate.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes));
		Assert.True(method.IsPublic);
		Assert.Equal(returnType, method.ReturnType);
	}

	private static IReadOnlyList<(int Offset, MethodBase Method)> GetCalledMethods(MethodBase method)
	{
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		Dictionary<short, OpCode> opCodes = typeof(OpCodes)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.FieldType == typeof(OpCode))
			.Select(field => (OpCode)field.GetValue(null)!)
			.ToDictionary(opCode => opCode.Value);
		var calledMethods = new List<(int Offset, MethodBase Method)>();
		int position = 0;
		while (position < il.Length)
		{
			int offset = position;
			short value = il[position++] == 0xfe
				? unchecked((short)(0xfe00 | il[position++]))
				: il[position - 1];
			OpCode opCode = opCodes[value];
			if (opCode.OperandType == OperandType.InlineMethod)
			{
				int token = BitConverter.ToInt32(il, position);
				MethodBase? called = method.Module.ResolveMethod(
					token,
					method.DeclaringType?.GetGenericArguments(),
					method.IsGenericMethod ? method.GetGenericArguments() : null);
				if (called is not null)
				{
					calledMethods.Add((offset, called));
				}
			}
			position += GetOperandSize(opCode.OperandType, il, position);
		}
		return calledMethods;
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
			_ => throw new InvalidOperationException("An unsupported IL operand type was encountered."),
		};

	private static IEnumerable<MemberInfo> GetReferencedMembers(MethodBase method)
	{
		byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		Dictionary<short, OpCode> opCodes = typeof(OpCodes)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.FieldType == typeof(OpCode))
			.Select(field => (OpCode)field.GetValue(null)!)
			.ToDictionary(opCode => opCode.Value);
		int position = 0;
		while (position < il.Length)
		{
			short value = il[position++] == 0xfe
				? unchecked((short)(0xfe00 | il[position++]))
				: il[position - 1];
			OpCode opCode = opCodes[value];
			if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or
				OperandType.InlineType or OperandType.InlineTok)
			{
				int token = BitConverter.ToInt32(il, position);
				MemberInfo? member = method.Module.ResolveMember(
					token,
					method.DeclaringType?.GetGenericArguments(),
					method.IsGenericMethod ? method.GetGenericArguments() : null);
				if (member is not null)
				{
					yield return member;
				}
			}
			position += GetOperandSize(opCode.OperandType, il, position);
		}
	}

	private static IEnumerable<Type> MemberSignatureTypes(MemberInfo member) => member switch
	{
		FieldInfo field => [field.FieldType],
		PropertyInfo property => [property.PropertyType],
		MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
			.Append(method.ReturnType),
		ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
		_ => [],
	};

	private static void AssertProtectedText(string expected, string actual) =>
		Assert.True(
			StringComparer.Ordinal.Equals(expected, actual),
			"A protected Liquid address test value did not match.");

	private static void AssertProtectedBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
		Assert.True(
			expected.SequenceEqual(actual),
			"Protected Liquid address test bytes did not match.");

	private static void AssertProtectedAbsent(string protectedValue, string actual) =>
		Assert.False(
			actual.Contains(protectedValue, StringComparison.OrdinalIgnoreCase),
			"A protected Liquid address test value appeared in diagnostic text.");

	private static void AssertOpaqueAddressFailureText(
		string protectedValue,
		Exception exception)
	{
		Assert.True(
			StringComparer.Ordinal.Equals(
				"The Liquid address could not be accepted.",
				exception.Message),
			"The Liquid address failure message changed.");
		Assert.True(
			exception.InnerException is null,
			"The Liquid address failure gained an inner exception.");
		AssertOpaqueDiagnostic(protectedValue, exception.ToString());
	}

	private static void AssertOpaqueDiagnostic(string protectedValue, string diagnostic)
	{
		if (protectedValue.Length >= 8)
		{
			AssertProtectedAbsent(protectedValue, diagnostic);
		}
	}

	private static TException CaptureExpectedFailure<TException>(Action action)
		where TException : Exception
	{
		try
		{
			action();
		}
		catch (Exception exception)
		{
			if (exception.GetType() == typeof(TException))
			{
				return (TException)exception;
			}
			Assert.Fail("The operation returned an unexpected failure type.");
		}

		Assert.Fail("The operation did not return the expected failure type.");
		throw new InvalidOperationException("Unreachable after a failed assertion.");
	}

	private static TException CaptureAssignableFailure<TException>(Action action)
		where TException : Exception
	{
		try
		{
			action();
		}
		catch (Exception exception)
		{
			if (exception is TException expected)
			{
				return expected;
			}
			Assert.Fail("The operation returned an unexpected failure family.");
		}

		Assert.Fail("The operation did not return the expected failure family.");
		throw new InvalidOperationException("Unreachable after a failed assertion.");
	}

	private static void AssertFailure(
		ElementsPublicNetworkManifest manifest,
		string malformedAddress,
		LiquidAddressParseFailure failure)
	{
		LiquidAddressFormatException exception = CaptureExpectedFailure<LiquidAddressFormatException>(
			() => LiquidAddress.Parse(manifest, malformedAddress));
		AssertOpaqueAddressFailureText(malformedAddress, exception);
		Assert.Equal(failure, exception.Failure);
	}

	private static ElementsPublicNetworkManifest OtherManifest(
		ElementsPublicNetworkManifest manifest) =>
		ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidMainnet)
			? ElementsPublicNetworkManifest.LiquidTestnet
			: ElementsPublicNetworkManifest.LiquidMainnet;

	private static string ExpectedManifestId(ElementsPublicNetworkManifest manifest) =>
		ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidMainnet)
			? "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b"
			: "e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20";

	private static bool IsForbiddenSignatureType(Type type)
	{
		if (type.HasElementType)
		{
			return IsForbiddenSignatureType(type.GetElementType()!);
		}
		if (type.IsGenericType && type.GetGenericArguments().Any(IsForbiddenSignatureType))
		{
			return true;
		}
		string identity = type.FullName ?? type.Name;
		return identity.StartsWith("WalletWasabi.Liquid.Rpc.", StringComparison.Ordinal) ||
			identity.StartsWith("WalletWasabi.Liquid.Wallet.", StringComparison.Ordinal) ||
			identity.StartsWith("WalletWasabi.WabiSabi.", StringComparison.Ordinal) ||
			identity.StartsWith("WalletWasabi.Coordinator.", StringComparison.Ordinal) ||
			identity.StartsWith("WalletWasabi.Fluent.", StringComparison.Ordinal) ||
			identity.StartsWith("NBitcoin.", StringComparison.Ordinal) ||
			identity.StartsWith("System.IO.", StringComparison.Ordinal) ||
			identity.StartsWith("System.Net.", StringComparison.Ordinal) ||
			identity.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal) ||
			identity.StartsWith("System.Environment", StringComparison.Ordinal) ||
			identity.StartsWith("System.Runtime.InteropServices.", StringComparison.Ordinal) ||
			identity.Contains("Pset", StringComparison.OrdinalIgnoreCase) ||
			identity.Contains("Psbt", StringComparison.OrdinalIgnoreCase) ||
			identity.Contains("Native", StringComparison.OrdinalIgnoreCase) ||
			identity.Contains("Json", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsForbiddenMember(MemberInfo member) =>
		(member.DeclaringType is { } declaringType && IsForbiddenSignatureType(declaringType)) ||
		MemberSignatureTypes(member).Any(IsForbiddenSignatureType);
}
