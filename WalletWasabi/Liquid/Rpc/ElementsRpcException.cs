using System.Net;

namespace WalletWasabi.Liquid.Rpc;

public enum ElementsRpcFailureKind
{
	Transport,
	Timeout,
	Http,
	Rpc,
	Protocol,
}

public sealed class ElementsRpcException : Exception
{
	internal ElementsRpcException(
		ElementsRpcFailureKind failureKind,
		string message,
		int? rpcCode = null,
		HttpStatusCode? httpStatusCode = null,
		Exception? innerException = null,
		string? method = null)
		: base(message, innerException)
	{
		FailureKind = failureKind;
		RpcCode = rpcCode;
		HttpStatusCode = httpStatusCode;
		Method = method;
	}

	public ElementsRpcFailureKind FailureKind { get; }
	public int? RpcCode { get; }
	public HttpStatusCode? HttpStatusCode { get; }

	/// <summary>
	/// The exact RPC method name string (e.g. <c>getnodegeneration</c>, <c>getblockchaininfo</c>,
	/// <c>sendrawtransaction</c>, the fee-asset calls) already interpolated into the message,
	/// carried as structured data so a caller never parses the message text. <see langword="null"/>
	/// only for a non-method-scoped failure.
	/// </summary>
	public string? Method { get; }
}

/// <summary>
/// The broadcast-operation stage at which a failure occurred, carried so the send executor can
/// distinguish a pre-submit observation-phase RPC rejection (zero broadcasts issued) from a
/// rejection returned by <c>sendrawtransaction</c> itself. Internal classification data only;
/// it changes no retry, fence, receipt-authority, or message shape.
/// </summary>
internal enum ElementsBroadcastStage
{
	/// <summary>The failure occurred before the <c>sendrawtransaction</c> call was issued.</summary>
	PreSubmitObservation,

	/// <summary>The failure is attributed to the <c>sendrawtransaction</c> call itself.</summary>
	Submit,
}

/// <summary>
/// The internal broadcast-stage wrapper: carries the exact <see cref="ElementsBroadcastStage"/>
/// alongside the original <see cref="ElementsRpcException"/> (as <see cref="Exception.InnerException"/>)
/// so the send executor can classify a broadcast failure without parsing message text. It wraps
/// only RPC-kind rejections; every other failure (transport, timeout, HTTP, protocol, mismatch)
/// propagates unwrapped because its stage cannot be proven pre-submit and is therefore
/// submission-ambiguous. No retry, loop, or second call follows.
/// </summary>
internal sealed class ElementsBroadcastStageException : Exception
{
	internal ElementsBroadcastStageException(ElementsBroadcastStage stage, ElementsRpcException rpcException)
		: base(rpcException.Message, rpcException)
	{
		ArgumentNullException.ThrowIfNull(rpcException);
		Stage = stage;
		RpcException = rpcException;
	}

	internal ElementsBroadcastStage Stage { get; }
	internal ElementsRpcException RpcException { get; }
}
