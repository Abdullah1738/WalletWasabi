using System;
using System.IO;

namespace WalletWasabi.Liquid.Application;

public sealed record LiquidWalletApplicationOptions
{
	public LiquidWalletApplicationOptions(string applicationDataDirectory, string liquidWalletDirectory, string reviewedManifestId)
	{
		ApplicationDataDirectory = GetCanonicalAbsolutePath(applicationDataDirectory, nameof(applicationDataDirectory));
		LiquidWalletDirectory = GetCanonicalAbsolutePath(liquidWalletDirectory, nameof(liquidWalletDirectory));
		ArgumentException.ThrowIfNullOrWhiteSpace(reviewedManifestId);
		ReviewedManifestId = reviewedManifestId;
	}

	public string ApplicationDataDirectory { get; }
	public string LiquidWalletDirectory { get; }
	public string ReviewedManifestId { get; }

	private static string GetCanonicalAbsolutePath(string path, string parameterName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
		if (!Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException("The path must be absolute.", parameterName);
		}

		return Path.GetFullPath(path);
	}
}
