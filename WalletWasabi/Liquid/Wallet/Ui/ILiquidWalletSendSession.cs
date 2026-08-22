using NBitcoin;
using WalletWasabi.Liquid.Rpc;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 sections 5 and 9; 2026-08-21 amendment section 8):
/// the typed, non-secret-by-signature view of one open authenticated Liquid wallet session,
/// consumed by the WalletWasabi-resident send-execution command service. It carries exactly the
/// values the per-call execution scope re-derives its secret-bearing material from (the retained
/// authenticated master, the descriptor, the last derivation index) plus the already-owned key
/// adapter and RPC client. This interface is public so the Client composition root can name it
/// across the assembly boundary with no <c>InternalsVisibleTo</c>; its only implementation is
/// the internal Client authenticated session, so no session value is ever publicly constructible
/// or retained outside the session's own lifetime. The command service reaches the session only
/// through this typed surface — no <see langword="dynamic"/>, no opaque <see cref="object"/>,
/// no <c>Func&lt;object, ...&gt;</c>.
/// </summary>
public interface ILiquidWalletSendSession
{
	/// <summary>The retained authenticated master key (never disposed here; secret-bearing).</summary>
	ExtKey AuthenticatedMaster { get; }

	/// <summary>The retained public spend descriptor text captured at open time.</summary>
	string Descriptor { get; }

	/// <summary>The retained highest derivation index bound to the descriptor.</summary>
	ulong LastIndex { get; }

	/// <summary>The real caller-owned key owner (never disposed by the send scope).</summary>
	ILiquidWalletSigner SignerKeyAdapter { get; }

	/// <summary>The shared application-owned RPC client (never disposed by the send scope).</summary>
	ElementsRpcClient RpcClient { get; }

	/// <summary>The canonical wallet id (the wallet name the send request carries).</summary>
	string CanonicalWalletId { get; }

	/// <summary>The network manifest id the session is bound to.</summary>
	string NetworkManifestId { get; }

	/// <summary>
	/// The single source of truth for the directory the wallet's landed state files live under.
	/// The send request does not carry a copy; the command service guards the request's wallet
	/// name against this session (ordinal) and the executor loads state from this directory.
	/// </summary>
	string WalletDataDirectory { get; }

	/// <summary>
	/// Records one node-accepted canonical transaction id for the next scan cycle. This is
	/// the session's refresh sink: it is never a no-op — it durably records the accepted
	/// txid (newest-first, bounded, ordinal-distinct) so the next scan/state-refresh cycle
	/// can consume it, and additionally invokes the composition-supplied
	/// <see cref="Action{String}"/> sink when one is registered.
	/// </summary>
	void RecordAcceptedTransactionId(string canonicalTransactionIdHex);
}
