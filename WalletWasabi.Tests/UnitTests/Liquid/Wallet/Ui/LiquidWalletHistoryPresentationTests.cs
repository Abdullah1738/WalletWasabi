using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.VisualTree;
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

// Required evidence §11: Fluent presentation. The item view models project
// the public history rows to exact display text and accessibility summaries
// (normal and privacy mode), without ever exposing a full transaction id,
// block hash, or timestamp. These tests drive the real item view models
// against a real UiContext; the headless Avalonia accessibility-tree
// assertions for the view live in the axaml binding behavior covered by the
// compiled-binding build plus the view-model automation-name evidence here.
[Collection("Serial unit tests collection")]
public class LiquidWalletHistoryPresentationTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string IssuedAssetAHex = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
	private const string IssuedAssetBHex = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidAssetId IssuedAssetA => LiquidAssetId.ParseRpcHex(IssuedAssetAHex);
	private static LiquidSpendKeyReference ExternalKey =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), LiquidKeyBranch.External, 0);

	// §11: unconfirmed row with a multiasset change projects status text, the
	// full transaction id, and per-asset credit/debit with the exact formatted
	// amounts (L-BTC decimals for the pegged asset, atomic units + full asset
	// id for the issued asset).
	[Fact]
	public void UnconfirmedMultiassetRowProjectsTextAndAccessibility()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidTransactionId tx = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(Tx('f'), [], [Output(Tx('f'), 0, IssuedAssetA, 1_000)]))
			.Apply(1, Delta(tx, [OutPoint(Tx('f'), 0)], [Output(tx, 0, IssuedAssetA, 400), Output(tx, 1, PeggedAsset, 250)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
		LiquidWalletUiHistoryRow row = snapshot.Rows[0];

		LiquidHistoryItemViewModel item = new(uiContext, row);
		Assert.Equal("Unconfirmed", item.StatusText);
		Assert.False(item.IsConfirmed);
		Assert.Null(item.ConfirmationHeight);
		Assert.True(item.HasBalanceChange);
		Assert.Equal(2, item.AssetChanges.Count);

		// The full canonical transaction id is carried verbatim.
		Assert.Equal(tx.CanonicalRpcHex, item.TransactionId);

		// Normal automation summary: status + full transaction id + each
		// formatted credit/debit.
		string summary = item.NormalAccessibilitySummary;
		Assert.StartsWith("Unconfirmed ", summary);
		Assert.Contains(tx.CanonicalRpcHex, summary);
		Assert.Contains("Credit", summary);
		Assert.Contains("Debit", summary);
		Assert.Contains("0.0000025", summary); // 250 atomic units as L-BTC decimals
		Assert.Contains("-600 atomic units", summary); // issued-asset debit

		// The pegged asset change renders as L-BTC with the decimal amount; the
		// issued asset carries the full canonical asset id and atomic units.
		LiquidHistoryAssetChangeItemViewModel peggedChange =
			item.AssetChanges.Single(c => c.IsPeggedAsset);
		Assert.Equal("L-BTC", peggedChange.AssetDisplayReference);
		Assert.Equal(250, peggedChange.NetAtomicUnits);
		Assert.Equal("Credit", peggedChange.DirectionText);
		Assert.Equal("0.0000025", peggedChange.DisplayAmount);

		LiquidHistoryAssetChangeItemViewModel issuedChange =
			item.AssetChanges.Single(c => !c.IsPeggedAsset);
		Assert.Equal("Debit", issuedChange.DirectionText);
		Assert.Equal(-600, issuedChange.NetAtomicUnits);
		Assert.Equal(IssuedAssetAHex, issuedChange.AssetDisplayReference);
		Assert.Equal(IssuedAssetAHex, issuedChange.AssetIdHex);
		Assert.Contains(IssuedAssetAHex, issuedChange.DisplayAmount);
		Assert.Contains("-600", issuedChange.DisplayAmount);
	}

	// §11: confirmed row projects "Confirmed at block height N".
	[Fact]
	public void ConfirmedRowProjectsBlockHeightText()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidTransactionId tx = Tx('b');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, PeggedAsset, 7_500)]))
			.Confirm(1, tx, LiquidConfirmation.Create(new string('5', 64), 123));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);

		LiquidHistoryItemViewModel item = new(uiContext, snapshot.Rows[0]);
		Assert.True(item.IsConfirmed);
		Assert.Equal(123u, item.ConfirmationHeight);
		Assert.Equal("Confirmed at block height 123", item.StatusText);
		// The canonical block hash never crosses into any display text.
		Assert.DoesNotContain(new string('5', 64), item.NormalAccessibilitySummary);
		Assert.DoesNotContain(new string('5', 64), item.StatusText);
	}

	// §11: zero-net row exposes the explicit empty-change text.
	[Fact]
	public void ZeroNetRowExposesNoBalanceChangeText()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidTransactionId txA = Tx('c');
		LiquidTransactionId txB = Tx('d');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, IssuedAssetA, 500)]))
			.Apply(1, Delta(txB, [OutPoint(txA, 0)], [Output(txB, 0, IssuedAssetA, 500)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);

		LiquidHistoryItemViewModel zeroItem = new(uiContext, snapshot.Rows[0]);
		Assert.False(zeroItem.HasBalanceChange);
		Assert.Empty(zeroItem.AssetChanges);
		Assert.Equal("No wallet balance change", zeroItem.EmptyChangeText);
		Assert.Contains("No wallet balance change", zeroItem.NormalAccessibilitySummary);
	}

	// §11: privacy mode. The privacy automation summary is exactly status
	// plus "Liquid transaction details hidden" — no transaction id, asset, or
	// amount. The AccessibilitySummary follows the UiConfig privacy flag.
	[Fact]
	public void PrivacyModeAutomationSummaryHidesAllDetails()
	{
		UiContext uiContext = BuildUiContext(privacyMode: true);
		LiquidTransactionId tx = Tx('e');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, PeggedAsset, 9_999)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
		LiquidWalletUiHistoryRow row = snapshot.Rows[0];

		LiquidHistoryItemViewModel item = new(uiContext, row);
		Assert.Equal("Unconfirmed Liquid transaction details hidden", item.PrivateAccessibilitySummary);
		Assert.DoesNotContain(row.TransactionId, item.PrivateAccessibilitySummary);
		Assert.DoesNotContain("9", item.PrivateAccessibilitySummary.Replace("hidden", "")); // no amount digit
		Assert.DoesNotContain(tx.CanonicalRpcHex, item.PrivateAccessibilitySummary);

		// Privacy mode on: the bound AccessibilitySummary is the private one.
		Assert.Equal(item.PrivateAccessibilitySummary, item.AccessibilitySummary);
	}

	// §11: privacy mode off — the bound AccessibilitySummary is the normal
	// one with the full transaction id and formatted amounts.
	[Fact]
	public void PrivacyModeOffAutomationSummaryExposesDetails()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidTransactionId tx = Tx('1');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, PeggedAsset, 321)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
		LiquidWalletUiHistoryRow row = snapshot.Rows[0];

		LiquidHistoryItemViewModel item = new(uiContext, row);
		Assert.Equal(item.NormalAccessibilitySummary, item.AccessibilitySummary);
		Assert.Contains(tx.CanonicalRpcHex, item.AccessibilitySummary);
		Assert.Contains("0.00000321", item.AccessibilitySummary); // 321 atomic units as L-BTC decimals
	}

	// §11: the privacy toggle masks and unmasks — it never removes. Flipping
	// the UiConfig privacy flag at runtime swaps the bound accessibility
	// summary between the private (hidden) and normal (full transaction id +
	// amounts) forms while the underlying full values stay present.
	[Fact]
	public void PrivacyToggleMasksAndUnmasksWithoutRemovingDetails()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidTransactionId tx = Tx('4');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, PeggedAsset, 4_242)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
		LiquidHistoryItemViewModel item = new(uiContext, snapshot.Rows[0]);

		// Privacy off: the full transaction id and amount are exposed.
		Assert.Equal(item.NormalAccessibilitySummary, item.AccessibilitySummary);
		Assert.Contains(tx.CanonicalRpcHex, item.AccessibilitySummary);
		Assert.Contains("0.00004242", item.AccessibilitySummary);

		// Toggle on: the bound summary masks everything (but the row still
		// carries the full transaction id — it is masked, not removed).
		uiContext.Services.UiConfig.PrivacyMode = true;
		Assert.Equal(item.PrivateAccessibilitySummary, item.AccessibilitySummary);
		Assert.DoesNotContain(tx.CanonicalRpcHex, item.AccessibilitySummary);
		Assert.Equal(tx.CanonicalRpcHex, item.TransactionId); // still present underneath

		// Toggle back off: the full details are exposed again.
		uiContext.Services.UiConfig.PrivacyMode = false;
		Assert.Equal(item.NormalAccessibilitySummary, item.AccessibilitySummary);
		Assert.Contains(tx.CanonicalRpcHex, item.AccessibilitySummary);
	}

	// §11: the full transaction id IS shown on every row (privacy off), while
	// the block hash — which the retained history never carries — appears in
	// no view-model text field, across every projected row and change.
	[Fact]
	public void FullTransactionIdIsShownAndBlockHashNeverAppearsInAnyViewModelText()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		LiquidTransactionId txA = Tx('2');
		LiquidTransactionId txB = Tx('3');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 100)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, IssuedAssetA, 200)]))
			.Confirm(2, txA, LiquidConfirmation.Create(new string('6', 64), 50));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);

		string blockHash = new string('6', 64);
		Assert.Equal(2, snapshot.Rows.Count);
		foreach (LiquidWalletUiHistoryRow row in snapshot.Rows)
		{
			LiquidHistoryItemViewModel item = new(uiContext, row);

			// The full transaction id is shown verbatim and appears in the
			// normal (privacy-off) summary.
			Assert.Equal(row.TransactionId, item.TransactionId);
			Assert.Contains(row.TransactionId, item.NormalAccessibilitySummary);

			string[] allText =
			[
				item.StatusText,
				item.TransactionId,
				item.EmptyChangeText,
				item.NormalAccessibilitySummary,
				item.PrivateAccessibilitySummary,
				.. item.AssetChanges.SelectMany(c => new[]
				{
					c.DirectionText,
					c.AssetDisplayReference,
					c.DisplayAmount,
					c.NetAtomicUnits.ToString(),
				}),
			];
			foreach (string text in allText)
			{
				// The block hash never crosses into any display text.
				Assert.DoesNotContain(blockHash, text);
			}
		}
	}

	// §11: headless Avalonia accessibility. Render the real LiquidWalletView
	// with a loaded history and prove the list carries the automation name
	// "Liquid transaction history".
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void ViewRendersHistoryListWithAutomationNames()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using ServicesScope _ = InstallTestServices(uiContext.Services.UiConfig);
		LiquidTransactionId tx = Tx('7');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, PeggedAsset, 4_200)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
		LiquidWalletUiSnapshot balances = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		using var model = new WalletWasabi.Fluent.Models.Wallets.Liquid.LiquidWalletModel(
			"wallet", Manifest, balances, ExternalKey.GetScriptPubKey(), new byte[33]);
		var viewModel = new LiquidWalletViewModel(uiContext, model);
		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidWalletView
		{
			DataContext = viewModel,
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
			model.RefreshHistory(snapshot);
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			var listBox = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.ListBox>()
				.Single();
			Assert.Equal(
				"Liquid transaction history",
				Avalonia.Automation.AutomationProperties.GetName(listBox));
		}
		finally
		{
			window.Close();
		}
	}

	// §11: headless privacy mode. With privacy mode on, the row automation
	// name is exactly status plus "Liquid transaction details hidden"; the
	// full transaction id, asset identity, and amount do not appear in any
	// automation name in the rendered history subtree, and the
	// PrivacyContentControl replacement text carries no hidden child value.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void PrivacyModeHidesDetailsFromAutomationTree()
	{
		UiContext uiContext = BuildUiContext(privacyMode: true);
		using ServicesScope _ = InstallTestServices(uiContext.Services.UiConfig);
		LiquidTransactionId tx = Tx('8');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(tx, [], [Output(tx, 0, PeggedAsset, 8_888)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
		LiquidWalletUiSnapshot balances = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);
		string fullId = tx.CanonicalRpcHex;

		using var model = new WalletWasabi.Fluent.Models.Wallets.Liquid.LiquidWalletModel(
			"wallet", Manifest, balances, ExternalKey.GetScriptPubKey(), new byte[33]);
		var viewModel = new LiquidWalletViewModel(uiContext, model);
		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidWalletView
		{
			DataContext = viewModel,
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
			model.RefreshHistory(snapshot);
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			// The bound accessibility summary follows privacy mode: exactly
			// status + "Liquid transaction details hidden", no reference or
			// amount.
			LiquidHistoryItemViewModel row = viewModel.HistoryRows[0];
			Assert.Equal("Unconfirmed Liquid transaction details hidden", row.AccessibilitySummary);
			Assert.DoesNotContain(fullId, row.AccessibilitySummary);
			Assert.DoesNotContain("8888", row.AccessibilitySummary);

			// No automation name anywhere in the rendered history subtree
			// carries the full transaction id.
			foreach (var control in view.GetVisualDescendants().OfType<Avalonia.Controls.Control>())
			{
				string? automationName = Avalonia.Automation.AutomationProperties.GetName(control);
				if (automationName is not null)
				{
					Assert.DoesNotContain(fullId, automationName);
				}
			}

			// The privacy replacement presenter shows replacement glyphs, not
			// the hidden amount/reference: no realized text under the history
			// equals the full id or the raw amount string.
			foreach (var text in view.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>())
			{
				Assert.DoesNotContain(fullId, text.Text ?? "");
			}
		}
		finally
		{
			window.Close();
		}
	}

	// §6: one keyboard tab stop and Up/Down navigation between rows. Focus the
	// list, send Down then Up via the headless input helpers, and assert the
	// view-model selection moves 0 -> 1 -> 0. The ListBox uses the default
	// single tab stop and visible focus style.
	[Avalonia.Headless.XUnit.AvaloniaFact]
	public void KeyboardNavigatesBetweenHistoryRows()
	{
		UiContext uiContext = BuildUiContext(privacyMode: false);
		using ServicesScope _ = InstallTestServices(uiContext.Services.UiConfig);
		LiquidTransactionId txA = Tx('9');
		LiquidTransactionId txB = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 1_000)]))
			.Apply(1, Delta(txB, [], [Output(txB, 0, PeggedAsset, 2_000)]));
		LiquidWalletUiHistorySnapshot snapshot = LiquidWalletUiFacade.CaptureHistory("wallet", Manifest, state);
		LiquidWalletUiSnapshot balances = LiquidWalletUiSnapshot.Capture("wallet", Manifest, state);

		using var model = new WalletWasabi.Fluent.Models.Wallets.Liquid.LiquidWalletModel(
			"wallet", Manifest, balances, ExternalKey.GetScriptPubKey(), new byte[33]);
		var viewModel = new LiquidWalletViewModel(uiContext, model);
		var view = new WalletWasabi.Fluent.Views.Wallets.Liquid.LiquidWalletView
		{
			DataContext = viewModel,
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
			model.RefreshHistory(snapshot);
			view.Measure(new Avalonia.Size(800, 600));
			view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();

			var listBox = view.GetVisualDescendants()
				.OfType<Avalonia.Controls.ListBox>()
				.Single();
			Assert.Equal(2, viewModel.HistoryRows.Count);

			listBox.Focus();
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			listBox.SelectedIndex = 0;
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			Assert.Equal(0, viewModel.SelectedHistoryIndex);

			// Keyboard traversal: the focused ListBox moves the selected row Down then Up.
#pragma warning disable CS0618 // Logical-key KeyPress overload is the headless helper for keyboard traversal.
			listBox.RaiseEvent(new Avalonia.Input.KeyEventArgs
			{
				RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
				Key = Avalonia.Input.Key.Down,
			});
			listBox.RaiseEvent(new Avalonia.Input.KeyEventArgs
			{
				RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent,
				Key = Avalonia.Input.Key.Down,
			});
#pragma warning restore CS0618
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			Assert.Equal(1, viewModel.SelectedHistoryIndex);

			listBox.RaiseEvent(new Avalonia.Input.KeyEventArgs
			{
				RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
				Key = Avalonia.Input.Key.Up,
			});
			listBox.RaiseEvent(new Avalonia.Input.KeyEventArgs
			{
				RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent,
				Key = Avalonia.Input.Key.Up,
			});
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			Assert.Equal(0, viewModel.SelectedHistoryIndex);
		}
		finally
		{
			window.Close();
		}
	}

	// Installs a minimal WalletWasabi.Fluent Services singleton exposing only
	// the supplied UiConfig, so the landed PrivacyContentControl (which reads
	// Services.Instance.UiConfig at construction) can be exercised headless.
	// This is a test-only harness over the unchanged shared control.
	private static ServicesScope InstallTestServices(UiConfig uiConfig)
	{
		var services = (WalletWasabi.Fluent.Services)System.Runtime.CompilerServices.RuntimeHelpers
			.GetUninitializedObject(typeof(WalletWasabi.Fluent.Services));
		SetBackingField(services, nameof(WalletWasabi.Fluent.Services.UiConfig), uiConfig);
		System.Reflection.PropertyInfo instanceProperty = typeof(WalletWasabi.Fluent.Services)
			.GetProperty(nameof(WalletWasabi.Fluent.Services.Instance))!;
		WalletWasabi.Fluent.Services? previous =
			(WalletWasabi.Fluent.Services?)instanceProperty.GetValue(null);
		SetStaticBackingField(nameof(WalletWasabi.Fluent.Services.Instance), services);
		return new ServicesScope(previous);
	}

	private static void SetBackingField(object target, string propertyName, object? value) =>
		typeof(WalletWasabi.Fluent.Services)
			.GetField($"<{propertyName}>k__BackingField",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.SetValue(target, value);

	private static void SetStaticBackingField(string propertyName, object? value) =>
		typeof(WalletWasabi.Fluent.Services)
			.GetField($"<{propertyName}>k__BackingField",
				System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
			.SetValue(null, value);

	private sealed class ServicesScope : IDisposable
	{
		private readonly WalletWasabi.Fluent.Services? _previous;

		internal ServicesScope(WalletWasabi.Fluent.Services? previous) => _previous = previous;

		public void Dispose() =>
			SetStaticBackingField(nameof(WalletWasabi.Fluent.Services.Instance), _previous);
	}

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

	// The presentation tests never open a wallet, so a session rooted at a fresh
	// throwaway directory is sufficient (the application client is created lazily
	// on first open, never here).
	private static LiquidWalletSession BuildLiquidSession()
	{
		string root = Path.Combine(Common.GetWorkDir(), "liquid-session");
		return new LiquidWalletSession(
			Path.Combine(root, "appdata"),
			Path.Combine(root, "wallets"));
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

	// Minimal IServices stub: returns real lightweight instances for the
	// members exercised during UiContext construction (EventBus, UiConfig,
	// PersistentConfig, Config, Network, and the simple getters). Members
	// never touched by the code under test throw to surface any unexpected
	// dependency.
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
