using System.IO;
using System.Text;
using Xunit;
using WalletWasabi.Liquid.WalletFacts;
using WalletWasabi.Liquid.WalletFacts.Wire;

namespace WalletWasabi.Tests.UnitTests.Liquid.WalletFacts;

public class LiquidWalletNativeFactsObserverTests
{
	[Fact]
	public void PinnedIdentityAndCapsAreFrozen()
	{
		Assert.Equal(1u, LiquidWalletNativeFactsObserver.AbiVersionV1);
		Assert.Equal(268_435_456u, LiquidWalletNativeFactsObserver.MaxFrameBytesV1);
		Assert.Equal(80_599_492u, LiquidWalletNativeFactsObserver.MaxReachableResponseBytesV1);
		Assert.Equal("bd50133a9fbcac5d187757e634c1cc2fc65a10ac", LiquidWalletNativeFactsObserver.PinnedNativeCommit);
		Assert.Equal(64, LiquidWalletNativeFactsObserver.MacOsLibrarySha256.Length);
	}

	[Fact]
	public void UnsupportedLinuxArtifactFailsClosedWhenSelected()
	{
		if (OperatingSystem.IsLinux())
		{
			Assert.Throws<PlatformNotSupportedException>(LiquidWalletNativeFactsObserver.EnsurePinnedNativeArtifact);
		}
	}

	[Fact]
	public void LiveNativeObserveRejectsUnspendableCandidateSetDuringCapacityQuery()
	{
		if (!OperatingSystem.IsMacOS() || !File.Exists(LiquidWalletNativeFactsObserver.ResolveLibraryPath()))
		{
			return;
		}

		byte[] epoch = new byte[32];
		byte[] slip77MasterKey = new byte[32];
		epoch[0] = 1;
		slip77MasterKey[0] = 1;
		byte[] descriptor = Encoding.ASCII.GetBytes(
			"elwpkh([28b3f14e/84'/1'/0']tpubDC2Q4xK4XH72GM7MowNuajyWVbigRLBWKswyP5T88hpPwu5nGqJWnda8zhJEFt71av73Hm8mUMMFSz9acNVzz8b1UbdSHCDXKTbSv5eEytu/<0;1>/*)#u0khc0kg");

		var garbageCandidate = new LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource(
			new byte[] { 1 },
			[]);
		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletNativeFactsObserver.Observe(
				epoch,
				LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
				0,
				descriptor,
				[garbageCandidate],
				slip77MasterKey));

		// The capacity query performs the complete bounded semantic operation, so this native
		// observation rejection proves pinning, loading, request marshalling, and error propagation
		// without requiring a fabricated positive owned-output fixture.
		Assert.Equal("Native capacity query failed with status -7 and length 0.", exception.Message);
	}
}
