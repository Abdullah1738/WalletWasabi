using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using Xunit;

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
		int sidechainCalls = 0;
		using var harness = new ElementsRpcHarness(async (invocation, cancellationToken) =>
		{
			if (invocation.Method == "getnodegeneration")
			{
				return Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash));
			}
			if (invocation.Method == "getsidechaininfo" && sidechainCalls++ == 0)
			{
				middleCallEntered.TrySetResult();
				await releaseMiddleCall.Task.WaitAsync(cancellationToken);
				return Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset));
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
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CancellationDuringBracketReleasesProbeLockAsync(bool cancelClosingGeneration)
	{
		const string StartupId = "abababababababababababababababababababababababababababababababab";
		var blockedCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int generationCalls = 0;
		int sidechainCalls = 0;
		using var harness = new ElementsRpcHarness(async (invocation, cancellationToken) =>
		{
			if (invocation.Method == "getnodegeneration")
			{
				generationCalls++;
				if (cancelClosingGeneration && generationCalls == 2)
				{
					blockedCallEntered.TrySetResult();
					await neverCompletes.Task.WaitAsync(cancellationToken);
				}

				return Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash));
			}
			if (invocation.Method == "getsidechaininfo" && sidechainCalls++ == 0)
			{
				if (!cancelClosingGeneration)
				{
					blockedCallEntered.TrySetResult();
					await neverCompletes.Task.WaitAsync(cancellationToken);
				}

				return Envelope(invocation.Id, FeeAssetResult(PeggedAsset, PeggedAsset));
			}

			return ValidResult(invocation);
		});
		using var cancellation = new CancellationTokenSource();

		Task<ElementsFeeAssetGenerationObservation> observationTask =
			harness.Client.GetFeeAssetGenerationObservationAsync(cancellation.Token);
		await blockedCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observationTask);
		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("elementsregtest", status.Chain);
		Assert.Equal(cancelClosingGeneration ? 8 : 7, harness.Handler.Methods.Count);
		Assert.Equal("getnetworkinfo", harness.Handler.Methods[cancelClosingGeneration ? 3 : 2]);
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
			TotalRequestTimeout: TimeSpan.FromSeconds(2),
			ResponseIdleTimeout: TimeSpan.FromSeconds(1)));
		using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

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
		Assert.Equal(
			"Elements RPC 'expectation-bound node observation' returned an invalid result: node status did not match the generation fence.",
			statusException.Message);
		Assert.DoesNotContain(BestBlockHash, statusException.Message, StringComparison.Ordinal);
		Assert.Equal(10, statusHarness.Handler.Methods.Count);
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

	private static string ExpectationBoundStatusDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult(blocks: 41, headers: 41)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private static string ExpectationBoundPeggedAssetDriftResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getsidechaininfo" when invocation.Id == "9" =>
			Envelope(invocation.Id, FeeAssetResult(ExpectationOtherAsset, PeggedAsset)),
		_ => ExpectationBoundValidResult(invocation),
	};

	private const string ExpectationStartupId = "abababababababababababababababababababababababababababababababab";
	private const string ExpectationOtherAsset = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
}
