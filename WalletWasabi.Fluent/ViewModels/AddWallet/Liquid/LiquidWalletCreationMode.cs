namespace WalletWasabi.Fluent.ViewModels.AddWallet.Liquid;

/// <summary>
/// The two Liquid onboarding paths that begin at a wallet-name page: creating
/// a fresh wallet (recovery words are generated and shown for backup) or
/// recovering an existing wallet from typed recovery words.
/// </summary>
public enum LiquidWalletCreationMode
{
	CreateNew,
	Recover
}
