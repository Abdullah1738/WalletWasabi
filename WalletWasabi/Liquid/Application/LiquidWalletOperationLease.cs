using System;
using System.Threading;

namespace WalletWasabi.Liquid.Application;

internal sealed class LiquidWalletOperationLease : IDisposable
{
	private LiquidAuthenticatedWalletSession? _session;

	internal LiquidWalletOperationLease(LiquidAuthenticatedWalletSession session) =>
		_session = session ?? throw new ArgumentNullException(nameof(session));

	internal LiquidAuthenticatedWalletSession Session =>
		Volatile.Read(ref _session)
		?? throw new ObjectDisposedException(nameof(LiquidWalletOperationLease));

	public void Dispose()
	{
		LiquidAuthenticatedWalletSession? session = Interlocked.Exchange(ref _session, null);
		session?.ReleaseOperation();
	}
}
