using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NBitcoin;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Tests;
using WalletWasabi.Tests.Helpers;
using BitcoinNetwork = NBitcoin.Network;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public class LiquidReceiveMaterialTests
{
	[Fact]
	public void Slip77PublicKeyMatchesIndependentSecp256k1Derivation()
	{
		byte[] master = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
		byte[] script = Convert.FromHexString("0014751E76E8199196D454941C45D1B3A323F1433BD6");
		byte[] scalar = HMACSHA256.HashData(master, script);
		byte[] expected;
		using (var key = new Key(scalar))
		{
			expected = key.PubKey.ToBytes();
		}

		byte[] actual = LiquidSlip77PublicKey.Derive(master, script);

		Assert.Equal(expected, actual);
		Assert.Equal(33, actual.Length);
	}

	[Fact]
	public void FirstOpenInitializesPersistsAndReopensEmptyState()
	{
		using TemporaryDirectory directory = new();
		const string walletName = "liquid-wallet";
		byte[] key = Enumerable.Repeat((byte)0x17, 32).ToArray();
		byte[] context = Enumerable.Repeat((byte)0x28, 32).ToArray();
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		string filePath = Path.Combine(directory.Path, walletName + ".lwwal");
		Assert.False(File.Exists(filePath));

		LiquidWalletExternalIndexAllocation first = LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
			directory.Path, walletName, key, context, peggedAsset);

		// Genesis seals the empty state at generation 0 with a zero external-index
		// high-water; the open is a pure peek, so it presents next-receive index 0
		// WITHOUT advancing the high-water or persisting a new generation.
		Assert.Equal(0UL, first.Index);
		Assert.Equal(0UL, first.PersistedGeneration);
		Assert.Equal(0UL, first.PersistedExternalIndexHighWater);
		Assert.True(File.Exists(filePath));

		LiquidWalletLoadSaveResult persisted = LiquidWalletLoadSave.Load(directory.Path, walletName, key, context);
		Assert.Equal(0UL, persisted.State!.Revision);
		Assert.Equal(0UL, persisted.Generation);
		Assert.Equal(0UL, persisted.ExternalIndexHighWater);
		Assert.Equal(
			peggedAsset.CanonicalRpcHex,
			persisted.State.PeggedAssetId.CanonicalRpcHex);
		Assert.Equal(0, persisted.State.AppliedTransactionCount);

		// A reopen performs no re-initialization and PEEKS: it presents the same
		// next-receive index and advances neither the persisted generation nor the
		// external-index high-water.
		LiquidWalletExternalIndexAllocation second = LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
			directory.Path, walletName, key, context, peggedAsset);
		Assert.Equal(first.Index, second.Index);
		Assert.Equal(first.PersistedGeneration, second.PersistedGeneration);
		Assert.Equal(first.PersistedExternalIndexHighWater, second.PersistedExternalIndexHighWater);
	}

	[Fact]
	public void FirstOpenPreservesAnExistingHealthyState()
	{
		using TemporaryDirectory directory = new();
		const string walletName = "liquid-wallet";
		byte[] key = Enumerable.Repeat((byte)0x39, 32).ToArray();
		byte[] context = Enumerable.Repeat((byte)0x4a, 32).ToArray();
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset);
		_ = LiquidWalletLoadSave.Save(directory.Path, walletName, state, generation: 9, key, context);

		LiquidWalletExternalIndexAllocation allocation = LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
			directory.Path, walletName, key, context, peggedAsset);

		Assert.Equal(0UL, allocation.Index);
		Assert.Equal(0UL, allocation.PersistedExternalIndexHighWater);
		Assert.Equal(9UL, allocation.PersistedGeneration);
	}

	[Theory]
	[InlineData("corrupt-frame")]
	[InlineData("wrong-key")]
	[InlineData("orphaned-new")]
	[InlineData("orphaned-old")]
	[InlineData("main-and-old")]
	public void FirstOpenNeverConvertsPresentStateToEmpty(string scenario)
	{
		using TemporaryDirectory directory = new();
		const string walletName = "liquid-wallet";
		byte[] key = Enumerable.Repeat((byte)0x5b, 32).ToArray();
		byte[] context = Enumerable.Repeat((byte)0x6c, 32).ToArray();
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		string filePath = Path.Combine(directory.Path, walletName + ".lwwal");

		switch (scenario)
		{
			case "corrupt-frame":
				// A present file that fails framing must fail closed, never reset.
				File.WriteAllBytes(filePath, [0xde, 0xad, 0xbe, 0xef]);
				Assert.Throws<LiquidWalletPersistenceFormatException>(() =>
					LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
						directory.Path, walletName, key, context, peggedAsset));
				Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, File.ReadAllBytes(filePath));
				break;
			case "wrong-key":
				// A present sealed state under another key fails the replay-protection
				// fence; it must never be replaced by an empty state.
				{
					byte[] otherKey = Enumerable.Repeat((byte)0x7d, 32).ToArray();
					_ = LiquidWalletLoadSave.Save(directory.Path, walletName, LiquidWalletState.Empty(peggedAsset), generation: 4, otherKey, context);
					Assert.Throws<LiquidWalletReplayProtectionException>(() =>
						LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
							directory.Path, walletName, key, context, peggedAsset));
					Assert.Equal(4UL, LiquidWalletLoadSave.Load(directory.Path, walletName, otherKey, context).Generation);
				}
				break;
			case "orphaned-new":
				// A partially written state (only .new present) counts as present: no
				// initialization, the landed read fails closed.
				File.WriteAllBytes(filePath + ".new", [0x01, 0x02]);
				Assert.Throws<InvalidOperationException>(() =>
					LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
						directory.Path, walletName, key, context, peggedAsset));
				Assert.False(File.Exists(filePath));
				Assert.True(File.Exists(filePath + ".new"));
				break;
			case "orphaned-old":
				File.WriteAllBytes(filePath + ".old", [0x03, 0x04]);
				Assert.Throws<InvalidOperationException>(() =>
					LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
						directory.Path, walletName, key, context, peggedAsset));
				Assert.False(File.Exists(filePath));
				Assert.True(File.Exists(filePath + ".old"));
				break;
			case "main-and-old":
				// The main/.old conflict resolves to the main file via the landed SafeFile
				// read path; the healthy main state is loaded, never reset.
				_ = LiquidWalletLoadSave.Save(directory.Path, walletName, LiquidWalletState.Empty(peggedAsset), generation: 6, key, context);
				File.WriteAllBytes(filePath + ".old", [0x05, 0x06]);
				{
					LiquidWalletExternalIndexAllocation allocation = LiquidWalletExternalIndexAllocator.AllocateWithFirstOpenInitialization(
						directory.Path, walletName, key, context, peggedAsset);
					Assert.Equal(0UL, allocation.Index);
					Assert.Equal(0UL, allocation.PersistedExternalIndexHighWater);
					Assert.Equal(6UL, allocation.PersistedGeneration);
				}
				break;
		}
	}

	[Fact]
	public void ExternalIndexHighWaterSurvivesOlderStateSaveAndFreshReopen()
	{
		using TemporaryDirectory directory = new();
		const string walletName = "liquid-wallet";
		byte[] key = Enumerable.Repeat((byte)0x31, 32).ToArray();
		byte[] context = Enumerable.Repeat((byte)0x52, 32).ToArray();
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset);
		_ = LiquidWalletLoadSave.Save(directory.Path, walletName, state, generation: 0, key, context);

		LiquidWalletExternalIndexAllocation first = LiquidWalletExternalIndexAllocator.Allocate(
			directory.Path, walletName, key, context);
		LiquidWalletExternalIndexAllocation second = LiquidWalletExternalIndexAllocator.Allocate(
			directory.Path, walletName, key, context);

		Assert.Equal(0UL, first.Index);
		Assert.Equal(1UL, first.PersistedGeneration);
		Assert.Equal(1UL, second.Index);
		Assert.Equal(2UL, second.PersistedGeneration);
		Assert.Equal(first.StateRevision, second.StateRevision);
		LiquidWalletLoadSaveResult reopened = LiquidWalletLoadSave.Load(directory.Path, walletName, key, context);
		Assert.Equal(2UL, reopened.Generation);
		Assert.Equal(2UL, reopened.ExternalIndexHighWater);
		Assert.Equal(state.Revision, reopened.Revision);

		Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletLoadSave.SaveWithExternalIndexHighWater(
				directory.Path,
				walletName,
				state,
				generation: 3,
				externalIndexHighWater: 1,
				expectedGeneration: reopened.Generation,
				key,
				context));

		// A fenced state save may replace the replay snapshot, but it retains the
		// authenticated receive-index high-water. The next allocation deliberately has no
		// process-local allocator state to consult.
		_ = LiquidWalletLoadSave.SaveWithExternalIndexHighWater(
			directory.Path,
			walletName,
			state,
			generation: 3,
			externalIndexHighWater: reopened.ExternalIndexHighWater,
			expectedGeneration: reopened.Generation,
			key,
			context);
		LiquidWalletExternalIndexAllocation afterReopen = LiquidWalletExternalIndexAllocator.Allocate(
			directory.Path, walletName, key, context);
		Assert.Equal(2UL, afterReopen.Index);
		Assert.Equal(3UL, afterReopen.PersistedExternalIndexHighWater);
	}

	[Fact]
	public void GenericSavePreservesPersistedExternalIndexHighWater()
	{
		using TemporaryDirectory directory = new();
		const string walletName = "liquid-wallet";
		byte[] key = Enumerable.Repeat((byte)0x63, 32).ToArray();
		byte[] context = Enumerable.Repeat((byte)0x74, 32).ToArray();
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset);
		_ = LiquidWalletLoadSave.Save(directory.Path, walletName, state, generation: 0, key, context);

		// Allocate (issue) one index without observing it. The persisted high-water is 1.
		LiquidWalletExternalIndexAllocation allocated = LiquidWalletExternalIndexAllocator.Allocate(
			directory.Path, walletName, key, context);
		Assert.Equal(0UL, allocated.Index);
		Assert.Equal(1UL, allocated.PersistedExternalIndexHighWater);

		// A plain state save must carry the on-disk high-water forward, not reset it to zero.
		_ = LiquidWalletLoadSave.Save(directory.Path, walletName, state, generation: 2, key, context);
		LiquidWalletLoadSaveResult reopened = LiquidWalletLoadSave.Load(directory.Path, walletName, key, context);
		Assert.Equal(2UL, reopened.Generation);
		Assert.Equal(1UL, reopened.ExternalIndexHighWater);

		// The issued-but-unused index must not be reissued after the generic save.
		LiquidWalletExternalIndexAllocation afterGenericSave = LiquidWalletExternalIndexAllocator.Allocate(
			directory.Path, walletName, key, context);
		Assert.Equal(1UL, afterGenericSave.Index);
	}

	[Fact]
	public void IssuedIndexIsNotReissuedAfterOlderStateRollback()
	{
		using TemporaryDirectory directory = new();
		const string walletName = "liquid-wallet";
		byte[] key = Enumerable.Repeat((byte)0x85, 32).ToArray();
		byte[] context = Enumerable.Repeat((byte)0x96, 32).ToArray();
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		LiquidWalletState olderState = LiquidWalletState.Empty(peggedAsset);
		_ = LiquidWalletLoadSave.Save(directory.Path, walletName, olderState, generation: 0, key, context);

		// Issue index 0 with no output observed against it. Persisted high-water becomes 1.
		LiquidWalletExternalIndexAllocation issued = LiquidWalletExternalIndexAllocator.Allocate(
			directory.Path, walletName, key, context);
		Assert.Equal(0UL, issued.Index);
		Assert.Equal(1UL, issued.PersistedExternalIndexHighWater);

		// Advance the retained state: an owned output is observed at the issued index,
		// pushing the state to revision 1. This is the newer state that a crash/rollback
		// would discard in favour of the retained older one.
		LiquidSpendKeyReference externalKey = LiquidSpendKeyReference.Create(
			Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
			LiquidKeyBranch.External,
			0);
		LiquidOwnedOutput received = LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(LiquidTransactionId.ParseRpcHex(new string('b', 64)), 0),
			externalKey.GetScriptPubKey(),
			LiquidAssetAmount.Create(peggedAsset, peggedAsset, 50_000),
			externalKey);
		LiquidWalletState newerState = olderState
			.Apply(0, LiquidWalletTransactionDelta.Create(LiquidTransactionId.ParseRpcHex(new string('b', 64)), [], [received]));
		Assert.Equal(1UL, newerState.Revision);
		_ = LiquidWalletLoadSave.Save(directory.Path, walletName, newerState, generation: 2, key, context);
		Assert.Equal(2UL, LiquidWalletLoadSave.Load(directory.Path, walletName, key, context).Generation);

		// Roll back: persist the retained older (revision-0) state over the newer one under the
		// generation fence. The durable high-water must still be carried forward, not reset.
		_ = LiquidWalletLoadSave.Save(directory.Path, walletName, olderState, generation: 3, key, context);
		LiquidWalletLoadSaveResult reopened = LiquidWalletLoadSave.Load(directory.Path, walletName, key, context);
		Assert.Equal(3UL, reopened.Generation);
		Assert.Equal(0UL, reopened.State!.Revision);
		Assert.Equal(1UL, reopened.ExternalIndexHighWater);

		// Reopening allocates from the durable high-water: index 0 is not reissued.
		LiquidWalletExternalIndexAllocation next = LiquidWalletExternalIndexAllocator.Allocate(
			directory.Path, walletName, key, context);
		Assert.Equal(1UL, next.Index);
	}

	[Fact]
	public void LegacyPayloadVersionOneOpensWithZeroExternalIndexHighWater()
	{
		using TemporaryDirectory directory = new();
		const string walletName = "liquid-wallet";
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		LiquidAssetId peggedAsset = LiquidAssetId.ParseRpcHex(ElementsPublicNetworkManifest.LiquidTestnet.PeggedAssetId);
		LiquidWalletState state = LiquidWalletState.Empty(peggedAsset);
		const ulong generation = 7;

		// Construct a payload-v1 envelope byte-exactly: the inner prefix is generation + canonical
		// length with no trailing external-index high-water, sealed under the v1 version header.
		byte[] envelope = CreateLegacyPayloadV1Envelope(state.ExportReplaySnapshot(), generation, key, context);
		// Wrap the sealed envelope in the versioned on-disk persistence frame (WLWALFMT header).
		byte[] framed = new byte[16 + envelope.Length];
		"WLWALFMT"u8.CopyTo(framed);
		BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(framed.AsSpan(10), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(12), (uint)envelope.Length);
		envelope.CopyTo(framed.AsSpan(16));
		string filePath = Path.Combine(directory.Path, walletName + ".lwwal");
		File.WriteAllBytes(filePath, framed);

		LiquidWalletLoadSaveResult loaded = LiquidWalletLoadSave.Load(directory.Path, walletName, key, context);

		Assert.Equal(generation, loaded.Generation);
		Assert.Equal(0UL, loaded.ExternalIndexHighWater);
		Assert.Equal(state.Revision, loaded.Revision);
	}

	private static byte[] CreateLegacyPayloadV1Envelope(
		LiquidWalletReplaySnapshot snapshot,
		ulong generation,
		byte[] key,
		byte[] context)
	{
		const int headerLength = 48;
		const int paddingBucketLength = 4_096;
		const ushort envelopeVersion = 1;
		const ushort legacyPayloadVersion = 1;
		const ushort aes256GcmAlgorithm = 1;
		const int innerPrefixLength = sizeof(ulong) + sizeof(uint);

		byte[] canonical = LiquidWalletReplayCodec.Encode(snapshot, includeReceiveLabels: false);
		int innerLength = checked(innerPrefixLength + canonical.Length);
		int paddedLength = checked(((innerLength + paddingBucketLength - 1) / paddingBucketLength) * paddingBucketLength);

		byte[] plaintext = new byte[paddedLength];
		byte[] envelope = new byte[checked(headerLength + paddedLength + LiquidWalletReplayProtectedPayload.TagLength)];
		byte[] associatedData = new byte[headerLength + context.Length];
		try
		{
			BinaryPrimitives.WriteUInt64LittleEndian(plaintext, generation);
			BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(sizeof(ulong)), (uint)canonical.Length);
			canonical.CopyTo(plaintext.AsSpan(innerPrefixLength));
			RandomNumberGenerator.Fill(plaintext.AsSpan(innerLength));

			Span<byte> header = envelope.AsSpan(0, headerLength);
			"WLRPENV1"u8.CopyTo(header);
			BinaryPrimitives.WriteUInt16LittleEndian(header[8..], envelopeVersion);
			BinaryPrimitives.WriteUInt16LittleEndian(header[10..], legacyPayloadVersion);
			BinaryPrimitives.WriteUInt16LittleEndian(header[12..], aes256GcmAlgorithm);
			BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 0);
			BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)paddedLength);
			BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)paddedLength);
			BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0);
			RandomNumberGenerator.Fill(header[32..44]);

			header.CopyTo(associatedData);
			context.CopyTo(associatedData.AsSpan(headerLength));
			using var aes = new AesGcm(key, LiquidWalletReplayProtectedPayload.TagLength);
			aes.Encrypt(
				header[32..44],
				plaintext,
				envelope.AsSpan(headerLength, paddedLength),
				envelope.AsSpan(headerLength + paddedLength, LiquidWalletReplayProtectedPayload.TagLength),
				associatedData);
			return envelope;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
			CryptographicOperations.ZeroMemory(canonical);
			CryptographicOperations.ZeroMemory(associatedData);
		}
	}

	[Fact]
	public void ReceiveMaterialDefensivelyCopiesPublicBytes()
	{
		byte[] script = [0x00, 0x14, 0x01];
		byte[] blinding = new byte[33];
		blinding[0] = 0x02;
		var material = new LiquidWalletUiReceiveMaterial(script, blinding);
		script[0] = 0xff;
		blinding[0] = 0xff;
		byte[] exportedScript = material.NextReceiveScriptPubKey;
		byte[] exportedBlinding = material.NextReceiveBlindingPublicKey;
		exportedScript[0] = 0xff;
		exportedBlinding[0] = 0xff;

		Assert.Equal(0x00, material.NextReceiveScriptPubKey[0]);
		Assert.Equal(0x02, material.NextReceiveBlindingPublicKey[0]);
	}

	[Fact]
	public void DescriptorAndReceiveScriptUseTheLiquidAccountExternalBranch()
	{
		using Key rootKey = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
		ExtKey master = new(rootKey, Enumerable.Repeat((byte)0x24, 32).ToArray());
		LiquidWalletReceiveDerivation derivation = LiquidWalletReceiveDerivation.Create(master, BitcoinNetwork.TestNet, account: 0, externalIndex: 7);
		ExtKey account = master.Derive(new KeyPath("2089617494h/1984574463h/0h"));
		byte[] expectedScript = account.Neuter().Derive(0).Derive(7).PubKey.WitHash.ScriptPubKey.ToBytes();

		Assert.Equal(expectedScript, derivation.ScriptPubKey);
		string descriptorBody = $"elwpkh({account.Neuter().ToString(BitcoinNetwork.TestNet)}/<0;1>/*)";
		Assert.Equal(descriptorBody + "#" + derivation.Descriptor[^8..], derivation.Descriptor);
		Assert.Equal(8, derivation.Descriptor.Length - descriptorBody.Length - 1);
		Assert.Equal(7UL, derivation.LastIndex);
	}

	private sealed class TemporaryDirectory : IDisposable
	{
		internal TemporaryDirectory()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "liquid-receive-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		internal string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}
}
