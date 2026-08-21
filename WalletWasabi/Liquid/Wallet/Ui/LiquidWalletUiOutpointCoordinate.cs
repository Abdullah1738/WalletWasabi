namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The immutable, public BIP32 coordinates of one unspent Liquid output's spend
/// key, projected out of the internal wallet state for the signing seam's
/// outpoint locator. <see cref="Account"/> is always <c>0</c> in v1 (the frozen
/// domain has exactly one spend account); <see cref="Change"/> is the external
/// (0) or internal (1) branch; <see cref="Index"/> is the normal descriptor
/// derivation index. This is a pure value projection: it carries no secret, no
/// script, no amount, and no outpoint, and it performs no I/O, no derivation, and
/// no validation.
/// </summary>
public readonly record struct LiquidWalletUiOutpointCoordinate(int Account, int Change, int Index);
