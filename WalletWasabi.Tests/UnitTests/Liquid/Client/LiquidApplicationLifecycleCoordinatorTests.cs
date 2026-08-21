using System;
using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Client;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Client.Liquid;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;
#pragma warning disable CA2000

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidApplicationLifecycleCoordinatorTests
{
	[Fact]
	public async Task CleanupIsOneTaskAndTerminationCallbackRunsOnceAsync()
	{
		using TemporaryDirectory directory = new();
		string wallets = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		await using LiquidWalletRuntimeComposition composition = new(CreateProvider(directory.Path, wallets), new LiquidWalletRuntimeHandoff("alpha", "liquid-mainnet", new LiquidWalletUiBootstrapSnapshot("alpha", "liquid-mainnet", 0)));

		int terminated = 0;
		Global global = new(directory.Path, new Config(PersistentConfigManager.DefaultMainNetConfig, cliArgs: []));
		using SingleInstanceChecker singleInstanceChecker = new(Path.Combine(directory.Path, "single-instance"));
		LiquidApplicationLifecycleCoordinator coordinator = new(composition, global, singleInstanceChecker, () => terminated++);

		Task<LiquidApplicationCleanupResult> first = coordinator.StartOrJoinCleanupAsync();
		Task<LiquidApplicationCleanupResult> second = coordinator.StartOrJoinCleanupAsync();
		LiquidApplicationCleanupResult result = await first;

		Assert.Same(first, second);
		Assert.Same(result, coordinator.FinalResult);
		Assert.Equal(1, terminated);
		Assert.Empty(result.Errors);
		Assert.Throws<InvalidOperationException>(() => { coordinator.EnterRun(); coordinator.EnterRun(); });
	}

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(string dataDirectory, string walletDirectory) =>
		new(new LiquidRpcProfileSource(dataDirectory), new LiquidWalletDirectories(walletDirectory), new ElementsPublicNetworkManifestSource("liquid-mainnet"));

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-lifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, true);
	}
}
