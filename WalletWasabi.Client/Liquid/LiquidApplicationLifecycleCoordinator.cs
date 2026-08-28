using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace WalletWasabi.Client.Liquid;

internal sealed record LiquidApplicationCleanupResult(ImmutableArray<Exception> Errors);

internal sealed class LiquidApplicationLifecycleCoordinator
{
	private readonly Func<Task> _disposeCompositionAsync;
	private readonly Func<Task> _disposeGlobalAsync;
	private readonly Action _disposeSingleInstanceChecker;
	private readonly Action _appConfigTerminate;
	private readonly object _errorGate = new();
	private readonly List<Exception> _errors = [];
	private Task<LiquidApplicationCleanupResult>? _cleanupTask;
	private int _runEntered;
	private int _terminationRecorded;
	private bool _sealed;
	private LiquidApplicationCleanupResult? _finalResult;

	internal LiquidApplicationLifecycleCoordinator(LiquidWalletRuntimeComposition composition, Global global, SingleInstanceChecker singleInstanceChecker, Action appConfigTerminate)
		: this(
			composition is null ? throw new ArgumentNullException(nameof(composition)) : () => composition.DisposeAsync().AsTask(),
			(global ?? throw new ArgumentNullException(nameof(global))).DisposeAsync,
			(singleInstanceChecker ?? throw new ArgumentNullException(nameof(singleInstanceChecker))).Dispose,
			appConfigTerminate)
	{
	}

	internal LiquidApplicationLifecycleCoordinator(
		Func<Task> disposeCompositionAsync,
		Func<Task> disposeGlobalAsync,
		Action disposeSingleInstanceChecker,
		Action appConfigTerminate)
	{
		_disposeCompositionAsync = disposeCompositionAsync ?? throw new ArgumentNullException(nameof(disposeCompositionAsync));
		_disposeGlobalAsync = disposeGlobalAsync ?? throw new ArgumentNullException(nameof(disposeGlobalAsync));
		_disposeSingleInstanceChecker = disposeSingleInstanceChecker ?? throw new ArgumentNullException(nameof(disposeSingleInstanceChecker));
		_appConfigTerminate = appConfigTerminate ?? throw new ArgumentNullException(nameof(appConfigTerminate));
	}

	internal LiquidApplicationCleanupResult FinalResult => _finalResult ?? throw new InvalidOperationException("Cleanup has not completed.");

	internal void EnterRun()
	{
		if (Interlocked.CompareExchange(ref _runEntered, 1, 0) != 0)
		{
			throw new InvalidOperationException("Liquid application run may only be entered once.");
		}
	}

	internal void RecordSynchronousTermination()
	{
		lock (_errorGate)
		{
			if (_sealed || Interlocked.CompareExchange(ref _terminationRecorded, 1, 0) != 0)
			{
				return;
			}
		}

		try
		{
			_appConfigTerminate();
		}
		catch (Exception ex)
		{
			lock (_errorGate)
			{
				if (!_sealed)
				{
					_errors.Add(ex);
				}
			}
		}
	}

	internal async Task TerminateApplicationAsync() => await StartOrJoinCleanupAsync().ConfigureAwait(false);

	internal Task<LiquidApplicationCleanupResult> StartOrJoinCleanupAsync()
	{
		lock (_errorGate)
		{
			return _cleanupTask ??= CleanupAsync();
		}
	}

	private async Task<LiquidApplicationCleanupResult> CleanupAsync()
	{
		RecordSynchronousTermination();
		try
		{
			await _disposeCompositionAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			lock (_errorGate)
			{
				_errors.Add(ex);
			}
		}

		try
		{
			await _disposeGlobalAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			lock (_errorGate)
			{
				_errors.Add(ex);
			}
		}

		try
		{
			_disposeSingleInstanceChecker();
		}
		catch (Exception ex)
		{
			lock (_errorGate)
			{
				_errors.Add(ex);
			}
		}

		lock (_errorGate)
		{
			_sealed = true;
			_finalResult = new(_errors.ToImmutableArray());
			return _finalResult;
		}
	}
}
