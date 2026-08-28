using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using WalletWasabi.Client;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Client;

public sealed class ApplicationRuntimeSelectorTests
{
	[Theory]
	[InlineData("bitcoin")]
	[InlineData("liquid-mainnet")]
	[InlineData("liquid-testnet")]
	public void RealBuilderConstructsRuntimeInFreshChild(string mode)
	{
		string dataDirectory = Path.Combine(Path.GetTempPath(), $"runtime-selector-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dataDirectory);
		try
		{
			using JsonDocument output = RoslynFreshChildHarness.RunChild(
				FreshChildAssemblyPath.Value,
				new { mode, dataDirectory });
			JsonElement result = output.RootElement;
			Assert.Equal("APPLICATION_RUNTIME_SELECTOR_V1", result.GetProperty("token").GetString());
			Assert.Equal(mode, result.GetProperty("mode").GetString());
			Assert.True(result.GetProperty("runtimeNullBeforeOpen").GetBoolean());
			Assert.True(result.GetProperty("terminated").GetBoolean());

			if (mode == "bitcoin")
			{
				Assert.Equal(JsonValueKind.Null, result.GetProperty("sendDelegateStable").ValueKind);
				Assert.Equal(JsonValueKind.Null, result.GetProperty("reviewedManifestId").ValueKind);
			}
			else
			{
				Assert.True(result.GetProperty("sendDelegateStable").GetBoolean());
				string expectedManifestId = mode == "liquid-mainnet"
					? ElementsPublicNetworkManifest.LiquidMainnet.ManifestId
					: ElementsPublicNetworkManifest.LiquidTestnet.ManifestId;
				Assert.Equal(expectedManifestId, result.GetProperty("reviewedManifestId").GetString());
			}
		}
		finally
		{
			Directory.Delete(dataDirectory, recursive: true);
		}
	}

	[Fact]
	public void ParameterlessBuildLiquidFailsBeforeApplicationSideEffects()
	{
		WasabiAppBuilder builder = WasabiAppBuilder.Create("test", []);
		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => builder.BuildLiquid());
		Assert.Contains("explicit reviewed Liquid manifest ID", exception.Message);
	}

	[Fact]
	public void LiquidRuntimeSelectorWithoutManifestFailsClosed()
	{
		WasabiAppBuilder builder = WasabiAppBuilder.Create("test", []);
		Assert.Throws<InvalidOperationException>(() => builder.Build((ApplicationRuntime)2));
	}

	[Fact]
	public void BuildLiquidRejectsUnreviewedManifest()
	{
		WasabiAppBuilder builder = WasabiAppBuilder.Create("test", []);
		Assert.Throws<ElementsNetworkManifestException>(() => builder.BuildLiquid("elements-regtest"));
	}

	[Fact]
	public void BuildLiquidRequiresExplicitReviewedManifestId()
	{
		MethodInfo method = Assert.Single(
			typeof(WasabiAppBuilder).GetMethods(BindingFlags.Public | BindingFlags.Instance),
			candidate => candidate.Name == nameof(WasabiAppBuilder.BuildLiquid) && candidate.GetParameters().Length == 1);
		ParameterInfo parameter = Assert.Single(method.GetParameters());
		Assert.Equal(typeof(string), parameter.ParameterType);
		Assert.Equal("reviewedManifestId", parameter.Name);
	}

	[Fact]
	public void BitcoinForwardersFailClosed()
	{
		WasabiApplication application = (WasabiApplication)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WasabiApplication));
		Assert.Null(application.LiquidWalletRuntime);
		Assert.Throws<NotSupportedException>(() => application.CreateLiquidWalletOpenAuthorization("secret"));
		Assert.Throws<NotSupportedException>(() => application.OpenLiquidWalletAsync(null!, null!, default));
		Assert.Throws<NotSupportedException>(() => application.CloseLiquidWalletAsync(null!, default));
		Assert.Throws<NotSupportedException>(() => application.LiquidWalletSendCommand);
	}

	[Fact]
	public void UnsupportedRuntimeDoesNotInvokeBitcoinFactory()
	{
		bool invoked = false;
		WasabiAppBuilder builder = WasabiAppBuilder.Create("test", []);

		Assert.Throws<ArgumentOutOfRangeException>(() => builder.SelectApplication<object>((ApplicationRuntime)int.MaxValue, () => { invoked = true; throw new InvalidOperationException(); }));
		Assert.False(invoked);
	}

	[Fact]
	public void BitcoinRuntimeInvokesProvidedFactory()
	{
		WasabiAppBuilder builder = WasabiAppBuilder.Create("test", []);
		Assert.Throws<SentinelException>(() => builder.SelectApplication<object>(ApplicationRuntime.Bitcoin, () => throw new SentinelException()));
	}

	private static readonly Lazy<string> FreshChildAssemblyPath = new(CompileFreshChildAssembly);

	private sealed class SentinelException : Exception;

	private static string CompileFreshChildAssembly()
	{
		string coreAssembly = typeof(ElementsPublicNetworkManifest).Assembly.Location;
		string clientAssembly = typeof(WasabiAppBuilder).Assembly.Location;
		string childPath = RoslynFreshChildHarness.CompileChildAssembly(
			"""
			using System;
			using System.Reflection;
			using System.Text.Json;
			using WalletWasabi.Client;
			using WalletWasabi.Liquid.Network;

			using JsonDocument input = JsonDocument.Parse(Console.In.ReadToEnd());
			string mode = input.RootElement.GetProperty("mode").GetString()!;
			string dataDirectory = input.RootElement.GetProperty("dataDirectory").GetString()!;
			Environment.SetEnvironmentVariable("WASABI_DATADIR", dataDirectory);

			bool terminated = false;
			WasabiAppBuilder builder = WasabiAppBuilder
				.Create("runtime-selector-child", ["--LogModes=File"])
				.OnTermination(() => terminated = true);
			WasabiApplication application = mode switch
			{
				"bitcoin" => builder.Build(),
				"liquid-mainnet" => builder.BuildLiquid(ElementsPublicNetworkManifest.LiquidMainnet.ManifestId),
				"liquid-testnet" => builder.BuildLiquid(ElementsPublicNetworkManifest.LiquidTestnet.ManifestId),
				_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported child mode."),
			};

			bool runtimeNullBeforeOpen = application.LiquidWalletRuntime is null;
			bool? sendDelegateStable = null;
			string? reviewedManifestId = null;
			if (mode != "bitcoin")
			{
				sendDelegateStable = ReferenceEquals(application.LiquidWalletSendCommand, application.LiquidWalletSendCommand);
				FieldInfo compositionField = typeof(WasabiApplication)
					.GetField("_liquidComposition", BindingFlags.Instance | BindingFlags.NonPublic)!;
				object composition = compositionField.GetValue(application)!;
				object applicationClient = composition.GetType()
					.GetProperty("ApplicationClient", BindingFlags.Instance | BindingFlags.NonPublic)!
					.GetValue(composition)!;
				object options = applicationClient.GetType()
					.GetProperty("Options", BindingFlags.Instance | BindingFlags.NonPublic)!
					.GetValue(applicationClient)!;
				reviewedManifestId = (string)options.GetType()
					.GetProperty("ReviewedManifestId")!
					.GetValue(options)!;
			}

			application.TerminateService.Terminate();
			Console.Write(JsonSerializer.Serialize(new
			{
				token = "APPLICATION_RUNTIME_SELECTOR_V1",
				mode,
				runtimeNullBeforeOpen,
				sendDelegateStable,
				reviewedManifestId,
				terminated,
			}));
			""",
			"application-runtime-selector-child",
			"ApplicationRuntimeSelectorChild.dll",
			[coreAssembly, clientAssembly]);

		string childDirectory = Path.GetDirectoryName(childPath)!;
		foreach (string dependencyPath in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
		{
			string fileName = Path.GetFileName(dependencyPath);
			if (fileName == "WalletWasabi.Tests.dll")
			{
				continue;
			}

			File.Copy(dependencyPath, Path.Combine(childDirectory, fileName), overwrite: true);
		}

		string nativeRuntimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
		if (Directory.Exists(nativeRuntimeDirectory))
		{
			foreach (string nativeDependencyPath in Directory.EnumerateFiles(nativeRuntimeDirectory))
			{
				File.Copy(
					nativeDependencyPath,
					Path.Combine(childDirectory, Path.GetFileName(nativeDependencyPath)),
					overwrite: true);
			}
		}

		File.Copy(coreAssembly, Path.Combine(childDirectory, "WalletWasabi.dll"), overwrite: true);
		File.Copy(clientAssembly, Path.Combine(childDirectory, "WalletWasabi.Client.dll"), overwrite: true);
		return childPath;
	}
}
