using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Liquid.Application;

/// <summary>
/// The fail-closed, generation-fenced command that persists a durable label set bound to one
/// receive (branch-0) derivation index. It captures the session's exact immutable snapshot,
/// validates the label set via <see cref="LiquidWalletLabelSet.Create"/>, applies it to the
/// captured state (removing the entry when the set is empty), projects a replacement owner that
/// rebinds the next-receive material's labels, and persists under the exact captured persistence
/// generation — a concurrent generation change rejects the write (no stale label persistence) and
/// the session snapshot is left untouched. Only after a committed save is the replacement owner
/// installed and published. This is the model half of the receive-label surface: it performs no
/// Fluent UI, no key derivation beyond the replay/context values the refresh path already derives,
/// no address generation, no send, and no RPC.
/// </summary>
internal sealed class LiquidWalletReceiveLabelCommandService
{
	private const uint ReplayContextBranchIndex = 1108790945;
	private const string ReplayKeyInfo = "WalletWasabi/Liquid/v1/replay";
	private const string ContextKeyInfo = "WalletWasabi/Liquid/v1/context";

	private readonly LiquidAuthenticatedRuntimeProvider _runtimeProvider;
	private readonly Dependencies _dependencies;

	private LiquidWalletReceiveLabelCommandService(LiquidAuthenticatedRuntimeProvider runtimeProvider, Dependencies dependencies)
	{
		_runtimeProvider = runtimeProvider ?? throw new ArgumentNullException(nameof(runtimeProvider));
		_dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
	}

	internal static Func<SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> CreateSetReceiveLabelsCommand(
		LiquidAuthenticatedRuntimeProvider runtimeProvider)
	{
		ArgumentNullException.ThrowIfNull(runtimeProvider);
		return new LiquidWalletReceiveLabelCommandService(runtimeProvider, Dependencies.Production).ExecuteAsync;
	}

	internal static Func<SetReceiveLabelsRequest, Task<LiquidAuthenticatedWalletStateOwner>> CreateSetReceiveLabelsCommandForTesting(
		LiquidAuthenticatedRuntimeProvider runtimeProvider,
		Dependencies dependencies)
	{
		ArgumentNullException.ThrowIfNull(runtimeProvider);
		ArgumentNullException.ThrowIfNull(dependencies);
		return new LiquidWalletReceiveLabelCommandService(runtimeProvider, dependencies).ExecuteAsync;
	}

	private Task<LiquidAuthenticatedWalletStateOwner> ExecuteAsync(SetReceiveLabelsRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		// Validate the label set before acquiring any session or persistence resource.
		LiquidWalletLabelSet labelSet = LiquidWalletLabelSet.Create(request.Labels);

		using LiquidWalletOperationLease operationLease = _runtimeProvider.AcquireOperation(request.CanonicalWalletId);
		LiquidAuthenticatedWalletSession session = operationLease.Session;
		object snapshotReference = session.CaptureRefreshSnapshot();
		LiquidAuthenticatedWalletStateOwner captured = session.StateOwner;

		// Apply the label set to the captured (never mutated) state. The label map is durable
		// metadata, not a transaction transition, so the revision is unchanged.
		LiquidWalletState labeledState = captured.State.SetReceiveLabels(request.Index, labelSet);
		ulong nextGeneration = checked(captured.PersistenceGeneration + 1);

		byte[] replayChildMaterial = Array.Empty<byte>();
		byte[] saltInput = Array.Empty<byte>();
		byte[] salt = Array.Empty<byte>();
		byte[] replayKey = Array.Empty<byte>();
		byte[] context = Array.Empty<byte>();
		try
		{
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

			LiquidAuthenticatedWalletStateOwner replacement = captured.CreateReplacement(labeledState, nextGeneration);
			LiquidWalletReceiveLabelAllocation saved = _dependencies.Save(new SaveRequest(
				session.WalletDataDirectory,
				session.Identity.CanonicalWalletId,
				labeledState,
				nextGeneration,
				captured.PersistenceGeneration,
				request.Index,
				labelSet,
				replayKey,
				context));
			LiquidWalletRuntimeHandoff handoff = new(
				session.Identity.CanonicalWalletId,
				session.Identity.NetworkManifestId,
				replacement.Balances,
				replacement.SelectableOutputs,
				replacement.History,
				replacement.ReceiveMaterial);
			bool installed = session.TryInstallRefreshSnapshot(snapshotReference, replacement, handoff);
			if (installed)
			{
				_dependencies.Publish(_runtimeProvider, session, handoff);
			}

			return Task.FromResult(replacement);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(replayKey);
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(saltInput);
			CryptographicOperations.ZeroMemory(replayChildMaterial);
		}
	}

	internal sealed record SetReceiveLabelsRequest(
		string CanonicalWalletId,
		uint Index,
		IReadOnlyList<string> Labels);

	internal sealed record SaveRequest(
		string WalletDataDirectory,
		string WalletName,
		LiquidWalletState State,
		ulong NextGeneration,
		ulong BaseGeneration,
		uint Index,
		LiquidWalletLabelSet Labels,
		byte[] ReplayKey,
		byte[] Context);

	internal sealed class Dependencies
	{
		private Dependencies(
			Func<SaveRequest, LiquidWalletReceiveLabelAllocation> save,
			Func<LiquidAuthenticatedRuntimeProvider, LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool> publish)
		{
			Save = save;
			Publish = publish;
		}

		internal static Dependencies Production { get; } = new(
			static request => LiquidWalletReceiveLabelAllocator.SetLabels(
				request.WalletDataDirectory,
				request.WalletName,
				request.ReplayKey,
				request.Context,
				request.Index,
				request.Labels.GetLabels()),
			static (provider, session, handoff) => provider.TryPublishRefresh(session, handoff));

		internal Func<SaveRequest, LiquidWalletReceiveLabelAllocation> Save { get; }
		internal Func<LiquidAuthenticatedRuntimeProvider, LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool> Publish { get; }

		internal static Dependencies CreateForTesting(
			Func<SaveRequest, LiquidWalletReceiveLabelAllocation> save,
			Func<LiquidAuthenticatedRuntimeProvider, LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool> publish) =>
			new(save, publish);
	}
}
