using System.Text.RegularExpressions;

namespace Zing.Core.Tests;

/// <summary>
/// Source rules for host/Zing.Host, which has no tests of its own (it needs Windows and WebView2).
///
/// The error boundary rule: no <c>async void</c> in Zing.Host, neither a method nor an async
/// lambda subscribed to an event (<c>+= async</c>). An exception out of <c>async void</c> crashes
/// the process. A handler that has to await is a plain <c>void</c> that hands its work, an
/// <c>async Task</c> method, to <c>TestHooks.Background(task, kind)</c>, which records a failure in
/// the fault log (and ZING_ERRORS_FILE in e2e runs) and never rethrows; a ViewGoneException is
/// expected and not recorded. With no <c>async void</c> left, no <c>throw;</c> can escape one either.
///
/// The check is textual: it reads every line that isn't a comment, so it can't be fooled by a
/// mention in a doc comment, and needs nothing but the source files.
/// </summary>
public sealed partial class HostSourceRulesTests
{
	[GeneratedRegex(@"\basync\s+void\b")]
	private static partial Regex AsyncVoidMethod();

	[GeneratedRegex(@"\+=\s*async\b")]
	private static partial Regex AsyncHandlerLambda();

	[Fact]
	public void HostHasNoAsyncVoid()
	{
		var offences = HostSources()
			.SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, number: index + 1)))
			.Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
			.Where(l => AsyncVoidMethod().IsMatch(l.line) || AsyncHandlerLambda().IsMatch(l.line))
			.Select(l => $"{Path.GetFileName(l.file)}:{l.number}: {l.line.Trim()}")
			.ToList();
		Assert.True(offences.Count == 0, "async void crashes Zing on a throw; pass an async Task to TestHooks.Background instead:\n" + string.Join("\n", offences));
	}

	/// <summary>
	/// Windows App SDK's AppInstance keeps every process id of an exe path in shared memory and never
	/// removes its own on exit. A later launch given a reused id dies in AppInstance's first call with
	/// COMException 0x8000FFFF, and no caller can prevent that. ProfileOwner replaces it.
	/// </summary>
	[Fact]
	public void HostDoesNotUseAppInstance()
	{
		var offences = HostSources()
			.SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, number: index + 1)))
			.Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
			.Where(l => l.line.Contains("AppInstance", StringComparison.Ordinal))
			.Select(l => $"{Path.GetFileName(l.file)}:{l.number}: {l.line.Trim()}")
			.ToList();
		Assert.True(offences.Count == 0, "AppInstance crashes a launch that reuses an old process id; use ProfileOwner:" + string.Join(" ", offences));
	}

	/// <summary>
	/// Zing's own views hold the whole bridge (engines, DNS, settings). DevTools there (F12, Ctrl+Shift+I) would make
	/// "paste this into the console" a way in, so only e2e runs, which drive the views over CDP, have them.
	/// </summary>
	[Fact]
	public void RendererViewsHaveNoDevToolsOutsideEndToEndRuns()
	{
		var source = File.ReadAllText(HostSources().Single(f => Path.GetFileName(f) == "RendererView.cs"));
		Assert.Contains("core.Settings.AreDevToolsEnabled = TestHooks.IsEndToEndRun;", source);
	}

	/// <summary>
	/// SystemParametersInfo can fail, and the preview reads high contrast inside tab events (PreviewChanged, from
	/// NavigationStarting and close paths). So the window reads it in one place, records a failure there, and hands out
	/// the value it keeps.
	/// </summary>
	[Fact]
	public void HighContrastIsReadInOnePlace()
	{
		var reads = HostSources()
			.Where(file => Path.GetFileName(file) != "Native.cs")
			.SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, number: index + 1)))
			.Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.line.Contains("Native.HighContrastOn()", StringComparison.Ordinal))
			.Select(l => $"{Path.GetFileName(l.file)}:{l.number}")
			.ToList();
		Assert.True(reads.Count == 1, "Read high contrast through MainWindow's kept value, not Native.HighContrastOn: " + string.Join(" ", reads));
	}

	/// <summary>A view's start ended while its popup is being destroyed is gone only when the engine aborted it (E_ABORT); any other error is a real one.</summary>
	[Fact]
	public void AViewStartFailingWhileItsPopupIsDestroyedIsGoneOnlyWhenAborted()
	{
		var source = File.ReadAllText(HostSources().Single(f => Path.GetFileName(f) == "HostedView.cs"));
		Assert.Contains("Destroying.Contains(hwnd) && error.HResult == E_ABORT", source);
	}

	[Fact]
	public void RuleSeesTheHostSources()
	{
		// Guards the guard: a wrong root would find no files and pass vacuously.
		Assert.Contains(HostSources(), f => Path.GetFileName(f) == "WebView2Page.cs");
	}

	private static string[] HostSources() => Directory.GetFiles(Path.Combine(RepoRoot(), "host", "Zing.Host"), "*.cs", SearchOption.TopDirectoryOnly);

	private static string RepoRoot()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			if (Directory.Exists(Path.Combine(dir.FullName, "host", "Zing.Host"))) return dir.FullName;
		}
		throw new DirectoryNotFoundException($"No host/Zing.Host above {AppContext.BaseDirectory}");
	}
}
