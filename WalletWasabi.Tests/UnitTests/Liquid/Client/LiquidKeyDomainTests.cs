using System;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Client.Liquid;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidKeyDomainTests
{
	[Fact]
	public void UsesFrozenHardenedLabelIndicesAndSeparatedHkdfDomains()
	{
		Assert.Equal(2089617494U, LiquidKeyDomain.Index("WalletWasabi/Liquid/v1"));
		Assert.Equal(1984574463U, LiquidKeyDomain.Index("spend"));
		Assert.Equal(1786312740U, LiquidKeyDomain.Index("slip77"));
		Assert.Equal(1108790945U, LiquidKeyDomain.Index("replay-context"));

		byte[] root = EnumerableBytes(32, 0x11);
		byte[] salt = EnumerableBytes(32, 0x22);
		byte[] slip77 = LiquidKeyDomain.DeriveHkdf(root, salt, "WalletWasabi/Liquid/v1/slip77");
		byte[] replay = LiquidKeyDomain.DeriveHkdf(root, salt, "WalletWasabi/Liquid/v1/replay");
		Assert.Equal(32, slip77.Length);
		Assert.Equal(32, replay.Length);
		Assert.NotEqual(Convert.ToHexString(slip77), Convert.ToHexString(replay));
		CryptographicOperations.ZeroMemory(root);
		CryptographicOperations.ZeroMemory(salt);
		CryptographicOperations.ZeroMemory(slip77);
		CryptographicOperations.ZeroMemory(replay);
	}

	private static byte[] EnumerableBytes(int count, byte value)
	{
		byte[] result = new byte[count];
		Array.Fill(result, value);
		return result;
	}
}
