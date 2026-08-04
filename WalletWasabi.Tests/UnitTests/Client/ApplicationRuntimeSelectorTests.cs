using System.IO;
using System.Reflection;
using WalletWasabi.Client;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Client;

public class ApplicationRuntimeSelectorTests
{
	[Fact]
	public void BitcoinInvokesFactoryExactlyOnce()
	{
		var expected = new object();
		int invocationCount = 0;

		object actual = WasabiAppBuilder.SelectApplication(
			ApplicationRuntime.Bitcoin,
			() =>
			{
				invocationCount++;
				return expected;
			});

		Assert.Same(expected, actual);
		Assert.Equal(1, invocationCount);
	}

	[Fact]
	public void LiquidRejectsBeforeInvokingFactory()
	{
		string sentinelPath = NewSentinelPath();

		Assert.Throws<NotSupportedException>(
			() => WasabiAppBuilder.SelectApplication(ApplicationRuntime.Liquid, PoisonFactory(sentinelPath)));

		Assert.False(Directory.Exists(sentinelPath));
	}

	[Theory]
	[InlineData((int)ApplicationRuntime.Unspecified)]
	[InlineData(int.MaxValue)]
	public void InvalidRuntimeRejectsBeforeInvokingFactory(int runtimeValue)
	{
		string sentinelPath = NewSentinelPath();
		var runtime = (ApplicationRuntime)runtimeValue;

		var exception = Assert.Throws<ArgumentOutOfRangeException>(
			() => WasabiAppBuilder.SelectApplication(runtime, PoisonFactory(sentinelPath)));

		Assert.Equal("runtime", exception.ParamName);
		Assert.False(Directory.Exists(sentinelPath));
	}

	[Fact]
	public void RuntimeSelectionSurfaceIsNotPublic()
	{
		Type builderType = typeof(WasabiAppBuilder);
		MethodInfo publicBuild = Assert.Single(
			builderType.GetMethods(BindingFlags.Instance | BindingFlags.Public),
			method => method.Name == nameof(WasabiAppBuilder.Build));

		Assert.True(typeof(ApplicationRuntime).IsNotPublic);
		Assert.Empty(publicBuild.GetParameters());
		Assert.Equal(typeof(WasabiApplication), publicBuild.ReturnType);
		Assert.NotNull(
			builderType.GetMethod(
				nameof(WasabiAppBuilder.Build),
				BindingFlags.Instance | BindingFlags.NonPublic,
				binder: null,
				types: [typeof(ApplicationRuntime)],
				modifiers: null));
		Assert.Contains(
			builderType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
			method => method.Name == "SelectApplication" && method.IsGenericMethodDefinition);
	}

	private static Func<object> PoisonFactory(string sentinelPath) =>
		() =>
		{
			Directory.CreateDirectory(sentinelPath);
			return new object();
		};

	private static string NewSentinelPath() =>
		Path.Combine(Path.GetTempPath(), $"wasabi-runtime-selector-{Guid.NewGuid():N}");
}
