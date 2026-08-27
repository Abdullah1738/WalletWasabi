using System.Collections.Generic;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.WalletFacts.Wire;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-FACTS-FFI-001: the production native wallet-facts observation seam. One
/// static operation composes the landed managed WLFQ encoder
/// (<see cref="LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame"/>), the
/// two-call native observe through <see cref="LiquidWalletNativeFactsBinding"/> with a fresh
/// <see cref="RandomNumberGenerator"/> fill per call, the landed managed WLFV untrusted decoder
/// (<see cref="LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse"/>),
/// and the frozen domain projection into a <see cref="LiquidWalletObservationBatch"/>. The
/// observer is stateless: it owns no session, retains no key material, and disposes nothing of
/// its own; the borrowed SLIP-77 master is caller-owned blinding material (never a spend key) and
/// is NOT zeroed here — its storage is governed by the caller. Every staging buffer the operation
/// allocates (request frame copy, entropy, response staging) is zeroized in
/// <see langword="finally"/>. Fail-closed everywhere: any encoder/native/decoder/domain rejection
/// returns <see langword="false"/> with a <see langword="null"/> batch — no partial result, no
/// retry beyond the single capacity re-call, no caching, no fallback parser, no dynamic symbol
/// probing. This observer performs no node contact, no RPC, no broadcast, no signing, and no
/// key custody; the produced batch is source-only facts with no chain, unspentness,
/// confirmation, balance-credit, wallet-state, or persistence authority.
/// </summary>
internal static class LiquidWalletNativeFactsObserver
{
	/// <summary>
	/// The internal-only test hook that pins the two entropy seeds (query then write) so the
	/// seed-pinned ground-truth row is byte-exact. The property must return exactly 32 bytes per
	/// invocation and is invoked once per native call. The returned arrays are zeroed after use.
	/// Production leaves this <see langword="null"/> and each call is a fresh
	/// <see cref="RandomNumberGenerator.Fill(Span{byte})"/>.
	/// </summary>
	internal static Func<byte[]>? EntropyOverrideForTesting { get; set; }

	/// <summary>
	/// Encodes one canonical WLFQ v1 request for the supplied candidates, observes it through the
	/// pinned native boundary, decodes the canonical WLFV v1 response against the supplied source
	/// epoch, and projects the decoded views into a <see cref="LiquidWalletObservationBatch"/>.
	/// Returns <see langword="false"/> with a <see langword="null"/> batch on any rejection.
	/// </summary>
	internal static bool TryObserve(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidWalletFactsWireV1DescriptorNetworkClass networkClass,
		uint lastDerivationIndex,
		ReadOnlySpan<byte> descriptorAscii,
		ReadOnlySpan<byte> slip77MasterKey,
		IReadOnlyList<LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource> candidates,
		out LiquidWalletObservationBatch? batch)
	{
		batch = null;

		// Guard argument shapes before any native call: exactly-32 nonzero epoch, exactly-32
		// SLIP-77 master, non-empty descriptor, non-null candidates.
		if (sourceEpoch.Length != LiquidWalletNativeFactsBinding.SourceEpochLength ||
			!ContainsNonzero(sourceEpoch) ||
			slip77MasterKey.Length != LiquidWalletNativeFactsBinding.Slip77MasterKeyLength ||
			descriptorAscii.IsEmpty ||
			candidates is null)
		{
			return false;
		}

		// frame and response are declared null and assigned from the factory out parameters so the
		// dispose analyzer tracks them; each is disposed unconditionally in a finally on every path
		// (the packet's "disposed on every path").
		LiquidWalletFactsWireV1UnpreparedRequestFrame? frame = null;
		LiquidWalletFactsWireV1UntrustedStructuralResponse? response = null;
		byte[]? staging = null;
		byte[]? responseStaging = null;
		byte[] entropy = new byte[LiquidWalletNativeFactsBinding.EntropyLength];
		try
		{
			if (!LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
				sourceEpoch,
				networkClass,
				lastDerivationIndex,
				descriptorAscii,
				candidates,
				out frame,
				out _) ||
				frame is null)
			{
				return false;
			}

			staging = new byte[frame.Length];
			frame.CopyFrameTo(staging);

			// Two-call capacity protocol, frozen: the query publishes the required length,
			// the write call reallocates exactly that length with a second fresh seed. A
			// second capacity status, any other status, or a divergent length is fail-closed;
			// there is no retry loop.
			if (!FillEntropy(entropy))
			{
				return false;
			}
			int status = LiquidWalletNativeFactsBinding.Observe(
				staging,
				sourceEpoch,
				slip77MasterKey,
				Span<byte>.Empty,
				out ulong requiredLength,
				entropy);
			if (status != LiquidWalletNativeFactsBinding.StatusOutputCapacityV1 ||
				requiredLength < (ulong)LiquidWalletNativeFactsBinding.MinimumResponseFrameBytes ||
				requiredLength > LiquidWalletNativeFactsBinding.MaxReachableResponseBytesV1)
			{
				return false;
			}

			responseStaging = new byte[(int)requiredLength];
			if (!FillEntropy(entropy))
			{
				return false;
			}
			status = LiquidWalletNativeFactsBinding.Observe(
				staging,
				sourceEpoch,
				slip77MasterKey,
				responseStaging,
				out ulong writtenLength,
				entropy);
			if (status != LiquidWalletNativeFactsBinding.StatusOkV1 || writtenLength != requiredLength)
			{
				return false;
			}

			// The decoder takes its own owned copy of the response frame; the staging buffer is
			// zeroed by the finally below. A native OK followed by a decoder rejection is an
			// internal contract failure: fail closed, never a fallback path.
			if (!LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				responseStaging,
				sourceEpoch,
				out response,
				out _) ||
				response is null)
			{
				return false;
			}

			return TryProject(response, out batch);
		}
		catch (Exception exception) when (exception is PlatformNotSupportedException or ArgumentException or InvalidOperationException)
		{
			// Fail-closed: the pinned-artifact guard, a domain-constructor rejection, or an
			// argument-shape violation surfaces as false with no partial batch.
			return false;
		}
		finally
		{
			frame?.Dispose();
			response?.Dispose();
			if (staging is not null)
			{
				CryptographicOperations.ZeroMemory(staging);
			}
			if (responseStaging is not null)
			{
				CryptographicOperations.ZeroMemory(responseStaging);
			}
			CryptographicOperations.ZeroMemory(entropy);
		}
	}

	/// <summary>
	/// The frozen WLFV → domain projection: each decoded transaction view becomes one
	/// <see cref="LiquidWalletTransactionObservation"/> (consensus-order id, bound witness
	/// binding, spendable outpoint inputs, owned outputs ordered as decoded) and the ordered set
	/// assembles into the batch. Any domain-constructor rejection propagates as fail-closed.
	/// </summary>
	private static bool TryProject(
		LiquidWalletFactsWireV1UntrustedStructuralResponse response,
		out LiquidWalletObservationBatch? batch)
	{
		batch = null;
		var transactions = new LiquidWalletTransactionObservation[response.TransactionCount];
		for (int transactionIndex = 0; transactionIndex < transactions.Length; transactionIndex++)
		{
			LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralTransactionView
				transaction = response.GetTransaction(transactionIndex);

			var inputs = new WalletWasabi.Liquid.Transactions.LiquidOutPoint[transaction.InputCount];
			for (int inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
			{
				LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralInputView
					input = transaction.GetInput(inputIndex);
				inputs[inputIndex] = WalletWasabi.Liquid.Transactions.LiquidOutPoint.CreateSpendable(
					WalletWasabi.Liquid.Transactions.LiquidTransactionId.ParseConsensusBytes(
						input.GetPreviousTransactionIdConsensusBytes(), "previousTransactionId"),
					input.PreviousOutputIndex);
			}

			byte[] transactionIdConsensusBytes = transaction.GetTransactionIdConsensusBytes();
			byte[] transactionWitnessBinding = transaction.GetTransactionWitnessBinding();
			var ownedOutputs = new LiquidOwnedOutputObservation[transaction.OwnedOutputCount];
			for (int outputIndex = 0; outputIndex < ownedOutputs.Length; outputIndex++)
			{
				LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView
					output = transaction.GetOwnedOutput(outputIndex);
				ownedOutputs[outputIndex] = LiquidOwnedOutputObservation.Create(
					transactionIdConsensusBytes,
					output.OutputIndex,
					transactionWitnessBinding,
					output.GetScriptPubKey(),
					output.GetSpendPublicKey(),
					output.GetBlindingPublicKey(),
					(LiquidKeyBranch)output.Branch,
					output.DerivationIndex,
					output.GetAssetIdConsensusBytes(),
					output.Value);
			}

			transactions[transactionIndex] = LiquidWalletTransactionObservation.Create(
				transactionIdConsensusBytes,
				transactionWitnessBinding,
				inputs,
				ownedOutputs);
		}

		batch = LiquidWalletObservationBatch.Create(transactions);
		return true;
	}

	/// <summary>
	/// Fills the 32-byte entropy buffer: a fresh <see cref="RandomNumberGenerator"/> fill in
	/// production, or the test-only override when one is set. The override must return exactly
	/// 32 bytes; the returned array is zeroed after use. Any other length is fail-closed.
	/// </summary>
	private static bool FillEntropy(byte[] entropy)
	{
		Func<byte[]>? entropyOverride = EntropyOverrideForTesting;
		if (entropyOverride is null)
		{
			RandomNumberGenerator.Fill(entropy);
			return true;
		}

		byte[] supplied = entropyOverride();
		try
		{
			if (supplied.Length != entropy.Length)
			{
				return false;
			}
			supplied.CopyTo(entropy, 0);
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(supplied);
		}
	}

	private static bool ContainsNonzero(ReadOnlySpan<byte> value) => value.ContainsAnyExcept((byte)0);
}
