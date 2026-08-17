using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Rpc;

public sealed record ElementsNodeGenerationObservation
{
	internal ElementsNodeGenerationObservation(
		string startupId,
		ulong chainstateRevision,
		int blocks,
		string bestBlockHash)
	{
		StartupId = ElementsNodeStatus.RequireHex32(startupId, nameof(startupId));
		ChainstateRevision = chainstateRevision;
		Blocks = ElementsNodeStatus.RequireNonNegative(blocks, nameof(blocks));
		BestBlockHash = ElementsNodeStatus.RequireHex32(bestBlockHash, nameof(bestBlockHash));
	}

	public string StartupId { get; }
	public ulong ChainstateRevision { get; }
	public int Blocks { get; }
	public string BestBlockHash { get; }
}

public sealed record ElementsFeeAssetGenerationObservation
{
	internal ElementsFeeAssetGenerationObservation(
		LiquidAssetId peggedAsset,
		LiquidAssetId effectiveFeeAsset,
		ElementsNodeGenerationObservation generationBefore,
		ElementsNodeGenerationObservation generationAfter)
	{
		ArgumentNullException.ThrowIfNull(peggedAsset);
		ArgumentNullException.ThrowIfNull(effectiveFeeAsset);
		ArgumentNullException.ThrowIfNull(generationBefore);
		ArgumentNullException.ThrowIfNull(generationAfter);
		PeggedAsset = peggedAsset.CanonicalRpcHex;
		EffectiveFeeAsset = effectiveFeeAsset.CanonicalRpcHex;
		GenerationBefore = generationBefore;
		GenerationAfter = generationAfter;
	}

	public string PeggedAsset { get; }
	public string EffectiveFeeAsset { get; }
	public ElementsNodeGenerationObservation GenerationBefore { get; }
	public ElementsNodeGenerationObservation GenerationAfter { get; }
	public bool UsesPeggedAssetForFees => StringComparer.Ordinal.Equals(PeggedAsset, EffectiveFeeAsset);
	public bool ChainstateChangedDuringObservation =>
		GenerationBefore.ChainstateRevision != GenerationAfter.ChainstateRevision;
}
