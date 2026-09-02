using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Liquid.Application;

public sealed class LiquidWalletApplicationClient : IAsyncDisposable
{
	private const int MaxPasswordLength = 1024;
	private readonly LiquidWalletApplicationOptions _options;
	private readonly LiquidWalletDirectories _walletDirectories;
	private readonly LiquidAuthenticatedRuntimeProvider _runtimeProvider;
	private readonly Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> _refreshCommand;
	private readonly Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> _sendCommand;
	private readonly Func<LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> _setReceiveLabelsCommand;
	private int _isDisposed;

	private LiquidWalletApplicationClient(
		LiquidWalletApplicationOptions options,
		LiquidWalletDirectories walletDirectories,
		LiquidAuthenticatedRuntimeProvider runtimeProvider,
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> refreshCommand,
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> sendCommand,
		Func<LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> setReceiveLabelsCommand)
	{
		_options = options;
		_walletDirectories = walletDirectories;
		_runtimeProvider = runtimeProvider;
		_refreshCommand = refreshCommand;
		_sendCommand = sendCommand;
		_setReceiveLabelsCommand = setReceiveLabelsCommand;
	}

	internal LiquidWalletApplicationOptions Options => _options;
	internal LiquidAuthenticatedRuntimeProvider RuntimeProvider => _runtimeProvider;

	public LiquidWalletRuntimeHandoff? CurrentHandoff => _runtimeProvider.CurrentHandoff;

	public Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> RefreshCommand => _refreshCommand;

	public Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> SendCommand => _sendCommand;

	/// <summary>
	/// The single narrow public surface the Fluent receive-label write path
	/// calls: persists the durable label set bound to the wallet's current
	/// next-receive derivation index through the landed, generation-fenced
	/// receive-label command service. The command runs entirely inside this
	/// assembly: it resolves the open authenticated session for
	/// <see cref="LiquidWalletUiSetReceiveLabelsRequest.CanonicalWalletId"/>,
	/// reads the session's current next-receive index, invokes the internal
	/// command, and on success republishes the handoff so the rebound
	/// <see cref="LiquidWalletUiReceiveMaterial.NextReceiveLabels"/> is live.
	/// Key material never crosses this public signature. Fail-closed: any
	/// rejection from the landed surface surfaces as-is.
	/// </summary>
	public async Task SetNextReceiveLabelsAsync(
		LiquidWalletUiSetReceiveLabelsRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();

		LiquidAuthenticatedWalletSession session = _runtimeProvider.TryGetOpenSession(request.CanonicalWalletId)
			?? throw new InvalidOperationException("No authenticated Liquid wallet session is open for the named wallet.");

		uint index = checked((uint)session.StateOwner.LastIndex);
		await _setReceiveLabelsCommand(
			new LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest(
				request.CanonicalWalletId,
				index,
				request.Labels)).ConfigureAwait(false);
	}

	public static LiquidWalletApplicationClient Create(LiquidWalletApplicationOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		ElementsPublicNetworkManifest manifest =
			ElementsPublicNetworkManifest.GetByManifestId(options.ReviewedManifestId);
		string applicationDataDirectory = RequireCanonicalDirectory(
			options.ApplicationDataDirectory,
			nameof(options.ApplicationDataDirectory));
		string liquidWalletDirectory = RequireCanonicalDirectory(
			options.LiquidWalletDirectory,
			nameof(options.LiquidWalletDirectory));
		var canonicalOptions = new LiquidWalletApplicationOptions(
			applicationDataDirectory,
			liquidWalletDirectory,
			manifest.ManifestId);
		var rpcProfileSource = new LiquidRpcProfileSource(applicationDataDirectory);
		var walletDirectories = new LiquidWalletDirectories(liquidWalletDirectory);
		var manifestSource = new ElementsPublicNetworkManifestSource(manifest.ManifestId);
		LiquidAuthenticatedRuntimeProvider? runtimeProvider = null;

		try
		{
			runtimeProvider = new LiquidAuthenticatedRuntimeProvider(
				rpcProfileSource,
				walletDirectories,
				manifestSource,
				sendRefreshSink: null);
			Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> refreshCommand =
				runtimeProvider.RefreshCommand;
			Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> sendCommand =
				LiquidWalletSendExecutionCommandService.CreateSendCommand(runtimeProvider);
			Func<LiquidWalletReceiveLabelCommandService.SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> setReceiveLabelsCommand =
				LiquidWalletReceiveLabelCommandService.CreateSetReceiveLabelsCommand(runtimeProvider);
			return new LiquidWalletApplicationClient(
				canonicalOptions,
				walletDirectories,
				runtimeProvider,
				refreshCommand,
				sendCommand,
				setReceiveLabelsCommand);
		}
		catch (Exception originalException)
		{
			if (runtimeProvider is null)
			{
				throw;
			}

			try
			{
				runtimeProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
			}
			catch (Exception cleanupException)
			{
				throw new AggregateException(originalException, cleanupException);
			}

			ExceptionDispatchInfo.Capture(originalException).Throw();
			throw;
		}
	}

	public LiquidWalletOpenAuthorization CreateOpenAuthorization(ReadOnlySpan<char> password)
	{
		ThrowIfDisposed();
		if (password.IsEmpty || password.Length > MaxPasswordLength)
		{
			throw new ArgumentException("A password between 1 and 1024 characters is required.", nameof(password));
		}

		return new(password.ToArray());
	}

	public async ValueTask<LiquidWalletRuntimeHandoff> OpenAsync(
		LiquidWalletOpenRequest request,
		LiquidWalletOpenAuthorization authorization,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(authorization);
		char[] buffer = authorization.TakeBuffer();
		try
		{
			ArgumentNullException.ThrowIfNull(request);
			ThrowIfDisposed();
			cancellationToken.ThrowIfCancellationRequested();
			LiquidWalletIdentity identity = LiquidWalletIdentity.Create(
				request.CanonicalWalletId,
				request.CanonicalWalletFilePath,
				request.RuntimeProfileName,
				_options.ReviewedManifestId,
				_walletDirectories);
			LiquidAuthenticatedWalletSession session =
				await _runtimeProvider.OpenAsync(identity, buffer, cancellationToken).ConfigureAwait(false);
			return session.PublicHandoff;
		}
		finally
		{
			LiquidWalletOpenAuthorization.ZeroBuffer(buffer);
		}
	}

	public ValueTask CloseAsync(string canonicalWalletId, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		return _runtimeProvider.CloseAsync(canonicalWalletId, cancellationToken);
	}

	public ValueTask DisposeAsync()
	{
		Interlocked.Exchange(ref _isDisposed, 1);
		return _runtimeProvider.DisposeAsync();
	}

	private static string RequireCanonicalDirectory(string path, string parameterName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
		if (!Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException("The directory path must be absolute.", parameterName);
		}

		string canonicalPath = Path.GetFullPath(path);
		if (!Directory.Exists(canonicalPath))
		{
			throw new DirectoryNotFoundException(canonicalPath);
		}
		if (File.GetAttributes(canonicalPath).HasFlag(FileAttributes.ReparsePoint))
		{
			throw new SecurityException("The directory must not be a link.");
		}

		return canonicalPath;
	}

	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref _isDisposed) != 0)
		{
			throw new ObjectDisposedException(nameof(LiquidWalletApplicationClient));
		}
	}
}
