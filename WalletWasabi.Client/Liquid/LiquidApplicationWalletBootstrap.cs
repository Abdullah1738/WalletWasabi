using System;
using System.IO;

namespace WalletWasabi.Client.Liquid;

/// <summary>
/// Client-owned application bootstrap inputs for one authenticated Liquid wallet open.
/// Carries canonical non-secret identity data only; the password lease is supplied
/// separately by the caller at open time and is never stored here.
/// </summary>
internal sealed record LiquidApplicationBootstrapInputs(
    string CanonicalWalletId,
    string CanonicalWalletFilePath,
    string RuntimeProfileName,
    string NetworkManifestId);

/// <summary>
/// Application-owned factory for the authenticated Liquid runtime provider.
/// Holds the wallet directory root and application data directory; constructs a
/// provider bound to those roots. No secrets pass through this type.
/// </summary>
internal sealed class LiquidApplicationWalletBootstrap
{
    private readonly LiquidWalletDirectories _walletDirectories;
    private readonly string _applicationDataDirectory;

    internal LiquidApplicationWalletBootstrap(LiquidWalletDirectories walletDirectories, string applicationDataDirectory)
    {
        _walletDirectories = walletDirectories ?? throw new ArgumentNullException(nameof(walletDirectories));
        if (!Path.IsPathFullyQualified(applicationDataDirectory))
        {
            throw new ArgumentException("The application data directory must be absolute.", nameof(applicationDataDirectory));
        }
        _applicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
    }

    internal LiquidAuthenticatedRuntimeProvider CreateProvider()
    {
        LiquidRpcProfileSource profileSource = new(_applicationDataDirectory);
        ElementsPublicNetworkManifestSource manifestSource = new("elements-regtest");
        return new LiquidAuthenticatedRuntimeProvider(profileSource, _walletDirectories, manifestSource);
    }
}
