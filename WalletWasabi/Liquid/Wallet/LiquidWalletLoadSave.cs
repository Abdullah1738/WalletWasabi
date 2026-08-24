using System.IO;
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
	internal static object GenerationFence { get; } = new();

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
			result.Generation,
			result.ExternalIndexHighWater);
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
		lock (GenerationFence)
		{
			return SaveCore(walletDataDir, walletName, state, generation, key, externalWalletNetworkContext, null, null);
		}
	}

	internal static LiquidWalletLoadSaveResult SaveWithExternalIndexHighWater(
		string walletDataDir,
		string walletName,
		LiquidWalletState state,
		ulong generation,
		ulong externalIndexHighWater,
		ulong expectedGeneration,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		lock (GenerationFence)
		{
			return SaveCore(
				walletDataDir,
				walletName,
				state,
				generation,
				key,
				externalWalletNetworkContext,
				externalIndexHighWater,
				expectedGeneration);
		}
	}

	private static LiquidWalletLoadSaveResult SaveCore(
		string walletDataDir,
		string walletName,
		LiquidWalletState state,
		ulong generation,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		ulong? requestedExternalIndexHighWater,
		ulong? expectedGeneration)
	{
		ArgumentNullException.ThrowIfNull(state);

		string filePath = LiquidWalletPersistencePaths.GetWalletStateFilePath(
			walletDataDir,
			walletName);
		ulong externalIndexHighWater = requestedExternalIndexHighWater ?? 0;
		if (File.Exists(filePath))
		{
			// Read the current on-disk high-water to carry it forward. A file that cannot be
			// decrypted or parsed under this key/context is treated as absent (high-water 0):
			// Save is an idempotent overwrite and must not fail on a stale or foreign file.
			// The strict expected-generation / rollback rejections below only apply when the
			// current state is actually readable.
			LiquidWalletLoadSaveResult? current = null;
			try
			{
				current = Load(walletDataDir, walletName, key, externalWalletNetworkContext);
			}
			catch (LiquidWalletReplayProtectionException)
			{
			}
			catch (LiquidWalletPersistenceFormatException)
			{
			}

			if (current is not null)
			{
				if (requestedExternalIndexHighWater.HasValue || expectedGeneration.HasValue)
				{
					if (expectedGeneration is ulong expected && current.Generation != expected)
					{
						throw new InvalidOperationException("The Liquid wallet persistence generation changed during save.");
					}
					if (generation < current.Generation)
					{
						throw new InvalidOperationException("The Liquid wallet persistence generation moved backwards.");
					}
					if (requestedExternalIndexHighWater is ulong requested && requested < current.ExternalIndexHighWater)
					{
						throw new InvalidOperationException("The Liquid external receive-index high-water moved backwards.");
					}
				}

				// A state save never lowers the authenticated external receive-index high-water.
				// A generic save carries the on-disk value forward unchanged; an allocating save
				// supplies the advanced value above.
				externalIndexHighWater = Math.Max(externalIndexHighWater, current.ExternalIndexHighWater);
			}
		}
		LiquidWalletPersistenceHandoffResult result =
			LiquidWalletPersistenceHandoff.Export(
				state,
				generation,
				key,
				externalWalletNetworkContext,
				externalIndexHighWater);
		// Export always returns a non-null Envelope; the null-forgiving
		// operator adds no runtime check and no fallback.
		LiquidWalletPersistenceFormat.Save(filePath, result.Envelope!);
		return LiquidWalletLoadSaveResult.CreateSaved(result.Revision, result.Generation, result.ExternalIndexHighWater);
	}
}
