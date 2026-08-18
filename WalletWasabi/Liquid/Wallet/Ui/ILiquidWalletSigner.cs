namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The caller-owned Liquid signing boundary: the managed analogue of the
/// native <c>OrdinaryP2wpkhSigner</c> trait. The implementation owns its own
/// keys; this seam never receives, and no implementation may return, a
/// secret key. It is the seam a test double satisfies today and a
/// separately-reviewed production native FFI binding satisfies tomorrow (a
/// managed wrapper over the native <c>OrdinaryP2wpkhSigner</c> callback
/// surface). Every method is fail-closed: a <see langword="null"/> return is
/// a refusal, never a partial or substituted result.
/// </summary>
public interface ILiquidWalletSigner
{
	/// <summary>
	/// Returns the compressed 33-byte public key (66-character lowercase hex)
	/// expected to own the named input, or <see langword="null"/> for a
	/// fail-closed refusal. Called once per input before any digest is
	/// requested.
	/// </summary>
	string? GetPublicKeyHex(string outPointHex);

	/// <summary>
	/// Returns the strict-DER low-S signature (lowercase hex, including the
	/// sighash byte) over the named 32-byte digest (64-character lowercase
	/// hex) for the named input, or <see langword="null"/> for a fail-closed
	/// refusal. The digest is computed by the caller from the same wire frame
	/// the native engine validates; this seam never computes it.
	/// </summary>
	string? SignDigestHex(string outPointHex, string digestHex);
}
