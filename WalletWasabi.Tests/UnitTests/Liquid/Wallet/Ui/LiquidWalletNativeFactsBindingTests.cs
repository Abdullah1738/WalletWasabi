using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire;
using Xunit;
using SHA256 = System.Security.Cryptography.SHA256;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-FACTS-FFI-001: the production native wallet-facts observation binding test
/// matrix. Drives <see cref="LiquidWalletNativeFactsBinding.Observe"/> against the real
/// pinned-commit native cdylib (<c>wln_wallet_facts_observe_v1</c>): the artifact-pin row
/// recomputes the host-platform SHA-256 against the production pin, the live capacity row drives
/// the committed empty-request corpus frame through the frozen two-call capacity protocol, the
/// corpus rows mirror the native <c>tests/abi.rs</c>
/// <c>canonical_corpus_failures_map_to_frozen_statuses_atomically</c> row set (frozen statuses,
/// sentinel-normalized output length, untouched output), and the fail-closed rows pin the wrong-
/// epoch source-binding mismatch and the pre-native argument guards. This binding performs no
/// node contact, no RPC, no broadcast, and no signing.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletNativeFactsBindingTests
{
	/// <summary>The full native commit the pinned cdylib was built from.</summary>
	private const string ExpectedPinnedNativeCommit = "bd50133a9fbcac5d187757e634c1cc2fc65a10ac";

	private static string FramesRoot => Path.Combine(
		AppContext.BaseDirectory,
		"TestData",
		"Liquid",
		"WalletFactsWireV1",
		"nonlinkable-reference",
		"vectors",
		"frames");

	private static byte[] ReadFrame(string name)
	{
		byte[] text = File.ReadAllBytes(Path.Combine(FramesRoot, name));
		Assert.NotEmpty(text);
		Assert.Equal((byte)'\n', text[^1]);
		return Convert.FromHexString(System.Text.Encoding.ASCII.GetString(text.AsSpan(0, text.Length - 1)));
	}

	// Required evidence §9.1 / §8.5: the production loader resolves the platform-correct pinned
	// wallet-facts cdylib from the dedicated NativeWalletFacts/ subdirectory, verifies it is a
	// tracked regular file (no reparse point), and recomputes its SHA-256 against the production
	// pin. The single frozen export resolves from the hash-pinned handle.
	[Fact]
	public void ProductionLoaderResolvesThePinnedNativeWalletFactsArtifact()
	{
		string libraryPath = LiquidWalletNativeFactsBinding.ResolveLibraryPath();
		Assert.Equal(LiquidWalletNativeFactsBinding.LibraryFileName, Path.GetFileName(libraryPath));
		Assert.Equal(
			LiquidWalletNativeFactsBinding.ArtifactSubdirectory,
			Path.GetFileName(Path.GetDirectoryName(libraryPath)));
		Assert.True(File.Exists(libraryPath), $"Missing native library: {libraryPath}");
		Assert.False(
			(File.GetAttributes(libraryPath) & FileAttributes.ReparsePoint) != 0,
			$"Native library reparse point is forbidden: {libraryPath}");

		byte[] libraryBytes = File.ReadAllBytes(libraryPath);
		Assert.NotEmpty(libraryBytes);
		string actual = Convert.ToHexStringLower(SHA256.HashData(libraryBytes));
		string expected = OperatingSystem.IsLinux()
			? LiquidWalletNativeFactsBinding.LinuxLibrarySha256
			: LiquidWalletNativeFactsBinding.MacOsLibrarySha256;
		Assert.Equal(expected, actual);

		// The pin call itself must not throw on a supported platform.
		LiquidWalletNativeFactsBinding.EnsurePinnedNativeArtifact();
		Assert.Equal(ExpectedPinnedNativeCommit, LiquidWalletNativeFactsBinding.PinnedNativeCommit);
	}

	// Required evidence §8.4: the binding exposes zero public methods and no public surface of
	// any kind (mirror of TransactionIdHelperIsInternalAndAddsNoPublicSurface).
	[Fact]
	public void BindingIsInternalAndExposesNoPublicSurface()
	{
		Type binding = typeof(LiquidWalletNativeFactsBinding);
		Assert.True(binding.IsNotPublic);
		Assert.True(binding.IsAbstract && binding.IsSealed);
		Assert.Empty(binding.GetMethods(
			BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
		Assert.Empty(binding.GetConstructors(
			BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
		Assert.Empty(binding.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));
		Assert.Empty(binding.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));
	}

	// Required evidence §8.4: no broadcast / no node surface on either new production type
	// (mirror of NativeSignerIntroducesNoBroadcastOrNodeSurface).
	[Fact]
	public void BindingAndObserverIntroduceNoBroadcastOrNodeSurface()
	{
		foreach (Type type in new[] { typeof(LiquidWalletNativeFactsBinding), typeof(LiquidWalletNativeFactsObserver) })
		{
			foreach (MethodInfo method in type.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
				BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				string name = method.Name;
				Assert.DoesNotContain("Broadcast", name, StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain("Rpc", name, StringComparison.Ordinal);
				Assert.DoesNotContain("Send", name, StringComparison.Ordinal);
			}
			Assert.DoesNotContain(
				type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
				field => field.FieldType.FullName?.Contains("Rpc", StringComparison.Ordinal) is true);
		}
	}

	// Required evidence §9.1: the live capacity/empty-response row. The committed corpus frame
	// request-00-base-empty (the same frame the native dynamic test consumes; epoch = frame
	// bytes [28:60]) drives the real pinned cdylib through the frozen two-call protocol: the
	// query returns exactly -10 with the published required length 64; the write call with a
	// second fresh seed returns 0, the identical length, WLFV magic, and an untouched tail on an
	// oversized buffer.
	[Fact]
	public void CapacityQueryAndWriteReturnTheCanonicalEmptyResponse()
	{
		byte[] frame = ReadFrame("request-00-base-empty.hex");
		byte[] epoch = frame[28..60];
		byte[] slip77 = Enumerable.Repeat((byte)0x52, 32).ToArray();
		byte[] queryEntropy = Enumerable.Repeat((byte)0x63, 32).ToArray();
		byte[] writeEntropy = Enumerable.Repeat((byte)0x74, 32).ToArray();
		try
		{
			// Query: an empty output span is the genuine null-pointer capacity query.
			int queryStatus = LiquidWalletNativeFactsBinding.Observe(
				frame,
				epoch,
				slip77,
				Span<byte>.Empty,
				out ulong requiredLength,
				queryEntropy);
			Assert.Equal(LiquidWalletNativeFactsBinding.StatusOutputCapacityV1, queryStatus);
			Assert.Equal((ulong)LiquidWalletNativeFactsBinding.MinimumResponseFrameBytes, requiredLength);

			// Write: exact capacity plus a sentinel tail that must stay untouched.
			byte[] output = Enumerable.Repeat((byte)0xa5, (int)requiredLength + 8).ToArray();
			int writeStatus = LiquidWalletNativeFactsBinding.Observe(
				frame,
				epoch,
				slip77,
				output,
				out ulong writtenLength,
				writeEntropy);
			Assert.Equal(LiquidWalletNativeFactsBinding.StatusOkV1, writeStatus);
			Assert.Equal(requiredLength, writtenLength);
			Assert.Equal("WLFV"u8.ToArray(), output[..4]);
			Assert.All(output[(int)writtenLength..], value => Assert.Equal((byte)0xa5, value));
			CryptographicOperations.ZeroMemory(output);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(queryEntropy);
			CryptographicOperations.ZeroMemory(writeEntropy);
		}
	}

	// Required evidence §9.1: the 24 noncanonical corpus frames the native tests/abi.rs
	// canonical_corpus_failures_map_to_frozen_statuses_atomically consumes, each mapped to its
	// frozen status with out_response_length normalized to 0 from a nonzero sentinel and the
	// output buffer untouched.
	[Theory]
	[MemberData(nameof(CorpusFailureRows))]
	public void NoncanonicalCorpusFramesMapToFrozenStatusesAtomically(string frameName, int expectedStatus)
	{
		byte[] frame = ReadFrame(frameName);
		byte[] epoch = frame[28..60];
		byte[] slip77 = Enumerable.Repeat((byte)0x52, 32).ToArray();
		byte[] entropy = Enumerable.Repeat((byte)0x63, 32).ToArray();
		byte[] output = Enumerable.Repeat((byte)0xa5, 128).ToArray();
		try
		{
			int status = LiquidWalletNativeFactsBinding.Observe(
				frame,
				epoch,
				slip77,
				output,
				out ulong outResponseLength,
				entropy);
			Assert.Equal(expectedStatus, status);
			Assert.Equal(0UL, outResponseLength);
			Assert.All(output, value => Assert.Equal((byte)0xa5, value));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(entropy);
			CryptographicOperations.ZeroMemory(output);
		}
	}

	// The exact (frame, frozen status) row set of the native
	// canonical_corpus_failures_map_to_frozen_statuses_atomically test.
	public static IEnumerable<object[]> CorpusFailureRows()
	{
		yield return ["request-12-wrong-magic.hex", -2];
		yield return ["request-13-wrong-version.hex", -2];
		yield return ["request-09-body-truncated.hex", -3];
		yield return ["request-19-derivation-plus-one.hex", -4];
		yield return ["request-02-base-semantic-reject.hex", -5];
		yield return ["request-01-base-nonempty.hex", -7];
		yield return ["request-15-declared-length-mismatch.hex", -3];
		yield return ["request-16-flags-nonzero.hex", -3];
		yield return ["request-17-unknown-network.hex", -3];
		yield return ["request-18-header-reserved.hex", -3];
		yield return ["request-20-zero-source.hex", -1];
		yield return ["request-21-zero-descriptor-length.hex", -3];
		yield return ["request-22-candidate-count-plus-one.hex", -4];
		yield return ["request-23-previous-count-plus-one.hex", -4];
		yield return ["request-24-header-tail-reserved.hex", -3];
		yield return ["request-25-trailing-byte.hex", -3];
		yield return ["request-26-concatenated.hex", -3];
		yield return ["request-27-descriptor-whitespace.hex", -3];
		yield return ["request-28-descriptor-checksum-uppercase.hex", -3];
		yield return ["request-28a-descriptor-nul.hex", -3];
		yield return ["request-28b-descriptor-non-ascii.hex", -3];
		yield return ["request-29-zero-candidate-length.hex", -3];
		yield return ["request-30-candidate-reserved.hex", -3];
		yield return ["request-31-previous-count-mismatch.hex", -3];
	}

	// Required evidence §9.1: a wrong expected epoch is the frozen source-binding mismatch, with
	// the output length normalized and the output buffer untouched.
	[Fact]
	public void WrongExpectedEpochMapsToSourceBindingMismatch()
	{
		byte[] frame = ReadFrame("request-00-base-empty.hex");
		byte[] wrongEpoch = Enumerable.Repeat((byte)0x42, 32).ToArray();
		byte[] slip77 = Enumerable.Repeat((byte)0x52, 32).ToArray();
		byte[] entropy = Enumerable.Repeat((byte)0x63, 32).ToArray();
		byte[] output = Enumerable.Repeat((byte)0xa5, 64).ToArray();
		try
		{
			int status = LiquidWalletNativeFactsBinding.Observe(
				frame,
				wrongEpoch,
				slip77,
				output,
				out ulong outResponseLength,
				entropy);
			Assert.Equal(LiquidWalletNativeFactsBinding.StatusSourceBindingMismatchV1, status);
			Assert.Equal(0UL, outResponseLength);
			Assert.All(output, value => Assert.Equal((byte)0xa5, value));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(wrongEpoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(entropy);
			CryptographicOperations.ZeroMemory(output);
		}
	}

	// Required evidence §9.1: the managed argument guards throw ArgumentException before the
	// native boundary (length-shaped epoch/key/entropy, empty frame).
	[Fact]
	public void ObserveRejectsInvalidArgumentShapesBeforeNativeEntry()
	{
		byte[] frame = ReadFrame("request-00-base-empty.hex");
		byte[] epoch = Enumerable.Repeat((byte)0x41, 32).ToArray();
		byte[] slip77 = Enumerable.Repeat((byte)0x52, 32).ToArray();
		byte[] entropy = Enumerable.Repeat((byte)0x63, 32).ToArray();
		byte[] output = new byte[64];
		try
		{
			Assert.Throws<ArgumentException>(() => LiquidWalletNativeFactsBinding.Observe(
				ReadOnlySpan<byte>.Empty, epoch, slip77, output, out _, entropy));
			Assert.Throws<ArgumentException>(() => LiquidWalletNativeFactsBinding.Observe(
				frame, epoch.AsSpan(..^1), slip77, output, out _, entropy));
			Assert.Throws<ArgumentException>(() => LiquidWalletNativeFactsBinding.Observe(
				frame, new byte[33], slip77, output, out _, entropy));
			Assert.Throws<ArgumentException>(() => LiquidWalletNativeFactsBinding.Observe(
				frame, epoch, slip77.AsSpan(..^1), output, out _, entropy));
			Assert.Throws<ArgumentException>(() => LiquidWalletNativeFactsBinding.Observe(
				frame, epoch, new byte[31], output, out _, entropy));
			Assert.Throws<ArgumentException>(() => LiquidWalletNativeFactsBinding.Observe(
				frame, epoch, slip77, output, out _, entropy.AsSpan(..^1)));
			Assert.Throws<ArgumentException>(() => LiquidWalletNativeFactsBinding.Observe(
				frame, epoch, slip77, output, out _, new byte[33]));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frame);
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(entropy);
			CryptographicOperations.ZeroMemory(output);
		}
	}
}
