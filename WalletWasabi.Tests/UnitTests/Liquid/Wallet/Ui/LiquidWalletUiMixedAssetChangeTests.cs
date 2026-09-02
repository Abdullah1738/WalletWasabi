using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

/// <summary>
/// LIQUID-SEND-MIXED-ASSET-CHANGE-001: facade-level proof that a selection whose per-asset
/// totals exceed the destination plus explicit fee appends wallet-owned change destinations so
/// the exact plan validator balances per asset, while an exact (no-surplus) selection keeps the
/// one-destination batch byte-identical. The change destinations are supplied to the facade as
/// public-safe (asset, confidential address, amount) values; the facade owns only the
/// surplus computation and batch composition.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletUiMixedAssetChangeTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlindingKeyHex = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";
	private const string IssuedAssetAHex = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
	private const string ChangePublicKeyHex = "03f028892bad7ed57d2fb57bf33081d5cfcf6f9ed3d3d7f159c2e2fff579dc341a";
	private const string ChangeBlindingKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAssetA => LiquidAssetId.ParseRpcHex(IssuedAssetAHex);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);
	private static LiquidSpendKeyReference ChangeKey => LiquidSpendKeyReference.Create(
		Convert.FromHexString(ChangePublicKeyHex), LiquidKeyBranch.Internal, 0);
	private static byte[] BlindingKey => Convert.FromHexString(BlindingKeyHex);
	private static byte[] ChangeBlindingKey => Convert.FromHexString(ChangeBlindingKeyHex);
	private static byte[] ReceiveScript => ExternalKey.GetScriptPubKey();
	private static byte[] ChangeScript => ChangeKey.GetScriptPubKey();

	// Surplus exists: 1000 pegged + 5000 issued selected, destination 5000 issued, fee 100 pegged.
	// Pegged surplus = 1000 - 100 = 900 > 0, issued surplus = 5000 - 5000 = 0. One change
	// destination (900 pegged) is appended; the plan balances per asset.
	[Fact]
	public void SurplusAppendsChangeDestination()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txB = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, IssuedAssetA, 5_000)]));

		string changeAddress = ConfidentialChangeAddress();
		var changeDestination = new LiquidWalletUiChangeDestination(changeAddress);

		LiquidWalletUiSpendPlan plan = LiquidWalletUiFacade.CreateSpendPlan(
			"wallet",
			Manifest,
			state,
			[OutPointHex(txA, 0), OutPointHex(txB, 0)],
			ConfidentialAddress(),
			IssuedAssetAHex,
			destinationAtomicUnits: 5_000,
			explicitFeeAtomicUnits: 100,
			changeDestination: changeDestination);

		Assert.Equal(2, plan.ConfidentialOutputCount);
		Assert.Equal(2, plan.Destinations.Count);

		// The user destination is first; the change destination is appended after it.
		Assert.Equal(IssuedAssetAHex, plan.Destinations[0].AssetIdHex);
		Assert.Equal(5_000, plan.Destinations[0].AtomicUnits);
		Assert.False(plan.Destinations[0].IsWalletOwnedChange);

		LiquidWalletUiSpendPlanDestination changeRow = plan.Destinations[1];
		Assert.Equal(Manifest.PeggedAssetId, changeRow.AssetIdHex);
		Assert.Equal(900, changeRow.AtomicUnits);
		Assert.True(changeRow.IsConfidential);
		// The additive change-attribution flag is set exactly on the change row.
		Assert.True(changeRow.IsWalletOwnedChange);
		Assert.Equal(
			LiquidAddress.Parse(Manifest, changeAddress).GetCanonicalAddressText(),
			changeRow.ConfidentialAddressText);

		// The exact-selection requirement now balances per asset: pegged selected total equals
		// change (900) plus fee (100); issued selected total equals the destination (5000).
		Assert.Equal(2, plan.SelectedTotals.Count);
		Assert.Equal(IssuedAssetAHex, plan.SelectedTotals[0].AssetIdHex);
		Assert.Equal(5_000, plan.SelectedTotals[0].AtomicUnits);
		Assert.Equal(Manifest.PeggedAssetId, plan.SelectedTotals[1].AssetIdHex);
		Assert.Equal(1_000, plan.SelectedTotals[1].AtomicUnits);
	}

	// No surplus: an exact selection (1000 = 900 destination + 100 fee, pegged only) supplies no
	// change destinations; the one-destination batch is byte-identical to the pre-change shape.
	[Fact]
	public void NoSurplusKeepsOneDestinationBatch()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]));

		LiquidWalletUiSpendPlan plan = LiquidWalletUiFacade.CreateSpendPlan(
			"wallet",
			Manifest,
			state,
			[OutPointHex(txA, 0)],
			ConfidentialAddress(),
			Manifest.PeggedAssetId,
			destinationAtomicUnits: 900,
			explicitFeeAtomicUnits: 100,
			changeDestination: null);

		Assert.Equal(1, plan.ConfidentialOutputCount);
		LiquidWalletUiSpendPlanDestination destination = Assert.Single(plan.Destinations);
		Assert.Equal(Manifest.PeggedAssetId, destination.AssetIdHex);
		Assert.Equal(900, destination.AtomicUnits);
		// No change destination supplied: the flag is never fabricated.
		Assert.False(destination.IsWalletOwnedChange);

		LiquidWalletUiAssetAmount total = Assert.Single(plan.SelectedTotals);
		Assert.True(total.IsPeggedAsset);
		Assert.Equal(1_000, total.AtomicUnits);
	}

	private static string ConfidentialAddress() =>
		LiquidAddress.FromScriptPubKey(
				Manifest,
				ReceiveScript,
				LiquidBlindingPublicKey.Create(BlindingKey))
			.GetCanonicalAddressText();

	private static string ConfidentialChangeAddress() =>
		LiquidAddress.FromScriptPubKey(
				Manifest,
				ChangeScript,
				LiquidBlindingPublicKey.Create(ChangeBlindingKey))
			.GetCanonicalAddressText();

	private static string OutPointHex(LiquidTransactionId transactionId, uint outputIndex) =>
		Convert.ToHexString(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex).ToConsensusBytes());

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
