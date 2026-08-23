using System;
using NBitcoin;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidWalletReceiveDerivation
{
	private const uint PurposeBranch = 2089617494;
	private const uint CoinTypeBranch = 1984574463;

	private LiquidWalletReceiveDerivation(string descriptor, ulong lastIndex, byte[] scriptPubKey)
	{
		Descriptor = descriptor;
		LastIndex = lastIndex;
		ScriptPubKey = scriptPubKey;
	}

	internal string Descriptor { get; }
	internal ulong LastIndex { get; }
	internal byte[] ScriptPubKey { get; }

	internal static LiquidWalletReceiveDerivation Create(ExtKey authenticatedMaster, Network network, int account, ulong externalIndex)
	{
		ArgumentNullException.ThrowIfNull(authenticatedMaster);
		ArgumentNullException.ThrowIfNull(network);
		if (account < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(account));
		}
		if (externalIndex > 0x7fffffffUL)
		{
			throw new InvalidOperationException("The Liquid external receive-index space is exhausted.");
		}

		ExtKey accountKey = authenticatedMaster.Derive(
			new KeyPath($"{PurposeBranch}h/{CoinTypeBranch}h/{account}h"));
		ExtPubKey accountPublicKey = accountKey.Neuter();
		ExtPubKey spendPublicKey = accountPublicKey.Derive(0).Derive((uint)externalIndex);
		string descriptor = $"elwpkh({accountPublicKey.ToString(network)}/<0;1>/*)";
		byte[] scriptPubKey = spendPublicKey.PubKey.WitHash.ScriptPubKey.ToBytes();
		return new LiquidWalletReceiveDerivation(descriptor, externalIndex, scriptPubKey);
	}
}
