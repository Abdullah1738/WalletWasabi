using System;
using System.IO;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidWalletDirectories
{
	internal LiquidWalletDirectories(string walletDirectory)
	{
		if (!Path.IsPathFullyQualified(walletDirectory) || !Directory.Exists(walletDirectory))
		{
			throw new DirectoryNotFoundException(walletDirectory);
		}

		WalletDirectory = Path.GetFullPath(walletDirectory);
	}

	internal string WalletDirectory { get; }
}

internal sealed record LiquidWalletIdentity
{
	private LiquidWalletIdentity(
		string canonicalWalletId,
		string canonicalWalletFilePath,
		string runtimeProfileName,
		string networkManifestId)
	{
		CanonicalWalletId = canonicalWalletId;
		CanonicalWalletFilePath = canonicalWalletFilePath;
		RuntimeProfileName = runtimeProfileName;
		NetworkManifestId = networkManifestId;
	}

	internal string CanonicalWalletId { get; }
	internal string CanonicalWalletFilePath { get; }
	internal string RuntimeProfileName { get; }
	internal string NetworkManifestId { get; }

	internal static LiquidWalletIdentity Create(
		string walletId,
		string walletFilePath,
		string runtimeProfileName,
		string networkManifestId,
		LiquidWalletDirectories walletDirectories)
	{
		ArgumentNullException.ThrowIfNull(walletDirectories);
		string canonicalWalletId = RequireValue(walletId, nameof(walletId));
		string canonicalProfileName = RequireValue(runtimeProfileName, nameof(runtimeProfileName));
		string canonicalManifestId = RequireValue(networkManifestId, nameof(networkManifestId));
		string canonicalWalletFilePath = RequireRegularFileUnderDirectory(walletFilePath, walletDirectories.WalletDirectory);

		return new(canonicalWalletId, canonicalWalletFilePath, canonicalProfileName, canonicalManifestId);
	}

	private static string RequireValue(string value, string parameterName)
	{
		ArgumentNullException.ThrowIfNull(value, parameterName);
		string normalized = value.Trim();
		if (normalized.Length == 0)
		{
			throw new ArgumentException("A non-empty normalized value is required.", parameterName);
		}

		return normalized;
	}

	private static string RequireRegularFileUnderDirectory(string walletFilePath, string walletDirectory)
	{
		if (!Path.IsPathFullyQualified(walletFilePath))
		{
			throw new InvalidDataException("The wallet path must be absolute.");
		}

		string canonicalPath = Path.GetFullPath(walletFilePath);
		string canonicalRoot = EnsureTrailingSeparator(Path.GetFullPath(walletDirectory));
		if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.Ordinal)
			|| !File.Exists(canonicalPath)
			|| File.GetAttributes(canonicalPath).HasFlag(FileAttributes.ReparsePoint))
		{
			throw new InvalidDataException("The wallet path must be a regular file under the configured Liquid wallet directory.");
		}

		return canonicalPath;
	}

	private static string EnsureTrailingSeparator(string path) =>
		path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
