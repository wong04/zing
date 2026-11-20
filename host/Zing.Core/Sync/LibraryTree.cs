using System.Security.Cryptography;
using System.Text;

namespace Zing.Core.Sync;

/// <summary>What kind of node an entry is. The two lists are the top of the bookmarks and the Reading List, which always exist.</summary>
public enum LibraryKind
{
	List,
	Folder,
	Link,
	Reading,
}

/// <summary>
/// One node of the library as sync sees it, under its stable ID: where it sits (<see cref="Parent"/>), its own fields, and, for a list or
/// folder, the order of its children. A move changes the node's parent and the order of the folders it leaves and joins; a rename changes
/// its title. Each is a separate field, so concurrent edits to different fields of a node, or to different nodes, all survive.
/// </summary>
public sealed record LibraryEntry(
	LibraryKind Kind,
	string Parent,
	string Title,
	string? Url,
	string? ImportedFrom,
	long Added,
	bool IsRead,
	long UpdatedAt,
	IReadOnlyList<string>? Order)
{
	public bool SameAs(LibraryEntry? other) =>
		other is not null && Kind == other.Kind && Parent == other.Parent && Title == other.Title && Url == other.Url
		&& ImportedFrom == other.ImportedFrom && Added == other.Added && IsRead == other.IsRead && UpdatedAt == other.UpdatedAt
		&& (Order ?? []).SequenceEqual(other.Order ?? [], StringComparer.Ordinal);

	public bool IsContainer => Kind is LibraryKind.List or LibraryKind.Folder;
}

/// <summary>
/// The library as a flat map of entries, and back; the three-way merge of two copies against the last one they had in common; and the
/// clean-up that makes any merge a valid tree. Pure: the same inputs give the same tree on every PC.
/// </summary>
public static class LibraryTree
{
	/// <summary>The top of the bookmarks: Favourites, Pinned and every other top-level node.</summary>
	public const string BookmarksId = "~bookmarks";

	/// <summary>The Reading List's order.</summary>
	public const string ReadingId = "~reading";

	public static Dictionary<string, LibraryEntry> Flatten(BrowserLibrary library)
	{
		var entries = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal)
		{
			[BookmarksId] = List([.. library.Bookmarks.Select(n => n.Id)]),
			[ReadingId] = List([.. library.ReadingList.Select(i => i.Id)]),
		};
		foreach (var node in library.Bookmarks) AddNode(entries, node, BookmarksId);
		foreach (var item in library.ReadingList)
			entries[item.Id] = new LibraryEntry(LibraryKind.Reading, ReadingId, item.Title, item.Url, null, item.Added, item.IsRead, item.UpdatedAt, null);
		return entries;
	}

	private static void AddNode(Dictionary<string, LibraryEntry> entries, BookmarkNode node, string parent)
	{
		switch (node)
		{
			case BookmarkLink link:
				entries[link.Id] = new LibraryEntry(LibraryKind.Link, parent, link.Title, link.Url, link.ImportedFrom, 0, false, link.UpdatedAt, null);
				break;
			case BookmarkFolder folder:
				entries[folder.Id] = new LibraryEntry(LibraryKind.Folder, parent, folder.Title, null, folder.ImportedFrom, 0, false, folder.UpdatedAt, [.. folder.Children.Select(c => c.Id)]);
				foreach (var child in folder.Children) AddNode(entries, child, folder.Id);
				break;
		}
	}

	/// <summary>The library a normalized map describes (<see cref="Normalize"/>).</summary>
	public static BrowserLibrary Build(IReadOnlyDictionary<string, LibraryEntry> entries)
	{
		BookmarkNode Node(string id)
		{
			var entry = entries[id];
			return entry.Kind == LibraryKind.Folder
				? new BookmarkFolder(id, entry.Title, [.. (entry.Order ?? []).Select(Node)], entry.UpdatedAt, entry.ImportedFrom)
				: new BookmarkLink(id, entry.Title, entry.Url ?? "", entry.UpdatedAt, entry.ImportedFrom);
		}

		var bookmarks = (entries[BookmarksId].Order ?? []).Select(Node).ToList();
		var reading = (entries[ReadingId].Order ?? []).Select(id =>
		{
			var entry = entries[id];
			return new ReadingItem(id, entry.Title, entry.Url ?? "", entry.Added, entry.IsRead, entry.UpdatedAt);
		}).ToList();
		return new BrowserLibrary(bookmarks, reading);
	}

	public static bool Same(IReadOnlyDictionary<string, LibraryEntry> a, IReadOnlyDictionary<string, LibraryEntry> b) =>
		a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SameAs(other));

	/// <summary>
	/// The merge of this PC's copy (<paramref name="mine"/>) and the server's (<paramref name="theirs"/>) against the copy both started from
	/// (<paramref name="common"/>; empty on a first sync), with the vault's rules (Sync design): a field changed on one side only takes that
	/// side's value; changed on both, it takes the node edited later (<c>UpdatedAt</c>, a tie going to the server). A node deleted on one
	/// side stays deleted unless the other side changed it meanwhile: a delete loses to an edit it hadn't seen. Normalize the result.
	/// </summary>
	public static Dictionary<string, LibraryEntry> Merge(
		IReadOnlyDictionary<string, LibraryEntry> common,
		IReadOnlyDictionary<string, LibraryEntry> mine,
		IReadOnlyDictionary<string, LibraryEntry> theirs)
	{
		var merged = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
		foreach (var id in mine.Keys.Union(theirs.Keys, StringComparer.Ordinal))
		{
			common.TryGetValue(id, out var b);
			mine.TryGetValue(id, out var m);
			theirs.TryGetValue(id, out var t);
			if (MergeEntry(b, m, t) is { } entry) merged[id] = entry;
		}
		return merged;
	}

	private static LibraryEntry? MergeEntry(LibraryEntry? b, LibraryEntry? m, LibraryEntry? t)
	{
		if (m is null && t is null) return null;
		if (m is null) return b is not null && t!.SameAs(b) ? null : t;
		if (t is null) return b is not null && m.SameAs(b) ? null : m;
		return Fields(b, m, t);
	}

	private static LibraryEntry Fields(LibraryEntry? b, LibraryEntry m, LibraryEntry t)
	{
		var later = m.UpdatedAt > t.UpdatedAt ? m : t;

		T Field<T>(Func<LibraryEntry, T> field)
		{
			var mine = field(m);
			var theirs = field(t);
			if (EqualityComparer<T>.Default.Equals(mine, theirs)) return mine;
			if (b is not null && EqualityComparer<T>.Default.Equals(mine, field(b))) return theirs;
			if (b is not null && EqualityComparer<T>.Default.Equals(theirs, field(b))) return mine;
			return field(later);
		}

		return new LibraryEntry(
			t.Kind,
			Field(e => e.Parent),
			Field(e => e.Title),
			Field(e => e.Url),
			Field(e => e.ImportedFrom),
			Field(e => e.Added),
			Field(e => e.IsRead),
			Math.Max(m.UpdatedAt, t.UpdatedAt),
			m.IsContainer || t.IsContainer ? MergeOrder(b?.Order ?? [], m.Order ?? [], t.Order ?? []) : null);
	}

	/// <summary>
	/// The order of a list both sides may have changed. The side that reordered what both had leads (the server's when both did); IDs the
	/// other side removed go, and IDs it added go in after the ID they followed there. Children that moved in or out are settled by
	/// <see cref="Normalize"/>, which follows each node's own parent.
	/// </summary>
	public static IReadOnlyList<string> MergeOrder(IReadOnlyList<string> common, IReadOnlyList<string> mine, IReadOnlyList<string> theirs)
	{
		var inCommon = common.ToHashSet(StringComparer.Ordinal);
		var (lead, other) = SameRelativeOrder(common, theirs) ? (mine, theirs) : (theirs, mine);
		var inOther = other.ToHashSet(StringComparer.Ordinal);
		var result = lead.Where(id => !inCommon.Contains(id) || inOther.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
		for (var i = 0; i < other.Count; i++)
		{
			var id = other[i];
			if (inCommon.Contains(id) || result.Contains(id, StringComparer.Ordinal)) continue;
			var at = 0;
			for (var before = i - 1; before >= 0; before--)
			{
				var found = result.IndexOf(other[before]);
				if (found < 0) continue;
				at = found + 1;
				break;
			}
			result.Insert(at, id);
		}
		return result;
	}

	/// <summary>Whether the IDs both lists hold come in the same order in each.</summary>
	private static bool SameRelativeOrder(IReadOnlyList<string> a, IReadOnlyList<string> b)
	{
		var inB = b.ToHashSet(StringComparer.Ordinal);
		var inA = a.ToHashSet(StringComparer.Ordinal);
		return a.Where(inB.Contains).SequenceEqual(b.Where(inA.Contains), StringComparer.Ordinal);
	}

	/// <summary>
	/// A valid tree from any merge: both lists and the fixed folders exist; a node whose parent is gone, or isn't a folder, goes to the
	/// top of the bookmarks; a folder moved into its own descendant on another PC breaks out of the loop at the node with the lowest ID;
	/// and every container's order holds exactly its children, the ones its order already names first, then the rest by ID.
	/// </summary>
	public static Dictionary<string, LibraryEntry> Normalize(IReadOnlyDictionary<string, LibraryEntry> merged, long now)
	{
		var entries = new Dictionary<string, LibraryEntry>(merged, StringComparer.Ordinal);
		if (!entries.ContainsKey(BookmarksId)) entries[BookmarksId] = List([]);
		if (!entries.ContainsKey(ReadingId)) entries[ReadingId] = List([]);
		AddFixedFolder(entries, Library.FavouritesId, Library.FavouritesTitle, now);
		AddFixedFolder(entries, Library.PinnedId, Library.PinnedTitle, now);

		foreach (var (id, entry) in entries.ToList())
		{
			var parent = entry.Kind switch
			{
				LibraryKind.List => "",
				LibraryKind.Reading => ReadingId,
				_ when Library.IsFixedFolder(id) => BookmarksId,
				_ => entry.Parent != id && entries.TryGetValue(entry.Parent, out var p) && (p.Kind == LibraryKind.Folder || entry.Parent == BookmarksId) ? entry.Parent : BookmarksId,
			};
			if (parent != entry.Parent) entries[id] = entry with { Parent = parent };
		}
		BreakLoops(entries);

		var children = entries.Where(e => e.Value.Kind != LibraryKind.List).GroupBy(e => e.Value.Parent, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Select(e => e.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
		foreach (var (id, entry) in entries.ToList())
		{
			if (!entry.IsContainer)
			{
				if (entry.Order is not null) entries[id] = entry with { Order = null };
				continue;
			}
			var mine = children.TryGetValue(id, out var found) ? found : [];
			var ordered = (entry.Order ?? []).Where(mine.Contains).Distinct(StringComparer.Ordinal).ToList();
			ordered.AddRange(mine.Except(ordered, StringComparer.Ordinal).Order(StringComparer.Ordinal));
			if (!ordered.SequenceEqual(entry.Order ?? [], StringComparer.Ordinal)) entries[id] = entry with { Order = ordered };
		}
		return entries;
	}

	private static void AddFixedFolder(Dictionary<string, LibraryEntry> entries, string id, string title, long now)
	{
		if (entries.TryGetValue(id, out var entry) && entry.Kind == LibraryKind.Folder) return;
		entries[id] = new LibraryEntry(LibraryKind.Folder, BookmarksId, title, null, null, 0, false, now, []);
	}

	private static void BreakLoops(Dictionary<string, LibraryEntry> entries)
	{
		foreach (var start in entries.Keys.Order(StringComparer.Ordinal).ToList())
		{
			var path = new List<string>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var at = start;
			while (entries.TryGetValue(at, out var entry) && entry.Kind == LibraryKind.Folder)
			{
				if (!seen.Add(at))
				{
					var loop = path.SkipWhile(id => id != at).ToList();
					var cut = loop.Min(StringComparer.Ordinal)!;
					entries[cut] = entries[cut] with { Parent = BookmarksId };
					break;
				}
				path.Add(at);
				at = entry.Parent;
			}
		}
	}

	/// <summary>
	/// A first sync of two PCs that each made the same thing: a node new on this PC (not in <paramref name="common"/> nor on the server)
	/// takes the ID of a node new on the server with the same place and identity, so it merges instead of appearing twice. A link or a
	/// Reading List item is known by its address, a folder by its import mark (<see cref="ImportMerge.Merge"/>'s source key) or else its
	/// title. Parents go first, so the children of two matching folders can match too.
	/// </summary>
	public static Dictionary<string, LibraryEntry> MatchNew(
		IReadOnlyDictionary<string, LibraryEntry> common,
		IReadOnlyDictionary<string, LibraryEntry> mine,
		IReadOnlyDictionary<string, LibraryEntry> theirs)
	{
		var unclaimed = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
		foreach (var id in TopDown(theirs))
		{
			if (common.ContainsKey(id) || mine.ContainsKey(id)) continue;
			var key = Identity(theirs[id], theirs[id].Parent);
			if (!unclaimed.TryGetValue(key, out var queue)) unclaimed[key] = queue = new Queue<string>();
			queue.Enqueue(id);
		}
		if (unclaimed.Count == 0) return new Dictionary<string, LibraryEntry>(mine, StringComparer.Ordinal);

		var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var id in TopDown(mine))
		{
			if (common.ContainsKey(id) || theirs.ContainsKey(id)) continue;
			var entry = mine[id];
			var parent = renamed.GetValueOrDefault(entry.Parent, entry.Parent);
			if (unclaimed.TryGetValue(Identity(entry, parent), out var queue) && queue.Count > 0) renamed[id] = queue.Dequeue();
		}
		if (renamed.Count == 0) return new Dictionary<string, LibraryEntry>(mine, StringComparer.Ordinal);

		string Rename(string id) => renamed.GetValueOrDefault(id, id);
		return mine.ToDictionary(
			pair => Rename(pair.Key),
			pair => pair.Value with { Parent = Rename(pair.Value.Parent), Order = pair.Value.Order?.Select(Rename).ToList() },
			StringComparer.Ordinal);
	}

	private static string Identity(LibraryEntry entry, string parent) => entry.Kind switch
	{
		LibraryKind.Link => $"link\n{parent}\n{entry.Url}",
		LibraryKind.Folder => entry.ImportedFrom is { } mark ? $"import\n{parent}\n{mark}" : $"folder\n{parent}\n{entry.Title}",
		LibraryKind.Reading => $"reading\n{entry.Url}",
		_ => $"list\n{parent}",
	};

	/// <summary>Every ID, each after its parent, following the containers' orders from the two lists, then any the orders miss.</summary>
	private static List<string> TopDown(IReadOnlyDictionary<string, LibraryEntry> entries)
	{
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var queue = new Queue<string>([BookmarksId, ReadingId]);
		while (queue.Count > 0)
		{
			var id = queue.Dequeue();
			if (!entries.TryGetValue(id, out var entry) || !seen.Add(id)) continue;
			result.Add(id);
			foreach (var child in entry.Order ?? []) queue.Enqueue(child);
		}
		result.AddRange(entries.Keys.Where(id => !seen.Contains(id)).Order(StringComparer.Ordinal));
		return result;
	}

	/// <summary>Which of the 32 sync items a node is stored in: by a hash of its ID, so it never moves between them.</summary>
	public static string ShardOf(string id) => $"library-{SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % ShardCount:x2}";

	public const int ShardCount = 32;

	private static LibraryEntry List(IReadOnlyList<string> order) => new(LibraryKind.List, "", "", null, null, 0, false, 0, order);
}
