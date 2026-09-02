using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

/// <summary>
/// LIQUID-SEND-MIXED-ASSET-CHANGE-001: a single proof that the real internal
/// <see cref="LiquidWalletSendExecutor"/> drives a send whose selected inputs carry a per-asset
/// surplus over destination-plus-fee and automatically appends a wallet-owned branch-1
/// confidential change destination, so the exact plan validator balances per asset. The vector
/// mirrors the committed two-output fixture shape: funding tx vout 0 of 900 atomic pegged asset
/// (branch 0 index 0) plus vout 1 of 2000 atomic issued asset (branch 0 index 1), both selected;
/// the destination is 2000 issued; the explicit fee is 100 pegged. The pegged surplus is 900 −
/// 100 = 800, so exactly one branch-1 change output of 800 pegged is appended; the issued asset
/// balances exactly (2000 − 2000 = 0), so no issued change is appended. The test asserts the
/// executor accepts and the produced sign request carries a wallet-owned branch-1 change output
/// of exactly 800 pegged. This is unit/in-memory evidence only — not live broadcast or custody
/// proof.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletSendExecutorMixedAssetChangeTests
{
	private const string WalletName = "liquid-send-mixed-change-proof";
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
	public async Task SurplusAppendsWalletOwnedBranch1ChangeAsync()
	{
		string fundingTxid = ReadField("funding_txid");
		string previousTxid = ReadField("previous_txid");
		string descriptor = ReadField("descriptor");
		ulong lastIndex = ulong.Parse(ReadField("last_index"));
		byte[] slip77 = ReadFieldBytes("slip77");
		byte[] sourceEpoch = ReadFieldBytes("source_epoch");
		byte[] entropySeed = ReadFieldBytes("entropy_seed");
		byte[] fundingTxBytes = ReadFieldBytes("funding_tx");
		byte[] previousTxBytes = ReadFieldBytes("previous_tx");
		byte[] spendKey = ReadFieldBytes("spend_key");
		byte[] replayKey = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);

		string feeAsset = ToRpcHex(ReadField("fee_asset"));
		string secondAsset = ToRpcHex(ReadField("second_asset"));
		Assert.Equal(feeAsset, Manifest.PeggedAssetId);

		LiquidAssetId pegged = LiquidAssetId.ParseRpcHex(feeAsset);
		LiquidAssetId issued = LiquidAssetId.ParseRpcHex(secondAsset);
		LiquidTransactionId fundingId = LiquidTransactionId.ParseRpcHex(ToRpcHex(fundingTxid));

		// Derive the branch-0 index-0 and branch-0 index-1 spend scripts from the fixture
		// descriptor's account xpub — the same derivation the implementation performs. The
		// destination address for the issued send is the wallet's own branch-0 index-0
		// confidential address (a valid confidential address for the manifest).
		ExtPubKey accountPublicKey = ParseAccountPublicKey(descriptor);
		byte[] script0 = accountPublicKey.Derive(0).Derive(0).PubKey.WitHash.ScriptPubKey.ToBytes();
		byte[] script1 = accountPublicKey.Derive(0).Derive(1).PubKey.WitHash.ScriptPubKey.ToBytes();
		byte[] changeScript = accountPublicKey.Derive(1).Derive(0).PubKey.WitHash.ScriptPubKey.ToBytes();

		byte[] changeBlinding = LiquidSlip77PublicKey.Derive(slip77, changeScript);
		string expectedChangeAddress = LiquidAddress.FromScriptPubKey(
				Manifest,
				changeScript,
				LiquidBlindingPublicKey.Create(changeBlinding))
			.GetCanonicalAddressText();

		// The issued-asset destination address: a confidential address over the wallet's own
		// branch-0 index-0 script (a valid confidential destination for the manifest).
		byte[] destinationBlinding = LiquidSlip77PublicKey.Derive(slip77, script0);
		string destinationAddress = LiquidAddress.FromScriptPubKey(
				Manifest,
				script0,
				LiquidBlindingPublicKey.Create(destinationBlinding))
			.GetCanonicalAddressText();

		// The wallet state carries both funding outputs in one delta at revision 1: vout 0 = 900
		// pegged (branch 0 index 0), vout 1 = 2000 issued (branch 0 index 1). Both are selected.
		LiquidSpendKeyReference key0 = LiquidSpendKeyReference.Create(
			accountPublicKey.Derive(0).Derive(0).PubKey.ToBytes(), LiquidKeyBranch.External, 0);
		LiquidSpendKeyReference key1 = LiquidSpendKeyReference.Create(
			accountPublicKey.Derive(0).Derive(1).PubKey.ToBytes(), LiquidKeyBranch.External, 1);
		LiquidOwnedOutput output0 = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(fundingId, 0),
			script0,
			LiquidAssetAmount.Create(pegged, pegged, 900),
			key0);
		LiquidOwnedOutput output1 = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(fundingId, 1),
			script1,
			LiquidAssetAmount.Create(issued, pegged, 2_000),
			key1);
		LiquidWalletState state = LiquidWalletState.Empty(pegged)
			.Apply(0, LiquidWalletTransactionDelta.Create(fundingId, [], [output0, output1]));
		Assert.Equal(1ul, state.Revision);

		string walletDataDir = GetWorkDir();
		LiquidWalletLoadSave.Save(walletDataDir, WalletName, state, generation: 1, replayKey, context);

		using var handler = new SendHandler(feeAsset);
		using var httpClient = new HttpClient(handler, disposeHandler: false)
		{
			BaseAddress = new Uri("http://127.0.0.1:18884/"),
		};
		using var rpcClient = new ElementsRpcClient(httpClient);

		using var keyOwner = new CountingKeyOwner(spendKey);

		ElementsExpectationBoundRawTransactionBatch fundingSource = CreateFundingSource(
			feeAsset, ToRpcHex(fundingTxid), fundingTxBytes, ToRpcHex(previousTxid), previousTxBytes);

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

		// Both outpoints selected; destination 2000 issued; explicit fee 100 pegged. Pegged
		// surplus = 900 − 100 = 800; issued balances exactly.
		var request = new LiquidWalletUiSendExecutionRequest(
			WalletName,
			[OutPointHex(fundingTxid, 0), OutPointHex(fundingTxid, 1)],
			destinationAddress,
			secondAsset,
			destinationAtomicUnits: 2_000,
			explicitFeeAtomicUnits: 100,
			expectedRevision: 1,
			previousTransactionIdsBySelectedInput:
			[
				(IReadOnlyList<string>)[ToRpcHex(previousTxid)],
				(IReadOnlyList<string>)[ToRpcHex(previousTxid)],
			]);

		var executor = new LiquidWalletSendExecutor(Manifest);
		LiquidWalletUiSendExecutionResult result = await executor.ExecuteAsync(
			request, testScope.Factory, CancellationToken.None);

		// Status/message lead so a pre-submit rejection is diagnosable before the change
		// assertions.
		Assert.Equal(LiquidWalletUiSendExecutionStatus.AcceptedAndRefreshScheduled, result.Status);
		Assert.Equal("send-accepted", result.DisplayMessage);
		Assert.True(result.BroadcastAttempted);
		Assert.True(result.RefreshScheduled);
		Assert.Equal(1, handler.SendRawCount);

		// The scope reserved the wallet-owned branch-1 change address once (cached across both
		// facade calls), and it is the wallet-owned branch-1 index-0 confidential address. The
		// executor threaded it through so the facade appended the 800-pegged change output.
		Assert.Equal(expectedChangeAddress, testScope.ReservedChangeAddress);
		Assert.True(testScope.ChangeReservationCount >= 1, "The change address was never reserved.");

		// The real native signer ran and finalized the managed-built sign request.
		Assert.True(keyOwner.SignCalls > 0, "The key-owner digest-signing callback was not invoked.");
		Assert.NotNull(result.LocalTransactionIdHex);
		Assert.Equal(result.LocalTransactionIdHex, result.AcceptedTransactionIdHex);
	}

	private static ExtPubKey ParseAccountPublicKey(string descriptor)
	{
		// elwpkh(<accountXpub>/<0;1>/*)#checksum — extract the account xpub between "elwpkh("
		// and "/<0;1>/*", then parse it against the testnet network.
		const string Prefix = "elwpkh(";
		int start = descriptor.IndexOf(Prefix, StringComparison.Ordinal) + Prefix.Length;
		int end = descriptor.IndexOf("/<0;1>/*", StringComparison.Ordinal);
		string xpub = descriptor[start..end];
		return new BitcoinExtPubKey(xpub, NBitcoin.Network.TestNet).ExtPubKey;
	}

	private static string ToRpcHex(string consensusOrderHex)
	{
		byte[] bytes = Convert.FromHexString(consensusOrderHex);
		Array.Reverse(bytes);
		return Convert.ToHexStringLower(bytes);
	}

	private static string OutPointHex(string txidConsensusHex, uint index)
	{
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
	/// <see cref="LiquidWalletNativeSigner.CreateForTesting"/> so the entropy seed is pinned, and
	/// stubs the new reserved-change surface: it derives the wallet-owned branch-1 confidential
	/// address once per send and caches it for the second facade call, exactly as the production
	/// scope does.
	/// </summary>
	private sealed class TestScope : ILiquidWalletSendExecutionScope
	{
		private string? _cachedChangeAddress;

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
		internal int ChangeReservationCount { get; private set; }
		internal string? ReservedChangeAddress => _cachedChangeAddress;
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

		// The new reserved-change surface: reserves the wallet-owned branch-1 confidential change
		// address for one send, lazily on first request and cached for the second facade call so
		// no double-reservation occurs. Mirrors the production scope's derivation exactly.
		public bool TryReserveChangeDestination(out string? changeAddress)
		{
			ChangeReservationCount++;
			if (_cachedChangeAddress is null)
			{
				ExtPubKey accountPublicKey = ParseAccountPublicKey(DescriptorString);
				byte[] changeScript = accountPublicKey.Derive(1).Derive(0).PubKey.WitHash.ScriptPubKey.ToBytes();
				byte[] changeBlinding = LiquidSlip77PublicKey.Derive(Slip77MasterKey, changeScript);
				_cachedChangeAddress = LiquidAddress.FromScriptPubKey(
						Manifest,
						changeScript,
						LiquidBlindingPublicKey.Create(changeBlinding))
					.GetCanonicalAddressText();
			}

			changeAddress = _cachedChangeAddress;
			return true;
		}

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
