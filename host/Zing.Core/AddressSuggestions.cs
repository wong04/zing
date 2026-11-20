namespace Zing.Core;

public static class SuggestionKinds
{
	public const string Tab = "tab";
	public const string Bookmark = "bookmark";
	public const string Reading = "reading";
}

/// <summary>One row under the address bar (src/shared/ipc.ts AddressSuggestion). <paramref name="TabId"/> is set only for an open tab, which picking switches to.</summary>
public sealed record AddressSuggestion(string Kind, string Title, string Url, string? TabId = null);

/// <summary>What the suggestions view draws: the rows, the one the arrow keys picked (-1 for none), and the address field it hangs under, in DIPs of the window's content.</summary>
public sealed record AddressSuggestionsState(Rect Field, IReadOnlyList<AddressSuggestion> Items, int Active);

/// <summary>
/// Ranks what Zing already holds against what's typed in the address bar: open tabs, bookmarks and the Reading List.
/// Never history: Zing keeps none by default, and remembering it is the owner's privacy call.
/// </summary>
public static class AddressRanking
{
	public const int Max = 8;

	private static readonly string[] Schemes = ["http", "https", "file"];

	/// <summary>
	/// Every typed word must match the title or the address. A word scores 3 at the start of the host, 2 at the start of
	/// a title word, 1 anywhere else; ties go to open tabs, then bookmarks, then the Reading List, each in its own order.
	/// The tab on screen is left out, and an address listed twice keeps its first row (an open tab before its bookmark).
	/// </summary>
	public static IReadOnlyList<AddressSuggestion> Rank(string query, IReadOnlyList<TabRecord> tabs, string? activeTabId, BrowserLibrary library)
	{
		var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (words.Length == 0) return [];
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var scored = new List<(AddressSuggestion Item, int Score, int Order)>();
		foreach (var candidate in Candidates(tabs, activeTabId, library))
		{
			if (!IsOpenable(candidate.Url) || !seen.Add(candidate.Url)) continue;
			var score = Score(words, candidate);
			if (score > 0) scored.Add((candidate, score, scored.Count));
		}
		return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Take(Max).Select(s => s.Item)];
	}

	private static IEnumerable<AddressSuggestion> Candidates(IReadOnlyList<TabRecord> tabs, string? activeTabId, BrowserLibrary library)
	{
		foreach (var tab in tabs)
			if (tab.Id != activeTabId) yield return new AddressSuggestion(SuggestionKinds.Tab, tab.Title, tab.Url, tab.Id);
		foreach (var link in Links(library.Bookmarks)) yield return new AddressSuggestion(SuggestionKinds.Bookmark, link.Title, link.Url);
		foreach (var item in library.ReadingList) yield return new AddressSuggestion(SuggestionKinds.Reading, item.Title, item.Url);
	}

	private static IEnumerable<BookmarkLink> Links(IReadOnlyList<BookmarkNode> nodes)
	{
		foreach (var node in nodes)
		{
			if (node is BookmarkLink link) yield return link;
			if (node is not BookmarkFolder folder) continue;
			foreach (var child in Links(folder.Children)) yield return child;
		}
	}

	private static bool IsOpenable(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var parsed) && Schemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase);

	/// <summary>0 when any word matches nothing.</summary>
	private static int Score(string[] words, AddressSuggestion candidate)
	{
		var title = candidate.Title.ToLowerInvariant();
		var address = Bare(candidate.Url.ToLowerInvariant());
		var titleWords = WordsOf(title);
		var total = 0;
		foreach (var word in words)
		{
			var score = address.StartsWith(word, StringComparison.Ordinal) ? 3
				: titleWords.Any(w => w.StartsWith(word, StringComparison.Ordinal)) ? 2
				: title.Contains(word, StringComparison.Ordinal) || address.Contains(word, StringComparison.Ordinal) ? 1
				: 0;
			if (score == 0) return 0;
			total += score;
		}
		return total;
	}

	/// <summary>The address as people type it: no scheme, no www.</summary>
	private static string Bare(string url)
	{
		var at = url.IndexOf("://", StringComparison.Ordinal);
		var rest = at < 0 ? url : url[(at + 3)..];
		return rest.StartsWith("www.", StringComparison.Ordinal) ? rest[4..] : rest;
	}

	private static string[] WordsOf(string text)
	{
		var parts = new List<string>();
		var start = 0;
		for (var i = 0; i <= text.Length; i++)
		{
			if (i < text.Length && char.IsLetterOrDigit(text[i])) continue;
			if (i > start) parts.Add(text[start..i]);
			start = i + 1;
		}
		return [.. parts];
	}
}

/// <summary>The address bar's suggestions of one window. Every change is pushed (`addressbar:suggestions`) to the renderer.</summary>
public sealed class AddressSuggestions(Action<AddressSuggestionsState?> changed)
{
	public AddressSuggestionsState? Current { get; private set; }

	/// <summary>Shows these rows under the field, none highlighted; no rows closes the list.</summary>
	public IReadOnlyList<AddressSuggestion> Show(IReadOnlyList<AddressSuggestion> items, Rect field)
	{
		if (items.Count == 0)
		{
			Close();
			return items;
		}
		Set(new AddressSuggestionsState(field, items, -1));
		return items;
	}

	/// <summary>The arrow keys moved the highlight; -1 is back to what was typed.</summary>
	public void Highlight(int index)
	{
		if (Current is not { } current) throw new InvalidOperationException("No address suggestions are open");
		if (index < -1 || index >= current.Items.Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"There are {current.Items.Count} suggestions");
		Set(current with { Active = index });
	}

	public void Close()
	{
		if (Current is null) return;
		Set(null);
	}

	private void Set(AddressSuggestionsState? state)
	{
		Current = state;
		changed(state);
	}
}
