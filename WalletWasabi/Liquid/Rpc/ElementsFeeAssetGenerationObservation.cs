using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Rpc;

public sealed record ElementsNodeGenerationObservation
{
	private const string FallbackTipStartupIdSentinel =
		"0000000000000000000000000000000000000000000000000000000000000000";

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

	private ElementsNodeGenerationObservation(
		string startupId,
		ulong chainstateRevision,
		int blocks,
		string bestBlockHash,
		bool allowSentinelStartupId)
	{
		StartupId = allowSentinelStartupId
			? startupId
			: ElementsNodeStatus.RequireHex32(startupId, nameof(startupId));
		ChainstateRevision = chainstateRevision;
		Blocks = ElementsNodeStatus.RequireNonNegative(blocks, nameof(blocks));
		BestBlockHash = ElementsNodeStatus.RequireHex32(bestBlockHash, nameof(bestBlockHash));
	}

	/// <summary>
	/// The fixed fallback observation shape used when the reviewed network manifest declares the
	/// fork-only <c>getnodegeneration</c> RPC absent: an all-zero startup-id sentinel and a
	/// chainstate revision that proxies the observed block height, so the existing fences tolerate
	/// forward-only tip movement (a new block advances the height and therefore the revision) while
	/// still rejecting any rollback (the height and therefore the revision regresses) and any
	/// same-height tip identity change (the revision is unchanged but the best-block hash differs).
	/// Restart detection remains genuinely unavailable without <c>getnodegeneration</c>; the all-zero
	/// startup-id sentinel is retained and never claims otherwise. Never produced for a manifest that
	/// declares the generation API present.
	/// </summary>
	internal static ElementsNodeGenerationObservation CreateFallbackTipObservation(
		int blocks,
		string bestBlockHash) =>
		new(
			FallbackTipStartupIdSentinel,
			(ulong)ElementsNodeStatus.RequireNonNegative(blocks, nameof(blocks)),
			ElementsNodeStatus.RequireNonNegative(blocks, nameof(blocks)),
			ElementsNodeStatus.RequireHex32(bestBlockHash, nameof(bestBlockHash)),
			allowSentinelStartupId: true);

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
