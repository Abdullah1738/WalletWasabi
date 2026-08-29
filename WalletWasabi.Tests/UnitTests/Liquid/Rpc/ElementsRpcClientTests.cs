using System.Collections.Generic;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Wire;
using Xunit;
using LiquidOrdinaryWalletPlanEncodedFrame = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanEncodedFrame;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;

namespace WalletWasabi.Tests.UnitTests.Liquid.Rpc;

public class ElementsRpcClientTests
{
	private const string BestBlockHash = "0101010101010101010101010101010101010101010101010101010101010101";
	private const string GenesisBlockHash = "cd179c84c35f51825f20a3b91a18d45f0c53b5ceb744a5b6ef8f0babe809396f";
	private const string PeggedAsset = "b2e15d0d7a0c94e4e2ce0fe6e8691b9e451377f6e46e8045a86f7c4b5d4f0f23";
	private const string ParentGenesis = "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206";

	[Fact]
	public async Task ProbesElementsIdentityInClosedOrderAsync()
	{
		using var harness = new ElementsRpcHarness(ValidResult);

		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None);

		Assert.Equal(["getnetworkinfo", "getblockchaininfo", "getblockhash", "getblockhash", "getsidechaininfo"], harness.Handler.Methods);
		Assert.Equal(["1", "2", "3", "4", "5"], harness.Handler.Ids);
		Assert.Equal(["[]", "[]", "[42]", "[0]", "[]"], harness.Handler.Parameters);
		Assert.Equal("elementsregtest", status.Chain);
		Assert.Equal(42, status.Blocks);
		Assert.Equal(42, status.Headers);
		Assert.Equal(BestBlockHash, status.BestBlockHash);
		Assert.Equal(GenesisBlockHash, status.GenesisBlockHash);
		Assert.False(status.InitialBlockDownload);
		Assert.False(status.Pruned);
		Assert.False(status.TrimHeaders);
		Assert.Equal("51", status.FedpegScript);
		Assert.Equal(PeggedAsset, status.PeggedAsset);
		Assert.Equal(ParentGenesis, status.ParentGenesisBlockHash);
		Assert.Equal(8, status.PeginConfirmationDepth);
		Assert.False(status.EnforcePak);
		Assert.Equal(230303, status.Version);
		Assert.Equal(70016, status.ProtocolVersion);
		Assert.Equal("/Elements Core:23.3.3/", status.Subversion);
		Assert.True(status.HasSynchronizedTipObservation);
		Assert.True(status.HasCompleteArchiveObservation);
		Assert.True(status.HasClearWarningObservation);
		Assert.True(status.HasOnlineNetworkObservation);
		Assert.All(harness.Handler.Requests, request =>
		{
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.Equal("application/json", request.ContentType);
		});
	}

	[Fact]
	public async Task AcceptsOnlyTheConfiguredStableIdentityAsync()
	{
		using var harness = new ElementsRpcHarness(ValidResult);
		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None);
		var expected = new ElementsNodeExpectation(
			Chain: "elementsregtest",
			GenesisBlockHash,
			FedpegScript: "51",
			PeggedAsset,
			ParentGenesisBlockHash: ParentGenesis,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

		status.EnsureMatches(expected);

		var wrong = expected with { Chain = "liquidv1", EnforcePak = true };
		var exception = Assert.Throws<ElementsNodeMismatchException>(() => status.EnsureMatches(wrong));
		Assert.Equal(["chain", "enforce_pak"], exception.MismatchedFields);
		Assert.DoesNotContain(PeggedAsset, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProbesAndBindsReviewedPublicNetworkInOneOperationAsync()
	{
		using var harness = new ElementsRpcHarness(LiquidTestnetResult);

		ElementsManifestBoundObservation observation = await harness.Client.GetPublicNetworkObservationAsync(
			ElementsPublicNetworkManifest.LiquidTestnet,
			CancellationToken.None);

		Assert.Same(ElementsPublicNetworkManifest.LiquidTestnet, observation.Manifest);
		Assert.Equal("liquidtestnet", observation.NodeStatus.Chain);
		Assert.Equal(ElementsNodeManifestBindingLevel.SelfReportedManifestTupleObservationOnly, observation.BindingLevel);
		Assert.False(observation.HasArtifactSourceAttestation);
		Assert.False(observation.HasEffectiveFeeAssetObservation);
		Assert.False(observation.HasAtomicGenerationObservation);
		Assert.False(observation.HasRuntimeQualification);
		Assert.False(observation.HasPublicCtFixtureQualification);
		Assert.Equal(["getnetworkinfo", "getblockchaininfo", "getblockhash", "getblockhash", "getsidechaininfo"], harness.Handler.Methods);
	}

	[Fact]
	public async Task BracketsEffectiveFeeAssetWithExactNodeGenerationAsync()
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		const string OverrideFeeAsset = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnodegeneration" => Envelope(
				invocation.Id,
				GenerationResult(StartupId, 9, 42, BestBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, FeeAssetResult(PeggedAsset, OverrideFeeAsset)),
			_ => throw new InvalidOperationException(),
		});

		ElementsFeeAssetGenerationObservation observation =
			await harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None);

		Assert.Equal(PeggedAsset, observation.PeggedAsset);
		Assert.Equal(OverrideFeeAsset, observation.EffectiveFeeAsset);
		Assert.False(observation.UsesPeggedAssetForFees);
		Assert.False(observation.ChainstateChangedDuringObservation);
		Assert.Equal(StartupId, observation.GenerationBefore.StartupId);
		Assert.Equal(9UL, observation.GenerationBefore.ChainstateRevision);
		Assert.Equal(observation.GenerationBefore, observation.GenerationAfter);
		Assert.Equal(["getnodegeneration", "getsidechaininfo", "getnodegeneration"], harness.Handler.Methods);
		Assert.All(harness.Handler.Parameters, parameters => Assert.Equal("[]", parameters));
	}

	[Fact]
	public async Task SerializesOtherPublicProbesOutsideFeeAssetGenerationBracketAsync()
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		var middleCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseMiddleCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int phase = 0;
		int sidechainCalls = 0;
		using var harness = new ElementsRpcHarness(async (invocation, cancellationToken) =>
		{
			if (invocation.Method == "getnodegeneration")
			{
				return Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash));
			}
			if (invocation.Method == "getsidechaininfo")
			{
				int sidechainCall = sidechainCalls++;
				if (sidechainCall == 0 && phase != 2)
				{
					middleCallEntered.TrySetResult();
					await releaseMiddleCall.Task.WaitAsync(cancellationToken);
					if (phase == 0)
					{
						return Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset));
					}
				}
				if (phase is 1 or 2 && sidechainCall == 1)
				{
					return Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset));
				}
				if (phase == 3 && sidechainCall == 1)
				{
					string planPeggedAsset = ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId;
					return Envelope(invocation.Id, FeeAssetResult(planPeggedAsset, planPeggedAsset));
				}
			}
			if (phase == 2 && invocation.Method == "getrawtransaction")
			{
				middleCallEntered.TrySetResult();
				await releaseMiddleCall.Task.WaitAsync(cancellationToken);
				return Envelope(invocation.Id, JsonSerializer.Serialize("0102"));
			}
			if (phase == 3)
			{
				if (invocation.Method == "getrawtransaction")
				{
					return Envelope(invocation.Id, JsonSerializer.Serialize("0102"));
				}
				return LiquidTestnetResult(invocation);
			}

			return ValidResult(invocation);
		});

		Task<ElementsFeeAssetGenerationObservation> observationTask =
			harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None);
		await middleCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<ElementsNodeStatus> statusTask = harness.Client.GetNodeStatusAsync(CancellationToken.None);
		try
		{
			Assert.Equal(["getnodegeneration", "getsidechaininfo"], harness.Handler.Methods);
			Assert.False(statusTask.IsCompleted);
		}
		finally
		{
			releaseMiddleCall.TrySetResult();
		}

		ElementsFeeAssetGenerationObservation observation =
			await observationTask.WaitAsync(TimeSpan.FromSeconds(5));
		ElementsNodeStatus status = await statusTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(observation.ChainstateChangedDuringObservation);
		Assert.Equal("elementsregtest", status.Chain);
		Assert.Equal(
			[
				"getnodegeneration",
				"getsidechaininfo",
				"getnodegeneration",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
			],
			harness.Handler.Methods);

		phase = 1;
		sidechainCalls = 0;
		middleCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		releaseMiddleCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int methodCountBeforeComposite = harness.Handler.Methods.Count;
		Task<ElementsExpectationBoundNodeObservation> compositeTask =
			harness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation(),
				PeggedAsset,
				CancellationToken.None);
		await middleCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<ElementsNodeStatus> competingStatusTask = harness.Client.GetNodeStatusAsync(CancellationToken.None);
		try
		{
			Assert.Equal(
				[
					"getnodegeneration",
					"getnetworkinfo",
					"getblockchaininfo",
					"getblockhash",
					"getblockhash",
					"getsidechaininfo",
				],
				harness.Handler.Methods.Skip(methodCountBeforeComposite));
			Assert.False(competingStatusTask.IsCompleted);
		}
		finally
		{
			releaseMiddleCall.TrySetResult();
		}

		ElementsExpectationBoundNodeObservation compositeObservation =
			await compositeTask.WaitAsync(TimeSpan.FromSeconds(5));
		ElementsNodeStatus competingStatus =
			await competingStatusTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(compositeObservation.HasExactGenerationFenceObservation);
		Assert.Equal("elementsregtest", competingStatus.Chain);
		Assert.Equal(
			[
				"getnodegeneration",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
				"getnodegeneration",
				"getnodegeneration",
				"getsidechaininfo",
				"getnodegeneration",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
			],
			harness.Handler.Methods.Skip(methodCountBeforeComposite));

		phase = 2;
		sidechainCalls = 0;
		middleCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		releaseMiddleCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int methodCountBeforeRawTransactions = harness.Handler.Methods.Count;
		Task<ElementsExpectationBoundRawTransactionBatch> rawTransactionsTask =
			harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
				CancellationToken.None);
		await middleCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<ElementsNodeStatus> statusDuringRawTransactionsTask =
			harness.Client.GetNodeStatusAsync(CancellationToken.None);
		try
		{
			Assert.Equal(
				[
					"getnodegeneration",
					"getnetworkinfo",
					"getblockchaininfo",
					"getblockhash",
					"getblockhash",
					"getsidechaininfo",
					"getnodegeneration",
					"getnodegeneration",
					"getsidechaininfo",
					"getnodegeneration",
					"getrawtransaction",
				],
				harness.Handler.Methods.Skip(methodCountBeforeRawTransactions));
			Assert.False(statusDuringRawTransactionsTask.IsCompleted);
		}
		finally
		{
			releaseMiddleCall.TrySetResult();
		}

		ElementsExpectationBoundRawTransactionBatch rawTransactions =
			await rawTransactionsTask.WaitAsync(TimeSpan.FromSeconds(5));
		ElementsNodeStatus statusAfterRawTransactions =
			await statusDuringRawTransactionsTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Single(rawTransactions.GetTransactions());
		Assert.Equal("elementsregtest", statusAfterRawTransactions.Chain);
		Assert.Equal(
			[
				"getnodegeneration",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
				"getnodegeneration",
				"getnodegeneration",
				"getsidechaininfo",
				"getnodegeneration",
				"getrawtransaction",
				"getnodegeneration",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
			],
			harness.Handler.Methods.Skip(methodCountBeforeRawTransactions));

		phase = 3;
		sidechainCalls = 0;
		middleCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		releaseMiddleCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int methodCountBeforePlanEncoding = harness.Handler.Methods.Count;
		LiquidOrdinaryWalletExactSpendPlan plan = CreateExpectationBoundPlan();
		byte[] planSourceEpoch = PlanSourceEpoch();
		IReadOnlyList<string>?[] planPreviousTransactionIds = [Array.Empty<string>()];
		Task<(
			ElementsExpectationBoundNodeObservation NodeObservation,
			LiquidOrdinaryWalletPlanEncodedFrame Frame)> planEncodingTask =
			harness.Client.EncodeExpectationBoundOrdinaryWalletPlanAsync(
				LiquidTestnetExpectation(),
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				planSourceEpoch,
				plan,
				planPreviousTransactionIds,
				CancellationToken.None);
		await middleCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		CryptographicOperations.ZeroMemory(planSourceEpoch);
		planPreviousTransactionIds[0] = [RawTransactionIdTwo];
		Task<ElementsNodeStatus> statusDuringPlanEncodingTask =
			harness.Client.GetNodeStatusAsync(CancellationToken.None);
		try
		{
			Assert.Equal(
				[
					"getblockchaininfo",
					"getblockhash",
					"getnetworkinfo",
					"getblockchaininfo",
					"getblockhash",
					"getblockhash",
					"getsidechaininfo",
				],
				harness.Handler.Methods.Skip(methodCountBeforePlanEncoding));
			Assert.False(statusDuringPlanEncodingTask.IsCompleted);
		}
		finally
		{
			releaseMiddleCall.TrySetResult();
		}

		(
			ElementsExpectationBoundNodeObservation planObservation,
			LiquidOrdinaryWalletPlanEncodedFrame planFrame) =
			await planEncodingTask.WaitAsync(TimeSpan.FromSeconds(5));
		using (planFrame)
		{
			Assert.True(planObservation.HasExactGenerationFenceObservation);
			byte[] frameBytes = new byte[planFrame.Length];
			try
			{
				planFrame.CopyFrameTo(frameBytes);
				Assert.Equal(PlanSourceEpoch(), frameBytes[24..56]);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(frameBytes);
			}
		}
		ElementsNodeStatus statusAfterPlanEncoding =
			await statusDuringPlanEncodingTask.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("liquidtestnet", statusAfterPlanEncoding.Chain);
		Assert.Equal(
			[
				"getblockchaininfo",
				"getblockhash",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockchaininfo",
				"getblockhash",
				"getsidechaininfo",
				"getblockchaininfo",
				"getblockhash",
				"getrawtransaction",
				"getblockchaininfo",
				"getblockhash",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
			],
			harness.Handler.Methods.Skip(methodCountBeforePlanEncoding));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CancellationDuringBracketReleasesProbeLockAsync(bool cancelClosingGeneration)
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		var blockedCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int phase = 0;
		int generationCalls = 0;
		int sidechainCalls = 0;
		using var harness = new ElementsRpcHarness(async (invocation, cancellationToken) =>
		{
			if (invocation.Method == "getnodegeneration")
			{
				generationCalls++;
				if (cancelClosingGeneration
					&& ((phase == 0 && generationCalls == 2)
						|| (phase == 1 && generationCalls == 4)))
				{
					blockedCallEntered.TrySetResult();
					await neverCompletes.Task.WaitAsync(cancellationToken);
				}

				return Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash));
			}
			if (invocation.Method == "getsidechaininfo")
			{
				int sidechainCall = sidechainCalls++;
				if (!cancelClosingGeneration && sidechainCall == 0 && phase is 0 or 1)
				{
					blockedCallEntered.TrySetResult();
					await neverCompletes.Task.WaitAsync(cancellationToken);
				}
				if ((phase == 0 && sidechainCall == 0)
					|| (phase is 1 or 3 && sidechainCall == 1))
				{
					return Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset));
				}
				if (phase == 5 && sidechainCall == 1)
				{
					string planPeggedAsset = ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId;
					return Envelope(invocation.Id, FeeAssetResult(planPeggedAsset, planPeggedAsset));
				}
			}
			if (phase is 3 or 5 && invocation.Method == "getrawtransaction")
			{
				blockedCallEntered.TrySetResult();
				await neverCompletes.Task.WaitAsync(cancellationToken);
			}
			if (phase == 5)
			{
				return LiquidTestnetResult(invocation);
			}

			return ValidResult(invocation);
		});
		using var cancellation = new CancellationTokenSource();

		Task<ElementsFeeAssetGenerationObservation> observationTask =
			harness.Client.GetFeeAssetGenerationObservationAsync(cancellation.Token);
		await blockedCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => observationTask.WaitAsync(TimeSpan.FromSeconds(5)));
		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("elementsregtest", status.Chain);
		Assert.Equal(cancelClosingGeneration ? 8 : 7, harness.Handler.Methods.Count);
		Assert.Equal("getnetworkinfo", harness.Handler.Methods[cancelClosingGeneration ? 3 : 2]);

		phase = 1;
		generationCalls = 0;
		sidechainCalls = 0;
		blockedCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int methodCountBeforeComposite = harness.Handler.Methods.Count;
		using var compositeCancellation = new CancellationTokenSource();
		Task<ElementsExpectationBoundNodeObservation> compositeTask =
			harness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation(),
				PeggedAsset,
				compositeCancellation.Token);
		await blockedCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		compositeCancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => compositeTask.WaitAsync(TimeSpan.FromSeconds(5)));
		phase = 2;
		ElementsNodeStatus statusAfterCompositeCancellation =
			await harness.Client.GetNodeStatusAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("elementsregtest", statusAfterCompositeCancellation.Chain);
		Assert.Equal(
			cancelClosingGeneration ? 15 : 11,
			harness.Handler.Methods.Count - methodCountBeforeComposite);
		Assert.Equal(
			"getnetworkinfo",
			harness.Handler.Methods[methodCountBeforeComposite + (cancelClosingGeneration ? 10 : 6)]);

		phase = 3;
		generationCalls = 0;
		sidechainCalls = 0;
		blockedCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int methodCountBeforeRawTransactions = harness.Handler.Methods.Count;
		using var rawTransactionsCancellation = new CancellationTokenSource();
		Task<ElementsExpectationBoundRawTransactionBatch> rawTransactionsTask =
			harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
				rawTransactionsCancellation.Token);
		await blockedCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		rawTransactionsCancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => rawTransactionsTask.WaitAsync(TimeSpan.FromSeconds(5)));
		phase = 4;
		ElementsNodeStatus statusAfterRawTransactionCancellation =
			await harness.Client.GetNodeStatusAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("elementsregtest", statusAfterRawTransactionCancellation.Chain);
		Assert.Equal(16, harness.Handler.Methods.Count - methodCountBeforeRawTransactions);
		Assert.Equal("getrawtransaction", harness.Handler.Methods[methodCountBeforeRawTransactions + 10]);
		Assert.Equal("getnetworkinfo", harness.Handler.Methods[methodCountBeforeRawTransactions + 11]);

		phase = 5;
		generationCalls = 0;
		sidechainCalls = 0;
		blockedCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int methodCountBeforePlanEncoding = harness.Handler.Methods.Count;
		using var planEncodingCancellation = new CancellationTokenSource();
		Task<(
			ElementsExpectationBoundNodeObservation NodeObservation,
			LiquidOrdinaryWalletPlanEncodedFrame Frame)> planEncodingTask =
			harness.Client.EncodeExpectationBoundOrdinaryWalletPlanAsync(
				LiquidTestnetExpectation(),
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				PlanSourceEpoch(),
				CreateExpectationBoundPlan(),
				[Array.Empty<string>()],
				planEncodingCancellation.Token);
		await blockedCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		planEncodingCancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => planEncodingTask.WaitAsync(TimeSpan.FromSeconds(5)));
		phase = 6;
		ElementsNodeStatus statusAfterPlanEncodingCancellation =
			await harness.Client.GetNodeStatusAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("elementsregtest", statusAfterPlanEncodingCancellation.Chain);
		Assert.Equal(20, harness.Handler.Methods.Count - methodCountBeforePlanEncoding);
		Assert.Equal("getrawtransaction", harness.Handler.Methods[methodCountBeforePlanEncoding + 14]);
		Assert.Equal("getnetworkinfo", harness.Handler.Methods[methodCountBeforePlanEncoding + 15]);
	}

	[Theory]
	[InlineData(10UL, 43, "0202020202020202020202020202020202020202020202020202020202020202")]
	[InlineData(10UL, 42, BestBlockHash)]
	public async Task AcceptsAdvancedGenerationIncludingAbaTipAsync(
		ulong closingRevision,
		int closingBlocks,
		string closingHash)
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		int generationCalls = 0;
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnodegeneration" => Envelope(
				invocation.Id,
				generationCalls++ == 0
					? GenerationResult(StartupId, 9, 42, BestBlockHash)
					: GenerationResult(StartupId, closingRevision, closingBlocks, closingHash)),
			"getsidechaininfo" => Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset)),
			_ => throw new InvalidOperationException(),
		});

		ElementsFeeAssetGenerationObservation observation =
			await harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None);

		Assert.True(observation.UsesPeggedAssetForFees);
		Assert.True(observation.ChainstateChangedDuringObservation);
		Assert.Equal(closingRevision, observation.GenerationAfter.ChainstateRevision);
		Assert.Equal(closingBlocks, observation.GenerationAfter.Blocks);
		Assert.Equal(closingHash, observation.GenerationAfter.BestBlockHash);
	}

	[Theory]
	[InlineData("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", 9UL, 42, BestBlockHash, "startup_id")]
	[InlineData("abababababababababababababababababababababababababababababababab", 8UL, 42, BestBlockHash, "chainstate_revision")]
	[InlineData("abababababababababababababababababababababababababababababababab", 9UL, 43, BestBlockHash, "inconsistent tip")]
	[InlineData("abababababababababababababababababababababababababababababababab", 9UL, 42, "0202020202020202020202020202020202020202020202020202020202020202", "inconsistent tip")]
	public async Task RejectsInconsistentGenerationFenceWithoutValuesAsync(
		string closingStartupId,
		ulong closingRevision,
		int closingBlocks,
		string closingHash,
		string expectedReason)
	{
		const string OpeningStartupId = "abababababababababababababababababababababababababababababababab";
		int generationCalls = 0;
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnodegeneration" => Envelope(
				invocation.Id,
				generationCalls++ == 0
					? GenerationResult(OpeningStartupId, 9, 42, BestBlockHash)
					: GenerationResult(closingStartupId, closingRevision, closingBlocks, closingHash)),
			"getsidechaininfo" => Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset)),
			_ => throw new InvalidOperationException(),
		});

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains(expectedReason, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(OpeningStartupId, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(closingStartupId, exception.Message, StringComparison.Ordinal);
		Assert.Equal(3, harness.Handler.Methods.Count);
	}

	[Theory]
	[InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	public async Task RequiresCanonicalEffectiveFeeAssetBeforeClosingGenerationAsync(string invalidFeeAsset)
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnodegeneration" => Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, FeeAssetResult(PeggedAsset, invalidFeeAsset)),
			_ => throw new InvalidOperationException(),
		});

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None));

		Assert.Contains("fee_asset", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(invalidFeeAsset, exception.Message, StringComparison.Ordinal);
		Assert.Equal(["getnodegeneration", "getsidechaininfo"], harness.Handler.Methods);
	}

	[Fact]
	public async Task RequiresEffectiveFeeAssetFieldBeforeClosingGenerationAsync()
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnodegeneration" => Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
			_ => throw new InvalidOperationException(),
		});

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None));

		Assert.Contains("fee_asset", exception.Message, StringComparison.Ordinal);
		Assert.Equal(["getnodegeneration", "getsidechaininfo"], harness.Handler.Methods);
	}

	[Fact]
	public async Task DefaultsEffectiveFeeAssetToPeggedAssetWhenSchema2ManifestOmitsFeeAssetFieldAsync()
	{
		string testnetPeggedAsset = ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId;
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult()),
			"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, SidechainResult(peggedAsset: testnetPeggedAsset)),
			_ => throw new InvalidOperationException($"Unexpected RPC method '{invocation.Method}'."),
		});

		ElementsFeeAssetGenerationObservation observation =
			await harness.Client.GetFeeAssetGenerationObservationAsync(
				ElementsPublicNetworkManifest.LiquidTestnet,
				CancellationToken.None);

		Assert.Equal(testnetPeggedAsset, observation.PeggedAsset);
		Assert.Equal(testnetPeggedAsset, observation.EffectiveFeeAsset);
		Assert.True(observation.UsesPeggedAssetForFees);
		Assert.Equal(
			["getblockchaininfo", "getblockhash", "getsidechaininfo", "getblockchaininfo", "getblockhash"],
			harness.Handler.Methods);
	}

	[Fact]
	public async Task ManifestLessObservationStillRequiresFeeAssetFieldAsync()
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnodegeneration" => Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
			_ => throw new InvalidOperationException(),
		});

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None));

		Assert.Contains("fee_asset", exception.Message, StringComparison.Ordinal);
		Assert.Equal(["getnodegeneration", "getsidechaininfo"], harness.Handler.Methods);
	}

	[Theory]
	[InlineData("{\"startup_id\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"chainstate_revision\":9,\"blocks\":42,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}", "startup_id")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":1.0,\"blocks\":42,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}", "chainstate_revision")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":1e0,\"blocks\":42,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}", "chainstate_revision")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":\"9\",\"blocks\":42,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}", "chainstate_revision")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":18446744073709551616,\"blocks\":42,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}", "chainstate_revision")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":9,\"blocks\":-1,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}", "blocks")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":9,\"blocks\":1e0,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}", "blocks")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":9,\"blocks\":42,\"bestblockhash\":\"0000000000000000000000000000000000000000000000000000000000000000\"}", "bestblockhash")]
	public async Task RejectsMalformedOpeningNodeGenerationAsync(string generationResult, string field)
	{
		using var harness = new ElementsRpcHarness(invocation => Envelope(invocation.Id, generationResult));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains(field, exception.Message, StringComparison.Ordinal);
		Assert.Single(harness.Handler.Methods);
	}

	[Theory]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":9,\"blocks\":42}")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":9,\"blocks\":42,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\",\"future\":true}")]
	[InlineData("{\"startup_id\":\"abababababababababababababababababababababababababababababababab\",\"chainstate_revision\":9,\"chainstate_revision\":9,\"blocks\":42,\"bestblockhash\":\"0101010101010101010101010101010101010101010101010101010101010101\"}")]
	public async Task RejectsNonExactOpeningNodeGenerationSchemaAsync(string generationResult)
	{
		using var harness = new ElementsRpcHarness(invocation => Envelope(invocation.Id, generationResult));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Single(harness.Handler.Methods);
	}

	[Fact]
	public async Task MissingGenerationRpcDoesNotPoisonLegacyStatusProbeAsync()
	{
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getnodegeneration"
				? $$"""{"result":null,"error":{"code":-32601,"message":"method not found"},"id":"{{invocation.Id}}"}"""
				: ValidResult(invocation));
		harness.Handler.StatusCode = HttpStatusCode.NotFound;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None));
		harness.Handler.StatusCode = HttpStatusCode.OK;
		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None);

		Assert.Equal(ElementsRpcFailureKind.Rpc, exception.FailureKind);
		Assert.Equal(-32601, exception.RpcCode);
		Assert.Equal("elementsregtest", status.Chain);
		Assert.Equal(
			["getnodegeneration", "getnetworkinfo", "getblockchaininfo", "getblockhash", "getblockhash", "getsidechaininfo"],
			harness.Handler.Methods);
	}

	[Fact]
	public async Task BracketsEffectiveFeeAssetWithFallbackTipFenceWhenManifestDeclaresGenerationApiAbsentAsync()
	{
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult()),
			"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset)),
			_ => throw new InvalidOperationException($"Unexpected RPC method '{invocation.Method}'."),
		});

		ElementsFeeAssetGenerationObservation observation =
			await harness.Client.GetFeeAssetGenerationObservationAsync(
				ElementsPublicNetworkManifest.LiquidTestnet,
				CancellationToken.None);

		Assert.True(observation.UsesPeggedAssetForFees);
		Assert.False(observation.ChainstateChangedDuringObservation);
		Assert.Equal("0000000000000000000000000000000000000000000000000000000000000000", observation.GenerationBefore.StartupId);
		Assert.Equal(0UL, observation.GenerationBefore.ChainstateRevision);
		Assert.Equal(42, observation.GenerationBefore.Blocks);
		Assert.Equal(BestBlockHash, observation.GenerationBefore.BestBlockHash);
		Assert.Equal(observation.GenerationBefore, observation.GenerationAfter);
		Assert.Equal(
			["getblockchaininfo", "getblockhash", "getsidechaininfo", "getblockchaininfo", "getblockhash"],
			harness.Handler.Methods);
	}

	[Fact]
	public async Task ManifestGatedFenceKeepsGetNodeGenerationWhenManifestDeclaresItPresentAsync()
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnodegeneration" => Envelope(
				invocation.Id,
				GenerationResult(StartupId, 9, 42, BestBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset)),
			_ => throw new InvalidOperationException($"Unexpected RPC method '{invocation.Method}'."),
		});

		ElementsFeeAssetGenerationObservation observation =
			await harness.Client.GetFeeAssetGenerationObservationAsync(CancellationToken.None);

		Assert.Equal(StartupId, observation.GenerationBefore.StartupId);
		Assert.Equal(9UL, observation.GenerationBefore.ChainstateRevision);
		Assert.Equal(["getnodegeneration", "getsidechaininfo", "getnodegeneration"], harness.Handler.Methods);
	}

	[Theory]
	[InlineData(43, BestBlockHash)]
	[InlineData(42, "0202020202020202020202020202020202020202020202020202020202020202")]
	public async Task RejectsTipDriftInsideFallbackFenceWithoutGetNodeGenerationAsync(
		int closingBlocks,
		string closingHash)
	{
		int blockchainCalls = 0;
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getblockchaininfo" => Envelope(
				invocation.Id,
				++blockchainCalls == 1
					? BlockchainResult()
					: BlockchainResult(blocks: closingBlocks, headers: closingBlocks, bestBlockHash: closingHash)),
			"getblockhash" => Envelope(
				invocation.Id,
				JsonSerializer.Serialize(blockchainCalls <= 1 ? BestBlockHash : closingHash)),
			"getsidechaininfo" => Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset)),
			_ => throw new InvalidOperationException($"Unexpected RPC method '{invocation.Method}'."),
		});

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetFeeAssetGenerationObservationAsync(
				ElementsPublicNetworkManifest.LiquidTestnet,
				CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("inconsistent tip", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("getnodegeneration", harness.Handler.Methods);
	}

	[Fact]
	public async Task RejectsMissingPublicManifestBeforeRpcAsync()
	{
		using var harness = new ElementsRpcHarness(ValidResult);

		await Assert.ThrowsAsync<ArgumentNullException>(
			() => harness.Client.GetPublicNetworkObservationAsync(null!, CancellationToken.None));

		Assert.Empty(harness.Handler.Methods);
	}

	[Fact]
	public async Task RejectsPublicManifestMismatchWithoutObservedValuesAsync()
	{
		using var harness = new ElementsRpcHarness(LiquidTestnetResult);

		var exception = await Assert.ThrowsAsync<ElementsNodeMismatchException>(
			() => harness.Client.GetPublicNetworkObservationAsync(
				ElementsPublicNetworkManifest.LiquidMainnet,
				CancellationToken.None));

		Assert.Contains("chain", exception.MismatchedFields);
		Assert.DoesNotContain("liquidtestnet", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId, exception.Message, StringComparison.Ordinal);
		Assert.Equal(5, harness.Handler.Methods.Count);
	}

	[Fact]
	public async Task ReportsCatchingUpNodeAsNotSynchronizedAsync()
	{
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getblockchaininfo"
				? Envelope(invocation.Id, BlockchainResult(blocks: 41, headers: 42, initialBlockDownload: true))
				: ValidResult(invocation));
		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None);

		Assert.False(status.HasSynchronizedTipObservation);
	}

	[Theory]
	[InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
	[InlineData("B2E15D0D7A0C94E4E2CE0FE6E8691B9E451377F6E46E8045A86F7C4B5D4F0F23")]
	[InlineData("b2e15d0d7a0c94e4e2ce0fe6e8691b9e451377f6e46e8045a86f7c4b5d4f0f2")]
	public async Task RejectsNoncanonicalPeggedAssetWithoutDisclosingItAsync(string invalidAsset)
	{
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getsidechaininfo"
				? Envelope(invocation.Id, SidechainResult(peggedAsset: invalidAsset))
				: ValidResult(invocation));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("pegged_asset", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(invalidAsset, exception.Message, StringComparison.Ordinal);
		Assert.Equal(5, harness.Handler.Methods.Count);
	}

	[Theory]
	[InlineData(-28, 500)]
	[InlineData(-32600, 400)]
	[InlineData(-32601, 404)]
	public async Task PreservesRpcErrorBeforeFollowingCallsAsync(int rpcCode, int httpStatusCode)
	{
		using var harness = new ElementsRpcHarness(invocation => $$"""{"result":null,"error":{"code":{{rpcCode}},"message":"not found"},"id":"{{invocation.Id}}"}""");
		harness.Handler.StatusCode = (HttpStatusCode)httpStatusCode;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Rpc, exception.FailureKind);
		Assert.Equal(rpcCode, exception.RpcCode);
		Assert.Equal((HttpStatusCode)httpStatusCode, exception.HttpStatusCode);
		Assert.Equal($"Elements RPC 'getnetworkinfo' failed with code {rpcCode}.", exception.Message);
		Assert.Single(harness.Handler.Methods);
	}

	[Fact]
	public async Task RejectsMismatchedResponseIdAsync()
	{
		using var harness = new ElementsRpcHarness(invocation => ValidResult(invocation with { Id = "foreign" }));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Contains("response id does not match", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RejectsDuplicateEnvelopeFieldAsync()
	{
		using var harness = new ElementsRpcHarness(invocation =>
			$$"""{"result":{},"result":{},"error":null,"id":"{{invocation.Id}}"}""");

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Contains("duplicate JSON field", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("bestblockhash", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[InlineData("bestblockhash", "0000000000000000000000000000000000000000000000000000000000000000")]
	[InlineData("bestblockhash", "01")]
	public async Task RejectsNonCanonicalStableHashesAsync(string field, string value)
	{
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getblockchaininfo"
				? Envelope(invocation.Id, BlockchainResult(bestBlockHash: value))
				: ValidResult(invocation));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Contains(field, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RejectsBitcoinCoreShapeAsync()
	{
		string bitcoinResult = $$"""{"chain":"regtest","blocks":42,"headers":42,"bestblockhash":"{{BestBlockHash}}","initialblockdownload":false,"pruned":false}""";
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getblockchaininfo"
				? Envelope(invocation.Id, bitcoinResult)
				: ValidResult(invocation));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Contains("trim_headers", exception.Message, StringComparison.Ordinal);
		Assert.Equal(2, harness.Handler.Methods.Count);
	}

	[Fact]
	public async Task RejectsResponseAboveBoundAsync()
	{
		string padding = new('a', 1024 * 1024);
		using var harness = new ElementsRpcHarness(invocation =>
			$$"""{"result":{"padding":"{{padding}}"},"error":null,"id":"{{invocation.Id}}"}""");

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Contains("one-megabyte limit", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RequiresAnAbsoluteRpcEndpoint()
	{
		using var handler = new ElementsRpcHandler(ValidResult);
		using var httpClient = new HttpClient(handler, disposeHandler: false);

		Assert.Throws<ArgumentException>(() => new ElementsRpcClient(httpClient));
	}

	[Theory]
	[InlineData("http://localhost:18884/")]
	[InlineData("http://127.0.0.2:18884/")]
	[InlineData("http://[::1]:18884/")]
	[InlineData("ftp://127.0.0.1:18884/")]
	[InlineData("http://user:fixture@127.0.0.1:18884/")]
	[InlineData("http://127.0.0.1:18884/rpc")]
	[InlineData("http://127.0.0.1:18884/?node=one")]
	public void RejectsUnsafeRpcEndpoints(string endpoint)
	{
		using var handler = new ElementsRpcHandler(ValidResult);
		using var httpClient = new HttpClient(handler, disposeHandler: false)
		{
			BaseAddress = new Uri(endpoint),
		};

		Assert.Throws<ArgumentException>(() => new ElementsRpcClient(httpClient));
	}

	[Fact]
	public void CreatesOwnedRedirectDisabledTransport()
	{
		var timeouts = new ElementsRpcTimeouts(
			ConnectTimeout: TimeSpan.FromSeconds(1),
			TotalRequestTimeout: TimeSpan.FromSeconds(3),
			ResponseIdleTimeout: TimeSpan.FromSeconds(2));
		using SocketsHttpHandler handler = ElementsRpcClient.CreateTransportHandler(
			new NetworkCredential("fixture-user", "fixture-credential"),
			timeouts);

		Assert.False(handler.AllowAutoRedirect);
		Assert.False(handler.UseCookies);
		Assert.False(handler.UseProxy);
		Assert.Equal(1, handler.MaxConnectionsPerServer);
		Assert.Equal(timeouts.ConnectTimeout, handler.ConnectTimeout);

		using ElementsRpcClient client = ElementsRpcClient.Create(
			new Uri("https://elements.example/"),
			new NetworkCredential("fixture-user", "fixture-credential"),
			timeouts);
	}

	[Theory]
	[InlineData(300)]
	[InlineData(301)]
	[InlineData(302)]
	[InlineData(303)]
	[InlineData(307)]
	[InlineData(308)]
	public async Task RejectsRedirectWithoutFollowingAsync(int statusCode)
	{
		using var harness = new ElementsRpcHarness(ValidResult);
		harness.Handler.StatusCode = (HttpStatusCode)statusCode;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Http, exception.FailureKind);
		Assert.Equal((HttpStatusCode)statusCode, exception.HttpStatusCode);
		Assert.Single(harness.Handler.Methods);
	}

	[Fact]
	public async Task PreservesUnauthorizedStatusAsync()
	{
		using var harness = new ElementsRpcHarness(ValidResult);
		harness.Handler.StatusCode = HttpStatusCode.Unauthorized;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Http, exception.FailureKind);
		Assert.Null(exception.RpcCode);
		Assert.Equal(HttpStatusCode.Unauthorized, exception.HttpStatusCode);
	}

	[Fact]
	public async Task RejectsInvalidContentTypeAsync()
	{
		using var harness = new ElementsRpcHarness(ValidResult);
		harness.Handler.ContentType = "text/plain";

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("content type", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RejectsChangedResponseEndpointAsync()
	{
		using var harness = new ElementsRpcHarness(ValidResult);
		harness.Handler.ResponseUriOverride = new Uri("https://other.example/");

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("endpoint changed", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RejectsMalformedRpcErrorCategoryAsync()
	{
		using var harness = new ElementsRpcHarness(invocation =>
			$$"""{"result":null,"error":"malformed","id":"{{invocation.Id}}"}""");
		harness.Handler.StatusCode = HttpStatusCode.InternalServerError;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Null(exception.RpcCode);
		Assert.Equal(HttpStatusCode.InternalServerError, exception.HttpStatusCode);
	}

	[Theory]
	[InlineData("{\"message\":\"missing code\"}")]
	[InlineData("{\"code\":-28}")]
	[InlineData("{\"code\":\"-28\",\"message\":\"wrong type\"}")]
	[InlineData("{\"code\":-28,\"message\":7}")]
	[InlineData("{\"code\":-28,\"message\":\"ok\",\"data\":null}")]
	public async Task RejectsMalformedRpcErrorObjectsAsync(string error)
	{
		using var harness = new ElementsRpcHarness(invocation =>
			$$"""{"result":null,"error":{{error}},"id":"{{invocation.Id}}"}""");
		harness.Handler.StatusCode = HttpStatusCode.InternalServerError;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Null(exception.RpcCode);
		Assert.Equal(HttpStatusCode.InternalServerError, exception.HttpStatusCode);
	}

	[Fact]
	public async Task RejectsNonNullResultAlongsideRpcErrorAsync()
	{
		using var harness = new ElementsRpcHarness(invocation =>
			$$"""{"result":{},"error":{"code":-28,"message":"warmup"},"id":"{{invocation.Id}}"}""");
		harness.Handler.StatusCode = HttpStatusCode.InternalServerError;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("null result", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(-28, 404)]
	[InlineData(-32600, 500)]
	[InlineData(-32601, 500)]
	[InlineData(-32601, 200)]
	public async Task RejectsMismatchedRpcHttpStatusAsync(int rpcCode, int httpStatusCode)
	{
		using var harness = new ElementsRpcHarness(invocation =>
			$$"""{"result":null,"error":{"code":{{rpcCode}},"message":"mismatch"},"id":"{{invocation.Id}}"}""");
		harness.Handler.StatusCode = (HttpStatusCode)httpStatusCode;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Null(exception.RpcCode);
		Assert.Contains("HTTP status", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(401)]
	[InlineData(403)]
	[InlineData(502)]
	[InlineData(503)]
	public async Task PreservesUnrelatedHttpFailuresAsync(int httpStatusCode)
	{
		using var harness = new ElementsRpcHarness(ValidResult);
		harness.Handler.StatusCode = (HttpStatusCode)httpStatusCode;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Http, exception.FailureKind);
		Assert.Null(exception.RpcCode);
		Assert.Equal((HttpStatusCode)httpStatusCode, exception.HttpStatusCode);
	}

	[Fact]
	public async Task RejectsNestedDuplicateFieldAsync()
	{
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getnetworkinfo"
				? Envelope(invocation.Id, AddProperty(NetworkResult(), "\"nested\":{\"value\":1,\"value\":2}"))
				: ValidResult(invocation));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("duplicate JSON field", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RejectsSubLimitOversizedJsonStringAsync()
	{
		string padding = new('a', 65537);
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getnetworkinfo"
				? Envelope(invocation.Id, AddProperty(NetworkResult(), $"\"padding\":\"{padding}\""))
				: ValidResult(invocation));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("JSON string limit", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RejectsSubLimitOversizedJsonArrayAsync()
	{
		string values = string.Join(',', Enumerable.Repeat("0", 4097));
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getnetworkinfo"
				? Envelope(invocation.Id, AddProperty(NetworkResult(), $"\"padding\":[{values}]"))
				: ValidResult(invocation));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("array-item limit", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RejectsBatchResponseRootAsync()
	{
		using var harness = new ElementsRpcHarness(invocation =>
			$$"""[{"result":{},"error":null,"id":"{{invocation.Id}}"}]""");

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("object result is required", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReportsWarningsAsNonAuthoritativeObservationsAsync()
	{
		using (var legacyHarness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnetworkinfo" => Envelope(invocation.Id, NetworkResult(warnings: "network warning")),
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult(warnings: "chain warning")),
			_ => ValidResult(invocation),
		}))
		{
			ElementsNodeStatus legacyStatus = await legacyHarness.Client.GetNodeStatusAsync(CancellationToken.None);

			Assert.True(legacyStatus.NetworkWarningsPresent);
			Assert.True(legacyStatus.BlockchainWarningsPresent);
			Assert.False(legacyStatus.HasClearWarningObservation);
			Assert.True(legacyStatus.HasCompleteArchiveObservation);
		}

		using (var arrayHarness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnetworkinfo" => Envelope(invocation.Id, NetworkResult(warningsJson: "[\"network warning\"]")),
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult(warningsJson: "[\"chain warning\",\"second warning\"]")),
			_ => ValidResult(invocation),
		}))
		{
			ElementsNodeStatus arrayStatus = await arrayHarness.Client.GetNodeStatusAsync(CancellationToken.None);

			Assert.True(arrayStatus.NetworkWarningsPresent);
			Assert.True(arrayStatus.BlockchainWarningsPresent);
			Assert.False(arrayStatus.HasClearWarningObservation);
		}

		using var emptyArrayHarness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnetworkinfo" => Envelope(invocation.Id, NetworkResult(warningsJson: "[]")),
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult(warningsJson: "[]")),
			_ => ValidResult(invocation),
		});
		ElementsNodeStatus emptyArrayStatus = await emptyArrayHarness.Client.GetNodeStatusAsync(CancellationToken.None);

		Assert.False(emptyArrayStatus.NetworkWarningsPresent);
		Assert.False(emptyArrayStatus.BlockchainWarningsPresent);
		Assert.True(emptyArrayStatus.HasClearWarningObservation);

		using var boundaryHarness = new ElementsRpcHarness(BoundaryWarningsResult);
		ElementsNodeStatus boundaryStatus = await boundaryHarness.Client.GetNodeStatusAsync(CancellationToken.None);
		Assert.True(boundaryStatus.NetworkWarningsPresent);
		Assert.True(boundaryStatus.BlockchainWarningsPresent);
	}

	[Fact]
	public async Task RejectsMissingWarningsBeforeFollowingCallsAsync()
	{
		using var missingHarness = new ElementsRpcHarness(MissingWarningsResult);
		var missingException = await Assert.ThrowsAsync<ElementsRpcException>(
			() => missingHarness.Client.GetNodeStatusAsync(CancellationToken.None));
		Assert.Contains("warnings", missingException.Message, StringComparison.Ordinal);
		Assert.Single(missingHarness.Handler.Methods);

		Func<RpcInvocation, string>[] invalidResults = new Func<RpcInvocation, string>[]
		{
			NullWarningsResult,
			ObjectWarningsResult,
			NumberWarningsResult,
			BooleanWarningsResult,
			MixedArrayWarningsResult,
			EmptyEntryWarningsResult,
			TooManyWarningsResult,
			TooLongWarningsResult,
			TooLongLegacyWarningsResult,
		};
		foreach (Func<RpcInvocation, string> invalidResult in invalidResults)
		{
			using var harness = new ElementsRpcHarness(invalidResult);
			ElementsRpcException? exception = null;
			try
			{
				await harness.Client.GetNodeStatusAsync(CancellationToken.None);
			}
			catch (ElementsRpcException candidate)
			{
				exception = candidate;
			}

			AssertInvalidWarningsFailure(Assert.IsType<ElementsRpcException>(exception), harness);
		}
	}

	[Fact]
	public async Task AcceptsPreservedElements2333RegtestResponsesAsync()
	{
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnetworkinfo" => Envelope(invocation.Id, GoldenNetworkResult()),
			"getblockchaininfo" => Envelope(invocation.Id, GoldenBlockchainResult()),
			"getblockhash" when invocation.Parameters == "[0]" => Envelope(invocation.Id, JsonSerializer.Serialize(GenesisBlockHash)),
			"getsidechaininfo" => Envelope(invocation.Id, GoldenSidechainResult()),
			_ => throw new InvalidOperationException(),
		});

		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None);

		Assert.Equal(["getnetworkinfo", "getblockchaininfo", "getblockhash", "getsidechaininfo"], harness.Handler.Methods);
		Assert.Equal("/Elements Core:23.3.3/", status.Subversion);
		Assert.Equal(GenesisBlockHash, status.GenesisBlockHash);
		Assert.Equal(PeggedAsset, status.PeggedAsset);
		Assert.Equal(8, status.PeginConfirmationDepth);
		Assert.False(status.HasSynchronizedTipObservation);
		Assert.True(status.HasClearWarningObservation);
		Assert.True(status.HasOnlineNetworkObservation);
	}

	[Fact]
	public async Task AcceptsLiquidTestnetZeroParentGenesisAsync()
	{
		const string ZeroParent = "0000000000000000000000000000000000000000000000000000000000000000";
		using var harness = new ElementsRpcHarness(invocation =>
			invocation.Method == "getsidechaininfo"
				? Envelope(invocation.Id, SidechainResult(parentGenesis: ZeroParent))
				: ValidResult(invocation));

		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None);

		Assert.Equal(ZeroParent, status.ParentGenesisBlockHash);
		var expectation = new ElementsNodeExpectation(
			Chain: "elementsregtest",
			GenesisBlockHash,
			FedpegScript: "51",
			PeggedAsset,
			ParentGenesisBlockHash: ZeroParent,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");
		status.EnsureMatches(expectation);
	}

	[Fact]
	public async Task EnforcesResponseBodyIdleTimeoutAsync()
	{
		using var handler = new StreamResponseHandler(() => new StallingStream());
		using var httpClient = CreateStreamingHttpClient(handler);
		using var client = new ElementsRpcClient(httpClient, new ElementsRpcTimeouts(
			ConnectTimeout: TimeSpan.FromMilliseconds(50),
			TotalRequestTimeout: TimeSpan.FromMilliseconds(500),
			ResponseIdleTimeout: TimeSpan.FromMilliseconds(75)));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Timeout, exception.FailureKind);
		Assert.Contains("idle timeout", exception.Message, StringComparison.Ordinal);
		Assert.Equal(1, handler.RequestCount);
	}

	[Fact]
	public async Task EnforcesTotalTimeoutAgainstSlowTrickleAsync()
	{
		byte[] slowBody = Encoding.UTF8.GetBytes(new string(' ', 1024));
		using var handler = new StreamResponseHandler(() => new TrickleStream(slowBody, TimeSpan.FromMilliseconds(25)));
		using var httpClient = CreateStreamingHttpClient(handler);
		using var client = new ElementsRpcClient(httpClient, new ElementsRpcTimeouts(
			ConnectTimeout: TimeSpan.FromMilliseconds(50),
			TotalRequestTimeout: TimeSpan.FromMilliseconds(400),
			ResponseIdleTimeout: TimeSpan.FromMilliseconds(250)));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Timeout, exception.FailureKind);
		Assert.Contains("total request timeout", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PropagatesCallerCancellationAsync()
	{
		using var handler = new StreamResponseHandler(() => new StallingStream());
		using var httpClient = CreateStreamingHttpClient(handler);
		using var client = new ElementsRpcClient(httpClient, new ElementsRpcTimeouts(
			ConnectTimeout: TimeSpan.FromMilliseconds(100),
			TotalRequestTimeout: TimeSpan.FromSeconds(60),
			ResponseIdleTimeout: TimeSpan.FromSeconds(30)));
		using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => client.GetNodeStatusAsync(cancellation.Token));
	}

	[Fact]
	public async Task UsesSnapshottedTimeoutAfterHttpClientMutationAsync()
	{
		using var handler = new StreamResponseHandler(() => new StallingStream());
		using var httpClient = CreateStreamingHttpClient(handler);
		using var client = new ElementsRpcClient(httpClient, new ElementsRpcTimeouts(
			ConnectTimeout: TimeSpan.FromMilliseconds(50),
			TotalRequestTimeout: TimeSpan.FromMilliseconds(300),
			ResponseIdleTimeout: TimeSpan.FromMilliseconds(100)));
		httpClient.Timeout = Timeout.InfiniteTimeSpan;

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Timeout, exception.FailureKind);
		Assert.Contains("idle timeout", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PreservesTransportFailureCategoryAsync()
	{
		using var handler = new ThrowingHandler();
		using var httpClient = new HttpClient(handler, disposeHandler: false)
		{
			BaseAddress = new Uri("http://127.0.0.1:18884/"),
		};
		using var client = new ElementsRpcClient(httpClient);

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Transport, exception.FailureKind);
		Assert.Null(exception.HttpStatusCode);
		Assert.Null(exception.RpcCode);
	}

	[Theory]
	[InlineData(0, 1, 1)]
	[InlineData(1, 0, 1)]
	[InlineData(1, 1, 0)]
	[InlineData(2, 1, 1)]
	[InlineData(1, 1, 2)]
	public void RejectsInvalidTimeoutProfiles(int connectSeconds, int totalSeconds, int idleSeconds)
	{
		var timeouts = new ElementsRpcTimeouts(
			TimeSpan.FromSeconds(connectSeconds),
			TimeSpan.FromSeconds(totalSeconds),
			TimeSpan.FromSeconds(idleSeconds));

		Assert.Throws<ArgumentOutOfRangeException>(() => timeouts.Validate());
	}

	private static HttpClient CreateStreamingHttpClient(HttpMessageHandler handler) =>
		new(handler, disposeHandler: false)
		{
			BaseAddress = new Uri("http://127.0.0.1:18884/"),
		};

	private static string ValidResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnetworkinfo" => Envelope(invocation.Id, NetworkResult()),
		"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult()),
		"getblockhash" when invocation.Parameters == "[0]" => Envelope(invocation.Id, JsonSerializer.Serialize(GenesisBlockHash)),
		"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
		"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
		_ => throw new InvalidOperationException($"Unexpected RPC method '{invocation.Method}' with parameters '{invocation.Parameters}'."),
	};

	private static string LiquidTestnetResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnetworkinfo" => Envelope(invocation.Id, NetworkResult()),
		"getblockchaininfo" => Envelope(
			invocation.Id,
			BlockchainResult(
				blocks: 42,
				headers: 42,
				bestBlockHash: BestBlockHash,
				chain: "liquidtestnet")),
		"getblockhash" when invocation.Parameters == "[0]" => Envelope(
			invocation.Id,
			JsonSerializer.Serialize(ElementsPublicNetworkManifest.LiquidTestnet.GenesisBlockHash)),
		"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
		"getsidechaininfo" => Envelope(
			invocation.Id,
			SidechainResult(
				parentGenesis: ElementsPublicNetworkManifest.LiquidTestnet.ParentGenesisHash,
				peggedAsset: ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				peginConfirmationDepth: 0)),
		_ => throw new InvalidOperationException($"Unexpected RPC method '{invocation.Method}' with parameters '{invocation.Parameters}'."),
	};

	private static string FeeAssetResult(string peggedAsset, string effectiveFeeAsset) =>
		$$"""{"pegged_asset":"{{peggedAsset}}","fee_asset":"{{effectiveFeeAsset}}"}""";

	private static string GenerationResult(string startupId, ulong revision, int blocks, string bestBlockHash) =>
		$$"""{"startup_id":"{{startupId}}","chainstate_revision":{{revision}},"blocks":{{blocks}},"bestblockhash":"{{bestBlockHash}}"}""";

	private static string BlockchainResult(
		int blocks = 42,
		int headers = 42,
		bool initialBlockDownload = false,
		string bestBlockHash = BestBlockHash,
		string warnings = "",
		string chain = "elementsregtest",
		string? warningsJson = null) =>
		$$"""{"chain":"{{chain}}","blocks":{{blocks}},"headers":{{headers}},"bestblockhash":"{{bestBlockHash}}","initialblockdownload":{{initialBlockDownload.ToString().ToLowerInvariant()}},"pruned":false,"trim_headers":false,"warnings":{{warningsJson ?? JsonSerializer.Serialize(warnings)}}}""";

	private static string SidechainResult(
		string parentGenesis = ParentGenesis,
		string peggedAsset = PeggedAsset,
		int peginConfirmationDepth = 8) =>
		$$"""{"fedpegscript":"51","pegged_asset":"{{peggedAsset}}","parent_blockhash":"{{parentGenesis}}","pegin_confirmation_depth":{{peginConfirmationDepth}},"enforce_pak":false}""";

	private static string NetworkResult(string warnings = "", string? warningsJson = null) =>
		$$"""{"version":230303,"protocolversion":70016,"subversion":"/Elements Core:23.3.3/","localrelay":true,"networkactive":true,"warnings":{{warningsJson ?? JsonSerializer.Serialize(warnings)}}}""";

	private static string MissingWarningsResult(RpcInvocation invocation) => Envelope(
		invocation.Id,
		"{\"version\":230303,\"protocolversion\":70016,\"subversion\":\"/Elements Core:23.3.3/\",\"localrelay\":true,\"networkactive\":true}");

	private static string NullWarningsResult(RpcInvocation invocation) =>
		Envelope(invocation.Id, NetworkResult(warningsJson: "null"));

	private static string ObjectWarningsResult(RpcInvocation invocation) =>
		Envelope(invocation.Id, NetworkResult(warningsJson: "{}"));

	private static string NumberWarningsResult(RpcInvocation invocation) =>
		Envelope(invocation.Id, NetworkResult(warningsJson: "42"));

	private static string BooleanWarningsResult(RpcInvocation invocation) =>
		Envelope(invocation.Id, NetworkResult(warningsJson: "true"));

	private static string MixedArrayWarningsResult(RpcInvocation invocation) =>
		Envelope(invocation.Id, NetworkResult(warningsJson: "[\"do-not-leak\",1]"));

	private static string EmptyEntryWarningsResult(RpcInvocation invocation) =>
		Envelope(invocation.Id, NetworkResult(warningsJson: "[\"\"]"));

	private static string TooManyWarningsResult(RpcInvocation invocation) => Envelope(
		invocation.Id,
		NetworkResult(warningsJson: $"[{string.Join(',', Enumerable.Repeat("\"warning\"", 65))}]"));

	private static string TooLongWarningsResult(RpcInvocation invocation) => Envelope(
		invocation.Id,
		NetworkResult(warningsJson: JsonSerializer.Serialize(new[] { new string('w', 4097) })));

	private static string TooLongLegacyWarningsResult(RpcInvocation invocation) =>
		Envelope(invocation.Id, NetworkResult(warnings: new string('w', 4097)));

	private static string BoundaryWarningsResult(RpcInvocation invocation)
	{
		string warningsJson = JsonSerializer.Serialize(Enumerable.Repeat(new string('w', 64), 64));
		return invocation.Method switch
		{
			"getnetworkinfo" => Envelope(invocation.Id, NetworkResult(warningsJson: warningsJson)),
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult(warningsJson: warningsJson)),
			_ => ValidResult(invocation),
		};
	}

	private static void AssertInvalidWarningsFailure(
		ElementsRpcException exception,
		ElementsRpcHarness harness)
	{
		Assert.Equal(
			"Elements RPC 'node status' returned an invalid result: field 'warnings' must be a bounded string or string array.",
			exception.Message);
		Assert.DoesNotContain("do-not-leak", exception.Message, StringComparison.Ordinal);
		Assert.Single(harness.Handler.Methods);
	}

	private static string AddProperty(string jsonObject, string property) =>
		$"{jsonObject[..^1]},{property}}}";

	private static string GoldenNetworkResult() =>
		"""
		{
		  "version": 230303,
		  "subversion": "/Elements Core:23.3.3/",
		  "protocolversion": 70016,
		  "localservices": "0000000000000409",
		  "localservicesnames": ["NETWORK", "WITNESS", "NETWORK_LIMITED"],
		  "localrelay": true,
		  "timeoffset": 0,
		  "networkactive": true,
		  "connections": 0,
		  "connections_in": 0,
		  "connections_out": 0,
		  "networks": [
		    {"name":"ipv4","limited":false,"reachable":true,"proxy":"","proxy_randomize_credentials":false},
		    {"name":"ipv6","limited":false,"reachable":true,"proxy":"","proxy_randomize_credentials":false},
		    {"name":"onion","limited":true,"reachable":false,"proxy":"","proxy_randomize_credentials":false},
		    {"name":"i2p","limited":true,"reachable":false,"proxy":"","proxy_randomize_credentials":false},
		    {"name":"cjdns","limited":true,"reachable":false,"proxy":"","proxy_randomize_credentials":false}
		  ],
		  "relayfee": 0.00000100,
		  "incrementalfee": 0.00000100,
		  "localaddresses": [],
		  "warnings": ""
		}
		""";

	private static string GoldenBlockchainResult() =>
		$$"""
		{
		  "chain": "elementsregtest",
		  "blocks": 0,
		  "headers": 0,
		  "bestblockhash": "{{GenesisBlockHash}}",
		  "time": 1296688602,
		  "mediantime": 1296688602,
		  "verificationprogress": 0,
		  "initialblockdownload": true,
		  "size_on_disk": 218,
		  "pruned": false,
		  "trim_headers": false,
		  "current_params_root": "3700bdb2975ff8e0dadaaba2b33857b0ca2610c950a92b1db725025e3647a8e1",
		  "current_signblock_asm": "0 4ae81572f06e1b88fd5ced7a1a000945432e83e1551e6f721ee9c00b8cc33260",
		  "current_signblock_hex": "00204ae81572f06e1b88fd5ced7a1a000945432e83e1551e6f721ee9c00b8cc33260",
		  "max_block_witness": 74,
		  "current_fedpeg_program": "a91472c44f957fc011d97e3406667dca5b1c930c402687",
		  "current_fedpeg_script": "51",
		  "extension_space": ["02fcba7ecf41bc7e1be4ee122d9d22e3333671eb0a3a87b5cdf099d59874e1940f02fcba7ecf41bc7e1be4ee122d9d22e3333671eb0a3a87b5cdf099d59874e1940f"],
		  "epoch_length": 10,
		  "total_valid_epochs": 2,
		  "epoch_age": 0,
		  "warnings": ""
		}
		""";

	private static string GoldenSidechainResult() =>
		$$"""
		{
		  "fedpegscript": "51",
		  "current_fedpeg_programs": ["a91472c44f957fc011d97e3406667dca5b1c930c402687"],
		  "current_fedpegscripts": ["51"],
		  "pegged_asset": "{{PeggedAsset}}",
		  "min_peg_diff": "7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
		  "parent_blockhash": "{{ParentGenesis}}",
		  "parent_chain_has_pow": true,
		  "enforce_pak": false,
		  "pegin_confirmation_depth": 8
		}
		""";

	private static string Envelope(string id, string result) =>
		$$"""{"result":{{result}},"error":null,"id":"{{id}}"}""";

	private sealed class ElementsRpcHandler : HttpMessageHandler
	{
		private readonly Func<RpcInvocation, CancellationToken, Task<string>> _responseFactory;

		public ElementsRpcHandler(Func<RpcInvocation, string> responseFactory)
			: this((invocation, _) => Task.FromResult(responseFactory(invocation)))
		{
		}

		public ElementsRpcHandler(Func<RpcInvocation, CancellationToken, Task<string>> responseFactory)
		{
			_responseFactory = responseFactory;
		}

		public List<string> Methods { get; } = [];
		public List<string> Ids { get; } = [];
		public List<string> Parameters { get; } = [];
		public List<CapturedRequest> Requests { get; } = [];
		public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
		public string ContentType { get; set; } = "application/json";
		public Uri? ResponseUriOverride { get; set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument document = JsonDocument.Parse(body);
			string method = document.RootElement.GetProperty("method").GetString()!;
			string id = document.RootElement.GetProperty("id").GetString()!;
			string parameters = document.RootElement.GetProperty("params").GetRawText();
			Methods.Add(method);
			Ids.Add(id);
			Parameters.Add(parameters);
			Requests.Add(new CapturedRequest(
				request.Method,
				request.Content.Headers.ContentType?.MediaType,
				body));

			if (ResponseUriOverride is { } responseUri)
			{
				request.RequestUri = responseUri;
			}
			string responseBody = await _responseFactory(new RpcInvocation(method, id, parameters), cancellationToken);

			return new HttpResponseMessage(StatusCode)
			{
				Content = new StringContent(responseBody, Encoding.UTF8, ContentType),
				RequestMessage = request,
			};
		}
	}

	private sealed class ElementsRpcHarness : IDisposable
	{
		public ElementsRpcHarness(Func<RpcInvocation, string> responseFactory)
		{
			Handler = new ElementsRpcHandler(responseFactory);
			HttpClient = CreateHttpClient(Handler);
			Client = new ElementsRpcClient(HttpClient);
		}

		public ElementsRpcHarness(Func<RpcInvocation, CancellationToken, Task<string>> responseFactory)
		{
			Handler = new ElementsRpcHandler(responseFactory);
			HttpClient = CreateHttpClient(Handler);
			Client = new ElementsRpcClient(HttpClient);
		}

		private static HttpClient CreateHttpClient(ElementsRpcHandler handler) =>
			new(handler, disposeHandler: true)
			{
				BaseAddress = new Uri("http://127.0.0.1:18884/"),
			};

		public ElementsRpcHandler Handler { get; }
		public HttpClient HttpClient { get; }
		public ElementsRpcClient Client { get; }

		public void Dispose()
		{
			Client.Dispose();
			HttpClient.Dispose();
		}
	}

	private sealed class StreamResponseHandler(Func<Stream> streamFactory) : HttpMessageHandler
	{
		public int RequestCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			var content = new StreamContent(streamFactory());
			content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = content,
				RequestMessage = request,
			});
		}
	}

	private sealed class ThrowingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromException<HttpResponseMessage>(new HttpRequestException("Fixture transport failure."));
	}

	private sealed class StallingStream : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 0;
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private sealed class TrickleStream(byte[] bytes, TimeSpan delay) : Stream
	{
		private int _position;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => bytes.Length;
		public override long Position
		{
			get => _position;
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_position == bytes.Length)
			{
				return 0;
			}

			await Task.Delay(delay, cancellationToken);
			buffer.Span[0] = bytes[_position++];
			return 1;
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private sealed record CapturedRequest(HttpMethod Method, string? ContentType, string Body);
	private sealed record RpcInvocation(string Method, string Id, string Parameters);

	[Fact]
	public async Task BindsExactExpectationAndFeeInsideUnchangedGenerationAsync()
	{
		using var harness = new ElementsRpcHarness(ExpectationBoundValidResult);

		ElementsExpectationBoundNodeObservation observation =
			await harness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation(),
				PeggedAsset,
				CancellationToken.None);

		Assert.Equal(ValidExpectation(), observation.Expectation);
		Assert.Equal(PeggedAsset, observation.EffectiveFeeAsset);
		Assert.Equal("elementsregtest", observation.NodeStatus.Chain);
		Assert.Equal(ExpectationStartupId, observation.Generation.StartupId);
		Assert.Equal(9UL, observation.Generation.ChainstateRevision);
		Assert.Equal(42, observation.Generation.Blocks);
		Assert.Equal(BestBlockHash, observation.Generation.BestBlockHash);
		Assert.Equal(
			ElementsNodeExpectationBindingLevel.SelfReportedExactTupleAndFeeObservationOnly,
			observation.BindingLevel);
		Assert.True(observation.HasExactGenerationFenceObservation);
		Assert.True(observation.HasEffectiveFeeAssetObservation);
		Assert.False(observation.HasArtifactSourceAttestation);
		Assert.False(observation.HasRuntimeQualification);
		Assert.False(observation.HasCurrentnessAuthority);
		Assert.False(observation.HasReservationAuthority);
		Assert.False(observation.HasBroadcastAuthority);
		Assert.Equal(
			[
				"getnodegeneration",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
				"getnodegeneration",
				"getnodegeneration",
				"getsidechaininfo",
				"getnodegeneration",
			],
			harness.Handler.Methods);
		Assert.All(harness.Handler.Parameters, parameters =>
			Assert.True(parameters == "[]" || parameters == "[42]" || parameters == "[0]"));
	}

	[Fact]
	public async Task RejectsGenerationOrStatusDriftBeforeExpectationAndFeeMismatchAsync()
	{
		using var generationHarness = new ElementsRpcHarness(ExpectationBoundGenerationDriftResult);
		ElementsRpcException? generationException = null;
		try
		{
			await generationHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation() with { Chain = "liquidv1" },
				ExpectationOtherAsset,
				CancellationToken.None);
		}
		catch (ElementsRpcException exception)
		{
			generationException = exception;
		}

		Assert.NotNull(generationException);
		Assert.Equal(ElementsRpcFailureKind.Protocol, generationException.FailureKind);
		Assert.Equal(
			"Elements RPC 'expectation-bound node observation' returned an invalid result: node generation changed during the observation.",
			generationException.Message);
		Assert.DoesNotContain(ExpectationStartupId, generationException.Message, StringComparison.Ordinal);
		Assert.Equal(10, generationHarness.Handler.Methods.Count);

		using var beforeFeeHarness = new ElementsRpcHarness(ExpectationBoundBeforeFeeGenerationDriftResult);
		ElementsRpcException? beforeFeeException = null;
		try
		{
			await beforeFeeHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation() with { Chain = "liquidv1" },
				ExpectationOtherAsset,
				CancellationToken.None);
		}
		catch (ElementsRpcException exception)
		{
			beforeFeeException = exception;
		}

		Assert.NotNull(beforeFeeException);
		Assert.Equal(ElementsRpcFailureKind.Protocol, beforeFeeException.FailureKind);
		Assert.Equal(
			"Elements RPC 'expectation-bound node observation' returned an invalid result: node generation changed during the observation.",
			beforeFeeException.Message);
		Assert.DoesNotContain(ExpectationStartupId, beforeFeeException.Message, StringComparison.Ordinal);
		Assert.Equal(10, beforeFeeHarness.Handler.Methods.Count);

		using var afterFeeHarness = new ElementsRpcHarness(ExpectationBoundAfterFeeGenerationDriftResult);
		ElementsRpcException? afterFeeException = null;
		try
		{
			await afterFeeHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation() with { Chain = "liquidv1" },
				ExpectationOtherAsset,
				CancellationToken.None);
		}
		catch (ElementsRpcException exception)
		{
			afterFeeException = exception;
		}

		Assert.NotNull(afterFeeException);
		Assert.Equal(ElementsRpcFailureKind.Protocol, afterFeeException.FailureKind);
		Assert.Equal(
			"Elements RPC 'expectation-bound node observation' returned an invalid result: node generation changed during the observation.",
			afterFeeException.Message);
		Assert.DoesNotContain(ExpectationStartupId, afterFeeException.Message, StringComparison.Ordinal);
		Assert.Equal(10, afterFeeHarness.Handler.Methods.Count);

		using var statusHarness = new ElementsRpcHarness(ExpectationBoundStatusDriftResult);
		ElementsRpcException? statusException = null;
		try
		{
			await statusHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation() with { Chain = "liquidv1" },
				ExpectationOtherAsset,
				CancellationToken.None);
		}
		catch (ElementsRpcException exception)
		{
			statusException = exception;
		}

		Assert.NotNull(statusException);
		Assert.Equal(ElementsRpcFailureKind.Protocol, statusException.FailureKind);
		Assert.Equal(
			"Elements RPC 'expectation-bound node observation' returned an invalid result: node status did not match the generation fence.",
			statusException.Message);
		Assert.DoesNotContain(BestBlockHash, statusException.Message, StringComparison.Ordinal);
		Assert.Equal(10, statusHarness.Handler.Methods.Count);

		using var statusTipHarness = new ElementsRpcHarness(ExpectationBoundStatusTipDriftResult);
		ElementsRpcException? statusTipException = null;
		try
		{
			await statusTipHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation() with { Chain = "liquidv1" },
				ExpectationOtherAsset,
				CancellationToken.None);
		}
		catch (ElementsRpcException exception)
		{
			statusTipException = exception;
		}

		Assert.NotNull(statusTipException);
		Assert.Equal(ElementsRpcFailureKind.Protocol, statusTipException.FailureKind);
		Assert.Equal(
			"Elements RPC 'expectation-bound node observation' returned an invalid result: node status did not match the generation fence.",
			statusTipException.Message);
		Assert.DoesNotContain(BestBlockHash, statusTipException.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(ExpectationOtherBestBlockHash, statusTipException.Message, StringComparison.Ordinal);
		Assert.Equal(10, statusTipHarness.Handler.Methods.Count);
	}

	[Fact]
	public async Task RejectsIdentityOrFeeMismatchOnlyAfterStableFenceAsync()
	{
		using var identityHarness = new ElementsRpcHarness(ExpectationBoundValidResult);
		ElementsNodeMismatchException? identityException = null;
		try
		{
			await identityHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation() with { Chain = "liquidv1" },
				ExpectationOtherAsset,
				CancellationToken.None);
		}
		catch (ElementsNodeMismatchException exception)
		{
			identityException = exception;
		}

		Assert.NotNull(identityException);
		Assert.Equal(["chain"], identityException.MismatchedFields);
		Assert.DoesNotContain(PeggedAsset, identityException.Message, StringComparison.Ordinal);
		Assert.Equal(10, identityHarness.Handler.Methods.Count);

		using var feeHarness = new ElementsRpcHarness(ExpectationBoundValidResult);
		ElementsNodeMismatchException? feeException = null;
		try
		{
			await feeHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation(),
				ExpectationOtherAsset,
				CancellationToken.None);
		}
		catch (ElementsNodeMismatchException exception)
		{
			feeException = exception;
		}

		Assert.NotNull(feeException);
		Assert.Equal(["fee_asset"], feeException.MismatchedFields);
		Assert.DoesNotContain(ExpectationOtherAsset, feeException.Message, StringComparison.Ordinal);
		Assert.Equal(10, feeHarness.Handler.Methods.Count);

		using var peggedHarness = new ElementsRpcHarness(ExpectationBoundPeggedAssetDriftResult);
		ElementsNodeMismatchException? peggedException = null;
		try
		{
			await peggedHarness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation(),
				PeggedAsset,
				CancellationToken.None);
		}
		catch (ElementsNodeMismatchException exception)
		{
			peggedException = exception;
		}

		Assert.NotNull(peggedException);
		Assert.Equal(["pegged_asset"], peggedException.MismatchedFields);
		Assert.DoesNotContain(ExpectationOtherAsset, peggedException.Message, StringComparison.Ordinal);
		Assert.Equal(10, peggedHarness.Handler.Methods.Count);
	}

	[Fact]
	public async Task ValidatesExpectationBoundInputsBeforeTransportAsync()
	{
		using var harness = new ElementsRpcHarness(ExpectationBoundValidResult);
		ArgumentException? expectationException = null;
		try
		{
			await harness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation() with { Chain = "INVALID" },
				PeggedAsset,
				CancellationToken.None);
		}
		catch (ArgumentException exception)
		{
			expectationException = exception;
		}

		Assert.NotNull(expectationException);
		Assert.Empty(harness.Handler.Methods);

		ArgumentException? feeException = null;
		try
		{
			await harness.Client.GetExpectationBoundNodeObservationAsync(
				ValidExpectation(),
				ExpectationOtherAsset.ToUpperInvariant(),
				CancellationToken.None);
		}
		catch (ArgumentException exception)
		{
			feeException = exception;
		}

		Assert.NotNull(feeException);
		Assert.Empty(harness.Handler.Methods);

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				[],
				CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				Enumerable.Range(0, 101)
					.Select(index => new ElementsRawTransactionRequest(index.ToString("x64"), null))
					.ToArray(),
				CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(
			() => harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				[new ElementsRawTransactionRequest(new string('0', 64), null)],
				CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(
			() => harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				[new ElementsRawTransactionRequest(RawTransactionIdOne, new string('0', 64))],
				CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(
			() => harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				[
					new ElementsRawTransactionRequest(RawTransactionIdOne, null),
					new ElementsRawTransactionRequest(RawTransactionIdOne, BestBlockHash),
				],
				CancellationToken.None));
		Assert.Empty(harness.Handler.Methods);
	}

	[Fact]
	public async Task FetchesExpectationBoundRawTransactionsInsideExactFenceAsync()
	{
		using var harness = new ElementsRpcHarness(ExpectationBoundRawTransactionResult);
		ElementsRawTransactionRequest[] requests =
		[
			new(RawTransactionIdOne, BestBlockHash),
			new(RawTransactionIdTwo, null),
		];

		ElementsExpectationBoundRawTransactionBatch batch =
			await harness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				requests,
				CancellationToken.None);

		Assert.Equal(2, batch.TransactionCount);
		Assert.Equal(
			ElementsRawTransactionBindingLevel.SelfReportedExactTupleFeeAndGenerationFencedRawBytesOnly,
			batch.BindingLevel);
		Assert.Equal(9UL, batch.NodeObservation.Generation.ChainstateRevision);
		Assert.True(batch.HasExactGenerationFenceObservation);
		Assert.True(batch.HasEffectiveFeeAssetObservation);
		Assert.False(batch.HasTransactionIdValidation);
		Assert.False(batch.HasBlockMembershipAuthority);
		Assert.False(batch.HasArtifactSourceAttestation);
		Assert.False(batch.HasRuntimeQualification);
		Assert.False(batch.HasCurrentnessAuthority);
		Assert.False(batch.HasReservationAuthority);
		Assert.False(batch.HasBroadcastAuthority);

		IReadOnlyList<ElementsRawTransactionObservation> transactions = batch.GetTransactions();
		Assert.Equal(requests, transactions.Select(transaction => transaction.Request));
		Assert.Equal(nameof(ElementsRawTransactionRequest), requests[0].ToString());
		Assert.Equal(nameof(ElementsRawTransactionObservation), transactions[0].ToString());
		Assert.Equal(nameof(ElementsExpectationBoundRawTransactionBatch), batch.ToString());
		Assert.DoesNotContain(RawTransactionIdOne, requests[0].ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain(BestBlockHash, requests[0].ToString(), StringComparison.Ordinal);
		Assert.Equal([0x01, 0x02, 0x03], transactions[0].GetTransactionBytes());
		Assert.Equal(65_537, transactions[1].TransactionByteLength);
		byte[] largeTransaction = transactions[1].GetTransactionBytes();
		Assert.All(largeTransaction, value => Assert.Equal(0xaa, value));
		largeTransaction[0] = 0;
		Assert.Equal(0xaa, transactions[1].GetTransactionBytes()[0]);
		Assert.NotSame(transactions, batch.GetTransactions());

		Assert.Equal(
			[
				"getnodegeneration",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
				"getnodegeneration",
				"getnodegeneration",
				"getsidechaininfo",
				"getnodegeneration",
				"getrawtransaction",
				"getrawtransaction",
				"getnodegeneration",
			],
			harness.Handler.Methods);
		Assert.Equal(
			$"[\"{RawTransactionIdOne}\",false,\"{BestBlockHash}\"]",
			harness.Handler.Parameters[10]);
		Assert.Equal(
			$"[\"{RawTransactionIdTwo}\",false]",
			harness.Handler.Parameters[11]);
	}

	[Fact]
	public async Task RejectsMalformedOrDriftingRawTransactionsWithoutPartialAuthorityAsync()
	{
		using (var driftHarness = new ElementsRpcHarness(ExpectationBoundRawTransactionDriftResult))
		{
			ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(
				() => driftHarness.Client.GetExpectationBoundRawTransactionsAsync(
					ValidExpectation(),
					PeggedAsset,
					[new ElementsRawTransactionRequest(RawTransactionIdOne, BestBlockHash)],
					CancellationToken.None));

			Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
			Assert.Equal(
				"Elements RPC 'expectation-bound raw transaction batch' returned an invalid result: node generation changed during raw transaction acquisition.",
				exception.Message);
			Assert.DoesNotContain(RawTransactionIdOne, exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(BestBlockHash, exception.Message, StringComparison.Ordinal);
			Assert.Equal(12, driftHarness.Handler.Methods.Count);
		}

		using (var startupHarness = new ElementsRpcHarness(ExpectationBoundRawTransactionStartupDriftResult))
		{
			ElementsRpcException? exception = null;
			try
			{
				await startupHarness.Client.GetExpectationBoundRawTransactionsAsync(
					ValidExpectation(),
					PeggedAsset,
					[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
					CancellationToken.None);
			}
			catch (ElementsRpcException candidate)
			{
				exception = candidate;
			}

			Assert.NotNull(exception);
			Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
			Assert.Contains("node generation changed", exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(ExpectationOtherStartupId, exception.Message, StringComparison.Ordinal);
			Assert.Equal(12, startupHarness.Handler.Methods.Count);
		}

		using (var tipHarness = new ElementsRpcHarness(ExpectationBoundRawTransactionTipDriftResult))
		{
			ElementsRpcException? exception = null;
			try
			{
				await tipHarness.Client.GetExpectationBoundRawTransactionsAsync(
					ValidExpectation(),
					PeggedAsset,
					[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
					CancellationToken.None);
			}
			catch (ElementsRpcException candidate)
			{
				exception = candidate;
			}

			Assert.NotNull(exception);
			Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
			Assert.Contains("node generation changed", exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(ExpectationOtherBestBlockHash, exception.Message, StringComparison.Ordinal);
			Assert.Equal(12, tipHarness.Handler.Methods.Count);
		}

		using (var escapedHarness = new ElementsRpcHarness(ExpectationBoundEscapedRawTransactionResult))
		{
			ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(
				() => escapedHarness.Client.GetExpectationBoundRawTransactionsAsync(
					ValidExpectation(),
					PeggedAsset,
					[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
					CancellationToken.None));

			Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
			Assert.Contains("canonical raw transaction", exception.Message, StringComparison.Ordinal);
			Assert.Equal(11, escapedHarness.Handler.Methods.Count);
		}

		using (var upperHarness = new ElementsRpcHarness(ExpectationBoundUpperRawTransactionResult))
		{
			ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(
				() => upperHarness.Client.GetExpectationBoundRawTransactionsAsync(
					ValidExpectation(),
					PeggedAsset,
					[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
					CancellationToken.None));

			Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
			Assert.Contains("canonical raw transaction", exception.Message, StringComparison.Ordinal);
			Assert.Equal(11, upperHarness.Handler.Methods.Count);
		}

		using (var oddHarness = new ElementsRpcHarness(ExpectationBoundOddRawTransactionResult))
		{
			ElementsRpcException? exception = null;
			try
			{
				await oddHarness.Client.GetExpectationBoundRawTransactionsAsync(
					ValidExpectation(),
					PeggedAsset,
					[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
					CancellationToken.None);
			}
			catch (ElementsRpcException candidate)
			{
				exception = candidate;
			}

			Assert.NotNull(exception);
			Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
			Assert.Contains("canonical raw transaction", exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("010", exception.Message, StringComparison.Ordinal);
			Assert.Equal(11, oddHarness.Handler.Methods.Count);
		}

		using var oversizedHarness = new ElementsRpcHarness(ExpectationBoundOversizedRawTransactionResult);
		ElementsRpcException oversizedException = await Assert.ThrowsAsync<ElementsRpcException>(
			() => oversizedHarness.Client.GetExpectationBoundRawTransactionsAsync(
				ValidExpectation(),
				PeggedAsset,
				[new ElementsRawTransactionRequest(RawTransactionIdOne, null)],
				CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, oversizedException.FailureKind);
		Assert.Contains("JSON string limit", oversizedException.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(RawTransactionIdOne, oversizedException.Message, StringComparison.Ordinal);
		Assert.Equal(11, oversizedHarness.Handler.Methods.Count);
	}

	private static ElementsNodeExpectation ValidExpectation() =>
		new(
			Chain: "elementsregtest",
			GenesisBlockHash,
			FedpegScript: "51",
			PeggedAsset,
			ParentGenesisBlockHash: ParentGenesis,
			PeginConfirmationDepth: 8,
			EnforcePak: false,
			Version: 230303,
			ProtocolVersion: 70016,
			Subversion: "/Elements Core:23.3.3/");

	private static string ExpectationBoundValidResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnodegeneration" => Envelope(invocation.Id, GenerationResult(ExpectationStartupId, 9, 42, BestBlockHash)),
		"getsidechaininfo" when invocation.Id == "9" =>
			Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset)),
		_ => ValidResult(invocation),
	};

	private static string ExpectationBoundGenerationDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnodegeneration" when invocation.Id == "7" =>
			Envelope(invocation.Id, GenerationResult(ExpectationStartupId, 10, 42, BestBlockHash)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundBeforeFeeGenerationDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnodegeneration" when invocation.Id == "8" =>
			Envelope(invocation.Id, GenerationResult(ExpectationStartupId, 8, 42, BestBlockHash)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundAfterFeeGenerationDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnodegeneration" when invocation.Id == "10" =>
			Envelope(invocation.Id, GenerationResult(ExpectationStartupId, 10, 42, BestBlockHash)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundStatusDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult(blocks: 41, headers: 41)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundStatusTipDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getblockchaininfo" =>
			Envelope(invocation.Id, BlockchainResult(bestBlockHash: ExpectationOtherBestBlockHash)),
		"getblockhash" when invocation.Parameters == "[42]" =>
			Envelope(invocation.Id, JsonSerializer.Serialize(ExpectationOtherBestBlockHash)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundPeggedAssetDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getsidechaininfo" when invocation.Id == "9" =>
			Envelope(invocation.Id, FeeAssetResult(ExpectationOtherAsset, PeggedAsset)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundRawTransactionResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" when invocation.Parameters.Contains(RawTransactionIdOne, StringComparison.Ordinal) =>
			Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
		"getrawtransaction" when invocation.Parameters.Contains(RawTransactionIdTwo, StringComparison.Ordinal) =>
			Envelope(invocation.Id, JsonSerializer.Serialize(new string('a', 65_537 * 2))),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundRawTransactionDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
		"getnodegeneration" when invocation.Id == "12" =>
			Envelope(invocation.Id, GenerationResult(ExpectationStartupId, 10, 42, BestBlockHash)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundRawTransactionStartupDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
		"getnodegeneration" when invocation.Id == "12" =>
			Envelope(invocation.Id, GenerationResult(ExpectationOtherStartupId, 9, 42, BestBlockHash)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundRawTransactionTipDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
		"getnodegeneration" when invocation.Id == "12" =>
			Envelope(invocation.Id, GenerationResult(ExpectationStartupId, 9, 43, ExpectationOtherBestBlockHash)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundEscapedRawTransactionResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" => Envelope(invocation.Id, "\"\\u0030\\u0031\""),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundUpperRawTransactionResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("AA")),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundOddRawTransactionResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010")),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundOversizedRawTransactionResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize(new string('a', 4_194_305 * 2))),
		_ => ExpectationBoundValidResult(invocation),
	};

	private const string ExpectationStartupId = "abababababababababababababababababababababababababababababababab";
	private const string ExpectationOtherStartupId = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";
	private const string ExpectationOtherAsset = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	private const string ExpectationOtherBestBlockHash = "0202020202020202020202020202020202020202020202020202020202020202";
	private const string RawTransactionIdOne = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string RawTransactionIdTwo = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string RawTransactionIdThree = "3333333333333333333333333333333333333333333333333333333333333333";
	private const string PlanPublicKeyHex =
		"0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string PlanScriptHex = "00140102030405060708090a0b0c0d0e0f1011121314";

	[Fact]
	public async Task EncodesOneExpectationBoundPlanFromCanonicalAcquiredTransactionsAsync()
	{
		using var harness = new ElementsRpcHarness(ExpectationBoundPlanCompositionFactory());
		LiquidOrdinaryWalletExactSpendPlan plan = CreateExpectationBoundPlan();
		byte[] sourceEpoch = PlanSourceEpoch();
		(
			ElementsExpectationBoundNodeObservation nodeObservation,
			LiquidOrdinaryWalletPlanEncodedFrame frame) =
			await harness.Client.EncodeExpectationBoundOrdinaryWalletPlanAsync(
				LiquidTestnetExpectation(),
				ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
				sourceEpoch,
				plan,
				[[RawTransactionIdTwo]],
				CancellationToken.None);
		using (frame)
		{
			Assert.Equal("liquidtestnet", nodeObservation.Expectation!.Chain);
			Assert.Equal(
				"0000000000000000000000000000000000000000000000000000000000000000",
				nodeObservation.Generation.StartupId);
			Assert.Equal(0UL, nodeObservation.Generation.ChainstateRevision);
			Assert.True(nodeObservation.HasExactGenerationFenceObservation);
			Assert.True(nodeObservation.HasEffectiveFeeAssetObservation);
			Assert.False(nodeObservation.HasArtifactSourceAttestation);
			Assert.False(nodeObservation.HasRuntimeQualification);
			Assert.False(nodeObservation.HasCurrentnessAuthority);
			Assert.False(nodeObservation.HasReservationAuthority);
			Assert.False(nodeObservation.HasBroadcastAuthority);

			var expectedRawTransactions = new ElementsExpectationBoundRawTransactionBatch(
				nodeObservation,
				[
					new ElementsRawTransactionObservation(
						new ElementsRawTransactionRequest(RawTransactionIdOne, BestBlockHash),
						[0x01, 0x02, 0x03]),
					new ElementsRawTransactionObservation(
						new ElementsRawTransactionRequest(RawTransactionIdTwo, null),
						[0x04, 0x05]),
				]);
			bool fundingAccepted = expectedRawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
				plan,
				[[RawTransactionIdTwo]],
				out LiquidOrdinaryWalletPlanFundingBatch? expectedFundingBatch,
				out LiquidOrdinaryWalletPlanWireErrorCode fundingErrorCode);
			Assert.True(fundingAccepted, fundingErrorCode.ToString());
			Assert.NotNull(expectedFundingBatch);
			using (expectedFundingBatch)
			{
				bool encodingAccepted = LiquidOrdinaryWalletPlanEncoder.TryEncode(
					sourceEpoch,
					plan,
					expectedFundingBatch,
					out LiquidOrdinaryWalletPlanEncodedFrame? expectedFrame,
					out LiquidOrdinaryWalletPlanWireErrorCode encodingErrorCode);
				Assert.True(encodingAccepted, encodingErrorCode.ToString());
				Assert.NotNull(expectedFrame);
				using (expectedFrame)
				{
					byte[] actualBytes = new byte[frame.Length];
					byte[] expectedBytes = new byte[expectedFrame.Length];
					try
					{
						frame.CopyFrameTo(actualBytes);
						expectedFrame.CopyFrameTo(expectedBytes);
						Assert.Equal(expectedBytes, actualBytes);
						Assert.Equal(sourceEpoch, actualBytes[24..56]);
					}
					finally
					{
						CryptographicOperations.ZeroMemory(actualBytes);
						CryptographicOperations.ZeroMemory(expectedBytes);
					}
				}
			}
		}
		CryptographicOperations.ZeroMemory(sourceEpoch);

		Assert.Equal(
			[
				"getblockchaininfo",
				"getblockhash",
				"getnetworkinfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockhash",
				"getsidechaininfo",
				"getblockchaininfo",
				"getblockhash",
				"getblockchaininfo",
				"getblockhash",
				"getsidechaininfo",
				"getblockchaininfo",
				"getblockhash",
				"getrawtransaction",
				"getrawtransaction",
				"getblockchaininfo",
				"getblockhash",
			],
			harness.Handler.Methods);
		Assert.Equal(
			$"[\"{RawTransactionIdOne}\",false,\"{BestBlockHash}\"]",
			harness.Handler.Parameters[14]);
		Assert.Equal(
			$"[\"{RawTransactionIdTwo}\",false]",
			harness.Handler.Parameters[15]);
	}

	[Fact]
	public async Task RejectsInvalidPlanCompositionBeforeRpcAndInvalidFundingWithoutPartialFrameAsync()
	{
		LiquidOrdinaryWalletExactSpendPlan plan = CreateExpectationBoundPlan();
		using (var contextHarness = new ElementsRpcHarness(ExpectationBoundPlanCompositionFactory()))
		{
			await AssertPlanEncodingArgumentRejectedAsync(
				contextHarness,
				LiquidTestnetExpectation() with { Chain = "elementsregtest" },
				PlanSourceEpoch(),
				plan,
				[[RawTransactionIdTwo]]);
		}
		using (var epochHarness = new ElementsRpcHarness(ExpectationBoundPlanCompositionFactory()))
		{
			await AssertPlanEncodingArgumentRejectedAsync(
				epochHarness,
				LiquidTestnetExpectation(),
				new byte[32],
				plan,
				[[RawTransactionIdTwo]]);
		}
		using (var mappingHarness = new ElementsRpcHarness(ExpectationBoundPlanCompositionFactory()))
		{
			await AssertPlanEncodingArgumentRejectedAsync(
				mappingHarness,
				LiquidTestnetExpectation(),
				PlanSourceEpoch(),
				plan,
				[[new string('b', 64), new string('a', 64)]]);
		}

		using var invalidFundingHarness =
			new ElementsRpcHarness(ExpectationBoundDuplicatePlanFundingFactory());
		ElementsRpcException? rejection = null;
		try
		{
			(
				ElementsExpectationBoundNodeObservation _,
				LiquidOrdinaryWalletPlanEncodedFrame rejectedFrame) =
				await invalidFundingHarness.Client.EncodeExpectationBoundOrdinaryWalletPlanAsync(
					LiquidTestnetExpectation(),
					ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
					PlanSourceEpoch(),
					plan,
					[[RawTransactionIdTwo, RawTransactionIdThree]],
					CancellationToken.None);
			rejectedFrame.Dispose();
		}
		catch (ElementsRpcException exception)
		{
			rejection = exception;
		}

		Assert.NotNull(rejection);
		Assert.Equal(ElementsRpcFailureKind.Protocol, rejection.FailureKind);
		Assert.Equal(
			"Elements RPC 'expectation-bound ordinary-wallet plan frame' returned an invalid result: ordinary wallet plan wire encoding is invalid.",
			rejection.Message);
		Assert.DoesNotContain(RawTransactionIdOne, rejection.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(RawTransactionIdTwo, rejection.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(RawTransactionIdThree, rejection.Message, StringComparison.Ordinal);
		Assert.Equal(19, invalidFundingHarness.Handler.Methods.Count);
	}

	private static async Task AssertPlanEncodingArgumentRejectedAsync(
		ElementsRpcHarness harness,
		ElementsNodeExpectation expectation,
		byte[] sourceEpoch,
		LiquidOrdinaryWalletExactSpendPlan plan,
		IReadOnlyList<IReadOnlyList<string>?> previousTransactionIdsBySelectedInput)
	{
		ArgumentException? rejection = null;
		try
		{
			(
				ElementsExpectationBoundNodeObservation _,
				LiquidOrdinaryWalletPlanEncodedFrame rejectedFrame) =
				await harness.Client.EncodeExpectationBoundOrdinaryWalletPlanAsync(
					expectation,
					ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
					sourceEpoch,
					plan,
					previousTransactionIdsBySelectedInput,
					CancellationToken.None);
			rejectedFrame.Dispose();
		}
		catch (ArgumentException exception)
		{
			rejection = exception;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceEpoch);
		}

		Assert.NotNull(rejection);
		Assert.Empty(harness.Handler.Methods);
	}

	private static LiquidOrdinaryWalletExactSpendPlan CreateExpectationBoundPlan()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId);
		LiquidTransactionId transactionId = LiquidTransactionId.ParseRpcHex(RawTransactionIdOne);
		LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString(PlanPublicKeyHex),
			LiquidKeyBranch.External,
			0);
		LiquidOwnedOutput output = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, 0),
			spendKey.GetScriptPubKey(),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 10),
			spendKey);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset).Apply(
			0,
			LiquidWalletTransactionDelta.Create(transactionId, [], [output]));
		state = state.Confirm(
			state.Revision,
			transactionId,
			LiquidConfirmation.Create(BestBlockHash, 42));
		LiquidAddress address = LiquidAddress.FromScriptPubKey(
			manifest,
			Convert.FromHexString(PlanScriptHex),
			LiquidBlindingPublicKey.Create(Convert.FromHexString(PlanPublicKeyHex)));
		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(
				manifest,
				address,
				peggedAsset,
				LiquidAssetAmount.Create(peggedAsset, peggedAsset, 9),
				LiquidWalletLabelSet.Create(["rpc-plan"]));
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[output.OutPoint],
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 1));
	}

	private static ElementsNodeExpectation LiquidTestnetExpectation()
	{
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidTestnet;
		return new ElementsNodeExpectation(
			manifest.ChainRpcName,
			manifest.GenesisBlockHash,
			"51",
			manifest.PeggedAssetId,
			manifest.ParentGenesisHash,
			0,
			false,
			manifest.ElementsNumericVersion,
			manifest.ElementsProtocolVersion,
			manifest.ExpectedSubversion);
	}

	private static byte[] PlanSourceEpoch() =>
	[
		1, 2, 3, 4, 5, 6, 7, 8,
		9, 10, 11, 12, 13, 14, 15, 16,
		17, 18, 19, 20, 21, 22, 23, 24,
		25, 26, 27, 28, 29, 30, 31, 32,
	];

	private static Func<RpcInvocation, string> ExpectationBoundPlanCompositionFactory()
	{
		int sidechainCalls = 0;
		return invocation =>
		{
			int sidechainCallIndex = invocation.Method == "getsidechaininfo" ? sidechainCalls++ : -1;
			return ExpectationBoundPlanCompositionResultCore(invocation, sidechainCallIndex);
		};
	}

	private static string ExpectationBoundPlanCompositionResultCore(RpcInvocation invocation, int sidechainCallIndex) =>
		invocation.Method switch
		{
			"getnodegeneration" => Envelope(
				invocation.Id,
				GenerationResult(ExpectationStartupId, 9, 42, BestBlockHash)),
			"getsidechaininfo" when sidechainCallIndex > 0 => Envelope(
				invocation.Id,
				FeeAssetResult(
					ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId,
					ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId)),
			"getrawtransaction" when invocation.Parameters.Contains(
				RawTransactionIdOne,
				StringComparison.Ordinal) => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
			"getrawtransaction" when invocation.Parameters.Contains(
				RawTransactionIdTwo,
				StringComparison.Ordinal) => Envelope(invocation.Id, JsonSerializer.Serialize("0405")),
			_ => LiquidTestnetResult(invocation),
		};

	private static Func<RpcInvocation, string> ExpectationBoundDuplicatePlanFundingFactory()
	{
		Func<RpcInvocation, string> composition = ExpectationBoundPlanCompositionFactory();
		return invocation => invocation.Method == "getrawtransaction"
			? Envelope(invocation.Id, JsonSerializer.Serialize("0102"))
			: composition(invocation);
	}
}