namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// A uniform, privacy-redacted failure to frame or unframe an on-disk Liquid
/// wallet persistence file. Thrown only by
/// <see cref="LiquidWalletPersistenceFrame.Encode"/> and
/// <see cref="LiquidWalletPersistenceFrame.Decode"/>; never thrown for file
/// system errors (those surface as <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/>) and never for envelope
/// authentication failures (those surface as
/// <see cref="LiquidWalletReplayProtectionException"/> from the landed
/// <see cref="LiquidWalletReplayProtectedPayload.Open"/> at
/// <see cref="LiquidWalletPersistenceHandoff.Import"/> time).
/// </summary>
internal sealed class LiquidWalletPersistenceFormatException : Exception
{
	public LiquidWalletPersistenceFormatException()
		: base("The Liquid wallet persistence file is invalid.")
	{
	}
}
