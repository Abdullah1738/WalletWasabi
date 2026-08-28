namespace WalletWasabi.Liquid.Application;

public sealed record LiquidWalletOpenRequest(
	string CanonicalWalletId,
	string CanonicalWalletFilePath,
	string RuntimeProfileName);
