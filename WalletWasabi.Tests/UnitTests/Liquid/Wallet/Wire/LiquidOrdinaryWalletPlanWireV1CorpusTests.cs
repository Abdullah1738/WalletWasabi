using Xunit;
using Xunit.Sdk;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;

public class LiquidOrdinaryWalletPlanWireV1CorpusTests
{
	private const string FirstDigest = "0000000000000000000000000000000000000000000000000000000000000000";
	private const string SecondDigest = "1111111111111111111111111111111111111111111111111111111111111111";

	[Fact]
	public void ExactImportedCorpusIsClosedAndAuthentic()
	{
		OrdinaryWalletPlanWireV1Corpus.AssertAuthenticPacket();
	}

	[Fact]
	public void InventoryRowsMustBeStrictlyIncreasingAndExactlyDelimited()
	{
		string reordered = $"{FirstDigest}  b\n{SecondDigest}  a\n";
		string duplicated = $"{FirstDigest}  a\n{SecondDigest}  a\n";
		string thirdSpace = $"{FirstDigest}   a\n";

		Assert.ThrowsAny<XunitException>(() => OrdinaryWalletPlanWireV1Corpus.ParseInventory(reordered));
		Assert.ThrowsAny<XunitException>(() => OrdinaryWalletPlanWireV1Corpus.ParseInventory(duplicated));
		Assert.ThrowsAny<XunitException>(() => OrdinaryWalletPlanWireV1Corpus.ParseInventory(thirdSpace));
	}
}
