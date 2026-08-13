namespace WalletWasabi.Liquid.Wallet.Wire;

internal static class LiquidOrdinaryWalletPlanWireLimits
{
	internal const int SourceEpochLength = 32;
	internal const int HeaderLength = 152;
	internal const int SelectedFixedLength = 88;
	internal const int DestinationFixedLength = 48;
	internal const int PreviousLengthPrefix = 4;
	internal const int MaximumAddressLength = 256;
	internal const int MaximumTransactionLength = 4_194_304;
	internal const int MaximumPreviousTransactionCount = 16_384;
	internal const int MaximumAggregateTransactionLength = 67_108_864;
	internal const int MaximumReachableFrameLength = 67_260_872;
}
