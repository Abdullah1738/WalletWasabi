using System;
using System.IO;
using System.Linq;
using System.Reflection;
using WalletWasabi.Liquid.Wallet.Ui;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (native txid prerequisite, managed binding half): the
/// production binding to the native <c>wln_wlpq_transaction_id_v1</c> read-only export. The
/// pinned cdylib already exports the symbol alongside <c>wln_wlpq_sign_finalize_v1</c>; this
/// matrix proves the managed binding resolves that second export through the same
/// hash-pinned load discipline and projects a transaction's finalized id into a 64-byte
/// lowercase ASCII RPC/display-order buffer, fail-closed on any native non-OK status.
/// </summary>
[Collection("Serial unit tests collection")]
public class LiquidWalletNativeSigningBindingTransactionIdTests
{
	private static string FixtureRoot => Path.Combine(
		AppContext.BaseDirectory,
		"TestData",
		"Liquid",
		"OrdinaryWalletPlanWireV1",
		"signable");

	private static byte[] ReadFieldBytes(string name) =>
		Convert.FromHexString(File.ReadAllText(Path.Combine(FixtureRoot, name + ".txt")).Trim());

	// The export must be resolvable from the pinned artifact and the helper must compute the
	// exact RPC/display-order id of the committed ground-truth signed transaction. The
	// native-reported txid for that transaction is committed as signed_txid (consensus byte
	// order); the RPC/display order is its byte reversal.
	[Fact]
	public void TryGetTransactionIdReturnsTheRpcDisplayOrderId()
	{
		byte[] signedTransaction = ReadFieldBytes("signed_tx");
		byte[] expectedTxidConsensus = ReadFieldBytes("signed_txid");
		Assert.Equal(32, expectedTxidConsensus.Length);
		byte[] expectedRpcOrder = expectedTxidConsensus.Reverse().ToArray();
		string expectedHex = Convert.ToHexStringLower(expectedRpcOrder);

		bool succeeded = LiquidWalletNativeSigningBinding.TryGetTransactionId(
			signedTransaction,
			out byte[] txidHex64);

		Assert.True(succeeded);
		Assert.NotNull(txidHex64);
		Assert.Equal(64, txidHex64.Length);
		string actualHex = System.Text.Encoding.ASCII.GetString(txidHex64);
		Assert.Equal(expectedHex, actualHex);
	}

	// Fail-closed: a null or empty transaction yields false with no populated id, and the call
	// never throws. The helper is read-only (it decodes and hashes, it does not validate the
	// spend); its fail-closed surface is the empty/null guard plus the native non-OK status
	// propagated as false. The id commits to the exact serialized bytes only up to the
	// transaction's decodability and its non-witness serialization — a witness-only mutation is
	// invisible to the txid, which is the consensus-correct behavior.
	[Fact]
	public void TryGetTransactionIdFailsClosed()
	{
		Assert.False(LiquidWalletNativeSigningBinding.TryGetTransactionId([], out byte[] emptyResult));
		Assert.Empty(emptyResult);
		Assert.False(LiquidWalletNativeSigningBinding.TryGetTransactionId(null!, out byte[] nullResult));
		Assert.Empty(nullResult);

		// A truncated (undecodable) transaction fails closed rather than producing a partial id.
		byte[] signedTransaction = ReadFieldBytes("signed_tx");
		byte[] truncated = signedTransaction[..(signedTransaction.Length / 2)];
		Assert.False(LiquidWalletNativeSigningBinding.TryGetTransactionId(truncated, out _));
	}

	// The binding exposes exactly the frozen surface: the new helper is internal and the public
	// method set is unchanged (Create/TrySignAndFinalize live on the signer, not the binding).
	[Fact]
	public void TransactionIdHelperIsInternalAndAddsNoPublicSurface()
	{
		MethodInfo? helper = typeof(LiquidWalletNativeSigningBinding).GetMethod(
			"TryGetTransactionId",
			BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(helper);
		Assert.True(helper!.IsAssembly || helper.IsFamily || helper.IsFamilyOrAssembly);

		Assert.Empty(
			typeof(LiquidWalletNativeSigningBinding).GetMethods(
				BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
	}
}
