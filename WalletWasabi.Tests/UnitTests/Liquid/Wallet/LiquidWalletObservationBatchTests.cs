using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletObservationBatchTests
{
	private const int MaxTransactionCount = 8_192;
	private const int MaxAggregateInputCount = 1_636_801;
	private const int MaxAggregateOwnedOutputCount = 148_470;
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
	public void AcceptsEmptyAndDistinguishesSpendOnlySingleton()
	{
		LiquidWalletObservationBatch empty = LiquidWalletObservationBatch.Create([]);
		Assert.True(empty.IsEmpty);
		Assert.Equal(0, empty.TransactionCount);
		Assert.Equal(0, empty.OwnedOutputCount);
		Assert.Empty(empty.GetTransactions());

		LiquidWalletTransactionObservation spendOnly = Observation(IdForOrdinal(1));
		LiquidWalletObservationBatch singleton = LiquidWalletObservationBatch.Create([spendOnly]);
		Assert.False(singleton.IsEmpty);
		Assert.Equal(1, singleton.TransactionCount);
		Assert.Equal(0, singleton.OwnedOutputCount);
		Assert.Same(spendOnly, Assert.Single(singleton.GetTransactions()));
	}

	[Fact]
	public void PreservesExactNativeMultiassetFixture()
	{
		LiquidWalletTransactionObservation observation = ExactNativeFixture();
		LiquidWalletObservationBatch batch = LiquidWalletObservationBatch.Create([observation]);

		Assert.Equal(1, batch.TransactionCount);
		Assert.Equal(2, batch.OwnedOutputCount);
		LiquidWalletTransactionObservation retained = Assert.Single(batch.GetTransactions());
		Assert.Same(observation, retained);
		Assert.Equal(Convert.FromHexString(TransactionIdHex), retained.GetTransactionIdConsensusBytes());
		Assert.Equal(Convert.FromHexString(WitnessBindingHex), retained.GetTransactionWitnessBinding());
	}

	[Fact]
	public void UsesUnsignedLexicographicConsensusByteOrderAtEveryPosition()
	{
		for (int position = 0; position < LiquidTransactionId.ConsensusByteLength; position++)
		{
			(byte low, byte high) = position switch
			{
				0 => ((byte)0x7f, (byte)0x80),
				31 => ((byte)0x00, (byte)0xff),
				_ => ((byte)0x31, (byte)0xc2),
			};
			byte[] firstId = Enumerable.Repeat((byte)0x55, LiquidTransactionId.ConsensusByteLength).ToArray();
			byte[] secondId = [.. firstId];
			firstId[position] = low;
			secondId[position] = high;
			for (int later = position + 1; later < LiquidTransactionId.ConsensusByteLength; later++)
			{
				firstId[later] = 0xff;
				secondId[later] = 0x00;
			}

			LiquidWalletTransactionObservation first = Observation(firstId);
			LiquidWalletTransactionObservation second = Observation(secondId);
			LiquidWalletObservationBatch accepted = LiquidWalletObservationBatch.Create([first, second]);
			Assert.Equal([first, second], accepted.GetTransactions());
			Assert.Throws<ArgumentException>(() => LiquidWalletObservationBatch.Create([second, first]));
		}

		LiquidWalletTransactionObservation one = Observation(IdForOrdinal(1));
		LiquidWalletTransactionObservation two = Observation(IdForOrdinal(2));
		LiquidWalletTransactionObservation three = Observation(IdForOrdinal(3));
		Assert.Equal([one, two, three], LiquidWalletObservationBatch.Create([one, two, three]).GetTransactions());
		LiquidWalletObservationBatch? retainedResult = null;
		Assert.Throws<ArgumentException>(() => retainedResult = LiquidWalletObservationBatch.Create([one, three, two]));
		Assert.Null(retainedResult);
	}

	[Fact]
	public void RejectsDuplicateIdentityDespiteDifferentObservationFields()
	{
		byte[] identity = IdForOrdinal(17);
		LiquidWalletTransactionObservation first = Observation(identity);
		LiquidWalletTransactionObservation changed = Observation(
			identity,
			witnessBinding: Enumerable.Repeat((byte)0x5a, LiquidTransactionWitnessBinding.ByteLength).ToArray(),
			input: OutPoint('b', 9));

		Assert.NotEqual(first, changed);
		LiquidWalletObservationBatch? retainedResult = null;
		ArgumentException error = Assert.Throws<ArgumentException>(
			() => retainedResult = LiquidWalletObservationBatch.Create(
				[Observation(IdForOrdinal(1)), first, changed]));
		Assert.Null(retainedResult);
		Assert.Equal("transactions", error.ParamName);
		Assert.StartsWith(
			"Wallet observation transactions must have unique, strictly ascending consensus identifiers.",
			error.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void RejectsNullNegativeAndOversizedCollectionsBeforeElements()
	{
		Assert.Throws<ArgumentNullException>(() => LiquidWalletObservationBatch.Create(null!));
		Assert.Throws<ArgumentNullException>(() => LiquidWalletObservationBatch.Create([null!]));

		var negative = new UntouchableReadOnlyList<LiquidWalletTransactionObservation>(-1);
		ArgumentOutOfRangeException negativeError = Assert.Throws<ArgumentOutOfRangeException>(
			() => LiquidWalletObservationBatch.Create(negative));
		Assert.Equal(1, negative.CountReadCount);
		Assert.Equal(0, negative.ElementAccessCount);
		Assert.Equal("transactions", negativeError.ParamName);
		Assert.StartsWith(
			"A nonnegative wallet observation transaction count is required.",
			negativeError.Message,
			StringComparison.Ordinal);

		var oversized = new UntouchableReadOnlyList<LiquidWalletTransactionObservation>(MaxTransactionCount + 1);
		ArgumentOutOfRangeException oversizedError = Assert.Throws<ArgumentOutOfRangeException>(
			() => LiquidWalletObservationBatch.Create(oversized));
		Assert.Equal(1, oversized.CountReadCount);
		Assert.Equal(0, oversized.ElementAccessCount);
		Assert.Equal("transactions", oversizedError.ParamName);
		Assert.StartsWith(
			"The wallet observation transaction limit was exceeded.",
			oversizedError.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AcceptsExactTransactionCapWithOneCountAndOneIndexReadPerElement()
	{
		LiquidWalletTransactionObservation[] observations = Enumerable.Range(1, MaxTransactionCount)
			.Select(index => Observation(IdForOrdinal(index)))
			.ToArray();
		var source = new InstrumentedReadOnlyList<LiquidWalletTransactionObservation>(observations);

		LiquidWalletObservationBatch batch = LiquidWalletObservationBatch.Create(source);

		Assert.Equal(MaxTransactionCount, batch.TransactionCount);
		Assert.Equal(1, source.CountReadCount);
		Assert.Equal(MaxTransactionCount, source.ElementAccessCount);
		Assert.Equal(Enumerable.Repeat(1, MaxTransactionCount), source.IndexReadCounts);
		Assert.Equal(observations, batch.GetTransactions());
	}

	[Fact]
	public void CapturesEachReentrantElementExactlyOnce()
	{
		LiquidWalletTransactionObservation first = Observation(IdForOrdinal(1));
		LiquidWalletTransactionObservation second = Observation(IdForOrdinal(2));
		LiquidWalletTransactionObservation replacement = Observation(IdForOrdinal(3));
		var source = new ReentrantReadOnlyList<LiquidWalletTransactionObservation>([first, second], replacement);

		LiquidWalletObservationBatch batch = LiquidWalletObservationBatch.Create(source);

		Assert.Equal([first, second], batch.GetTransactions());
		Assert.Equal(1, source.CountReadCount);
		Assert.Equal([1, 1], source.IndexReadCounts);
	}

	[Fact]
	public void EnforcesExactAggregateInputBoundaryAtomically()
	{
		LiquidWalletTransactionObservation[] exact = ValidatedInputObservations(MaxAggregateInputCount);
		LiquidWalletObservationBatch accepted = LiquidWalletObservationBatch.Create(exact);
		Assert.Equal(exact.Length, accepted.TransactionCount);

		LiquidWalletTransactionObservation[] exceeded = ValidatedInputObservations(MaxAggregateInputCount + 1);
		LiquidWalletObservationBatch? result = null;
		ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
			() => result = LiquidWalletObservationBatch.Create(exceeded));
		Assert.Null(result);
		Assert.Equal("transactions", error.ParamName);
		Assert.StartsWith(
			"The wallet observation aggregate input limit was exceeded.",
			error.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void EnforcesExactAggregateOwnedOutputBoundaryAndSumsOutputs()
	{
		LiquidWalletTransactionObservation[] exact = ValidatedOutputObservations(MaxAggregateOwnedOutputCount);
		LiquidWalletObservationBatch accepted = LiquidWalletObservationBatch.Create(exact);
		Assert.Equal(MaxAggregateOwnedOutputCount, accepted.OwnedOutputCount);

		LiquidWalletTransactionObservation[] exceeded = ValidatedOutputObservations(MaxAggregateOwnedOutputCount + 1);
		LiquidWalletObservationBatch? result = null;
		ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
			() => result = LiquidWalletObservationBatch.Create(exceeded));
		Assert.Null(result);
		Assert.Equal("transactions", error.ParamName);
		Assert.StartsWith(
			"The wallet observation aggregate owned-output limit was exceeded.",
			error.Message,
			StringComparison.Ordinal);

		LiquidWalletObservationBatch mixed = LiquidWalletObservationBatch.Create(
			[
				ValidatedObservation(IdForOrdinal(1), 0),
				ValidatedObservation(IdForOrdinal(2), 2),
				ValidatedObservation(IdForOrdinal(3), 7),
			]);
		Assert.Equal(9, mixed.OwnedOutputCount);
	}

	[Fact]
	public void DefensivelyCopiesSourceAndEveryReturnedCollection()
	{
		LiquidWalletTransactionObservation first = Observation(IdForOrdinal(1));
		LiquidWalletTransactionObservation second = Observation(IdForOrdinal(2));
		LiquidWalletTransactionObservation replacement = Observation(IdForOrdinal(3));
		var source = new List<LiquidWalletTransactionObservation> { first, second };
		LiquidWalletObservationBatch expected = LiquidWalletObservationBatch.Create([first, second]);
		LiquidWalletObservationBatch batch = LiquidWalletObservationBatch.Create(source);
		int hash = batch.GetHashCode();

		source[0] = replacement;
		source.Clear();
		IReadOnlyList<LiquidWalletTransactionObservation> firstFetch = batch.GetTransactions();
		IReadOnlyList<LiquidWalletTransactionObservation> secondFetch = batch.GetTransactions();
		Assert.NotSame(firstFetch, secondFetch);
		AssertReadOnlyThroughAllCasts(firstFetch, replacement);

		Assert.Equal([first, second], firstFetch);
		Assert.Equal([first, second], secondFetch);
		Assert.Equal(expected, batch);
		Assert.Equal(hash, batch.GetHashCode());
		Assert.Equal(2, batch.TransactionCount);
	}

	[Fact]
	public void EqualityAndHashDependOnExactOrderedObservationValues()
	{
		LiquidWalletTransactionObservation first = Observation(IdForOrdinal(1));
		LiquidWalletTransactionObservation firstEqual = Observation(IdForOrdinal(1));
		LiquidWalletTransactionObservation second = Observation(IdForOrdinal(2));
		LiquidWalletObservationBatch baseline = LiquidWalletObservationBatch.Create([first, second]);
		LiquidWalletObservationBatch equal = LiquidWalletObservationBatch.Create([firstEqual, Observation(IdForOrdinal(2))]);
		LiquidWalletObservationBatch shorter = LiquidWalletObservationBatch.Create([first]);

		Assert.True(baseline.Equals(equal));
		Assert.True(baseline.Equals((object)equal));
		Assert.Equal(baseline.GetHashCode(), equal.GetHashCode());
		Assert.False(baseline.Equals(shorter));
		Assert.False(baseline.Equals(null));
		Assert.False(baseline.Equals(new object()));

		LiquidWalletTransactionObservation changedBinding = Observation(
			IdForOrdinal(1),
			witnessBinding: Enumerable.Repeat((byte)0x5a, LiquidTransactionWitnessBinding.ByteLength).ToArray());
		LiquidWalletTransactionObservation changedInput = Observation(IdForOrdinal(1), input: OutPoint('b', 0));
		LiquidWalletTransactionObservation changedOutputs = ValidatedObservation(IdForOrdinal(1), 1);
		foreach (LiquidWalletTransactionObservation changed in new[] { changedBinding, changedInput, changedOutputs })
		{
			LiquidWalletObservationBatch changedBatch = LiquidWalletObservationBatch.Create([changed]);
			LiquidWalletObservationBatch singleBaseline = LiquidWalletObservationBatch.Create([first]);
			Assert.NotEqual(singleBaseline, changedBatch);
		}
	}

	[Fact]
	public void HashCodeDataFlowAddsEachOrderedObservationBeforeReturningTheAccumulator()
	{
		MethodInfo method = GetMethod(nameof(LiquidWalletObservationBatch.GetHashCode));
		Instruction[] instructions = ReadInstructions(method).ToArray();
		ManagedValueFlow flow = ManagedValueFlow.Analyze(method, instructions);
		Assert.True(flow.IsValid);
		int add = Assert.Single(instructions
			.Select((instruction, instructionIndex) => (instruction, instructionIndex))
			.Where(pair =>
				pair.instruction.OpCode.OperandType == OperandType.InlineMethod &&
				ResolveMember(method, pair.instruction) is MethodInfo target &&
				IsExactObservationHashAdd(target))
			.Select(pair => pair.instructionIndex));
		MethodInfo resolvedAdd = Assert.IsAssignableFrom<MethodInfo>(ResolveMember(method, instructions[add]));
		Assert.True(IsExactObservationHashAdd(resolvedAdd));
		MethodInfo objectAdd = typeof(HashCode).GetMethods(BindingFlags.Public | BindingFlags.Instance)
			.Single(candidate =>
				candidate.Name == nameof(HashCode.Add) && candidate.IsGenericMethodDefinition &&
				candidate.GetParameters().Length == 1)
			.MakeGenericMethod(typeof(object));
		Assert.False(IsExactObservationHashAdd(objectAdd));
		int toHashCode = Assert.Single(instructions
			.Select((instruction, instructionIndex) => (instruction, instructionIndex))
			.Where(pair =>
				pair.instruction.OpCode.OperandType == OperandType.InlineMethod &&
				ResolveMember(method, pair.instruction) is MethodInfo target &&
				target.DeclaringType == typeof(HashCode) && target.Name == nameof(HashCode.ToHashCode) &&
				!target.IsGenericMethod && target.GetParameters().Length == 0 && target.ReturnType == typeof(int))
			.Select(pair => pair.instructionIndex));
		IReadOnlyList<ManagedFlowValue> addOperands = Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(flow.PoppedAt(add));
		Assert.Equal(2, addOperands.Count);
		int hashLocal = AssertAddressedLocal(instructions, UnwrapStoredValue(addOperands[0]));
		ManagedFlowValue element = UnwrapStoredValue(addOperands[1]);
		Assert.Equal(ManagedFlowValueKind.Producer, element.Kind);
		Assert.Equal(OpCodes.Ldelem_Ref, instructions[element.Instruction].OpCode);

		IReadOnlyList<ManagedFlowValue> elementOperands = Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(
			flow.PoppedAt(element.Instruction));
		Assert.Equal(2, elementOperands.Count);
		ManagedFlowValue index = elementOperands[1];
		Assert.Equal(ManagedFlowValueKind.LocalVersion, index.Kind);
		Assert.True(index.Local >= 0);
		ManagedFlowValue array = UnwrapStoredValue(elementOperands[0]);
		Assert.Equal(ManagedFlowValueKind.Producer, array.Kind);
		Assert.Equal(OpCodes.Ldfld, instructions[array.Instruction].OpCode);
		Assert.Equal(
			typeof(LiquidWalletObservationBatch).GetField("_transactions", DeclaredMemberFlags),
			ResolveMember(method, instructions[array.Instruction]));

		int[] indexStores = instructions
			.Select((instruction, instructionIndex) => (instruction, instructionIndex))
			.Where(pair => TryGetLocalIndex(pair.instruction, load: false, out int local) && local == index.Local)
			.Select(pair => pair.instructionIndex)
			.ToArray();
		Assert.Equal(2, indexStores.Length);
		ManagedFlowValue initialIndex = UnwrapStoredValue(
			Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(flow.PoppedAt(indexStores[0]))));
		Assert.Equal(new ManagedFlowValue(ManagedFlowValueKind.Constant, Constant: 0), initialIndex);
		ManagedFlowValue increment = UnwrapStoredValue(
			Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(flow.PoppedAt(indexStores[1]))));
		Assert.Equal(ManagedFlowValueKind.Producer, increment.Kind);
		Assert.Equal(OpCodes.Add, instructions[increment.Instruction].OpCode);
		IReadOnlyList<ManagedFlowValue> incrementOperands = Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(
			flow.PoppedAt(increment.Instruction));
		Assert.Equal(2, incrementOperands.Count);
		Assert.Equal(ManagedFlowValueKind.LocalVersion, incrementOperands[0].Kind);
		Assert.Equal(index.Local, incrementOperands[0].Local);
		Assert.Equal(
			new ManagedFlowValue(ManagedFlowValueKind.Constant, Constant: 1),
			UnwrapStoredValue(incrementOperands[1]));

		int length = Assert.Single(instructions
			.Select((instruction, instructionIndex) => (instruction, instructionIndex))
			.Where(pair => pair.instruction.OpCode == OpCodes.Ldlen)
			.Select(pair => pair.instructionIndex));
		ManagedFlowValue lengthArray = UnwrapStoredValue(
			Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(flow.PoppedAt(length))));
		Assert.Equal(array, lengthArray);
		int[] directBoundBranches = instructions
			.Select((instruction, instructionIndex) => (instruction, instructionIndex))
			.Where(pair => pair.instruction.OpCode == OpCodes.Blt || pair.instruction.OpCode == OpCodes.Blt_S)
			.Select(pair => pair.instructionIndex)
			.ToArray();
		int[] lessThanComparisons = instructions
			.Select((instruction, instructionIndex) => (instruction, instructionIndex))
			.Where(pair => pair.instruction.OpCode == OpCodes.Clt || pair.instruction.OpCode == OpCodes.Clt_Un)
			.Select(pair => pair.instructionIndex)
			.ToArray();
		Assert.True((directBoundBranches.Length == 1) ^ (lessThanComparisons.Length == 1));
		int boundTest = directBoundBranches.Length == 1 ? directBoundBranches[0] : lessThanComparisons[0];
		int boundBranch = directBoundBranches.Length == 1
			? directBoundBranches[0]
			: Assert.Single(instructions
				.Select((instruction, instructionIndex) => (instruction, instructionIndex))
				.Where(pair =>
					pair.instruction.OpCode is var branchOpcode &&
					(branchOpcode == OpCodes.Brtrue || branchOpcode == OpCodes.Brtrue_S) &&
					flow.PoppedAt(pair.instructionIndex) is { Count: 1 } branchOperands &&
					UnwrapStoredValue(branchOperands[0]) ==
						new ManagedFlowValue(ManagedFlowValueKind.Producer, Instruction: boundTest))
				.Select(pair => pair.instructionIndex));
		IReadOnlyList<ManagedFlowValue> boundOperands = Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(
			flow.PoppedAt(boundTest));
		Assert.Equal(2, boundOperands.Count);
		Assert.Equal(ManagedFlowValueKind.LocalVersion, boundOperands[0].Kind);
		Assert.Equal(index.Local, boundOperands[0].Local);
		ManagedFlowValue boundLength = UnwrapStoredValue(boundOperands[1]);
		Assert.Equal(ManagedFlowValueKind.Producer, boundLength.Kind);
		if (instructions[boundLength.Instruction].OpCode == OpCodes.Conv_I4)
		{
			boundLength = UnwrapStoredValue(
				Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(
					flow.PoppedAt(boundLength.Instruction))));
		}
		Assert.Equal(new ManagedFlowValue(ManagedFlowValueKind.Producer, Instruction: length), boundLength);

		IReadOnlyList<ManagedFlowValue> finalOperands = Assert.IsAssignableFrom<IReadOnlyList<ManagedFlowValue>>(
			flow.PoppedAt(toHashCode));
		Assert.Equal(hashLocal, AssertAddressedLocal(instructions, UnwrapStoredValue(Assert.Single(finalOperands))));
		int terminalReturn = Assert.Single(instructions
			.Select((instruction, instructionIndex) => (instruction, instructionIndex))
			.Where(pair => pair.instruction.OpCode == OpCodes.Ret)
			.Select(pair => pair.instructionIndex));
		Assert.Equal(
			new ManagedFlowValue(ManagedFlowValueKind.Producer, Instruction: toHashCode),
			UnwrapStoredValue(Assert.Single(flow.PoppedAt(terminalReturn)!)));

		DirectedGraph graph = DirectedGraph.FromInstructions(instructions);
		int takenSuccessor = Array.FindIndex(
			instructions,
			instruction => instruction.Offset == Assert.IsType<int>(instructions[boundBranch].Operand));
		int falseSuccessor = boundBranch + 1;
		Assert.True(takenSuccessor >= 0);
		Assert.NotEqual(takenSuccessor, falseSuccessor);
		Assert.Equal([takenSuccessor, falseSuccessor], graph.Successors(boundBranch));
		Assert.True(graph.CanReach(takenSuccessor, element.Instruction));
		Assert.True(graph.CanReach(takenSuccessor, add));
		Assert.True(graph.DominatesFrom(takenSuccessor, element.Instruction, add));
		Assert.True(graph.DominatesFrom(takenSuccessor, element.Instruction, boundBranch));
		Assert.True(graph.DominatesFrom(takenSuccessor, add, boundBranch));
		Assert.True(graph.DominatesFrom(takenSuccessor, element.Instruction, toHashCode));
		Assert.True(graph.DominatesFrom(takenSuccessor, add, toHashCode));
		Assert.True(graph.DominatesFrom(takenSuccessor, indexStores[1], toHashCode));
		Assert.True(graph.DominatesFrom(takenSuccessor, boundBranch, toHashCode));
		DirectedGraph recheckBypass = graph.WithAdditionalEdge(takenSuccessor, boundBranch);
		Assert.False(recheckBypass.DominatesFrom(takenSuccessor, element.Instruction, boundBranch));
		Assert.False(recheckBypass.DominatesFrom(takenSuccessor, add, boundBranch));
		DirectedGraph finalizationBypass = graph.WithAdditionalEdge(takenSuccessor, toHashCode);
		Assert.False(finalizationBypass.DominatesFrom(takenSuccessor, element.Instruction, toHashCode));
		Assert.False(finalizationBypass.DominatesFrom(takenSuccessor, add, toHashCode));
		DirectedGraph postAddFinalizationBypass = graph.WithAdditionalEdge(add, toHashCode);
		Assert.False(postAddFinalizationBypass.DominatesFrom(takenSuccessor, indexStores[1], toHashCode));
		Assert.False(postAddFinalizationBypass.DominatesFrom(takenSuccessor, boundBranch, toHashCode));
		Assert.False(graph.CanReach(falseSuccessor, element.Instruction));
		Assert.False(graph.CanReach(falseSuccessor, add));
		Assert.True(graph.CanReach(falseSuccessor, toHashCode));
		Assert.True(graph.Dominates(indexStores[0], element.Instruction));
		Assert.True(graph.Dominates(boundBranch, element.Instruction));
		Assert.True(graph.Dominates(boundBranch, add));
		Assert.True(graph.Dominates(element.Instruction, add));
		Assert.True(graph.Dominates(add, indexStores[1]));
		Assert.True(graph.DominatesFrom(add, indexStores[1], boundBranch));
		Assert.True(graph.Dominates(boundBranch, toHashCode));
		Assert.True(graph.Dominates(toHashCode, terminalReturn));
		Assert.True(graph.CanReach(indexStores[0], element.Instruction));
		Assert.True(graph.CanReach(indexStores[0], boundBranch));
		Assert.False(graph.CanReach(element.Instruction, indexStores[0]));
		Assert.True(graph.CanReach(element.Instruction, add));
		Assert.True(graph.CanReach(add, indexStores[1]));
		Assert.True(graph.CanReach(indexStores[1], boundBranch));
		Assert.True(graph.CanReach(boundBranch, element.Instruction));
		Assert.True(graph.CanReach(add, toHashCode));
		Assert.False(graph.CanReach(toHashCode, add));
	}

	[Fact]
	public void ErrorsAndFormattingRemainPrivacyRedacted()
	{
		LiquidWalletTransactionObservation first = Observation(TransactionId);
		const ulong PrivateAmount = 987_654_321;
		const ulong BreachingPrivateAmount = 876_543_219;
		const uint PrivateInputIndex = 777;
		const uint BreachingPrivateInputIndex = 778;
		LiquidOutPoint privateInputOutPoint = OutPoint('f', PrivateInputIndex);
		LiquidOutPoint breachingPrivateInputOutPoint = OutPoint('e', BreachingPrivateInputIndex);
		LiquidWalletTransactionObservation privateInput = LiquidWalletTransactionObservation.Create(
			IdForOrdinal(1),
			new byte[LiquidTransactionWitnessBinding.ByteLength],
			[privateInputOutPoint],
			[]);
		LiquidWalletTransactionObservation privateOutput = ValidatedObservation(
			IdForOrdinal(1),
			ownedOutputCount: 1,
			amount: PrivateAmount);
		LiquidWalletTransactionObservation[] middleInputs =
			ValidatedInputObservations(MaxAggregateInputCount - 1, startOrdinal: 2);
		LiquidWalletTransactionObservation breachingPrivateInput = LiquidWalletTransactionObservation.Create(
			IdForOrdinal(2 + middleInputs.Length),
			new byte[LiquidTransactionWitnessBinding.ByteLength],
			[breachingPrivateInputOutPoint],
			[]);
		LiquidWalletTransactionObservation[] aggregateInputs =
			[privateInput, .. middleInputs, breachingPrivateInput];
		LiquidWalletTransactionObservation[] middleOutputs =
			ValidatedOutputObservations(MaxAggregateOwnedOutputCount - 1, startOrdinal: 2);
		LiquidWalletTransactionObservation breachingPrivateOutput = ValidatedObservation(
			IdForOrdinal(2 + middleOutputs.Length),
			ownedOutputCount: 1,
			amount: BreachingPrivateAmount);
		LiquidWalletTransactionObservation[] aggregateOutputs =
			[privateOutput, .. middleOutputs, breachingPrivateOutput];
		string[] aggregatePrivateTransactionIds =
		[
			.. aggregateInputs.Concat(aggregateOutputs)
				.SelectMany(observation =>
				{
					byte[] consensusBytes = observation.GetTransactionIdConsensusBytes();
					return new[]
					{
						Convert.ToHexString(consensusBytes),
						LiquidTransactionId.ParseConsensusBytes(consensusBytes).CanonicalRpcHex,
					};
				})
				.Distinct(StringComparer.OrdinalIgnoreCase),
		];
		string[] privateOutPointRepresentations =
		[
			Convert.ToHexString(privateInputOutPoint.ToConsensusBytes()),
			Convert.ToHexString(breachingPrivateInputOutPoint.ToConsensusBytes()),
			$"{privateInputOutPoint.TransactionId.CanonicalRpcHex}:{PrivateInputIndex}",
			$"{breachingPrivateInputOutPoint.TransactionId.CanonicalRpcHex}:{BreachingPrivateInputIndex}",
		];
		string[] errors =
		[
			Assert.Throws<ArgumentNullException>(() => LiquidWalletObservationBatch.Create(null!)).ToString(),
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletObservationBatch.Create([Observation(IdForOrdinal(1)), null!])).ToString(),
			Assert.Throws<ArgumentException>(() => LiquidWalletObservationBatch.Create([first, first])).ToString(),
			Assert.Throws<ArgumentOutOfRangeException>(() =>
				LiquidWalletObservationBatch.Create(new UntouchableReadOnlyList<LiquidWalletTransactionObservation>(-1))).ToString(),
			Assert.Throws<ArgumentOutOfRangeException>(() =>
				LiquidWalletObservationBatch.Create(new UntouchableReadOnlyList<LiquidWalletTransactionObservation>(MaxTransactionCount + 1))).ToString(),
			Assert.Throws<ArgumentOutOfRangeException>(() =>
				LiquidWalletObservationBatch.Create(aggregateInputs)).ToString(),
			Assert.Throws<ArgumentOutOfRangeException>(() =>
				LiquidWalletObservationBatch.Create(aggregateOutputs)).ToString(),
		];
		string formatted = LiquidWalletObservationBatch.Create([first]).ToString();
		Assert.Equal(nameof(LiquidWalletObservationBatch), formatted);
		foreach (string text in errors.Append(formatted))
		{
			Assert.DoesNotContain(TransactionIdHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(WitnessBindingHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(FirstInputHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(ExternalScriptHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(ExternalAssetHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(new string('f', 64), text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(new string('e', 64), text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(PrivateInputIndex.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
			Assert.DoesNotContain(BreachingPrivateInputIndex.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
			Assert.DoesNotContain(PrivateAmount.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
			Assert.DoesNotContain(BreachingPrivateAmount.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
			foreach (string privateTransactionId in aggregatePrivateTransactionIds)
			{
				Assert.DoesNotContain(privateTransactionId, text, StringComparison.OrdinalIgnoreCase);
			}
			foreach (string privateOutPoint in privateOutPointRepresentations)
			{
				Assert.DoesNotContain(privateOutPoint, text, StringComparison.OrdinalIgnoreCase);
			}
		}

		var injected = new CallerThrowingReadOnlyList<LiquidWalletTransactionObservation>("caller-private-sentinel");
		InvalidOperationException callerError = Assert.Throws<InvalidOperationException>(
			() => LiquidWalletObservationBatch.Create(injected));
		Assert.Equal("caller-private-sentinel", callerError.Message);

		var indexerInjected = new IndexerThrowingReadOnlyList<LiquidWalletTransactionObservation>(
			"indexer-private-sentinel");
		InvalidOperationException indexerError = Assert.Throws<InvalidOperationException>(
			() => LiquidWalletObservationBatch.Create(indexerInjected));
		Assert.Equal("indexer-private-sentinel", indexerError.Message);
	}

	[Fact]
	public void ExactClassAndCallableManifestIsFrozen()
	{
		Type type = typeof(LiquidWalletObservationBatch);
		Assert.True(type.IsNotPublic);
		Assert.False(type.IsNested);
		Assert.True(type.IsSealed);
		Assert.False(type.IsAbstract);
		Assert.False(type.IsGenericType);
		Assert.Equal(typeof(object), type.BaseType);
		Assert.Equal(TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, type.Attributes);
		Assert.Equal(LayoutKind.Auto, type.StructLayoutAttribute!.Value);
		Assert.Equal(CharSet.Ansi, type.StructLayoutAttribute.CharSet);
		Assert.Equal(0, type.StructLayoutAttribute.Pack);
		Assert.Equal(0, type.StructLayoutAttribute.Size);
		Assert.Equal([typeof(IEquatable<LiquidWalletObservationBatch>)], type.GetInterfaces());
		Assert.Empty(type.GetEvents(DeclaredMemberFlags));
		Assert.Empty(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

		AssertExactSet(
			type.GetFields(DeclaredMemberFlags).Select(FieldManifest),
			[
				FieldManifest("MaxTransactionCount", typeof(int), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, MaxTransactionCount),
				FieldManifest("MaxAggregateInputCount", typeof(int), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, MaxAggregateInputCount),
				FieldManifest("MaxAggregateOwnedOutputCount", typeof(int), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, MaxAggregateOwnedOutputCount),
				FieldManifest("_transactions", typeof(LiquidWalletTransactionObservation[]), FieldAttributes.Private | FieldAttributes.InitOnly, null),
				FieldManifest("<OwnedOutputCount>k__BackingField", typeof(int), FieldAttributes.Private | FieldAttributes.InitOnly, null),
			]);

		ConstructorInfo constructor = Assert.Single(type.GetConstructors(DeclaredMemberFlags));
		AssertCallable(
			constructor,
			MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
			("transactions", typeof(LiquidWalletTransactionObservation[])),
			("ownedOutputCount", typeof(int)));

		AssertExactSet(
			type.GetMethods(DeclaredMemberFlags).Select(MethodManifest),
			[
				MethodManifest("get_TransactionCount", typeof(int), MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName),
				MethodManifest("get_OwnedOutputCount", typeof(int), MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName),
				MethodManifest("get_IsEmpty", typeof(bool), MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName),
				MethodManifest("Create", type, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(IReadOnlyList<LiquidWalletTransactionObservation>)),
				MethodManifest("GetTransactions", typeof(IReadOnlyList<LiquidWalletTransactionObservation>), MethodAttributes.Public | MethodAttributes.HideBySig),
				MethodManifest("Equals", typeof(bool), MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot, type),
				MethodManifest("Equals", typeof(bool), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(object)),
				MethodManifest("GetHashCode", typeof(int), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig),
				MethodManifest("ToString", typeof(string), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig),
			]);

		AssertExactSet(
			type.GetProperties(DeclaredMemberFlags).Select(PropertyManifest),
			[
				PropertyManifest("TransactionCount", typeof(int), "get_TransactionCount"),
				PropertyManifest("OwnedOutputCount", typeof(int), "get_OwnedOutputCount"),
				PropertyManifest("IsEmpty", typeof(bool), "get_IsEmpty"),
			]);
		Assert.All(type.GetProperties(DeclaredMemberFlags), property =>
		{
			Assert.Equal(PropertyAttributes.None, property.Attributes);
			Assert.Empty(property.GetIndexParameters());
			Assert.Null(property.SetMethod);
			Assert.Empty(property.GetAccessors(nonPublic: true).Skip(1));
		});

		AssertExactNullableAttributes(type);
		AssertAllParametersAndReturns(type, constructor);
	}

	[Fact]
	public void EveryProductionBodyMatchesExactNormalizedInstructionManifest()
	{
		byte[] manifest = BuildNormalizedInstructionManifest();
#if DEBUG
		const int ExpectedLength = 8_108;
		const string ExpectedHash = "9a4fb62dcd79222d1bd970c04a0242f3369f0674b22286f654a4aeda6bc849f0";
#else
		const int ExpectedLength = 6_817;
		const string ExpectedHash = "79dc56784ad90195c330d862e2199664f9d9cea31a30c4ee616d6857a65e7b9b";
#endif
		Assert.Equal(ExpectedLength, manifest.Length);
		Assert.Equal(ExpectedHash, Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant());

		string text = Encoding.UTF8.GetString(manifest);
		Assert.EndsWith("\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
		Assert.StartsWith("METHOD|", text, StringComparison.Ordinal);

		AssertManifestMutationRejected(manifest, bytes => ReplaceFirst(bytes, "|ldarg.0\n", "|ldarg.1\n"), ExpectedHash);
		AssertManifestMutationRejected(manifest, bytes => ReplaceFirst(bytes, "|stloc.0\n", "|stloc.1\n"), ExpectedHash);
		AssertManifestMutationRejected(manifest, bytes => ReplaceFirst(bytes, "|ret\n", "|nop\n"), ExpectedHash);
		AssertManifestMutationRejected(manifest, bytes => MutateFirstBranchTarget(bytes), ExpectedHash);
	}

	[Fact]
	public void EveryProductionBodyHasExactPositiveManagedDependencyManifest()
	{
		foreach (MethodBase method in DeclaredBodies())
		{
			Assert.Empty(VerifyManagedSemanticBody(
				ManagedBodyView.FromMethod(method),
				ManagedBodyPolicy.ForProduction(method)));
		}

		MethodInfo create = GetMethod("Create", typeof(IReadOnlyList<LiquidWalletTransactionObservation>));
		Instruction[] createInstructions = ReadInstructions(create).ToArray();
		Assert.Equal(2, createInstructions.Count(instruction => instruction.OpCode == OpCodes.Add_Ovf));
		AssertCreateInstructionOrderAndControlFlow(create, createInstructions);
	}

	[Fact]
	public void SemanticGatesRejectIsolatedForbiddenChannels()
	{
		AssertPairedChannelPolicy(
			FixtureChannel.Field,
			nameof(ForbiddenChannelFixtures.AllowedFieldChannel),
			nameof(ForbiddenChannelFixtures.ForbiddenFieldChannel));
		AssertPairedChannelPolicy(
			FixtureChannel.Type,
			nameof(ForbiddenChannelFixtures.AllowedTypeChannel),
			nameof(ForbiddenChannelFixtures.ForbiddenTypeChannel));
		AssertPairedChannelPolicy(
			FixtureChannel.Token,
			nameof(ForbiddenChannelFixtures.AllowedTokenChannel),
			nameof(ForbiddenChannelFixtures.ForbiddenTokenChannel));
		AssertPairedChannelPolicy(
			FixtureChannel.Local,
			"AllowedLocalChannel",
			"ForbiddenLocalChannel");
		AssertPairedChannelPolicy(
			FixtureChannel.Catch,
			nameof(ForbiddenChannelFixtures.AllowedCatchChannel),
			nameof(ForbiddenChannelFixtures.ForbiddenCatchChannel));
		AssertPairedChannelPolicy(
			FixtureChannel.String,
			nameof(ForbiddenChannelFixtures.AllowedStringChannel),
			nameof(ForbiddenChannelFixtures.ForbiddenStringChannel));

		MethodInfo create = GetMethod("Create", typeof(IReadOnlyList<LiquidWalletTransactionObservation>));
		ManagedBodyView baseline = ManagedBodyView.FromMethod(create);
		ManagedBodyPolicy productionPolicy = ManagedBodyPolicy.ForProduction(create);

		ManagedBodyView extraString = baseline with
		{
			Instructions =
			[
				.. baseline.Instructions,
				new Instruction(10_000, 10_005, OpCodes.Ldstr, "transactions"),
			],
		};
		AssertBodyViolation(extraString, productionPolicy, "STRING_OPERAND");

		Instruction[] wrongCallOpcode = CloneInstructions(baseline.Instructions);
		int callIndex = Array.FindIndex(wrongCallOpcode, instruction =>
			instruction.OpCode == OpCodes.Call && instruction.OpCode.OperandType == OperandType.InlineMethod);
		Assert.True(callIndex >= 0);
		wrongCallOpcode[callIndex] = wrongCallOpcode[callIndex] with { OpCode = OpCodes.Callvirt };
		AssertBodyViolation(
			baseline with { Instructions = wrongCallOpcode },
			productionPolicy,
			"CALL_OPCODE");
	}

	[Fact]
	public void CreateValueFlowAndGuardVerifierRejectsIsolatedMutations()
	{
		MethodInfo create = GetMethod("Create", typeof(IReadOnlyList<LiquidWalletTransactionObservation>));
		Instruction[] baseline = ReadInstructions(create).ToArray();
		Assert.Empty(VerifyCreateValueAndGuardFlow(create, baseline));

		AssertCreateMutationRejected(
			create,
			baseline,
			WrongFirstNullArgument(create, baseline),
			"NULL_ORIGINAL_ARGUMENT");
		AssertCreateMutationRejected(
			create,
			baseline,
			ConflictingFirstNullName(create, baseline),
			"NULL_ORIGINAL_ARGUMENT");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverOriginalNullGuard(create, baseline),
			"NULL_ORIGINAL_ARGUMENT");
		AssertCreateMutationRejected(
			create,
			baseline,
			ConflictingCountReceiver(create, baseline),
			"COLLECTION_ORIGINAL_ARGUMENT");
		AssertCreateMutationRejected(
			create,
			baseline,
			ConflictingIndexerReceiver(create, baseline),
			"COLLECTION_ORIGINAL_ARGUMENT");
		AssertCreateMutationRejected(
			create,
			baseline,
			WrongCapturedNullArgument(create, baseline),
			"CAPTURED_ELEMENT_NULL_ARGUMENT");
		AssertCreateMutationRejected(
			create,
			baseline,
			ConflictingCapturedObservationStore(create, baseline),
			"CAPTURED_ELEMENT_ARRAY_STORE",
			"CAPTURED_ELEMENT_MEMBER_RECEIVER",
			"CAPTURED_ELEMENT_NULL_ARGUMENT");
		AssertCreateMutationRejected(
			create,
			baseline,
			DuplicateCapturedObservationStore(create, baseline),
			"CAPTURED_ELEMENT_CAPTURE");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCapturedNullGuard(create, baseline),
			"CAPTURED_ELEMENT_NULL_GUARD");
		AssertCreateMutationRejected(
			create,
			baseline,
			SubstituteExceptionArgument(create, baseline),
			"EXCEPTION_ARGUMENT_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCorrectExceptionArguments(create, baseline),
			"EXCEPTION_ARGUMENT_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			ReplaceInputGetterWithOutputGetter(create, baseline),
			"INPUT_ADD_GETTER");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCorrectObservationMemberReceiver(create, baseline),
			"CAPTURED_ELEMENT_MEMBER_RECEIVER");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCorrectArrayStoreValue(create, baseline),
			"CAPTURED_ELEMENT_ARRAY_STORE");
		AssertCreateMutationRejected(
			create,
			baseline,
			ReplaceAggregateLocalBeforeAdd(create, baseline),
			"INPUT_ADD_LOCAL");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCorrectInputAddProducers(create, baseline),
			"INPUT_ADD_GETTER");
		AssertCreateMutationRejected(
			create,
			baseline,
			ReplaceInputCapConstant(baseline),
			"INPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			DiscardCheckedInputResult(baseline),
			"INPUT_ADD_LOCAL");
		AssertCreateMutationRejected(
			create,
			baseline,
			StoreCheckedInputResultInStaleLocal(baseline),
			"INPUT_ADD_LOCAL");
		AssertCreateMutationRejected(
			create,
			baseline,
			OverwriteCheckedInputAggregateBeforeCap(create, baseline),
			"INPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			LoadSubstitutedInputAggregateForCap(create, baseline),
			"INPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			CompareUnrelatedInputCapOperand(create, baseline),
			"INPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			RetainDeadCorrectInputLoadButCompareSubstitute(create, baseline),
			"INPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCorrectInputCapProducers(create, baseline),
			"INPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			UnequalStackJoinBeforeInputCapProducers(create, baseline),
			"VALUE_FLOW");
		AssertCreateMutationRejected(
			create,
			baseline,
			InvertInputCapBranch(create, baseline),
			"INPUT_CAP_FAILURE_EDGE");
		AssertCreateMutationRejected(
			create,
			baseline,
			AppendUnrelatedCheckedAdd(baseline),
			"CHECKED_ADD_COUNT");
		AssertCreateMutationRejected(
			create,
			baseline,
			ReplaceOutputGetterWithUnrelatedGetter(create, baseline),
			"OUTPUT_ADD_GETTER");
		AssertCreateMutationRejected(
			create,
			baseline,
			ReplaceOutputAggregateLocalBeforeAdd(create, baseline),
			"OUTPUT_ADD_LOCAL");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCorrectOutputAddProducers(create, baseline),
			"INPUT_CAP_FAILURE_EDGE",
			"OUTPUT_ADD_GETTER");
		AssertCreateMutationRejected(
			create,
			baseline,
			ReplaceOutputCapConstant(baseline),
			"OUTPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			DiscardCheckedOutputResult(create, baseline),
			"OUTPUT_ADD_LOCAL");
		AssertCreateMutationRejected(
			create,
			baseline,
			StoreCheckedOutputResultInStaleLocal(create, baseline),
			"OUTPUT_ADD_LOCAL");
		AssertCreateMutationRejected(
			create,
			baseline,
			OverwriteCheckedOutputAggregateBeforeCap(create, baseline),
			"OUTPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			LoadSubstitutedOutputAggregateForCap(create, baseline),
			"OUTPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			CompareUnrelatedOutputCapOperand(create, baseline),
			"OUTPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			RetainDeadCorrectOutputLoadButCompareSubstitute(create, baseline),
			"OUTPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			BranchOverCorrectOutputCapProducers(create, baseline),
			"OUTPUT_CAP_BINDING");
		AssertCreateMutationRejected(
			create,
			baseline,
			UnequalStackJoinBeforeOutputCapProducers(create, baseline),
			"VALUE_FLOW");
		AssertCreateMutationRejected(
			create,
			baseline,
			InvertOutputCapBranch(create, baseline),
			"OUTPUT_CAP_FAILURE_EDGE");
		AssertCreateMutationRejected(
			create,
			baseline,
			SwapAggregateCapExceptionMessages(create, baseline),
			"INPUT_CAP_FAILURE_EDGE",
			"OUTPUT_CAP_FAILURE_EDGE");
		AssertGuardMutationRejected(
			create,
			baseline,
			InsertPreGuardAllocation(create, baseline),
			"COUNT_GUARD_DOMINANCE");
		AssertGuardMutationRejected(
			create,
			baseline,
			InsertPreGuardIndexer(create, baseline),
			"COUNT_GUARD_DOMINANCE");
		AssertGuardMutationRejected(
			create,
			baseline,
			MoveOutputWorkBeforeInputCap(create, baseline),
			"INPUT_CAP_DOMINANCE");
		AssertGuardMutationRejected(
			create,
			baseline,
			ContinueInputCapFailureToConstruction(create, baseline),
			"INPUT_CAP_FAILURE_EDGE");
		AssertGuardMutationRejected(
			create,
			baseline,
			MoveTransactionIdWorkBeforeOutputCap(create, baseline),
			"OUTPUT_CAP_DOMINANCE");
		AssertGuardMutationRejected(
			create,
			baseline,
			ContinueOutputCapFailureToConstruction(create, baseline),
			"OUTPUT_CAP_FAILURE_EDGE");
		AssertGuardMutationRejected(
			create,
			baseline,
			ReplaceBranchWithUnresolvedTarget(baseline),
			"CFG_INVALID",
			requireConsistentStack: false);
		AssertGuardMutationRejected(
			create,
			baseline,
			DuplicateInstructionOffset(baseline),
			"CFG_INVALID",
			requireConsistentStack: false);
	}

	[Fact]
	public void DecodedInstructionMutationsUseTheProductionNormalizer()
	{
		MethodInfo create = GetMethod("Create", typeof(IReadOnlyList<LiquidWalletTransactionObservation>));
		Instruction[] baseline = ReadInstructions(create).ToArray();
		byte[] baselineManifest = NormalizeMethodInstructions(create, baseline);

		MethodInfo tailFixture = CreateTailPrefixFixture();
		Instruction[] prefixed = ReadInstructions(tailFixture).ToArray();
		int tail = Assert.Single(prefixed
			.Select((instruction, index) => (instruction, index))
			.Where(pair => pair.instruction.OpCode == OpCodes.Tailcall)
			.Select(pair => pair.index));
		Assert.True(tail + 2 < prefixed.Length);
		Assert.Equal(OpCodes.Callvirt, prefixed[tail + 1].OpCode);
		Assert.Equal(prefixed[tail].EndOffset, prefixed[tail + 1].Offset);
		Assert.Equal(prefixed[tail + 1].EndOffset, prefixed[tail + 2].Offset);
		Assert.Equal(OpCodes.Ret, prefixed[tail + 2].OpCode);
		Assert.Equal(
			typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes),
			ResolveMember(tailFixture, prefixed[tail + 1]));
		Instruction[] withoutPrefix = [.. prefixed[..tail], .. prefixed[(tail + 1)..]];
		byte[] withoutPrefixManifest = NormalizeMethodInstructions(tailFixture, withoutPrefix);
		AssertNormalizedMutationRejected(tailFixture, withoutPrefixManifest, prefixed, OpCodes.Tailcall);

		Instruction[] changedArgument = CloneInstructions(baseline);
		int argumentIndex = Array.FindIndex(changedArgument, instruction => instruction.OpCode == OpCodes.Ldarg_0);
		Assert.True(argumentIndex >= 0);
		changedArgument[argumentIndex] = changedArgument[argumentIndex] with { OpCode = OpCodes.Ldarg_1 };
		AssertNormalizedMutationRejected(create, baselineManifest, changedArgument, OpCodes.Ldarg_1);

		Instruction[] changedLocal = CloneInstructions(baseline);
		int localIndex = Array.FindIndex(changedLocal, instruction => TryGetLocalIndex(instruction, load: true, out _));
		Assert.True(localIndex >= 0);
		changedLocal[localIndex] = changedLocal[localIndex] with { OpCode = OpCodes.Ldloc, Operand = 19 };
		AssertNormalizedMutationRejected(create, baselineManifest, changedLocal, OpCodes.Ldloc);

		Instruction[] changedBranch = CloneInstructions(baseline);
		int branchIndex = Array.FindIndex(changedBranch, instruction =>
			instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch && instruction.Operand is int);
		Assert.True(branchIndex >= 0);
		int alternateTarget = baseline.First(instruction => instruction.Offset != (int)changedBranch[branchIndex].Operand!).Offset;
		changedBranch[branchIndex] = changedBranch[branchIndex] with { Operand = alternateTarget };
		AssertNormalizedMutationRejected(create, baselineManifest, changedBranch, changedBranch[branchIndex].OpCode);

		Instruction[] changedOperandless = CloneInstructions(baseline);
		int returnIndex = Array.FindLastIndex(changedOperandless, instruction => instruction.OpCode == OpCodes.Ret);
		Assert.True(returnIndex >= 0);
		changedOperandless[returnIndex] = changedOperandless[returnIndex] with { OpCode = OpCodes.Nop };
		AssertNormalizedMutationRejected(create, baselineManifest, changedOperandless, OpCodes.Nop);
	}

	[Fact]
	public void ManagedBodyMetadataGateRejectsIsolatedBodyControls()
	{
		MethodInfo validCall = CreateManagedBodyFixture(ManagedBodyFixtureKind.ValidCall);
		ManagedBodyPolicy policy = ManagedBodyPolicy.ForFixture(validCall);
		ManagedBodyView valid = ManagedBodyView.FromMethod(validCall);
		Assert.Empty(VerifyManagedSemanticBody(valid, policy));
		AssertBodyViolation(valid with { HasBody = false }, policy, "BODY_REQUIRED");
		AssertBodyViolation(valid with { InitLocals = !valid.InitLocals }, policy, "INIT_LOCALS");

		MethodInfo extraLocalMethod = CreateManagedBodyFixture(ManagedBodyFixtureKind.ExtraLocal);
		ManagedBodyView extraLocal = ManagedBodyView.FromMethod(extraLocalMethod);
		ManagedLocal unpinnedArrayLocal = Assert.Single(extraLocal.Locals);
		Assert.Equal(TypeKey(typeof(LiquidWalletTransactionObservation[])), unpinnedArrayLocal.Type);
		Assert.False(unpinnedArrayLocal.IsPinned);
		ManagedBodyPolicy extraLocalPolicy = ManagedBodyPolicy.ForFixture(extraLocalMethod);
		Assert.Empty(VerifyManagedSemanticBody(extraLocal, extraLocalPolicy));
		AssertBodyViolation(extraLocal, policy, "LOCAL_TYPE");

		MethodInfo pinnedLocalMethod = CreateManagedBodyFixture(ManagedBodyFixtureKind.PinnedLocal);
		ManagedBodyView pinnedLocal = ManagedBodyView.FromMethod(pinnedLocalMethod);
		ManagedLocal pinnedArrayLocal = Assert.Single(pinnedLocal.Locals);
		Assert.Equal(unpinnedArrayLocal.Type, pinnedArrayLocal.Type);
		Assert.True(pinnedArrayLocal.IsPinned);
		Assert.Equal(extraLocal.Attributes, pinnedLocal.Attributes);
		Assert.Equal(extraLocal.Implementation, pinnedLocal.Implementation);
		Assert.Equal(extraLocal.HasBody, pinnedLocal.HasBody);
		Assert.Equal(extraLocal.InitLocals, pinnedLocal.InitLocals);
		Assert.Equal(extraLocal.Clauses, pinnedLocal.Clauses);
		Assert.Equal(extraLocal.Instructions, pinnedLocal.Instructions);
		Assert.Equal(extraLocal.Locals, pinnedLocal.Locals.Select(local => local with { IsPinned = false }));
		AssertBodyViolation(pinnedLocal, extraLocalPolicy, "PINNED_LOCAL");

		foreach ((ManagedBodyFixtureKind kind, ExceptionHandlingClauseOptions clauseKind, string code) in new[]
		{
			(ManagedBodyFixtureKind.Finally, ExceptionHandlingClauseOptions.Finally, "EXCEPTION_CLAUSE"),
			(ManagedBodyFixtureKind.Fault, ExceptionHandlingClauseOptions.Fault, "EXCEPTION_CLAUSE"),
			(ManagedBodyFixtureKind.Filter, ExceptionHandlingClauseOptions.Filter, "EXCEPTION_CLAUSE"),
		})
		{
			ManagedBodyView view = ManagedBodyView.FromMethod(CreateManagedBodyFixture(kind));
			Assert.Contains(view.Clauses, clause => clause == clauseKind);
			Assert.Empty(VerifyManagedSemanticBody(view with { Clauses = [] }, policy));
			AssertBodyViolation(view, policy, code);
		}

		ManagedBodyView wrongCall = ManagedBodyView.FromMethod(
			CreateManagedBodyFixture(ManagedBodyFixtureKind.WrongCallOpcode));
		Assert.Contains(wrongCall.Instructions, instruction => instruction.OpCode == OpCodes.Call);
		AssertBodyViolation(wrongCall, policy, "CALL_OPCODE");

		ManagedBodyView pinvoke = valid with { Attributes = valid.Attributes | MethodAttributes.PinvokeImpl };
		Assert.NotEqual((MethodAttributes)0, pinvoke.Attributes & MethodAttributes.PinvokeImpl);
		AssertBodyViolation(pinvoke, policy, "PINVOKE");

		ManagedBodyView unexpectedCallableFlags = valid with
		{
			Attributes = valid.Attributes | MethodAttributes.SpecialName,
		};
		Assert.NotEqual(valid.Attributes, unexpectedCallableFlags.Attributes);
		AssertBodyViolation(unexpectedCallableFlags, policy, "CALLABLE_FLAGS");

		foreach (MethodImplAttributes flag in new[]
		{
			MethodImplAttributes.Runtime,
			MethodImplAttributes.InternalCall,
			MethodImplAttributes.Native,
			MethodImplAttributes.Unmanaged,
		})
		{
			ManagedBodyView forbiddenImplementation = valid with { Implementation = valid.Implementation | flag };
			Assert.NotEqual((MethodImplAttributes)0, forbiddenImplementation.Implementation & flag);
			AssertBodyViolation(forbiddenImplementation, policy, "IMPLEMENTATION_FLAGS");
		}

		Instruction[] unresolved = CloneInstructions(valid.Instructions);
		int call = Array.FindIndex(unresolved, instruction => instruction.OpCode.OperandType == OperandType.InlineMethod);
		unresolved[call] = unresolved[call] with { Operand = int.MaxValue };
		ManagedBodyView unresolvedView = valid with { Instructions = unresolved };
		Assert.Contains(unresolvedView.Instructions, instruction => instruction.Operand is int token && token == int.MaxValue);
		AssertBodyViolation(unresolvedView, policy, "UNRESOLVED_TOKEN");

		Instruction[] signature = [.. valid.Instructions, new Instruction(10_000, 10_005, OpCodes.Calli, 0x11000001)];
		ManagedBodyView signatureView = valid with { Instructions = signature };
		Assert.Contains(signatureView.Instructions, instruction => instruction.OpCode.OperandType == OperandType.InlineSig);
		AssertBodyViolation(signatureView, policy, "SIGNATURE_OPERAND");
	}

	[Fact]
	public void RawMetadataRowsAndSignaturesAreClosedAndModifierFree()
	{
		byte[] assemblyBytes = File.ReadAllBytes(typeof(LiquidWalletObservationBatch).Assembly.Location);
		IReadOnlyList<string> productionRawViolations = VerifyRawPeMetadata(
			assemblyBytes,
			typeof(LiquidWalletObservationBatch).Namespace!,
			typeof(LiquidWalletObservationBatch).Name);
		Assert.True(
			productionRawViolations.Count == 0,
			string.Join(',', productionRawViolations));
		using var stream = new MemoryStream(assemblyBytes, writable: false);
		using var peReader = new PEReader(stream);
		MetadataReader reader = peReader.GetMetadataReader();
		TypeDefinitionHandle typeHandle = FindType(reader, typeof(LiquidWalletObservationBatch));
		TypeDefinition definition = reader.GetTypeDefinition(typeHandle);
		var provider = new ModifierRejectingTypeProvider(reader);

		Assert.Equal(TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, definition.Attributes);
		Assert.Equal(RawEntityTypeKey(typeof(object)), provider.DecodeEntityType(definition.BaseType));
		Assert.Empty(definition.GetGenericParameters());
		Assert.Empty(definition.GetEvents());
		TypeLayout layout = definition.GetLayout();
		Assert.Equal(0, layout.PackingSize);
		Assert.Equal(0, layout.Size);
		InterfaceImplementationHandle interfaceHandle = Assert.Single(definition.GetInterfaceImplementations());
		InterfaceImplementation implementation = reader.GetInterfaceImplementation(interfaceHandle);
		CustomAttributeHandle interfaceAttributeHandle = Assert.Single(implementation.GetCustomAttributes());
		CustomAttribute interfaceAttribute = reader.GetCustomAttribute(interfaceAttributeHandle);
		Assert.Equal(
			"System.Runtime.CompilerServices.NullableAttribute::.ctor",
			RawAttributeConstructorKey(reader, interfaceAttribute.Constructor));
		Assert.Equal("01000200000000010000", Convert.ToHexString(reader.GetBlobBytes(interfaceAttribute.Value)).ToLowerInvariant());
		Assert.Equal(RawTypeKey(typeof(IEquatable<LiquidWalletObservationBatch>)),
			provider.DecodeEntityType(implementation.Interface));
		Assert.Empty(definition.GetMethodImplementations());
		AssertExactSet(
			RawAttributeLocationManifest(reader, definition, provider),
			ExpectedRawAttributeLocationManifest());

		foreach (FieldDefinitionHandle handle in definition.GetFields())
		{
			FieldDefinition field = reader.GetFieldDefinition(handle);
			BlobReader blob = reader.GetBlobReader(field.Signature);
			string fieldType = new SignatureDecoder<string, object?>(provider, reader, null)
				.DecodeFieldSignature(ref blob);
			Assert.Equal(0, blob.RemainingBytes);
			FieldInfo reflectedField = typeof(LiquidWalletObservationBatch).GetField(
				reader.GetString(field.Name),
				DeclaredMemberFlags)!;
			Assert.NotNull(reflectedField);
			Assert.Equal(RawTypeKey(reflectedField.FieldType), fieldType);
			Assert.Equal(reflectedField.Attributes, field.Attributes);
		}

		IReadOnlyDictionary<EntityHandle, MemberInfo> expectedReachable = ExpectedReachableMembers();
		var reachable = new HashSet<EntityHandle>();
		foreach (MethodDefinitionHandle handle in definition.GetMethods())
		{
			MethodDefinition method = reader.GetMethodDefinition(handle);
			BlobReader blob = reader.GetBlobReader(method.Signature);
			MethodSignature<string> signature = new SignatureDecoder<string, object?>(provider, reader, null)
				.DecodeMethodSignature(ref blob);
			Assert.Equal(0, blob.RemainingBytes);
			Assert.Equal(SignatureCallingConvention.Default, signature.Header.CallingConvention);
			Assert.False(signature.Header.HasExplicitThis);
			Assert.False(signature.Header.IsGeneric);
			Assert.Equal(0, signature.GenericParameterCount);
			Assert.Equal(signature.ParameterTypes.Length, signature.RequiredParameterCount);
			MethodBase reflectedMethod = FindReflectedCallable(
				reader.GetString(method.Name),
				signature.ParameterTypes);
			Assert.Equal(!reflectedMethod.IsStatic, signature.Header.IsInstance);
			Assert.Equal(RawReturnTypeKey(reflectedMethod), signature.ReturnType);
			Assert.Equal(
				reflectedMethod.GetParameters().Select(parameter => RawTypeKey(parameter.ParameterType)),
				signature.ParameterTypes);
			Assert.Equal(reflectedMethod.Attributes, method.Attributes);
			Assert.Equal(reflectedMethod.MethodImplementationFlags, method.ImplAttributes);
			Assert.Empty(method.GetGenericParameters());

			if (method.RelativeVirtualAddress != 0)
			{
				MethodBodyBlock body = peReader.GetMethodBody(method.RelativeVirtualAddress);
				Assert.Empty(body.ExceptionRegions);
				Assert.Equal(reflectedMethod.GetMethodBody()!.InitLocals, body.LocalVariablesInitialized);
				if (!body.LocalSignature.IsNil)
				{
					StandaloneSignature local = reader.GetStandaloneSignature(body.LocalSignature);
					BlobReader localBlob = reader.GetBlobReader(local.Signature);
					ImmutableArray<string> localTypes = new SignatureDecoder<string, object?>(provider, reader, null)
						.DecodeLocalSignature(ref localBlob);
					Assert.Equal(0, localBlob.RemainingBytes);
					Assert.Equal(
						reflectedMethod.GetMethodBody()!.LocalVariables.Select(localVariable => RawTypeKey(localVariable.LocalType)),
						localTypes);
				}
				CollectReferencedHandles(
					body.GetILBytes() ?? throw new InvalidOperationException("A managed IL body is required."),
					reachable);
			}
		}

		foreach (PropertyDefinitionHandle handle in definition.GetProperties())
		{
			PropertyDefinition property = reader.GetPropertyDefinition(handle);
			BlobReader blob = reader.GetBlobReader(property.Signature);
			MethodSignature<string> signature = new SignatureDecoder<string, object?>(provider, reader, null)
				.DecodeMethodSignature(ref blob);
			Assert.Equal(0, blob.RemainingBytes);
			Assert.Equal(SignatureKind.Property, signature.Header.Kind);
			Assert.True(signature.Header.IsInstance);
			Assert.Empty(signature.ParameterTypes);
			Assert.Equal(0, signature.RequiredParameterCount);
			PropertyInfo reflectedProperty = typeof(LiquidWalletObservationBatch).GetProperty(
				reader.GetString(property.Name),
				DeclaredMemberFlags)!;
			Assert.NotNull(reflectedProperty);
			Assert.Equal(RawTypeKey(reflectedProperty.PropertyType), signature.ReturnType);
			Assert.Equal(reflectedProperty.Attributes, property.Attributes);
			PropertyAccessors accessors = property.GetAccessors();
			Assert.False(accessors.Getter.IsNil);
			Assert.True(accessors.Setter.IsNil);
			Assert.Empty(accessors.Others);
		}

		Assert.Equal(expectedReachable.Keys.OrderBy(MetadataToken), reachable.OrderBy(MetadataToken));
		foreach (EntityHandle handle in reachable)
		{
			DecodeReachableSignature(reader, provider, handle, expectedReachable[handle]);
		}

		Assert.Empty(provider.Modifiers);
		Assert.DoesNotContain(provider.Types, ContainsForbiddenRawTypeShape);
	}

	[Fact]
	public void RawSignatureGateRejectsMalformedHeadersModifiersAndNestedTypes()
	{
		RawSignaturePolicy method = new(RawSignatureKind.Method, IsInstance: false, GenericArity: 0, ParameterCount: 0);
		RawSignaturePolicy genericMethod = method with { GenericArity = 1 };
		RawSignaturePolicy instanceMember = method with { IsInstance = true };
		RawSignaturePolicy field = new(RawSignatureKind.Field, false, 0, 0);
		RawSignaturePolicy property = new(RawSignatureKind.Property, true, 0, 0);
		RawSignaturePolicy local = new(RawSignatureKind.Local, false, 0, 1);
		RawSignaturePolicy methodSpec = new(RawSignatureKind.MethodSpecification, false, 1, 0);
		RawSignaturePolicy type = new(RawSignatureKind.Type, false, 0, 0);

		AssertRawSignatureAccepted(method, [0x00, 0x00, 0x01]);
		AssertRawSignatureAccepted(genericMethod, [0x10, 0x01, 0x00, 0x01]);
		AssertRawSignatureAccepted(instanceMember, [0x20, 0x00, 0x01]);
		AssertRawSignatureAccepted(field, [0x06, 0x08]);
		AssertRawSignatureAccepted(property, [0x28, 0x00, 0x08]);
		AssertRawSignatureAccepted(local, [0x07, 0x01, 0x08]);
		AssertRawSignatureAccepted(methodSpec, [0x0a, 0x01, 0x08]);
		AssertRawSignatureAccepted(type, [0x08]);
		AssertRawSignatureAccepted(field, [0x06, 0x14, 0x08, 0x01, 0x00, 0x01, 0x7f]);
		AssertRawSignatureAccepted(type, [0x11, 0x05]);

		AssertRawSignatureRejected(method, [0x01, 0x00, 0x01], "UNMANAGED_CONVENTION");
		AssertRawSignatureRejected(instanceMember, [0x21, 0x00, 0x01], "UNMANAGED_CONVENTION");
		AssertRawSignatureRejected(field, [0x26, 0x08], "FIELD_HEADER");
		AssertRawSignatureRejected(property, [0x20, 0x00, 0x08], "PROPERTY_HEADER");
		AssertRawSignatureRejected(method, [0x40, 0x00, 0x01], "EXPLICIT_THIS");
		AssertRawSignatureRejected(method, [0x80, 0x00, 0x01], "METHOD_HEADER");
		AssertRawSignatureRejected(method, [0x20, 0x00, 0x01], "INSTANCE_BIT");
		AssertRawSignatureRejected(method, [0x05, 0x00, 0x01], "VARARGS");
		AssertRawSignatureRejected(method, [0x00, 0x01, 0x01, 0x41], "SENTINEL");
		AssertRawSignatureRejected(method, [0x00, 0x00, 0x01, 0x00], "TRAILING_DATA");
		AssertRawSignatureRejected(methodSpec with { GenericArity = 2 }, [0x0a, 0x01, 0x08], "GENERIC_ARITY");
		AssertRawSignatureRejected(methodSpec, [0x0a, 0x01, 0x08, 0x00], "TRAILING_DATA");
		AssertRawSignatureRejected(method, [0x10, 0x01, 0x00, 0x01], "GENERIC_ARITY");
		AssertRawSignatureRejected(method, [0x10, 0x00, 0x00, 0x01], "GENERIC_BIT");
		AssertRawSignatureRejected(genericMethod, [0x00, 0x00, 0x01], "GENERIC_BIT");
		AssertRawSignatureRejected(method, [0x00, 0x01, 0x01], "MALFORMED_SIGNATURE");
		AssertRawSignatureRejected(property, [0x38, 0x00, 0x08], "PROPERTY_HEADER");
		AssertRawSignatureRejected(property, [0xa8, 0x00, 0x08], "PROPERTY_HEADER");
		AssertRawSignatureRejectedExactly(
			field,
			[0x06, 0x14, 0x08, 0x80, 0x01, 0x00, 0x00],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			field,
			[0x06, 0x14, 0x08, 0xc0, 0x00, 0x00, 0x01, 0x00, 0x00],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			field,
			[0x06, 0x14, 0x08, 0x01, 0x80, 0x00, 0x00],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			field,
			[0x06, 0x14, 0x08, 0x01, 0x01, 0x80, 0x01, 0x00],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			field,
			[0x06, 0x14, 0x08, 0x01, 0x00, 0x80, 0x00],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			field,
			[0x06, 0x14, 0x08, 0x01, 0x00, 0x01, 0xbf, 0xff],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			type,
			[0x11, 0x80, 0x05],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			type,
			[0x11, 0xc0, 0x00, 0x00, 0x05],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejectedExactly(
			genericMethod,
			[0x10, 0x80, 0x01, 0x00, 0x01],
			"NON_CANONICAL_COMPRESSED_INTEGER");
		AssertRawSignatureRejected(type, [0x11, 0xe0], "MALFORMED_SIGNATURE");

		foreach ((RawSignaturePolicy policy, byte[] bytes) in new[]
		{
			(field, new byte[] { 0x06, 0x1f, 0x01, 0x08 }),
			(property, new byte[] { 0x28, 0x00, 0x1f, 0x01, 0x08 }),
			(local, new byte[] { 0x07, 0x01, 0x1f, 0x01, 0x08 }),
			(method, new byte[] { 0x00, 0x00, 0x1f, 0x01, 0x08 }),
			(instanceMember, new byte[] { 0x20, 0x00, 0x1f, 0x01, 0x08 }),
			(methodSpec, new byte[] { 0x0a, 0x01, 0x1f, 0x01, 0x08 }),
			(type, new byte[] { 0x1f, 0x01, 0x08 }),
			(type, new byte[] { 0x1d, 0x1f, 0x01, 0x08 }),
		})
		{
			Assert.Contains((byte)0x1f, bytes);
			AssertRawSignatureRejected(policy, bytes, "CUSTOM_MODIFIER");
		}

		AssertRawSignatureRejected(type, [0x1b, 0x00, 0x00, 0x01], "FUNCTION_POINTER");
		AssertRawSignatureRejected(type, [0x0f, 0x08], "POINTER_TYPE");
		AssertRawSignatureRejected(type, [0x10, 0x08], "BYREF_TYPE");
		AssertRawSignatureRejected(type, [0x1d], "MALFORMED_SIGNATURE");
	}

	[Fact]
	public void RawInterfaceAnnotationGateRejectsWrongOrTypeCarryingAttributes()
	{
		var valid = new RawInterfaceAttributeView(
			TypeKey(typeof(IEquatable<LiquidWalletObservationBatch>)),
			[RawNullableInterfaceAttribute]);
		Assert.Empty(VerifyRawInterfaceAttribute(valid));

		AssertRawInterfaceViolation(
			valid with { InterfaceType = TypeKey(typeof(IDisposable)) },
			"INTERFACE_TYPE");
		AssertRawInterfaceViolation(valid with { Attributes = [] }, "INTERFACE_ATTRIBUTE_COUNT");
		AssertRawInterfaceViolation(
			valid with { Attributes = [RawNullableInterfaceAttribute, RawNullableInterfaceAttribute] },
			"INTERFACE_ATTRIBUTE_COUNT");
		AssertRawInterfaceViolation(
			valid with
			{
				Attributes =
				[
					RawNullableInterfaceAttribute with
					{
						Blob = Convert.FromHexString("01000200000000010100"),
					},
				],
			},
			"INTERFACE_NULLABILITY");
		AssertRawInterfaceViolation(
			valid with
			{
				Attributes =
				[
					new RawAttributeView(
						$"{TypeKey(typeof(TypeCarrierAttribute))}::.ctor",
						Convert.FromHexString("01000000")),
				],
			},
			"INTERFACE_ATTRIBUTE_CONSTRUCTOR");
	}

	[Fact]
	public void RawTypeGraphGateRejectsCyclesUnresolvedRootsAndNestedModifiers()
	{
		var valid = new RawTypeGraph(
			Root: 1,
			Nodes: new Dictionary<int, RawTypeNode>
			{
				[1] = new(2, IsModified: false),
				[2] = new(null, IsModified: false),
			});
		Assert.Empty(VerifyRawTypeGraph(valid));

		AssertRawTypeGraphViolation(valid with { Root = 99 }, "UNRESOLVED_TYPE_ROOT");
		AssertRawTypeGraphViolation(
			valid with { Nodes = new Dictionary<int, RawTypeNode> { [1] = new(99, false) } },
			"UNRESOLVED_TYPE_ROOT");
		AssertRawTypeGraphViolation(
			valid with
			{
				Nodes = new Dictionary<int, RawTypeNode>
				{
					[1] = new(2, false),
					[2] = new(1, false),
				},
			},
			"TYPE_SPEC_CYCLE");
		AssertRawTypeGraphViolation(
			valid with
			{
				Nodes = new Dictionary<int, RawTypeNode>
				{
					[1] = new(2, false),
					[2] = new(null, true),
				},
			},
			"CUSTOM_MODIFIER");
	}

	[Fact]
	public void InMemoryRawPeControlsPassThroughTheMetadataFirstGate()
	{
		byte[] valid = BuildRawPeFixture(RawPeMutation.None);
		AssertRawPeMutationPresent(valid, RawPeMutation.None);
		Assert.Empty(VerifyRawPeMetadata(valid, "RawFixture", "ObservationBatchFixture"));
		byte[] validInstance = BuildRawPeFixture(RawPeMutation.InstanceMethodDefinition);
		AssertRawPeMutationPresent(validInstance, RawPeMutation.InstanceMethodDefinition);
		Assert.Empty(VerifyRawPeMetadata(
			validInstance,
			"RawFixture",
			"ObservationBatchFixture",
			RawPeMutation.InstanceMethodDefinition));
		byte[] classLayout = BuildRawPeFixture(RawPeMutation.ClassLayout);
		Assert.Equal(RawPeSubjectMemberShape(valid), RawPeSubjectMemberShape(classLayout));
		foreach (RawPeMutation validSibling in new[]
		{
			RawPeMutation.LiteralFieldDefinition,
			RawPeMutation.ParameterizedMethodDefinition,
			RawPeMutation.ReturnParameterDefinition,
			RawPeMutation.MethodTypeSpecObject,
			RawPeMutation.FieldSzArray,
			RawPeMutation.FieldMdArrayRankOne,
			RawPeMutation.MethodGenericConstraintObject,
			RawPeMutation.MethodGenericConstraintTypeSpecObject,
			RawPeMutation.NestedTypeReferenceScope,
			RawPeMutation.NestedTypeDefinitionScope,
		})
		{
			byte[] sibling = BuildRawPeFixture(validSibling);
			AssertRawPeMutationPresent(sibling, validSibling);
			IReadOnlyList<string> siblingViolations = VerifyRawPeMetadata(
				sibling,
				"RawFixture",
				"ObservationBatchFixture",
				validSibling);
			Assert.True(
				siblingViolations.Count == 0,
				$"{validSibling} reported {string.Join(',', siblingViolations)}.");
		}
		using (var stream = new MemoryStream(valid, writable: false))
		using (var peReader = new PEReader(stream))
		{
			MetadataReader reader = peReader.GetMetadataReader();
			MemberReferenceHandle unmapped = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.MemberRef))
				.Select(MetadataTokens.MemberReferenceHandle)
				.Single(handle => reader.GetString(reader.GetMemberReference(handle).Name) == "GenericTarget");
			var violations = new HashSet<string>(StringComparer.Ordinal);
			ValidateRawPeHandle(
				reader,
				new ModifierRejectingTypeProvider(reader),
				unmapped,
				null,
				new Dictionary<EntityHandle, MemberInfo>(),
				RawPePolicyMode.Production,
				violations);
			Assert.Equal(["UNMAPPED_REACHABLE_HANDLE"], violations);

			TypeDefinition definition = reader.GetTypeDefinition(FindType(reader, "RawFixture", "ObservationBatchFixture"));
			CustomAttributeHandle unmappedAttribute = reader.GetInterfaceImplementation(
				definition.GetInterfaceImplementations().Single()).GetCustomAttributes().Single();
			violations.Clear();
			ValidateRawCustomAttribute(
				reader,
				new ModifierRejectingTypeProvider(reader),
				unmappedAttribute,
				new Dictionary<EntityHandle, MemberInfo>(),
				RawPePolicyMode.Production,
				violations);
			Assert.Equal(["UNMAPPED_REACHABLE_HANDLE"], violations);
		}

		foreach ((RawPeMutation mutation, string expectedViolation) in new[]
		{
			(RawPeMutation.BaseTypeModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.MethodTypeSpecModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.MethodTypeSpecTrailingData, "TRAILING_DATA"),
			(RawPeMutation.MethodTypeSpecCycle, "TYPE_SPEC_CYCLE"),
			(RawPeMutation.MethodTypeSpecNestedCycleAttribute, "TYPE_SPEC_CYCLE"),
			(RawPeMutation.MethodTypeSpecUnresolved, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.InterfaceTypeSpecModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.MemberReferenceParentModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.FieldModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.PropertyModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.LocalModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.LocalModifierInt64, "LOCAL_TYPE"),
			(RawPeMutation.MethodModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.MethodPrimitiveInt32AsTypeReference, "UNAPPROVED_TYPE"),
			(RawPeMutation.MethodObjectAsValueType, "UNAPPROVED_TYPE"),
			(RawPeMutation.MethodTypeSpecObjectAsValueType, "UNAPPROVED_TYPE"),
			(RawPeMutation.MethodMemberReferenceModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.MethodMemberReferenceTypeSpecObject, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.MethodMemberReferenceTypeSpecObjectAsValueType, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.FieldMemberReferenceModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.FieldPrimitiveInt32AsTypeReference, "UNAPPROVED_TYPE"),
			(RawPeMutation.FieldSzArrayPrimitiveInt32AsTypeReference, "UNAPPROVED_TYPE"),
			(RawPeMutation.FieldMdArrayPrimitiveInt32AsTypeReference, "UNAPPROVED_TYPE"),
			(RawPeMutation.FieldTypeSpecInt32, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.FieldTypeSpecInt32AsClass, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.FieldMemberReferenceTypeSpecInt32, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.FieldMemberReferenceTypeSpecInt32AsClass, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.FieldSzArrayAsMdRankOne, "UNAPPROVED_TYPE"),
			(RawPeMutation.FieldMdArrayExplicitSize, "UNAPPROVED_TYPE"),
			(RawPeMutation.FieldMdArrayLowerBound, "UNAPPROVED_TYPE"),
			(RawPeMutation.WrongInterfaceNullableArgument, "INTERFACE_NULLABILITY"),
			(RawPeMutation.WrongInterfaceAttributeConstructor, "INTERFACE_ATTRIBUTE_CONSTRUCTOR"),
			(RawPeMutation.TypeCarryingInterfaceAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingInterfaceNamedAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingInterfaceArrayAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingTypeAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingFieldAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingConstructorAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingMethodArrayAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingMethodArrayWrongTokenObservationAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingMethodExactObservationAttribute, "CUSTOM_ATTRIBUTE"),
			(RawPeMutation.TypeCarryingMethodUnqualifiedObservationAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingMethodCounterfeitObservationAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingReturnAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingParameterAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingPropertyNamedAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingPropertyNamedWrongVersionObservationAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingEventAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingGenericParameterAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingGenericConstraintAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingStandaloneSignatureAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingMemberReferenceAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingMethodSpecificationAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingTypeSpecificationAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.TypeCarryingTypeReferenceAttribute, "EMBEDDED_TYPE"),
			(RawPeMutation.MethodImplementation, "METHOD_IMPL_ROW"),
			(RawPeMutation.UnmanagedMethodDefinition, "UNMANAGED_CONVENTION"),
			(RawPeMutation.ReservedMethodHeader, "METHOD_HEADER"),
			(RawPeMutation.ZeroArityGenericBitMethodDefinition, "GENERIC_BIT"),
			(RawPeMutation.MissingGenericBitMethodDefinition, "GENERIC_BIT"),
			(RawPeMutation.SelfConsistentGenericMethodDefinition, "GENERIC_ARITY"),
			(RawPeMutation.UnexpectedMethodAttributes, "CALLABLE_FLAGS"),
			(RawPeMutation.UnmanagedMethodMemberReference, "UNMANAGED_CONVENTION"),
			(RawPeMutation.UnauthorizedMethodMemberReferenceType, "UNAPPROVED_TYPE"),
			(RawPeMutation.UnauthorizedMethodMemberReferenceReturnType, "UNAPPROVED_TYPE"),
			(RawPeMutation.UnauthorizedFieldMemberReferenceType, "UNAPPROVED_TYPE"),
			(RawPeMutation.FieldMemberReferenceIntAsClass, "UNAPPROVED_TYPE"),
			(RawPeMutation.UnauthorizedMethodSpecificationArgument, "UNAPPROVED_TYPE"),
			(RawPeMutation.MethodBitsOnFieldMemberReference, "FIELD_HEADER"),
			(RawPeMutation.MalformedPropertyHeader, "PROPERTY_HEADER"),
			(RawPeMutation.GenericPropertyHeader, "PROPERTY_HEADER"),
			(RawPeMutation.ReservedPropertyHeader, "PROPERTY_HEADER"),
			(RawPeMutation.MalformedMethodSpecificationHeader, "METHOD_SPEC_HEADER"),
			(RawPeMutation.MethodSpecificationArityMismatch, "GENERIC_ARITY"),
			(RawPeMutation.MethodSpecificationTrailingData, "TRAILING_DATA"),
			(RawPeMutation.MethodSpecificationNonGenericParent, "METHOD_SPEC_PARENT"),
			(RawPeMutation.MethodSpecificationZeroArityGenericParent, "METHOD_SPEC_PARENT"),
			(RawPeMutation.VarArgsMethodDefinition, "VARARGS"),
			(RawPeMutation.ExplicitThisMethodDefinition, "EXPLICIT_THIS"),
			(RawPeMutation.ClassLayout, "CLASS_LAYOUT"),
			(RawPeMutation.NotSerializedField, "FIELD_FLAGS"),
			(RawPeMutation.MutatedLiteralField, "LITERAL_VALUE"),
			(RawPeMutation.MarshaledField, "FIELD_MARSHAL"),
			(RawPeMutation.SynchronizedMethod, "IMPLEMENTATION_FLAGS"),
			(RawPeMutation.WrongParameterName, "PARAMETER_METADATA"),
			(RawPeMutation.OptionalParameter, "PARAMETER_METADATA"),
			(RawPeMutation.DefaultParameter, "PARAMETER_METADATA"),
			(RawPeMutation.MarshaledParameter, "PARAMETER_MARSHAL"),
			(RawPeMutation.WrongReturnParameterName, "PARAMETER_METADATA"),
			(RawPeMutation.OptionalReturnParameter, "PARAMETER_METADATA"),
			(RawPeMutation.DefaultReturnParameter, "PARAMETER_METADATA"),
			(RawPeMutation.MarshaledReturnParameter, "PARAMETER_MARSHAL"),
			(RawPeMutation.UnexpectedPropertyAttributes, "PROPERTY_METADATA"),
			(RawPeMutation.MissingPropertyGetter, "PROPERTY_METADATA"),
			(RawPeMutation.WrongPropertyGetter, "PROPERTY_METADATA"),
			(RawPeMutation.SetterPropertySemantics, "PROPERTY_METADATA"),
			(RawPeMutation.OtherPropertySemantics, "PROPERTY_METADATA"),
			(RawPeMutation.MethodGenericConstraintForbidden, "UNAPPROVED_TYPE"),
			(RawPeMutation.MethodGenericConstraintModifier, "CUSTOM_MODIFIER"),
			(RawPeMutation.MethodGenericConstraintUnresolved, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.MethodGenericConstraintCycle, "TYPE_SPEC_CYCLE"),
			(RawPeMutation.MethodGenericConstraintTrailingData, "TRAILING_DATA"),
			(RawPeMutation.BaseTypeCycle, "TYPE_SPEC_CYCLE"),
			(RawPeMutation.BaseTypeUnresolved, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.TypeReferenceScopeCycle, "TYPE_SCOPE_CYCLE"),
			(RawPeMutation.TypeReferenceScopeUnresolved, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.TypeReferenceUnexpectedScope, "UNEXPECTED_TYPE_SCOPE"),
			(RawPeMutation.TopLevelNestedTypeReferenceAlias, "UNAPPROVED_TYPE"),
			(RawPeMutation.MethodGenericMetadataNameAlias, "UNAPPROVED_TYPE"),
			(RawPeMutation.AssemblyReferenceTypeAlias, "EXTENDS_ROOT"),
			(RawPeMutation.MethodAssemblyReferenceTypeAlias, "UNAPPROVED_TYPE"),
			(RawPeMutation.AssemblyReferenceCrossApproved, "EXTENDS_ROOT"),
			(RawPeMutation.MethodAssemblyReferenceCrossApproved, "UNAPPROVED_TYPE"),
			(RawPeMutation.LocalTypeDefinitionAlias, "EXTENDS_ROOT"),
			(RawPeMutation.MethodLocalTypeDefinitionAlias, "UNAPPROVED_TYPE"),
			(RawPeMutation.MethodLocalObservationTypeDefinitionAlias, "UNAPPROVED_TYPE"),
			(RawPeMutation.FieldLocalBatchTypeDefinitionAlias, "UNAPPROVED_TYPE"),
			(RawPeMutation.AssemblyReferenceWrongVersion, "EXTENDS_ROOT"),
			(RawPeMutation.AssemblyReferenceWrongCulture, "EXTENDS_ROOT"),
			(RawPeMutation.AssemblyReferenceLiteralNeutralCulture, "EXTENDS_ROOT"),
			(RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture, "UNAPPROVED_TYPE"),
			(RawPeMutation.AssemblyReferenceWrongToken, "EXTENDS_ROOT"),
			(RawPeMutation.AssemblyReferencePublicKey, "EXTENDS_ROOT"),
			(RawPeMutation.AssemblyReferenceRetargetable, "EXTENDS_ROOT"),
			(RawPeMutation.MethodAssemblyReferenceRetargetable, "UNAPPROVED_TYPE"),
			(RawPeMutation.AssemblyReferenceWindowsRuntime, "EXTENDS_ROOT"),
			(RawPeMutation.AssemblyReferenceHash, "EXTENDS_ROOT"),
			(RawPeMutation.ModuleReferenceTypeScope, "EXTENDS_ROOT"),
			(RawPeMutation.ModuleDefinitionTypeScope, "EXTENDS_ROOT"),
			(RawPeMutation.TypeDefinitionScopeCycle, "TYPE_SCOPE_CYCLE"),
			(RawPeMutation.TypeDefinitionScopeUnresolved, "UNRESOLVED_TYPE_ROOT"),
			(RawPeMutation.TypeDefinitionUnexpectedScope, "UNEXPECTED_TYPE_SCOPE"),
			(RawPeMutation.TypeCarryingAssemblyReferenceAttribute, "CUSTOM_ATTRIBUTE"),
			(RawPeMutation.TypeCarryingModuleReferenceAttribute, "CUSTOM_ATTRIBUTE"),
			(RawPeMutation.TypeCarryingModuleDefinitionAttribute, "CUSTOM_ATTRIBUTE"),
		})
		{
			byte[] malformed = BuildRawPeFixture(mutation);
			AssertRawPeMutationPresent(malformed, mutation);
			IReadOnlyList<string> violations = VerifyRawPeMetadata(
				malformed,
				"RawFixture",
				"ObservationBatchFixture",
				mutation);
			if (mutation is RawPeMutation.TypeCarryingGenericParameterAttribute or
				RawPeMutation.TypeCarryingGenericConstraintAttribute)
			{
				Assert.DoesNotContain("GENERIC_BIT", violations);
				Assert.DoesNotContain("GENERIC_ARITY", violations);
			}
			if (mutation == RawPeMutation.LocalModifierInt64)
			{
				Assert.Equal(["CUSTOM_MODIFIER", "LOCAL_TYPE"], violations);
			}
			else if (mutation is RawPeMutation.BaseTypeModifier or
				RawPeMutation.MethodTypeSpecModifier or
				RawPeMutation.InterfaceTypeSpecModifier or
				RawPeMutation.MemberReferenceParentModifier or
				RawPeMutation.FieldModifier or
				RawPeMutation.PropertyModifier or
				RawPeMutation.LocalModifier or
				RawPeMutation.MethodModifier or
				RawPeMutation.MethodMemberReferenceModifier or
				RawPeMutation.FieldMemberReferenceModifier or
				RawPeMutation.MethodGenericConstraintModifier)
			{
				Assert.Equal(["CUSTOM_MODIFIER"], violations);
			}
			else if (mutation == RawPeMutation.MethodTypeSpecNestedCycleAttribute)
			{
				Assert.Equal(["CUSTOM_ATTRIBUTE", "EMBEDDED_TYPE", "TYPE_SPEC_CYCLE"], violations);
			}
			else if (mutation == RawPeMutation.TypeCarryingMethodExactObservationAttribute)
			{
				Assert.Equal(["CUSTOM_ATTRIBUTE"], violations);
			}
			else if (mutation == RawPeMutation.WrongInterfaceAttributeConstructor)
			{
				Assert.Equal(
					["CUSTOM_ATTRIBUTE_CONSTRUCTOR", "INTERFACE_ATTRIBUTE_CONSTRUCTOR"],
					violations);
			}
			else if (mutation is RawPeMutation.TypeCarryingInterfaceAttribute or
				RawPeMutation.TypeCarryingInterfaceNamedAttribute or
				RawPeMutation.TypeCarryingInterfaceArrayAttribute)
			{
				Assert.Equal(["EMBEDDED_TYPE"], violations);
			}
			else if (mutation is RawPeMutation.TypeCarryingMethodUnqualifiedObservationAttribute or
				RawPeMutation.TypeCarryingMethodCounterfeitObservationAttribute or
				RawPeMutation.TypeCarryingMethodArrayWrongTokenObservationAttribute or
				RawPeMutation.TypeCarryingPropertyNamedWrongVersionObservationAttribute)
			{
				Assert.Equal(["CUSTOM_ATTRIBUTE", "EMBEDDED_TYPE"], violations);
			}
			else if (mutation == RawPeMutation.TypeReferenceScopeCycle)
			{
				Assert.Equal(["EXTENDS_ROOT", "TYPE_SCOPE_CYCLE"], violations);
			}
			else if (mutation == RawPeMutation.TypeReferenceScopeUnresolved)
			{
				Assert.Equal(["EXTENDS_ROOT", "UNRESOLVED_TYPE_ROOT"], violations);
			}
			else if (mutation == RawPeMutation.TypeReferenceUnexpectedScope)
			{
				Assert.Equal(["EXTENDS_ROOT", "UNEXPECTED_TYPE_SCOPE"], violations);
			}
			else if (mutation == RawPeMutation.TypeCarryingAssemblyReferenceAttribute)
			{
				Assert.Equal(["CUSTOM_ATTRIBUTE", "EMBEDDED_TYPE"], violations);
			}
			else if (mutation is RawPeMutation.TypeCarryingModuleReferenceAttribute or
				RawPeMutation.TypeCarryingModuleDefinitionAttribute)
			{
				Assert.Equal(["CUSTOM_ATTRIBUTE", "EMBEDDED_TYPE", "EXTENDS_ROOT"], violations);
			}
			else if (mutation is RawPeMutation.ZeroArityGenericBitMethodDefinition or
				RawPeMutation.MissingGenericBitMethodDefinition or
				RawPeMutation.UnexpectedMethodAttributes or
				RawPeMutation.ExplicitThisMethodDefinition or
				RawPeMutation.ClassLayout or
				RawPeMutation.NotSerializedField or
				RawPeMutation.MutatedLiteralField or
				RawPeMutation.MarshaledField or
				RawPeMutation.SynchronizedMethod or
				RawPeMutation.WrongParameterName or
				RawPeMutation.OptionalParameter or
				RawPeMutation.DefaultParameter or
				RawPeMutation.MarshaledParameter or
				RawPeMutation.WrongReturnParameterName or
				RawPeMutation.OptionalReturnParameter or
				RawPeMutation.DefaultReturnParameter or
				RawPeMutation.MarshaledReturnParameter or
				RawPeMutation.UnexpectedPropertyAttributes or
				RawPeMutation.MissingPropertyGetter or
				RawPeMutation.WrongPropertyGetter or
				RawPeMutation.SetterPropertySemantics or
				RawPeMutation.OtherPropertySemantics or
				RawPeMutation.MethodTypeSpecTrailingData or
				RawPeMutation.MethodTypeSpecCycle or
				RawPeMutation.MethodTypeSpecUnresolved or
				RawPeMutation.MethodPrimitiveInt32AsTypeReference or
				RawPeMutation.MethodObjectAsValueType or
				RawPeMutation.MethodTypeSpecObjectAsValueType or
				RawPeMutation.MethodGenericConstraintForbidden or
				RawPeMutation.MethodGenericConstraintUnresolved or
				RawPeMutation.MethodGenericConstraintCycle or
				RawPeMutation.MethodGenericConstraintTrailingData or
				RawPeMutation.UnauthorizedMethodMemberReferenceType or
				RawPeMutation.UnauthorizedMethodMemberReferenceReturnType or
				RawPeMutation.MethodMemberReferenceTypeSpecObject or
				RawPeMutation.MethodMemberReferenceTypeSpecObjectAsValueType or
				RawPeMutation.UnauthorizedFieldMemberReferenceType or
				RawPeMutation.FieldMemberReferenceIntAsClass or
				RawPeMutation.FieldPrimitiveInt32AsTypeReference or
				RawPeMutation.FieldSzArrayPrimitiveInt32AsTypeReference or
				RawPeMutation.FieldMdArrayPrimitiveInt32AsTypeReference or
				RawPeMutation.FieldTypeSpecInt32 or
				RawPeMutation.FieldTypeSpecInt32AsClass or
				RawPeMutation.FieldMemberReferenceTypeSpecInt32 or
				RawPeMutation.FieldMemberReferenceTypeSpecInt32AsClass or
				RawPeMutation.FieldSzArrayAsMdRankOne or
				RawPeMutation.FieldMdArrayExplicitSize or
				RawPeMutation.FieldMdArrayLowerBound or
				RawPeMutation.UnauthorizedMethodSpecificationArgument or
				RawPeMutation.MethodAssemblyReferenceTypeAlias or
				RawPeMutation.MethodAssemblyReferenceCrossApproved or
				RawPeMutation.MethodAssemblyReferenceRetargetable or
				RawPeMutation.MethodLocalTypeDefinitionAlias or
				RawPeMutation.MethodLocalObservationTypeDefinitionAlias or
				RawPeMutation.FieldLocalBatchTypeDefinitionAlias or
				RawPeMutation.ModuleReferenceTypeScope or
				RawPeMutation.ModuleDefinitionTypeScope or
				RawPeMutation.AssemblyReferenceTypeAlias or
				RawPeMutation.AssemblyReferenceCrossApproved or
				RawPeMutation.LocalTypeDefinitionAlias or
				RawPeMutation.AssemblyReferenceWrongVersion or
				RawPeMutation.AssemblyReferenceWrongCulture or
				RawPeMutation.AssemblyReferenceLiteralNeutralCulture or
				RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture or
				RawPeMutation.AssemblyReferenceWrongToken or
				RawPeMutation.AssemblyReferencePublicKey or
				RawPeMutation.AssemblyReferenceRetargetable or
				RawPeMutation.AssemblyReferenceWindowsRuntime or
				RawPeMutation.AssemblyReferenceHash or
				RawPeMutation.TopLevelNestedTypeReferenceAlias or
				RawPeMutation.MethodGenericMetadataNameAlias or
				RawPeMutation.TypeDefinitionScopeCycle or
				RawPeMutation.TypeDefinitionScopeUnresolved or
				RawPeMutation.TypeDefinitionUnexpectedScope)
			{
				Assert.True(
					violations.SequenceEqual([expectedViolation], StringComparer.Ordinal),
					$"{mutation} expected only {expectedViolation}; actual: {string.Join(',', violations)}.");
			}
			else
			{
				Assert.True(
					violations.Contains(expectedViolation, StringComparer.Ordinal),
					$"{mutation} did not report {expectedViolation}. Actual: {string.Join(',', violations)}");
			}
		}
		byte[] mixedTypeSpecCycle = BuildRawPeFixture(RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope);
		AssertRawPeMutationPresent(mixedTypeSpecCycle, RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope);
		Assert.Equal(
			["TYPE_SPEC_CYCLE", "UNEXPECTED_TYPE_SCOPE"],
			VerifyRawPeMetadata(
				mixedTypeSpecCycle,
				"RawFixture",
				"ObservationBatchFixture",
				RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope));
	}

	[Fact]
	public void ReflectionAndRawRowMetadataGatesRejectIsolatedControls()
	{
		Assert.Empty(VerifyReflectionMetadata(typeof(ValidReflectionMetadataFixture)));
		AssertReflectionViolation(typeof(NotSerializedMetadataFixture), "FIELD_FLAGS");
		AssertReflectionViolation(typeof(UnexpectedCallableFlagsFixture), "CALLABLE_FLAGS");
		AssertReflectionViolation(typeof(SynchronizedMetadataFixture), "IMPLEMENTATION_FLAGS");
		AssertReflectionViolation(typeof(LayoutMetadataFixture), "CLASS_LAYOUT");
		AssertReflectionViolation(typeof(OptionalParameterMetadataFixture), "PARAMETER_METADATA");
		AssertReflectionViolation(typeof(MarshalParameterMetadataFixture), "PARAMETER_MARSHAL");
		AssertReflectionViolation(typeof(IndexedPropertyMetadataFixture), "PROPERTY_METADATA");
		AssertReflectionViolation(typeof(MutatedLiteralMetadataFixture), "LITERAL_VALUE");
		AssertReflectionViolation(typeof(SerializableMetadataFixture), "TYPE_FLAGS");
		AssertReflectionViolation(typeof(VarArgsMetadataFixture), "CALLING_CONVENTION");
		AssertReflectionViolation(typeof(TypeCarryingMethodAttributeFixture), "CUSTOM_ATTRIBUTE");
		AssertReflectionViolation(typeof(TypeCarryingFieldAttributeFixture), "CUSTOM_ATTRIBUTE");
		AssertReflectionViolation(typeof(TypeCarryingConstructorAttributeFixture), "CUSTOM_ATTRIBUTE");
		AssertReflectionViolation(typeof(TypeCarryingPropertyAttributeFixture), "CUSTOM_ATTRIBUTE");
		AssertReflectionViolation(typeof(TypeCarryingParameterAttributeFixture), "CUSTOM_ATTRIBUTE");
		AssertReflectionViolation(typeof(TypeCarryingReturnAttributeFixture), "CUSTOM_ATTRIBUTE");

		Type explicitImplementation = typeof(MethodImplementationMetadataFixture);
		Assert.True(HasRawMethodImplementation(explicitImplementation));
		AssertReflectionViolation(explicitImplementation, "METHOD_IMPL_ROW");
	}

	[Fact]
	public void MetadataRootGateRejectsRealCompiledShapeSiblings()
	{
		Assert.Empty(VerifyMetadataRoot(typeof(ValidMetadataRootFixture)));
		AssertMetadataRootViolation(typeof(DerivedMetadataRootFixture), "EXTENDS_ROOT");
		AssertMetadataRootViolation(typeof(InterfaceMetadataRootFixture), "INTERFACE_ROOT");
		AssertMetadataRootViolation(typeof(GenericMetadataRootFixture<>), "GENERIC_ROOT");
		AssertMetadataRootViolation(typeof(EventMetadataRootFixture), "EVENT_ROOT");
	}

	[Fact]
	public void AttributeValueGateRecursivelyRejectsForbiddenEmbeddedTypes()
	{
		AssertAttributePair(
			typeof(AllowedConstructorTypeAttributeFixture),
			typeof(ForbiddenConstructorTypeAttributeFixture));
		AssertAttributePair(
			typeof(AllowedNamedTypeAttributeFixture),
			typeof(ForbiddenNamedTypeAttributeFixture));
		AssertAttributePair(
			typeof(AllowedArrayTypeAttributeFixture),
			typeof(ForbiddenArrayTypeAttributeFixture));
	}

	[Fact]
	public void ControlFlowDominanceVerifierRejectsGuardBypasses()
	{
		var valid = new DirectedGraph(
			entry: 0,
			edges:
			[
				(0, 1), (0, 9),
				(1, 2), (1, 9),
				(2, 3),
				(3, 4), (3, 9),
				(4, 5), (4, 9),
				(5, 6),
				(6, 2), (6, 7),
			]);
		Assert.True(valid.Dominates(0, 2));
		Assert.True(valid.Dominates(1, 2));
		Assert.True(valid.Dominates(3, 4));
		Assert.True(valid.Dominates(4, 5));
		Assert.True(valid.Dominates(4, 7));

		var allocationBeforeCountCheck = new DirectedGraph(0, [(0, 2), (2, 1), (1, 7)]);
		var aggregateCheckAfterNextWork = new DirectedGraph(0, [(0, 2), (2, 5), (5, 3), (3, 7)]);
		var capFailureContinues = new DirectedGraph(0, [(0, 3), (3, 4), (3, 9), (9, 7), (4, 7)]);
		Assert.False(allocationBeforeCountCheck.Dominates(1, 2));
		Assert.False(aggregateCheckAfterNextWork.Dominates(3, 5));
		Assert.False(capFailureContinues.Dominates(4, 7));
	}

	private static LiquidWalletTransactionObservation ExactNativeFixture()
	{
		LiquidOwnedOutputObservation external = LiquidOwnedOutputObservation.Create(
			TransactionId,
			0,
			WitnessBinding,
			ExternalScript,
			ExternalSpendPublicKey,
			ExternalBlindingPublicKey,
			LiquidKeyBranch.External,
			0,
			ExternalAsset,
			900);
		LiquidOwnedOutputObservation internalOutput = LiquidOwnedOutputObservation.Create(
			TransactionId,
			1,
			WitnessBinding,
			InternalScript,
			InternalSpendPublicKey,
			InternalBlindingPublicKey,
			LiquidKeyBranch.Internal,
			1,
			InternalAsset,
			2_000);
		return LiquidWalletTransactionObservation.Create(
			TransactionId,
			WitnessBinding,
			[
				LiquidOutPoint.ParseSpendableConsensusBytes(Convert.FromHexString(FirstInputHex)),
				LiquidOutPoint.ParseSpendableConsensusBytes(Convert.FromHexString(SecondInputHex)),
			],
			[external, internalOutput]);
	}

	private static LiquidWalletTransactionObservation Observation(
		byte[] transactionId,
		byte[]? witnessBinding = null,
		LiquidOutPoint? input = null) =>
		LiquidWalletTransactionObservation.Create(
			transactionId,
			witnessBinding ?? new byte[LiquidTransactionWitnessBinding.ByteLength],
			[input ?? OutPoint('a', 0)],
			[]);

	private static LiquidWalletTransactionObservation[] ValidatedInputObservations(
		int aggregateInputCount,
		int startOrdinal = 1)
	{
		const int PerTransactionLimit = 102_298;
		LiquidOutPoint[] inputs = Enumerable.Range(1, PerTransactionLimit)
			.Select(index => LiquidOutPoint.CreateSpendable(
				LiquidTransactionId.ParseConsensusBytes(IdForOrdinal(index), nameof(index)),
				0))
			.ToArray();
		int transactionCount = (aggregateInputCount + PerTransactionLimit - 1) / PerTransactionLimit;
		return Enumerable.Range(startOrdinal, transactionCount)
			.Select(index =>
			{
				int remaining = aggregateInputCount - ((index - startOrdinal) * PerTransactionLimit);
				int count = Math.Min(PerTransactionLimit, remaining);
				return LiquidWalletTransactionObservation.Create(
					IdForOrdinal(index),
					new byte[LiquidTransactionWitnessBinding.ByteLength],
					new ArraySegment<LiquidOutPoint>(inputs, 0, count),
					[]);
			})
			.ToArray();
	}

	private static LiquidWalletTransactionObservation[] ValidatedOutputObservations(
		int aggregateOutputCount,
		int startOrdinal = 1)
	{
		const int PerTransactionLimit = 9_279;
		int transactionCount = (aggregateOutputCount + PerTransactionLimit - 1) / PerTransactionLimit;
		return Enumerable.Range(startOrdinal, transactionCount)
			.Select(index =>
			{
				int remaining = aggregateOutputCount - ((index - startOrdinal) * PerTransactionLimit);
				return ValidatedObservation(IdForOrdinal(index), Math.Min(PerTransactionLimit, remaining));
			})
			.ToArray();
	}

	private static LiquidWalletTransactionObservation ValidatedObservation(
		byte[] transactionId,
		int ownedOutputCount,
		ulong amount = 1)
	{
		byte[] witnessBinding = new byte[LiquidTransactionWitnessBinding.ByteLength];
		var outputs = new GeneratedReadOnlyList<LiquidOwnedOutputObservation>(
			ownedOutputCount,
			index => LiquidOwnedOutputObservation.Create(
				transactionId,
				checked((uint)index),
				witnessBinding,
				ExternalScript,
				ExternalSpendPublicKey,
				ExternalBlindingPublicKey,
				LiquidKeyBranch.External,
				0,
				ExternalAsset,
				amount));
		return LiquidWalletTransactionObservation.Create(
			transactionId,
			witnessBinding,
			[OutPoint('a', 0)],
			outputs);
	}

	private static byte[] IdForOrdinal(int ordinal)
	{
		var bytes = new byte[LiquidTransactionId.ConsensusByteLength];
		bytes[0] = 1;
		BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(bytes.Length - sizeof(int)), ordinal);
		return bytes;
	}

	private static LiquidOutPoint OutPoint(char transactionIdDigit, uint outputIndex) =>
		LiquidOutPoint.CreateSpendable(
			LiquidTransactionId.ParseRpcHex(new string(transactionIdDigit, 64)),
			outputIndex);

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

	private static void AssertExactNullableAttributes(Type type)
	{
		Assert.Equal(
			[
				"System.Runtime.CompilerServices.NullableAttribute|0",
				"System.Runtime.CompilerServices.NullableContextAttribute|1",
			],
			type.CustomAttributes.Select(AttributeKey).Order(StringComparer.Ordinal));

		foreach (FieldInfo field in type.GetFields(DeclaredMemberFlags))
		{
#if DEBUG
			string[] expected = field.Name == "<OwnedOutputCount>k__BackingField"
				? [
					"System.Diagnostics.DebuggerBrowsableAttribute|0",
					"System.Runtime.CompilerServices.CompilerGeneratedAttribute|",
				]
				: [];
#else
			string[] expected = field.Name == "<OwnedOutputCount>k__BackingField"
				? ["System.Runtime.CompilerServices.CompilerGeneratedAttribute|"]
				: [];
#endif
			Assert.Equal(expected, field.CustomAttributes.Select(AttributeKey).Order(StringComparer.Ordinal));
			Assert.Empty(field.GetRequiredCustomModifiers());
			Assert.Empty(field.GetOptionalCustomModifiers());
			Assert.Null(field.GetCustomAttribute<MarshalAsAttribute>());
			Assert.Null(field.GetCustomAttribute<FieldOffsetAttribute>());
		}

		foreach (MethodInfo method in type.GetMethods(DeclaredMemberFlags))
		{
			string[] expected = method.Name switch
			{
				"get_OwnedOutputCount" => ["System.Runtime.CompilerServices.CompilerGeneratedAttribute|"],
				"Equals" => ["System.Runtime.CompilerServices.NullableContextAttribute|2"],
				_ => [],
			};
			Assert.Equal(expected, method.CustomAttributes.Select(AttributeKey).Order(StringComparer.Ordinal));
		}

		ConstructorInfo constructor = Assert.Single(type.GetConstructors(DeclaredMemberFlags));
		Assert.Empty(constructor.CustomAttributes);
		Assert.All(type.GetProperties(DeclaredMemberFlags), property => Assert.Empty(property.CustomAttributes));

		var actualLocations = new List<string>();
		AddAttributeLocations(actualLocations, "type", type.CustomAttributes);
		foreach (FieldInfo field in type.GetFields(DeclaredMemberFlags))
		{
			AddAttributeLocations(actualLocations, $"field:{field.Name}", field.CustomAttributes);
		}
		AddAttributeLocations(actualLocations, "constructor", constructor.CustomAttributes);
		foreach (MethodInfo method in type.GetMethods(DeclaredMemberFlags))
		{
			string methodLocation = $"method:{MethodKey(method)}";
			AddAttributeLocations(actualLocations, methodLocation, method.CustomAttributes);
			AddAttributeLocations(actualLocations, $"return:{MethodKey(method)}", method.ReturnParameter.CustomAttributes);
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				AddAttributeLocations(
					actualLocations,
					$"parameter:{MethodKey(method)}:{parameter.Position}:{parameter.Name}",
					parameter.CustomAttributes);
			}
		}
		foreach (ParameterInfo parameter in constructor.GetParameters())
		{
			AddAttributeLocations(
				actualLocations,
				$"parameter:{MethodKey(constructor)}:{parameter.Position}:{parameter.Name}",
				parameter.CustomAttributes);
		}
		foreach (PropertyInfo property in type.GetProperties(DeclaredMemberFlags))
		{
			AddAttributeLocations(actualLocations, $"property:{property.Name}", property.CustomAttributes);
		}

		string equalsBatch = MethodKey(GetMethod("Equals", type));
		string equalsObject = MethodKey(GetMethod("Equals", typeof(object)));
#if DEBUG
		string[] expectedLocations =
		[
			"type|System.Runtime.CompilerServices.NullableAttribute|0",
			"type|System.Runtime.CompilerServices.NullableContextAttribute|1",
			"field:<OwnedOutputCount>k__BackingField|System.Diagnostics.DebuggerBrowsableAttribute|0",
			"field:<OwnedOutputCount>k__BackingField|System.Runtime.CompilerServices.CompilerGeneratedAttribute|",
			"method:$BATCH::get_OwnedOutputCount()->System.Int32|System.Runtime.CompilerServices.CompilerGeneratedAttribute|",
			$"method:{equalsBatch}|System.Runtime.CompilerServices.NullableContextAttribute|2",
			$"method:{equalsObject}|System.Runtime.CompilerServices.NullableContextAttribute|2",
		];
#else
		string[] expectedLocations =
		[
			"type|System.Runtime.CompilerServices.NullableAttribute|0",
			"type|System.Runtime.CompilerServices.NullableContextAttribute|1",
			"field:<OwnedOutputCount>k__BackingField|System.Runtime.CompilerServices.CompilerGeneratedAttribute|",
			"method:$BATCH::get_OwnedOutputCount()->System.Int32|System.Runtime.CompilerServices.CompilerGeneratedAttribute|",
			$"method:{equalsBatch}|System.Runtime.CompilerServices.NullableContextAttribute|2",
			$"method:{equalsObject}|System.Runtime.CompilerServices.NullableContextAttribute|2",
		];
#endif
		AssertExactSet(actualLocations, expectedLocations);
	}

	private static void AddAttributeLocations(
		ICollection<string> locations,
		string location,
		IEnumerable<CustomAttributeData> attributes)
	{
		foreach (CustomAttributeData attribute in attributes)
		{
			locations.Add($"{location}|{AttributeKey(attribute)}");
		}
	}

	private static string AttributeKey(CustomAttributeData attribute)
	{
		Assert.Equal(".ctor", attribute.Constructor.Name);
		Assert.Equal(attribute.AttributeType, attribute.Constructor.DeclaringType);
		Assert.Empty(attribute.NamedArguments);
		Assert.All(attribute.ConstructorArguments, AssertNoEmbeddedTypeArgument);
		return $"{attribute.AttributeType.FullName}|{string.Join(',', attribute.ConstructorArguments.Select(argument => argument.Value))}";
	}

	private static void AssertNoEmbeddedTypeArgument(CustomAttributeTypedArgument argument)
	{
		Assert.NotEqual(typeof(Type), argument.ArgumentType);
		if (argument.Value is ReadOnlyCollection<CustomAttributeTypedArgument> nested)
		{
			Assert.All(nested, AssertNoEmbeddedTypeArgument);
		}
	}

	private static void AssertAllParametersAndReturns(Type type, ConstructorInfo constructor)
	{
		var expectedNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			[MethodKey(constructor)] = ["transactions", "ownedOutputCount"],
			[MethodKey(GetMethod("Create", typeof(IReadOnlyList<LiquidWalletTransactionObservation>)))] = ["transactions"],
			[MethodKey(GetMethod("Equals", type))] = ["other"],
			[MethodKey(GetMethod("Equals", typeof(object)))] = ["obj"],
		};

		foreach (MethodBase callable in type.GetMethods(DeclaredMemberFlags).Cast<MethodBase>().Append(constructor))
		{
			Assert.Equal(MethodImplAttributes.IL, callable.MethodImplementationFlags);
			Assert.Equal(
				callable.IsStatic ? CallingConventions.Standard : CallingConventions.Standard | CallingConventions.HasThis,
				callable.CallingConvention);
			if (callable is MethodInfo genericCandidate)
			{
				Assert.Empty(genericCandidate.GetGenericArguments());
			}
			string[] parameterNames = callable.GetParameters().Select(parameter => parameter.Name!).ToArray();
			Assert.Equal(expectedNames.GetValueOrDefault(MethodKey(callable), []), parameterNames);

			foreach (ParameterInfo parameter in callable.GetParameters())
			{
				Assert.Equal(ParameterAttributes.None, parameter.Attributes);
				Assert.False(parameter.IsOptional);
				Assert.False(parameter.HasDefaultValue);
				Assert.False(parameter.IsIn);
				Assert.False(parameter.IsOut);
				Assert.Empty(parameter.GetRequiredCustomModifiers());
				Assert.Empty(parameter.GetOptionalCustomModifiers());
				Assert.Empty(parameter.CustomAttributes);
				Assert.Null(parameter.GetCustomAttribute<MarshalAsAttribute>());
			}

			if (callable is MethodInfo method)
			{
				ParameterInfo result = method.ReturnParameter;
				Assert.Null(result.Name);
				Assert.Equal(-1, result.Position);
				Assert.Equal(ParameterAttributes.None, result.Attributes);
				Assert.False(result.HasDefaultValue);
				Assert.Empty(result.GetRequiredCustomModifiers());
				Assert.Empty(result.GetOptionalCustomModifiers());
				Assert.Empty(result.CustomAttributes);
				Assert.Null(result.GetCustomAttribute<MarshalAsAttribute>());
			}
		}
	}

	private static string FieldManifest(FieldInfo field) =>
		FieldManifest(
			field.Name,
			field.FieldType,
			field.Attributes,
			field.IsLiteral ? field.GetRawConstantValue() : null);

	private static string FieldManifest(string name, Type type, FieldAttributes attributes, object? value) =>
		$"{name}|{TypeKey(type)}|{(int)attributes}|{value?.ToString() ?? "null"}";

	private static string MethodManifest(MethodInfo method) =>
		MethodManifest(method.Name, method.ReturnType, method.Attributes,
			method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

	private static string MethodManifest(
		string name,
		Type returnType,
		MethodAttributes attributes,
		params Type[] parameterTypes) =>
		$"{name}|{TypeKey(returnType)}|{(int)attributes}|{string.Join(',', parameterTypes.Select(TypeKey))}";

	private static string PropertyManifest(PropertyInfo property) =>
		PropertyManifest(property.Name, property.PropertyType, property.GetMethod!.Name);

	private static string PropertyManifest(string name, Type type, string getter) =>
		$"{name}|{TypeKey(type)}|{getter}";

	private static void AssertCallable(
		ConstructorInfo constructor,
		MethodAttributes attributes,
		params (string Name, Type Type)[] parameters)
	{
		Assert.Equal(attributes, constructor.Attributes);
		Assert.Equal(parameters.Select(value => value.Name), constructor.GetParameters().Select(value => value.Name));
		Assert.Equal(parameters.Select(value => value.Type), constructor.GetParameters().Select(value => value.ParameterType));
	}

	private static void AssertExactSet(IEnumerable<string> actual, IEnumerable<string> expected)
	{
		string[] actualValues = actual.Order(StringComparer.Ordinal).ToArray();
		string[] expectedValues = expected.Order(StringComparer.Ordinal).ToArray();
		Assert.True(
			actualValues.SequenceEqual(expectedValues, StringComparer.Ordinal),
			"Exact manifest differs.\nMissing:\n" +
			string.Join('\n', expectedValues.Except(actualValues, StringComparer.Ordinal)) +
			"\nUnexpected:\n" +
			string.Join('\n', actualValues.Except(expectedValues, StringComparer.Ordinal)));
	}

	private static void AssertExactMultiset(
		IEnumerable<string> actual,
		IEnumerable<string> expected,
		string label)
	{
		string[] actualValues = actual.Order(StringComparer.Ordinal).ToArray();
		string[] expectedValues = expected.Order(StringComparer.Ordinal).ToArray();
		Assert.True(
			actualValues.SequenceEqual(expectedValues, StringComparer.Ordinal),
			$"Exact {label} differs.\nExpected:\n{string.Join('\n', expectedValues)}\nActual:\n{string.Join('\n', actualValues)}");
	}

	private static bool ExactMultisetMatches(IEnumerable<string> left, IEnumerable<string> right) =>
		left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

	private static IEnumerable<MethodBase> DeclaredBodies() =>
		typeof(LiquidWalletObservationBatch)
			.GetMethods(DeclaredMemberFlags)
			.Cast<MethodBase>()
			.Concat(typeof(LiquidWalletObservationBatch).GetConstructors(DeclaredMemberFlags))
			.OrderBy(MethodKey, StringComparer.Ordinal);

	private static MethodInfo GetMethod(string name, params Type[] parameterTypes) =>
		typeof(LiquidWalletObservationBatch).GetMethod(
			name,
			DeclaredMemberFlags,
			binder: null,
			parameterTypes,
			modifiers: null)!;

	private static MethodAttributes ExpectedMethodAttributes(MethodBase method)
	{
		if (method.Name == ".ctor")
		{
			return MethodAttributes.Private | MethodAttributes.HideBySig |
				MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
		}
		if (method.Name is "get_TransactionCount" or "get_OwnedOutputCount" or "get_IsEmpty")
		{
			return MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName;
		}
		if (method.Name == "Create")
		{
			return MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
		}
		if (method.Name == "GetTransactions")
		{
			return MethodAttributes.Public | MethodAttributes.HideBySig;
		}
		if (method.Name == "Equals" && method.GetParameters().Single().ParameterType == typeof(LiquidWalletObservationBatch))
		{
			return MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual |
				MethodAttributes.HideBySig | MethodAttributes.NewSlot;
		}
		return MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;
	}

	private static IEnumerable<string> ExpectedCalls(MethodBase method)
	{
		if (method.Name == ".ctor")
		{
			return [Call(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!)];
		}
		if (method.Name == "get_IsEmpty")
		{
			return [Call(OpCodes.Call, GetMethod("get_TransactionCount"))];
		}
		if (method.Name == "Create")
		{
			MethodInfo throwIfNull = typeof(ArgumentNullException).GetMethod(
				nameof(ArgumentNullException.ThrowIfNull),
				[typeof(object), typeof(string)])!;
			ConstructorInfo outOfRange = typeof(ArgumentOutOfRangeException).GetConstructor(
				[typeof(string), typeof(string)])!;
			return
			[
				Call(OpCodes.Call, throwIfNull),
				Call(OpCodes.Call, throwIfNull),
				Call(OpCodes.Callvirt, typeof(IReadOnlyCollection<LiquidWalletTransactionObservation>).GetProperty("Count")!.GetMethod!),
				Call(OpCodes.Newobj, outOfRange),
				Call(OpCodes.Newobj, outOfRange),
				Call(OpCodes.Newobj, outOfRange),
				Call(OpCodes.Newobj, outOfRange),
				Call(OpCodes.Callvirt, typeof(IReadOnlyList<LiquidWalletTransactionObservation>).GetProperty("Item")!.GetMethod!),
				Call(OpCodes.Callvirt, typeof(LiquidWalletTransactionObservation).GetProperty("InputCount")!.GetMethod!),
				Call(OpCodes.Callvirt, typeof(LiquidWalletTransactionObservation).GetProperty("OwnedOutputCount")!.GetMethod!),
				Call(OpCodes.Callvirt, typeof(LiquidWalletTransactionObservation).GetMethod(nameof(LiquidWalletTransactionObservation.GetTransactionIdConsensusBytes))!),
				Call(OpCodes.Newobj, typeof(ArgumentException).GetConstructor([typeof(string), typeof(string)])!),
				Call(OpCodes.Newobj, Assert.Single(typeof(LiquidWalletObservationBatch).GetConstructors(DeclaredMemberFlags))),
			];
		}
		if (method.Name == "GetTransactions")
		{
			return
			[
				Call(OpCodes.Call, typeof(Array).GetMethod(nameof(Array.Copy), [typeof(Array), typeof(Array), typeof(int)])!),
				Call(OpCodes.Newobj, typeof(ReadOnlyCollection<LiquidWalletTransactionObservation>).GetConstructor([typeof(IList<LiquidWalletTransactionObservation>)])!),
			];
		}
		if (method.Name == "Equals" && method.GetParameters().Select(value => value.ParameterType).SequenceEqual([typeof(LiquidWalletObservationBatch)]))
		{
			return [Call(OpCodes.Callvirt, typeof(LiquidWalletTransactionObservation).GetMethod(
				nameof(LiquidWalletTransactionObservation.Equals),
				[typeof(LiquidWalletTransactionObservation)])!)];
		}
		if (method.Name == "Equals" && method.GetParameters().Select(value => value.ParameterType).SequenceEqual([typeof(object)]))
		{
			return [Call(OpCodes.Call, GetMethod("Equals", typeof(LiquidWalletObservationBatch)))];
		}
		if (method.Name == "GetHashCode")
		{
			MethodInfo add = typeof(HashCode).GetMethods(BindingFlags.Public | BindingFlags.Instance)
				.Single(candidate => candidate.Name == nameof(HashCode.Add) && candidate.IsGenericMethodDefinition && candidate.GetParameters().Length == 1)
				.MakeGenericMethod(typeof(LiquidWalletTransactionObservation));
			return
			[
				Call(OpCodes.Call, add),
				Call(OpCodes.Call, typeof(HashCode).GetMethod(nameof(HashCode.ToHashCode), Type.EmptyTypes)!),
			];
		}
		return [];
	}

	private static IEnumerable<string> ExpectedFields(MethodBase method)
	{
		FieldInfo transactions = typeof(LiquidWalletObservationBatch).GetField("_transactions", DeclaredMemberFlags)!;
		FieldInfo outputs = typeof(LiquidWalletObservationBatch).GetField("<OwnedOutputCount>k__BackingField", DeclaredMemberFlags)!;
		return method.Name switch
		{
			".ctor" => [Field(OpCodes.Stfld, transactions), Field(OpCodes.Stfld, outputs)],
			"get_TransactionCount" => [Field(OpCodes.Ldfld, transactions)],
			"get_OwnedOutputCount" => [Field(OpCodes.Ldfld, outputs)],
			"GetTransactions" => [Field(OpCodes.Ldfld, transactions)],
			"Equals" when method.GetParameters().Select(value => value.ParameterType).SequenceEqual([typeof(LiquidWalletObservationBatch)]) =>
				[Field(OpCodes.Ldfld, transactions), Field(OpCodes.Ldfld, transactions)],
			"GetHashCode" => [Field(OpCodes.Ldfld, transactions)],
			_ => [],
		};
	}

	private static IEnumerable<string> ExpectedTypeTokens(MethodBase method) => method.Name switch
	{
		"Create" => [$"newarr|{TypeKey(typeof(LiquidWalletTransactionObservation))}"],
		"GetTransactions" => [$"newarr|{TypeKey(typeof(LiquidWalletTransactionObservation))}"],
		"Equals" when method.GetParameters().Select(value => value.ParameterType).SequenceEqual([typeof(object)]) =>
			[$"isinst|{TypeKey(typeof(LiquidWalletObservationBatch))}"],
		"GetHashCode" => [$"initobj|{TypeKey(typeof(HashCode))}"],
		_ => [],
	};

	private static IEnumerable<string> ExpectedStrings(MethodBase method)
	{
		if (method.Name == "Create")
		{
			return
			[
				"transactions", "transactions", "transactions", "transactions", "transactions", "transactions", "transactions",
				"A nonnegative wallet observation transaction count is required.",
				"The wallet observation transaction limit was exceeded.",
				"The wallet observation aggregate input limit was exceeded.",
				"The wallet observation aggregate owned-output limit was exceeded.",
				"Wallet observation transactions must have unique, strictly ascending consensus identifiers.",
			];
		}
		return method.Name == "ToString" ? ["LiquidWalletObservationBatch"] : [];
	}

	private static bool ExpectedInitLocals(MethodBase method) => method.Name is "Create" or "GetTransactions" or "GetHashCode" ||
		method.Name == "Equals" && method.GetParameters().Select(value => value.ParameterType).SequenceEqual([typeof(LiquidWalletObservationBatch)]);

	private static IEnumerable<string> ExpectedLocals(MethodBase method)
	{
		Type observation = typeof(LiquidWalletTransactionObservation);
		if (method.Name == "Create")
		{
#if DEBUG
			Type[] types =
			[
				typeof(int), observation.MakeArrayType(), typeof(int), typeof(int), typeof(byte[]),
				typeof(bool), typeof(bool), typeof(int), observation, typeof(byte[]), typeof(bool),
				typeof(bool), typeof(bool), typeof(int), typeof(int), typeof(bool), typeof(bool),
				typeof(bool), typeof(bool), typeof(bool), typeof(LiquidWalletObservationBatch),
			];
#else
			Type[] types =
			[
				typeof(int), observation.MakeArrayType(), typeof(int), typeof(int), typeof(byte[]),
				typeof(int), observation, typeof(byte[]), typeof(int), typeof(int),
			];
#endif
			return types.Select((type, index) => $"{index}|{TypeKey(type)}|False");
		}
		if (method.Name == "GetTransactions")
		{
#if DEBUG
			Type[] types = [observation.MakeArrayType(), observation.MakeArrayType(), typeof(IReadOnlyList<LiquidWalletTransactionObservation>)];
#else
			Type[] types = [observation.MakeArrayType(), observation.MakeArrayType()];
#endif
			return types.Select((type, index) => $"{index}|{TypeKey(type)}|False");
		}
		if (method.Name == "Equals" && method.GetParameters().Select(value => value.ParameterType).SequenceEqual([typeof(LiquidWalletObservationBatch)]))
		{
#if DEBUG
			Type[] types = [observation.MakeArrayType(), observation.MakeArrayType(), typeof(bool), typeof(bool), typeof(bool), typeof(int), typeof(bool), typeof(bool)];
#else
			Type[] types = [observation.MakeArrayType(), observation.MakeArrayType(), typeof(int)];
#endif
			return types.Select((type, index) => $"{index}|{TypeKey(type)}|False");
		}
		if (method.Name == "GetHashCode")
		{
#if DEBUG
			Type[] types = [typeof(HashCode), observation.MakeArrayType(), typeof(int), typeof(bool), typeof(int)];
#else
			Type[] types = [typeof(HashCode), observation.MakeArrayType(), typeof(int)];
#endif
			return types.Select((type, index) => $"{index}|{TypeKey(type)}|False");
		}
		return [];
	}

	private static string LocalKey(LocalVariableInfo local) =>
		$"{local.LocalIndex}|{TypeKey(local.LocalType)}|{local.IsPinned}";

	private static string LocalTypeKey(ManagedLocal local, int index) => $"{index}|{local.Type}";

	private static string Call(OpCode opcode, MethodBase method) => $"{opcode.Name}|{MemberKey(method)}";
	private static string Field(OpCode opcode, FieldInfo field) => $"{opcode.Name}|{MemberKey(field)}";

	private static void AssertPairedChannelPolicy(
		FixtureChannel channel,
		string allowedMethodName,
		string forbiddenMethodName)
	{
		MethodInfo allowed;
		MethodInfo forbidden;
		if (channel == FixtureChannel.Local)
		{
			allowed = CreateLocalMetadataFixture(allowedMethodName, typeof(LiquidWalletTransactionObservation));
			forbidden = CreateLocalMetadataFixture(forbiddenMethodName, typeof(LiquidWalletState));
		}
		else
		{
			allowed = typeof(ForbiddenChannelFixtures).GetMethod(allowedMethodName)!;
			forbidden = typeof(ForbiddenChannelFixtures).GetMethod(forbiddenMethodName)!;
		}

		Assert.Equal(ChannelShape(allowed, channel), ChannelShape(forbidden, channel));
		Assert.True(HasFixtureChannel(allowed, channel));
		Assert.True(HasFixtureChannel(forbidden, channel));
		if (channel == FixtureChannel.Catch)
		{
			ManagedBodyView allowedView = ManagedBodyView.FromMethod(allowed);
			ManagedBodyView forbiddenView = ManagedBodyView.FromMethod(forbidden);
			Assert.Equal(allowedView.Attributes, forbiddenView.Attributes);
			Assert.Equal(allowedView.Implementation, forbiddenView.Implementation);
			Assert.Equal(allowedView.HasBody, forbiddenView.HasBody);
			Assert.Equal(allowedView.InitLocals, forbiddenView.InitLocals);
			Assert.Equal(allowedView.Locals, forbiddenView.Locals);
			Assert.Equal(allowedView.Clauses, forbiddenView.Clauses);
			Assert.Equal(allowedView.Instructions, forbiddenView.Instructions);
			ExceptionHandlingClause allowedClause = Assert.Single(allowed.GetMethodBody()!.ExceptionHandlingClauses);
			ExceptionHandlingClause forbiddenClause = Assert.Single(forbidden.GetMethodBody()!.ExceptionHandlingClauses);
			Assert.Equal(ExceptionHandlingClauseOptions.Clause, allowedClause.Flags);
			Assert.Equal(allowedClause.Flags, forbiddenClause.Flags);
			Assert.Equal(allowedClause.TryOffset, forbiddenClause.TryOffset);
			Assert.Equal(allowedClause.TryLength, forbiddenClause.TryLength);
			Assert.Equal(allowedClause.HandlerOffset, forbiddenClause.HandlerOffset);
			Assert.Equal(allowedClause.HandlerLength, forbiddenClause.HandlerLength);
			Assert.Equal(typeof(Exception), allowedClause.CatchType);
			Assert.Equal(typeof(ElementsRpcException), forbiddenClause.CatchType);
		}
		ManagedBodyPolicy policy = ManagedBodyPolicy.ForFixture(allowed);
		Assert.Empty(VerifyManagedSemanticBody(ManagedBodyView.FromMethod(allowed), policy));
		IReadOnlyList<string> violations = VerifyManagedSemanticBody(ManagedBodyView.FromMethod(forbidden), policy);
		string expectedViolation = channel switch
		{
			FixtureChannel.Field => "FIELD_OPERAND",
			FixtureChannel.Type => "TYPE_OPERAND",
			FixtureChannel.Token => "TOKEN_OPERAND",
			FixtureChannel.Local => "LOCAL_TYPE",
			FixtureChannel.Catch => "CATCH_TYPE",
			FixtureChannel.String => "STRING_OPERAND",
			_ => throw new InvalidOperationException(channel.ToString()),
		};
		Assert.Equal([expectedViolation], violations);
	}

	private static bool HasFixtureChannel(MethodInfo method, FixtureChannel channel) => channel switch
	{
		FixtureChannel.Field => ReadInstructions(method).Any(instruction => instruction.OpCode.OperandType == OperandType.InlineField),
		FixtureChannel.Type => ReadInstructions(method).Any(instruction => instruction.OpCode.OperandType == OperandType.InlineType),
		FixtureChannel.Token => ReadInstructions(method).Any(instruction => instruction.OpCode.OperandType == OperandType.InlineTok),
		FixtureChannel.Local => method.GetMethodBody()!.LocalVariables.Count == 1,
		FixtureChannel.Catch => method.GetMethodBody()!.ExceptionHandlingClauses.Count == 1,
		FixtureChannel.String => ReadInstructions(method).Any(instruction => instruction.OpCode.OperandType == OperandType.InlineString),
		_ => false,
	};

	private static string ChannelShape(MethodInfo method, FixtureChannel channel)
	{
		IEnumerable<string> instructionShape = ReadInstructions(method).Select(instruction =>
		{
			bool target = channel switch
			{
				FixtureChannel.Field => instruction.OpCode.OperandType == OperandType.InlineField,
				FixtureChannel.Type => instruction.OpCode.OperandType == OperandType.InlineType,
				FixtureChannel.Token => instruction.OpCode.OperandType == OperandType.InlineTok,
				FixtureChannel.String => instruction.OpCode.OperandType == OperandType.InlineString,
				_ => false,
			};
			return target ? $"{instruction.OpCode.Name}|<channel>" : instruction.OpCode.Name!;
		});
		return string.Join(',', instructionShape) + channel switch
		{
			FixtureChannel.Local => $"|locals:{method.GetMethodBody()!.LocalVariables.Count}",
			FixtureChannel.Catch => $"|catches:{method.GetMethodBody()!.ExceptionHandlingClauses.Count}",
			_ => string.Empty,
		};
	}

	private static MethodInfo CreateLocalMetadataFixture(string methodName, Type localType)
	{
		AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName($"ObservationBatchLocalFixture{Guid.NewGuid():N}"),
			AssemblyBuilderAccess.Run);
		TypeBuilder type = assembly.DefineDynamicModule("main").DefineType(
			"LocalFixture",
			TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract);
		MethodBuilder method = type.DefineMethod(
			methodName,
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		ILGenerator generator = method.GetILGenerator();
		_ = generator.DeclareLocal(localType);
		generator.Emit(OpCodes.Ret);
		return type.CreateType()!.GetMethod(methodName)!;
	}

	private static MethodInfo CreateManagedBodyFixture(ManagedBodyFixtureKind kind)
	{
		AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName($"ObservationBatchBodyFixture{Guid.NewGuid():N}"),
			AssemblyBuilderAccess.Run);
		TypeBuilder type = assembly.DefineDynamicModule("main").DefineType(
			"BodyFixture",
			TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract);
		MethodBuilder method = type.DefineMethod(
			"Body",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			[typeof(object)]);
		ILGenerator generator = method.GetILGenerator();
		if (kind is ManagedBodyFixtureKind.ExtraLocal or ManagedBodyFixtureKind.PinnedLocal)
		{
			_ = generator.DeclareLocal(
				typeof(LiquidWalletTransactionObservation[]),
				pinned: kind == ManagedBodyFixtureKind.PinnedLocal);
		}

		MethodInfo toString = typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!;
		void EmitApprovedCall()
		{
			generator.Emit(OpCodes.Ldarg_0);
			generator.Emit(
				kind == ManagedBodyFixtureKind.WrongCallOpcode ? OpCodes.Call : OpCodes.Callvirt,
				toString);
			generator.Emit(OpCodes.Pop);
		}

		switch (kind)
		{
			case ManagedBodyFixtureKind.Finally:
				_ = generator.BeginExceptionBlock();
				EmitApprovedCall();
				generator.BeginFinallyBlock();
				generator.Emit(OpCodes.Nop);
				generator.EndExceptionBlock();
				break;
			case ManagedBodyFixtureKind.Fault:
				_ = generator.BeginExceptionBlock();
				EmitApprovedCall();
				generator.BeginFaultBlock();
				generator.Emit(OpCodes.Nop);
				generator.EndExceptionBlock();
				break;
			case ManagedBodyFixtureKind.Filter:
				_ = generator.BeginExceptionBlock();
				EmitApprovedCall();
				generator.BeginExceptFilterBlock();
				generator.Emit(OpCodes.Pop);
				generator.Emit(OpCodes.Ldc_I4_0);
				generator.BeginCatchBlock(null);
				generator.Emit(OpCodes.Pop);
				generator.EndExceptionBlock();
				break;
			default:
				EmitApprovedCall();
				break;
		}
		generator.Emit(OpCodes.Ret);
		return type.CreateType()!.GetMethod("Body")!;
	}

	private static MethodInfo CreateTailPrefixFixture()
	{
		AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName($"ObservationBatchTailFixture{Guid.NewGuid():N}"),
			AssemblyBuilderAccess.Run);
		TypeBuilder type = assembly.DefineDynamicModule("main").DefineType(
			"TailFixture",
			TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract);
		MethodBuilder method = type.DefineMethod(
			"TailCall",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(string),
			[typeof(object)]);
		ILGenerator generator = method.GetILGenerator();
		generator.Emit(OpCodes.Ldarg_0);
		generator.Emit(OpCodes.Tailcall);
		generator.Emit(OpCodes.Callvirt, typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
		generator.Emit(OpCodes.Ret);
		return type.CreateType()!.GetMethod("TailCall")!;
	}

	private static IReadOnlyList<string> VerifyManagedSemanticBody(
		ManagedBodyView view,
		ManagedBodyPolicy policy)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		if ((view.Attributes & MethodAttributes.PinvokeImpl) != 0)
		{
			violations.Add("PINVOKE");
		}
		else if (view.Attributes != policy.Attributes)
		{
			violations.Add("CALLABLE_FLAGS");
		}
		if (view.Implementation != policy.Implementation)
		{
			violations.Add("IMPLEMENTATION_FLAGS");
		}
		if (!view.HasBody)
		{
			violations.Add("BODY_REQUIRED");
		}
		if (view.InitLocals != policy.InitLocals)
		{
			violations.Add("INIT_LOCALS");
		}
		if (view.Locals.Any(local => local.IsPinned))
		{
			violations.Add("PINNED_LOCAL");
		}
		if (!view.Locals.Select(LocalTypeKey).SequenceEqual(policy.Locals, StringComparer.Ordinal))
		{
			violations.Add("LOCAL_TYPE");
		}
		if (!view.Clauses.SequenceEqual(policy.Clauses))
		{
			violations.Add("EXCEPTION_CLAUSE");
		}

		string[] catchTypes = view.Source.GetMethodBody()?.ExceptionHandlingClauses
			.Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause)
			.Select(clause => TypeKey(clause.CatchType!))
			.ToArray() ?? [];
		if (!catchTypes.SequenceEqual(policy.CatchTypes, StringComparer.Ordinal))
		{
			violations.Add("CATCH_TYPE");
		}

		var calls = new List<string>();
		var fields = new List<string>();
		var types = new List<string>();
		var tokens = new List<string>();
		var strings = new List<string>();
		bool unresolved = false;
		foreach (Instruction instruction in view.Instructions)
		{
			if (instruction.OpCode == OpCodes.Calli || instruction.OpCode.OperandType == OperandType.InlineSig)
			{
				violations.Add("SIGNATURE_OPERAND");
				continue;
			}
			if (instruction.OpCode is var opcode &&
				(opcode == OpCodes.Ldftn || opcode == OpCodes.Ldvirtftn || opcode == OpCodes.Jmp || opcode == OpCodes.Localloc))
			{
				violations.Add("FORBIDDEN_OPCODE");
			}
			if (instruction.OpCode.OperandType == OperandType.InlineString)
			{
				try
				{
					strings.Add(ResolveInstructionString(view.Source, instruction));
				}
				catch (Exception)
				{
					unresolved = true;
					violations.Add("UNRESOLVED_TOKEN");
				}
				continue;
			}
			if (instruction.OpCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or
				OperandType.InlineType or OperandType.InlineTok)
			{
				try
				{
					MemberInfo member = ResolveMember(view.Source, instruction);
					switch (instruction.OpCode.OperandType)
					{
						case OperandType.InlineMethod:
							calls.Add($"{instruction.OpCode.Name}|{MemberKey(member)}");
							break;
						case OperandType.InlineField:
							fields.Add($"{instruction.OpCode.Name}|{MemberKey(member)}");
							break;
						case OperandType.InlineType:
							types.Add($"{instruction.OpCode.Name}|{MemberKey(member)}");
							break;
						case OperandType.InlineTok:
							tokens.Add($"{instruction.OpCode.Name}|{MemberKey(member)}");
							break;
					}
				}
				catch (Exception)
				{
					unresolved = true;
					violations.Add("UNRESOLVED_TOKEN");
				}
			}
		}
		if (!unresolved)
		{
			if (!ExactMultisetMatches(calls, policy.Calls))
			{
				bool sameMembers = ExactMultisetMatches(
					calls.Select(value => value[(value.IndexOf('|') + 1)..]),
					policy.Calls.Select(value => value[(value.IndexOf('|') + 1)..]));
				violations.Add(sameMembers ? "CALL_OPCODE" : "CALL_OPERAND");
			}
			if (!ExactMultisetMatches(fields, policy.Fields)) { violations.Add("FIELD_OPERAND"); }
			if (!ExactMultisetMatches(types, policy.Types)) { violations.Add("TYPE_OPERAND"); }
			if (!ExactMultisetMatches(tokens, policy.Tokens)) { violations.Add("TOKEN_OPERAND"); }
			if (!ExactMultisetMatches(strings, policy.Strings)) { violations.Add("STRING_OPERAND"); }
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static void AssertBodyViolation(
		ManagedBodyView view,
		ManagedBodyPolicy policy,
		string expectedViolation) =>
		Assert.Equal([expectedViolation], VerifyManagedSemanticBody(view, policy));

	private static bool ContainsType(Type candidate, Type expected) =>
		candidate == expected ||
		candidate.HasElementType && ContainsType(candidate.GetElementType()!, expected) ||
		candidate.IsGenericType && candidate.GetGenericArguments().Any(argument => ContainsType(argument, expected));

	private static byte[] BuildNormalizedInstructionManifest()
	{
		var manifest = new StringBuilder();
		foreach (MethodBase method in DeclaredBodies())
		{
			manifest.Append("METHOD|").Append(MethodKey(method)).Append('\n');
			Instruction[] instructions = ReadInstructions(method).ToArray();
			IReadOnlyDictionary<int, int> indices = instructions
				.Select((instruction, index) => (instruction.Offset, index))
				.ToDictionary(pair => pair.Offset, pair => pair.index);
			for (int index = 0; index < instructions.Length; index++)
			{
				Instruction instruction = instructions[index];
				manifest.Append(index.ToString("D4", CultureInfo.InvariantCulture))
					.Append('|')
					.Append(instruction.OpCode.Name);
				string? normalized = NormalizeOperand(method, instruction, indices);
				if (normalized is not null)
				{
					manifest.Append('|').Append(normalized);
				}
				manifest.Append('\n');
			}
		}
		return Encoding.UTF8.GetBytes(manifest.ToString());
	}

	private static string? NormalizeOperand(
		MethodBase method,
		Instruction instruction,
		IReadOnlyDictionary<int, int> indices)
	{
		object? operand = instruction.Operand;
		if (operand is null)
		{
			return null;
		}
		if (instruction.OpCode.OperandType is OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget)
		{
			return $"target:{indices[(int)operand]}";
		}
		if (instruction.OpCode.OperandType == OperandType.InlineSwitch)
		{
			return "targets:" + string.Join(',', ((int[])operand).Select(target => indices[target]));
		}
		int token = operand is int value ? value : 0;
		Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
		Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
		return instruction.OpCode.OperandType switch
		{
			OperandType.InlineMethod => "method:" + MemberKey(method.Module.ResolveMethod(token, typeArguments, methodArguments)!),
			OperandType.InlineField => "field:" + MemberKey(method.Module.ResolveField(token, typeArguments, methodArguments)!),
			OperandType.InlineType => "type:" + TypeKey(method.Module.ResolveType(token, typeArguments, methodArguments)),
			OperandType.InlineTok => "token:" + MemberKey(method.Module.ResolveMember(token, typeArguments, methodArguments)!),
			OperandType.InlineString => "string:" + Convert.ToHexString(
				Encoding.UTF8.GetBytes(method.Module.ResolveString(token))).ToLowerInvariant(),
			OperandType.InlineSig => $"signature:{token:x8}",
			_ => Convert.ToString(operand, CultureInfo.InvariantCulture),
		};
	}

	private static IEnumerable<Instruction> ReadInstructions(MethodBase method)
	{
		byte[] bytes = method.GetMethodBody()?.GetILAsByteArray() ??
			throw new InvalidOperationException($"{MethodKey(method)} requires a managed IL body.");
		int offset = 0;
		while (offset < bytes.Length)
		{
			int instructionOffset = offset;
			ushort value = bytes[offset++];
			if (value == 0xfe)
			{
				value = (ushort)(0xfe00 | bytes[offset++]);
			}
			OpCode opcode = OpCodeByValue[value];
			object? operand = ReadOperand(opcode.OperandType, bytes, ref offset);
			yield return new Instruction(instructionOffset, offset, opcode, operand);
		}
	}

	private static object? ReadOperand(OperandType operandType, byte[] bytes, ref int offset)
	{
		switch (operandType)
		{
			case OperandType.InlineNone:
				return null;
			case OperandType.ShortInlineI:
				return unchecked((sbyte)bytes[offset++]);
			case OperandType.InlineI:
				int inlineI = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
				offset += 4;
				return inlineI;
			case OperandType.InlineI8:
				long inlineI8 = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
				offset += 8;
				return inlineI8;
			case OperandType.ShortInlineR:
				int floatBits = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
				offset += 4;
				return $"f32:{floatBits:x8}";
			case OperandType.InlineR:
				long doubleBits = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
				offset += 8;
				return $"f64:{doubleBits:x16}";
			case OperandType.ShortInlineVar:
				return (int)bytes[offset++];
			case OperandType.InlineVar:
				int variable = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
				offset += 2;
				return variable;
			case OperandType.ShortInlineBrTarget:
				int shortDelta = unchecked((sbyte)bytes[offset++]);
				return offset + shortDelta;
			case OperandType.InlineBrTarget:
				int delta = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
				offset += 4;
				return offset + delta;
			case OperandType.InlineSwitch:
				int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
				offset += 4;
				int baseOffset = offset + (count * 4);
				var targets = new int[count];
				for (int index = 0; index < count; index++)
				{
					targets[index] = baseOffset + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
					offset += 4;
				}
				return targets;
			case OperandType.InlineField:
			case OperandType.InlineMethod:
			case OperandType.InlineSig:
			case OperandType.InlineString:
			case OperandType.InlineTok:
			case OperandType.InlineType:
				int token = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
				offset += 4;
				return token;
			default:
				throw new InvalidOperationException(operandType.ToString());
		}
	}

	private static MemberInfo ResolveMember(MethodBase method, Instruction instruction)
	{
		int token = Assert.IsType<int>(instruction.Operand);
		Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
		Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
		return method.Module.ResolveMember(token, typeArguments, methodArguments) ??
			throw new InvalidOperationException($"Unresolved metadata token {token:x8}.");
	}

	private static string MemberKey(MemberInfo member) => member switch
	{
		Type type => TypeKey(type),
		FieldInfo field => $"{TypeKey(field.DeclaringType!)}::{field.Name}:{TypeKey(field.FieldType)}",
		MethodBase method => MethodKey(method),
		_ => throw new InvalidOperationException(member.MemberType.ToString()),
	};

	private static string MethodKey(MethodBase method)
	{
		string genericArguments = method.IsGenericMethod
			? $"<{string.Join(',', method.GetGenericArguments().Select(TypeKey))}>"
			: string.Empty;
		string parameters = string.Join(',', method.GetParameters().Select(parameter => TypeKey(parameter.ParameterType)));
		string returnType = method is MethodInfo methodInfo ? TypeKey(methodInfo.ReturnType) : "void";
		return $"{TypeKey(method.DeclaringType!)}::{method.Name}{genericArguments}({parameters})->{returnType}";
	}

	private static string TypeKey(Type type)
	{
		if (type == typeof(LiquidWalletObservationBatch))
		{
			return "$BATCH";
		}
		if (type == typeof(LiquidWalletTransactionObservation))
		{
			return "$OBSERVATION";
		}
		if (type.IsArray)
		{
			return $"{TypeKey(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
		}
		if (type.IsByRef)
		{
			return TypeKey(type.GetElementType()!) + "&";
		}
		if (type.IsPointer)
		{
			return TypeKey(type.GetElementType()!) + "*";
		}
		if (type.IsGenericType)
		{
			return $"{type.GetGenericTypeDefinition().FullName}<{string.Join(',', type.GetGenericArguments().Select(TypeKey))}>";
		}
		return type.FullName ?? type.Name;
	}

	private static void AssertManifestMutationRejected(
		byte[] manifest,
		Func<byte[], byte[]> mutate,
		string expectedHash)
	{
		byte[] changed = mutate([.. manifest]);
		Assert.NotEqual(expectedHash, Convert.ToHexString(SHA256.HashData(changed)).ToLowerInvariant());
	}

	private static byte[] ReplaceFirst(byte[] bytes, string oldText, string newText)
	{
		string text = Encoding.UTF8.GetString(bytes);
		int index = text.IndexOf(oldText, StringComparison.Ordinal);
		Assert.True(index >= 0, $"Manifest control operand {oldText} was not present.");
		return Encoding.UTF8.GetBytes(string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length)));
	}

	private static byte[] MutateFirstBranchTarget(byte[] bytes)
	{
		string text = Encoding.UTF8.GetString(bytes);
		int marker = text.IndexOf("|target:", StringComparison.Ordinal);
		Assert.True(marker >= 0);
		int digit = marker + "|target:".Length;
		char replacement = text[digit] == '9' ? '8' : (char)(text[digit] + 1);
		return Encoding.UTF8.GetBytes(text[..digit] + replacement + text[(digit + 1)..]);
	}

	private static void AssertCreateInstructionOrderAndControlFlow(MethodInfo create, Instruction[] instructions)
	{
		int countCall = FindInstruction(instructions, create, "get_Count");
		int allocation = Array.FindIndex(instructions, instruction => instruction.OpCode == OpCodes.Newarr);
		int indexer = FindInstruction(instructions, create, "get_Item");
		int inputGetter = FindInstruction(instructions, create, "get_InputCount");
		int outputGetter = FindInstruction(instructions, create, "get_OwnedOutputCount");
		int transactionIdGetter = FindInstruction(instructions, create, nameof(LiquidWalletTransactionObservation.GetTransactionIdConsensusBytes));
		int constructor = Array.FindLastIndex(instructions, instruction =>
			instruction.OpCode == OpCodes.Newobj && ResolveMember(create, instruction).DeclaringType == typeof(LiquidWalletObservationBatch));
		int[] additions = instructions.Select((instruction, index) => (instruction, index))
			.Where(pair => pair.instruction.OpCode == OpCodes.Add_Ovf)
			.Select(pair => pair.index)
			.ToArray();
		int[] countBranches = instructions.Select((instruction, index) => (instruction, index))
			.Where(pair => pair.index > countCall && pair.index < allocation && pair.instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
			.Select(pair => pair.index)
			.ToArray();
		int inputCapBranch = FindNextConditionalBranch(instructions, additions[0]);
		int outputCapBranch = FindNextConditionalBranch(instructions, additions[1]);

		Assert.Equal(2, countBranches.Length);
		Assert.True(countCall < countBranches[0]);
		Assert.True(countBranches[1] < allocation);
		Assert.True(allocation < indexer);
		Assert.True(indexer < inputGetter);
		Assert.True(inputGetter < additions[0]);
		Assert.True(additions[0] < inputCapBranch);
		Assert.True(inputCapBranch < outputGetter);
		Assert.True(outputGetter < additions[1]);
		Assert.True(additions[1] < outputCapBranch);
		Assert.True(outputCapBranch < transactionIdGetter);
		Assert.True(transactionIdGetter < constructor);

		DirectedGraph graph = DirectedGraph.FromInstructions(instructions);
		foreach (int guard in countBranches)
		{
			Assert.True(graph.Dominates(guard, allocation));
			Assert.True(graph.Dominates(guard, indexer));
			Assert.True(graph.Dominates(guard, constructor));
		}
		Assert.True(graph.Dominates(additions[0], outputGetter));
		Assert.True(graph.Dominates(inputCapBranch, outputGetter));
		Assert.True(graph.DominatesFrom(additions[0], inputCapBranch, constructor));
		Assert.True(graph.Dominates(additions[1], transactionIdGetter));
		Assert.True(graph.Dominates(outputCapBranch, transactionIdGetter));
		Assert.True(graph.DominatesFrom(additions[1], outputCapBranch, constructor));
	}

	private static int FindInstruction(Instruction[] instructions, MethodBase caller, string methodName) =>
		Array.FindIndex(instructions, instruction =>
			instruction.OpCode.OperandType == OperandType.InlineMethod &&
			ResolveMember(caller, instruction).Name == methodName);

	private static int FindNextConditionalBranch(Instruction[] instructions, int index)
	{
		int result = Array.FindIndex(instructions, index + 1, instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch);
		Assert.True(result >= 0);
		return result;
	}

	private static IReadOnlyList<string> VerifyCreateValueAndGuardFlow(MethodInfo create, Instruction[] instructions)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			ManagedValueFlow valueFlow = ManagedValueFlow.Analyze(create, instructions);
			if (!valueFlow.IsValid)
			{
				violations.Add("VALUE_FLOW");
				return violations.Order(StringComparer.Ordinal).ToArray();
			}
			DirectedGraph graph = DirectedGraph.FromInstructions(instructions);
			ManagedFlowValue originalTransactions = new(ManagedFlowValueKind.Argument, Instruction: 0);
			ManagedFlowValue transactionsName = new(ManagedFlowValueKind.String, Text: "transactions");
			int[] nullChecks = FindCallIndices(instructions, create, nameof(ArgumentNullException.ThrowIfNull));
			int[] countCalls = FindCallIndices(instructions, create, "get_Count");
			if (countCalls.Length != 1)
			{
				violations.Add("COLLECTION_ORIGINAL_ARGUMENT");
				return violations.Order(StringComparer.Ordinal).ToArray();
			}
			int countCall = countCalls[0];
			if (nullChecks.Length != 2 ||
				!FlowOperandsEqual(valueFlow, nullChecks[0], originalTransactions, transactionsName) ||
				!graph.Dominates(nullChecks[0], countCall))
			{
				violations.Add("NULL_ORIGINAL_ARGUMENT");
			}

			int[] itemCalls = FindCallIndices(instructions, create, "get_Item");
			if (itemCalls.Length != 1)
			{
				violations.Add("COLLECTION_ORIGINAL_ARGUMENT");
				return violations.Order(StringComparer.Ordinal).ToArray();
			}
			int itemCall = itemCalls[0];
			if (!FlowOperandsEqual(valueFlow, countCall, originalTransactions) ||
				valueFlow.PoppedAt(itemCall) is not { Count: 2 } itemOperands || itemOperands[0] != originalTransactions)
			{
				violations.Add("COLLECTION_ORIGINAL_ARGUMENT");
			}
			ManagedFlowValue capturedItem = new(ManagedFlowValueKind.Producer, Instruction: itemCall);
			int[] capturedStores = Enumerable.Range(itemCall + 1, instructions.Length - itemCall - 1)
				.Where(index =>
					TryGetLocalIndex(instructions[index], load: false, out _) &&
					valueFlow.PoppedAt(index) is { Count: 1 } storeOperands && storeOperands[0] == capturedItem)
				.ToArray();
			if (capturedStores.Length != 1)
			{
				violations.Add("CAPTURED_ELEMENT_CAPTURE");
			}
			int capturedStore = capturedStores.LastOrDefault(-1);
			ManagedFlowValue? capturedObservation = capturedStore >= 0 &&
				TryGetLocalIndex(instructions[capturedStore], load: false, out int capturedLocal)
					? new ManagedFlowValue(
						ManagedFlowValueKind.LocalVersion,
						Local: capturedLocal,
						Store: capturedStore,
						StoredValue: capturedItem)
					: null;
			if (nullChecks.Length != 2 ||
				capturedObservation is null ||
				!FlowOperandsEqual(valueFlow, nullChecks[1], capturedObservation, transactionsName))
			{
				violations.Add("CAPTURED_ELEMENT_NULL_ARGUMENT");
			}

			int[] inputGetters = FindCallIndices(instructions, create, "get_InputCount");
			if (inputGetters.Length != 1)
			{
				violations.Add("INPUT_ADD_GETTER");
				return violations.Order(StringComparer.Ordinal).ToArray();
			}
			int[] outputGetters = FindCallIndices(instructions, create, "get_OwnedOutputCount");
			int[] transactionIdGetters = FindCallIndices(
				instructions,
				create,
				nameof(LiquidWalletTransactionObservation.GetTransactionIdConsensusBytes));
			if (outputGetters.Length != 1 || transactionIdGetters.Length != 1)
			{
				violations.Add("OBSERVATION_CALL_COUNT");
				return violations.Order(StringComparer.Ordinal).ToArray();
			}
			int inputGetter = inputGetters[0];
			int outputGetter = outputGetters[0];
			int transactionIdGetter = transactionIdGetters[0];
			int[] arrayStores = instructions.Select((instruction, index) => (instruction, index))
				.Where(pair => pair.instruction.OpCode == OpCodes.Stelem_Ref)
				.Select(pair => pair.index)
				.ToArray();
			int arrayStore = arrayStores.Length == 1 ? arrayStores[0] : -1;
			if (nullChecks.Length != 2 || arrayStores.Length != 1 ||
				!new[] { inputGetter, outputGetter, transactionIdGetter, arrayStore }
					.All(consumer => graph.Dominates(nullChecks[1], consumer)))
			{
				violations.Add("CAPTURED_ELEMENT_NULL_GUARD");
			}
			if (ResolveMember(create, instructions[inputGetter]).DeclaringType != typeof(LiquidWalletTransactionObservation))
			{
				violations.Add("INPUT_ADD_GETTER");
			}
			if (ResolveMember(create, instructions[outputGetter]).DeclaringType != typeof(LiquidWalletTransactionObservation))
			{
				violations.Add("OUTPUT_ADD_GETTER");
			}
			foreach (int memberCall in new[] { inputGetter, outputGetter, transactionIdGetter })
			{
				if (capturedObservation is null ||
					!FlowOperandsEqual(valueFlow, memberCall, capturedObservation))
				{
					violations.Add("CAPTURED_ELEMENT_MEMBER_RECEIVER");
				}
			}
			IReadOnlyList<ManagedFlowValue>? arrayStoreOperands = arrayStore >= 0 ? valueFlow.PoppedAt(arrayStore) : null;
			if (arrayStores.Length != 1 || arrayStoreOperands is not { Count: 3 } || capturedObservation is null ||
				arrayStoreOperands[2] != capturedObservation)
			{
				violations.Add("CAPTURED_ELEMENT_ARRAY_STORE");
			}

			AssertExceptionBindings(create, instructions, valueFlow, violations);
			AggregateCapCheck? inputCapCheck = AssertAggregateBinding(
				create,
				instructions,
				inputGetter,
				outputGetter,
				MaxAggregateInputCount,
				"INPUT",
				valueFlow,
				violations);
			AggregateCapCheck? outputCapCheck = AssertAggregateBinding(
				create,
				instructions,
				outputGetter,
				transactionIdGetter,
				MaxAggregateOwnedOutputCount,
				"OUTPUT",
				valueFlow,
				violations);

			if (instructions.Count(instruction => instruction.OpCode == OpCodes.Add_Ovf) != 2)
			{
				violations.Add("CHECKED_ADD_COUNT");
			}
			AssertCreateGuardGraph(create, instructions, violations, inputCapCheck, outputCapCheck);
		}
		catch (Exception)
		{
			violations.Add("IL_DECODE");
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static bool FlowOperandsEqual(
		ManagedValueFlow valueFlow,
		int instruction,
		params ManagedFlowValue[] expected) =>
		valueFlow.PoppedAt(instruction) is { } actual && actual.SequenceEqual(expected);

	private static ManagedFlowValue UnwrapStoredValue(ManagedFlowValue value)
	{
		while (value.Kind == ManagedFlowValueKind.LocalVersion && value.StoredValue is not null)
		{
			value = value.StoredValue;
		}
		return value;
	}

	private static int AssertAddressedLocal(Instruction[] instructions, ManagedFlowValue value)
	{
		Assert.Equal(ManagedFlowValueKind.Producer, value.Kind);
		Instruction instruction = instructions[value.Instruction];
		Assert.True(instruction.OpCode == OpCodes.Ldloca || instruction.OpCode == OpCodes.Ldloca_S);
		return Convert.ToInt32(instruction.Operand, CultureInfo.InvariantCulture);
	}

	private static bool IsExactObservationHashAdd(MethodInfo method) =>
		method.DeclaringType == typeof(HashCode) && method.Name == nameof(HashCode.Add) &&
		method.IsGenericMethod && !method.IsGenericMethodDefinition && method.ReturnType == typeof(void) &&
		method.GetGenericArguments().SequenceEqual([typeof(LiquidWalletTransactionObservation)]) &&
		method.GetParameters().Select(parameter => parameter.ParameterType)
			.SequenceEqual([typeof(LiquidWalletTransactionObservation)]);

	private static void AssertExceptionBindings(
		MethodInfo create,
		Instruction[] instructions,
		ManagedValueFlow valueFlow,
		ISet<string> violations)
	{
		string[] expectedOutOfRangeMessages =
		[
			"A nonnegative wallet observation transaction count is required.",
			"The wallet observation transaction limit was exceeded.",
			"The wallet observation aggregate input limit was exceeded.",
			"The wallet observation aggregate owned-output limit was exceeded.",
		];
		string[] actualOutOfRangeMessages = instructions
			.Select((instruction, index) => (instruction, index))
			.Where(pair => pair.instruction.OpCode == OpCodes.Newobj &&
				ResolveMember(create, pair.instruction).DeclaringType == typeof(ArgumentOutOfRangeException))
			.Select(pair =>
			{
				IReadOnlyList<ManagedFlowValue>? operands = valueFlow.PoppedAt(pair.index);
				if (operands is not { Count: 2 } ||
					operands[0] != new ManagedFlowValue(ManagedFlowValueKind.String, Text: "transactions") ||
					operands[1].Kind != ManagedFlowValueKind.String || operands[1].Text is null)
				{
					violations.Add("EXCEPTION_ARGUMENT_BINDING");
					return string.Empty;
				}
				return operands[1].Text!;
			})
			.Order(StringComparer.Ordinal)
			.ToArray();
		if (!actualOutOfRangeMessages.SequenceEqual(expectedOutOfRangeMessages.Order(StringComparer.Ordinal), StringComparer.Ordinal))
		{
			violations.Add("EXCEPTION_ARGUMENT_BINDING");
		}

		int argumentException = Array.FindIndex(instructions, instruction =>
			instruction.OpCode == OpCodes.Newobj &&
			ResolveMember(create, instruction).DeclaringType == typeof(ArgumentException));
		if (argumentException < 0 ||
			!FlowOperandsEqual(
				valueFlow,
				argumentException,
				new ManagedFlowValue(
					ManagedFlowValueKind.String,
					Text: "Wallet observation transactions must have unique, strictly ascending consensus identifiers."),
				new ManagedFlowValue(ManagedFlowValueKind.String, Text: "transactions")))
		{
			violations.Add("EXCEPTION_ARGUMENT_BINDING");
		}
	}

	private static AggregateCapCheck? AssertAggregateBinding(
		MethodInfo create,
		Instruction[] instructions,
		int getterIndex,
		int nextWorkIndex,
		int cap,
		string prefix,
		ManagedValueFlow valueFlow,
		ISet<string> violations)
	{
		DirectedGraph graph = DirectedGraph.FromInstructions(instructions);
		int addIndex = Array.FindIndex(instructions, getterIndex + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		IReadOnlyList<ManagedFlowValue>? addOperands = addIndex >= 0 ? valueFlow.PoppedAt(addIndex) : null;
		if (addIndex < 0 || addIndex >= nextWorkIndex || addOperands is not { Count: 2 } ||
			addOperands[1] != new ManagedFlowValue(ManagedFlowValueKind.Getter, Instruction: getterIndex) ||
			!graph.Dominates(getterIndex, addIndex))
		{
			violations.Add($"{prefix}_ADD_GETTER");
			return null;
		}
		if (addOperands[0].Kind != ManagedFlowValueKind.LocalVersion || addOperands[0].Local < 0)
		{
			violations.Add($"{prefix}_ADD_LOCAL");
			return null;
		}
		int aggregateLocal = addOperands[0].Local;
		int aggregateStore = addIndex + 1;
		if (aggregateStore >= instructions.Length ||
			!TryGetLocalIndex(instructions[aggregateStore], load: false, out int storedLocal) ||
			storedLocal != aggregateLocal)
		{
			violations.Add($"{prefix}_ADD_LOCAL");
			return null;
		}
		IReadOnlyList<ManagedFlowValue>? storeOperands = valueFlow.PoppedAt(aggregateStore);
		if (storeOperands is not { Count: 1 } ||
			storeOperands[0] != new ManagedFlowValue(ManagedFlowValueKind.CheckedAdd, Instruction: addIndex))
		{
			violations.Add($"{prefix}_ADD_LOCAL");
			return null;
		}
		if (!TryResolveAggregateCapCheck(
			create,
			instructions,
			aggregateStore,
			nextWorkIndex,
			aggregateLocal,
			cap,
			graph,
			valueFlow,
			out AggregateCapCheck? check))
		{
			violations.Add($"{prefix}_CAP_BINDING");
			return null;
		}
		return check;
	}

	private static bool TryResolveAggregateCapCheck(
		MethodInfo create,
		Instruction[] instructions,
		int aggregateStore,
		int nextWorkIndex,
		int aggregateLocal,
		int cap,
		DirectedGraph graph,
		ManagedValueFlow valueFlow,
		[NotNullWhen(true)] out AggregateCapCheck? check)
	{
		check = null;
		for (int index = aggregateStore + 1; index < nextWorkIndex; index++)
		{
			OpCode opcode = instructions[index].OpCode;
			if (opcode == OpCodes.Ble || opcode == OpCodes.Ble_S ||
				opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S)
			{
				if (!HasExactAggregateAndCapOperands(index, aggregateStore, aggregateLocal, cap, valueFlow))
				{
					continue;
				}
				return TryCreateAggregateCapCheck(instructions, index, greaterBranches: opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S, out check);
			}
			if (opcode != OpCodes.Cgt)
			{
				continue;
			}
			if (!HasExactAggregateAndCapOperands(index, aggregateStore, aggregateLocal, cap, valueFlow))
			{
				continue;
			}

			int branch = index + 1;
			if (branch + 2 < nextWorkIndex &&
				TryGetLocalIndex(instructions[branch], load: false, out int booleanLocal) &&
				TryGetLocalIndex(instructions[branch + 1], load: true, out int loadedBooleanLocal) &&
				loadedBooleanLocal == booleanLocal &&
				FindPreviousLocalStore(instructions, branch + 1, booleanLocal) == branch &&
				graph.Dominates(index, branch) &&
				IsExactLocalVersion(graph, instructions, branch, branch + 1, booleanLocal))
			{
				branch += 2;
			}
			OpCode branchOpcode = instructions[branch].OpCode;
			if (branchOpcode != OpCodes.Brtrue && branchOpcode != OpCodes.Brtrue_S &&
				branchOpcode != OpCodes.Brfalse && branchOpcode != OpCodes.Brfalse_S)
			{
				continue;
			}
			if (!graph.Dominates(index, branch))
			{
				continue;
			}
			IReadOnlyList<ManagedFlowValue>? branchOperands = valueFlow.PoppedAt(branch);
			ManagedFlowValue expectedGreater = new(
				ManagedFlowValueKind.GreaterThan,
				Instruction: index);
			bool exactGreaterValue = branch == index + 1
				? branchOperands is { Count: 1 } && branchOperands[0] == expectedGreater
				: branchOperands is { Count: 1 } &&
					branchOperands[0].Kind == ManagedFlowValueKind.LocalVersion &&
					branchOperands[0].Store == index + 1 && branchOperands[0].StoredValue == expectedGreater;
			if (!exactGreaterValue)
			{
				continue;
			}
			return TryCreateAggregateCapCheck(
				instructions,
				branch,
				greaterBranches: branchOpcode == OpCodes.Brtrue || branchOpcode == OpCodes.Brtrue_S,
				out check);
		}
		return false;
	}

	private static bool HasExactAggregateAndCapOperands(
		int comparison,
		int aggregateStore,
		int aggregateLocal,
		int cap,
		ManagedValueFlow valueFlow)
	{
		IReadOnlyList<ManagedFlowValue>? operands = valueFlow.PoppedAt(comparison);
		return operands is { Count: 2 } &&
			operands[0].Kind == ManagedFlowValueKind.LocalVersion &&
			operands[0].Local == aggregateLocal && operands[0].Store == aggregateStore &&
			operands[0].StoredValue == new ManagedFlowValue(
				ManagedFlowValueKind.CheckedAdd,
				Instruction: aggregateStore - 1) &&
			operands[1] == new ManagedFlowValue(ManagedFlowValueKind.Constant, Constant: cap);
	}

	private static bool IsExactLocalVersion(
		DirectedGraph graph,
		IReadOnlyList<Instruction> instructions,
		int store,
		int load,
		int local)
	{
		if (!graph.Dominates(store, load))
		{
			return false;
		}
		return !instructions.Select((instruction, index) => (instruction, index)).Any(pair =>
			pair.index != store &&
			TryGetLocalIndex(pair.instruction, load: false, out int candidateLocal) &&
			candidateLocal == local &&
			graph.CanReach(store, pair.index) &&
			graph.CanReach(pair.index, load));
	}

	private static bool TryCreateAggregateCapCheck(
		Instruction[] instructions,
		int branch,
		bool greaterBranches,
		[NotNullWhen(true)] out AggregateCapCheck? check)
	{
		check = null;
		if (instructions[branch].Operand is not int targetOffset)
		{
			return false;
		}
		int target = Array.FindIndex(instructions, instruction => instruction.Offset == targetOffset);
		if (target < 0 || branch + 1 >= instructions.Length)
		{
			return false;
		}
		check = greaterBranches
			? new AggregateCapCheck(branch, target, branch + 1)
			: new AggregateCapCheck(branch, branch + 1, target);
		return true;
	}

	private static void AssertCreateGuardGraph(
		MethodInfo create,
		Instruction[] instructions,
		ISet<string> violations,
		AggregateCapCheck? inputCapCheck = null,
		AggregateCapCheck? outputCapCheck = null)
	{
		DirectedGraph graph = DirectedGraph.FromInstructions(instructions);
		int countCall = FindCallIndex(instructions, create, "get_Count");
		int allocation = Array.FindIndex(instructions, instruction => instruction.OpCode == OpCodes.Newarr);
		int indexer = FindCallIndices(instructions, create, "get_Item").Min();
		int constructor = Array.FindLastIndex(instructions, instruction =>
			instruction.OpCode == OpCodes.Newobj && ResolveMember(create, instruction).DeclaringType == typeof(LiquidWalletObservationBatch));
		int[] countBranches = instructions.Select((instruction, index) => (instruction, index))
			.Where(pair => pair.index > countCall && pair.index < allocation && pair.instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
			.Select(pair => pair.index)
			.ToArray();
		if (countBranches.Length != 2 || countBranches.Any(branch =>
			!graph.Dominates(branch, allocation) || !graph.Dominates(branch, indexer) || !graph.Dominates(branch, constructor)))
		{
			violations.Add("COUNT_GUARD_DOMINANCE");
		}

		int inputGetter = FindCallIndex(instructions, create, "get_InputCount");
		int outputGetter = FindCallIndices(instructions, create, "get_OwnedOutputCount").Min();
		int transactionIdGetter = FindCallIndices(
			instructions,
			create,
			nameof(LiquidWalletTransactionObservation.GetTransactionIdConsensusBytes)).Min();
		int inputAdd = Array.FindIndex(instructions, inputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int outputAdd = Array.FindIndex(instructions, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		inputCapCheck ??= ResolveAggregateCapCheck(create, instructions, inputGetter, outputGetter, MaxAggregateInputCount);
		outputCapCheck ??= ResolveAggregateCapCheck(create, instructions, outputGetter, transactionIdGetter, MaxAggregateOwnedOutputCount);
		int inputBranch = inputCapCheck?.Branch ?? FindNextConditionalBranch(instructions, inputAdd);
		int outputBranch = outputCapCheck?.Branch ?? FindNextConditionalBranch(instructions, outputAdd);
		int loopBack = Array.FindLastIndex(instructions, instruction =>
			instruction.Operand is int target && target < instruction.Offset &&
			instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch);
		if (!graph.DominatesFrom(inputAdd, inputBranch, outputGetter) ||
			!graph.DominatesFrom(inputAdd, inputBranch, transactionIdGetter) ||
			!graph.DominatesFrom(inputAdd, inputBranch, loopBack) ||
			!graph.DominatesFrom(inputAdd, inputBranch, constructor))
		{
			violations.Add("INPUT_CAP_DOMINANCE");
		}
		if (!graph.DominatesFrom(outputAdd, outputBranch, transactionIdGetter) ||
			!graph.DominatesFrom(outputAdd, outputBranch, loopBack) ||
			!graph.DominatesFrom(outputAdd, outputBranch, constructor))
		{
			violations.Add("OUTPUT_CAP_DOMINANCE");
		}
		if (inputCapCheck is not null && !HasExactCapFailureEdge(
			create,
			graph,
			instructions,
			inputCapCheck,
			outputGetter,
			constructor,
			"The wallet observation aggregate input limit was exceeded."))
		{
			violations.Add("INPUT_CAP_FAILURE_EDGE");
		}
		if (outputCapCheck is not null && !HasExactCapFailureEdge(
			create,
			graph,
			instructions,
			outputCapCheck,
			transactionIdGetter,
			constructor,
			"The wallet observation aggregate owned-output limit was exceeded."))
		{
			violations.Add("OUTPUT_CAP_FAILURE_EDGE");
		}
	}

	private static AggregateCapCheck? ResolveAggregateCapCheck(
		MethodInfo create,
		Instruction[] instructions,
		int getter,
		int nextWork,
		int cap)
	{
		int add = Array.FindIndex(instructions, getter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		if (add < 0 || add >= nextWork || add + 1 >= instructions.Length ||
			!TryGetLocalIndex(instructions[add + 1], load: false, out int aggregateLocal))
		{
			return null;
		}
		DirectedGraph graph = DirectedGraph.FromInstructions(instructions);
		ManagedValueFlow valueFlow = ManagedValueFlow.Analyze(create, instructions);
		return TryResolveAggregateCapCheck(
			create,
			instructions,
			add + 1,
			nextWork,
			aggregateLocal,
			cap,
			graph,
			valueFlow,
			out AggregateCapCheck? check)
			? check
			: null;
	}

	private static bool HasExactCapFailureEdge(
		MethodInfo create,
		DirectedGraph graph,
		Instruction[] instructions,
		AggregateCapCheck check,
		int nextWork,
		int constructor,
		string expectedMessage)
	{
		int expectedThrow = Enumerable.Range(check.Branch + 1, nextWork - check.Branch - 1)
			.FirstOrDefault(index => IsExactCapThrow(create, instructions, index, expectedMessage), -1);
		if (expectedThrow < 0 ||
			!graph.Successors(check.Branch).Order().SequenceEqual(new[] { check.GreaterSuccessor, check.NotGreaterSuccessor }.Order()))
		{
			return false;
		}
		return AllPathsReachBefore(graph, check.GreaterSuccessor, expectedThrow, new HashSet<int> { nextWork, constructor }) &&
			AllPathsReachBefore(graph, check.NotGreaterSuccessor, nextWork, new HashSet<int> { expectedThrow });
	}

	private static bool IsExactCapThrow(
		MethodInfo create,
		Instruction[] instructions,
		int index,
		string expectedMessage)
	{
		if (instructions[index].OpCode != OpCodes.Throw)
		{
			return false;
		}
		int constructor = FindProducerIndex(create, instructions, index, 0);
		return instructions[constructor].OpCode == OpCodes.Newobj &&
			ResolveMember(create, instructions[constructor]).DeclaringType == typeof(ArgumentOutOfRangeException) &&
			ResolveStringOrigin(create, instructions, constructor, 1) == "transactions" &&
			ResolveStringOrigin(create, instructions, constructor, 0) == expectedMessage;
	}

	private static bool AllPathsReachBefore(
		DirectedGraph graph,
		int start,
		int required,
		IReadOnlySet<int> forbidden)
	{
		var pending = new Stack<(int Node, HashSet<int> Path)>();
		pending.Push((start, []));
		while (pending.TryPop(out (int Node, HashSet<int> Path) state))
		{
			if (state.Node == required)
			{
				continue;
			}
			if (forbidden.Contains(state.Node) || !state.Path.Add(state.Node))
			{
				return false;
			}
			int[] successors = graph.Successors(state.Node);
			if (successors.Length == 0)
			{
				return false;
			}
			foreach (int successor in successors)
			{
				pending.Push((successor, [.. state.Path]));
			}
		}
		return true;
	}

	private static int[] FindCallIndices(Instruction[] instructions, MethodBase caller, string methodName) =>
		instructions.Select((instruction, index) => (instruction, index))
			.Where(pair => pair.instruction.OpCode.OperandType == OperandType.InlineMethod &&
				ResolveMember(caller, pair.instruction).Name == methodName)
			.Select(pair => pair.index)
			.ToArray();

	private static int FindCallIndex(Instruction[] instructions, MethodBase caller, string methodName)
	{
		int[] indices = FindCallIndices(instructions, caller, methodName);
		return Assert.Single(indices);
	}

	private static string ResolveStringOrigin(
		MethodBase method,
		Instruction[] instructions,
		int consumerIndex,
		int fromTop)
	{
		int producer = FindProducerIndex(method, instructions, consumerIndex, fromTop);
		Instruction instruction = instructions[producer];
		return instruction.OpCode == OpCodes.Ldstr
			? ResolveInstructionString(method, instruction)
			: ResolveOrigin(method, instructions, consumerIndex, fromTop);
	}

	private static string ResolveOrigin(
		MethodBase method,
		Instruction[] instructions,
		int consumerIndex,
		int fromTop,
		ISet<(int Consumer, int FromTop)>? visited = null)
	{
		visited ??= new HashSet<(int Consumer, int FromTop)>();
		if (!visited.Add((consumerIndex, fromTop)))
		{
			return "cycle";
		}
		int producer = FindProducerIndex(method, instructions, consumerIndex, fromTop);
		Instruction instruction = instructions[producer];
		if (TryGetArgumentIndex(instruction, out int argument))
		{
			return $"arg:{argument}";
		}
		if (TryGetLocalIndex(instruction, load: true, out int local))
		{
			int store = FindPreviousLocalStore(instructions, producer, local);
			return store < 0 ? $"local:{local}:unset" : ResolveOrigin(method, instructions, store, 0, visited);
		}
		if (instruction.OpCode == OpCodes.Ldstr)
		{
			return "string:" + ResolveInstructionString(method, instruction);
		}
		if (instruction.OpCode == OpCodes.Ldnull)
		{
			return "null";
		}
		if (instruction.OpCode.OperandType == OperandType.InlineMethod &&
			instruction.OpCode is var opcode && (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj))
		{
			return CallOrigin(method, instruction, producer);
		}
		if (TryGetInt32Constant(instruction, out int value))
		{
			return $"i4:{value}";
		}
		return $"opcode:{instruction.OpCode.Name}@{producer}";
	}

	private static string CallOrigin(MethodBase method, Instruction instruction, int index) =>
		$"call:{MemberKey(ResolveMember(method, instruction))}@{index}";

	private static int FindProducerIndex(
		MethodBase method,
		IReadOnlyList<Instruction> instructions,
		int consumerIndex,
		int fromTop)
	{
		int depth = fromTop;
		for (int index = consumerIndex - 1; index >= 0; index--)
		{
			int pushes = PushCount(method, instructions[index]);
			if (depth < pushes)
			{
				return index;
			}
			depth = checked(depth - pushes + PopCount(method, instructions[index]));
		}
		throw new InvalidOperationException("A stack producer could not be resolved.");
	}

	private static int PushCount(MethodBase caller, Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
	{
		StackBehaviour.Push0 => 0,
		StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or
			StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
		StackBehaviour.Push1_push1 => 2,
		StackBehaviour.Varpush => instruction.OpCode == OpCodes.Newobj ||
			ResolveMember(caller, instruction) is MethodInfo method && method.ReturnType != typeof(void) ? 1 : 0,
		_ => throw new InvalidOperationException($"Unsupported push behavior {instruction.OpCode.StackBehaviourPush}."),
	};

	private static int PopCount(MethodBase caller, Instruction instruction) => instruction.OpCode.StackBehaviourPop switch
	{
		StackBehaviour.Pop0 => 0,
		StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
		StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or
			StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or
			StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
		StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or
			StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or
			StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
		StackBehaviour.Varpop => VariablePopCount(caller, instruction),
		_ => throw new InvalidOperationException($"Unsupported pop behavior {instruction.OpCode.StackBehaviourPop}."),
	};

	private static int VariablePopCount(MethodBase caller, Instruction instruction)
	{
		if (instruction.OpCode == OpCodes.Ret)
		{
			return caller is MethodInfo method && method.ReturnType != typeof(void) ? 1 : 0;
		}
		MethodBase target = (MethodBase)ResolveMember(caller, instruction);
		return target.GetParameters().Length +
			(instruction.OpCode != OpCodes.Newobj && !target.IsStatic ? 1 : 0);
	}

	private static bool TryGetArgumentIndex(Instruction instruction, out int index)
	{
		if (instruction.OpCode == OpCodes.Ldarg_0) { index = 0; return true; }
		if (instruction.OpCode == OpCodes.Ldarg_1) { index = 1; return true; }
		if (instruction.OpCode == OpCodes.Ldarg_2) { index = 2; return true; }
		if (instruction.OpCode == OpCodes.Ldarg_3) { index = 3; return true; }
		if (instruction.OpCode is var opcode && (opcode == OpCodes.Ldarg || opcode == OpCodes.Ldarg_S))
		{
			index = Convert.ToInt32(instruction.Operand, CultureInfo.InvariantCulture);
			return true;
		}
		index = -1;
		return false;
	}

	private static bool TryGetLocalIndex(Instruction instruction, bool load, out int index)
	{
		OpCode opcode = instruction.OpCode;
		if (load)
		{
			if (opcode == OpCodes.Ldloc_0) { index = 0; return true; }
			if (opcode == OpCodes.Ldloc_1) { index = 1; return true; }
			if (opcode == OpCodes.Ldloc_2) { index = 2; return true; }
			if (opcode == OpCodes.Ldloc_3) { index = 3; return true; }
			if (opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S)
			{
				index = Convert.ToInt32(instruction.Operand, CultureInfo.InvariantCulture);
				return true;
			}
		}
		else
		{
			if (opcode == OpCodes.Stloc_0) { index = 0; return true; }
			if (opcode == OpCodes.Stloc_1) { index = 1; return true; }
			if (opcode == OpCodes.Stloc_2) { index = 2; return true; }
			if (opcode == OpCodes.Stloc_3) { index = 3; return true; }
			if (opcode == OpCodes.Stloc || opcode == OpCodes.Stloc_S)
			{
				index = Convert.ToInt32(instruction.Operand, CultureInfo.InvariantCulture);
				return true;
			}
		}
		index = -1;
		return false;
	}

	private static int FindPreviousLocalStore(IReadOnlyList<Instruction> instructions, int before, int local)
	{
		for (int index = before - 1; index >= 0; index--)
		{
			if (TryGetLocalIndex(instructions[index], load: false, out int candidate) && candidate == local)
			{
				return index;
			}
		}
		return -1;
	}

	private static bool TryGetInt32Constant(Instruction instruction, out int value)
	{
		if (instruction.OpCode == OpCodes.Ldc_I4_M1) { value = -1; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_0) { value = 0; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_1) { value = 1; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_2) { value = 2; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_3) { value = 3; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_4) { value = 4; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_5) { value = 5; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_6) { value = 6; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_7) { value = 7; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4_8) { value = 8; return true; }
		if (instruction.OpCode == OpCodes.Ldc_I4 || instruction.OpCode == OpCodes.Ldc_I4_S)
		{
			value = Convert.ToInt32(instruction.Operand, CultureInfo.InvariantCulture);
			return true;
		}
		value = 0;
		return false;
	}

	private static string ResolveInstructionString(MethodBase method, Instruction instruction) =>
		instruction.Operand is string synthetic
			? synthetic
			: method.Module.ResolveString((int)instruction.Operand!);

	private static void AssertCreateMutationRejected(
		MethodInfo create,
		Instruction[] baseline,
		Instruction[] mutation,
		params string[] expectedViolations)
	{
		Assert.Empty(VerifyCreateValueAndGuardFlow(create, baseline));
		IReadOnlyList<string> violations = VerifyCreateValueAndGuardFlow(create, mutation);
		Assert.Equal(expectedViolations.Order(StringComparer.Ordinal), violations);
	}

	private static void AssertGuardMutationRejected(
		MethodInfo create,
		Instruction[] baseline,
		Instruction[] mutation,
		string expectedViolation,
		bool requireConsistentStack = true)
	{
		Assert.True(HasConsistentEvaluationStack(create, baseline));
		if (requireConsistentStack)
		{
			Assert.True(
				HasConsistentEvaluationStack(create, mutation),
				$"The {expectedViolation} control must remain evaluation-stack consistent.");
		}
		var baselineViolations = new HashSet<string>(StringComparer.Ordinal);
		AssertCreateGuardGraph(create, baseline, baselineViolations);
		Assert.Empty(baselineViolations);
		var violations = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			AssertCreateGuardGraph(create, mutation, violations);
		}
		catch (Exception)
		{
			violations.Add("CFG_INVALID");
		}
		Assert.Equal([expectedViolation], violations.Order(StringComparer.Ordinal));
	}

	private static bool HasConsistentEvaluationStack(MethodInfo method, Instruction[] instructions)
	{
		DirectedGraph graph;
		try
		{
			graph = DirectedGraph.FromInstructions(instructions);
		}
		catch (Exception)
		{
			return false;
		}
		var entries = new int?[instructions.Length];
		var pending = new Queue<int>();
		entries[0] = 0;
		pending.Enqueue(0);
		bool sawTerminal = false;
		while (pending.TryDequeue(out int index))
		{
			int entry = entries[index]!.Value;
			int pop;
			int push;
			try
			{
				pop = PopCount(method, instructions[index]);
				push = PushCount(method, instructions[index]);
			}
			catch (Exception)
			{
				return false;
			}
			if (entry < pop)
			{
				return false;
			}
			int exit = entry - pop + push;
			if (instructions[index].OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
			{
				sawTerminal = true;
				if (exit != 0)
				{
					return false;
				}
			}
			foreach (int successor in graph.Successors(index))
			{
				if (entries[successor] is int existing && existing != exit)
				{
					return false;
				}
				if (entries[successor] is null)
				{
					entries[successor] = exit;
					pending.Enqueue(successor);
				}
			}
		}
		return sawTerminal;
	}

	private static Instruction[] WrongFirstNullArgument(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int nullCheck = FindCallIndices(result, create, nameof(ArgumentNullException.ThrowIfNull))[0];
		int producer = FindProducerIndex(create, result, nullCheck, 1);
		result[producer] = result[producer] with { OpCode = OpCodes.Ldnull, Operand = null };
		return result;
	}

	private static Instruction[] ConflictingFirstNullName(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int nullCheck = FindCallIndices(result, create, nameof(ArgumentNullException.ThrowIfNull))[0];
		int objectProducer = FindProducerIndex(create, result, nullCheck, 1);
		int nameProducer = FindProducerIndex(create, result, nullCheck, 0);
		int firstProducer = Math.Min(objectProducer, nameProducer);
		return
		[
			.. result[..firstProducer],
			new Instruction(-240, -239, OpCodes.Ldc_I4_0, null),
			new Instruction(-239, -238, OpCodes.Brtrue, -234),
			.. result[firstProducer..nullCheck],
			new Instruction(-238, -237, OpCodes.Br, result[nullCheck].Offset),
			new Instruction(-234, -233, OpCodes.Ldarg_0, null),
			new Instruction(-233, -232, OpCodes.Ldstr, "otherTransactions"),
			new Instruction(-232, -231, OpCodes.Br, result[nullCheck].Offset),
			.. result[nullCheck..],
		];
	}

	private static Instruction[] BranchOverOriginalNullGuard(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int nullCheck = FindCallIndices(result, create, nameof(ArgumentNullException.ThrowIfNull))[0];
		int objectProducer = FindProducerIndex(create, result, nullCheck, 1);
		int nameProducer = FindProducerIndex(create, result, nullCheck, 0);
		int firstProducer = Math.Min(objectProducer, nameProducer);
		return
		[
			.. result[..firstProducer],
			new Instruction(-200, -199, OpCodes.Ldc_I4_0, null),
			new Instruction(-199, -198, OpCodes.Brtrue, result[nullCheck + 1].Offset),
			.. result[firstProducer..(nullCheck + 1)],
			.. result[(nullCheck + 1)..],
		];
	}

	private static Instruction[] ConflictingCountReceiver(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int countCall = FindCallIndex(result, create, "get_Count");
		int receiver = FindProducerIndex(create, result, countCall, 0);
		return
		[
			.. result[..receiver],
			new Instruction(-246, -245, OpCodes.Ldc_I4_0, null),
			new Instruction(-245, -244, OpCodes.Brtrue, -240),
			.. result[receiver..countCall],
			new Instruction(-244, -243, OpCodes.Br, result[countCall].Offset),
			new Instruction(-240, -239, OpCodes.Ldnull, null),
			new Instruction(-239, -238, OpCodes.Br, result[countCall].Offset),
			.. result[countCall..],
		];
	}

	private static Instruction[] ConflictingIndexerReceiver(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int indexer = FindCallIndex(result, create, "get_Item");
		int receiver = FindProducerIndex(create, result, indexer, 1);
		int index = FindProducerIndex(create, result, indexer, 0);
		Assert.True(receiver < index);
		Assert.True(TryGetLocalIndex(result[index], load: true, out _));
		RedirectIncomingBranches(result, result[receiver].Offset, -252);
		Instruction wrongIndex = result[index] with { Offset = -245, EndOffset = -244 };
		return
		[
			.. result[..receiver],
			new Instruction(-252, -251, OpCodes.Ldc_I4_0, null),
			new Instruction(-251, -250, OpCodes.Brtrue, -246),
			.. result[receiver..indexer],
			new Instruction(-250, -249, OpCodes.Br, result[indexer].Offset),
			new Instruction(-246, -245, OpCodes.Ldnull, null),
			wrongIndex,
			new Instruction(-244, -243, OpCodes.Br, result[indexer].Offset),
			.. result[indexer..],
		];
	}

	private static Instruction[] WrongCapturedNullArgument(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int nullCheck = FindCallIndices(result, create, nameof(ArgumentNullException.ThrowIfNull))[1];
		int producer = FindProducerIndex(create, result, nullCheck, 1);
		result[producer] = result[producer] with { OpCode = OpCodes.Ldnull, Operand = null };
		return result;
	}

	private static Instruction[] ConflictingCapturedObservationStore(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int nullCheck = FindCallIndices(result, create, nameof(ArgumentNullException.ThrowIfNull))[1];
		int capturedLoad = FindProducerIndex(create, result, nullCheck, 1);
		Assert.True(TryGetLocalIndex(result[capturedLoad], load: true, out int capturedLocal));
		Assert.True(FindPreviousLocalStore(result, capturedLoad, capturedLocal) >= 0);
		RedirectIncomingBranches(result, result[capturedLoad].Offset, -204);
		return
		[
			.. result[..capturedLoad],
			new Instruction(-204, -203, OpCodes.Ldc_I4_0, null),
			new Instruction(-203, -202, OpCodes.Brfalse, result[capturedLoad].Offset),
			new Instruction(-202, -201, OpCodes.Ldnull, null),
			new Instruction(-201, -200, OpCodes.Stloc, capturedLocal),
			.. result[capturedLoad..],
		];
	}

	private static Instruction[] DuplicateCapturedObservationStore(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int itemCall = FindCallIndex(result, create, "get_Item");
		int capturedStore = itemCall + 1;
		Assert.True(TryGetLocalIndex(result[capturedStore], load: false, out int capturedLocal));
		return
		[
			.. result[..capturedStore],
			new Instruction(-206, -205, OpCodes.Dup, null),
			new Instruction(-205, -204, OpCodes.Stloc, FindAlternativeLocal(result, capturedLocal)),
			.. result[capturedStore..],
		];
	}

	private static Instruction[] BranchOverCapturedNullGuard(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int nullCheck = FindCallIndices(result, create, nameof(ArgumentNullException.ThrowIfNull))[1];
		int objectProducer = FindProducerIndex(create, result, nullCheck, 1);
		int nameProducer = FindProducerIndex(create, result, nullCheck, 0);
		int firstProducer = Math.Min(objectProducer, nameProducer);
		return
		[
			.. result[..firstProducer],
			new Instruction(-210, -209, OpCodes.Ldc_I4_0, null),
			new Instruction(-209, -208, OpCodes.Brtrue, result[nullCheck + 1].Offset),
			.. result[firstProducer..(nullCheck + 1)],
			.. result[(nullCheck + 1)..],
		];
	}

	private static Instruction[] SubstituteExceptionArgument(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int constructor = Array.FindIndex(result, instruction =>
			instruction.OpCode == OpCodes.Newobj && ResolveMember(create, instruction).DeclaringType == typeof(ArgumentOutOfRangeException));
		int message = FindProducerIndex(create, result, constructor, 0);
		result[message] = result[message] with { Operand = "transactions" };
		return result;
	}

	private static Instruction[] BranchOverCorrectExceptionArguments(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int constructor = Array.FindIndex(result, instruction =>
			instruction.OpCode == OpCodes.Newobj &&
			ResolveMember(create, instruction).DeclaringType == typeof(ArgumentException));
		Assert.True(constructor >= 0);
		int firstProducer = Math.Min(
			FindProducerIndex(create, result, constructor, 1),
			FindProducerIndex(create, result, constructor, 0));
		return
		[
			.. result[..firstProducer],
			new Instruction(-214, -213, OpCodes.Ldc_I4_0, null),
			new Instruction(-213, -212, OpCodes.Brtrue, -210),
			.. result[firstProducer..constructor],
			new Instruction(-212, -211, OpCodes.Br, result[constructor].Offset),
			new Instruction(-210, -209, OpCodes.Ldstr, "transactions"),
			new Instruction(-209, -208, OpCodes.Ldstr, "transactions"),
			new Instruction(-208, -207, OpCodes.Br, result[constructor].Offset),
			.. result[constructor..],
		];
	}

	private static Instruction[] ReplaceInputGetterWithOutputGetter(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int input = FindCallIndex(result, create, "get_InputCount");
		int output = FindCallIndex(result, create, "get_OwnedOutputCount");
		result[input] = result[input] with { Operand = result[output].Operand };
		return result;
	}

	private static Instruction[] BranchOverCorrectObservationMemberReceiver(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int memberCall = FindCallIndex(result, create, "get_InputCount");
		int receiver = FindProducerIndex(create, result, memberCall, 0);
		return
		[
			.. result[..receiver],
			new Instruction(-218, -217, OpCodes.Ldc_I4_0, null),
			new Instruction(-217, -216, OpCodes.Brtrue, -214),
			.. result[receiver..memberCall],
			new Instruction(-216, -215, OpCodes.Br, result[memberCall].Offset),
			new Instruction(-214, -213, OpCodes.Ldnull, null),
			new Instruction(-213, -212, OpCodes.Br, result[memberCall].Offset),
			.. result[memberCall..],
		];
	}

	private static Instruction[] BranchOverCorrectArrayStoreValue(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int arrayStore = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Stelem_Ref);
		int valueProducer = FindProducerIndex(create, result, arrayStore, 0);
		return
		[
			.. result[..valueProducer],
			new Instruction(-222, -221, OpCodes.Ldc_I4_0, null),
			new Instruction(-221, -220, OpCodes.Brtrue, -218),
			.. result[valueProducer..arrayStore],
			new Instruction(-220, -219, OpCodes.Br, result[arrayStore].Offset),
			new Instruction(-218, -217, OpCodes.Ldnull, null),
			new Instruction(-217, -216, OpCodes.Br, result[arrayStore].Offset),
			.. result[arrayStore..],
		];
	}

	private static void RedirectIncomingBranches(Instruction[] instructions, int oldTarget, int newTarget)
	{
		for (int index = 0; index < instructions.Length; index++)
		{
			if (instructions[index].Operand is int target && target == oldTarget)
			{
				instructions[index] = instructions[index] with { Operand = newTarget };
			}
		}
	}

	private static Instruction[] ReplaceAggregateLocalBeforeAdd(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int load = FindProducerIndex(create, result, add, 1);
		result[load] = result[load] with { OpCode = OpCodes.Ldloc, Operand = 3 };
		return result;
	}

	private static Instruction[] BranchOverCorrectInputAddProducers(MethodInfo create, Instruction[] baseline) =>
		BranchOverCorrectAddProducers(create, baseline, "get_InputCount", -120);

	private static Instruction[] ReplaceInputCapConstant(Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int cap = Array.FindIndex(result, instruction => TryGetInt32Constant(instruction, out int value) && value == MaxAggregateInputCount);
		result[cap] = result[cap] with { OpCode = OpCodes.Ldc_I4, Operand = MaxAggregateOwnedOutputCount };
		return result;
	}

	private static Instruction[] DiscardCheckedInputResult(Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(add >= 0);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out _));
		result[add + 1] = result[add + 1] with { OpCode = OpCodes.Pop, Operand = null };
		return result;
	}

	private static Instruction[] StoreCheckedInputResultInStaleLocal(Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		result[add + 1] = result[add + 1] with
		{
			OpCode = OpCodes.Stloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return result;
	}

	private static Instruction[] LoadSubstitutedInputAggregateForCap(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		result[aggregateLoad] = result[aggregateLoad] with
		{
			OpCode = OpCodes.Ldloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return result;
	}

	private static Instruction[] CompareUnrelatedInputCapOperand(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int cap = FindProducerIndex(create, result, comparison, 0);
		result[cap] = result[cap] with
		{
			OpCode = OpCodes.Ldloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return result;
	}

	private static Instruction[] InvertInputCapBranch(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		InvertAggregateCapBranch(result, add);
		return result;
	}

	private static Instruction[] OverwriteCheckedInputAggregateBeforeCap(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		return
		[
			.. result[..aggregateLoad],
			new Instruction(-104, -103, OpCodes.Ldc_I4_0, null),
			new Instruction(-103, -102, OpCodes.Stloc, aggregateLocal),
			.. result[aggregateLoad..],
		];
	}

	private static Instruction[] RetainDeadCorrectInputLoadButCompareSubstitute(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int add = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		result[aggregateLoad] = result[aggregateLoad] with
		{
			OpCode = OpCodes.Ldloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return
		[
			.. result[..aggregateLoad],
			new Instruction(-106, -105, OpCodes.Ldloc, aggregateLocal),
			new Instruction(-105, -104, OpCodes.Pop, null),
			.. result[aggregateLoad..],
		];
	}

	private static Instruction[] BranchOverCorrectInputCapProducers(MethodInfo create, Instruction[] baseline) =>
		BranchOverCorrectCapProducers(create, baseline, "get_InputCount", MaxAggregateInputCount, -124);

	private static Instruction[] UnequalStackJoinBeforeInputCapProducers(MethodInfo create, Instruction[] baseline) =>
		UnequalStackJoinBeforeCorrectCapProducers(create, baseline, "get_InputCount", -140);

	private static Instruction[] DiscardCheckedOutputResult(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(add >= 0 && TryGetLocalIndex(result[add + 1], load: false, out _));
		result[add + 1] = result[add + 1] with { OpCode = OpCodes.Pop, Operand = null };
		return result;
	}

	private static Instruction[] StoreCheckedOutputResultInStaleLocal(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		result[add + 1] = result[add + 1] with
		{
			OpCode = OpCodes.Stloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return result;
	}

	private static Instruction[] BranchOverCorrectOutputAddProducers(MethodInfo create, Instruction[] baseline) =>
		BranchOverCorrectAddProducers(create, baseline, "get_OwnedOutputCount", -128);

	private static Instruction[] LoadSubstitutedOutputAggregateForCap(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		result[aggregateLoad] = result[aggregateLoad] with
		{
			OpCode = OpCodes.Ldloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return result;
	}

	private static Instruction[] OverwriteCheckedOutputAggregateBeforeCap(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		return
		[
			.. result[..aggregateLoad],
			new Instruction(-108, -107, OpCodes.Ldc_I4_0, null),
			new Instruction(-107, -106, OpCodes.Stloc, aggregateLocal),
			.. result[aggregateLoad..],
		];
	}

	private static Instruction[] CompareUnrelatedOutputCapOperand(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int cap = FindProducerIndex(create, result, comparison, 0);
		result[cap] = result[cap] with
		{
			OpCode = OpCodes.Ldloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return result;
	}

	private static Instruction[] RetainDeadCorrectOutputLoadButCompareSubstitute(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		result[aggregateLoad] = result[aggregateLoad] with
		{
			OpCode = OpCodes.Ldloc,
			Operand = FindAlternativeLocal(result, aggregateLocal),
		};
		return
		[
			.. result[..aggregateLoad],
			new Instruction(-110, -109, OpCodes.Ldloc, aggregateLocal),
			new Instruction(-109, -108, OpCodes.Pop, null),
			.. result[aggregateLoad..],
		];
	}

	private static Instruction[] BranchOverCorrectOutputCapProducers(MethodInfo create, Instruction[] baseline) =>
		BranchOverCorrectCapProducers(create, baseline, "get_OwnedOutputCount", MaxAggregateOwnedOutputCount, -132);

	private static Instruction[] UnequalStackJoinBeforeOutputCapProducers(MethodInfo create, Instruction[] baseline) =>
		UnequalStackJoinBeforeCorrectCapProducers(create, baseline, "get_OwnedOutputCount", -146);

	private static Instruction[] InvertOutputCapBranch(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		InvertAggregateCapBranch(result, add);
		return result;
	}

	private static Instruction[] SwapAggregateCapExceptionMessages(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int inputConstructor = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Newobj &&
			ResolveMember(create, instruction).DeclaringType == typeof(ArgumentOutOfRangeException) &&
			ResolveStringOrigin(create, result, Array.IndexOf(result, instruction), 0) ==
				"The wallet observation aggregate input limit was exceeded.");
		int outputConstructor = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Newobj &&
			ResolveMember(create, instruction).DeclaringType == typeof(ArgumentOutOfRangeException) &&
			ResolveStringOrigin(create, result, Array.IndexOf(result, instruction), 0) ==
				"The wallet observation aggregate owned-output limit was exceeded.");
		Assert.True(inputConstructor >= 0 && outputConstructor >= 0);
		int inputMessage = FindProducerIndex(create, result, inputConstructor, 0);
		int outputMessage = FindProducerIndex(create, result, outputConstructor, 0);
		(result[inputMessage], result[outputMessage]) =
			(result[inputMessage] with { Operand = result[outputMessage].Operand },
				result[outputMessage] with { Operand = result[inputMessage].Operand });
		return result;
	}

	private static Instruction[] BranchOverCorrectAddProducers(
		MethodInfo create,
		Instruction[] baseline,
		string getterName,
		int syntheticOffset)
	{
		Instruction[] result = CloneInstructions(baseline);
		int getter = FindCallIndex(result, create, getterName);
		int add = Array.FindIndex(result, getter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int aggregateLoad = FindProducerIndex(create, result, add, 1);
		Assert.True(TryGetLocalIndex(result[aggregateLoad], load: true, out int aggregateLocal));
		RedirectIncomingBranches(result, result[aggregateLoad].Offset, syntheticOffset);
		return
		[
			.. result[..aggregateLoad],
			new Instruction(syntheticOffset, syntheticOffset + 1, OpCodes.Ldc_I4_0, null),
			new Instruction(syntheticOffset + 1, syntheticOffset + 2, OpCodes.Brtrue, syntheticOffset + 5),
			.. result[aggregateLoad..add],
			new Instruction(syntheticOffset + 2, syntheticOffset + 3, OpCodes.Br, result[add].Offset),
			new Instruction(syntheticOffset + 5, syntheticOffset + 6, OpCodes.Ldloc, aggregateLocal),
			new Instruction(syntheticOffset + 6, syntheticOffset + 7, OpCodes.Ldc_I4_0, null),
			new Instruction(syntheticOffset + 7, syntheticOffset + 8, OpCodes.Br, result[add].Offset),
			.. result[add..],
		];
	}

	private static Instruction[] BranchOverCorrectCapProducers(
		MethodInfo create,
		Instruction[] baseline,
		string getterName,
		int cap,
		int syntheticOffset)
	{
		Instruction[] result = CloneInstructions(baseline);
		int getter = FindCallIndex(result, create, getterName);
		int add = Array.FindIndex(result, getter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		Assert.True(TryGetLocalIndex(result[add + 1], load: false, out int aggregateLocal));
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		return
		[
			.. result[..aggregateLoad],
			new Instruction(syntheticOffset, syntheticOffset + 1, OpCodes.Ldloc, FindAlternativeLocal(result, aggregateLocal)),
			new Instruction(syntheticOffset + 1, syntheticOffset + 2, OpCodes.Ldc_I4, cap),
			new Instruction(syntheticOffset + 2, syntheticOffset + 3, OpCodes.Br, result[comparison].Offset),
			.. result[aggregateLoad..],
		];
	}

	private static Instruction[] UnequalStackJoinBeforeCorrectCapProducers(
		MethodInfo create,
		Instruction[] baseline,
		string getterName,
		int syntheticOffset)
	{
		Instruction[] result = CloneInstructions(baseline);
		int getter = FindCallIndex(result, create, getterName);
		int add = Array.FindIndex(result, getter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int comparison = FindAggregateCapComparison(result, add);
		int aggregateLoad = FindProducerIndex(create, result, comparison, 1);
		for (int index = 0; index < result.Length; index++)
		{
			if (result[index].Operand is int target && target == result[aggregateLoad].Offset)
			{
				result[index] = result[index] with { Operand = syntheticOffset };
			}
		}
		return
		[
			.. result[..aggregateLoad],
			new Instruction(syntheticOffset, syntheticOffset + 1, OpCodes.Ldc_I4_0, null),
			new Instruction(syntheticOffset + 1, syntheticOffset + 2, OpCodes.Brfalse, syntheticOffset + 4),
			new Instruction(syntheticOffset + 2, syntheticOffset + 3, OpCodes.Ldc_I4_0, null),
			new Instruction(syntheticOffset + 3, syntheticOffset + 4, OpCodes.Br, result[aggregateLoad].Offset),
			new Instruction(syntheticOffset + 4, syntheticOffset + 5, OpCodes.Br, result[aggregateLoad].Offset),
			.. result[aggregateLoad..],
		];
	}

	private static int FindAggregateCapComparison(Instruction[] instructions, int add) =>
		Array.FindIndex(instructions, add + 1, instruction =>
			instruction.OpCode == OpCodes.Cgt || instruction.OpCode == OpCodes.Ble || instruction.OpCode == OpCodes.Ble_S ||
			instruction.OpCode == OpCodes.Bgt || instruction.OpCode == OpCodes.Bgt_S);

	private static void InvertAggregateCapBranch(Instruction[] instructions, int add)
	{
		int comparison = FindAggregateCapComparison(instructions, add);
		int branch = instructions[comparison].OpCode == OpCodes.Cgt
			? FindNextConditionalBranch(instructions, comparison)
			: comparison;
		OpCode opcode = instructions[branch].OpCode;
		Assert.True(opcode == OpCodes.Ble || opcode == OpCodes.Ble_S || opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S ||
			opcode == OpCodes.Brtrue || opcode == OpCodes.Brtrue_S || opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S);
		instructions[branch] = instructions[branch] with
		{
			OpCode = opcode == OpCodes.Ble ? OpCodes.Bgt :
				opcode == OpCodes.Ble_S ? OpCodes.Bgt_S :
				opcode == OpCodes.Bgt ? OpCodes.Ble :
				opcode == OpCodes.Bgt_S ? OpCodes.Ble_S :
				opcode == OpCodes.Brtrue ? OpCodes.Brfalse :
				opcode == OpCodes.Brtrue_S ? OpCodes.Brfalse_S :
				opcode == OpCodes.Brfalse ? OpCodes.Brtrue : OpCodes.Brtrue_S,
		};
	}

	private static int FindAlternativeLocal(IEnumerable<Instruction> instructions, int excluded) =>
		instructions.Select(instruction =>
			TryGetLocalIndex(instruction, load: true, out int local) ? local : -1)
			.First(local => local >= 0 && local != excluded);

	private static Instruction[] AppendUnrelatedCheckedAdd(Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int terminalReturn = Array.FindLastIndex(result, instruction => instruction.OpCode == OpCodes.Ret);
		Assert.Equal(result.Length - 1, terminalReturn);
		return
		[
			.. result[..terminalReturn],
			new Instruction(-48, -47, OpCodes.Ldc_I4_0, null),
			new Instruction(-47, -46, OpCodes.Ldc_I4_0, null),
			new Instruction(-46, -45, OpCodes.Add_Ovf, null),
			new Instruction(-45, -44, OpCodes.Pop, null),
			result[terminalReturn],
		];
	}

	private static Instruction[] ReplaceOutputGetterWithUnrelatedGetter(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int output = FindCallIndex(result, create, "get_OwnedOutputCount");
		MethodInfo unrelated = typeof(LiquidWalletObservationBatch)
			.GetProperty(nameof(LiquidWalletObservationBatch.OwnedOutputCount), DeclaredMemberFlags)!
			.GetMethod!;
		result[output] = result[output] with { Operand = unrelated.MetadataToken };
		return result;
	}

	private static Instruction[] ReplaceOutputAggregateLocalBeforeAdd(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int add = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int load = FindProducerIndex(create, result, add, 1);
		result[load] = result[load] with { OpCode = OpCodes.Ldloc, Operand = 2 };
		return result;
	}

	private static Instruction[] ReplaceOutputCapConstant(Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int cap = Array.FindIndex(result, instruction =>
			TryGetInt32Constant(instruction, out int value) && value == MaxAggregateOwnedOutputCount);
		result[cap] = result[cap] with { OpCode = OpCodes.Ldc_I4, Operand = MaxAggregateInputCount };
		return result;
	}

	private static Instruction[] InsertPreGuardAllocation(MethodInfo create, Instruction[] baseline)
	{
		Instruction template = baseline.First(instruction => instruction.OpCode == OpCodes.Newarr);
		return
		[
			new Instruction(-24, -23, OpCodes.Ldc_I4_0, null),
			template with { Offset = -23, EndOffset = -22 },
			new Instruction(-22, -21, OpCodes.Pop, null),
			.. CloneInstructions(baseline),
		];
	}

	private static Instruction[] InsertPreGuardIndexer(MethodInfo create, Instruction[] baseline)
	{
		Instruction template = baseline[FindCallIndex(baseline, create, "get_Item")];
		return
		[
			new Instruction(-28, -27, OpCodes.Ldarg_0, null),
			new Instruction(-27, -26, OpCodes.Ldc_I4_0, null),
			template with { Offset = -26, EndOffset = -25 },
			new Instruction(-25, -24, OpCodes.Pop, null),
			.. CloneInstructions(baseline),
		];
	}

	private static Instruction[] MoveOutputWorkBeforeInputCap(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int inputAdd = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int branch = FindNextConditionalBranch(result, inputAdd);
		int output = FindCallIndex(result, create, "get_OwnedOutputCount");
		int receiver = FindProducerIndex(create, result, output, 0);
		return
		[
			.. result[..branch],
			result[receiver] with { Offset = -32, EndOffset = -31 },
			result[output] with { Offset = -31, EndOffset = -30 },
			new Instruction(-30, -29, OpCodes.Pop, null),
			.. result[branch..],
		];
	}

	private static Instruction[] ContinueInputCapFailureToConstruction(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int inputAdd = Array.FindIndex(result, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int branch = FindNextConditionalBranch(result, inputAdd);
		int successTarget = (int)result[branch].Operand!;
		int failureThrow = Array.FindIndex(result, branch + 1, instruction =>
			instruction.Offset < successTarget && instruction.OpCode == OpCodes.Throw);
		int constructor = Array.FindLastIndex(result, instruction =>
			instruction.OpCode == OpCodes.Newobj && ResolveMember(create, instruction).DeclaringType == typeof(LiquidWalletObservationBatch));
		int constructorArguments = PopCount(create, result[constructor]);
		int firstConstructorProducer = Enumerable.Range(0, constructorArguments)
			.Select(fromTop => FindProducerIndex(create, result, constructor, fromTop))
			.Min();
		return
		[
			.. result[..failureThrow],
			result[failureThrow] with { OpCode = OpCodes.Pop, Operand = null },
			new Instruction(-36, -35, OpCodes.Br, result[firstConstructorProducer].Offset),
			.. result[(failureThrow + 1)..],
		];
	}

	private static Instruction[] MoveTransactionIdWorkBeforeOutputCap(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int outputAdd = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int branch = FindNextConditionalBranch(result, outputAdd);
		int transactionIdGetter = FindCallIndex(
			result,
			create,
			nameof(LiquidWalletTransactionObservation.GetTransactionIdConsensusBytes));
		int receiver = FindProducerIndex(create, result, transactionIdGetter, 0);
		return
		[
			.. result[..branch],
			result[receiver] with { Offset = -40, EndOffset = -39 },
			result[transactionIdGetter] with { Offset = -39, EndOffset = -38 },
			new Instruction(-38, -37, OpCodes.Pop, null),
			.. result[branch..],
		];
	}

	private static Instruction[] ContinueOutputCapFailureToConstruction(MethodInfo create, Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int outputGetter = FindCallIndex(result, create, "get_OwnedOutputCount");
		int outputAdd = Array.FindIndex(result, outputGetter + 1, instruction => instruction.OpCode == OpCodes.Add_Ovf);
		int branch = FindNextConditionalBranch(result, outputAdd);
		int successTarget = (int)result[branch].Operand!;
		int failureThrow = Array.FindIndex(result, branch + 1, instruction =>
			instruction.Offset < successTarget && instruction.OpCode == OpCodes.Throw);
		int constructor = Array.FindLastIndex(result, instruction =>
			instruction.OpCode == OpCodes.Newobj &&
			ResolveMember(create, instruction).DeclaringType == typeof(LiquidWalletObservationBatch));
		int constructorArguments = PopCount(create, result[constructor]);
		int firstConstructorProducer = Enumerable.Range(0, constructorArguments)
			.Select(fromTop => FindProducerIndex(create, result, constructor, fromTop))
			.Min();
		return
		[
			.. result[..failureThrow],
			result[failureThrow] with { OpCode = OpCodes.Pop, Operand = null },
			new Instruction(-44, -43, OpCodes.Br, result[firstConstructorProducer].Offset),
			.. result[(failureThrow + 1)..],
		];
	}

	private static Instruction[] ReplaceBranchWithUnresolvedTarget(Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		int branch = Array.FindIndex(result, instruction =>
			instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch &&
			instruction.Operand is int);
		result[branch] = result[branch] with { Operand = int.MaxValue };
		return result;
	}

	private static Instruction[] DuplicateInstructionOffset(Instruction[] baseline)
	{
		Instruction[] result = CloneInstructions(baseline);
		result[1] = result[1] with { Offset = result[0].Offset };
		return result;
	}

	private static Instruction[] CloneInstructions(Instruction[] instructions) => [.. instructions];

	private static byte[] NormalizeMethodInstructions(MethodBase method, IReadOnlyList<Instruction> instructions)
	{
		IReadOnlyDictionary<int, int> indices = instructions
			.Select((instruction, index) => (instruction.Offset, index))
			.ToDictionary(pair => pair.Offset, pair => pair.index);
		var manifest = new StringBuilder();
		manifest.Append("METHOD|").Append(MethodKey(method)).Append('\n');
		for (int index = 0; index < instructions.Count; index++)
		{
			Instruction instruction = instructions[index];
			manifest.Append(index.ToString("D4", CultureInfo.InvariantCulture)).Append('|').Append(instruction.OpCode.Name);
			string? operand = NormalizeOperand(method, instruction, indices);
			if (operand is not null)
			{
				manifest.Append('|').Append(operand);
			}
			manifest.Append('\n');
		}
		return Encoding.UTF8.GetBytes(manifest.ToString());
	}

	private static void AssertNormalizedMutationRejected(
		MethodBase method,
		byte[] baseline,
		Instruction[] mutation,
		OpCode expectedOpcode)
	{
		Assert.Contains(mutation, instruction => instruction.OpCode == expectedOpcode);
		Assert.NotEqual(
			Convert.ToHexString(SHA256.HashData(baseline)),
			Convert.ToHexString(SHA256.HashData(NormalizeMethodInstructions(method, mutation))));
	}

	private static TypeDefinitionHandle FindType(MetadataReader reader, Type reflectedType)
	{
		foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
		{
			TypeDefinition definition = reader.GetTypeDefinition(handle);
			bool nameMatches = reader.GetString(definition.Name) == reflectedType.Name;
			bool locationMatches = !reflectedType.IsNested
				? reader.GetString(definition.Namespace) == reflectedType.Namespace
				: !definition.GetDeclaringType().IsNil &&
					reader.GetString(reader.GetTypeDefinition(definition.GetDeclaringType()).Name) == reflectedType.DeclaringType!.Name;
			if (nameMatches && locationMatches)
			{
				return handle;
			}
		}
		throw new InvalidOperationException($"Raw type row for {reflectedType.FullName} was not found.");
	}

	private static TypeDefinitionHandle FindType(MetadataReader reader, string @namespace, string name)
	{
		TypeDefinitionHandle[] named = reader.TypeDefinitions.Where(handle =>
			reader.GetString(reader.GetTypeDefinition(handle).Name) == name).ToArray();
		foreach (TypeDefinitionHandle handle in named)
		{
			if (TypeDefinitionNamespace(reader, handle) == @namespace)
			{
				return handle;
			}
		}
		if (named.Length == 1)
		{
			return named[0];
		}
		throw new InvalidOperationException($"Raw type row for {@namespace}.{name} was not found.");
	}

	private static string TypeDefinitionNamespace(MetadataReader reader, TypeDefinitionHandle handle)
	{
		var visited = new HashSet<TypeDefinitionHandle>();
		while (!handle.IsNil && visited.Add(handle))
		{
			int row = MetadataTokens.GetRowNumber(handle);
			if (row <= 0 || row > reader.GetTableRowCount(TableIndex.TypeDef))
			{
				return string.Empty;
			}
			TypeDefinition definition = reader.GetTypeDefinition(handle);
			string @namespace = reader.GetString(definition.Namespace);
			if (!string.IsNullOrEmpty(@namespace))
			{
				return @namespace;
			}
			handle = definition.GetDeclaringType();
		}
		return string.Empty;
	}

	private static void AssertRawSignatureAccepted(RawSignaturePolicy policy, byte[] bytes) =>
		Assert.Empty(VerifyRawSignature(policy, bytes));

	private static void AssertRawSignatureRejected(
		RawSignaturePolicy policy,
		byte[] bytes,
		string expectedViolation)
	{
		IReadOnlyList<string> violations = VerifyRawSignature(policy, bytes);
		Assert.Contains(expectedViolation, violations);
	}

	private static void AssertRawSignatureRejectedExactly(
		RawSignaturePolicy policy,
		byte[] bytes,
		string expectedViolation) =>
		Assert.Equal([expectedViolation], VerifyRawSignature(policy, bytes));

	private static readonly RawAttributeView RawNullableInterfaceAttribute = new(
		"System.Runtime.CompilerServices.NullableAttribute::.ctor",
		Convert.FromHexString("01000200000000010000"));

	private static IReadOnlyList<string> VerifyRawInterfaceAttribute(RawInterfaceAttributeView view)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		if (view.InterfaceType != TypeKey(typeof(IEquatable<LiquidWalletObservationBatch>)))
		{
			violations.Add("INTERFACE_TYPE");
		}
		if (view.Attributes.Length != 1)
		{
			violations.Add("INTERFACE_ATTRIBUTE_COUNT");
			return violations.Order(StringComparer.Ordinal).ToArray();
		}
		RawAttributeView attribute = view.Attributes[0];
		if (attribute.ConstructorKey != RawNullableInterfaceAttribute.ConstructorKey)
		{
			violations.Add("INTERFACE_ATTRIBUTE_CONSTRUCTOR");
		}
		if (!attribute.Blob.AsSpan().SequenceEqual(RawNullableInterfaceAttribute.Blob))
		{
			violations.Add("INTERFACE_NULLABILITY");
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static void AssertRawInterfaceViolation(RawInterfaceAttributeView view, string expectedViolation) =>
		Assert.Contains(expectedViolation, VerifyRawInterfaceAttribute(view));

	private static IReadOnlyList<string> VerifyRawTypeGraph(RawTypeGraph graph)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		var active = new HashSet<int>();
		var completed = new HashSet<int>();
		Visit(graph.Root);
		return violations.Order(StringComparer.Ordinal).ToArray();

		void Visit(int handle)
		{
			if (completed.Contains(handle))
			{
				return;
			}
			if (!graph.Nodes.TryGetValue(handle, out RawTypeNode? node))
			{
				violations.Add("UNRESOLVED_TYPE_ROOT");
				return;
			}
			if (!active.Add(handle))
			{
				violations.Add("TYPE_SPEC_CYCLE");
				return;
			}
			if (node.IsModified)
			{
				violations.Add("CUSTOM_MODIFIER");
			}
			if (node.Next is int next)
			{
				Visit(next);
			}
			active.Remove(handle);
			completed.Add(handle);
		}
	}

	private static void AssertRawTypeGraphViolation(RawTypeGraph graph, string expectedViolation) =>
		Assert.Contains(expectedViolation, VerifyRawTypeGraph(graph));

	private static byte[] BuildRawPeFixture(RawPeMutation mutation)
	{
		var metadata = new MetadataBuilder();
		var ilStream = new BlobBuilder();
		var methodBodies = new MethodBodyStreamEncoder(ilStream);
		StringHandle moduleName = metadata.GetOrAddString("RawObservationBatchFixture.dll");
		metadata.AddModule(
			0,
			moduleName,
			metadata.GetOrAddGuid(new Guid("38dc3b1a-4cb1-4cb7-a111-0c1ae4c2e159")),
			default,
			default);
		metadata.AddAssembly(
			metadata.GetOrAddString("RawObservationBatchFixture"),
			new Version(1, 0, 0, 0),
			default,
			default,
			default,
			AssemblyHashAlgorithm.Sha256);
		AssemblyReferenceHandle coreLibrary = metadata.AddAssemblyReference(
			metadata.GetOrAddString("System.Runtime"),
			new Version(10, 0, 0, 0),
			default,
			metadata.GetOrAddBlob(Convert.FromHexString("b03f5f7f11d50a3a")),
			default,
			default);
		AssemblyReferenceHandle productAssembly = AddExactAssemblyReference(
			metadata,
			typeof(LiquidWalletState).Assembly.GetName());
		TypeReferenceHandle objectType = metadata.AddTypeReference(
			coreLibrary,
			metadata.GetOrAddString("System"),
			metadata.GetOrAddString("Object"));
		TypeReferenceHandle systemType = metadata.AddTypeReference(
			coreLibrary,
			metadata.GetOrAddString("System"),
			metadata.GetOrAddString("Type"));
		TypeReferenceHandle int32Type = metadata.AddTypeReference(
			coreLibrary,
			metadata.GetOrAddString("System"),
			metadata.GetOrAddString("Int32"));
		TypeReferenceHandle iEquatableType = metadata.AddTypeReference(
			coreLibrary,
			metadata.GetOrAddString("System"),
			metadata.GetOrAddString("IEquatable`1"));
		TypeReferenceHandle nullableAttributeType = metadata.AddTypeReference(
			coreLibrary,
			metadata.GetOrAddString("System.Runtime.CompilerServices"),
			metadata.GetOrAddString("NullableAttribute"));
		TypeReferenceHandle typeCarrierAttributeType = metadata.AddTypeReference(
			coreLibrary,
			metadata.GetOrAddString("RawFixture"),
			metadata.GetOrAddString("TypeCarrierAttribute"));
		TypeReferenceHandle forbiddenType = metadata.AddTypeReference(
			productAssembly,
			metadata.GetOrAddString("WalletWasabi.Liquid.Wallet"),
			metadata.GetOrAddString("LiquidWalletState"));
		string exactSerializedObservation = typeof(LiquidWalletTransactionObservation).AssemblyQualifiedName!;
		string exactSerializedForbidden = typeof(LiquidWalletState).AssemblyQualifiedName!;
		AssemblyName observationAssembly = typeof(LiquidWalletTransactionObservation).Assembly.GetName();
		string observationToken = Convert.ToHexString(observationAssembly.GetPublicKeyToken() ?? []).ToLowerInvariant();
		if (string.IsNullOrEmpty(observationToken))
		{
			observationToken = "null";
		}
		string counterfeitSerializedObservation = exactSerializedObservation.Replace(
			$", {observationAssembly.Name},",
			", Alias.Assembly,",
			StringComparison.Ordinal);
		string wrongVersionSerializedObservation = exactSerializedObservation.Replace(
			$"Version={observationAssembly.Version}",
			"Version=0.0.0.0",
			StringComparison.Ordinal);
		string wrongTokenSerializedObservation = exactSerializedObservation.Replace(
			$"PublicKeyToken={observationToken}",
			"PublicKeyToken=0011223344556677",
			StringComparison.Ordinal);
		bool methodAssemblyReferenceFixture = mutation is
			RawPeMutation.MethodAssemblyReferenceTypeAlias or
			RawPeMutation.MethodAssemblyReferenceCrossApproved or
			RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture or
			RawPeMutation.MethodAssemblyReferenceRetargetable;
		bool typeReferenceScopeFixture = mutation is
			RawPeMutation.AssemblyReferenceTypeAlias or RawPeMutation.MethodAssemblyReferenceTypeAlias or
			RawPeMutation.AssemblyReferenceCrossApproved or RawPeMutation.MethodAssemblyReferenceCrossApproved or
			RawPeMutation.AssemblyReferenceWrongVersion or
			RawPeMutation.AssemblyReferenceWrongCulture or RawPeMutation.AssemblyReferenceLiteralNeutralCulture or
			RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture or RawPeMutation.AssemblyReferenceWrongToken or
			RawPeMutation.AssemblyReferencePublicKey or RawPeMutation.AssemblyReferenceRetargetable or
			RawPeMutation.AssemblyReferenceWindowsRuntime or
			RawPeMutation.AssemblyReferenceHash or
			RawPeMutation.MethodAssemblyReferenceRetargetable or RawPeMutation.ModuleReferenceTypeScope or
			RawPeMutation.ModuleDefinitionTypeScope or
			RawPeMutation.TypeReferenceScopeCycle or RawPeMutation.TypeReferenceScopeUnresolved or
			RawPeMutation.TypeReferenceUnexpectedScope or RawPeMutation.TypeCarryingModuleReferenceAttribute or
			RawPeMutation.TypeCarryingModuleDefinitionAttribute;
		EntityHandle scopedObjectType = objectType;
		EntityHandle scopedObjectEndpoint = mutation == RawPeMutation.TypeCarryingAssemblyReferenceAttribute
			? coreLibrary
			: default;
		if (typeReferenceScopeFixture)
		{
			EntityHandle scope = mutation switch
			{
				RawPeMutation.AssemblyReferenceTypeAlias or
				RawPeMutation.MethodAssemblyReferenceTypeAlias or
				RawPeMutation.AssemblyReferenceCrossApproved or
				RawPeMutation.MethodAssemblyReferenceCrossApproved or
				RawPeMutation.AssemblyReferenceWrongVersion or
				RawPeMutation.AssemblyReferenceWrongCulture or
				RawPeMutation.AssemblyReferenceLiteralNeutralCulture or
				RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture or
				RawPeMutation.AssemblyReferenceWrongToken or
				RawPeMutation.AssemblyReferencePublicKey or
				RawPeMutation.AssemblyReferenceRetargetable or
				RawPeMutation.AssemblyReferenceWindowsRuntime or
				RawPeMutation.AssemblyReferenceHash or
				RawPeMutation.MethodAssemblyReferenceRetargetable => metadata.AddAssemblyReference(
					metadata.GetOrAddString(mutation switch
					{
						RawPeMutation.AssemblyReferenceTypeAlias or
							RawPeMutation.MethodAssemblyReferenceTypeAlias => "System.Arbitrary",
						RawPeMutation.AssemblyReferenceCrossApproved or
							RawPeMutation.MethodAssemblyReferenceCrossApproved => "System.Collections",
						_ => "System.Runtime",
					}),
					mutation == RawPeMutation.AssemblyReferenceWrongVersion
						? new Version(9, 0, 0, 0)
						: new Version(10, 0, 0, 0),
					mutation switch
					{
						RawPeMutation.AssemblyReferenceWrongCulture => metadata.GetOrAddString("zz"),
						RawPeMutation.AssemblyReferenceLiteralNeutralCulture or
							RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture => metadata.GetOrAddString("neutral"),
						_ => default,
					},
					metadata.GetOrAddBlob(Convert.FromHexString(
						mutation == RawPeMutation.AssemblyReferenceWrongToken
							? "0102030405060708"
							: "b03f5f7f11d50a3a")),
					mutation switch
					{
						RawPeMutation.AssemblyReferencePublicKey => AssemblyFlags.PublicKey,
						RawPeMutation.AssemblyReferenceRetargetable or
							RawPeMutation.MethodAssemblyReferenceRetargetable => AssemblyFlags.Retargetable,
						RawPeMutation.AssemblyReferenceWindowsRuntime => AssemblyFlags.WindowsRuntime,
						_ => default,
					},
					mutation == RawPeMutation.AssemblyReferenceHash
						? metadata.GetOrAddBlob(Convert.FromHexString("a1b2c3d4"))
						: default),
				RawPeMutation.ModuleReferenceTypeScope or RawPeMutation.TypeCarryingModuleReferenceAttribute => metadata.AddModuleReference(
					metadata.GetOrAddString("ScopedTypes.netmodule")),
				RawPeMutation.ModuleDefinitionTypeScope or RawPeMutation.TypeCarryingModuleDefinitionAttribute => MetadataTokens.EntityHandle(1),
				RawPeMutation.TypeReferenceScopeCycle => MetadataTokens.TypeReferenceHandle(8),
				RawPeMutation.TypeReferenceScopeUnresolved => MetadataTokens.TypeReferenceHandle(63),
				RawPeMutation.TypeReferenceUnexpectedScope => default,
				_ => throw new InvalidOperationException(mutation.ToString()),
			};
			scopedObjectEndpoint = scope;
			scopedObjectType = metadata.AddTypeReference(
				scope,
				mutation is RawPeMutation.TypeReferenceScopeCycle or RawPeMutation.TypeReferenceScopeUnresolved
					? default
					: metadata.GetOrAddString("System"),
				metadata.GetOrAddString(methodAssemblyReferenceFixture ? "Type" : "Object"));
			if (mutation == RawPeMutation.TypeReferenceScopeCycle)
			{
				_ = metadata.AddTypeReference(
					(TypeReferenceHandle)scopedObjectType,
					default,
					metadata.GetOrAddString("Object"));
			}
		}
		TypeReferenceHandle nestedMethodType = default;
		if (mutation == RawPeMutation.NestedTypeReferenceScope)
		{
			TypeReferenceHandle nestedOuterType = metadata.AddTypeReference(
				coreLibrary,
				metadata.GetOrAddString("System"),
				metadata.GetOrAddString("Environment"));
			nestedMethodType = metadata.AddTypeReference(
				nestedOuterType,
				default,
				metadata.GetOrAddString("SpecialFolder"));
		}
		TypeReferenceHandle mixedCycleScopedType = default;
		if (mutation == RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope)
		{
			mixedCycleScopedType = metadata.AddTypeReference(
				default,
				default,
				metadata.GetOrAddString("ScopedCycleType"));
		}
		TypeReferenceHandle topLevelNestedAlias = mutation == RawPeMutation.TopLevelNestedTypeReferenceAlias
			? metadata.AddTypeReference(
				coreLibrary,
				metadata.GetOrAddString("System"),
				metadata.GetOrAddString("Environment+SpecialFolder"))
			: default;
		TypeReferenceHandle genericMetadataNameAlias = mutation == RawPeMutation.MethodGenericMetadataNameAlias
			? metadata.AddTypeReference(
				coreLibrary,
				metadata.GetOrAddString("System.Collections.Generic"),
				metadata.GetOrAddString($"IList`1<{RawTypeKey(typeof(int))}>"))
			: default;

		metadata.AddTypeDefinition(
			TypeAttributes.NotPublic,
			default,
			metadata.GetOrAddString("<Module>"),
			default,
			MetadataTokens.FieldDefinitionHandle(1),
			MetadataTokens.MethodDefinitionHandle(1));
		TypeDefinitionHandle localObjectAlias = default;
		if (mutation is RawPeMutation.LocalTypeDefinitionAlias or RawPeMutation.MethodLocalTypeDefinitionAlias)
		{
			localObjectAlias = metadata.AddTypeDefinition(
				TypeAttributes.Public,
				metadata.GetOrAddString("System"),
				metadata.GetOrAddString(
					mutation == RawPeMutation.MethodLocalTypeDefinitionAlias ? "Type" : "Object"),
				objectType,
				MetadataTokens.FieldDefinitionHandle(1),
				MetadataTokens.MethodDefinitionHandle(1));
		}
		TypeDefinitionHandle localProductAlias = default;
		if (mutation is RawPeMutation.MethodLocalObservationTypeDefinitionAlias or
			RawPeMutation.FieldLocalBatchTypeDefinitionAlias)
		{
			localProductAlias = metadata.AddTypeDefinition(
				TypeAttributes.Public | TypeAttributes.Sealed,
				metadata.GetOrAddString("WalletWasabi.Liquid.Wallet"),
				metadata.GetOrAddString(
					mutation == RawPeMutation.MethodLocalObservationTypeDefinitionAlias
						? "LiquidWalletTransactionObservation"
						: "LiquidWalletObservationBatch"),
				objectType,
				MetadataTokens.FieldDefinitionHandle(1),
				MetadataTokens.MethodDefinitionHandle(1));
		}

		EntityHandle baseType = typeReferenceScopeFixture && mutation is not RawPeMutation.MethodAssemblyReferenceTypeAlias and
			not RawPeMutation.MethodAssemblyReferenceCrossApproved and
			not RawPeMutation.MethodAssemblyReferenceRetargetable and
			not RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture
			? scopedObjectType
			: mutation == RawPeMutation.LocalTypeDefinitionAlias
				? localObjectAlias
				: objectType;
		if (mutation is RawPeMutation.BaseTypeModifier or RawPeMutation.BaseTypeCycle or RawPeMutation.BaseTypeUnresolved)
		{
			byte[] baseSignature = mutation switch
			{
				RawPeMutation.BaseTypeModifier => ConcatBytes(
					[0x1f],
					EncodeTypeDefOrRef(forbiddenType),
					[0x12],
					EncodeTypeDefOrRef(objectType)),
				RawPeMutation.BaseTypeCycle => ConcatBytes(
					[0x12],
					EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(1))),
				_ => ConcatBytes(
					[0x12],
					EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(63))),
			};
			baseType = metadata.AddTypeSpecification(metadata.GetOrAddBlob(baseSignature));
		}

		byte[] modifiedInt32 = ConcatBytes([0x1f], EncodeTypeDefOrRef(forbiddenType), [0x08]);
		byte[] modifiedInt64 = ConcatBytes([0x1f], EncodeTypeDefOrRef(forbiddenType), [0x0a]);
		byte[] modifiedVoid = ConcatBytes([0x1f], EncodeTypeDefOrRef(forbiddenType), [0x01]);
		byte[] modifiedGenericMethodParameter = ConcatBytes(
			[0x1f], EncodeTypeDefOrRef(forbiddenType), [0x1e, 0x00]);
		bool fieldTypeSpecificationFixture = mutation is
			RawPeMutation.FieldTypeSpecInt32 or RawPeMutation.FieldTypeSpecInt32AsClass;
		TypeSpecificationHandle fieldTypeSpecification = fieldTypeSpecificationFixture
			? metadata.AddTypeSpecification(metadata.GetOrAddBlob(ConcatBytes(
				[0x12], EncodeTypeDefOrRef(systemType))))
			: default;
		byte[] fieldSignature = mutation switch
		{
			RawPeMutation.FieldModifier => ConcatBytes([0x06], modifiedInt32),
			RawPeMutation.FieldPrimitiveInt32AsTypeReference => ConcatBytes(
				[0x06, 0x11], EncodeTypeDefOrRef(int32Type)),
			RawPeMutation.FieldSzArrayPrimitiveInt32AsTypeReference => ConcatBytes(
				[0x06, 0x1d, 0x11], EncodeTypeDefOrRef(int32Type)),
			RawPeMutation.FieldMdArrayPrimitiveInt32AsTypeReference => ConcatBytes(
				[0x06, 0x14, 0x11], EncodeTypeDefOrRef(int32Type), [0x01, 0x00, 0x00]),
			RawPeMutation.FieldLocalBatchTypeDefinitionAlias => ConcatBytes(
				[0x06, 0x12], EncodeTypeDefOrRef(localProductAlias)),
			RawPeMutation.FieldTypeSpecInt32 => ConcatBytes(
				[0x06, 0x11], EncodeTypeDefOrRef(fieldTypeSpecification)),
			RawPeMutation.FieldTypeSpecInt32AsClass => ConcatBytes(
				[0x06, 0x12], EncodeTypeDefOrRef(fieldTypeSpecification)),
			RawPeMutation.FieldSzArray => [0x06, 0x1d, 0x08],
			RawPeMutation.FieldMdArrayRankOne or
			RawPeMutation.FieldSzArrayAsMdRankOne => [0x06, 0x14, 0x08, 0x01, 0x00, 0x00],
			RawPeMutation.FieldMdArrayExplicitSize => [0x06, 0x14, 0x08, 0x01, 0x01, 0x04, 0x00],
			RawPeMutation.FieldMdArrayLowerBound => [0x06, 0x14, 0x08, 0x01, 0x00, 0x01, 0x00],
			_ => [0x06, 0x08],
		};
		bool literalField = mutation is RawPeMutation.LiteralFieldDefinition or RawPeMutation.MutatedLiteralField;
		FieldAttributes fieldAttributes = literalField
			? FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault
			: FieldAttributes.Private;
		if (mutation == RawPeMutation.NotSerializedField)
		{
			fieldAttributes |= (FieldAttributes)0x00000080;
		}
		if (mutation == RawPeMutation.MarshaledField)
		{
			fieldAttributes |= FieldAttributes.HasFieldMarshal;
		}
		FieldDefinitionHandle field = metadata.AddFieldDefinition(
			fieldAttributes,
			metadata.GetOrAddString("_value"),
			metadata.GetOrAddBlob(fieldSignature));
		if (literalField)
		{
			metadata.AddConstant(field, mutation == RawPeMutation.MutatedLiteralField ? 2 : 1);
		}
		if (mutation == RawPeMutation.MarshaledField)
		{
			metadata.AddMarshallingDescriptor(field, metadata.GetOrAddBlob(new byte[] { 0x07 }));
		}
		bool methodTypeSpecificationFixture = mutation is
			RawPeMutation.MethodTypeSpecObject or RawPeMutation.MethodTypeSpecObjectAsValueType or
			RawPeMutation.MethodTypeSpecModifier or
			RawPeMutation.MethodTypeSpecTrailingData or RawPeMutation.MethodTypeSpecCycle or
			RawPeMutation.MethodTypeSpecUnresolved or RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope or
			RawPeMutation.MethodTypeSpecNestedCycleAttribute;
		EntityHandle methodReturnType = default;
		TypeSpecificationHandle nestedAttributedTypeSpecification = default;
		if (methodTypeSpecificationFixture)
		{
			if (mutation == RawPeMutation.MethodTypeSpecNestedCycleAttribute)
			{
				methodReturnType = metadata.AddTypeSpecification(metadata.GetOrAddBlob(ConcatBytes(
					[0x12], EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(2)))));
				nestedAttributedTypeSpecification = metadata.AddTypeSpecification(metadata.GetOrAddBlob(ConcatBytes(
					[0x12], EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(1)))));
			}
			else
			{
				methodReturnType = mutation == RawPeMutation.MethodTypeSpecUnresolved
				? MetadataTokens.TypeSpecificationHandle(63)
				: metadata.AddTypeSpecification(metadata.GetOrAddBlob(mutation switch
				{
					RawPeMutation.MethodTypeSpecObject or
					RawPeMutation.MethodTypeSpecObjectAsValueType => ConcatBytes(
						[0x12], EncodeTypeDefOrRef(systemType)),
					RawPeMutation.MethodTypeSpecModifier => ConcatBytes(
						[0x1f], EncodeTypeDefOrRef(forbiddenType), [0x12], EncodeTypeDefOrRef(systemType)),
					RawPeMutation.MethodTypeSpecTrailingData => ConcatBytes(
						[0x12], EncodeTypeDefOrRef(systemType), [0x00]),
					RawPeMutation.MethodTypeSpecCycle => ConcatBytes(
						[0x12], EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(1))),
					RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope => ConcatBytes(
						[0x15, 0x12], EncodeTypeDefOrRef(objectType),
						[0x02, 0x12], EncodeTypeDefOrRef(mixedCycleScopedType),
						[0x12], EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(1))),
					_ => throw new InvalidOperationException(mutation.ToString()),
				}));
			}
		}

		byte[] methodSignature = mutation switch
		{
			RawPeMutation.UnmanagedMethodDefinition => [0x01, 0x00, 0x01],
			RawPeMutation.ReservedMethodHeader => [0x80, 0x00, 0x01],
			RawPeMutation.ZeroArityGenericBitMethodDefinition => [0x10, 0x00, 0x00, 0x01],
			RawPeMutation.SelfConsistentGenericMethodDefinition => [0x10, 0x01, 0x00, 0x01],
			RawPeMutation.VarArgsMethodDefinition => [0x05, 0x00, 0x01],
			RawPeMutation.InstanceMethodDefinition => [0x20, 0x00, 0x01],
			RawPeMutation.ExplicitThisMethodDefinition => [0x60, 0x00, 0x01],
			RawPeMutation.MethodModifier => ConcatBytes([0x00, 0x00], modifiedVoid),
			RawPeMutation.MethodPrimitiveInt32AsTypeReference => ConcatBytes(
				[0x00, 0x00, 0x11], EncodeTypeDefOrRef(int32Type)),
			RawPeMutation.MethodTypeSpecObject or
			RawPeMutation.MethodTypeSpecObjectAsValueType or
			RawPeMutation.MethodTypeSpecModifier or
			RawPeMutation.MethodTypeSpecTrailingData or
			RawPeMutation.MethodTypeSpecCycle or
			RawPeMutation.MethodTypeSpecUnresolved or
			RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope or
			RawPeMutation.MethodTypeSpecNestedCycleAttribute => ConcatBytes(
				[0x00, 0x00,
					mutation == RawPeMutation.MethodTypeSpecObjectAsValueType ? (byte)0x11 : (byte)0x12],
				EncodeTypeDefOrRef(methodReturnType)),
			RawPeMutation.MethodAssemblyReferenceTypeAlias or
			RawPeMutation.MethodAssemblyReferenceCrossApproved or
			RawPeMutation.MethodAssemblyReferenceRetargetable or
			RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture => ConcatBytes(
				[0x00, 0x00, 0x12], EncodeTypeDefOrRef(scopedObjectType)),
			RawPeMutation.MethodLocalTypeDefinitionAlias => ConcatBytes(
				[0x00, 0x00, 0x12], EncodeTypeDefOrRef(localObjectAlias)),
			RawPeMutation.MethodLocalObservationTypeDefinitionAlias => ConcatBytes(
				[0x00, 0x00, 0x12], EncodeTypeDefOrRef(localProductAlias)),
			RawPeMutation.MethodObjectAsValueType => ConcatBytes(
				[0x00, 0x00, 0x11], EncodeTypeDefOrRef(systemType)),
			RawPeMutation.NestedTypeReferenceScope => ConcatBytes(
				[0x00, 0x00, 0x11], EncodeTypeDefOrRef(nestedMethodType)),
			RawPeMutation.TopLevelNestedTypeReferenceAlias => ConcatBytes(
				[0x00, 0x00, 0x11], EncodeTypeDefOrRef(topLevelNestedAlias)),
			RawPeMutation.MethodGenericMetadataNameAlias => ConcatBytes(
				[0x00, 0x00, 0x12], EncodeTypeDefOrRef(genericMetadataNameAlias)),
			RawPeMutation.TypeCarryingParameterAttribute or
				RawPeMutation.ParameterizedMethodDefinition or
				RawPeMutation.WrongParameterName or
				RawPeMutation.OptionalParameter or
				RawPeMutation.DefaultParameter or
				RawPeMutation.MarshaledParameter => [0x00, 0x01, 0x01, 0x08],
			RawPeMutation.TypeCarryingGenericParameterAttribute or
				RawPeMutation.TypeCarryingGenericConstraintAttribute or
				RawPeMutation.MethodGenericConstraintObject or
				RawPeMutation.MethodGenericConstraintTypeSpecObject or
				RawPeMutation.MethodGenericConstraintForbidden or
				RawPeMutation.MethodGenericConstraintModifier or
				RawPeMutation.MethodGenericConstraintUnresolved or
				RawPeMutation.MethodGenericConstraintCycle or
				RawPeMutation.MethodGenericConstraintTrailingData => [0x10, 0x01, 0x00, 0x01],
			_ => [0x00, 0x00, 0x01],
		};

		EntityHandle memberParent = objectType;
		if (mutation == RawPeMutation.MemberReferenceParentModifier)
		{
			memberParent = metadata.AddTypeSpecification(metadata.GetOrAddBlob(ConcatBytes(
				[0x1f],
				EncodeTypeDefOrRef(forbiddenType),
				[0x12],
				EncodeTypeDefOrRef(objectType))));
		}
		bool methodMemberTypeSpecificationFixture = mutation is
			RawPeMutation.MethodMemberReferenceTypeSpecObject or
			RawPeMutation.MethodMemberReferenceTypeSpecObjectAsValueType;
		TypeSpecificationHandle methodMemberTypeSpecification = methodMemberTypeSpecificationFixture
			? metadata.AddTypeSpecification(metadata.GetOrAddBlob(ConcatBytes(
				[0x12], EncodeTypeDefOrRef(systemType))))
			: default;
		byte[] genericMemberSignature = mutation switch
		{
			RawPeMutation.UnmanagedMethodMemberReference => [0x31, 0x01, 0x01, 0x01, 0x1e, 0x00],
			RawPeMutation.MethodSpecificationNonGenericParent => [0x20, 0x00, 0x01],
			RawPeMutation.MethodSpecificationZeroArityGenericParent => [0x30, 0x00, 0x00, 0x01],
			RawPeMutation.UnauthorizedMethodMemberReferenceType => ConcatBytes(
				[0x30, 0x01, 0x01, 0x01, 0x12],
				EncodeTypeDefOrRef(forbiddenType)),
			RawPeMutation.UnauthorizedMethodMemberReferenceReturnType => ConcatBytes(
				[0x30, 0x01, 0x01, 0x12],
				EncodeTypeDefOrRef(forbiddenType),
				[0x1e, 0x00]),
			RawPeMutation.MethodMemberReferenceModifier => ConcatBytes(
				[0x30, 0x01, 0x01, 0x01],
				modifiedGenericMethodParameter),
			RawPeMutation.MethodMemberReferenceTypeSpecObject or
			RawPeMutation.MethodMemberReferenceTypeSpecObjectAsValueType => ConcatBytes(
				[0x30, 0x01, 0x00,
					mutation == RawPeMutation.MethodMemberReferenceTypeSpecObjectAsValueType ? (byte)0x11 : (byte)0x12],
				EncodeTypeDefOrRef(methodMemberTypeSpecification)),
			_ => [0x30, 0x01, 0x01, 0x01, 0x1e, 0x00],
		};
		MemberReferenceHandle genericMember = metadata.AddMemberReference(
			memberParent,
			metadata.GetOrAddString(
				methodMemberTypeSpecificationFixture ? "TypeSpecMethodTarget" : "GenericTarget"),
			metadata.GetOrAddBlob(genericMemberSignature));
		byte[] methodSpecificationSignature = mutation switch
		{
			RawPeMutation.MalformedMethodSpecificationHeader => [0x09, 0x01, 0x08],
			RawPeMutation.MethodSpecificationArityMismatch => [0x0a, 0x02, 0x08, 0x08],
			RawPeMutation.MethodSpecificationTrailingData => [0x0a, 0x01, 0x08, 0x00],
			RawPeMutation.UnauthorizedMethodSpecificationArgument => ConcatBytes(
				[0x0a, 0x01, 0x12],
				EncodeTypeDefOrRef(forbiddenType)),
			RawPeMutation.MethodSpecificationNonGenericParent or
				RawPeMutation.MethodSpecificationZeroArityGenericParent => [0x0a, 0x00],
			_ => [0x0a, 0x01, 0x08],
		};
		MethodSpecificationHandle methodSpecification = metadata.AddMethodSpecification(
			genericMember,
			metadata.GetOrAddBlob(methodSpecificationSignature));

		bool fieldMemberTypeSpecificationFixture = mutation is
			RawPeMutation.FieldMemberReferenceTypeSpecInt32 or
			RawPeMutation.FieldMemberReferenceTypeSpecInt32AsClass;
		TypeSpecificationHandle fieldMemberTypeSpecification = fieldMemberTypeSpecificationFixture
			? metadata.AddTypeSpecification(metadata.GetOrAddBlob(ConcatBytes(
				[0x12], EncodeTypeDefOrRef(systemType))))
			: default;
		byte[] fieldMemberSignature = mutation switch
		{
			RawPeMutation.MethodBitsOnFieldMemberReference => [0x26, 0x08],
			RawPeMutation.FieldMemberReferenceModifier => ConcatBytes([0x06], modifiedInt32),
			RawPeMutation.UnauthorizedFieldMemberReferenceType => ConcatBytes(
				[0x06, 0x12],
				EncodeTypeDefOrRef(forbiddenType)),
			RawPeMutation.FieldMemberReferenceIntAsClass => ConcatBytes(
				[0x06, 0x12],
				EncodeTypeDefOrRef(int32Type)),
			RawPeMutation.FieldMemberReferenceTypeSpecInt32 or
			RawPeMutation.FieldMemberReferenceTypeSpecInt32AsClass => ConcatBytes(
				[0x06,
					mutation == RawPeMutation.FieldMemberReferenceTypeSpecInt32AsClass ? (byte)0x12 : (byte)0x11],
				EncodeTypeDefOrRef(fieldMemberTypeSpecification)),
			_ => [0x06, 0x08],
		};
		MemberReferenceHandle fieldMember = metadata.AddMemberReference(
			objectType,
			metadata.GetOrAddString(
				fieldMemberTypeSpecificationFixture ? "TypeSpecFieldTarget" : "FieldTarget"),
			metadata.GetOrAddBlob(fieldMemberSignature));

		byte[] localSignature = mutation switch
		{
			RawPeMutation.LocalModifier => ConcatBytes([0x07, 0x01], modifiedInt32),
			RawPeMutation.LocalModifierInt64 => ConcatBytes([0x07, 0x01], modifiedInt64),
			_ => [0x07, 0x01, 0x08],
		};
		StandaloneSignatureHandle locals = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(localSignature));
		var code = new BlobBuilder();
		var instructions = new InstructionEncoder(code);
		instructions.OpCode(ILOpCode.Ldnull);
		instructions.Call(methodSpecification);
		instructions.OpCode(ILOpCode.Pop);
		instructions.OpCode(ILOpCode.Ldsfld);
		instructions.Token(fieldMember);
		instructions.OpCode(ILOpCode.Pop);
		instructions.OpCode(ILOpCode.Ret);
		int bodyOffset = methodBodies.AddMethodBody(
			instructions,
			maxStack: 2,
			localVariablesSignature: locals,
			attributes: MethodBodyAttributes.InitLocals);
		var getterCode = new BlobBuilder();
		var getterInstructions = new InstructionEncoder(getterCode);
		getterInstructions.OpCode(ILOpCode.Ldc_i4_0);
		getterInstructions.OpCode(ILOpCode.Ret);
		int getterBodyOffset = methodBodies.AddMethodBody(getterInstructions, maxStack: 1);
		MethodAttributes methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig;
		if (mutation is not RawPeMutation.InstanceMethodDefinition and not RawPeMutation.ExplicitThisMethodDefinition)
		{
			methodAttributes |= MethodAttributes.Static;
		}
		if (mutation == RawPeMutation.UnexpectedMethodAttributes)
		{
			methodAttributes |= MethodAttributes.SpecialName;
		}
		MethodImplAttributes methodImplementation = MethodImplAttributes.IL;
		if (mutation == RawPeMutation.SynchronizedMethod)
		{
			methodImplementation |= MethodImplAttributes.Synchronized;
		}
		ParameterHandle decoratedParameter = default;
		bool parameterFixture = mutation is RawPeMutation.ParameterizedMethodDefinition or
			RawPeMutation.WrongParameterName or RawPeMutation.OptionalParameter or
			RawPeMutation.DefaultParameter or RawPeMutation.MarshaledParameter;
		bool returnParameterFixture = mutation is RawPeMutation.ReturnParameterDefinition or
			RawPeMutation.WrongReturnParameterName or RawPeMutation.OptionalReturnParameter or
			RawPeMutation.DefaultReturnParameter or RawPeMutation.MarshaledReturnParameter;
		if (returnParameterFixture)
		{
			ParameterAttributes attributes = mutation switch
			{
				RawPeMutation.OptionalReturnParameter => ParameterAttributes.Optional,
				RawPeMutation.DefaultReturnParameter => ParameterAttributes.HasDefault,
				RawPeMutation.MarshaledReturnParameter => ParameterAttributes.HasFieldMarshal,
				_ => ParameterAttributes.None,
			};
			decoratedParameter = metadata.AddParameter(
				attributes,
				mutation == RawPeMutation.WrongReturnParameterName
					? metadata.GetOrAddString("result")
					: default,
				0);
			if (mutation == RawPeMutation.DefaultReturnParameter)
			{
				metadata.AddConstant(decoratedParameter, 7);
			}
			if (mutation == RawPeMutation.MarshaledReturnParameter)
			{
				metadata.AddMarshallingDescriptor(decoratedParameter, metadata.GetOrAddBlob(new byte[] { 0x07 }));
			}
		}
		else if (mutation == RawPeMutation.TypeCarryingReturnAttribute)
		{
			decoratedParameter = metadata.AddParameter(ParameterAttributes.None, default, 0);
		}
		else if (mutation == RawPeMutation.TypeCarryingParameterAttribute)
		{
			decoratedParameter = metadata.AddParameter(
				ParameterAttributes.None,
				metadata.GetOrAddString("value"),
				1);
		}
		else if (parameterFixture)
		{
			ParameterAttributes attributes = mutation switch
			{
				RawPeMutation.OptionalParameter => ParameterAttributes.Optional,
				RawPeMutation.DefaultParameter => ParameterAttributes.HasDefault,
				RawPeMutation.MarshaledParameter => ParameterAttributes.HasFieldMarshal,
				_ => ParameterAttributes.None,
			};
			decoratedParameter = metadata.AddParameter(
				attributes,
				metadata.GetOrAddString(mutation == RawPeMutation.WrongParameterName ? "other" : "value"),
				1);
			if (mutation == RawPeMutation.DefaultParameter)
			{
				metadata.AddConstant(decoratedParameter, 7);
			}
			if (mutation == RawPeMutation.MarshaledParameter)
			{
				metadata.AddMarshallingDescriptor(decoratedParameter, metadata.GetOrAddBlob(new byte[] { 0x07 }));
			}
		}
		bool explicitGenericFixture = mutation is RawPeMutation.MissingGenericBitMethodDefinition or
			RawPeMutation.TypeCarryingGenericParameterAttribute or
			RawPeMutation.TypeCarryingGenericConstraintAttribute or
			RawPeMutation.MethodGenericConstraintObject or
			RawPeMutation.MethodGenericConstraintTypeSpecObject or
			RawPeMutation.MethodGenericConstraintForbidden or
			RawPeMutation.MethodGenericConstraintModifier or
			RawPeMutation.MethodGenericConstraintUnresolved or
			RawPeMutation.MethodGenericConstraintCycle or
			RawPeMutation.MethodGenericConstraintTrailingData;
		MethodDefinitionHandle method = metadata.AddMethodDefinition(
			methodAttributes,
			methodImplementation,
			metadata.GetOrAddString(explicitGenericFixture ? "GenericMethod" : "Method"),
			metadata.GetOrAddBlob(methodSignature),
			bodyOffset,
			MetadataTokens.ParameterHandle(1));
		GenericParameterHandle decoratedGenericParameter = default;
		if (mutation is RawPeMutation.MissingGenericBitMethodDefinition or
			RawPeMutation.SelfConsistentGenericMethodDefinition or
			RawPeMutation.TypeCarryingGenericParameterAttribute or
			RawPeMutation.TypeCarryingGenericConstraintAttribute or
			RawPeMutation.MethodGenericConstraintObject or
			RawPeMutation.MethodGenericConstraintTypeSpecObject or
			RawPeMutation.MethodGenericConstraintForbidden or
			RawPeMutation.MethodGenericConstraintModifier or
			RawPeMutation.MethodGenericConstraintUnresolved or
			RawPeMutation.MethodGenericConstraintCycle or
			RawPeMutation.MethodGenericConstraintTrailingData)
		{
			decoratedGenericParameter = metadata.AddGenericParameter(
				method,
				GenericParameterAttributes.None,
				metadata.GetOrAddString("T"),
				0);
		}
		GenericParameterConstraintHandle decoratedGenericConstraint = default;
		if (mutation == RawPeMutation.TypeCarryingGenericConstraintAttribute)
		{
			decoratedGenericConstraint = metadata.AddGenericParameterConstraint(decoratedGenericParameter, objectType);
		}
		else if (mutation is RawPeMutation.MethodGenericConstraintObject or
			RawPeMutation.MethodGenericConstraintTypeSpecObject or
			RawPeMutation.MethodGenericConstraintForbidden or
			RawPeMutation.MethodGenericConstraintModifier or
			RawPeMutation.MethodGenericConstraintUnresolved or
			RawPeMutation.MethodGenericConstraintCycle or
			RawPeMutation.MethodGenericConstraintTrailingData)
		{
			EntityHandle constraintType = mutation switch
			{
				RawPeMutation.MethodGenericConstraintObject => objectType,
				RawPeMutation.MethodGenericConstraintTypeSpecObject => metadata.AddTypeSpecification(
					metadata.GetOrAddBlob(ConcatBytes(
						[0x12],
						EncodeTypeDefOrRef(objectType)))),
				RawPeMutation.MethodGenericConstraintForbidden => forbiddenType,
				RawPeMutation.MethodGenericConstraintModifier => metadata.AddTypeSpecification(
					metadata.GetOrAddBlob(ConcatBytes(
						[0x1f],
						EncodeTypeDefOrRef(forbiddenType),
						[0x12],
						EncodeTypeDefOrRef(objectType)))),
				RawPeMutation.MethodGenericConstraintUnresolved => MetadataTokens.TypeSpecificationHandle(63),
				RawPeMutation.MethodGenericConstraintCycle => metadata.AddTypeSpecification(
					metadata.GetOrAddBlob(ConcatBytes(
						[0x12],
						EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(1))))),
				RawPeMutation.MethodGenericConstraintTrailingData => metadata.AddTypeSpecification(
					metadata.GetOrAddBlob(ConcatBytes(
						[0x12],
						EncodeTypeDefOrRef(objectType),
						[0x00]))),
				_ => throw new InvalidOperationException(mutation.ToString()),
			};
			decoratedGenericConstraint = metadata.AddGenericParameterConstraint(decoratedGenericParameter, constraintType);
		}
		MethodDefinitionHandle getter = metadata.AddMethodDefinition(
			MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
			MethodImplAttributes.IL,
			metadata.GetOrAddString("get_Property"),
			metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x08 }),
			getterBodyOffset,
			MetadataTokens.ParameterHandle(decoratedParameter.IsNil ? 1 : 2));
		MethodDefinitionHandle constructor = default;
		if (mutation == RawPeMutation.TypeCarryingConstructorAttribute)
		{
			constructor = metadata.AddMethodDefinition(
				MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
				MethodImplAttributes.IL,
				metadata.GetOrAddString(".ctor"),
				metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x01 }),
				bodyOffset,
				MetadataTokens.ParameterHandle(decoratedParameter.IsNil ? 1 : 2));
		}

		byte[] propertySignature = mutation == RawPeMutation.MalformedPropertyHeader
			? [0x20, 0x00, 0x08]
			: mutation == RawPeMutation.GenericPropertyHeader
				? [0x38, 0x00, 0x08]
				: mutation == RawPeMutation.ReservedPropertyHeader
					? [0xa8, 0x00, 0x08]
			: mutation == RawPeMutation.PropertyModifier
				? ConcatBytes([0x28, 0x00], modifiedInt32)
				: [0x28, 0x00, 0x08];
		PropertyDefinitionHandle property = metadata.AddProperty(
			mutation == RawPeMutation.UnexpectedPropertyAttributes
				? PropertyAttributes.SpecialName
				: PropertyAttributes.None,
			metadata.GetOrAddString("Property"),
			metadata.GetOrAddBlob(propertySignature));
		EventDefinitionHandle subjectEvent = default;
		if (mutation == RawPeMutation.TypeCarryingEventAttribute)
		{
			subjectEvent = metadata.AddEvent(
				EventAttributes.None,
				metadata.GetOrAddString("Event"),
				objectType);
		}

		bool typeDefinitionScopeFixture = mutation is
			RawPeMutation.NestedTypeDefinitionScope or RawPeMutation.TypeDefinitionScopeCycle or
			RawPeMutation.TypeDefinitionScopeUnresolved or RawPeMutation.TypeDefinitionUnexpectedScope;
		TypeAttributes targetVisibility = mutation is
			RawPeMutation.NestedTypeDefinitionScope or RawPeMutation.TypeDefinitionScopeCycle or
			RawPeMutation.TypeDefinitionScopeUnresolved
				? TypeAttributes.NestedPublic
				: TypeAttributes.Public;
		TypeAttributes targetTypeAttributes = targetVisibility | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
		if (mutation == RawPeMutation.ClassLayout)
		{
			targetTypeAttributes |= TypeAttributes.SequentialLayout;
		}
		TypeDefinitionHandle targetType = metadata.AddTypeDefinition(
			targetTypeAttributes,
			typeDefinitionScopeFixture ? default : metadata.GetOrAddString("RawFixture"),
			metadata.GetOrAddString("ObservationBatchFixture"),
			baseType,
			field,
			method);
		if (typeDefinitionScopeFixture)
		{
			TypeDefinitionHandle declaringType = mutation == RawPeMutation.TypeDefinitionScopeUnresolved
				? MetadataTokens.TypeDefinitionHandle(63)
				: metadata.AddTypeDefinition(
					mutation == RawPeMutation.TypeDefinitionScopeCycle
						? TypeAttributes.NestedPublic
						: TypeAttributes.Public,
					mutation == RawPeMutation.TypeDefinitionScopeCycle
						? default
						: metadata.GetOrAddString("RawFixture"),
					metadata.GetOrAddString("OuterFixture"),
					objectType,
					MetadataTokens.FieldDefinitionHandle(2),
					MetadataTokens.MethodDefinitionHandle(3));
			metadata.AddNestedType(targetType, declaringType);
			if (mutation == RawPeMutation.TypeDefinitionScopeCycle)
			{
				metadata.AddNestedType(declaringType, targetType);
			}
		}
		if (mutation == RawPeMutation.ClassLayout)
		{
			metadata.AddTypeLayout(targetType, packingSize: 4, size: 8);
		}
		metadata.AddPropertyMap(targetType, property);
		if (mutation != RawPeMutation.MissingPropertyGetter)
		{
			metadata.AddMethodSemantics(
				property,
				mutation switch
				{
					RawPeMutation.SetterPropertySemantics => MethodSemanticsAttributes.Setter,
					RawPeMutation.OtherPropertySemantics => MethodSemanticsAttributes.Other,
					_ => MethodSemanticsAttributes.Getter,
				},
				mutation == RawPeMutation.WrongPropertyGetter ? method : getter);
		}
		if (!subjectEvent.IsNil)
		{
			metadata.AddEventMap(targetType, subjectEvent);
		}

		byte[] interfaceSignature = ConcatBytes(
			[0x15, 0x12],
			EncodeTypeDefOrRef(iEquatableType),
			[0x01, 0x12],
			EncodeTypeDefOrRef(targetType));
		if (mutation == RawPeMutation.InterfaceTypeSpecModifier)
		{
			interfaceSignature = ConcatBytes([0x1f], EncodeTypeDefOrRef(forbiddenType), interfaceSignature);
		}
		TypeSpecificationHandle interfaceType = metadata.AddTypeSpecification(metadata.GetOrAddBlob(interfaceSignature));
		InterfaceImplementationHandle interfaceImplementation = metadata.AddInterfaceImplementation(targetType, interfaceType);
		MemberReferenceHandle nullableConstructor = metadata.AddMemberReference(
			nullableAttributeType,
			metadata.GetOrAddString(".ctor"),
			metadata.GetOrAddBlob(new byte[] { 0x20, 0x01, 0x01, 0x1d, 0x05 }));
		MemberReferenceHandle typeCarrierConstructor = metadata.AddMemberReference(
			typeCarrierAttributeType,
			metadata.GetOrAddString(".ctor"),
			metadata.GetOrAddBlob(ConcatBytes(
				[0x20, 0x01, 0x01, 0x12],
				EncodeTypeDefOrRef(systemType))));
		MemberReferenceHandle typeCarrierArrayConstructor = metadata.AddMemberReference(
			typeCarrierAttributeType,
			metadata.GetOrAddString(".ctor"),
			metadata.GetOrAddBlob(ConcatBytes(
				[0x20, 0x01, 0x01, 0x1d, 0x12],
				EncodeTypeDefOrRef(systemType))));
		MemberReferenceHandle typeCarrierByteArrayConstructor = metadata.AddMemberReference(
			typeCarrierAttributeType,
			metadata.GetOrAddString(".ctor"),
			metadata.GetOrAddBlob(new byte[] { 0x20, 0x01, 0x01, 0x1d, 0x05 }));
		EntityHandle interfaceAttributeConstructor = mutation switch
		{
			RawPeMutation.WrongInterfaceAttributeConstructor => typeCarrierByteArrayConstructor,
			RawPeMutation.TypeCarryingInterfaceAttribute or
				RawPeMutation.TypeCarryingInterfaceNamedAttribute => typeCarrierConstructor,
			RawPeMutation.TypeCarryingInterfaceArrayAttribute => typeCarrierArrayConstructor,
			_ => nullableConstructor,
		};
		byte[] forbiddenTypeAttributeValue = ConcatBytes(
			[0x01, 0x00],
			EncodeSerializedString(exactSerializedForbidden),
			[0x00, 0x00]);
		byte[] fixedTypeAttributeValue = mutation switch
		{
			RawPeMutation.TypeCarryingMethodExactObservationAttribute => ConcatBytes(
				[0x01, 0x00], EncodeSerializedString(exactSerializedObservation), [0x00, 0x00]),
			RawPeMutation.TypeCarryingMethodUnqualifiedObservationAttribute => ConcatBytes(
				[0x01, 0x00],
				EncodeSerializedString(typeof(LiquidWalletTransactionObservation).FullName!), [0x00, 0x00]),
			RawPeMutation.TypeCarryingMethodCounterfeitObservationAttribute => ConcatBytes(
				[0x01, 0x00], EncodeSerializedString(counterfeitSerializedObservation), [0x00, 0x00]),
			_ => forbiddenTypeAttributeValue,
		};
		byte[] interfaceAttributeValue = mutation switch
		{
			RawPeMutation.WrongInterfaceNullableArgument => Convert.FromHexString("01000200000000010100"),
			RawPeMutation.TypeCarryingInterfaceAttribute => ConcatBytes(
				[0x01, 0x00], EncodeSerializedString(exactSerializedForbidden), [0x00, 0x00]),
			RawPeMutation.TypeCarryingInterfaceNamedAttribute => ConcatBytes(
				[0x01, 0x00],
				EncodeSerializedString(exactSerializedObservation),
				[0x01, 0x00, 0x54, 0x50],
				EncodeSerializedString(nameof(TypeCarrierAttribute.Target)),
				EncodeSerializedString(exactSerializedForbidden)),
			RawPeMutation.TypeCarryingInterfaceArrayAttribute => ConcatBytes(
				[0x01, 0x00, 0x01, 0x00, 0x00, 0x00],
				EncodeSerializedString(exactSerializedForbidden),
				[0x00, 0x00]),
			_ => Convert.FromHexString("01000200000000010000"),
		};
		metadata.AddCustomAttribute(
			interfaceImplementation,
			interfaceAttributeConstructor,
			metadata.GetOrAddBlob(interfaceAttributeValue));
		EntityHandle fixedTypeAttributeParent = mutation switch
		{
			RawPeMutation.TypeCarryingTypeAttribute => targetType,
			RawPeMutation.TypeCarryingFieldAttribute => field,
			RawPeMutation.TypeCarryingConstructorAttribute => constructor,
			RawPeMutation.TypeCarryingReturnAttribute => decoratedParameter,
			RawPeMutation.TypeCarryingParameterAttribute => decoratedParameter,
			RawPeMutation.TypeCarryingEventAttribute => subjectEvent,
			RawPeMutation.TypeCarryingGenericParameterAttribute => decoratedGenericParameter,
			RawPeMutation.TypeCarryingGenericConstraintAttribute => decoratedGenericConstraint,
			RawPeMutation.TypeCarryingStandaloneSignatureAttribute => locals,
			RawPeMutation.TypeCarryingMemberReferenceAttribute => genericMember,
			RawPeMutation.TypeCarryingMethodSpecificationAttribute => methodSpecification,
			RawPeMutation.TypeCarryingTypeSpecificationAttribute => interfaceType,
			RawPeMutation.MethodTypeSpecNestedCycleAttribute => nestedAttributedTypeSpecification,
			RawPeMutation.TypeCarryingMethodExactObservationAttribute or
			RawPeMutation.TypeCarryingMethodUnqualifiedObservationAttribute or
			RawPeMutation.TypeCarryingMethodCounterfeitObservationAttribute => method,
			RawPeMutation.TypeCarryingTypeReferenceAttribute => objectType,
			RawPeMutation.TypeCarryingAssemblyReferenceAttribute or
			RawPeMutation.TypeCarryingModuleReferenceAttribute or
			RawPeMutation.TypeCarryingModuleDefinitionAttribute => scopedObjectEndpoint,
			_ => default,
		};
		if (!fixedTypeAttributeParent.IsNil)
		{
			metadata.AddCustomAttribute(
				fixedTypeAttributeParent,
				typeCarrierConstructor,
				metadata.GetOrAddBlob(fixedTypeAttributeValue));
		}
		if (mutation is RawPeMutation.TypeCarryingMethodArrayAttribute or
			RawPeMutation.TypeCarryingMethodArrayWrongTokenObservationAttribute)
		{
			metadata.AddCustomAttribute(
				method,
				typeCarrierArrayConstructor,
				metadata.GetOrAddBlob(ConcatBytes(
					[0x01, 0x00, 0x01, 0x00, 0x00, 0x00],
					EncodeSerializedString(
						mutation == RawPeMutation.TypeCarryingMethodArrayWrongTokenObservationAttribute
							? wrongTokenSerializedObservation
							: exactSerializedForbidden),
					[0x00, 0x00])));
		}
		if (mutation is RawPeMutation.TypeCarryingPropertyNamedAttribute or
			RawPeMutation.TypeCarryingPropertyNamedWrongVersionObservationAttribute)
		{
			metadata.AddCustomAttribute(
				property,
				typeCarrierConstructor,
				metadata.GetOrAddBlob(ConcatBytes(
					[0x01, 0x00],
					EncodeSerializedString(exactSerializedObservation),
					[0x01, 0x00, 0x54, 0x50],
					EncodeSerializedString(nameof(TypeCarrierAttribute.Target)),
					EncodeSerializedString(
						mutation == RawPeMutation.TypeCarryingPropertyNamedWrongVersionObservationAttribute
							? wrongVersionSerializedObservation
							: exactSerializedForbidden))));
		}
		if (mutation == RawPeMutation.MethodImplementation)
		{
			metadata.AddMethodImplementation(targetType, method, method);
		}

		var peBuilder = new ManagedPEBuilder(
			new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
			new MetadataRootBuilder(metadata),
			ilStream,
			flags: CorFlags.ILOnly);
		var peBlob = new BlobBuilder();
		peBuilder.Serialize(peBlob);
		return peBlob.ToArray();
	}

	private static string[] RawPeSubjectMemberShape(byte[] peBytes)
	{
		using var stream = new MemoryStream(peBytes, writable: false);
		using var peReader = new PEReader(stream);
		MetadataReader reader = peReader.GetMetadataReader();
		TypeDefinition definition = reader.GetTypeDefinition(FindType(reader, "RawFixture", "ObservationBatchFixture"));
		return
		[
			.. definition.GetFields().Select(handle =>
			{
				FieldDefinition field = reader.GetFieldDefinition(handle);
				return $"field|{reader.GetString(field.Name)}|{field.Attributes}|{Convert.ToHexString(reader.GetBlobBytes(field.Signature))}";
			}),
			.. definition.GetMethods().Select(handle =>
			{
				MethodDefinition method = reader.GetMethodDefinition(handle);
				return $"method|{reader.GetString(method.Name)}|{method.Attributes}|{method.ImplAttributes}|" +
					$"{Convert.ToHexString(reader.GetBlobBytes(method.Signature))}|" +
					$"params:{method.GetParameters().Count}|generics:{method.GetGenericParameters().Count}";
			}),
			.. definition.GetProperties().Select(handle =>
			{
				PropertyDefinition property = reader.GetPropertyDefinition(handle);
				return $"property|{reader.GetString(property.Name)}|{property.Attributes}|" +
					Convert.ToHexString(reader.GetBlobBytes(property.Signature));
			}),
			.. definition.GetEvents().Select(handle =>
			{
				EventDefinition subjectEvent = reader.GetEventDefinition(handle);
				return $"event|{reader.GetString(subjectEvent.Name)}|{subjectEvent.Attributes}|{MetadataTokens.GetToken(subjectEvent.Type)}";
			}),
			$"interfaces:{definition.GetInterfaceImplementations().Count}",
			$"method-impls:{definition.GetMethodImplementations().Count}",
			$"generic-params:{definition.GetGenericParameters().Count}",
		];
	}

	private static byte[] EncodeTypeDefOrRef(EntityHandle handle)
	{
		uint tag = handle.Kind switch
		{
			HandleKind.TypeDefinition => 0u,
			HandleKind.TypeReference => 1u,
			HandleKind.TypeSpecification => 2u,
			_ => throw new InvalidOperationException($"{handle.Kind} is not a TypeDefOrRefOrSpec handle."),
		};
		uint value = checked(((uint)MetadataTokens.GetRowNumber(handle) << 2) | tag);
		if (value <= 0x7f)
		{
			return [(byte)value];
		}
		if (value <= 0x3fff)
		{
			return [(byte)((value >> 8) | 0x80), (byte)value];
		}
		return
		[
			(byte)((value >> 24) | 0xc0),
			(byte)(value >> 16),
			(byte)(value >> 8),
			(byte)value,
		];
	}

	private static byte[] ConcatBytes(params byte[][] values)
	{
		int length = values.Sum(value => value.Length);
		var result = new byte[length];
		int offset = 0;
		foreach (byte[] value in values)
		{
			value.CopyTo(result, offset);
			offset += value.Length;
		}
		return result;
	}

	private static byte[] EncodeSerializedString(string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		Assert.InRange(bytes.Length, 0, 0x3fff);
		return bytes.Length <= 0x7f
			? [(byte)bytes.Length, .. bytes]
			: [(byte)(0x80 | bytes.Length >> 8), (byte)bytes.Length, .. bytes];
	}

	private static object? ReadRawConstant(MetadataReader reader, ConstantHandle handle)
	{
		Constant constant = reader.GetConstant(handle);
		BlobReader value = reader.GetBlobReader(constant.Value);
		return constant.TypeCode switch
		{
			ConstantTypeCode.Int32 => value.ReadInt32(),
			ConstantTypeCode.UInt32 => value.ReadUInt32(),
			ConstantTypeCode.Int64 => value.ReadInt64(),
			ConstantTypeCode.UInt64 => value.ReadUInt64(),
			ConstantTypeCode.String => value.ReadUTF16(value.RemainingBytes),
			ConstantTypeCode.NullReference => null,
			_ => throw new InvalidOperationException($"Unsupported raw constant {constant.TypeCode}."),
		};
	}

	private static IReadOnlyList<string> VerifyRawPeMetadata(
		byte[] peBytes,
		string @namespace,
		string typeName,
		RawPeMutation fixtureMutation = RawPeMutation.None)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		using var stream = new MemoryStream(peBytes, writable: false);
		using var peReader = new PEReader(stream);
		MetadataReader reader;
		try
		{
			reader = peReader.GetMetadataReader();
		}
		catch (BadImageFormatException)
		{
			return ["MALFORMED_METADATA"];
		}
		TypeDefinitionHandle typeHandle;
		try
		{
			typeHandle = FindType(reader, @namespace, typeName);
		}
		catch (InvalidOperationException)
		{
			return ["TYPE_ROOT"];
		}
		TypeDefinition definition = reader.GetTypeDefinition(typeHandle);
		var provider = new ModifierRejectingTypeProvider(reader);
		string? decodedSubject = TryDecodeRawType(provider, typeHandle, violations);
		bool production = @namespace == typeof(LiquidWalletObservationBatch).Namespace &&
			typeName == typeof(LiquidWalletObservationBatch).Name;
		RawPePolicyMode policyMode = production ? RawPePolicyMode.Production : RawPePolicyMode.Fixture;
		bool fixtureTypeCarrierInterfaceAttribute = !production && fixtureMutation is
			RawPeMutation.TypeCarryingInterfaceAttribute or
			RawPeMutation.TypeCarryingInterfaceNamedAttribute or
			RawPeMutation.TypeCarryingInterfaceArrayAttribute;
		Dictionary<EntityHandle, MemberInfo> productionMembers = production
			? ExpectedReachableMembers().ToDictionary(pair => pair.Key, pair => pair.Value)
			: [];
		if (production)
		{
			AddExpectedProductionAttributeConstructors(reader, definition, productionMembers);
		}
		IReadOnlyDictionary<MethodDefinitionHandle, RawMethodDefinitionExpectation> methodExpectations = production
			? ProductionRawMethodDefinitionExpectations()
			: RawPeFixtureMethodExpectations(fixtureMutation);
		var reachable = new HashSet<EntityHandle>();
		var localSignatureOwners = new HashSet<EntityHandle>();

		string expectedSelf = production
			? RawEntityTypeKey(typeof(LiquidWalletObservationBatch))
			: decodedSubject ?? $"{@namespace}.{typeName}";
		string expectedInterface = RawIdentityNode(
			"generic-instantiation",
			RawTypeKindKey(RawNamedTypeKey(typeof(IEquatable<>)), 0x12),
			RawTypeKindKey(expectedSelf, 0x12));
		string? baseType = TryDecodeRawType(provider, definition.BaseType, violations);
		bool baseTypeContainsModifier = provider.Modifiers.Count != 0;
		string expectedBaseType = RawEntityTypeKey(typeof(object));
		bool isModifiedExpectedBaseType = baseTypeContainsModifier &&
			baseType == RawTypeKindKey(expectedBaseType, 0x12);
		if (baseType is not null && baseType != expectedBaseType && !isModifiedExpectedBaseType)
		{
			violations.Add("EXTENDS_ROOT");
		}
		TypeLayout layout = definition.GetLayout();
		if ((definition.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.AutoLayout ||
			layout.PackingSize != 0 || layout.Size != 0)
		{
			violations.Add("CLASS_LAYOUT");
		}

		InterfaceImplementationHandle[] interfaces = definition.GetInterfaceImplementations().ToArray();
		if (interfaces.Length != 1)
		{
			violations.Add("INTERFACE_COUNT");
		}
		else
		{
			InterfaceImplementation implementation = reader.GetInterfaceImplementation(interfaces[0]);
			string? interfaceType = TryDecodeRawType(provider, implementation.Interface, violations);
			if (interfaceType is not null && interfaceType != expectedInterface)
			{
				violations.Add("INTERFACE_TYPE");
			}
			CustomAttributeHandle[] attributes = implementation.GetCustomAttributes().ToArray();
			if (attributes.Length != 1)
			{
				violations.Add("INTERFACE_ATTRIBUTE_COUNT");
			}
			else
			{
				CustomAttribute attribute = reader.GetCustomAttribute(attributes[0]);
				if (!fixtureTypeCarrierInterfaceAttribute)
				{
					try
					{
						if (RawAttributeConstructorKey(reader, attribute.Constructor) != RawNullableInterfaceAttribute.ConstructorKey)
						{
							violations.Add("INTERFACE_ATTRIBUTE_CONSTRUCTOR");
						}
					}
					catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException)
					{
						violations.Add("INTERFACE_ATTRIBUTE_CONSTRUCTOR");
					}
					if (!reader.GetBlobBytes(attribute.Value).AsSpan().SequenceEqual(RawNullableInterfaceAttribute.Blob))
					{
						violations.Add("INTERFACE_NULLABILITY");
					}
					if (attribute.Constructor.Kind == HandleKind.MemberReference)
					{
						ValidateRawMemberReference(
							reader,
							provider,
							(MemberReferenceHandle)attribute.Constructor,
							[
								RawMemberExpectation.ForMethod(
									RawSystemRuntimeTypeKey("System.Runtime.CompilerServices.NullableAttribute"),
									".ctor",
									RawTypeKey(typeof(void)),
									[RawTypeKey(typeof(byte[]))],
									true,
									0),
							],
							"INTERFACE_ATTRIBUTE_CONSTRUCTOR",
							violations);
					}
					else
					{
						violations.Add("INTERFACE_ATTRIBUTE_CONSTRUCTOR");
					}
				}
				ValidateRawCustomAttribute(reader, provider, attributes[0], productionMembers, policyMode, violations);
			}
		}

		foreach (CustomAttributeHandle attributeHandle in SubjectOwnedCustomAttributes(reader, definition))
		{
			ValidateRawCustomAttribute(reader, provider, attributeHandle, productionMembers, policyMode, violations);
		}
		if (production)
		{
			try
			{
				if (!ExactMultisetMatches(
					RawAttributeLocationManifest(reader, definition, provider),
					ExpectedRawAttributeLocationManifest()))
				{
					violations.Add("CUSTOM_ATTRIBUTE");
				}
			}
			catch (Exception exception) when (IsRawMetadataException(exception))
			{
				RecordRawMetadataException(exception, violations);
				violations.Add("CUSTOM_ATTRIBUTE");
			}
		}
		else if (SubjectOwnedCustomAttributes(reader, definition).Any() ||
			interfaces.SelectMany(handle => reader.GetInterfaceImplementation(handle).GetCustomAttributes()).Count() != 1)
		{
			violations.Add("CUSTOM_ATTRIBUTE");
		}

		foreach (GenericParameterHandle parameterHandle in definition.GetGenericParameters())
		{
			GenericParameter parameter = reader.GetGenericParameter(parameterHandle);
			foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
			{
				TryDecodeRawType(provider, reader.GetGenericParameterConstraint(constraintHandle).Type, violations);
			}
		}
		foreach (EventDefinitionHandle eventHandle in definition.GetEvents())
		{
			TryDecodeRawType(provider, reader.GetEventDefinition(eventHandle).Type, violations);
		}

		foreach (FieldDefinitionHandle handle in definition.GetFields())
		{
			FieldDefinition field = reader.GetFieldDefinition(handle);
			FieldInfo? expectedField = production
				? typeof(LiquidWalletObservationBatch).GetField(reader.GetString(field.Name), DeclaredMemberFlags)
				: null;
			bool expectedLiteral = !production && fixtureMutation is
				(RawPeMutation.LiteralFieldDefinition or RawPeMutation.MutatedLiteralField);
			FieldAttributes expectedAttributes = production
				? expectedField!.Attributes
				: expectedLiteral
					? FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault
					: FieldAttributes.Private;
			FieldAttributes semanticMask = ~(FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal);
			if ((field.Attributes & semanticMask) != (expectedAttributes & semanticMask))
			{
				violations.Add("FIELD_FLAGS");
			}
			ConstantHandle constant = field.GetDefaultValue();
			bool expectedConstant = production ? expectedField!.IsLiteral : expectedLiteral;
			object? expectedConstantValue = production && expectedConstant
				? expectedField!.GetRawConstantValue()
				: expectedLiteral ? 1 : null;
			if (((field.Attributes & FieldAttributes.HasDefault) != 0) != expectedConstant ||
				constant.IsNil != !expectedConstant ||
				!constant.IsNil && !Equals(ReadRawConstant(reader, constant), expectedConstantValue))
			{
				violations.Add("LITERAL_VALUE");
			}
			BlobHandle marshal = field.GetMarshallingDescriptor();
			if ((field.Attributes & FieldAttributes.HasFieldMarshal) != 0 || !marshal.IsNil)
			{
				violations.Add("FIELD_MARSHAL");
			}
			byte[] bytes = reader.GetBlobBytes(field.Signature);
			AddViolations(violations, VerifyRawSignature(new RawSignaturePolicy(RawSignatureKind.Field, false, 0, 0), bytes));
			try
			{
				BlobReader blob = reader.GetBlobReader(field.Signature);
				string decoded = new SignatureDecoder<string, object?>(provider, reader, null).DecodeFieldSignature(ref blob);
				if (blob.RemainingBytes != 0) { violations.Add("TRAILING_DATA"); }
				string expectedType = production
					? RawTypeKey(expectedField!.FieldType)
					: fixtureMutation is RawPeMutation.FieldTypeSpecInt32 or RawPeMutation.FieldTypeSpecInt32AsClass
						? RawTypeSpecificationKindKey(RawTypeKey(typeof(Type)), 0x11)
					: fixtureMutation is RawPeMutation.FieldSzArray or
						RawPeMutation.FieldSzArrayAsMdRankOne or
						RawPeMutation.FieldSzArrayPrimitiveInt32AsTypeReference
						? RawTypeKey(typeof(int[]))
					: fixtureMutation is RawPeMutation.FieldMdArrayRankOne or
						RawPeMutation.FieldMdArrayExplicitSize or RawPeMutation.FieldMdArrayLowerBound or
						RawPeMutation.FieldMdArrayPrimitiveInt32AsTypeReference
						? RawTypeKey(typeof(int).MakeArrayType(1))
					: fixtureMutation == RawPeMutation.FieldLocalBatchTypeDefinitionAlias
						? RawTypeKey(typeof(LiquidWalletObservationBatch))
						: RawTypeKey(typeof(int));
				if (decoded != expectedType)
				{
					violations.Add("UNAPPROVED_TYPE");
				}
			}
			catch (Exception exception) when (IsRawMetadataException(exception))
			{
				RecordRawMetadataException(exception, violations);
			}
		}

		foreach (MethodDefinitionHandle handle in definition.GetMethods())
		{
			MethodDefinition method = reader.GetMethodDefinition(handle);
			RawMethodDefinitionExpectation? methodExpectation;
			if (!methodExpectations.TryGetValue(handle, out methodExpectation))
			{
				violations.Add("UNMAPPED_REACHABLE_HANDLE");
			}
			else
			{
				ValidateRawMethodDefinition(reader, provider, handle, methodExpectation, violations);
			}
			ValidateRawMethodGenericConstraints(
				reader,
				provider,
				method,
				violations);

			if (method.RelativeVirtualAddress == 0)
			{
				continue;
			}
			MethodBodyBlock body;
			try
			{
				body = peReader.GetMethodBody(method.RelativeVirtualAddress);
			}
			catch (BadImageFormatException)
			{
				violations.Add("MALFORMED_METHOD_BODY");
				continue;
			}
			foreach (ExceptionRegion region in body.ExceptionRegions)
			{
				if (region.Kind == ExceptionRegionKind.Catch)
				{
					TryDecodeRawType(provider, region.CatchType, violations);
				}
			}
			string[] expectedLocalTypes = ExpectedRawLocalTypes(production, handle, methodExpectation);
			if (body.LocalSignature.IsNil)
			{
				if (expectedLocalTypes.Length != 0)
				{
					violations.Add("LOCAL_TYPE");
				}
			}
			else
			{
				localSignatureOwners.Add(body.LocalSignature);
				StandaloneSignature local = reader.GetStandaloneSignature(body.LocalSignature);
				byte[] localBytes = reader.GetBlobBytes(local.Signature);
				try
				{
					BlobReader localBlob = reader.GetBlobReader(local.Signature);
					ImmutableArray<string> localTypes = new SignatureDecoder<string, object?>(provider, reader, null)
						.DecodeLocalSignature(ref localBlob);
					AddViolations(
						violations,
						VerifyRawSignature(
							new RawSignaturePolicy(RawSignatureKind.Local, false, 0, localTypes.Length),
							localBytes));
					if (localBlob.RemainingBytes != 0) { violations.Add("TRAILING_DATA"); }
					if (!localTypes.SequenceEqual(expectedLocalTypes, StringComparer.Ordinal))
					{
						violations.Add("LOCAL_TYPE");
					}
				}
				catch (Exception exception) when (IsRawMetadataException(exception))
				{
					RecordRawMetadataException(exception, violations);
					AddViolations(
						violations,
						VerifyRawSignature(new RawSignaturePolicy(RawSignatureKind.Local, false, 0, 1), localBytes));
				}
			}
			try
			{
				CollectReferencedHandles(body.GetILBytes() ?? [], reachable);
			}
			catch (Exception exception) when (IsRawMetadataException(exception))
			{
				RecordRawMetadataException(exception, violations);
			}
		}

		foreach (PropertyDefinitionHandle handle in definition.GetProperties())
		{
			PropertyDefinition property = reader.GetPropertyDefinition(handle);
			PropertyInfo? expectedProperty = production
				? typeof(LiquidWalletObservationBatch).GetProperty(reader.GetString(property.Name), DeclaredMemberFlags)
				: typeof(RawPropertyExpectationFixture).GetProperty(
					nameof(RawPropertyExpectationFixture.Property),
					BindingFlags.Public | BindingFlags.Instance);
			PropertyAccessors accessors = property.GetAccessors();
			MethodDefinitionHandle expectedGetter = production
				? (MethodDefinitionHandle)MetadataTokens.EntityHandle(expectedProperty!.GetMethod!.MetadataToken)
				: MetadataTokens.MethodDefinitionHandle(2);
			if (property.Attributes != expectedProperty!.Attributes ||
				accessors.Getter != expectedGetter || !accessors.Setter.IsNil || !accessors.Others.IsEmpty ||
				!property.GetDefaultValue().IsNil)
			{
				violations.Add("PROPERTY_METADATA");
			}
			byte[] bytes = reader.GetBlobBytes(property.Signature);
			AddViolations(
				violations,
				VerifyRawSignature(new RawSignaturePolicy(RawSignatureKind.Property, true, 0, 0), bytes));
			try
			{
				BlobReader blob = reader.GetBlobReader(property.Signature);
				MethodSignature<string> decoded = new SignatureDecoder<string, object?>(provider, reader, null)
					.DecodeMethodSignature(ref blob);
				if (blob.RemainingBytes != 0) { violations.Add("TRAILING_DATA"); }
				string expectedType = RawTypeKey(expectedProperty.PropertyType);
				if (decoded.ReturnType != expectedType || !decoded.ParameterTypes.IsEmpty)
				{
					violations.Add("UNAPPROVED_TYPE");
				}
			}
			catch (Exception exception) when (IsRawMetadataException(exception))
			{
				RecordRawMetadataException(exception, violations);
			}
		}

		foreach (EntityHandle handle in reachable)
		{
			productionMembers.TryGetValue(handle, out MemberInfo? expected);
			ValidateRawPeHandle(reader, provider, handle, expected, productionMembers, policyMode, violations);
		}
		var attributeOwners = new HashSet<EntityHandle>(reachable);
		attributeOwners.Remove(typeHandle);
		attributeOwners.UnionWith(localSignatureOwners);
		attributeOwners.UnionWith(provider.EntityHandles.Where(handle =>
			handle.Kind is HandleKind.TypeReference or HandleKind.TypeSpecification ||
			handle.Kind == HandleKind.TypeDefinition && handle != typeHandle ||
			handle.Kind is HandleKind.AssemblyReference or HandleKind.ModuleDefinition or HandleKind.ModuleReference));
		IEnumerable<CustomAttributeHandle> rootedAttributes = SubjectOwnedCustomAttributes(reader, definition)
			.Concat(interfaces.SelectMany(handle => reader.GetInterfaceImplementation(handle).GetCustomAttributes()));
		foreach (CustomAttributeHandle attribute in rootedAttributes)
		{
			EntityHandle constructor = reader.GetCustomAttribute(attribute).Constructor;
			if (constructor.Kind == HandleKind.MemberReference)
			{
				attributeOwners.Add(constructor);
			}
		}
		bool addedAttributeConstructor;
		do
		{
			ExpandRawReachableAttributeOwners(reader, typeHandle, attributeOwners, violations);
			addedAttributeConstructor = false;
			foreach (EntityHandle owner in attributeOwners.Where(IsReachableCustomAttributeOwner).ToArray())
			{
				foreach (CustomAttributeHandle attribute in ReachableCustomAttributes(reader, owner))
				{
					EntityHandle constructor = reader.GetCustomAttribute(attribute).Constructor;
					if (constructor.Kind == HandleKind.MemberReference)
					{
						addedAttributeConstructor |= attributeOwners.Add(constructor);
					}
				}
			}
		}
		while (addedAttributeConstructor);
		foreach (EntityHandle owner in attributeOwners.Where(IsReachableCustomAttributeOwner))
		{
			if (production && owner.Kind == HandleKind.TypeDefinition &&
				productionMembers.TryGetValue(owner, out MemberInfo? expectedType) && expectedType is Type)
			{
				continue;
			}
			CustomAttributeHandle[] attributes = ReachableCustomAttributes(reader, owner).ToArray();
			if (attributes.Length == 0)
			{
				continue;
			}
			violations.Add("CUSTOM_ATTRIBUTE");
			foreach (CustomAttributeHandle attribute in attributes)
			{
				ValidateRawCustomAttribute(reader, provider, attribute, productionMembers, policyMode, violations);
			}
		}
		foreach (MethodImplementationHandle handle in definition.GetMethodImplementations())
		{
			violations.Add("METHOD_IMPL_ROW");
			MethodImplementation implementation = reader.GetMethodImplementation(handle);
			ValidateRawPeHandle(reader, provider, implementation.MethodDeclaration, null, productionMembers, policyMode, violations);
			ValidateRawPeHandle(reader, provider, implementation.MethodBody, null, productionMembers, policyMode, violations);
		}
		if (provider.Modifiers.Count != 0)
		{
			violations.Add("CUSTOM_MODIFIER");
		}
		AddViolations(violations, provider.RawTypeSpecificationViolations);
		AddViolations(violations, provider.RawTypeScopeViolations);
		if (provider.Types.Any(ContainsForbiddenRawTypeShape))
		{
			violations.Add("FORBIDDEN_TYPE_SHAPE");
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static string[] ExpectedRawLocalTypes(
		bool production,
		MethodDefinitionHandle handle,
		RawMethodDefinitionExpectation? expectation)
	{
		if (!production)
		{
			return MetadataTokens.GetRowNumber(handle) == 1 ? [RawTypeKey(typeof(int))] : [];
		}
		return expectation?.Callable.GetMethodBody()?.LocalVariables
			.OrderBy(local => local.LocalIndex)
			.Select(local => local.IsPinned
				? RawIdentityNode("pinned", RawTypeKey(local.LocalType))
				: RawTypeKey(local.LocalType))
			.ToArray() ?? [];
	}

	private static IReadOnlyDictionary<MethodDefinitionHandle, RawMethodDefinitionExpectation> ProductionRawMethodDefinitionExpectations()
	{
		var result = new Dictionary<MethodDefinitionHandle, RawMethodDefinitionExpectation>();
		foreach (MethodBase method in DeclaredBodies())
		{
			EntityHandle tokenHandle = MetadataTokens.EntityHandle(method.MetadataToken);
			Assert.Equal(HandleKind.MethodDefinition, tokenHandle.Kind);
			result.Add(
				(MethodDefinitionHandle)tokenHandle,
				new RawMethodDefinitionExpectation(
					method,
					ExpectedMethodAttributes(method),
					method.MethodImplementationFlags));
		}
		return result;
	}

	private static IReadOnlyDictionary<MethodDefinitionHandle, RawMethodDefinitionExpectation> RawPeFixtureMethodExpectations(
		RawPeMutation mutation)
	{
		bool generic = mutation is RawPeMutation.MissingGenericBitMethodDefinition or
			RawPeMutation.TypeCarryingGenericParameterAttribute or
			RawPeMutation.TypeCarryingGenericConstraintAttribute or
			RawPeMutation.MethodGenericConstraintObject or
			RawPeMutation.MethodGenericConstraintTypeSpecObject or
			RawPeMutation.MethodGenericConstraintForbidden or
			RawPeMutation.MethodGenericConstraintModifier or
			RawPeMutation.MethodGenericConstraintUnresolved or
			RawPeMutation.MethodGenericConstraintCycle or
			RawPeMutation.MethodGenericConstraintTrailingData;
		bool parameterized = mutation is RawPeMutation.ParameterizedMethodDefinition or
			RawPeMutation.WrongParameterName or RawPeMutation.OptionalParameter or
			RawPeMutation.DefaultParameter or RawPeMutation.MarshaledParameter;
		bool typeSpecificationReturn = mutation is
			RawPeMutation.MethodTypeSpecObject or RawPeMutation.MethodTypeSpecObjectAsValueType or
			RawPeMutation.MethodTypeSpecModifier or
			RawPeMutation.MethodTypeSpecTrailingData or RawPeMutation.MethodTypeSpecCycle or
			RawPeMutation.MethodTypeSpecUnresolved or RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope or
			RawPeMutation.MethodTypeSpecNestedCycleAttribute;
		bool objectTypeReferenceReturn = mutation is RawPeMutation.MethodAssemblyReferenceTypeAlias or
			RawPeMutation.MethodAssemblyReferenceCrossApproved or RawPeMutation.MethodAssemblyReferenceRetargetable or
			RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture or
			RawPeMutation.MethodLocalTypeDefinitionAlias or RawPeMutation.MethodObjectAsValueType;
		bool observationTypeDefinitionReturn = mutation == RawPeMutation.MethodLocalObservationTypeDefinitionAlias;
		bool primitiveInt32TypeReferenceReturn = mutation == RawPeMutation.MethodPrimitiveInt32AsTypeReference;
		bool nestedTypeReferenceReturn = mutation is
			RawPeMutation.NestedTypeReferenceScope or RawPeMutation.TopLevelNestedTypeReferenceAlias;
		bool genericMetadataNameReturn = mutation == RawPeMutation.MethodGenericMetadataNameAlias;
		bool instance = mutation is RawPeMutation.InstanceMethodDefinition or RawPeMutation.ExplicitThisMethodDefinition;
		MethodInfo expectedMethod = instance
			? typeof(RawMethodDefinitionInstanceExpectationFixture).GetMethod(
				nameof(RawMethodDefinitionInstanceExpectationFixture.Method),
				BindingFlags.Public | BindingFlags.Instance)!
			: primitiveInt32TypeReferenceReturn
				? typeof(RawMethodDefinitionPrimitiveInt32ExpectationFixture).GetMethod(
					nameof(RawMethodDefinitionPrimitiveInt32ExpectationFixture.Method),
					BindingFlags.Public | BindingFlags.Static)!
			: nestedTypeReferenceReturn
				? typeof(RawMethodDefinitionNestedTypeExpectationFixture).GetMethod(
					nameof(RawMethodDefinitionNestedTypeExpectationFixture.Method),
					BindingFlags.Public | BindingFlags.Static)!
			: genericMetadataNameReturn
				? typeof(RawMethodDefinitionGenericExpectationFixture).GetMethod(
					nameof(RawMethodDefinitionGenericExpectationFixture.Method),
					BindingFlags.Public | BindingFlags.Static)!
			: typeSpecificationReturn || objectTypeReferenceReturn
				? typeof(RawMethodDefinitionTypeSpecExpectationFixture).GetMethod(
					nameof(RawMethodDefinitionTypeSpecExpectationFixture.Method),
					BindingFlags.Public | BindingFlags.Static)!
			: observationTypeDefinitionReturn
				? typeof(RawMethodDefinitionObservationExpectationFixture).GetMethod(
					nameof(RawMethodDefinitionObservationExpectationFixture.Method),
					BindingFlags.Public | BindingFlags.Static)!
			: parameterized
				? typeof(RawMethodDefinitionParameterizedExpectationFixture).GetMethod(
					nameof(RawMethodDefinitionParameterizedExpectationFixture.Method),
					BindingFlags.Public | BindingFlags.Static)!
				: typeof(RawMethodDefinitionExpectationFixture).GetMethod(
					generic ? nameof(RawMethodDefinitionExpectationFixture.GenericMethod) : nameof(RawMethodDefinitionExpectationFixture.Method),
					BindingFlags.Public | BindingFlags.Static)!;
		MethodAttributes expectedAttributes = MethodAttributes.Public | MethodAttributes.HideBySig;
		if (!instance)
		{
			expectedAttributes |= MethodAttributes.Static;
		}
		var result = new Dictionary<MethodDefinitionHandle, RawMethodDefinitionExpectation>
		{
			[MetadataTokens.MethodDefinitionHandle(1)] = new(
				expectedMethod,
				expectedAttributes,
				MethodImplAttributes.IL,
				mutation is RawPeMutation.ReturnParameterDefinition or
					RawPeMutation.WrongReturnParameterName or
					RawPeMutation.OptionalReturnParameter or
					RawPeMutation.DefaultReturnParameter or
					RawPeMutation.MarshaledReturnParameter,
				typeSpecificationReturn),
		};
		MethodInfo getter = typeof(RawPropertyExpectationFixture).GetProperty(
			nameof(RawPropertyExpectationFixture.Property),
			BindingFlags.Public | BindingFlags.Instance)!.GetMethod!;
		result.Add(
			MetadataTokens.MethodDefinitionHandle(2),
			new RawMethodDefinitionExpectation(
				getter,
				MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
				MethodImplAttributes.IL));
		if (mutation == RawPeMutation.TypeCarryingConstructorAttribute)
		{
			ConstructorInfo constructor = typeof(RawMethodDefinitionConstructorExpectationFixture).GetConstructor(
				BindingFlags.Instance | BindingFlags.NonPublic,
				binder: null,
				Type.EmptyTypes,
				modifiers: null)!;
			result.Add(
				MetadataTokens.MethodDefinitionHandle(3),
				new RawMethodDefinitionExpectation(
					constructor,
					MethodAttributes.Private | MethodAttributes.HideBySig |
						MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
					MethodImplAttributes.IL));
		}
		return result;
	}

	private static void ValidateRawMethodDefinition(
		MetadataReader reader,
		ModifierRejectingTypeProvider provider,
		MethodDefinitionHandle handle,
		RawMethodDefinitionExpectation expectation,
		ISet<string> violations)
	{
		MethodDefinition method = reader.GetMethodDefinition(handle);
		MethodBase expected = expectation.Callable;
		int expectedGenericArity = expected is MethodInfo methodInfo && methodInfo.IsGenericMethodDefinition
			? methodInfo.GetGenericArguments().Length
			: 0;
		int expectedParameterCount = expected.GetParameters().Length;
		byte[] bytes = reader.GetBlobBytes(method.Signature);
		AddViolations(
			violations,
			VerifyRawSignature(
				new RawSignaturePolicy(
					RawSignatureKind.Method,
					!expected.IsStatic,
					expectedGenericArity,
					expectedParameterCount),
				bytes));
		if (method.GetGenericParameters().Count != expectedGenericArity)
		{
			violations.Add("GENERIC_ARITY");
		}
		if (method.Attributes != expectation.Attributes)
		{
			violations.Add("CALLABLE_FLAGS");
		}
		if (method.ImplAttributes != expectation.Implementation)
		{
			violations.Add("IMPLEMENTATION_FLAGS");
		}
		ParameterHandle[] parameterHandles = method.GetParameters().ToArray();
		Parameter[] parameterRows = parameterHandles.Select(reader.GetParameter).ToArray();
		if (parameterRows.Count(parameter => parameter.SequenceNumber != 0) != expectedParameterCount ||
			parameterRows.Where(parameter => parameter.SequenceNumber != 0)
				.Select(parameter => parameter.SequenceNumber)
				.Order()
				.SequenceEqual(Enumerable.Range(1, expectedParameterCount)) is false)
		{
			violations.Add("PARAMETER_METADATA");
		}
		ParameterInfo[] expectedParameters = expected.GetParameters();
		ParameterInfo? expectedReturnParameter = expected is MethodInfo expectedMethod
			? expectedMethod.ReturnParameter
			: null;
		bool expectedReturnRow = expectation.ExpectReturnParameter ||
			expectedReturnParameter is not null &&
				(expectedReturnParameter.Attributes != ParameterAttributes.None ||
					expectedReturnParameter.CustomAttributes.Any());
		Parameter[] returnRows = parameterRows.Where(parameter => parameter.SequenceNumber == 0).ToArray();
		if (returnRows.Length != (expectedReturnRow ? 1 : 0))
		{
			violations.Add("PARAMETER_METADATA");
		}
		foreach (Parameter parameter in parameterRows)
		{
			ParameterInfo? expectedParameter;
			if (parameter.SequenceNumber == 0)
			{
				expectedParameter = expectedReturnRow ? expectedReturnParameter : null;
			}
			else
			{
				int expectedIndex = parameter.SequenceNumber - 1;
				expectedParameter = expectedIndex >= 0 && expectedIndex < expectedParameters.Length
					? expectedParameters[expectedIndex]
					: null;
			}
			if (expectedParameter is null)
			{
				violations.Add("PARAMETER_METADATA");
				continue;
			}
			ParameterAttributes nonMarshalAttributes = parameter.Attributes & ~ParameterAttributes.HasFieldMarshal;
			if (reader.GetString(parameter.Name) != (expectedParameter.Name ?? string.Empty) ||
				nonMarshalAttributes != (expectedParameter.Attributes & ~ParameterAttributes.HasFieldMarshal))
			{
				violations.Add("PARAMETER_METADATA");
			}
			ConstantHandle defaultValue = parameter.GetDefaultValue();
			bool expectedDefault = (expectedParameter.Attributes & ParameterAttributes.HasDefault) != 0;
			if (((parameter.Attributes & ParameterAttributes.HasDefault) != 0) != expectedDefault ||
				defaultValue.IsNil == expectedDefault ||
				expectedDefault && !Equals(ReadRawConstant(reader, defaultValue), expectedParameter.RawDefaultValue))
			{
				violations.Add("PARAMETER_METADATA");
			}
			BlobHandle marshal = parameter.GetMarshallingDescriptor();
			bool expectedMarshal = (expectedParameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0;
			if (((parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0) != expectedMarshal ||
				marshal.IsNil == expectedMarshal)
			{
				violations.Add("PARAMETER_MARSHAL");
			}
		}
		if (expectation.DecodeTypeSpecificationHandles)
		{
			var referencedTypes = new List<(EntityHandle Handle, byte RawTypeKind)>();
			var cursorViolations = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				var cursor = new RawSignatureCursor(
					bytes,
					cursorViolations,
					typeReferenceVisitor: null,
					(handle, rawTypeKind) => referencedTypes.Add((handle, rawTypeKind)));
				cursor.ConsumeMethod(new RawSignaturePolicy(
					RawSignatureKind.Method,
					!expected.IsStatic,
					expectedGenericArity,
					expectedParameterCount));
				if (!cursor.AtEnd)
				{
					cursorViolations.Add("TRAILING_DATA");
				}
			}
			catch (InvalidOperationException)
			{
				cursorViolations.Add("MALFORMED_SIGNATURE");
			}
			AddViolations(violations, cursorViolations);
			string? decodedReturn = referencedTypes.Count == 1
				? TryDecodeRawSignatureType(
					provider,
					referencedTypes[0].Handle,
					referencedTypes[0].RawTypeKind,
					violations)
				: null;
			byte expectedReturnKind = expected is MethodInfo expectedMethodInfo
				? ExpectedRawTypeKind(expectedMethodInfo.ReturnType)
				: (byte)0x01;
			string expectedReturn = RawTypeSpecificationKindKey(RawReturnTypeKey(expected), expectedReturnKind);
			if (reader.GetString(method.Name) != expected.Name ||
				referencedTypes.Count != 1 ||
				decodedReturn is not null && decodedReturn != expectedReturn)
			{
				violations.Add("UNAPPROVED_TYPE");
			}
		}
		else
		{
			try
			{
				BlobReader blob = reader.GetBlobReader(method.Signature);
				MethodSignature<string> decoded = new SignatureDecoder<string, object?>(provider, reader, null)
					.DecodeMethodSignature(ref blob);
				if (blob.RemainingBytes != 0) { violations.Add("TRAILING_DATA"); }
				if (reader.GetString(method.Name) != expected.Name ||
					decoded.ReturnType != RawReturnTypeKey(expected) ||
					!decoded.ParameterTypes.SequenceEqual(
						expected.GetParameters().Select(parameter => RawTypeKey(parameter.ParameterType)),
						StringComparer.Ordinal))
				{
					violations.Add("UNAPPROVED_TYPE");
				}
			}
			catch (Exception exception) when (IsRawMetadataException(exception))
			{
				RecordRawMetadataException(exception, violations);
			}
		}
	}

	private static void ValidateRawMethodGenericConstraints(
		MetadataReader reader,
		ModifierRejectingTypeProvider provider,
		MethodDefinition method,
		ISet<string> violations)
	{
		foreach (GenericParameterHandle parameterHandle in method.GetGenericParameters())
		{
			GenericParameter parameter = reader.GetGenericParameter(parameterHandle);
			foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
			{
				EntityHandle constraintType = reader.GetGenericParameterConstraint(constraintHandle).Type;
				string? decoded = TryDecodeRawType(provider, constraintType, violations);
				string expected = constraintType.Kind == HandleKind.TypeSpecification
					? RawTypeKindKey(RawEntityTypeKey(typeof(object)), 0x12)
					: RawEntityTypeKey(typeof(object));
				if (decoded is not null && decoded != expected)
				{
					violations.Add("UNAPPROVED_TYPE");
				}
			}
		}
	}

	private static void ValidateRawMemberReference(
		MetadataReader reader,
		ModifierRejectingTypeProvider provider,
		MemberReferenceHandle handle,
		IReadOnlyList<RawMemberExpectation> expectations,
		string mismatchViolation,
		ISet<string> violations)
	{
		MemberReference member = reader.GetMemberReference(handle);
		int parentModifierCount = provider.Modifiers.Count;
		string? parentType = TryDecodeRawType(provider, member.Parent, violations);
		bool parentContainsModifier = provider.Modifiers.Count != parentModifierCount;
		byte[] signatureBytes = reader.GetBlobBytes(member.Signature);
		if (member.GetKind() == MemberReferenceKind.Field)
		{
			AddViolations(
				violations,
				VerifyRawSignature(new RawSignaturePolicy(RawSignatureKind.Field, false, 0, 0), signatureBytes));
			BlobReader blob = reader.GetBlobReader(member.Signature);
			string fieldType = new SignatureDecoder<string, object?>(provider, reader, null)
				.DecodeFieldSignature(ref blob);
			if (blob.RemainingBytes != 0) { violations.Add("TRAILING_DATA"); }
			if (!expectations.Any(expectation =>
				expectation.IsField &&
				(parentType == expectation.Parent ||
					parentContainsModifier && parentType == RawTypeKindKey(expectation.Parent, 0x12)) &&
				reader.GetString(member.Name) == expectation.Name &&
				fieldType == expectation.ReturnOrFieldType))
			{
				violations.Add(mismatchViolation);
			}
			return;
		}

		int[] arities = expectations.Where(expectation => !expectation.IsField)
			.Select(expectation => expectation.GenericArity)
			.Distinct()
			.ToArray();
		int[] parameterCounts = expectations.Where(expectation => !expectation.IsField)
			.Select(expectation => expectation.Parameters.Length)
			.Distinct()
			.ToArray();
		bool[] instanceKinds = expectations.Where(expectation => !expectation.IsField)
			.Select(expectation => expectation.IsInstance)
			.Distinct()
			.ToArray();
		if (arities.Length == 1 && parameterCounts.Length == 1 && instanceKinds.Length == 1)
		{
			AddViolations(
				violations,
				VerifyRawSignature(
					new RawSignaturePolicy(RawSignatureKind.Method, instanceKinds[0], arities[0], parameterCounts[0]),
					signatureBytes));
		}
		BlobReader methodBlob = reader.GetBlobReader(member.Signature);
		MethodSignature<string> decoded = new SignatureDecoder<string, object?>(provider, reader, null)
			.DecodeMethodSignature(ref methodBlob);
		if (methodBlob.RemainingBytes != 0) { violations.Add("TRAILING_DATA"); }
		if (!expectations.Any(expectation =>
			!expectation.IsField &&
			(parentType == expectation.Parent ||
				parentContainsModifier && parentType == RawTypeKindKey(expectation.Parent, 0x12)) &&
			reader.GetString(member.Name) == expectation.Name &&
			decoded.Header.IsInstance == expectation.IsInstance &&
			decoded.Header.IsGeneric == (expectation.GenericArity != 0) &&
			decoded.GenericParameterCount == expectation.GenericArity &&
			decoded.ReturnType == expectation.ReturnOrFieldType &&
			decoded.ParameterTypes.SequenceEqual(expectation.Parameters, StringComparer.Ordinal)))
		{
			violations.Add(mismatchViolation);
		}
	}

	private static void ValidateRawPeHandle(
		MetadataReader reader,
		ModifierRejectingTypeProvider provider,
		EntityHandle handle,
		MemberInfo? expected,
		IReadOnlyDictionary<EntityHandle, MemberInfo> productionMembers,
		RawPePolicyMode policyMode,
		ISet<string> violations)
	{
		if (policyMode == RawPePolicyMode.Production && expected is null)
		{
			violations.Add("UNMAPPED_REACHABLE_HANDLE");
			return;
		}
		try
		{
			switch (handle.Kind)
			{
				case HandleKind.MemberReference:
					MemberReference member = reader.GetMemberReference((MemberReferenceHandle)handle);
					RawMemberExpectation expectation;
					if (member.GetKind() == MemberReferenceKind.Field)
					{
						expectation = expected is FieldInfo reflectedField
							? RawMemberExpectation.ForField(
								RawMemberParentTypeKey(reflectedField.DeclaringType!),
								reflectedField.Name,
								RawTypeKey(reflectedField.FieldType))
							: reader.GetString(member.Name) == "TypeSpecFieldTarget"
								? RawMemberExpectation.ForField(
									RawEntityTypeKey(typeof(object)),
									"TypeSpecFieldTarget",
									RawTypeSpecificationKindKey(RawTypeKey(typeof(Type)), 0x11))
							: RawMemberExpectation.ForField(RawEntityTypeKey(typeof(object)), "FieldTarget", RawTypeKey(typeof(int)));
					}
					else if (expected is MethodBase reflectedMethod)
					{
						MethodBase signatureMethod = RawSignatureDefinition(reflectedMethod);
						expectation = RawMemberExpectation.ForMethod(
							RawMemberParentTypeKey(reflectedMethod.DeclaringType!),
							reflectedMethod.Name,
							RawReturnTypeKey(signatureMethod),
							signatureMethod.GetParameters().Select(parameter => RawTypeKey(parameter.ParameterType)).ToArray(),
							!reflectedMethod.IsStatic,
							signatureMethod is MethodInfo methodInfo && methodInfo.IsGenericMethodDefinition
								? methodInfo.GetGenericArguments().Length
								: 0);
					}
					else if (reader.GetString(member.Name) == "TypeSpecMethodTarget")
					{
						expectation = RawMemberExpectation.ForMethod(
							RawEntityTypeKey(typeof(object)),
							"TypeSpecMethodTarget",
							RawTypeSpecificationKindKey(RawTypeKey(typeof(Type)), 0x12),
							[],
							true,
							1);
					}
					else if (reader.GetString(member.Name) == "GenericTarget")
					{
						expectation = RawMemberExpectation.ForMethod(
							RawEntityTypeKey(typeof(object)),
							"GenericTarget",
							RawTypeKey(typeof(void)),
							[RawIdentityNode("generic-method-parameter", "0")],
							true,
							1);
					}
					else
					{
						expectation = RawMemberExpectation.ForMethod(
							RawSystemRuntimeTypeKey("System.Runtime.CompilerServices.NullableAttribute"),
							".ctor",
							RawTypeKey(typeof(void)),
							[RawTypeKey(typeof(byte[]))],
							true,
							0);
					}
					ValidateRawMemberReference(
						reader,
						provider,
						(MemberReferenceHandle)handle,
						[expectation],
						"UNAPPROVED_TYPE",
						violations);
					break;
				case HandleKind.MethodSpecification:
					MethodSpecification specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
					ValidateRawPeHandle(
						reader,
						provider,
						specification.Method,
							expected is MethodInfo expectedMethodSpecification && expectedMethodSpecification.IsGenericMethod
								? expectedMethodSpecification.GetGenericMethodDefinition()
								: null,
							productionMembers,
							policyMode,
							violations);
					RawMethodGenericShape parentGeneric = ReadRawMethodGenericShape(reader, specification.Method);
					if (!parentGeneric.IsGeneric || parentGeneric.Arity <= 0)
					{
						violations.Add("METHOD_SPEC_PARENT");
					}
					byte[] specificationBytes = reader.GetBlobBytes(specification.Signature);
					AddViolations(
						violations,
						VerifyRawSignature(
							new RawSignaturePolicy(RawSignatureKind.MethodSpecification, false, parentGeneric.Arity, 0),
							specificationBytes));
					BlobReader specificationBlob = reader.GetBlobReader(specification.Signature);
					ImmutableArray<string> decodedArguments = new SignatureDecoder<string, object?>(provider, reader, null)
						.DecodeMethodSpecificationSignature(ref specificationBlob);
					if (specificationBlob.RemainingBytes != 0) { violations.Add("TRAILING_DATA"); }
					string[] expectedArguments = expected is MethodInfo expectedSpecification && expectedSpecification.IsGenericMethod
						? expectedSpecification.GetGenericArguments().Select(RawTypeKey).ToArray()
						: [RawTypeKey(typeof(int))];
					if (!decodedArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal))
					{
						violations.Add("UNAPPROVED_TYPE");
					}
					break;
				case HandleKind.TypeDefinition:
				case HandleKind.TypeReference:
				case HandleKind.TypeSpecification:
					string? decodedType = TryDecodeRawType(provider, handle, violations);
					string? expectedTypeKey = expected is not Type expectedType
						? null
						: handle.Kind == HandleKind.TypeSpecification
							? RawTypeKey(expectedType)
							: RawEntityTypeKey(expectedType);
					if (expectedTypeKey is not null && decodedType != expectedTypeKey)
					{
						violations.Add("UNAPPROVED_TYPE");
					}
					break;
				case HandleKind.MethodDefinition:
				case HandleKind.FieldDefinition:
					break;
				case HandleKind.StandaloneSignature:
					violations.Add("SIGNATURE_OPERAND");
					break;
				default:
					violations.Add("UNRESOLVED_TOKEN");
					break;
			}
		}
		catch (Exception exception) when (IsRawMetadataException(exception))
		{
			RecordRawMetadataException(exception, violations);
		}
	}

	private static RawMethodGenericShape ReadRawMethodGenericShape(MetadataReader reader, EntityHandle method)
	{
		BlobHandle signature = method.Kind switch
		{
			HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)method).Signature,
			HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)method).Signature,
			_ => throw new BadImageFormatException("A MethodSpec parent must be a method definition or reference."),
		};
		BlobReader blob = reader.GetBlobReader(signature);
		SignatureHeader header = blob.ReadSignatureHeader();
		return new RawMethodGenericShape(
			header.IsGeneric,
			header.IsGeneric ? blob.ReadCompressedInteger() : 0);
	}

	private static void ExpandRawReachableAttributeOwners(
		MetadataReader reader,
		EntityHandle subjectRoot,
		ISet<EntityHandle> owners,
		ISet<string> violations)
	{
		var pending = new Queue<EntityHandle>(owners);
		var visited = new HashSet<EntityHandle>();
		while (pending.TryDequeue(out EntityHandle handle))
		{
			if (!visited.Add(handle))
			{
				continue;
			}
			var traversalProvider = new ModifierRejectingTypeProvider(reader);
			try
			{
				switch (handle.Kind)
				{
					case HandleKind.MemberReference:
						MemberReference member = reader.GetMemberReference((MemberReferenceHandle)handle);
						AddOwner(member.Parent);
						BlobReader memberBlob = reader.GetBlobReader(member.Signature);
						var memberDecoder = new SignatureDecoder<string, object?>(traversalProvider, reader, null);
						if (member.GetKind() == MemberReferenceKind.Field)
						{
							_ = memberDecoder.DecodeFieldSignature(ref memberBlob);
						}
						else
						{
							_ = memberDecoder.DecodeMethodSignature(ref memberBlob);
						}
						break;
					case HandleKind.MethodSpecification:
						MethodSpecification specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
						AddOwner(specification.Method);
						BlobReader specificationBlob = reader.GetBlobReader(specification.Signature);
						_ = new SignatureDecoder<string, object?>(traversalProvider, reader, null)
							.DecodeMethodSpecificationSignature(ref specificationBlob);
						break;
					case HandleKind.StandaloneSignature:
						StandaloneSignature standalone = reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
						BlobReader standaloneBlob = reader.GetBlobReader(standalone.Signature);
						var standaloneDecoder = new SignatureDecoder<string, object?>(traversalProvider, reader, null);
						if (reader.GetBlobBytes(standalone.Signature)[0] == 0x07)
						{
							_ = standaloneDecoder.DecodeLocalSignature(ref standaloneBlob);
						}
						else
						{
							_ = standaloneDecoder.DecodeMethodSignature(ref standaloneBlob);
						}
						break;
					case HandleKind.TypeSpecification:
					case HandleKind.TypeDefinition:
					case HandleKind.TypeReference:
						_ = traversalProvider.DecodeEntityType(handle);
						break;
				}
				foreach (EntityHandle typeHandle in traversalProvider.EntityHandles.Where(value =>
					value.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification or
						HandleKind.AssemblyReference or HandleKind.ModuleDefinition or HandleKind.ModuleReference))
				{
					AddOwner(typeHandle);
				}
			}
			catch (Exception exception) when (IsRawMetadataException(exception))
			{
				RecordRawMetadataException(exception, violations);
			}
			AddViolations(violations, traversalProvider.RawTypeSpecificationViolations);
			AddViolations(violations, traversalProvider.RawTypeScopeViolations);
		}

		void AddOwner(EntityHandle handle)
		{
			if (handle == subjectRoot)
			{
				return;
			}
			if ((handle.Kind is HandleKind.MemberReference or HandleKind.MethodSpecification or
				HandleKind.StandaloneSignature or HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification or
				HandleKind.AssemblyReference or HandleKind.ModuleDefinition or HandleKind.ModuleReference) && owners.Add(handle))
			{
				pending.Enqueue(handle);
			}
		}
	}

	private static bool IsReachableCustomAttributeOwner(EntityHandle handle) => handle.Kind is
		HandleKind.StandaloneSignature or HandleKind.MemberReference or HandleKind.MethodSpecification or
		HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification or
		HandleKind.AssemblyReference or HandleKind.ModuleDefinition or HandleKind.ModuleReference;

	private static IEnumerable<CustomAttributeHandle> ReachableCustomAttributes(
		MetadataReader reader,
		EntityHandle owner) => owner.Kind switch
		{
			HandleKind.TypeDefinition => reader.GetTypeDefinition((TypeDefinitionHandle)owner).GetCustomAttributes(),
			HandleKind.StandaloneSignature => reader.GetStandaloneSignature((StandaloneSignatureHandle)owner).GetCustomAttributes(),
			HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)owner).GetCustomAttributes(),
			HandleKind.MethodSpecification => reader.GetMethodSpecification((MethodSpecificationHandle)owner).GetCustomAttributes(),
			HandleKind.TypeReference => reader.GetCustomAttributes(owner),
			HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)owner).GetCustomAttributes(),
			HandleKind.AssemblyReference => reader.GetCustomAttributes(owner),
			HandleKind.ModuleDefinition => reader.GetCustomAttributes(owner),
			HandleKind.ModuleReference => reader.GetModuleReference((ModuleReferenceHandle)owner).GetCustomAttributes(),
			_ => [],
		};

	private static string? TryDecodeRawType(
		ModifierRejectingTypeProvider provider,
		EntityHandle handle,
		ISet<string> violations)
	{
		try
		{
			return provider.DecodeEntityType(handle);
		}
		catch (Exception exception) when (IsRawMetadataException(exception))
		{
			RecordRawMetadataException(exception, violations);
			return null;
		}
	}

	private static string? TryDecodeRawSignatureType(
		ModifierRejectingTypeProvider provider,
		EntityHandle handle,
		byte rawTypeKind,
		ISet<string> violations)
	{
		try
		{
			return provider.DecodeSignatureEntityType(handle, rawTypeKind);
		}
		catch (Exception exception) when (IsRawMetadataException(exception))
		{
			RecordRawMetadataException(exception, violations);
			return null;
		}
	}

	private static bool IsRawMetadataException(Exception exception) =>
		exception is BadImageFormatException or InvalidOperationException or IndexOutOfRangeException or Xunit.Sdk.XunitException;

	private static void RecordRawMetadataException(Exception exception, ISet<string> violations)
	{
		if (exception is Xunit.Sdk.XunitException && exception.Message.Contains("cyclic TypeSpec", StringComparison.Ordinal))
		{
			violations.Add("TYPE_SPEC_CYCLE");
		}
		else if (exception is BadImageFormatException or IndexOutOfRangeException)
		{
			violations.Add("UNRESOLVED_TYPE_ROOT");
		}
		else
		{
			violations.Add("MALFORMED_SIGNATURE");
		}
	}

	private static void AddViolations(ISet<string> target, IEnumerable<string> values)
	{
		foreach (string value in values)
		{
			target.Add(value);
		}
	}

	private static void AssertRawPeMutationPresent(byte[] peBytes, RawPeMutation mutation)
	{
		using var stream = new MemoryStream(peBytes, writable: false);
		using var peReader = new PEReader(stream);
		MetadataReader reader = peReader.GetMetadataReader();
		TypeDefinitionHandle subjectTypeHandle = FindType(reader, "RawFixture", "ObservationBatchFixture");
		TypeDefinition definition = reader.GetTypeDefinition(subjectTypeHandle);
		TypeReferenceHandle forbiddenProductType = AssertExactForbiddenProductTypeReference(reader);
		MethodDefinitionHandle subjectMethodHandle = definition.GetMethods().Single(handle =>
			reader.GetString(reader.GetMethodDefinition(handle).Name) is "Method" or "GenericMethod");
		if (mutation == RawPeMutation.None)
		{
			Assert.Single(definition.GetInterfaceImplementations());
			Assert.Empty(definition.GetMethodImplementations());
			return;
		}
		Assert.NotEqual(
			Convert.ToHexString(SHA256.HashData(BuildRawPeFixture(RawPeMutation.None))),
			Convert.ToHexString(SHA256.HashData(peBytes)));

		switch (mutation)
		{
			case RawPeMutation.BaseTypeModifier:
				{
					Assert.Equal(HandleKind.TypeSpecification, definition.BaseType.Kind);
					byte[] signature = reader.GetBlobBytes(
						reader.GetTypeSpecification((TypeSpecificationHandle)definition.BaseType).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[],
						ConcatBytes(
							[0x12],
							EncodeTypeDefOrRef(FindRawTypeReference(reader, "System", "Object"))),
						forbiddenProductType,
							cursor => cursor.ConsumeType(allowVoid: false));
					Assert.Equal(
						[(forbiddenProductType, (byte)0), (FindRawTypeReference(reader, "System", "Object"), (byte)0x12)],
						types);
					break;
				}
			case RawPeMutation.BaseTypeCycle:
			case RawPeMutation.BaseTypeUnresolved:
				Assert.Equal(HandleKind.TypeSpecification, definition.BaseType.Kind);
				Assert.NotEmpty(reader.GetBlobBytes(reader.GetTypeSpecification((TypeSpecificationHandle)definition.BaseType).Signature));
				break;
			case RawPeMutation.MethodTypeSpecObject:
			case RawPeMutation.MethodTypeSpecObjectAsValueType:
			case RawPeMutation.MethodTypeSpecModifier:
			case RawPeMutation.MethodTypeSpecTrailingData:
			case RawPeMutation.MethodTypeSpecCycle:
			case RawPeMutation.MethodTypeSpecUnresolved:
			case RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope:
			case RawPeMutation.MethodTypeSpecNestedCycleAttribute:
				byte[] methodTypeSpecSignature = reader.GetBlobBytes(
					reader.GetMethodDefinition(subjectMethodHandle).Signature);
				var referencedMethodTypes = new List<EntityHandle>();
				var methodTypeSpecViolations = new HashSet<string>(StringComparer.Ordinal);
				var methodTypeSpecCursor = new RawSignatureCursor(
					methodTypeSpecSignature,
					methodTypeSpecViolations,
					referencedMethodTypes.Add);
				methodTypeSpecCursor.ConsumeMethod(new RawSignaturePolicy(RawSignatureKind.Method, false, 0, 0));
				Assert.True(methodTypeSpecCursor.AtEnd);
				Assert.Empty(methodTypeSpecViolations);
				EntityHandle methodTypeSpecHandle = Assert.Single(referencedMethodTypes);
				Assert.Equal(HandleKind.TypeSpecification, methodTypeSpecHandle.Kind);
				byte methodTypeSpecOuterKind = mutation == RawPeMutation.MethodTypeSpecObjectAsValueType
					? (byte)0x11
					: (byte)0x12;
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x00, 0x00, methodTypeSpecOuterKind], EncodeTypeDefOrRef(methodTypeSpecHandle))),
					Convert.ToHexString(methodTypeSpecSignature));
				if (mutation == RawPeMutation.MethodTypeSpecUnresolved)
				{
					Assert.Equal(63, MetadataTokens.GetRowNumber(methodTypeSpecHandle));
					break;
				}
				byte[] methodTypeSpec = reader.GetBlobBytes(
					reader.GetTypeSpecification((TypeSpecificationHandle)methodTypeSpecHandle).Signature);
				TypeReferenceHandle presenceObjectType = FindRawTypeReference(reader, "System", "Object");
				TypeReferenceHandle presenceSystemType = FindRawTypeReference(reader, "System", "Type");
				TypeReferenceHandle presenceForbiddenType = forbiddenProductType;
				TypeReferenceHandle presenceMixedScopedType = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.SingleOrDefault(handle => reader.GetString(reader.GetTypeReference(handle).Name) == "ScopedCycleType");
				byte[] expectedMethodTypeSpec = mutation switch
				{
					RawPeMutation.MethodTypeSpecObject or
					RawPeMutation.MethodTypeSpecObjectAsValueType => ConcatBytes(
						[0x12], EncodeTypeDefOrRef(presenceSystemType)),
					RawPeMutation.MethodTypeSpecModifier => ConcatBytes(
						[0x1f], EncodeTypeDefOrRef(presenceForbiddenType),
						[0x12], EncodeTypeDefOrRef(presenceSystemType)),
					RawPeMutation.MethodTypeSpecTrailingData => ConcatBytes(
						[0x12], EncodeTypeDefOrRef(presenceSystemType), [0x00]),
					RawPeMutation.MethodTypeSpecCycle => ConcatBytes(
						[0x12], EncodeTypeDefOrRef(methodTypeSpecHandle)),
					RawPeMutation.MethodTypeSpecCycleWithUnexpectedScope => ConcatBytes(
						[0x15, 0x12], EncodeTypeDefOrRef(presenceObjectType),
						[0x02, 0x12], EncodeTypeDefOrRef(presenceMixedScopedType),
						[0x12], EncodeTypeDefOrRef(methodTypeSpecHandle)),
					RawPeMutation.MethodTypeSpecNestedCycleAttribute => ConcatBytes(
						[0x12], EncodeTypeDefOrRef(MetadataTokens.TypeSpecificationHandle(2))),
					_ => throw new InvalidOperationException(mutation.ToString()),
				};
				Assert.Equal(Convert.ToHexString(expectedMethodTypeSpec), Convert.ToHexString(methodTypeSpec));
				if (mutation == RawPeMutation.MethodTypeSpecNestedCycleAttribute)
				{
					TypeSpecificationHandle nestedCycleHandle = MetadataTokens.TypeSpecificationHandle(2);
					Assert.Equal(
						Convert.ToHexString(ConcatBytes([0x12], EncodeTypeDefOrRef(methodTypeSpecHandle))),
						Convert.ToHexString(reader.GetBlobBytes(reader.GetTypeSpecification(nestedCycleHandle).Signature)));
					Assert.Single(reader.GetTypeSpecification(nestedCycleHandle).GetCustomAttributes());
				}
				if (mutation == RawPeMutation.MethodTypeSpecModifier)
				{
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						methodTypeSpec,
						[],
						ConcatBytes([0x12], EncodeTypeDefOrRef(presenceSystemType)),
						forbiddenProductType,
						cursor => cursor.ConsumeType(allowVoid: false));
					Assert.Equal(
						[(forbiddenProductType, (byte)0), (presenceSystemType, (byte)0x12)],
						types);
				}
				if (mutation is RawPeMutation.MethodTypeSpecObject or
					RawPeMutation.MethodTypeSpecObjectAsValueType or RawPeMutation.MethodTypeSpecTrailingData)
				{
					byte[] validPrefix = mutation == RawPeMutation.MethodTypeSpecTrailingData
						? methodTypeSpec[..^1]
						: methodTypeSpec;
					var validTypeSpecViolations = new HashSet<string>(StringComparer.Ordinal);
					var validTypeSpecCursor = new RawSignatureCursor(validPrefix, validTypeSpecViolations);
					validTypeSpecCursor.ConsumeType(allowVoid: false);
					Assert.True(validTypeSpecCursor.AtEnd);
					Assert.Empty(validTypeSpecViolations);
					if (mutation == RawPeMutation.MethodTypeSpecTrailingData)
					{
						Assert.Equal((byte)0x00, methodTypeSpec[^1]);
						var trailingTypeSpecCursor = new RawSignatureCursor(
							methodTypeSpec,
							new HashSet<string>(StringComparer.Ordinal));
						trailingTypeSpecCursor.ConsumeType(allowVoid: false);
						Assert.False(trailingTypeSpecCursor.AtEnd);
					}
				}
				break;
			case RawPeMutation.MethodAssemblyReferenceTypeAlias:
			case RawPeMutation.MethodAssemblyReferenceCrossApproved:
			case RawPeMutation.MethodAssemblyReferenceRetargetable:
			case RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture:
				byte[] methodAssemblyAliasSignature = reader.GetBlobBytes(
					reader.GetMethodDefinition(subjectMethodHandle).Signature);
				var methodAssemblyAliasTypes = new List<EntityHandle>();
				var methodAssemblyAliasViolations = new HashSet<string>(StringComparer.Ordinal);
				var methodAssemblyAliasCursor = new RawSignatureCursor(
					methodAssemblyAliasSignature,
					methodAssemblyAliasViolations,
					methodAssemblyAliasTypes.Add);
				methodAssemblyAliasCursor.ConsumeMethod(new RawSignaturePolicy(RawSignatureKind.Method, false, 0, 0));
				Assert.True(methodAssemblyAliasCursor.AtEnd);
				Assert.Empty(methodAssemblyAliasViolations);
				EntityHandle methodAssemblyAliasType = Assert.Single(methodAssemblyAliasTypes);
				Assert.Equal(HandleKind.TypeReference, methodAssemblyAliasType.Kind);
				TypeReference methodAssemblyAliasReference = reader.GetTypeReference((TypeReferenceHandle)methodAssemblyAliasType);
				Assert.Equal("System", reader.GetString(methodAssemblyAliasReference.Namespace));
				Assert.Equal("Type", reader.GetString(methodAssemblyAliasReference.Name));
				Assert.Equal(HandleKind.AssemblyReference, methodAssemblyAliasReference.ResolutionScope.Kind);
				AssemblyReference methodAliasAssembly = reader.GetAssemblyReference(
					(AssemblyReferenceHandle)methodAssemblyAliasReference.ResolutionScope);
				Assert.Equal(
					mutation switch
					{
						RawPeMutation.MethodAssemblyReferenceTypeAlias => "System.Arbitrary",
						RawPeMutation.MethodAssemblyReferenceCrossApproved => "System.Collections",
						_ => "System.Runtime",
					},
					reader.GetString(methodAliasAssembly.Name));
				Assert.Equal(new Version(10, 0, 0, 0), methodAliasAssembly.Version);
				Assert.Equal(
					mutation == RawPeMutation.MethodAssemblyReferenceLiteralNeutralCulture ? "neutral" : string.Empty,
					reader.GetString(methodAliasAssembly.Culture));
				Assert.Equal(
					"b03f5f7f11d50a3a",
					Convert.ToHexString(reader.GetBlobBytes(methodAliasAssembly.PublicKeyOrToken)).ToLowerInvariant());
				Assert.Empty(reader.GetBlobBytes(methodAliasAssembly.HashValue));
				Assert.Equal(
					mutation == RawPeMutation.MethodAssemblyReferenceRetargetable
						? AssemblyFlags.Retargetable
						: default,
					methodAliasAssembly.Flags);
				break;
			case RawPeMutation.MethodLocalTypeDefinitionAlias:
				byte[] methodLocalAliasSignature = reader.GetBlobBytes(
					reader.GetMethodDefinition(subjectMethodHandle).Signature);
				var methodLocalAliasTypes = new List<EntityHandle>();
				var methodLocalAliasViolations = new HashSet<string>(StringComparer.Ordinal);
				var methodLocalAliasCursor = new RawSignatureCursor(
					methodLocalAliasSignature,
					methodLocalAliasViolations,
					methodLocalAliasTypes.Add);
				methodLocalAliasCursor.ConsumeMethod(new RawSignaturePolicy(RawSignatureKind.Method, false, 0, 0));
				Assert.True(methodLocalAliasCursor.AtEnd);
				Assert.Empty(methodLocalAliasViolations);
				EntityHandle methodLocalAliasType = Assert.Single(methodLocalAliasTypes);
				Assert.Equal(HandleKind.TypeDefinition, methodLocalAliasType.Kind);
				TypeDefinition methodLocalAliasDefinition = reader.GetTypeDefinition((TypeDefinitionHandle)methodLocalAliasType);
				Assert.Equal("System", reader.GetString(methodLocalAliasDefinition.Namespace));
				Assert.Equal("Type", reader.GetString(methodLocalAliasDefinition.Name));
				Assert.Equal(HandleKind.TypeReference, methodLocalAliasDefinition.BaseType.Kind);
				break;
			case RawPeMutation.MethodLocalObservationTypeDefinitionAlias:
				byte[] methodObservationAliasSignature = reader.GetBlobBytes(
					reader.GetMethodDefinition(subjectMethodHandle).Signature);
				var methodObservationAliasTypes = new List<EntityHandle>();
				var methodObservationAliasViolations = new HashSet<string>(StringComparer.Ordinal);
				var methodObservationAliasCursor = new RawSignatureCursor(
					methodObservationAliasSignature,
					methodObservationAliasViolations,
					methodObservationAliasTypes.Add);
				methodObservationAliasCursor.ConsumeMethod(new RawSignaturePolicy(RawSignatureKind.Method, false, 0, 0));
				Assert.True(methodObservationAliasCursor.AtEnd);
				Assert.Empty(methodObservationAliasViolations);
				EntityHandle methodObservationAliasType = Assert.Single(methodObservationAliasTypes);
				Assert.Equal(HandleKind.TypeDefinition, methodObservationAliasType.Kind);
				TypeDefinition methodObservationAliasDefinition = reader.GetTypeDefinition(
					(TypeDefinitionHandle)methodObservationAliasType);
				Assert.Equal("WalletWasabi.Liquid.Wallet", reader.GetString(methodObservationAliasDefinition.Namespace));
				Assert.Equal("LiquidWalletTransactionObservation", reader.GetString(methodObservationAliasDefinition.Name));
				break;
			case RawPeMutation.MethodObjectAsValueType:
				byte[] objectAsValueTypeSignature = reader.GetBlobBytes(
					reader.GetMethodDefinition(subjectMethodHandle).Signature);
				Assert.Equal((byte)0x11, objectAsValueTypeSignature[2]);
				TypeReferenceHandle objectAsValueTypeReference = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.Single(handle =>
						reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
						reader.GetString(reader.GetTypeReference(handle).Name) == "Type");
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x00, 0x00, 0x11], EncodeTypeDefOrRef(objectAsValueTypeReference))),
					Convert.ToHexString(objectAsValueTypeSignature));
				break;
			case RawPeMutation.MethodPrimitiveInt32AsTypeReference:
			case RawPeMutation.TopLevelNestedTypeReferenceAlias:
			case RawPeMutation.MethodGenericMetadataNameAlias:
				byte[] aliasedMethodSignature = reader.GetBlobBytes(
					reader.GetMethodDefinition(subjectMethodHandle).Signature);
				(string aliasNamespace, string aliasName, byte aliasKind) = mutation switch
				{
					RawPeMutation.MethodPrimitiveInt32AsTypeReference => ("System", "Int32", (byte)0x11),
					RawPeMutation.TopLevelNestedTypeReferenceAlias =>
						("System", "Environment+SpecialFolder", (byte)0x11),
					RawPeMutation.MethodGenericMetadataNameAlias =>
						("System.Collections.Generic", $"IList`1<{RawTypeKey(typeof(int))}>", (byte)0x12),
					_ => throw new InvalidOperationException(mutation.ToString()),
				};
				TypeReferenceHandle aliasedMethodType = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.Single(handle =>
						reader.GetString(reader.GetTypeReference(handle).Namespace) == aliasNamespace &&
						reader.GetString(reader.GetTypeReference(handle).Name) == aliasName);
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x00, 0x00, aliasKind], EncodeTypeDefOrRef(aliasedMethodType))),
					Convert.ToHexString(aliasedMethodSignature));
				break;
			case RawPeMutation.FieldLocalBatchTypeDefinitionAlias:
				FieldDefinition localBatchAliasField = reader.GetFieldDefinition(definition.GetFields().Single());
				byte[] localBatchAliasFieldSignature = reader.GetBlobBytes(localBatchAliasField.Signature);
				var localBatchAliasFieldTypes = new List<EntityHandle>();
				var localBatchAliasFieldViolations = new HashSet<string>(StringComparer.Ordinal);
				var localBatchAliasFieldCursor = new RawSignatureCursor(
					localBatchAliasFieldSignature,
					localBatchAliasFieldViolations,
					localBatchAliasFieldTypes.Add);
				localBatchAliasFieldCursor.ConsumeField();
				Assert.True(localBatchAliasFieldCursor.AtEnd);
				Assert.Empty(localBatchAliasFieldViolations);
				EntityHandle localBatchAliasFieldType = Assert.Single(localBatchAliasFieldTypes);
				Assert.Equal(HandleKind.TypeDefinition, localBatchAliasFieldType.Kind);
				TypeDefinition localBatchAliasDefinition = reader.GetTypeDefinition(
					(TypeDefinitionHandle)localBatchAliasFieldType);
				Assert.Equal("WalletWasabi.Liquid.Wallet", reader.GetString(localBatchAliasDefinition.Namespace));
				Assert.Equal("LiquidWalletObservationBatch", reader.GetString(localBatchAliasDefinition.Name));
				break;
			case RawPeMutation.FieldTypeSpecInt32:
			case RawPeMutation.FieldTypeSpecInt32AsClass:
				byte[] fieldTypeSpecSignature = reader.GetBlobBytes(
					reader.GetFieldDefinition(definition.GetFields().Single()).Signature);
				var fieldTypeSpecTypes = new List<(EntityHandle Handle, byte RawTypeKind)>();
				var fieldTypeSpecViolations = new HashSet<string>(StringComparer.Ordinal);
				var fieldTypeSpecCursor = new RawSignatureCursor(
					fieldTypeSpecSignature,
					fieldTypeSpecViolations,
					typeReferenceVisitor: null,
					signatureTypeReferenceVisitor: (handle, rawTypeKind) =>
						fieldTypeSpecTypes.Add((handle, rawTypeKind)));
				fieldTypeSpecCursor.ConsumeField();
				Assert.True(fieldTypeSpecCursor.AtEnd);
				Assert.Empty(fieldTypeSpecViolations);
				(EntityHandle fieldTypeSpecHandle, byte fieldTypeSpecKind) = Assert.Single(fieldTypeSpecTypes);
				Assert.Equal(HandleKind.TypeSpecification, fieldTypeSpecHandle.Kind);
				Assert.Equal(
					mutation == RawPeMutation.FieldTypeSpecInt32AsClass ? (byte)0x12 : (byte)0x11,
					fieldTypeSpecKind);
				TypeReferenceHandle fieldTypeSpecTypeReference = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.Single(handle =>
						reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
						reader.GetString(reader.GetTypeReference(handle).Name) == "Type");
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x12], EncodeTypeDefOrRef(fieldTypeSpecTypeReference))),
					Convert.ToHexString(reader.GetBlobBytes(reader.GetTypeSpecification(
						(TypeSpecificationHandle)fieldTypeSpecHandle).Signature)));
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x06, fieldTypeSpecKind], EncodeTypeDefOrRef(fieldTypeSpecHandle))),
					Convert.ToHexString(fieldTypeSpecSignature));
				break;
			case RawPeMutation.FieldPrimitiveInt32AsTypeReference:
				byte[] primitiveAliasFieldSignature = reader.GetBlobBytes(
					reader.GetFieldDefinition(definition.GetFields().Single()).Signature);
				TypeReferenceHandle primitiveAliasInt32 = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.Single(handle =>
						reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
						reader.GetString(reader.GetTypeReference(handle).Name) == "Int32");
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x06, 0x11], EncodeTypeDefOrRef(primitiveAliasInt32))),
					Convert.ToHexString(primitiveAliasFieldSignature));
				break;
			case RawPeMutation.FieldSzArray:
			case RawPeMutation.FieldMdArrayRankOne:
			case RawPeMutation.FieldSzArrayAsMdRankOne:
			case RawPeMutation.FieldMdArrayExplicitSize:
			case RawPeMutation.FieldMdArrayLowerBound:
			case RawPeMutation.FieldSzArrayPrimitiveInt32AsTypeReference:
			case RawPeMutation.FieldMdArrayPrimitiveInt32AsTypeReference:
				byte[] arrayFieldSignature = reader.GetBlobBytes(
					reader.GetFieldDefinition(definition.GetFields().Single()).Signature);
				byte[] expectedArrayFieldSignature = mutation switch
				{
					RawPeMutation.FieldSzArray => [0x06, 0x1d, 0x08],
					RawPeMutation.FieldMdArrayRankOne or
						RawPeMutation.FieldSzArrayAsMdRankOne => [0x06, 0x14, 0x08, 0x01, 0x00, 0x00],
					RawPeMutation.FieldMdArrayExplicitSize => [0x06, 0x14, 0x08, 0x01, 0x01, 0x04, 0x00],
					RawPeMutation.FieldMdArrayLowerBound => [0x06, 0x14, 0x08, 0x01, 0x00, 0x01, 0x00],
					RawPeMutation.FieldSzArrayPrimitiveInt32AsTypeReference => ConcatBytes(
						[0x06, 0x1d, 0x11], EncodeTypeDefOrRef(Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
							.Select(MetadataTokens.TypeReferenceHandle)
							.Single(handle =>
								reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
								reader.GetString(reader.GetTypeReference(handle).Name) == "Int32"))),
					RawPeMutation.FieldMdArrayPrimitiveInt32AsTypeReference => ConcatBytes(
						[0x06, 0x14, 0x11], EncodeTypeDefOrRef(Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
							.Select(MetadataTokens.TypeReferenceHandle)
							.Single(handle =>
								reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
								reader.GetString(reader.GetTypeReference(handle).Name) == "Int32")),
						[0x01, 0x00, 0x00]),
					_ => throw new InvalidOperationException(mutation.ToString()),
				};
				Assert.Equal(
					Convert.ToHexString(expectedArrayFieldSignature),
					Convert.ToHexString(arrayFieldSignature));
				break;
			case RawPeMutation.LocalTypeDefinitionAlias:
				Assert.Equal(HandleKind.TypeDefinition, definition.BaseType.Kind);
				TypeDefinition localAliasDefinition = reader.GetTypeDefinition((TypeDefinitionHandle)definition.BaseType);
				Assert.Equal("System", reader.GetString(localAliasDefinition.Namespace));
				Assert.Equal("Object", reader.GetString(localAliasDefinition.Name));
				Assert.Equal(HandleKind.TypeReference, localAliasDefinition.BaseType.Kind);
				break;
			case RawPeMutation.AssemblyReferenceTypeAlias:
			case RawPeMutation.AssemblyReferenceCrossApproved:
			case RawPeMutation.AssemblyReferenceWrongVersion:
			case RawPeMutation.AssemblyReferenceWrongCulture:
			case RawPeMutation.AssemblyReferenceLiteralNeutralCulture:
			case RawPeMutation.AssemblyReferenceWrongToken:
			case RawPeMutation.AssemblyReferencePublicKey:
			case RawPeMutation.AssemblyReferenceRetargetable:
			case RawPeMutation.AssemblyReferenceWindowsRuntime:
			case RawPeMutation.AssemblyReferenceHash:
			case RawPeMutation.ModuleReferenceTypeScope:
			case RawPeMutation.ModuleDefinitionTypeScope:
			case RawPeMutation.TypeReferenceScopeCycle:
			case RawPeMutation.TypeReferenceScopeUnresolved:
			case RawPeMutation.TypeReferenceUnexpectedScope:
				Assert.Equal(HandleKind.TypeReference, definition.BaseType.Kind);
				TypeReference scopedReference = reader.GetTypeReference((TypeReferenceHandle)definition.BaseType);
				Assert.Equal(
					mutation is RawPeMutation.TypeReferenceScopeCycle or RawPeMutation.TypeReferenceScopeUnresolved
						? string.Empty
						: "System",
					reader.GetString(scopedReference.Namespace));
				Assert.Equal("Object", reader.GetString(scopedReference.Name));
				EntityHandle resolutionScope = scopedReference.ResolutionScope;
				if (mutation is RawPeMutation.AssemblyReferenceTypeAlias or
					RawPeMutation.AssemblyReferenceCrossApproved or
					RawPeMutation.AssemblyReferenceWrongVersion or
					RawPeMutation.AssemblyReferenceWrongCulture or
					RawPeMutation.AssemblyReferenceLiteralNeutralCulture or
					RawPeMutation.AssemblyReferenceWrongToken or
					RawPeMutation.AssemblyReferencePublicKey or
					RawPeMutation.AssemblyReferenceRetargetable or
					RawPeMutation.AssemblyReferenceWindowsRuntime or
					RawPeMutation.AssemblyReferenceHash)
				{
					Assert.Equal(HandleKind.AssemblyReference, resolutionScope.Kind);
					AssemblyReference aliasAssembly = reader.GetAssemblyReference((AssemblyReferenceHandle)resolutionScope);
					Assert.Equal(
						mutation switch
						{
							RawPeMutation.AssemblyReferenceTypeAlias => "System.Arbitrary",
							RawPeMutation.AssemblyReferenceCrossApproved => "System.Collections",
							_ => "System.Runtime",
						},
						reader.GetString(aliasAssembly.Name));
					Assert.Equal(
						mutation == RawPeMutation.AssemblyReferenceWrongVersion
							? new Version(9, 0, 0, 0)
							: new Version(10, 0, 0, 0),
						aliasAssembly.Version);
					Assert.Equal(
						mutation switch
						{
							RawPeMutation.AssemblyReferenceWrongCulture => "zz",
							RawPeMutation.AssemblyReferenceLiteralNeutralCulture => "neutral",
							_ => string.Empty,
						},
						reader.GetString(aliasAssembly.Culture));
					Assert.Equal(
						mutation == RawPeMutation.AssemblyReferenceWrongToken
							? "0102030405060708"
							: "b03f5f7f11d50a3a",
						Convert.ToHexString(reader.GetBlobBytes(aliasAssembly.PublicKeyOrToken)).ToLowerInvariant());
					Assert.Equal(
						mutation switch
						{
							RawPeMutation.AssemblyReferencePublicKey => AssemblyFlags.PublicKey,
							RawPeMutation.AssemblyReferenceRetargetable => AssemblyFlags.Retargetable,
							RawPeMutation.AssemblyReferenceWindowsRuntime => AssemblyFlags.WindowsRuntime,
							_ => default,
						},
						aliasAssembly.Flags);
					Assert.Equal(
						mutation == RawPeMutation.AssemblyReferenceHash ? "a1b2c3d4" : string.Empty,
						Convert.ToHexString(reader.GetBlobBytes(aliasAssembly.HashValue)).ToLowerInvariant());
				}
				else if (mutation == RawPeMutation.ModuleReferenceTypeScope)
				{
					Assert.Equal(HandleKind.ModuleReference, resolutionScope.Kind);
				}
				else if (mutation == RawPeMutation.ModuleDefinitionTypeScope)
				{
					Assert.Equal(HandleKind.ModuleDefinition, resolutionScope.Kind);
					Assert.Equal(1, MetadataTokens.GetRowNumber(resolutionScope));
				}
				else if (mutation == RawPeMutation.TypeReferenceScopeCycle)
				{
					Assert.Equal(HandleKind.TypeReference, resolutionScope.Kind);
					Assert.Equal(
						definition.BaseType,
						reader.GetTypeReference((TypeReferenceHandle)resolutionScope).ResolutionScope);
				}
				else if (mutation == RawPeMutation.TypeReferenceScopeUnresolved)
				{
					Assert.Equal(HandleKind.TypeReference, resolutionScope.Kind);
					Assert.Equal(63, MetadataTokens.GetRowNumber(resolutionScope));
				}
				else
				{
					Assert.True(resolutionScope.IsNil);
				}
				break;
			case RawPeMutation.NestedTypeReferenceScope:
				byte[] nestedTypeMethodSignature = reader.GetBlobBytes(
					reader.GetMethodDefinition(subjectMethodHandle).Signature);
				var nestedTypeHandles = new List<EntityHandle>();
				var nestedTypeSignatureViolations = new HashSet<string>(StringComparer.Ordinal);
				var nestedTypeSignatureCursor = new RawSignatureCursor(
					nestedTypeMethodSignature,
					nestedTypeSignatureViolations,
					nestedTypeHandles.Add);
				nestedTypeSignatureCursor.ConsumeMethod(
					new RawSignaturePolicy(RawSignatureKind.Method, false, 0, 0));
				Assert.True(nestedTypeSignatureCursor.AtEnd);
				Assert.Empty(nestedTypeSignatureViolations);
				EntityHandle nestedTypeHandle = Assert.Single(nestedTypeHandles);
				Assert.Equal(HandleKind.TypeReference, nestedTypeHandle.Kind);
				TypeReference nestedTypeReference = reader.GetTypeReference((TypeReferenceHandle)nestedTypeHandle);
				Assert.Equal(string.Empty, reader.GetString(nestedTypeReference.Namespace));
				Assert.Equal("SpecialFolder", reader.GetString(nestedTypeReference.Name));
				Assert.Equal(HandleKind.TypeReference, nestedTypeReference.ResolutionScope.Kind);
				TypeReference nestedDeclaringType = reader.GetTypeReference(
					(TypeReferenceHandle)nestedTypeReference.ResolutionScope);
				Assert.Equal("System", reader.GetString(nestedDeclaringType.Namespace));
				Assert.Equal("Environment", reader.GetString(nestedDeclaringType.Name));
				Assert.Equal(HandleKind.AssemblyReference, nestedDeclaringType.ResolutionScope.Kind);
				Assert.Equal(
					Convert.ToHexString(ConcatBytes([0x00, 0x00, 0x11], EncodeTypeDefOrRef(nestedTypeHandle))),
					Convert.ToHexString(nestedTypeMethodSignature));
				break;
			case RawPeMutation.NestedTypeDefinitionScope:
			case RawPeMutation.TypeDefinitionScopeCycle:
			case RawPeMutation.TypeDefinitionScopeUnresolved:
			case RawPeMutation.TypeDefinitionUnexpectedScope:
				TypeDefinitionHandle declaringType = definition.GetDeclaringType();
				Assert.False(declaringType.IsNil);
				if (mutation == RawPeMutation.TypeDefinitionScopeUnresolved)
				{
					Assert.Equal(63, MetadataTokens.GetRowNumber(declaringType));
					break;
				}
				TypeDefinition enclosingDefinition = reader.GetTypeDefinition(declaringType);
				Assert.Equal(string.Empty, reader.GetString(definition.Namespace));
				if (mutation == RawPeMutation.TypeDefinitionScopeCycle)
				{
					Assert.Equal(FindType(reader, "RawFixture", "ObservationBatchFixture"), enclosingDefinition.GetDeclaringType());
				}
				else
				{
					Assert.True(enclosingDefinition.GetDeclaringType().IsNil);
					Assert.Equal("RawFixture", reader.GetString(enclosingDefinition.Namespace));
					Assert.Equal("OuterFixture", reader.GetString(enclosingDefinition.Name));
				}
				Assert.Equal(
					mutation == RawPeMutation.TypeDefinitionUnexpectedScope
						? TypeAttributes.Public
						: TypeAttributes.NestedPublic,
					definition.Attributes & TypeAttributes.VisibilityMask);
				break;
			case RawPeMutation.InterfaceTypeSpecModifier:
				{
					InterfaceImplementation implementation = reader.GetInterfaceImplementation(
						definition.GetInterfaceImplementations().Single());
					Assert.Equal(HandleKind.TypeSpecification, implementation.Interface.Kind);
					byte[] signature = reader.GetBlobBytes(
						reader.GetTypeSpecification((TypeSpecificationHandle)implementation.Interface).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[],
						ConcatBytes(
							[0x15, 0x12],
							EncodeTypeDefOrRef(FindRawTypeReference(reader, "System", "IEquatable`1")),
							[0x01, 0x12],
							EncodeTypeDefOrRef(subjectTypeHandle)),
						forbiddenProductType,
							cursor => cursor.ConsumeType(allowVoid: false));
					Assert.Equal(
						[
							(forbiddenProductType, (byte)0),
						(FindRawTypeReference(reader, "System", "IEquatable`1"), (byte)0x12),
						(subjectTypeHandle, (byte)0x12),
					],
						types);
					break;
				}
			case RawPeMutation.MemberReferenceParentModifier:
				{
					MemberReference member = reader.GetMemberReference(FindMemberReference(reader, "GenericTarget"));
					Assert.Equal(HandleKind.TypeSpecification, member.Parent.Kind);
					byte[] signature = reader.GetBlobBytes(
						reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[],
						ConcatBytes(
							[0x12],
							EncodeTypeDefOrRef(FindRawTypeReference(reader, "System", "Object"))),
						forbiddenProductType,
							cursor => cursor.ConsumeType(allowVoid: false));
					Assert.Equal(
						[(forbiddenProductType, (byte)0), (FindRawTypeReference(reader, "System", "Object"), (byte)0x12)],
						types);
					break;
				}
			case RawPeMutation.FieldModifier:
				{
					byte[] signature = reader.GetBlobBytes(
						reader.GetFieldDefinition(definition.GetFields().Single()).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[0x06],
						[0x08],
						forbiddenProductType,
							cursor => cursor.ConsumeField());
					Assert.Equal([(forbiddenProductType, (byte)0)], types);
					break;
				}
			case RawPeMutation.PropertyModifier:
				{
					byte[] signature = reader.GetBlobBytes(
						reader.GetPropertyDefinition(definition.GetProperties().Single()).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[0x28, 0x00],
						[0x08],
						forbiddenProductType,
							cursor => cursor.ConsumeProperty(
								new RawSignaturePolicy(RawSignatureKind.Property, true, 0, 0)));
					Assert.Equal([(forbiddenProductType, (byte)0)], types);
					break;
				}
			case RawPeMutation.MalformedPropertyHeader:
			case RawPeMutation.GenericPropertyHeader:
			case RawPeMutation.ReservedPropertyHeader:
				Assert.NotEqual(
					Convert.ToHexString(new byte[] { 0x28, 0x00, 0x08 }),
					Convert.ToHexString(reader.GetBlobBytes(reader.GetPropertyDefinition(definition.GetProperties().Single()).Signature)));
				break;
			case RawPeMutation.LocalModifier:
			case RawPeMutation.LocalModifierInt64:
				{
					MethodDefinition localMethod = reader.GetMethodDefinition(subjectMethodHandle);
					MethodBodyBlock body = peReader.GetMethodBody(localMethod.RelativeVirtualAddress);
					byte[] signature = reader.GetBlobBytes(reader.GetStandaloneSignature(body.LocalSignature).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[0x07, 0x01],
						mutation == RawPeMutation.LocalModifier ? [0x08] : [0x0a],
						forbiddenProductType,
							cursor => cursor.ConsumeLocals(1));
					Assert.Equal([(forbiddenProductType, (byte)0)], types);
					break;
				}
			case RawPeMutation.MethodModifier:
				{
					byte[] signature = reader.GetBlobBytes(reader.GetMethodDefinition(subjectMethodHandle).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[0x00, 0x00],
						[0x01],
						forbiddenProductType,
							cursor => cursor.ConsumeMethod(
								new RawSignaturePolicy(RawSignatureKind.Method, false, 0, 0)));
					Assert.Equal([(forbiddenProductType, (byte)0)], types);
					break;
				}
			case RawPeMutation.UnmanagedMethodDefinition:
			case RawPeMutation.ReservedMethodHeader:
			case RawPeMutation.ZeroArityGenericBitMethodDefinition:
			case RawPeMutation.SelfConsistentGenericMethodDefinition:
			case RawPeMutation.VarArgsMethodDefinition:
			case RawPeMutation.InstanceMethodDefinition:
			case RawPeMutation.ExplicitThisMethodDefinition:
				byte[] methodSignature = reader.GetBlobBytes(reader.GetMethodDefinition(subjectMethodHandle).Signature);
				Assert.NotEqual(
					Convert.ToHexString(new byte[] { 0x00, 0x00, 0x01 }),
					Convert.ToHexString(methodSignature));
				if (mutation == RawPeMutation.InstanceMethodDefinition)
				{
					Assert.Equal(Convert.ToHexString(new byte[] { 0x20, 0x00, 0x01 }), Convert.ToHexString(methodSignature));
				}
				if (mutation == RawPeMutation.ExplicitThisMethodDefinition)
				{
					Assert.Equal(Convert.ToHexString(new byte[] { 0x60, 0x00, 0x01 }), Convert.ToHexString(methodSignature));
				}
				if (mutation is RawPeMutation.InstanceMethodDefinition or RawPeMutation.ExplicitThisMethodDefinition)
				{
					MethodDefinition instanceMethod = reader.GetMethodDefinition(subjectMethodHandle);
					Assert.Equal(MethodAttributes.Public | MethodAttributes.HideBySig, instanceMethod.Attributes);
					BlobReader signatureBlob = reader.GetBlobReader(instanceMethod.Signature);
					MethodSignature<string> decoded = new SignatureDecoder<string, object?>(
						new ModifierRejectingTypeProvider(reader),
						reader,
						null).DecodeMethodSignature(ref signatureBlob);
					Assert.True(decoded.Header.IsInstance);
					Assert.Equal(mutation == RawPeMutation.ExplicitThisMethodDefinition, decoded.Header.HasExplicitThis);
					Assert.Equal(0, signatureBlob.RemainingBytes);
				}
				if (mutation == RawPeMutation.SelfConsistentGenericMethodDefinition)
				{
					MethodDefinition selfConsistent = reader.GetMethodDefinition(subjectMethodHandle);
					Assert.Single(selfConsistent.GetGenericParameters());
					Assert.Equal(
						Convert.ToHexString(new byte[] { 0x10, 0x01, 0x00, 0x01 }),
						Convert.ToHexString(reader.GetBlobBytes(selfConsistent.Signature)));
				}
				break;
			case RawPeMutation.ClassLayout:
				TypeLayout layout = definition.GetLayout();
				Assert.Equal(TypeAttributes.SequentialLayout, definition.Attributes & TypeAttributes.LayoutMask);
				Assert.Equal(4, layout.PackingSize);
				Assert.Equal(8, layout.Size);
				break;
			case RawPeMutation.LiteralFieldDefinition:
			case RawPeMutation.MutatedLiteralField:
			case RawPeMutation.NotSerializedField:
			case RawPeMutation.MarshaledField:
				FieldDefinition controlledField = reader.GetFieldDefinition(definition.GetFields().Single());
				if (mutation is RawPeMutation.LiteralFieldDefinition or RawPeMutation.MutatedLiteralField)
				{
					Assert.Equal(
						FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
						controlledField.Attributes);
					Assert.Equal(mutation == RawPeMutation.MutatedLiteralField ? 2 : 1,
						ReadRawConstant(reader, controlledField.GetDefaultValue()));
				}
				if (mutation == RawPeMutation.NotSerializedField)
				{
					Assert.NotEqual((FieldAttributes)0, controlledField.Attributes & (FieldAttributes)0x00000080);
				}
				if (mutation == RawPeMutation.MarshaledField)
				{
					Assert.NotEqual((FieldAttributes)0, controlledField.Attributes & FieldAttributes.HasFieldMarshal);
					Assert.Equal(new byte[] { 0x07 }, reader.GetBlobBytes(controlledField.GetMarshallingDescriptor()));
				}
				break;
			case RawPeMutation.SynchronizedMethod:
				Assert.NotEqual(
					(MethodImplAttributes)0,
					reader.GetMethodDefinition(subjectMethodHandle).ImplAttributes & MethodImplAttributes.Synchronized);
				break;
			case RawPeMutation.ParameterizedMethodDefinition:
			case RawPeMutation.WrongParameterName:
			case RawPeMutation.OptionalParameter:
			case RawPeMutation.DefaultParameter:
			case RawPeMutation.MarshaledParameter:
				Parameter controlledParameter = reader.GetParameter(
					reader.GetMethodDefinition(subjectMethodHandle).GetParameters().Single());
				Assert.Equal(1, controlledParameter.SequenceNumber);
				Assert.Equal(mutation == RawPeMutation.WrongParameterName ? "other" : "value", reader.GetString(controlledParameter.Name));
				if (mutation == RawPeMutation.OptionalParameter)
				{
					Assert.NotEqual((ParameterAttributes)0, controlledParameter.Attributes & ParameterAttributes.Optional);
				}
				if (mutation == RawPeMutation.DefaultParameter)
				{
					Assert.Equal(7, ReadRawConstant(reader, controlledParameter.GetDefaultValue()));
				}
				if (mutation == RawPeMutation.MarshaledParameter)
				{
					Assert.Equal(new byte[] { 0x07 }, reader.GetBlobBytes(controlledParameter.GetMarshallingDescriptor()));
				}
				break;
			case RawPeMutation.ReturnParameterDefinition:
			case RawPeMutation.WrongReturnParameterName:
			case RawPeMutation.OptionalReturnParameter:
			case RawPeMutation.DefaultReturnParameter:
			case RawPeMutation.MarshaledReturnParameter:
				Parameter controlledReturn = reader.GetParameter(
					reader.GetMethodDefinition(subjectMethodHandle).GetParameters().Single());
				Assert.Equal(0, controlledReturn.SequenceNumber);
				Assert.Equal(
					mutation == RawPeMutation.WrongReturnParameterName ? "result" : string.Empty,
					reader.GetString(controlledReturn.Name));
				if (mutation == RawPeMutation.OptionalReturnParameter)
				{
					Assert.NotEqual((ParameterAttributes)0, controlledReturn.Attributes & ParameterAttributes.Optional);
				}
				if (mutation == RawPeMutation.DefaultReturnParameter)
				{
					Assert.Equal(7, ReadRawConstant(reader, controlledReturn.GetDefaultValue()));
				}
				if (mutation == RawPeMutation.MarshaledReturnParameter)
				{
					Assert.Equal(new byte[] { 0x07 }, reader.GetBlobBytes(controlledReturn.GetMarshallingDescriptor()));
				}
				break;
			case RawPeMutation.UnexpectedPropertyAttributes:
			case RawPeMutation.MissingPropertyGetter:
			case RawPeMutation.WrongPropertyGetter:
			case RawPeMutation.SetterPropertySemantics:
			case RawPeMutation.OtherPropertySemantics:
				PropertyDefinition controlledProperty = reader.GetPropertyDefinition(definition.GetProperties().Single());
				PropertyAccessors controlledAccessors = controlledProperty.GetAccessors();
				if (mutation == RawPeMutation.UnexpectedPropertyAttributes)
				{
					Assert.Equal(PropertyAttributes.SpecialName, controlledProperty.Attributes);
				}
				if (mutation == RawPeMutation.MissingPropertyGetter)
				{
					Assert.True(controlledAccessors.Getter.IsNil);
				}
				if (mutation == RawPeMutation.WrongPropertyGetter)
				{
					Assert.Equal(subjectMethodHandle, controlledAccessors.Getter);
				}
				if (mutation == RawPeMutation.SetterPropertySemantics)
				{
					Assert.False(controlledAccessors.Setter.IsNil);
				}
				if (mutation == RawPeMutation.OtherPropertySemantics)
				{
					Assert.Single(controlledAccessors.Others);
				}
				break;
			case RawPeMutation.MethodGenericConstraintObject:
			case RawPeMutation.MethodGenericConstraintTypeSpecObject:
			case RawPeMutation.MethodGenericConstraintForbidden:
			case RawPeMutation.MethodGenericConstraintModifier:
			case RawPeMutation.MethodGenericConstraintUnresolved:
			case RawPeMutation.MethodGenericConstraintCycle:
			case RawPeMutation.MethodGenericConstraintTrailingData:
				GenericParameter methodParameter = reader.GetGenericParameter(
					reader.GetMethodDefinition(subjectMethodHandle).GetGenericParameters().Single());
				GenericParameterConstraint methodConstraint = reader.GetGenericParameterConstraint(
					methodParameter.GetConstraints().Single());
				Assert.False(methodConstraint.Type.IsNil);
				if (mutation == RawPeMutation.MethodGenericConstraintModifier)
				{
					Assert.Equal(HandleKind.TypeSpecification, methodConstraint.Type.Kind);
					byte[] signature = reader.GetBlobBytes(
						reader.GetTypeSpecification((TypeSpecificationHandle)methodConstraint.Type).Signature);
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[],
						ConcatBytes(
							[0x12],
							EncodeTypeDefOrRef(FindRawTypeReference(reader, "System", "Object"))),
						forbiddenProductType,
						cursor => cursor.ConsumeType(allowVoid: false));
					Assert.Equal(
						new (EntityHandle Handle, byte RawTypeKind)[]
						{
							(forbiddenProductType, 0),
							(FindRawTypeReference(reader, "System", "Object"), 0x12),
						},
						types);
				}
				else if (mutation is RawPeMutation.MethodGenericConstraintTypeSpecObject or
					RawPeMutation.MethodGenericConstraintTrailingData)
				{
					Assert.Equal(HandleKind.TypeSpecification, methodConstraint.Type.Kind);
					byte[] typeSpec = reader.GetBlobBytes(
						reader.GetTypeSpecification((TypeSpecificationHandle)methodConstraint.Type).Signature);
					byte[] validSignature = mutation == RawPeMutation.MethodGenericConstraintTrailingData
						? typeSpec[..^1]
						: typeSpec;
					var rawViolations = new HashSet<string>(StringComparer.Ordinal);
					var cursor = new RawSignatureCursor(validSignature, rawViolations);
					cursor.ConsumeType(allowVoid: false);
					Assert.True(cursor.AtEnd);
					Assert.Empty(rawViolations);
					if (mutation == RawPeMutation.MethodGenericConstraintTrailingData)
					{
						Assert.Equal((byte)0x00, typeSpec[^1]);
						var trailingCursor = new RawSignatureCursor(typeSpec, new HashSet<string>(StringComparer.Ordinal));
						trailingCursor.ConsumeType(allowVoid: false);
						Assert.False(trailingCursor.AtEnd);
					}
				}
				break;
			case RawPeMutation.MissingGenericBitMethodDefinition:
				Assert.Single(reader.GetMethodDefinition(subjectMethodHandle).GetGenericParameters());
				Assert.Equal(
					Convert.ToHexString(new byte[] { 0x00, 0x00, 0x01 }),
					Convert.ToHexString(reader.GetBlobBytes(reader.GetMethodDefinition(subjectMethodHandle).Signature)));
				break;
			case RawPeMutation.MethodMemberReferenceModifier:
				{
					byte[] signature = MemberReferenceSignature(reader, "GenericTarget");
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[0x30, 0x01, 0x01, 0x01],
						[0x1e, 0x00],
						forbiddenProductType,
							cursor => cursor.ConsumeMethod(
								new RawSignaturePolicy(RawSignatureKind.Method, true, 1, 1)));
					Assert.Equal([(forbiddenProductType, (byte)0)], types);
					break;
				}
			case RawPeMutation.UnmanagedMethodMemberReference:
			case RawPeMutation.UnauthorizedMethodMemberReferenceType:
			case RawPeMutation.UnauthorizedMethodMemberReferenceReturnType:
				Assert.NotEqual(
					Convert.ToHexString(new byte[] { 0x30, 0x01, 0x01, 0x01, 0x1e, 0x00 }),
					Convert.ToHexString(MemberReferenceSignature(reader, "GenericTarget")));
				break;
			case RawPeMutation.MethodMemberReferenceTypeSpecObject:
			case RawPeMutation.MethodMemberReferenceTypeSpecObjectAsValueType:
				byte[] typeSpecMethodMemberSignature = MemberReferenceSignature(reader, "TypeSpecMethodTarget");
				var typeSpecMethodMemberTypes = new List<(EntityHandle Handle, byte RawTypeKind)>();
				var typeSpecMethodMemberViolations = new HashSet<string>(StringComparer.Ordinal);
				var typeSpecMethodMemberCursor = new RawSignatureCursor(
					typeSpecMethodMemberSignature,
					typeSpecMethodMemberViolations,
					typeReferenceVisitor: null,
					signatureTypeReferenceVisitor: (handle, rawTypeKind) =>
						typeSpecMethodMemberTypes.Add((handle, rawTypeKind)));
				typeSpecMethodMemberCursor.ConsumeMethod(
					new RawSignaturePolicy(RawSignatureKind.Method, true, 1, 0));
				Assert.True(typeSpecMethodMemberCursor.AtEnd);
				Assert.Empty(typeSpecMethodMemberViolations);
				(EntityHandle typeSpecMethodMemberHandle, byte typeSpecMethodMemberKind) =
					Assert.Single(typeSpecMethodMemberTypes);
				Assert.Equal(HandleKind.TypeSpecification, typeSpecMethodMemberHandle.Kind);
				Assert.Equal(
					mutation == RawPeMutation.MethodMemberReferenceTypeSpecObjectAsValueType ? (byte)0x11 : (byte)0x12,
					typeSpecMethodMemberKind);
				TypeReferenceHandle typeSpecMethodMemberType = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.Single(handle =>
						reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
						reader.GetString(reader.GetTypeReference(handle).Name) == "Type");
				Assert.Equal(
					Convert.ToHexString(ConcatBytes([0x12], EncodeTypeDefOrRef(typeSpecMethodMemberType))),
					Convert.ToHexString(reader.GetBlobBytes(reader.GetTypeSpecification(
						(TypeSpecificationHandle)typeSpecMethodMemberHandle).Signature)));
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x30, 0x01, 0x00, typeSpecMethodMemberKind],
						EncodeTypeDefOrRef(typeSpecMethodMemberHandle))),
					Convert.ToHexString(typeSpecMethodMemberSignature));
				break;
			case RawPeMutation.MethodSpecificationNonGenericParent:
			case RawPeMutation.MethodSpecificationZeroArityGenericParent:
				Assert.NotEqual(
					Convert.ToHexString(new byte[] { 0x30, 0x01, 0x01, 0x01, 0x1e, 0x00 }),
					Convert.ToHexString(MemberReferenceSignature(reader, "GenericTarget")));
				Assert.Equal(
					Convert.ToHexString(new byte[] { 0x0a, 0x00 }),
					Convert.ToHexString(MethodSpecificationSignature(reader)));
				break;
			case RawPeMutation.UnexpectedMethodAttributes:
				Assert.Equal(
					MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
					reader.GetMethodDefinition(subjectMethodHandle).Attributes);
				break;
			case RawPeMutation.FieldMemberReferenceModifier:
				{
					byte[] signature = MemberReferenceSignature(reader, "FieldTarget");
					IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> types = AssertRequiredModifierSignature(
						signature,
						[0x06],
						[0x08],
						forbiddenProductType,
							cursor => cursor.ConsumeField());
					Assert.Equal([(forbiddenProductType, (byte)0)], types);
					break;
				}
			case RawPeMutation.MethodBitsOnFieldMemberReference:
			case RawPeMutation.UnauthorizedFieldMemberReferenceType:
			case RawPeMutation.FieldMemberReferenceIntAsClass:
				Assert.NotEqual(
					Convert.ToHexString(new byte[] { 0x06, 0x08 }),
					Convert.ToHexString(MemberReferenceSignature(reader, "FieldTarget")));
				if (mutation == RawPeMutation.FieldMemberReferenceIntAsClass)
				{
					byte[] signature = MemberReferenceSignature(reader, "FieldTarget");
					Assert.True(signature.Length >= 2);
					Assert.Equal((byte)0x12, signature[1]);
				}
				break;
			case RawPeMutation.FieldMemberReferenceTypeSpecInt32:
			case RawPeMutation.FieldMemberReferenceTypeSpecInt32AsClass:
				byte[] typeSpecFieldMemberSignature = MemberReferenceSignature(reader, "TypeSpecFieldTarget");
				var typeSpecFieldMemberTypes = new List<(EntityHandle Handle, byte RawTypeKind)>();
				var typeSpecFieldMemberViolations = new HashSet<string>(StringComparer.Ordinal);
				var typeSpecFieldMemberCursor = new RawSignatureCursor(
					typeSpecFieldMemberSignature,
					typeSpecFieldMemberViolations,
					typeReferenceVisitor: null,
					signatureTypeReferenceVisitor: (handle, rawTypeKind) =>
						typeSpecFieldMemberTypes.Add((handle, rawTypeKind)));
				typeSpecFieldMemberCursor.ConsumeField();
				Assert.True(typeSpecFieldMemberCursor.AtEnd);
				Assert.Empty(typeSpecFieldMemberViolations);
				(EntityHandle typeSpecFieldMemberHandle, byte typeSpecFieldMemberKind) = Assert.Single(typeSpecFieldMemberTypes);
				Assert.Equal(HandleKind.TypeSpecification, typeSpecFieldMemberHandle.Kind);
				Assert.Equal(
					mutation == RawPeMutation.FieldMemberReferenceTypeSpecInt32AsClass ? (byte)0x12 : (byte)0x11,
					typeSpecFieldMemberKind);
				TypeReferenceHandle typeSpecFieldMemberType = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.Single(handle =>
						reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
						reader.GetString(reader.GetTypeReference(handle).Name) == "Type");
				Assert.Equal(
					Convert.ToHexString(ConcatBytes([0x12], EncodeTypeDefOrRef(typeSpecFieldMemberType))),
					Convert.ToHexString(reader.GetBlobBytes(reader.GetTypeSpecification(
						(TypeSpecificationHandle)typeSpecFieldMemberHandle).Signature)));
				Assert.Equal(
					Convert.ToHexString(ConcatBytes(
						[0x06, typeSpecFieldMemberKind], EncodeTypeDefOrRef(typeSpecFieldMemberHandle))),
					Convert.ToHexString(typeSpecFieldMemberSignature));
				break;
			case RawPeMutation.WrongInterfaceNullableArgument:
				{
					InterfaceImplementation implementation = reader.GetInterfaceImplementation(
						definition.GetInterfaceImplementations().Single());
					CustomAttribute attribute = reader.GetCustomAttribute(implementation.GetCustomAttributes().Single());
					Assert.Equal(RawNullableInterfaceAttribute.ConstructorKey, RawAttributeConstructorKey(reader, attribute.Constructor));
					Assert.False(reader.GetBlobBytes(attribute.Value).AsSpan().SequenceEqual(RawNullableInterfaceAttribute.Blob));
					break;
				}
			case RawPeMutation.WrongInterfaceAttributeConstructor:
				{
					InterfaceImplementation implementation = reader.GetInterfaceImplementation(
						definition.GetInterfaceImplementations().Single());
					CustomAttribute attribute = reader.GetCustomAttribute(implementation.GetCustomAttributes().Single());
					Assert.Equal("RawFixture.TypeCarrierAttribute::.ctor", RawAttributeConstructorKey(reader, attribute.Constructor));
					Assert.True(reader.GetBlobBytes(attribute.Value).AsSpan().SequenceEqual(RawNullableInterfaceAttribute.Blob));
					MemberReference constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
					Assert.Equal(
						"2001011D05",
						Convert.ToHexString(reader.GetBlobBytes(constructor.Signature)));
					break;
				}
			case RawPeMutation.TypeCarryingInterfaceAttribute:
			case RawPeMutation.TypeCarryingInterfaceNamedAttribute:
			case RawPeMutation.TypeCarryingInterfaceArrayAttribute:
				{
					InterfaceImplementation implementation = reader.GetInterfaceImplementation(
						definition.GetInterfaceImplementations().Single());
					CustomAttribute attribute = reader.GetCustomAttribute(implementation.GetCustomAttributes().Single());
					Assert.Equal("RawFixture.TypeCarrierAttribute::.ctor", RawAttributeConstructorKey(reader, attribute.Constructor));
					var provider = new ModifierRejectingTypeProvider(reader);
					CustomAttributeValue<string> value = attribute.DecodeValue(provider);
					string expectedForbidden = $"[serialized:{typeof(LiquidWalletState).AssemblyQualifiedName}]";
					if (mutation == RawPeMutation.TypeCarryingInterfaceAttribute)
					{
						System.Reflection.Metadata.CustomAttributeTypedArgument<string> argument = Assert.Single(value.FixedArguments);
						Assert.Equal(RawEntityTypeKey(typeof(Type)), argument.Type);
						Assert.Equal(expectedForbidden, Assert.IsType<string>(argument.Value));
						Assert.Empty(value.NamedArguments);
					}
					else if (mutation == RawPeMutation.TypeCarryingInterfaceNamedAttribute)
					{
						System.Reflection.Metadata.CustomAttributeTypedArgument<string> fixedArgument = Assert.Single(value.FixedArguments);
						Assert.Equal(RawEntityTypeKey(typeof(Type)), fixedArgument.Type);
						Assert.Equal("$OBSERVATION", Assert.IsType<string>(fixedArgument.Value));
						CustomAttributeNamedArgument<string> namedArgument = Assert.Single(value.NamedArguments);
						Assert.Equal(CustomAttributeNamedArgumentKind.Property, namedArgument.Kind);
						Assert.Equal(nameof(TypeCarrierAttribute.Target), namedArgument.Name);
						Assert.Equal(RawTypeKey(typeof(Type)), namedArgument.Type);
						Assert.Equal(expectedForbidden, Assert.IsType<string>(namedArgument.Value));
					}
					else
					{
						System.Reflection.Metadata.CustomAttributeTypedArgument<string> arrayArgument = Assert.Single(value.FixedArguments);
						Assert.Equal(RawSzArrayKey(RawEntityTypeKey(typeof(Type))), arrayArgument.Type);
						ImmutableArray<System.Reflection.Metadata.CustomAttributeTypedArgument<string>> nested =
							Assert.IsType<ImmutableArray<System.Reflection.Metadata.CustomAttributeTypedArgument<string>>>(arrayArgument.Value);
						System.Reflection.Metadata.CustomAttributeTypedArgument<string> element = Assert.Single(nested);
						Assert.Equal(RawEntityTypeKey(typeof(Type)), element.Type);
						Assert.Equal(expectedForbidden, Assert.IsType<string>(element.Value));
						Assert.Empty(value.NamedArguments);
					}
					break;
				}
			case RawPeMutation.TypeCarryingMethodArrayAttribute:
			case RawPeMutation.TypeCarryingMethodArrayWrongTokenObservationAttribute:
			case RawPeMutation.TypeCarryingMethodExactObservationAttribute:
			case RawPeMutation.TypeCarryingMethodUnqualifiedObservationAttribute:
			case RawPeMutation.TypeCarryingMethodCounterfeitObservationAttribute:
				Assert.Single(reader.GetMethodDefinition(subjectMethodHandle).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingTypeAttribute:
				Assert.Single(definition.GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingFieldAttribute:
				Assert.Single(reader.GetFieldDefinition(definition.GetFields().Single()).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingConstructorAttribute:
				Assert.Single(definition.GetMethods()
					.Select(reader.GetMethodDefinition)
					.Single(value => reader.GetString(value.Name) == ".ctor")
					.GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingReturnAttribute:
				Assert.Single(reader.GetMethodDefinition(subjectMethodHandle).GetParameters());
				Assert.Single(reader.GetParameter(reader.GetMethodDefinition(subjectMethodHandle).GetParameters().Single()).GetCustomAttributes());
				Assert.Equal(0, reader.GetParameter(reader.GetMethodDefinition(subjectMethodHandle).GetParameters().Single()).SequenceNumber);
				break;
			case RawPeMutation.TypeCarryingParameterAttribute:
				Assert.Single(reader.GetMethodDefinition(subjectMethodHandle).GetParameters());
				Assert.Single(reader.GetParameter(reader.GetMethodDefinition(subjectMethodHandle).GetParameters().Single()).GetCustomAttributes());
				Assert.Equal(1, reader.GetParameter(reader.GetMethodDefinition(subjectMethodHandle).GetParameters().Single()).SequenceNumber);
				break;
			case RawPeMutation.TypeCarryingPropertyNamedAttribute:
			case RawPeMutation.TypeCarryingPropertyNamedWrongVersionObservationAttribute:
				Assert.Single(reader.GetPropertyDefinition(definition.GetProperties().Single()).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingEventAttribute:
				Assert.Single(reader.GetEventDefinition(definition.GetEvents().Single()).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingGenericParameterAttribute:
				MethodDefinition attributedGenericMethod = reader.GetMethodDefinition(subjectMethodHandle);
				Assert.Equal(
					Convert.ToHexString(new byte[] { 0x10, 0x01, 0x00, 0x01 }),
					Convert.ToHexString(reader.GetBlobBytes(attributedGenericMethod.Signature)));
				Assert.Single(reader.GetGenericParameter(
					attributedGenericMethod.GetGenericParameters().Single()).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingGenericConstraintAttribute:
				MethodDefinition constrainedGenericMethod = reader.GetMethodDefinition(subjectMethodHandle);
				Assert.Equal(
					Convert.ToHexString(new byte[] { 0x10, 0x01, 0x00, 0x01 }),
					Convert.ToHexString(reader.GetBlobBytes(constrainedGenericMethod.Signature)));
				GenericParameter genericParameter = reader.GetGenericParameter(constrainedGenericMethod.GetGenericParameters().Single());
				Assert.Single(reader.GetGenericParameterConstraint(genericParameter.GetConstraints().Single()).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingStandaloneSignatureAttribute:
				MethodDefinition standaloneMethod = reader.GetMethodDefinition(subjectMethodHandle);
				StandaloneSignatureHandle standalone = peReader.GetMethodBody(standaloneMethod.RelativeVirtualAddress).LocalSignature;
				Assert.Single(reader.GetStandaloneSignature(standalone).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingMemberReferenceAttribute:
				MemberReferenceHandle memberReference = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.MemberRef))
					.Select(MetadataTokens.MemberReferenceHandle)
					.Single(handle => reader.GetString(reader.GetMemberReference(handle).Name) == "GenericTarget");
				Assert.Single(reader.GetMemberReference(memberReference).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingMethodSpecificationAttribute:
				Assert.Single(reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(1)).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingTypeSpecificationAttribute:
				EntityHandle interfaceType = reader.GetInterfaceImplementation(definition.GetInterfaceImplementations().Single()).Interface;
				Assert.Equal(HandleKind.TypeSpecification, interfaceType.Kind);
				Assert.Single(reader.GetTypeSpecification((TypeSpecificationHandle)interfaceType).GetCustomAttributes());
				break;
			case RawPeMutation.TypeCarryingTypeReferenceAttribute:
				TypeReferenceHandle objectType = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
					.Select(MetadataTokens.TypeReferenceHandle)
					.Single(handle =>
						reader.GetString(reader.GetTypeReference(handle).Namespace) == "System" &&
						reader.GetString(reader.GetTypeReference(handle).Name) == "Object");
				Assert.Single(reader.GetCustomAttributes(objectType));
				break;
			case RawPeMutation.TypeCarryingAssemblyReferenceAttribute:
			case RawPeMutation.TypeCarryingModuleReferenceAttribute:
			case RawPeMutation.TypeCarryingModuleDefinitionAttribute:
				Assert.Equal(HandleKind.TypeReference, definition.BaseType.Kind);
				EntityHandle attributedScope = reader.GetTypeReference((TypeReferenceHandle)definition.BaseType).ResolutionScope;
				Assert.Equal(
					mutation switch
					{
						RawPeMutation.TypeCarryingAssemblyReferenceAttribute => HandleKind.AssemblyReference,
						RawPeMutation.TypeCarryingModuleReferenceAttribute => HandleKind.ModuleReference,
						_ => HandleKind.ModuleDefinition,
					},
					attributedScope.Kind);
				Assert.Single(reader.GetCustomAttributes(attributedScope));
				break;
			case RawPeMutation.MethodImplementation:
				Assert.Single(definition.GetMethodImplementations());
				break;
			case RawPeMutation.MalformedMethodSpecificationHeader:
			case RawPeMutation.MethodSpecificationArityMismatch:
			case RawPeMutation.MethodSpecificationTrailingData:
			case RawPeMutation.UnauthorizedMethodSpecificationArgument:
				Assert.NotEqual(
					Convert.ToHexString(new byte[] { 0x0a, 0x01, 0x08 }),
					Convert.ToHexString(MethodSpecificationSignature(reader)));
				break;
		}
	}

	private static TypeReferenceHandle AssertExactForbiddenProductTypeReference(MetadataReader reader)
	{
		TypeReferenceHandle handle = Assert.Single(
			Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
				.Select(MetadataTokens.TypeReferenceHandle),
			candidate => reader.GetString(reader.GetTypeReference(candidate).Namespace)
				.StartsWith("WalletWasabi.Liquid", StringComparison.Ordinal));
		TypeReference reference = reader.GetTypeReference(handle);
		Assert.Equal("WalletWasabi.Liquid.Wallet", reader.GetString(reference.Namespace));
		Assert.Equal("LiquidWalletState", reader.GetString(reference.Name));
		Assert.Equal(HandleKind.AssemblyReference, reference.ResolutionScope.Kind);
		AssemblyReferenceHandle assembly = (AssemblyReferenceHandle)reference.ResolutionScope;
		Assert.Equal(
			ReflectionAssemblyIdentity(typeof(LiquidWalletState).Assembly.GetName()),
			MetadataAssemblyReferenceIdentity(reader, assembly));
		return handle;
	}

	private static TypeReferenceHandle FindRawTypeReference(
		MetadataReader reader,
		string @namespace,
		string name) => Assert.Single(
		Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeRef))
			.Select(MetadataTokens.TypeReferenceHandle),
		handle =>
				reader.GetString(reader.GetTypeReference(handle).Namespace) == @namespace &&
				reader.GetString(reader.GetTypeReference(handle).Name) == name);

	private static IReadOnlyList<(EntityHandle Handle, byte RawTypeKind)> AssertRequiredModifierSignature(
		byte[] signature,
		byte[] expectedPrefix,
		byte[] expectedSuffix,
		TypeReferenceHandle expectedModifier,
		Action<RawSignatureCursor> consume)
	{
		int modifierOffset = expectedPrefix.Length;
		byte[] encodedModifier = EncodeTypeDefOrRef(expectedModifier);
		Assert.Equal(
			Convert.ToHexString(ConcatBytes(expectedPrefix, [0x1f], encodedModifier, expectedSuffix)),
			Convert.ToHexString(signature));
		Assert.Equal((byte)0x1f, signature[modifierOffset]);
		Assert.True(signature.AsSpan(modifierOffset + 1, encodedModifier.Length).SequenceEqual(encodedModifier));
		var referencedTypes = new List<(EntityHandle Handle, byte RawTypeKind)>();
		var violations = new HashSet<string>(StringComparer.Ordinal);
		var cursor = new RawSignatureCursor(
			signature,
			violations,
			typeReferenceVisitor: null,
			signatureTypeReferenceVisitor: (handle, rawTypeKind) =>
				referencedTypes.Add((handle, rawTypeKind)));
		consume(cursor);
		Assert.True(cursor.AtEnd);
		Assert.Equal(["CUSTOM_MODIFIER"], violations);
		Assert.NotEmpty(referencedTypes);
		Assert.Equal((expectedModifier, (byte)0), referencedTypes[0]);
		return referencedTypes;
	}

	private static void VerifyRawTypeSpecificationGraph(
		MetadataReader reader,
		TypeSpecificationHandle handle,
		ISet<TypeSpecificationHandle> active,
		ISet<TypeSpecificationHandle> completed,
		ISet<string> violations,
		Action<EntityHandle>? reachedType = null)
	{
		if (completed.Contains(handle))
		{
			return;
		}
		int row = MetadataTokens.GetRowNumber(handle);
		if (row <= 0 || row > reader.GetTableRowCount(TableIndex.TypeSpec))
		{
			violations.Add("UNRESOLVED_TYPE_ROOT");
			return;
		}
		if (!active.Add(handle))
		{
			violations.Add("TYPE_SPEC_CYCLE");
			return;
		}
		try
		{
			byte[] signature = reader.GetBlobBytes(reader.GetTypeSpecification(handle).Signature);
			var cursor = new RawSignatureCursor(signature, violations, VisitRawEntity);
			cursor.ConsumeType(allowVoid: false);
			if (!cursor.AtEnd)
			{
				violations.Add("TRAILING_DATA");
			}
		}
		catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException)
		{
			violations.Add("MALFORMED_SIGNATURE");
		}
		finally
		{
			active.Remove(handle);
			completed.Add(handle);
		}

		void VisitRawEntity(EntityHandle entity)
		{
			int entityRow = MetadataTokens.GetRowNumber(entity);
			TableIndex table = entity.Kind switch
			{
				HandleKind.TypeDefinition => TableIndex.TypeDef,
				HandleKind.TypeReference => TableIndex.TypeRef,
				HandleKind.TypeSpecification => TableIndex.TypeSpec,
				_ => throw new BadImageFormatException("Invalid TypeDefOrRef coded index."),
			};
			if (entityRow <= 0 || entityRow > reader.GetTableRowCount(table))
			{
				violations.Add("UNRESOLVED_TYPE_ROOT");
				return;
			}
			reachedType?.Invoke(entity);
			if (entity.Kind == HandleKind.TypeSpecification)
			{
				VerifyRawTypeSpecificationGraph(
					reader,
					(TypeSpecificationHandle)entity,
					active,
					completed,
					violations,
					reachedType);
			}
		}
	}

	private static MemberReferenceHandle FindMemberReference(MetadataReader reader, string name) => Assert.Single(
		Enumerable.Range(1, reader.GetTableRowCount(TableIndex.MemberRef))
			.Select(MetadataTokens.MemberReferenceHandle),
		handle => reader.GetString(reader.GetMemberReference(handle).Name) == name);

	private static byte[] MemberReferenceSignature(MetadataReader reader, string name) =>
		reader.GetBlobBytes(reader.GetMemberReference(FindMemberReference(reader, name)).Signature);

	private static byte[] MethodSpecificationSignature(MetadataReader reader)
	{
		Assert.Equal(1, reader.GetTableRowCount(TableIndex.MethodSpec));
		return reader.GetBlobBytes(reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(1)).Signature);
	}

	private static IReadOnlyList<string> VerifyRawSignature(RawSignaturePolicy policy, ReadOnlySpan<byte> bytes)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		var cursor = new RawSignatureCursor(bytes.ToArray(), violations);
		try
		{
			switch (policy.Kind)
			{
				case RawSignatureKind.Method:
					cursor.ConsumeMethod(policy);
					break;
				case RawSignatureKind.Field:
					cursor.ConsumeField();
					break;
				case RawSignatureKind.Property:
					cursor.ConsumeProperty(policy);
					break;
				case RawSignatureKind.Local:
					cursor.ConsumeLocals(policy.ParameterCount);
					break;
				case RawSignatureKind.MethodSpecification:
					cursor.ConsumeMethodSpecification(policy.GenericArity);
					break;
				case RawSignatureKind.Type:
					cursor.ConsumeType(allowVoid: false);
					break;
			}
			if (!cursor.AtEnd)
			{
				violations.Add("TRAILING_DATA");
			}
		}
		catch (InvalidOperationException)
		{
			violations.Add("MALFORMED_SIGNATURE");
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static IReadOnlyList<string> VerifyReflectionMetadata(Type type)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		StructLayoutAttribute layout = type.StructLayoutAttribute!;
		if (layout.Value != LayoutKind.Auto || layout.CharSet != CharSet.Ansi || layout.Pack != 0 || layout.Size != 0)
		{
			violations.Add("CLASS_LAYOUT");
		}
		if ((type.Attributes & (TypeAttributes)0x00002000) != 0 || type.IsImport || type.IsCOMObject)
		{
			violations.Add("TYPE_FLAGS");
		}
		if (type.CustomAttributes.Any(HasForbiddenEmbeddedType))
		{
			violations.Add("CUSTOM_ATTRIBUTE");
		}

		foreach (FieldInfo field in type.GetFields(DeclaredMemberFlags))
		{
			if ((field.Attributes & ((FieldAttributes)0x00000080 | FieldAttributes.HasFieldMarshal)) != 0)
			{
				violations.Add("FIELD_FLAGS");
			}
			if (field.Name == "Value" && field.IsLiteral && !Equals(field.GetRawConstantValue(), 1))
			{
				violations.Add("LITERAL_VALUE");
			}
			if (field.CustomAttributes.Any(HasForbiddenEmbeddedType))
			{
				violations.Add("CUSTOM_ATTRIBUTE");
			}
			if (field.GetRequiredCustomModifiers().Length != 0 || field.GetOptionalCustomModifiers().Length != 0)
			{
				violations.Add("CUSTOM_MODIFIER");
			}
		}

		IEnumerable<MethodBase> callables = type.GetMethods(DeclaredMemberFlags).Cast<MethodBase>()
			.Concat(type.GetConstructors(DeclaredMemberFlags));
		foreach (MethodBase callable in callables)
		{
			bool propertyAccessor = callable is MethodInfo methodInfo && type.GetProperties(DeclaredMemberFlags)
				.SelectMany(property => property.GetAccessors(nonPublic: true))
				.Contains(methodInfo);
			if ((callable.Attributes & (MethodAttributes.PinvokeImpl | MethodAttributes.Abstract | MethodAttributes.UnmanagedExport)) != 0 ||
				callable is MethodInfo { IsSpecialName: true } && !propertyAccessor)
			{
				violations.Add("CALLABLE_FLAGS");
			}
			if ((callable.MethodImplementationFlags & MethodImplAttributes.Synchronized) != 0)
			{
				violations.Add("IMPLEMENTATION_FLAGS");
			}
			if ((callable.CallingConvention & CallingConventions.VarArgs) != 0 ||
				(callable.CallingConvention & CallingConventions.ExplicitThis) != 0)
			{
				violations.Add("CALLING_CONVENTION");
			}
			if (callable.CustomAttributes.Any(HasForbiddenEmbeddedType))
			{
				violations.Add("CUSTOM_ATTRIBUTE");
			}
			foreach (ParameterInfo parameter in callable.GetParameters())
			{
				if (parameter.CustomAttributes.Any(HasForbiddenEmbeddedType))
				{
					violations.Add("CUSTOM_ATTRIBUTE");
				}
				if (parameter.IsOptional || parameter.HasDefaultValue || parameter.IsIn || parameter.IsOut ||
					parameter.Attributes != ParameterAttributes.None)
				{
					violations.Add("PARAMETER_METADATA");
				}
				if (parameter.GetCustomAttribute<MarshalAsAttribute>() is not null ||
					(parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0)
				{
					violations.Add("PARAMETER_MARSHAL");
				}
				if (parameter.GetRequiredCustomModifiers().Length != 0 || parameter.GetOptionalCustomModifiers().Length != 0)
				{
					violations.Add("CUSTOM_MODIFIER");
				}
			}
			if (callable is MethodInfo method && method.ReturnParameter.CustomAttributes.Any(HasForbiddenEmbeddedType))
			{
				violations.Add("CUSTOM_ATTRIBUTE");
			}
		}

		foreach (PropertyInfo property in type.GetProperties(DeclaredMemberFlags))
		{
			if (property.CustomAttributes.Any(HasForbiddenEmbeddedType))
			{
				violations.Add("CUSTOM_ATTRIBUTE");
			}
			if (property.Attributes != PropertyAttributes.None || property.GetIndexParameters().Length != 0 ||
				property.GetMethod is null || property.SetMethod is not null || property.GetAccessors(nonPublic: true).Length != 1)
			{
				violations.Add("PROPERTY_METADATA");
			}
		}
		if (HasRawMethodImplementation(type))
		{
			violations.Add("METHOD_IMPL_ROW");
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static bool HasForbiddenEmbeddedType(CustomAttributeData attribute) =>
		attribute.ConstructorArguments.Any(HasForbiddenEmbeddedType) ||
		attribute.NamedArguments.Any(argument => HasForbiddenEmbeddedType(argument.TypedValue));

	private static bool HasForbiddenEmbeddedType(CustomAttributeTypedArgument argument)
	{
		if (argument.ArgumentType == typeof(Type))
		{
			return argument.Value is not Type embedded || embedded != typeof(LiquidWalletTransactionObservation);
		}
		return argument.Value is ReadOnlyCollection<CustomAttributeTypedArgument> nested &&
			nested.Any(HasForbiddenEmbeddedType);
	}

	private static IReadOnlyList<string> VerifyMetadataRoot(Type type)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		if (type.BaseType != typeof(object))
		{
			violations.Add("EXTENDS_ROOT");
		}
		if (type.GetInterfaces().Length != 0)
		{
			violations.Add("INTERFACE_ROOT");
		}
		if (type.IsGenericType || type.GetGenericArguments().Length != 0)
		{
			violations.Add("GENERIC_ROOT");
		}
		if (type.GetEvents(DeclaredMemberFlags).Length != 0)
		{
			violations.Add("EVENT_ROOT");
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static void AssertMetadataRootViolation(Type type, string expectedViolation)
	{
		IReadOnlyList<string> violations = VerifyMetadataRoot(type);
		Assert.Equal([expectedViolation], violations);
	}

	private static void AssertReflectionViolation(Type type, string expectedViolation) =>
		Assert.Contains(expectedViolation, VerifyReflectionMetadata(type));

	private static bool HasRawMethodImplementation(Type type)
	{
		using var stream = File.OpenRead(type.Assembly.Location);
		using var peReader = new PEReader(stream);
		MetadataReader reader = peReader.GetMetadataReader();
		TypeDefinition definition = reader.GetTypeDefinition(FindType(reader, type));
		return definition.GetMethodImplementations().Count > 0;
	}

	private static void AssertAttributePair(Type allowedType, Type forbiddenType)
	{
		CustomAttributeData allowed = Assert.Single(
			allowedType.CustomAttributes,
			attribute => attribute.AttributeType == typeof(TypeCarrierAttribute));
		CustomAttributeData forbidden = Assert.Single(
			forbiddenType.CustomAttributes,
			attribute => attribute.AttributeType == typeof(TypeCarrierAttribute));
		Assert.Equal(AttributeShape(allowed), AttributeShape(forbidden));
		Assert.Empty(VerifyAttributeValues(allowed, typeof(LiquidWalletTransactionObservation)));
		Assert.Contains(
			"EMBEDDED_TYPE",
			VerifyAttributeValues(forbidden, typeof(LiquidWalletTransactionObservation)));
	}

	private static string AttributeShape(CustomAttributeData attribute) =>
		$"{TypeKey(attribute.AttributeType)}|ctor:{string.Join(',', attribute.Constructor.GetParameters().Select(parameter => TypeKey(parameter.ParameterType)))}|" +
		$"args:{string.Join(',', attribute.ConstructorArguments.Select(argument => TypeKey(argument.ArgumentType)))}|" +
		$"named:{string.Join(',', attribute.NamedArguments.Select(argument => $"{argument.MemberName}:{TypeKey(argument.TypedValue.ArgumentType)}"))}";

	private static IReadOnlyList<string> VerifyAttributeValues(CustomAttributeData attribute, Type allowedEmbeddedType)
	{
		var violations = new HashSet<string>(StringComparer.Ordinal);
		if (attribute.AttributeType != typeof(TypeCarrierAttribute) ||
			attribute.Constructor.GetParameters().Select(parameter => parameter.ParameterType).SingleOrDefault() != typeof(Type))
		{
			violations.Add("ATTRIBUTE_IDENTITY");
		}
		foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
		{
			ValidateAttributeArgument(argument, allowedEmbeddedType, violations);
		}
		foreach (CustomAttributeNamedArgument argument in attribute.NamedArguments)
		{
			if (argument.MemberName is not nameof(TypeCarrierAttribute.Target) and not nameof(TypeCarrierAttribute.Targets))
			{
				violations.Add("ATTRIBUTE_NAMED_ARGUMENT");
			}
			ValidateAttributeArgument(argument.TypedValue, allowedEmbeddedType, violations);
		}
		return violations.Order(StringComparer.Ordinal).ToArray();
	}

	private static void ValidateAttributeArgument(
		CustomAttributeTypedArgument argument,
		Type allowedEmbeddedType,
		ISet<string> violations)
	{
		if (argument.ArgumentType == typeof(Type))
		{
			if (argument.Value is not Type embedded || embedded != allowedEmbeddedType)
			{
				violations.Add("EMBEDDED_TYPE");
			}
			return;
		}
		if (argument.Value is ReadOnlyCollection<CustomAttributeTypedArgument> nested)
		{
			foreach (CustomAttributeTypedArgument value in nested)
			{
				ValidateAttributeArgument(value, allowedEmbeddedType, violations);
			}
		}
	}

	private static string RawAttributeConstructorKey(MetadataReader reader, EntityHandle constructorHandle)
	{
		MemberReference constructor = reader.GetMemberReference((MemberReferenceHandle)constructorHandle);
		TypeReference attributeType = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
		return $"{reader.GetString(attributeType.Namespace)}.{reader.GetString(attributeType.Name)}::{reader.GetString(constructor.Name)}";
	}

	private static IEnumerable<CustomAttributeHandle> SubjectOwnedCustomAttributes(
		MetadataReader reader,
		TypeDefinition definition)
	{
		foreach (CustomAttributeHandle handle in definition.GetCustomAttributes())
		{
			yield return handle;
		}
		foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
		{
			foreach (CustomAttributeHandle handle in reader.GetFieldDefinition(fieldHandle).GetCustomAttributes())
			{
				yield return handle;
			}
		}
		foreach (MethodDefinitionHandle methodHandle in definition.GetMethods())
		{
			MethodDefinition method = reader.GetMethodDefinition(methodHandle);
			foreach (CustomAttributeHandle handle in method.GetCustomAttributes())
			{
				yield return handle;
			}
			foreach (ParameterHandle parameterHandle in method.GetParameters())
			{
				foreach (CustomAttributeHandle handle in reader.GetParameter(parameterHandle).GetCustomAttributes())
				{
					yield return handle;
				}
			}
		}
		foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
		{
			foreach (CustomAttributeHandle handle in reader.GetPropertyDefinition(propertyHandle).GetCustomAttributes())
			{
				yield return handle;
			}
		}
		foreach (EventDefinitionHandle eventHandle in definition.GetEvents())
		{
			foreach (CustomAttributeHandle handle in reader.GetEventDefinition(eventHandle).GetCustomAttributes())
			{
				yield return handle;
			}
		}
		foreach (GenericParameterHandle parameterHandle in definition.GetGenericParameters())
		{
			GenericParameter parameter = reader.GetGenericParameter(parameterHandle);
			foreach (CustomAttributeHandle handle in parameter.GetCustomAttributes())
			{
				yield return handle;
			}
			foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
			{
				foreach (CustomAttributeHandle handle in reader.GetGenericParameterConstraint(constraintHandle).GetCustomAttributes())
				{
					yield return handle;
				}
			}
		}
		foreach (MethodDefinitionHandle methodHandle in definition.GetMethods())
		{
			foreach (GenericParameterHandle parameterHandle in reader.GetMethodDefinition(methodHandle).GetGenericParameters())
			{
				GenericParameter parameter = reader.GetGenericParameter(parameterHandle);
				foreach (CustomAttributeHandle handle in parameter.GetCustomAttributes())
				{
					yield return handle;
				}
				foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
				{
					foreach (CustomAttributeHandle handle in reader.GetGenericParameterConstraint(constraintHandle).GetCustomAttributes())
					{
						yield return handle;
					}
				}
			}
		}
	}

	private static void AddExpectedProductionAttributeConstructors(
		MetadataReader reader,
		TypeDefinition definition,
		IDictionary<EntityHandle, MemberInfo> expectedMembers)
	{
		IEnumerable<CustomAttributeHandle> attributes = SubjectOwnedCustomAttributes(reader, definition)
			.Concat(definition.GetInterfaceImplementations()
				.SelectMany(handle => reader.GetInterfaceImplementation(handle).GetCustomAttributes()));
		foreach (CustomAttributeHandle handle in attributes)
		{
			CustomAttribute attribute = reader.GetCustomAttribute(handle);
			if (attribute.Constructor.Kind != HandleKind.MemberReference)
			{
				continue;
			}
			string key = RawAttributeConstructorKey(reader, attribute.Constructor);
			string blob = Convert.ToHexString(reader.GetBlobBytes(attribute.Value)).ToLowerInvariant();
			Type[]? parameters = (key, blob) switch
			{
				("System.Runtime.CompilerServices.NullableAttribute::.ctor", "0100000000") => [typeof(byte)],
				("System.Runtime.CompilerServices.NullableAttribute::.ctor", "01000200000000010000") => [typeof(byte[])],
				("System.Runtime.CompilerServices.NullableContextAttribute::.ctor", "0100010000" or "0100020000") => [typeof(byte)],
				("System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor", "01000000") => [],
				("System.Diagnostics.DebuggerBrowsableAttribute::.ctor", "0100000000000000") => [typeof(System.Diagnostics.DebuggerBrowsableState)],
				_ => null,
			};
			if (parameters is null)
			{
				continue;
			}
			string attributeTypeName = key[..key.IndexOf("::", StringComparison.Ordinal)];
			Type attributeType = typeof(object).Assembly.GetType(attributeTypeName, throwOnError: true)!;
			ConstructorInfo constructor = attributeType.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null,
				parameters,
				modifiers: null)!;
			Assert.NotNull(constructor);
			if (expectedMembers.TryGetValue(attribute.Constructor, out MemberInfo? existing))
			{
				Assert.Equal(MemberKey(existing), MemberKey(constructor));
			}
			else
			{
				expectedMembers.Add(attribute.Constructor, constructor);
			}
		}
	}

	private static void ValidateRawCustomAttribute(
		MetadataReader reader,
		ModifierRejectingTypeProvider provider,
		CustomAttributeHandle handle,
		IReadOnlyDictionary<EntityHandle, MemberInfo> productionMembers,
		RawPePolicyMode policyMode,
		ISet<string> violations)
	{
		CustomAttribute attribute = reader.GetCustomAttribute(handle);
		if (attribute.Constructor.Kind != HandleKind.MemberReference)
		{
			violations.Add("CUSTOM_ATTRIBUTE_CONSTRUCTOR");
			return;
		}
		string constructorKey;
		try
		{
			constructorKey = RawAttributeConstructorKey(reader, attribute.Constructor);
		}
		catch (Exception exception) when (IsRawMetadataException(exception))
		{
			RecordRawMetadataException(exception, violations);
			violations.Add("CUSTOM_ATTRIBUTE_CONSTRUCTOR");
			return;
		}
		if (policyMode == RawPePolicyMode.Production)
		{
			productionMembers.TryGetValue(attribute.Constructor, out MemberInfo? expectedConstructor);
			if (expectedConstructor is null)
			{
				violations.Add("UNMAPPED_REACHABLE_HANDLE");
				return;
			}
			ValidateRawPeHandle(
				reader,
				provider,
				attribute.Constructor,
				expectedConstructor,
				productionMembers,
				policyMode,
				violations);
		}
		else
		{
			RawMemberExpectation[] expectations = RawAttributeConstructorExpectations(constructorKey, policyMode);
			if (expectations.Length == 0)
			{
				violations.Add("CUSTOM_ATTRIBUTE_CONSTRUCTOR");
			}
			else
			{
				ValidateRawMemberReference(
					reader,
					provider,
					(MemberReferenceHandle)attribute.Constructor,
					expectations,
					"CUSTOM_ATTRIBUTE_CONSTRUCTOR",
					violations);
			}
		}

		try
		{
			CustomAttributeValue<string> value = attribute.DecodeValue(provider);
			foreach (System.Reflection.Metadata.CustomAttributeTypedArgument<string> argument in value.FixedArguments)
			{
				ValidateRawAttributeValue(argument.Type, argument.Value, violations);
			}
			foreach (CustomAttributeNamedArgument<string> argument in value.NamedArguments)
			{
				ValidateRawAttributeValue(argument.Type, argument.Value, violations);
			}
		}
		catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException)
		{
			violations.Add("CUSTOM_ATTRIBUTE_BLOB");
		}
	}

	private static void ValidateRawAttributeValue(string type, object? value, ISet<string> violations)
	{
		if (type == RawTypeKey(typeof(Type)) || type == RawEntityTypeKey(typeof(Type)))
		{
			if (value is not string embedded || embedded != "$OBSERVATION")
			{
				violations.Add("EMBEDDED_TYPE");
			}
			return;
		}
		if (value is ImmutableArray<System.Reflection.Metadata.CustomAttributeTypedArgument<string>> nested)
		{
			foreach (System.Reflection.Metadata.CustomAttributeTypedArgument<string> argument in nested)
			{
				ValidateRawAttributeValue(argument.Type, argument.Value, violations);
			}
		}
	}

	private static RawMemberExpectation[] RawAttributeConstructorExpectations(
		string constructorKey,
		RawPePolicyMode policyMode)
	{
		string voidType = RawTypeKey(typeof(void));
		return constructorKey switch
		{
			"System.Runtime.CompilerServices.NullableAttribute::.ctor" =>
			[
				RawMemberExpectation.ForMethod(
					RawSystemRuntimeTypeKey("System.Runtime.CompilerServices.NullableAttribute"), ".ctor", voidType, [RawTypeKey(typeof(byte))], true, 0),
				RawMemberExpectation.ForMethod(
					RawSystemRuntimeTypeKey("System.Runtime.CompilerServices.NullableAttribute"), ".ctor", voidType, [RawTypeKey(typeof(byte[]))], true, 0),
			],
			"System.Runtime.CompilerServices.NullableContextAttribute::.ctor" =>
			[
				RawMemberExpectation.ForMethod(
					RawSystemRuntimeTypeKey("System.Runtime.CompilerServices.NullableContextAttribute"), ".ctor", voidType, [RawTypeKey(typeof(byte))], true, 0),
			],
			"System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor" =>
			[
				RawMemberExpectation.ForMethod(
					RawSystemRuntimeTypeKey("System.Runtime.CompilerServices.CompilerGeneratedAttribute"), ".ctor", voidType, [], true, 0),
			],
			"System.Diagnostics.DebuggerBrowsableAttribute::.ctor" =>
			[
				RawMemberExpectation.ForMethod(
					RawSystemRuntimeTypeKey("System.Diagnostics.DebuggerBrowsableAttribute"), ".ctor", voidType,
					[RawSystemRuntimeTypeKey("System.Diagnostics.DebuggerBrowsableState")], true, 0),
			],
			"RawFixture.TypeCarrierAttribute::.ctor" when policyMode == RawPePolicyMode.Fixture =>
			[
				RawMemberExpectation.ForMethod(
					RawSystemRuntimeTypeKey("RawFixture.TypeCarrierAttribute"), ".ctor", voidType, [RawTypeKey(typeof(Type))], true, 0),
				RawMemberExpectation.ForMethod(
					RawSystemRuntimeTypeKey("RawFixture.TypeCarrierAttribute"), ".ctor", voidType, [RawTypeKey(typeof(Type[]))], true, 0),
			],
			_ => [],
		};
	}

	private static IEnumerable<string> RawAttributeLocationManifest(
		MetadataReader reader,
		TypeDefinition definition,
		ModifierRejectingTypeProvider provider)
	{
		foreach (CustomAttributeHandle handle in definition.GetCustomAttributes())
		{
			yield return RawAttributeLocation(reader, "type", handle);
		}
		foreach (GenericParameterHandle parameterHandle in definition.GetGenericParameters())
		{
			GenericParameter parameter = reader.GetGenericParameter(parameterHandle);
			string genericLocation = $"type-generic:{parameter.Index}:{reader.GetString(parameter.Name)}";
			foreach (CustomAttributeHandle handle in parameter.GetCustomAttributes())
			{
				yield return RawAttributeLocation(reader, genericLocation, handle);
			}
			foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
			{
				foreach (CustomAttributeHandle handle in reader.GetGenericParameterConstraint(constraintHandle).GetCustomAttributes())
				{
					yield return RawAttributeLocation(reader, genericLocation + ":constraint", handle);
				}
			}
		}
		foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
		{
			FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
			foreach (CustomAttributeHandle handle in field.GetCustomAttributes())
			{
				yield return RawAttributeLocation(reader, $"field:{reader.GetString(field.Name)}", handle);
			}
		}
		foreach (MethodDefinitionHandle methodHandle in definition.GetMethods())
		{
			MethodDefinition method = reader.GetMethodDefinition(methodHandle);
			BlobReader blob = reader.GetBlobReader(method.Signature);
			MethodSignature<string> signature = new SignatureDecoder<string, object?>(provider, reader, null)
				.DecodeMethodSignature(ref blob);
			MethodBase reflected = FindReflectedCallable(reader.GetString(method.Name), signature.ParameterTypes);
			string location = $"method:{MethodKey(reflected)}";
			foreach (CustomAttributeHandle handle in method.GetCustomAttributes())
			{
				yield return RawAttributeLocation(reader, location, handle);
			}
			foreach (ParameterHandle parameterHandle in method.GetParameters())
			{
				Parameter parameter = reader.GetParameter(parameterHandle);
				string parameterLocation = parameter.SequenceNumber == 0
					? $"return:{MethodKey(reflected)}"
					: $"parameter:{MethodKey(reflected)}:{parameter.SequenceNumber - 1}:{reader.GetString(parameter.Name)}";
				foreach (CustomAttributeHandle handle in parameter.GetCustomAttributes())
				{
					yield return RawAttributeLocation(reader, parameterLocation, handle);
				}
			}
			foreach (GenericParameterHandle parameterHandle in method.GetGenericParameters())
			{
				GenericParameter parameter = reader.GetGenericParameter(parameterHandle);
				string genericLocation = $"{location}:generic:{parameter.Index}:{reader.GetString(parameter.Name)}";
				foreach (CustomAttributeHandle handle in parameter.GetCustomAttributes())
				{
					yield return RawAttributeLocation(reader, genericLocation, handle);
				}
				foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
				{
					foreach (CustomAttributeHandle handle in reader.GetGenericParameterConstraint(constraintHandle).GetCustomAttributes())
					{
						yield return RawAttributeLocation(reader, genericLocation + ":constraint", handle);
					}
				}
			}
		}
		foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
		{
			PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
			foreach (CustomAttributeHandle handle in property.GetCustomAttributes())
			{
				yield return RawAttributeLocation(reader, $"property:{reader.GetString(property.Name)}", handle);
			}
		}
		foreach (EventDefinitionHandle eventHandle in definition.GetEvents())
		{
			EventDefinition eventDefinition = reader.GetEventDefinition(eventHandle);
			foreach (CustomAttributeHandle handle in eventDefinition.GetCustomAttributes())
			{
				yield return RawAttributeLocation(reader, $"event:{reader.GetString(eventDefinition.Name)}", handle);
			}
		}
		int interfaceIndex = 0;
		foreach (InterfaceImplementationHandle interfaceHandle in definition.GetInterfaceImplementations())
		{
			InterfaceImplementation implementation = reader.GetInterfaceImplementation(interfaceHandle);
			foreach (CustomAttributeHandle handle in implementation.GetCustomAttributes())
			{
				yield return RawAttributeLocation(reader, $"interface:{interfaceIndex}", handle);
			}
			interfaceIndex++;
		}
	}

	private static string RawAttributeLocation(
		MetadataReader reader,
		string location,
		CustomAttributeHandle handle)
	{
		CustomAttribute attribute = reader.GetCustomAttribute(handle);
		return $"{location}|{RawAttributeConstructorKey(reader, attribute.Constructor)}|" +
			Convert.ToHexString(reader.GetBlobBytes(attribute.Value)).ToLowerInvariant();
	}

	private static string[] ExpectedRawAttributeLocationManifest()
	{
		string equalsBatch = MethodKey(GetMethod("Equals", typeof(LiquidWalletObservationBatch)));
		string equalsObject = MethodKey(GetMethod("Equals", typeof(object)));
#if DEBUG
		return
		[
			"type|System.Runtime.CompilerServices.NullableAttribute::.ctor|0100000000",
			"type|System.Runtime.CompilerServices.NullableContextAttribute::.ctor|0100010000",
			"field:<OwnedOutputCount>k__BackingField|System.Diagnostics.DebuggerBrowsableAttribute::.ctor|0100000000000000",
			"field:<OwnedOutputCount>k__BackingField|System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor|01000000",
			"method:$BATCH::get_OwnedOutputCount()->System.Int32|System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor|01000000",
			$"method:{equalsBatch}|System.Runtime.CompilerServices.NullableContextAttribute::.ctor|0100020000",
			$"method:{equalsObject}|System.Runtime.CompilerServices.NullableContextAttribute::.ctor|0100020000",
			"interface:0|System.Runtime.CompilerServices.NullableAttribute::.ctor|01000200000000010000",
		];
#else
		return
		[
			"type|System.Runtime.CompilerServices.NullableAttribute::.ctor|0100000000",
			"type|System.Runtime.CompilerServices.NullableContextAttribute::.ctor|0100010000",
			"field:<OwnedOutputCount>k__BackingField|System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor|01000000",
			"method:$BATCH::get_OwnedOutputCount()->System.Int32|System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor|01000000",
			$"method:{equalsBatch}|System.Runtime.CompilerServices.NullableContextAttribute::.ctor|0100020000",
			$"method:{equalsObject}|System.Runtime.CompilerServices.NullableContextAttribute::.ctor|0100020000",
			"interface:0|System.Runtime.CompilerServices.NullableAttribute::.ctor|01000200000000010000",
		];
#endif
	}

	private static IReadOnlyDictionary<EntityHandle, MemberInfo> ExpectedReachableMembers()
	{
		var result = new Dictionary<EntityHandle, MemberInfo>();
		foreach (MethodBase method in DeclaredBodies())
		{
			foreach (Instruction instruction in ReadInstructions(method).Where(instruction =>
				instruction.OpCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
					OperandType.InlineTok or OperandType.InlineType))
			{
				int token = Assert.IsType<int>(instruction.Operand);
				EntityHandle handle = MetadataTokens.EntityHandle(token);
				MemberInfo member = ResolveMember(method, instruction);
				if (result.TryGetValue(handle, out MemberInfo? existing))
				{
					Assert.Equal(MemberKey(existing), MemberKey(member));
				}
				else
				{
					result.Add(handle, member);
				}
			}
		}
		return result;
	}

	private static MethodBase FindReflectedCallable(string metadataName, ImmutableArray<string> parameterTypes)
	{
		IEnumerable<MethodBase> candidates = metadataName == ".ctor"
			? typeof(LiquidWalletObservationBatch).GetConstructors(DeclaredMemberFlags)
			: typeof(LiquidWalletObservationBatch).GetMethods(DeclaredMemberFlags)
				.Where(method => method.Name == metadataName);
		return Assert.Single(candidates, candidate => candidate.GetParameters()
			.Select(parameter => RawTypeKey(parameter.ParameterType))
			.SequenceEqual(parameterTypes, StringComparer.Ordinal));
	}

	private static string RawReturnTypeKey(MethodBase method) =>
		method is MethodInfo methodInfo ? RawTypeKey(methodInfo.ReturnType) : RawTypeKey(typeof(void));

	private static string RawTypeKey(Type type)
	{
		string? primitive = RawPrimitiveTypeKey(type);
		if (primitive is not null)
		{
			return primitive;
		}
		if (type.IsGenericParameter)
		{
			return RawIdentityNode(
				type.DeclaringMethod is null ? "generic-type-parameter" : "generic-method-parameter",
				type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture));
		}
		if (type.IsArray)
		{
			string elementType = RawTypeKey(type.GetElementType()!);
			return type.IsSZArray
				? RawSzArrayKey(elementType)
				: RawMdArrayKey(elementType, type.GetArrayRank(), [], []);
		}
		if (type.IsByRef)
		{
			return RawIdentityNode("byref", RawTypeKey(type.GetElementType()!));
		}
		if (type.IsPointer)
		{
			return RawIdentityNode("pointer", RawTypeKey(type.GetElementType()!));
		}
		if (type.IsGenericType)
		{
			Type definition = type.GetGenericTypeDefinition();
			return RawIdentityNode(
				"generic-instantiation",
				[RawTypeKindKey(RawNamedTypeKey(definition), ExpectedRawTypeKind(definition)),
				.. type.GetGenericArguments().Select(RawTypeKey)]);
		}
		return RawTypeKindKey(RawNamedTypeKey(type), ExpectedRawTypeKind(type));
	}

	private static string RawEntityTypeKey(Type type) => RawNamedTypeKey(type);

	private static string RawMemberParentTypeKey(Type type) =>
		type.IsGenericType ? RawTypeKey(type) : RawEntityTypeKey(type);

	private static byte ExpectedRawTypeKind(Type type) => type == typeof(void)
		? (byte)0x01
		: type.IsValueType
			? (byte)0x11
			: (byte)0x12;

	private static string RawTypeKindKey(string identity, byte rawTypeKind) => rawTypeKind switch
	{
		0x01 => RawIdentityNode("signature-kind-void", identity),
		0x11 => RawIdentityNode("signature-kind-valuetype", identity),
		0x12 => RawIdentityNode("signature-kind-class", identity),
		_ => identity,
	};

	private static string RawTypeSpecificationKindKey(string identity, byte rawTypeKind) => rawTypeKind switch
	{
		0 => identity,
		0x11 => RawIdentityNode("typespec-kind-valuetype", identity),
		0x12 => RawIdentityNode("typespec-kind-class", identity),
		_ => RawIdentityNode("typespec-kind-unknown", rawTypeKind.ToString(CultureInfo.InvariantCulture), identity),
	};

	private static string RawSzArrayKey(string elementType) => RawIdentityNode("sz-array", elementType);

	private static string RawMdArrayKey(
		string elementType,
		int rank,
		IEnumerable<int> sizes,
		IEnumerable<int> lowerBounds) => RawIdentityNode(
		"md-array",
		elementType,
		rank.ToString(CultureInfo.InvariantCulture),
		RawIdentityNode("sizes", sizes.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray()),
		RawIdentityNode("lower-bounds", lowerBounds.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray()));

	private static string? RawPrimitiveTypeKey(Type type) =>
		type == typeof(void) ? RawIdentityNode("primitive", "void") :
		type == typeof(bool) ? RawIdentityNode("primitive", "boolean") :
		type == typeof(char) ? RawIdentityNode("primitive", "char") :
		type == typeof(sbyte) ? RawIdentityNode("primitive", "i1") :
		type == typeof(byte) ? RawIdentityNode("primitive", "u1") :
		type == typeof(short) ? RawIdentityNode("primitive", "i2") :
		type == typeof(ushort) ? RawIdentityNode("primitive", "u2") :
		type == typeof(int) ? RawIdentityNode("primitive", "i4") :
		type == typeof(uint) ? RawIdentityNode("primitive", "u4") :
		type == typeof(long) ? RawIdentityNode("primitive", "i8") :
		type == typeof(ulong) ? RawIdentityNode("primitive", "u8") :
		type == typeof(float) ? RawIdentityNode("primitive", "r4") :
		type == typeof(double) ? RawIdentityNode("primitive", "r8") :
		type == typeof(string) ? RawIdentityNode("primitive", "string") :
		type == typeof(TypedReference) ? RawIdentityNode("primitive", "typedbyref") :
		type == typeof(IntPtr) ? RawIdentityNode("primitive", "native-int") :
		type == typeof(UIntPtr) ? RawIdentityNode("primitive", "native-uint") :
		type == typeof(object) ? RawIdentityNode("primitive", "object") :
		null;

	private static string RawIdentityNode(string kind, params string[] components)
	{
		return $"node({RawIdentityComponent(kind)}|{string.Join('|', components.Select(RawIdentityComponent))})";
	}

	private static string RawIdentityComponent(string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		return $"{bytes.Length.ToString(CultureInfo.InvariantCulture)}:{Convert.ToHexString(bytes).ToLowerInvariant()}";
	}

	private static bool ContainsForbiddenRawTypeShape(string type) =>
		type.Contains($"node({RawIdentityComponent("pointer")}|", StringComparison.Ordinal) ||
		type.Contains($"node({RawIdentityComponent("byref")}|", StringComparison.Ordinal) ||
		type.Contains($"node({RawIdentityComponent("function-pointer")}|", StringComparison.Ordinal);

	private static string RawNamedTypeKey(Type type)
	{
		string fullName = type.FullName ?? type.Name;
		if (fullName is
			"System.ArgumentException" or
			"System.ArgumentNullException" or
			"System.ArgumentOutOfRangeException" or
			"System.Array" or
			"System.Boolean" or
			"System.Byte" or
			"System.Char" or
			"System.Diagnostics.DebuggerBrowsableAttribute" or
			"System.Diagnostics.DebuggerBrowsableState" or
			"System.Double" or
			"System.Environment" or
			"System.Environment+SpecialFolder" or
			"System.HashCode" or
			"System.IDisposable" or
			"System.IEquatable`1" or
			"System.Int16" or
			"System.Int32" or
			"System.Int64" or
			"System.IntPtr" or
			"System.Object" or
			"System.Runtime.CompilerServices.CompilerGeneratedAttribute" or
			"System.Runtime.CompilerServices.NullableAttribute" or
			"System.Runtime.CompilerServices.NullableContextAttribute" or
			"System.SByte" or
			"System.Single" or
			"System.String" or
			"System.Type" or
			"System.TypedReference" or
			"System.UInt16" or
			"System.UInt32" or
			"System.UInt64" or
			"System.UIntPtr" or
			"System.Void" or
			"System.Collections.Generic.IList`1" or
			"System.Collections.Generic.IReadOnlyCollection`1" or
			"System.Collections.Generic.IReadOnlyList`1" or
			"System.Collections.ObjectModel.ReadOnlyCollection`1")
		{
			return RawSystemRuntimeTypeKey(fullName);
		}
		if (type.Assembly == typeof(LiquidWalletObservationBatch).Assembly)
		{
			return RawLocalTypeDefinitionKey(type);
		}
		return RawReflectionTypeReferenceKey(ReflectionAssemblyIdentity(type.Assembly.GetName()), type);
	}

	private static string ReflectionAssemblyIdentity(AssemblyName reference)
	{
		AssemblyFlags flags = (AssemblyFlags)(int)reference.Flags;
		bool carriesPublicKey = (flags & AssemblyFlags.PublicKey) != 0;
		byte[] key = carriesPublicKey
			? reference.GetPublicKey() ?? []
			: reference.GetPublicKeyToken() ?? [];
		return RawIdentityNode(
			"assembly",
			reference.Name ?? string.Empty,
			reference.Version?.ToString() ?? string.Empty,
			reference.CultureName ?? string.Empty,
			((int)flags).ToString(CultureInfo.InvariantCulture),
			carriesPublicKey ? "public-key" : "token",
			Convert.ToHexString(key).ToLowerInvariant(),
			string.Empty);
	}

	private static AssemblyReferenceHandle AddExactAssemblyReference(
		MetadataBuilder metadata,
		AssemblyName reference)
	{
		AssemblyFlags flags = (AssemblyFlags)(int)reference.Flags;
		bool carriesPublicKey = (flags & AssemblyFlags.PublicKey) != 0;
		byte[] key = carriesPublicKey
			? reference.GetPublicKey() ?? []
			: reference.GetPublicKeyToken() ?? [];
		return metadata.AddAssemblyReference(
			metadata.GetOrAddString(reference.Name ?? string.Empty),
			reference.Version ?? new Version(0, 0, 0, 0),
			string.IsNullOrEmpty(reference.CultureName)
				? default
				: metadata.GetOrAddString(reference.CultureName),
			key.Length == 0 ? default : metadata.GetOrAddBlob(key),
			flags,
			default);
	}

	private static string MetadataAssemblyReferenceIdentity(
		MetadataReader metadata,
		AssemblyReferenceHandle handle)
	{
		int row = MetadataTokens.GetRowNumber(handle);
		if (row <= 0 || row > metadata.GetTableRowCount(TableIndex.AssemblyRef))
		{
			return RawIdentityNode(
				"invalid-assembly",
				MetadataTokens.GetRowNumber(handle).ToString(CultureInfo.InvariantCulture));
		}
		AssemblyReference reference = metadata.GetAssemblyReference(handle);
		string keyKind = (reference.Flags & AssemblyFlags.PublicKey) != 0 ? "public-key" : "token";
		return RawIdentityNode(
			"assembly",
			metadata.GetString(reference.Name),
			reference.Version.ToString(),
			metadata.GetString(reference.Culture),
			((int)reference.Flags).ToString(CultureInfo.InvariantCulture),
			keyKind,
			Convert.ToHexString(metadata.GetBlobBytes(reference.PublicKeyOrToken)).ToLowerInvariant(),
			Convert.ToHexString(metadata.GetBlobBytes(reference.HashValue)).ToLowerInvariant());
	}

	private static string RawLocalTypeDefinitionKey(Type type) => RawIdentityNode(
		"typedef",
		type.Module.Name,
		MetadataTokens.GetRowNumber(MetadataTokens.EntityHandle(type.MetadataToken)).ToString(CultureInfo.InvariantCulture),
		RawReflectionDefinitionNameKey(type));

	private static string RawSystemRuntimeTypeKey(string fullName) => RawFullNameTypeReferenceKey(
		RawIdentityNode("assembly", "System.Runtime", "10.0.0.0", string.Empty, "0", "token", "b03f5f7f11d50a3a", string.Empty),
		fullName);

	private static string RawReflectionTypeReferenceKey(string assemblyIdentity, Type type)
	{
		string scope = type.DeclaringType is null
			? RawIdentityNode("assembly-scope", assemblyIdentity)
			: RawReflectionTypeReferenceKey(assemblyIdentity, type.DeclaringType);
		return RawIdentityNode(
			"typeref",
			scope,
			type.DeclaringType is null ? type.Namespace ?? string.Empty : string.Empty,
			type.Name);
	}

	private static string RawFullNameTypeReferenceKey(string assemblyIdentity, string fullName)
	{
		string[] nestedNames = fullName.Split('+');
		int namespaceSeparator = nestedNames[0].LastIndexOf('.');
		string @namespace = namespaceSeparator < 0 ? string.Empty : nestedNames[0][..namespaceSeparator];
		string name = namespaceSeparator < 0 ? nestedNames[0] : nestedNames[0][(namespaceSeparator + 1)..];
		string current = RawIdentityNode(
			"typeref",
			RawIdentityNode("assembly-scope", assemblyIdentity),
			@namespace,
			name);
		foreach (string nestedName in nestedNames.Skip(1))
		{
			current = RawIdentityNode("typeref", current, string.Empty, nestedName);
		}
		return current;
	}

	private static string RawReflectionDefinitionNameKey(Type type)
	{
		string? declaring = type.DeclaringType is null ? null : RawReflectionDefinitionNameKey(type.DeclaringType);
		return RawIdentityNode(
			"typedef-name",
			declaring ?? string.Empty,
			type.DeclaringType is null ? type.Namespace ?? string.Empty : string.Empty,
			type.Name);
	}

	private static int MetadataToken(EntityHandle handle) => MetadataTokens.GetToken(handle);

	private static void CollectReferencedHandles(byte[] bytes, ISet<EntityHandle> handles)
	{
		byte[] body = bytes;
		int offset = 0;
		while (offset < body.Length)
		{
			ushort value = body[offset++];
			if (value == 0xfe)
			{
				value = (ushort)(0xfe00 | body[offset++]);
			}
			OpCode opcode = OpCodeByValue[value];
			object? operand = ReadOperand(opcode.OperandType, body, ref offset);
			if (opcode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
				OperandType.InlineSig or OperandType.InlineTok or OperandType.InlineType)
			{
				handles.Add(MetadataTokens.EntityHandle((int)operand!));
			}
		}
	}

	private static void DecodeReachableSignature(
		MetadataReader reader,
		ModifierRejectingTypeProvider provider,
		EntityHandle handle,
		MemberInfo expected)
	{
		switch (handle.Kind)
		{
			case HandleKind.MemberReference:
				MemberReference reference = reader.GetMemberReference((MemberReferenceHandle)handle);
				Assert.Equal(RawMemberParentTypeKey(expected.DeclaringType!), provider.DecodeEntityType(reference.Parent));
				BlobReader memberBlob = reader.GetBlobReader(reference.Signature);
				var decoder = new SignatureDecoder<string, object?>(provider, reader, null);
				if (reference.GetKind() == MemberReferenceKind.Method)
				{
					MethodSignature<string> signature = decoder.DecodeMethodSignature(ref memberBlob);
					MethodBase expectedMethod = Assert.IsAssignableFrom<MethodBase>(expected);
					MethodBase signatureMethod = RawSignatureDefinition(expectedMethod);
					Assert.Equal(SignatureCallingConvention.Default, signature.Header.CallingConvention);
					Assert.False(signature.Header.HasExplicitThis);
					Assert.Equal(!expectedMethod.IsStatic, signature.Header.IsInstance);
					bool expectedGeneric = signatureMethod is MethodInfo expectedMethodInfo && expectedMethodInfo.IsGenericMethodDefinition;
					Assert.Equal(expectedGeneric, signature.Header.IsGeneric);
					Assert.Equal(expectedGeneric ? ((MethodInfo)signatureMethod).GetGenericArguments().Length : 0, signature.GenericParameterCount);
					Assert.Equal(signature.ParameterTypes.Length, signature.RequiredParameterCount);
					Assert.Equal(RawReturnTypeKey(signatureMethod), signature.ReturnType);
					Assert.Equal(
						signatureMethod.GetParameters().Select(parameter => RawTypeKey(parameter.ParameterType)),
						signature.ParameterTypes);
				}
				else
				{
					FieldInfo expectedField = Assert.IsAssignableFrom<FieldInfo>(expected);
					Assert.Equal(RawTypeKey(expectedField.FieldType), decoder.DecodeFieldSignature(ref memberBlob));
				}
				Assert.Equal(0, memberBlob.RemainingBytes);
				break;
			case HandleKind.MethodSpecification:
				MethodSpecification specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
				MethodInfo expectedSpecification = Assert.IsAssignableFrom<MethodInfo>(expected);
				Assert.True(expectedSpecification.IsGenericMethod);
				MethodInfo expectedDefinition = expectedSpecification.GetGenericMethodDefinition();
				DecodeReachableSignature(reader, provider, specification.Method, expectedDefinition);
				BlobReader specificationBlob = reader.GetBlobReader(specification.Signature);
				ImmutableArray<string> typeArguments = new SignatureDecoder<string, object?>(provider, reader, null)
					.DecodeMethodSpecificationSignature(ref specificationBlob);
				Assert.Equal(0, specificationBlob.RemainingBytes);
				Assert.Equal(expectedDefinition.GetGenericArguments().Length, typeArguments.Length);
				Assert.Equal(
					expectedSpecification.GetGenericArguments().Select(RawTypeKey),
					typeArguments);
				break;
			case HandleKind.TypeSpecification:
				Assert.Equal(RawTypeKey(Assert.IsAssignableFrom<Type>(expected)), provider.DecodeEntityType(handle));
				break;
			case HandleKind.TypeDefinition:
			case HandleKind.TypeReference:
				Assert.Equal(RawEntityTypeKey(Assert.IsAssignableFrom<Type>(expected)), provider.DecodeEntityType(handle));
				break;
			case HandleKind.MethodDefinition:
				MethodDefinition methodDefinition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
				MethodBase expectedMethodDefinition = Assert.IsAssignableFrom<MethodBase>(expected);
				Assert.Equal(expectedMethodDefinition.Name, reader.GetString(methodDefinition.Name));
				Assert.Equal(
					RawEntityTypeKey(expectedMethodDefinition.DeclaringType!),
					provider.DecodeEntityType(methodDefinition.GetDeclaringType()));
				BlobReader methodDefinitionBlob = reader.GetBlobReader(methodDefinition.Signature);
				MethodSignature<string> methodDefinitionSignature = new SignatureDecoder<string, object?>(provider, reader, null)
					.DecodeMethodSignature(ref methodDefinitionBlob);
				Assert.Equal(0, methodDefinitionBlob.RemainingBytes);
				Assert.Equal(!expectedMethodDefinition.IsStatic, methodDefinitionSignature.Header.IsInstance);
				Assert.Equal(RawReturnTypeKey(expectedMethodDefinition), methodDefinitionSignature.ReturnType);
				Assert.Equal(
					expectedMethodDefinition.GetParameters().Select(parameter => RawTypeKey(parameter.ParameterType)),
					methodDefinitionSignature.ParameterTypes);
				break;
			case HandleKind.FieldDefinition:
				FieldDefinition fieldDefinition = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
				FieldInfo expectedFieldDefinition = Assert.IsAssignableFrom<FieldInfo>(expected);
				Assert.Equal(expectedFieldDefinition.Name, reader.GetString(fieldDefinition.Name));
				Assert.Equal(
					RawEntityTypeKey(expectedFieldDefinition.DeclaringType!),
					provider.DecodeEntityType(fieldDefinition.GetDeclaringType()));
				BlobReader fieldDefinitionBlob = reader.GetBlobReader(fieldDefinition.Signature);
				Assert.Equal(
					RawTypeKey(expectedFieldDefinition.FieldType),
					new SignatureDecoder<string, object?>(provider, reader, null).DecodeFieldSignature(ref fieldDefinitionBlob));
				Assert.Equal(0, fieldDefinitionBlob.RemainingBytes);
				break;
			case HandleKind.StandaloneSignature:
				throw new Xunit.Sdk.XunitException("Production IL must not carry an InlineSig operand.");
			default:
				throw new Xunit.Sdk.XunitException($"Unexpected reachable metadata handle {handle.Kind}.");
		}
	}

	private static MethodBase RawSignatureDefinition(MethodBase method)
	{
		Type? declaringType = method.DeclaringType;
		if (declaringType is null || !declaringType.IsConstructedGenericType)
		{
			return method;
		}
		Type definition = declaringType.GetGenericTypeDefinition();
		return Assert.Single(
			definition.GetMembers(DeclaredMemberFlags).OfType<MethodBase>(),
			candidate => candidate.MetadataToken == method.MetadataToken);
	}

	private sealed class ModifierRejectingTypeProvider(MetadataReader reader) :
		ISignatureTypeProvider<string, object?>,
		ICustomAttributeTypeProvider<string>
	{
		private readonly HashSet<TypeSpecificationHandle> _activeTypeSpecifications = [];
		private readonly HashSet<TypeSpecificationHandle> _graphActiveTypeSpecifications = [];
		private readonly HashSet<TypeSpecificationHandle> _graphCompletedTypeSpecifications = [];
		private readonly HashSet<TypeDefinitionHandle> _activeTypeDefinitions = [];
		private readonly HashSet<TypeDefinitionHandle> _completedTypeDefinitions = [];
		private readonly HashSet<TypeReferenceHandle> _activeTypeReferences = [];
		private readonly HashSet<TypeReferenceHandle> _completedTypeReferences = [];

		public List<string> Modifiers { get; } = [];
		public List<string> Types { get; } = [];
		public HashSet<EntityHandle> EntityHandles { get; } = [];
		public HashSet<string> RawTypeSpecificationViolations { get; } = new(StringComparer.Ordinal);
		public HashSet<string> RawTypeScopeViolations { get; } = new(StringComparer.Ordinal);

		public string DecodeEntityType(EntityHandle handle) => DecodeSignatureEntityType(handle, 0);

		public string DecodeSignatureEntityType(EntityHandle handle, byte rawTypeKind)
		{
			string result = handle.Kind switch
			{
				HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, rawTypeKind),
				HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, rawTypeKind),
				HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, rawTypeKind),
				_ => throw new Xunit.Sdk.XunitException($"Expected a type handle, received {handle.Kind}."),
			};
			Types.Add(result);
			return result;
		}

		public string GetArrayType(string elementType, ArrayShape shape) =>
			Record(RawMdArrayKey(elementType, shape.Rank, shape.Sizes, shape.LowerBounds));

		public string GetByReferenceType(string elementType) => Record(RawIdentityNode("byref", elementType));

		public string GetFunctionPointerType(MethodSignature<string> signature) =>
			Record(RawIdentityNode("function-pointer", [signature.ReturnType, .. signature.ParameterTypes]));

		public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
			Record(RawIdentityNode("generic-instantiation", [genericType, .. typeArguments]));

		public string GetGenericMethodParameter(object? genericContext, int index) => Record(
			RawIdentityNode("generic-method-parameter", index.ToString(CultureInfo.InvariantCulture)));

		public string GetGenericTypeParameter(object? genericContext, int index) => Record(
			RawIdentityNode("generic-type-parameter", index.ToString(CultureInfo.InvariantCulture)));

		public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
		{
			Modifiers.Add($"{(isRequired ? "modreq" : "modopt")}|{modifier}|{unmodifiedType}");
			return Record(unmodifiedType);
		}

		public string GetPinnedType(string elementType) => Record(RawIdentityNode("pinned", elementType));

		public string GetPointerType(string elementType) => Record(RawIdentityNode("pointer", elementType));

		public string GetPrimitiveType(PrimitiveTypeCode typeCode) => Record(typeCode switch
		{
			PrimitiveTypeCode.Boolean => RawTypeKey(typeof(bool)),
			PrimitiveTypeCode.Byte => RawTypeKey(typeof(byte)),
			PrimitiveTypeCode.Char => RawTypeKey(typeof(char)),
			PrimitiveTypeCode.Double => RawTypeKey(typeof(double)),
			PrimitiveTypeCode.Int16 => RawTypeKey(typeof(short)),
			PrimitiveTypeCode.Int32 => RawTypeKey(typeof(int)),
			PrimitiveTypeCode.Int64 => RawTypeKey(typeof(long)),
			PrimitiveTypeCode.IntPtr => RawTypeKey(typeof(IntPtr)),
			PrimitiveTypeCode.Object => RawTypeKey(typeof(object)),
			PrimitiveTypeCode.SByte => RawTypeKey(typeof(sbyte)),
			PrimitiveTypeCode.Single => RawTypeKey(typeof(float)),
			PrimitiveTypeCode.String => RawTypeKey(typeof(string)),
			PrimitiveTypeCode.TypedReference => RawTypeKey(typeof(TypedReference)),
			PrimitiveTypeCode.UInt16 => RawTypeKey(typeof(ushort)),
			PrimitiveTypeCode.UInt32 => RawTypeKey(typeof(uint)),
			PrimitiveTypeCode.UInt64 => RawTypeKey(typeof(ulong)),
			PrimitiveTypeCode.UIntPtr => RawTypeKey(typeof(UIntPtr)),
			PrimitiveTypeCode.Void => RawTypeKey(typeof(void)),
			_ => throw new Xunit.Sdk.XunitException($"Unexpected primitive signature type {typeCode}."),
		});

		public string GetSZArrayType(string elementType) => Record(RawSzArrayKey(elementType));

		public string GetSystemType() => Record(RawTypeKey(typeof(Type)));

		public bool IsSystemType(string type) =>
			type == RawTypeKey(typeof(Type)) || type == RawEntityTypeKey(typeof(Type));

		public string GetTypeFromSerializedName(string name)
		{
			return Record(MapSerializedTypeName(name));
		}

		public PrimitiveTypeCode GetUnderlyingEnumType(string type) => type switch
		{
			"System.Diagnostics.DebuggerBrowsableState" => PrimitiveTypeCode.Int32,
			_ => PrimitiveTypeCode.Int32,
		};

		public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
		{
			EntityHandles.Add(handle);
			VerifyTypeDefinitionScope(metadata, handle);
			return Record(RawTypeKindKey(
				TypeDefinitionIdentity(metadata, handle, new HashSet<TypeDefinitionHandle>()),
				rawTypeKind));
		}

		public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
		{
			EntityHandles.Add(handle);
			VerifyTypeReferenceScope(metadata, handle);
			return Record(RawTypeKindKey(
				TypeReferenceIdentity(metadata, handle, new HashSet<TypeReferenceHandle>()),
				rawTypeKind));
		}

		public string GetTypeFromSpecification(
			MetadataReader metadata,
			object? genericContext,
			TypeSpecificationHandle handle,
			byte rawTypeKind)
		{
			EntityHandles.Add(handle);
			var graphViolations = new HashSet<string>(StringComparer.Ordinal);
			VerifyRawTypeSpecificationGraph(
				metadata,
				handle,
				_graphActiveTypeSpecifications,
				_graphCompletedTypeSpecifications,
				graphViolations,
				VerifyTypeScope);
			RawTypeSpecificationViolations.UnionWith(graphViolations);
			if (graphViolations.Contains("TYPE_SPEC_CYCLE"))
			{
				throw new Xunit.Sdk.XunitException("A cyclic TypeSpec graph is forbidden.");
			}
			if (!_activeTypeSpecifications.Add(handle))
			{
				throw new Xunit.Sdk.XunitException("A cyclic TypeSpec graph is forbidden.");
			}
			try
			{
				string decoded = metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
				return Record(RawTypeSpecificationKindKey(decoded, rawTypeKind));
			}
			finally
			{
				_activeTypeSpecifications.Remove(handle);
			}
		}

		private string Record(string type)
		{
			Types.Add(type);
			return type;
		}

		private void VerifyTypeScope(EntityHandle handle)
		{
			EntityHandles.Add(handle);
			switch (handle.Kind)
			{
				case HandleKind.TypeDefinition:
					VerifyTypeDefinitionScope(reader, (TypeDefinitionHandle)handle);
					break;
				case HandleKind.TypeReference:
					VerifyTypeReferenceScope(reader, (TypeReferenceHandle)handle);
					break;
			}
		}

		private void VerifyTypeDefinitionScope(MetadataReader metadata, TypeDefinitionHandle handle)
		{
			EntityHandles.Add(handle);
			if (!IsValidRow(metadata, handle, TableIndex.TypeDef))
			{
				RawTypeScopeViolations.Add("UNRESOLVED_TYPE_ROOT");
				return;
			}
			if (_completedTypeDefinitions.Contains(handle))
			{
				return;
			}
			if (!_activeTypeDefinitions.Add(handle))
			{
				RawTypeScopeViolations.Add("TYPE_SCOPE_CYCLE");
				return;
			}
			try
			{
				TypeDefinition definition = metadata.GetTypeDefinition(handle);
				TypeDefinitionHandle declaringType = definition.GetDeclaringType();
				TypeAttributes visibility = definition.Attributes & TypeAttributes.VisibilityMask;
				bool nestedVisibility = visibility is
					TypeAttributes.NestedPublic or TypeAttributes.NestedPrivate or
					TypeAttributes.NestedFamily or TypeAttributes.NestedAssembly or
					TypeAttributes.NestedFamANDAssem or TypeAttributes.NestedFamORAssem;
				if (nestedVisibility != !declaringType.IsNil)
				{
					RawTypeScopeViolations.Add("UNEXPECTED_TYPE_SCOPE");
				}
				if (!declaringType.IsNil && !string.IsNullOrEmpty(metadata.GetString(definition.Namespace)))
				{
					RawTypeScopeViolations.Add("UNEXPECTED_TYPE_SCOPE");
				}
				if (!declaringType.IsNil)
				{
					VerifyTypeDefinitionScope(metadata, declaringType);
				}
			}
			catch (BadImageFormatException)
			{
				RawTypeScopeViolations.Add("UNRESOLVED_TYPE_ROOT");
			}
			finally
			{
				_activeTypeDefinitions.Remove(handle);
				_completedTypeDefinitions.Add(handle);
			}
		}

		private void VerifyTypeReferenceScope(MetadataReader metadata, TypeReferenceHandle handle)
		{
			EntityHandles.Add(handle);
			if (!IsValidRow(metadata, handle, TableIndex.TypeRef))
			{
				RawTypeScopeViolations.Add("UNRESOLVED_TYPE_ROOT");
				return;
			}
			if (_completedTypeReferences.Contains(handle))
			{
				return;
			}
			if (!_activeTypeReferences.Add(handle))
			{
				RawTypeScopeViolations.Add("TYPE_SCOPE_CYCLE");
				return;
			}
			try
			{
				EntityHandle scope = metadata.GetTypeReference(handle).ResolutionScope;
				if (scope.IsNil)
				{
					RawTypeScopeViolations.Add("UNEXPECTED_TYPE_SCOPE");
					return;
				}
				switch (scope.Kind)
				{
					case HandleKind.TypeReference:
						if (!string.IsNullOrEmpty(metadata.GetString(metadata.GetTypeReference(handle).Namespace)))
						{
							RawTypeScopeViolations.Add("UNEXPECTED_TYPE_SCOPE");
						}
						VerifyTypeReferenceScope(metadata, (TypeReferenceHandle)scope);
						break;
					case HandleKind.AssemblyReference:
						EntityHandles.Add(scope);
						VerifyScopeEndpoint(metadata, scope, TableIndex.AssemblyRef);
						break;
					case HandleKind.ModuleReference:
						EntityHandles.Add(scope);
						VerifyScopeEndpoint(metadata, scope, TableIndex.ModuleRef);
						break;
					case HandleKind.ModuleDefinition:
						EntityHandles.Add(scope);
						if (MetadataTokens.GetRowNumber(scope) != 1)
						{
							RawTypeScopeViolations.Add("UNRESOLVED_TYPE_ROOT");
						}
						break;
					default:
						RawTypeScopeViolations.Add("UNEXPECTED_TYPE_SCOPE");
						break;
				}
			}
			catch (BadImageFormatException)
			{
				RawTypeScopeViolations.Add("UNRESOLVED_TYPE_ROOT");
			}
			finally
			{
				_activeTypeReferences.Remove(handle);
				_completedTypeReferences.Add(handle);
			}
		}

		private void VerifyScopeEndpoint(MetadataReader metadata, EntityHandle handle, TableIndex table)
		{
			if (!IsValidRow(metadata, handle, table))
			{
				RawTypeScopeViolations.Add("UNRESOLVED_TYPE_ROOT");
			}
		}

		private static bool IsValidRow(MetadataReader metadata, EntityHandle handle, TableIndex table)
		{
			int row = MetadataTokens.GetRowNumber(handle);
			return row > 0 && row <= metadata.GetTableRowCount(table);
		}

		private static string TypeDefinitionIdentity(
			MetadataReader metadata,
			TypeDefinitionHandle handle,
			ISet<TypeDefinitionHandle> active)
		{
			if (!IsValidRow(metadata, handle, TableIndex.TypeDef))
			{
				return RawIdentityNode(
					"invalid-typedef",
					MetadataTokens.GetRowNumber(handle).ToString(CultureInfo.InvariantCulture));
			}
			return RawIdentityNode(
				"typedef",
				metadata.GetString(metadata.GetModuleDefinition().Name),
				MetadataTokens.GetRowNumber(handle).ToString(CultureInfo.InvariantCulture),
				TypeDefinitionNameIdentity(metadata, handle, active));
		}

		private static string TypeDefinitionNameIdentity(
			MetadataReader metadata,
			TypeDefinitionHandle handle,
			ISet<TypeDefinitionHandle> active)
		{
			if (!IsValidRow(metadata, handle, TableIndex.TypeDef) || !active.Add(handle))
			{
				return RawIdentityNode(
					"invalid-typedef-name",
					MetadataTokens.GetRowNumber(handle).ToString(CultureInfo.InvariantCulture));
			}
			try
			{
				TypeDefinition definition = metadata.GetTypeDefinition(handle);
				string name = metadata.GetString(definition.Name);
				TypeDefinitionHandle declaringType = definition.GetDeclaringType();
				string @namespace = metadata.GetString(definition.Namespace);
				return RawIdentityNode(
					"typedef-name",
					declaringType.IsNil ? string.Empty : TypeDefinitionNameIdentity(metadata, declaringType, active),
					declaringType.IsNil ? @namespace : string.Empty,
					name);
			}
			finally
			{
				active.Remove(handle);
			}
		}

		private static string TypeReferenceIdentity(
			MetadataReader metadata,
			TypeReferenceHandle handle,
			ISet<TypeReferenceHandle> active)
		{
			if (!IsValidRow(metadata, handle, TableIndex.TypeRef) || !active.Add(handle))
			{
				return RawIdentityNode(
					"invalid-typeref",
					MetadataTokens.GetRowNumber(handle).ToString(CultureInfo.InvariantCulture));
			}
			try
			{
				TypeReference reference = metadata.GetTypeReference(handle);
				string name = metadata.GetString(reference.Name);
				string @namespace = metadata.GetString(reference.Namespace);
				EntityHandle scope = reference.ResolutionScope;
				string scopeIdentity = scope.Kind switch
				{
					HandleKind.TypeReference => TypeReferenceIdentity(metadata, (TypeReferenceHandle)scope, active),
					HandleKind.AssemblyReference => RawIdentityNode(
						"assembly-scope",
						AssemblyReferenceIdentity(metadata, (AssemblyReferenceHandle)scope)),
					HandleKind.ModuleReference => RawIdentityNode(
						"module-reference-scope",
						ScopeName(metadata, scope, TableIndex.ModuleRef)),
					HandleKind.ModuleDefinition => RawIdentityNode(
						"module-scope",
						metadata.GetString(metadata.GetModuleDefinition().Name)),
					_ => RawIdentityNode("invalid-scope", scope.Kind.ToString()),
				};
				return RawIdentityNode("typeref", scopeIdentity, @namespace, name);
			}
			finally
			{
				active.Remove(handle);
			}
		}

		private static string ScopeName(MetadataReader metadata, EntityHandle handle, TableIndex table) =>
			IsValidRow(metadata, handle, table)
				? metadata.GetString(metadata.GetModuleReference((ModuleReferenceHandle)handle).Name)
				: MetadataTokens.GetRowNumber(handle).ToString(CultureInfo.InvariantCulture);

		private static string AssemblyReferenceIdentity(MetadataReader metadata, AssemblyReferenceHandle handle)
			=> MetadataAssemblyReferenceIdentity(metadata, handle);

		private static string MapSerializedTypeName(string name)
		{
			return name switch
			{
				string value when value == typeof(LiquidWalletObservationBatch).AssemblyQualifiedName => "$BATCH",
				string value when value == typeof(LiquidWalletTransactionObservation).AssemblyQualifiedName => "$OBSERVATION",
				_ => $"[serialized:{name}]",
			};
		}
	}

	private sealed class RawSignatureCursor(
		byte[] bytes,
		ISet<string> violations,
		Action<EntityHandle>? typeReferenceVisitor = null,
		Action<EntityHandle, byte>? signatureTypeReferenceVisitor = null)
	{
		private int _offset;

		public bool AtEnd => _offset == bytes.Length;

		public void ConsumeMethod(RawSignaturePolicy policy)
		{
			byte header = ReadByte();
			byte expectedHeader = (byte)((policy.GenericArity > 0 ? 0x10 : 0x00) | (policy.IsInstance ? 0x20 : 0x00));
			bool classifiedHeaderDifference = false;
			int convention = header & 0x0f;
			if (convention is 1 or 2 or 3 or 4 or 9 or 11)
			{
				violations.Add("UNMANAGED_CONVENTION");
				classifiedHeaderDifference = true;
			}
			else if (convention == 5)
			{
				violations.Add("VARARGS");
				classifiedHeaderDifference = true;
			}
			else if (convention != 0)
			{
				violations.Add("METHOD_HEADER");
				classifiedHeaderDifference = true;
			}
			if ((header & 0x80) != 0)
			{
				violations.Add("METHOD_HEADER");
				classifiedHeaderDifference = true;
			}
			bool generic = (header & 0x10) != 0;
			bool instance = (header & 0x20) != 0;
			if (generic != (policy.GenericArity != 0))
			{
				violations.Add("GENERIC_BIT");
				classifiedHeaderDifference = true;
			}
			if ((header & 0x40) != 0)
			{
				violations.Add("EXPLICIT_THIS");
				classifiedHeaderDifference = true;
			}
			if (instance != policy.IsInstance)
			{
				violations.Add("INSTANCE_BIT");
				classifiedHeaderDifference = true;
			}
			if (header != expectedHeader && !classifiedHeaderDifference)
			{
				violations.Add("METHOD_HEADER");
			}
			int arity = generic ? checked((int)ReadCompressedUInt32()) : 0;
			if (generic && arity != policy.GenericArity)
			{
				violations.Add("GENERIC_ARITY");
			}
			int parameterCount = checked((int)ReadCompressedUInt32());
			if (parameterCount != policy.ParameterCount)
			{
				violations.Add("PARAMETER_COUNT");
			}
			ConsumeType(allowVoid: true);
			for (int index = 0; index < parameterCount; index++)
			{
				if (PeekByte() == 0x41)
				{
					_ = ReadByte();
					violations.Add("SENTINEL");
				}
				ConsumeType(allowVoid: false);
			}
		}

		public void ConsumeField()
		{
			if (ReadByte() != 0x06)
			{
				violations.Add("FIELD_HEADER");
			}
			ConsumeType(allowVoid: false);
		}

		public void ConsumeProperty(RawSignaturePolicy policy)
		{
			byte header = ReadByte();
			if (header != 0x28)
			{
				violations.Add("PROPERTY_HEADER");
			}
			if ((header & 0x20) == 0 || !policy.IsInstance)
			{
				violations.Add("INSTANCE_BIT");
			}
			if ((header & 0x40) != 0)
			{
				violations.Add("EXPLICIT_THIS");
			}
			int parameterCount = checked((int)ReadCompressedUInt32());
			if (parameterCount != policy.ParameterCount)
			{
				violations.Add("PARAMETER_COUNT");
			}
			ConsumeType(allowVoid: false);
			for (int index = 0; index < parameterCount; index++)
			{
				ConsumeType(allowVoid: false);
			}
		}

		public void ConsumeLocals(int expectedCount)
		{
			if (ReadByte() != 0x07)
			{
				violations.Add("LOCAL_HEADER");
			}
			int count = checked((int)ReadCompressedUInt32());
			if (count != expectedCount)
			{
				violations.Add("LOCAL_COUNT");
			}
			for (int index = 0; index < count; index++)
			{
				if (PeekByte() == 0x45)
				{
					_ = ReadByte();
					violations.Add("PINNED_LOCAL");
				}
				ConsumeType(allowVoid: false);
			}
		}

		public void ConsumeMethodSpecification(int parentArity)
		{
			if (ReadByte() != 0x0a)
			{
				violations.Add("METHOD_SPEC_HEADER");
			}
			int argumentCount = checked((int)ReadCompressedUInt32());
			if (argumentCount != parentArity)
			{
				violations.Add("GENERIC_ARITY");
			}
			for (int index = 0; index < argumentCount; index++)
			{
				ConsumeType(allowVoid: false);
			}
		}

		public void ConsumeType(bool allowVoid)
		{
			byte element = ReadByte();
			switch (element)
			{
				case 0x01:
					if (!allowVoid) { violations.Add("VOID_TYPE"); }
					return;
				case >= 0x02 and <= 0x0e:
				case 0x16:
				case 0x18:
				case 0x19:
				case 0x1c:
					return;
				case 0x0f:
					violations.Add("POINTER_TYPE");
					ConsumeType(allowVoid: true);
					return;
				case 0x10:
					violations.Add("BYREF_TYPE");
					ConsumeType(allowVoid: false);
					return;
				case 0x11:
				case 0x12:
					ConsumeTypeReference(element);
					return;
				case 0x13:
				case 0x1e:
					_ = ReadCompressedUInt32();
					return;
				case 0x14:
					ConsumeType(allowVoid: false);
					_ = ReadCompressedUInt32();
					uint sizeCount = ReadCompressedUInt32();
					for (uint index = 0; index < sizeCount; index++) { _ = ReadCompressedUInt32(); }
					uint boundCount = ReadCompressedUInt32();
					for (uint index = 0; index < boundCount; index++) { _ = ReadCompressedInt32(); }
					return;
				case 0x15:
					byte kind = ReadByte();
					if (kind is not 0x11 and not 0x12) { throw new InvalidOperationException(); }
					ConsumeTypeReference(kind);
					uint argumentCount = ReadCompressedUInt32();
					for (uint index = 0; index < argumentCount; index++) { ConsumeType(allowVoid: false); }
					return;
				case 0x1b:
					violations.Add("FUNCTION_POINTER");
					ConsumeMethod(new RawSignaturePolicy(RawSignatureKind.Method, false, 0, 0));
					return;
				case 0x1d:
					ConsumeType(allowVoid: false);
					return;
				case 0x1f:
				case 0x20:
					violations.Add("CUSTOM_MODIFIER");
					ConsumeTypeReference();
					ConsumeType(allowVoid);
					return;
				case 0x41:
					violations.Add("SENTINEL");
					return;
				case 0x45:
					violations.Add("PINNED_LOCAL");
					ConsumeType(allowVoid: false);
					return;
				default:
					throw new InvalidOperationException();
			}
		}

		private byte PeekByte() => _offset < bytes.Length ? bytes[_offset] : throw new InvalidOperationException();
		private byte ReadByte() => _offset < bytes.Length ? bytes[_offset++] : throw new InvalidOperationException();

		private void ConsumeTypeReference(byte rawTypeKind = 0)
		{
			uint codedIndex = ReadCompressedUInt32();
			int row = checked((int)(codedIndex >> 2));
			EntityHandle handle = (codedIndex & 3) switch
			{
				0 => MetadataTokens.TypeDefinitionHandle(row),
				1 => MetadataTokens.TypeReferenceHandle(row),
				2 => MetadataTokens.TypeSpecificationHandle(row),
				_ => throw new InvalidOperationException(),
			};
			typeReferenceVisitor?.Invoke(handle);
			signatureTypeReferenceVisitor?.Invoke(handle, rawTypeKind);
		}

		private uint ReadCompressedUInt32()
		{
			(uint value, _, int width) = ReadCompressedPayload();
			if (width == 2 && value < 0x80 || width == 4 && value < 0x4000)
			{
				violations.Add("NON_CANONICAL_COMPRESSED_INTEGER");
			}
			return value;
		}

		private int ReadCompressedInt32()
		{
			(uint raw, int dataBits, int width) = ReadCompressedPayload();
			int value = checked((int)(raw >> 1));
			if ((raw & 1) != 0)
			{
				value |= -1 << (dataBits - 1);
			}
			if (width == 2 && value is >= -64 and <= 63 ||
				width == 4 && value is >= -8192 and <= 8191)
			{
				violations.Add("NON_CANONICAL_COMPRESSED_INTEGER");
			}
			return value;
		}

		private (uint Value, int DataBits, int Width) ReadCompressedPayload()
		{
			byte first = ReadByte();
			if ((first & 0x80) == 0)
			{
				return (first, 7, 1);
			}
			if ((first & 0xc0) == 0x80)
			{
				return ((uint)(((first & 0x3f) << 8) | ReadByte()), 14, 2);
			}
			if ((first & 0xe0) == 0xc0)
			{
				return ((uint)(((first & 0x1f) << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte()), 29, 4);
			}
			throw new InvalidOperationException();
		}
	}

	private sealed class DirectedGraph
	{
		private readonly int _entry;
		private readonly IReadOnlyDictionary<int, int[]> _edges;

		public DirectedGraph(int entry, IEnumerable<(int From, int To)> edges)
		{
			_entry = entry;
			_edges = edges
				.GroupBy(edge => edge.From)
				.ToDictionary(group => group.Key, group => group.Select(edge => edge.To).Distinct().ToArray());
		}

		public static DirectedGraph FromInstructions(IReadOnlyList<Instruction> instructions)
		{
			IReadOnlyDictionary<int, int> indices = instructions
				.Select((instruction, index) => (instruction.Offset, index))
				.ToDictionary(pair => pair.Offset, pair => pair.index);
			var edges = new List<(int From, int To)>();
			for (int index = 0; index < instructions.Count; index++)
			{
				Instruction instruction = instructions[index];
				if (instruction.OpCode.FlowControl == FlowControl.Branch)
				{
					edges.Add((index, indices[(int)instruction.Operand!]));
					continue;
				}
				if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
				{
					if (instruction.Operand is int target)
					{
						edges.Add((index, indices[target]));
					}
					else
					{
						edges.AddRange(((int[])instruction.Operand!).Select(targetOffset => (index, indices[targetOffset])));
					}
					if (index + 1 < instructions.Count)
					{
						edges.Add((index, index + 1));
					}
					continue;
				}
				if (instruction.OpCode.FlowControl is not FlowControl.Return and not FlowControl.Throw &&
					index + 1 < instructions.Count)
				{
					edges.Add((index, index + 1));
				}
			}
			return new DirectedGraph(0, edges);
		}

		public bool Dominates(int dominator, int node)
			=> DominatesFrom(_entry, dominator, node);

		public bool DominatesFrom(int entry, int dominator, int node)
		{
			if (dominator == entry)
			{
				return true;
			}
			var pending = new Queue<int>();
			var visited = new HashSet<int> { dominator };
			pending.Enqueue(entry);
			while (pending.TryDequeue(out int current))
			{
				if (!visited.Add(current))
				{
					continue;
				}
				if (current == node)
				{
					return false;
				}
				if (_edges.TryGetValue(current, out int[]? successors))
				{
					foreach (int successor in successors)
					{
						pending.Enqueue(successor);
					}
				}
			}
			return true;
		}

		public int[] Successors(int node) => _edges.GetValueOrDefault(node, []);

		public DirectedGraph WithAdditionalEdge(int from, int to) => new(
			_entry,
			_edges.SelectMany(pair => pair.Value.Select(target => (From: pair.Key, To: target)))
				.Append((from, to)));

		public bool CanReach(int start, int target) => CanReach(start, node => node == target);

		public bool CanReach(int start, Func<int, bool> predicate)
		{
			var pending = new Queue<int>();
			var visited = new HashSet<int>();
			pending.Enqueue(start);
			while (pending.TryDequeue(out int current))
			{
				if (!visited.Add(current))
				{
					continue;
				}
				if (predicate(current))
				{
					return true;
				}
				foreach (int successor in Successors(current))
				{
					pending.Enqueue(successor);
				}
			}
			return false;
		}
	}

	private sealed class ManagedValueFlow
	{
		private readonly ManagedFlowValue[]?[] _popped;

		private ManagedValueFlow(ManagedFlowValue[]?[] popped, bool isValid)
		{
			_popped = popped;
			IsValid = isValid;
		}

		public bool IsValid { get; }

		public IReadOnlyList<ManagedFlowValue>? PoppedAt(int instruction) => _popped[instruction];

		public static ManagedValueFlow Analyze(MethodInfo method, Instruction[] instructions)
		{
			DirectedGraph graph = DirectedGraph.FromInstructions(instructions);
			var entries = new ManagedFlowState?[instructions.Length];
			var popped = new ManagedFlowValue[]?[instructions.Length];
			var pending = new Queue<int>();
			entries[0] = new ManagedFlowState([], new Dictionary<int, ManagedStoredLocal>());
			pending.Enqueue(0);
			bool valid = true;
			while (pending.TryDequeue(out int index))
			{
				ManagedFlowState entry = entries[index]!;
				if (!TryTransfer(method, instructions[index], index, entry, out ManagedFlowState? exit, out ManagedFlowValue[] consumed))
				{
					valid = false;
					continue;
				}
				if (!TryMergeValues(popped[index], consumed, out ManagedFlowValue[] mergedConsumed))
				{
					valid = false;
				}
				else
				{
					popped[index] = mergedConsumed;
				}
				foreach (int successor in graph.Successors(index))
				{
					if (!TryMergeStates(entries[successor], exit, out ManagedFlowState merged, out bool changed))
					{
						valid = false;
						continue;
					}
					if (changed)
					{
						entries[successor] = merged;
						pending.Enqueue(successor);
					}
				}
			}
			return new ManagedValueFlow(popped, valid);
		}

		private static bool TryTransfer(
			MethodInfo method,
			Instruction instruction,
			int index,
			ManagedFlowState entry,
			[NotNullWhen(true)] out ManagedFlowState? exit,
			out ManagedFlowValue[] consumed)
		{
			var stack = entry.Stack.ToList();
			var locals = entry.Locals.ToDictionary(pair => pair.Key, pair => pair.Value);
			int popCount = PopCount(method, instruction);
			if (popCount > stack.Count)
			{
				exit = null;
				consumed = [];
				return false;
			}
			consumed = stack.Skip(stack.Count - popCount).ToArray();
			stack.RemoveRange(stack.Count - popCount, popCount);

			if (TryGetArgumentIndex(instruction, out int argument))
			{
				stack.Add(new ManagedFlowValue(ManagedFlowValueKind.Argument, Instruction: argument));
			}
			else if (instruction.OpCode == OpCodes.Ldstr)
			{
				stack.Add(new ManagedFlowValue(
					ManagedFlowValueKind.String,
					Text: ResolveInstructionString(method, instruction)));
			}
			else if (instruction.OpCode == OpCodes.Ldnull)
			{
				stack.Add(new ManagedFlowValue(ManagedFlowValueKind.Null));
			}
			else if (TryGetLocalIndex(instruction, load: true, out int loadedLocal))
			{
				ManagedStoredLocal stored = locals.GetValueOrDefault(loadedLocal, ManagedStoredLocal.Unknown);
				stack.Add(new ManagedFlowValue(
					ManagedFlowValueKind.LocalVersion,
					Local: loadedLocal,
					Store: stored.Store,
					StoredValue: stored.Value));
			}
			else if (TryGetLocalIndex(instruction, load: false, out int storedLocal))
			{
				locals[storedLocal] = new ManagedStoredLocal(index, consumed.Single());
			}
			else if (TryGetInt32Constant(instruction, out int constant))
			{
				stack.Add(new ManagedFlowValue(ManagedFlowValueKind.Constant, Constant: constant));
			}
			else if (instruction.OpCode == OpCodes.Dup)
			{
				ManagedFlowValue value = consumed.Single();
				stack.Add(value);
				stack.Add(value);
			}
			else if (instruction.OpCode == OpCodes.Add_Ovf)
			{
				stack.Add(new ManagedFlowValue(ManagedFlowValueKind.CheckedAdd, Instruction: index));
			}
			else if (instruction.OpCode == OpCodes.Cgt)
			{
				stack.Add(new ManagedFlowValue(ManagedFlowValueKind.GreaterThan, Instruction: index));
			}
			else
			{
				int pushCount = PushCount(method, instruction);
				ManagedFlowValue pushed = instruction.OpCode.OperandType == OperandType.InlineMethod &&
					ResolveMember(method, instruction).Name is "get_InputCount" or "get_OwnedOutputCount"
					? new ManagedFlowValue(ManagedFlowValueKind.Getter, Instruction: index)
					: new ManagedFlowValue(ManagedFlowValueKind.Producer, Instruction: index);
				for (int pushedIndex = 0; pushedIndex < pushCount; pushedIndex++)
				{
					stack.Add(pushed);
				}
			}
			exit = new ManagedFlowState(stack.ToArray(), locals);
			return true;
		}

		private static bool TryMergeStates(
			ManagedFlowState? existing,
			ManagedFlowState incoming,
			out ManagedFlowState merged,
			out bool changed)
		{
			if (existing is null)
			{
				merged = incoming;
				changed = true;
				return true;
			}
			if (!TryMergeValues(existing.Stack, incoming.Stack, out ManagedFlowValue[] mergedStack))
			{
				merged = existing;
				changed = false;
				return false;
			}
			var mergedLocals = new Dictionary<int, ManagedStoredLocal>();
			foreach (int local in existing.Locals.Keys.Concat(incoming.Locals.Keys).Distinct())
			{
				ManagedStoredLocal left = existing.Locals.GetValueOrDefault(local, ManagedStoredLocal.Unknown);
				ManagedStoredLocal right = incoming.Locals.GetValueOrDefault(local, ManagedStoredLocal.Unknown);
				mergedLocals[local] = left == right
					? left
					: new ManagedStoredLocal(-1, MergeValue(left.Value, right.Value));
			}
			var mergedState = new ManagedFlowState(mergedStack, mergedLocals);
			changed = !existing.Stack.SequenceEqual(mergedState.Stack) ||
				existing.Locals.Count != mergedState.Locals.Count ||
				existing.Locals.Any(pair => !mergedState.Locals.TryGetValue(pair.Key, out ManagedStoredLocal? value) || value != pair.Value);
			merged = mergedState;
			return true;
		}

		private static bool TryMergeValues(
			IReadOnlyList<ManagedFlowValue>? existing,
			IReadOnlyList<ManagedFlowValue> incoming,
			out ManagedFlowValue[] merged)
		{
			if (existing is null)
			{
				merged = incoming.ToArray();
				return true;
			}
			if (existing.Count != incoming.Count)
			{
				merged = [];
				return false;
			}
			merged = existing.Zip(incoming, MergeValue).ToArray();
			return true;
		}

		private static ManagedFlowValue MergeValue(ManagedFlowValue left, ManagedFlowValue right)
		{
			if (left == right)
			{
				return left;
			}
			if (left.Kind == ManagedFlowValueKind.LocalVersion &&
				right.Kind == ManagedFlowValueKind.LocalVersion && left.Local == right.Local)
			{
				return new ManagedFlowValue(
					ManagedFlowValueKind.LocalVersion,
					Local: left.Local,
					Store: -1,
					StoredValue: ManagedFlowValue.Unknown);
			}
			return ManagedFlowValue.Unknown;
		}
	}

	private sealed class InstrumentedReadOnlyList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
	{
		private readonly int[] _indexReadCounts = new int[values.Count];

		public int Count
		{
			get
			{
				CountReadCount++;
				return values.Count;
			}
		}

		public int CountReadCount { get; private set; }
		public int ElementAccessCount { get; private set; }
		public IReadOnlyList<int> IndexReadCounts => _indexReadCounts;

		public T this[int index]
		{
			get
			{
				ElementAccessCount++;
				_indexReadCounts[index]++;
				return values[index];
			}
		}

		public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumeration is forbidden.");
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class GeneratedReadOnlyList<T>(int count, Func<int, T> valueFactory) : IReadOnlyList<T>
	{
		public int Count { get; } = count;

		public T this[int index] => index >= 0 && index < Count
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

	private sealed class ReentrantReadOnlyList<T>(IReadOnlyList<T> values, T replacement) : IReadOnlyList<T>
	{
		private readonly int[] _indexReadCounts = new int[values.Count];

		public int Count
		{
			get
			{
				CountReadCount++;
				return values.Count;
			}
		}

		public int CountReadCount { get; private set; }
		public IReadOnlyList<int> IndexReadCounts => _indexReadCounts;

		public T this[int index]
		{
			get
			{
				_indexReadCounts[index]++;
				return _indexReadCounts[index] == 1 ? values[index] : replacement;
			}
		}

		public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumeration is forbidden.");
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class UntouchableReadOnlyList<T>(int count) : IReadOnlyList<T>
	{
		public int Count
		{
			get
			{
				CountReadCount++;
				return count;
			}
		}

		public int CountReadCount { get; private set; }
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

	private sealed class CallerThrowingReadOnlyList<T>(string message) : IReadOnlyList<T>
	{
		public int Count => throw new InvalidOperationException(message);
		public T this[int index] => throw new InvalidOperationException(message);
		public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException(message);
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class IndexerThrowingReadOnlyList<T>(string message) : IReadOnlyList<T>
	{
		public int Count => 1;
		public T this[int index] => throw new InvalidOperationException(message);
		public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException(message);
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private static class ForbiddenChannelFixtures
	{
		private static LiquidWalletTransactionObservation? AllowedObservation = null;
		private static LiquidWalletState? ForbiddenState = null;

		public static object? AllowedFieldChannel() => AllowedObservation;
		public static object? ForbiddenFieldChannel() => ForbiddenState;
		public static bool AllowedTypeChannel(object value) => value is LiquidWalletTransactionObservation;
		public static bool ForbiddenTypeChannel(object value) => value is LiquidWalletState;
		public static Type AllowedTokenChannel() => typeof(LiquidWalletTransactionObservation);
		public static Type ForbiddenTokenChannel() => typeof(LiquidWalletState);

		public static void AllowedCatchChannel()
		{
			try
			{
				MayThrow();
			}
			catch (Exception)
			{
			}
		}

		public static void ForbiddenCatchChannel()
		{
			try
			{
				MayThrow();
			}
			catch (ElementsRpcException)
			{
			}
		}

		public static string AllowedStringChannel() => "allowed-batch-literal";
		public static string ForbiddenStringChannel() => "not-an-authorized-batch-literal";
		private static void MayThrow() { }
	}

	[AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
	private sealed class TypeCarrierAttribute(Type value) : Attribute
	{
		public Type Value { get; } = value;
		public Type? Target { get; set; }
		public Type[]? Targets { get; set; }
	}

	private sealed class ValidReflectionMetadataFixture
	{
		public const int Value = 1;
		public int Property { get; }
		public void Method(int value) => GC.KeepAlive(value);
	}

	private sealed class NotSerializedMetadataFixture
	{
		[field: NonSerialized]
		public int Property { get; } = 0;
	}

	private sealed class UnexpectedCallableFlagsFixture
	{
		[SpecialName]
		public void Method() { }
	}

	private sealed class SynchronizedMetadataFixture
	{
		[MethodImpl(MethodImplOptions.Synchronized)]
		public void Method() { }
	}

	[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
	private sealed class LayoutMetadataFixture;

	private sealed class OptionalParameterMetadataFixture
	{
		public void Method([Optional, DefaultParameterValue(7)] int value) => GC.KeepAlive(value);
	}

	private sealed class MarshalParameterMetadataFixture
	{
		public void Method([MarshalAs(UnmanagedType.I4)] int value) => GC.KeepAlive(value);
	}

	private sealed class IndexedPropertyMetadataFixture
	{
		public int this[int index] => index;
	}

	private sealed class MutatedLiteralMetadataFixture
	{
		public const int Value = 2;
	}

	[Serializable]
	private sealed class SerializableMetadataFixture;

	private sealed class VarArgsMetadataFixture
	{
		public static void Method(__arglist) { }
	}

	private static class RawMethodDefinitionExpectationFixture
	{
		public static void Method() { }
		public static void GenericMethod<T>() { }
	}

	private static class RawMethodDefinitionTypeSpecExpectationFixture
	{
		public static Type Method() => typeof(object);
	}

	private static class RawMethodDefinitionPrimitiveInt32ExpectationFixture
	{
		public static int Method() => 0;
	}

	private static class RawMethodDefinitionGenericExpectationFixture
	{
		public static IList<int> Method() => [];
	}

	private static class RawMethodDefinitionObservationExpectationFixture
	{
		public static LiquidWalletTransactionObservation Method() => null!;
	}

	private static class RawMethodDefinitionNestedTypeExpectationFixture
	{
		public static Environment.SpecialFolder Method() => default;
	}

	private sealed class RawMethodDefinitionInstanceExpectationFixture
	{
		public void Method() { }
	}

	private static class RawMethodDefinitionParameterizedExpectationFixture
	{
		public static void Method(int value) => GC.KeepAlive(value);
	}

	private sealed class RawPropertyExpectationFixture
	{
		public int Property => 0;
	}

	private sealed class RawMethodDefinitionConstructorExpectationFixture
	{
		private RawMethodDefinitionConstructorExpectationFixture() { }
	}

	private sealed class TypeCarryingMethodAttributeFixture
	{
		[TypeCarrier(typeof(LiquidWalletState))]
		public void Method() { }
	}

	private sealed class TypeCarryingFieldAttributeFixture
	{
		[TypeCarrier(typeof(LiquidWalletState))]
		private int _field = 0;
		public void Read() => GC.KeepAlive(_field);
	}

	private sealed class TypeCarryingConstructorAttributeFixture
	{
		[TypeCarrier(typeof(LiquidWalletState))]
		public TypeCarryingConstructorAttributeFixture() { }
	}

	private sealed class TypeCarryingPropertyAttributeFixture
	{
		[TypeCarrier(typeof(LiquidWalletState))]
		public int Property => 0;
	}

	private sealed class TypeCarryingParameterAttributeFixture
	{
		public void Method([TypeCarrier(typeof(LiquidWalletState))] int value) => GC.KeepAlive(value);
	}

	private sealed class TypeCarryingReturnAttributeFixture
	{
		[return: TypeCarrier(typeof(LiquidWalletState))]
		public int Method() => 0;
	}

	private interface IMetadataFixture
	{
		void Method();
	}

	private sealed class MethodImplementationMetadataFixture : IMetadataFixture
	{
		void IMetadataFixture.Method() { }
	}

	private sealed class ValidMetadataRootFixture;

	private class MetadataRootBaseFixture;

	private sealed class DerivedMetadataRootFixture : MetadataRootBaseFixture;

	private sealed class InterfaceMetadataRootFixture : IDisposable
	{
		public void Dispose() { }
	}

	private sealed class GenericMetadataRootFixture<T>;

	private sealed class EventMetadataRootFixture
	{
#pragma warning disable CS0067
		public event EventHandler? Changed;
#pragma warning restore CS0067
	}

	[TypeCarrier(typeof(LiquidWalletTransactionObservation))]
	private sealed class AllowedConstructorTypeAttributeFixture;

	[TypeCarrier(typeof(LiquidWalletState))]
	private sealed class ForbiddenConstructorTypeAttributeFixture;

	[TypeCarrier(typeof(LiquidWalletTransactionObservation), Target = typeof(LiquidWalletTransactionObservation))]
	private sealed class AllowedNamedTypeAttributeFixture;

	[TypeCarrier(typeof(LiquidWalletTransactionObservation), Target = typeof(LiquidWalletState))]
	private sealed class ForbiddenNamedTypeAttributeFixture;

	[TypeCarrier(typeof(LiquidWalletTransactionObservation), Targets = [typeof(LiquidWalletTransactionObservation)])]
	private sealed class AllowedArrayTypeAttributeFixture;

	[TypeCarrier(typeof(LiquidWalletTransactionObservation), Targets = [typeof(LiquidWalletState)])]
	private sealed class ForbiddenArrayTypeAttributeFixture;

	private enum FixtureChannel
	{
		Field,
		Type,
		Token,
		Local,
		Catch,
		String,
	}

	private enum ManagedBodyFixtureKind
	{
		ValidCall,
		ExtraLocal,
		PinnedLocal,
		Finally,
		Fault,
		Filter,
		WrongCallOpcode,
	}

	private enum RawSignatureKind
	{
		Method,
		Field,
		Property,
		Local,
		MethodSpecification,
		Type,
	}

	private sealed record AggregateCapCheck(int Branch, int GreaterSuccessor, int NotGreaterSuccessor);

	private enum ManagedFlowValueKind
	{
		Unknown,
		Producer,
		Argument,
		String,
		Null,
		Constant,
		Getter,
		LocalVersion,
		CheckedAdd,
		GreaterThan,
	}

	private sealed record ManagedFlowValue(
		ManagedFlowValueKind Kind,
		int Instruction = -1,
		int Local = -1,
		int Store = -1,
		int Constant = 0,
		string? Text = null,
		ManagedFlowValue? StoredValue = null)
	{
		public static ManagedFlowValue Unknown { get; } = new(ManagedFlowValueKind.Unknown);
	}

	private sealed record ManagedStoredLocal(int Store, ManagedFlowValue Value)
	{
		public static ManagedStoredLocal Unknown { get; } = new(-1, ManagedFlowValue.Unknown);
	}

	private sealed record ManagedFlowState(
		IReadOnlyList<ManagedFlowValue> Stack,
		IReadOnlyDictionary<int, ManagedStoredLocal> Locals);

	private enum RawPePolicyMode
	{
		Production,
		Fixture,
	}

	private enum RawPeMutation
	{
		None,
		BaseTypeModifier,
		MethodTypeSpecObject,
		MethodTypeSpecObjectAsValueType,
		MethodTypeSpecModifier,
		MethodTypeSpecTrailingData,
		MethodTypeSpecCycle,
		MethodTypeSpecNestedCycleAttribute,
		MethodTypeSpecUnresolved,
		MethodTypeSpecCycleWithUnexpectedScope,
		InterfaceTypeSpecModifier,
		MemberReferenceParentModifier,
		FieldModifier,
		PropertyModifier,
		LocalModifier,
		LocalModifierInt64,
		MethodModifier,
		MethodPrimitiveInt32AsTypeReference,
		MethodObjectAsValueType,
		MethodMemberReferenceModifier,
		MethodMemberReferenceTypeSpecObject,
		MethodMemberReferenceTypeSpecObjectAsValueType,
		FieldMemberReferenceModifier,
		FieldPrimitiveInt32AsTypeReference,
		FieldSzArrayPrimitiveInt32AsTypeReference,
		FieldMdArrayPrimitiveInt32AsTypeReference,
		FieldTypeSpecInt32,
		FieldTypeSpecInt32AsClass,
		FieldMemberReferenceTypeSpecInt32,
		FieldMemberReferenceTypeSpecInt32AsClass,
		FieldSzArray,
		FieldMdArrayRankOne,
		FieldSzArrayAsMdRankOne,
		FieldMdArrayExplicitSize,
		FieldMdArrayLowerBound,
		WrongInterfaceNullableArgument,
		WrongInterfaceAttributeConstructor,
		TypeCarryingInterfaceAttribute,
		TypeCarryingInterfaceNamedAttribute,
		TypeCarryingInterfaceArrayAttribute,
		TypeCarryingTypeAttribute,
		TypeCarryingFieldAttribute,
		TypeCarryingConstructorAttribute,
		TypeCarryingMethodArrayAttribute,
		TypeCarryingMethodArrayWrongTokenObservationAttribute,
		TypeCarryingMethodExactObservationAttribute,
		TypeCarryingMethodUnqualifiedObservationAttribute,
		TypeCarryingMethodCounterfeitObservationAttribute,
		TypeCarryingReturnAttribute,
		TypeCarryingParameterAttribute,
		TypeCarryingPropertyNamedAttribute,
		TypeCarryingPropertyNamedWrongVersionObservationAttribute,
		TypeCarryingEventAttribute,
		TypeCarryingGenericParameterAttribute,
		TypeCarryingGenericConstraintAttribute,
		TypeCarryingStandaloneSignatureAttribute,
		TypeCarryingMemberReferenceAttribute,
		TypeCarryingMethodSpecificationAttribute,
		TypeCarryingTypeSpecificationAttribute,
		TypeCarryingTypeReferenceAttribute,
		TypeCarryingAssemblyReferenceAttribute,
		TypeCarryingModuleReferenceAttribute,
		TypeCarryingModuleDefinitionAttribute,
		MethodImplementation,
		UnmanagedMethodDefinition,
		ReservedMethodHeader,
		ZeroArityGenericBitMethodDefinition,
		MissingGenericBitMethodDefinition,
		SelfConsistentGenericMethodDefinition,
		UnexpectedMethodAttributes,
		UnmanagedMethodMemberReference,
		UnauthorizedMethodMemberReferenceType,
		UnauthorizedMethodMemberReferenceReturnType,
		UnauthorizedFieldMemberReferenceType,
		FieldMemberReferenceIntAsClass,
		UnauthorizedMethodSpecificationArgument,
		MethodBitsOnFieldMemberReference,
		MalformedPropertyHeader,
		GenericPropertyHeader,
		ReservedPropertyHeader,
		MalformedMethodSpecificationHeader,
		MethodSpecificationArityMismatch,
		MethodSpecificationTrailingData,
		MethodSpecificationNonGenericParent,
		MethodSpecificationZeroArityGenericParent,
		VarArgsMethodDefinition,
		InstanceMethodDefinition,
		ExplicitThisMethodDefinition,
		ClassLayout,
		LiteralFieldDefinition,
		NotSerializedField,
		MutatedLiteralField,
		MarshaledField,
		SynchronizedMethod,
		ParameterizedMethodDefinition,
		WrongParameterName,
		OptionalParameter,
		DefaultParameter,
		MarshaledParameter,
		ReturnParameterDefinition,
		WrongReturnParameterName,
		OptionalReturnParameter,
		DefaultReturnParameter,
		MarshaledReturnParameter,
		UnexpectedPropertyAttributes,
		MissingPropertyGetter,
		WrongPropertyGetter,
		SetterPropertySemantics,
		OtherPropertySemantics,
		MethodGenericConstraintObject,
		MethodGenericConstraintTypeSpecObject,
		MethodGenericConstraintForbidden,
		MethodGenericConstraintModifier,
		MethodGenericConstraintUnresolved,
		MethodGenericConstraintCycle,
		MethodGenericConstraintTrailingData,
		BaseTypeCycle,
		BaseTypeUnresolved,
		NestedTypeReferenceScope,
		AssemblyReferenceTypeAlias,
		MethodAssemblyReferenceTypeAlias,
		AssemblyReferenceCrossApproved,
		MethodAssemblyReferenceCrossApproved,
		LocalTypeDefinitionAlias,
		MethodLocalTypeDefinitionAlias,
		MethodLocalObservationTypeDefinitionAlias,
		FieldLocalBatchTypeDefinitionAlias,
		AssemblyReferenceWrongVersion,
		AssemblyReferenceWrongCulture,
		AssemblyReferenceLiteralNeutralCulture,
		MethodAssemblyReferenceLiteralNeutralCulture,
		AssemblyReferenceWrongToken,
		AssemblyReferencePublicKey,
		AssemblyReferenceRetargetable,
		MethodAssemblyReferenceRetargetable,
		AssemblyReferenceWindowsRuntime,
		AssemblyReferenceHash,
		ModuleReferenceTypeScope,
		ModuleDefinitionTypeScope,
		TypeReferenceScopeCycle,
		TypeReferenceScopeUnresolved,
		TypeReferenceUnexpectedScope,
		TopLevelNestedTypeReferenceAlias,
		MethodGenericMetadataNameAlias,
		NestedTypeDefinitionScope,
		TypeDefinitionScopeCycle,
		TypeDefinitionScopeUnresolved,
		TypeDefinitionUnexpectedScope,
	}

	private sealed record RawSignaturePolicy(
		RawSignatureKind Kind,
		bool IsInstance,
		int GenericArity,
		int ParameterCount);

	private sealed record RawMethodGenericShape(bool IsGeneric, int Arity);

	private sealed record RawMethodDefinitionExpectation(
		MethodBase Callable,
		MethodAttributes Attributes,
		MethodImplAttributes Implementation,
		bool ExpectReturnParameter = false,
		bool DecodeTypeSpecificationHandles = false);

	private sealed record RawMemberExpectation(
		bool IsField,
		string Parent,
		string Name,
		string ReturnOrFieldType,
		string[] Parameters,
		bool IsInstance,
		int GenericArity)
	{
		public static RawMemberExpectation ForField(string parent, string name, string fieldType) =>
			new(true, parent, name, fieldType, [], false, 0);

		public static RawMemberExpectation ForMethod(
			string parent,
			string name,
			string returnType,
			string[] parameters,
			bool isInstance,
			int genericArity) =>
			new(false, parent, name, returnType, parameters, isInstance, genericArity);
	}

	private sealed record RawAttributeView(string ConstructorKey, byte[] Blob);

	private sealed record RawInterfaceAttributeView(string InterfaceType, RawAttributeView[] Attributes);

	private sealed record RawTypeNode(int? Next, bool IsModified);

	private sealed record RawTypeGraph(int Root, IReadOnlyDictionary<int, RawTypeNode> Nodes);

	private sealed record ManagedLocal(string Type, bool IsPinned);

	private sealed record ManagedBodyView(
		MethodBase Source,
		MethodAttributes Attributes,
		MethodImplAttributes Implementation,
		bool HasBody,
		bool InitLocals,
		ManagedLocal[] Locals,
		ExceptionHandlingClauseOptions[] Clauses,
		Instruction[] Instructions)
	{
		public static ManagedBodyView FromMethod(MethodBase method)
		{
			MethodBody? body = method.GetMethodBody();
			return new ManagedBodyView(
				method,
				method.Attributes,
				method.MethodImplementationFlags,
				body is not null,
				body?.InitLocals ?? false,
				body?.LocalVariables.Select(local => new ManagedLocal(TypeKey(local.LocalType), local.IsPinned)).ToArray() ?? [],
				body?.ExceptionHandlingClauses.Select(clause => clause.Flags).ToArray() ?? [],
				body is null ? [] : ReadInstructions(method).ToArray());
		}
	}

	private sealed record ManagedBodyPolicy(
		MethodAttributes Attributes,
		MethodImplAttributes Implementation,
		bool InitLocals,
		string[] Locals,
		ExceptionHandlingClauseOptions[] Clauses,
		string[] CatchTypes,
		string[] Calls,
		string[] Fields,
		string[] Types,
		string[] Tokens,
		string[] Strings)
	{
		public static ManagedBodyPolicy ForProduction(MethodBase method) => new(
			ExpectedMethodAttributes(method),
			MethodImplAttributes.IL,
			ExpectedInitLocals(method),
			ExpectedLocals(method)
				.Select(value => string.Join('|', value.Split('|').Take(2)))
				.ToArray(),
			[],
			[],
			ExpectedCalls(method).ToArray(),
			ExpectedFields(method).ToArray(),
			ExpectedTypeTokens(method).ToArray(),
			[],
			ExpectedStrings(method).ToArray());

		public static ManagedBodyPolicy ForFixture(MethodBase method)
		{
			ManagedBodyView view = ManagedBodyView.FromMethod(method);
			return new ManagedBodyPolicy(
				view.Attributes,
				view.Implementation,
				view.InitLocals,
				view.Locals.Select(LocalTypeKey).ToArray(),
				view.Clauses,
				method.GetMethodBody()?.ExceptionHandlingClauses
					.Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause)
					.Select(clause => TypeKey(clause.CatchType!))
					.ToArray() ?? [],
				view.Instructions
					.Where(instruction => instruction.OpCode.OperandType == OperandType.InlineMethod)
					.Select(instruction => $"{instruction.OpCode.Name}|{MemberKey(ResolveMember(method, instruction))}")
					.ToArray(),
				view.Instructions
					.Where(instruction => instruction.OpCode.OperandType == OperandType.InlineField)
					.Select(instruction => $"{instruction.OpCode.Name}|{MemberKey(ResolveMember(method, instruction))}")
					.ToArray(),
				view.Instructions
					.Where(instruction => instruction.OpCode.OperandType == OperandType.InlineType)
					.Select(instruction => $"{instruction.OpCode.Name}|{MemberKey(ResolveMember(method, instruction))}")
					.ToArray(),
				view.Instructions
					.Where(instruction => instruction.OpCode.OperandType == OperandType.InlineTok)
					.Select(instruction => $"{instruction.OpCode.Name}|{MemberKey(ResolveMember(method, instruction))}")
					.ToArray(),
				view.Instructions
					.Where(instruction => instruction.OpCode.OperandType == OperandType.InlineString)
					.Select(instruction => ResolveInstructionString(method, instruction))
					.ToArray());
		}
	}

	private const BindingFlags DeclaredMemberFlags =
		BindingFlags.DeclaredOnly |
		BindingFlags.Instance |
		BindingFlags.Static |
		BindingFlags.Public |
		BindingFlags.NonPublic;

	private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodeByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(OpCode))
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(opcode => unchecked((ushort)opcode.Value));

	private sealed record Instruction(int Offset, int EndOffset, OpCode OpCode, object? Operand);
}
