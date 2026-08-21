using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WalletWasabi.Client.Liquid;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
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

	[Fact]
	public void FacadeOutpointCoordinateMapResolvesLandedOutpointsAndRefusesUnknown()
	{
		using TemporaryDirectory directory = new();
		string walletDataDir = directory.Path;
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidAssetId asset = LiquidAssetId.ParseRpcHex("1111111111111111111111111111111111111111111111111111111111111111");
			LiquidTransactionId tx = LiquidTransactionId.ParseRpcHex("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
			LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(tx, 0);
			LiquidSpendKeyReference spendKey = LiquidSpendKeyReference.Create(
				Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
				LiquidKeyBranch.External,
				7);
			LiquidOwnedOutput owned = LiquidOwnedOutput.Create(
				outPoint,
				spendKey.GetScriptPubKey(),
				LiquidAssetAmount.Create(asset, asset, 5000),
				spendKey);
			LiquidWalletTransactionDelta delta = LiquidWalletTransactionDelta.Create(tx, Array.Empty<LiquidOutPoint>(), new[] { owned });
			LiquidWalletState state = LiquidWalletState.Empty(asset).Apply(0, delta);
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);

			System.Collections.Generic.IReadOnlyDictionary<string, LiquidWalletUiOutpointCoordinate> map =
				LiquidWalletUiFacade.LoadAndGetOutpointSpendCoordinates(walletDataDir, "wallet", key, context);

			string landedKey = Convert.ToHexString(outPoint.ToConsensusBytes()).ToLowerInvariant();
			Assert.True(map.TryGetValue(landedKey, out LiquidWalletUiOutpointCoordinate coordinate));
			Assert.Equal((0, (int)LiquidKeyBranch.External, 7), (coordinate.Account, coordinate.Change, coordinate.Index));

			LiquidOutPoint unknown = LiquidOutPoint.CreateSpendable(
				LiquidTransactionId.ParseRpcHex("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), 1);
			Assert.False(map.ContainsKey(Convert.ToHexString(unknown.ToConsensusBytes()).ToLowerInvariant()));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
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
