using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Application;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Application;

public sealed class LiquidWalletOpenAuthorizationTests
{
	[Fact]
	public async Task RejectsOutOfBoundsPasswordAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		Assert.Throws<ArgumentException>(() => client.CreateOpenAuthorization(ReadOnlySpan<char>.Empty));
		Assert.Throws<ArgumentException>(() => client.CreateOpenAuthorization(new string('x', 1025)));
	}

	[Fact]
	public async Task CopiesInputAndExposesNoReadableSecretMemberAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		char[] source = "secret".ToCharArray();
		using LiquidWalletOpenAuthorization authorization = client.CreateOpenAuthorization(source);
		source.AsSpan().Clear();

		char[] buffer = GetBuffer(authorization)!;
		Assert.Equal("secret", new string(buffer));
		Assert.Empty(typeof(LiquidWalletOpenAuthorization).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
		Assert.Empty(typeof(LiquidWalletOpenAuthorization).GetProperties(BindingFlags.Public | BindingFlags.Instance));
		Assert.Empty(typeof(LiquidWalletOpenAuthorization).GetFields(BindingFlags.Public | BindingFlags.Instance));
	}

	[Fact]
	public async Task DisposeIsIdempotentAndZeroizesBufferAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		LiquidWalletOpenAuthorization authorization = client.CreateOpenAuthorization("secret");
		char[] buffer = GetBuffer(authorization)!;

		authorization.Dispose();
		authorization.Dispose();

		Assert.All(buffer, character => Assert.Equal('\0', character));
		Assert.Null(GetBuffer(authorization));
	}

	[Fact]
	public async Task OpenConsumesAndZeroizesBeforeEveryFacadeFailureAsync()
	{
		LiquidWalletApplicationClient client = CreateClient();
		LiquidWalletOpenAuthorization nullRequestAuthorization = client.CreateOpenAuthorization("secret");
		char[] nullRequestBuffer = GetBuffer(nullRequestAuthorization)!;
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			client.OpenAsync(null!, nullRequestAuthorization, CancellationToken.None).AsTask());
		AssertConsumedAndZeroed(nullRequestAuthorization, nullRequestBuffer);

		LiquidWalletOpenAuthorization invalidRequestAuthorization = client.CreateOpenAuthorization("secret");
		char[] invalidRequestBuffer = GetBuffer(invalidRequestAuthorization)!;
		var invalidRequest = new LiquidWalletOpenRequest("alpha", "relative-wallet.json", "local");
		await Assert.ThrowsAsync<InvalidDataException>(() =>
			client.OpenAsync(invalidRequest, invalidRequestAuthorization, CancellationToken.None).AsTask());
		AssertConsumedAndZeroed(invalidRequestAuthorization, invalidRequestBuffer);

		LiquidWalletOpenAuthorization canceledAuthorization = client.CreateOpenAuthorization("secret");
		char[] canceledBuffer = GetBuffer(canceledAuthorization)!;
		using var canceled = new CancellationTokenSource();
		canceled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.OpenAsync(invalidRequest, canceledAuthorization, canceled.Token).AsTask());
		AssertConsumedAndZeroed(canceledAuthorization, canceledBuffer);

		LiquidWalletOpenAuthorization disposedAuthorization = client.CreateOpenAuthorization("secret");
		char[] disposedBuffer = GetBuffer(disposedAuthorization)!;
		await client.DisposeAsync();
		await Assert.ThrowsAsync<ObjectDisposedException>(() =>
			client.OpenAsync(invalidRequest, disposedAuthorization, CancellationToken.None).AsTask());
		AssertConsumedAndZeroed(disposedAuthorization, disposedBuffer);
	}

	[Fact]
	public async Task ConcurrentConsumeHasExactlyOneOwnerAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		using LiquidWalletOpenAuthorization authorization = client.CreateOpenAuthorization("secret");
		using Barrier barrier = new(2);
		Task<char[]?> first = Task.Run(() => TakeAtBarrier(authorization, barrier));
		Task<char[]?> second = Task.Run(() => TakeAtBarrier(authorization, barrier));
		char[]?[] results = await Task.WhenAll(first, second);

		char[] owned = Assert.Single(results, x => x is not null)!;
		Assert.Single(results, x => x is null);
		LiquidWalletOpenAuthorization.ZeroBuffer(owned);
		Assert.All(owned, character => Assert.Equal('\0', character));
	}

	[Fact]
	public async Task ConcurrentConsumeAndDisposeHaveExactlyOneOwnerAsync()
	{
		await using LiquidWalletApplicationClient client = CreateClient();
		LiquidWalletOpenAuthorization authorization = client.CreateOpenAuthorization("secret");
		char[] original = GetBuffer(authorization)!;
		using Barrier barrier = new(2);
		Task<char[]?> consume = Task.Run(() => TakeAtBarrier(authorization, barrier));
		Task dispose = Task.Run(() =>
		{
			barrier.SignalAndWait();
			authorization.Dispose();
		});
		await Task.WhenAll(consume, dispose);

		char[]? consumed = await consume;
		if (consumed is not null)
		{
			Assert.Equal("secret", new string(consumed));
			LiquidWalletOpenAuthorization.ZeroBuffer(consumed);
		}
		Assert.All(original, character => Assert.Equal('\0', character));
	}

	private static char[]? TakeAtBarrier(LiquidWalletOpenAuthorization authorization, Barrier barrier)
	{
		barrier.SignalAndWait();
		try
		{
			return authorization.TakeBuffer();
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private static void AssertConsumedAndZeroed(
		LiquidWalletOpenAuthorization authorization,
		char[] buffer)
	{
		Assert.Null(GetBuffer(authorization));
		Assert.All(buffer, character => Assert.Equal('\0', character));
	}

	private static char[]? GetBuffer(LiquidWalletOpenAuthorization authorization) =>
		(char[]?)typeof(LiquidWalletOpenAuthorization)
			.GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(authorization);

	private static LiquidWalletApplicationClient CreateClient() =>
		LiquidWalletApplicationClient.Create(new(
			System.IO.Path.GetTempPath(),
			System.IO.Path.GetTempPath(),
			"b88244f81daf14b2f47915d430ec41e5402de538020f1e4847e8ddbd6f238e5b"));
}
