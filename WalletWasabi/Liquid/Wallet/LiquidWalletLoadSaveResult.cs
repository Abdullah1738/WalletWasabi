using WalletWasabi.Liquid.Wallet.Sync;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// The immutable outcome of one wallet load/save wiring operation. On
/// <see cref="LiquidWalletLoadSave.Load"/>, <see cref="State"/> is the
/// restored wallet state; on <see cref="LiquidWalletLoadSave.Save"/>,
/// <see cref="State"/> is null. <see cref="Revision"/> is the snapshot's
/// revision; <see cref="Generation"/> is the caller-defined envelope metadata
/// from export time and is explicitly not an anti-rollback or freshness
/// claim. Construction is by the wiring only; the type carries no chain,
/// confirmation-source, currentness, persistence, or broadcast authority.
/// </summary>
internal sealed class LiquidWalletLoadSaveResult
{
	private LiquidWalletLoadSaveResult(
		LiquidWalletState? state,
		ulong revision,
		ulong generation)
	{
		State = state;
		Revision = revision;
		Generation = generation;
	}

	public LiquidWalletState? State { get; }
	public ulong Revision { get; }
	public ulong Generation { get; }

	internal static LiquidWalletLoadSaveResult CreateLoaded(
		LiquidWalletState state,
		ulong revision,
		ulong generation)
	{
		ArgumentNullException.ThrowIfNull(state);
		return new LiquidWalletLoadSaveResult(state, revision, generation);
	}

	internal static LiquidWalletLoadSaveResult CreateSaved(
		ulong revision,
		ulong generation) =>
		new(state: null, revision, generation);

	public override string ToString() => nameof(LiquidWalletLoadSaveResult);
}
