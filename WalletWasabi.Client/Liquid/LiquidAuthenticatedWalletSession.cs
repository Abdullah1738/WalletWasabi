using System;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidAuthenticatedWalletSession : IAsyncDisposable
{
	private int _disposed;

	internal LiquidAuthenticatedWalletSession(
		LiquidWalletIdentity identity,
		LiquidWalletRuntimeHandoff publicHandoff,
		KeyManager keyManager,
		LiquidWalletSignerKeyAdapter signerKeyAdapter,
		ElementsRpcClient rpcClient)
	{
		Identity = identity ?? throw new ArgumentNullException(nameof(identity));
		PublicHandoff = publicHandoff ?? throw new ArgumentNullException(nameof(publicHandoff));
		KeyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
		SignerKeyAdapter = signerKeyAdapter ?? throw new ArgumentNullException(nameof(signerKeyAdapter));
		RpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
	}

	internal LiquidWalletIdentity Identity { get; }
	internal LiquidWalletRuntimeHandoff PublicHandoff { get; }
	internal KeyManager KeyManager { get; }
	internal LiquidWalletSignerKeyAdapter SignerKeyAdapter { get; }
	internal ElementsRpcClient RpcClient { get; }
	internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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
