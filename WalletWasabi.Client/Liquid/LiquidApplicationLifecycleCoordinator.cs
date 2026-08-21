using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace WalletWasabi.Client.Liquid;

internal sealed record LiquidApplicationCleanupResult(ImmutableArray<Exception> Errors);

internal sealed class LiquidApplicationLifecycleCoordinator
{
	private readonly LiquidWalletRuntimeComposition _composition;
	private readonly Global _global;
	private readonly SingleInstanceChecker _singleInstanceChecker;
	private readonly Action _appConfigTerminate;
	private readonly object _errorGate = new();
	private readonly List<Exception> _errors = [];
	private Task<LiquidApplicationCleanupResult>? _cleanupTask;
	private int _runEntered;
	private int _terminationRecorded;
	private bool _sealed;
	private LiquidApplicationCleanupResult? _finalResult;

	internal LiquidApplicationLifecycleCoordinator(LiquidWalletRuntimeComposition composition, Global global, SingleInstanceChecker singleInstanceChecker, Action appConfigTerminate)
	{
		_composition = composition ?? throw new ArgumentNullException(nameof(composition));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_singleInstanceChecker = singleInstanceChecker ?? throw new ArgumentNullException(nameof(singleInstanceChecker));
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
			await _composition.DisposeAsync().ConfigureAwait(false);
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
			await _global.DisposeAsync().ConfigureAwait(false);
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
			_singleInstanceChecker.Dispose();
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
