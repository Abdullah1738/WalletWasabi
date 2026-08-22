using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Rpc;

public enum ElementsNodeExpectationBindingLevel
{
	SelfReportedExactTupleAndFeeObservationOnly = 0,
}

public sealed class ElementsExpectationBoundNodeObservation
{
	internal ElementsExpectationBoundNodeObservation(
		ElementsNodeExpectation expectation,
		string effectiveFeeAsset,
		ElementsNodeStatus nodeStatus,
		ElementsNodeGenerationObservation generation)
	{
		ArgumentNullException.ThrowIfNull(expectation);
		ArgumentNullException.ThrowIfNull(nodeStatus);
		ArgumentNullException.ThrowIfNull(generation);

		Expectation = expectation.Normalize();
		EffectiveFeeAsset = LiquidAssetId.ParseRpcHex(
			effectiveFeeAsset,
			nameof(effectiveFeeAsset)).CanonicalRpcHex;
		NodeStatus = nodeStatus;
		Generation = generation;
	}

	/// <summary>
	/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (amendment section 7): an observation made without a
	/// fabricated <see cref="ElementsNodeExpectation"/>. The send-execution funding path binds
	/// the effective fee asset and the generation fence and self-observes the node status; it
	/// does not invent a static node-identity expectation, so <see cref="Expectation"/> is
	/// null here.
	/// </summary>
	internal ElementsExpectationBoundNodeObservation(
		string effectiveFeeAsset,
		ElementsNodeStatus nodeStatus,
		ElementsNodeGenerationObservation generation)
	{
		ArgumentNullException.ThrowIfNull(nodeStatus);
		ArgumentNullException.ThrowIfNull(generation);

		Expectation = null;
		EffectiveFeeAsset = LiquidAssetId.ParseRpcHex(
			effectiveFeeAsset,
			nameof(effectiveFeeAsset)).CanonicalRpcHex;
		NodeStatus = nodeStatus;
		Generation = generation;
	}

	public ElementsNodeExpectation? Expectation { get; }
	public string EffectiveFeeAsset { get; }
	public ElementsNodeStatus NodeStatus { get; }
	public ElementsNodeGenerationObservation Generation { get; }
	public ElementsNodeExpectationBindingLevel BindingLevel =>
		ElementsNodeExpectationBindingLevel.SelfReportedExactTupleAndFeeObservationOnly;
	public bool HasExactGenerationFenceObservation => true;
	public bool HasEffectiveFeeAssetObservation => true;
	public bool HasArtifactSourceAttestation => false;
	public bool HasRuntimeQualification => false;
	public bool HasCurrentnessAuthority => false;
	public bool HasReservationAuthority => false;
	public bool HasBroadcastAuthority => false;
}
