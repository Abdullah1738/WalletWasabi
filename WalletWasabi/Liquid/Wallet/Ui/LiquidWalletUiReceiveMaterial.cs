using System;
using System.Collections.Generic;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>Immutable public receive material derived by the authenticated Client key owner.</summary>
public sealed class LiquidWalletUiReceiveMaterial
{
	private readonly byte[] _nextReceiveScriptPubKey;
	private readonly byte[] _nextReceiveBlindingPublicKey;
	private readonly string[] _nextReceiveLabels;

	public LiquidWalletUiReceiveMaterial(
		ReadOnlySpan<byte> nextReceiveScriptPubKey,
		ReadOnlySpan<byte> nextReceiveBlindingPublicKey,
		IReadOnlyList<string>? nextReceiveLabels = null)
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
		if (nextReceiveLabels is null)
		{
			_nextReceiveLabels = [];
		}
		else
		{
			var labels = new string[nextReceiveLabels.Count];
			for (int index = 0; index < labels.Length; index++)
			{
				labels[index] = nextReceiveLabels[index]
					?? throw new ArgumentException("A next receive label cannot be null.", nameof(nextReceiveLabels));
			}
			_nextReceiveLabels = labels;
		}
	}

	public byte[] NextReceiveScriptPubKey => [.. _nextReceiveScriptPubKey];
	public byte[] NextReceiveBlindingPublicKey => [.. _nextReceiveBlindingPublicKey];

	/// <summary>
	/// The durable label set bound to the next receive derivation index, as an
	/// immutable list of label strings (empty when the address is unlabeled).
	/// </summary>
	public IReadOnlyList<string> NextReceiveLabels => [.. _nextReceiveLabels];
}
