using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The update source of an e2e run, which never contacts GitHub or restarts. Created only by <see cref="TestHooks.UpdateSource"/>.
/// Up to date until {kind: "test", action: "update-ready", version} offers a version, which the next check finds and "downloads";
/// {action: "update-restarts", file} reads back how many restarts were asked for. An offer with quits makes a restart quit Zing, as Velopack's does.
/// </summary>
public sealed class FakeUpdateSource : IUpdateSource
{
	private string? _offered;
	private bool _quits;

	public string CurrentVersion => "0.9.0";

	public int Restarts { get; private set; }

	/// <summary>What quits Zing, as Velopack's source is given it (Installation.UpdateSource).</summary>
	public Action? Quit { get; set; }

	public void Offer(string version, bool quits)
	{
		_offered = version;
		_quits = quits;
	}

	public Task<string?> FindAsync(CancellationToken cancel) => Task.FromResult(_offered);

	public Task DownloadAsync(CancellationToken cancel) => Task.CompletedTask;

	public void RestartIntoUpdate()
	{
		Restarts++;
		if (_quits) (Quit ?? throw new InvalidOperationException("The fake update source was never given Zing's quit"))();
	}

	public void ApplyOnExit()
	{
	}
}
