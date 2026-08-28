using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Client;
using WalletWasabi.Client.Liquid;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidApplicationCompositionTests
{
	[Fact]
	public async Task CompositionOwnsOneFacadeAndForwardsExactMembersAsync()
	{
		await using LiquidWalletApplicationClient applicationClient = CreateApplicationClient();
		await using LiquidWalletRuntimeComposition composition = new(applicationClient);

		Assert.Same(applicationClient, composition.ApplicationClient);
		Assert.Same(applicationClient.SendCommand, composition.SendCommand);
		Assert.Same(applicationClient.RefreshCommand, composition.RefreshCommand);
		Assert.Null(composition.PublicHandoff);

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

	[Fact]
	public void WasabiApplicationExposesForwardersWithoutFacade()
	{
		Type application = typeof(WasabiApplication);
		Assert.NotNull(application.GetMethod(nameof(WasabiApplication.CreateLiquidWalletOpenAuthorization)));
		Assert.NotNull(application.GetMethod(nameof(WasabiApplication.OpenLiquidWalletAsync)));
		Assert.NotNull(application.GetMethod(nameof(WasabiApplication.CloseLiquidWalletAsync)));
		Assert.NotNull(application.GetProperty(nameof(WasabiApplication.LiquidWalletRuntime)));
		Assert.NotNull(application.GetProperty(nameof(WasabiApplication.LiquidWalletSendCommand)));
		Assert.DoesNotContain(
			application.GetMembers(BindingFlags.Public | BindingFlags.Instance),
			member => GetSignatureTypes(member).Contains(typeof(LiquidWalletApplicationClient)));
	}

	[Fact]
	public void CompositionContainsNoRegtestManifestHardcode()
	{
		string source = File.ReadAllText(Path.Combine(
			FindRepositoryRoot(),
			"WalletWasabi.Client",
			"Liquid",
			"LiquidApplicationWalletBootstrap.cs"));
		Assert.DoesNotContain("elements-regtest", source, StringComparison.Ordinal);
	}

	private static LiquidWalletApplicationClient CreateApplicationClient()
	{
		string root = Path.GetTempPath();
		return LiquidWalletApplicationClient.Create(new(
			root,
			root,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
	}

	private static Type[] GetSignatureTypes(MemberInfo member) => member switch
	{
		MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType).ToArray(),
		PropertyInfo property => [property.PropertyType],
		_ => []
	};

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

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "WalletWasabi.Client")))
		{
			directory = directory.Parent;
		}
		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
	}
}
