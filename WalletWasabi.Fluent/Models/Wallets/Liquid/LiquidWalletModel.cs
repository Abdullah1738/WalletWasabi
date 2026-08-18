using System.Reactive.Linq;
using System.Reactive.Subjects;
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
	private readonly byte[] _nextReceiveScriptPubKey;
	private readonly byte[] _nextReceiveBlindingPublicKey;

	public LiquidWalletModel(
		string name,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletUiSnapshot initialSnapshot,
		ReadOnlyMemory<byte> nextReceiveScriptPubKey,
		ReadOnlyMemory<byte> nextReceiveBlindingPublicKey)
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

		_balances = new BehaviorSubject<LiquidWalletUiSnapshot>(initialSnapshot);
		_loaded = new BehaviorSubject<bool>(true);

		Balances = _balances.AsObservable();
		HasBalance = Balances.Select(snapshot => !snapshot.IsEmpty);
		Loaded = _loaded.AsObservable();
	}

	public string Name { get; }
	public string NetworkManifestId { get; }
	public LiquidWalletUiSnapshot? Snapshot { get; private set; }
	public IObservable<LiquidWalletUiSnapshot> Balances { get; }
	public IObservable<bool> HasBalance { get; }
	public bool IsLoaded => _loaded.Value;
	public IObservable<bool> Loaded { get; }

	/// <summary>
	/// Re-captures the balance snapshot from the caller's advanced state
	/// (the caller holds the live state from its own landed load; this model
	/// never does). Each emission on <see cref="Balances"/> is a fresh
	/// immutable <see cref="LiquidWalletUiSnapshot"/>.
	/// </summary>
	public void RefreshBalances(LiquidWalletUiSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Snapshot = snapshot;
		_balances.OnNext(snapshot);
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
	}
}
