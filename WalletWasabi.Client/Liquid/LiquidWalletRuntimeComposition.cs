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
		LiquidWalletRuntimeHandoff? publicHandoff,
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>? sendCommand = null)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		PublicHandoff = publicHandoff;
		SendCommand = sendCommand;
	}

	internal LiquidWalletRuntimeHandoff? PublicHandoff { get; }

	/// <summary>
	/// The composition-time send-execution command surface, built once by the WalletWasabi-resident
	/// command service's public static <c>CreateSendCommand</c> over the provider's typed session
	/// source. The composition stores only this public delegate; it never names the executor, the
	/// scope, the session, the RPC client, or any secret-bearing type.
	/// </summary>
	internal Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>? SendCommand { get; }

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
