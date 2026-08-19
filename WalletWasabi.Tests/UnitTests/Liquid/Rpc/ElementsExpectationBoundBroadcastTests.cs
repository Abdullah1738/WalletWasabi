using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Rpc;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Rpc;

[Collection("Serial unit tests collection")]
public class ElementsExpectationBoundBroadcastTests
{
	private const string BestBlockHash = "0101010101010101010101010101010101010101010101010101010101010101";
	private const string GenesisBlockHash = "cd179c84c35f51825f20a3b91a18d45f0c53b5ceb744a5b6ef8f0babe809396f";
	private const string PeggedAsset = "b2e15d0d7a0c94e4e2ce0fe6e8691b9e451377f6e46e8045a86f7c4b5d4f0f23";
	private const string ParentGenesis = "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206";
	private const string StartupId = "abababababababababababababababababababababababababababababababab";
	private const string SignedTransactionHex = "01020304";
	private const string AcceptedTransactionId = "2222222222222222222222222222222222222222222222222222222222222222";

	[Fact]
	public async Task SubmitsOnceInsideExactFenceAndReturnsNarrowReceiptAsync()
	{
		using var harness = new BroadcastHarness(ValidBroadcastResult);

		ElementsExpectationBoundBroadcastReceipt receipt =
			await harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(),
				PeggedAsset,
				SignedTransactionHex,
				CancellationToken.None);

		Assert.Equal(AcceptedTransactionId, receipt.AcceptedTransactionIdHex);
		Assert.Equal(ValidExpectation(), receipt.NodeObservation.Expectation);
		Assert.Equal(PeggedAsset, receipt.NodeObservation.EffectiveFeeAsset);
		Assert.Equal(
			ElementsBroadcastBindingLevel.SelfReportedExactTupleFeeAndGenerationFencedNodeAcceptanceOnly,
			receipt.BindingLevel);
		Assert.True(receipt.HasBroadcastAuthority);
		Assert.True(receipt.HasExactGenerationFenceObservation);
		Assert.True(receipt.HasEffectiveFeeAssetObservation);
		Assert.False(receipt.HasConfirmationAuthority);
		Assert.False(receipt.HasCurrentnessAuthority);
		Assert.False(receipt.HasReservationAuthority);
		Assert.False(receipt.HasArtifactSourceAttestation);
		Assert.False(receipt.HasRuntimeQualification);
		Assert.False(receipt.HasTransactionIdValidation);
		Assert.Equal(nameof(ElementsExpectationBoundBroadcastReceipt), receipt.ToString());
		Assert.DoesNotContain(AcceptedTransactionId, receipt.ToString(), StringComparison.Ordinal);
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
				"sendrawtransaction",
				"getnodegeneration",
			],
			harness.Handler.Methods);
		Assert.Equal($"[\"{SignedTransactionHex}\"]", harness.Handler.Parameters[10]);
		Assert.Equal("11", harness.Handler.Ids[10]);
		Assert.Single(harness.Handler.Methods, method => method == "sendrawtransaction");
	}

	[Theory]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("AA")]
	[InlineData("gg")]
	public async Task RejectsInvalidTransactionHexBeforeAnyNodeContactAsync(string invalidHex)
	{
		using var harness = new BroadcastHarness(ValidBroadcastResult);

		await Assert.ThrowsAsync<ArgumentException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, invalidHex, CancellationToken.None));

		Assert.Empty(harness.Handler.Methods);
	}

	[Fact]
	public async Task RejectsNullTransactionHexBeforeAnyNodeContactAsync()
	{
		using var harness = new BroadcastHarness(ValidBroadcastResult);

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, null!, CancellationToken.None));

		Assert.Empty(harness.Handler.Methods);
	}

	[Theory]
	[InlineData(-26)]
	[InlineData(-27)]
	[InlineData(-25)]
	public async Task SurfacesNodeRejectionWithoutRetryOrReceiptAsync(int rpcCode)
	{
		using var harness = new BroadcastHarness(invocation => invocation.Method == "sendrawtransaction"
			? $"{{\"result\":null,\"error\":{{\"code\":{rpcCode},\"message\":\"rejected\"}},\"id\":\"{invocation.Id}\"}}"
			: ValidBroadcastResult(invocation));
		harness.Handler.StatusCodeSelector = invocation =>
			invocation.Method == "sendrawtransaction" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, SignedTransactionHex, CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Rpc, exception.FailureKind);
		Assert.Equal(rpcCode, exception.RpcCode);
		Assert.Single(harness.Handler.Methods, method => method == "sendrawtransaction");
		Assert.Equal("sendrawtransaction", harness.Handler.Methods[^1]);
	}

	[Fact]
	public async Task SurfacesTransportFailureWithoutRetryAsync()
	{
		using var handler = new ThrowOnBroadcastHandler();
		using var httpClient = CreateHttpClient(handler);
		using var client = new ElementsRpcClient(httpClient);

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() =>
			client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, SignedTransactionHex, CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Transport, exception.FailureKind);
	}

	[Fact]
	public async Task SurfacesBroadcastTimeoutWithoutRetryAsync()
	{
		using var harness = new BroadcastHarness(async (invocation, cancellationToken) =>
		{
			if (invocation.Method == "sendrawtransaction")
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}
			return ValidBroadcastResult(invocation);
		}, new ElementsRpcTimeouts(
			TimeSpan.FromMilliseconds(50),
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(50)));

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, SignedTransactionHex, CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Timeout, exception.FailureKind);
		Assert.Single(harness.Handler.Methods, method => method == "sendrawtransaction");
	}

	[Fact]
	public async Task RejectsExpectationMismatchBeforeSubmittingAsync()
	{
		using var harness = new BroadcastHarness(ValidBroadcastResult);

		ElementsNodeMismatchException exception = await Assert.ThrowsAsync<ElementsNodeMismatchException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation() with { Chain = "liquidv1" },
				PeggedAsset,
				SignedTransactionHex,
				CancellationToken.None));

		Assert.Equal(["chain"], exception.MismatchedFields);
		Assert.DoesNotContain("sendrawtransaction", harness.Handler.Methods);
	}

	[Fact]
	public async Task FailsClosedAfterSubmitWhenGenerationChangesAsync()
	{
		int generationCalls = 0;
		using var harness = new BroadcastHarness(invocation => invocation.Method == "getnodegeneration"
			? Envelope(
				invocation.Id,
				GenerationResult(
					StartupId,
					generationCalls++ < 4 ? 9UL : 10UL,
					42,
					BestBlockHash))
			: ValidBroadcastResult(invocation));

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, SignedTransactionHex, CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Contains("node generation changed", exception.Message, StringComparison.Ordinal);
		Assert.Single(harness.Handler.Methods, method => method == "sendrawtransaction");
		Assert.Equal("getnodegeneration", harness.Handler.Methods[^1]);
	}

	[Theory]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[InlineData("11")]
	[InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
	[InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
	public async Task RejectsMalformedAcceptanceWithoutReceiptAsync(string invalidResult)
	{
		using var harness = new BroadcastHarness(invocation => invocation.Method == "sendrawtransaction"
			? Envelope(invocation.Id, JsonSerializer.Serialize(invalidResult))
			: ValidBroadcastResult(invocation));

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, SignedTransactionHex, CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Single(harness.Handler.Methods, method => method == "sendrawtransaction");
	}

	[Fact]
	public void BroadcastAuthorityExistsOnlyOnReceiptType()
	{
		PropertyInfo nodeAuthority = typeof(ElementsExpectationBoundNodeObservation)
			.GetProperty(nameof(ElementsExpectationBoundNodeObservation.HasBroadcastAuthority))!;
		PropertyInfo batchAuthority = typeof(ElementsExpectationBoundRawTransactionBatch)
			.GetProperty(nameof(ElementsExpectationBoundRawTransactionBatch.HasBroadcastAuthority))!;
		PropertyInfo receiptAuthority = typeof(ElementsExpectationBoundBroadcastReceipt)
			.GetProperty(nameof(ElementsExpectationBoundBroadcastReceipt.HasBroadcastAuthority))!;
		Assert.Equal(false, nodeAuthority.GetMethod!.Invoke(
			new ElementsExpectationBoundNodeObservation(
				ValidExpectation(), PeggedAsset, ValidStatus(), StableGeneration()), null));
		Assert.Equal(false, batchAuthority.GetMethod!.Invoke(
			new ElementsExpectationBoundRawTransactionBatch(
				new ElementsExpectationBoundNodeObservation(
					ValidExpectation(), PeggedAsset, ValidStatus(), StableGeneration()), []), null));
		Assert.Equal(true, receiptAuthority.GetMethod!.Invoke(
			new ElementsExpectationBoundBroadcastReceipt(
				new ElementsExpectationBoundNodeObservation(
					ValidExpectation(), PeggedAsset, ValidStatus(), StableGeneration()),
				AcceptedTransactionId), null));

		Type[] authorityFlagTypes = typeof(ElementsRpcClient).Assembly.GetTypes()
			.Where(type => type.Namespace == typeof(ElementsRpcClient).Namespace)
			.Where(type => type.GetProperty(
				"HasBroadcastAuthority",
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null)
			.OrderBy(type => type.FullName, StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(
			[
				typeof(ElementsExpectationBoundBroadcastReceipt),
				typeof(ElementsExpectationBoundNodeObservation),
				typeof(ElementsExpectationBoundRawTransactionBatch),
			],
			authorityFlagTypes);
	}

	[Theory]
	[InlineData("non-string")]
	[InlineData("non-json")]
	[InlineData("oversized")]
	[InlineData("id-mismatch")]
	public async Task RejectsProtocolFailureResponsesWithoutRetryAsync(string scenario)
	{
		using var harness = new BroadcastHarness(invocation =>
		{
			if (invocation.Method != "sendrawtransaction")
			{
				return ValidBroadcastResult(invocation);
			}

			return scenario switch
			{
				"non-string" => Envelope(invocation.Id, "42"),
				"non-json" => "not-json",
				"oversized" => Envelope(invocation.Id, JsonSerializer.Serialize(new string('a', 1024 * 1024))),
				"id-mismatch" => Envelope("mismatched-id", JsonSerializer.Serialize(AcceptedTransactionId)),
				_ => throw new InvalidOperationException(),
			};
		});

		ElementsRpcException exception = await Assert.ThrowsAsync<ElementsRpcException>(() =>
			harness.Client.BroadcastExpectationBoundRawTransactionAsync(
				ValidExpectation(), PeggedAsset, SignedTransactionHex, CancellationToken.None));

		Assert.Equal(ElementsRpcFailureKind.Protocol, exception.FailureKind);
		Assert.Single(harness.Handler.Methods, method => method == "sendrawtransaction");
		Assert.Equal("sendrawtransaction", harness.Handler.Methods[^1]);
	}

	private static string ValidBroadcastResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnodegeneration" => Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash)),
		"getnetworkinfo" => Envelope(invocation.Id, NetworkResult()),
		"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult()),
		"getblockhash" when invocation.Parameters == "[0]" => Envelope(invocation.Id, JsonSerializer.Serialize(GenesisBlockHash)),
		"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
		"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
		"sendrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize(AcceptedTransactionId)),
		_ => throw new InvalidOperationException($"Unexpected RPC method '{invocation.Method}'."),
	};

	private static ElementsNodeExpectation ValidExpectation() => new(
		"elementsregtest", GenesisBlockHash, "51", PeggedAsset, ParentGenesis, 8,
		false, 230303, 70016, "/Elements Core:23.3.3/");

	private static ElementsNodeStatus ValidStatus() => new(
		"elementsregtest", 42, 42, BestBlockHash, GenesisBlockHash, false, false, false,
		false, true, true, false, "51", PeggedAsset, ParentGenesis, 8, false,
		230303, 70016, "/Elements Core:23.3.3/");

	private static ElementsNodeGenerationObservation StableGeneration() =>
		new(StartupId, 9, 42, BestBlockHash);

	private static string NetworkResult() =>
		"{\"version\":230303,\"protocolversion\":70016,\"subversion\":\"/Elements Core:23.3.3/\",\"localrelay\":true,\"networkactive\":true,\"warnings\":\"\"}";

	private static string BlockchainResult() =>
		$$"""{"chain":"elementsregtest","blocks":42,"headers":42,"bestblockhash":"{{BestBlockHash}}","initialblockdownload":false,"pruned":false,"trim_headers":false,"warnings":""}""";

	private static string SidechainResult() =>
		$$"""{"fedpegscript":"51","pegged_asset":"{{PeggedAsset}}","fee_asset":"{{PeggedAsset}}","parent_blockhash":"{{ParentGenesis}}","pegin_confirmation_depth":8,"enforce_pak":false}""";

	private static string GenerationResult(string startupId, ulong revision, int blocks, string bestBlockHash) =>
		$$"""{"startup_id":"{{startupId}}","chainstate_revision":{{revision}},"blocks":{{blocks}},"bestblockhash":"{{bestBlockHash}}"}""";

	private static string Envelope(string id, string result) =>
		$$"""{"result":{{result}},"error":null,"id":"{{id}}"}""";

	private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler, disposeHandler: true)
	{
		BaseAddress = new Uri("http://127.0.0.1:18884/"),
	};

	private sealed class BroadcastHarness : IDisposable
	{
		public BroadcastHarness(Func<RpcInvocation, string> responseFactory, ElementsRpcTimeouts? timeouts = null)
			: this((invocation, _) => Task.FromResult(responseFactory(invocation)), timeouts)
		{
		}

		public BroadcastHarness(
			Func<RpcInvocation, CancellationToken, Task<string>> responseFactory,
			ElementsRpcTimeouts? timeouts = null)
		{
			Handler = new BroadcastHandler(responseFactory);
			HttpClient = CreateHttpClient(Handler);
			Client = new ElementsRpcClient(HttpClient, timeouts);
		}

		public BroadcastHandler Handler { get; }
		public HttpClient HttpClient { get; }
		public ElementsRpcClient Client { get; }

		public void Dispose()
		{
			Client.Dispose();
			HttpClient.Dispose();
		}
	}

	private sealed class BroadcastHandler(
		Func<RpcInvocation, CancellationToken, Task<string>> responseFactory) : HttpMessageHandler
	{
		public List<string> Methods { get; } = [];
		public List<string> Ids { get; } = [];
		public List<string> Parameters { get; } = [];
		public Func<RpcInvocation, HttpStatusCode>? StatusCodeSelector { get; set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument document = JsonDocument.Parse(body);
			string method = document.RootElement.GetProperty("method").GetString()!;
			string id = document.RootElement.GetProperty("id").GetString()!;
			string parameters = document.RootElement.GetProperty("params").GetRawText();
			var invocation = new RpcInvocation(method, id, parameters);
			Methods.Add(method);
			Ids.Add(id);
			Parameters.Add(parameters);
			string responseBody = await responseFactory(invocation, cancellationToken);
			return new HttpResponseMessage(StatusCodeSelector?.Invoke(invocation) ?? HttpStatusCode.OK)
			{
				Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
				RequestMessage = request,
			};
		}
	}

	private sealed class ThrowOnBroadcastHandler : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument document = JsonDocument.Parse(body);
			string method = document.RootElement.GetProperty("method").GetString()!;
			string id = document.RootElement.GetProperty("id").GetString()!;
			if (method == "sendrawtransaction")
			{
				throw new HttpRequestException("Fixture broadcast failure.");
			}
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(ValidBroadcastResult(new RpcInvocation(
					method,
					id,
					document.RootElement.GetProperty("params").GetRawText())), Encoding.UTF8, "application/json"),
				RequestMessage = request,
			};
		}
	}

	private sealed record RpcInvocation(string Method, string Id, string Parameters);
}
