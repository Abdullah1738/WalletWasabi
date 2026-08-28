using System;
using WalletWasabi.Liquid.Application;

namespace WalletWasabi.Client.Liquid;

internal static class LiquidApplicationWalletBootstrap
{
	internal static LiquidWalletApplicationClient CreateApplicationClient(
		string applicationDataDirectory,
		string liquidWalletDirectory,
		string reviewedManifestId) =>
		LiquidWalletApplicationClient.Create(new LiquidWalletApplicationOptions(
			applicationDataDirectory,
			liquidWalletDirectory,
			reviewedManifestId));
}
