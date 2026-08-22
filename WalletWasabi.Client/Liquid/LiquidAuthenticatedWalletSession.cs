using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidAuthenticatedWalletSession : IAsyncDisposable, ILiquidWalletSendSession
{
	// The bounded accepted-txid record for the next scan cycle: newest-first, ordinal,
	// distinct. The send-execution command service's refresh sink records here; the landed
	// scan path consumes it. Bounded so a long-lived session cannot grow it without limit.
	private const int MaxRecordedAcceptedTransactionIds = 64;

	private readonly object _refreshGate = new();
	private readonly List<string> _acceptedTransactionIds = [];
	private readonly Action<string>? _compositionRefreshSink;
	private int _disposed;

	internal LiquidAuthenticatedWalletSession(
		LiquidWalletIdentity identity,
		LiquidWalletRuntimeHandoff publicHandoff,
		KeyManager keyManager,
		LiquidWalletSignerKeyAdapter signerKeyAdapter,
		ElementsRpcClient rpcClient,
		ExtKey authenticatedMaster,
		string descriptor,
		ulong lastIndex,
		string walletDataDirectory,
		Action<string>? compositionRefreshSink = null)
	{
		Identity = identity ?? throw new ArgumentNullException(nameof(identity));
		PublicHandoff = publicHandoff ?? throw new ArgumentNullException(nameof(publicHandoff));
		KeyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
		SignerKeyAdapter = signerKeyAdapter ?? throw new ArgumentNullException(nameof(signerKeyAdapter));
		RpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
		AuthenticatedMaster = authenticatedMaster ?? throw new ArgumentNullException(nameof(authenticatedMaster));
		Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		LastIndex = lastIndex;
		WalletDataDirectory = string.IsNullOrWhiteSpace(walletDataDirectory)
			? throw new ArgumentException("A wallet data directory is required.", nameof(walletDataDirectory))
			: walletDataDirectory;
		_compositionRefreshSink = compositionRefreshSink;
	}

	internal LiquidWalletIdentity Identity { get; }
	internal LiquidWalletRuntimeHandoff PublicHandoff { get; }
	internal KeyManager KeyManager { get; }
	internal LiquidWalletSignerKeyAdapter SignerKeyAdapter { get; }
	internal ElementsRpcClient RpcClient { get; }

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

	internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

	// ILiquidWalletSendSession forwarders: the interface is the command-service-facing view
	// of this same instance; every member is already retained here.
	string ILiquidWalletSendSession.CanonicalWalletId => Identity.CanonicalWalletId;
	string ILiquidWalletSendSession.NetworkManifestId => Identity.NetworkManifestId;
	ExtKey ILiquidWalletSendSession.AuthenticatedMaster => AuthenticatedMaster;
	string ILiquidWalletSendSession.Descriptor => Descriptor;
	ulong ILiquidWalletSendSession.LastIndex => LastIndex;
	ILiquidWalletSigner ILiquidWalletSendSession.SignerKeyAdapter => SignerKeyAdapter;
	ElementsRpcClient ILiquidWalletSendSession.RpcClient => RpcClient;
	string ILiquidWalletSendSession.WalletDataDirectory => WalletDataDirectory;

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
			_acceptedTransactionIds.Remove(canonicalTransactionIdHex);
			_acceptedTransactionIds.Insert(0, canonicalTransactionIdHex);
			if (_acceptedTransactionIds.Count > MaxRecordedAcceptedTransactionIds)
			{
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

	public ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return ValueTask.CompletedTask;
		}

		try
		{
			SignerKeyAdapter.Dispose();
		}
		finally
		{
			RpcClient.Dispose();
		}

		return ValueTask.CompletedTask;
	}
}
