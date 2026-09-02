using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

[Collection("Serial unit tests collection")]
public class LiquidWalletUiSpendPlanTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlindingKeyHex = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";
	private const string IssuedAssetAHex = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAssetA => LiquidAssetId.ParseRpcHex(IssuedAssetAHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);
	private static byte[] BlindingKey => Convert.FromHexString(BlindingKeyHex);
	private static byte[] ReceiveScript => ExternalKey.GetScriptPubKey();

	// Required evidence §1: spend-plan construction from a confidential
	// destination + per-asset amount. A state with a pegged-asset balance
	// and one issued-asset balance, a caller-supplied confidential
	// destination address (valid for the manifest), a destination asset id
	// matching the issued asset, a destination amount within the balance,
	// and an explicit fee within the pegged-asset balance yields a
	// LiquidWalletUiSpendPlan with the exact landed projection values.
	[Fact]
	public void CreateSpendPlanFromConfidentialDestinationAndPerAssetAmount()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, IssuedAssetA, 5_000)]));

		string confidentialAddress = ConfidentialAddress();
		string[] selectedOutPointHexes =
		[
			OutPointHex(txA, 0),
			OutPointHex(txB, 0),
		];

		LiquidWalletUiSpendPlan plan = LiquidWalletUiFacade.CreateSpendPlan(
			"wallet",
			Manifest,
			state,
			selectedOutPointHexes,
			confidentialAddress,
			IssuedAssetAHex,
			destinationAtomicUnits: 5_000,
			explicitFeeAtomicUnits: 1_000);

		Assert.Equal("wallet", plan.WalletName);
		Assert.Equal(Manifest.ManifestId, plan.NetworkManifestId);
		Assert.Equal(Manifest.PeggedAssetId, plan.PeggedAssetIdHex);
		Assert.Equal(state.Revision, plan.SourceRevision);
		Assert.Equal(2, plan.SelectedInputCount);
		Assert.Equal(1, plan.ConfidentialOutputCount);
		Assert.True(plan.IsConfidential);

		LiquidAddress landedAddress = LiquidAddress.Parse(Manifest, confidentialAddress);
		LiquidWalletUiSpendPlanDestination destination = Assert.Single(plan.Destinations);
		Assert.Equal(landedAddress.GetCanonicalAddressText(), destination.ConfidentialAddressText);
		Assert.Equal(landedAddress.GetUnconfidentialAddressText(), destination.UnconfidentialAddressText);
		Assert.Equal(IssuedAssetAHex, destination.AssetIdHex);
		Assert.Equal(5_000, destination.AtomicUnits);
		Assert.True(destination.IsConfidential);

		Assert.Equal(1_000, plan.ExplicitFee.AtomicUnits);
		Assert.True(plan.ExplicitFee.IsPeggedAsset);
		Assert.Equal(Manifest.PeggedAssetId, plan.ExplicitFee.AssetIdHex);

		// The per-asset selected totals: the pegged asset total equals the
		// explicit fee (no pegged destination), the issued asset total
		// equals the destination amount; canonical ascending asset-id-hex
		// order (the issued asset 0a0a… sorts before the pegged asset).
		Assert.Equal(2, plan.SelectedTotals.Count);
		Assert.Equal(IssuedAssetAHex, plan.SelectedTotals[0].AssetIdHex);
		Assert.False(plan.SelectedTotals[0].IsPeggedAsset);
		Assert.Equal(5_000, plan.SelectedTotals[0].AtomicUnits);
		Assert.Equal(Manifest.PeggedAssetId, plan.SelectedTotals[1].AssetIdHex);
		Assert.True(plan.SelectedTotals[1].IsPeggedAsset);
		Assert.Equal(1_000, plan.SelectedTotals[1].AtomicUnits);
	}

	// Required evidence §1 (second row): the same construction with the
	// pegged asset as the destination asset yields a plan whose
	// SelectedTotals has exactly one entry (the pegged asset) whose
	// AtomicUnits equals the destination amount plus the explicit fee.
	[Fact]
	public void CreateSpendPlanPeggedDestinationYieldsSingleSelectedTotal()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));

		LiquidWalletUiSpendPlan plan = LiquidWalletUiFacade.CreateSpendPlan(
			"wallet",
			Manifest,
			state,
			[OutPointHex(txA, 0)],
			ConfidentialAddress(),
			Manifest.PeggedAssetId,
			destinationAtomicUnits: 9_000,
			explicitFeeAtomicUnits: 1_000);

		Assert.Equal(1, plan.SelectedInputCount);
		Assert.Equal(1, plan.ConfidentialOutputCount);
		Assert.Equal(Manifest.PeggedAssetId, plan.Destinations[0].AssetIdHex);
		Assert.Equal(9_000, plan.Destinations[0].AtomicUnits);

		LiquidWalletUiAssetAmount total = Assert.Single(plan.SelectedTotals);
		Assert.True(total.IsPeggedAsset);
		Assert.Equal(Manifest.PeggedAssetId, total.AssetIdHex);
		Assert.Equal(10_000, total.AtomicUnits);
	}

	// Required evidence §1 (round-trip through the public entry point): a
	// saved state loads and plans through LoadAndCreateSpendPlan to the
	// same projection.
	[Fact]
	public void LoadAndCreateSpendPlanRoundTripsThroughPublicEntryPoint()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 7_500)]));

			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);

			LiquidWalletUiSpendPlan plan = LiquidWalletUiFacade.LoadAndCreateSpendPlan(
				walletDataDir,
				"wallet",
				Manifest,
				key,
				context,
				[OutPointHex(txA, 0)],
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 7_000,
				explicitFeeAtomicUnits: 500);

			Assert.Equal("wallet", plan.WalletName);
			Assert.Equal(1ul, plan.SourceRevision);
			Assert.Equal(1, plan.SelectedInputCount);
			Assert.Equal(1, plan.ConfidentialOutputCount);
			Assert.Equal(500, plan.ExplicitFee.AtomicUnits);
			Assert.True(plan.IsConfidential);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §2: fail-closed on invalid destination. A malformed
	// destination address throws LiquidAddressFormatException; a
	// network-mismatched destination address throws
	// LiquidAddressFormatException; a non-confidential destination address
	// throws ArgumentException from the landed
	// LiquidSuppliedConfidentialDestination.Create. No plan escapes.
	[Fact]
	public void CreateSpendPlanFailsClosedOnInvalidDestination()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
		string[] selectedOutPointHexes = [OutPointHex(txA, 0)];

		// Malformed destination address.
		Assert.Throws<LiquidAddressFormatException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				selectedOutPointHexes,
				"not-a-liquid-address",
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));

		// Network-mismatched destination address: a mainnet confidential
		// address presented against the testnet manifest.
		string mainnetAddress = LiquidAddress.FromScriptPubKey(
				ElementsPublicNetworkManifest.LiquidMainnet,
				ReceiveScript,
				LiquidBlindingPublicKey.Create(BlindingKey))
			.GetCanonicalAddressText();
		Assert.Throws<LiquidAddressFormatException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				selectedOutPointHexes,
				mainnetAddress,
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));

		// Non-confidential destination address: parses for the manifest but
		// carries no blinding key, so the landed destination Create rejects
		// it with ArgumentException.
		string unconfidentialAddress = LiquidAddress.FromScriptPubKey(
				Manifest,
				ReceiveScript,
				LiquidBlindingPublicKey.Create(BlindingKey))
			.GetUnconfidentialAddressText();
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				selectedOutPointHexes,
				unconfidentialAddress,
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));
	}

	// Required evidence §3: fail-closed on insufficient balance. A
	// destination amount exceeding the wallet's balance for that asset
	// throws ArgumentException from the landed
	// LiquidOrdinaryWalletExactSpendPlan.Create (selected-totals /
	// required-totals mismatch). No plan escapes.
	[Fact]
	public void CreateSpendPlanFailsClosedOnInsufficientBalance()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));

		// The destination amount exceeds the selected pegged total (the
		// exact-selection requirement fails).
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				[OutPointHex(txA, 0)],
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 20_000,
				explicitFeeAtomicUnits: 1_000));
	}

	// Required evidence §4: fail-closed on oversized plan. A
	// selected-outpoint count above MaximumSelectedInputCount = 100 throws
	// ArgumentOutOfRangeException from the landed
	// LiquidOrdinaryWalletExactSpendPlan.Create. No plan escapes.
	[Fact]
	public void CreateSpendPlanFailsClosedOnOversizedPlan()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));

		// 101 distinct well-formed outpoint hexes (the selection count
		// exceeds the landed maximum before any balance check).
		string[] oversizedSelection = new string[
			LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1];
		for (int index = 0; index < oversizedSelection.Length; index++)
		{
			oversizedSelection[index] = OutPointHex(txA, (uint)index);
		}

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				oversizedSelection,
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));
	}

	// Required evidence §4 (destination-count row): a destination count
	// above MaximumConfidentialOutputCount = 255 throws
	// ArgumentOutOfRangeException from the landed
	// LiquidSuppliedConfidentialDestinationBatch.Create. The facade's
	// single-destination surface cannot reach that count, so this row
	// exercises the landed batch bound directly.
	[Fact]
	public void DestinationBatchFailsClosedAboveMaximumDestinationCount()
	{
		LiquidAddress address = LiquidAddress.Parse(Manifest, ConfidentialAddress());
		LiquidAssetAmount amount = LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 1);
		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(
				Manifest,
				address,
				PeggedAsset,
				amount,
				LiquidWalletLabelSet.Empty);

		var destinations = new LiquidSuppliedConfidentialDestination[
			LiquidSuppliedConfidentialDestinationBatch.MaximumDestinationCount + 1];
		Array.Fill(destinations, destination);

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidSuppliedConfidentialDestinationBatch.Create(destinations));
	}

	// Required evidence §4 (outpoint hex decode): a malformed outpoint hex
	// string (wrong length, non-hex character, or null element) is wrapped
	// fail-closed as ArgumentException naming selectedOutPointHexes; a
	// well-formed hex whose consensus bytes are not a spendable outpoint
	// (a zero transaction id) surfaces the landed ArgumentException. No
	// plan escapes.
	[Fact]
	public void CreateSpendPlanFailsClosedOnInvalidOutPointHex()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));

		// Wrong length (70 hex chars, not 72).
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				[OutPointHex(txA, 0)[..70]],
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));

		// Non-hex character.
		string nonHex = OutPointHex(txA, 0);
		nonHex = "zz" + nonHex[2..];
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				[nonHex],
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));

		// Null element.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				[null!],
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));

		// Well-formed 72-char hex whose transaction id is zero (coinbase):
		// the landed ParseSpendableConsensusBytes rejects it.
		string zeroTxIdOutPoint = new string('0', 64) + "00000000";
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet",
				Manifest,
				state,
				[zeroTxIdOutPoint],
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000));
	}

	// Required evidence §5: fail-closed on stale revision. A
	// caller-supplied expectedRevision behind the loaded state's Revision
	// throws InvalidOperationException from the landed EnsureRevision —
	// through the public LoadAndCreateSpendPlan entry point, through the
	// internal CreateSpendPlan composition point, and at the state level
	// directly. No plan escapes.
	[Fact]
	public void CreateSpendPlanFailsClosedOnStaleRevision()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidTransactionId txB = Tx('b');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]))
				.Apply(1, Delta(txB, [], [Output(txB, 0, PeggedAsset, 5_000)]));
			Assert.Equal(2ul, state.Revision);

			string confidentialAddress = ConfidentialAddress();
			string[] selectedOutPointHexes = [OutPointHex(txA, 0)];

			// The internal composition point: a stale expectedRevision
			// throws the landed InvalidOperationException.
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletUiFacade.CreateSpendPlan(
					"wallet",
					Manifest,
					state,
					selectedOutPointHexes,
					confidentialAddress,
					Manifest.PeggedAssetId,
					destinationAtomicUnits: 9_000,
					explicitFeeAtomicUnits: 1_000,
					expectedRevision: 1));

			// The state-level row exercises the same fence directly.
			LiquidAddress address = LiquidAddress.Parse(Manifest, confidentialAddress);
			LiquidAssetAmount amount = LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 9_000);
			LiquidSuppliedConfidentialDestination destination =
				LiquidSuppliedConfidentialDestination.Create(
					Manifest,
					address,
					PeggedAsset,
					amount,
					LiquidWalletLabelSet.Empty);
			LiquidSuppliedConfidentialDestinationBatch batch =
				LiquidSuppliedConfidentialDestinationBatch.Create([destination]);
			LiquidAssetAmount explicitFee = LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 1_000);
			Assert.Throws<InvalidOperationException>(() =>
				state.CreateExactOrdinaryWalletSpendPlan(
					expectedRevision: 1,
					[LiquidOutPoint.CreateSpendable(txA, 0)],
					batch,
					explicitFee));

			// The public entry point: a stale expectedRevision against the
			// freshly loaded state throws the landed
			// InvalidOperationException.
			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSpendPlan(
					walletDataDir,
					"wallet",
					Manifest,
					key,
					context,
					selectedOutPointHexes,
					confidentialAddress,
					Manifest.PeggedAssetId,
					destinationAtomicUnits: 9_000,
					explicitFeeAtomicUnits: 1_000,
					expectedRevision: 1));

			// The matching expectedRevision passes the fence.
			LiquidWalletUiSpendPlan plan = LiquidWalletUiFacade.LoadAndCreateSpendPlan(
				walletDataDir,
				"wallet",
				Manifest,
				key,
				context,
				selectedOutPointHexes,
				confidentialAddress,
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 9_000,
				explicitFeeAtomicUnits: 1_000,
				expectedRevision: 2);
			Assert.Equal(2ul, plan.SourceRevision);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §6: fail-closed on manifest mismatch. FromPlan with
	// a plan.GetPeggedAssetId().CanonicalRpcHex not equal to
	// manifest.PeggedAssetId throws ArgumentException and yields no plan.
	[Fact]
	public void FromPlanFailsClosedOnManifestMismatch()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));

		LiquidOrdinaryWalletExactSpendPlan plan = BuildLandedPlan(state, txA);

		// The plan is bound to the testnet manifest; projecting it against
		// the mainnet manifest fails closed.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSpendPlan.FromPlan(
				"wallet",
				ElementsPublicNetworkManifest.LiquidMainnet,
				plan));
	}

	// Required evidence §7: facade projection boundary. Reflection rows
	// prove each new public snapshot type exposes exactly the frozen
	// property set and no other public instance property.
	[Fact]
	public void SpendPlanSnapshotExposesExactlyTheFrozenPropertySet()
	{
		Assert.Equal(
			[
				"ConfidentialOutputCount",
				"Destinations",
				"ExplicitFee",
				"IsConfidential",
				"NetworkManifestId",
				"PeggedAssetIdHex",
				"SelectedInputCount",
				"SelectedTotals",
				"SourceRevision",
				"WalletName",
			],
			PublicInstancePropertyNames(typeof(LiquidWalletUiSpendPlan)));

		Assert.Equal(
			[
				"AssetIdHex",
				"AtomicUnits",
				"ConfidentialAddressText",
				"IsConfidential",
				"IsPeggedAsset",
				"UnconfidentialAddressText",
			],
			PublicInstancePropertyNames(typeof(LiquidWalletUiSpendPlanDestination)));

		Assert.Equal(
			[
				"AssetIdHex",
				"AtomicUnits",
				"IsPeggedAsset",
			],
			PublicInstancePropertyNames(typeof(LiquidWalletUiAssetAmount)));
	}

	// Null-argument rows for the two new facade methods and the three new
	// factories.
	[Fact]
	public void NullArgumentRows()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
			string[] selectedOutPointHexes = [OutPointHex(txA, 0)];
			string confidentialAddress = ConfidentialAddress();

			// CreateSpendPlan null-argument rows.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSpendPlan(
					null!, Manifest, state, selectedOutPointHexes, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSpendPlan(
					"wallet", null!, state, selectedOutPointHexes, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSpendPlan(
					"wallet", Manifest, null!, selectedOutPointHexes, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSpendPlan(
					"wallet", Manifest, state, null!, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSpendPlan(
					"wallet", Manifest, state, selectedOutPointHexes, null!,
					Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSpendPlan(
					"wallet", Manifest, state, selectedOutPointHexes, confidentialAddress,
					null!, 9_000, 1_000));

			// LoadAndCreateSpendPlan null-argument rows.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSpendPlan(
					"dir", "wallet", null!, key, context, selectedOutPointHexes,
					confidentialAddress, Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSpendPlan(
					"dir", "wallet", Manifest, key, context, null!,
					confidentialAddress, Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSpendPlan(
					"dir", "wallet", Manifest, key, context, selectedOutPointHexes,
					null!, Manifest.PeggedAssetId, 9_000, 1_000));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSpendPlan(
					"dir", "wallet", Manifest, key, context, selectedOutPointHexes,
					confidentialAddress, null!, 9_000, 1_000));

			// FromPlan / FromDestination / FromAmount null-argument rows.
			LiquidOrdinaryWalletExactSpendPlan plan = BuildLandedPlan(state, txA);
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSpendPlan.FromPlan(null!, Manifest, plan));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSpendPlan.FromPlan("wallet", null!, plan));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSpendPlan.FromPlan("wallet", Manifest, null!));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSpendPlanDestination.FromDestination(null!));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiAssetAmount.FromAmount(null!));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// The facade's own argument validation: an empty selection, a
	// non-positive destination amount, and a non-positive explicit fee are
	// rejected before any state access.
	[Fact]
	public void CreateSpendPlanValidatesArgumentsBeforePlanning()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));

		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet", Manifest, state, [], ConfidentialAddress(),
				Manifest.PeggedAssetId, 9_000, 1_000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet", Manifest, state, [OutPointHex(txA, 0)], ConfidentialAddress(),
				Manifest.PeggedAssetId, 0, 1_000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletUiFacade.CreateSpendPlan(
				"wallet", Manifest, state, [OutPointHex(txA, 0)], ConfidentialAddress(),
				Manifest.PeggedAssetId, 9_000, 0));
	}

	private static LiquidOrdinaryWalletExactSpendPlan BuildLandedPlan(
		LiquidWalletState state,
		LiquidTransactionId transactionId)
	{
		LiquidAddress address = LiquidAddress.Parse(
			ElementsPublicNetworkManifest.LiquidTestnet,
			ConfidentialAddress());
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(
			ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		LiquidAssetAmount amount = LiquidAssetAmount.Create(peggedAsset, peggedAsset, 9_000);
		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(
				ElementsPublicNetworkManifest.LiquidTestnet,
				address,
				peggedAsset,
				amount,
				LiquidWalletLabelSet.Empty);
		LiquidSuppliedConfidentialDestinationBatch batch =
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]);
		LiquidAssetAmount explicitFee = LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1_000);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[LiquidOutPoint.CreateSpendable(transactionId, 0)],
			batch,
			explicitFee);
	}

	private static string[] PublicInstancePropertyNames(Type type) =>
		type
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(property => property.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();

	private static string ConfidentialAddress() =>
		LiquidAddress.FromScriptPubKey(
				Manifest,
				ReceiveScript,
				LiquidBlindingPublicKey.Create(BlindingKey))
			.GetCanonicalAddressText();

	private static string OutPointHex(LiquidTransactionId transactionId, uint outputIndex) =>
		Convert.ToHexString(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex).ToConsensusBytes());

	private static string GetWorkDir()
	{
		string dir = Common.GetWorkDir();
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidSpendKeyReference Key(LiquidKeyBranch branch, uint index) =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), branch, index);

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidSpendKeyReference key = ExternalKey;
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			key.GetScriptPubKey(),
			LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits),
			key);
	}

	private static LiquidWalletTransactionDelta Delta(
		LiquidTransactionId transactionId,
		System.Collections.Generic.IEnumerable<LiquidOutPoint> spent,
		System.Collections.Generic.IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);
}
