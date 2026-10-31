namespace Zing.Host;

/// <summary>
/// e2e-only behaviour, switched on by ZING_E2E=1 (the WebView2 counterpart of
/// src/main/test-hooks.ts). Test runs must not disturb the person at the PC
/// (AGENTS.md): the window opens off-screen and out of the switchers, and
/// unhandled errors go to ZING_ERRORS_FILE, which the harness fails on.
/// </summary>
public static class TestHooks
{
	public static bool IsEndToEndRun => Environment.GetEnvironmentVariable("ZING_E2E") == "1";

	/// <summary>Remote debugging port for Playwright; only honoured in e2e runs.</summary>
	public static string? DebuggingPort => IsEndToEndRun ? Environment.GetEnvironmentVariable("ZING_CDP_PORT") : null;

	public static void RecordUnhandledErrors()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("unhandled", e.ExceptionObject);
		TaskScheduler.UnobservedTaskException += (_, e) => Record("unobserved task", e.Exception);
	}

	public static void Record(string kind, object error)
	{
		var file = Environment.GetEnvironmentVariable("ZING_ERRORS_FILE");
		if (!IsEndToEndRun || string.IsNullOrEmpty(file)) return;
		File.AppendAllText(file, $"{kind}: {error}\n\n");
	}
}
