using System.Linq;
using WalletWasabi.Liquid.Transactions;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Transactions;

public class LiquidTransactionWitnessBindingTests
{
	[Fact]
	public void PreservesAsymmetricSingleSha256BytesWithoutReversal()
	{
		byte[] digest = Enumerable.Range(0, LiquidTransactionWitnessBinding.ByteLength)
			.Select(index => (byte)(index + 1))
			.ToArray();
		byte[] expected = [.. digest];

		LiquidTransactionWitnessBinding binding = LiquidTransactionWitnessBinding.Create(digest);
		digest.AsSpan().Reverse();

		Assert.Equal(expected, binding.GetBytes());
		Assert.NotEqual(expected.Reverse().ToArray(), binding.GetBytes());
	}

	[Fact]
	public void AcceptsAnAllZeroDigestAsAnOpaqueHashValue()
	{
		byte[] zeroDigest = new byte[LiquidTransactionWitnessBinding.ByteLength];

		LiquidTransactionWitnessBinding binding = LiquidTransactionWitnessBinding.Create(zeroDigest);

		Assert.Equal(zeroDigest, binding.GetBytes());
	}

	[Fact]
	public void RejectsNonExactDigestLengths()
	{
		Assert.Throws<ArgumentException>(() => LiquidTransactionWitnessBinding.Create(new byte[31]));
		Assert.Throws<ArgumentException>(() => LiquidTransactionWitnessBinding.Create(new byte[33]));
	}

	[Fact]
	public void GetterIsDefensiveAndEqualityBindsEveryByte()
	{
		byte[] digest = Enumerable.Range(0, LiquidTransactionWitnessBinding.ByteLength)
			.Select(index => (byte)(0x80 + index))
			.ToArray();
		LiquidTransactionWitnessBinding first = LiquidTransactionWitnessBinding.Create(digest);
		LiquidTransactionWitnessBinding equal = LiquidTransactionWitnessBinding.Create(digest);
		byte[] changedDigest = [.. digest];
		changedDigest[^1] ^= 1;
		LiquidTransactionWitnessBinding changed = LiquidTransactionWitnessBinding.Create(changedDigest);

		byte[] exported = first.GetBytes();
		exported.AsSpan().Clear();

		Assert.Equal(digest, first.GetBytes());
		Assert.Equal(first, equal);
		Assert.Equal(first.GetHashCode(), equal.GetHashCode());
		Assert.NotEqual(first, changed);
		Assert.Equal(nameof(LiquidTransactionWitnessBinding), first.ToString());
		Assert.DoesNotContain(Convert.ToHexString(digest), first.ToString(), StringComparison.OrdinalIgnoreCase);
	}
}
