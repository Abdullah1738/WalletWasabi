using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.WalletFacts.Wire;

namespace WalletWasabi.Liquid.WalletFacts;

/// <summary>
/// The pinned managed binding over <c>wln_wallet_facts_observe_v1</c>. This
/// adapter performs no transaction parsing or confidential-output opening;
/// native returns the canonical WLFV frame and the landed structural decoder
/// is the only managed decoding path.
/// </summary>
internal static unsafe partial class LiquidWalletNativeFactsObserver
{
	internal const uint AbiVersionV1 = 1;
	internal const ulong MaxFrameBytesV1 = 268_435_456;
	internal const ulong MaxReachableResponseBytesV1 = 80_599_492;
	internal const int StatusOkV1 = 0;
	internal const int StatusInvalidArgumentV1 = -1;
	internal const int StatusVersionMismatchV1 = -2;
	internal const int StatusInvalidEncodingV1 = -3;
	internal const int StatusLimitExceededV1 = -4;
	internal const int StatusDescriptorRejectedV1 = -5;
	internal const int StatusCandidateRejectedV1 = -6;
	internal const int StatusObservationRejectedV1 = -7;
	internal const int StatusSourceBindingMismatchV1 = -8;
	internal const int StatusInternalErrorV1 = -9;
	internal const int StatusOutputCapacityV1 = -10;
	internal const int SourceEpochLength = 32;
	internal const int Slip77MasterKeyLength = 32;
	internal const int EntropyLength = 32;
	internal const string PinnedNativeCommit = "bd50133a9fbcac5d187757e634c1cc2fc65a10ac";
	internal const string MacOsLibrarySha256 = "60de8e090e797e5ff3d093914dacecb90f919193a1a2c05ff40a6343fcf2a29a";
	internal const string LinuxLibrarySha256 = "";
	internal const string ArtifactSubdirectory = "NativeWalletFacts";

	internal static string LibraryFileName => OperatingSystem.IsLinux()
		? "libwasabi_liquid_wallet_facts_v1.so"
		: "libwasabi_liquid_wallet_facts_v1.dylib";

	internal static string ResolveLibraryPath() => Path.GetFullPath(Path.Combine(
		AppContext.BaseDirectory, ArtifactSubdirectory, LibraryFileName));

	internal static void EnsurePinnedNativeArtifact()
	{
		string path = ResolveLibraryPath();
		if (!File.Exists(path))
		{
			throw new PlatformNotSupportedException($"The pinned native wallet-facts cdylib is missing: {path}");
		}
		if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
		{
			throw new PlatformNotSupportedException($"The pinned native wallet-facts cdylib reparse point is forbidden: {path}");
		}
		string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
		if (!StringComparer.Ordinal.Equals(actual, PlatformLibraryPin.ExpectedSha256))
		{
			throw new PlatformNotSupportedException("The pinned native wallet-facts cdylib hash does not match the production pin.");
		}
	}

	internal static LiquidWalletObservationBatch Observe(
		ReadOnlySpan<byte> expectedSourceEpoch,
		LiquidWalletFactsWireV1DescriptorNetworkClass descriptorNetworkClass,
		uint lastDerivationIndex,
		ReadOnlySpan<byte> descriptorAscii,
		IReadOnlyList<LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource> candidates,
		ReadOnlySpan<byte> slip77MasterKey)
	{
		if (expectedSourceEpoch.Length != SourceEpochLength) throw new ArgumentException("The source epoch must be exactly 32 bytes.", nameof(expectedSourceEpoch));
		if (slip77MasterKey.Length != Slip77MasterKeyLength) throw new ArgumentException("The SLIP-77 master key must be exactly 32 bytes.", nameof(slip77MasterKey));
		if (!LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(expectedSourceEpoch, descriptorNetworkClass, lastDerivationIndex, descriptorAscii, candidates, out LiquidWalletFactsWireV1UnpreparedRequestFrame? frame, out LiquidWalletFactsWireErrorCode errorCode) || frame is null)
		{
			throw new ArgumentException($"The WLFQ request was rejected: {errorCode}.", nameof(candidates));
		}
		using (frame)
		{
			byte[] request = new byte[frame.Length];
			frame.CopyFrameTo(request);
			byte[] epoch = expectedSourceEpoch.ToArray();
			byte[] key = slip77MasterKey.ToArray();
			byte[] entropy = new byte[EntropyLength];
			byte[]? response = null;
			try
			{
				ulong required;
				RandomNumberGenerator.Fill(entropy);
				int status = Invoke(request, epoch, key, null, 0, entropy, out required);
				if (status != StatusOutputCapacityV1 || required is < 64 or > MaxReachableResponseBytesV1) throw new InvalidOperationException($"Native capacity query failed with status {status} and length {required}.");
				response = new byte[checked((int)required)];
				CryptographicOperations.ZeroMemory(entropy);
				RandomNumberGenerator.Fill(entropy);
				status = Invoke(request, epoch, key, response, (ulong)response.Length, entropy, out ulong actual);
				if (status != StatusOkV1 || actual != required) throw new InvalidOperationException("Native wallet-facts response length or status drifted.");
				if (!LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(response, epoch, out LiquidWalletFactsWireV1UntrustedStructuralResponse? decoded, out LiquidWalletFactsWireErrorCode decodeError) || decoded is null) throw new InvalidOperationException($"Native OK response was rejected by WLFV decoder: {decodeError}.");
				using (decoded)
				{
					var transactions = new List<LiquidWalletTransactionObservation>(decoded.TransactionCount);
					for (int ti = 0; ti < decoded.TransactionCount; ti++)
					{
						var tv = decoded.GetTransaction(ti);
						var inputs = new List<LiquidOutPoint>(tv.InputCount);
						for (int ii = 0; ii < tv.InputCount; ii++)
						{
							var iv = tv.GetInput(ii);
							inputs.Add(LiquidOutPoint.CreateSpendable(LiquidTransactionId.ParseConsensusBytes(iv.GetPreviousTransactionIdConsensusBytes()), iv.PreviousOutputIndex));
						}
						var outputs = new List<LiquidOwnedOutputObservation>(tv.OwnedOutputCount);
						for (int oi = 0; oi < tv.OwnedOutputCount; oi++)
						{
							var ov = tv.GetOwnedOutput(oi);
							outputs.Add(LiquidOwnedOutputObservation.Create(tv.GetTransactionIdConsensusBytes(), ov.OutputIndex, tv.GetTransactionWitnessBinding(), ov.GetScriptPubKey(), ov.GetSpendPublicKey(), ov.GetBlindingPublicKey(), (LiquidKeyBranch)ov.Branch, ov.DerivationIndex, ov.GetAssetIdConsensusBytes(), ov.Value));
						}
						transactions.Add(LiquidWalletTransactionObservation.Create(tv.GetTransactionIdConsensusBytes(), tv.GetTransactionWitnessBinding(), inputs, outputs));
					}
					return LiquidWalletObservationBatch.Create(transactions);
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(entropy);
				CryptographicOperations.ZeroMemory(request);
				CryptographicOperations.ZeroMemory(epoch);
				CryptographicOperations.ZeroMemory(key);
				if (response is not null) CryptographicOperations.ZeroMemory(response);
			}
		}
	}

	private static int Invoke(byte[] request, byte[] epoch, byte[] key, byte[]? response, ulong capacity, byte[] entropy, out ulong length)
	{
		ulong resultLength = 0;
		int status;
		fixed (byte* rp = request, ep = epoch, kp = key, entropyPointer = entropy)
		fixed (byte* op = response)
		{
			status = ((delegate* unmanaged[Cdecl]<byte*, ulong, byte*, byte*, byte*, ulong, ulong*, byte*, ulong, int>)NativeEntryPointAddress.Value)(rp, (ulong)request.Length, ep, kp, op, capacity, &resultLength, entropyPointer, (ulong)entropy.Length);
		}
		length = resultLength;
		return status;
	}

	private static readonly Lazy<IntPtr> NativeLibraryHandle = new(LoadNativeLibraryHandle);
	private static readonly Lazy<IntPtr> NativeEntryPointAddress = new(() => NativeLibrary.GetExport(NativeLibraryHandle.Value, "wln_wallet_facts_observe_v1"));
	private static IntPtr LoadNativeLibraryHandle() { EnsurePinnedNativeArtifact(); return NativeLibrary.Load(ResolveLibraryPath()); }

	private static class PlatformLibraryPin
	{
		internal static readonly string ExpectedSha256 = Select();
		private static string Select() => OperatingSystem.IsMacOS() ? MacOsLibrarySha256 : OperatingSystem.IsLinux() && LinuxLibrarySha256.Length != 0 ? LinuxLibrarySha256 : throw new PlatformNotSupportedException("The pinned native wallet-facts cdylib is available for macOS only.");
	}
}
