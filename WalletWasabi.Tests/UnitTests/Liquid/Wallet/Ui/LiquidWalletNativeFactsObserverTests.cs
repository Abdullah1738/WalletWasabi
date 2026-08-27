using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;
using CandidateSource = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-FACTS-FFI-001 §9.2: the production native wallet-facts observation seam test
/// matrix. Drives <see cref="LiquidWalletNativeFactsObserver.TryObserve"/> end to end against the
/// real pinned-commit native cdylib over the committed native-produced ground-truth fixture under
/// <c>TestData/Liquid/WalletFactsWireV1/native-observe/</c> (WLFQ requests, WLFV expected
/// responses, descriptor, SLIP-77 master, pinned entropy pair, and the decoded field rows of
/// <c>expected-fields.tsv</c>). The seed-pinned rows pin both entropy seeds through
/// <see cref="LiquidWalletNativeFactsObserver.EntropyOverrideForTesting"/> and assert the observed
/// batch byte-for-byte against the independently-authored field expectations (transaction id,
/// witness binding, inputs, and per-owned-output outpoint/branch/derivation index/asset/value/
/// 22-byte script/spend key/blinding key) across owned external+internal outputs, the lawful
/// zero-owned batch, and multi-candidate ordering. The random-entropy row proves the live
/// <see cref="RandomNumberGenerator"/> path succeeds; the fail-closed rows prove a corrupt
/// candidate surfaces <see langword="false"/>, a request epoch that disagrees with the supplied
/// epoch is the frozen source-binding mismatch (-8), and a second output-capacity status (the
/// write call re-issued with a too-small buffer) is fail-closed with no retry. This observer
/// performs no node contact, no RPC, no broadcast, no signing, and no key custody.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletNativeFactsObserverTests
{
	private static string FixtureRoot => Path.Combine(
		AppContext.BaseDirectory,
		"TestData",
		"Liquid",
		"WalletFactsWireV1",
		"native-observe");

	private static string ReadField(string name) =>
		File.ReadAllText(Path.Combine(FixtureRoot, name)).Trim();

	private static byte[] ReadFieldBytes(string name) => Convert.FromHexString(ReadField(name));

	private static byte[] SourceEpoch => ReadFieldBytes("source_epoch.txt");
	private static byte[] Slip77MasterKey => ReadFieldBytes("slip77.txt");
	private static byte[] DescriptorAscii => System.Text.Encoding.ASCII.GetBytes(ReadField("descriptor.txt"));
	private static LiquidWalletFactsWireV1DescriptorNetworkClass NetworkClass =>
		LiquidWalletFactsWireV1DescriptorNetworkClass.Test;
	private static uint LastDerivationIndex => uint.Parse(ReadField("last_derivation_index.txt"));

	private static CandidateSource OwnedCandidate() => new(
		ReadFieldBytes("owned-transaction.txt"),
		[ReadFieldBytes("previous-transaction.txt")]);

	private static CandidateSource UnownedCandidate() => new(
		ReadFieldBytes("unowned-transaction.txt"),
		[ReadFieldBytes("previous-transaction.txt")]);

	/// <summary>
	/// Pins the two frozen entropy seeds (capacity query then write call) through the test-only
	/// hook for the duration of <paramref name="action"/>, then clears the hook so no state leaks
	/// across rows.
	/// </summary>
	private static void WithPinnedEntropy(Action action)
	{
		byte[][] seeds = [ReadFieldBytes("entropy_query.txt"), ReadFieldBytes("entropy_write.txt")];
		int call = 0;
		try
		{
			LiquidWalletNativeFactsObserver.EntropyOverrideForTesting = () => seeds[call++];
			action();
		}
		finally
		{
			LiquidWalletNativeFactsObserver.EntropyOverrideForTesting = null;
			CryptographicOperations.ZeroMemory(seeds[0]);
			CryptographicOperations.ZeroMemory(seeds[1]);
		}
	}

	// Required evidence §9.2: the seed-pinned end-to-end ground-truth rows. Each shape encodes its
	// WLFQ from the fixture candidates, observes through the real pinned cdylib under the pinned
	// entropy pair, decodes, and asserts the projected batch equals the independently-authored
	// expectations of expected-fields.tsv. "single" owns the external (branch 0, index 0) and
	// internal (branch 1, index 1) outputs of one candidate; "zero" is the lawful zero-owned batch;
	// "multi" lists both candidates and orders the two transactions by ascending consensus txid.
	[Theory]
	[InlineData("single")]
	[InlineData("zero")]
	[InlineData("multi")]
	public void SeedPinnedObservationMatchesNativeGroundTruth(string shape)
	{
		CandidateSource[] candidates = shape switch
		{
			"single" => [OwnedCandidate()],
			"zero" => [UnownedCandidate()],
			"multi" => [OwnedCandidate(), UnownedCandidate()],
			_ => throw new ArgumentOutOfRangeException(nameof(shape)),
		};
		byte[] epoch = SourceEpoch;
		byte[] slip77 = Slip77MasterKey;
		byte[] descriptor = DescriptorAscii;

		// The fixture candidates must re-encode to the exact committed WLFQ request bytes.
		Assert.True(LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
			epoch,
			NetworkClass,
			LastDerivationIndex,
			descriptor,
			candidates,
			out LiquidWalletFactsWireV1UnpreparedRequestFrame? builtFrame,
			out _));
		Assert.NotNull(builtFrame);
		using (builtFrame)
		{
			byte[] builtBytes = new byte[builtFrame.Length];
			builtFrame.CopyFrameTo(builtBytes);
			byte[] expectedRequest = ReadFieldBytes($"request-{shape}.hex");
			Assert.Equal(expectedRequest, builtBytes);
			CryptographicOperations.ZeroMemory(builtBytes);
			CryptographicOperations.ZeroMemory(expectedRequest);
		}

		LiquidWalletObservationBatch? batch = null;
		WithPinnedEntropy(() =>
		{
			Assert.True(LiquidWalletNativeFactsObserver.TryObserve(
				epoch,
				NetworkClass,
				LastDerivationIndex,
				descriptor,
				slip77,
				candidates,
				out batch));
		});
		Assert.NotNull(batch);
		try
		{
			IReadOnlyList<string[]> expectations = ExpectationsFor(shape);
			Assert.Equal(expectations.Count(row => row[3] == "txid"), batch.TransactionCount);

			IReadOnlyList<LiquidWalletTransactionObservation> transactions = batch.GetTransactions();
			foreach (IGrouping<int, string[]> transactionRows in expectations
				.GroupBy(row => int.Parse(row[2]))
				.OrderBy(group => group.Key))
			{
				string[] txidRow = transactionRows.Single(row => row[3] == "txid");
				int transactionIndex = int.Parse(txidRow[2]);
				LiquidWalletTransactionObservation transaction = transactions[transactionIndex];

				Assert.Equal(
					Convert.FromHexString(txidRow[4]),
					transaction.GetTransactionIdConsensusBytes());
				Assert.Equal(
					Convert.FromHexString(transactionRows.Single(row => row[3] == "witness-binding")[4]),
					transaction.GetTransactionWitnessBinding());

				string[][] inputRows = transactionRows.Where(row => row[3] == "input")
					.OrderBy(row => int.Parse(row[4]))
					.ToArray();
				Assert.Equal(inputRows.Length, transaction.InputCount);
				IReadOnlyList<WalletWasabi.Liquid.Transactions.LiquidOutPoint> inputs = transaction.GetInputs();
				for (int inputIndex = 0; inputIndex < inputRows.Length; inputIndex++)
				{
					Assert.Equal(
						Convert.FromHexString(inputRows[inputIndex][5]),
						inputs[inputIndex].TransactionId.ToConsensusBytes());
					Assert.Equal(uint.Parse(inputRows[inputIndex][6]), inputs[inputIndex].OutputIndex);
				}

				string[][] outputRows = transactionRows.Where(row => row[3] == "output")
					.OrderBy(row => int.Parse(row[4]))
					.ToArray();
				Assert.Equal(outputRows.Length, transaction.OwnedOutputCount);
				IReadOnlyList<LiquidOwnedOutputObservation> ownedOutputs = transaction.GetOwnedOutputs();
				for (int outputIndex = 0; outputIndex < outputRows.Length; outputIndex++)
				{
					string[] row = outputRows[outputIndex];
					LiquidOwnedOutputObservation output = ownedOutputs[outputIndex];
					Assert.Equal(uint.Parse(row[4]), output.OutputIndex);
					Assert.Equal(transaction.GetTransactionIdConsensusBytes(), output.GetTransactionIdConsensusBytes());
					Assert.Equal(transaction.GetTransactionWitnessBinding(), output.GetTransactionWitnessBinding());
					Assert.Equal((LiquidKeyBranch)byte.Parse(row[5]), output.Branch);
					Assert.Equal(uint.Parse(row[6]), output.DerivationIndex);
					Assert.Equal(Convert.FromHexString(row[8]), output.GetScriptPubKey());
					Assert.Equal(Convert.FromHexString(row[9]), output.GetSpendPublicKey());
					Assert.Equal(Convert.FromHexString(row[10]), output.GetBlindingPublicKey());
					Assert.Equal(Convert.FromHexString(row[11]), output.GetAssetIdConsensusBytes());
					Assert.Equal(long.Parse(row[12]), output.Value);
				}
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(descriptor);
		}
	}

	// Required evidence §9.2: both native calls run under live RandomNumberGenerator seeds (no
	// test hook) and the observation succeeds against the real pinned cdylib. This proves the
	// production entropy path rather than the seed-pinned override.
	[Fact]
	public void RandomEntropyObservationSucceeds()
	{
		CandidateSource[] candidates = [OwnedCandidate()];
		byte[] epoch = SourceEpoch;
		byte[] slip77 = Slip77MasterKey;
		byte[] descriptor = DescriptorAscii;
		try
		{
			Assert.True(LiquidWalletNativeFactsObserver.TryObserve(
				epoch,
				NetworkClass,
				LastDerivationIndex,
				descriptor,
				slip77,
				candidates,
				out LiquidWalletObservationBatch? batch));
			Assert.NotNull(batch);
			Assert.Equal(1, batch.TransactionCount);
			Assert.Equal(2, batch.OwnedOutputCount);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(descriptor);
		}
	}

	// Required evidence §9.2: a corrupt candidate transaction is rejected at encode or native
	// time and surfaces false with no partial batch.
	[Fact]
	public void CorruptCandidateReturnsFalse()
	{
		byte[] corruptTransaction = ReadFieldBytes("owned-transaction.txt");
		corruptTransaction[^1] ^= 0xff;
		CandidateSource[] candidates = [new CandidateSource(corruptTransaction, [ReadFieldBytes("previous-transaction.txt")])];
		byte[] epoch = SourceEpoch;
		byte[] slip77 = Slip77MasterKey;
		byte[] descriptor = DescriptorAscii;
		try
		{
			Assert.False(LiquidWalletNativeFactsObserver.TryObserve(
				epoch,
				NetworkClass,
				LastDerivationIndex,
				descriptor,
				slip77,
				candidates,
				out LiquidWalletObservationBatch? batch));
			Assert.Null(batch);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(corruptTransaction);
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(descriptor);
		}
	}

	// Required evidence §9.2: the frozen source-binding mismatch. A copy of the committed
	// multi-candidate request with the embedded source epoch (frame bytes [28:60]) replaced by a
	// nonzero foreign epoch is observed against the true epoch; the native side reports the
	// source-binding mismatch (-8) and the binding normalizes the published length to zero.
	[Fact]
	public void MismatchedEpochMapsToSourceBindingMismatch()
	{
		byte[] frame = ReadFieldBytes("request-multi.hex");
		byte[] epoch = SourceEpoch;
		byte[] slip77 = Slip77MasterKey;
		byte[] entropy = ReadFieldBytes("entropy_query.txt");
		byte[] output = Enumerable.Repeat((byte)0xa5, 64).ToArray();
		try
		{
			// Replace the request-embedded epoch with a nonzero foreign epoch so the request epoch
			// disagrees with the supplied expected epoch (the native -8 source-binding mismatch).
			for (int index = 28; index < 60; index++)
			{
				frame[index] = 0x42;
			}
			int status = LiquidWalletNativeFactsBinding.Observe(
				frame,
				epoch,
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
			CryptographicOperations.ZeroMemory(epoch);
			CryptographicOperations.ZeroMemory(slip77);
			CryptographicOperations.ZeroMemory(entropy);
			CryptographicOperations.ZeroMemory(output);
		}
	}

	// Required evidence §9.2: the frozen capacity protocol admits no retry. A second output-capacity
	// status — here the write call issued with a buffer one byte short of the length the query just
	// published — is fail-closed: the binding re-reports the required length, normalizes no success,
	// and there is no retry loop. The write uses a second fresh entropy seed with the request,
	// epoch, and SLIP-77 unchanged, exactly as the observer's write call does.
	[Fact]
	public void SecondCapacityStatusIsFailClosedWithNoRetry()
	{
		byte[] frame = ReadFieldBytes("request-single.hex");
		byte[] epoch = SourceEpoch;
		byte[] slip77 = Slip77MasterKey;
		byte[] queryEntropy = ReadFieldBytes("entropy_query.txt");
		byte[] writeEntropy = ReadFieldBytes("entropy_write.txt");
		try
		{
			int queryStatus = LiquidWalletNativeFactsBinding.Observe(
				frame,
				epoch,
				slip77,
				Span<byte>.Empty,
				out ulong requiredLength,
				queryEntropy);
			Assert.Equal(LiquidWalletNativeFactsBinding.StatusOutputCapacityV1, queryStatus);
			Assert.InRange(
				requiredLength,
				(ulong)LiquidWalletNativeFactsBinding.MinimumResponseFrameBytes,
				LiquidWalletNativeFactsBinding.MaxReachableResponseBytesV1);

			// The write call with one byte too few must fail closed with a second capacity status
			// and still publish the required length; there is no retry.
			byte[] shortBuffer = Enumerable.Repeat((byte)0xa5, (int)requiredLength - 1).ToArray();
			try
			{
				int writeStatus = LiquidWalletNativeFactsBinding.Observe(
					frame,
					epoch,
					slip77,
					shortBuffer,
					out ulong shortLength,
					writeEntropy);
				Assert.Equal(LiquidWalletNativeFactsBinding.StatusOutputCapacityV1, writeStatus);
				Assert.Equal(requiredLength, shortLength);
				Assert.All(shortBuffer, value => Assert.Equal((byte)0xa5, value));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(shortBuffer);
			}
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

	// Required evidence §8.4: the observer exposes exactly the frozen public surface (none — it is
	// an internal static type) and its internal operation surface is exactly TryObserve plus the
	// test-only entropy hook (mirror of NativeSignerExposesExactlyTheFrozenSurface).
	[Fact]
	public void ObserverExposesExactlyTheFrozenSurface()
	{
		Type observer = typeof(LiquidWalletNativeFactsObserver);
		Assert.True(observer.IsNotPublic);
		Assert.True(observer.IsAbstract && observer.IsSealed);

		// Zero public methods, properties, fields, or constructors escape the internal type.
		Assert.Empty(observer.GetMethods(
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));
		Assert.Empty(observer.GetProperties(
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
			System.Reflection.BindingFlags.Instance));
		Assert.Empty(observer.GetFields(
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
			System.Reflection.BindingFlags.Instance));
		Assert.Empty(observer.GetConstructors(
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));

		// The internal operation surface is exactly the frozen seam: TryObserve, the entropy-hook
		// accessors, and the private projection/entropy helpers.
		Assert.Equal(
			new[]
			{
				"ContainsNonzero",
				"FillEntropy",
				"TryObserve",
				"TryProject",
				"get_EntropyOverrideForTesting",
				"set_EntropyOverrideForTesting",
			},
			observer.GetMethods(
					System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static |
					System.Reflection.BindingFlags.DeclaredOnly)
				.Select(method => method.Name)
				.Order(StringComparer.Ordinal)
				.ToArray());

		Assert.Equal(
			new[] { "EntropyOverrideForTesting" },
			observer.GetProperties(
					System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
				.Select(property => property.Name)
				.ToArray());
	}

	/// <summary>
	/// Reads the decoded field rows of expected-fields.tsv for one shape. Each row is
	/// {shape, "tx", txIndex, kind, ...}: txid/witness-binding rows carry one hex field at [4];
	/// input rows carry {inputIndex, previousTxidHex, previousIndex} at [4..6]; output rows carry
	/// {outputIndex, branch, derivationIndex, reserved, scriptHex, spendKeyHex, blindingKeyHex,
	/// assetHex, value} at [4..12].
	/// </summary>
	private static IReadOnlyList<string[]> ExpectationsFor(string shape) =>
		File.ReadAllText(Path.Combine(FixtureRoot, "expected-fields.tsv"))
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.Split('\t'))
			.Where(fields => fields[0] == shape)
			.ToArray();
}
