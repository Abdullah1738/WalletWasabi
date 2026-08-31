using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using ReactiveUI;
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
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Fluent.ViewModels.NavBar;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.SearchBar.Sources;
using WalletWasabi.Fluent.ViewModels.Wallets.Liquid;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
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

// Slice 1 wiring evidence: the previously orphaned Liquid Fluent surface is now
// reachable. Registering a LiquidWalletModel into the LiquidWalletRepository
// produces one LiquidWalletPageViewModel in the NavBar; selecting it pushes a
// LiquidWalletViewModel (titled with the wallet name) onto the HomeScreen; and
// the Liquid receive surface renders the confidential address derived from the
// model's next-receive script and blinding public key. These tests drive the
// real view models against a real UiContext, in the style of
// LiquidWalletHistoryPresentationTests.
[Collection("Serial unit tests collection")]
public class LiquidWalletWiringTests
{
	// The LiquidWalletModels created here are registered into the app-lifetime
	// LiquidWalletRepository, which owns and disposes them on Remove; they are
	// not disposed at the creation site.
#pragma warning disable CA2000
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlindingKeyHex = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";
	private const string IssuedAssetAHex = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAssetA => LiquidAssetId.ParseRpcHex(IssuedAssetAHex);
	private static LiquidSpendKeyReference ExternalKey =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), LiquidKeyBranch.External, 0);
	private static byte[] BlindingKey => Convert.FromHexString(BlindingKeyHex);
	private static byte[] ReceiveScript => ExternalKey.GetScriptPubKey();

	// A registered Liquid wallet appears as exactly one NavBar item carrying the
	// same model, and removing it from the repository removes the NavBar item.
	[Fact]
	public void RegisteredLiquidWalletProducesNavBarItem()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		// The NavBar observes the UiContext's Liquid repository.
		NavBarViewModel navBar = new(uiContext);
		navBar.Activate();

		LiquidWalletModel model = CreateModel("liquid-alpha", peggedAtomic: 5_000);
		uiContext.LiquidWalletRepository.AddOrUpdate(model);

		LiquidWalletPageViewModel item = Assert.Single(navBar.LiquidWallets);
		Assert.Same(model, item.WalletModel);
		Assert.Equal("liquid-alpha", item.Title);

		uiContext.LiquidWalletRepository.Remove("liquid-alpha");
		Assert.Empty(navBar.LiquidWallets);
	}

	// Multiple registered Liquid wallets each get their own NavBar item, sorted
	// by wallet name, and selecting one marks only it selected.
	[Fact]
	public void MultipleLiquidWalletsAreSortedByName()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		NavBarViewModel navBar = new(uiContext);
		navBar.Activate();

		uiContext.LiquidWalletRepository.AddOrUpdate(CreateModel("zeta", 1));
		uiContext.LiquidWalletRepository.AddOrUpdate(CreateModel("alpha", 2));
		uiContext.LiquidWalletRepository.AddOrUpdate(CreateModel("mid", 3));

		Assert.Equal(
			new[] { "alpha", "mid", "zeta" },
			navBar.LiquidWallets.Select(x => x.WalletModel.Name).ToArray());

		navBar.SelectLiquidWallet("mid");
		Assert.Equal("mid", navBar.SelectedLiquidWallet!.WalletModel.Name);
		Assert.True(navBar.SelectedLiquidWallet.IsSelected);
	}

	// The selected Liquid NavBar item produces the LiquidWalletViewModel the
	// NavBar navigates to (via CreateWalletViewModel): titled with the wallet
	// name and carrying the model's multiasset balance rows. (The navigation
	// push itself requires the app's registered navigation state, which a bare
	// NavBar in a unit test does not have; SelectLiquidWallet still applies the
	// selection.)
	[Fact]
	public void SelectedLiquidWalletProducesTitledLiquidWalletViewModel()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		NavBarViewModel navBar = new(uiContext);
		navBar.Activate();

		LiquidWalletModel model = CreateModel("liquid-home", peggedAtomic: 7_500, issuedAtomic: 250);
		uiContext.LiquidWalletRepository.AddOrUpdate(model);

		navBar.SelectLiquidWallet("liquid-home");

		LiquidWalletPageViewModel item = Assert.IsType<LiquidWalletPageViewModel>(navBar.SelectedLiquidWallet);
		LiquidWalletViewModel page = item.CreateWalletViewModel();
		Assert.Same(model, page.WalletModel);
		Assert.Equal("liquid-home", page.Title);

		// The balance projection renders one row per asset, pegged (L-BTC) first.
		Assert.NotNull(page.BalanceRows);
		Assert.Equal(2, page.BalanceRows!.Count);
		Assert.True(page.BalanceRows[0].IsPeggedAsset);
		Assert.Equal(7_500, page.BalanceRows[0].AtomicUnits);
	}

	// Selecting a Liquid wallet clears the BTC selection so exactly one wallet
	// page is shown; selecting a BTC wallet clears the Liquid selection.
	[Fact]
	public void LiquidAndBtcSelectionsAreMutuallyExclusive()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		NavBarViewModel navBar = new(uiContext);
		navBar.Activate();

		uiContext.LiquidWalletRepository.AddOrUpdate(CreateModel("liquid-x", 1));
		navBar.SelectLiquidWallet("liquid-x");
		Assert.NotNull(navBar.SelectedLiquidWallet);
		Assert.Null(navBar.SelectedWallet);
	}

	// The receive surface renders the confidential address derived from the
	// model's next-receive script and blinding public key, alongside the
	// unconfidential form, exactly as the landed facade projects them.
	[Fact]
	public void ReceiveViewModelRendersConfidentialAddressFromModel()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidWalletModel model = CreateModel("liquid-recv", peggedAtomic: 1_000);

		LiquidAddress landed = LiquidAddress.FromScriptPubKey(
			Manifest,
			ReceiveScript,
			LiquidBlindingPublicKey.Create(BlindingKey));

		LiquidReceiveViewModel receive = new(uiContext, model);
		receive.OnNavigatedTo(isInHistory: false);

		Assert.Equal(landed.GetCanonicalAddressText(), receive.ConfidentialAddressText);
		Assert.NotEqual(receive.ConfidentialAddressText, receive.UnconfidentialAddressText);
		Assert.NotNull(receive.QrCode);
	}

	// The Liquid wallet home carries a Send command beside Receive; executing
	// it navigates the DialogScreen to a LiquidSendViewModel wired with the
	// session's send executor.
	[Fact]
	public void SendCommandNavigatesToLiquidSendViewModel()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		var mainScreen = new TargettedNavigationStack(uiContext, NavigationTarget.HomeScreen);
		var dialogScreen = new DialogScreenViewModel(uiContext);
		var fullScreen = new DialogScreenViewModel(uiContext, NavigationTarget.FullScreen);
		var compactDialogScreen = new DialogScreenViewModel(uiContext, NavigationTarget.CompactDialogScreen);
		NavBarViewModel navBar = new(uiContext);
		uiContext.RegisterNavigation(
			new NavigationState(uiContext, mainScreen, dialogScreen, fullScreen, compactDialogScreen, navBar));

		LiquidWalletModel model = CreateModel("liquid-send-nav", peggedAtomic: 5_000);
		LiquidWalletViewModel page = new(uiContext, model);

		Assert.NotNull(page.SendCommand);
		Assert.True(page.SendCommand.CanExecute(null));
		page.SendCommand.Execute(null);

		LiquidSendViewModel send = Assert.IsType<LiquidSendViewModel>(dialogScreen.CurrentPage);
		Assert.Same(model, send.WalletModel);
		Assert.NotNull(send.ExecuteSendCommand);
		Assert.True(send.IsSendExecutionAvailable);
	}

	// The send screen's "Sign & broadcast" action builds the non-secret
	// execution request from the current inputs (one row per selected
	// outpoint, the last-rendered snapshot revision as the freshness fence),
	// invokes the session-wired delegate exactly once, and renders the
	// returned status + txids; a landed rejection surfaces as-is with no
	// fabricated success.
	[Fact]
	public async Task SendViewModelExecuteSendDelegatesAndSurfacesResultAsync()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidWalletModel model = CreateModel("liquid-send-exec", peggedAtomic: 5_000);
		ulong expectedRevision = model.Snapshot!.Revision;

		string outPointHex = "aa" + new string('f', 70);
		var result = new LiquidWalletUiSendExecutionResult(
			LiquidWalletUiSendExecutionStatus.AcceptedAndRefreshScheduled,
			model.Name,
			Manifest.ManifestId,
			expectedRevision,
			new string('1', 64),
			new string('2', 64),
			broadcastAttempted: true,
			refreshScheduled: true,
			"accepted");

		LiquidWalletUiSendExecutionRequest? captured = null;
		Task<LiquidWalletUiSendExecutionResult> Executor(
			LiquidWalletUiSendExecutionRequest request,
			CancellationToken cancellationToken)
		{
			captured = request;
			return Task.FromResult(result);
		}

		LiquidSendViewModel send = new(uiContext, model, Executor);
		send.Recipient.ConfidentialAddressText = "tex1qdestination";
		send.Recipient.AssetIdHex = new string('b', 64);
		send.Recipient.AtomicUnits = 4_000;
		send.SelectedOutPointHexesText = outPointHex;
		send.ExplicitFeeAtomicUnits = 100;

		await send.SendExecution.Execute().ToTask();

		Assert.NotNull(captured);
		Assert.Equal(model.Name, captured.WalletName);
		Assert.Equal(new[] { outPointHex }, captured.SelectedOutPointHexes);
		Assert.Equal("tex1qdestination", captured.ConfidentialDestinationAddress);
		Assert.Equal(new string('b', 64), captured.DestinationAssetIdHex);
		Assert.Equal(4_000, captured.DestinationAtomicUnits);
		Assert.Equal(100, captured.ExplicitFeeAtomicUnits);
		Assert.Equal(expectedRevision, captured.ExpectedRevision);
		Assert.Single(captured.PreviousTransactionIdsBySelectedInput);
		Assert.Null(captured.PreviousTransactionIdsBySelectedInput[0]);

		Assert.Same(result, send.ExecutionResult);
		Assert.Null(send.ExecutionErrorText);
	}

	// A landed send rejection surfaces as-is: the error text is set and no
	// result is fabricated.
	[Fact]
	public async Task SendViewModelExecuteSendSurfacesRejectionAsync()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidWalletModel model = CreateModel("liquid-send-reject", peggedAtomic: 5_000);

		Task<LiquidWalletUiSendExecutionResult> Executor(
			LiquidWalletUiSendExecutionRequest request,
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException("landed rejection");

		LiquidSendViewModel send = new(uiContext, model, Executor);
		send.Recipient.ConfidentialAddressText = "tex1qdestination";
		send.Recipient.AssetIdHex = new string('b', 64);
		send.Recipient.AtomicUnits = 4_000;
		send.SelectedOutPointHexesText = "aa" + new string('f', 70);
		send.ExplicitFeeAtomicUnits = 100;

		await send.SendExecution.Execute().ToTask();

		Assert.Null(send.ExecutionResult);
		Assert.Equal("landed rejection", send.ExecutionErrorText);
	}

	// Without a session-wired executor the send screen reports the missing
	// surface instead of fabricating a result.
	[Fact]
	public async Task SendViewModelWithoutExecutorReportsUnwiredSurfaceAsync()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidWalletModel model = CreateModel("liquid-send-unwired", peggedAtomic: 5_000);

		LiquidSendViewModel send = new(uiContext, model);
		Assert.False(send.IsSendExecutionAvailable);

		await send.SendExecution.Execute().ToTask();

		Assert.Null(send.ExecutionResult);
		Assert.Equal("The Liquid send execution surface is not wired for this wallet session.", send.ExecutionErrorText);
	}

	// The session's send executor delegates to the single application
	// client's SendCommand for the wallet the request names, after replacing
	// the request's previous-transaction-id rows with the rows derived from
	// the open session's current selectable outputs (one row per selected
	// outpoint carrying the outpoint's own transaction id, mirroring the
	// harness send phase); a selected outpoint that is not in the session's
	// selectable set keeps the caller's row. The delegate carries no key
	// material — keys stay in the session layer.
	[Fact]
	public async Task SessionExecutorDelegatesToClientSendCommandWithSessionPrevTxRowsAsync()
	{
		string root = Path.Combine(Common.GetWorkDir(), "liquid-session-delegate");
		var session = new LiquidWalletSession(
			Path.Combine(root, "appdata"),
			Path.Combine(root, "wallets"));

		string selectedOutPointHex = "aa" + new string('f', 70);
		string unknownOutPointHex = "bb" + new string('f', 70);
		string selectedTransactionIdHex = new string('c', 64);
		var handoff = new LiquidWalletRuntimeHandoff(
			"liquid-alpha",
			Manifest.ManifestId,
			CreateBalances("liquid-alpha", revision: 7),
			CreateSelectableOutputs("liquid-alpha", 7, (selectedOutPointHex, selectedTransactionIdHex)),
			CreateHistory("liquid-alpha", revision: 7),
			CreateReceiveMaterial());

		var request = new LiquidWalletUiSendExecutionRequest(
			"liquid-alpha",
			[selectedOutPointHex, unknownOutPointHex],
			"tex1qdestination",
			new string('b', 64),
			4_000,
			100,
			expectedRevision: 7,
			new IReadOnlyList<string>?[] { null, new[] { new string('d', 64) } });

		LiquidWalletUiSendExecutionRequest? receivedByClient = null;
		var expected = new LiquidWalletUiSendExecutionResult(
			LiquidWalletUiSendExecutionStatus.SubmissionAmbiguous,
			"liquid-alpha",
			Manifest.ManifestId,
			7,
			localTransactionIdHex: null,
			acceptedTransactionIdHex: null,
			broadcastAttempted: true,
			refreshScheduled: true,
			"ambiguous");

		var client = (LiquidWalletApplicationClient)RuntimeHelpers.GetUninitializedObject(typeof(LiquidWalletApplicationClient));
		SetField(client, "_sendCommand", (Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>)(
			(LiquidWalletUiSendExecutionRequest seen, CancellationToken _) =>
			{
				receivedByClient = seen;
				return Task.FromResult(expected);
			}));
		SetField(client, "_runtimeProvider", CreateHandoffProvider(handoff));
		SetField(session, "_client", client);

		LiquidWalletUiSendExecutionResult result = await session.ExecuteSendAsync(request, CancellationToken.None);

		// The client's SendCommand was invoked exactly once, with the session's
		// prev-tx rows substituted for the selected outpoints it knows.
		Assert.NotNull(receivedByClient);
		Assert.Equal("liquid-alpha", receivedByClient.WalletName);
		Assert.Equal(new[] { selectedOutPointHex, unknownOutPointHex }, receivedByClient.SelectedOutPointHexes);
		Assert.Equal("tex1qdestination", receivedByClient.ConfidentialDestinationAddress);
		Assert.Equal(new string('b', 64), receivedByClient.DestinationAssetIdHex);
		Assert.Equal(4_000, receivedByClient.DestinationAtomicUnits);
		Assert.Equal(100, receivedByClient.ExplicitFeeAtomicUnits);
		Assert.Equal(7UL, receivedByClient.ExpectedRevision);
		Assert.Equal(2, receivedByClient.PreviousTransactionIdsBySelectedInput.Count);
		Assert.Equal(new[] { selectedTransactionIdHex }, receivedByClient.PreviousTransactionIdsBySelectedInput[0]);
		Assert.Equal(new[] { new string('d', 64) }, receivedByClient.PreviousTransactionIdsBySelectedInput[1]);

		Assert.Same(expected, result);
	}

	// When no wallet session is open the executor forwards the request
	// unchanged; the landed command service's own fail-closed session
	// resolution is the rejection authority.
	[Fact]
	public async Task SessionExecutorWithoutOpenHandoffForwardsRequestUnchangedAsync()
	{
		string root = Path.Combine(Common.GetWorkDir(), "liquid-session-nohandoff");
		var session = new LiquidWalletSession(
			Path.Combine(root, "appdata"),
			Path.Combine(root, "wallets"));

		var request = new LiquidWalletUiSendExecutionRequest(
			"liquid-alpha",
			new[] { "aa" + new string('f', 70) },
			"tex1qdestination",
			new string('b', 64),
			4_000,
			100,
			expectedRevision: 7,
			new IReadOnlyList<string>?[] { null });

		LiquidWalletUiSendExecutionRequest? receivedByClient = null;
		var client = (LiquidWalletApplicationClient)RuntimeHelpers.GetUninitializedObject(typeof(LiquidWalletApplicationClient));
		SetField(client, "_sendCommand", (Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>)(
			(LiquidWalletUiSendExecutionRequest seen, CancellationToken _) =>
			{
				receivedByClient = seen;
				throw new InvalidOperationException("no open session");
			}));
		SetField(client, "_runtimeProvider", CreateHandoffProvider(handoff: null));
		SetField(session, "_client", client);

		InvalidOperationException rejection = await Assert.ThrowsAsync<InvalidOperationException>(
			() => session.ExecuteSendAsync(request, CancellationToken.None));
		Assert.Equal("no open session", rejection.Message);

		Assert.NotNull(receivedByClient);
		Assert.Same(request.WalletName, receivedByClient.WalletName);
		Assert.Null(receivedByClient.PreviousTransactionIdsBySelectedInput[0]);
	}

	// Builds a LiquidWalletModel over a state with the given balances, with the
	// shared next-receive script + blinding public key.
	private static LiquidWalletModel CreateModel(string name, long peggedAtomic, long? issuedAtomic = null)
	{
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset);
		ulong revision = 0;
		if (peggedAtomic > 0)
		{
			state = state.Apply(revision, Delta(Tx('f'), [], [Output(Tx('f'), 0, PeggedAsset, peggedAtomic)]));
			revision++;
		}

		if (issuedAtomic is { } issued && issued > 0)
		{
			state = state.Apply(revision, Delta(Tx('e'), [], [Output(Tx('e'), 0, IssuedAssetA, issued)]));
		}

		LiquidWalletUiSnapshot snapshot = LiquidWalletUiSnapshot.Capture(name, Manifest, state);
		return new LiquidWalletModel(name, Manifest, snapshot, ReceiveScript, BlindingKey);
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

	private static UiContext BuildUiContext(bool privacyMode)
	{
		var services = new StubServices(privacyMode);
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

	private static LiquidWalletSession BuildLiquidSession()
	{
		string root = Path.Combine(Common.GetWorkDir(), "liquid-session");
		return new LiquidWalletSession(
			Path.Combine(root, "appdata"),
			Path.Combine(root, "wallets"));
	}

	// A runtime provider carrying only the current handoff, for the session
	// executor delegation tests: no RPC, no sessions, no real provider
	// lifecycle. Mirrors the reflection-based construction in
	// LiquidWalletSendExecutionCommandServiceLifetimeTests (same assembly
	// internals via InternalsVisibleTo).
	private static LiquidAuthenticatedRuntimeProvider CreateHandoffProvider(LiquidWalletRuntimeHandoff? handoff)
	{
		var provider = (LiquidAuthenticatedRuntimeProvider)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedRuntimeProvider));
		SetField(provider, "_gate", new object());
		SetField(provider, "_currentHandoff", handoff);
		return provider;
	}

	private static void SetField(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	// Builds a sealed snapshot instance without running its constructor (the
	// landed types have private constructors): only the auto-property backing
	// fields the session executor reads are populated. Mirrors the
	// reflection-based construction in
	// LiquidWalletSendExecutionCommandServiceLifetimeTests.
	private static T CreateUninitialized<T>(params (string Name, object? Value)[] fields) where T : class
	{
		var instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
		foreach ((string name, object? value) in fields)
		{
			SetField(instance, $"<{name}>k__BackingField", value);
		}

		return instance;
	}

	private static LiquidWalletUiSnapshot CreateBalances(string walletName, ulong revision) =>
		CreateUninitialized<LiquidWalletUiSnapshot>(
			(nameof(LiquidWalletUiSnapshot.WalletName), walletName),
			(nameof(LiquidWalletUiSnapshot.NetworkManifestId), Manifest.ManifestId),
			(nameof(LiquidWalletUiSnapshot.PeggedAssetIdHex), Manifest.PeggedAssetId),
			(nameof(LiquidWalletUiSnapshot.Revision), revision));

	private static LiquidWalletUiSelectableOutputsSnapshot CreateSelectableOutputs(
		string walletName,
		ulong revision,
		params (string SelectionId, string TransactionIdHex)[] outputs) =>
		CreateUninitialized<LiquidWalletUiSelectableOutputsSnapshot>(
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.WalletName), walletName),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.NetworkManifestId), Manifest.ManifestId),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.PeggedAssetIdHex), Manifest.PeggedAssetId),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.Revision), revision),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.Outputs),
				outputs.Select(output => CreateUninitialized<LiquidWalletUiSelectableOutput>(
					(nameof(LiquidWalletUiSelectableOutput.SelectionId), output.SelectionId),
					(nameof(LiquidWalletUiSelectableOutput.TransactionIdHex), output.TransactionIdHex)))
				.ToArray()));

	private static LiquidWalletUiHistorySnapshot CreateHistory(string walletName, ulong revision) =>
		CreateUninitialized<LiquidWalletUiHistorySnapshot>(
			(nameof(LiquidWalletUiHistorySnapshot.WalletName), walletName),
			(nameof(LiquidWalletUiHistorySnapshot.NetworkManifestId), Manifest.ManifestId),
			(nameof(LiquidWalletUiHistorySnapshot.PeggedAssetIdHex), Manifest.PeggedAssetId),
			(nameof(LiquidWalletUiHistorySnapshot.Revision), revision));

	private static LiquidWalletUiReceiveMaterial CreateReceiveMaterial() =>
		new(new byte[] { 0x51 }, new byte[33]);

	// Minimal IServices stub, matching the one in
	// LiquidWalletHistoryPresentationTests exactly: real lightweight instances
	// for the members exercised during UiContext construction (EventBus, UiConfig,
	// PersistentConfig, Config, Network, and the simple getters); members never
	// touched by the code under test throw to surface any unexpected dependency.
	private sealed class StubServices : IServices
	{
		public StubServices(bool privacyMode)
		{
			string filePath = Path.Combine(Common.GetWorkDir(), "UiConfig.json");
			UiConfig = new UiConfig(filePath) { PrivacyMode = privacyMode };
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
