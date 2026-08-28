using System;
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

public sealed class LiquidWalletSendExecutionCommandServiceLifetimeTests
{
	[Fact]
	public async Task ProviderLeaseSpansScopeDisposalAndAcceptedRecordingAndBlocksSessionDisposalAsync()
	{
		using var handler = new TrackingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> command =
			LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(
				provider,
				async (request, scopeFactory, cancellationToken) =>
				{
					ILiquidWalletSendExecutionScope scope = scopeFactory.Open(request.WalletName);
					byte[] replayProtectionKey = scope.ReplayProtectionKey;
					Assert.Equal(1, ActiveOperationCount(session));

					await scope.ScheduleRefreshAsync(TransactionId, cancellationToken);
					Assert.Equal([TransactionId], session.GetRecordedAcceptedTransactionIds());
					Assert.Equal(1, ActiveOperationCount(session));

					scope.Dispose();
					Assert.All(replayProtectionKey, value => Assert.Equal(0, value));
					Assert.Equal(1, ActiveOperationCount(session));
					Assert.Equal(0, handler.DisposeCount);

					Task disposal = provider.DisposeAsync().AsTask();
					Assert.False(disposal.IsCompleted);
					Assert.Equal(0, handler.DisposeCount);

					await Task.Yield();
					Assert.False(disposal.IsCompleted);
					return Result(request);
				});

		LiquidWalletUiSendExecutionResult result = await command(Request(), CancellationToken.None);
		await provider.DisposeAsync();

		Assert.Equal(LiquidWalletUiSendExecutionStatus.RejectedBeforeSubmit, result.Status);
		Assert.Equal(0, ActiveOperationCount(session));
		Assert.True(session.IsDisposed);
		Assert.Equal(1, handler.DisposeCount);
	}

	[Fact]
	public async Task ProviderPathRetainsOneActiveSendPerWalletFenceAsync()
	{
		using var handler = new TrackingHandler();
		LiquidAuthenticatedWalletSession session = CreateSession(handler);
		LiquidAuthenticatedRuntimeProvider provider = CreateProvider(session);
		var releaseExecution = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var executionEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		int executionCount = 0;
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> command =
			LiquidWalletSendExecutionCommandService.CreateSendCommandForTesting(
				provider,
				async (request, _, _) =>
				{
					Interlocked.Increment(ref executionCount);
					executionEntered.TrySetResult(null);
					await releaseExecution.Task;
					return Result(request);
				});

		Task<LiquidWalletUiSendExecutionResult> first = command(Request(), CancellationToken.None);
		await executionEntered.Task;

		await Assert.ThrowsAsync<InvalidOperationException>(() => command(Request(), CancellationToken.None));
		Assert.Equal(1, executionCount);
		Assert.Equal(1, ActiveOperationCount(session));

		releaseExecution.TrySetResult(null);
		await first;
		Assert.Equal(0, ActiveOperationCount(session));
		await provider.DisposeAsync();
		Assert.Equal(1, handler.DisposeCount);
	}

	private const string WalletName = "alpha";
	private const string TransactionId = "1111111111111111111111111111111111111111111111111111111111111111";

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

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(LiquidAuthenticatedWalletSession session)
	{
		var provider = (LiquidAuthenticatedRuntimeProvider)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedRuntimeProvider));
		SetField(provider, "_gate", new object());
		SetField(provider, "_manifestSource", new ElementsPublicNetworkManifestSource(ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
		SetField(provider, "_session", session);
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
		SetField(session, "_acceptedTransactionIds", new System.Collections.Generic.List<string>());
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

	private static int ActiveOperationCount(LiquidAuthenticatedWalletSession session) =>
		GetField<int>(session, "_activeOperationCount");

	private static T GetField<T>(object target, string name) =>
		(T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

	private static void SetField(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private sealed class TrackingHandler : HttpMessageHandler
	{
		internal int DisposeCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("No HTTP request is expected from this lifetime test.");

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				DisposeCount++;
			}
			base.Dispose(disposing);
		}
	}
}
