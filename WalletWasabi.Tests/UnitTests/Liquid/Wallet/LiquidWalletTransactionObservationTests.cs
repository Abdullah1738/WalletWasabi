using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletTransactionObservationTests
{
	private const int MaxInputCount = 102_298;
	private const int MaxOwnedOutputCount = 9_279;
	private const string TransactionIdHex = "35ab905fc934c08fa976d55427bdd3970383e0f01ece059426ec04144b4ecc3d";
	private const string WitnessBindingHex = "78ee7e96e486b0fbe2ad4df5820fe00f4c77b0c7475562bf9bf31871d3294e01";
	private const string FirstInputHex = "a2fb2fd3085d34848af57f14793f7111614a19f4c5f616f19dbd270a3579b56200000000";
	private const string SecondInputHex = "a2fb2fd3085d34848af57f14793f7111614a19f4c5f616f19dbd270a3579b56201000000";
	private const string ExternalAssetHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
	private const string ExternalScriptHex = "0014d363d538bea12647f61c634bdd7a791d676850e9";
	private const string ExternalSpendPublicKeyHex = "0211b24105b70886a90f848da8c659be73bd6e3486cf2aa706693907479865bf81";
	private const string ExternalBlindingPublicKeyHex = "023217042995590e0ad7e37bc929d062233f4d913bb3794c8cbabdc6634a580500";
	private const string InternalAssetHex = "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f";
	private const string InternalScriptHex = "0014a3e18f06b5369914234bd7df7462d7bbd3635714";
	private const string InternalSpendPublicKeyHex = "034473b362e3ff48c9188e9e02165d72e111412e5ba451bf76cd2b78109186d866";
	private const string InternalBlindingPublicKeyHex = "036e0ed3aff52f55da08facb4a980a93b6207abad0258b49e556021fe6f539c6e5";

	private static readonly byte[] TransactionId = Convert.FromHexString(TransactionIdHex);
	private static readonly byte[] WitnessBinding = Convert.FromHexString(WitnessBindingHex);
	private static readonly byte[] ExternalAsset = Convert.FromHexString(ExternalAssetHex);
	private static readonly byte[] ExternalScript = Convert.FromHexString(ExternalScriptHex);
	private static readonly byte[] ExternalSpendPublicKey = Convert.FromHexString(ExternalSpendPublicKeyHex);
	private static readonly byte[] ExternalBlindingPublicKey = Convert.FromHexString(ExternalBlindingPublicKeyHex);
	private static readonly byte[] InternalAsset = Convert.FromHexString(InternalAssetHex);
	private static readonly byte[] InternalScript = Convert.FromHexString(InternalScriptHex);
	private static readonly byte[] InternalSpendPublicKey = Convert.FromHexString(InternalSpendPublicKeyHex);
	private static readonly byte[] InternalBlindingPublicKey = Convert.FromHexString(InternalBlindingPublicKeyHex);

	[Fact]
	public void PreservesExactMultiassetFixtureAndDefensiveCopies()
	{
		byte[] transactionId = [.. TransactionId];
		byte[] witnessBinding = [.. WitnessBinding];
		LiquidOutPoint firstInput = ParseOutPoint(FirstInputHex);
		LiquidOutPoint secondInput = ParseOutPoint(SecondInputHex);
		LiquidOwnedOutputObservation external = ExternalOutput();
		LiquidOwnedOutputObservation internalOutput = InternalOutput();
		var sourceInputs = new List<LiquidOutPoint> { firstInput, secondInput };
		var sourceOutputs = new List<LiquidOwnedOutputObservation> { external, internalOutput };

		LiquidWalletTransactionObservation observation = LiquidWalletTransactionObservation.Create(
			transactionId,
			witnessBinding,
			sourceInputs,
			sourceOutputs);

		transactionId.AsSpan().Clear();
		witnessBinding.AsSpan().Clear();
		sourceInputs.Clear();
		sourceOutputs.Clear();

		Assert.Equal(2, observation.InputCount);
		Assert.Equal(2, observation.OwnedOutputCount);
		Assert.Equal(TransactionId, observation.GetTransactionIdConsensusBytes());
		Assert.NotEqual(TransactionId.Reverse().ToArray(), observation.GetTransactionIdConsensusBytes());
		Assert.Equal(WitnessBinding, observation.GetTransactionWitnessBinding());

		IReadOnlyList<LiquidOutPoint> inputs = observation.GetInputs();
		Assert.Equal(Convert.FromHexString(FirstInputHex), inputs[0].ToConsensusBytes());
		Assert.Equal(Convert.FromHexString(SecondInputHex), inputs[1].ToConsensusBytes());
		Assert.NotEqual(Convert.FromHexString(FirstInputHex).Reverse().ToArray(), inputs[0].ToConsensusBytes());
		Assert.NotEqual(Convert.FromHexString(SecondInputHex).Reverse().ToArray(), inputs[1].ToConsensusBytes());

		IReadOnlyList<LiquidOwnedOutputObservation> outputs = observation.GetOwnedOutputs();
		AssertOutput(
			outputs[0],
			0,
			LiquidKeyBranch.External,
			0,
			900,
			ExternalAsset,
			ExternalScript,
			ExternalSpendPublicKey,
			ExternalBlindingPublicKey);
		AssertOutput(
			outputs[1],
			1,
			LiquidKeyBranch.Internal,
			1,
			2_000,
			InternalAsset,
			InternalScript,
			InternalSpendPublicKey,
			InternalBlindingPublicKey);
	}

	[Fact]
	public void PreservesNonmonotonicInputOrderAndAllowsSpendOnlyObservation()
	{
		LiquidOutPoint first = OutPoint('a', 7);
		LiquidOutPoint second = OutPoint('a', 2);

		LiquidWalletTransactionObservation observation = Create(
			inputs: [first, second],
			ownedOutputs: []);

		Assert.Equal([first, second], observation.GetInputs());
		Assert.Equal(2, observation.InputCount);
		Assert.Equal(0, observation.OwnedOutputCount);
		Assert.Empty(observation.GetOwnedOutputs());
	}

	[Fact]
	public void RejectsZeroAndDuplicateInputsButAllowsCrossObservationReuse()
	{
		LiquidOutPoint input = OutPoint('a', 0);

		Assert.Throws<ArgumentException>(() => Create(inputs: []));
		Assert.Throws<ArgumentException>(() => Create(inputs: [input, input]));

		LiquidWalletTransactionObservation first = Create(inputs: [input]);
		LiquidWalletTransactionObservation second = Create(inputs: [input]);
		Assert.Equal(input, Assert.Single(first.GetInputs()));
		Assert.Equal(input, Assert.Single(second.GetInputs()));
	}

	[Fact]
	public void EnforcesExactInputCapBeforeTouchingCollectionElements()
	{
		var exactInputs = new GeneratedReadOnlyList<LiquidOutPoint>(
			MaxInputCount,
			index => OutPoint('a', checked((uint)index)));

		LiquidWalletTransactionObservation exact = Create(inputs: exactInputs);

		Assert.Equal(MaxInputCount, exact.InputCount);

		var oversizedInputs = new IndexingForbiddenReadOnlyList<LiquidOutPoint>(MaxInputCount + 1);
		var untouchedOutputs = new IndexingForbiddenReadOnlyList<LiquidOwnedOutputObservation>(0);
		Assert.Throws<ArgumentOutOfRangeException>(() => Create(
			inputs: oversizedInputs,
			ownedOutputs: untouchedOutputs));
		Assert.Equal(0, oversizedInputs.ElementAccessCount);
		Assert.Equal(0, untouchedOutputs.ElementAccessCount);
	}

	[Fact]
	public void EnforcesExactOwnedOutputCapBeforeTouchingEitherCollection()
	{
		var exactOutputs = new GeneratedReadOnlyList<LiquidOwnedOutputObservation>(
			MaxOwnedOutputCount,
			index => ExternalOutput(checked((uint)index)));

		LiquidWalletTransactionObservation exact = Create(ownedOutputs: exactOutputs);

		Assert.Equal(MaxOwnedOutputCount, exact.OwnedOutputCount);

		var untouchedInputs = new IndexingForbiddenReadOnlyList<LiquidOutPoint>(1);
		var oversizedOutputs =
			new IndexingForbiddenReadOnlyList<LiquidOwnedOutputObservation>(MaxOwnedOutputCount + 1);
		Assert.Throws<ArgumentOutOfRangeException>(() => Create(
			inputs: untouchedInputs,
			ownedOutputs: oversizedOutputs));
		Assert.Equal(0, untouchedInputs.ElementAccessCount);
		Assert.Equal(0, oversizedOutputs.ElementAccessCount);
	}

	[Fact]
	public void RejectsNullCollectionsAndElements()
	{
		Assert.Throws<ArgumentNullException>(() => LiquidWalletTransactionObservation.Create(
			TransactionId,
			WitnessBinding,
			null!,
			[]));
		Assert.Throws<ArgumentNullException>(() => LiquidWalletTransactionObservation.Create(
			TransactionId,
			WitnessBinding,
			[OutPoint('a', 0)],
			null!));
		Assert.Throws<ArgumentNullException>(() => Create(inputs: [null!]));
		Assert.Throws<ArgumentNullException>(() => Create(ownedOutputs: [null!]));
	}

	[Fact]
	public void RejectsOutputIdentityBindingDuplicateAndOrderMismatches()
	{
		byte[] changedTransactionId = [.. TransactionId];
		changedTransactionId[0] ^= 1;
		byte[] changedWitnessBinding = [.. WitnessBinding];
		changedWitnessBinding[0] ^= 1;
		LiquidOwnedOutputObservation transactionMismatch = ExternalOutput(
			transactionId: changedTransactionId);
		LiquidOwnedOutputObservation bindingMismatch = ExternalOutput(
			witnessBinding: changedWitnessBinding);
		LiquidOwnedOutputObservation first = ExternalOutput(0);
		LiquidOwnedOutputObservation second = InternalOutput(1);

		Assert.Throws<ArgumentException>(() => Create(ownedOutputs: [transactionMismatch]));
		Assert.Throws<ArgumentException>(() => Create(ownedOutputs: [bindingMismatch]));
		Assert.Throws<ArgumentException>(() => Create(ownedOutputs: [first, first]));
		Assert.Throws<ArgumentException>(() => Create(ownedOutputs: [second, first]));
	}

	[Fact]
	public void AcceptsAndPreservesAllZeroWitnessBinding()
	{
		byte[] zeroBinding = new byte[LiquidTransactionWitnessBinding.ByteLength];
		LiquidOwnedOutputObservation output = ExternalOutput(witnessBinding: zeroBinding);

		LiquidWalletTransactionObservation observation = Create(
			witnessBinding: zeroBinding,
			ownedOutputs: [output]);

		zeroBinding.AsSpan().Fill(0x5a);
		Assert.Equal(new byte[LiquidTransactionWitnessBinding.ByteLength], observation.GetTransactionWitnessBinding());
	}

	[Fact]
	public void RejectsZeroOrWrongLengthTransactionIdentityAndWrongLengthBinding()
	{
		Assert.Throws<ArgumentException>(() => Create(transactionId: new byte[32]));
		Assert.Throws<ArgumentException>(() => Create(transactionId: new byte[31]));
		Assert.Throws<ArgumentException>(() => Create(transactionId: new byte[33]));
		Assert.Throws<ArgumentException>(() => Create(witnessBinding: new byte[31]));
		Assert.Throws<ArgumentException>(() => Create(witnessBinding: new byte[33]));
	}

	[Fact]
	public void LateOutputMismatchReturnsNoPartialObservation()
	{
		byte[] changedBinding = [.. WitnessBinding];
		changedBinding[^1] ^= 1;
		LiquidWalletTransactionObservation? observation = null;

		Assert.Throws<ArgumentException>(() => observation = Create(
			ownedOutputs:
			[
				ExternalOutput(0),
				InternalOutput(1, witnessBinding: changedBinding),
			]));

		Assert.Null(observation);
	}

	[Fact]
	public void CollectionsStayImmutableAcrossSourceMutationCastsAndRefetch()
	{
		LiquidOutPoint input = ParseOutPoint(FirstInputHex);
		LiquidOwnedOutputObservation output = ExternalOutput();
		var sourceInputs = new List<LiquidOutPoint> { input };
		var sourceOutputs = new List<LiquidOwnedOutputObservation> { output };
		LiquidWalletTransactionObservation observation = Create(
			inputs: sourceInputs,
			ownedOutputs: sourceOutputs);
		LiquidWalletTransactionObservation expected = Create(inputs: [input], ownedOutputs: [output]);
		int hashBefore = observation.GetHashCode();

		sourceInputs[0] = OutPoint('b', 1);
		sourceOutputs[0] = InternalOutput(1);
		sourceInputs.Clear();
		sourceOutputs.Clear();

		IReadOnlyList<LiquidOutPoint> inputs = observation.GetInputs();
		IReadOnlyList<LiquidOwnedOutputObservation> outputs = observation.GetOwnedOutputs();
		byte[] transactionIdBytes = observation.GetTransactionIdConsensusBytes();
		byte[] witnessBindingBytes = observation.GetTransactionWitnessBinding();
		AssertReadOnlyThroughAllCasts(inputs, OutPoint('c', 2));
		AssertReadOnlyThroughAllCasts(outputs, InternalOutput(1));
		transactionIdBytes.AsSpan().Clear();
		witnessBindingBytes.AsSpan().Clear();

		Assert.Equal(input, Assert.Single(observation.GetInputs()));
		Assert.Equal(output, Assert.Single(observation.GetOwnedOutputs()));
		Assert.Equal(TransactionId, observation.GetTransactionIdConsensusBytes());
		Assert.Equal(WitnessBinding, observation.GetTransactionWitnessBinding());
		Assert.Equal(1, observation.InputCount);
		Assert.Equal(1, observation.OwnedOutputCount);
		Assert.Equal(expected, observation);
		Assert.Equal(hashBefore, observation.GetHashCode());
		Assert.Equal(expected.GetHashCode(), observation.GetHashCode());
	}

	[Fact]
	public void EqualityAndHashBindEveryRetainedFieldAndOrder()
	{
		LiquidOutPoint firstInput = OutPoint('a', 0);
		LiquidOutPoint secondInput = OutPoint('b', 1);
		LiquidOwnedOutputObservation firstOutput = ExternalOutput(0);
		LiquidOwnedOutputObservation secondOutput = InternalOutput(1);
		LiquidWalletTransactionObservation baseline = Create(
			inputs: [firstInput, secondInput],
			ownedOutputs: [firstOutput, secondOutput]);
		LiquidWalletTransactionObservation equal = Create(
			inputs: [firstInput, secondInput],
			ownedOutputs: [firstOutput, secondOutput]);
		byte[] changedTransactionId = [.. TransactionId];
		changedTransactionId[0] ^= 1;
		byte[] changedBinding = [.. WitnessBinding];
		changedBinding[0] ^= 1;

		Assert.Equal(baseline, equal);
		Assert.Equal(baseline.GetHashCode(), equal.GetHashCode());
		Assert.NotEqual(baseline, Create(
			transactionId: changedTransactionId,
			inputs: [firstInput, secondInput],
			ownedOutputs:
			[
				ExternalOutput(0, transactionId: changedTransactionId),
				InternalOutput(1, transactionId: changedTransactionId),
			]));
		Assert.NotEqual(baseline, Create(
			witnessBinding: changedBinding,
			inputs: [firstInput, secondInput],
			ownedOutputs:
			[
				ExternalOutput(0, witnessBinding: changedBinding),
				InternalOutput(1, witnessBinding: changedBinding),
			]));
		Assert.NotEqual(baseline, Create(
			inputs: [secondInput, firstInput],
			ownedOutputs: [firstOutput, secondOutput]));
		Assert.NotEqual(baseline, Create(
			inputs: [firstInput, secondInput],
			ownedOutputs: [ExternalOutput(0), InternalOutput(2)]));
	}

	[Fact]
	public void StringsAndErrorsRevealNoObservationFacts()
	{
		LiquidOwnedOutputObservation mismatched = ExternalOutput(witnessBinding: new byte[32]);
		var exception = Assert.Throws<ArgumentException>(() => Create(ownedOutputs: [mismatched]));
		LiquidWalletTransactionObservation observation = Create();

		Assert.Equal(nameof(LiquidWalletTransactionObservation), observation.ToString());
		foreach (string text in new[] { observation.ToString(), exception.ToString() })
		{
			Assert.DoesNotContain(TransactionIdHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(WitnessBindingHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(FirstInputHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(ExternalScriptHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(ExternalSpendPublicKeyHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(ExternalAssetHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("900", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ChangedTypeSurfacesContainOnlyExplicitObservationTypes()
	{
		AssertExactTransactionObservationSurface();
		AssertExactOwnedOutputObservationSurface();

		Type[] changedTypes =
		[
			typeof(LiquidWalletTransactionObservation),
			typeof(LiquidOwnedOutputObservation),
		];

		foreach (Type changedType in changedTypes)
		{
			IEnumerable<Type> signatureTypes = changedType
				.GetConstructors(BindingFlags.Instance | BindingFlags.Static |
					BindingFlags.Public | BindingFlags.NonPublic)
				.SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
				.Concat(changedType
					.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
						BindingFlags.Public | BindingFlags.NonPublic)
					.SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
						.Append(method.ReturnType)))
				.Concat(changedType
					.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
						BindingFlags.Public | BindingFlags.NonPublic)
					.SelectMany(property => property.GetIndexParameters()
						.Select(parameter => parameter.ParameterType)
						.Append(property.PropertyType)))
				.Concat(changedType
					.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
						BindingFlags.Public | BindingFlags.NonPublic)
					.Select(field => field.FieldType))
				.Concat(changedType
					.GetEvents(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
						BindingFlags.Public | BindingFlags.NonPublic)
					.Select(@event => @event.EventHandlerType!))
				.Concat(changedType.GetInterfaces());

			Assert.All(signatureTypes, type => Assert.True(
				IsAllowedObservationSurfaceType(type),
				$"{changedType.FullName} exposes unapproved type {type.FullName ?? type.Name}."));
			Assert.Empty(changedType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
		}
	}

	private static void AssertExactTransactionObservationSurface()
	{
		Type type = typeof(LiquidWalletTransactionObservation);
		AssertExactChangedTypeShape(type);
		AssertExactSet(
			DeclaredFieldSurface(type),
			[
				FieldSurface("private", true, false, true, typeof(int), "MaxInputCount"),
				FieldSurface("private", true, false, true, typeof(int), "MaxOwnedOutputCount"),
				FieldSurface("private", false, true, false, typeof(LiquidTransactionId), "_transactionId"),
				FieldSurface("private", false, true, false, typeof(LiquidTransactionWitnessBinding), "_transactionWitnessBinding"),
				FieldSurface("private", false, true, false, typeof(LiquidOutPoint[]), "_inputs"),
				FieldSurface("private", false, true, false, typeof(LiquidOwnedOutputObservation[]), "_ownedOutputs"),
			]);
		AssertExactSet(
			DeclaredConstructorSurface(type),
			[
				ConstructorSurface(
					"private",
					false,
					typeof(LiquidTransactionId),
					typeof(LiquidTransactionWitnessBinding),
					typeof(LiquidOutPoint[]),
					typeof(LiquidOwnedOutputObservation[])),
			]);
		AssertExactSet(
			DeclaredMethodSurface(type),
			[
				MethodSurface("public", false, "get_InputCount", typeof(int)),
				MethodSurface("public", false, "get_OwnedOutputCount", typeof(int)),
				MethodSurface(
					"public", true, "Create", type,
					typeof(ReadOnlySpan<byte>),
					typeof(ReadOnlySpan<byte>),
					typeof(IReadOnlyList<LiquidOutPoint>),
					typeof(IReadOnlyList<LiquidOwnedOutputObservation>)),
				MethodSurface("public", false, "GetTransactionIdConsensusBytes", typeof(byte[])),
				MethodSurface("public", false, "GetTransactionWitnessBinding", typeof(byte[])),
				MethodSurface("public", false, "GetInputs", typeof(IReadOnlyList<LiquidOutPoint>)),
				MethodSurface("public", false, "GetOwnedOutputs", typeof(IReadOnlyList<LiquidOwnedOutputObservation>)),
				FinalVirtualMethodSurface("public", false, "Equals", typeof(bool), type),
				VirtualMethodSurface("public", false, "Equals", typeof(bool), typeof(object)),
				VirtualMethodSurface("public", false, "GetHashCode", typeof(int)),
				VirtualMethodSurface("public", false, "ToString", typeof(string)),
			]);
		AssertExactSet(
			DeclaredPropertySurface(type),
			[
				PropertySurface("InputCount", typeof(int), "public", null),
				PropertySurface("OwnedOutputCount", typeof(int), "public", null),
			]);
		AssertExactSet(
			type.GetInterfaces().Select(TypeKey),
			[TypeKey(typeof(IEquatable<LiquidWalletTransactionObservation>))]);
		Assert.Empty(type.GetEvents(DeclaredMemberFlags));
		Assert.Empty(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
	}

	private static void AssertExactOwnedOutputObservationSurface()
	{
		Type type = typeof(LiquidOwnedOutputObservation);
		AssertExactChangedTypeShape(type);
		AssertExactSet(
			DeclaredFieldSurface(type),
			[
				FieldSurface("public", true, false, true, typeof(uint), "MaxDerivationIndex"),
				FieldSurface("private", false, true, false, typeof(LiquidOutPoint), "_outPoint"),
				FieldSurface("private", false, true, false, typeof(LiquidTransactionWitnessBinding), "_transactionWitnessBinding"),
				FieldSurface("private", false, true, false, typeof(byte[]), "_scriptPubKey"),
				FieldSurface("private", false, true, false, typeof(LiquidSpendKeyReference), "_spendKey"),
				FieldSurface("private", false, true, false, typeof(LiquidBlindingPublicKey), "_blindingPublicKey"),
				FieldSurface("private", false, true, false, typeof(LiquidAssetId), "_assetId"),
				FieldSurface("private", false, true, false, typeof(long), "<Value>k__BackingField"),
			]);
		AssertExactSet(
			DeclaredConstructorSurface(type),
			[
				ConstructorSurface(
					"private",
					false,
					typeof(LiquidOutPoint),
					typeof(LiquidTransactionWitnessBinding),
					typeof(byte[]),
					typeof(LiquidSpendKeyReference),
					typeof(LiquidBlindingPublicKey),
					typeof(LiquidAssetId),
					typeof(long)),
			]);
		AssertExactSet(
			DeclaredMethodSurface(type),
			[
				MethodSurface("public", false, "get_OutputIndex", typeof(uint)),
				MethodSurface("public", false, "get_Branch", typeof(LiquidKeyBranch)),
				MethodSurface("public", false, "get_DerivationIndex", typeof(uint)),
				MethodSurface("public", false, "get_Value", typeof(long)),
				MethodSurface(
					"public", true, "Create", type,
					typeof(ReadOnlySpan<byte>),
					typeof(uint),
					typeof(ReadOnlySpan<byte>),
					typeof(ReadOnlySpan<byte>),
					typeof(ReadOnlySpan<byte>),
					typeof(ReadOnlySpan<byte>),
					typeof(LiquidKeyBranch),
					typeof(uint),
					typeof(ReadOnlySpan<byte>),
					typeof(ulong)),
				MethodSurface("public", false, "GetTransactionIdConsensusBytes", typeof(byte[])),
				MethodSurface("public", false, "GetTransactionWitnessBinding", typeof(byte[])),
				MethodSurface("public", false, "GetScriptPubKey", typeof(byte[])),
				MethodSurface("public", false, "GetSpendPublicKey", typeof(byte[])),
				MethodSurface("public", false, "GetBlindingPublicKey", typeof(byte[])),
				MethodSurface("public", false, "GetAssetIdConsensusBytes", typeof(byte[])),
				MethodSurface("assembly", false, "MatchesTransactionId", typeof(bool), typeof(LiquidTransactionId)),
				MethodSurface(
					"assembly", false, "MatchesTransactionWitnessBinding", typeof(bool),
					typeof(LiquidTransactionWitnessBinding)),
				FinalVirtualMethodSurface("public", false, "Equals", typeof(bool), type),
				VirtualMethodSurface("public", false, "Equals", typeof(bool), typeof(object)),
				VirtualMethodSurface("public", false, "GetHashCode", typeof(int)),
				VirtualMethodSurface("public", false, "ToString", typeof(string)),
			]);
		AssertExactSet(
			DeclaredPropertySurface(type),
			[
				PropertySurface("OutputIndex", typeof(uint), "public", null),
				PropertySurface("Branch", typeof(LiquidKeyBranch), "public", null),
				PropertySurface("DerivationIndex", typeof(uint), "public", null),
				PropertySurface("Value", typeof(long), "public", null),
			]);
		AssertExactSet(
			type.GetInterfaces().Select(TypeKey),
			[TypeKey(typeof(IEquatable<LiquidOwnedOutputObservation>))]);
		Assert.Empty(type.GetEvents(DeclaredMemberFlags));
		Assert.Empty(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
	}

	[Fact]
	public void EveryChangedTypeMethodBodyCallsOnlyExplicitObservationDependencies()
	{
		Type[] changedTypes =
		[
			typeof(LiquidWalletTransactionObservation),
			typeof(LiquidOwnedOutputObservation),
		];
		var unexpectedReferences = new List<string>();

		foreach (Type changedType in changedTypes)
		{
			MethodBase[] declaredBodies = changedType
				.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
					BindingFlags.Public | BindingFlags.NonPublic)
				.Cast<MethodBase>()
				.Concat(changedType.GetConstructors(
					BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				.ToArray();
			Assert.NotEmpty(declaredBodies);

			foreach (MethodBase method in declaredBodies)
			{
				Assert.Equal(
					(MethodAttributes)0,
					method.Attributes & MethodAttributes.PinvokeImpl);
				MethodImplAttributes forbiddenImplementationFlags =
					MethodImplAttributes.InternalCall |
					MethodImplAttributes.Runtime |
					MethodImplAttributes.Native |
					MethodImplAttributes.Unmanaged;
				Assert.Equal(
					(MethodImplAttributes)0,
					method.MethodImplementationFlags & forbiddenImplementationFlags);
				Assert.NotNull(method.GetMethodBody());
				Assert.DoesNotContain(ReadOpcodes(method), opcode =>
					opcode == OpCodes.Calli ||
					opcode == OpCodes.Ldftn ||
					opcode == OpCodes.Ldvirtftn ||
					opcode == OpCodes.Jmp ||
					opcode.OperandType == OperandType.InlineSig);

				ReferencedCall[] referencedMembers = ReferencedMembers(method).ToArray();
				foreach (ReferencedCall referencedCall in referencedMembers)
				{
					MemberInfo referencedMember = referencedCall.Member;
					if (!IsAllowedObservationCallMember(method, referencedMember))
					{
						unexpectedReferences.Add(
							$"{CallMemberKey(method)} -> {CallMemberKey(referencedMember)}");
					}
				}

				AssertRequiredObservationCalls(method, referencedMembers);
			}
		}

		Assert.True(
			unexpectedReferences.Count == 0,
			"Only exact approved observation dependencies may be called:\n" +
			string.Join("\n", unexpectedReferences));
	}

	private static LiquidWalletTransactionObservation Create(
		byte[]? transactionId = null,
		byte[]? witnessBinding = null,
		IReadOnlyList<LiquidOutPoint>? inputs = null,
		IReadOnlyList<LiquidOwnedOutputObservation>? ownedOutputs = null) =>
		LiquidWalletTransactionObservation.Create(
			transactionId ?? TransactionId,
			witnessBinding ?? WitnessBinding,
			inputs ?? [ParseOutPoint(FirstInputHex)],
			ownedOutputs ?? []);

	private static LiquidOwnedOutputObservation ExternalOutput(
		uint outputIndex = 0,
		byte[]? transactionId = null,
		byte[]? witnessBinding = null) =>
		LiquidOwnedOutputObservation.Create(
			transactionId ?? TransactionId,
			outputIndex,
			witnessBinding ?? WitnessBinding,
			ExternalScript,
			ExternalSpendPublicKey,
			ExternalBlindingPublicKey,
			LiquidKeyBranch.External,
			0,
			ExternalAsset,
			900);

	private static LiquidOwnedOutputObservation InternalOutput(
		uint outputIndex = 1,
		byte[]? transactionId = null,
		byte[]? witnessBinding = null) =>
		LiquidOwnedOutputObservation.Create(
			transactionId ?? TransactionId,
			outputIndex,
			witnessBinding ?? WitnessBinding,
			InternalScript,
			InternalSpendPublicKey,
			InternalBlindingPublicKey,
			LiquidKeyBranch.Internal,
			1,
			InternalAsset,
			2_000);

	private static LiquidOutPoint ParseOutPoint(string consensusHex) =>
		LiquidOutPoint.ParseSpendableConsensusBytes(Convert.FromHexString(consensusHex));

	private static LiquidOutPoint OutPoint(char transactionIdDigit, uint outputIndex) =>
		LiquidOutPoint.CreateSpendable(
			LiquidTransactionId.ParseRpcHex(new string(transactionIdDigit, 64)),
			outputIndex);

	private static void AssertOutput(
		LiquidOwnedOutputObservation output,
		uint outputIndex,
		LiquidKeyBranch branch,
		uint derivationIndex,
		long value,
		byte[] asset,
		byte[] script,
		byte[] spendPublicKey,
		byte[] blindingPublicKey)
	{
		Assert.Equal(TransactionId, output.GetTransactionIdConsensusBytes());
		Assert.Equal(WitnessBinding, output.GetTransactionWitnessBinding());
		Assert.Equal(outputIndex, output.OutputIndex);
		Assert.Equal(branch, output.Branch);
		Assert.Equal(derivationIndex, output.DerivationIndex);
		Assert.Equal(value, output.Value);
		Assert.Equal(asset, output.GetAssetIdConsensusBytes());
		Assert.Equal(script, output.GetScriptPubKey());
		Assert.Equal(spendPublicKey, output.GetSpendPublicKey());
		Assert.Equal(blindingPublicKey, output.GetBlindingPublicKey());
	}

	private static void AssertReadOnlyThroughAllCasts<T>(IReadOnlyList<T> values, T replacement)
	{
		var genericList = Assert.IsAssignableFrom<IList<T>>(values);
		var genericCollection = Assert.IsAssignableFrom<ICollection<T>>(values);
		var nonGenericList = Assert.IsAssignableFrom<IList>(values);
		Assert.True(genericList.IsReadOnly);
		Assert.True(genericCollection.IsReadOnly);
		Assert.True(nonGenericList.IsReadOnly);
		Assert.True(nonGenericList.IsFixedSize);
		Assert.Throws<NotSupportedException>(() => genericList[0] = replacement);
		Assert.Throws<NotSupportedException>(() => genericList.Add(replacement));
		Assert.Throws<NotSupportedException>(() => genericList.RemoveAt(0));
		Assert.Throws<NotSupportedException>(() => genericCollection.Add(replacement));
		Assert.Throws<NotSupportedException>(() => genericCollection.Remove(replacement));
		Assert.Throws<NotSupportedException>(() => genericCollection.Clear());
		Assert.Throws<NotSupportedException>(() => nonGenericList[0] = replacement);
		Assert.Throws<NotSupportedException>(() => nonGenericList.Add(replacement));
		Assert.Throws<NotSupportedException>(() => nonGenericList.RemoveAt(0));
		Assert.Throws<NotSupportedException>(() => nonGenericList.Clear());
	}

	private static bool ContainsType(Type candidate, Type expected) =>
		candidate == expected ||
		(candidate.HasElementType && ContainsType(candidate.GetElementType()!, expected)) ||
		(candidate.IsGenericType && candidate.GetGenericArguments().Any(argument => ContainsType(argument, expected)));

	private const BindingFlags DeclaredMemberFlags =
		BindingFlags.DeclaredOnly |
		BindingFlags.Instance |
		BindingFlags.Static |
		BindingFlags.Public |
		BindingFlags.NonPublic;

	private static void AssertExactSet(IEnumerable<string> actual, IEnumerable<string> expected)
	{
		string[] actualValues = actual.Order(StringComparer.Ordinal).ToArray();
		string[] expectedValues = expected.Order(StringComparer.Ordinal).ToArray();
		Assert.True(
			actualValues.SequenceEqual(expectedValues, StringComparer.Ordinal),
			"Declared surface differs.\nMissing:\n" +
			string.Join("\n", expectedValues.Except(actualValues, StringComparer.Ordinal)) +
			"\nUnexpected:\n" +
			string.Join("\n", actualValues.Except(expectedValues, StringComparer.Ordinal)));
	}

	private static void AssertExactChangedTypeShape(Type type)
	{
		Assert.True(type.IsNotPublic);
		Assert.False(type.IsNested);
		Assert.True(type.IsSealed);
		Assert.False(type.IsAbstract);
		Assert.False(type.IsGenericType);
		Assert.False(type.IsGenericTypeDefinition);
		Assert.Equal(typeof(object), type.BaseType);
		Assert.False(type.IsByRefLike);
		Assert.False(type.IsPointer);
	}

	private static IEnumerable<string> DeclaredFieldSurface(Type type) =>
		type.GetFields(DeclaredMemberFlags).Select(field => FieldSurface(
			Visibility(field),
			field.IsStatic,
			field.IsInitOnly,
			field.IsLiteral,
			field.FieldType,
			field.Name));

	private static IEnumerable<string> DeclaredConstructorSurface(Type type) =>
		type.GetConstructors(DeclaredMemberFlags).Select(constructor => ConstructorSurface(
			Visibility(constructor),
			constructor.IsStatic,
			constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray()));

	private static IEnumerable<string> DeclaredMethodSurface(Type type) =>
		type.GetMethods(DeclaredMemberFlags).Select(method => MethodSurfaceCore(
			Visibility(method),
			method.IsStatic,
			method.IsVirtual,
			method.IsFinal,
			method.IsAbstract,
			method.CallingConvention,
			method.GetGenericArguments(),
			method.Name,
			method.ReturnType,
			method.GetParameters().Select(parameter => parameter.ParameterType).ToArray()));

	private static IEnumerable<string> DeclaredPropertySurface(Type type) =>
		type.GetProperties(DeclaredMemberFlags).Select(property => PropertySurface(
			property.Name,
			property.PropertyType,
			property.GetMethod is null ? null : Visibility(property.GetMethod),
			property.SetMethod is null ? null : Visibility(property.SetMethod)));

	private static string FieldSurface(
		string visibility,
		bool isStatic,
		bool isReadOnly,
		bool isLiteral,
		Type fieldType,
		string name) =>
		$"field|{visibility}|static={isStatic}|readonly={isReadOnly}|literal={isLiteral}|{TypeKey(fieldType)}|{name}";

	private static string ConstructorSurface(
		string visibility,
		bool isStatic,
		params Type[] parameterTypes) =>
		$"constructor|{visibility}|static={isStatic}|({string.Join(",", parameterTypes.Select(TypeKey))})";

	private static string MethodSurface(
		string visibility,
		bool isStatic,
		string name,
		Type returnType,
		params Type[] parameterTypes) =>
		MethodSurfaceCore(
			visibility,
			isStatic,
			false,
			false,
			false,
			isStatic ? CallingConventions.Standard : CallingConventions.Standard | CallingConventions.HasThis,
			[],
			name,
			returnType,
			parameterTypes);

	private static string VirtualMethodSurface(
		string visibility,
		bool isStatic,
		string name,
		Type returnType,
		params Type[] parameterTypes) =>
		MethodSurfaceCore(
			visibility,
			isStatic,
			true,
			false,
			false,
			isStatic ? CallingConventions.Standard : CallingConventions.Standard | CallingConventions.HasThis,
			[],
			name,
			returnType,
			parameterTypes);

	private static string FinalVirtualMethodSurface(
		string visibility,
		bool isStatic,
		string name,
		Type returnType,
		params Type[] parameterTypes) =>
		MethodSurfaceCore(
			visibility,
			isStatic,
			true,
			true,
			false,
			isStatic ? CallingConventions.Standard : CallingConventions.Standard | CallingConventions.HasThis,
			[],
			name,
			returnType,
			parameterTypes);

	private static string MethodSurfaceCore(
		string visibility,
		bool isStatic,
		bool isVirtual,
		bool isFinal,
		bool isAbstract,
		CallingConventions callingConvention,
		IReadOnlyList<Type> genericArguments,
		string name,
		Type returnType,
		IReadOnlyList<Type> parameterTypes) =>
		$"method|{visibility}|static={isStatic}|virtual={isVirtual}|final={isFinal}|" +
		$"abstract={isAbstract}|calling={callingConvention}|generic={GenericShape(genericArguments)}|" +
		$"{name}|{TypeKey(returnType)}|({string.Join(",", parameterTypes.Select(TypeKey))})";

	private static string GenericShape(IReadOnlyList<Type> genericArguments) =>
		string.Join(
			";",
			genericArguments.Select(argument =>
				argument.IsGenericParameter
					? $"{argument.GenericParameterAttributes}:" +
						string.Join(",", argument.GetGenericParameterConstraints().Select(TypeKey).Order(StringComparer.Ordinal))
					: TypeKey(argument)));

	private static string PropertySurface(
		string name,
		Type propertyType,
		string? getterVisibility,
		string? setterVisibility) =>
		$"property|{name}|{TypeKey(propertyType)}|get={getterVisibility ?? "none"}|set={setterVisibility ?? "none"}";

	private static string Visibility(FieldInfo field) =>
		field.IsPublic ? "public" :
		field.IsAssembly ? "assembly" :
		field.IsFamily ? "family" :
		field.IsFamilyOrAssembly ? "family-or-assembly" :
		field.IsFamilyAndAssembly ? "family-and-assembly" :
		field.IsPrivate ? "private" :
		"unknown";

	private static string Visibility(MethodBase method) =>
		method.IsPublic ? "public" :
		method.IsAssembly ? "assembly" :
		method.IsFamily ? "family" :
		method.IsFamilyOrAssembly ? "family-or-assembly" :
		method.IsFamilyAndAssembly ? "family-and-assembly" :
		method.IsPrivate ? "private" :
		"unknown";

	private static string TypeKey(Type type)
	{
		if (type.IsArray)
		{
			return $"{TypeKey(type.GetElementType()!)}[]";
		}
		if (type.IsByRef)
		{
			return $"{TypeKey(type.GetElementType()!)}&";
		}
		if (type.IsPointer)
		{
			return $"{TypeKey(type.GetElementType()!)}*";
		}
		if (type.IsGenericType)
		{
			string definition = type.GetGenericTypeDefinition().FullName ?? type.GetGenericTypeDefinition().Name;
			return $"{definition}<{string.Join(",", type.GetGenericArguments().Select(TypeKey))}>";
		}

		return type.FullName ?? type.Name;
	}

	private static bool IsAllowedObservationSurfaceType(Type type)
	{
		if (type.HasElementType)
		{
			return !type.IsPointer && !type.IsByRef && !type.IsByRefLike &&
				IsAllowedObservationSurfaceType(type.GetElementType()!);
		}
		if (type.IsGenericType)
		{
			Type genericDefinition = type.GetGenericTypeDefinition();
			if (genericDefinition != typeof(IEquatable<>) &&
				genericDefinition != typeof(IReadOnlyList<>) &&
				genericDefinition != typeof(ReadOnlySpan<>))
			{
				return false;
			}

			return type.GetGenericArguments().All(IsAllowedObservationSurfaceType);
		}

		return type == typeof(void) ||
			type == typeof(bool) ||
			type == typeof(byte) ||
			type == typeof(int) ||
			type == typeof(uint) ||
			type == typeof(long) ||
			type == typeof(ulong) ||
			type == typeof(string) ||
			type == typeof(object) ||
			type == typeof(LiquidKeyBranch) ||
			type == typeof(LiquidAssetId) ||
			type == typeof(LiquidBlindingPublicKey) ||
			type == typeof(LiquidOutPoint) ||
			type == typeof(LiquidSpendKeyReference) ||
			type == typeof(LiquidTransactionId) ||
			type == typeof(LiquidTransactionWitnessBinding) ||
			type == typeof(LiquidOwnedOutputObservation) ||
			type == typeof(LiquidWalletTransactionObservation);
	}

	private static bool IsAllowedObservationCallMember(MethodBase caller, MemberInfo member)
	{
		if (member is not MethodBase method || member.DeclaringType is null)
		{
			return false;
		}
		if (method.GetParameters().Any(parameter => ContainsInteropSurface(parameter.ParameterType)) ||
			method is MethodInfo methodInfo && ContainsInteropSurface(methodInfo.ReturnType))
		{
			return false;
		}


		return AllowedCallKeys(caller).Contains(CallMemberKey(member), StringComparer.Ordinal);
	}

	private static IReadOnlyList<string> AllowedCallKeys(MethodBase caller)
	{
		Type callerType = caller.DeclaringType!;
		string callerName = caller.Name;
		Type[] callerParameters = caller.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

		if (callerType == typeof(LiquidWalletTransactionObservation))
		{
			if (callerName == ".ctor")
			{
				return [CallKey(typeof(object), false, ".ctor", typeof(void))];
			}
			if (callerName is "get_InputCount" or "get_OwnedOutputCount" or "ToString")
			{
				return [];
			}
			if (callerName == "Create")
			{
				return
				[
					CallKey(typeof(ArgumentNullException), true, "ThrowIfNull", typeof(void), typeof(object), typeof(string)),
					CallKey(typeof(IReadOnlyCollection<LiquidOutPoint>), false, "get_Count", typeof(int)),
					CallKey(typeof(IReadOnlyCollection<LiquidOwnedOutputObservation>), false, "get_Count", typeof(int)),
					CallKey(typeof(ArgumentException), false, ".ctor", typeof(void), typeof(string), typeof(string)),
					CallKey(typeof(ArgumentOutOfRangeException), false, ".ctor", typeof(void), typeof(string), typeof(string)),
					CallKey(typeof(LiquidTransactionId), true, "ParseConsensusBytes", typeof(LiquidTransactionId), typeof(ReadOnlySpan<byte>), typeof(string)),
					CallKey(typeof(LiquidTransactionId), false, "get_IsZero", typeof(bool)),
					CallKey(typeof(LiquidTransactionWitnessBinding), true, "Create", typeof(LiquidTransactionWitnessBinding), typeof(ReadOnlySpan<byte>)),
					CallKey(typeof(HashSet<LiquidOutPoint>), false, ".ctor", typeof(void)),
					CallKey(typeof(IReadOnlyList<LiquidOutPoint>), false, "get_Item", typeof(LiquidOutPoint), typeof(int)),
					CallKey(typeof(HashSet<LiquidOutPoint>), false, "Add", typeof(bool), typeof(LiquidOutPoint)),
					CallKey(typeof(IReadOnlyList<LiquidOwnedOutputObservation>), false, "get_Item", typeof(LiquidOwnedOutputObservation), typeof(int)),
					CallKey(typeof(LiquidOwnedOutputObservation), false, "MatchesTransactionId", typeof(bool), typeof(LiquidTransactionId)),
					CallKey(typeof(LiquidOwnedOutputObservation), false, "MatchesTransactionWitnessBinding", typeof(bool), typeof(LiquidTransactionWitnessBinding)),
					CallKey(typeof(LiquidOwnedOutputObservation), false, "get_OutputIndex", typeof(uint)),
					CallKey(
						typeof(LiquidWalletTransactionObservation),
						false,
						".ctor",
						typeof(void),
						typeof(LiquidTransactionId),
						typeof(LiquidTransactionWitnessBinding),
						typeof(LiquidOutPoint[]),
						typeof(LiquidOwnedOutputObservation[])),
				];
			}
			if (callerName == "GetTransactionIdConsensusBytes")
			{
				return [CallKey(typeof(LiquidTransactionId), false, "ToConsensusBytes", typeof(byte[]))];
			}
			if (callerName == "GetTransactionWitnessBinding")
			{
				return [CallKey(typeof(LiquidTransactionWitnessBinding), false, "GetBytes", typeof(byte[]))];
			}
			if (callerName == "GetInputs")
			{
				return CopyCallKeys<LiquidOutPoint>();
			}
			if (callerName == "GetOwnedOutputs")
			{
				return CopyCallKeys<LiquidOwnedOutputObservation>();
			}
			if (callerName == "Equals" && callerParameters.SequenceEqual([typeof(LiquidWalletTransactionObservation)]))
			{
				return
				[
					CallKey(typeof(LiquidTransactionId), true, "op_Equality", typeof(bool), typeof(LiquidTransactionId), typeof(LiquidTransactionId)),
					CallKey(typeof(LiquidTransactionWitnessBinding), false, "Equals", typeof(bool), typeof(LiquidTransactionWitnessBinding)),
					.. SequenceEqualityCallKeys<LiquidOutPoint>(),
					.. SequenceEqualityCallKeys<LiquidOwnedOutputObservation>(),
				];
			}
			if (callerName == "Equals" && callerParameters.SequenceEqual([typeof(object)]))
			{
				return [CallKey(typeof(LiquidWalletTransactionObservation), false, "Equals", typeof(bool), typeof(LiquidWalletTransactionObservation))];
			}
			if (callerName == "GetHashCode")
			{
				return
				[
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidTransactionId)], typeof(void), typeof(LiquidTransactionId)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidTransactionWitnessBinding)], typeof(void), typeof(LiquidTransactionWitnessBinding)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidOutPoint)], typeof(void), typeof(LiquidOutPoint)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidOwnedOutputObservation)], typeof(void), typeof(LiquidOwnedOutputObservation)),
					CallKey(typeof(HashCode), false, "ToHashCode", typeof(int)),
				];
			}
		}

		if (callerType == typeof(LiquidOwnedOutputObservation))
		{
			if (callerName == ".ctor")
			{
				return [CallKey(typeof(object), false, ".ctor", typeof(void))];
			}
			if (callerName == "get_OutputIndex")
			{
				return [CallKey(typeof(LiquidOutPoint), false, "get_OutputIndex", typeof(uint))];
			}
			if (callerName == "get_Branch")
			{
				return [CallKey(typeof(LiquidSpendKeyReference), false, "get_Branch", typeof(LiquidKeyBranch))];
			}
			if (callerName == "get_DerivationIndex")
			{
				return [CallKey(typeof(LiquidSpendKeyReference), false, "get_Index", typeof(uint))];
			}
			if (callerName is "get_Value" or "ToString")
			{
				return [];
			}
			if (callerName == "Create")
			{
				return
				[
					CallKey(typeof(ArgumentOutOfRangeException), false, ".ctor", typeof(void), typeof(string), typeof(string)),
					CallKey(typeof(LiquidTransactionId), true, "ParseConsensusBytes", typeof(LiquidTransactionId), typeof(ReadOnlySpan<byte>), typeof(string)),
					CallKey(typeof(LiquidOutPoint), true, "CreateSpendable", typeof(LiquidOutPoint), typeof(LiquidTransactionId), typeof(uint)),
					CallKey(typeof(LiquidTransactionWitnessBinding), true, "Create", typeof(LiquidTransactionWitnessBinding), typeof(ReadOnlySpan<byte>)),
					GenericCallKey(typeof(Enum), true, "IsDefined", [typeof(LiquidKeyBranch)], typeof(bool), typeof(LiquidKeyBranch)),
					CallKey(typeof(LiquidSpendKeyReference), true, "Create", typeof(LiquidSpendKeyReference), typeof(ReadOnlySpan<byte>), typeof(LiquidKeyBranch), typeof(uint)),
					CallKey(typeof(ArgumentException), false, ".ctor", typeof(void), typeof(string), typeof(string)),
					CallKey(typeof(ArgumentException), false, ".ctor", typeof(void), typeof(string), typeof(string), typeof(Exception)),
					CallKey(typeof(LiquidSpendKeyReference), false, "MatchesScriptPubKey", typeof(bool), typeof(ReadOnlySpan<byte>)),
					CallKey(typeof(LiquidBlindingPublicKey), true, "Create", typeof(LiquidBlindingPublicKey), typeof(ReadOnlySpan<byte>)),
					CallKey(typeof(LiquidAssetId), true, "ParseConsensusBytes", typeof(LiquidAssetId), typeof(ReadOnlySpan<byte>), typeof(string)),
					CallKey(typeof(ReadOnlySpan<byte>), false, "ToArray", typeof(byte[])),
					CallKey(
						typeof(LiquidOwnedOutputObservation),
						false,
						".ctor",
						typeof(void),
						typeof(LiquidOutPoint),
						typeof(LiquidTransactionWitnessBinding),
						typeof(byte[]),
						typeof(LiquidSpendKeyReference),
						typeof(LiquidBlindingPublicKey),
						typeof(LiquidAssetId),
						typeof(long)),
				];
			}
			if (callerName == "GetTransactionIdConsensusBytes")
			{
				return
				[
					CallKey(typeof(LiquidOutPoint), false, "get_TransactionId", typeof(LiquidTransactionId)),
					CallKey(typeof(LiquidTransactionId), false, "ToConsensusBytes", typeof(byte[])),
				];
			}
			if (callerName == "GetTransactionWitnessBinding")
			{
				return [CallKey(typeof(LiquidTransactionWitnessBinding), false, "GetBytes", typeof(byte[]))];
			}
			if (callerName == "GetScriptPubKey")
			{
				return [GenericCallKey(typeof(System.Linq.Enumerable), true, "ToArray", [typeof(byte)], typeof(byte[]), typeof(IEnumerable<byte>))];
			}
			if (callerName == "GetSpendPublicKey")
			{
				return [CallKey(typeof(LiquidSpendKeyReference), false, "GetCompressedPublicKey", typeof(byte[]))];
			}
			if (callerName == "GetBlindingPublicKey")
			{
				return [CallKey(typeof(LiquidBlindingPublicKey), false, "GetCompressedPublicKey", typeof(byte[]))];
			}
			if (callerName == "GetAssetIdConsensusBytes")
			{
				return [CallKey(typeof(LiquidAssetId), false, "ToConsensusBytes", typeof(byte[]))];
			}
			if (callerName == "MatchesTransactionId")
			{
				return
				[
					CallKey(typeof(LiquidOutPoint), false, "get_TransactionId", typeof(LiquidTransactionId)),
					CallKey(typeof(LiquidTransactionId), true, "op_Equality", typeof(bool), typeof(LiquidTransactionId), typeof(LiquidTransactionId)),
				];
			}
			if (callerName == "MatchesTransactionWitnessBinding")
			{
				return [CallKey(typeof(LiquidTransactionWitnessBinding), false, "Equals", typeof(bool), typeof(LiquidTransactionWitnessBinding))];
			}
			if (callerName == "Equals" && callerParameters.SequenceEqual([typeof(LiquidOwnedOutputObservation)]))
			{
				return
				[
					CallKey(typeof(LiquidOutPoint), true, "op_Equality", typeof(bool), typeof(LiquidOutPoint), typeof(LiquidOutPoint)),
					CallKey(typeof(LiquidTransactionWitnessBinding), false, "Equals", typeof(bool), typeof(LiquidTransactionWitnessBinding)),
					.. SequenceEqualityCallKeys<byte>(),
					CallKey(typeof(LiquidSpendKeyReference), false, "Equals", typeof(bool), typeof(LiquidSpendKeyReference)),
					CallKey(typeof(LiquidBlindingPublicKey), false, "Equals", typeof(bool), typeof(LiquidBlindingPublicKey)),
					CallKey(typeof(LiquidAssetId), true, "op_Equality", typeof(bool), typeof(LiquidAssetId), typeof(LiquidAssetId)),
					CallKey(typeof(LiquidOwnedOutputObservation), false, "get_Value", typeof(long)),
				];
			}
			if (callerName == "Equals" && callerParameters.SequenceEqual([typeof(object)]))
			{
				return [CallKey(typeof(LiquidOwnedOutputObservation), false, "Equals", typeof(bool), typeof(LiquidOwnedOutputObservation))];
			}
			if (callerName == "GetHashCode")
			{
				return
				[
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidOutPoint)], typeof(void), typeof(LiquidOutPoint)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidTransactionWitnessBinding)], typeof(void), typeof(LiquidTransactionWitnessBinding)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(byte)], typeof(void), typeof(byte)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidSpendKeyReference)], typeof(void), typeof(LiquidSpendKeyReference)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidBlindingPublicKey)], typeof(void), typeof(LiquidBlindingPublicKey)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(LiquidAssetId)], typeof(void), typeof(LiquidAssetId)),
					CallKey(typeof(LiquidOwnedOutputObservation), false, "get_Value", typeof(long)),
					GenericCallKey(typeof(HashCode), false, "Add", [typeof(long)], typeof(void), typeof(long)),
					CallKey(typeof(HashCode), false, "ToHashCode", typeof(int)),
				];
			}
		}

		return [];
	}

	private static IReadOnlyList<string> CopyCallKeys<T>() =>
	[
		CallKey(typeof(List<T>), false, ".ctor", typeof(void), typeof(int)),
		GenericCallKey(typeof(System.Runtime.InteropServices.CollectionsMarshal), true, "SetCount", [typeof(T)], typeof(void), typeof(List<T>), typeof(int)),
		GenericCallKey(typeof(System.Runtime.InteropServices.CollectionsMarshal), true, "AsSpan", [typeof(T)], typeof(Span<T>), typeof(List<T>)),
		CallKey(typeof(ReadOnlySpan<T>), false, ".ctor", typeof(void), typeof(T[])),
		CallKey(typeof(ReadOnlySpan<T>), false, "get_Length", typeof(int)),
		CallKey(typeof(Span<T>), false, "Slice", typeof(Span<T>), typeof(int), typeof(int)),
		CallKey(typeof(ReadOnlySpan<T>), false, "CopyTo", typeof(void), typeof(Span<T>)),
		CallKey(typeof(ReadOnlyCollection<T>), false, ".ctor", typeof(void), typeof(IList<T>)),
	];

	private static IReadOnlyList<string> SequenceEqualityCallKeys<T>() =>
	[
		GenericCallKey(typeof(MemoryExtensions), true, "AsSpan", [typeof(T)], typeof(Span<T>), typeof(T[])),
		CallKey(typeof(Span<T>), true, "op_Implicit", typeof(ReadOnlySpan<T>), typeof(Span<T>)),
		CallKey(typeof(ReadOnlySpan<T>), true, "op_Implicit", typeof(ReadOnlySpan<T>), typeof(T[])),
		GenericCallKey(
			typeof(MemoryExtensions),
			true,
			"SequenceEqual",
			[typeof(T)],
			typeof(bool),
			typeof(ReadOnlySpan<T>),
			typeof(ReadOnlySpan<T>)),
	];

	private static string CallKey(
		Type declaringType,
		bool isStatic,
		string name,
		Type returnType,
		params Type[] parameterTypes) =>
		CallKeyCore(declaringType, isStatic, name, [], returnType, parameterTypes);

	private static string GenericCallKey(
		Type declaringType,
		bool isStatic,
		string name,
		IReadOnlyList<Type> genericArguments,
		Type returnType,
		params Type[] parameterTypes) =>
		CallKeyCore(declaringType, isStatic, name, genericArguments, returnType, parameterTypes);

	private static string CallKeyCore(
		Type declaringType,
		bool isStatic,
		string name,
		IReadOnlyList<Type> genericArguments,
		Type returnType,
		IReadOnlyList<Type> parameterTypes) =>
		$"{TypeKey(declaringType)}|static={isStatic}|{name}|generic={GenericShape(genericArguments)}|" +
		$"{TypeKey(returnType)}|({string.Join(",", parameterTypes.Select(TypeKey))})";

	private static string CallMemberKey(MemberInfo member)
	{
		if (member is not MethodBase method || member.DeclaringType is null)
		{
			return $"unsupported|{member.MemberType}|{member.Name}";
		}

		Type returnType = method is MethodInfo methodInfo ? methodInfo.ReturnType : typeof(void);
		Type[] genericArguments = method is MethodInfo genericMethod
			? genericMethod.GetGenericArguments()
			: [];
		return $"{TypeKey(member.DeclaringType)}|static={method.IsStatic}|{method.Name}|" +
			$"generic={GenericShape(genericArguments)}|{TypeKey(returnType)}|" +
			$"({string.Join(",", method.GetParameters().Select(parameter => TypeKey(parameter.ParameterType)))})";
	}

	private static void AssertRequiredObservationCalls(MethodBase method, IReadOnlyList<ReferencedCall> referencedMembers)
	{
		if (method.DeclaringType == typeof(LiquidWalletTransactionObservation) &&
			method.Name == "Create")
		{
			Assert.True(HasCall(referencedMembers, typeof(LiquidTransactionId), "ParseConsensusBytes", 2));
			Assert.True(HasCall(referencedMembers, typeof(LiquidTransactionWitnessBinding), "Create", 1));
			Assert.True(HasCall(referencedMembers, typeof(LiquidOwnedOutputObservation), "MatchesTransactionId", 1));
			Assert.True(HasCall(
				referencedMembers,
				typeof(LiquidOwnedOutputObservation),
				"MatchesTransactionWitnessBinding",
				1));
			Assert.True(HasCall(referencedMembers, typeof(LiquidWalletTransactionObservation), ".ctor", 4));
		}
		else if (method.DeclaringType == typeof(LiquidOwnedOutputObservation) &&
			method.Name == "MatchesTransactionId")
		{
			Assert.True(HasCall(referencedMembers, typeof(LiquidOutPoint), "get_TransactionId", 0));
			Assert.True(HasCall(referencedMembers, typeof(LiquidTransactionId), "op_Equality", 2));
		}
		else if (method.DeclaringType == typeof(LiquidOwnedOutputObservation) &&
			method.Name == "MatchesTransactionWitnessBinding")
		{
			Assert.True(HasCall(referencedMembers, typeof(LiquidTransactionWitnessBinding), "Equals", 1));
		}
	}

	private static bool HasCall(
		IEnumerable<ReferencedCall> members,
		Type declaringType,
		string name,
		int parameterCount) =>
		members.Any(reference =>
		{
			MemberInfo member = reference.Member;
			return (reference.Opcode == OpCodes.Call ||
				reference.Opcode == OpCodes.Callvirt ||
				reference.Opcode == OpCodes.Newobj) &&
			member is MethodBase method &&
			member.DeclaringType is not null &&
			member.DeclaringType == declaringType &&
			method.Name == name &&
			method.GetParameters().Length == parameterCount;
		});

	private static bool ContainsInteropSurface(Type type)
	{
		if (type.IsPointer || type.IsByRef || type == typeof(IntPtr) || type == typeof(UIntPtr) ||
			type == typeof(System.Runtime.InteropServices.SafeHandle) ||
			type.IsSubclassOf(typeof(System.Runtime.InteropServices.SafeHandle)))
		{
			return true;
		}

		return type.HasElementType && ContainsInteropSurface(type.GetElementType()!) ||
			type.IsGenericType && type.GetGenericArguments().Any(ContainsInteropSurface);
	}

	private static IEnumerable<OpCode> ReadOpcodes(MethodBase method)
	{
		byte[]? bytes = method.GetMethodBody()?.GetILAsByteArray();
		if (bytes is null)
		{
			yield break;
		}

		int offset = 0;
		while (offset < bytes.Length)
		{
			short value = bytes[offset++] == 0xfe
				? unchecked((short)(0xfe00 | bytes[offset++]))
				: bytes[offset - 1];
			OpCode opcode = OpCodeByValue[value];
			yield return opcode;
			offset += OperandSize(opcode.OperandType, bytes, offset);
		}
	}

	private static IEnumerable<ReferencedCall> ReferencedMembers(MethodBase method)
	{
		byte[]? bytes = method.GetMethodBody()?.GetILAsByteArray();
		if (bytes is null)
		{
			yield break;
		}

		int offset = 0;
		while (offset < bytes.Length)
		{
			short value = bytes[offset++] == 0xfe
				? unchecked((short)(0xfe00 | bytes[offset++]))
				: bytes[offset - 1];
			OpCode opcode = OpCodeByValue[value];
			if (opcode.OperandType == OperandType.InlineMethod)
			{
				int token = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
				MemberInfo? referenced = method.Module.ResolveMember(
					token,
					method.DeclaringType?.GetGenericArguments(),
					method.IsGenericMethod ? method.GetGenericArguments() : null);
				if (referenced is not null)
				{
					yield return new ReferencedCall(opcode, referenced);
				}
			}

			offset += OperandSize(opcode.OperandType, bytes, offset);
		}
	}

	private static int OperandSize(OperandType operandType, byte[] bytes, int offset) =>
		operandType switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
				OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
				OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => sizeof(int) +
				(sizeof(int) * BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)))),
			_ => throw new InvalidOperationException("An unsupported IL operand was encountered."),
		};

	private static readonly IReadOnlyDictionary<short, OpCode> OpCodeByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(OpCode))
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(opcode => opcode.Value);

	private readonly record struct ReferencedCall(OpCode Opcode, MemberInfo Member);

	private sealed class GeneratedReadOnlyList<T>(int count, Func<int, T> valueFactory) : IReadOnlyList<T>
	{
		public int Count { get; } = count;

		public T this[int index] =>
			index >= 0 && index < Count
				? valueFactory(index)
				: throw new ArgumentOutOfRangeException(nameof(index));

		public IEnumerator<T> GetEnumerator()
		{
			for (int index = 0; index < Count; index++)
			{
				yield return this[index];
			}
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class IndexingForbiddenReadOnlyList<T>(int count) : IReadOnlyList<T>
	{
		public int Count { get; } = count;
		public int ElementAccessCount { get; private set; }

		public T this[int index]
		{
			get
			{
				ElementAccessCount++;
				throw new InvalidOperationException("Collection elements must not be accessed.");
			}
		}

		public IEnumerator<T> GetEnumerator()
		{
			ElementAccessCount++;
			throw new InvalidOperationException("Collection elements must not be enumerated.");
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
