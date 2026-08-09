using System.Collections.Generic;

namespace WalletWasabi.Liquid.Rpc;

public sealed record ElementsNodeExpectation(
	string Chain,
	string GenesisBlockHash,
	string FedpegScript,
	string PeggedAsset,
	string ParentGenesisBlockHash,
	int PeginConfirmationDepth,
	bool EnforcePak,
	int Version,
	int ProtocolVersion,
	string Subversion)
{
	public ElementsNodeExpectation Normalize() =>
		this with
		{
			Chain = ElementsNodeStatus.RequireChain(Chain, nameof(Chain)),
			GenesisBlockHash = ElementsNodeStatus.RequireHex32(GenesisBlockHash, nameof(GenesisBlockHash)),
			FedpegScript = ElementsNodeStatus.RequireHex(FedpegScript, nameof(FedpegScript)),
			PeggedAsset = ElementsNodeStatus.RequireHex32(PeggedAsset, nameof(PeggedAsset)),
			ParentGenesisBlockHash = ElementsNodeStatus.RequireHex32(ParentGenesisBlockHash, nameof(ParentGenesisBlockHash)),
			PeginConfirmationDepth = ElementsNodeStatus.RequireNonNegative(PeginConfirmationDepth, nameof(PeginConfirmationDepth)),
			Version = ElementsNodeStatus.RequirePositive(Version, nameof(Version)),
			ProtocolVersion = ElementsNodeStatus.RequirePositive(ProtocolVersion, nameof(ProtocolVersion)),
			Subversion = ElementsNodeStatus.RequireText(Subversion, nameof(Subversion)),
		};
}

public sealed record ElementsNodeStatus(
	string Chain,
	int Blocks,
	int Headers,
	string BestBlockHash,
	string GenesisBlockHash,
	bool InitialBlockDownload,
	bool Pruned,
	bool TrimHeaders,
	bool BlockchainWarningsPresent,
	bool NetworkActive,
	bool LocalRelay,
	bool NetworkWarningsPresent,
	string FedpegScript,
	string PeggedAsset,
	string ParentGenesisBlockHash,
	int PeginConfirmationDepth,
	bool EnforcePak,
	int Version,
	int ProtocolVersion,
	string Subversion)
{
	public bool HasSynchronizedTipObservation => !InitialBlockDownload && Blocks == Headers;
	public bool HasCompleteArchiveObservation => HasSynchronizedTipObservation && !Pruned && !TrimHeaders;
	public bool HasClearWarningObservation => !BlockchainWarningsPresent && !NetworkWarningsPresent;
	public bool HasOnlineNetworkObservation => NetworkActive && LocalRelay;

	public void EnsureMatches(ElementsNodeExpectation expectation)
	{
		ArgumentNullException.ThrowIfNull(expectation);
		var normalized = expectation.Normalize();
		var mismatches = new List<string>();

		AddMismatch(mismatches, "chain", Chain, normalized.Chain);
		AddMismatch(mismatches, "genesis_block_hash", GenesisBlockHash, normalized.GenesisBlockHash);
		AddMismatch(mismatches, "fedpegscript", FedpegScript, normalized.FedpegScript);
		AddMismatch(mismatches, "pegged_asset", PeggedAsset, normalized.PeggedAsset);
		AddMismatch(mismatches, "parent_blockhash", ParentGenesisBlockHash, normalized.ParentGenesisBlockHash);
		if (PeginConfirmationDepth != normalized.PeginConfirmationDepth)
		{
			mismatches.Add("pegin_confirmation_depth");
		}
		if (EnforcePak != normalized.EnforcePak)
		{
			mismatches.Add("enforce_pak");
		}
		if (Version != normalized.Version)
		{
			mismatches.Add("version");
		}
		if (ProtocolVersion != normalized.ProtocolVersion)
		{
			mismatches.Add("protocolversion");
		}
		AddMismatch(mismatches, "subversion", Subversion, normalized.Subversion);

		if (mismatches.Count > 0)
		{
			throw new ElementsNodeMismatchException(mismatches);
		}
	}

	internal static string RequireChain(string value, string parameterName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
		if (value.Length > 64)
		{
			throw new ArgumentOutOfRangeException(parameterName, "Chain name is too long.");
		}

		foreach (char character in value)
		{
			if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_')
			{
				throw new ArgumentException("Chain name is not canonical.", parameterName);
			}
		}

		return value;
	}

	internal static string RequireHex32(string value, string parameterName)
	{
		ArgumentNullException.ThrowIfNull(value, parameterName);
		if (value.Length != 64)
		{
			throw new ArgumentException("A canonical 32-byte lowercase hexadecimal value is required.", parameterName);
		}

		foreach (char character in value)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				throw new ArgumentException("A canonical 32-byte lowercase hexadecimal value is required.", parameterName);
			}
		}
		if (value.AsSpan().IndexOfAnyExcept('0') < 0)
		{
			throw new ArgumentException("A nonzero 32-byte hexadecimal value is required.", parameterName);
		}

		return value;
	}

	internal static string RequireHex(string value, string parameterName)
	{
		ArgumentNullException.ThrowIfNull(value, parameterName);
		if (value.Length is 0 or > 20000 || value.Length % 2 != 0)
		{
			throw new ArgumentException("A nonempty canonical lowercase hexadecimal value is required.", parameterName);
		}

		foreach (char character in value)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				throw new ArgumentException("A nonempty canonical lowercase hexadecimal value is required.", parameterName);
			}
		}

		return value;
	}

	internal static int RequireNonNegative(int value, string parameterName)
	{
		if (value < 0)
		{
			throw new ArgumentOutOfRangeException(parameterName, "A non-negative value is required.");
		}

		return value;
	}

	internal static int RequirePositive(int value, string parameterName)
	{
		if (value <= 0)
		{
			throw new ArgumentOutOfRangeException(parameterName, "A positive value is required.");
		}

		return value;
	}

	internal static string RequireText(string value, string parameterName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
		if (value.Length > 256)
		{
			throw new ArgumentOutOfRangeException(parameterName, "Text value is too long.");
		}

		foreach (char character in value)
		{
			if (char.IsControl(character))
			{
				throw new ArgumentException("Control characters are not allowed.", parameterName);
			}
		}

		return value;
	}

	private static void AddMismatch(List<string> mismatches, string field, string actual, string expected)
	{
		if (!StringComparer.Ordinal.Equals(actual, expected))
		{
			mismatches.Add(field);
		}
	}
}

public sealed class ElementsNodeMismatchException : Exception
{
	public ElementsNodeMismatchException(IReadOnlyList<string> mismatchedFields)
		: base($"Elements node identity mismatch: {string.Join(", ", mismatchedFields)}.")
	{
		MismatchedFields = [.. mismatchedFields];
	}

	public IReadOnlyList<string> MismatchedFields { get; }
}
