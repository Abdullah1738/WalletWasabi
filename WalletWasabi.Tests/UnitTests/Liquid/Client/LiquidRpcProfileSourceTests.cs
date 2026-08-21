using System;
using System.IO;
using System.Net;
using System.Text;
using WalletWasabi.Client.Liquid;
using Xunit;

#pragma warning disable CA1416

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidRpcProfileSourceTests
{
	[Fact]
	public void LoadsLoopbackProfileAndCookieLeaseIsPerRequest()
	{
		using var directory = new TemporaryDirectory();
		string profileDirectory = Path.Combine(directory.Path, "liquid-rpc-profiles");
		Directory.CreateDirectory(profileDirectory);
		string cookiePath = Path.Combine(directory.Path, "elements.cookie");
		File.WriteAllText(cookiePath, "node-user:node-password\n", new UTF8Encoding(false));
		SetOwnerOnly(cookiePath);
		string profilePath = Path.Combine(profileDirectory, "local.json");
		File.WriteAllText(profilePath, "{\"schema\":\"walletwasabi-liquid-rpc-profile/v1\",\"name\":\"local\",\"endpoint\":\"http://127.0.0.1:18884\",\"cookieFile\":\"" +
			cookiePath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\",\"network\":\"elementsregtest\",\"manifest\":\"elements-regtest\",\"connectTimeoutMs\":1000,\"requestTimeoutMs\":5000}");
		File.SetUnixFileMode(profilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

		var source = new LiquidRpcProfileSource(directory.Path);
		LiquidRpcProfile profile = source.LoadValidated("local");

		Assert.Equal("local", profile.Name);
		Assert.Equal(new Uri("http://127.0.0.1:18884"), profile.Endpoint);
		Assert.Equal(cookiePath, profile.CookieFilePath);
		Assert.Equal("elementsregtest", profile.Network);

		var cookies = new LiquidRpcCookieCredentialSource(profile);
		using (LiquidRpcAuthenticationLease first = cookies.Acquire())
		{
			Assert.Equal("node-user", first.Username.ToString());
			Assert.Equal("node-password", first.Password.ToString());
		}
		File.WriteAllText(cookiePath, "rotated-user:rotated-password\n", new UTF8Encoding(false));
		SetOwnerOnly(cookiePath);
		using LiquidRpcAuthenticationLease second = cookies.Acquire();
		Assert.Equal("rotated-user", second.Username.ToString());
		Assert.Equal("rotated-password", second.Password.ToString());
	}

	[Theory]
	[InlineData("http://localhost:18884")]
	[InlineData("http://192.0.2.1:18884")]
	[InlineData("https://127.0.0.1:18884")]
	public void RejectsNonLoopbackOrNonHttpEndpoint(string endpoint)
	{
		using var directory = new TemporaryDirectory();
		string profileDirectory = Path.Combine(directory.Path, "liquid-rpc-profiles");
		Directory.CreateDirectory(profileDirectory);
		string cookiePath = Path.Combine(directory.Path, "elements.cookie");
		File.WriteAllText(cookiePath, "user:password\n");
		SetOwnerOnly(cookiePath);
		string escapedCookiePath = cookiePath.Replace("\\", "\\\\", StringComparison.Ordinal);
		File.WriteAllText(Path.Combine(profileDirectory, "bad.json"), "{\"schema\":\"walletwasabi-liquid-rpc-profile/v1\",\"name\":\"bad\",\"endpoint\":\"" + endpoint + "\",\"cookieFile\":\"" + escapedCookiePath + "\",\"network\":\"elementsregtest\",\"manifest\":\"elements-regtest\",\"connectTimeoutMs\":1000,\"requestTimeoutMs\":5000}");
		SetOwnerOnly(Path.Combine(profileDirectory, "bad.json"));

		var source = new LiquidRpcProfileSource(directory.Path);
		Assert.Throws<InvalidDataException>(() => source.LoadValidated("bad"));
	}

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wasabi-liquid-rpc-" + Guid.NewGuid().ToString("N"));
		internal string Path { get; }
		public void Dispose()
		{
			if (Directory.Exists(Path))
				Directory.Delete(Path, recursive: true);
		}
	}

	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	private static void SetOwnerOnly(string path)
	{
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}
}

#pragma warning restore CA1416
