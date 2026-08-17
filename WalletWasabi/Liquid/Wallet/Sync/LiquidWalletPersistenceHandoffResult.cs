namespace WalletWasabi.Liquid.Wallet.Sync;

/// <summary>
/// The immutable outcome of one wallet persistence handoff. On
/// <see cref="LiquidWalletPersistenceHandoff.Export"/>, <see cref="Envelope"/>
/// is the sealed in-memory payload the caller hands to its own storage writer
/// and <see cref="State"/> is null; on
/// <see cref="LiquidWalletPersistenceHandoff.Import"/>, <see cref="State"/> is
/// the restored wallet state and <see cref="Envelope"/> is null.
/// <see cref="Revision"/> is the snapshot's revision; <see cref="Generation"/>
/// is the caller-defined envelope metadata and is explicitly not an
/// anti-rollback or freshness claim. Construction is by the handoff only; the
/// type carries no chain, confirmation-source, currentness, persistence, or
/// broadcast authority.
/// </summary>
internal sealed class LiquidWalletPersistenceHandoffResult
{
	private LiquidWalletPersistenceHandoffResult(
		LiquidWalletReplayProtectedPayload? envelope,
		ulong revision,
		ulong generation,
		LiquidWalletState? state)
	{
		Envelope = envelope;
		Revision = revision;
		Generation = generation;
		State = state;
	}

	public LiquidWalletReplayProtectedPayload? Envelope { get; }
	public ulong Revision { get; }
	public ulong Generation { get; }
	public LiquidWalletState? State { get; }

	internal static LiquidWalletPersistenceHandoffResult Create(
		LiquidWalletReplayProtectedPayload envelope,
		ulong revision,
		ulong generation)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		return new LiquidWalletPersistenceHandoffResult(envelope, revision, generation, state: null);
	}

	internal static LiquidWalletPersistenceHandoffResult Create(
		LiquidWalletReplayProtectedPayload? envelope,
		ulong revision,
		ulong generation,
		LiquidWalletState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return new LiquidWalletPersistenceHandoffResult(envelope, revision, generation, state);
	}

	public override string ToString() => nameof(LiquidWalletPersistenceHandoffResult);
}
