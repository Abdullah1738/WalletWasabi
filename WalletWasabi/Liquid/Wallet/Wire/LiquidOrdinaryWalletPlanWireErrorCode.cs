namespace WalletWasabi.Liquid.Wallet.Wire;

internal enum LiquidOrdinaryWalletPlanWireErrorCode : uint
{
	None = 0,
	InvalidArgument = 1,
	VersionMismatch = 2,
	InvalidEncoding = 3,
	LimitExceeded = 4,
	SourceBindingMismatch = 5,
	ContextRejected = 6,
	PlanRejected = 7,
	FundingRejected = 8,
}

internal static class LiquidOrdinaryWalletPlanWireErrorCodeExtensions
{
	internal static string GetMessage(this LiquidOrdinaryWalletPlanWireErrorCode errorCode) =>
		errorCode switch
		{
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument =>
				"ordinary wallet plan wire argument is invalid",
			LiquidOrdinaryWalletPlanWireErrorCode.VersionMismatch =>
				"ordinary wallet plan wire version is unsupported",
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding =>
				"ordinary wallet plan wire encoding is invalid",
			LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded =>
				"ordinary wallet plan wire limit exceeded",
			LiquidOrdinaryWalletPlanWireErrorCode.SourceBindingMismatch =>
				"ordinary wallet plan wire source binding does not match",
			LiquidOrdinaryWalletPlanWireErrorCode.ContextRejected =>
				"ordinary wallet plan wire context was rejected",
			LiquidOrdinaryWalletPlanWireErrorCode.PlanRejected =>
				"ordinary wallet plan wire plan was rejected",
			LiquidOrdinaryWalletPlanWireErrorCode.FundingRejected =>
				"ordinary wallet plan wire funding was rejected",
			_ => throw new ArgumentOutOfRangeException(
				nameof(errorCode),
				"An ordinary wallet plan wire failure code is required."),
		};
}
