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

	/// <summary>Temp file + move, so a crash mid-write leaves the old file intact instead of a torn one.</summary>
	public static void WriteJsonAtomic(string path, object data)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
		Directory.CreateDirectory(directory);
		var temp = $"{path}.{Environment.ProcessId}.tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(data, TabIndented) + "\n");
		File.Move(temp, path, overwrite: true);
	}
}

/// <summary>Runs an action after a delay; injectable so tests need no real waiting.</summary>
public interface IScheduler
{
	IDisposable Schedule(TimeSpan delay, Action action);
}

public sealed class SystemScheduler : IScheduler
{
	public IDisposable Schedule(TimeSpan delay, Action action) =>
		new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);
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

/// <summary>Debounces writes so dragging a slider is one write, not one per step.</summary>
public sealed class DebouncedJsonWriter(string path, TimeSpan delay, IScheduler scheduler)
{
	private readonly object _gate = new();
	private object? _pending;
	private IDisposable? _timer;

	public string Path { get; } = path;

	public void Write(object data)
	{
		lock (_gate)
		{
			_pending = data;
			_timer?.Dispose();
			_timer = scheduler.Schedule(delay, Flush);
		}
	}

	/// <summary>Writes now if anything is pending. Called on quit so the last change isn't lost.</summary>
	public void Flush()
	{
		lock (_gate)
		{
			_timer?.Dispose();
			_timer = null;
			if (_pending is null) return;
			var data = _pending;
			_pending = null;
			JsonFiles.WriteJsonAtomic(Path, data);
		}
	}
}
