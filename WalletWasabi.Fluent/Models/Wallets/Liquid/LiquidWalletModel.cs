using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Wallet.Ui;
using System.Collections.Generic;

namespace WalletWasabi.Fluent.Models.Wallets.Liquid;

/// <summary>
/// The Liquid-native wallet model: a parallel, Liquid-specific model beside
/// the BTC <see cref="WalletModel"/>, deliberately <b>not</b> an
/// <see cref="IWalletModel"/> implementation and not a
/// <see cref="WalletModel"/> subtype — <see cref="IWalletModel"/> is
/// BTC-shaped (an <c>NBitcoin.Network</c>, a single BTC
/// <c>IObservable&lt;Amount&gt;</c> balance, coins, CoinJoin, auth against
/// a <c>KeyManager</c>, HD addresses), and a Liquid managed wallet has none
/// of those. The model holds only the public immutable
/// <see cref="LiquidWalletUiSnapshot"/> projections produced by
/// <see cref="LiquidWalletUiFacade"/>; it never stores the key or context
/// spans, never holds a <c>LiquidWalletState</c> reference (that internal
/// type never crosses the assembly boundary), and performs no node
/// connection, no sync session, and no send flow.
/// </summary>
[AppLifetime]
public sealed class LiquidWalletModel : ReactiveObject, IDisposable
{
	private readonly ElementsPublicNetworkManifest _manifest;
	private readonly BehaviorSubject<LiquidWalletUiSnapshot> _balances;
	private readonly BehaviorSubject<bool> _loaded;
	private readonly BehaviorSubject<LiquidWalletUiHistorySnapshot?> _history;
	private readonly BehaviorSubject<bool> _historyLoaded;
	private readonly byte[] _nextReceiveScriptPubKey;
	private readonly byte[] _nextReceiveBlindingPublicKey;
	private readonly string[] _nextReceiveLabels;
	private readonly Func<LiquidWalletUiSetReceiveLabelsRequest, CancellationToken, Task>? _setNextReceiveLabelsCommand;

	public LiquidWalletModel(
		string name,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletUiSnapshot initialSnapshot,
		ReadOnlyMemory<byte> nextReceiveScriptPubKey,
		ReadOnlyMemory<byte> nextReceiveBlindingPublicKey,
		IReadOnlyList<string>? nextReceiveLabels = null,
		Func<LiquidWalletUiSetReceiveLabelsRequest, CancellationToken, Task>? setNextReceiveLabelsCommand = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(initialSnapshot);

		Name = name;
		_manifest = manifest;
		NetworkManifestId = initialSnapshot.NetworkManifestId;
		Snapshot = initialSnapshot;
		_nextReceiveScriptPubKey = nextReceiveScriptPubKey.ToArray();
		_nextReceiveBlindingPublicKey = nextReceiveBlindingPublicKey.ToArray();
		_nextReceiveLabels = nextReceiveLabels?.ToArray() ?? [];
		_setNextReceiveLabelsCommand = setNextReceiveLabelsCommand;

		_balances = new BehaviorSubject<LiquidWalletUiSnapshot>(initialSnapshot);
		_loaded = new BehaviorSubject<bool>(true);
		_history = new BehaviorSubject<LiquidWalletUiHistorySnapshot?>(null);
		_historyLoaded = new BehaviorSubject<bool>(false);

		Balances = _balances.AsObservable();
		HasBalance = Balances.Select(snapshot => !snapshot.IsEmpty);
		Loaded = _loaded.AsObservable();
		History = _history.AsObservable()
			.Where(snapshot => snapshot is not null)
			.Select(snapshot => snapshot!);
		HistoryLoaded = _historyLoaded.AsObservable();
	}

	public string Name { get; }
	public string NetworkManifestId { get; }
	public LiquidWalletUiSnapshot? Snapshot { get; private set; }
	public IObservable<LiquidWalletUiSnapshot> Balances { get; }
	public IObservable<bool> HasBalance { get; }
	public bool IsLoaded => _loaded.Value;
	public IObservable<bool> Loaded { get; }

	/// <summary>
	/// The retained Liquid transaction history paired with the current
	/// balance <see cref="Snapshot"/>, or <see langword="null"/> when no
	/// exact-revision history has been captured. History starts unloaded:
	/// no fabricated empty snapshot is ever emitted.
	/// </summary>
	public LiquidWalletUiHistorySnapshot? HistorySnapshot { get; private set; }
	public IObservable<LiquidWalletUiHistorySnapshot> History { get; }
	public bool IsHistoryLoaded => _historyLoaded.Value;
	public IObservable<bool> HistoryLoaded { get; }

	/// <summary>
	/// Re-captures the balance snapshot from the caller's advanced state
	/// (the caller holds the live state from its own landed load; this model
	/// never does). Each emission on <see cref="Balances"/> is a fresh
	/// immutable <see cref="LiquidWalletUiSnapshot"/>. Revision-pair fence:
	/// when the accepted balance snapshot's revision differs from
	/// <see cref="HistorySnapshot"/>/<c>?.Revision</c>, history becomes
	/// unloaded before the new balance emission is observable — the previous
	/// history object may remain held for diagnostics but is never again
	/// displayed or announced. A same-revision balance refresh leaves a
	/// successfully paired history loaded. This is an in-process
	/// projection-pair fence only; it is not persistence freshness or
	/// anti-rollback authority.
	/// </summary>
	public void RefreshBalances(LiquidWalletUiSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (HistorySnapshot is { } history && history.Revision != snapshot.Revision)
		{
			HistorySnapshot = null;
			_historyLoaded.OnNext(false);
		}

		Snapshot = snapshot;
		_balances.OnNext(snapshot);
	}

	/// <summary>
	/// Accepts one immutable history snapshot captured by the
	/// application-owned wallet lifetime layer via
	/// <see cref="LiquidWalletUiFacade.LoadAndCaptureHistory"/> with
	/// <c>expectedBaseRevision: Snapshot.Revision</c>, or from the same
	/// already-loaded state inside <c>WalletWasabi</c>. Validates wallet
	/// name, network manifest id, pegged asset id, and exact revision
	/// against the model's current <see cref="Snapshot"/>; any mismatch
	/// throws before changing either history field or stream. Success
	/// stores and emits the immutable snapshot, then marks history loaded.
	/// This model never receives key or context spans.
	/// </summary>
	public void RefreshHistory(LiquidWalletUiHistorySnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		LiquidWalletUiSnapshot balance = Snapshot ??
			throw new InvalidOperationException(
				"Liquid history cannot be paired before a balance snapshot exists.");
		if (!StringComparer.Ordinal.Equals(snapshot.WalletName, Name) ||
			!StringComparer.Ordinal.Equals(snapshot.NetworkManifestId, balance.NetworkManifestId) ||
			!StringComparer.Ordinal.Equals(snapshot.PeggedAssetIdHex, balance.PeggedAssetIdHex) ||
			snapshot.Revision != balance.Revision)
		{
			throw new InvalidOperationException(
				"The Liquid history snapshot does not pair with the current balance snapshot.");
		}

		HistorySnapshot = snapshot;
		_history.OnNext(snapshot);
		_historyLoaded.OnNext(true);
	}

	/// <summary>
	/// Derives one confidential receive address from the caller-supplied
	/// next-receive script and blinding key via the landed facade. The
	/// caller owns the script and blinding-key derivation (key management
	/// is outside this layer).
	/// </summary>
	public LiquidWalletUiReceiveAddress CreateReceiveAddress(
		ReadOnlySpan<byte> scriptPubKey,
		ReadOnlySpan<byte> blindingPublicKey) =>
		LiquidWalletUiFacade.CreateReceiveAddress(_manifest, scriptPubKey, blindingPublicKey);

	/// <summary>
	/// Derives the wallet's next confidential receive address from the
	/// caller-supplied next-receive script and blinding key captured at
	/// open time. The receive command calls this.
	/// </summary>
	public LiquidWalletUiReceiveAddress CreateNextReceiveAddress() =>
		CreateReceiveAddress(_nextReceiveScriptPubKey, _nextReceiveBlindingPublicKey);

	/// <summary>
	/// The durable label set bound to the wallet's next receive derivation
	/// index, as published on the open handoff's receive material (empty when
	/// the address is unlabeled). Read-only projection; labels carry no key
	/// material.
	/// </summary>
	public IReadOnlyList<string> NextReceiveLabels => [.. _nextReceiveLabels];

	/// <summary>
	/// Persists a durable label set for the wallet's current next-receive
	/// derivation index through the session-wired command (the landed,
	/// generation-fenced receive-label command service — not a process-local
	/// dictionary). An empty <paramref name="labels"/> clears the label. The
	/// model only forwards the public request; key management stays in the
	/// session layer. Fail-closed: any rejection from the landed surface
	/// surfaces as-is. Throws when no write surface is wired.
	/// </summary>
	public Task SetNextReceiveLabelsAsync(
		IReadOnlyList<string> labels,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(labels);
		if (_setNextReceiveLabelsCommand is not { } command)
		{
			throw new InvalidOperationException(
				"The Liquid receive-label write surface is not wired for this wallet session.");
		}

		return command(new LiquidWalletUiSetReceiveLabelsRequest(Name, labels), cancellationToken);
	}

	/// <summary>
	/// Builds the exact Liquid spend plan for the send flow from the
	/// caller-supplied selected outpoints (as hex strings), the confidential
	/// destination address, the destination asset id, the destination
	/// amount, and the explicit fee. Delegates to the public span-only
	/// facade entry point
	/// <see cref="LiquidWalletUiFacade.LoadAndCreateSpendPlan"/>: the Fluent
	/// side supplies only the wallet name (<see cref="Name"/>), the wallet
	/// data directory, and the caller's key/context spans; the facade loads
	/// the state in-assembly and never returns or exposes it. This model
	/// never names, holds, or obtains a <c>LiquidWalletState</c> (that
	/// internal type never crosses the assembly boundary), never stores the
	/// key or context spans (a <see cref="ReadOnlySpan{T}"/> cannot be
	/// captured or stored; the caller owns their provenance and lifetime,
	/// exactly as at wallet open), and performs no node connection, no
	/// signing, and no broadcast. The <paramref name="expectedRevision"/>
	/// argument, when supplied, is the caller's freshness fence (typically
	/// the <see cref="LiquidWalletUiSnapshot.Revision"/> the UI last
	/// rendered); when null, no plan-time revision fence is applied.
	/// Fail-closed: any rejection from the landed load or spend-plan
	/// surface surfaces as-is.
	/// </summary>
	public LiquidWalletUiSpendPlan CreateSpendPlan(
		string walletDataDir,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		IReadOnlyList<string> selectedOutPointHexes,
		string confidentialDestinationAddress,
		string destinationAssetIdHex,
		long destinationAtomicUnits,
		long explicitFeeAtomicUnits,
		ulong? expectedRevision = null) =>
		LiquidWalletUiFacade.LoadAndCreateSpendPlan(
			walletDataDir,
			Name,
			_manifest,
			key,
			externalWalletNetworkContext,
			selectedOutPointHexes,
			confidentialDestinationAddress,
			destinationAssetIdHex,
			destinationAtomicUnits,
			explicitFeeAtomicUnits,
			expectedRevision);

	public void Dispose()
	{
		_balances.Dispose();
		_loaded.Dispose();
		_history.Dispose();
		_historyLoaded.Dispose();
	}
}
