using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Client;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Client.Liquid;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidApplicationLifecycleCoordinatorTests
{
	[Fact]
	public async Task CleanupIsOneTaskAndDisposesExactFacadeAsync()
	{
		using TemporaryDirectory directory = new();
#pragma warning disable CA2000 // Ownership transfers to the composition.
		LiquidWalletApplicationClient applicationClient = CreateApplicationClient(directory.Path);
#pragma warning restore CA2000
		await using LiquidWalletRuntimeComposition composition = new(applicationClient);
		int terminated = 0;
		Global global = new(directory.Path, new Config(PersistentConfigManager.DefaultMainNetConfig, cliArgs: []));
		using SingleInstanceChecker singleInstanceChecker = new(Path.Combine(directory.Path, "single-instance"));
		LiquidApplicationLifecycleCoordinator coordinator = new(composition, global, singleInstanceChecker, () => terminated++);

		Assert.Same(applicationClient, composition.ApplicationClient);
		Task<LiquidApplicationCleanupResult> first = coordinator.StartOrJoinCleanupAsync();
		Task<LiquidApplicationCleanupResult> second = coordinator.StartOrJoinCleanupAsync();
		LiquidApplicationCleanupResult result = await first;

		Assert.Same(first, second);
		Assert.Same(result, coordinator.FinalResult);
		Assert.True(composition.IsDisposed);
		Assert.Equal(1, terminated);
		Assert.Empty(result.Errors);
		Assert.Throws<ObjectDisposedException>(() => applicationClient.CreateOpenAuthorization("secret"));
		Assert.Throws<InvalidOperationException>(() => { coordinator.EnterRun(); coordinator.EnterRun(); });
	}

	[Fact]
	public async Task CleanupDelegatesRunOnceInOrderAndAggregateFailuresAsync()
	{
		List<string> calls = [];
		InvalidOperationException compositionException = new("composition");
		InvalidOperationException globalException = new("global");
		InvalidOperationException checkerException = new("checker");
		LiquidApplicationLifecycleCoordinator coordinator = new(
			() =>
			{
				calls.Add("composition");
				return Task.FromException(compositionException);
			},
			() =>
			{
				calls.Add("global");
				return Task.FromException(globalException);
			},
			() =>
			{
				calls.Add("checker");
				throw checkerException;
			},
			() => calls.Add("terminate"));

		Task<LiquidApplicationCleanupResult> first = coordinator.StartOrJoinCleanupAsync();
		Task<LiquidApplicationCleanupResult> second = coordinator.StartOrJoinCleanupAsync();
		LiquidApplicationCleanupResult result = await first;

		Assert.Same(first, second);
		Assert.Equal(["terminate", "composition", "global", "checker"], calls);
		Assert.Collection(
			result.Errors,
			error => Assert.Same(compositionException, error),
			error => Assert.Same(globalException, error),
			error => Assert.Same(checkerException, error));
	}

	[Fact]
	public async Task FailedInstallationDisposesExactFacadeOnceAndContinuesInOrderAsync()
	{
		using TemporaryDirectory directory = new();
		await using LiquidWalletApplicationClient applicationClient = CreateApplicationClient(directory.Path);
		List<string> calls = [];
		int facadeDisposals = 0;
		InvalidOperationException originalException = new("original");
		InvalidOperationException globalException = new("global");
		InvalidOperationException checkerException = new("checker");

		AggregateException aggregate = Assert.Throws<AggregateException>(() =>
			WasabiApplication.RollbackFailedLiquidInstallation(
				originalException,
				async () =>
				{
					calls.Add("facade");
					facadeDisposals++;
					await applicationClient.DisposeAsync();
				},
				() =>
				{
					calls.Add("global");
					return Task.FromException(globalException);
				},
				() =>
				{
					calls.Add("checker");
					throw checkerException;
				}));

		Assert.Equal(1, facadeDisposals);
		Assert.Equal(["facade", "global", "checker"], calls);
		Assert.Collection(
			aggregate.InnerExceptions,
			error => Assert.Same(originalException, error),
			error => Assert.Same(globalException, error),
			error => Assert.Same(checkerException, error));
		Assert.Throws<ObjectDisposedException>(() => applicationClient.CreateOpenAuthorization("secret"));
	}

	[Fact]
	public void FailedInstallationWithNoCleanupFailurePreservesOriginalException()
	{
		InvalidOperationException originalException = new("original");

		InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
			WasabiApplication.RollbackFailedLiquidInstallation(originalException, null, null, null));

		Assert.Same(originalException, thrown);
	}

	private static LiquidWalletApplicationClient CreateApplicationClient(string root)
	{
		string wallets = Directory.CreateDirectory(Path.Combine(root, "wallets")).FullName;
		return LiquidWalletApplicationClient.Create(new(
			root,
			wallets,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
	}

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-lifecycle-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, true);
	}
}
