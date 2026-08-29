using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet.Sync;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 sections 5, 8, and 9; amendment section 7, option
/// (a)): the WalletWasabi-resident send-execution command service. The executor and scope are
/// <see langword="internal"/> to this assembly; the application composition root lives in
/// <c>WalletWasabi.Client</c> and may not name them, and no <c>InternalsVisibleTo</c> change
/// is permitted. This type is the frozen seam across that boundary: its only public surface
/// is the static <see cref="CreateSendCommand"/> factory, whose returned
/// <see cref="Func{T1,T2,TResult}"/> delegate body runs entirely inside this assembly — it
/// resolves the open authenticated session, opens one internal per-call scope, runs the
/// internal executor, and returns the public immutable result. The Client stores only the
/// returned delegate and never names the executor, the scope, the session, the RPC client,
/// or any secret-bearing type.
/// </summary>
public sealed class LiquidWalletSendExecutionCommandService
{
	private const uint ReplayContextBranchIndex = 1108790945;
	private const string ReplayKeyInfo = "WalletWasabi/Liquid/v1/replay";
	private const string ContextKeyInfo = "WalletWasabi/Liquid/v1/context";
	private const string Slip77Info = "WalletWasabi/Liquid/v1/slip77";

	private readonly LiquidAuthenticatedRuntimeProvider _runtimeProvider;
	private readonly ElementsPublicNetworkManifest _manifest;
	private readonly Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> _refreshCommand;
	private readonly Dictionary<string, byte> _activeExecutions = new(StringComparer.Ordinal);
	private readonly object _fenceGate = new();
	private readonly LiquidWalletSendExecutor _executor;
	private readonly Func<
		LiquidWalletUiSendExecutionRequest,
		ILiquidWalletSendExecutionScopeFactory,
		CancellationToken,
		Task<LiquidWalletUiSendExecutionResult>> _execute;

	private LiquidWalletSendExecutionCommandService(
		LiquidAuthenticatedRuntimeProvider runtimeProvider,
		ElementsPublicNetworkManifest manifest,
		Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>>? refreshCommand = null,
		Func<
			LiquidWalletUiSendExecutionRequest,
			ILiquidWalletSendExecutionScopeFactory,
			CancellationToken,
			Task<LiquidWalletUiSendExecutionResult>>? execute = null)
	{
		_runtimeProvider = runtimeProvider ?? throw new ArgumentNullException(nameof(runtimeProvider));
		_manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
		if (!StringComparer.Ordinal.Equals(runtimeProvider.ManifestId, manifest.ManifestId))
		{
			throw new ArgumentException(
				"The authenticated Liquid runtime provider must match the send manifest.",
				nameof(manifest));
		}
		// The accepted-send path must invoke the exact shared refresh delegate the facade exposes.
		// Default to the provider's own instance so production composition shares one reference.
		_refreshCommand = refreshCommand ?? runtimeProvider.RefreshCommand;
		_executor = new LiquidWalletSendExecutor(manifest);
		_execute = execute ?? _executor.ExecuteAsync;
	}

	/// <summary>
	/// Internal core composition path. The returned command acquires one provider operation lease
	/// before reading any session authority and releases it only after the executor has completed,
	/// including scope disposal and accepted-transaction recording.
	/// </summary>
	internal static Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>
		CreateSendCommand(LiquidAuthenticatedRuntimeProvider runtimeProvider)
	{
		ArgumentNullException.ThrowIfNull(runtimeProvider);
		ElementsPublicNetworkManifest manifest =
			ElementsPublicNetworkManifest.GetByManifestId(runtimeProvider.ManifestId);
		LiquidWalletSendExecutionCommandService service = new(runtimeProvider, manifest);
		return service.ExecuteAsync;
	}

	internal static Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<LiquidWalletUiSendExecutionResult>>
		CreateSendCommandForTesting(
			LiquidAuthenticatedRuntimeProvider runtimeProvider,
			Func<
				LiquidWalletUiSendExecutionRequest,
				ILiquidWalletSendExecutionScopeFactory,
				CancellationToken,
				Task<LiquidWalletUiSendExecutionResult>> execute,
			Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>>? refreshCommand = null)
	{
		ArgumentNullException.ThrowIfNull(runtimeProvider);
		ArgumentNullException.ThrowIfNull(execute);
		ElementsPublicNetworkManifest manifest =
			ElementsPublicNetworkManifest.GetByManifestId(runtimeProvider.ManifestId);
		LiquidWalletSendExecutionCommandService service = new(runtimeProvider, manifest, refreshCommand, execute);
		return service.ExecuteAsync;
	}

	/// <summary>
	/// Executes one ordinary Liquid send under the application-layer
	/// one-active-execution-per-wallet fence. A second invocation for the same wallet while
	/// one is nonterminal is refused before any call; the fence is an application-layer
	/// guarantee, with the node double-spend rejection as backstop only.
	/// </summary>
	private async Task<LiquidWalletUiSendExecutionResult> ExecuteAsync(
		LiquidWalletUiSendExecutionRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		using LiquidWalletOperationLease operationLease =
			_runtimeProvider.AcquireOperation(request.WalletName);

		lock (_fenceGate)
		{
			if (!_activeExecutions.TryAdd(request.WalletName, 0))
			{
				throw new InvalidOperationException(
					"A Liquid send execution is already active for this wallet.");
			}
		}

		try
		{
			ILiquidWalletSendExecutionScopeFactory scopeFactory = new ProviderScopeFactory(
				operationLease.Session,
				_manifest,
				_refreshCommand);
			return await _execute(request, scopeFactory, cancellationToken)
				.ConfigureAwait(false);
		}
		finally
		{
			lock (_fenceGate)
			{
				_activeExecutions.Remove(request.WalletName);
			}
		}
	}

	/// <summary>
	/// The per-call execution scope factory. Re-derives every secret-bearing value from the
	/// leased authenticated session's retained master at scope-open time — never re-reading a
	/// file, never asking the caller, never a service locator. All mutable byte arrays the scope
	/// builds are owned and zeroized on dispose; the call-scoped signer is the one the scope owns;
	/// the shared RPC client is never disposed by the scope.
	/// </summary>
	private sealed class ProviderScopeFactory : ILiquidWalletSendExecutionScopeFactory
	{
		private readonly LiquidAuthenticatedWalletSession _session;
		private readonly ElementsPublicNetworkManifest _manifest;
		private readonly Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> _refreshCommand;

		internal ProviderScopeFactory(
			LiquidAuthenticatedWalletSession session,
			ElementsPublicNetworkManifest manifest,
			Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> refreshCommand)
		{
			_session = session;
			_manifest = manifest;
			_refreshCommand = refreshCommand ?? throw new ArgumentNullException(nameof(refreshCommand));
		}

		public ILiquidWalletSendExecutionScope Open(string walletName)
		{
			ArgumentException.ThrowIfNullOrEmpty(walletName);

			LiquidAuthenticatedWalletSession session = _session;

			// Fail-closed ordinal guard: the leased session must be the session for the wallet the
			// request names. The session is the single source of truth for the wallet data
			// directory; the request carries no directory copy, so this guard binds the request to
			// the exact session before any directory/key material is read.
			if (!StringComparer.Ordinal.Equals(session.Identity.CanonicalWalletId, walletName))
			{
				throw new InvalidOperationException(
					"The authenticated Liquid wallet session does not match the named wallet.");
			}

			ExtKey master = session.AuthenticatedMaster;
			byte[] masterPrivateKeyBytes = master.PrivateKey.ToBytes();
			byte[] slip77;
			byte[] replayProtectionKey;
			byte[] externalWalletNetworkContext;
			try
			{
				slip77 = DeriveHkdf(masterPrivateKeyBytes, [], Slip77Info);

				ExtKey replayContextChild = master.Derive(new KeyPath(ReplayContextBranchIndex | 0x80000000U));
				byte[] keyMaterial = replayContextChild.PrivateKey.ToBytes();
				try
				{
											string networkManifestId = session.Identity.NetworkManifestId;
					byte[] salt = ComputePersistenceSalt(networkManifestId, walletName);
					replayProtectionKey = DeriveHkdf(keyMaterial, salt, ReplayKeyInfo);
					externalWalletNetworkContext = DeriveHkdf(keyMaterial, salt, ContextKeyInfo);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(keyMaterial);
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(masterPrivateKeyBytes);
			}

			byte[] sourceEpoch = RandomNumberGenerator.GetBytes(32);
			byte[] descriptorBytes = Encoding.UTF8.GetBytes(session.Descriptor);

			ILiquidWalletSigner keyOwner = session.SignerKeyAdapter;
			ulong lastIndex = session.LastIndex;
			ElementsRpcClient rpcClient = session.RpcClient;
			string walletDataDirectory = session.WalletDataDirectory;
			string canonicalWalletId = session.Identity.CanonicalWalletId;
			Action<string> recordAcceptedTxid = session.RecordAcceptedTransactionId;

			return new LiquidWalletSendExecutionScope(
				replayProtectionKey,
				externalWalletNetworkContext,
				sourceEpoch,
				keyOwner,
				descriptorBytes,
				lastIndex,
				slip77,
				rpcClient,
				_manifest.PeggedAssetId,
				walletDataDirectory,
				(request, ct) => AcquireFundingSourceAsync(rpcClient, _manifest.PeggedAssetId, _manifest, session.StateOwner.State, request, ct),
				(canonicalTransactionIdHex, ct) => ScheduleAcceptedRefreshAsync(recordAcceptedTxid, canonicalWalletId, canonicalTransactionIdHex, ct),
				ct => ScheduleManualRefreshAsync(canonicalWalletId, ct));
		}

		private async Task ScheduleAcceptedRefreshAsync(
			Action<string> recordAcceptedTransactionId,
			string canonicalWalletId,
			string canonicalTransactionIdHex,
			CancellationToken cancellationToken)
		{
			// Mandatory ordering (brief section 4): once node acceptance is proven, the accepted
			// canonical id is recorded BEFORE cancellation is consulted, so caller cancellation can
			// never erase knowledge of the accepted transaction. Then the exact shared refresh
			// delegate is invoked once with Trigger = AcceptedSend and awaited; never queued,
			// fire-and-forget, merged, or retried.
			LiquidTransactionId acceptedId = LiquidTransactionId.ParseRpcHex(canonicalTransactionIdHex);
			recordAcceptedTransactionId(acceptedId.CanonicalRpcHex);

			var request = new LiquidWalletUiRefreshRequest(
				canonicalWalletId,
				LiquidWalletUiRefreshTrigger.AcceptedSend,
				acceptedId.CanonicalRpcHex);
			_ = await _refreshCommand(request, cancellationToken).ConfigureAwait(false);
		}

		private async Task ScheduleManualRefreshAsync(
			string canonicalWalletId,
			CancellationToken cancellationToken)
		{
			// Submission ambiguity is not proven acceptance: invoke the same shared refresh delegate
			// as Manual with no accepted id so bounded node discovery may find the transaction. No
			// accepted id is recorded, ambiguity never becomes acceptance, and no second broadcast
			// is ever issued.
			var request = new LiquidWalletUiRefreshRequest(
				canonicalWalletId,
				LiquidWalletUiRefreshTrigger.Manual,
				acceptedTransactionIdHex: null);
			_ = await _refreshCommand(request, cancellationToken).ConfigureAwait(false);
		}

		private static async Task<ElementsExpectationBoundRawTransactionBatch> AcquireFundingSourceAsync(
			ElementsRpcClient rpcClient,
			string expectedEffectiveFeeAsset,
			ElementsPublicNetworkManifest manifest,
			WalletWasabi.Liquid.Wallet.LiquidWalletState walletState,
			LiquidWalletUiSendExecutionRequest request,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(walletState);

			// Build the raw-transaction requests: one per selected outpoint's candidate
			// transaction plus one per previous-transaction-id dependency row, deduplicated
			// and bounded by the RPC surface. A confirmed candidate carries its recorded
			// confirmation block hash so the landed funding-batch composition's exact
			// confirmation-binding check (request block hash == confirmation block hash) is
			// satisfied; an unconfirmed candidate and every previous-transaction dependency
			// keep a null block hash. The funding source is acquired under the session's RPC
			// client and effective fee asset before signing; the RPC client performs its own
			// pre-submit fee-asset and generation observation under the node-probe lock (no
			// fabricated expectation object).
			var candidateBlockHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
			foreach (string outPointHex in request.SelectedOutPointHexes)
			{
				byte[] consensusBytes = Convert.FromHexString(outPointHex);
				LiquidOutPoint outPoint = LiquidOutPoint.ParseSpendableConsensusBytes(consensusBytes);
				string candidateId = outPoint.TransactionId.CanonicalRpcHex;
				string? blockHash =
					walletState.TryGetConfirmation(outPoint.TransactionId, out LiquidConfirmation? confirmation)
						? confirmation?.CanonicalBlockHash
						: null;
				candidateBlockHashes[candidateId] = blockHash;
			}

			var transactionIds = new SortedSet<string>(StringComparer.Ordinal);
			foreach (string candidateId in candidateBlockHashes.Keys)
			{
				transactionIds.Add(candidateId);
			}
			foreach (IReadOnlyList<string>? row in request.PreviousTransactionIdsBySelectedInput)
			{
				if (row is null)
				{
					continue;
				}
				foreach (string previousId in row)
				{
					LiquidTransactionId parsed = LiquidTransactionId.ParseRpcHex(previousId);
					if (!parsed.IsZero)
					{
						transactionIds.Add(parsed.CanonicalRpcHex);
					}
				}
			}

			var requests = new List<ElementsRawTransactionRequest>(transactionIds.Count);
			foreach (string transactionId in transactionIds)
			{
				// Only a selected candidate carries a confirmation block hash; dependency
				// transactions always fetch with a null block hash.
				string? blockHash = candidateBlockHashes.TryGetValue(transactionId, out string? candidate)
					? candidate
					: null;
				requests.Add(new ElementsRawTransactionRequest(transactionId, blockHash));
			}

			return await rpcClient.GetObservedRawTransactionsAsync(
				expectedEffectiveFeeAsset,
				requests,
				manifest,
				cancellationToken).ConfigureAwait(false);
		}

		// HKDF-SHA256, 32-byte output, UTF-8 info. Local to this assembly: the Client's
		// LiquidKeyDomain is internal to the Client and cannot be named here without an
		// InternalsVisibleTo change, which V2 section 9 forbids. The derivation inputs
		// (branch, infos, salt) match the provider's BuildOutpointLocator exactly.
		private static byte[] DeriveHkdf(ReadOnlySpan<byte> keyMaterial, ReadOnlySpan<byte> salt, string info)
		{
			ArgumentNullException.ThrowIfNull(info);
			return HKDF.DeriveKey(
				HashAlgorithmName.SHA256,
				keyMaterial.ToArray(),
				32,
				salt.ToArray(),
				Encoding.UTF8.GetBytes(info));
		}

		// salt = SHA256(UTF8(networkGenesisDisplay) || UTF8(canonicalWalletId)), matching the
		// provider's pinned fallback: UTF8(identity.NetworkManifestId) as the
		// networkGenesisDisplay bytes for this slice.
		private static byte[] ComputePersistenceSalt(string networkManifestId, string canonicalWalletId)
		{
			byte[] networkGenesisDisplay = Encoding.UTF8.GetBytes(networkManifestId);
			byte[] canonicalWalletIdBytes = Encoding.UTF8.GetBytes(canonicalWalletId);
			byte[] saltInput = new byte[networkGenesisDisplay.Length + canonicalWalletIdBytes.Length];
			networkGenesisDisplay.CopyTo(saltInput, 0);
			canonicalWalletIdBytes.CopyTo(saltInput, networkGenesisDisplay.Length);
			return SHA256.HashData(saltInput);
		}
	}
}
