using System.IO;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// The pure Liquid wallet data-directory convention. One Liquid-specific
/// subdirectory, named <c>Liquid</c>, directly under the caller-supplied
/// Wasabi wallet work directory (the same <c>workDir</c> the landed
/// <see cref="Wallets.WalletDirectories"/> constructor takes). The
/// <c>.lwwal</c> wallet state files live under that subdirectory, keeping
/// them out of the BTC <c>Wallets/</c> JSON enumeration
/// (<see cref="Wallets.WalletDirectories.EnumerateWalletFiles"/> scans only
/// its own <c>WalletsDir</c> for <c>*.json</c>, so a <c>Liquid/</c> sibling
/// subdirectory is never mistaken for a BTC wallet). This type performs no
/// I/O (it does not create the directory — the landed
/// <see cref="Io.SafeFile"/> write path creates the containing directory via
/// <see cref="Helpers.IoHelpers.EnsureContainingDirectoryExists"/>), no
/// canonicalization, and no confinement check; it is a pure path-composition
/// helper. The caller owns the choice of <paramref name="walletsWorkDir"/>.
/// </summary>
internal static class LiquidWalletDataDir
{
	private const string LiquidWalletDataDirName = "Liquid";

	/// <summary>
	/// Returns the Liquid wallet data directory under the supplied Wasabi
	/// wallet work directory. Validates that <paramref name="walletsWorkDir"/>
	/// is non-null and non-empty.
	/// </summary>
	public static string GetLiquidWalletDataDir(string walletsWorkDir)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletsWorkDir);
		return Path.Combine(walletsWorkDir, LiquidWalletDataDirName);
	}
}
