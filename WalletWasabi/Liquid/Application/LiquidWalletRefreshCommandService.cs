using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Liquid.WalletFacts.Wire;

namespace WalletWasabi.Liquid.Application;

internal sealed class LiquidWalletRefreshCommandService
{
	private const uint ReplayContextBranchIndex = 1108790945;
	private const string ReplayKeyInfo = "WalletWasabi/Liquid/v1/replay";
	private const string ContextKeyInfo = "WalletWasabi/Liquid/v1/context";
	private const string Slip77Info = "WalletWasabi/Liquid/v1/slip77";

	private readonly LiquidAuthenticatedRuntimeProvider _runtimeProvider;
	private readonly Dependencies _dependencies;
	private readonly object _fenceGate = new();
	private readonly HashSet<string> _activeWallets = new(StringComparer.Ordinal);

	private LiquidWalletRefreshCommandService(LiquidAuthenticatedRuntimeProvider runtimeProvider, Dependencies dependencies)
	{
		_runtimeProvider = runtimeProvider ?? throw new ArgumentNullException(nameof(runtimeProvider));
		_dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
	}

	internal static Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> CreateRefreshCommand(
		LiquidAuthenticatedRuntimeProvider runtimeProvider)
	{
		ArgumentNullException.ThrowIfNull(runtimeProvider);
		return new LiquidWalletRefreshCommandService(runtimeProvider, Dependencies.Production).ExecuteAsync;
	}

	internal static Func<LiquidWalletUiRefreshRequest, CancellationToken, Task<LiquidWalletUiRefreshResult>> CreateRefreshCommandForTesting(
		LiquidAuthenticatedRuntimeProvider runtimeProvider,
		Dependencies dependencies)
	{
		ArgumentNullException.ThrowIfNull(runtimeProvider);
		ArgumentNullException.ThrowIfNull(dependencies);
		return new LiquidWalletRefreshCommandService(runtimeProvider, dependencies).ExecuteAsync;
	}

	private async Task<LiquidWalletUiRefreshResult> ExecuteAsync(
		LiquidWalletUiRefreshRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		using LiquidWalletOperationLease operationLease = _runtimeProvider.AcquireOperation(request.CanonicalWalletId);
		lock (_fenceGate)
		{
			if (!_activeWallets.Add(request.CanonicalWalletId))
			{
				throw new InvalidOperationException("A Liquid wallet refresh is already active for this wallet.");
			}
		}

		try
		{
			LiquidAuthenticatedWalletSession session = operationLease.Session;
			LiquidWalletRefreshStateCapture captured = session.CaptureRefreshState();
			cancellationToken.ThrowIfCancellationRequested();
			if (request.Trigger == LiquidWalletUiRefreshTrigger.AcceptedSend
				&& !ContainsOrdinal(captured.AcceptedTransactionIds, request.AcceptedTransactionIdHex!))
			{
				throw new InvalidOperationException("The accepted transaction identifier was not present in the captured session record.");
			}

			using ElementsWalletRefreshObservation observation = await _dependencies.AcquireObservationAsync(
				session, captured, request.AcceptedTransactionIdHex, cancellationToken).ConfigureAwait(false);
			if (observation.Candidates.Count == 0)
			{
				if (request.Trigger == LiquidWalletUiRefreshTrigger.AcceptedSend)
				{
					throw new InvalidOperationException("The accepted transaction was unavailable from the fenced node observation.");
				}
				return NoCandidates(request, captured);
			}

			return CommitCandidateBatch(request, session, captured, observation, cancellationToken);
		}
		finally
		{
			lock (_fenceGate)
			{
				_activeWallets.Remove(request.CanonicalWalletId);
			}
		}
	}

	private LiquidWalletUiRefreshResult CommitCandidateBatch(
		LiquidWalletUiRefreshRequest request,
		LiquidAuthenticatedWalletSession session,
		LiquidWalletRefreshStateCapture captured,
		ElementsWalletRefreshObservation observation,
		CancellationToken cancellationToken)
	{
		byte[] sourceEpoch = new byte[32];
		byte[] descriptor = Array.Empty<byte>();
		byte[] masterPrivateKey = Array.Empty<byte>();
		byte[] slip77 = Array.Empty<byte>();
		byte[] replayChildMaterial = Array.Empty<byte>();
		byte[] saltInput = Array.Empty<byte>();
		byte[] salt = Array.Empty<byte>();
		byte[] replayKey = Array.Empty<byte>();
		byte[] context = Array.Empty<byte>();
		var rawCopies = new List<byte[]>();
		try
		{
			var intents = new LiquidWalletScanIntent[observation.Candidates.Count];
			var candidateById = new Dictionary<string, ElementsWalletRefreshCandidate>(StringComparer.Ordinal);
			foreach (ElementsWalletRefreshCandidate candidate in observation.Candidates)
			{
				candidateById.Add(candidate.TransactionId, candidate);
			}
			for (int index = 0; index < intents.Length; index++)
			{
				ElementsWalletRefreshCandidate candidate = observation.Candidates[index];
				intents[index] = LiquidWalletScanIntent.Create(
					LiquidTransactionId.ParseRpcHex(candidate.TransactionId), candidate.BlockHash);
			}
			LiquidWalletScanIntentDerivation derivation = LiquidWalletScanIntentDeriver.Derive(intents);
			var rawById = new Dictionary<string, ElementsWalletRefreshRawTransaction>(StringComparer.Ordinal);
			foreach (ElementsWalletRefreshRawTransaction raw in observation.RawTransactions)
			{
				rawById.Add(raw.TransactionId, raw);
			}
			var structural = new LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource[derivation.Intents.Count];
			var committedCandidateIds = new HashSet<string>(StringComparer.Ordinal);
			for (int index = 0; index < derivation.Intents.Count; index++)
			{
				LiquidWalletSyncBatchPlanner.FetchIntent intent = derivation.Intents[index];
				if (!candidateById.TryGetValue(intent.TransactionId, out ElementsWalletRefreshCandidate? candidate)
					|| !rawById.TryGetValue(intent.TransactionId, out ElementsWalletRefreshRawTransaction? candidateRaw))
				{
					throw new InvalidOperationException("The normalized refresh candidate has no complete RPC observation.");
				}
				byte[] candidateBytes = candidateRaw.GetTransactionBytes();
				rawCopies.Add(candidateBytes);
				var previous = new ReadOnlyMemory<byte>[candidate.PreviousTransactionIds.Count];
				for (int previousIndex = 0; previousIndex < previous.Length; previousIndex++)
				{
					string previousId = candidate.PreviousTransactionIds[previousIndex];
					if (!rawById.TryGetValue(previousId, out ElementsWalletRefreshRawTransaction? previousRaw))
					{
						throw new InvalidOperationException("A refresh candidate dependency is unavailable.");
					}
					byte[] previousBytes = previousRaw.GetTransactionBytes();
					rawCopies.Add(previousBytes);
					previous[previousIndex] = previousBytes;
				}
				structural[index] = new(candidateBytes, previous);
				committedCandidateIds.Add(intent.TransactionId);
			}
			_dependencies.StageObserver("derive/stage");

			RandomNumberGenerator.Fill(sourceEpoch);
			if (sourceEpoch.AsSpan().IndexOfAnyExcept((byte)0) < 0)
			{
				sourceEpoch[0] = 1;
			}
			descriptor = Encoding.UTF8.GetBytes(session.Descriptor);
			masterPrivateKey = session.AuthenticatedMaster.PrivateKey.ToBytes();
			slip77 = LiquidKeyDomain.DeriveHkdf(masterPrivateKey, [], Slip77Info);
			ExtKey replayChild = session.AuthenticatedMaster.Derive(new KeyPath(ReplayContextBranchIndex | 0x80000000U));
			replayChildMaterial = replayChild.PrivateKey.ToBytes();
			byte[] manifestId = Encoding.UTF8.GetBytes(session.Manifest.ManifestId);
			byte[] walletId = Encoding.UTF8.GetBytes(session.Identity.CanonicalWalletId);
			saltInput = new byte[manifestId.Length + walletId.Length];
			manifestId.CopyTo(saltInput, 0);
			walletId.CopyTo(saltInput, manifestId.Length);
			CryptographicOperations.ZeroMemory(manifestId);
			CryptographicOperations.ZeroMemory(walletId);
			salt = SHA256.HashData(saltInput);
			replayKey = LiquidKeyDomain.DeriveHkdf(replayChildMaterial, salt, ReplayKeyInfo);
			context = LiquidKeyDomain.DeriveHkdf(replayChildMaterial, salt, ContextKeyInfo);
			LiquidWalletFactsWireV1DescriptorNetworkClass networkClass = ReferenceEquals(session.Manifest, ElementsPublicNetworkManifest.LiquidMainnet)
				? LiquidWalletFactsWireV1DescriptorNetworkClass.Mainnet
				: ReferenceEquals(session.Manifest, ElementsPublicNetworkManifest.LiquidTestnet)
					? LiquidWalletFactsWireV1DescriptorNetworkClass.Test
					: throw new InvalidOperationException("The refresh manifest has no reviewed descriptor network class.");
			uint lastIndex = checked((uint)session.LastIndex);
			LiquidWalletObservationBatch nativeBatch = _dependencies.ObserveNative(
				new NativeObservationRequest(sourceEpoch, networkClass, lastIndex, descriptor, slip77, structural));

			// Recent-block discovery on public networks stages every non-coinbase
			// transaction; only the native observation can prove wallet relevance.
			// A Confirm row is emitted only for a candidate whose transaction the
			// native batch actually observed as relevant — at least one owned output,
			// or at least one input that spends a wallet unspent outpoint (mirroring
			// the sync session's skip rule: zero owned outputs AND zero wallet spends
			// means not this wallet's transaction). A candidate that is merely
			// confirmed (has a BlockHash) but unrelated to the wallet produces an
			// empty delta, is skipped by the sync session's commit, and must never
			// reach LiquidWalletState.Confirm, which rejects non-applied
			// transactions. This preserves confirmation of genuinely-owned
			// transactions while failing closed on unrelated testnet traffic.
			var relevantTransactionIds = new HashSet<LiquidTransactionId>();
			foreach (LiquidWalletTransactionObservation transaction in nativeBatch.GetTransactions())
			{
				bool spendsWalletOutpoint = false;
				if (transaction.OwnedOutputCount == 0)
				{
					foreach (LiquidOutPoint input in transaction.GetInputs())
					{
						if (captured.State.ContainsUnspent(input))
						{
							spendsWalletOutpoint = true;
							break;
						}
					}
				}
				if (transaction.OwnedOutputCount > 0 || spendsWalletOutpoint)
				{
					relevantTransactionIds.Add(
						LiquidTransactionId.ParseConsensusBytes(transaction.GetTransactionIdConsensusBytes()));
				}
			}
			var confirmations = new List<LiquidWalletSyncConfirmation>();
			foreach (LiquidWalletSyncBatchPlanner.FetchIntent intent in derivation.Intents)
			{
				ElementsWalletRefreshCandidate candidate = candidateById[intent.TransactionId];
				if (candidate.BlockHash is not null && candidate.BlockHeight is uint height)
				{
					LiquidTransactionId candidateTransactionId =
						LiquidTransactionId.ParseRpcHex(candidate.TransactionId);
					if (relevantTransactionIds.Contains(candidateTransactionId))
					{
						confirmations.Add(LiquidWalletSyncConfirmation.Create(
							LiquidWalletSyncConfirmationKind.Confirm,
							candidateTransactionId,
							LiquidConfirmation.Create(candidate.BlockHash, height)));
					}
				}
			}
			LiquidWalletSyncResult committed = LiquidWalletSyncSession.Open(
				captured.State, observation.NodeObservation, session.Manifest.PeggedAssetId).Commit(nativeBatch, confirmations);
			_dependencies.StageObserver("sync");

			ulong nextGeneration = checked(captured.PersistenceGeneration + 1);
			LiquidAuthenticatedWalletStateOwner replacement = captured.Owner.CreateReplacement(committed.State, nextGeneration);
			var handoff = new LiquidWalletRuntimeHandoff(
				session.Identity.CanonicalWalletId, session.Identity.NetworkManifestId,
				replacement.Balances, replacement.SelectableOutputs, replacement.History, replacement.ReceiveMaterial);
			_dependencies.StageObserver("project");
			if (!session.ValidateRefreshState(captured))
			{
				throw new InvalidOperationException("The Liquid wallet refresh state changed before persistence.");
			}
			_dependencies.StageObserver("validate");
			cancellationToken.ThrowIfCancellationRequested();
			LiquidWalletLoadSaveResult saved = _dependencies.Save(new SaveRequest(
				session.WalletDataDirectory, session.Identity.CanonicalWalletId, committed.State,
				nextGeneration, captured.PersistenceGeneration, captured.ExternalIndexHighWater,
				captured.InternalIndexHighWater, replayKey, context));
			if (saved.Revision != committed.ResultRevision
				|| saved.Generation != nextGeneration
				|| saved.ExternalIndexHighWater != captured.ExternalIndexHighWater
				|| saved.InternalIndexHighWater != captured.InternalIndexHighWater)
			{
				throw new InvalidOperationException("The Liquid wallet refresh save result violated its exact fences.");
			}
			bool installed = session.TryInstallRefreshSnapshot(captured.SnapshotReference, replacement, handoff);
			if (installed)
			{
				_dependencies.StageObserver("install");
			}
			bool published = installed && _dependencies.Publish(_runtimeProvider, session, handoff);
			session.RemoveCapturedAcceptedIds(captured, committedCandidateIds);
			_dependencies.StageObserver("remove");
			return new LiquidWalletUiRefreshResult(
				LiquidWalletUiRefreshStatus.Committed, request.CanonicalWalletId, request.Trigger,
				request.AcceptedTransactionIdHex, observation.Candidates.Count, committed.AppliedTransactionCount,
				saved.Revision, saved.Generation, request.Trigger == LiquidWalletUiRefreshTrigger.AcceptedSend, published);
		}
		finally
		{
			foreach (byte[] rawCopy in rawCopies)
			{
				CryptographicOperations.ZeroMemory(rawCopy);
			}
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(replayKey);
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(saltInput);
			CryptographicOperations.ZeroMemory(replayChildMaterial);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(masterPrivateKey);
			CryptographicOperations.ZeroMemory(descriptor);
			CryptographicOperations.ZeroMemory(sourceEpoch);
		}
	}

	private static LiquidWalletUiRefreshResult NoCandidates(LiquidWalletUiRefreshRequest request, LiquidWalletRefreshStateCapture captured) =>
		new(LiquidWalletUiRefreshStatus.NoCandidates, request.CanonicalWalletId, request.Trigger,
			request.AcceptedTransactionIdHex, 0, 0, captured.StateRevision, captured.PersistenceGeneration, false, false);

	private static bool ContainsOrdinal(IReadOnlyList<string> values, string value)
	{
		for (int index = 0; index < values.Count; index++)
		{
			if (StringComparer.Ordinal.Equals(values[index], value))
			{
				return true;
			}
		}
		return false;
	}

	internal sealed record NativeObservationRequest(
		byte[] SourceEpoch,
		LiquidWalletFactsWireV1DescriptorNetworkClass NetworkClass,
		uint LastIndex,
		byte[] Descriptor,
		byte[] Slip77,
		IReadOnlyList<LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource> Candidates);

	internal sealed record SaveRequest(
		string WalletDataDirectory,
		string WalletName,
		LiquidWalletState State,
		ulong NextGeneration,
		ulong BaseGeneration,
		ulong ExternalIndexHighWater,
		ulong InternalIndexHighWater,
		byte[] ReplayKey,
		byte[] Context);

	internal sealed class Dependencies
	{
		private Dependencies(
			Func<LiquidAuthenticatedWalletSession, LiquidWalletRefreshStateCapture, string?, CancellationToken, Task<ElementsWalletRefreshObservation>> acquireObservationAsync,
			Func<NativeObservationRequest, LiquidWalletObservationBatch> observeNative,
			Func<SaveRequest, LiquidWalletLoadSaveResult> save,
			Func<LiquidAuthenticatedRuntimeProvider, LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool> publish,
			Action<string> stageObserver)
		{
			AcquireObservationAsync = acquireObservationAsync;
			ObserveNative = observeNative;
			Save = save;
			Publish = publish;
			StageObserver = stageObserver;
		}

		internal static Dependencies Production { get; } = new(
			static (session, captured, suppliedId, cancellationToken) => session.RpcClient.GetWalletRefreshObservationAsync(
				captured.Owner.NodeExpectation, session.Manifest.RequiredFeeAssetId,
				captured.AcceptedTransactionIds, suppliedId, session.Manifest, cancellationToken, captured.MinConfirmedHeight),
			static request => LiquidWalletNativeFactsObserver.TryObserve(
				request.SourceEpoch, request.NetworkClass, request.LastIndex, request.Descriptor,
				request.Slip77, request.Candidates, out LiquidWalletObservationBatch? batch) && batch is not null
					? batch
					: throw new InvalidOperationException("Native Liquid wallet facts observation failed."),
			static request => LiquidWalletLoadSave.SaveWithExpectedGeneration(
				request.WalletDataDirectory, request.WalletName, request.State,
				request.NextGeneration, request.BaseGeneration, request.ReplayKey, request.Context),
			static (provider, session, handoff) => provider.TryPublishRefresh(session, handoff),
			static _ => { });

		internal Func<LiquidAuthenticatedWalletSession, LiquidWalletRefreshStateCapture, string?, CancellationToken, Task<ElementsWalletRefreshObservation>> AcquireObservationAsync { get; }
		internal Func<NativeObservationRequest, LiquidWalletObservationBatch> ObserveNative { get; }
		internal Func<SaveRequest, LiquidWalletLoadSaveResult> Save { get; }
		internal Func<LiquidAuthenticatedRuntimeProvider, LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool> Publish { get; }
		internal Action<string> StageObserver { get; }

		internal static Dependencies CreateForTesting(
			Func<LiquidAuthenticatedWalletSession, LiquidWalletRefreshStateCapture, string?, CancellationToken, Task<ElementsWalletRefreshObservation>> acquireObservationAsync,
			Func<NativeObservationRequest, LiquidWalletObservationBatch> observeNative,
			Func<SaveRequest, LiquidWalletLoadSaveResult> save,
			Func<LiquidAuthenticatedRuntimeProvider, LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool> publish,
			Action<string> stageObserver) =>
			new(acquireObservationAsync, observeNative, save, publish, stageObserver);
	}
}
