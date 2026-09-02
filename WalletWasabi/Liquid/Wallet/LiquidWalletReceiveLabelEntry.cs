namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// One immutable receive-label binding: the label set attached to a receive
/// address's external (branch-0) derivation index. The index identifies the
/// derived receive address; an index with no label is simply absent from the
/// wallet's label map. Construction validates nothing beyond non-null: the
/// label set is already validated immutable by
/// <see cref="LiquidWalletLabelSet.Create"/>.
/// </summary>
internal sealed class LiquidWalletReceiveLabelEntry
{
	private LiquidWalletReceiveLabelEntry(uint index, LiquidWalletLabelSet labels)
	{
		Index = index;
		Labels = labels;
	}

	public uint Index { get; }
	public LiquidWalletLabelSet Labels { get; }

	public static LiquidWalletReceiveLabelEntry Create(uint index, LiquidWalletLabelSet labels)
	{
		ArgumentNullException.ThrowIfNull(labels);
		return new LiquidWalletReceiveLabelEntry(index, labels);
	}

	public override string ToString() => nameof(LiquidWalletReceiveLabelEntry);
}
