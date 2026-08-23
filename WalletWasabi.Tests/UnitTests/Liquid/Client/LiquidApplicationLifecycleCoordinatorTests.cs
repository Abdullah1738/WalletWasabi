using System;
using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Client;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Client.Liquid;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Wallet;
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
		await using LiquidWalletRuntimeComposition composition = new(CreateProvider(directory.Path, wallets), CreateHandoff());

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
		new(new LiquidRpcProfileSource(dataDirectory), new LiquidWalletDirectories(walletDirectory), new ElementsPublicNetworkManifestSource("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b"));

	private static LiquidWalletRuntimeHandoff CreateHandoff()
	{
		const string walletName = "alpha";
		ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.LiquidMainnet;
		LiquidWalletState state = LiquidWalletState.Empty(LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId));
		return new LiquidWalletRuntimeHandoff(
			walletName,
			manifest.ManifestId,
			LiquidWalletUiSnapshot.Capture(walletName, manifest, state),
			LiquidWalletUiSelectableOutputsSnapshot.Capture(walletName, manifest, state),
			LiquidWalletUiHistorySnapshot.Capture(walletName, manifest, state),
			new LiquidWalletUiReceiveMaterial([0x51], Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798")));
	}

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-lifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, true);
	}
}
