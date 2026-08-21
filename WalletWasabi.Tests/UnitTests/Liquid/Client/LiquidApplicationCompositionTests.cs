using System;
using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Client.Liquid;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;
#pragma warning disable CA2000

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidApplicationCompositionTests
{
	[Fact]
	public async Task CompositionDisposesOwnedProviderAndRejectsSecondDisposalAsync()
	{
		using TemporaryDirectory directory = new();
		string wallets = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		LiquidWalletRuntimeHandoff handoff = new("alpha", "liquid-mainnet", new LiquidWalletUiBootstrapSnapshot("alpha", "liquid-mainnet", 0));
		LiquidWalletRuntimeComposition composition = new(CreateProvider(directory.Path, wallets), handoff);


		await composition.DisposeAsync();
		await composition.DisposeAsync();

		Assert.True(composition.IsDisposed);
	}

	private static LiquidAuthenticatedRuntimeProvider CreateProvider(string dataDirectory, string walletDirectory) =>
		new(new LiquidRpcProfileSource(dataDirectory), new LiquidWalletDirectories(walletDirectory), new ElementsPublicNetworkManifestSource("liquid-mainnet"));

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-composition-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}
		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, true);
	}
}
