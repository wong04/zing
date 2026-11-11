using Zing.Core.Vault;

namespace Zing.Core.Import;

public sealed class DownloadsFolderMissingException(string path) : DirectoryNotFoundException($"The downloads folder {path} doesn't exist, so Zing can't watch it for a password export.");

public sealed class PasswordExportWatchAlreadyStartedException() : InvalidOperationException("This password export watch was already started");

/// <summary>
/// After the user starts a password export in another browser, waits for the CSV to appear in the downloads folder.
/// Polls once a second (rescheduling one tick at a time) and takes the first new file whose length held across two polls and whose header a password import accepts.
/// </summary>
public sealed class PasswordExportWatch(IScheduler scheduler, string downloadsDir, Func<DateTimeOffset> now, Action<string> found, Action timedOut)
{
	public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
	public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

	private readonly Dictionary<string, long> _lastLength = [];
	private readonly HashSet<string> _ignored = [];
	private DateTimeOffset _start;
	private IDisposable? _timer;
	private bool _started;
	private bool _done;

	/// <exception cref="DownloadsFolderMissingException"/>
	/// <exception cref="PasswordExportWatchAlreadyStartedException"/>
	public void Start()
	{
		if (_started) throw new PasswordExportWatchAlreadyStartedException();
		if (!Directory.Exists(downloadsDir)) throw new DownloadsFolderMissingException(downloadsDir);
		_started = true;
		_start = now();
		_timer = scheduler.Schedule(PollInterval, Poll);
	}

	public void Stop()
	{
		_done = true;
		_timer?.Dispose();
		_timer = null;
	}

	private void Poll()
	{
		if (_done) return;
		string? path;
		try
		{
			path = FindExport();
		}
		catch (DirectoryNotFoundException)
		{
			Stop();
			timedOut();
			return;
		}
		if (path is not null)
		{
			Stop();
			found(path);
			return;
		}
		if (now() - _start >= Timeout)
		{
			Stop();
			timedOut();
			return;
		}
		_timer = scheduler.Schedule(PollInterval, Poll);
	}

	private string? FindExport()
	{
		var seen = new Dictionary<string, long>();
		foreach (var file in new DirectoryInfo(downloadsDir).EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly).OrderBy(f => f.Name, StringComparer.Ordinal))
		{
			if (_ignored.Contains(file.FullName) || file.LastWriteTimeUtc < _start.UtcDateTime) continue;
			try
			{
				var length = file.Length;
				seen[file.FullName] = length;
				if (!_lastLength.TryGetValue(file.FullName, out var previous) || previous != length) continue;
				if (IsPasswordExport(file.FullName)) return file.FullName;
				_ignored.Add(file.FullName);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// The browser may still hold the file, or have just renamed or removed it; the next poll tries again.
			}
		}
		_lastLength.Clear();
		foreach (var (path, length) in seen) _lastLength[path] = length;
		return null;
	}

	public static bool IsPasswordExport(string path)
	{
		try
		{
			VaultCsv.ParsePasswords(VaultCsv.ReadFile(path));
			return true;
		}
		catch (Exception e) when (e is UnknownPasswordCsvException or CsvSyntaxException or CsvTooLargeException)
		{
			return false;
		}
	}
}
