using System;
using System.Linq;
using WalletWasabi.Liquid.Application;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Client;

public sealed class LiquidPreRefreshOwnerTests
{
	private const string TransactionIdOne = "0101010101010101010101010101010101010101010101010101010101010101";
	private const string TransactionIdTwo = "0202020202020202020202020202020202020202020202020202020202020202";

	[Fact]
	public void BuilderPreservesIndependentOrderAndCreatesNullBlockHashes()
	{
		var requests = LiquidAuthenticatedWalletSession.BuildPreRefreshRawTransactionRequests(
			[TransactionIdTwo, TransactionIdOne]);

		Assert.Equal([TransactionIdTwo, TransactionIdOne], requests.Select(request => request.TransactionId));
		Assert.All(requests, request => Assert.Null(request.BlockHash));
	}

	[Fact]
	public void BuilderRejectsNullEmptyZeroDuplicateAndOverOneHundred()
	{
		Assert.Throws<ArgumentNullException>(() => LiquidAuthenticatedWalletSession.BuildPreRefreshRawTransactionRequests(null!));
		Assert.Throws<ArgumentOutOfRangeException>(() => LiquidAuthenticatedWalletSession.BuildPreRefreshRawTransactionRequests([]));
		Assert.Throws<ArgumentException>(() => LiquidAuthenticatedWalletSession.BuildPreRefreshRawTransactionRequests([new string('0', 64)]));
		Assert.Throws<ArgumentException>(() => LiquidAuthenticatedWalletSession.BuildPreRefreshRawTransactionRequests([TransactionIdOne, TransactionIdOne]));
		Assert.Throws<ArgumentOutOfRangeException>(() => LiquidAuthenticatedWalletSession.BuildPreRefreshRawTransactionRequests(
			Enumerable.Range(1, 101).Select(value => value.ToString("x64")).ToArray()));
	}
}
