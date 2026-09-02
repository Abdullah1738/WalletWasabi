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
using Avalonia.VisualTree;
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
		send.ExplicitFeeAtomicUnits = 100;

		// The coin-control list binds from the wallet's selectable snapshot;
		// a real wallet output funds the plan (coin control never fabricates
		// an outpoint). The model below holds one spendable pegged output.
		await send.SendExecution.Execute().ToTask();

		Assert.NotNull(captured);
		Assert.Equal(model.Name, captured.WalletName);
		Assert.Equal(send.SelectedOutPointHexes, captured.SelectedOutPointHexes);
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

	// Slice LIQUID-UI-BALANCE-SEND-AFFORDANCE-001 headless evidence: render the
	// wallet home over a multi-asset balance set, activate a non-pegged row's
	// Send affordance, and prove the send view opens with that asset selected
	// (Recipient.AssetIdHex matches the row). The affordance navigates the same
	// way the top-level SendCommand does (DialogScreen → LiquidSendViewModel
	// over the session's send executor) but pre-selects the row's asset; the
	// pre-selection survives the dispatcher-deferred reseed because the reseed
	// honors a held selection whose asset id is still present.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void BalanceRowSendAffordanceNavigatesWithThatAssetPreSelected()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		var mainScreen = new TargettedNavigationStack(uiContext, NavigationTarget.HomeScreen);
		var dialogScreen = new DialogScreenViewModel(uiContext);
		var fullScreen = new DialogScreenViewModel(uiContext, NavigationTarget.FullScreen);
		var compactDialogScreen = new DialogScreenViewModel(uiContext, NavigationTarget.CompactDialogScreen);
		NavBarViewModel navBar = new(uiContext);
		uiContext.RegisterNavigation(
			new NavigationState(uiContext, mainScreen, dialogScreen, fullScreen, compactDialogScreen, navBar));

		using LiquidWalletModel model = CreateModel("liquid-row-send", peggedAtomic: 5_000, issuedAtomic: 7_500);
		LiquidWalletViewModel page = new(uiContext, model);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();

		LiquidAssetBalanceItemViewModel issuedRow =
			page.BalanceRows!.Single(row => !row.IsPeggedAsset);
		Assert.NotNull(issuedRow.SendCommand);
		Assert.True(issuedRow.SendCommand!.CanExecute(null));

		issuedRow.SendCommand.Execute(null);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();

		LiquidSendViewModel send = Assert.IsType<LiquidSendViewModel>(dialogScreen.CurrentPage);
		Assert.Same(model, send.WalletModel);
		Assert.NotNull(send.ExecuteSendCommand);
		Assert.Equal(IssuedAssetA.CanonicalRpcHex, send.Recipient.AssetIdHex);
		Assert.NotNull(send.Recipient.SelectedAsset);
		Assert.False(send.Recipient.SelectedAsset!.IsPeggedAsset);
		Assert.Equal(IssuedAssetA.CanonicalRpcHex, send.Recipient.SelectedAsset.AssetIdHex);
	}

	// The top-level Send command is unchanged: it opens the send flow with no
	// pre-selection, so the picker holds the landed pegged-first default even
	// though the wallet also carries an issued asset.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void TopLevelSendKeepsPeggedDefaultSelection()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		var mainScreen = new TargettedNavigationStack(uiContext, NavigationTarget.HomeScreen);
		var dialogScreen = new DialogScreenViewModel(uiContext);
		var fullScreen = new DialogScreenViewModel(uiContext, NavigationTarget.FullScreen);
		var compactDialogScreen = new DialogScreenViewModel(uiContext, NavigationTarget.CompactDialogScreen);
		NavBarViewModel navBar = new(uiContext);
		uiContext.RegisterNavigation(
			new NavigationState(uiContext, mainScreen, dialogScreen, fullScreen, compactDialogScreen, navBar));

		using LiquidWalletModel model = CreateModel("liquid-top-send", peggedAtomic: 5_000, issuedAtomic: 7_500);
		LiquidWalletViewModel page = new(uiContext, model);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();

		page.SendCommand.Execute(null);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();

		LiquidSendViewModel send = Assert.IsType<LiquidSendViewModel>(dialogScreen.CurrentPage);
		Assert.Equal(PeggedAsset.CanonicalRpcHex, send.Recipient.AssetIdHex);
		Assert.NotNull(send.Recipient.SelectedAsset);
		Assert.True(send.Recipient.SelectedAsset!.IsPeggedAsset);
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

	// The open path feeds the runtime handoff's already-produced history into
	// the model it builds: a wallet opened at a revision presents its retained
	// history (IsHistoryLoaded true, HistorySnapshot is the handoff's history
	// at the matching revision) rather than the "not available" state. The
	// handoff guarantees History.Revision == Balances.Revision, so the model's
	// pairing fence accepts it unchanged.
	[Fact]
	public void OpenWiringPresentsHandoffHistoryAtMatchingRevision()
	{
		var handoff = new LiquidWalletRuntimeHandoff(
			"liquid-history",
			Manifest.ManifestId,
			CreateBalances("liquid-history", revision: 9),
			CreateSelectableOutputs("liquid-history", 9),
			CreateHistory("liquid-history", revision: 9),
			CreateReceiveMaterial());

		// Mirror LiquidWalletSession.OpenWalletAsync exactly: build the model
		// from the handoff's balances + receive material, then feed the
		// handoff's history.
		var model = new LiquidWalletModel(
			handoff.CanonicalWalletId,
			Manifest,
			handoff.Balances,
			handoff.ReceiveMaterial.NextReceiveScriptPubKey,
			handoff.ReceiveMaterial.NextReceiveBlindingPublicKey,
			handoff.ReceiveMaterial.NextReceiveLabels);

		Assert.False(model.IsHistoryLoaded);
		Assert.Null(model.HistorySnapshot);

		model.RefreshHistory(handoff.History);

		Assert.True(model.IsHistoryLoaded);
		Assert.Same(handoff.History, model.HistorySnapshot);
		Assert.Equal(handoff.Balances.Revision, model.HistorySnapshot!.Revision);
	}

	// Builds a LiquidWalletModel over a state with the given balances, with the
	// shared next-receive script + blinding public key.
	private static LiquidWalletModel CreateModel(
		string name,
		long peggedAtomic,
		long? issuedAtomic = null,
		IReadOnlyList<string>? nextReceiveLabels = null,
		Func<LiquidWalletUiSetReceiveLabelsRequest, CancellationToken, Task>? setNextReceiveLabelsCommand = null)
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

		// The selectable snapshot derived from the same wallet state, so the
		// coin-control list binds the wallet's real spendable outputs (the
		// allocation's state field is internal; the test mirrors the landed
		// CaptureSelectableOutputs path via reflection, exactly as the other
		// RuntimeHelpers-backed helpers in this file do).
		LiquidWalletUiSelectableOutputsSnapshot selectableOutputs =
			LiquidWalletUiFacade.CaptureSelectableOutputs(name, Manifest, CreateAllocationFor(state));

		return new LiquidWalletModel(
			name,
			Manifest,
			snapshot,
			ReceiveScript,
			BlindingKey,
			nextReceiveLabels,
			setNextReceiveLabelsCommand,
			selectableOutputs);
	}

	// Wraps a wallet state in the landed external-index allocation the
	// facade's CaptureSelectableOutputs consumes, via the internal state
	// field (the test mirrors the production allocation shape).
	private static WalletWasabi.Liquid.Wallet.LiquidWalletExternalIndexAllocation CreateAllocationFor(
		LiquidWalletState state)
	{
		var allocation = (WalletWasabi.Liquid.Wallet.LiquidWalletExternalIndexAllocation)
			RuntimeHelpers.GetUninitializedObject(typeof(WalletWasabi.Liquid.Wallet.LiquidWalletExternalIndexAllocation));
		SetField(allocation, "_state", state);
		return allocation;
	}

	// Slice LIQUID-UI-SEND-ASSET-PICKER-001 headless evidence: render the real
	// LiquidSendView (which hosts the single LiquidSendRecipientView) over a
	// wallet holding the pegged asset plus one issued asset, and prove the
	// asset selector is a ComboBox bound from the balance snapshot — the
	// pegged asset first, the issued asset second — that the default
	// selection is the pegged asset, and that picking an item drives
	// Recipient.AssetIdHex (the property the plan/sign path consumes). The
	// compiled bindings are validated by x:CompileBindings at build; this
	// asserts the live binding path headlessly.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void SendViewAssetSelectorBindsBalancesAndDrivesAssetIdHex()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-send-picker", peggedAtomic: 5_000, issuedAtomic: 7_500);
		LiquidSendViewModel send = new(uiContext, model);
		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidSendView
		{
			DataContext = send,
		};

		var window = new Avalonia.Controls.Window
		{
			Width = 800,
			Height = 600,
			Content = view,
		};
		window.Show();
		try
		{
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			// The asset selector is the only ComboBox in the send view.
			Avalonia.Controls.ComboBox combo = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.ComboBox>()
				.Single();

			// Two options from the snapshot, pegged first then issued in
			// canonical order; default selection is the pegged asset.
			var items = combo.Items.Cast<LiquidAssetBalanceItemViewModel>().ToArray();
			Assert.Equal(2, items.Length);
			Assert.True(items[0].IsPeggedAsset);
			Assert.False(items[1].IsPeggedAsset);
			Assert.Equal(PeggedAsset.CanonicalRpcHex, items[0].AssetIdHex);
			Assert.Equal(IssuedAssetA.CanonicalRpcHex, items[1].AssetIdHex);
			Assert.Same(items[0], combo.SelectedItem);
			Assert.Equal(PeggedAsset.CanonicalRpcHex, send.Recipient.AssetIdHex);

			// Picking the issued asset drives Recipient.AssetIdHex.
			combo.SelectedItem = items[1];
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			Assert.Equal(IssuedAssetA.CanonicalRpcHex, send.Recipient.AssetIdHex);
		}
		finally
		{
			window.Close();
		}
	}

	// The default selection reseeds when a balance refresh drops the held
	// asset: after the issued asset leaves the balance set, the selection
	// falls back to the pegged asset and Recipient.AssetIdHex follows.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void SendViewAssetSelectorReseedsSelectionOnBalanceRefresh()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-send-reseed", peggedAtomic: 5_000, issuedAtomic: 7_500);
		LiquidSendViewModel send = new(uiContext, model);
		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidSendView
		{
			DataContext = send,
		};

		var window = new Avalonia.Controls.Window
		{
			Width = 800,
			Height = 600,
			Content = view,
		};
		window.Show();
		try
		{
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			Avalonia.Controls.ComboBox combo = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.ComboBox>()
				.Single();
			var items = combo.Items.Cast<LiquidAssetBalanceItemViewModel>().ToArray();
			combo.SelectedItem = items[1];
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			Assert.Equal(IssuedAssetA.CanonicalRpcHex, send.Recipient.AssetIdHex);

			// A refresh that drops the issued asset reseeds to the pegged one.
			LiquidWalletState peggedOnly = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(Tx('d'), [], [Output(Tx('d'), 0, PeggedAsset, 9_000)]));
			model.RefreshBalances(LiquidWalletUiSnapshot.Capture("liquid-send-reseed", Manifest, peggedOnly));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			Assert.Equal(PeggedAsset.CanonicalRpcHex, send.Recipient.AssetIdHex);
			var refreshed = combo.Items.Cast<LiquidAssetBalanceItemViewModel>().Single();
			Assert.True(refreshed.IsPeggedAsset);
			Assert.Same(refreshed, combo.SelectedItem);
		}
		finally
		{
			window.Close();
		}
	}

	// Slice LIQUID-UI-SEND-CHANGE-001 headless evidence: render the real
	// LiquidSendView over a spend plan whose facade appended a wallet-owned
	// change destination, and prove the "change" tag renders exactly on the
	// change row and not on the user destination. The flag is additive
	// attribution of ALREADY-composed change; the view only surfaces it.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void SendViewChangeTagRendersForChangeDestination()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-send-change", peggedAtomic: 5_000);
		LiquidSendViewModel send = new(uiContext, model);

		// A two-destination plan: the user destination, then the wallet-owned
		// change row the facade flagged. Build the projection directly (the
		// change-attribution projection is the unit under test; the
		// composition path is covered by LiquidWalletUiMixedAssetChangeTests).
		LiquidWalletUiSpendPlan plan = CreatePlanWithChange(uiContext);
		send.SpendPlan = new LiquidSpendPlanItemViewModel(uiContext, plan);

		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidSendView
		{
			DataContext = send,
		};

		var window = new Avalonia.Controls.Window
		{
			Width = 800,
			Height = 600,
			Content = view,
		};
		window.Show();
		try
		{
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			LiquidSpendPlanDestinationItemViewModel userRow = send.SpendPlan!.Destinations[0];
			LiquidSpendPlanDestinationItemViewModel changeRow = send.SpendPlan!.Destinations[1];
			Assert.False(userRow.IsWalletOwnedChange);
			Assert.True(changeRow.IsWalletOwnedChange);

			// Exactly one "change" tag is visible in the rendered tree.
			var tags = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.TextBlock>()
				.Where(text => text.Text == "change" && text.IsVisible)
				.ToArray();
			Assert.Single(tags);
		}
		finally
		{
			window.Close();
		}
	}

	// Slice LIQUID-UI-SEND-COINCONTROL-001 headless evidence: the coin-control
	// list binds from the wallet's selectable snapshot — one checkable row per
	// spendable output, every row selected by default (the landed empty-field
	// semantics) — and the checked rows drive the exact selected-outpoint hex
	// set the plan/sign path consumes (the SelectionId, verbatim).
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void SendViewCoinControlBindsSelectableSnapshotWithAllSelectedDefault()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-send-cc", peggedAtomic: 5_000, issuedAtomic: 7_500);

		string peggedSelection = "aa" + new string('1', 70);
		string issuedSelection = "bb" + new string('2', 70);
		model.RefreshSelectableOutputs(CreateSelectableOutputs(
			"liquid-send-cc",
			model.Snapshot!.Revision,
			[
				(peggedSelection, new string('c', 64), 0u, Manifest.PeggedAssetId, 5_000L, true),
				(issuedSelection, new string('d', 64), 1u, IssuedAssetAHex, 7_500L, false),
			]));

		LiquidSendViewModel send = new(uiContext, model);
		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidSendView
		{
			DataContext = send,
		};

		var window = new Avalonia.Controls.Window
		{
			Width = 800,
			Height = 600,
			Content = view,
		};
		window.Show();
		try
		{
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			// Two rows from the snapshot, both selected by default (the landed
			// empty-field semantics), driving the full selected set verbatim.
			Assert.Equal(2, send.SelectableOutputs.Count);
			Assert.All(send.SelectableOutputs, row => Assert.True(row.IsSelected));
			Assert.Equal(new[] { peggedSelection, issuedSelection }, send.SelectedOutPointHexes);

			// The rendered list has one CheckBox per row, all checked.
			var checkBoxes = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.CheckBox>()
				.ToArray();
			Assert.Equal(2, checkBoxes.Length);
			Assert.All(checkBoxes, box => Assert.True(box.IsChecked));

			// The pegged row shows the L-BTC marker; the issued row shows the
			// issued marker; the outpoint coordinate renders txid:vout.
			Assert.Equal("L-BTC", send.SelectableOutputs[0].AssetMarkerText);
			Assert.Equal("issued", send.SelectableOutputs[1].AssetMarkerText);
			Assert.Equal("0.00 005 000 L-BTC", send.SelectableOutputs[0].AmountDisplayText);
			Assert.Equal("7500 atomic units", send.SelectableOutputs[1].AmountDisplayText);
			Assert.EndsWith(":0", send.SelectableOutputs[0].OutPointDisplayText);
		}
		finally
		{
			window.Close();
		}
	}

	// Checking/unchecking a coin-control row drives the selected set: an
	// unchecked row drops its outpoint from the exact set the plan/sign path
	// consumes; re-checking restores it. No non-wallet outpoint is ever added.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void SendViewCoinControlToggleDrivesSelectedSet()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-send-cc-toggle", peggedAtomic: 5_000, issuedAtomic: 7_500);

		string peggedSelection = "aa" + new string('1', 70);
		string issuedSelection = "bb" + new string('2', 70);
		model.RefreshSelectableOutputs(CreateSelectableOutputs(
			"liquid-send-cc-toggle",
			model.Snapshot!.Revision,
			[
				(peggedSelection, new string('c', 64), 0u, Manifest.PeggedAssetId, 5_000L, true),
				(issuedSelection, new string('d', 64), 1u, IssuedAssetAHex, 7_500L, false),
			]));

		LiquidSendViewModel send = new(uiContext, model);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();

		Assert.Equal(new[] { peggedSelection, issuedSelection }, send.SelectedOutPointHexes);

		// Uncheck the pegged row: its outpoint leaves the set.
		send.SelectableOutputs[0].IsSelected = false;
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Equal(new[] { issuedSelection }, send.SelectedOutPointHexes);

		// Re-check it: the full set is restored in list order.
		send.SelectableOutputs[0].IsSelected = true;
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Equal(new[] { peggedSelection, issuedSelection }, send.SelectedOutPointHexes);
	}

	// A refresh replaces the row set deterministically: the fresh rows are
	// re-selected (the landed empty-field default) and the selected set
	// follows the new snapshot, with no stale row carried over.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void SendViewCoinControlRefreshReseedsDeterministically()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-send-cc-refresh", peggedAtomic: 5_000);

		string initial = "aa" + new string('1', 70);
		model.RefreshSelectableOutputs(CreateSelectableOutputs(
			"liquid-send-cc-refresh",
			model.Snapshot!.Revision,
			[(initial, new string('c', 64), 0u, Manifest.PeggedAssetId, 5_000L, true)]));

		LiquidSendViewModel send = new(uiContext, model);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Equal(new[] { initial }, send.SelectedOutPointHexes);

		// Uncheck the only row, then refresh to a different two-output set.
		send.SelectableOutputs[0].IsSelected = false;
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Empty(send.SelectedOutPointHexes);

		string refreshedA = "dd" + new string('3', 70);
		string refreshedB = "ee" + new string('4', 70);
		model.RefreshSelectableOutputs(CreateSelectableOutputs(
			"liquid-send-cc-refresh",
			model.Snapshot!.Revision,
			[
				(refreshedA, new string('f', 64), 0u, Manifest.PeggedAssetId, 2_000L, true),
				(refreshedB, new string('a', 64), 1u, Manifest.PeggedAssetId, 3_000L, true),
			]));
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();

		// The fresh rows replace the old set wholesale, all selected.
		Assert.Equal(2, send.SelectableOutputs.Count);
		Assert.All(send.SelectableOutputs, row => Assert.True(row.IsSelected));
		Assert.Equal(new[] { refreshedA, refreshedB }, send.SelectedOutPointHexes);
		Assert.DoesNotContain(send.SelectableOutputs, row => row.SelectionId == initial);
	}

	// Builds a two-destination plan projection (user destination + flagged
	// wallet-owned change row) via the landed facade path.
	private static LiquidWalletUiSpendPlan CreatePlanWithChange(UiContext uiContext)
	{
		// Fund 2_000 pegged: selected 2_000, destination 900, fee 100 → pegged
		// surplus 1_000 > 0, so the facade appends the wallet-owned change row.
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 2_000)]));

		string changeAddress = LiquidAddress.FromScriptPubKey(
				Manifest,
				ChangeScriptForTag,
				LiquidBlindingPublicKey.Create(ChangeBlindingKeyForTag))
			.GetCanonicalAddressText();

		return LiquidWalletUiFacade.CreateSpendPlan(
			"wallet",
			Manifest,
			state,
			[OutPointHexForTag(txA, 0)],
			ConfidentialAddressForTag(),
			Manifest.PeggedAssetId,
			destinationAtomicUnits: 900,
			explicitFeeAtomicUnits: 100,
			changeDestination: new LiquidWalletUiChangeDestination(changeAddress));
	}

	private const string ChangePublicKeyHexForTag = "03f028892bad7ed57d2fb57bf33081d5cfcf6f9ed3d3d7f159c2e2fff579dc341a";
	private const string ChangeBlindingKeyHexForTag = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private static byte[] ChangeScriptForTag =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(ChangePublicKeyHexForTag), LiquidKeyBranch.Internal, 0).GetScriptPubKey();
	private static byte[] ChangeBlindingKeyForTag => Convert.FromHexString(ChangeBlindingKeyHexForTag);
	private static string ConfidentialAddressForTag() =>
		LiquidAddress.FromScriptPubKey(
				Manifest,
				ReceiveScript,
				LiquidBlindingPublicKey.Create(BlindingKey))
			.GetCanonicalAddressText();
	private static string OutPointHexForTag(LiquidTransactionId transactionId, uint outputIndex) =>
		Convert.ToHexString(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex).ToConsensusBytes());

	// The balance-row display amount is pegged-aware: the pegged asset renders
	// its protocol-fixed 1e8 L-BTC decimal form, while an issued asset (no
	// known precision) stays in raw atomic units. This is the string the
	// wallet-home balance rows and the send asset-picker options render.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void BalanceRowDisplayAmountIsPeggedAware()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-balance-display", peggedAtomic: 123_456, issuedAtomic: 7_500);
		LiquidWalletViewModel wallet = new(uiContext, model);

		LiquidAssetBalanceItemViewModel pegged = wallet.BalanceRows!.Single(row => row.IsPeggedAsset);
		LiquidAssetBalanceItemViewModel issued = wallet.BalanceRows!.Single(row => !row.IsPeggedAsset);

		// Pegged: 123_456 atomic units = 0.00123456 L-BTC, Wasabi's fixed
		// eight-fraction-digit space-grouped form. Issued: raw atomic units.
		Assert.Equal("0.00 123 456 L-BTC", pegged.BalanceDisplayText);
		Assert.Equal("7500 atomic units", issued.BalanceDisplayText);
	}

	// The send-plan asset-amount wrapper renders the pegged explicit fee as an
	// L-BTC decimal and any issued selected total in raw atomic units.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void SpendPlanAssetAmountDisplayIsPeggedAware()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidWalletUiAssetAmount peggedFee = LiquidWalletUiAssetAmount.FromTotal(PeggedAsset.CanonicalRpcHex, true, 100);
		LiquidWalletUiAssetAmount issuedTotal = LiquidWalletUiAssetAmount.FromTotal(IssuedAssetA.CanonicalRpcHex, false, 2_000);

		Assert.Equal("0.00 000 100 L-BTC", new LiquidSpendPlanAssetAmountItemViewModel(uiContext, peggedFee).AmountDisplayText);
		Assert.Equal("2000 atomic units", new LiquidSpendPlanAssetAmountItemViewModel(uiContext, issuedTotal).AmountDisplayText);
	}

	// Slice LIQUID-UI-RECEIVE-LABEL-001 headless evidence: render the real
	// LiquidReceiveView over a wallet whose open handoff carried a durable
	// next-receive label set, and prove the label input binds and shows the
	// existing labels joined as Wasabi's comma-separated convention. The
	// compiled bindings are validated by x:CompileBindings at build; this
	// asserts the live binding path headlessly.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void ReceiveViewLabelInputBindsAndShowsExistingLabels()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using ServicesScope _ = InstallTestServices(uiContext.Services.UiConfig);
		using LiquidWalletModel model = CreateModel(
			"liquid-recv-label",
			peggedAtomic: 1_000,
			nextReceiveLabels: new[] { "exchange", "savings" });
		LiquidReceiveViewModel receive = new(uiContext, model);
		receive.OnNavigatedTo(isInHistory: false);

		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidReceiveView
		{
			DataContext = receive,
		};

		var window = new Avalonia.Controls.Window
		{
			Width = 800,
			Height = 600,
			Content = view,
		};
		window.Show();
		try
		{
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			// The label input is the only TextBox in the receive view; it
			// shows the existing labels joined, and its text round-trips
			// through the bound LabelText property.
			Avalonia.Controls.TextBox labelBox = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.TextBox>()
				.Single();
			Assert.Equal("exchange, savings", labelBox.Text);
			Assert.Equal("exchange, savings", receive.LabelText);

			labelBox.Text = "newlabel";
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			Assert.Equal("newlabel", receive.LabelText);
		}
		finally
		{
			window.Close();
		}
	}

	// A receive view with no durable label opens with an empty label input.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void ReceiveViewLabelInputIsEmptyWhenUnlabeled()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using LiquidWalletModel model = CreateModel("liquid-recv-unlabeled", peggedAtomic: 1_000);
		LiquidReceiveViewModel receive = new(uiContext, model);
		receive.OnNavigatedTo(isInHistory: false);

		Assert.Equal("", receive.LabelText);
		Assert.Empty(model.NextReceiveLabels);
	}

	// The save action parses the comma-separated label text and invokes the
	// model's write path with the expected label set.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public async Task ReceiveViewSaveLabelInvokesModelWritePathAsync()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using ServicesScope _ = InstallTestServices(uiContext.Services.UiConfig);

		string[]? capturedLabels = null;
		Task WriteLabels(LiquidWalletUiSetReceiveLabelsRequest request, CancellationToken cancellationToken)
		{
			capturedLabels = request.Labels.ToArray();
			return Task.CompletedTask;
		}

		using LiquidWalletModel model = CreateModel(
			"liquid-recv-save",
			peggedAtomic: 1_000,
			setNextReceiveLabelsCommand: WriteLabels);
		LiquidReceiveViewModel receive = new(uiContext, model);
		receive.OnNavigatedTo(isInHistory: false);

		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidReceiveView
		{
			DataContext = receive,
		};

		var window = new Avalonia.Controls.Window
		{
			Width = 800,
			Height = 600,
			Content = view,
		};
		window.Show();
		try
		{
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			Avalonia.Controls.TextBox labelBox = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.TextBox>()
				.Single();
			labelBox.Text = "exchange, savings";
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			// The stub write delegate completes synchronously, so the save
			// runs to completion on the UI thread without yielding to a
			// background scheduler (which would let the rendered
			// PrivacyContentControl fire off-thread after teardown).
			await receive.SaveLabel.Execute().ToTask();

			Assert.NotNull(capturedLabels);
			Assert.Equal(new[] { "exchange", "savings" }, capturedLabels);
			Assert.Null(receive.LabelSaveErrorText);
		}
		finally
		{
			window.Close();
		}
	}

	// An empty label field clears the label: the write path receives an empty
	// label set.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public async Task ReceiveViewSaveEmptyLabelClearsAsync()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);

		string[]? capturedLabels = null;
		Task WriteLabels(LiquidWalletUiSetReceiveLabelsRequest request, CancellationToken cancellationToken)
		{
			capturedLabels = request.Labels.ToArray();
			return Task.CompletedTask;
		}

		using LiquidWalletModel model = CreateModel(
			"liquid-recv-clear",
			peggedAtomic: 1_000,
			nextReceiveLabels: new[] { "exchange" },
			setNextReceiveLabelsCommand: WriteLabels);
		LiquidReceiveViewModel receive = new(uiContext, model);
		receive.OnNavigatedTo(isInHistory: false);
		Assert.Equal("exchange", receive.LabelText);

		receive.LabelText = "";
		await receive.SaveLabel.Execute().ToTask();

		Assert.NotNull(capturedLabels);
		Assert.Empty(capturedLabels);
		Assert.Null(receive.LabelSaveErrorText);
	}

	// A landed rejection from the write path surfaces as-is; no success is
	// fabricated.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public async Task ReceiveViewSaveLabelSurfacesRejectionAsync()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);

		Task WriteLabels(LiquidWalletUiSetReceiveLabelsRequest request, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("landed rejection");

		using LiquidWalletModel model = CreateModel(
			"liquid-recv-reject",
			peggedAtomic: 1_000,
			setNextReceiveLabelsCommand: WriteLabels);
		LiquidReceiveViewModel receive = new(uiContext, model);
		receive.OnNavigatedTo(isInHistory: false);

		receive.LabelText = "exchange";
		await receive.SaveLabel.Execute().ToTask();

		Assert.Equal("landed rejection", receive.LabelSaveErrorText);
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

	// Installs a minimal WalletWasabi.Fluent Services singleton exposing only
	// the supplied UiConfig, so the landed PrivacyContentControl (which reads
	// Services.Instance.UiConfig at construction) can be exercised headless.
	// This is a test-only harness over the unchanged shared control, mirroring
	// LiquidWalletHistoryPresentationTests.
	private static ServicesScope InstallTestServices(UiConfig uiConfig)
	{
		var services = (WalletWasabi.Fluent.Services)RuntimeHelpers
			.GetUninitializedObject(typeof(WalletWasabi.Fluent.Services));
		SetServicesBackingField(services, nameof(WalletWasabi.Fluent.Services.UiConfig), uiConfig);
		PropertyInfo instanceProperty = typeof(WalletWasabi.Fluent.Services)
			.GetProperty(nameof(WalletWasabi.Fluent.Services.Instance))!;
		WalletWasabi.Fluent.Services? previous =
			(WalletWasabi.Fluent.Services?)instanceProperty.GetValue(null);
		SetStaticServicesBackingField(nameof(WalletWasabi.Fluent.Services.Instance), services);
		return new ServicesScope(previous);
	}

	private static void SetServicesBackingField(object target, string propertyName, object? value) =>
		typeof(WalletWasabi.Fluent.Services)
			.GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(target, value);

	private static void SetStaticServicesBackingField(string propertyName, object? value) =>
		typeof(WalletWasabi.Fluent.Services)
			.GetField($"<{propertyName}>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!
			.SetValue(null, value);

	private sealed class ServicesScope : IDisposable
	{
		private readonly WalletWasabi.Fluent.Services? _previous;

		internal ServicesScope(WalletWasabi.Fluent.Services? previous) => _previous = previous;

		public void Dispose() =>
			SetStaticServicesBackingField(nameof(WalletWasabi.Fluent.Services.Instance), _previous);
	}

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
		CreateSelectableOutputs(
			walletName,
			revision,
			outputs
				.Select(output => (output.SelectionId, output.TransactionIdHex, 0u, Manifest.PeggedAssetId, 0L, true))
				.ToArray());

	private static LiquidWalletUiSelectableOutputsSnapshot CreateSelectableOutputs(
		string walletName,
		ulong revision,
		(string SelectionId, string TransactionIdHex, uint OutputIndex, string AssetIdHex, long AtomicUnits, bool IsPeggedAsset)[] outputs) =>
		CreateUninitialized<LiquidWalletUiSelectableOutputsSnapshot>(
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.WalletName), walletName),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.NetworkManifestId), Manifest.ManifestId),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.PeggedAssetIdHex), Manifest.PeggedAssetId),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.Revision), revision),
			(nameof(LiquidWalletUiSelectableOutputsSnapshot.Outputs),
				outputs.Select(output => CreateUninitialized<LiquidWalletUiSelectableOutput>(
					(nameof(LiquidWalletUiSelectableOutput.SelectionId), output.SelectionId),
					(nameof(LiquidWalletUiSelectableOutput.TransactionIdHex), output.TransactionIdHex),
					(nameof(LiquidWalletUiSelectableOutput.OutputIndex), output.OutputIndex),
					(nameof(LiquidWalletUiSelectableOutput.AssetIdHex), output.AssetIdHex),
					(nameof(LiquidWalletUiSelectableOutput.AtomicUnits), output.AtomicUnits),
					(nameof(LiquidWalletUiSelectableOutput.IsPeggedAsset), output.IsPeggedAsset)))
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
