using System;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Rpc;
using Xunit;
#pragma warning disable CA2000

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletOperationLeaseTests
{
	[Fact]
	public async Task LeaseReleaseIsIdempotentAndCompletesDrainAsync()
	{
		LiquidAuthenticatedWalletSession session = CreateLifetimeOnlySession();
		LiquidWalletOperationLease lease = session.AcquireOperationUnderProviderGate();
		Task drainTask = session.BeginCloseUnderProviderGate();

		Assert.False(drainTask.IsCompleted);
		Assert.Throws<InvalidOperationException>(() => session.AcquireOperationUnderProviderGate());

		lease.Dispose();
		lease.Dispose();

		await drainTask;
		Assert.Equal(0, GetField<int>(session, "_activeOperationCount"));
		Assert.Throws<ObjectDisposedException>(() => lease.Session);
	}

	[Fact]
	public async Task ConcurrentDisposeAsyncCallsJoinAndWaitForFinalLeaseAsync()
	{
		using var handler = new TrackingHandler();
		LiquidAuthenticatedWalletSession session = CreateDisposableSession(handler);
		LiquidWalletOperationLease lease = session.AcquireOperationUnderProviderGate();
		string? publicKeyBeforeClose = session.SignerKeyAdapter.GetPublicKeyHex("owned-outpoint");

		Task first = session.DisposeAsync().AsTask();
		Task second = session.DisposeAsync().AsTask();

		Assert.Same(first, second);
		Assert.False(first.IsCompleted);
		Assert.NotNull(publicKeyBeforeClose);
		Assert.Equal(publicKeyBeforeClose, session.SignerKeyAdapter.GetPublicKeyHex("owned-outpoint"));
		Assert.Equal(0, handler.DisposeCount);
		Assert.Throws<InvalidOperationException>(() => session.AcquireOperationUnderProviderGate());

		lease.Dispose();
		await Task.WhenAll(first, second);

		Assert.True(session.IsDisposed);
		Assert.Null(session.SignerKeyAdapter.GetPublicKeyHex("owned-outpoint"));
		Assert.Equal(1, handler.DisposeCount);
	}

	[Fact]
	public async Task ZeroOperationDisposalRunsOutsideProviderAndSessionLocksAsync()
	{
		object directProviderGate = new();
		using var directHandler = new TrackingHandler();
		LiquidAuthenticatedWalletSession directSession = CreateDisposableSession(directHandler);
		object directLifetimeGate = GetField<object>(directSession, "_lifetimeGate");
		directHandler.DisposalObserver = () =>
		{
			directHandler.ProviderGateEnteredAtDisposal = Monitor.IsEntered(directProviderGate);
			directHandler.SessionGateEnteredAtDisposal = Monitor.IsEntered(directLifetimeGate);
		};

		Task directFirst = directSession.DisposeAsync().AsTask();
		Task directSecond = directSession.DisposeAsync().AsTask();
		Assert.Same(directFirst, directSecond);
		await Task.WhenAll(directFirst, directSecond);

		Assert.False(directHandler.ProviderGateEnteredAtDisposal);
		Assert.False(directHandler.SessionGateEnteredAtDisposal);
		Assert.Equal(1, directHandler.DisposeCount);

		using var providerHandler = new TrackingHandler();
		LiquidAuthenticatedWalletSession providerSession = CreateDisposableSession(providerHandler);
		LiquidWalletIdentity identity = CreateIdentity();
		SetField(providerSession, "<Identity>k__BackingField", identity);
		var provider = (LiquidAuthenticatedRuntimeProvider)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedRuntimeProvider));
		object providerGate = new();
		SetField(provider, "_gate", providerGate);
		SetField(provider, "_manifestSource", new ElementsPublicNetworkManifestSource(identity.NetworkManifestId));
		SetField(provider, "_session", providerSession);
		object providerLifetimeGate = GetField<object>(providerSession, "_lifetimeGate");
		providerHandler.DisposalObserver = () =>
		{
			providerHandler.ProviderGateEnteredAtDisposal = Monitor.IsEntered(providerGate);
			providerHandler.SessionGateEnteredAtDisposal = Monitor.IsEntered(providerLifetimeGate);
		};

		Task providerFirst = provider.CloseAsync(identity, default).AsTask();
		Task detachedCloseTask = GetDetachedCloseTask(provider);
		Task providerSecond = provider.CloseAsync(identity, default).AsTask();
		Task providerDisposal = provider.DisposeAsync().AsTask();
		await Task.WhenAll(providerFirst, providerSecond, providerDisposal);

		Assert.Same(detachedCloseTask, GetDetachedCloseTask(provider));

		Assert.False(providerHandler.ProviderGateEnteredAtDisposal);
		Assert.False(providerHandler.SessionGateEnteredAtDisposal);
		Assert.Equal(1, providerHandler.DisposeCount);
	}

	[Fact]
	public async Task DrainContinuationRunsAfterLifetimeLockIsReleasedAsync()
	{
		LiquidAuthenticatedWalletSession session = CreateLifetimeOnlySession();
		LiquidWalletOperationLease lease = session.AcquireOperationUnderProviderGate();
		Task drainTask = session.BeginCloseUnderProviderGate();
		object lifetimeGate = GetField<object>(session, "_lifetimeGate");
		var continuationEnteredGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = drainTask.ContinueWith(
			_ =>
			{
				bool entered = Monitor.TryEnter(lifetimeGate);
				if (entered)
				{
					Monitor.Exit(lifetimeGate);
				}
				continuationEnteredGate.TrySetResult(entered);
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

		lease.Dispose();

		Assert.True(await continuationEnteredGate.Task);
	}

	private static LiquidAuthenticatedWalletSession CreateLifetimeOnlySession()
	{
		var session = (LiquidAuthenticatedWalletSession)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedWalletSession));
		SetField(session, "_lifetimeGate", new object());
		return session;
	}

	private static LiquidAuthenticatedWalletSession CreateDisposableSession(TrackingHandler handler)
	{
		LiquidAuthenticatedWalletSession session = CreateLifetimeOnlySession();
		var master = new ExtKey();
		var adapter = new LiquidWalletSignerKeyAdapter(master, _ => (0, 0, 0), NBitcoin.Network.Main);
		var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
		var rpcClient = new ElementsRpcClient(httpClient);
		SetField(rpcClient, "_ownsHttpClient", true);
		SetField(session, "<SignerKeyAdapter>k__BackingField", adapter);
		SetField(session, "<RpcClient>k__BackingField", rpcClient);
		return session;
	}

	private static LiquidWalletIdentity CreateIdentity()
	{
		LiquidWalletIdentity identity = (LiquidWalletIdentity)RuntimeHelpers.GetUninitializedObject(typeof(LiquidWalletIdentity));
		SetField(identity, "<CanonicalWalletId>k__BackingField", "alpha");
		SetField(identity, "<NetworkManifestId>k__BackingField", "b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b");
		return identity;
	}

	private static Task GetDetachedCloseTask(LiquidAuthenticatedRuntimeProvider provider)
	{
		object detachedClose = GetField<object>(provider, "_detachedClose");
		return (Task)detachedClose.GetType()
			.GetProperty("DisposeTask", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(detachedClose)!;
	}

	private static T GetField<T>(object target, string name) =>
		(T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

	private static void SetField(object target, string name, object value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private sealed class TrackingHandler : HttpMessageHandler
	{
		internal Action? DisposalObserver { get; set; }
		internal int DisposeCount { get; private set; }
		internal bool ProviderGateEnteredAtDisposal { get; set; }
		internal bool SessionGateEnteredAtDisposal { get; set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("No HTTP request is expected from this lifetime test.");

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				DisposalObserver?.Invoke();
				DisposeCount++;
			}
			base.Dispose(disposing);
		}
	}
}
