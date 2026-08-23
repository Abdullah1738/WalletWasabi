using System;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>Immutable public receive material derived by the authenticated Client key owner.</summary>
public sealed class LiquidWalletUiReceiveMaterial
{
	private readonly byte[] _nextReceiveScriptPubKey;
	private readonly byte[] _nextReceiveBlindingPublicKey;

	public LiquidWalletUiReceiveMaterial(
		ReadOnlySpan<byte> nextReceiveScriptPubKey,
		ReadOnlySpan<byte> nextReceiveBlindingPublicKey)
	{
		if (nextReceiveScriptPubKey.IsEmpty)
		{
			throw new ArgumentException("A non-empty next receive script is required.", nameof(nextReceiveScriptPubKey));
		}
		if (nextReceiveBlindingPublicKey.Length != 33)
		{
			throw new ArgumentException("An exact compressed next receive blinding public key is required.", nameof(nextReceiveBlindingPublicKey));
		}

		_nextReceiveScriptPubKey = nextReceiveScriptPubKey.ToArray();
		_nextReceiveBlindingPublicKey = nextReceiveBlindingPublicKey.ToArray();
	}

	public byte[] NextReceiveScriptPubKey => [.. _nextReceiveScriptPubKey];
	public byte[] NextReceiveBlindingPublicKey => [.. _nextReceiveBlindingPublicKey];
}
