using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Fluent;
using WalletWasabi.Fluent.Models;
using WalletWasabi.Fluent.Models.ClientConfig;
using WalletWasabi.Fluent.Models.FileSystem;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Models.Wallets.Liquid;
using WalletWasabi.Fluent.ViewModels.SearchBar.Sources;
using WalletWasabi.Fluent.ViewModels.Wallets.Liquid;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using Xunit;
using ClientScheme = WalletWasabi.Client.Scheme;
using NBNetwork = NBitcoin.Network;
using WWallet = WalletWasabi.Wallets.Wallet;
using WWalletManager = WalletWasabi.Wallets.WalletManager;
using WWalletTransaction = WalletWasabi.Blockchain.Transactions.SmartTransaction;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

// LIQUID-UI-HOME-LAYOUT-001: the balance tile's view-model surface. The
// LiquidWalletViewModel exposes PeggedBalanceRow — a presentation-only mirror
// of the pegged (L-BTC) entry of BalanceRows — so the tile can bind the same
// row prominently without fabricating a USD amount or a second balance. The
// tile mirrors, it never adds: PeggedBalanceRow must reference the very row
// object inside BalanceRows.
[Collection("Serial unit tests collection")]
public class LiquidWalletBalanceTilePresentationTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string IssuedAssetAHex = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAssetA => LiquidAssetId.ParseRpcHex(IssuedAssetAHex);
	private static LiquidSpendKeyReference ExternalKey =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), LiquidKeyBranch.External, 0);

	// The pegged row of the initial snapshot is mirrored into PeggedBalanceRow
	// — same object, same atomic units — and the full per-asset row list keeps
	// every asset (multi-asset is a feature, not replaced by the tile).
	[Fact]
	public void PeggedBalanceRowMirrorsThePeggedEntryOfBalanceRows()
	{
		LiquidTransactionId tx = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, PeggedAsset, 12_345), Output(tx, 1, IssuedAssetA, 777)]));
		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		using var model = new WalletWasabi.Fluent.Models.Wallets.Liquid.LiquidWalletModel(
			"wallet", Manifest, snapshot, ExternalKey.GetScriptPubKey(), new byte[33]);
		var viewModel = new LiquidWalletViewModel(BuildUiContext(), model);

		Assert.NotNull(viewModel.BalanceRows);
		Assert.Equal(2, viewModel.BalanceRows!.Count);
		Assert.NotNull(viewModel.PeggedBalanceRow);
		Assert.True(viewModel.PeggedBalanceRow!.IsPeggedAsset);
		Assert.Equal(12_345, viewModel.PeggedBalanceRow.AtomicUnits);

		// Mirror, not a copy: the tile row is the same row object the
		// per-asset list shows.
		LiquidAssetBalanceItemViewModel peggedRow =
			viewModel.BalanceRows.Single(row => row.IsPeggedAsset);
		Assert.Same(peggedRow, viewModel.PeggedBalanceRow);
	}

	// A snapshot without any pegged-asset entry (issued-asset-only balances)
	// exposes no tile row and still lists every asset row.
	[Fact]
	public void SnapshotWithoutPeggedAssetExposesNoPeggedBalanceRow()
	{
		LiquidTransactionId tx = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, IssuedAssetA, 500)]));
		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		using var model = new WalletWasabi.Fluent.Models.Wallets.Liquid.LiquidWalletModel(
			"wallet", Manifest, snapshot, ExternalKey.GetScriptPubKey(), new byte[33]);
		var viewModel = new LiquidWalletViewModel(BuildUiContext(), model);

		Assert.Null(viewModel.PeggedBalanceRow);
		Assert.Single(viewModel.BalanceRows!);
		Assert.False(viewModel.BalanceRows![0].IsPeggedAsset);
	}

	// The mirror tracks balance refreshes: after RefreshBalances, both the row
	// list and PeggedBalanceRow project the new snapshot (old row objects are
	// replaced wholesale, never mutated in place).
	[Fact]
	public void PeggedBalanceRowFollowsBalanceRefreshes()
	{
		LiquidTransactionId txA = Tx('c');
		LiquidTransactionId txB = Tx('d');
		LiquidWalletState initial = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]));
		LiquidWalletState advanced = initial
			.Apply(1, Delta(txB, [], [Output(txB, 0, IssuedAssetA, 42)]));

		using var model = new WalletWasabi.Fluent.Models.Wallets.Liquid.LiquidWalletModel(
			"wallet", Manifest, LiquidWalletUiSnapshot.Capture("wallet", Manifest, initial),
			ExternalKey.GetScriptPubKey(), new byte[33]);
		var viewModel = new LiquidWalletViewModel(BuildUiContext(), model);
		LiquidAssetBalanceItemViewModel before = viewModel.PeggedBalanceRow!;
		Assert.Equal(1_000, before.AtomicUnits);

		model.RefreshBalances(LiquidWalletUiSnapshot.Capture("wallet", Manifest, advanced));

		Assert.Equal(2, viewModel.BalanceRows!.Count);
		Assert.NotSame(before, viewModel.PeggedBalanceRow);
		Assert.Same(
			viewModel.BalanceRows.Single(row => row.IsPeggedAsset),
			viewModel.PeggedBalanceRow);
		Assert.Equal(1_000, viewModel.PeggedBalanceRow!.AtomicUnits); // pegged output unspent
	}

	// The mirror raises change notification so the tile re-renders on refresh.
	[Fact]
	public void PeggedBalanceRowRaisesPropertyChangedOnRefresh()
	{
		LiquidTransactionId txA = Tx('e');
		LiquidTransactionId txB = Tx('f');
		LiquidWalletState initial = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 100)]));
		LiquidWalletState advanced = initial
			.Apply(1, Delta(txB, [OutPoint(txA, 0)], [Output(txB, 0, PeggedAsset, 250)]));

		using var model = new WalletWasabi.Fluent.Models.Wallets.Liquid.LiquidWalletModel(
			"wallet", Manifest, LiquidWalletUiSnapshot.Capture("wallet", Manifest, initial),
			ExternalKey.GetScriptPubKey(), new byte[33]);
		var viewModel = new LiquidWalletViewModel(BuildUiContext(), model);

		string? raised = null;
		viewModel.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(LiquidWalletViewModel.PeggedBalanceRow))
			{
				raised = args.PropertyName;
			}
		};

		model.RefreshBalances(LiquidWalletUiSnapshot.Capture("wallet", Manifest, advanced));

		Assert.Equal(nameof(LiquidWalletViewModel.PeggedBalanceRow), raised);
		Assert.Equal(250, viewModel.PeggedBalanceRow!.AtomicUnits);
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidOutPoint OutPoint(LiquidTransactionId transactionId, uint outputIndex) =>
		LiquidOutPoint.CreateSpendable(transactionId, outputIndex);

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
		IEnumerable<LiquidOutPoint> spent,
		IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);

	private static UiContext BuildUiContext()
	{
		var services = new StubServices();
		var amountProvider = new AmountProvider(services);
		var torStatusChecker = new TorStatusCheckerModel(services);
		return new UiContext(
			services,
			new QrCodeGenerator(),
			new QrCodeReader(),
			new UiClipboard(),
			new WalletRepository(services, amountProvider),
#pragma warning disable CA2000 // Ownership transfers to the UiContext, which holds it for the app lifetime.
			new LiquidWalletRepository(),
#pragma warning restore CA2000
			BuildLiquidSession(),
			new CoinjoinModel(services),
			new HardwareWalletInterface(services),
			new FileSystemModel(),
			new ClientConfigModel(services),
			new ApplicationSettings(services, services.PersistentConfig, services.Config, services.UiConfig),
			new TransactionBroadcasterModel(services, services.GetNetwork()),
			amountProvider,
			new EditableSearchSource(),
			torStatusChecker,
			new HealthMonitor(services, torStatusChecker),
			new WalletWasabi.Announcements.ReleaseHighlights(),
			services.Scheme);
	}

	// These tests never open a wallet, so a session rooted at a fresh throwaway
	// directory is sufficient (the application client is created lazily on
	// first open, never here).
	private static LiquidWalletSession BuildLiquidSession()
	{
		string root = Path.Combine(Common.GetWorkDir(), "liquid-session");
		return new LiquidWalletSession(
			Path.Combine(root, "appdata"),
			Path.Combine(root, "wallets"));
	}

	// Minimal IServices stub: returns real lightweight instances for the
	// members exercised during UiContext construction. Members never touched
	// by the code under test throw to surface any unexpected dependency.
	private sealed class StubServices : IServices
	{
		public StubServices()
		{
			string filePath = Path.Combine(Common.GetWorkDir(), "UiConfig.json");
			UiConfig = new UiConfig(filePath);
			EventBus = new EventBus();
			PersistentConfig = CreatePersistentConfig();
			Config = new Config(PersistentConfig, []);
			WalletManager = new WWalletManager(
				NBNetwork.RegTest,
				new WalletWasabi.Wallets.WalletDirectories(NBNetwork.RegTest, Common.GetWorkDir()),
				keyManager => throw new NotSupportedException());
		}

		public string DataDir => Common.GetWorkDir();
		public string PersistentConfigFilePath => Path.Combine(DataDir, "PersistentConfig.json");
		public PersistentConfig PersistentConfig { get; }
		public WWalletManager WalletManager { get; }
		public UiConfig UiConfig { get; }
		public Config Config { get; }
		public EventBus EventBus { get; }
		public ClientScheme Scheme => null!;
		public uint GetTipHeight() => 0;
		public uint GetServerTipHeight() => 0;
		public int GetHashesLeft() => 0;
		public SmartHeader? GetTip() => null;
		public uint GetBlockHeadersTipHeight() => 0;
		public int GetPeerCount() => 0;
		public uint? GetMinimumBlockHeight() => null;
		public IEnumerable<LabelsArray> GetTransactionLabels() => [];
		public bool TryGetTransaction(uint256 hash, [NotNullWhen(true)] out SmartTransaction? tx)
		{
			tx = null;
			return false;
		}
		public NBNetwork GetNetwork() => NBNetwork.RegTest;
		public IEnumerable<WWallet> GetWallets() => [];
		public bool HasWallet() => false;
		public WWallet GetWalletByName(string walletName) => throw new NotSupportedException();
		public void RenameWallet(WWallet wallet, string newWalletName) => throw new NotSupportedException();
		public string GetWalletsDir() => Common.GetWorkDir();
		public string GetNextWalletName(string prefix) => throw new NotSupportedException();
		public string GetWalletFilePath(string walletName) => throw new NotSupportedException();
		public (ErrorSeverity Severity, string Message)? ValidateWalletName(string walletName) => null;
		public Task StartWalletAsync(WWallet wallet) => Task.CompletedTask;
		public void AddWallet(KeyManager keyManager) => throw new NotSupportedException();
		public string GetTorLogFilePath() => Path.Combine(DataDir, "Tor.log");
		public TorMode GetUseTor() => TorMode.Disabled;
		public decimal GetUsdExchangeRate() => 0m;
		public bool GetHideOnClose() => false;
		public double? GetWindowWidth() => null;
		public double? GetWindowHeight() => null;
		public void SetWindowWidth(double? width) { }
		public void SetWindowHeight(double? height) { }
		public string? GetLastSelectedWallet() => null;
		public void SetLastSelectedWallet(string? walletName) { }
		public bool GetPrivacyMode() => UiConfig.PrivacyMode;
		public bool GetAutocopy() => true;
		public bool GetAutoPaste() => false;
		public bool GetSendAmountConversionReversed() => false;
		public void SetSendAmountConversionReversed(bool value) { }
		public int GetFeeTarget() => 2;
		public void SetFeeTarget(int value) { }
		public T? GetHostedService<T>() where T : class, Microsoft.Extensions.Hosting.IHostedService => null;
		public Task SendTransactionAsync(WWalletTransaction transaction) => throw new NotSupportedException();
		public HttpClient CreateHttpClient(string name) => new HttpClient();
		public bool IsForcefulTerminationRequested() => false;

		private static PersistentConfig CreatePersistentConfig() =>
			new(
				NBNetwork.RegTest,
				"http://localhost:37127/",
				"Enabled",
				false,
				new WalletWasabi.Helpers.ValueList<string>(),
				false,
				"",
				"http://localhost:18443/",
				false,
				"",
				"",
				new WalletWasabi.Helpers.ValueList<string>(),
				Money.Coins(0.0001m),
				true,
				"coordinator",
				"CoinGecko",
				"BlockstreamInfo",
				"",
				0.003m,
				1,
				7,
				new WalletWasabi.Helpers.ValueList<string>(),
				1);
	}
}
