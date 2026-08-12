using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidWalletTransactionEffectSnapshot
{
	private readonly LiquidWalletTransactionEffect[] _effects;

	internal LiquidWalletTransactionEffectSnapshot(
		LiquidAssetId peggedAssetId,
		ulong revision,
		IReadOnlyList<LiquidWalletTransactionEffect> effects)
		: this(peggedAssetId, revision, CopyEffects(effects))
	{
	}

	private LiquidWalletTransactionEffectSnapshot(
		LiquidAssetId peggedAssetId,
		ulong revision,
		LiquidWalletTransactionEffect[] ownedEffects)
	{
		ArgumentNullException.ThrowIfNull(peggedAssetId);
		ArgumentNullException.ThrowIfNull(ownedEffects);
		for (int index = 0; index < ownedEffects.Length; index++)
		{
			LiquidWalletTransactionEffect effect = ownedEffects[index];
			ArgumentNullException.ThrowIfNull(effect, nameof(ownedEffects));
			if (effect.PeggedAssetId != peggedAssetId)
			{
				throw new ArgumentException(
					"Every transaction effect must use the snapshot pegged-asset context.",
					nameof(ownedEffects));
			}
		}

		PeggedAssetId = peggedAssetId;
		Revision = revision;
		_effects = ownedEffects;
	}

	public LiquidAssetId PeggedAssetId { get; }
	public ulong Revision { get; }

	public IReadOnlyList<LiquidWalletTransactionEffect> GetEffects() =>
		new ReadOnlyCollection<LiquidWalletTransactionEffect>([.. _effects]);

	public override string ToString() => nameof(LiquidWalletTransactionEffectSnapshot);

	/// <summary>
	/// Takes exclusive ownership of a freshly allocated wallet-state result
	/// array. The caller must not retain, access, or mutate the array after this
	/// call.
	/// </summary>
	internal static LiquidWalletTransactionEffectSnapshot TakeOwnershipFromState(
		LiquidAssetId peggedAssetId,
		ulong revision,
		LiquidWalletTransactionEffect[] stateOwnedEffects) =>
		new(peggedAssetId, revision, stateOwnedEffects);

	private static LiquidWalletTransactionEffect[] CopyEffects(
		IReadOnlyList<LiquidWalletTransactionEffect> effects)
	{
		ArgumentNullException.ThrowIfNull(effects);
		var copy = new LiquidWalletTransactionEffect[effects.Count];
		for (int index = 0; index < copy.Length; index++)
		{
			copy[index] = effects[index];
		}
		return copy;
	}
}
