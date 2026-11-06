using System.Text.Json;

namespace Zing.Core;

/// <summary>One tab as it survives a quit (src/shared/session/session.ts).</summary>
/// <param name="ItemId">The saved item (Favourites or Pinned) the tab belongs to, so it stays tied after a restore.</param>
/// <param name="Split">The split view the tab is a pane of, so the split comes back with the tabs.</param>
public sealed record SessionTab(string Url, string Title, bool Pinned, bool Active, string? ItemId = null, SessionSplit? Split = null);

/// <summary>A tab's place in a split: which split, its orientation, the pane's position and its share of the space.</summary>
public sealed record SessionSplit(string Id, string Orientation, int Pane, double Ratio);

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

	/// <summary>For startup: a corrupt file is no tabs (Load), and one that can't be read is no tabs plus the problem, so the browser still opens.</summary>
	public static Loaded<List<SessionTab>> LoadOrRecover(string path)
	{
		try
		{
			return new Loaded<List<SessionTab>>(Load(path), null);
		}
		catch (SessionFileException error)
		{
			return new Loaded<List<SessionTab>>([], new LoadProblem(path, null, error));
		}
	}

	/// <summary>With restore off nothing may stay on disk, including a file left by an earlier run.</summary>
	public static void Discard(string path)
	{
		if (File.Exists(path)) File.Delete(path);
	}

	/// <summary>A relayed tab is saved under its real address: the relay scheme only loads when Zing started it, and the consent for a relay is not kept, so a restored tab loads as an ordinary page.</summary>
	public static List<SessionTab> ToSessionTabs(IEnumerable<TabRecord> records, string? activeId, IReadOnlyList<SplitLayout>? splits = null) =>
		records.Select(t => new SessionTab(Net.RelayedContent.ToWebAddress(t.Url), t.Title, t.Pinned, t.Id == activeId, t.ItemId, SplitOf(t.Id, splits ?? []))).ToList();

	private static SessionSplit? SplitOf(string tabId, IReadOnlyList<SplitLayout> splits)
	{
		if (splits.FirstOrDefault(s => s.TabIds.Contains(tabId)) is not { } split) return null;
		var pane = split.TabIds.ToList().IndexOf(tabId);
		return new SessionSplit(split.Id, split.Orientation, pane, split.Ratios[pane]);
	}

	/// <summary>
	/// The splits among restored tabs (in the order the tabs came back): each saved split whose panes all came back,
	/// in pane order. A split with a pane missing (its tab wasn't saved) is dropped; its tabs come back on their own.
	/// </summary>
	public static List<SplitLayout> SplitsOf(IReadOnlyList<SessionTab> tabs, IReadOnlyList<string> tabIds)
	{
		var layouts = new List<SplitLayout>();
		foreach (var group in tabs.Select((t, i) => (Tab: t, Id: tabIds[i])).Where(p => p.Tab.Split is not null).GroupBy(p => p.Tab.Split!.Id))
		{
			var panes = group.OrderBy(p => p.Tab.Split!.Pane).ToList();
			if (panes.Count is < Split.MinPanes or > Split.MaxPanes || panes.Select((p, i) => p.Tab.Split!.Pane == i).Contains(false)) continue;
			var orientation = panes[0].Tab.Split!.Orientation;
			if (orientation is not ("row" or "column") || panes.Any(p => p.Tab.Split!.Orientation != orientation)) continue;
			var ratios = panes.Select(p => p.Tab.Split!.Ratio).ToList();
			if (ratios.Any(r => !double.IsFinite(r) || r <= 0)) continue;
			var total = ratios.Sum();
			layouts.Add(new SplitLayout(Guid.NewGuid().ToString(), orientation, [.. panes.Select(p => p.Id)], [.. ratios.Select(r => r / total)]));
		}
		return layouts;
	}

	private static SessionTab? ToTab(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Object) return null;
		if (!TryString(value, "url", out var url) || !TryString(value, "title", out var title)) return null;
		if (!TryBool(value, "pinned", out var pinned) || !TryBool(value, "active", out var active)) return null;
		// Optional: sessions saved before saved tabs or split view have none.
		var itemId = value.TryGetProperty("itemId", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
		return new SessionTab(url, title, pinned, active, itemId, ToSplit(value));
	}

	/// <summary>A malformed split is dropped, never the tab: a session file must never stop the browser opening.</summary>
	private static SessionSplit? ToSplit(JsonElement tab)
	{
		if (!tab.TryGetProperty("split", out var split) || split.ValueKind != JsonValueKind.Object) return null;
		if (!TryString(split, "id", out var id) || !TryString(split, "orientation", out var orientation)) return null;
		if (!split.TryGetProperty("pane", out var pane) || !pane.TryGetInt32(out var index)) return null;
		if (!split.TryGetProperty("ratio", out var ratio) || ratio.ValueKind != JsonValueKind.Number) return null;
		return new SessionSplit(id, orientation, index, ratio.GetDouble());
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
