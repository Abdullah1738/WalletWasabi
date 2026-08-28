using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Rpc;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Rpc;

public class ElementsWalletRefreshObservationTests
{
	private const string BestBlockHash = "0101010101010101010101010101010101010101010101010101010101010101";
	private const string GenesisBlockHash = "cd179c84c35f51825f20a3b91a18d45f0c53b5ceb744a5b6ef8f0babe809396f";
	private const string PeggedAsset = "b2e15d0d7a0c94e4e2ce0fe6e8691b9e451377f6e46e8045a86f7c4b5d4f0f23";
	private const string ParentGenesis = "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206";
	private const string StartupId = "abababababababababababababababababababababababababababababababab";

	private static readonly string SuppliedAcceptedId = Id(0xA0);
	private static readonly string OtherAcceptedId = Id(0xA1);
	private static readonly string MempoolLowId = Id(0x10);
	private static readonly string MempoolHighId = Id(0x70);
	private static readonly string SharedId = Id(0x55);
	private static readonly string BlockTipFirstId = Id(0x61);
	private static readonly string BlockTipSecondId = Id(0x62);
	private static readonly string BlockOlderId = Id(0x63);
	private static readonly string BlockHashBest = Id(0xAF);
	private static readonly string BlockHashTip = Id(0xB0);
	private static readonly string BlockHashPrevious = Id(0xB1);

	[Fact]
	public async Task BindsNormalizedExpectationAndRejectsMismatchBeforeDiscoveryWithoutRetryAsync()
	{
		ElementsNodeExpectation expectation = ValidExpectation();
		using var matchingHarness = new ElementsRpcHarness(RefreshOrderingResult);

		using ElementsWalletRefreshObservation observation = await matchingHarness.Client.GetWalletRefreshObservationAsync(
			expectation,
			PeggedAsset,
			[OtherAcceptedId, SuppliedAcceptedId],
			SuppliedAcceptedId,
			CancellationToken.None);

		Assert.Equal(expectation.Normalize(), observation.NodeObservation.Expectation);

		using var mismatchingHarness = new ElementsRpcHarness(RefreshCapBaseResult);
		ElementsNodeExpectation mismatch = expectation with { Chain = "liquidtestnet" };
		await Assert.ThrowsAsync<ElementsNodeMismatchException>(() => mismatchingHarness.Client.GetWalletRefreshObservationAsync(
			mismatch,
			PeggedAsset,
			[OtherAcceptedId, SuppliedAcceptedId],
			SuppliedAcceptedId,
			CancellationToken.None));

		Assert.Equal(1, mismatchingHarness.Handler.Methods.Count(method => method == "getnetworkinfo"));
		Assert.DoesNotContain(mismatchingHarness.Handler.Methods, method => method == "getrawmempool");
		Assert.DoesNotContain(mismatchingHarness.Handler.Parameters, IsRawFetch);
	}

	[Fact]
	public async Task AcceptedIdForcedFirstOrdinalDedupSortedMempoolRecentBlockOrderAndCapAsync()
	{
		// Unsorted mempool (high then low), SharedId present in both mempool and the tip block to
		// prove ordinal dedupe keeps the earliest (mempool) position. Recent blocks are processed
		// newest-first preserving node transaction order within a block.
		using var harness = new ElementsRpcHarness(RefreshOrderingResult);

		ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[OtherAcceptedId, SuppliedAcceptedId, SharedId],
			SuppliedAcceptedId,
			CancellationToken.None);

		Assert.Equal(
			[
				SuppliedAcceptedId,
				OtherAcceptedId,
				SharedId,
				MempoolLowId,
				MempoolHighId,
				BlockTipFirstId,
				BlockTipSecondId,
				BlockOlderId,
			],
			observation.Candidates.Select(candidate => candidate.TransactionId).ToArray());

		// The recent-block metadata binds to the shared and block-origin candidates.
		Assert.Equal(BlockHashTip, observation.Candidates[2].BlockHash);
		Assert.Equal(41u, observation.Candidates[2].BlockHeight);
		Assert.Null(observation.Candidates[0].BlockHash);
		Assert.Null(observation.Candidates[3].BlockHash);

		Assert.Equal(PeggedAsset, observation.NodeObservation.EffectiveFeeAsset);
		Assert.Equal("elementsregtest", observation.NodeObservation.NodeStatus.Chain);
		Assert.Equal(StartupId, observation.NodeObservation.Generation.StartupId);
		Assert.True(observation.NodeObservation.HasExactGenerationFenceObservation);
		Assert.False(observation.HasTransactionIdValidation);
		Assert.False(observation.HasBlockMembershipAuthority);
		Assert.False(observation.HasCurrentnessAuthority);
	}

	[Fact]
	public async Task SelectedCandidateCountIsCappedAtSixtyFourAsync()
	{
		// 3 accepted + 4 mempool + 60 block = 67 distinct, so selection must stop at 64.
		using var harness = new ElementsRpcHarness(RefreshCapResult);

		ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[Id(0x01), Id(0x02), Id(0x03)],
			null,
			CancellationToken.None);

		Assert.Equal(64, observation.Candidates.Count);
		Assert.Equal(Id(0x01), observation.Candidates[0].TransactionId);
		Assert.Equal(64, observation.RawTransactions.Count);
		Assert.Equal(128, harness.Handler.Methods.Count(m => m == "getrawtransaction"));
	}

	[Fact]
	public async Task PreservesCompleteInputsDeduplicatesDependenciesAndFetchesEachRawOnceAsync()
	{
		string candidateId = Id(0xC0);
		string previousA = Id(0xC1);
		string previousB = Id(0xC2);
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getrawmempool" => Envelope(invocation.Id, "[]"),
			"getblock" => Envelope(invocation.Id, BlockResult()),
			"getrawtransaction" when IsVerbose(invocation) => Envelope(
				invocation.Id,
				$$"""{"txid":"{{candidateId}}","vin":[{"txid":"{{previousA}}"},{"txid":"{{previousA}}"},{"coinbase":"00"},{"txid":"{{previousB}}"}]}"""),
			_ => RefreshCapBaseResult(invocation),
		});

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[candidateId],
			null,
			CancellationToken.None);

		ElementsWalletRefreshCandidate candidate = Assert.Single(observation.Candidates);
		Assert.Equal(4, candidate.Inputs.Count);
		Assert.Equal(
			new string?[] { previousA, previousA, null, previousB },
			candidate.Inputs.Select(input => input.PreviousTransactionId).ToArray());
		Assert.Equal([false, false, true, false], candidate.Inputs.Select(input => input.IsCoinbase).ToArray());
		Assert.Equal([previousA, previousB], candidate.PreviousTransactionIds);
		Assert.Equal([candidateId, previousA, previousB], observation.RawTransactions.Select(raw => raw.TransactionId).ToArray());
		Assert.Equal(1, RawFetchCount(harness, candidateId));
		Assert.Equal(1, RawFetchCount(harness, previousA));
		Assert.Equal(1, RawFetchCount(harness, previousB));
	}

	[Fact]
	public async Task CandidatePlusDependencyCapFailsBeforeFirstRawFetchAsync()
	{
		string[] candidateIds = Enumerable.Range(1, 64).Select(Id).ToArray();
		string[] dependencyIds = Enumerable.Range(0x80, 37).Select(Id).ToArray();
		using var harness = new ElementsRpcHarness(invocation =>
		{
			if (invocation.Method == "getrawmempool")
			{
				return Envelope(invocation.Id, "[]");
			}
			if (invocation.Method == "getrawtransaction" && IsVerbose(invocation))
			{
				string requestedId = ExtractRequestedTransactionId(invocation.Parameters);
				int candidateIndex = System.Array.IndexOf(candidateIds, requestedId);
				string vin = candidateIndex < dependencyIds.Length
					? $$"""[{"txid":"{{dependencyIds[candidateIndex]}}"}]"""
					: "[]";
				return Envelope(invocation.Id, $$"""{"txid":"{{requestedId}}","vin":{{vin}}}""");
			}
			return RefreshCapBaseResult(invocation);
		});

		await Assert.ThrowsAsync<ElementsRpcException>(() => harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			candidateIds,
			null,
			CancellationToken.None));

		Assert.Equal(64, harness.Handler.Methods.Count(method => method == "getrawtransaction"));
		Assert.DoesNotContain(
			harness.Handler.Parameters,
			parameters => IsRawFetch(parameters));
	}


	[Fact]
	public async Task RejectsArrayRootForTypedBlockResultAsync()
	{
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getrawmempool" => Envelope(invocation.Id, "[]"),
			"getblock" => Envelope(invocation.Id, "[]"),
			_ => RefreshCapBaseResult(invocation),
		});

		await Assert.ThrowsAsync<ElementsRpcException>(() => harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[],
			null,
			CancellationToken.None));

		Assert.DoesNotContain(harness.Handler.Methods, method => method == "getrawtransaction");
	}

	[Fact]
	public void RawTransactionReturnsControlledCopyAndAggregateDisposeZerosEveryBackingBuffer()
	{
		byte[] source1 = [1, 2, 3];
		byte[] source2 = [4, 5, 6];
		using var raw1 = new ElementsWalletRefreshRawTransaction(Id(0xE0), source1);
		using var raw2 = new ElementsWalletRefreshRawTransaction(Id(0xE1), source2);
		source1[0] = 99;
		Assert.Equal(1, raw1.GetTransactionBytes()[0]);
		byte[] returned = raw1.GetTransactionBytes();
		returned[1] = 99;
		Assert.Equal(2, raw1.GetTransactionBytes()[1]);

		var status = new ElementsNodeStatus(
			"elementsregtest", 0, 0, BestBlockHash, BestBlockHash, false, false, false, false,
			true, true, false, "51", PeggedAsset, ParentGenesis, 8, false, 230303, 70016, "/Elements/");
		var generation = new ElementsNodeGenerationObservation(StartupId, 9, 0, BestBlockHash);
		var nodeObservation = new ElementsExpectationBoundNodeObservation(PeggedAsset, status, generation);
		var candidate = new ElementsWalletRefreshCandidate(Id(0xE0), null, null, [], []);
		using var observation = new ElementsWalletRefreshObservation(nodeObservation, [candidate], [raw1, raw2]);
		FieldInfo backingField = typeof(ElementsWalletRefreshRawTransaction).GetField(
			"_transactionBytes", BindingFlags.Instance | BindingFlags.NonPublic)!;
		byte[] backing1 = (byte[])backingField.GetValue(raw1)!;
		byte[] backing2 = (byte[])backingField.GetValue(raw2)!;

		observation.Dispose();

		Assert.All(backing1, value => Assert.Equal(0, value));
		Assert.All(backing2, value => Assert.Equal(0, value));
		Assert.Throws<ObjectDisposedException>(() => raw1.GetTransactionBytes());
		Assert.Throws<ObjectDisposedException>(() => raw2.GetTransactionBytes());
	}

	[Theory]
	[InlineData("coinbase+txid")]
	[InlineData("neither")]
	[InlineData("requested/response txid mismatch")]
	[InlineData("discovered/verbose blockhash conflict")]
	public async Task MalformedVerboseRowsFailBeforeAnyRawFetchAsync(string malformedRow)
	{
		string candidateId = Id(0xE2);
		string otherId = Id(0xE3);
		string verbose = malformedRow switch
		{
			"coinbase+txid" => $$"""{"txid":"{{candidateId}}","vin":[{"coinbase":"00","txid":"{{otherId}}"}]}""",
			"neither" => $$"""{"txid":"{{candidateId}}","vin":[{}]}""",
			"requested/response txid mismatch" => $$"""{"txid":"{{otherId}}","vin":[]}""",
			"discovered/verbose blockhash conflict" => $$"""{"txid":"{{candidateId}}","blockhash":"{{otherId}}","vin":[]}""",
			_ => throw new InvalidOperationException(malformedRow),
		};
		using var harness = new ElementsRpcHarness(invocation =>
		{
			if (invocation.Method == "getrawmempool")
			{
				return Envelope(invocation.Id, "[]");
			}
			if (invocation.Method == "getblock" && malformedRow == "discovered/verbose blockhash conflict")
			{
				return Envelope(invocation.Id, BlockResult(candidateId));
			}
			if (invocation.Method == "getrawtransaction" && IsVerbose(invocation))
			{
				return Envelope(invocation.Id, verbose);
			}
			return RefreshCapBaseResult(invocation);
		});

		await Assert.ThrowsAsync<ElementsRpcException>(() => harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset, [candidateId], null, CancellationToken.None));
		Assert.DoesNotContain(harness.Handler.Parameters, IsRawFetch);
	}
	[Fact]
	public async Task ExactGenerationDriftFailsAtMempoolAndFinalStatusWithoutRetryAsync()
	{
		await AssertDriftFailsAsync(changeAfterMethod: "getrawmempool", expectedNetworkCalls: 1);
		await AssertDriftFailsAsync(changeAfterMethod: "final-status", expectedNetworkCalls: 2);
	}

	[Fact]
	public async Task ProbeLockExcludesCompetingProbeForCompleteRefreshAcquisitionAsync()
	{
		var firstRpcEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstRpc = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int generationCalls = 0;
		using var harness = new ElementsRpcHarness(async (invocation, cancellationToken) =>
		{
			if (invocation.Method == "getnodegeneration" && generationCalls++ == 0)
			{
				firstRpcEntered.TrySetResult();
				await releaseFirstRpc.Task.WaitAsync(cancellationToken);
			}
			return ValidRefreshResult(invocation, Id(0xD0), Id(0xD1));
		});

		Task<ElementsWalletRefreshObservation> refreshTask = harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[Id(0xD0)],
			null,
			CancellationToken.None);
		await firstRpcEntered.Task.WaitAsync(System.TimeSpan.FromSeconds(5));
		Task<ElementsNodeStatus> competingProbe = harness.Client.GetNodeStatusAsync(CancellationToken.None);
		try
		{
			await Task.Delay(50);
			Assert.Single(harness.Handler.Methods);
			Assert.False(competingProbe.IsCompleted);
		}
		finally
		{
			releaseFirstRpc.TrySetResult();
		}

		using ElementsWalletRefreshObservation observation = await refreshTask.WaitAsync(System.TimeSpan.FromSeconds(5));
		await competingProbe.WaitAsync(System.TimeSpan.FromSeconds(5));
	}

	private static async Task AssertDriftFailsAsync(string changeAfterMethod, int expectedNetworkCalls)
	{
		string candidateId = Id(0xD0);
		string dependencyId = Id(0xD1);
		bool drift = false;
		int sidechainCalls = 0;
		using var harness = new ElementsRpcHarness(invocation =>
		{
			if (changeAfterMethod == "final-status"
				&& invocation.Method == "getsidechaininfo"
				&& ++sidechainCalls == 3)
			{
				drift = true;
			}
			string response = invocation.Method == "getnodegeneration" && drift
				? Envelope(invocation.Id, GenerationResult(StartupId, 10, 42, BestBlockHash))
				: ValidRefreshResult(invocation, candidateId, dependencyId);
			if (StringComparer.Ordinal.Equals(invocation.Method, changeAfterMethod))
			{
				drift = true;
			}
			return response;
		});

		await Assert.ThrowsAsync<ElementsRpcException>(() => harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[candidateId],
			null,
			CancellationToken.None));

		Assert.Equal(1, harness.Handler.Methods.Count(method => method == "getrawmempool"));
		Assert.Equal(expectedNetworkCalls, harness.Handler.Methods.Count(method => method == "getnetworkinfo"));
		if (changeAfterMethod == "getrawmempool")
		{
			Assert.DoesNotContain(harness.Handler.Methods, method => method == "getblock");
			Assert.DoesNotContain(harness.Handler.Parameters, IsRawFetch);
		}
		else
		{
			Assert.Equal(1, RawFetchCount(harness, candidateId));
			Assert.Equal(1, RawFetchCount(harness, dependencyId));
		}
	}

	private static int RawFetchCount(ElementsRpcHarness harness, string transactionId) =>
		harness.Handler.Parameters.Count(parameters =>
			IsRawFetch(parameters) && StringComparer.Ordinal.Equals(ExtractRequestedTransactionId(parameters), transactionId));

	private static bool IsRawFetch(string parameters)
	{
		using JsonDocument document = JsonDocument.Parse(parameters);
		return document.RootElement.GetArrayLength() >= 2 && document.RootElement[1].ValueKind == JsonValueKind.False;
	}

	private static string BlockResult(params string[] transactionIds) =>
		$"{{\"tx\":[{string.Join(',', transactionIds.Select(static id => JsonSerializer.Serialize(id)))}]}}";

	private static string Id(int byteValue) => new string(byteValue.ToString("x2")[0], 62) + byteValue.ToString("x2");

	private static string RefreshOrderingResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnodegeneration" => Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash)),
		"getnetworkinfo" => Envelope(invocation.Id, NetworkResult()),
		"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult()),
		"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
		"getblockhash" when invocation.Parameters == "[0]" => Envelope(invocation.Id, JsonSerializer.Serialize(GenesisBlockHash)),
		"getblockhash" when invocation.Parameters == "[42]" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
		"getblockhash" when invocation.Parameters == "[41]" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashTip)),
		"getblockhash" when invocation.Parameters == "[40]" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashPrevious)),
		"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashPrevious)),
		"getrawmempool" => Envelope(
			invocation.Id,
			$"[{JsonSerializer.Serialize(MempoolHighId)},{JsonSerializer.Serialize(SharedId)},{JsonSerializer.Serialize(MempoolLowId)}]"),
		"getblock" when invocation.Parameters.Contains(BestBlockHash, System.StringComparison.Ordinal) => Envelope(invocation.Id, BlockResult()),
		"getblock" when invocation.Parameters.Contains(BlockHashTip, System.StringComparison.Ordinal) => Envelope(
			invocation.Id,
			BlockResult(BlockTipFirstId, SharedId, BlockTipSecondId)),
		"getblock" when invocation.Parameters.Contains(BlockHashPrevious, System.StringComparison.Ordinal) => Envelope(
			invocation.Id,
			BlockResult(BlockOlderId)),
		"getblock" => Envelope(invocation.Id, BlockResult()),
		"getrawtransaction" when IsVerbose(invocation) => Envelope(invocation.Id, VerboseTransactionResult(invocation)),
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
		_ => throw new System.InvalidOperationException($"Unexpected RPC method '{invocation.Method}' with parameters '{invocation.Parameters}'."),
	};


	private static string RefreshCapResult(RpcInvocation invocation)
	{
		if (invocation.Method == "getrawmempool")
		{
			return Envelope(
				invocation.Id,
				$"[{string.Join(',', Enumerable.Range(0x10, 4).Select(i => JsonSerializer.Serialize(Id(i))))}]");
		}
		if (invocation.Method == "getblock" && invocation.Parameters.Contains(BlockHashTip, System.StringComparison.Ordinal))
		{
			return Envelope(
				invocation.Id,
				BlockResult(Enumerable.Range(0x20, 60).Select(Id).ToArray()));
		}
		if (invocation.Method == "getblock" && invocation.Parameters.Contains(BlockHashPrevious, System.StringComparison.Ordinal))
		{
			return Envelope(invocation.Id, BlockResult());
		}
		return RefreshCapBaseResult(invocation);
	}

	private static string ValidRefreshResult(RpcInvocation invocation, string candidateId, string dependencyId) =>
		invocation.Method == "getrawtransaction" && IsVerbose(invocation)
			? Envelope(invocation.Id, $$"""{"txid":"{{ExtractRequestedTransactionId(invocation.Parameters)}}","vin":[{"txid":"{{dependencyId}}"}]}""")
			: invocation.Method == "getrawmempool"
				? Envelope(invocation.Id, "[]")
				: RefreshCapBaseResult(invocation);
	private static string RefreshCapBaseResult(RpcInvocation invocation) => invocation.Method switch
	{
		"getnodegeneration" => Envelope(invocation.Id, GenerationResult(StartupId, 9, 42, BestBlockHash)),
		"getnetworkinfo" => Envelope(invocation.Id, NetworkResult()),
		"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult()),
		"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
		"getblockhash" when invocation.Parameters == "[0]" => Envelope(invocation.Id, JsonSerializer.Serialize(GenesisBlockHash)),
		"getblockhash" when invocation.Parameters == "[42]" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
		"getblockhash" when invocation.Parameters == "[41]" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashTip)),
		"getblockhash" when invocation.Parameters == "[40]" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashPrevious)),
		"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashPrevious)),
		"getblock" => Envelope(invocation.Id, BlockResult()),
		"getrawtransaction" when IsVerbose(invocation) => Envelope(invocation.Id, VerboseTransactionResult(invocation)),
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
		_ => throw new System.InvalidOperationException($"Unexpected RPC method '{invocation.Method}' with parameters '{invocation.Parameters}'."),
	};

	private static bool IsVerbose(RpcInvocation invocation) =>
		invocation.Parameters.Contains("true", System.StringComparison.Ordinal);

	private static string VerboseTransactionResult(RpcInvocation invocation)
	{
		string requestedId = ExtractRequestedTransactionId(invocation.Parameters);
		return $"{{\"txid\":\"{requestedId}\",\"vin\":[]}}";
	}

	private static string ExtractRequestedTransactionId(string parameters)
	{
		using JsonDocument document = JsonDocument.Parse(parameters);
		return document.RootElement[0].GetString()!;
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

	private static string NetworkResult() =>
		"""{"version":230303,"protocolversion":70016,"subversion":"/Elements Core:23.3.3/","localrelay":true,"networkactive":true,"warnings":""}""";

	private static string BlockchainResult() =>
		$$"""{"chain":"elementsregtest","blocks":42,"headers":42,"bestblockhash":"{{BestBlockHash}}","initialblockdownload":false,"pruned":false,"trim_headers":false,"warnings":""}""";

	private static string SidechainResult() =>
		$$"""{"fedpegscript":"51","pegged_asset":"{{PeggedAsset}}","parent_blockhash":"{{ParentGenesis}}","pegin_confirmation_depth":8,"enforce_pak":false,"fee_asset":"{{PeggedAsset}}"}""";

	private static string GenerationResult(string startupId, ulong revision, int blocks, string bestBlockHash) =>
		$$"""{"startup_id":"{{startupId}}","chainstate_revision":{{revision}},"blocks":{{blocks}},"bestblockhash":"{{bestBlockHash}}"}""";

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
		public List<string> Parameters { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument document = JsonDocument.Parse(body);
			string method = document.RootElement.GetProperty("method").GetString()!;
			string id = document.RootElement.GetProperty("id").GetString()!;
			string parameters = document.RootElement.GetProperty("params").GetRawText();
			Methods.Add(method);
			Parameters.Add(parameters);
			string responseBody = await _responseFactory(new RpcInvocation(method, id, parameters), cancellationToken);
			return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
				RequestMessage = request,
			};
		}
	}

	private sealed class ElementsRpcHarness : System.IDisposable
	{
		public ElementsRpcHarness(Func<RpcInvocation, string> responseFactory)
		{
			Handler = new ElementsRpcHandler(responseFactory);
			HttpClient = new HttpClient(Handler, disposeHandler: true)
			{
				BaseAddress = new System.Uri("http://127.0.0.1:18884/"),
			};
			Client = new ElementsRpcClient(HttpClient);
		}

		public ElementsRpcHarness(Func<RpcInvocation, CancellationToken, Task<string>> responseFactory)
		{
			Handler = new ElementsRpcHandler(responseFactory);
			HttpClient = new HttpClient(Handler, disposeHandler: true)
			{
				BaseAddress = new System.Uri("http://127.0.0.1:18884/"),
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

	private sealed record RpcInvocation(string Method, string Id, string Parameters);
}
