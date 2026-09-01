using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Models.Wallets.Liquid;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

// LIQUID-UI-OPEN-RESILIENCE-001: opening a Liquid wallet must survive the two
// live-reproduced fragilities — (a) a failed open must leave no open/opening
// guard set so an immediate retry of the same wallet is allowed, and (b) the
// transient acquisition-window node-generation race (a testnet block landing
// mid-acquisition) is retried with a small backoff instead of surfacing the
// error dialog. A non-transient failure still fails closed: no retry, no
// session, error surfaces. The open core is replaced through the session's
// test seam (no node contact); the session's retry wrapper — the resilience
// under test — is the production code path the tests drive.
[Collection("Serial unit tests collection")]
public sealed class LiquidWalletOpenResilienceTests
{
	private const string TransientMessage =
		"Elements RPC 'wallet refresh observation' returned an invalid result: node generation changed during the acquisition.";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;

	// Precondition: the Fluent project builds the test seam these tests drive.
	// The Tests project cannot reference the internal constructor directly (no
	// InternalsVisibleTo), so this asserts the landed source carries it — a
	// missing seam fails here instead of as a reflection error at test time.
	[Fact]
	public void FluentSessionExposesOpenCoreTestSeam()
	{
		string source = File.ReadAllText(FluentSessionSourcePath());
		Assert.Contains("internal LiquidWalletSession(", source, StringComparison.Ordinal);
		Assert.Contains("OpenWalletCoreAsync", source, StringComparison.Ordinal);
		Assert.Contains("RunWithTransientGenerationRetryAsync", source, StringComparison.Ordinal);
	}

	// (a) A failed attempt leaves no open/opening guard set: a first attempt
	// that trips the transient node-generation fence is automatically retried
	// (the failed attempt left no half-registered session to block the retry),
	// and the open completes without the caller ever seeing the transient error
	// or an "already open or opening" rejection.
	[Fact]
	public async Task FailedAttemptLeavesNoGuardAndIsRetriedAsync()
	{
		using var fixture = new SessionFixture();
		int attempts = 0;
		LiquidWalletSession session = fixture.CreateSession((_, _, _, _, _) =>
		{
			attempts++;
			if (attempts == 1)
			{
				throw new InvalidOperationException(TransientMessage);
			}

			return Task.FromResult(fixture.CreateModel());
		});

		// The first attempt's transient failure is retried by the wrapper; it
		// succeeds only because the failed attempt released any open/opening
		// state rather than leaving the guard stuck.
		LiquidWalletModel model = await session.OpenWalletAsync(
			"alpha", fixture.WalletFilePath, "password", CancellationToken.None);

		Assert.Equal("alpha", model.Name);
		Assert.Equal(2, attempts);
	}

	// (b) The transient node-generation message is retried: the first two
	// attempts throw the acquisition-window fence, the third succeeds, and the
	// caller never sees the transient error.
	[Fact]
	public async Task TransientNodeGenerationRaceIsRetriedThenSucceedsAsync()
	{
		using var fixture = new SessionFixture();
		int attempts = 0;
		LiquidWalletSession session = fixture.CreateSession((_, _, _, _, _) =>
		{
			attempts++;
			if (attempts <= 2)
			{
				throw new InvalidOperationException(TransientMessage);
			}

			return Task.FromResult(fixture.CreateModel());
		});

		LiquidWalletModel model = await session.OpenWalletAsync(
			"alpha", fixture.WalletFilePath, "password", CancellationToken.None);

		Assert.Equal("alpha", model.Name);
		Assert.Equal(3, attempts);
	}

	// The ElementsRpcException flavor of the same acquisition-window fence is
	// retried too — the wrapper matches on the message, not the exception type.
	[Fact]
	public async Task TransientNodeGenerationRpcExceptionIsRetriedAsync()
	{
		using var fixture = new SessionFixture();
		int attempts = 0;
		LiquidWalletSession session = fixture.CreateSession((_, _, _, _, _) =>
		{
			attempts++;
			if (attempts == 1)
			{
				throw CreateRpcException(TransientMessage);
			}

			return Task.FromResult(fixture.CreateModel());
		});

		LiquidWalletModel model = await session.OpenWalletAsync(
			"alpha", fixture.WalletFilePath, "password", CancellationToken.None);

		Assert.Equal("alpha", model.Name);
		Assert.Equal(2, attempts);
	}

	// (c) A non-transient failure does not retry and leaves no guard set: the
	// error surfaces on the first attempt, and a subsequent open is a fresh
	// attempt (not blocked by "already open or opening").
	[Fact]
	public async Task NonTransientFailureDoesNotRetryAsync()
	{
		using var fixture = new SessionFixture();
		int attempts = 0;
		LiquidWalletSession session = fixture.CreateSession((_, _, _, _, _) =>
		{
			attempts++;
			throw new InvalidDataException("Liquid wallet authentication failed.");
		});

		InvalidDataException first = await Assert.ThrowsAsync<InvalidDataException>(
			() => session.OpenWalletAsync("alpha", fixture.WalletFilePath, "password", CancellationToken.None));
		Assert.Equal("Liquid wallet authentication failed.", first.Message);
		Assert.Equal(1, attempts);

		// Still no guard: the retry runs the core again and fails the same way,
		// not with an "already open or opening" rejection.
		InvalidDataException second = await Assert.ThrowsAsync<InvalidDataException>(
			() => session.OpenWalletAsync("alpha", fixture.WalletFilePath, "password", CancellationToken.None));
		Assert.Equal("Liquid wallet authentication failed.", second.Message);
		Assert.Equal(2, attempts);
	}

	// A non-transient node-generation mismatch (a genuinely changed generation
	// mid-session, carrying a different message) is NOT retried: the fence
	// still fails closed.
	[Fact]
	public async Task NonTransientGenerationMismatchDoesNotRetryAsync()
	{
		using var fixture = new SessionFixture();
		int attempts = 0;
		LiquidWalletSession session = fixture.CreateSession((_, _, _, _, _) =>
		{
			attempts++;
			throw new InvalidOperationException(
				"Elements RPC 'expectation-bound node observation' returned an invalid result: node generation changed during the observation.");
		});

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => session.OpenWalletAsync("alpha", fixture.WalletFilePath, "password", CancellationToken.None));
		Assert.Equal(1, attempts);
	}

	// The transient retry is bounded: a fence that keeps tripping past the
	// retry budget surfaces the error instead of looping forever.
	[Fact]
	public async Task TransientRetryIsBoundedAsync()
	{
		using var fixture = new SessionFixture();
		int attempts = 0;
		LiquidWalletSession session = fixture.CreateSession((_, _, _, _, _) =>
		{
			attempts++;
			throw new InvalidOperationException(TransientMessage);
		});

		InvalidOperationException surfaced = await Assert.ThrowsAsync<InvalidOperationException>(
			() => session.OpenWalletAsync("alpha", fixture.WalletFilePath, "password", CancellationToken.None));
		Assert.Equal(TransientMessage, surfaced.Message);

		// 1 initial attempt + 2 retries (TransientGenerationMaxRetries).
		Assert.Equal(3, attempts);
	}

	private static ElementsRpcException CreateRpcException(string message)
	{
		var exception = (ElementsRpcException)RuntimeHelpers.GetUninitializedObject(typeof(ElementsRpcException));
		typeof(Exception).GetField("_message", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(exception, message);
		return exception;
	}

	private static string FluentSessionSourcePath() =>
		Path.Combine(
			RepositoryRoot(),
			"WalletWasabi.Fluent", "Models", "Wallets", "Liquid", "LiquidWalletSession.cs");

	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "WalletWasabi.Client")))
		{
			directory = directory.Parent;
		}
		return Assert.IsType<DirectoryInfo>(directory).FullName;
	}

	// (a) at the guard itself: a failed OpenAsync releases the provider's open
	// reservation, so a subsequent ReserveOpen for the same wallet is NOT
	// rejected with "A Liquid wallet session is already open or opening." This
	// is the regression the live retry hit — before the fix the reservation
	// stayed set after a failed open and blocked every retry until app restart.
	// The failure here is a wrong password against a real (RegTest) wallet file,
	// so OpenAsync throws after taking the reservation; no node is contacted.
	[Fact]
	public async Task FailedProviderOpenReleasesOpenReservationAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "CorrectPassword", NBitcoin.Network.RegTest, walletFile);
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"alpha",
			walletFile,
			"local",
			manifest.ManifestId,
			new LiquidWalletDirectories(walletDirectory));
		CreateRpcProfile(directory.Path, identity.RuntimeProfileName, manifest);

		await using LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource(manifest.ManifestId));

		// Wrong password -> OpenAsync takes the reservation, then fails during
		// authentication and must release it.
		await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(provider, identity, "WrongPassword"));

		// The reservation was released: a fresh reserve for the same wallet is
		// granted instead of throwing "already open or opening".
		object reservation = ReserveOpen(provider, identity);
		CompleteReservation(reservation);
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
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
			throw;
		}
	}

	private static void CompleteReservation(object reservation) =>
		((TaskCompletionSource<object?>)reservation.GetType()
			.GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(reservation)!).TrySetResult(null);

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

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-open-resilience-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}

	private sealed class SessionFixture : IDisposable
	{
		internal SessionFixture()
		{
			Root = Path.Combine(Common.GetWorkDir(), "liquid-open-resilience");
			Directory.CreateDirectory(Path.Combine(Root, "appdata"));
			string walletDirectory = Directory.CreateDirectory(Path.Combine(Root, "wallets")).FullName;
			WalletFilePath = Path.Combine(walletDirectory, "alpha.json");
		}

		private string Root { get; }
		internal string WalletFilePath { get; }

		internal LiquidWalletSession CreateSession(
			Func<LiquidWalletSession, string, string, string, CancellationToken, Task<LiquidWalletModel>> openCore)
		{
			var session = (LiquidWalletSession)RuntimeHelpers.GetUninitializedObject(typeof(LiquidWalletSession));
			SetField(session, "_applicationDataDirectory", Path.Combine(Root, "appdata"));
			SetField(session, "_liquidWalletDirectory", Path.Combine(Root, "wallets"));
#pragma warning disable CA2000 // Ownership transfers to the session under construction; it disposes the gate.
			SetField(session, "_clientGate", new SemaphoreSlim(1, 1));
#pragma warning restore CA2000
			SetField(session, "_openCore", openCore);
			return session;
		}

		internal LiquidWalletModel CreateModel() =>
			new(
				"alpha",
				Manifest,
				CreateBalances("alpha", revision: 1),
				new byte[] { 0x51 },
				new byte[33]);

		private static LiquidWalletUiSnapshot CreateBalances(string walletName, ulong revision)
		{
			var snapshot = (LiquidWalletUiSnapshot)RuntimeHelpers.GetUninitializedObject(typeof(LiquidWalletUiSnapshot));
			SetField(snapshot, "<WalletName>k__BackingField", walletName);
			SetField(snapshot, "<NetworkManifestId>k__BackingField", Manifest.ManifestId);
			SetField(snapshot, "<PeggedAssetIdHex>k__BackingField", Manifest.PeggedAssetId);
			SetField(snapshot, "<Revision>k__BackingField", revision);
			return snapshot;
		}

		private static void SetField(object target, string name, object? value) =>
			target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

		public void Dispose()
		{
			try
			{
				Directory.Delete(Root, recursive: true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}

