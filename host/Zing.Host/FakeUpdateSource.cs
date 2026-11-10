using Zing.Core;

namespace Zing.Host;

/// <summary>
/// The update source of an e2e run, which never contacts GitHub or restarts. Created only by <see cref="TestHooks.UpdateSource"/>.
/// Up to date until {kind: "test", action: "update-ready", version} offers a version, which the next check finds and "downloads";
/// {action: "update-restarts", file} reads back how many restarts were asked for.
/// </summary>
public sealed class FakeUpdateSource : IUpdateSource
{
	private string? _offered;

	public string CurrentVersion => "0.9.0";

	public int Restarts { get; private set; }

	public void Offer(string version) => _offered = version;

	public Task<string?> FindAsync(CancellationToken cancel) => Task.FromResult(_offered);

	public Task DownloadAsync(CancellationToken cancel) => Task.CompletedTask;

	public void RestartIntoUpdate() => Restarts++;

	public void ApplyOnExit()
	{
	}
}
