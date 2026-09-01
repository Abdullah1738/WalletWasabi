using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin.Secp256k1;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

/// <summary>
/// LIQUID-SEND-EXECUTOR-TESTS-001: a single proof that the real internal
/// <see cref="LiquidWalletSendExecutor"/> (not <c>CreateSendCommandForTesting</c> and not a
/// substituted executor) drives its accepted path end to end with test-owned scope/RPC doubles.
/// The vector is the committed signable fixture's exact L-BTC selection: funding tx vout 0 of 900
/// atomic pegged asset, one confidential destination of 800, an explicit 100 fee, no change output
/// and no issued-asset input. The wallet state carries only the funding output (revision 1); the
/// managed-built sign request is a fresh one-input/one-confidential-output frame over the fixture's
/// real spend key/script/descriptor/SLIP-77/funding/previous data — it does not reproduce the
/// committed mixed-asset fixture frame. The real <see cref="LiquidWalletNativeSigner"/> (the real
/// pinned native binding) signs and finalizes that managed-built frame; the entropy seed is pinned
/// only to make this newly built transaction deterministic. The test asserts the key-owner
/// callbacks ran, the RPC double observed exactly one <c>sendrawtransaction</c> carrying the
/// produced signed transaction, the receipt id matched the local canonical txid, and the accepted
/// refresh was scheduled after broadcast. This is unit/in-memory evidence only — not live
/// broadcast or custody proof.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletSendExecutorAcceptedPathTests
{
	private const string WalletName = "liquid-send-proof";
	private const string GenesisBlockHash = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string BestBlockHash = "2222222222222222222222222222222222222222222222222222222222222222";
	private const string ParentGenesis = "3333333333333333333333333333333333333333333333333333333333333333";
	private const int Blocks = 42;

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;

	private static string FixtureRoot => System.IO.Path.Combine(
		AppContext.BaseDirectory, "TestData", "Liquid", "OrdinaryWalletPlanWireV1", "signable");

	private static string ReadField(string name) =>
		System.IO.File.ReadAllText(System.IO.Path.Combine(FixtureRoot, name + ".txt")).Trim();

	private static byte[] ReadFieldBytes(string name) => Convert.FromHexString(ReadField(name));

	[Fact]
	public async Task RealExecutorAcceptedPathSignsBroadcastsAndSchedulesRefreshAsync()
	{
		// The committed fixture scalars and raw transactions.
		string fundingTxid = ReadField("funding_txid");
		string previousTxid = ReadField("previous_txid");
		string confidentialAddress = ReadField("confidential_address");
		string descriptor = ReadField("descriptor");
		ulong lastIndex = ulong.Parse(ReadField("last_index"));
		byte[] slip77 = ReadFieldBytes("slip77");
		byte[] sourceEpoch = ReadFieldBytes("source_epoch");
		byte[] entropySeed = ReadFieldBytes("entropy_seed");
		byte[] fundingTxBytes = ReadFieldBytes("funding_tx");
		byte[] previousTxBytes = ReadFieldBytes("previous_tx");
		byte[] spendKey = ReadFieldBytes("spend_key");
		byte[] spendScript = ReadFieldBytes("spend_script");
		byte[] replayKey = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);

		// The fixture's fee_asset is the testnet pegged asset in consensus byte order; the managed
		// manifest/RPC surface uses the byte-reversed (RPC/display) hex. Convert once here.
		string feeAsset = ToRpcHex(ReadField("fee_asset"));
		Assert.Equal(feeAsset, Manifest.PeggedAssetId);

		// The wallet state carries only the funding output: funding tx vout 0, 900 atomic pegged,
		// at revision 1. This is the exact-selection vector (900 = 800 destination + 100 fee).
		LiquidAssetId pegged = LiquidAssetId.ParseRpcHex(feeAsset);
		// funding_txid is stored in the fixture in consensus byte order (the established fixture
		// test feeds it directly as consensus bytes); the managed id type is RPC/display order.
		LiquidTransactionId fundingId = LiquidTransactionId.ParseRpcHex(ToRpcHex(fundingTxid));
		LiquidSpendKeyReference fundingKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString(ReadField("spend_pubkey")), LiquidKeyBranch.External, 0);
		LiquidOwnedOutput fundingOutput = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(fundingId, 0),
			spendScript,
			LiquidAssetAmount.Create(pegged, pegged, 900),
			fundingKey);
		LiquidWalletState state = LiquidWalletState.Empty(pegged)
			.Apply(0, LiquidWalletTransactionDelta.Create(fundingId, [], [fundingOutput]));
		Assert.Equal(1ul, state.Revision);

		string walletDataDir = GetWorkDir();
		LiquidWalletLoadSave.Save(walletDataDir, WalletName, state, generation: 1, replayKey, context);

		// The RPC double: the pre-submit generation/fee-asset observation (the generation API is
		// absent on Liquid testnet, so the generation fence is getblockchaininfo-derived), exactly
		// one sendrawtransaction that captures the broadcast hex and answers with the
		// native-recomputed canonical txid, then a post-submit generation re-check.
		using var handler = new SendHandler(feeAsset);
		using var httpClient = new HttpClient(handler, disposeHandler: false)
		{
			BaseAddress = new Uri("http://127.0.0.1:18884/"),
		};
		using var rpcClient = new ElementsRpcClient(httpClient);

		// The key owner holding the fixture spend key; counts the seam callbacks.
		using var keyOwner = new CountingKeyOwner(spendKey);

		// The funding source: the funding tx (candidate) plus the one previous tx the funding
		// dependency row names. The expectation-bound observation is constructed in-memory; the
		// candidate carries no confirmation, so no block hash binds the row. The batch's request
		// keys are canonical RPC/display-order ids (the established ElementsRawTransactionRequest
		// convention), so the raw fixture ids are converted exactly once here.
		ElementsExpectationBoundRawTransactionBatch fundingSource = CreateFundingSource(
			feeAsset, ToRpcHex(fundingTxid), fundingTxBytes, ToRpcHex(previousTxid), previousTxBytes);

		// The test-owned per-call scope. It carries the production scope values (key owner,
		// descriptor, last index, SLIP-77, source epoch, funding source, RPC client, fee asset,
		// wallet data directory) but constructs the real LiquidWalletNativeSigner through
		// CreateForTesting so the entropy seed is pinned — making this newly built transaction
		// deterministic. The signer type and the real native binding are unchanged; only the
		// entropy source is pinned.
		using var testScope = new TestScope(
			replayKey,
			context,
			sourceEpoch,
			keyOwner,
			descriptor,
			lastIndex,
			slip77,
			rpcClient,
			feeAsset,
			walletDataDir,
			entropySeed,
			fundingSource,
			handler);

		var request = new LiquidWalletUiSendExecutionRequest(
			WalletName,
			[OutPointHex(fundingTxid, 0)],
			confidentialAddress,
			feeAsset,
			destinationAtomicUnits: 800,
			explicitFeeAtomicUnits: 100,
			expectedRevision: 1,
			previousTransactionIdsBySelectedInput: [(IReadOnlyList<string>)[ToRpcHex(previousTxid)]]);

		var executor = new LiquidWalletSendExecutor(Manifest);
		LiquidWalletUiSendExecutionResult result = await executor.ExecuteAsync(
			request, testScope.Factory, CancellationToken.None);

		// The executor accepted: exactly one sendrawtransaction carried the produced signed
		// transaction, the receipt id matched the local canonical txid, and the accepted refresh
		// was scheduled after broadcast. Status/message lead so a pre-submit rejection (which
		// never reaches the signer) is diagnosable before the callback assertions.
		Assert.Equal(LiquidWalletUiSendExecutionStatus.AcceptedAndRefreshScheduled, result.Status);
		Assert.Equal("send-accepted", result.DisplayMessage);
		Assert.True(result.BroadcastAttempted);
		Assert.True(result.RefreshScheduled);
		Assert.Equal(1, handler.SendRawCount);

		// The real native signer ran and finalized the managed-built sign request.
		Assert.True(keyOwner.PublicKeyCalls > 0, "The key-owner public-key callback was not invoked.");
		Assert.True(keyOwner.SignCalls > 0, "The key-owner digest-signing callback was not invoked.");
		Assert.Equal(keyOwner.PublicKeyCalls, keyOwner.SignCalls);
		Assert.NotNull(result.LocalTransactionIdHex);
		Assert.Equal(result.LocalTransactionIdHex, result.AcceptedTransactionIdHex);
		Assert.Equal(result.LocalTransactionIdHex, handler.ScheduledAcceptedTxid);
		Assert.True(handler.RefreshScheduledAfterBroadcast);

		// The single sendrawtransaction argument is exactly the produced signed transaction, and the
		// receipt id is the native-recomputed canonical txid of those bytes (the result's local id).
		Assert.Equal(handler.BroadcastHex, handler.SendRawSignedTransactionHex);
		Assert.True(
			LiquidWalletNativeSigningBinding.TryGetTransactionId(
				Convert.FromHexString(handler.BroadcastHex!), out byte[] recomputed),
			"The produced transaction must re-decode to a transaction id.");
		Assert.Equal(result.LocalTransactionIdHex, Encoding.ASCII.GetString(recomputed));
	}

	private static string ToRpcHex(string consensusOrderHex)
	{
		byte[] bytes = Convert.FromHexString(consensusOrderHex);
		Array.Reverse(bytes);
		return Convert.ToHexStringLower(bytes);
	}

	private static string OutPointHex(string txidConsensusHex, uint index)
	{
		// txidConsensusHex is the fixture's consensus-order transaction-id bytes; the frame row uses
		// them directly (no byte reversal here).
		byte[] txid = Convert.FromHexString(txidConsensusHex);
		byte[] indexBytes = BitConverter.GetBytes(index);
		if (!BitConverter.IsLittleEndian)
		{
			Array.Reverse(indexBytes);
		}
		return Convert.ToHexStringLower([.. txid, .. indexBytes]);
	}

	private static ElementsExpectationBoundRawTransactionBatch CreateFundingSource(
		string feeAsset,
		string fundingTxid,
		byte[] fundingTxBytes,
		string previousTxid,
		byte[] previousTxBytes)
	{
		var expectation = new ElementsNodeExpectation(
			Manifest.ChainRpcName,
			GenesisBlockHash,
			"51",
			feeAsset,
			ParentGenesis,
			8,
			false,
			230303,
			70016,
			"/Elements Core:23.3.3/");
		var status = new ElementsNodeStatus(
			expectation.Chain,
			Blocks,
			Blocks,
			BestBlockHash,
			GenesisBlockHash,
			false,
			false,
			false,
			false,
			true,
			true,
			false,
			expectation.FedpegScript,
			expectation.PeggedAsset,
			expectation.ParentGenesisBlockHash,
			expectation.PeginConfirmationDepth,
			expectation.EnforcePak,
			expectation.Version,
			expectation.ProtocolVersion,
			expectation.Subversion);
		var generation = ElementsNodeGenerationObservation.CreateFallbackTipObservation(Blocks, BestBlockHash);
		var nodeObservation = new ElementsExpectationBoundNodeObservation(
			feeAsset, status, generation);
		ElementsRawTransactionObservation[] observations =
		[
			new ElementsRawTransactionObservation(
				new ElementsRawTransactionRequest(fundingTxid, null), fundingTxBytes),
			new ElementsRawTransactionObservation(
				new ElementsRawTransactionRequest(previousTxid, null), previousTxBytes),
		];
		return new ElementsExpectationBoundRawTransactionBatch(nodeObservation, observations);
	}

	private static string GetWorkDir()
	{
		string dir = Common.GetWorkDir();
		System.IO.Directory.CreateDirectory(dir);
		return dir;
	}

	/// <summary>
	/// A test-owned per-call scope that mirrors the production scope's inputs but constructs the
	/// real <see cref="LiquidWalletNativeSigner"/> through
	/// <see cref="LiquidWalletNativeSigner.CreateForTesting"/> so the entropy seed is pinned. Every
	/// other input (key owner, descriptor, last index, SLIP-77, source epoch, funding source, RPC
	/// client) is the production value. The factory is a test-owned
	/// <see cref="ILiquidWalletSendExecutionScopeFactory"/> that returns this one scope.
	/// </summary>
	private sealed class TestScope : ILiquidWalletSendExecutionScope
	{
		internal TestScope(
			byte[] replayKey,
			byte[] context,
			byte[] sourceEpoch,
			ILiquidWalletSigner keyOwner,
			string descriptor,
			ulong lastIndex,
			byte[] slip77,
			ElementsRpcClient rpcClient,
			string feeAsset,
			string walletDataDirectory,
			byte[] entropySeed,
			ElementsExpectationBoundRawTransactionBatch fundingSource,
			SendHandler handler)
		{
			ReplayProtectionKey = replayKey;
			ExternalWalletNetworkContext = context;
			SourceEpoch = sourceEpoch;
			KeyOwner = keyOwner;
			DescriptorBytes = Encoding.UTF8.GetBytes(descriptor);
			DescriptorString = descriptor;
			LastIndex = lastIndex;
			Slip77MasterKey = slip77;
			RpcClient = rpcClient;
			ExpectedEffectiveFeeAsset = feeAsset;
			WalletDataDirectory = walletDataDirectory;
			_fundingSource = fundingSource;
			_handler = handler;
			Factory = new TestScopeFactory(this);
			Signer = LiquidWalletNativeSigner.CreateForTesting(
				keyOwner, descriptor, lastIndex, slip77, () => (byte[])entropySeed.Clone());
		}

		private readonly ElementsExpectationBoundRawTransactionBatch _fundingSource;
		private readonly SendHandler _handler;

		internal ILiquidWalletSendExecutionScopeFactory Factory { get; }
		public byte[] ReplayProtectionKey { get; }
		public byte[] ExternalWalletNetworkContext { get; }
		public byte[] SourceEpoch { get; }
		public ILiquidWalletSigner KeyOwner { get; }
		public LiquidWalletNativeSigner Signer { get; }
		public byte[] DescriptorBytes { get; }
		public string DescriptorString { get; }
		public ulong LastIndex { get; }
		public byte[] Slip77MasterKey { get; }
		public ElementsRpcClient RpcClient { get; }
		public string ExpectedEffectiveFeeAsset { get; }
		public string WalletDataDirectory { get; }

		public Task<ElementsExpectationBoundRawTransactionBatch> AcquireFundingSourceAsync(
			LiquidWalletUiSendExecutionRequest request, CancellationToken cancellationToken) =>
			Task.FromResult(_fundingSource);

		public Task ScheduleRefreshAsync(string canonicalTransactionIdHex, CancellationToken cancellationToken)
		{
			_handler.ScheduledAcceptedTxid = canonicalTransactionIdHex;
			_handler.RefreshScheduledAfterBroadcast = _handler.SendRawCount == 1;
			return Task.CompletedTask;
		}

		public Task ScheduleManualRefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public void Dispose() => Signer.Dispose();

		private sealed class TestScopeFactory(TestScope scope) : ILiquidWalletSendExecutionScopeFactory
		{
			public ILiquidWalletSendExecutionScope Open(string walletName) => scope;
		}
	}

	/// <summary>
	/// The RPC double. The generation API is absent on Liquid testnet, so the executor's single
	/// broadcast performs its pre-submit generation/fee-asset observation from
	/// <c>getblockchaininfo</c>/<c>getsidechaininfo</c>/<c>getnetworkinfo</c>/<c>getblockhash</c>,
	/// issues exactly one <c>sendrawtransaction</c> (whose decoded first argument is captured and
	/// answered with the native-recomputed canonical txid), then re-checks the generation from
	/// <c>getblockchaininfo</c>.
	/// </summary>
	private sealed class SendHandler(string feeAsset) : HttpMessageHandler
	{
		internal int SendRawCount { get; private set; }
		internal string? BroadcastHex { get; private set; }
		internal string? SendRawSignedTransactionHex { get; private set; }
		internal string? ScheduledAcceptedTxid { get; set; }
		internal bool RefreshScheduledAfterBroadcast { get; set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument document = JsonDocument.Parse(body);
			string method = document.RootElement.GetProperty("method").GetString()!;
			string id = document.RootElement.GetProperty("id").GetString()!;
			string parameters = document.RootElement.GetProperty("params").GetRawText();

			string result = method switch
			{
				"getnetworkinfo" =>
					"{\"version\":230303,\"protocolversion\":70016,\"subversion\":\"/Elements Core:23.3.3/\",\"localrelay\":true,\"networkactive\":true,\"warnings\":\"\"}",
				"getblockchaininfo" =>
					$$"""{"chain":"{{LiquidTestnetChain}}","blocks":{{Blocks}},"headers":{{Blocks}},"bestblockhash":"{{BestBlockHash}}","initialblockdownload":false,"pruned":false,"trim_headers":false,"warnings":""}""",
				"getblockhash" when parameters == "[0]" => JsonSerializer.Serialize(GenesisBlockHash),
				"getblockhash" => JsonSerializer.Serialize(BestBlockHash),
				"getsidechaininfo" =>
					$$"""{"fedpegscript":"51","pegged_asset":"{{feeAsset}}","fee_asset":"{{feeAsset}}","parent_blockhash":"{{ParentGenesis}}","pegin_confirmation_depth":8,"enforce_pak":false}""",
				"sendrawtransaction" => HandleSendRaw(parameters),
				_ => throw new InvalidOperationException($"Unexpected RPC method '{method}'."),
			};

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					$$"""{"result":{{result}},"error":null,"id":"{{id}}"}""",
					Encoding.UTF8,
					"application/json"),
				RequestMessage = request,
			};
		}

		private string HandleSendRaw(string parameters)
		{
			SendRawCount++;
			using JsonDocument parameterDocument = JsonDocument.Parse(parameters);
			string signedHex = parameterDocument.RootElement[0].GetString()!;
			SendRawSignedTransactionHex = signedHex;
			BroadcastHex = signedHex;
			Assert.True(
				LiquidWalletNativeSigningBinding.TryGetTransactionId(
					Convert.FromHexString(signedHex), out byte[] txid),
				"The broadcast transaction must re-decode to a transaction id.");
			return JsonSerializer.Serialize(Encoding.ASCII.GetString(txid));
		}

		private static string LiquidTestnetChain => ElementsPublicNetworkManifest.LiquidTestnet.ChainRpcName;
	}

	/// <summary>
	/// A key owner holding the fixture spend key: returns its compressed public key and signs the
	/// natively computed digest with a strict-DER low-S signature plus the AllPlusRangeproof sighash
	/// byte. Counts the seam callbacks so the test can prove the key owner was exercised.
	/// </summary>
	private sealed class CountingKeyOwner : ILiquidWalletSigner, IDisposable
	{
		private const byte SighashAllPlusRangeproofByte = 0x41;
		private readonly ECPrivKey _key;
		private readonly string _publicKeyHex;

		internal CountingKeyOwner(byte[] spendKey)
		{
			_key = ECPrivKey.Create(spendKey);
			_publicKeyHex = Convert.ToHexStringLower(_key.CreatePubKey().ToBytes());
		}

		internal int PublicKeyCalls { get; private set; }
		internal int SignCalls { get; private set; }

		public string? GetPublicKeyHex(string outPointHex)
		{
			PublicKeyCalls++;
			return _publicKeyHex;
		}

		public string? SignDigestHex(string outPointHex, string digestHex)
		{
			SignCalls++;
			byte[] digest = Convert.FromHexString(digestHex);
			if (digest.Length != 32 || !_key.TrySignECDSA(digest, out SecpECDSASignature? signature) || signature is null)
			{
				return null;
			}
			byte[] der = signature.ToDER();
			return Convert.ToHexStringLower([.. der, SighashAllPlusRangeproofByte]);
		}

		public void Dispose() => _key.Dispose();
	}
}
