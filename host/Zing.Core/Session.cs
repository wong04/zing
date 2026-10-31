using System.Text.Json;

namespace Zing.Core;

/// <summary>One tab as it survives a quit (src/shared/session/session.ts).</summary>
public sealed record SessionTab(string Url, string Title, bool Pinned, bool Active);

public sealed class SessionFileException(string path, Exception cause)
	: Exception($"Could not read the saved session at {path}: {cause.Message}", cause)
{
	public string Path { get; } = path;
}

public static class Session
{
	/// <summary>Beyond this the file stops being a convenience and becomes a liability.</summary>
	public const int MaxSessionTabs = 40;

	/// <summary>A tab worth saving: real web pages only.</summary>
	public static bool IsRestorableUrl(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

	/// <summary>What is worth persisting. Pinned tabs survive the cap first.</summary>
	public static List<SessionTab> Prepare(IEnumerable<SessionTab> tabs)
	{
		var kept = tabs.Where(t => IsRestorableUrl(t.Url)).ToList();
		if (kept.Count <= MaxSessionTabs) return kept;
		return kept.Where(t => t.Pinned).Concat(kept.Where(t => !t.Pinned)).Take(MaxSessionTabs).ToList();
	}

	public static string Encode(IEnumerable<SessionTab> tabs) => JsonSerializer.Serialize(Prepare(tabs), ZingJson.Options);

	/// <summary>
	/// Krackn's rule: a corrupt or half-written session file must never stop the
	/// browser opening, so anything unreadable decodes to no tabs.
	/// </summary>
	public static List<SessionTab> Decode(string text)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(text);
		}
		catch (JsonException)
		{
			return [];
		}
		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
			return document.RootElement.EnumerateArray()
				.Select(ToTab)
				.OfType<SessionTab>()
				.Where(t => IsRestorableUrl(t.Url))
				.ToList();
		}
	}

	/// <summary>No file is a first run; a corrupt one decodes to no tabs. An unreadable one is an error.</summary>
	public static List<SessionTab> Load(string path)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
		{
			return [];
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new SessionFileException(path, error);
		}
		return Decode(text);
	}

	/// <summary>With restore off nothing may stay on disk, including a file left by an earlier run.</summary>
	public static void Discard(string path)
	{
		if (File.Exists(path)) File.Delete(path);
	}

	public static List<SessionTab> ToSessionTabs(IEnumerable<TabRecord> records, string? activeId) =>
		records.Select(t => new SessionTab(t.Url, t.Title, t.Pinned, t.Id == activeId)).ToList();

	private static SessionTab? ToTab(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Object) return null;
		if (!TryString(value, "url", out var url) || !TryString(value, "title", out var title)) return null;
		if (!TryBool(value, "pinned", out var pinned) || !TryBool(value, "active", out var active)) return null;
		return new SessionTab(url, title, pinned, active);
	}

	private static bool TryString(JsonElement obj, string name, out string value)
	{
		value = "";
		if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
		value = p.GetString()!;
		return true;
	}

	private static bool TryBool(JsonElement obj, string name, out bool value)
	{
		value = false;
		if (!obj.TryGetProperty(name, out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
		value = p.GetBoolean();
		return true;
	}
}
