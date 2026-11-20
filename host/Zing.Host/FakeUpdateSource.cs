using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The update source of an e2e run, which never contacts GitHub or restarts. Created only by <see cref="TestHooks.UpdateSource"/>.
/// Up to date until {kind: "test", action: "update-ready", version} offers a version, which the next check finds and "downloads";
/// {action: "update-restarts", file} reads back how many restarts were asked for. An offer with restart "quits" makes a restart quit Zing,
/// as Velopack's does, and with "fails" makes it throw, as an updater that won't start does; without it a restart is only counted.
/// </summary>
public sealed class FakeUpdateSource : IUpdateSource
{
	private string? _offered;
	private string? _restart;

	public string CurrentVersion => "0.9.0";

	public int Restarts { get; private set; }

	/// <summary>What quits Zing, as Velopack's source is given it (Installation.UpdateSource).</summary>
	public Action? Quit { get; set; }

	public void Offer(string version, string? restart)
	{
		if (restart is not (null or "quits" or "fails")) throw new ArgumentException($"Unknown restart {restart}", nameof(restart));
		_offered = version;
		_restart = restart;
	}

	public Task<string?> FindAsync(CancellationToken cancel) => Task.FromResult(_offered);

	public Task DownloadAsync(CancellationToken cancel) => Task.CompletedTask;

	public void RestartIntoUpdate()
	{
		Restarts++;
		if (_restart == "fails") throw new InvalidOperationException("The e2e updater would not start");
		if (_restart == "quits") (Quit ?? throw new InvalidOperationException("The fake update source was never given Zing's quit"))();
	}

	public void ApplyOnExit()
	{
	}
}
