using System;
using System.IO;
using System.Reflection;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Client.Liquid;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidProviderOwnershipSeamTests
{
	[Fact]
	public void PasswordAuthorizationLeaseRejectsEmptyPassword()
	{
		Assert.Throws<ArgumentException>(() => LiquidPasswordAuthorizationLease.Create(ReadOnlySpan<char>.Empty));
	}

	[Fact]
	public void PasswordAuthorizationLeaseRejectsOversizedPassword()
	{
		Assert.Throws<ArgumentException>(() => LiquidPasswordAuthorizationLease.Create(new string('x', 1025)));
	}

	[Fact]
	public void PasswordAuthorizationLeaseDisposesAndZeroizesOwnedBuffer()
	{
		LiquidPasswordAuthorizationLease lease = LiquidPasswordAuthorizationLease.Create("secret");
		char[] buffer = Assert.IsType<char[]>(typeof(LiquidPasswordAuthorizationLease)
			.GetField("_password", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(lease));

		lease.Dispose();
		lease.Dispose();

		Assert.True(lease.IsDisposed);
		Assert.All(buffer, value => Assert.Equal('\0', value));
		Assert.Throws<ObjectDisposedException>(() => ReadPassword(lease));
	}

	[Fact]
	public void WalletIdentityCanonicalizesOnlyReviewedIdentityComponents()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		File.WriteAllText(walletFile, "{}");
		LiquidWalletDirectories directories = new(walletDirectory);

		LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
			"  alpha  ",
			walletFile,
			" local ",
			" liquid-mainnet ",
			directories);

		Assert.Equal("alpha", identity.CanonicalWalletId);
		Assert.Equal(Path.GetFullPath(walletFile), identity.CanonicalWalletFilePath);
		Assert.Equal("local", identity.RuntimeProfileName);
		Assert.Equal("liquid-mainnet", identity.NetworkManifestId);
	}

	[Fact]
	public void WalletIdentityRejectsFilesOutsideConfiguredWalletDirectory()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string outsideFile = Path.Combine(directory.Path, "outside.json");
		File.WriteAllText(outsideFile, "{}");

		Assert.Throws<InvalidDataException>(() => LiquidWalletIdentity.Create(
			"alpha", outsideFile, "local", "liquid-mainnet", new LiquidWalletDirectories(walletDirectory)));
	}

	[Fact]
	public async System.Threading.Tasks.Task ProviderConsumesLeaseAndRejectsDuplicatePublishedIdentityAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", "liquid-mainnet", new LiquidWalletDirectories(walletDirectory));
		LiquidAuthenticatedRuntimeProvider provider = new(
			new LiquidRpcProfileSource(directory.Path),
			new LiquidWalletDirectories(walletDirectory),
			new ElementsPublicNetworkManifestSource("liquid-mainnet"));
		CreateRpcProfile(directory.Path, "local", "liquid-mainnet");

		using LiquidPasswordAuthorizationLease firstLease = LiquidPasswordAuthorizationLease.Create("TestPassword");
		LiquidAuthenticatedWalletSession session = await provider.OpenAsync(identity, firstLease, default);
		using LiquidPasswordAuthorizationLease duplicateLease = LiquidPasswordAuthorizationLease.Create("TestPassword");

		await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.OpenAsync(identity, duplicateLease, default));

		Assert.Equal(identity.CanonicalWalletId, session.PublicHandoff.CanonicalWalletId);

		await provider.CloseAsync(identity, default);
		Assert.True(session.IsDisposed);
		await provider.DisposeAsync();
	}

	[Fact]
	public async System.Threading.Tasks.Task ProviderDisposalDrainsPublishedSessionsAndRejectsNewOpensAsync()
	{
		using TemporaryDirectory directory = new();
		string walletDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "wallets")).FullName;
		string walletFile = Path.Combine(walletDirectory, "alpha.json");
		KeyManager.CreateNew(out _, "TestPassword", NBitcoin.Network.RegTest, walletFile);
		LiquidWalletIdentity identity = LiquidWalletIdentity.Create("alpha", walletFile, "local", "liquid-mainnet", new LiquidWalletDirectories(walletDirectory));
		CreateRpcProfile(directory.Path, "local", "liquid-mainnet");
		LiquidAuthenticatedRuntimeProvider provider = new(new LiquidRpcProfileSource(directory.Path), new LiquidWalletDirectories(walletDirectory), new ElementsPublicNetworkManifestSource("liquid-mainnet"));
		using LiquidPasswordAuthorizationLease openLease = LiquidPasswordAuthorizationLease.Create("TestPassword");
		LiquidAuthenticatedWalletSession session = await provider.OpenAsync(identity, openLease, default);

		await provider.DisposeAsync();

		Assert.True(session.IsDisposed);
		using LiquidPasswordAuthorizationLease rejectedLease = LiquidPasswordAuthorizationLease.Create("TestPassword");
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await provider.OpenAsync(identity, rejectedLease, default));
	}

	private static void CreateRpcProfile(string dataDirectory, string profileName, string manifest)
	{
		string profileDirectory = Directory.CreateDirectory(Path.Combine(dataDirectory, "liquid-rpc-profiles")).FullName;
		string cookieFile = Path.Combine(dataDirectory, "cookie");
		File.WriteAllText(cookieFile, "user:password\n");
		string profileFile = Path.Combine(profileDirectory, profileName + ".json");
		File.WriteAllText(profileFile, $$"""
			{"schema":"walletwasabi-liquid-rpc-profile/v1","name":"{{profileName}}","endpoint":"http://127.0.0.1:18884","cookieFile":"{{cookieFile}}","network":"liquid","manifest":"{{manifest}}","connectTimeoutMs":1000,"requestTimeoutMs":1000}
			""");
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
		{
			File.SetUnixFileMode(cookieFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.SetUnixFileMode(profileFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	private static string ReadPassword(LiquidPasswordAuthorizationLease lease) => new(lease.Password);

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-identity-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}
}
