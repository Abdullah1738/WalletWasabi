namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 sections 5 and 9; 2026-08-21 amendment section 8):
/// the typed session source the Client composition root passes to the WalletWasabi-resident
/// send-execution command service's public static <c>CreateSendCommand</c>. The command service
/// resolves the open authenticated session for a named wallet through this typed surface at
/// scope-open time; it never re-reads a file, never asks the caller, and never uses a service
/// locator. This interface is public so the Client composition root can name it across the
/// assembly boundary with no <c>InternalsVisibleTo</c>; its only implementation is the internal
/// Client authenticated runtime provider. The composition root stores only the returned public
/// delegate.
/// </summary>
public interface ILiquidWalletSendSessionSource
{
	/// <summary>
	/// The manifest identity this source's wallets are bound to (non-secret). The composition
	/// resolves the reviewed <c>ElementsPublicNetworkManifest</c> for the send command from this
	/// id at composition time.
	/// </summary>
	string ManifestId { get; }

	/// <summary>
	/// Resolves the live authenticated session for one canonical wallet id, or
	/// <see langword="null"/> when no session is open. The returned session is never
	/// disposed by the caller.
	/// </summary>
	ILiquidWalletSendSession? TryGetOpenSession(string canonicalWalletId);
}
