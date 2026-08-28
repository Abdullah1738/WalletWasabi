using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Liquid.Application;

internal sealed class ElementsPublicNetworkManifestSource
{
	internal ElementsPublicNetworkManifestSource(string manifestId) =>
		ManifestId = string.IsNullOrWhiteSpace(manifestId) ? throw new ArgumentException("A manifest identity is required.", nameof(manifestId)) : manifestId;

	internal string ManifestId { get; }
}

internal sealed class LiquidAuthenticatedRuntimeProvider : IAsyncDisposable
{
	private const uint ReplayContextBranchIndex = 1108790945;
	private const string ReplayKeyInfo = "WalletWasabi/Liquid/v1/replay";
	private const string ContextKeyInfo = "WalletWasabi/Liquid/v1/context";

	private readonly LiquidRpcProfileSource _rpcProfileSource;
	private readonly LiquidWalletDirectories _walletDirectories;
	private readonly ElementsPublicNetworkManifestSource _manifestSource;
	private readonly Action<string>? _sendRefreshSink;
	private readonly Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> _refreshCommand;
	private readonly Func<LiquidAuthenticatedWalletSession, CancellationToken, Task>? _beforePublicationAsync;
	private readonly object _gate = new();
	private LiquidAuthenticatedWalletSession? _session;
	private OpenReservation? _openReservation;
	private DetachedClose? _detachedClose;
	private LiquidWalletRuntimeHandoff? _currentHandoff;
	private Task? _disposeTask;
	private bool _disposed;

	internal LiquidAuthenticatedRuntimeProvider(
		LiquidRpcProfileSource rpcProfileSource,
		LiquidWalletDirectories walletDirectories,
		ElementsPublicNetworkManifestSource manifestSource,
		Action<string>? sendRefreshSink = null,
		Func<LiquidAuthenticatedWalletSession, CancellationToken, Task>? beforePublicationAsync = null)
	{
		_rpcProfileSource = rpcProfileSource ?? throw new ArgumentNullException(nameof(rpcProfileSource));
		_walletDirectories = walletDirectories ?? throw new ArgumentNullException(nameof(walletDirectories));
		_manifestSource = manifestSource ?? throw new ArgumentNullException(nameof(manifestSource));
		_sendRefreshSink = sendRefreshSink;
		_refreshCommand = LiquidWalletRefreshCommandService.CreateRefreshCommand(this);
		_beforePublicationAsync = beforePublicationAsync;
	}

	internal LiquidWalletRuntimeHandoff? CurrentHandoff
	{
		get
		{
			lock (_gate)
			{
				return _currentHandoff;
			}
		}
	}

	internal async ValueTask<LiquidAuthenticatedWalletSession> OpenAsync(
		LiquidWalletIdentity identity,
		char[] passwordBuffer,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(passwordBuffer);
		OpenReservation reservation = ReserveOpen(identity);
		LiquidWalletSignerKeyAdapter? adapter = null;
		ElementsRpcClient? rpcClient = null;
		bool providerOwnsRpcClient = false;
		LiquidAuthenticatedWalletSession? candidate = null;
		ExtKey? root = null;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			LiquidRpcProfile profile = _rpcProfileSource.LoadValidated(identity.RuntimeProfileName);
			if (!StringComparer.Ordinal.Equals(profile.Manifest, identity.NetworkManifestId))
			{
				throw new InvalidDataException("The RPC profile violates 'profile_manifest'.");
			}
			ValidateIdentity(identity);
			ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(identity.NetworkManifestId);
			if (!StringComparer.Ordinal.Equals(profile.Network, manifest.ChainRpcName))
			{
				throw new InvalidDataException("The RPC profile violates 'profile_network'.");
			}
			ElementsNodeExpectation nodeExpectation = ElementsReviewedNodeExpectationSource.Bind(manifest, profile);

			KeyManager keyManager = KeyManager.FromFile(identity.CanonicalWalletFilePath);
			try
			{
				root = keyManager.GetMasterExtKey(new string(passwordBuffer));
			}
			catch (System.Security.SecurityException)
			{
				throw new InvalidDataException("Liquid wallet authentication failed.");
			}

			using LiquidRpcAuthenticationLease lease = new LiquidRpcCookieCredentialSource(profile).Acquire();
			rpcClient = ElementsRpcClient.Create(
				profile.Endpoint,
				new NetworkCredential(lease.Username.ToString(), lease.Password.ToString()),
				new ElementsRpcTimeouts(profile.ConnectTimeout, profile.RequestTimeout, profile.RequestTimeout));
			providerOwnsRpcClient = true;
			Func<string, (int Account, int Change, int Index)?> outpointLocator = BuildOutpointLocator(identity, root);
			adapter = new LiquidWalletSignerKeyAdapter(root, outpointLocator, keyManager.GetNetwork());
			LiquidAuthenticatedWalletStateOwner stateOwner = LiquidAuthenticatedWalletStateOwner.Open(
				identity,
				manifest,
				nodeExpectation,
				_walletDirectories.WalletDirectory,
				root,
				adapter,
				rpcClient);

			string descriptor = stateOwner.Descriptor;
			ulong lastIndex = stateOwner.LastIndex;
			byte[] rootPrivateKey = root.PrivateKey.ToBytes();
			byte[] slip77 = Array.Empty<byte>();
			try
			{
				slip77 = LiquidKeyDomain.DeriveHkdf(
					rootPrivateKey,
					Array.Empty<byte>(),
					"WalletWasabi/Liquid/v1/slip77");
				using (LiquidWalletNativeSigner.Create(adapter, descriptor, lastIndex, slip77))
				{
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(slip77);
				CryptographicOperations.ZeroMemory(rootPrivateKey);
			}

			LiquidWalletRuntimeHandoff handoff = new(
				identity.CanonicalWalletId,
				identity.NetworkManifestId,
				stateOwner.Balances,
				stateOwner.SelectableOutputs,
				stateOwner.History,
				stateOwner.ReceiveMaterial);
#pragma warning disable CA2000 // Ownership transfers to the provider's session registry below.
			candidate = new LiquidAuthenticatedWalletSession(
				identity,
				handoff,
				keyManager,
				adapter,
				manifest,
				rpcClient,
				root,
				stateOwner,
				descriptor,
				lastIndex,
				_walletDirectories.WalletDirectory,
				_sendRefreshSink);
#pragma warning restore CA2000
			adapter = null;
			providerOwnsRpcClient = false;
			rpcClient = null;
			root = null;

			if (_beforePublicationAsync is not null)
			{
				await _beforePublicationAsync(candidate, cancellationToken).ConfigureAwait(false);
			}

			LiquidAuthenticatedWalletSession publishedSession;
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (!ReferenceEquals(_openReservation, reservation) || _session is not null)
				{
					throw new InvalidOperationException("The Liquid wallet open reservation is no longer valid.");
				}
			publishedSession = candidate;
			_session = publishedSession;
			candidate = null;
			_currentHandoff = handoff;
			_openReservation = null;
			}

			reservation.Completion.TrySetResult(null);
			return publishedSession;
		}
		catch (Exception originalException)
		{
			List<Exception>? cleanupErrors = null;
			if (candidate is not null)
			{
				try
				{
					await candidate.DisposeAsync().ConfigureAwait(false);
				}
				catch (Exception cleanupException)
				{
					(cleanupErrors ??= []).Add(cleanupException);
				}
			}
			else
			{
				try
				{
					adapter?.Dispose();
					if (adapter is null)
					{
						root?.PrivateKey.Dispose();
					}
				}
				catch (Exception cleanupException)
				{
					(cleanupErrors ??= []).Add(cleanupException);
				}
				try
				{
					if (providerOwnsRpcClient)
					{
						rpcClient?.Dispose();
					}
				}
				catch (Exception cleanupException)
				{
					(cleanupErrors ??= []).Add(cleanupException);
				}
			}

			lock (_gate)
			{
				if (ReferenceEquals(_openReservation, reservation))
				{
					_openReservation = null;
				}
			}

			if (cleanupErrors is null)
			{
				reservation.Completion.TrySetResult(null);
				throw;
			}

			Exception cleanupFailure = cleanupErrors.Count == 1
				? cleanupErrors[0]
				: new AggregateException(cleanupErrors);
			reservation.Completion.TrySetException(cleanupFailure);
			throw new AggregateException([originalException, .. cleanupErrors]);
		}
	}

	internal ValueTask CloseAsync(LiquidWalletIdentity identity, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(identity);
		if (!StringComparer.Ordinal.Equals(identity.NetworkManifestId, _manifestSource.ManifestId))
		{
			return ValueTask.CompletedTask;
		}

		return CloseAsync(identity.CanonicalWalletId, cancellationToken);
	}

	internal async ValueTask CloseAsync(string canonicalWalletId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(canonicalWalletId);
		cancellationToken.ThrowIfCancellationRequested();
		Task closeTask;
		LiquidAuthenticatedWalletSession? sessionToDispose = null;
		Task drainTask = Task.CompletedTask;
		DetachedClose? closeToComplete = null;
		lock (_gate)
		{
			string registryKey = RegistryKey(canonicalWalletId, _manifestSource.ManifestId);
			if (_detachedClose is { } detachedClose
				&& StringComparer.Ordinal.Equals(detachedClose.RegistryKey, registryKey))
			{
				// A detached close remains provider-owned until its exact disposal task has
				// completed. In particular, allow this join after provider disposal starts.
				closeTask = detachedClose.DisposeTask;
			}
			else
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_openReservation is { } reservation
					&& StringComparer.Ordinal.Equals(reservation.RegistryKey, registryKey))
				{
					throw new InvalidOperationException("The Liquid wallet open is in progress.");
				}

				LiquidAuthenticatedWalletSession? session = _session;
				if (session is null
					|| !StringComparer.Ordinal.Equals(session.Identity.CanonicalWalletId, canonicalWalletId)
					|| !StringComparer.Ordinal.Equals(session.Identity.NetworkManifestId, _manifestSource.ManifestId))
				{
					return;
				}

				_session = null;
				drainTask = session.BeginCloseUnderProviderGate();
				closeToComplete = new DetachedClose(registryKey);
				_detachedClose = closeToComplete;
				closeTask = closeToComplete.DisposeTask;
				sessionToDispose = session;
				if (ReferenceEquals(_currentHandoff, session.PublicHandoffOrNull))
				{
					_currentHandoff = null;
				}
			}
		}

		if (sessionToDispose is not null)
		{
			_ = CompleteDetachedCloseAsync(closeToComplete!, sessionToDispose, drainTask);
		}

		await closeTask.ConfigureAwait(false);
	}

	internal LiquidWalletOperationLease AcquireOperation(string canonicalWalletId)
	{
		ArgumentException.ThrowIfNullOrEmpty(canonicalWalletId);
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			LiquidAuthenticatedWalletSession session = _session
				?? throw new InvalidOperationException("No authenticated Liquid wallet session is open for the named wallet.");
			if (!StringComparer.Ordinal.Equals(session.Identity.CanonicalWalletId, canonicalWalletId)
				|| !StringComparer.Ordinal.Equals(session.Identity.NetworkManifestId, _manifestSource.ManifestId))
			{
				throw new InvalidOperationException("The authenticated Liquid wallet session does not match the named wallet and bound manifest.");
			}

			return session.AcquireOperationUnderProviderGate();
		}
	}

	internal LiquidAuthenticatedWalletSession? TryGetOpenSession(string canonicalWalletId)
	{
		ArgumentException.ThrowIfNullOrEmpty(canonicalWalletId);
		lock (_gate)
		{
			return _session is { } session
				&& StringComparer.Ordinal.Equals(session.Identity.CanonicalWalletId, canonicalWalletId)
				&& !session.IsDisposed
				? session
				: null;
		}
	}

	/// <summary>
	/// The provider's nonthrowing refresh-publication sink. Publishes <paramref name="alreadyInstalledHandoff"/>
	/// as the current public handoff only when <paramref name="session"/> is still the exact published session
	/// and <paramref name="alreadyInstalledHandoff"/> is that session's current snapshot handoff. When the
	/// session was detached/closed or the handoff is not the session's live pair, publication is a no-op and
	/// this returns <see langword="false"/>; it never republishes a closing wallet and never throws after a
	/// successful save. Runs entirely under the provider gate; it never awaits and never invokes callbacks.
	/// </summary>
	internal bool TryPublishRefresh(
		LiquidAuthenticatedWalletSession session,
		LiquidWalletRuntimeHandoff alreadyInstalledHandoff)
	{
		if (session is null || alreadyInstalledHandoff is null)
		{
			return false;
		}

		lock (_gate)
		{
			if (!ReferenceEquals(_session, session))
			{
				return false;
			}

			if (!ReferenceEquals(session.PublicHandoff, alreadyInstalledHandoff))
			{
				return false;
			}

			_currentHandoff = alreadyInstalledHandoff;
			return true;
		}
	}

	public ValueTask DisposeAsync()
	{
		TaskCompletionSource<object?> completion;
		LiquidAuthenticatedWalletSession? session;
		Task drainTask;
		Task reservationTask;
		Task? detachedCloseTask;
		lock (_gate)
		{
			if (_disposeTask is not null)
			{
				return new ValueTask(_disposeTask);
			}

			completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
			_disposeTask = completion.Task;
			_disposed = true;
			reservationTask = _openReservation?.Completion.Task ?? Task.CompletedTask;
			detachedCloseTask = _detachedClose?.DisposeTask;
			session = _session;
			_session = null;
			drainTask = session?.BeginCloseUnderProviderGate() ?? Task.CompletedTask;
			if (session is not null && ReferenceEquals(_currentHandoff, session.PublicHandoffOrNull))
			{
				_currentHandoff = null;
			}
		}

		_ = CompleteDisposalAsync(completion, reservationTask, detachedCloseTask, session, drainTask);
		return new ValueTask(completion.Task);
	}

	private static async Task CompleteDetachedCloseAsync(
		DetachedClose detachedClose,
		LiquidAuthenticatedWalletSession session,
		Task drainTask)
	{
		try
		{
			await session.DisposeAfterDrainAsync(drainTask).ConfigureAwait(false);
			detachedClose.Completion.TrySetResult(null);
		}
		catch (Exception exception)
		{
			detachedClose.Completion.TrySetException(exception);
		}
	}

	private static async Task CompleteDisposalAsync(
		TaskCompletionSource<object?> completion,
		Task reservationTask,
		Task? detachedCloseTask,
		LiquidAuthenticatedWalletSession? session,
		Task drainTask)
	{
		List<Exception>? errors = null;
		try
		{
			await reservationTask.ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			(errors ??= []).Add(exception);
		}

		if (detachedCloseTask is not null && !ReferenceEquals(detachedCloseTask, drainTask))
		{
			try
			{
				await detachedCloseTask.ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				(errors ??= []).Add(exception);
			}
		}

		if (session is not null)
		{
			try
			{
				await session.DisposeAfterDrainAsync(drainTask).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				(errors ??= []).Add(exception);
			}
		}

		if (errors is null)
		{
			completion.TrySetResult(null);
		}
		else if (errors.Count == 1)
		{
			completion.TrySetException(errors[0]);
		}
		else
		{
			completion.TrySetException(new AggregateException(errors));
		}
	}

	private OpenReservation ReserveOpen(LiquidWalletIdentity identity)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_detachedClose is { } detachedClose)
			{
				if (!detachedClose.DisposeTask.IsCompleted)
				{
					throw new InvalidOperationException("The Liquid wallet session is closing.");
				}

				if (!detachedClose.DisposeTask.IsCompletedSuccessfully)
				{
					throw new InvalidOperationException("The previous Liquid wallet session close failed.", detachedClose.DisposeTask.Exception?.InnerException);
				}

				_detachedClose = null;
			}

			if (_session is not null || _openReservation is not null)
			{
				throw new InvalidOperationException("A Liquid wallet session is already open or opening.");
			}

			return _openReservation = new OpenReservation(RegistryKey(identity));
		}
	}

	private Func<string, (int Account, int Change, int Index)?> BuildOutpointLocator(LiquidWalletIdentity identity, ExtKey root)
	{
		string walletDataDir = _walletDirectories.WalletDirectory;
		string walletName = identity.CanonicalWalletId;
		ExtKey replayContextChild = root.Derive(new KeyPath(ReplayContextBranchIndex | 0x80000000U));
		byte[] keyMaterial = replayContextChild.PrivateKey.ToBytes();
		try
		{
			byte[] salt = ComputePersistenceSalt(identity);
			byte[] key = LiquidKeyDomain.DeriveHkdf(keyMaterial, salt, ReplayKeyInfo);
			byte[] externalWalletNetworkContext = LiquidKeyDomain.DeriveHkdf(keyMaterial, salt, ContextKeyInfo);
			try
			{
				Dictionary<string, (int Account, int Change, int Index)> map = new(StringComparer.Ordinal);
				string filePath = Path.Combine(walletDataDir, walletName + ".lwwal");
				if (File.Exists(filePath))
				{
					foreach (KeyValuePair<string, LiquidWalletUiOutpointCoordinate> entry in
						LiquidWalletUiFacade.LoadAndGetOutpointSpendCoordinates(
							walletDataDir,
							walletName,
							key,
							externalWalletNetworkContext))
					{
						map[entry.Key] = (entry.Value.Account, entry.Value.Change, entry.Value.Index);
					}
				}

				return outpointHex =>
				{
					try
					{
						return outpointHex is not null && map.TryGetValue(outpointHex, out (int Account, int Change, int Index) coordinates)
							? coordinates
							: null;
					}
					catch (Exception)
					{
						return null;
					}
				};
			}
			finally
			{
				CryptographicOperations.ZeroMemory(key);
				CryptographicOperations.ZeroMemory(externalWalletNetworkContext);
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(keyMaterial);
		}
	}

	private static byte[] ComputePersistenceSalt(LiquidWalletIdentity identity)
	{
		byte[] networkGenesisDisplay = Encoding.UTF8.GetBytes(identity.NetworkManifestId);
		byte[] canonicalWalletId = Encoding.UTF8.GetBytes(identity.CanonicalWalletId);
		byte[] saltInput = new byte[networkGenesisDisplay.Length + canonicalWalletId.Length];
		networkGenesisDisplay.CopyTo(saltInput, 0);
		canonicalWalletId.CopyTo(saltInput, networkGenesisDisplay.Length);
		return SHA256.HashData(saltInput);
	}

	private void ValidateIdentity(LiquidWalletIdentity identity)
	{
		if (!StringComparer.Ordinal.Equals(identity.NetworkManifestId, _manifestSource.ManifestId))
		{
			throw new InvalidDataException("The wallet manifest identity is invalid.");
		}
		string root = Path.GetFullPath(_walletDirectories.WalletDirectory) + Path.DirectorySeparatorChar;
		if (!identity.CanonicalWalletFilePath.StartsWith(root, StringComparison.Ordinal))
		{
			throw new InvalidDataException("The wallet path is outside the configured Liquid wallet directory.");
		}
	}

	private static bool IdentityMatches(LiquidAuthenticatedWalletSession session, LiquidWalletIdentity identity) =>
		StringComparer.Ordinal.Equals(session.Identity.CanonicalWalletId, identity.CanonicalWalletId)
		&& StringComparer.Ordinal.Equals(session.Identity.NetworkManifestId, identity.NetworkManifestId);

	private static string RegistryKey(LiquidWalletIdentity identity) =>
		RegistryKey(identity.CanonicalWalletId, identity.NetworkManifestId);

	private static string RegistryKey(string canonicalWalletId, string networkManifestId) =>
		canonicalWalletId + "\0" + networkManifestId;

	internal string ManifestId => _manifestSource.ManifestId;

	internal Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> RefreshCommand =>
		_refreshCommand;

	private sealed record DetachedClose(string RegistryKey)
	{
		internal TaskCompletionSource<object?> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal Task DisposeTask => Completion.Task;
	}

	private sealed record OpenReservation(string RegistryKey)
	{
		internal TaskCompletionSource<object?> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
