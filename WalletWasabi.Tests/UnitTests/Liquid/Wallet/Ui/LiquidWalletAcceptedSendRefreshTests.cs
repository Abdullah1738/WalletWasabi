using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;
#pragma warning disable CA2000

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

public sealed class LiquidWalletAcceptedSendRefreshTests
{
	private const string WalletName = "alpha";
	private const string TransactionId = "1111111111111111111111111111111111111111111111111111111111111111";

	[Fact]
	public async Task AcceptedReceiptRecordsBeforeCancellationAndInvokesSharedRefreshOnceAsync()
	{
		using var handler = new TrackingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		var refreshCalls = new List<LiquidWalletUiRefreshRequest>();
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> sharedRefresh =
			(request, ct) =>
			{
				refreshCalls.Add(request);
				ct.ThrowIfCancellationRequested();
				return Task.FromResult(new LiquidWalletUiRefreshResult(
					LiquidWalletUiRefreshStatus.Committed,
					request.CanonicalWalletId,
					request.Trigger,
					request.AcceptedTransactionIdHex,
					candidateCount: 1,
					appliedTransactionCount: 1,
					resultRevision: 2,
					resultGeneration: 2,
					isPostSubmit: request.Trigger == LiquidWalletUiRefreshTrigger.AcceptedSend,
					handoffPublished: true));
			};
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session, sharedRefresh);

		// Pre-canceled token: the accepted ID must still be recorded before cancellation is
		// consulted, and the shared refresh delegate must be invoked exactly once with
		// Trigger = AcceptedSend and the canonical ID.
		using var canceled = new CancellationTokenSource();
		canceled.Cancel();
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> command =
			LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(
				provider,
				async (request, scopeFactory, cancellationToken) =>
				{
					ILiquidWalletSendExecutionScope scope = scopeFactory.Open(request.WalletName);
					try
					{
						await scope.ScheduleRefreshAsync(TransactionId, cancellationToken);
					}
					finally
					{
						scope.Dispose();
					}
					return Result(request);
				});

		_ = await Assert.ThrowsAnyAsync<Exception>(() => command(Request(), canceled.Token));

		Assert.Equal([TransactionId], session.GetRecordedAcceptedTransactionIds());
		LiquidWalletUiRefreshRequest call = Assert.Single(refreshCalls);
		Assert.Equal(LiquidWalletUiRefreshTrigger.AcceptedSend, call.Trigger);
		Assert.Equal(TransactionId, call.AcceptedTransactionIdHex);
		Assert.Equal(WalletName, call.CanonicalWalletId);
	}

	[Fact]
	public async Task AmbiguityRefreshDoesNotRecordAcceptedIdAsync()
	{
		using var handler = new TrackingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		var refreshCalls = new List<LiquidWalletUiRefreshRequest>();
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> sharedRefresh =
			(request, ct) =>
			{
				refreshCalls.Add(request);
				return Task.FromResult(new LiquidWalletUiRefreshResult(
					LiquidWalletUiRefreshStatus.NoCandidates,
					request.CanonicalWalletId,
					request.Trigger,
					request.AcceptedTransactionIdHex,
					candidateCount: 0,
					appliedTransactionCount: 0,
					resultRevision: 1,
					resultGeneration: 1,
					isPostSubmit: false,
					handoffPublished: false));
			};
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session, sharedRefresh);

		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> command =
			LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(
				provider,
				async (request, scopeFactory, cancellationToken) =>
				{
					ILiquidWalletSendExecutionScope scope = scopeFactory.Open(request.WalletName);
					try
					{
						// The ambiguity path performs a manual discovery refresh and records no accepted ID.
						await scope.ScheduleManualRefreshAsync(cancellationToken);
					}
					finally
					{
						scope.Dispose();
					}
					return Result(request);
				});

		_ = await command(Request(), CancellationToken.None);

		Assert.Empty(session.GetRecordedAcceptedTransactionIds());
		LiquidWalletUiRefreshRequest call = Assert.Single(refreshCalls);
		Assert.Equal(LiquidWalletUiRefreshTrigger.Manual, call.Trigger);
		Assert.Null(call.AcceptedTransactionIdHex);
	}

	private static LiquidWalletUiSendExecutionRequest Request() =>
		new(
			WalletName,
			[TransactionId + "00000000"],
			"el1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq",
			ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId,
			1,
			1,
			0,
			[null]);

	private static LiquidWalletUiSendExecutionResult Result(LiquidWalletUiSendExecutionRequest request) =>
		new(
			LiquidWalletUiSendExecutionStatus.RejectedBeforeSubmit,
			request.WalletName,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId,
			0,
			null,
			null,
			broadcastAttempted: false,
			refreshScheduled: false,
			"test-complete");

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(
		LiquidAuthenticatedWalletSession session,
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> sharedRefresh)
	{
		var provider = (LiquidAuthenticatedRuntimeProvider)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedRuntimeProvider));
		SetField(provider, "_gate", new object());
		SetField(provider, "_manifestSource", new ElementsPublicNetworkManifestSource(ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
		SetField(provider, "_session", session);
		SetField(provider, "_refreshCommand", sharedRefresh);
		return provider;
	}

	private static LiquidAuthenticatedWalletSession CreateSession(TrackingHandler handler)
	{
		var session = (LiquidAuthenticatedWalletSession)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedWalletSession));
		var master = new ExtKey();
		LiquidWalletReceiveDerivation receive = LiquidWalletReceiveDerivation.Create(master, NBitcoin.Network.Main, 0, 0);
		var adapter = new LiquidWalletSignerKeyAdapter(master, _ => (0, 0, 0), NBitcoin.Network.Main);
		var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
		var rpcClient = new ElementsRpcClient(httpClient);
		SetField(rpcClient, "_ownsHttpClient", true);

		SetField(session, "_refreshGate", new object());
		SetField(session, "_lifetimeGate", new object());
		SetField(session, "_acceptedTransactionIds", new List<string>());
		SetField(session, "<Identity>k__BackingField", CreateIdentity());
		SetField(session, "<AuthenticatedMaster>k__BackingField", master);
		SetField(session, "<Descriptor>k__BackingField", receive.Descriptor);
		SetField(session, "<LastIndex>k__BackingField", receive.LastIndex);
		SetField(session, "<SignerKeyAdapter>k__BackingField", adapter);
		SetField(session, "<RpcClient>k__BackingField", rpcClient);
		SetField(session, "<WalletDataDirectory>k__BackingField", AppContext.BaseDirectory);
		SetField(session, "_manifest", ElementsPublicNetworkManifest.LiquidMainnet);
		return session;
	}

	private static LiquidWalletIdentity CreateIdentity()
	{
		ConstructorInfo constructor = typeof(LiquidWalletIdentity).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			[string.Empty.GetType(), string.Empty.GetType(), string.Empty.GetType(), string.Empty.GetType()],
			modifiers: null)!;
		return (LiquidWalletIdentity)constructor.Invoke(
			[WalletName, "/unused/wallet.json", "unused", ElementsPublicNetworkManifest.LiquidMainnet.ManifestId]);
	}

	private static void SetField(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private sealed class TrackingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("No HTTP request is expected from this accepted-send test.");
	}
}
