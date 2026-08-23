using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace WalletWasabi.Client.Liquid;

internal sealed class ElementsPublicNetworkManifestSource
{
	internal ElementsPublicNetworkManifestSource(string manifestId) =>
		ManifestId = string.IsNullOrWhiteSpace(manifestId) ? throw new ArgumentException("A manifest identity is required.", nameof(manifestId)) : manifestId;

	internal string ManifestId { get; }
}

internal sealed class LiquidAuthenticatedRuntimeProvider : IAsyncDisposable, ILiquidWalletSendSessionSource
{
	// The frozen Liquid v1 replay/context branch: a hardened child of the
	// authenticated master whose private key is the HKDF key material for the
	// per-wallet persistence key and external network context.
	private const uint ReplayContextBranchIndex = 1108790945;
	private const string ReplayKeyInfo = "WalletWasabi/Liquid/v1/replay";
	private const string ContextKeyInfo = "WalletWasabi/Liquid/v1/context";

	private readonly LiquidRpcProfileSource _rpcProfileSource;
	private readonly LiquidWalletDirectories _walletDirectories;
	private readonly ElementsPublicNetworkManifestSource _manifestSource;
	private readonly Action<LiquidWalletRuntimeHandoff>? _publishHandoff;
	private readonly Action<string>? _sendRefreshSink;
	private readonly Dictionary<string, LiquidAuthenticatedWalletSession> _sessions = new(StringComparer.Ordinal);
	private readonly object _gate = new();
	private bool _disposed;

	internal LiquidAuthenticatedRuntimeProvider(
		LiquidRpcProfileSource rpcProfileSource,
		LiquidWalletDirectories walletDirectories,
		ElementsPublicNetworkManifestSource manifestSource,
		Action<LiquidWalletRuntimeHandoff>? publishHandoff = null,
		Action<string>? sendRefreshSink = null)
	{
		_rpcProfileSource = rpcProfileSource ?? throw new ArgumentNullException(nameof(rpcProfileSource));
		_walletDirectories = walletDirectories ?? throw new ArgumentNullException(nameof(walletDirectories));
		_manifestSource = manifestSource ?? throw new ArgumentNullException(nameof(manifestSource));
		_publishHandoff = publishHandoff;
		_sendRefreshSink = sendRefreshSink;
	}

	internal async ValueTask<LiquidAuthenticatedWalletSession> OpenAsync(
		LiquidWalletIdentity identity,
		LiquidPasswordAuthorizationLease passwordAuthorization,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(passwordAuthorization);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			ValidateIdentity(identity);
			LiquidRpcProfile profile = _rpcProfileSource.LoadValidated(identity.RuntimeProfileName);
			if (!StringComparer.Ordinal.Equals(profile.Manifest, identity.NetworkManifestId))
			{
				throw new InvalidDataException("The RPC profile manifest does not match the wallet identity.");
			}

			KeyManager km = KeyManager.FromFile(identity.CanonicalWalletFilePath);
			ExtKey root;
			try
			{
				root = km.GetMasterExtKey(new string(passwordAuthorization.Password));
			}
			catch (System.Security.SecurityException)
			{
				throw new InvalidDataException("Liquid wallet authentication failed.");
			}

			using LiquidRpcAuthenticationLease lease = new LiquidRpcCookieCredentialSource(profile).Acquire();
			ElementsRpcClient rpcClient = ElementsRpcClient.Create(
				profile.Endpoint,
				new NetworkCredential(lease.Username.ToString(), lease.Password.ToString()),
				new ElementsRpcTimeouts(profile.ConnectTimeout, profile.RequestTimeout, profile.RequestTimeout));
			Func<string, (int Account, int Change, int Index)?> outpointLocator = BuildOutpointLocator(identity, root);
			LiquidWalletSignerKeyAdapter adapter = new(root, outpointLocator, km.GetNetwork());
			ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(identity.NetworkManifestId);
			LiquidAuthenticatedWalletStateOwner stateOwner = LiquidAuthenticatedWalletStateOwner.Open(
				identity,
				manifest,
				_walletDirectories.WalletDirectory,
				root,
				adapter,
				rpcClient);

			// The descriptor is the real Liquid account xpub catalog and LastIndex is the durable
			// external allocation made by the state owner, never the root key or an observed-output guess.
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
			LiquidAuthenticatedWalletSession session = new(
				identity,
				handoff,
				km,
				adapter,
				rpcClient,
				root,
				stateOwner,
				descriptor,
				lastIndex,
				_walletDirectories.WalletDirectory,
				_sendRefreshSink);
			string key = RegistryKey(identity);
			bool duplicate;
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				duplicate = !_sessions.TryAdd(key, session);
			}
			if (duplicate)
			{
				// Duplicate open: this session owns a cookie-bearing RPC client and a
				// retained master-key copy. Dispose it before refusing so no secret-
				// bearing resources are orphaned. Disposal happens outside the lock.
				await session.DisposeAsync().ConfigureAwait(false);
				throw new InvalidOperationException("The Liquid wallet already has an active authenticated session.");
			}

			// Publication is the application's assignment-only surface; when no sink is
			// wired (provider used without a composition root) this is a no-op.
			_publishHandoff?.Invoke(handoff);

			return session;
		}
		finally
		{
			passwordAuthorization.Dispose();
		}
	}

	internal async ValueTask CloseAsync(LiquidWalletIdentity identity, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(identity);
		cancellationToken.ThrowIfCancellationRequested();
		LiquidAuthenticatedWalletSession? session;
		lock (_gate)
		{
			string key = RegistryKey(identity);
			session = _sessions.TryGetValue(key, out LiquidAuthenticatedWalletSession? existing) ? existing : null;
			_sessions.Remove(key);
		}

		if (session is not null)
		{
			await session.DisposeAsync().ConfigureAwait(false);
			session = null;
		}
	}

	/// <summary>
	/// Resolves the live authenticated session for one canonical wallet id, or
	/// <see langword="null"/> when no session is open. Internal-only: the per-call execution
	/// scope factory uses this to re-derive secret-bearing values from the session's retained
	/// authenticated master at scope-open time; the returned session is never disposed by the
	/// caller.
	/// </summary>
	internal LiquidAuthenticatedWalletSession? TryGetOpenSession(string canonicalWalletId)
	{
		ArgumentException.ThrowIfNullOrEmpty(canonicalWalletId);
		lock (_gate)
		{
			foreach (LiquidAuthenticatedWalletSession session in _sessions.Values)
			{
				if (StringComparer.Ordinal.Equals(session.Identity.CanonicalWalletId, canonicalWalletId)
					&& !session.IsDisposed)
				{
					return session;
				}
			}
		}
		return null;
	}

	public async ValueTask DisposeAsync()
	{
		LiquidAuthenticatedWalletSession[] sessions;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			sessions = _sessions.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value).ToArray();
			_sessions.Clear();
		}

		List<Exception>? errors = null;
		foreach (LiquidAuthenticatedWalletSession session in sessions)
		{
			try
			{
				await session.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				(errors ??= []).Add(ex);
			}
		}

		if (errors is { Count: 1 })
		{
			throw errors[0];
		}
		if (errors is { Count: > 1 })
		{
			throw new AggregateException(errors);
		}
	}

	// Builds the real outpoint locator from the opened wallet's landed state. The
	// per-wallet persistence key and external network context are derived from the
	// authenticated master via the frozen replay/context branch, then the landed
	// load/save path opens the sealed state. A fresh wallet with no landed state
	// yields an empty map: the locator still refuses every outpoint fail-closed.
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

	// salt = SHA256(UTF8(networkGenesisDisplay) || UTF8(canonicalWalletId)). The
	// Client binding carries only the manifest identity, not the manifest genesis
	// block hash, so the pinned fallback uses UTF8(identity.NetworkManifestId) as
	// the networkGenesisDisplay bytes for this slice.
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

	private static string RegistryKey(LiquidWalletIdentity identity) => identity.CanonicalWalletId + "\0" + identity.NetworkManifestId;

	/// <summary>The manifest identity this provider's wallets are bound to (non-secret).</summary>
	internal string ManifestId => _manifestSource.ManifestId;

	// ILiquidWalletSendSessionSource: the command-service-facing session-resolution view.
	// The session already exposes the internal ILiquidWalletSendSession surface.
	string ILiquidWalletSendSessionSource.ManifestId => ManifestId;

	ILiquidWalletSendSession? ILiquidWalletSendSessionSource.TryGetOpenSession(string canonicalWalletId) =>
		TryGetOpenSession(canonicalWalletId);
}