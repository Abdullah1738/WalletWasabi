using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NBitcoin;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Liquid.Application;

public static class LiquidWalletReceiveIssuanceCommand
{
	internal static LiquidWalletRuntimeHandoff Execute(
		LiquidAuthenticatedRuntimeProvider provider,
		string canonicalWalletId,
		string expectedConfidentialAddress,
		CancellationToken cancellationToken,
		Func<byte[], byte[], LiquidWalletExternalIndexAllocation>? allocate = null,
		Func<LiquidAuthenticatedWalletSession, LiquidWalletRuntimeHandoff, bool>? publish = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(expectedConfidentialAddress);
		cancellationToken.ThrowIfCancellationRequested();
		using LiquidWalletOperationLease lease = provider.AcquireOperation(canonicalWalletId);
		LiquidAuthenticatedWalletSession session = lease.Session;
		LiquidWalletRefreshStateCapture captured = session.CaptureRefreshState();
		var material = captured.Owner.ReceiveMaterial;
		if (!StringComparer.Ordinal.Equals(expectedConfidentialAddress,
			LiquidWalletUiFacade.CreateReceiveAddress(session.Manifest,
				material.NextReceiveScriptPubKey, material.NextReceiveBlindingPublicKey).ConfidentialAddressText))
		{
			throw new InvalidOperationException("The Liquid receive address changed. Reopen Receive before issuing another address.");
		}

		byte[] masterBytes = session.AuthenticatedMaster.PrivateKey.ToBytes();
		byte[] slip77 = [];
		byte[] childBytes = [];
		byte[] salt = [];
		byte[] replayKey = [];
		byte[] context = [];
		bool persistenceEntered = false;
		try
		{
			slip77 = LiquidKeyDomain.DeriveHkdf(masterBytes, [], "WalletWasabi/Liquid/v1/slip77");
			ExtKey child = session.AuthenticatedMaster.Derive(new KeyPath(1108790945U | 0x80000000U));
			using (child.PrivateKey)
			{
				childBytes = child.PrivateKey.ToBytes();
			}
			salt = SHA256.HashData(Encoding.UTF8.GetBytes(session.Manifest.ManifestId + canonicalWalletId));
			replayKey = LiquidKeyDomain.DeriveHkdf(childBytes, salt, "WalletWasabi/Liquid/v1/replay");
			context = LiquidKeyDomain.DeriveHkdf(childBytes, salt, "WalletWasabi/Liquid/v1/context");
			var replacement = captured.Owner.CreateReceiveReplacement(session.AuthenticatedMaster, slip77);
			var handoff = new LiquidWalletRuntimeHandoff(canonicalWalletId, session.Manifest.ManifestId,
				replacement.Balances, replacement.SelectableOutputs, replacement.History, replacement.ReceiveMaterial);
			try
			{
				session.CommitReceiveSnapshot(captured, replacement, handoff, () =>
				{
					var loaded = LiquidWalletLoadSave.Load(session.WalletDataDirectory, canonicalWalletId, replayKey, context);
					if (loaded.Generation != captured.PersistenceGeneration || loaded.Revision != captured.StateRevision
						|| loaded.ExternalIndexHighWater != captured.ExternalIndexHighWater
						|| loaded.InternalIndexHighWater != captured.InternalIndexHighWater)
					{
						throw new InvalidOperationException("The Liquid wallet state changed before receive issuance.");
					}
					cancellationToken.ThrowIfCancellationRequested();
					// SafeFile can throw after replacing the file, without reporting its commit point.
					persistenceEntered = true;
					var saved = allocate is null
						? LiquidWalletExternalIndexAllocator.Allocate(session.WalletDataDirectory, canonicalWalletId, replayKey, context)
						: allocate(replayKey, context);
					if (saved.Index != captured.ExternalIndexHighWater || saved.StateRevision != replacement.StateRevision
						|| saved.PersistedGeneration != replacement.PersistenceGeneration
						|| saved.PersistedExternalIndexHighWater != replacement.ExternalIndexHighWater
						|| saved.PersistedInternalIndexHighWater != replacement.InternalIndexHighWater)
					{
						throw new InvalidOperationException("The Liquid receive allocation violated its exact fences.");
					}
				});
				cancellationToken.ThrowIfCancellationRequested();
				if (!(publish is null ? provider.TryPublishRefresh(session, handoff) : publish(session, handoff)))
				{
					throw new InvalidOperationException("The Liquid receive address was saved but its session changed.");
				}
			}
			catch (Exception exception) when (persistenceEntered)
			{
				throw new LiquidWalletReceiveIssuanceUncertainException(
					"The Liquid receive address may have been saved but could not be published. Reopen the wallet.", exception);
			}
			return handoff;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(context);
			CryptographicOperations.ZeroMemory(replayKey);
			CryptographicOperations.ZeroMemory(salt);
			CryptographicOperations.ZeroMemory(childBytes);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(masterBytes);
		}
	}

	public sealed class LiquidWalletReceiveIssuanceUncertainException : InvalidOperationException
	{
		public LiquidWalletReceiveIssuanceUncertainException(string message, Exception innerException)
			: base(message, innerException) { }
	}
}
