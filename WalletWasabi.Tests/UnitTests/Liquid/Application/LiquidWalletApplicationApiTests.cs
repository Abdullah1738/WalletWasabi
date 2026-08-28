using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Client;
using ClientLiquidWalletRuntimeComposition = WalletWasabi.Client.Liquid.LiquidWalletRuntimeComposition;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletApplicationApiTests
{
	[Fact]
	public void FacadeExposesOnlyFrozenNonSecretSurface()
	{
		Type facade = typeof(LiquidWalletApplicationClient);
		Assert.True(facade.IsPublic && facade.IsSealed);
		Assert.Contains(typeof(IAsyncDisposable), facade.GetInterfaces());

		Assert.NotNull(facade.GetMethod(nameof(LiquidWalletApplicationClient.Create), BindingFlags.Public | BindingFlags.Static));
		Assert.NotNull(facade.GetMethod(nameof(LiquidWalletApplicationClient.CreateOpenAuthorization), BindingFlags.Public | BindingFlags.Instance));
		Assert.NotNull(facade.GetMethod(nameof(LiquidWalletApplicationClient.OpenAsync), BindingFlags.Public | BindingFlags.Instance));
		Assert.NotNull(facade.GetMethod(nameof(LiquidWalletApplicationClient.CloseAsync), BindingFlags.Public | BindingFlags.Instance));
		Assert.NotNull(facade.GetProperty(nameof(LiquidWalletApplicationClient.CurrentHandoff)));
		Assert.NotNull(facade.GetProperty(nameof(LiquidWalletApplicationClient.RefreshCommand)));
		Assert.NotNull(facade.GetProperty(nameof(LiquidWalletApplicationClient.SendCommand)));
		Assert.Null(facade.GetMethod("SendAsync", BindingFlags.Public | BindingFlags.Instance));

		Type[] forbidden =
		[
			typeof(ExtKey),
			typeof(Key),
			typeof(KeyManager),
			typeof(HttpClient),
			typeof(NetworkCredential),
			typeof(IServiceProvider),
			typeof(object),
			typeof(LiquidAuthenticatedRuntimeProvider),
			typeof(LiquidAuthenticatedWalletSession),
			typeof(LiquidWalletIdentity),
			typeof(LiquidWalletOperationLease),
			typeof(ElementsRpcClient)
		];
		foreach (MemberInfo member in facade.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
		{
			foreach (Type type in GetPublicSignatureTypes(member))
			{
				Assert.DoesNotContain(type, forbidden);
			}
		}
	}

	[Fact]
	public void OptionsAndOpenRequestContainOnlyFrozenFields()
	{
		Assert.Equal(
			[nameof(LiquidWalletApplicationOptions.ApplicationDataDirectory), nameof(LiquidWalletApplicationOptions.LiquidWalletDirectory), nameof(LiquidWalletApplicationOptions.ReviewedManifestId)],
			typeof(LiquidWalletApplicationOptions).GetProperties().Select(x => x.Name).Order().ToArray());
		Assert.Equal(
			[nameof(LiquidWalletOpenRequest.CanonicalWalletFilePath), nameof(LiquidWalletOpenRequest.CanonicalWalletId), nameof(LiquidWalletOpenRequest.RuntimeProfileName)],
			typeof(LiquidWalletOpenRequest).GetProperties().Select(x => x.Name).Order().ToArray());
		Assert.All(typeof(LiquidWalletApplicationOptions).GetProperties(), property => Assert.Equal(typeof(string), property.PropertyType));
		Assert.All(typeof(LiquidWalletOpenRequest).GetProperties(), property => Assert.Equal(typeof(string), property.PropertyType));
	}

	[Fact]
	public void RefreshDtosExposeOnlyFrozenFieldsAndValidateTriggerShape()
	{
		Assert.Equal(
			[
				nameof(LiquidWalletUiRefreshRequest.AcceptedTransactionIdHex),
				nameof(LiquidWalletUiRefreshRequest.CanonicalWalletId),
				nameof(LiquidWalletUiRefreshRequest.Trigger),
			],
			typeof(LiquidWalletUiRefreshRequest).GetProperties().Select(property => property.Name).Order().ToArray());
		Assert.Equal(
			[
				nameof(LiquidWalletUiRefreshResult.AcceptedTransactionIdHex),
				nameof(LiquidWalletUiRefreshResult.AppliedTransactionCount),
				nameof(LiquidWalletUiRefreshResult.CandidateCount),
				nameof(LiquidWalletUiRefreshResult.CanonicalWalletId),
				nameof(LiquidWalletUiRefreshResult.HandoffPublished),
				nameof(LiquidWalletUiRefreshResult.IsPostSubmit),
				nameof(LiquidWalletUiRefreshResult.ResultGeneration),
				nameof(LiquidWalletUiRefreshResult.ResultRevision),
				nameof(LiquidWalletUiRefreshResult.Status),
				nameof(LiquidWalletUiRefreshResult.Trigger),
			],
			typeof(LiquidWalletUiRefreshResult).GetProperties().Select(property => property.Name).Order().ToArray());

		LiquidWalletUiRefreshRequest manual = new("alpha", LiquidWalletUiRefreshTrigger.Manual, null);
		LiquidWalletUiRefreshRequest accepted = new("alpha", LiquidWalletUiRefreshTrigger.AcceptedSend, new string('1', 64));
		Assert.Null(manual.AcceptedTransactionIdHex);
		Assert.Equal(new string('1', 64), accepted.AcceptedTransactionIdHex);
		Assert.Throws<ArgumentException>(() => new LiquidWalletUiRefreshRequest("alpha", LiquidWalletUiRefreshTrigger.Manual, new string('1', 64)));
		Assert.Throws<ArgumentException>(() => new LiquidWalletUiRefreshRequest("alpha", LiquidWalletUiRefreshTrigger.AcceptedSend, null));
		Assert.Throws<ArgumentException>(() => new LiquidWalletUiRefreshRequest(" alpha", LiquidWalletUiRefreshTrigger.Manual, null));
		Assert.Throws<ArgumentException>(() => new LiquidWalletUiRefreshRequest("alpha", LiquidWalletUiRefreshTrigger.AcceptedSend, new string('0', 64)));
	}

	[Fact]
	public async Task CompositionAndApplicationForwardExactStableFacadeCommandAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		await using ClientLiquidWalletRuntimeComposition composition = new(client);
		Assert.Same(client, composition.ApplicationClient);
		Assert.Same(client.SendCommand, composition.SendCommand);

		WasabiApplication application = (WasabiApplication)RuntimeHelpers.GetUninitializedObject(typeof(WasabiApplication));
		FieldInfo compositionField = typeof(WasabiApplication).GetField("_liquidComposition", BindingFlags.Instance | BindingFlags.NonPublic)!;
		compositionField.SetValue(application, composition);
		Assert.Same(application.LiquidWalletSendCommand, application.LiquidWalletSendCommand);
		Assert.Same(client.SendCommand, application.LiquidWalletSendCommand);
	}

	[Fact]
	public void WasabiApplicationDoesNotExposeFacade()
	{
		Assert.DoesNotContain(
			typeof(WasabiApplication).GetMembers(BindingFlags.Public | BindingFlags.Instance),
			member => GetPublicSignatureTypes(member).Contains(typeof(LiquidWalletApplicationClient)));
	}

	[Fact]
	public async Task CreateBindsOneProviderAndStableProviderBackedSendCommandAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		LiquidAuthenticatedRuntimeProvider provider = client.RuntimeProvider;

		Assert.Same(provider, client.RuntimeProvider);
		Assert.Same(client.RefreshCommand, client.RefreshCommand);
		Assert.Same(provider.RefreshCommand, client.RefreshCommand);
		Assert.Same(provider, client.RefreshCommand.Target!.GetType()
			.GetField("_runtimeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(client.RefreshCommand.Target));
		Assert.Same(client.SendCommand, client.SendCommand);
		Assert.Same(provider, client.SendCommand.Target!.GetType()
			.GetField("_runtimeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(client.SendCommand.Target));
		Assert.Equal(client.Options.ReviewedManifestId, provider.ManifestId);
	}

	[Fact]
	public async Task CurrentHandoffReadsExactProviderPublicationAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		Assert.Null(client.CurrentHandoff);
		LiquidWalletRuntimeHandoff handoff = (LiquidWalletRuntimeHandoff)RuntimeHelpers.GetUninitializedObject(typeof(LiquidWalletRuntimeHandoff));
		SetField(client.RuntimeProvider, "_currentHandoff", handoff);

		Assert.Same(handoff, client.CurrentHandoff);
	}

	[Fact]
	public async Task DisposeAsyncJoinsProviderDisposalAndRejectsOperationsAsync()
	{
		LiquidWalletApplicationClient client = CreateClient();
		Task first = client.DisposeAsync().AsTask();
		Task second = client.DisposeAsync().AsTask();

		Assert.Same(first, second);
		await Task.WhenAll(first, second);
		Assert.Throws<ObjectDisposedException>(() => client.CreateOpenAuthorization("secret"));
		await Assert.ThrowsAsync<ObjectDisposedException>(() =>
			client.CloseAsync("alpha", CancellationToken.None).AsTask());
		LiquidWalletUiSendExecutionRequest request = new(
			"alpha",
			["111111111111111111111111111111111111111111111111111111111111111100000000"],
			"unused",
			ElementsPublicNetworkManifest.LiquidMainnet.PeggedAssetId,
			1,
			1,
			0,
			[null]);
		await Assert.ThrowsAsync<ObjectDisposedException>(() =>
			client.SendCommand(request, CancellationToken.None));
	}

	[Fact]
	public async Task CreateCanonicalizesAndBindsReviewedManifestAsync()
	{
		string root = Path.Combine(Path.GetTempPath(), "walletwasabi-liquid-facade", Guid.NewGuid().ToString("N"));
		string app = Path.Combine(root, "app");
		string wallets = Path.Combine(root, "wallets");
		Directory.CreateDirectory(app);
		Directory.CreateDirectory(wallets);
		try
		{
			await using LiquidWalletApplicationClient client = LiquidWalletApplicationClient.Create(new(
				Path.Combine(app, "."),
				Path.Combine(wallets, "."),
				ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));

			Assert.Equal(Path.GetFullPath(app), client.Options.ApplicationDataDirectory);
			Assert.Equal(Path.GetFullPath(wallets), client.Options.LiquidWalletDirectory);
			Assert.Equal(ElementsPublicNetworkManifest.LiquidMainnet.ManifestId, client.Options.ReviewedManifestId);
			Assert.Equal(ElementsPublicNetworkManifest.LiquidMainnet.ManifestId, client.RuntimeProvider.ManifestId);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static LiquidWalletApplicationClient CreateClient()
	{
		string root = Path.GetTempPath();
		return LiquidWalletApplicationClient.Create(new(
			root,
			root,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
	}

	private static void SetField(object target, string name, object? value) =>
		target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

	private static Type[] GetPublicSignatureTypes(MemberInfo member) => member switch
	{
		MethodInfo method => method.GetParameters().Select(x => x.ParameterType).Append(method.ReturnType).ToArray(),
		PropertyInfo property => [property.PropertyType],
		ConstructorInfo constructor => constructor.GetParameters().Select(x => x.ParameterType).ToArray(),
		_ => []
	};
}
