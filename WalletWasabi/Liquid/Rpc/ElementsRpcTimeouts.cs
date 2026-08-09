namespace WalletWasabi.Liquid.Rpc;

public sealed record ElementsRpcTimeouts(
	TimeSpan ConnectTimeout,
	TimeSpan TotalRequestTimeout,
	TimeSpan ResponseIdleTimeout)
{
	public static ElementsRpcTimeouts Default { get; } = new(
		ConnectTimeout: TimeSpan.FromSeconds(10),
		TotalRequestTimeout: TimeSpan.FromSeconds(30),
		ResponseIdleTimeout: TimeSpan.FromSeconds(5));

	internal ElementsRpcTimeouts Validate()
	{
		ValidateDuration(ConnectTimeout, nameof(ConnectTimeout));
		ValidateDuration(TotalRequestTimeout, nameof(TotalRequestTimeout));
		ValidateDuration(ResponseIdleTimeout, nameof(ResponseIdleTimeout));
		if (ConnectTimeout > TotalRequestTimeout)
		{
			throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), "Connect timeout cannot exceed the total request timeout.");
		}
		if (ResponseIdleTimeout > TotalRequestTimeout)
		{
			throw new ArgumentOutOfRangeException(nameof(ResponseIdleTimeout), "Response idle timeout cannot exceed the total request timeout.");
		}

		return this;
	}

	private static void ValidateDuration(TimeSpan value, string parameterName)
	{
		if (value == Timeout.InfiniteTimeSpan || value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10))
		{
			throw new ArgumentOutOfRangeException(parameterName, "A positive timeout no longer than ten minutes is required.");
		}
	}
}
