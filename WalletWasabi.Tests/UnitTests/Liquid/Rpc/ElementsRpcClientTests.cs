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
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnetworkinfo" => Envelope(invocation.Id, NetworkResult(warnings: "network warning")),
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult(warnings: "chain warning")),
			_ => ValidResult(invocation),
		});

		ElementsNodeStatus status = await harness.Client.GetNodeStatusAsync(CancellationToken.None);

		Assert.True(status.NetworkWarningsPresent);
		Assert.True(status.BlockchainWarningsPresent);
		Assert.False(status.HasClearWarningObservation);
		Assert.True(status.HasCompleteArchiveObservation);
	}

	[Fact]
	public async Task RejectsMissingWarningsBeforeFollowingCallsAsync()
	{
		string missingWarnings = "{\"version\":230303,\"protocolversion\":70016,\"subversion\":\"/Elements Core:23.3.3/\",\"localrelay\":true,\"networkactive\":true}";
		using var harness = new ElementsRpcHarness(invocation => Envelope(invocation.Id, missingWarnings));

		var exception = await Assert.ThrowsAsync<ElementsRpcException>(
			() => harness.Client.GetNodeStatusAsync(CancellationToken.None));

		Assert.Contains("warnings", exception.Message, StringComparison.Ordinal);
		Assert.Single(harness.Handler.Methods);
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

	private static string BlockchainResult(
		int blocks = 42,
		int headers = 42,
		bool initialBlockDownload = false,
		string bestBlockHash = BestBlockHash,
		string warnings = "",
		string chain = "elementsregtest") =>
		$$"""{"chain":"{{chain}}","blocks":{{blocks}},"headers":{{headers}},"bestblockhash":"{{bestBlockHash}}","initialblockdownload":{{initialBlockDownload.ToString().ToLowerInvariant()}},"pruned":false,"trim_headers":false,"warnings":{{JsonSerializer.Serialize(warnings)}}}""";

	private static string SidechainResult(
		string parentGenesis = ParentGenesis,
		string peggedAsset = PeggedAsset,
		int peginConfirmationDepth = 8) =>
		$$"""{"fedpegscript":"51","pegged_asset":"{{peggedAsset}}","parent_blockhash":"{{parentGenesis}}","pegin_confirmation_depth":{{peginConfirmationDepth}},"enforce_pak":false}""";

	private static string NetworkResult(string warnings = "") =>
		$$"""{"version":230303,"protocolversion":70016,"subversion":"/Elements Core:23.3.3/","localrelay":true,"networkactive":true,"warnings":{{JsonSerializer.Serialize(warnings)}}}""";

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

	private sealed class ElementsRpcHandler(Func<RpcInvocation, string> responseFactory) : HttpMessageHandler
	{
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

			return new HttpResponseMessage(StatusCode)
			{
				Content = new StringContent(responseFactory(new RpcInvocation(method, id, parameters)), Encoding.UTF8, ContentType),
				RequestMessage = request,
			};
		}
	}

	private sealed class ElementsRpcHarness : IDisposable
	{
		public ElementsRpcHarness(Func<RpcInvocation, string> responseFactory)
		{
			Handler = new ElementsRpcHandler(responseFactory);
			HttpClient = new HttpClient(Handler, disposeHandler: true)
			{
				BaseAddress = new Uri("http://127.0.0.1:18884/"),
			};
			Client = new ElementsRpcClient(HttpClient);
		}

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
}
