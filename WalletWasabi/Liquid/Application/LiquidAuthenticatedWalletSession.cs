using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Liquid.Application;

internal sealed class LiquidWalletRefreshStateCapture
{
	private readonly string[] _acceptedTransactionIds;
	private readonly IReadOnlyDictionary<string, ulong> _acceptedTransactionIdVersions;

	internal LiquidWalletRefreshStateCapture(
		object snapshotReference,
		LiquidAuthenticatedWalletStateOwner owner,
		LiquidWalletRuntimeHandoff publicHandoff,
		WalletWasabi.Liquid.Wallet.LiquidWalletState state,
		ulong stateRevision,
		ulong persistenceGeneration,
		ulong externalIndexHighWater,
		string[] acceptedTransactionIds,
		IReadOnlyDictionary<string, ulong> acceptedTransactionIdVersions)
	{
		SnapshotReference = snapshotReference;
		Owner = owner;
		PublicHandoff = publicHandoff;
		State = state;
		StateRevision = stateRevision;
		PersistenceGeneration = persistenceGeneration;
		ExternalIndexHighWater = externalIndexHighWater;
		_acceptedTransactionIds = acceptedTransactionIds;
		_acceptedTransactionIdVersions = acceptedTransactionIdVersions;
	}

	internal object SnapshotReference { get; }
	internal LiquidAuthenticatedWalletStateOwner Owner { get; }
	internal LiquidWalletRuntimeHandoff PublicHandoff { get; }
	internal WalletWasabi.Liquid.Wallet.LiquidWalletState State { get; }
	internal ulong StateRevision { get; }
	internal ulong PersistenceGeneration { get; }
	internal ulong ExternalIndexHighWater { get; }
	internal IReadOnlyList<string> AcceptedTransactionIds => _acceptedTransactionIds;
	internal IReadOnlyDictionary<string, ulong> AcceptedTransactionIdVersions => _acceptedTransactionIdVersions;
}

internal sealed class LiquidAuthenticatedWalletSession : IAsyncDisposable
{
	// The bounded accepted-txid record for the next scan cycle: newest-first, ordinal,
	// distinct. The send-execution command service's refresh sink records here; the landed
	// scan path consumes it. Bounded so a long-lived session cannot grow it without limit.
	private const int MaxRecordedAcceptedTransactionIds = 64;

	private readonly object _refreshGate = new();
	private readonly object _lifetimeGate = new();
	private readonly List<string> _acceptedTransactionIds = [];
	private Dictionary<string, ulong>? _acceptedTransactionIdVersions = new(StringComparer.Ordinal);
	private ulong _acceptedTransactionIdVersion;
	private readonly Action<string>? _compositionRefreshSink;
	private readonly ElementsPublicNetworkManifest _manifest;
	private int _activeOperationCount;
	private bool _closing;
	private TaskCompletionSource<object?>? _drained;
	private Task? _disposeTask;

	/// <summary>
	/// One immutable owner/public-handoff pair. The session holds exactly one reference to this
	/// snapshot; a refresh captures and installs that single reference atomically, so no reader
	/// can observe a new handoff paired with an old owner.
	/// </summary>
	private sealed class RefreshSnapshot
	{
		internal RefreshSnapshot(
			LiquidAuthenticatedWalletStateOwner owner,
			LiquidWalletRuntimeHandoff publicHandoff)
		{
			Owner = owner ?? throw new ArgumentNullException(nameof(owner));
			PublicHandoff = publicHandoff ?? throw new ArgumentNullException(nameof(publicHandoff));
		}

		internal LiquidAuthenticatedWalletStateOwner Owner { get; }
		internal LiquidWalletRuntimeHandoff PublicHandoff { get; }
	}

	private volatile RefreshSnapshot _snapshot = null!;

	internal LiquidAuthenticatedWalletSession(
		LiquidWalletIdentity identity,
		LiquidWalletRuntimeHandoff publicHandoff,
		KeyManager keyManager,
		LiquidWalletSignerKeyAdapter signerKeyAdapter,
		ElementsPublicNetworkManifest manifest,
		ElementsRpcClient rpcClient,
		ExtKey authenticatedMaster,
		LiquidAuthenticatedWalletStateOwner stateOwner,
		string descriptor,
		ulong lastIndex,
		string walletDataDirectory,
		Action<string>? compositionRefreshSink = null)
	{
		Identity = identity ?? throw new ArgumentNullException(nameof(identity));
		ArgumentNullException.ThrowIfNull(publicHandoff);
		KeyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
		SignerKeyAdapter = signerKeyAdapter ?? throw new ArgumentNullException(nameof(signerKeyAdapter));
		ArgumentNullException.ThrowIfNull(manifest);
		if (!String.Equals(manifest.ManifestId, identity.NetworkManifestId, StringComparison.Ordinal))
		{
			throw new ArgumentException("The manifest identity must match the wallet identity.", nameof(manifest));
		}
		_manifest = manifest;
		RpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
		AuthenticatedMaster = authenticatedMaster ?? throw new ArgumentNullException(nameof(authenticatedMaster));
		ArgumentNullException.ThrowIfNull(stateOwner);
		_snapshot = new RefreshSnapshot(stateOwner, publicHandoff);
		Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		LastIndex = lastIndex;
		WalletDataDirectory = string.IsNullOrWhiteSpace(walletDataDirectory)
			? throw new ArgumentException("A wallet data directory is required.", nameof(walletDataDirectory))
			: walletDataDirectory;
		_compositionRefreshSink = compositionRefreshSink;
	}

	internal LiquidWalletIdentity Identity { get; }
	internal LiquidWalletRuntimeHandoff PublicHandoff => _snapshot.PublicHandoff;
	internal KeyManager KeyManager { get; }
	internal LiquidWalletSignerKeyAdapter SignerKeyAdapter { get; }
	internal ElementsPublicNetworkManifest Manifest => _manifest;
	internal ElementsRpcClient RpcClient { get; }
	internal LiquidAuthenticatedWalletStateOwner StateOwner => _snapshot.Owner;
	internal ElementsNodeExpectation NodeExpectation => StateOwner.NodeExpectation;

	/// <summary>
	/// The session's current public handoff for a teardown probe, or null when the session was
	/// never fully constructed (a reflection-built fixture with no installed snapshot). Real
	/// sessions always return the live snapshot handoff; this is the single null-tolerant read
	/// the provider's close/dispose path uses, and it never weakens the atomic install.
	/// </summary>
	internal LiquidWalletRuntimeHandoff? PublicHandoffOrNull => _snapshot?.PublicHandoff;

	/// <summary>
	/// Atomically captures the exact immutable owner/handoff snapshot reference, its owner state
	/// fences, and a copy of the accepted-ID record under the short refresh monitor.
	/// </summary>
	internal LiquidWalletRefreshStateCapture CaptureRefreshState()
	{
		lock (_refreshGate)
		{
			RefreshSnapshot snapshot = _snapshot;
			LiquidAuthenticatedWalletStateOwner owner = snapshot.Owner;
			string[] acceptedTransactionIds = _acceptedTransactionIds.ToArray();
			var acceptedVersions = new Dictionary<string, ulong>(StringComparer.Ordinal);
			Dictionary<string, ulong> currentVersions = AcceptedTransactionIdVersions;
			foreach (string acceptedId in acceptedTransactionIds)
			{
				acceptedVersions.Add(acceptedId, currentVersions[acceptedId]);
			}
			return new LiquidWalletRefreshStateCapture(
				snapshot,
				owner,
				snapshot.PublicHandoff,
				owner.State,
				owner.StateRevision,
				owner.PersistenceGeneration,
				owner.ExternalIndexHighWater,
				acceptedTransactionIds,
				acceptedVersions);
		}
	}

	/// <summary>
	/// Validates every captured owner/handoff/state fence against the current exact snapshot under
	/// the short refresh monitor. It performs no allocation and never awaits.
	/// </summary>
	internal bool ValidateRefreshState(LiquidWalletRefreshStateCapture captured)
	{
		ArgumentNullException.ThrowIfNull(captured);
		lock (_refreshGate)
		{
			RefreshSnapshot snapshot = _snapshot;
			LiquidAuthenticatedWalletStateOwner owner = snapshot.Owner;
			return ReferenceEquals(snapshot, captured.SnapshotReference)
				&& ReferenceEquals(owner, captured.Owner)
				&& ReferenceEquals(snapshot.PublicHandoff, captured.PublicHandoff)
				&& ReferenceEquals(owner.State, captured.State)
				&& owner.StateRevision == captured.StateRevision
				&& owner.PersistenceGeneration == captured.PersistenceGeneration
				&& owner.ExternalIndexHighWater == captured.ExternalIndexHighWater;
		}
	}

	/// <summary>
	/// Removes only captured accepted-ID occurrences that belong to a committed candidate set.
	/// A captured ID re-recorded after capture is newer than its captured occurrence and is preserved,
	/// as are all IDs first recorded concurrently after capture.
	/// </summary>
	internal void RemoveCapturedAcceptedIds(
		LiquidWalletRefreshStateCapture captured,
		IReadOnlySet<string> committedCandidateIds)
	{
		ArgumentNullException.ThrowIfNull(captured);
		ArgumentNullException.ThrowIfNull(committedCandidateIds);
		lock (_refreshGate)
		{
			Dictionary<string, ulong> currentVersions = AcceptedTransactionIdVersions;
			foreach (string acceptedId in captured.AcceptedTransactionIds)
			{
				if (committedCandidateIds.Contains(acceptedId)
					&& currentVersions.TryGetValue(acceptedId, out ulong currentVersion)
					&& captured.AcceptedTransactionIdVersions.TryGetValue(acceptedId, out ulong capturedVersion)
					&& currentVersion == capturedVersion)
				{
					_acceptedTransactionIds.Remove(acceptedId);
					currentVersions.Remove(acceptedId);
				}
			}
		}
	}

	private Dictionary<string, ulong> AcceptedTransactionIdVersions
	{
		get
		{
			if (_acceptedTransactionIdVersions is null)
			{
				_acceptedTransactionIdVersions = new Dictionary<string, ulong>(StringComparer.Ordinal);
				foreach (string acceptedId in _acceptedTransactionIds)
				{
					_acceptedTransactionIdVersions.Add(acceptedId, ++_acceptedTransactionIdVersion);
				}
			}
			return _acceptedTransactionIdVersions;
		}
	}

	/// <summary>
	/// Captures the single immutable owner/handoff snapshot reference. The returned opaque handle
	/// is the exact reference a later <see cref="TryInstallRefreshSnapshot"/> compares against, so
	/// a refresh can prove the pair has not changed since capture. Reading it is atomic.
	/// </summary>
	internal object CaptureRefreshSnapshot() => _snapshot;

	/// <summary>
	/// Nonthrowingly installs one prepared owner/handoff pair, but only when the session's current
	/// snapshot is still the exact <paramref name="expectedSnapshot"/> reference captured before the
	/// refresh's persistence. All throwing projection and validation must already have happened
	/// (this method performs none). Returns <see langword="false"/> when the snapshot changed
	/// underneath, leaving the current pair untouched. The pair is installed as one reference, so
	/// no reader observes a new handoff with an old owner.
	/// </summary>
	internal bool TryInstallRefreshSnapshot(
		object expectedSnapshot,
		LiquidAuthenticatedWalletStateOwner owner,
		LiquidWalletRuntimeHandoff publicHandoff)
	{
		ArgumentNullException.ThrowIfNull(expectedSnapshot);
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(publicHandoff);
		lock (_refreshGate)
		{
			if (!ReferenceEquals(_snapshot, expectedSnapshot))
			{
				return false;
			}

			_snapshot = new RefreshSnapshot(owner, publicHandoff);
			return true;
		}
	}

	/// <summary>
	/// The retained authenticated master key (internal-only). The per-call execution scope
	/// factory re-derives the SLIP-77 master and the replay/context values from this at
	/// scope-open time; it is never public and never crosses the assembly boundary.
	/// </summary>
	internal ExtKey AuthenticatedMaster { get; }

	/// <summary>
	/// The retained public spend descriptor text (internal-only), captured at open time. The
	/// per-call execution scope copies its bytes and zeroizes the copy on dispose.
	/// </summary>
	internal string Descriptor { get; }

	/// <summary>The retained highest derivation index bound to the descriptor (internal-only).</summary>
	internal ulong LastIndex { get; }

	/// <summary>The directory the wallet's landed state files live under (non-secret).</summary>
	internal string WalletDataDirectory { get; }

	internal bool IsDisposed
	{
		get
		{
			lock (_lifetimeGate)
			{
				return _disposeTask?.IsCompleted == true;
			}
		}
	}

	internal LiquidWalletOperationLease AcquireOperationUnderProviderGate()
	{
		lock (_lifetimeGate)
		{
			if (_closing)
			{
				throw new InvalidOperationException("The Liquid wallet session is closing.");
			}

			_activeOperationCount = checked(_activeOperationCount + 1);
			return new LiquidWalletOperationLease(this);
		}
	}

	internal Task BeginCloseUnderProviderGate()
	{
		lock (_lifetimeGate)
		{
			_closing = true;
			if (_activeOperationCount == 0)
			{
				return Task.CompletedTask;
			}

			_drained ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
			return _drained.Task;
		}
	}

	internal Task DisposeAfterDrainAsync(Task drainTask)
	{
		ArgumentNullException.ThrowIfNull(drainTask);
		TaskCompletionSource<object?> completion;
		lock (_lifetimeGate)
		{
			if (_disposeTask is not null)
			{
				return _disposeTask;
			}

			completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
			_disposeTask = completion.Task;
		}

		_ = CompleteDisposalAsync(completion, drainTask);
		return completion.Task;
	}

	internal void ReleaseOperation()
	{
		TaskCompletionSource<object?>? drained = null;
		lock (_lifetimeGate)
		{
			if (_activeOperationCount <= 0)
			{
				throw new InvalidOperationException("The Liquid wallet operation counter underflowed.");
			}

			_activeOperationCount--;
			if (_closing && _activeOperationCount == 0)
			{
				drained = _drained;
			}
		}

		drained?.TrySetResult(null);
	}

	/// <summary>
	/// The session's state-refresh sink for the send-execution handoff. Records one
	/// node-accepted canonical transaction id for the next scan cycle (newest-first,
	/// ordinal-distinct, bounded), then invokes the composition-supplied
	/// <see cref="Action{String}"/> sink when one is registered. Never a no-op: the record
	/// happens unconditionally, so an accepted txid is never silently dropped even before a
	/// wallet-lifetime binding is wired.
	/// </summary>
	public void RecordAcceptedTransactionId(string canonicalTransactionIdHex)
	{
		ArgumentException.ThrowIfNullOrEmpty(canonicalTransactionIdHex);
		lock (_refreshGate)
		{
			Dictionary<string, ulong> versions = AcceptedTransactionIdVersions;
			_acceptedTransactionIds.Remove(canonicalTransactionIdHex);
			_acceptedTransactionIds.Insert(0, canonicalTransactionIdHex);
			versions[canonicalTransactionIdHex] = checked(++_acceptedTransactionIdVersion);
			if (_acceptedTransactionIds.Count > MaxRecordedAcceptedTransactionIds)
			{
				foreach (string removedId in _acceptedTransactionIds.Skip(MaxRecordedAcceptedTransactionIds))
				{
					versions.Remove(removedId);
				}
				_acceptedTransactionIds.RemoveRange(
					MaxRecordedAcceptedTransactionIds,
					_acceptedTransactionIds.Count - MaxRecordedAcceptedTransactionIds);
			}
		}

		_compositionRefreshSink?.Invoke(canonicalTransactionIdHex);
	}

	/// <summary>
	/// A snapshot of the recorded accepted transaction ids (newest-first) for the landed
	/// scan path to consume. Internal-only; never secret-bearing.
	/// </summary>
	internal IReadOnlyList<string> GetRecordedAcceptedTransactionIds()
	{
		lock (_refreshGate)
		{
			return _acceptedTransactionIds.ToArray();
		}
	}

	internal Task<ElementsExpectationBoundRawTransactionBatch?> FetchPreRefreshRawTransactionsAsync(
		CancellationToken cancellationToken)
	{
		IReadOnlyList<string> transactionIds = GetRecordedAcceptedTransactionIds();
		if (transactionIds.Count == 0)
		{
			return Task.FromResult<ElementsExpectationBoundRawTransactionBatch?>(null);
		}

		cancellationToken.ThrowIfCancellationRequested();
		IReadOnlyList<ElementsRawTransactionRequest> requests =
			BuildPreRefreshRawTransactionRequests(transactionIds);
		return FetchPreRefreshRawTransactionsCoreAsync(requests, cancellationToken);
	}

	internal static IReadOnlyList<ElementsRawTransactionRequest> BuildPreRefreshRawTransactionRequests(
		IReadOnlyList<string> transactionIds)
	{
		ArgumentNullException.ThrowIfNull(transactionIds);
		if (transactionIds.Count is < 1 or > 100)
		{
			throw new ArgumentOutOfRangeException(
				nameof(transactionIds),
				"Between one and one hundred transaction identifiers are required.");
		}

		var distinctTransactionIds = new HashSet<string>(StringComparer.Ordinal);
		var requests = new ElementsRawTransactionRequest[transactionIds.Count];
		for (int index = 0; index < transactionIds.Count; index++)
		{
			string transactionId = transactionIds[index]
				?? throw new ArgumentException("Every transaction identifier is required.", nameof(transactionIds));
			if (transactionId.Length != 64
				|| transactionId.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
			{
				throw new ArgumentException("A canonical lowercase Liquid transaction identifier is required.", nameof(transactionIds));
			}
			if (transactionId.All(character => character == '0'))
			{
				throw new ArgumentException("A nonzero Liquid transaction identifier is required.", nameof(transactionIds));
			}
			var request = new ElementsRawTransactionRequest(transactionId, BlockHash: null);
			if (!distinctTransactionIds.Add(request.TransactionId))
			{
				throw new ArgumentException("Transaction identifiers must be ordinal-distinct.", nameof(transactionIds));
			}

			requests[index] = request;
		}

		return requests;
	}

	private async Task<ElementsExpectationBoundRawTransactionBatch?> FetchPreRefreshRawTransactionsCoreAsync(
		IReadOnlyList<ElementsRawTransactionRequest> requests,
		CancellationToken cancellationToken) =>
		await StateOwner.GetPreRefreshRawTransactionsAsync(
			Manifest,
			RpcClient,
			requests,
			cancellationToken).ConfigureAwait(false);

	public ValueTask DisposeAsync()
	{
		Task drainTask = BeginCloseUnderProviderGate();
		return new ValueTask(DisposeAfterDrainAsync(drainTask));
	}

	private async Task CompleteDisposalAsync(TaskCompletionSource<object?> completion, Task drainTask)
	{
		try
		{
			await DisposeAfterDrainCoreAsync(drainTask).ConfigureAwait(false);
			completion.TrySetResult(null);
		}
		catch (Exception exception)
		{
			completion.TrySetException(exception);
		}
	}

	private async Task DisposeAfterDrainCoreAsync(Task drainTask)
	{
		await drainTask.ConfigureAwait(false);
		try
		{
			SignerKeyAdapter.Dispose();
		}
		finally
		{
			RpcClient.Dispose();
		}
	}
}
