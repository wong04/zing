using Velopack;
using Zing.Core;

namespace Zing.Host;

/// <summary>Updates for an installed Zing, through Velopack. Its awaits resume on the UI thread, where the Updater runs.</summary>
public sealed class VelopackUpdateSource(UpdateManager manager, Action beforeRestart) : IUpdateSource
{
	private UpdateInfo? _found;

	public string CurrentVersion => manager.CurrentVersion?.ToString() ?? "";

	public async Task<string?> FindAsync(CancellationToken cancel)
	{
		_found = await manager.CheckForUpdatesAsync().WaitAsync(cancel);
		return _found?.TargetFullRelease.Version.ToString();
	}

	public Task DownloadAsync(CancellationToken cancel) => manager.DownloadUpdatesAsync(Found(), null, cancel);

	/// <summary>Exits Zing at once, so what quitting saves is saved first.</summary>
	public void RestartIntoUpdate()
	{
		var release = Found().TargetFullRelease;
		beforeRestart();
		manager.ApplyUpdatesAndRestart(release);
	}

	/// <summary>The updater waits for Zing to exit, applies the update silently, and doesn't start Zing again.</summary>
	public void ApplyOnExit() => manager.WaitExitThenApplyUpdates(Found().TargetFullRelease, silent: true, restart: false);

	private UpdateInfo Found() => _found ?? throw new InvalidOperationException("No update was found to download or apply");
}
