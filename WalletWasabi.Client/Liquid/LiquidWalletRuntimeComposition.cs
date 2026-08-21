using System;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidWalletRuntimeComposition : IAsyncDisposable
{
	private readonly LiquidAuthenticatedRuntimeProvider _provider;
	private int _disposed;

	internal LiquidWalletRuntimeComposition(
		LiquidAuthenticatedRuntimeProvider provider,
		LiquidWalletRuntimeHandoff publicHandoff)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		PublicHandoff = publicHandoff ?? throw new ArgumentNullException(nameof(publicHandoff));
	}

	internal LiquidWalletRuntimeHandoff PublicHandoff { get; }
	internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		await _provider.DisposeAsync().ConfigureAwait(false);
	}
}
