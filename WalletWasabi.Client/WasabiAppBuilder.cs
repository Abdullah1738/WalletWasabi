using System;

namespace WalletWasabi.Client;

public enum ExitCode
{
	Ok,
	FailedAlreadyRunningSignaled,
	FailedAlreadyRunningError,
}

public record WasabiAppBuilder(string AppName, string[] Arguments)
{
	internal bool MustCheckSingleInstance { get; init; }
	internal EventHandler<Exception>? UnhandledExceptionEventHandler { get; init; }
	internal EventHandler<AggregateException>? UnobservedTaskExceptionsEventHandler { get; init; }
	internal Action Terminate { get; init; } = () => { };

	public WasabiAppBuilder EnsureSingleInstance(bool ensure = true) =>
		this with { MustCheckSingleInstance = ensure };

	public WasabiAppBuilder OnUnhandledExceptions(EventHandler<Exception> handler) =>
		this with { UnhandledExceptionEventHandler = handler };

	public WasabiAppBuilder OnUnobservedTaskExceptions(EventHandler<AggregateException> handler) =>
		this with { UnobservedTaskExceptionsEventHandler = handler };

	public WasabiAppBuilder OnTermination(Action action) =>
		this with { Terminate = action };

	public WasabiApplication Build() =>
		Build(ApplicationRuntime.Bitcoin);

	internal WasabiApplication Build(ApplicationRuntime runtime) =>
		SelectApplication(runtime, () => new WasabiApplication(this));

	internal static TApplication SelectApplication<TApplication>(
		ApplicationRuntime runtime,
		Func<TApplication> applicationFactory) =>
		runtime switch
		{
			ApplicationRuntime.Bitcoin => InvokeFactory(applicationFactory),
			ApplicationRuntime.Liquid => throw new NotSupportedException("Liquid application composition is not implemented."),
			_ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, "An explicit supported application runtime is required."),
		};

	private static TApplication InvokeFactory<TApplication>(Func<TApplication> applicationFactory)
	{
		ArgumentNullException.ThrowIfNull(applicationFactory);
		return applicationFactory();
	}

	public static WasabiAppBuilder Create(string appName, string[] args) =>
		new(appName, args);
}
