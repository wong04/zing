using System.Diagnostics;

namespace Zing.Host;

/// <summary>
/// When each step of start-up was reached, as wall-clock times, so an e2e run can set them beside the chrome's own paint
/// timings (performance.timeOrigin) and the process's start ("startup-marks"). A few entries on the UI thread; nothing is written outside a test.
/// </summary>
public static class StartupMarks
{
	private static readonly List<(string Name, double At)> Marks = [];

	public static void Mark(string name) => Marks.Add((name, (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds));

	/// <summary>The process's start and each mark, in ms since 1970, as JSON.</summary>
	public static string Json()
	{
		using var process = Process.GetCurrentProcess();
		var started = (process.StartTime.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
		return System.Text.Json.JsonSerializer.Serialize(new { processStart = started, marks = Marks.Select(m => new { name = m.Name, at = m.At }) });
	}
}
