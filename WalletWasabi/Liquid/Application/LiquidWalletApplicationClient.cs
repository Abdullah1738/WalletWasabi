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
	private readonly Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> _sendCommand;
	private int _isDisposed;

	private LiquidWalletApplicationClient(
		LiquidWalletApplicationOptions options,
		LiquidWalletDirectories walletDirectories,
		LiquidAuthenticatedRuntimeProvider runtimeProvider,
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> sendCommand)
	{
		_options = options;
		_walletDirectories = walletDirectories;
		_runtimeProvider = runtimeProvider;
		_sendCommand = sendCommand;
	}

	internal LiquidWalletApplicationOptions Options => _options;
	internal LiquidAuthenticatedRuntimeProvider RuntimeProvider => _runtimeProvider;

	public LiquidWalletRuntimeHandoff? CurrentHandoff => _runtimeProvider.CurrentHandoff;

	public Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> SendCommand => _sendCommand;

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
			Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>> sendCommand =
				LiquidWalletSendExecutionCommandService.CreateSendCommand(runtimeProvider);
			return new LiquidWalletApplicationClient(
				canonicalOptions,
				walletDirectories,
				runtimeProvider,
				sendCommand);
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
