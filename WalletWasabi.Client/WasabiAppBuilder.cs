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

	internal TApplication SelectApplication<TApplication>(
		ApplicationRuntime runtime,
		Func<TApplication> applicationFactory) =>
		runtime switch
		{
			ApplicationRuntime.Bitcoin => InvokeFactory(applicationFactory),
			// LIQUID-PROVIDER-OWNERSHIP-SEAM-001: the ownership seam types land in this slice;
			// the application-level Liquid composition root (bootstrap -> provider -> coordinator
			// wired into TerminateService) is deliberately not live yet. Fail closed rather than
			// silently running the Bitcoin path under a Liquid runtime selection.
			ApplicationRuntime.Liquid when typeof(TApplication) == typeof(WasabiApplication) => throw new NotSupportedException("Liquid application composition is not yet live; the ownership seam lands before the composition root."),
			ApplicationRuntime.Liquid => throw new NotSupportedException("Liquid application composition is only available for WasabiApplication."),
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
