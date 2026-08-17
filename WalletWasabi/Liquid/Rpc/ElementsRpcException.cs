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
		Exception? innerException = null)
		: base(message, innerException)
	{
		FailureKind = failureKind;
		RpcCode = rpcCode;
		HttpStatusCode = httpStatusCode;
	}

	public ElementsRpcFailureKind FailureKind { get; }
	public int? RpcCode { get; }
	public HttpStatusCode? HttpStatusCode { get; }
}
