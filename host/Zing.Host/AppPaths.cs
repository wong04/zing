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

	/// <summary>The password vault: vault.zing, keys.bin and sync.json (Zing.Core.Vault.VaultStore). Not created until the first save.</summary>
	public static string Vault => Path.Combine(UserData, "vault");

	/// <summary>
	/// The DNS settings from before Network &amp; VPN changed them, kept only until Undo. Local rather than roaming: they describe this
	/// PC's adapters. e2e runs keep it in their scratch folder like everything else.
	/// </summary>
	public static string DnsBackup { get; } = Path.Combine(LocalData(), "dns-before.json");

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

	/// <summary>
	/// Where downloaded engines live: %LOCALAPPDATA%Zingengines, beside Zing's other local data, since they are
	/// big and per-machine and are not settings. A test run keeps them inside its scratch ZING_USER_DATA. Not created
	/// until something is downloaded.
	/// </summary>
	public static string Engines
	{
		get
		{
			var configured = Environment.GetEnvironmentVariable("ZING_USER_DATA");
			if (!string.IsNullOrEmpty(configured)) return Path.Combine(configured, "engines");
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zing", "engines");
		}
	}

	/// <summary>Where each engine job gets its private working folder; emptied at every start, and a job removes its own when it ends.</summary>
	public static string EngineJobs => Path.Combine(Path.GetDirectoryName(Engines)!, "engine-jobs");

	/// <summary>Where Convert's HTML to PDF keeps the throwaway browser profile of a print; removed when the print ends.</summary>
	public static string PrintScratch => Path.Combine(Path.GetDirectoryName(Engines)!, "print-scratch");

	/// <summary>
	/// Where the file viewer keeps the converted copies it shows: a folder of Zing's own under the temp folder, emptied at every start
	/// and by each file when it closes. A test run keeps it inside its scratch ZING_USER_DATA.
	/// </summary>
	public static string ViewerCopies
	{
		get
		{
			var configured = Environment.GetEnvironmentVariable("ZING_USER_DATA");
			return string.IsNullOrEmpty(configured) ? Path.Combine(Path.GetTempPath(), "Zing-viewer") : Path.Combine(configured, "viewer-copies");
		}
	}

	private static string LocalData()
	{
		var configured = Environment.GetEnvironmentVariable("ZING_USER_DATA");
		if (TestHooks.IsEndToEndRun && string.IsNullOrEmpty(configured)) throw new InvalidOperationException("e2e runs must set ZING_USER_DATA so they never use the real profile");
		return string.IsNullOrEmpty(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zing") : configured;
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
