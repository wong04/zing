using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>The remembered site permissions file exists but can't be read or isn't a list of sites.</summary>
public sealed class SiteGrantsFileException(string path, Exception inner)
	: Exception($"Could not read the remembered site permissions from {path}: {inner.Message}", inner)
{
	public string Path { get; } = path;
}

public sealed class NotANotificationPromptException(string kind) : Exception($"Only a notification prompt can be remembered for the site, not a {kind} prompt");

public sealed class NotARememberableSiteException(string origin) : Exception($"Only a web site can be remembered, not {origin}");

/// <summary>What Settings lists: the sites remembered as allowed to show notifications, sorted.</summary>
public sealed record SiteGrantsState(IReadOnlyList<string> Notifications);

/// <summary>
/// The sites the person allowed to show notifications and asked Zing to remember ("Remember for this site"). Every other
/// grant lasts as long as its page (PermissionBroker); this is the one kind kept across runs, and only by that choice. One
/// JSON file, written atomically; it is deleted when the last site is removed, so nothing stays on disk. Shared by every
/// window, on the UI thread.
/// </summary>
public sealed class SiteGrants
{
	public const string FileName = "site-permissions.json";

	private readonly string? _path;
	private readonly SortedSet<string> _notifications;
	private readonly List<Action> _listeners = [];

	private SiteGrants(string? path, IEnumerable<string> notifications)
	{
		_path = path;
		_notifications = new SortedSet<string>(notifications, StringComparer.Ordinal);
	}

	/// <summary>Nothing remembered and nothing written: tests, and hosts that keep no files.</summary>
	public static SiteGrants InMemory() => new(null, []);

	/// <summary>No file means nothing is remembered. Anything unreadable is an error.</summary>
	public static SiteGrants Open(string path)
	{
		if (!File.Exists(path)) return new SiteGrants(path, []);
		try
		{
			var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("The file is empty");
			var list = root["notifications"]?.AsArray() ?? throw new InvalidDataException("There is no notifications list");
			return new SiteGrants(path, list.Select(n => Origin(n?.GetValue<string>())));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException or FormatException)
		{
			throw new SiteGrantsFileException(path, error);
		}
	}

	public SiteGrantsState State => new([.. _notifications]);

	public bool AllowsNotifications(string origin) => _notifications.Contains(origin);

	/// <summary>Called whenever the remembered sites change.</summary>
	public Action Subscribe(Action listener)
	{
		_listeners.Add(listener);
		return () => _listeners.Remove(listener);
	}

	public void RememberNotifications(string origin)
	{
		if (!IsWebOrigin(origin)) throw new NotARememberableSiteException(origin);
		if (!_notifications.Add(origin)) return;
		Changed();
	}

	/// <summary>Settings' Remove: the site is asked again next time.</summary>
	public void ForgetNotifications(string origin)
	{
		if (!_notifications.Remove(origin)) return;
		Changed();
	}

	private void Changed()
	{
		Save();
		foreach (var listener in _listeners.ToList()) listener();
	}

	private void Save()
	{
		if (_path is null) return;
		if (_notifications.Count == 0)
		{
			File.Delete(_path);
			return;
		}
		JsonFiles.WriteJsonAtomic(_path, State);
	}

	private static bool IsWebOrigin(string origin) =>
		Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is ("http" or "https") && uri.GetLeftPart(UriPartial.Authority) == origin;

	private static string Origin(string? value) =>
		value is not null && IsWebOrigin(value) ? value : throw new InvalidDataException($"'{value}' is not a site's origin");
}
