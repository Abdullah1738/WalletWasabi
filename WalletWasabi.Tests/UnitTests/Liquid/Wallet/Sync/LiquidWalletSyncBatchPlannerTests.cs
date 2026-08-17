using System;
using System.Linq;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Sync;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Sync;

[Collection("Serial unit tests collection")]
public class LiquidWalletSyncBatchPlannerTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string StartupIdHex = "abababababababababababababababababababababababababababababababab";
	private const string GenesisBlockHashHex = "cd179c84c35f51825f20a3b91a18d45f0c53b5ceb744a5b6ef8f0babe809396f";
	private const string ParentGenesisHex = "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206";
	private const string BestBlockHashHex = "0101010101010101010101010101010101010101010101010101010101010101";
	private const string BlockHashHex = "4444444444444444444444444444444444444444444444444444444444444444";
	private const int ObservedBlocks = 42;

	[Fact]
	public void CreateRequestsRejectsDuplicateIntentTxid()
	{
		string txid = Txid(1);
		LiquidWalletSyncBatchPlanner.FetchIntent[] intents =
		[
			new(txid, null),
			new(txid.ToUpperInvariant(), BlockHashHex),
		];

		ArgumentException failure = Assert.Throws<ArgumentException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests(intents));
		Assert.Equal("intents", failure.ParamName);
	}

	[Fact]
	public void CreateRequestsRejectsZeroAndOneHundredOneIntents()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests([]));

		LiquidWalletSyncBatchPlanner.FetchIntent[] tooMany = Enumerable
			.Range(1, LiquidWalletSyncBatchPlanner.MaximumRequestCount + 1)
			.Select(index => new LiquidWalletSyncBatchPlanner.FetchIntent(Txid(index), null))
			.ToArray();
		ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests(tooMany));
		Assert.Equal("intents", failure.ParamName);
	}

	[Fact]
	public void CreateRequestsAcceptsExactCapAndNormalizesToCanonicalLowercaseHex()
	{
		LiquidWalletSyncBatchPlanner.FetchIntent[] intents = Enumerable
			.Range(1, LiquidWalletSyncBatchPlanner.MaximumRequestCount)
			.Select(index => new LiquidWalletSyncBatchPlanner.FetchIntent(
				Txid(index).ToUpperInvariant(),
				index == 1 ? BlockHashHex.ToUpperInvariant() : null))
			.ToArray();

		ElementsRawTransactionRequest[] requests = LiquidWalletSyncBatchPlanner.CreateRequests(intents);

		Assert.Equal(LiquidWalletSyncBatchPlanner.MaximumRequestCount, requests.Length);
		for (int index = 0; index < requests.Length; index++)
		{
			Assert.Equal(Txid(index + 1), requests[index].TransactionId);
			Assert.Equal(
				index == 0 ? BlockHashHex : null,
				requests[index].BlockHash);
		}
	}

	[Fact]
	public void CreateRequestsRejectsNullAndMalformedIntents()
	{
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests(null!));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests([null!]));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests([new("0102", null)]));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests([new(new string('0', 64), null)]));
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletSyncBatchPlanner.CreateRequests([new(Txid(1), "0102")]));
	}

	[Fact]
	public void VerifyAcceptsExactRequestSet()
	{
		ElementsRawTransactionRequest[] requests = LiquidWalletSyncBatchPlanner.CreateRequests(
			[
				new(Txid(1), null),
				new(Txid(2), BlockHashHex),
			]);
		ElementsExpectationBoundRawTransactionBatch batch = Batch(
			(requests[0], [0x01, 0x02, 0x03]),
			(requests[1], [0x0a]));

		LiquidWalletSyncBatchPlanner.Verify(batch, requests);
	}

	[Fact]
	public void VerifyRejectsBatchMissingRequestedTxid()
	{
		ElementsRawTransactionRequest[] requests = LiquidWalletSyncBatchPlanner.CreateRequests(
			[
				new(Txid(1), null),
				new(Txid(2), null),
			]);
		ElementsExpectationBoundRawTransactionBatch batch = Batch((requests[0], [0x01]));

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletSyncBatchPlanner.Verify(batch, requests));
	}

	[Fact]
	public void VerifyRejectsBatchCarryingExtraTxid()
	{
		ElementsRawTransactionRequest[] requests = LiquidWalletSyncBatchPlanner.CreateRequests(
			[new(Txid(1), null)]);
		ElementsRawTransactionRequest extra = new(Txid(2), null);
		ElementsExpectationBoundRawTransactionBatch batch = Batch(
			(requests[0], [0x01]),
			(extra, [0x02]));

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletSyncBatchPlanner.Verify(batch, requests));
	}

	[Fact]
	public void VerifyRejectsZeroLengthTransactionBytes()
	{
		ElementsRawTransactionRequest[] requests = LiquidWalletSyncBatchPlanner.CreateRequests(
			[new(Txid(1), null)]);
		ElementsExpectationBoundRawTransactionBatch batch = Batch((requests[0], []));

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletSyncBatchPlanner.Verify(batch, requests));
	}

	[Fact]
	public void VerifyRejectsDuplicateBatchTxidsAndNullArguments()
	{
		ElementsRawTransactionRequest[] requests = LiquidWalletSyncBatchPlanner.CreateRequests(
			[
				new(Txid(1), null),
				new(Txid(2), null),
			]);
		ElementsExpectationBoundRawTransactionBatch duplicated = Batch(
			(requests[0], [0x01]),
			(requests[0], [0x02]));

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletSyncBatchPlanner.Verify(duplicated, requests));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncBatchPlanner.Verify(null!, requests));
		ElementsExpectationBoundRawTransactionBatch batch = Batch((requests[0], [0x01]));
		Assert.Throws<ArgumentNullException>(() =>
			LiquidWalletSyncBatchPlanner.Verify(batch, null!));
	}

	private static ElementsExpectationBoundRawTransactionBatch Batch(
		params (ElementsRawTransactionRequest Request, byte[] Bytes)[] rows) =>
		new(
			Observation(),
			rows
				.Select(row => new ElementsRawTransactionObservation(row.Request, row.Bytes))
				.ToArray());

	private static ElementsExpectationBoundNodeObservation Observation() =>
		new(
			Expectation(),
			PeggedAssetHex,
			NodeStatus(),
			Generation());

	private static ElementsNodeExpectation Expectation() =>
		new(
			Chain: "elementsregtest",
			GenesisBlockHash: GenesisBlockHashHex,
			FedpegScript: "51",
			PeggedAsset: PeggedAssetHex,
			ParentGenesisBlockHash: ParentGenesisHex,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

	private static ElementsNodeStatus NodeStatus() =>
		new(
			Chain: "elementsregtest",
			Blocks: ObservedBlocks,
			Headers: ObservedBlocks,
			BestBlockHash: BestBlockHashHex,
			GenesisBlockHash: GenesisBlockHashHex,
			InitialBlockDownload: false,
			Pruned: false,
			TrimHeaders: false,
			BlockchainWarningsPresent: false,
			NetworkActive: true,
			LocalRelay: true,
			NetworkWarningsPresent: false,
			FedpegScript: "51",
			PeggedAsset: PeggedAssetHex,
			ParentGenesisBlockHash: ParentGenesisHex,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

	private static ElementsNodeGenerationObservation Generation() =>
		new(StartupIdHex, 9, ObservedBlocks, BestBlockHashHex);

	private static string Txid(int ordinal)
	{
		// Digits-only 64-character hex so ToUpperInvariant stays valid hex.
		string digits = string.Create(64, ordinal, static (span, value) =>
		{
			span.Fill('0');
			int position = span.Length - 1;
			while (value > 0)
			{
				span[position--] = (char)('0' + value % 10);
				value /= 10;
			}
		});
		return digits;
	}
}
