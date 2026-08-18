using WalletWasabi.Liquid.Wallet.Sync;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// The fail-closed per-wallet Liquid load/save wiring entry point: the
/// transparent composition of the landed
/// <see cref="LiquidWalletPersistencePaths"/>,
/// <see cref="LiquidWalletPersistenceFormat"/>, and
/// <see cref="LiquidWalletPersistenceHandoff"/> surfaces into the
/// startup-load / shutdown-save lifecycle of one Liquid managed wallet.
/// <see cref="Load"/> resolves the wallet state file path, reads and
/// strictly validates the on-disk frame, and hands the enclosed sealed
/// envelope bytes to the landed <see cref="LiquidWalletPersistenceHandoff.Import"/>;
/// <see cref="Save"/> seals the caller's current
/// <see cref="LiquidWalletState"/> via the landed
/// <see cref="LiquidWalletPersistenceHandoff.Export"/> and persists the
/// sealed envelope atomically via the landed
/// <see cref="LiquidWalletPersistenceFormat.Save"/>. Each method body is
/// exactly that call order and nothing more: no retry, no fallback, no
/// second read or write, no empty-state substitution on failure, no key
/// storage, and no catch-and-rethrow remapping — every rejection surfaces
/// with the existing exception surface of the failing layer
/// (<see cref="ArgumentException"/> from path validation,
/// <see cref="InvalidOperationException"/> from the landed
/// <see cref="Io.SafeFile"/> read path when no safe version exists,
/// <see cref="LiquidWalletPersistenceFormatException"/> from framing,
/// <see cref="LiquidWalletReplayProtectionException"/> from the landed
/// <see cref="LiquidWalletReplayProtectedPayload.Open"/>,
/// <see cref="InvalidOperationException"/> from the landed revision fence,
/// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
/// from the file system). The key and context are caller-supplied
/// <see cref="ReadOnlySpan{T}"/> values threaded straight through to the
/// landed <c>Export</c>/<c>Import</c>: this type never stores them, never
/// logs them, never derives them, and never persists them, and because a
/// <see cref="ReadOnlySpan{T}"/> is a <c>ref struct</c> that cannot be
/// captured or stored, the spans cannot outlive the call — the clearing
/// obligation is structural, not a runtime wipe. This type performs no
/// multiasset rendering, no balance query, no send/receive, no CoinJoin, no
/// sync session, no scan-intent derivation, no node connection, no key
/// management, and no first-run wallet creation (a missing file fails closed
/// like any other load failure; first-run creation policy lives at the
/// caller's layer); it grants no persistence, chain, confirmation-source,
/// currentness, reservation, broadcast, artifact-source, or runtime
/// authority.
/// </summary>
internal static class LiquidWalletLoadSave
{
	/// <summary>
	/// Loads one Liquid managed wallet's sealed state file from
	/// <paramref name="walletDataDir"/> and restores the
	/// <see cref="LiquidWalletState"/> through the landed handoff. A missing
	/// file is not distinguished from a corrupt file by this method: both
	/// fail closed (the caller that wants first-run wallet creation checks
	/// <see cref="File.Exists"/> before calling, or catches, at its own
	/// layer).
	/// </summary>
	public static LiquidWalletLoadSaveResult Load(
		string walletDataDir,
		string walletName,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		ulong? expectedBaseRevision = null)
	{
		string filePath = LiquidWalletPersistencePaths.GetWalletStateFilePath(
			walletDataDir,
			walletName);
		LiquidWalletReplayProtectedPayload envelope =
			LiquidWalletPersistenceFormat.LoadEnvelope(filePath);
		LiquidWalletPersistenceHandoffResult result =
			LiquidWalletPersistenceHandoff.Import(
				envelope.GetBytes(),
				key,
				externalWalletNetworkContext,
				expectedBaseRevision);
		// Import always returns a non-null State; the null-forgiving operator
		// adds no runtime check and no fallback.
		return LiquidWalletLoadSaveResult.CreateLoaded(
			result.State!,
			result.Revision,
			result.Generation);
	}

	/// <summary>
	/// Seals the caller's current <see cref="LiquidWalletState"/> via the
	/// landed handoff and persists the sealed envelope atomically to the
	/// wallet state file under <paramref name="walletDataDir"/>. The caller's
	/// <paramref name="state"/> is never mutated;
	/// <see cref="LiquidWalletState.ExportReplaySnapshot"/> is a pure export.
	/// </summary>
	public static LiquidWalletLoadSaveResult Save(
		string walletDataDir,
		string walletName,
		LiquidWalletState state,
		ulong generation,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		ArgumentNullException.ThrowIfNull(state);

		string filePath = LiquidWalletPersistencePaths.GetWalletStateFilePath(
			walletDataDir,
			walletName);
		LiquidWalletPersistenceHandoffResult result =
			LiquidWalletPersistenceHandoff.Export(
				state,
				generation,
				key,
				externalWalletNetworkContext);
		// Export always returns a non-null Envelope; the null-forgiving
		// operator adds no runtime check and no fallback.
		LiquidWalletPersistenceFormat.Save(filePath, result.Envelope!);
		return LiquidWalletLoadSaveResult.CreateSaved(result.Revision, result.Generation);
	}
}
