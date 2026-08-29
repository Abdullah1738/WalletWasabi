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
		// A mixed coinbase + non-coinbase input row is now terminal/fail-closed (see
		// CONTROLLED-REGTEST-REFRESH-COINBASE-FILTER-001), so this input-ordering and
		// dependency-dedupe coverage uses a third distinct previous id in place of the former
		// coinbase input; the repeated previousA still proves first-occurrence dedupe.
		string candidateId = Id(0xC0);
		string previousA = Id(0xC1);
		string previousB = Id(0xC2);
		string previousC = Id(0xC3);
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getrawmempool" => Envelope(invocation.Id, "[]"),
			"getblock" => Envelope(invocation.Id, BlockResult()),
			"getrawtransaction" when IsVerbose(invocation) => Envelope(
				invocation.Id,
				$$"""{"txid":"{{candidateId}}","vin":[{"txid":"{{previousA}}"},{"txid":"{{previousA}}"},{"txid":"{{previousC}}"},{"txid":"{{previousB}}"}]}"""),
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
			new string?[] { previousA, previousA, previousC, previousB },
			candidate.Inputs.Select(input => input.PreviousTransactionId).ToArray());
		Assert.Equal([false, false, false, false], candidate.Inputs.Select(input => input.IsCoinbase).ToArray());
		Assert.Equal([previousA, previousC, previousB], candidate.PreviousTransactionIds);
		Assert.Equal([candidateId, previousA, previousB, previousC], observation.RawTransactions.Select(raw => raw.TransactionId).ToArray());
		Assert.Equal(1, RawFetchCount(harness, candidateId));
		Assert.Equal(1, RawFetchCount(harness, previousA));
		Assert.Equal(1, RawFetchCount(harness, previousB));
		Assert.Equal(1, RawFetchCount(harness, previousC));
	}

	[Fact]
	public async Task SupportedCandidateDependingOnSupportedCandidateFetchesEachRawExactlyOnceAsync()
	{
		// Supported candidate B depends on supported candidate A: A is a dependency that is itself a
		// supported candidate, so it must be raw-fetched exactly once (as the candidate, in supported
		// order) and never again in the dependency loop. The observation must contain each raw ID once
		// — a duplicate would fail the native staging rawById.Add.
		string candidateA = Id(0xD5);
		string candidateB = Id(0xD6);
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getrawmempool" => Envelope(invocation.Id, "[]"),
			"getblock" => Envelope(invocation.Id, BlockResult()),
			"getrawtransaction" when IsVerbose(invocation) => Envelope(
				invocation.Id,
				ExtractRequestedTransactionId(invocation.Parameters) switch
				{
					var requested when requested == candidateB =>
						$$"""{"txid":"{{candidateB}}","vin":[{"txid":"{{candidateA}}"}]}""",
					var requested =>
						$$"""{"txid":"{{requested}}","vin":[]}""",
				}),
			_ => RefreshCapBaseResult(invocation),
		});

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[candidateA, candidateB],
			null,
			CancellationToken.None);

		Assert.Equal([candidateA, candidateB], observation.Candidates.Select(candidate => candidate.TransactionId).ToArray());
		ElementsWalletRefreshCandidate b = observation.Candidates[1];
		Assert.Equal([candidateA], b.PreviousTransactionIds);
		Assert.Equal(
			new string?[] { candidateA },
			b.Inputs.Select(input => input.PreviousTransactionId).ToArray());
		Assert.Equal(
			[candidateA, candidateB],
			observation.RawTransactions.Select(raw => raw.TransactionId).ToArray());
		Assert.Equal(2, observation.RawTransactions.Count);
		Assert.Equal(1, RawFetchCount(harness, candidateA));
		Assert.Equal(1, RawFetchCount(harness, candidateB));
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
	public async Task SkippedTipBlockCoinbaseRefillsSupportedCapInSourceOrderAsync()
	{
		// CONTROLLED-REGTEST-REFRESH-COINBASE-FILTER-001: 63 accepted supported IDs leave one open
		// supported slot. The tip block leads with a canonical generation row (exactly one coinbase
		// input, zero previous IDs) that must be skipped entirely — absent from Candidates, never
		// raw-fetched — so the following supported spend refills the 64th slot. Every supported row
		// shares one valid dependency, which must be fetched exactly once.
		string[] acceptedIds = Enumerable.Range(0x01, 63).Select(Id).ToArray();
		string coinbaseId = Id(0x80);
		string spendId = Id(0x81);
		string sharedDependencyId = Id(0xE0);
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getrawmempool" => Envelope(invocation.Id, "[]"),
			"getblock" when invocation.Parameters.Contains(BestBlockHash, System.StringComparison.Ordinal) => Envelope(
				invocation.Id,
				BlockResult(coinbaseId, spendId)),
			"getblock" => Envelope(invocation.Id, BlockResult()),
			"getrawtransaction" when IsVerbose(invocation) => Envelope(
				invocation.Id,
				ExtractRequestedTransactionId(invocation.Parameters) switch
				{
					var requested when requested == coinbaseId =>
						$$"""{"txid":"{{coinbaseId}}","vin":[{"coinbase":"00"}]}""",
					var requested =>
						$$"""{"txid":"{{requested}}","vin":[{"txid":"{{sharedDependencyId}}"}]}""",
				}),
			_ => RefreshCapBaseResult(invocation),
		});

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			acceptedIds,
			null,
			CancellationToken.None);

		Assert.Equal(64, observation.Candidates.Count);
		Assert.Equal(
			acceptedIds.Concat([spendId]).ToArray(),
			observation.Candidates.Select(candidate => candidate.TransactionId).ToArray());
		Assert.DoesNotContain(
			observation.Candidates.Select(candidate => candidate.TransactionId),
			id => StringComparer.Ordinal.Equals(id, coinbaseId));
		Assert.Equal(BestBlockHash, observation.Candidates[63].BlockHash);
		Assert.Equal(42u, observation.Candidates[63].BlockHeight);
		Assert.Equal(65, observation.RawTransactions.Count);
		Assert.Equal(0, RawFetchCount(harness, coinbaseId));
		Assert.Equal(1, RawFetchCount(harness, spendId));
		Assert.Equal(1, RawFetchCount(harness, sharedDependencyId));
	}

	[Theory]
	[InlineData("accepted", new[] { "candidate" }, null)]
	[InlineData("supplied", new string[] { }, "candidate")]
	[InlineData("mempool", new string[] { }, null)]
	public async Task AcceptedSuppliedOrMempoolCoinbaseFailsBeforeRawFetchAsync(
		string origin,
		string[] acceptedMarkers,
		string? suppliedMarker)
	{
		string coinbaseId = Id(0xC9);
		string[] acceptedIds = acceptedMarkers.Select(_ => coinbaseId).ToArray();
		string? suppliedId = suppliedMarker is null ? null : coinbaseId;
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getrawmempool" => Envelope(
				invocation.Id,
				origin == "mempool" ? $"[{JsonSerializer.Serialize(coinbaseId)}]" : "[]"),
			"getblock" => Envelope(invocation.Id, BlockResult()),
			"getrawtransaction" when IsVerbose(invocation) => Envelope(
				invocation.Id,
				$$"""{"txid":"{{coinbaseId}}","vin":[{"coinbase":"00"}]}"""),
			_ => RefreshCapBaseResult(invocation),
		});

		await Assert.ThrowsAsync<ElementsRpcException>(() => harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			acceptedIds,
			suppliedId,
			CancellationToken.None));

		Assert.DoesNotContain(harness.Handler.Parameters, IsRawFetch);
	}

	[Theory]
	[InlineData("mixed coinbase and spend inputs")]
	[InlineData("multiple coinbase inputs")]
	public async Task BlockDiscoveredNonCanonicalCoinbaseShapeFailsBeforeRawFetchAsync(string malformedShape)
	{
		string candidateId = Id(0xCA);
		string otherId = Id(0xCB);
		string vin = malformedShape == "mixed coinbase and spend inputs"
			? $$"""[{"coinbase":"00"},{"txid":"{{otherId}}"}]"""
			: """[{"coinbase":"00"},{"coinbase":"01"}]""";
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getrawmempool" => Envelope(invocation.Id, "[]"),
			"getblock" when invocation.Parameters.Contains(BestBlockHash, System.StringComparison.Ordinal) => Envelope(
				invocation.Id,
				BlockResult(candidateId)),
			"getblock" => Envelope(invocation.Id, BlockResult()),
			"getrawtransaction" when IsVerbose(invocation) => Envelope(
				invocation.Id,
				$$"""{"txid":"{{candidateId}}","vin":{{vin}}}"""),
			_ => RefreshCapBaseResult(invocation),
		});

		await Assert.ThrowsAsync<ElementsRpcException>(() => harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[],
			null,
			CancellationToken.None));

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

	[Fact]
	public async Task CompletesRefreshAgainstManifestFallbackFenceWithoutGetNodeGenerationAsync()
	{
		// The official upstream Elements 23.3.3 build lacks the fork-only getnodegeneration RPC;
		// the reviewed testnet manifest declares generation_api absent, so the refresh path must
		// complete on the getblockchaininfo fallback fence without ever calling getnodegeneration.
		string candidateId = Id(0xE0);
		using var harness = new ElementsRpcHarness(invocation => invocation.Method switch
		{
			"getnetworkinfo" => Envelope(invocation.Id, NetworkResult()),
			"getblockchaininfo" => Envelope(invocation.Id, BlockchainResult()),
			"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
			"getblockhash" when invocation.Parameters == "[0]" => Envelope(invocation.Id, JsonSerializer.Serialize(GenesisBlockHash)),
			"getblockhash" when invocation.Parameters == "[42]" => Envelope(invocation.Id, JsonSerializer.Serialize(BestBlockHash)),
			"getblockhash" when invocation.Parameters == "[41]" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashTip)),
			"getblockhash" when invocation.Parameters == "[40]" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashPrevious)),
			"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(BlockHashPrevious)),
			"getrawmempool" => Envelope(invocation.Id, "[]"),
			"getblock" => Envelope(invocation.Id, BlockResult()),
			"getrawtransaction" when IsVerbose(invocation) => Envelope(
				invocation.Id,
				$$"""{"txid":"{{candidateId}}","vin":[]}"""),
			"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
			_ => throw new System.InvalidOperationException($"Unexpected RPC method '{invocation.Method}' with parameters '{invocation.Parameters}'."),
		});

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[candidateId],
			null,
			WalletWasabi.Liquid.Network.ElementsPublicNetworkManifest.LiquidTestnet,
			CancellationToken.None);

		ElementsWalletRefreshCandidate candidate = Assert.Single(observation.Candidates);
		Assert.Equal(candidateId, candidate.TransactionId);
		Assert.Equal(PeggedAsset, observation.NodeObservation.EffectiveFeeAsset);
		Assert.Equal(
			"0000000000000000000000000000000000000000000000000000000000000000",
			observation.NodeObservation.Generation.StartupId);
		Assert.Equal(0UL, observation.NodeObservation.Generation.ChainstateRevision);
		Assert.Equal(42, observation.NodeObservation.Generation.Blocks);
		Assert.Equal(BestBlockHash, observation.NodeObservation.Generation.BestBlockHash);
		Assert.DoesNotContain(harness.Handler.Methods, method => method == "getnodegeneration");
		Assert.Equal(1, RawFetchCount(harness, candidateId));
	}

	[Fact]
	public async Task FreshWalletNullAnchorRescansFullBoundedWindowAndDiscoversDeepConfirmedPaymentAsync()
	{
		// A fresh wallet (no confirmation, null anchor) walks the whole bounded window from the tip
		// (height 100) down to the rescan floor 100 - 1440 + 1 clamped to 0, so a confirmed external
		// payment at height 20 — far below the recent six-block window (95..100) — is discovered.
		const int tip = 100;
		const int paymentHeight = 20;
		string paymentId = Id(0x77);
		string paymentBlockHash = DeepBlockHash(paymentHeight);
		using var harness = new ElementsRpcHarness(DeepRescanResult(tip, paymentHeight, paymentId));

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[],
			null,
			CancellationToken.None);

		ElementsWalletRefreshCandidate candidate = Assert.Single(observation.Candidates);
		Assert.Equal(paymentId, candidate.TransactionId);
		Assert.Equal(paymentBlockHash, candidate.BlockHash);
		Assert.Equal((uint)paymentHeight, candidate.BlockHeight);
		Assert.Equal(1, RawFetchCount(harness, paymentId));
		// The walk covered every height from the tip down to the floor (0), not just the recent window.
		Assert.Equal(tip + 1, harness.Handler.Methods.Count(method => method == "getblock"));
	}

	[Fact]
	public async Task WalletWithAnchorAboveRescanFloorScansOnlyTheGapAsync()
	{
		// The wallet already holds a confirmation at height 80 (its confirmed-history high-water),
		// below the recent six-block window (95..100). Only the gap from the tip down to the anchor
		// is walked: a confirmed payment at height 90 is discovered, and no height below the anchor
		// is scanned.
		const int tip = 100;
		const int anchor = 80;
		const int paymentHeight = 90;
		string paymentId = Id(0x78);
		string paymentBlockHash = DeepBlockHash(paymentHeight);
		using var harness = new ElementsRpcHarness(DeepRescanResult(tip, paymentHeight, paymentId));

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[],
			null,
			CancellationToken.None,
			anchor);

		ElementsWalletRefreshCandidate candidate = Assert.Single(observation.Candidates);
		Assert.Equal(paymentId, candidate.TransactionId);
		Assert.Equal(paymentBlockHash, candidate.BlockHash);
		Assert.Equal((uint)paymentHeight, candidate.BlockHeight);
		// Heights 100..80 inclusive: exactly the recent window plus the gap, nothing below the anchor.
		Assert.Equal(tip - anchor + 1, harness.Handler.Methods.Count(method => method == "getblock"));
	}

	[Fact]
	public async Task GapDeeperThanRescanDepthFailsClosedWithoutHangingAsync()
	{
		// The wallet's confirmed-history high-water (height 10) sits deeper than MaxRefreshRescanDepth
		// below the tip (height 2000). The walk is bounded by the rescan floor 2000 - 1440 + 1 = 561,
		// so it scans exactly 1440 heights (2000..561), never descends toward the anchor, and the deep
		// payment at height 20 is never discovered: no supported candidates, no raw fetch, no hang.
		const int tip = 2000;
		const int anchor = 10;
		const int paymentHeight = 20;
		string paymentId = Id(0x79);
		using var harness = new ElementsRpcHarness(DeepRescanResult(tip, paymentHeight, paymentId));

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[],
			null,
			CancellationToken.None,
			anchor);

		Assert.Empty(observation.Candidates);
		Assert.Empty(observation.RawTransactions);
		Assert.DoesNotContain(harness.Handler.Parameters, IsRawFetch);
		// The walk stopped at the rescan floor, exactly MaxRefreshRescanDepth heights from the tip.
		Assert.Equal(1440, harness.Handler.Methods.Count(method => method == "getblock"));
	}

	[Fact]
	public async Task NoGapStillScansOnlyTheRecentSixBlocksAsync()
	{
		// The wallet's confirmed-history high-water (height 98) sits inside the recent six-block window
		// (95..100), so there is no gap: the walk is exactly the recent six heights, unchanged.
		const int tip = 100;
		const int anchor = 98;
		string paymentId = Id(0x7A);
		using var harness = new ElementsRpcHarness(DeepRescanResult(tip, paymentHeight: 30, paymentId));

		using ElementsWalletRefreshObservation observation = await harness.Client.GetWalletRefreshObservationAsync(
			ValidExpectation(),
			PeggedAsset,
			[],
			null,
			CancellationToken.None,
			anchor);

		// The payment at height 30 is below the recent window and there is no gap, so it is not discovered.
		Assert.Empty(observation.Candidates);
		Assert.Equal(6, harness.Handler.Methods.Count(method => method == "getblock"));
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

	// A deterministic, ordinal-distinct, nonzero lowercase 32-byte hash per walked height so the
	// newest-first deep-rescan walk can address any height from the tip down to the rescan floor.
	private static string DeepBlockHash(int height) => height.ToString("x8").PadLeft(63, '0') + "f";

	private static Func<RpcInvocation, string> DeepRescanResult(int tip, int paymentHeight, string paymentId) => invocation => invocation.Method switch
	{
		"getnodegeneration" => Envelope(invocation.Id, GenerationResult(StartupId, 9, tip, DeepBlockHash(tip))),
		"getnetworkinfo" => Envelope(invocation.Id, NetworkResult()),
		"getblockchaininfo" => Envelope(
			invocation.Id,
			$$"""{"chain":"elementsregtest","blocks":{{tip}},"headers":{{tip}},"bestblockhash":"{{DeepBlockHash(tip)}}","initialblockdownload":false,"pruned":false,"trim_headers":false,"warnings":""}"""),
		"getsidechaininfo" => Envelope(invocation.Id, SidechainResult()),
		"getblockhash" when invocation.Parameters == "[0]" => Envelope(invocation.Id, JsonSerializer.Serialize(GenesisBlockHash)),
		"getblockhash" => Envelope(invocation.Id, JsonSerializer.Serialize(DeepBlockHash(int.Parse(ExtractRequestedHeight(invocation.Parameters))))),
		"getrawmempool" => Envelope(invocation.Id, "[]"),
		"getblock" when invocation.Parameters.Contains(DeepBlockHash(paymentHeight), System.StringComparison.Ordinal) => Envelope(invocation.Id, BlockResult(paymentId)),
		"getblock" => Envelope(invocation.Id, BlockResult()),
		"getrawtransaction" when IsVerbose(invocation) => Envelope(invocation.Id, VerboseTransactionResult(invocation)),
		"getrawtransaction" => Envelope(invocation.Id, JsonSerializer.Serialize("010203")),
		_ => throw new System.InvalidOperationException($"Unexpected RPC method '{invocation.Method}' with parameters '{invocation.Parameters}'."),
	};

	private static string ExtractRequestedHeight(string parameters)
	{
		using JsonDocument document = JsonDocument.Parse(parameters);
		return document.RootElement[0].GetRawText();
	}
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
