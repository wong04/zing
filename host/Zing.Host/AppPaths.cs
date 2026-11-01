namespace Zing.Host;

/// <summary>
/// Where Zing keeps its files. The same folder Electron's app.getPath('userData')
/// used (%APPDATA%\Zing), so settings and the library carry over from the
/// Electron build. e2e runs pass ZING_USER_DATA and never touch the real profile.
/// </summary>
public static class AppPaths
{
	public static string UserData { get; } = Resolve();

	public static string Settings => Path.Combine(UserData, "settings.json");
	public static string Library => Path.Combine(UserData, "library.json");
	public static string Session => Path.Combine(UserData, "session.json");

	/// <summary>
	/// The local fault log (Faults), kept only by test runs: those set ZING_USER_DATA, and the log goes in
	/// that scratch folder as logs\faults.log. Null otherwise, so a real install writes no log at all, since
	/// Zing keeps nothing by default; an e2e run without ZING_USER_DATA is null too, as logging must not throw.
	/// </summary>
	public static string? FaultLog
	{
		get
		{
			var configured = Environment.GetEnvironmentVariable("ZING_USER_DATA");
			return string.IsNullOrEmpty(configured) ? null : Path.Combine(configured, "logs", "faults.log");
		}
	}

	private static string Resolve()
	{
		var configured = Environment.GetEnvironmentVariable("ZING_USER_DATA");
		if (TestHooks.IsEndToEndRun && string.IsNullOrEmpty(configured)) throw new InvalidOperationException("e2e runs must set ZING_USER_DATA so they never use the real profile");
		var path = string.IsNullOrEmpty(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zing") : configured;
		Directory.CreateDirectory(path);
		return path;
	}
}
