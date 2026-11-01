using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>The settings file exists but can't be read or isn't valid settings (src/main/settings-file.ts SettingsFileError).</summary>
public sealed class SettingsFileException(string path, Exception inner)
	: Exception($"Could not read settings from {path}: {inner.Message}", inner)
{
	public string Path { get; } = path;
}

public static class JsonFiles
{
	private static readonly JsonSerializerOptions TabIndented = new()
	{
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <summary>No file yet means first run: every key reads as its default. Anything unreadable is an error.</summary>
	public static JsonObject LoadSettingsFile(string path)
	{
		if (!File.Exists(path)) return SettingsSchema.Defaults();
		try
		{
			return Settings.Parse(JsonNode.Parse(File.ReadAllText(path)));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidSettingException or UnknownSettingException)
		{
			throw new SettingsFileException(path, error);
		}
	}

	/// <summary>Temp file, flushed to disk, then moved over the old one, so a crash or power cut mid-write leaves the old file intact instead of a torn or empty one.</summary>
	public static void WriteJsonAtomic(string path, object data) =>
		WriteAllBytesAtomic(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, TabIndented) + "\n"));

	/// <summary>The atomic write behind <see cref="WriteJsonAtomic"/>. If writing the temp file fails, the old file is untouched and the temp file is removed.</summary>
	public static void WriteAllBytesAtomic(string path, ReadOnlySpan<byte> data)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
		Directory.CreateDirectory(directory);
		var temp = $"{path}.{Environment.ProcessId}.tmp";
		try
		{
			using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				stream.Write(data);
				// Without this the move can reach the disk before the data does, and a power cut leaves an empty file.
				stream.Flush(flushToDisk: true);
			}
			File.Move(temp, path, overwrite: true);
		}
		catch
		{
			TryDelete(temp);
			throw;
		}
	}

	/// <summary>
	/// Settings for this launch. A file whose content is invalid is moved aside and Zing starts from the defaults;
	/// one that can't be read (locked, access denied) is left where it is. Either way startup goes on, and the problem is returned so the person can be told.
	/// </summary>
	public static Loaded<JsonObject> LoadSettingsOrRecover(string path, DateTime now) =>
		UserData.Load(path, () => LoadSettingsFile(path), SettingsSchema.Defaults, now);

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			// The write already failed; that is the error worth reporting.
		}
	}
}

/// <summary>A user data file that couldn't be used at startup. <see cref="KeptAs"/> is where an invalid file was moved; null means the file is still at <see cref="Path"/>.</summary>
public sealed record LoadProblem(string Path, string? KeptAs, Exception Error)
{
	/// <summary>The file wasn't moved aside, so saving this launch's state there would overwrite data that may still be good.</summary>
	public bool OriginalInPlace => KeptAs is null;
}

/// <summary>What a load produced, and the problem (if any) that made it fall back.</summary>
public sealed record Loaded<T>(T Value, LoadProblem? Problem);

/// <summary>Loading user data so that a bad file never stops Zing starting (docs/architecture-review.md W-3).</summary>
public static class UserData
{
	/// <summary>
	/// Runs <paramref name="load"/>. An error whose cause is invalid content quarantines the file as
	/// <c>name.corrupt-yyyyMMdd-HHmmss</c> and returns the fallback; any other read error returns the fallback and leaves the file alone.
	/// </summary>
	public static Loaded<T> Load<T>(string path, Func<T> load, Func<T> fallback, DateTime now)
	{
		try
		{
			return new Loaded<T>(load(), null);
		}
		catch (Exception error) when (IsInvalidContent(error))
		{
			string? kept = null;
			try
			{
				kept = Quarantine(path, now);
			}
			catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
			{
				return new Loaded<T>(fallback(), new LoadProblem(path, null, new AggregateException(error, moveError)));
			}
			return new Loaded<T>(fallback(), new LoadProblem(path, kept, error));
		}
		catch (Exception error) when (IsReadError(error))
		{
			return new Loaded<T>(fallback(), new LoadProblem(path, null, error));
		}
	}

	/// <summary>Moves the file to a name no earlier quarantine used, and returns that path.</summary>
	public static string Quarantine(string path, DateTime now)
	{
		var stem = $"{path}.corrupt-{now:yyyyMMdd-HHmmss}";
		var target = stem;
		for (var n = 2; File.Exists(target) || Directory.Exists(target); n++) target = $"{stem}-{n}";
		File.Move(path, target);
		return target;
	}

	private static bool IsInvalidContent(Exception error) => Cause(error) is JsonException or InvalidDataException or InvalidSettingException or UnknownSettingException;

	private static bool IsReadError(Exception error) => Cause(error) is IOException or UnauthorizedAccessException;

	/// <summary>The file exceptions wrap the reason; a bare one is its own reason.</summary>
	private static Exception Cause(Exception error) =>
		error is SettingsFileException or LibraryFileException or SessionFileException && error.InnerException is { } inner ? inner : error;
}

/// <summary>Runs an action after a delay; injectable so tests need no real waiting.</summary>
public interface IScheduler
{
	IDisposable Schedule(TimeSpan delay, Action action);
}

/// <summary>Fires scheduled actions only when a test advances its clock.</summary>
public sealed class ManualScheduler : IScheduler
{
	private sealed record Entry(TimeSpan Due, Action Action);

	private readonly List<Entry> _entries = [];
	private TimeSpan _now = TimeSpan.Zero;

	public int PendingCount => _entries.Count;

	public IDisposable Schedule(TimeSpan delay, Action action)
	{
		var entry = new Entry(_now + delay, action);
		_entries.Add(entry);
		return new Cancel(() => _entries.Remove(entry));
	}

	public void Advance(TimeSpan by)
	{
		_now += by;
		while (_entries.Where(e => e.Due <= _now).OrderBy(e => e.Due).FirstOrDefault() is { } due)
		{
			_entries.Remove(due);
			due.Action();
		}
	}

	private sealed class Cancel(Action remove) : IDisposable
	{
		public void Dispose() => remove();
	}
}

/// <summary>
/// Debounces writes so dragging a slider is one write, not one per step. Runs on the scheduler's thread, which in the
/// host is the UI thread (DispatcherScheduler), so it reads stores that don't lock without racing their edits.
/// A failed write goes to <paramref name="onError"/> rather than escaping a timer callback, which would crash the process.
/// </summary>
public sealed class DebouncedJsonWriter(string path, TimeSpan delay, IScheduler scheduler, Action<Exception> onError)
{
	private object? _pending;
	private IDisposable? _timer;

	public string Path { get; } = path;

	public void Write(object data)
	{
		_pending = data;
		_timer?.Dispose();
		_timer = scheduler.Schedule(delay, Flush);
	}

	/// <summary>Writes now if anything is pending. Called on quit so the last change isn't lost.</summary>
	public void Flush()
	{
		_timer?.Dispose();
		_timer = null;
		if (_pending is null) return;
		var data = _pending;
		_pending = null;
		try
		{
			JsonFiles.WriteJsonAtomic(Path, data);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			// Keep it so the next flush (at the latest, on quit) tries again, unless a newer change has replaced it.
			_pending ??= data;
			onError(error);
		}
	}
}
