using Zing.Core.Import;

namespace Zing.Core.Tests.Import;

public sealed class PasswordExportWatchTests : IDisposable
{
	private const string Csv = "name,url,username,password,note\r\nExample,https://example.com/,me,secret,\r\n";

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-watch-").FullName;
	private readonly ManualScheduler _scheduler = new();
	private readonly DateTimeOffset _clockStart = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
	private TimeSpan _elapsed = TimeSpan.Zero;
	private readonly List<string> _found = [];
	private int _timedOut;

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private PasswordExportWatch NewWatch() => new(_scheduler, _dir, () => _clockStart + _elapsed, _found.Add, () => _timedOut++);

	private void Poll(int times = 1)
	{
		for (var i = 0; i < times; i++)
		{
			_elapsed += TimeSpan.FromSeconds(1);
			_scheduler.Advance(TimeSpan.FromSeconds(1));
		}
	}

	private string Write(string name, string text, TimeSpan offsetFromStart)
	{
		var path = Path.Combine(_dir, name);
		File.WriteAllText(path, text);
		File.SetLastWriteTimeUtc(path, (_clockStart + offsetFromStart).UtcDateTime);
		return path;
	}

	[Fact]
	public void AnOlderExportAlreadyThereIsIgnored()
	{
		Write("old.csv", Csv, TimeSpan.FromMinutes(-5));
		NewWatch().Start();
		Poll(5);
		Assert.Empty(_found);
	}

	[Fact]
	public void ANonPasswordCsvIsIgnored()
	{
		Write("other.csv", "a,b\r\n1,2\r\n", TimeSpan.Zero);
		NewWatch().Start();
		Poll(5);
		Assert.Empty(_found);
	}

	[Fact]
	public void AFileStillGrowingIsTakenOnlyOnceStable()
	{
		var path = Write("Chrome Passwords.csv", Csv, TimeSpan.Zero);
		NewWatch().Start();
		Poll();
		Assert.Empty(_found);
		File.AppendAllText(path, "Two,https://two.example/,me,secret,\r\n");
		Poll();
		Assert.Empty(_found);
		Poll();
		Assert.Equal([path], _found);
	}

	[Fact]
	public void TheFirstExportIsReportedOnceAndTheWatchStops()
	{
		var path = Write("a.csv", Csv, TimeSpan.Zero);
		NewWatch().Start();
		Poll(2);
		Write("b.csv", Csv, TimeSpan.FromSeconds(1));
		Poll(5);
		Assert.Equal([path], _found);
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public void StopCancelsPolling()
	{
		var watch = NewWatch();
		watch.Start();
		watch.Stop();
		Write("a.csv", Csv, TimeSpan.Zero);
		Poll(5);
		Assert.Empty(_found);
		Assert.Equal(0, _timedOut);
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public void TimesOutAfterTenMinutes()
	{
		NewWatch().Start();
		Poll(599);
		Assert.Equal(0, _timedOut);
		Poll();
		Assert.Equal(1, _timedOut);
		Assert.Equal(0, _scheduler.PendingCount);
		Poll(3);
		Assert.Equal(1, _timedOut);
	}

	[Fact]
	public void AFileThatIsNotUtf8IsIgnoredForTheRestOfTheWatch()
	{
		var path = Path.Combine(_dir, "bad.csv");
		File.WriteAllBytes(path, [0xFF, 0xFE, 0xFD]);
		File.SetLastWriteTimeUtc(path, _clockStart.UtcDateTime);
		NewWatch().Start();
		Poll(5);
		Assert.Empty(_found);
	}

	[Fact]
	public void AMissingDownloadsFolderIsAnError()
	{
		var watch = new PasswordExportWatch(_scheduler, Path.Combine(_dir, "nope"), () => _clockStart, _found.Add, () => _timedOut++);
		Assert.Throws<DownloadsFolderMissingException>(watch.Start);
		Assert.Equal(0, _scheduler.PendingCount);
	}
}
