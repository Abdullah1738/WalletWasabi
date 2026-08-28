using System;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Client.Liquid;

internal sealed class LiquidWalletRuntimeComposition : IAsyncDisposable
{
	private readonly object _disposeGate = new();
	private Task? _disposeTask;
	private int _disposed;

	internal LiquidWalletRuntimeComposition(LiquidWalletApplicationClient applicationClient)
	{
		ApplicationClient = applicationClient ?? throw new ArgumentNullException(nameof(applicationClient));
	}

	internal LiquidWalletApplicationClient ApplicationClient { get; }
	internal LiquidWalletRuntimeHandoff? PublicHandoff => ApplicationClient.CurrentHandoff;
	internal Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> SendCommand => ApplicationClient.SendCommand;
	internal Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> RefreshCommand => ApplicationClient.RefreshCommand;
	internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

	public ValueTask DisposeAsync()
	{
		lock (_disposeGate)
		{
			return new ValueTask(_disposeTask ??= DisposeApplicationClientAsync());
		}
	}

	private async Task DisposeApplicationClientAsync()
	{
		try
		{
			await ApplicationClient.DisposeAsync().ConfigureAwait(false);
		}
		finally
		{
			Volatile.Write(ref _disposed, 1);
		}
	}
}
