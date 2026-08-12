using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidOrdinaryWalletExactSpendPlanTests
{
	private const string IssuedAssetHex =
		"2222222222222222222222222222222222222222222222222222222222222222";
	private const string ExtraAssetHex =
		"3333333333333333333333333333333333333333333333333333333333333333";
	private const string PublicKeyHex =
		"0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string FirstScriptHex = "00140102030405060708090a0b0c0d0e0f1011121314";
	private const string SecondScriptHex = "001415161718191a1b1c1d1e1f202122232425262728";
	private const string PrivateLabel = "private-plan-label-canary-583941";

	private static ElementsPublicNetworkManifest Manifest =>
		ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAsset => LiquidAssetId.ParseRpcHex(IssuedAssetHex);
	private static LiquidAssetId ExtraAsset => LiquidAssetId.ParseRpcHex(ExtraAssetHex);
	private static LiquidSpendKeyReference SpendKey => LiquidSpendKeyReference.Create(
		Convert.FromHexString(PublicKeyHex),
		LiquidKeyBranch.External,
		0);

	[Fact]
	public void CreatesExactTwoAssetPlanAndPreservesBothManagedOrders()
	{
		LiquidTransactionId transactionId = Tx(100);
		LiquidOwnedOutput issuedLater = Output(transactionId, 2, IssuedAsset, 20);
		LiquidOwnedOutput pegged = Output(transactionId, 1, PeggedAsset, 10);
		LiquidWalletState state = State(issuedLater, pegged);
		LiquidSuppliedConfidentialDestination first = Destination(
			SecondScriptHex,
			IssuedAsset,
			7,
			PrivateLabel);
		LiquidSuppliedConfidentialDestination second = Destination(
			FirstScriptHex,
			PeggedAsset,
			9,
			"pegged-change");
		LiquidSuppliedConfidentialDestination repeatedAsset = Destination(
			FirstScriptHex,
			IssuedAsset,
			13,
			"issued-change");
		LiquidSuppliedConfidentialDestinationBatch destinations =
			LiquidSuppliedConfidentialDestinationBatch.Create([first, second, repeatedAsset]);

		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[issuedLater.OutPoint, pegged.OutPoint],
			destinations,
			Amount(PeggedAsset, 1));

		Assert.Equal(state.Revision, plan.SourceRevision);
		Assert.Equal(2, plan.SelectedInputCount);
		Assert.Equal(3, plan.ConfidentialOutputCount);
		Assert.Equal(Manifest.ManifestId, plan.GetDestinationNetworkManifestId());
		Assert.Equal(PeggedAsset, plan.GetPeggedAssetId());
		Assert.Equal(
			[pegged.OutPoint, issuedLater.OutPoint],
			plan.GetSelectedEntries().Select(entry => entry.OutPoint));
		Assert.Equal([first, second, repeatedAsset], plan.GetDestinations());
		Assert.Equal(Amount(PeggedAsset, 1), plan.GetExplicitFee());
		Assert.Equal(nameof(LiquidOrdinaryWalletExactSpendPlan), plan.ToString());
	}

	[Fact]
	public void EnforcesOneHundredInputBoundaryBeforeElementAccess()
	{
		LiquidTransactionId transactionId = Tx(101);
		var outputs = new LiquidOwnedOutput[101];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] = Output(
				transactionId,
				(uint)index,
				index == 99 ? PeggedAsset : IssuedAsset,
				1);
		}
		LiquidWalletState state = State(outputs);
		LiquidOutPoint[] accepted = outputs.Take(100).Select(output => output.OutPoint).ToArray();
		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			accepted,
			Batch(Destination(FirstScriptHex, IssuedAsset, 99, "recipient")),
			Amount(PeggedAsset, 1));
		Assert.Equal(LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount, plan.SelectedInputCount);

		var overLimit = new CountedHostileSelectionList(101);
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				state.Revision,
				overLimit,
				null!,
				null!));
		Assert.Equal(1, overLimit.CountReads);
		Assert.Equal(0, overLimit.IndexReads);

		var negativeCount = new CountedHostileSelectionList(-1);
		ArgumentOutOfRangeException negativeFailure = Assert.Throws<ArgumentOutOfRangeException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				state.Revision,
				negativeCount,
				null!,
				null!));
		Assert.Equal(1, negativeCount.CountReads);
		Assert.Equal(0, negativeCount.IndexReads);
		Assert.Null(negativeFailure.ActualValue);
		Assert.Null(negativeFailure.InnerException);
		Assert.Empty(negativeFailure.Data);
		Assert.DoesNotContain("-1", negativeFailure.ToString(), StringComparison.Ordinal);
		Assert.Equal(101, state.GetCoinControlSnapshot().GetEntries().Count);

		LiquidWalletCoinControlSelection unrestricted = state.CreateCoinControlSelection(
			state.Revision,
			outputs.Select(output => output.OutPoint).ToArray());
		Assert.Equal(101, unrestricted.GetEntries().Count);
	}

	[Fact]
	public void EnforcesDestinationBoundariesWithoutImplicitChange()
	{
		LiquidTransactionId transactionId = Tx(102);
		LiquidOwnedOutput issued = Output(transactionId, 0, IssuedAsset, 256);
		LiquidOwnedOutput pegged = Output(transactionId, 1, PeggedAsset, 1);
		LiquidWalletState state = State(issued, pegged);

		LiquidOrdinaryWalletExactSpendPlan single = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[issued.OutPoint, pegged.OutPoint],
			Batch(Destination(FirstScriptHex, IssuedAsset, 256, "single")),
			Amount(PeggedAsset, 1));
		Assert.Equal(1, single.ConfidentialOutputCount);

		LiquidSuppliedConfidentialDestination[] maximum = Enumerable.Range(0, 255)
			.Select(index => Destination(
				index % 2 == 0 ? FirstScriptHex : SecondScriptHex,
				IssuedAsset,
				index == 0 ? 2 : 1,
				"maximum"))
			.ToArray();
		LiquidOrdinaryWalletExactSpendPlan full = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[issued.OutPoint, pegged.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create(maximum),
			Amount(PeggedAsset, 1));
		Assert.Equal(255, full.ConfidentialOutputCount);

		LiquidSuppliedConfidentialDestination[] overLimit = maximum
			.Append(Destination(FirstScriptHex, IssuedAsset, 1, "over-limit"))
			.ToArray();
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				state.Revision,
				[issued.OutPoint, pegged.OutPoint],
				LiquidSuppliedConfidentialDestinationBatch.Create(overLimit),
				Amount(PeggedAsset, 1)));
	}

	[Fact]
	public void EnforcesPerValueBoundaryAndAcceptsMaximumReachableTotals()
	{
		const long Maximum = LiquidOrdinaryWalletExactSpendPlan.MaximumAtomicUnits;
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			Output(Tx(10_300), 0, IssuedAsset, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			Destination(FirstScriptHex, IssuedAsset, 0, "zero"));
		LiquidTransactionId maximumId = Tx(103);
		LiquidWalletState maximumState = State(
			Output(maximumId, 0, IssuedAsset, Maximum),
			Output(maximumId, 1, PeggedAsset, 1));
		LiquidOrdinaryWalletExactSpendPlan exactMaximum =
			maximumState.CreateExactOrdinaryWalletSpendPlan(
				maximumState.Revision,
				maximumState.GetCoinControlSnapshot().GetEntries()
					.Select(entry => entry.OutPoint).ToArray(),
				Batch(Destination(FirstScriptHex, IssuedAsset, Maximum, "maximum")),
				Amount(PeggedAsset, 1));
		Assert.Equal(Maximum, Assert.Single(exactMaximum.GetDestinations()).GetAmount()!.AtomicUnits);

		LiquidTransactionId overSelectedId = Tx(104);
		LiquidOwnedOutput overSelected = Output(overSelectedId, 0, IssuedAsset, Maximum + 1);
		LiquidOwnedOutput selectedFee = Output(overSelectedId, 1, PeggedAsset, 1);
		LiquidWalletState overSelectedState = State(overSelected, selectedFee);
		Assert.Throws<ArgumentException>(() =>
			overSelectedState.CreateExactOrdinaryWalletSpendPlan(
				overSelectedState.Revision,
				[overSelected.OutPoint, selectedFee.OutPoint],
				Batch(Destination(FirstScriptHex, IssuedAsset, Maximum + 1, "over-selected")),
				Amount(PeggedAsset, 1)));

		Assert.Throws<ArgumentException>(() =>
			maximumState.CreateExactOrdinaryWalletSpendPlan(
				maximumState.Revision,
				maximumState.GetCoinControlSnapshot().GetEntries()
					.Select(entry => entry.OutPoint).ToArray(),
				Batch(Destination(FirstScriptHex, IssuedAsset, Maximum + 1, "over-destination")),
				Amount(PeggedAsset, 1)));

		LiquidTransactionId aggregateId = Tx(105);
		var aggregateOutputs = new LiquidOwnedOutput[100];
		var aggregateDestinations = new LiquidSuppliedConfidentialDestination[100];
		for (int index = 0; index < 99; index++)
		{
			aggregateOutputs[index] = Output(aggregateId, (uint)index, IssuedAsset, Maximum);
			aggregateDestinations[index] = Destination(
				index % 2 == 0 ? FirstScriptHex : SecondScriptHex,
				IssuedAsset,
				Maximum,
				"aggregate");
		}
		aggregateOutputs[^1] = Output(aggregateId, 99, PeggedAsset, Maximum);
		aggregateDestinations[^1] = Destination(
			FirstScriptHex,
			PeggedAsset,
			Maximum - 1,
			"aggregate-pegged");
		LiquidWalletState aggregateState = State(aggregateOutputs);
		LiquidOrdinaryWalletExactSpendPlan aggregate = aggregateState.CreateExactOrdinaryWalletSpendPlan(
			aggregateState.Revision,
			aggregateOutputs.Select(output => output.OutPoint).ToArray(),
			LiquidSuppliedConfidentialDestinationBatch.Create(aggregateDestinations),
			Amount(PeggedAsset, 1));
		Assert.Equal(100, aggregate.SelectedInputCount);
		Assert.Equal(100, aggregate.ConfidentialOutputCount);
	}

	[Fact]
	public void RejectsFeeContextAndEveryConservationMismatchAtomically()
	{
		LiquidTransactionId transactionId = Tx(106);
		LiquidOwnedOutput pegged = Output(transactionId, 0, PeggedAsset, 10);
		LiquidOwnedOutput issued = Output(transactionId, 1, IssuedAsset, 20);
		LiquidWalletState state = State(pegged, issued);
		LiquidOutPoint[] both = [pegged.OutPoint, issued.OutPoint];
		LiquidAssetId otherPegged = LiquidAssetId.ParseRpcHex(
			ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId);

		AssertPlanFailure(state, both, Batch(
			Destination(FirstScriptHex, PeggedAsset, 9, "fee-asset"),
			Destination(SecondScriptHex, IssuedAsset, 20, "fee-asset")),
			Amount(IssuedAsset, 1));
		AssertPlanFailure(state, both, Batch(
			Destination(FirstScriptHex, PeggedAsset, 10, "zero-fee"),
			Destination(SecondScriptHex, IssuedAsset, 20, "zero-fee")),
			LiquidAssetAmount.Zero(PeggedAsset, PeggedAsset));
		AssertPlanFailure(state, both, Batch(
			Destination(FirstScriptHex, PeggedAsset, 9, "wrong-context"),
			Destination(SecondScriptHex, IssuedAsset, 20, "wrong-context")),
			LiquidAssetAmount.Create(otherPegged, otherPegged, 1));
		AssertPlanFailure(state, both, Batch(
			Destination(FirstScriptHex, PeggedAsset, 9, "missing")),
			Amount(PeggedAsset, 1));
		AssertPlanFailure(state, [pegged.OutPoint], Batch(
			Destination(FirstScriptHex, PeggedAsset, 9, "extra"),
			Destination(SecondScriptHex, ExtraAsset, 1, "extra")),
			Amount(PeggedAsset, 1));
		AssertPlanFailure(state, both, Batch(
			Destination(FirstScriptHex, PeggedAsset, 8, "surplus"),
			Destination(SecondScriptHex, IssuedAsset, 20, "surplus")),
			Amount(PeggedAsset, 1));
		AssertPlanFailure(state, both, Batch(
			Destination(FirstScriptHex, PeggedAsset, 10, "deficit"),
			Destination(SecondScriptHex, IssuedAsset, 20, "deficit")),
			Amount(PeggedAsset, 1));

		LiquidSuppliedConfidentialDestination otherContext = Destination(
			ElementsPublicNetworkManifest.LiquidMainnet,
			FirstScriptHex,
			otherPegged,
			9,
			"other-context");
		AssertPlanFailure(
			state,
			[pegged.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([otherContext]),
			Amount(PeggedAsset, 1));
	}

	[Fact]
	public void ValidationPrecedenceLeavesStateAndLaterArgumentsUntouched()
	{
		LiquidTransactionId transactionId = Tx(107);
		LiquidOwnedOutput output = Output(transactionId, 0, PeggedAsset, 2);
		LiquidWalletState state = State(output);
		LiquidWalletCoinControlSnapshot before = state.GetCoinControlSnapshot();
		var staleSelection = new CountedHostileSelectionList(1);

		Assert.Throws<InvalidOperationException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				0,
				staleSelection,
				null!,
				null!));
		Assert.Equal(0, staleSelection.CountReads);
		Assert.Equal(0, staleSelection.IndexReads);
		Assert.Throws<ArgumentNullException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(state.Revision, null!, null!, null!));
		Assert.Throws<ArgumentNullException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				state.Revision,
				[output.OutPoint],
				null!,
				null!));
		Assert.Throws<ArgumentNullException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				state.Revision,
				[output.OutPoint],
				Batch(Destination(FirstScriptHex, PeggedAsset, 1, "null-fee")),
				null!));

		Assert.Equal(before.Revision, state.Revision);
		Assert.Equal(
			before.GetEntries().Select(entry => entry.OutPoint),
			state.GetCoinControlSnapshot().GetEntries().Select(entry => entry.OutPoint));
	}

	[Fact]
	public void CallerMutationAndLaterStateTransitionsCannotMutatePlan()
	{
		LiquidTransactionId transactionId = Tx(108);
		LiquidOwnedOutput selected = Output(transactionId, 0, PeggedAsset, 2);
		LiquidWalletState state = State(selected);
		var selectedSource = new List<LiquidOutPoint> { selected.OutPoint };
		LiquidSuppliedConfidentialDestination destination = Destination(
			FirstScriptHex,
			PeggedAsset,
			1,
			PrivateLabel);
		var destinationSource = new List<LiquidSuppliedConfidentialDestination> { destination };
		LiquidSuppliedConfidentialDestinationBatch destinationBatch =
			LiquidSuppliedConfidentialDestinationBatch.Create(destinationSource);
		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			selectedSource,
			destinationBatch,
			Amount(PeggedAsset, 1));

		selectedSource.Clear();
		destinationSource.Clear();
		LiquidTransactionId laterId = Tx(109);
		LiquidWalletState later = state.Apply(
			state.Revision,
			LiquidWalletTransactionDelta.Create(
				laterId,
				[selected.OutPoint],
				[Output(laterId, 0, PeggedAsset, 1)]));

		Assert.Equal(1ul, plan.SourceRevision);
		Assert.Equal(selected.OutPoint, Assert.Single(plan.GetSelectedEntries()).OutPoint);
		Assert.Same(destination, Assert.Single(plan.GetDestinations()));
		Assert.False(later.ContainsUnspent(selected.OutPoint));
		Assert.True(state.ContainsUnspent(selected.OutPoint));
	}

	[Fact]
	public void AccessorsReturnFreshReadOnlySnapshots()
	{
		LiquidTransactionId transactionId = Tx(110);
		LiquidOwnedOutput output = Output(transactionId, 0, PeggedAsset, 2);
		LiquidSuppliedConfidentialDestination destination = Destination(
			FirstScriptHex,
			PeggedAsset,
			1,
			"snapshot");
		LiquidWalletState state = State(output);
		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[output.OutPoint],
			Batch(destination),
			Amount(PeggedAsset, 1));

		IReadOnlyList<LiquidWalletCoinControlEntry> firstEntries = plan.GetSelectedEntries();
		IReadOnlyList<LiquidWalletCoinControlEntry> secondEntries = plan.GetSelectedEntries();
		IReadOnlyList<LiquidSuppliedConfidentialDestination> firstDestinations = plan.GetDestinations();
		IReadOnlyList<LiquidSuppliedConfidentialDestination> secondDestinations = plan.GetDestinations();

		Assert.NotSame(firstEntries, secondEntries);
		Assert.NotSame(firstDestinations, secondDestinations);
		Assert.False(firstEntries is LiquidWalletCoinControlEntry[]);
		Assert.False(firstDestinations is LiquidSuppliedConfidentialDestination[]);
		var mutableEntries = Assert.IsAssignableFrom<IList<LiquidWalletCoinControlEntry>>(firstEntries);
		var mutableDestinations = Assert.IsAssignableFrom<IList<LiquidSuppliedConfidentialDestination>>(firstDestinations);
		Assert.True(mutableEntries.IsReadOnly);
		Assert.True(mutableDestinations.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableEntries[0] = null!);
		Assert.Throws<NotSupportedException>(() => mutableDestinations[0] = null!);
		Assert.Equal(output.OutPoint, Assert.Single(secondEntries).OutPoint);
		Assert.Same(destination, Assert.Single(secondDestinations));
	}

	[Fact]
	public void ErrorsAndFormattingDoNotExposeRetainedWalletFacts()
	{
		LiquidTransactionId transactionId = Tx(4_832_917);
		LiquidOwnedOutput output = Output(transactionId, 707_059, PeggedAsset, 98_765_431);
		LiquidWalletState state = State(output);
		LiquidSuppliedConfidentialDestination destination = Destination(
			FirstScriptHex,
			PeggedAsset,
			98_765_429,
			PrivateLabel);
		LiquidSuppliedConfidentialDestinationBatch destinations = Batch(destination);
		Exception failure = Assert.Throws<ArgumentException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				state.Revision,
				[output.OutPoint],
				destinations,
				Amount(PeggedAsset, 1)));
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);

		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[output.OutPoint],
			Batch(Destination(
				FirstScriptHex,
				PeggedAsset,
				98_765_430,
				PrivateLabel)),
			Amount(PeggedAsset, 1));
		string rendered = failure.Message + "|" + failure + "|" + plan;
		foreach (string canary in new[]
		{
			transactionId.CanonicalRpcHex,
			"707059",
			"98765431",
			"98765429",
			PeggedAsset.CanonicalRpcHex,
			destination.GetAddress().GetCanonicalAddressText(),
			Convert.ToHexString(destination.GetAddress().GetScriptPubKey()),
			Convert.ToHexString(destination.GetAddress().GetBlindingPublicKey()!),
			PrivateLabel,
		})
		{
			Assert.DoesNotContain(canary, rendered, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public void SurfaceIsImmutableInternalAndContainsNoExecutionAuthority()
	{
		Type type = typeof(LiquidOrdinaryWalletExactSpendPlan);
		Assert.True(type.IsNotPublic);
		Assert.True(type.IsSealed);
		Assert.Equal(typeof(object), type.BaseType);
		Assert.Empty(type.GetInterfaces());
		Assert.All(
			type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
			constructor => Assert.True(constructor.IsPrivate));
		Assert.DoesNotContain(
			type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
			method => method.Name is "Equals" or "GetHashCode" or "Deconstruct");
		Assert.All(
			type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
			field => Assert.True(field.IsInitOnly));
		Assert.DoesNotContain(type.GetCustomAttributesData(), attribute =>
			attribute.AttributeType.Name.Contains("Serializable", StringComparison.OrdinalIgnoreCase) ||
			attribute.AttributeType.Name.Contains("Debugger", StringComparison.OrdinalIgnoreCase));

		MethodInfo stateEntryPoint = Assert.Single(
			typeof(LiquidWalletState).GetMethods(BindingFlags.Public | BindingFlags.Instance),
			method => method.Name == nameof(LiquidWalletState.CreateExactOrdinaryWalletSpendPlan));
		Assert.Equal(type, stateEntryPoint.ReturnType);
		Assert.Equal(
			[
				typeof(ulong),
				typeof(IReadOnlyList<LiquidOutPoint>),
				typeof(LiquidSuppliedConfidentialDestinationBatch),
				typeof(LiquidAssetAmount),
			],
			stateEntryPoint.GetParameters().Select(parameter => parameter.ParameterType));

		string[] forbidden =
		[
			"Native", "PInvoke", "DllImport", "Rpc", "File", "Directory", "Process",
			"Socket", "Http", "NetworkStream", "Transaction", "Descriptor", "Slip77",
			"PrivateKey", "Signer", "Pset", "Broadcast", "CoinJoin", "Sponsor", "Usdt",
		];
		IEnumerable<MemberInfo> members = type.GetMembers(
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
			BindingFlags.Static | BindingFlags.DeclaredOnly);
		Assert.DoesNotContain(members, member =>
			forbidden.Any(fragment =>
				MemberIdentity(member).Contains(fragment, StringComparison.OrdinalIgnoreCase)));
		Assert.DoesNotContain(
			type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly),
			method => method.GetCustomAttribute<DllImportAttribute>() is not null);
		Assert.DoesNotContain(
			type.Assembly.GetReferencedAssemblies(),
			assembly => (assembly.Name ?? "").Contains("liquid-native", StringComparison.OrdinalIgnoreCase));
	}

	private static void AssertPlanFailure(
		LiquidWalletState state,
		IReadOnlyList<LiquidOutPoint> selectedOutPoints,
		LiquidSuppliedConfidentialDestinationBatch destinations,
		LiquidAssetAmount fee)
	{
		ulong revision = state.Revision;
		IReadOnlyList<LiquidOutPoint> before = state.GetCoinControlSnapshot().GetEntries()
			.Select(entry => entry.OutPoint)
			.ToArray();
		Exception failure = Assert.Throws<ArgumentException>(() =>
			state.CreateExactOrdinaryWalletSpendPlan(
				state.Revision,
				selectedOutPoints,
				destinations,
				fee));
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
		Assert.Equal(revision, state.Revision);
		Assert.Equal(
			before,
			state.GetCoinControlSnapshot().GetEntries().Select(entry => entry.OutPoint));
	}

	private static LiquidWalletState State(params LiquidOwnedOutput[] outputs)
	{
		LiquidTransactionId transactionId = Assert.Single(
			outputs.Select(output => output.OutPoint.TransactionId).Distinct());
		return LiquidWalletState.Empty(PeggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], outputs));
	}

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidSpendKeyReference spendKey = SpendKey;
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			spendKey.GetScriptPubKey(),
			Amount(assetId, atomicUnits),
			spendKey);
	}

	private static LiquidSuppliedConfidentialDestination Destination(
		string scriptHex,
		LiquidAssetId assetId,
		long atomicUnits,
		string label) =>
		Destination(Manifest, scriptHex, assetId, atomicUnits, label);

	private static LiquidSuppliedConfidentialDestination Destination(
		ElementsPublicNetworkManifest manifest,
		string scriptHex,
		LiquidAssetId assetId,
		long atomicUnits,
		string label)
	{
		LiquidAssetId peggedAssetId = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString(scriptHex),
			LiquidBlindingPublicKey.Create(Convert.FromHexString(PublicKeyHex)));
		return LiquidSuppliedConfidentialDestination.Create(
			manifest,
			address,
			assetId,
			LiquidAssetAmount.Create(assetId, peggedAssetId, atomicUnits),
			LiquidWalletLabelSet.Create([label]));
	}

	private static LiquidSuppliedConfidentialDestinationBatch Batch(
		params LiquidSuppliedConfidentialDestination[] destinations) =>
		LiquidSuppliedConfidentialDestinationBatch.Create(destinations);

	private static LiquidAssetAmount Amount(LiquidAssetId assetId, long atomicUnits) =>
		LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits);

	private static LiquidTransactionId Tx(uint value) =>
		LiquidTransactionId.ParseRpcHex(value.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));

	private static string MemberIdentity(MemberInfo member) =>
		$"{member.DeclaringType?.FullName}|{member.Name}|{member}";

	private sealed class CountedHostileSelectionList(int count) : IReadOnlyList<LiquidOutPoint>
	{
		public int Count
		{
			get
			{
				CountReads++;
				return count;
			}
		}

		public int CountReads { get; private set; }
		public int IndexReads { get; private set; }

		public LiquidOutPoint this[int index]
		{
			get
			{
				IndexReads++;
				throw new HostileSelectionInspectedException();
			}
		}

		public IEnumerator<LiquidOutPoint> GetEnumerator() =>
			throw new HostileSelectionInspectedException();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class HostileSelectionInspectedException : Exception;
}
