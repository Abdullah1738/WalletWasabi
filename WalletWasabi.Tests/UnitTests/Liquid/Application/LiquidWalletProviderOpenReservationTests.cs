using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletProviderOpenReservationTests
{
	[Fact]
	public async Task DisposeDuringRealOpenPreventsPublicationAndWaitsForRollbackAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha",
			walletFile,
			"local",
			manifest.ManifestId,
			new LiquidWalletDirectories(walletDirectory));
		CreatePersistedLiquidState(walletDirectory, walletFile, "TestPassword", identity.CanonicalWalletId, manifest);
		CreateRpcProfile(directory.Path, identity.RuntimeProfileName, manifest);
		var callbackEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseCallback = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		LiquidAuthenticatedWalletSession? candidate = null;
		int callbackCount = 0;
		#pragma warning disable CA2000 // Provider disposal is the behavior under test below.
		LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource(manifest.ManifestId),
			beforePublicationAsync: async (realCandidate, cancellationToken) =>
			{
				Assert.Equal(1, Interlocked.Increment(ref callbackCount));
				candidate = realCandidate;
				callbackEntered.TrySetResult(null);
				await releaseCallback.Task.WaitAsync(cancellationToken);
			});
		#pragma warning restore CA2000

		Task<LiquidAuthenticatedWalletSession> open = OpenAsync(provider, identity, "TestPassword");
		await callbackEntered.Task;
		LiquidAuthenticatedWalletSession unpublishedCandidate = Assert.IsType<LiquidAuthenticatedWalletSession>(candidate);
		Assert.Null(provider.CurrentHandoff);
		Assert.Null(provider.TryGetOpenSession(identity.CanonicalWalletId));
		Assert.False(open.IsCompleted);

		Task firstDisposal = provider.DisposeAsync().AsTask();
		Task secondDisposal = provider.DisposeAsync().AsTask();
		Assert.Same(firstDisposal, secondDisposal);
		Assert.False(firstDisposal.IsCompleted);
		Assert.False(unpublishedCandidate.IsDisposed);
		Assert.Throws<ObjectDisposedException>(() => provider.AcquireOperation(identity.CanonicalWalletId));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => OpenAsync(provider, identity, "TestPassword"));
		bool? candidateWasDisposedWhenProviderCompleted = null;
		_ = firstDisposal.ContinueWith(
			_ => candidateWasDisposedWhenProviderCompleted = unpublishedCandidate.IsDisposed,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

		releaseCallback.TrySetResult(null);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => open);
		await Task.WhenAll(firstDisposal, secondDisposal);

		Assert.True(unpublishedCandidate.IsDisposed);
		Assert.True(candidateWasDisposedWhenProviderCompleted);
		Assert.Null(provider.CurrentHandoff);
		Assert.Null(provider.TryGetOpenSession(identity.CanonicalWalletId));
		Assert.Equal(1, callbackCount);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => OpenAsync(provider, identity, "TestPassword"));
	}

	[Fact]
	public async Task ReservationOccupiesOnlySlotAndMatchingCloseFailsAsync()
	{
		using var fixture = new ProviderFixture();
		LiquidAuthenticatedRuntimeProvider provider = fixture.Provider;
		object reservation = ReserveOpen(provider, fixture.Identity);

		InvalidOperationException secondOpen = Assert.Throws<InvalidOperationException>(
			() => ReserveOpen(provider, fixture.Identity));
		Assert.Equal("A Liquid wallet session is already open or opening.", secondOpen.Message);

		InvalidOperationException closeFailure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => provider.CloseAsync(fixture.Identity, default).AsTask());
		Assert.Equal("The Liquid wallet open is in progress.", closeFailure.Message);

		CompleteReservation(reservation);
		await provider.DisposeAsync();
	}

	[Fact]
	public async Task ConcurrentDisposeAsyncCallsJoinOpenReservationRollbackAsync()
	{
		using var fixture = new ProviderFixture();
		LiquidAuthenticatedRuntimeProvider provider = fixture.Provider;
		object reservation = ReserveOpen(provider, fixture.Identity);
		Task reservationCompletion = GetReservationCompletion(reservation).Task;

		Task first = provider.DisposeAsync().AsTask();
		Task second = provider.DisposeAsync().AsTask();

		Assert.Same(first, second);
		Assert.False(first.IsCompleted);
		Assert.False(reservationCompletion.IsCompleted);
		Assert.Throws<ObjectDisposedException>(() => ReserveOpen(provider, fixture.Identity));

		SetField(provider, "_openReservation", null);
		CompleteReservation(reservation);
		await Task.WhenAll(first, second);

		Assert.True(reservationCompletion.IsCompletedSuccessfully);
		Assert.True(first.IsCompletedSuccessfully);
	}

	private static async Task<LiquidAuthenticatedWalletSession> OpenAsync(
		LiquidAuthenticatedRuntimeProvider provider,
		LiquidWalletIdentity identity,
		string password)
	{
		char[] buffer = password.ToCharArray();
		try
		{
			return await provider.OpenAsync(identity, buffer, CancellationToken.None);
		}
		finally
		{
			LiquidWalletOpenAuthorization.ZeroBuffer(buffer);
		}
	}

	private static void CreatePersistedLiquidState(
		string walletDirectory,
		string walletFile,
		string password,
		string walletName,
		ElementsPublicNetworkManifest manifest)
	{
		KeyManager keyManager = KeyManager.FromFile(walletFile);
		ExtKey master = keyManager.GetMasterExtKey(password);
		ExtKey replayChild = master.Derive(new KeyPath(1108790945U | 0x80000000U));
		byte[] childMaterial = replayChild.PrivateKey.ToBytes();
		byte[] saltInput = Encoding.UTF8.GetBytes(manifest.ManifestId + walletName);
		byte[] salt = SHA256.HashData(saltInput);
		byte[] replayKey = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/replay");
		byte[] context = LiquidKeyDomain.DeriveHkdf(childMaterial, salt, "WalletWasabi/Liquid/v1/context");
		try
		{
			LiquidWalletState state = LiquidWalletState.Empty(LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId));
			_ = LiquidWalletLoadSave.Save(walletDirectory, walletName, state, generation: 0, replayKey, context);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(replayKey);
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(saltInput);
			CryptographicOperations.ZeroMemory(childMaterial);
		}
	}

	private static void CreateRpcProfile(
		string dataDirectory,
		string profileName,
		ElementsPublicNetworkManifest manifest)
	{
		string profileDirectory = Directory.CreateDirectory(Path.Combine(dataDirectory, "liquid-rpc-profiles")).FullName;
		string cookieFile = Path.Combine(dataDirectory, "cookie");
		File.WriteAllText(cookieFile, "user:password\n");
		string profileFile = Path.Combine(profileDirectory, profileName + ".json");
		File.WriteAllText(profileFile, $$"""
			{"schema":"walletwasabi-liquid-rpc-profile/v1","name":"{{profileName}}","endpoint":"http://127.0.0.1:18884","cookieFile":"{{cookieFile}}","network":"{{manifest.ChainRpcName}}","manifest":"{{manifest.ManifestId}}","connectTimeoutMs":1000,"requestTimeoutMs":1000}
			""");
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
		{
			File.SetUnixFileMode(cookieFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.SetUnixFileMode(profileFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	private static object ReserveOpen(LiquidAuthenticatedRuntimeProvider provider, LiquidWalletIdentity identity)
	{
		try
		{
			return typeof(LiquidAuthenticatedRuntimeProvider)
				.GetMethod("ReserveOpen", BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(provider, [identity])!;
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
			throw;
		}
	}

	private static TaskCompletionSource<object?> GetReservationCompletion(object reservation) =>
		(TaskCompletionSource<object?>)reservation.GetType()
			.GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(reservation)!;

	private static void CompleteReservation(object reservation) =>
		GetReservationCompletion(reservation).TrySetResult(null);

	private static void SetField(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-open-reservation-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}

	private sealed class ProviderFixture : IDisposable
	{
		internal ProviderFixture()
		{
			ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
			Provider = (LiquidAuthenticatedRuntimeProvider)RuntimeHelpers.GetUninitializedObject(typeof(LiquidAuthenticatedRuntimeProvider));
			SetField(Provider, "_gate", new object());
			SetField(Provider, "_manifestSource", new ElementsPublicNetworkManifestSource(manifest.ManifestId));
			Identity = (LiquidWalletIdentity)RuntimeHelpers.GetUninitializedObject(typeof(LiquidWalletIdentity));
			SetField(Identity, "<CanonicalWalletId>k__BackingField", "alpha");
			SetField(Identity, "<CanonicalWalletFilePath>k__BackingField", "/unused/alpha.json");
			SetField(Identity, "<RuntimeProfileName>k__BackingField", "local");
			SetField(Identity, "<NetworkManifestId>k__BackingField", manifest.ManifestId);
		}

		internal LiquidAuthenticatedRuntimeProvider Provider { get; }
		internal LiquidWalletIdentity Identity { get; }

		public void Dispose()
		{
		}
	}
}
