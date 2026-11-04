namespace Zing.Core.Engines;

/// <summary>What the Engines panel shows for one engine (src/shared/ipc.ts EngineInfo).</summary>
/// <param name="Url">Where it is fetched from, shown as text so a person can fetch it by hand.</param>
/// <param name="DownloadBytes">The size of the download, told to the person before they start it.</param>
public sealed record EngineInfo(string Id, string Name, string Version, string Url, string Licence, long DownloadBytes, string? InstalledVersion, long InstalledBytes)
{
	public static EngineInfo Of(EngineStatus status) => new(
		status.Engine.Id,
		status.Engine.Name,
		status.Engine.Version,
		status.Engine.Url.ToString(),
		status.Engine.Licence,
		status.Engine.Bytes,
		status.InstalledVersion,
		status.InstalledBytes);
}

/// <summary>Passes a download's progress on at most once per percent, so a 180 MB file isn't thousands of bridge events.</summary>
public sealed class PercentProgress(Action<double> push) : IProgress<double>
{
	private int _last = -1;

	public void Report(double fraction)
	{
		var percent = (int)Math.Floor(fraction * 100);
		if (percent == _last) return;
		_last = percent;
		push(fraction);
	}
}
