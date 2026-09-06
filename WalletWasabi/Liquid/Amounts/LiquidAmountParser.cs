using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Amounts;

public static class LiquidAmountParser
{
	public static LiquidAmountParseResult Parse(string? text, LiquidAssetMetadata? metadata)
	{
		if (string.IsNullOrEmpty(text))
			return LiquidAmountParseResult.Invalid("Enter an amount.");
		int precision = metadata?.Precision ?? 0;
		int fractionalDigits = 0;
		bool hasDot = false;
		long atomic = 0;
		foreach (char character in text)
		{
			if (character == '.' && !hasDot && precision > 0)
			{
				hasDot = true;
				continue;
			}
			if (character is < '0' or > '9')
				return LiquidAmountParseResult.Invalid(metadata is null
					? "Unknown asset: enter integer atomic units only."
					: "Use digits and a decimal point only; no signs, separators or exponents.");
			if (hasDot && ++fractionalDigits > precision)
				return LiquidAmountParseResult.Invalid($"This asset supports at most {precision} fractional digits.");
			int digit = character - '0';
			if (atomic > (long.MaxValue - digit) / 10)
				return LiquidAmountParseResult.Invalid("The amount is too large.");
			atomic = atomic * 10 + digit;
		}
		if (text[0] == '.' || text[^1] == '.')
			return LiquidAmountParseResult.Invalid("Enter digits before and after the decimal point.");
		for (int i = fractionalDigits; i < precision; i++)
		{
			if (atomic > long.MaxValue / 10) return LiquidAmountParseResult.Invalid("The amount is too large.");
			atomic *= 10;
		}
		return new(true, atomic, null);
	}
}
