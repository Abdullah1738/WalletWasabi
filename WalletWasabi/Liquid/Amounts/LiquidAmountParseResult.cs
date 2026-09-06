namespace WalletWasabi.Liquid.Amounts;

public readonly record struct LiquidAmountParseResult(bool Success, long AtomicUnits, string? Error)
{
	public static LiquidAmountParseResult Invalid(string error) => new(false, 0, error);
}
