using System;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Client.Liquid;

/// <summary>
/// The Client-owned composition root for the Liquid application path: it owns the
/// authenticated-runtime provider (through the landed composition) and the
/// handoff published into the application. No secret passes through this type;
/// the wallet-open password lease is supplied by the caller at open time. The
/// handoff holder starts null and is assigned exactly once when a wallet session
/// publishes its runtime handoff.
/// </summary>
internal sealed class LiquidApplicationCompositionRoot
{
	internal LiquidApplicationCompositionRoot(LiquidWalletRuntimeComposition composition, LiquidWalletRuntimeHandoffHolder handoff)
	{
		Composition = composition ?? throw new ArgumentNullException(nameof(composition));
		Handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
	}

	internal LiquidWalletRuntimeComposition Composition { get; }
	internal LiquidWalletRuntimeHandoffHolder Handoff { get; }
}

/// <summary>
/// The assignment-only holder for the Liquid wallet runtime handoff. The
/// application getter reads through this holder; it is null until a wallet
/// session opens and publishes its handoff, and it can be assigned at most once.
/// </summary>
internal sealed class LiquidWalletRuntimeHandoffHolder
{
	private LiquidWalletRuntimeHandoff? _handoff;

	internal LiquidWalletRuntimeHandoff? Value => _handoff;

	internal void Publish(LiquidWalletRuntimeHandoff handoff)
	{
		ArgumentNullException.ThrowIfNull(handoff);
		if (System.Threading.Interlocked.CompareExchange(ref _handoff, handoff, null) is not null)
		{
			throw new InvalidOperationException("The Liquid wallet runtime handoff has already been published.");
		}
	}
}
