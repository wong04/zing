using Microsoft.UI.Dispatching;

namespace Zing.Host;

/// <summary>
/// The host's error boundary. In test runs faults go to a small local log (AppPaths.FaultLog),
/// never uploaded; a real install keeps no log, because Zing keeps nothing by default (CLAUDE.md).
/// An error's message can hold a page's address or a file name, so outside e2e runs (a hand run on
/// a scratch profile) the log keeps only exception types and stack frames (Describe). e2e runs also fail on
/// ZING_ERRORS_FILE (TestHooks.Record). Event handlers never use <c>async void</c>: they hand
/// their work to TestHooks.Background, which records a failure and never rethrows
/// (HostSourceRulesTests holds Zing.Host to that).
/// </summary>
public static class Faults
{
	/// <summary>Past this the log moves to faults.1.log, so at most about twice this stays on disk.</summary>
	private const long RollOverBytes = 1024 * 1024;

	private static readonly object Gate = new();

	/// <summary>A fault: something went wrong that shouldn't have.</summary>
	public static void Error(string kind, object error) => Append("error", kind, error);

	/// <summary>An expected failure of an optional feature (IWindowHost.Warn); never fails an e2e run.</summary>
	public static void Warning(string kind, object error) => Append("warning", kind, error);

	/// <summary>
	/// Runs <paramref name="action"/> on the UI thread's queue. A queue that refuses it is shutting
	/// down, so the work is lost; that is recorded rather than ignored.
	/// </summary>
	public static void Enqueue(DispatcherQueue dispatcher, DispatcherQueueHandler action, string what)
	{
		if (!dispatcher.TryEnqueue(() => TestHooks.Guard(() => action(), what))) TestHooks.Record("dispatch", new DispatcherRefusedException(what));
	}

	/// <summary>
	/// What the log keeps of an error. In full (e2e runs) its whole text; otherwise each exception's
	/// type and stack frames, which name Zing's code, never its messages, which may name what the person browsed.
	/// </summary>
	public static string Describe(object error, bool full)
	{
		if (full) return error.ToString() ?? "";
		if (error is not Exception exception) return "(details withheld)";
		var lines = new List<string>();
		void Add(Exception e, int depth)
		{
			lines.Add($"{new string(' ', depth * 2)}{e.GetType().FullName}");
			if (e.StackTrace is { } trace) lines.Add(trace);
			if (e is AggregateException aggregate)
				foreach (var inner in aggregate.InnerExceptions) Add(inner, depth + 1);
			else if (e.InnerException is { } inner) Add(inner, depth + 1);
		}
		Add(exception, 0);
		return string.Join('\n', lines);
	}

	private static void Append(string level, string kind, object error)
	{
		// Logging may run on any thread (unhandled-exception handlers, task continuations).
		lock (Gate)
		{
			// The one place a failure is swallowed: a log that can't be written must not crash Zing.
			try
			{
				if (AppPaths.FaultLog is not { } file) return;
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				var existing = new FileInfo(file);
				if (existing.Exists && existing.Length > RollOverBytes) File.Move(file, Path.Combine(existing.DirectoryName!, "faults.1.log"), overwrite: true);
				File.AppendAllText(file, $"{DateTimeOffset.Now:O} {level} {kind}: {Describe(error, TestHooks.IsEndToEndRun)}\n\n");
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}

/// <summary>The UI thread's queue would not take work, because it is shutting down.</summary>
public sealed class DispatcherRefusedException(string what) : Exception($"The UI thread's queue refused {what}; it is shutting down");
