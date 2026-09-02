using System.IO;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

[Collection("Serial unit tests collection")]
public class LiquidWalletReceiveLabelAllocatorTests
{
	private const string PeggedAssetHex = "1111111111111111111111111111111111111111111111111111111111111111";
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(PeggedAssetHex);

	[Fact]
	public void SetLabelsPersistsSetAndClearAcrossReopen()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		string dir = GetWorkDir();
		LiquidWalletLoadSave.Save(dir, "labels", LiquidWalletState.Empty(PeggedAsset), generation: 0, key, context);

		LiquidWalletReceiveLabelAllocation set = LiquidWalletReceiveLabelAllocator.SetLabels(
			dir, "labels", key, context, 4, ["savings", "vault"]);
		Assert.Equal(1ul, set.PersistedGeneration);
		Assert.Single(set.State.GetReceiveLabels());
		Assert.Equal(LiquidWalletLabelSet.Create(["savings", "vault"]), set.State.GetReceiveLabels(4));

		// The label survives a reopen (durable, no process-local state).
		LiquidWalletLoadSaveResult reloaded = LiquidWalletLoadSave.Load(dir, "labels", key, context);
		Assert.Equal(LiquidWalletLabelSet.Create(["savings", "vault"]), reloaded.State!.GetReceiveLabels(4));

		// Clearing with an empty set removes the entry durably.
		LiquidWalletReceiveLabelAllocator.SetLabels(dir, "labels", key, context, 4, []);
		LiquidWalletState afterClear = LiquidWalletLoadSave.Load(dir, "labels", key, context).State!;
		Assert.Empty(afterClear.GetReceiveLabels());
		Assert.Null(afterClear.GetReceiveLabels(4));
	}

	[Fact]
	public void SetLabelsRejectsOutOfRangeIndex()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		string dir = GetWorkDir();
		LiquidWalletLoadSave.Save(dir, "labels-range", LiquidWalletState.Empty(PeggedAsset), generation: 0, key, context);

		// The external receive-index space is capped at 0x7fffffff like the index allocator.
		InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletReceiveLabelAllocator.SetLabels(
				dir, "labels-range", key, context, 0x80000000U, ["x"]));
		Assert.Equal("The Liquid external receive-index space is exhausted.", failure.Message);
	}

	[Fact]
	public void SetLabelsRejectsInvalidLabelSetBeforePersistence()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		string dir = GetWorkDir();
		LiquidWalletLoadSave.Save(dir, "labels-invalid", LiquidWalletState.Empty(PeggedAsset), generation: 0, key, context);
		byte[] retained = File.ReadAllBytes(Path.Combine(dir, "labels-invalid.lwwal"));

		// An over-limit label set is rejected by LiquidWalletLabelSet.Create before any write.
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletReceiveLabelAllocator.SetLabels(
				dir, "labels-invalid", key, context, 0, [new string('x', LiquidWalletLabelSet.MaximumLabelUtf8ByteCount + 1)]));

		// The on-disk bytes are untouched by the rejected write.
		Assert.Equal(retained, File.ReadAllBytes(Path.Combine(dir, "labels-invalid.lwwal")));
		Assert.Empty(LiquidWalletLoadSave.Load(dir, "labels-invalid", key, context).State!.GetReceiveLabels());
	}

	private static string GetWorkDir()
	{
		string dir = WalletWasabi.Tests.Helpers.Common.GetWorkDir();
		Directory.CreateDirectory(dir);
		return dir;
	}
}
