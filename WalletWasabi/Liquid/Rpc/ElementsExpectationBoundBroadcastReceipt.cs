namespace WalletWasabi.Liquid.Rpc;

public enum ElementsBroadcastBindingLevel
{
	SelfReportedExactTupleFeeAndGenerationFencedNodeAcceptanceOnly = 0,
}

/// <summary>
/// Records one node-self-reported transaction acceptance under an exact expectation, fee asset,
/// and unchanged generation fence. This is not confirmation, currentness, propagation,
/// reservation, artifact-source, runtime-qualification, or transaction-id-validation authority.
/// </summary>
public sealed class ElementsExpectationBoundBroadcastReceipt
{
	internal ElementsExpectationBoundBroadcastReceipt(
		ElementsExpectationBoundNodeObservation nodeObservation,
		string acceptedTransactionIdHex)
	{
		ArgumentNullException.ThrowIfNull(nodeObservation);
		NodeObservation = nodeObservation;
		AcceptedTransactionIdHex = ElementsNodeStatus.RequireHex32(
			acceptedTransactionIdHex,
			nameof(acceptedTransactionIdHex));
	}

	public ElementsExpectationBoundNodeObservation NodeObservation { get; }
	public string AcceptedTransactionIdHex { get; }
	public ElementsBroadcastBindingLevel BindingLevel =>
		ElementsBroadcastBindingLevel.SelfReportedExactTupleFeeAndGenerationFencedNodeAcceptanceOnly;
	public bool HasBroadcastAuthority => true;
	public bool HasExactGenerationFenceObservation => true;
	public bool HasEffectiveFeeAssetObservation => true;
	public bool HasConfirmationAuthority => false;
	public bool HasCurrentnessAuthority => false;
	public bool HasReservationAuthority => false;
	public bool HasArtifactSourceAttestation => false;
	public bool HasRuntimeQualification => false;
	public bool HasTransactionIdValidation => false;

	public override string ToString() => nameof(ElementsExpectationBoundBroadcastReceipt);
}