using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;
#pragma warning disable CA2000

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletReceiveLabelCommandServiceTests
{
	private const string WalletName = "alpha";

	[Fact]
	public async Task SetLabelsExecutesSaveThenInstallAndUpdatesNextReceiveLabelAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		var saves = new List<LiquidWalletReceiveLabelCommandService.SaveRequest>();
		LiquidWalletReceiveLabelCommandService.Dependencies dependencies =
			LiquidWalletReceiveLabelCommandService.Dependencies.CreateForTesting(
				save: request =>
				{
					saves.Add(request);
					return new WalletWasabi.Liquid.Wallet.LiquidWalletReceiveLabelAllocation(
						request.Index,
						request.Labels,
						request.State.Revision,
						request.NextGeneration,
						request.State);
				},
				publish: (_, _, _) => true);
		Func<LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> command =
			LiquidWalletReceiveLabelCommandService.CreateSetReceiveLabelsCommandForTesting(provider, dependencies);

		// The session's next-receive index is LastIndex (0). Label it.
		LiquidAuthenticatedWalletStateOwner replacement = await command(
			new LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest(
				WalletName, 0, ["savings", "vault"]));

		LiquidWalletReceiveLabelCommandService.SaveRequest save = Assert.Single(saves);
		Assert.Equal(1UL, save.NextGeneration);
		Assert.Equal(0UL, save.BaseGeneration);
		Assert.Equal(WalletName, save.WalletName);
		// The replacement owner reflects the label and an advanced generation.
		Assert.Equal(1UL, replacement.PersistenceGeneration);
		Assert.Equal(
			LiquidWalletLabelSet.Create(["savings", "vault"]),
			replacement.State.GetReceiveLabels(0));
		// The session installed the replacement: the next-receive label is now readable.
		Assert.Equal(
			LiquidWalletLabelSet.Create(["savings", "vault"]),
			session.StateOwner.State.GetReceiveLabels(checked((uint)session.LastIndex)));
		// The published receive material rebinds the durable next-receive labels (the
		// reviewer-flagged gap): it must not be dead-on-arrival empty after a label write.
		Assert.Equal(
			["savings", "vault"],
			replacement.ReceiveMaterial.NextReceiveLabels);
		Assert.Equal(
			["savings", "vault"],
			session.StateOwner.ReceiveMaterial.NextReceiveLabels);
	}

	[Fact]
	public async Task SetLabelsClearWithEmptySetRemovesNextReceiveLabelAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		// Pre-label the session's committed state at the next-receive index.
		LiquidAuthenticatedWalletStateOwner labeled = session.StateOwner.CreateReplacement(
			session.StateOwner.State.SetReceiveLabels(0, LiquidWalletLabelSet.Create(["temp"])),
			1UL);
		Assert.True(session.TryInstallRefreshSnapshot(
			session.CaptureRefreshSnapshot(),
			labeled,
			session.PublicHandoff));
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		LiquidWalletReceiveLabelCommandService.Dependencies dependencies =
			LiquidWalletReceiveLabelCommandService.Dependencies.CreateForTesting(
				save: request => new WalletWasabi.Liquid.Wallet.LiquidWalletReceiveLabelAllocation(
					request.Index,
					request.Labels,
					request.State.Revision,
					request.NextGeneration,
					request.State),
				publish: (_, _, _) => true);
		Func<LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> command =
			LiquidWalletReceiveLabelCommandService.CreateSetReceiveLabelsCommandForTesting(provider, dependencies);

		LiquidAuthenticatedWalletStateOwner replacement = await command(
			new LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest(WalletName, 0, []));

		Assert.Null(replacement.State.GetReceiveLabels(0));
		Assert.Null(session.StateOwner.State.GetReceiveLabels((uint)session.LastIndex));
	}

	[Fact]
	public async Task SetLabelsRejectsStaleGenerationBeforeSaveAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		bool saveCalled = false;
		LiquidWalletReceiveLabelCommandService.Dependencies dependencies =
			LiquidWalletReceiveLabelCommandService.Dependencies.CreateForTesting(
				save: request =>
				{
					saveCalled = true;
					// The durable save fences a concurrent generation change: the captured
					// base generation no longer matches the readable current state.
					throw new InvalidOperationException("The Liquid wallet persistence generation changed during save.");
				},
				publish: (_, _, _) => true);
		Func<LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> command =
			LiquidWalletReceiveLabelCommandService.CreateSetReceiveLabelsCommandForTesting(provider, dependencies);

		InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			command(new LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest(WalletName, 0, ["x"])));

		// The stale-write rejection surfaces and the write is not persisted.
		Assert.Equal("The Liquid wallet persistence generation changed during save.", failure.Message);
		Assert.True(saveCalled);
		Assert.Null(session.StateOwner.State.GetReceiveLabels(0));
	}

	[Fact]
	public async Task SetLabelsValidatesLabelSetBeforeSaveAsync()
	{
		using var handler = new RejectingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		bool saveCalled = false;
		LiquidWalletReceiveLabelCommandService.Dependencies dependencies =
			LiquidWalletReceiveLabelCommandService.Dependencies.CreateForTesting(
				save: _ =>
				{
					saveCalled = true;
					throw new InvalidOperationException("Save must not run for an invalid label set.");
				},
				publish: (_, _, _) => true);
		Func<LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> command =
			LiquidWalletReceiveLabelCommandService.CreateSetReceiveLabelsCommandForTesting(provider, dependencies);

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			command(new LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest(
				WalletName, 0, [new string('x', LiquidWalletLabelSet.MaximumLabelUtf8ByteCount + 1)])));

		Assert.False(saveCalled);
	}

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(LiquidAuthenticatedWalletSession session) =>
		CreateProvider(session, ElementsPublicNetworkManifest.LiquidMainnet);

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(LiquidAuthenticatedWalletSession session, ElementsPublicNetworkManifest manifest)
	{
		var provider = (LiquidAuthenticatedRuntimeProvider)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedRuntimeProvider));
		SetField(provider, "_gate", new object());
		SetField(provider, "_manifestSource", new ElementsPublicNetworkManifestSource(manifest.ManifestId));
		SetField(provider, "_session", session);
		return provider;
	}

	private static LiquidAuthenticatedWalletSession CreateSession(RejectingHandler handler) =>
		CreateSession(handler, ElementsPublicNetworkManifest.LiquidMainnet);

	private static LiquidAuthenticatedWalletSession CreateSession(RejectingHandler handler, ElementsPublicNetworkManifest manifest)
	{
		var session = (LiquidAuthenticatedWalletSession)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedWalletSession));
		var master = new ExtKey();
		LiquidWalletReceiveDerivation receive = LiquidWalletReceiveDerivation.Create(master, NBitcoin.Network.Main, 0, 0);
		var adapter = new LiquidWalletSignerKeyAdapter(master, _ => (0, 0, 0), NBitcoin.Network.Main);
		var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
		var rpcClient = new ElementsRpcClient(httpClient);
		SetField(rpcClient, "_ownsHttpClient", true);

		LiquidAuthenticatedWalletStateOwner owner = CreateOwner(manifest);
		var handoff = new LiquidWalletRuntimeHandoff(
			WalletName,
			manifest.ManifestId,
			owner.Balances,
			owner.SelectableOutputs,
			owner.History,
			owner.ReceiveMaterial);
		object snapshot = CreateSnapshot(owner, handoff);

		SetField(session, "_refreshGate", new object());
		SetField(session, "_lifetimeGate", new object());
		SetField(session, "_acceptedTransactionIds", new List<string>());
		SetField(session, "<Identity>k__BackingField", CreateIdentity(manifest));
		SetField(session, "<AuthenticatedMaster>k__BackingField", master);
		SetField(session, "<Descriptor>k__BackingField", receive.Descriptor);
		SetField(session, "<LastIndex>k__BackingField", receive.LastIndex);
		SetField(session, "<SignerKeyAdapter>k__BackingField", adapter);
		SetField(session, "<RpcClient>k__BackingField", rpcClient);
		SetField(session, "<WalletDataDirectory>k__BackingField", AppContext.BaseDirectory);
		SetField(session, "_manifest", manifest);
		SetField(session, "_snapshot", snapshot);
		return session;
	}

	private static LiquidAuthenticatedWalletStateOwner CreateOwner(ElementsPublicNetworkManifest manifest)
	{
		using var handler = new RejectingHandler();
		var master = new ExtKey();
		var adapter = new LiquidWalletSignerKeyAdapter(master, _ => null, NBitcoin.Network.Main);
		var rpcClient = new ElementsRpcClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") });
		var owner = (LiquidAuthenticatedWalletStateOwner)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedWalletStateOwner));
		var allocation = new WalletWasabi.Liquid.Wallet.LiquidWalletExternalIndexAllocation(
			index: 0,
			stateRevision: 0,
			persistedGeneration: 0,
			persistedExternalIndexHighWater: 0,
			persistedInternalIndexHighWater: 0,
			WalletWasabi.Liquid.Wallet.LiquidWalletState.Empty(
				WalletWasabi.Liquid.Assets.LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId)));
		SetField(owner, "_allocation", allocation);
		SetField(owner, "_walletName", WalletName);
		SetField(owner, "_manifest", manifest);
		SetAutoProperty(owner, "StateRevision", 0UL);
		SetAutoProperty(owner, "PersistenceGeneration", 0UL);
		SetAutoProperty(owner, "Descriptor", "wpkh(test)");
		SetAutoProperty(owner, "LastIndex", 0UL);
		SetAutoProperty(owner, "ReceiveMaterial", CreateReceiveMaterial());
		SetAutoProperty(owner, "Balances", WalletWasabi.Liquid.Wallet.Ui.LiquidWalletUiFacade.CaptureAllocationBalances(WalletName, manifest, allocation));
		SetAutoProperty(owner, "SelectableOutputs", WalletWasabi.Liquid.Wallet.Ui.LiquidWalletUiFacade.CaptureSelectableOutputs(WalletName, manifest, allocation));
		SetAutoProperty(owner, "History", WalletWasabi.Liquid.Wallet.Ui.LiquidWalletUiFacade.CaptureAllocationHistory(WalletName, manifest, allocation));
		SetAutoProperty(owner, "NodeExpectation", BoundExpectation(manifest));
		adapter.Dispose();
		rpcClient.Dispose();
		return owner;
	}

	private static LiquidWalletUiReceiveMaterial CreateReceiveMaterial() =>
		new([0x00, 0x14, .. Enumerable.Repeat((byte)1, 20)], [0x02, .. Enumerable.Repeat((byte)2, 32)]);

	private static ElementsNodeExpectation BoundExpectation(ElementsPublicNetworkManifest manifest) =>
		ElementsReviewedNodeExpectationSource.Bind(
			manifest,
			new LiquidRpcProfile("local", new Uri("http://127.0.0.1:18884"), "/tmp/unused", manifest.ChainRpcName, manifest.ManifestId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

	private static object CreateSnapshot(LiquidAuthenticatedWalletStateOwner owner, LiquidWalletRuntimeHandoff handoff)
	{
		Type type = typeof(LiquidAuthenticatedWalletSession).GetNestedType("RefreshSnapshot", BindingFlags.NonPublic)!;
		return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [owner, handoff], null)!;
	}

	private static LiquidWalletIdentity CreateIdentity(ElementsPublicNetworkManifest manifest)
	{
		ConstructorInfo constructor = typeof(LiquidWalletIdentity).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			null,
			[typeof(string), typeof(string), typeof(string), typeof(string)],
			null)!;
		return (LiquidWalletIdentity)constructor.Invoke(
			[WalletName, "/unused/wallet.json", "unused", manifest.ManifestId]);
	}

	private static void SetAutoProperty(object target, string name, object? value) =>
		SetField(target, $"<{name}>k__BackingField", value);

	private static void SetField(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private sealed class RejectingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("No HTTP request is expected from this orchestration test seam.");
	}
}
