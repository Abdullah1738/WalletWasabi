using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

public class LiquidProductModeTests
{
	[Fact]
	public void LiquidProductModeIsEnabled()
	{
		Assert.True(LiquidProductMode.Enabled);
	}

	[Fact]
	public void AddWalletHubExposesOnlyLiquidTileWhenGated()
	{
		AddWalletPageViewModel viewModel = (AddWalletPageViewModel)RuntimeHelpers.GetUninitializedObject(typeof(AddWalletPageViewModel));

		Assert.Equal(!LiquidProductMode.Enabled, viewModel.IsBtcUiVisible);
		Assert.False(viewModel.IsBtcUiVisible);
		Assert.NotNull(typeof(AddWalletPageViewModel).GetProperty(nameof(AddWalletPageViewModel.LiquidWalletCommand)));
	}

	[Fact]
	public void AddWalletHubRetainsBtcTilePropertiesBehindGate()
	{
		string[] btcCommandProperties =
		[
			nameof(AddWalletPageViewModel.CreateWalletCommand),
			nameof(AddWalletPageViewModel.ConnectHardwareWalletCommand),
			nameof(AddWalletPageViewModel.ImportWalletCommand),
			nameof(AddWalletPageViewModel.RecoverWalletCommand)
		];

		Assert.All(
			btcCommandProperties,
			propertyName => Assert.NotNull(typeof(AddWalletPageViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)));
	}
}
