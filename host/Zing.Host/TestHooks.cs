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

	/// <summary>Chromium switches for e2e runs: the debugging port, and with ZING_E2E_OFFLINE=1 a dead proxy so no test can reach a live site.</summary>
	public static string BrowserArguments()
	{
		if (!IsEndToEndRun) return "";
		var args = new List<string>();
		if (DebuggingPort is { } port) args.Add($"--remote-debugging-port={port}");
		if (Environment.GetEnvironmentVariable("ZING_E2E_OFFLINE") == "1") args.Add("--proxy-server=127.0.0.1:9 --proxy-bypass-list=<-loopback>");
		return string.Join(' ', args);
	}

	/// <summary>The app clock. ZING_TEST_CLOCK=1 in an e2e run gives one a test can move forward (test:advance-clock).</summary>
	public static Zing.Core.IClock Clock { get; } = IsEndToEndRun && Environment.GetEnvironmentVariable("ZING_TEST_CLOCK") == "1" ? new Zing.Core.OffsetClock() : new Zing.Core.SystemClock();

	/// <summary>
	/// Test-only messages from Zing's own pages in an e2e run, answered by doing it, never by a
	/// reply the renderer's bridge would not expect: {kind: "test", action: "advance-clock", ms}.
	/// </summary>
	public static bool TryHandle(string json)
	{
		if (!IsEndToEndRun) return false;
		using var message = System.Text.Json.JsonDocument.Parse(json);
		var root = message.RootElement;
		if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "test") return false;
		var action = root.GetProperty("action").GetString();
		if (action != "advance-clock") throw new InvalidOperationException($"Unknown test action {action}");
		if (Clock is not Zing.Core.OffsetClock clock) throw new InvalidOperationException("advance-clock needs ZING_TEST_CLOCK=1");
		clock.Advance(root.GetProperty("ms").GetInt64());
		return true;
	}

	/// <summary>Work nobody awaits: its failures are recorded, except a view that went with its closing window.</summary>
	public static void Background(Task task, string what) =>
		_ = task.ContinueWith(t =>
		{
			if (t.Exception!.InnerExceptions.All(e => e is ViewGoneException)) return;
			Record(what, t.Exception);
		}, TaskContinuationOptions.OnlyOnFaulted);

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
