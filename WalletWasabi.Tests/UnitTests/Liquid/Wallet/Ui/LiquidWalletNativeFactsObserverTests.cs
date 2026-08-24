using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

[Collection("Serial unit tests collection")]
public class LiquidWalletNativeFactsObserverTests
{
	private const string Descriptor = "elwpkh([28b3f14e/84'/1'/0']tpubDC2Q4xK4XH72GM7MowNuajyWVbigRLBWKswyP5T88hpPwu5nGqJWnda8zhJEFt71av73Hm8mUMMFSz9acNVzz8b1UbdSHCDXKTbSv5eEytu/<0;1>/*)#u0khc0kg";
	private static byte[] Epoch => [.. Enumerable.Repeat((byte)0x41, 32)];
	private static byte[] Slip77 => [.. Enumerable.Repeat((byte)0x52, 32)];

	[Fact]
	public void LivePinnedNativeObservationProjectsOwnedOutputs()
	{
		var fixture = LoadFixture();
		LiquidWalletObservationBatch batch = LiquidWalletNativeFactsObserver.Observe(
			Epoch,
			LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
			1,
			Encoding.ASCII.GetBytes(Descriptor),
			[fixture.Candidate],
			Slip77);

		Assert.Equal(1, batch.TransactionCount);
		Assert.Equal(2, batch.OwnedOutputCount);
		LiquidWalletTransactionObservation transaction = Assert.Single(batch.GetTransactions());
		Assert.Equal(2, transaction.InputCount);
		Assert.Equal(2, transaction.OwnedOutputCount);
		byte[] expectedTransactionId = Convert.FromHexString("6ACD40DD0689A64796AD15C4A4E04CC2E81DFFB5AC6B2FBF69E738BCB2C63FC6");
		Assert.Equal(expectedTransactionId, transaction.GetTransactionIdConsensusBytes());
		byte[] witnessBinding = transaction.GetTransactionWitnessBinding();
		Assert.Equal(32, witnessBinding.Length);
		Assert.Contains(witnessBinding, value => value != 0);

		IReadOnlyList<LiquidOutPoint> inputs = transaction.GetInputs();
		Assert.Equal(2, inputs.Count);
		byte[] expectedPreviousTransactionId = Convert.FromHexString("89C93216E369737550E3569A725C9245880AD548F008742E3ADC9EF8C6B849D0");
		Assert.Equal(expectedPreviousTransactionId, inputs[0].TransactionId.ToConsensusBytes());
		Assert.Equal(0u, inputs[0].OutputIndex);
		Assert.Equal(expectedPreviousTransactionId, inputs[1].TransactionId.ToConsensusBytes());
		Assert.Equal(1u, inputs[1].OutputIndex);

		IReadOnlyList<LiquidOwnedOutputObservation> outputs = transaction.GetOwnedOutputs();
		Assert.Equal(2, outputs.Count);
		Assert.True(outputs[0].OutputIndex < outputs[1].OutputIndex);
		AssertOwnedOutput(
			outputs[0],
			expectedTransactionId,
			witnessBinding,
			0,
			LiquidKeyBranch.External,
			0,
			900,
			"0014D363D538BEA12647F61C634BDD7A791D676850E9",
			"0211B24105B70886A90F848DA8C659BE73BD6E3486CF2AA706693907479865BF81",
			"03BA1B29067175A8D1B946C933A8465926EDAA7E66C4E40D8B1A998EE6AFCB4762");
		AssertOwnedOutput(
			outputs[1],
			expectedTransactionId,
			witnessBinding,
			1,
			LiquidKeyBranch.Internal,
			1,
			2_000,
			"0014A3E18F06B5369914234BD7DF7462D7BBD3635714",
			"034473B362E3FF48C9188E9E02165D72E111412E5BA451BF76CD2B78109186D866",
			"020E9219B45A3B58DF2E91BAC25F655ACEF4DA5C54ED84F85913FCCA0301447BE4");
	}

	[Fact]
	public void WrongExpectedEpochFailsClosedThroughLiveNativeSourceBindingPath()
	{
		var fixture = LoadFixture();
		byte[] frame = BuildFrame(fixture.Candidate);
		byte[] wrongEpoch = [.. Enumerable.Repeat((byte)0x42, 32)];
		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletNativeFactsObserver.ObservePreparedFrame(frame, wrongEpoch, Slip77));
		Assert.Contains("status -8", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void WrongSlip77MasterFailsClosedThroughLiveNativeObservationPath()
	{
		var fixture = LoadFixture();
		byte[] wrongKey = [.. Enumerable.Repeat((byte)0x99, 32)];
		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletNativeFactsObserver.Observe(Epoch, LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 1, Encoding.ASCII.GetBytes(Descriptor), [fixture.Candidate], wrongKey));
		Assert.Contains("status -7", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void BadEpochLengthIsRejectedBeforeNativeLoading()
	{
		var fixture = LoadFixture();
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletNativeFactsObserver.Observe(new byte[31], LiquidWalletFactsWireV1DescriptorNetworkClass.Test, 1, Encoding.ASCII.GetBytes(Descriptor), [fixture.Candidate], Slip77));
	}

	[Fact]
	public void ProductionArtifactIdentityIsPinnedAndVerified()
	{
		Assert.Equal("bd50133a9fbcac5d187757e634c1cc2fc65a10ac", LiquidWalletNativeFactsObserver.PinnedNativeCommit);
		Assert.Equal(1u, LiquidWalletNativeFactsObserver.AbiVersionV1);
		Assert.True(OperatingSystem.IsMacOS());
		LiquidWalletNativeFactsObserver.EnsurePinnedNativeArtifact();
		string path = LiquidWalletNativeFactsObserver.ResolveLibraryPath();
		Assert.True(File.Exists(path));
		Assert.Equal(
			LiquidWalletNativeFactsObserver.MacOsLibrarySha256,
			Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
	}

	private static byte[] BuildFrame(LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource candidate)
	{
		Assert.True(LiquidWalletFactsWireV1StructuralRequestCodec.TryBuildUnpreparedFrame(
			Epoch,
			LiquidWalletFactsWireV1DescriptorNetworkClass.Test,
			1,
			Encoding.ASCII.GetBytes(Descriptor),
			[candidate],
			out LiquidWalletFactsWireV1UnpreparedRequestFrame? frame,
			out LiquidWalletFactsWireErrorCode error), error.ToString());
		using (frame)
		{
			byte[] bytes = new byte[frame!.Length];
			frame.CopyFrameTo(bytes);
			return bytes;
		}
	}

	private static Fixture LoadFixture()
	{
		string root = Path.Combine(AppContext.BaseDirectory, "TestData", "Liquid", "WalletFactsWireV1", "live");
		byte[] transaction = Convert.FromHexString(File.ReadAllText(Path.Combine(root, "candidate.hex")).Trim());
		byte[] previous = Convert.FromHexString(File.ReadAllText(Path.Combine(root, "previous.hex")).Trim());
		return new Fixture(new LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource(transaction, [previous]));
	}

	private static void AssertOwnedOutput(
		LiquidOwnedOutputObservation output,
		byte[] expectedTransactionId,
		byte[] expectedWitnessBinding,
		uint expectedOutputIndex,
		LiquidKeyBranch expectedBranch,
		uint expectedDerivationIndex,
		long expectedValue,
		string expectedScriptPubKeyHex,
		string expectedSpendPublicKeyHex,
		string expectedBlindingPublicKeyHex)
	{
		Assert.Equal(expectedTransactionId, output.GetTransactionIdConsensusBytes());
		Assert.Equal(expectedWitnessBinding, output.GetTransactionWitnessBinding());
		Assert.Equal(expectedOutputIndex, output.OutputIndex);
		Assert.Equal(expectedBranch, output.Branch);
		Assert.Equal(expectedDerivationIndex, output.DerivationIndex);
		Assert.Equal(expectedValue, output.Value);
		Assert.Equal(Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"), output.GetAssetIdConsensusBytes());
		byte[] scriptPubKey = output.GetScriptPubKey();
		Assert.Equal(22, scriptPubKey.Length);
		Assert.Equal(0x00, scriptPubKey[0]);
		Assert.Equal(0x14, scriptPubKey[1]);
		Assert.Equal(Convert.FromHexString(expectedScriptPubKeyHex), scriptPubKey);
		Assert.Equal(33, output.GetSpendPublicKey().Length);
		Assert.Equal(Convert.FromHexString(expectedSpendPublicKeyHex), output.GetSpendPublicKey());
		Assert.Equal(33, output.GetBlindingPublicKey().Length);
		Assert.Equal(Convert.FromHexString(expectedBlindingPublicKeyHex), output.GetBlindingPublicKey());
	}

	private sealed record Fixture(LiquidWalletFactsWireV1StructuralRequestCodec.LiquidWalletFactsWireV1StructuralCandidateSource Candidate);
}
