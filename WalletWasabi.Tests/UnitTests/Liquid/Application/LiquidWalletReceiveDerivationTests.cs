using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NBitcoin;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public class LiquidWalletReceiveDerivationTests
{
	// Reference vectors published in Bitcoin Core doc/descriptors.md: each body maps to its
	// canonical 8-character checksum suffix. They pin the local BCH implementation against
	// externally published expectations so a shared regression cannot cancel out.
	[Theory]
	[InlineData("raw(dead)", "j7p6x6xf")]
	[InlineData("wpkh(03fff97bd5755eeea420453a14355235d382f6472f8568a18b2f057a1460297556)", "ytdtss9h")]
	[InlineData("sh(wsh(pkh(02e493dbf1c10d80f3581e4904930b1404cc6c13900ee0758474fa94abe8c4cd13)))", "2wtr0ej5")]
	[InlineData("wsh(and_v(v:pk(03a34b99f22c790c4e36b2b3c2c35a36db06226e41c692fc82b8b56ac1c540c5bd),older(100)))", "6a2sqgd4")]
	[InlineData("wsh(multi(1,03a34b99f22c790c4e36b2b3c2c35a36db06226e41c692fc82b8b56ac1c540c5bd,04a34b99f22c790c4e36b2b3c2c35a36db06226e41c692fc82b8b56ac1c540c5bd))", "gj5gxexw")]
	[InlineData("combo(0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798)", "lq9sf04s")]
	[InlineData("pkh(02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5)", "8fhd9pwu")]
	public void DescriptorChecksumMatchesPublishedReferenceVectors(string body, string expectedSuffix)
	{
		string checksummed = LiquidDescriptorChecksum.AppendChecksum(body);

		Assert.Equal(body + "#" + expectedSuffix, checksummed);
		// Independent cross-check: a second, locally scoped reimplementation must agree.
		Assert.Equal(expectedSuffix, ComputeReferenceChecksumSuffix(body));
	}

	[Fact]
	public void CreatedDescriptorCarriesOneExactCanonicalChecksum()
	{
		using Key rootKey = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
		ExtKey master = new(rootKey, Enumerable.Repeat((byte)0x24, 32).ToArray());

		LiquidWalletReceiveDerivation derivation = LiquidWalletReceiveDerivation.Create(master, NBitcoin.Network.TestNet, account: 0, externalIndex: 7);

		string descriptor = derivation.Descriptor;
		int separator = descriptor.IndexOf('#');
		Assert.True(separator > 0);
		Assert.Equal(descriptor.LastIndexOf('#'), separator);
		Assert.Equal(8, descriptor.Length - separator - 1);
		Assert.All(descriptor[(separator + 1)..], c => Assert.Contains(c, "qpzry9x8gf2tvdw0s3jn54khce6mua7l"));

		string body = descriptor[..separator];
		Assert.Equal($"elwpkh({master.Derive(new KeyPath("2089617494h/1984574463h/0h")).Neuter().ToString(NBitcoin.Network.TestNet)}/<0;1>/*)", body);
		Assert.Equal(ComputeReferenceChecksumSuffix(body), descriptor[(separator + 1)..]);
	}

	[Fact]
	public void DerivationScriptAndIndexAreUnchangedByChecksumAppending()
	{
		using Key rootKey = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
		ExtKey master = new(rootKey, Enumerable.Repeat((byte)0x24, 32).ToArray());
		LiquidWalletReceiveDerivation derivation = LiquidWalletReceiveDerivation.Create(master, NBitcoin.Network.TestNet, account: 0, externalIndex: 7);
		ExtKey account = master.Derive(new KeyPath("2089617494h/1984574463h/0h"));
		byte[] expectedScript = account.Neuter().Derive(0).Derive(7).PubKey.WitHash.ScriptPubKey.ToBytes();

		Assert.Equal(expectedScript, derivation.ScriptPubKey);
		Assert.Equal(7UL, derivation.LastIndex);
		Assert.Equal($"elwpkh({account.Neuter().ToString(NBitcoin.Network.TestNet)}/<0;1>/*)", derivation.Descriptor.Split('#')[0]);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void DescriptorKeyClassMatchesTheRequestedNetwork(bool mainnet)
	{
		NBitcoin.Network network = mainnet ? NBitcoin.Network.Main : NBitcoin.Network.TestNet;
		using Key rootKey = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
		ExtKey master = new(rootKey, Enumerable.Repeat((byte)0x22, 32).ToArray());

		LiquidWalletReceiveDerivation derivation = LiquidWalletReceiveDerivation.Create(master, network, account: 0, externalIndex: 0);

		string body = derivation.Descriptor.Split('#')[0];
		string accountKey = body.Substring("elwpkh(".Length, body.Length - "elwpkh(".Length - "/<0;1>/*)".Length);
		if (mainnet)
		{
			Assert.StartsWith("xpub", accountKey);
			Assert.DoesNotContain("tpub", accountKey, StringComparison.Ordinal);
		}
		else
		{
			Assert.StartsWith("tpub", accountKey);
		}

		Assert.Equal(master.Derive(new KeyPath("2089617494h/1984574463h/0h")).Neuter().ToString(network), accountKey);
	}

	[Fact]
	public void ProducedDescriptorIsAdmittedByTheWireStructuralCodecAtIndexZero()
	{
		using Key rootKey = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
		ExtKey master = new(rootKey, Enumerable.Repeat((byte)0x24, 32).ToArray());
		LiquidWalletReceiveDerivation derivation = LiquidWalletReceiveDerivation.Create(master, NBitcoin.Network.TestNet, account: 0, externalIndex: 0);
		byte[] sourceEpoch = Enumerable.Repeat((byte)0x41, 32).ToArray();
		byte[] transaction = [0x01, 0x02, 0x03];
		var candidate = new LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource(
			transaction,
			Array.Empty<ReadOnlyMemory<byte>>());

		bool success = LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
			sourceEpoch,
			LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
			lastDerivationIndex: 0,
			Encoding.ASCII.GetBytes(derivation.Descriptor),
			[candidate],
			out LiquidWalletFactsWireV1UnpreparedRequestFrame? frame,
			out LiquidWalletFactsWireErrorCode errorCode);

		try
		{
			Assert.True(success);
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1UnpreparedRequestFrame owned = Assert.IsType<LiquidWalletFactsWireV1UnpreparedRequestFrame>(frame);
			byte[] bytes = new byte[owned.Length];
			owned.CopyFrameTo(bytes);
			Assert.Equal((byte)'W', bytes[0]);
			Assert.Equal((byte)'L', bytes[1]);
			Assert.Equal((byte)'F', bytes[2]);
			Assert.Equal((byte)'Q', bytes[3]);
			Assert.True(bytes.Length > 76 + derivation.Descriptor.Length + transaction.Length);
		}
		finally
		{
			frame?.Dispose();
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MissingOrCorruptedChecksumRemainsRejectedByTheWireStructuralCodec(bool corrupted)
	{
		using Key rootKey = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
		ExtKey master = new(rootKey, Enumerable.Repeat((byte)0x24, 32).ToArray());
		LiquidWalletReceiveDerivation derivation = LiquidWalletReceiveDerivation.Create(master, NBitcoin.Network.TestNet, account: 0, externalIndex: 0);
		// The structural codec requires exactly '#' plus 8 bech32-charset checksum characters.
		// Missing: drop the suffix entirely. Corrupted: replace one checksum character with '!',
		// which is outside the checksum alphabet, so the shape stays but the charset fails.
		string descriptor = corrupted
			? derivation.Descriptor[..^1] + "!"
			: derivation.Descriptor.Split('#')[0];
		byte[] sourceEpoch = Enumerable.Repeat((byte)0x41, 32).ToArray();

		bool success = LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
			sourceEpoch,
			LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
			lastDerivationIndex: 0,
			Encoding.ASCII.GetBytes(descriptor),
			[],
			out LiquidWalletFactsWireV1UnpreparedRequestFrame? frame,
			out LiquidWalletFactsWireErrorCode errorCode);

		using (frame)
		{
			Assert.False(success);
			Assert.Null(frame);
			Assert.Equal(LiquidWalletFactsWireErrorCode.InvalidEncoding, errorCode);
		}
	}

	// Independent reimplementation of the Bitcoin Core descsum_create algorithm used only to
	// cross-check the production helper against freshly computed expectations in this file.
	private static string ComputeReferenceChecksumSuffix(string body)
	{
		const string inputCharset =
			"0123456789()[],'/*abcdefgh@:$%{}IJKLMNOPQRSTUVWXYZ&+-.;<=>?!^_|~ijklmnopqrstuvwxyzABCDEFGH`#\"\\ ";
		const string checksumCharset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
		ulong[] generator = [0xf5dee51989UL, 0xa9fdca3312UL, 0x1bab10e32dUL, 0x3706b1677aUL, 0x644d626ffdUL];

		ulong PolyMod(ulong c, ulong value)
		{
			byte top = (byte)(c >> 35);
			c = ((c & 0x7ffffffffUL) << 5) ^ value;
			for (int index = 0; index < generator.Length; index++)
			{
				if (((top >> index) & 1) != 0)
				{
					c ^= generator[index];
				}
			}

			return c;
		}

		var symbols = new List<ulong>();
		var groups = new List<int>();
		foreach (char ch in body)
		{
			int value = inputCharset.IndexOf(ch);
			symbols.Add((ulong)(value & 31));
			groups.Add(value >> 5);
			if (groups.Count == 3)
			{
				symbols.Add((ulong)(groups[0] * 9 + groups[1] * 3 + groups[2]));
				groups.Clear();
			}
		}

		if (groups.Count == 1)
		{
			symbols.Add((ulong)groups[0]);
		}
		else if (groups.Count == 2)
		{
			symbols.Add((ulong)(groups[0] * 3 + groups[1]));
		}

		for (int index = 0; index < 8; index++)
		{
			symbols.Add(0);
		}

		ulong c = 1;
		foreach (ulong symbol in symbols)
		{
			c = PolyMod(c, symbol);
		}

		c ^= 1;
		var suffix = new StringBuilder(8);
		for (int position = 0; position < 8; position++)
		{
			suffix.Append(checksumCharset[(int)((c >> (5 * (7 - position))) & 31UL)]);
		}

		return suffix.ToString();
	}
}
