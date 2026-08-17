using System.IO;

namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The pure file naming/directory convention for the on-disk Liquid wallet
/// persistence format. One file per wallet, named by the caller-chosen wallet
/// name, directly under the caller-supplied wallet data directory. The file
/// extension is <c>.lwwal</c> (Liquid wallet state). This type performs no
/// I/O, no canonicalization, and no confinement check; it is a pure
/// path-composition helper. The caller owns the choice of
/// <paramref name="walletDataDir"/>.
/// </summary>
internal static class LiquidWalletPersistencePaths
{
	private const string WalletStateFileExtension = ".lwwal";

	/// <summary>
	/// Returns the wallet state file path for one wallet under the supplied
	/// wallet data directory. Validates that <paramref name="walletDataDir"/>
	/// and <paramref name="walletName"/> are non-null and non-empty, and that
	/// <paramref name="walletName"/> contains no directory-separator or
	/// path-traversal characters (so the resolved path is always a direct
	/// child of <paramref name="walletDataDir"/>).
	/// </summary>
	public static string GetWalletStateFilePath(string walletDataDir, string walletName)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletDataDir);
		ArgumentException.ThrowIfNullOrEmpty(walletName);

		if (walletName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
			walletName.Contains(Path.DirectorySeparatorChar) ||
			walletName.Contains(Path.AltDirectorySeparatorChar) ||
			walletName.Contains("..", StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"The wallet name must not contain directory separators or path-traversal characters.",
				nameof(walletName));
		}

		return Path.Combine(walletDataDir, walletName + WalletStateFileExtension);
	}
}
