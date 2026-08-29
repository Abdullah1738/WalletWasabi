using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using Xunit;
#pragma warning disable CA2000

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletManifestCompositionTests
{
	[Theory]
	[InlineData("b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b")]
	[InlineData("e4e7ec03e19ce5f83fd04c586788b724d88052b65ef2480cc93bcd50324f6b20")]
	public async Task FacadeBindsEachReviewedManifestAndRequestCannotOverrideAsync(string manifestId)
	{
		using TemporaryDirectory directory = new();
		string wallets = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		await using LiquidWalletApplicationClient client = LiquidWalletApplicationClient.Create(new(
			directory.Path,
			wallets,
			manifestId));

		Assert.Equal(manifestId, client.Options.ReviewedManifestId);
		Assert.Equal(manifestId, client.RuntimeProvider.ManifestId);
		Assert.DoesNotContain(
			typeof(LiquidWalletOpenRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance),
			property => property.Name.Contains("Manifest", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("", typeof(ArgumentException))]
	[InlineData("0000000000000000000000000000000000000000000000000000000000000000", typeof(ElementsNetworkManifestException))]
	[InlineData("elements-regtest", typeof(ElementsNetworkManifestException))]
	public void ApplicationClientRejectsUnreviewedManifestBeforeComposition(
		string manifestId,
		Type expectedExceptionType)
	{
		using TemporaryDirectory directory = new();
		string wallets = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		LiquidWalletApplicationClient? client = null;
		object? provider = null;
		object? currentHandoff = null;

		Exception exception = Record.Exception(() =>
		{
			client = LiquidWalletApplicationClient.Create(new(
				directory.Path,
				wallets,
				manifestId));
			provider = client.RuntimeProvider;
			currentHandoff = client.CurrentHandoff;
		});

		Assert.IsType(expectedExceptionType, exception);
		Assert.Null(client);
		Assert.Null(provider);
		Assert.Null(currentHandoff);
	}

	[Fact]
	public async Task ProfileManifestMismatchFailsBeforeWalletKeyOrRpcAccessAsync()
	{
		using TemporaryDirectory directory = new();
		string wallets = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string invalidWalletFile = Path.Combine(wallets, "invalid-wallet.json");
		File.WriteAllText(invalidWalletFile, "{}");
		CreateProfile(
			directory.Path,
			"local",
			ElementsPublicNetworkManifest.LiquidTestnet.ManifestId,
			ElementsPublicNetworkManifest.LiquidTestnet.ChainRpcName);
		await using LiquidWalletApplicationClient client = LiquidWalletApplicationClient.Create(new(
			directory.Path,
			wallets,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId));
		using LiquidWalletOpenAuthorization authorization = client.CreateOpenAuthorization("secret");
		var request = new LiquidWalletOpenRequest("alpha", invalidWalletFile, "local");

		InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.OpenAsync(request, authorization, CancellationToken.None).AsTask());

		Assert.Contains("profile_manifest", exception.Message, StringComparison.Ordinal);
		Assert.Equal("{}", File.ReadAllText(invalidWalletFile));
		Assert.Null(client.CurrentHandoff);
	}

	private static void CreateProfile(
		string dataDirectory,
		string profileName,
		string manifestId,
		string network)
	{
		string profileDirectory = Directory.CreateDirectory(Path.Combine(dataDirectory, "liquid-rpc-profiles")).FullName;
		string cookieFile = Path.Combine(dataDirectory, "cookie");
		File.WriteAllText(cookieFile, "user:password\n");
		string profileFile = Path.Combine(profileDirectory, profileName + ".json");
		File.WriteAllText(profileFile, $$"""
			{"schema":"walletwasabi-liquid-rpc-profile/v1","name":"{{profileName}}","endpoint":"http://127.0.0.1:18884","cookieFile":"{{cookieFile}}","network":"{{network}}","manifest":"{{manifestId}}","connectTimeoutMs":1000,"requestTimeoutMs":1000}
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
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-manifest-composition-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}
}
