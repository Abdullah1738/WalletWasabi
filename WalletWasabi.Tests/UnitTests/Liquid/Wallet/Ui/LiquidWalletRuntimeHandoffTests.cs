using System;
using System.Linq;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

public sealed class LiquidWalletRuntimeHandoffTests
{
	[Fact]
	public void HandoffPublicSurfaceContainsNoDelegateDisposalOrProviderAuthority()
	{
		Type type = typeof(LiquidWalletRuntimeHandoff);
		Assert.DoesNotContain(type.GetProperties(), property => typeof(Delegate).IsAssignableFrom(property.PropertyType));
		Assert.DoesNotContain(type.GetMethods(), method => method.Name.Contains("Dispose", StringComparison.Ordinal));
		Assert.DoesNotContain(type.GetProperties(), property => property.PropertyType.Name.Contains("Provider", StringComparison.Ordinal));
		Assert.Equal(new[] { "Balances", "CanonicalWalletId", "History", "NetworkManifestId", "ReceiveMaterial", "SelectableOutputs" }, type.GetProperties().Select(x => x.Name).OrderBy(x => x).ToArray());
	}
}
