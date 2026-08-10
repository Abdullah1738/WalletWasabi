namespace WalletWasabi.Liquid.WalletFacts.Wire;

internal enum LiquidWalletFactsWireErrorCode : uint
{
	None = 0,
	InvalidArgument = 1,
	VersionMismatch = 2,
	InvalidEncoding = 3,
	LimitExceeded = 4,
	DescriptorRejected = 5,
	CandidateRejected = 6,
	ObservationRejected = 7,
	SourceBindingMismatch = 8,
}

internal static class LiquidWalletFactsWireErrorCodeExtensions
{
	internal static string GetMessage(this LiquidWalletFactsWireErrorCode errorCode) =>
		errorCode switch
		{
			LiquidWalletFactsWireErrorCode.InvalidArgument => "wallet facts wire argument is invalid",
			LiquidWalletFactsWireErrorCode.VersionMismatch => "wallet facts wire version is unsupported",
			LiquidWalletFactsWireErrorCode.InvalidEncoding => "wallet facts wire encoding is invalid",
			LiquidWalletFactsWireErrorCode.LimitExceeded => "wallet facts wire limit exceeded",
			LiquidWalletFactsWireErrorCode.DescriptorRejected => "wallet facts descriptor was rejected",
			LiquidWalletFactsWireErrorCode.CandidateRejected => "wallet facts candidate batch was rejected",
			LiquidWalletFactsWireErrorCode.ObservationRejected => "wallet facts observation was rejected",
			LiquidWalletFactsWireErrorCode.SourceBindingMismatch => "wallet facts source binding does not match",
			_ => throw new ArgumentOutOfRangeException(
				nameof(errorCode),
				"A wallet facts wire failure code is required."),
		};
}
