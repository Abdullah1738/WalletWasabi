using System.IO;
using System.Threading;
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

	private static readonly AsyncLocal<SaveObservationScope?> ActiveObservation = new();

	internal sealed class SaveObservationScope : IDisposable
	{
		private readonly SaveObservationScope? _previous;
		private int _disposed;

		private SaveObservationScope()
		{
			_previous = ActiveObservation.Value;
			ActiveObservation.Value = this;
		}

		private int _exportWriteEntryCount;

		internal int ExportWriteEntryCount => Volatile.Read(ref _exportWriteEntryCount);

		internal ManualResetEventSlim? EntryReached { get; set; }

		internal ManualResetEventSlim? EntryRelease { get; set; }

		internal static SaveObservationScope Begin() => new();

		internal void RecordExportWriteEntry()
		{
			Interlocked.Increment(ref _exportWriteEntryCount);
			EntryReached?.Set();
			EntryRelease?.Wait();
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				ActiveObservation.Value = _previous;
			}
		}
	}

	private readonly record struct CurrentMetadata(ulong Generation, ulong ExternalIndexHighWater, ulong InternalIndexHighWater);

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
			result.ExternalIndexHighWater,
			result.InternalIndexHighWater);
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
			return SaveCore(walletDataDir, walletName, state, generation, key, externalWalletNetworkContext, null, null, null);
		}
	}

	internal static LiquidWalletLoadSaveResult SaveWithExpectedGeneration(
		string walletDataDir,
		string walletName,
		LiquidWalletState state,
		ulong generation,
		ulong baseGeneration,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		ArgumentNullException.ThrowIfNull(state);

		lock (GenerationFence)
		{
			string filePath = LiquidWalletPersistencePaths.GetWalletStateFilePath(walletDataDir, walletName);
			CurrentMetadata? current = ReadCurrentMetadata(filePath, key, externalWalletNetworkContext);
			if (current is CurrentMetadata metadata)
			{
				if (metadata.Generation != baseGeneration)
				{
					throw new InvalidOperationException("The Liquid wallet persistence generation changed during save.");
				}
				if (generation < metadata.Generation)
				{
					throw new InvalidOperationException("The Liquid wallet persistence generation moved backwards.");
				}
			}

			return SaveCore(
				walletDataDir,
				walletName,
				state,
				generation,
				key,
				externalWalletNetworkContext,
				current?.ExternalIndexHighWater ?? 0,
				current?.InternalIndexHighWater ?? 0,
				expectedGeneration: null,
				current,
				usePreReadSnapshot: true);
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
				requestedInternalIndexHighWater: null,
				expectedGeneration);
		}
	}

	internal static LiquidWalletLoadSaveResult SaveWithIndexHighWaters(
		string walletDataDir,
		string walletName,
		LiquidWalletState state,
		ulong generation,
		ulong externalIndexHighWater,
		ulong internalIndexHighWater,
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
				internalIndexHighWater,
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
		ulong? requestedInternalIndexHighWater,
		ulong? expectedGeneration,
		CurrentMetadata? preReadSnapshot = null,
		bool usePreReadSnapshot = false)
	{
		ArgumentNullException.ThrowIfNull(state);

		string filePath = LiquidWalletPersistencePaths.GetWalletStateFilePath(
			walletDataDir,
			walletName);
		ulong externalIndexHighWater = requestedExternalIndexHighWater ?? 0;
		ulong internalIndexHighWater = requestedInternalIndexHighWater ?? 0;
		if (usePreReadSnapshot)
		{
			if (preReadSnapshot is CurrentMetadata snapshot)
			{
				externalIndexHighWater = Math.Max(externalIndexHighWater, snapshot.ExternalIndexHighWater);
				internalIndexHighWater = Math.Max(internalIndexHighWater, snapshot.InternalIndexHighWater);
			}
		}
		else if (File.Exists(filePath))
		{
			// Read the current on-disk high-waters to carry them forward. A file that cannot be
			// decrypted or parsed under this key/context is treated as absent (high-waters 0):
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
				if (requestedExternalIndexHighWater.HasValue || requestedInternalIndexHighWater.HasValue || expectedGeneration.HasValue)
				{
					if (expectedGeneration is ulong expected && current.Generation != expected)
					{
						throw new InvalidOperationException("The Liquid wallet persistence generation changed during save.");
					}
					if (generation < current.Generation)
					{
						throw new InvalidOperationException("The Liquid wallet persistence generation moved backwards.");
					}
					if (requestedExternalIndexHighWater is ulong requestedExternal && requestedExternal < current.ExternalIndexHighWater)
					{
						throw new InvalidOperationException("The Liquid external receive-index high-water moved backwards.");
					}
					if (requestedInternalIndexHighWater is ulong requestedInternal && requestedInternal < current.InternalIndexHighWater)
					{
						throw new InvalidOperationException("The Liquid internal change-index high-water moved backwards.");
					}
				}

				// A state save never lowers either authenticated index high-water. A generic save
				// carries the on-disk values forward unchanged; an allocating save supplies the
				// advanced value above.
				externalIndexHighWater = Math.Max(externalIndexHighWater, current.ExternalIndexHighWater);
				internalIndexHighWater = Math.Max(internalIndexHighWater, current.InternalIndexHighWater);
			}
		}
		LiquidWalletPersistenceHandoffResult result =
			ObserveAndExport(
				state,
				generation,
				key,
				externalWalletNetworkContext,
				externalIndexHighWater,
				internalIndexHighWater);
		LiquidWalletPersistenceFormat.Save(filePath, result.Envelope!);
		return LiquidWalletLoadSaveResult.CreateSaved(result.Revision, result.Generation, result.ExternalIndexHighWater, result.InternalIndexHighWater);
	}

	private static LiquidWalletPersistenceHandoffResult ObserveAndExport(
		LiquidWalletState state,
		ulong generation,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		ulong externalIndexHighWater,
		ulong internalIndexHighWater)
	{
		ActiveObservation.Value?.RecordExportWriteEntry();
		return
			LiquidWalletPersistenceHandoff.Export(
				state,
				generation,
				key,
				externalWalletNetworkContext,
				externalIndexHighWater,
				internalIndexHighWater);
		// Export always returns a non-null Envelope; the null-forgiving
		// operator adds no runtime check and no fallback.
	}

	private static CurrentMetadata? ReadCurrentMetadata(
		string filePath,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext)
	{
		if (!File.Exists(filePath))
		{
			return null;
		}

		try
		{
			LiquidWalletReplayProtectedPayload envelope = LiquidWalletPersistenceFormat.LoadEnvelope(filePath);
			LiquidWalletReplayOpenResult opened = LiquidWalletReplayProtectedPayload.Open(
				envelope.GetBytes(), key, externalWalletNetworkContext);
			return new CurrentMetadata(opened.Generation, opened.ExternalIndexHighWater, opened.InternalIndexHighWater);
		}
		catch (LiquidWalletReplayProtectionException)
		{
			return null;
		}
		catch (LiquidWalletPersistenceFormatException)
		{
			return null;
		}
	}
}
