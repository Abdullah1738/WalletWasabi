using System;
using System.IO;
using WalletWasabi.Client;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Client;

public sealed class ApplicationRuntimeSelectorTests
{
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

	private sealed class SentinelException : Exception;
}
