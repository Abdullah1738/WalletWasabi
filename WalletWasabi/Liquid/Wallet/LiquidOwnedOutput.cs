using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidOwnedOutput : IEquatable<LiquidOwnedOutput>
{
	private readonly byte[] _scriptPubKey;

	private LiquidOwnedOutput(
		LiquidOutPoint outPoint,
		byte[] scriptPubKey,
		LiquidAssetAmount amount,
		LiquidSpendKeyReference spendKey)
	{
		OutPoint = outPoint;
		_scriptPubKey = scriptPubKey;
		Amount = amount;
		SpendKey = spendKey;
	}

	public LiquidOutPoint OutPoint { get; }
	public LiquidAssetAmount Amount { get; }
	public LiquidSpendKeyReference SpendKey { get; }

	public static LiquidOwnedOutput Create(
		LiquidOutPoint outPoint,
		ReadOnlySpan<byte> scriptPubKey,
		LiquidAssetAmount amount,
		LiquidSpendKeyReference spendKey)
	{
		ArgumentNullException.ThrowIfNull(outPoint);
		ArgumentNullException.ThrowIfNull(amount);
		ArgumentNullException.ThrowIfNull(spendKey);
		if (amount.IsZero)
		{
			throw new ArgumentOutOfRangeException(nameof(amount), "A positive spendable Liquid amount is required.");
		}
		if (!spendKey.MatchesScriptPubKey(scriptPubKey))
		{
			throw new ArgumentException("The Liquid output script does not match its spend-key reference.", nameof(scriptPubKey));
		}

		return new LiquidOwnedOutput(outPoint, scriptPubKey.ToArray(), amount, spendKey);
	}

	public byte[] GetScriptPubKey() => [.. _scriptPubKey];

	public bool Equals(LiquidOwnedOutput? other) =>
		other is not null &&
		OutPoint == other.OutPoint &&
		Amount == other.Amount &&
		SpendKey.Equals(other.SpendKey) &&
		_scriptPubKey.AsSpan().SequenceEqual(other._scriptPubKey);

	public override bool Equals(object? obj) => Equals(obj as LiquidOwnedOutput);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(OutPoint);
		hash.Add(Amount);
		hash.Add(SpendKey);
		foreach (byte value in _scriptPubKey)
		{
			hash.Add(value);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidOwnedOutput);
}
