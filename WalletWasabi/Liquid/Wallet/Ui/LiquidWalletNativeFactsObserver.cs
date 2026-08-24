using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.WalletFacts.Wire;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// NATIVE-WALLET-FACTS-OBSERVATION-FFI-001: the production managed binding to
/// <c>wln_wallet_facts_observe_v1</c>. Its declaration exactly matches
/// <c>crates/wallet-facts-ffi/include/wasabi_liquid_wallet_facts_v1.h</c> at pinned native commit
/// <c>bd50133a9fbcac5d187757e634c1cc2fc65a10ac</c>: the nine parameters are a borrowed canonical
/// WLFQ v1 request pointer and length, borrowed 32-byte expected source epoch, borrowed 32-byte
/// SLIP-77 master key, caller-owned response pointer and capacity, caller-owned response-length
/// receiver, and borrowed caller-supplied entropy pointer and length. Frozen statuses are
/// <c>0</c> OK; <c>-1</c> invalid argument; <c>-2</c> version mismatch; <c>-3</c> invalid encoding;
/// <c>-4</c> limit exceeded; <c>-5</c> descriptor rejected; <c>-6</c> candidate rejected;
/// <c>-7</c> observation rejected; <c>-8</c> source-binding mismatch; <c>-9</c> internal error; and
/// <c>-10</c> output capacity. The null/zero capacity query must return exactly OUTPUT_CAPACITY
/// and a length in <c>64..=80_599_492</c>; the exact-size write must return exactly OK and the
/// identical length. Any status or length drift is an internal contract failure. A distinct fresh
/// 32-byte CSPRNG entropy seed is generated immediately before each of the two calls.
///
/// This binding performs no node contact, no RPC, no wallet loading, no persistence, no signing,
/// finalization, or broadcast, and no key generation, storage, or custody. It makes no chain,
/// currentness, confirmation, UTXO, balance-credit, or other authority claim. It performs no
/// fallback parsing or unblinding and probes no alternate symbol: native OK is decoded only by the
/// landed WLFV v1 structural decoder, whose rejection is an internal contract failure. No native
/// bytes, pointers, key material, or SLIP-77 material are exposed to Fluent UI.
/// </summary>
internal static unsafe class LiquidWalletNativeFactsObserver
{
	/// <summary>The frozen wallet-facts FFI ABI version.</summary>
	internal const uint AbiVersionV1 = 1;

	/// <summary>Successful observation.</summary>
	internal const int StatusOkV1 = 0;

	/// <summary>The output capacity is insufficient; the exact required length is reported.</summary>
	internal const int StatusOutputCapacityV1 = -10;

	/// <summary>The exact borrowed source-epoch length.</summary>
	internal const int SourceEpochLength = 32;

	/// <summary>The exact borrowed SLIP-77 master-key length.</summary>
	internal const int Slip77MasterKeyLength = 32;

	/// <summary>The exact caller-supplied CSPRNG entropy-seed length.</summary>
	internal const int EntropyLength = 32;

	/// <summary>The maximum response length reachable through the frozen WLFV v1 limits.</summary>
	internal const ulong MaxResponseBytesV1 = 80_599_492;

	/// <summary>The frozen maximum outer WLFQ v1 request-frame length.</summary>
	internal const int MaxRequestBytesV1 = 268_435_456;

	/// <summary>The full native commit from which the pinned cdylib was built.</summary>
	internal const string PinnedNativeCommit = "bd50133a9fbcac5d187757e634c1cc2fc65a10ac";

	/// <summary>The SHA-256 of the pinned-commit macOS arm64 cdylib.</summary>
	internal const string MacOsLibrarySha256 = "cda2d5e58970ed485ca442678168eb640a2e9588dfca96166715adf8111ee069";

	/// <summary>
	/// Linux is intentionally untracked in this lane: unlike the signer slice, no independently
	/// built and reviewed Linux artifact was supplied. The empty pin is never accepted; platform
	/// selection fails closed before path resolution or loading. Retaining the per-platform helper
	/// shape makes adding a future reviewed <c>.so</c> a pin-and-project-file-only extension rather
	/// than an alternate loading path.
	/// </summary>
	internal const string LinuxLibrarySha256 = "";

	internal const string ArtifactSubdirectory = "NativeWalletFacts";

	internal static string LibraryFileName =>
		OperatingSystem.IsLinux() ? "libwasabi_liquid_wallet_facts_v1.so" :
		"libwasabi_liquid_wallet_facts_v1.dylib";

	internal static string ResolveLibraryPath() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ArtifactSubdirectory, LibraryFileName));

	/// <summary>Verifies the exact regular-file artifact and platform pin before native loading.</summary>
	internal static void EnsurePinnedNativeArtifact()
	{
		string expectedSha256 = PlatformLibraryPin.ExpectedSha256;
		string libraryPath = ResolveLibraryPath();
		if (!File.Exists(libraryPath))
		{
			throw new PlatformNotSupportedException($"The pinned wallet facts native cdylib is missing: {libraryPath}");
		}
		if ((File.GetAttributes(libraryPath) & FileAttributes.ReparsePoint) != 0)
		{
			throw new PlatformNotSupportedException($"The pinned wallet facts native cdylib reparse point is forbidden: {libraryPath}");
		}

		string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(libraryPath)));
		if (!StringComparer.Ordinal.Equals(actualSha256, expectedSha256))
		{
			throw new PlatformNotSupportedException("The pinned wallet facts native cdylib hash does not match the production pin.");
		}
	}

	internal static LiquidWalletObservationBatch Observe(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidWalletFactsWireV1DescriptorNetworkClass networkClass,
		uint lastDerivationIndex,
		ReadOnlySpan<byte> descriptorAscii,
		IReadOnlyList<LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource> candidates,
		ReadOnlySpan<byte> slip77MasterKey)
	{
		if (sourceEpoch.Length != SourceEpochLength)
		{
			throw new ArgumentException("The source epoch must be exactly 32 bytes.", nameof(sourceEpoch));
		}
		if (slip77MasterKey.Length != Slip77MasterKeyLength)
		{
			throw new ArgumentException("The SLIP-77 master key must be exactly 32 bytes.", nameof(slip77MasterKey));
		}
		if (!LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
			sourceEpoch,
			networkClass,
			lastDerivationIndex,
			descriptorAscii,
			candidates,
			out LiquidWalletFactsWireV1UnpreparedRequestFrame? frame,
			out LiquidWalletFactsWireErrorCode error))
		{
			throw new ArgumentException($"The WLFQ request was rejected: {error}.", nameof(candidates));
		}

		using (frame)
		{
			byte[] request = new byte[frame!.Length];
			try
			{
				frame.CopyFrameTo(request);
				return ObservePreparedFrame(request, sourceEpoch, slip77MasterKey);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(request);
			}
		}
	}

	/// <summary>
	/// Observes one already-built canonical WLFQ frame. Kept internal so tests can prove that an
	/// expected epoch different from the frame epoch reaches native status -8 and fails closed.
	/// </summary>
	internal static LiquidWalletObservationBatch ObservePreparedFrame(
		ReadOnlySpan<byte> requestFrame,
		ReadOnlySpan<byte> expectedSourceEpoch,
		ReadOnlySpan<byte> slip77MasterKey)
	{
		if (requestFrame.IsEmpty || requestFrame.Length > MaxRequestBytesV1)
		{
			throw new ArgumentException(
				"The WLFQ request frame length must be in 1..=268435456.",
				nameof(requestFrame));
		}
		if (expectedSourceEpoch.Length != SourceEpochLength)
		{
			throw new ArgumentException("The expected source epoch must be exactly 32 bytes.", nameof(expectedSourceEpoch));
		}
		if (slip77MasterKey.Length != Slip77MasterKeyLength)
		{
			throw new ArgumentException("The SLIP-77 master key must be exactly 32 bytes.", nameof(slip77MasterKey));
		}

		byte[]? request = null;
		byte[]? epoch = null;
		byte[]? key = null;
		byte[]? queryEntropy = null;
		byte[]? writeEntropy = null;
		try
		{
			request = requestFrame.ToArray();
			epoch = expectedSourceEpoch.ToArray();
			key = slip77MasterKey.ToArray();
			queryEntropy = new byte[EntropyLength];
			writeEntropy = new byte[EntropyLength];
			RandomNumberGenerator.Fill(queryEntropy);
			ulong required = Call(request, epoch, key, null, 0, queryEntropy, capacityQuery: true);
			if (required < 64 || required > MaxResponseBytesV1)
			{
				throw new InvalidOperationException("Native wallet facts capacity query returned a length outside 64..=80599492.");
			}

			byte[] responseBytes = new byte[checked((int)required)];
			try
			{
				RandomNumberGenerator.Fill(writeEntropy);
				ulong actual = Call(request, epoch, key, responseBytes, required, writeEntropy, capacityQuery: false);
				if (actual != required)
				{
					throw new InvalidOperationException("Native wallet facts response length drifted between capacity query and write.");
				}
				if (!LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
					responseBytes,
					epoch,
					out LiquidWalletFactsWireV1UntrustedStructuralResponse? response,
					out LiquidWalletFactsWireErrorCode decodeError) || response is null)
				{
					throw new InvalidOperationException($"Native OK response was rejected by the managed WLFV v1 decoder: {decodeError}.");
				}

				using (response)
				{
					return Project(response);
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(responseBytes);
			}
		}
		finally
		{
			if (queryEntropy is not null)
			{
				CryptographicOperations.ZeroMemory(queryEntropy);
			}
			if (writeEntropy is not null)
			{
				CryptographicOperations.ZeroMemory(writeEntropy);
			}
			if (request is not null)
			{
				CryptographicOperations.ZeroMemory(request);
			}
			if (epoch is not null)
			{
				CryptographicOperations.ZeroMemory(epoch);
			}
			if (key is not null)
			{
				CryptographicOperations.ZeroMemory(key);
			}
		}
	}

	private static ulong Call(
		byte[] request,
		byte[] epoch,
		byte[] key,
		byte[]? output,
		ulong capacity,
		byte[] entropy,
		bool capacityQuery)
	{
		fixed (byte* requestPointer = request)
		fixed (byte* epochPointer = epoch)
		fixed (byte* keyPointer = key)
		fixed (byte* entropyPointer = entropy)
		fixed (byte* outputPointer = output)
		{
			ulong length = 0;
			int status = ((delegate* unmanaged[Cdecl]<byte*, ulong, byte*, byte*, byte*, ulong, ulong*, byte*, ulong, int>)NativeEntryPointAddress.Value)(
				requestPointer,
				(ulong)request.Length,
				epochPointer,
				keyPointer,
				outputPointer,
				capacity,
				&length,
				entropyPointer,
				(ulong)entropy.Length);
			int requiredStatus = capacityQuery ? StatusOutputCapacityV1 : StatusOkV1;
			if (status != requiredStatus)
			{
				throw new InvalidOperationException($"Native wallet facts call failed closed with status {status}; expected {requiredStatus}.");
			}
			return length;
		}
	}

	private static LiquidWalletObservationBatch Project(LiquidWalletFactsWireV1UntrustedStructuralResponse response)
	{
		var transactions = new List<LiquidWalletTransactionObservation>(response.TransactionCount);
		for (int transactionIndex = 0; transactionIndex < response.TransactionCount; transactionIndex++)
		{
			LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralTransactionView view = response.GetTransaction(transactionIndex);
			var inputs = new List<LiquidOutPoint>(view.InputCount);
			var outputs = new List<LiquidOwnedOutputObservation>(view.OwnedOutputCount);
			for (int inputIndex = 0; inputIndex < view.InputCount; inputIndex++)
			{
				LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralInputView input = view.GetInput(inputIndex);
				inputs.Add(LiquidOutPoint.CreateSpendable(
					LiquidTransactionId.ParseConsensusBytes(input.GetPreviousTransactionIdConsensusBytes()),
					input.PreviousOutputIndex));
			}
			for (int outputIndex = 0; outputIndex < view.OwnedOutputCount; outputIndex++)
			{
				LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView output = view.GetOwnedOutput(outputIndex);
				outputs.Add(LiquidOwnedOutputObservation.Create(
					view.GetTransactionIdConsensusBytes(),
					output.OutputIndex,
					view.GetTransactionWitnessBinding(),
					output.GetScriptPubKey(),
					output.GetSpendPublicKey(),
					output.GetBlindingPublicKey(),
					output.Branch == LiquidWalletFactsWireV1Branch.External ? LiquidKeyBranch.External : LiquidKeyBranch.Internal,
					output.DerivationIndex,
					output.GetAssetIdConsensusBytes(),
					output.Value));
			}
			transactions.Add(LiquidWalletTransactionObservation.Create(
				view.GetTransactionIdConsensusBytes(),
				view.GetTransactionWitnessBinding(),
				inputs,
				outputs));
		}
		return LiquidWalletObservationBatch.Create(transactions);
	}

	private static readonly Lazy<IntPtr> NativeLibraryHandle = new(LoadNativeLibraryHandle);
	private static readonly Lazy<IntPtr> NativeEntryPointAddress = new(LoadEntryPointAddress);

	private static IntPtr LoadNativeLibraryHandle()
	{
		EnsurePinnedNativeArtifact();
		return NativeLibrary.Load(ResolveLibraryPath());
	}

	private static IntPtr LoadEntryPointAddress() =>
		NativeLibrary.GetExport(NativeLibraryHandle.Value, "wln_wallet_facts_observe_v1");

	private static class PlatformLibraryPin
	{
		internal static readonly string ExpectedSha256 = Select();

		private static string Select()
		{
			if (OperatingSystem.IsMacOS())
			{
				return MacOsLibrarySha256;
			}
			if (OperatingSystem.IsLinux())
			{
				throw new PlatformNotSupportedException(
					"The wallet facts Linux artifact is intentionally untracked in this lane and fails closed.");
			}
			throw new PlatformNotSupportedException(
				"The pinned wallet facts native cdylib is tracked for macOS only.");
		}
	}
}
