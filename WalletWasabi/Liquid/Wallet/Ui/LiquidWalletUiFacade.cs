using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The fail-closed, read-only presentation entry point the Fluent layer
/// calls: a transparent projection of the landed internal Liquid wallet
/// surface into the public immutable snapshot types of this namespace.
/// <see cref="LoadAndCaptureBalances"/> is the single public entry point
/// the Fluent/Desktop lifetime layer calls on wallet open — it composes the
/// landed <see cref="LiquidWalletLoadSave.Load"/> in-assembly (the internal
/// <see cref="LiquidWalletState"/> never crosses the assembly boundary) and
/// projects the restored state via <see cref="LiquidWalletUiSnapshot.Capture"/>.
/// <see cref="CaptureBalances"/> is the in-assembly composition point for
/// the WalletWasabi-side wallet-lifetime caller that already holds the
/// loaded state. <see cref="CreateReceiveAddress"/> composes the landed
/// <see cref="LiquidBlindingPublicKey.Create"/> +
/// <see cref="LiquidAddress.FromScriptPubKey"/> + the confidential-only
/// <see cref="LiquidWalletUiReceiveAddress.FromAddress"/> projection. This
/// facade performs no I/O beyond the landed <c>Load</c>, no node
/// connection, no sync, no key derivation, no formatting, and no caching;
/// every rejection surfaces with the landed exception surface — no retry,
/// no fallback, no cached-last-good-value substitution, no empty-snapshot
/// substitution, and no catch-and-rethrow remapping. The key, context,
/// script, and blinding-key spans are caller-supplied
/// <see cref="ReadOnlySpan{T}"/> values that cannot be captured or stored,
/// so the clearing obligation is structural.
/// </summary>
public static class LiquidWalletUiFacade
{
	/// <summary>
	/// Projects the already-loaded <paramref name="state"/> into an
	/// immutable display-ready snapshot. The <paramref name="state"/>
	/// reference is used only for the duration of the call and is never
	/// stored. Throws <see cref="ArgumentException"/> when the state's
	/// pegged asset does not match the manifest's (a wallet is never
	/// presented against the wrong network).
	/// </summary>
	internal static LiquidWalletUiSnapshot CaptureBalances(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletState state) =>
		LiquidWalletUiSnapshot.Capture(walletName, manifest, state);

	/// <summary>
	/// Derives and projects one confidential receive address from the
	/// caller-supplied next-receive script and blinding public key. The
	/// caller owns the script and blinding-key derivation (key management
	/// is outside this layer). Fail-closed: an empty script, or a
	/// non-33-byte, invalid, or uncompressed blinding key, or a
	/// non-confidential derivation, throws <see cref="ArgumentException"/>;
	/// a malformed or network-mismatched composition surfaces the landed
	/// codec exception.
	/// </summary>
	public static LiquidWalletUiReceiveAddress CreateReceiveAddress(
		ElementsPublicNetworkManifest manifest,
		ReadOnlySpan<byte> scriptPubKey,
		ReadOnlySpan<byte> blindingPublicKey)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		if (scriptPubKey.IsEmpty)
		{
			throw new ArgumentException(
				"A non-empty Liquid receive script is required.",
				nameof(scriptPubKey));
		}

		LiquidBlindingPublicKey blindingKey = LiquidBlindingPublicKey.Create(blindingPublicKey);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(manifest, scriptPubKey, blindingKey);
		return LiquidWalletUiReceiveAddress.FromAddress(address);
	}

	/// <summary>
	/// The single public entry point the Fluent/Desktop lifetime layer
	/// calls on Liquid wallet open: resolves the loaded state via the
	/// landed <see cref="LiquidWalletLoadSave.Load"/> and projects it via
	/// <see cref="LiquidWalletUiSnapshot.Capture"/>. Fail-closed exactly as
	/// the landed <c>Load</c>: a missing file, corrupt frame, wrong key,
	/// wrong context, or revision mismatch surfaces as the landed exception
	/// with no retry, no fallback, and no empty-snapshot substitution. The
	/// loaded state is used only for the projection and is not retained.
	/// </summary>
	public static LiquidWalletUiSnapshot LoadAndCaptureBalances(
		string walletDataDir,
		string walletName,
		ElementsPublicNetworkManifest manifest,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		ulong? expectedBaseRevision = null)
	{
		ArgumentNullException.ThrowIfNull(manifest);

		LiquidWalletLoadSaveResult result = LiquidWalletLoadSave.Load(
			walletDataDir,
			walletName,
			key,
			externalWalletNetworkContext,
			expectedBaseRevision);
		// Load always returns a non-null State; the null-forgiving operator
		// adds no runtime check and no fallback.
		return LiquidWalletUiSnapshot.Capture(walletName, manifest, result.State!);
	}
}
