namespace Zing.Core.Import;

public sealed record MergeOutcome(BrowserLibrary Library, int AddedToBar, int AddedToFolder, string FolderTitle);

/// <summary>The C# twin of mergeImport in src/shared/import/importResult.ts, held to it by merge.vectors.json.</summary>
public static class ImportMerge
{
	private sealed record Merged(IReadOnlyList<BookmarkNode> Children, int Added);

	private delegate bool FolderMatch(BookmarkFolder existing, ImportedFolder incoming);

	/// <summary>
	/// Bar items join the Favourites bar; everything else lands in one "Imported from &lt;source&gt;" folder, found again by its importedFrom mark
	/// even if renamed. Re-importing only adds: nothing is ever removed, renamed or reordered.
	/// </summary>
	public static MergeOutcome Merge(BrowserLibrary library, BookmarkImport result, string source, long now, Func<string> newId)
	{
		var folderTitle = $"Imported from {source}";
		var bar = MergeChildren(Library.Favourites(library), result.Bar, (existing, incoming) => existing.Title == incoming.Title && existing.ImportedFrom == source, source, now, newId);
		var merged = Library.WithFavourites(library, bar.Children, now);
		if (result.Other.Count == 0) return new MergeOutcome(merged, bar.Added, 0, folderTitle);

		var index = merged.Bookmarks.ToList().FindIndex(n => n is BookmarkFolder { ImportedFrom: var from } && from == source);
		if (index == -1)
		{
			var id = newId();
			var built = ToLibraryNodes(result.Other, source, now, newId);
			var imported = new BookmarkFolder(id, folderTitle, built.Children, now, source);
			return new MergeOutcome(merged with { Bookmarks = [.. merged.Bookmarks, imported] }, bar.Added, built.Added, folderTitle);
		}

		var existing = (BookmarkFolder)merged.Bookmarks[index];
		var inner = MergeChildren(existing.Children, result.Other, (e, i) => e.Title == i.Title, source, now, newId);
		if (inner.Added == 0) return new MergeOutcome(merged, bar.Added, 0, folderTitle);
		var bookmarks = merged.Bookmarks.ToList();
		bookmarks[index] = existing with { Children = inner.Children, UpdatedAt = now };
		return new MergeOutcome(merged with { Bookmarks = bookmarks }, bar.Added, inner.Added, folderTitle);
	}

	// Ids are handed out parent first, so the order is the same on every port.
	private static Merged ToLibraryNodes(IEnumerable<ImportedNode> nodes, string source, long now, Func<string> newId)
	{
		var added = 0;
		BookmarkNode Build(ImportedNode node)
		{
			if (node is ImportedLink link)
			{
				added++;
				return new BookmarkLink(newId(), link.Title, link.Url, now, source);
			}
			var folder = (ImportedFolder)node;
			var id = newId();
			return new BookmarkFolder(id, folder.Title, folder.Children.Select(Build).ToList(), now, source);
		}
		var children = nodes.Select(Build).ToList();
		return new Merged(children, added);
	}

	// Adds what isn't there yet and touches nothing else. A link is "there" when a direct child has its address; a folder when `matches`
	// finds one, and then we go one level down. The user's own edits (renames, moves, extra links) stay.
	private static Merged MergeChildren(IReadOnlyList<BookmarkNode> existing, IReadOnlyList<ImportedNode> incoming, FolderMatch matches, string source, long now, Func<string> newId)
	{
		var urls = existing.OfType<BookmarkLink>().Select(l => l.Url).ToHashSet();
		var children = existing.ToList();
		// Folders match only nodes from before this merge, each at most once, so two same-titled incoming folders never merge into each other.
		var taken = new HashSet<int>();
		var added = 0;
		foreach (var node in incoming)
		{
			if (node is ImportedLink link)
			{
				if (urls.Contains(link.Url)) continue;
				added += Append(children, node, source, now, newId);
				continue;
			}
			var folder = (ImportedFolder)node;
			var index = FindMatch(existing, taken, folder, matches);
			if (index == -1)
			{
				added += Append(children, node, source, now, newId);
				continue;
			}
			taken.Add(index);
			var match = (BookmarkFolder)children[index];
			var inner = MergeChildren(match.Children, folder.Children, matches, source, now, newId);
			if (inner.Added == 0) continue;
			children[index] = match with { Children = inner.Children, UpdatedAt = now };
			added += inner.Added;
		}
		return new Merged(children, added);
	}

	private static int FindMatch(IReadOnlyList<BookmarkNode> existing, HashSet<int> taken, ImportedFolder incoming, FolderMatch matches)
	{
		for (var i = 0; i < existing.Count; i++)
			if (!taken.Contains(i) && existing[i] is BookmarkFolder folder && matches(folder, incoming)) return i;
		return -1;
	}

	private static int Append(List<BookmarkNode> children, ImportedNode node, string source, long now, Func<string> newId)
	{
		var built = ToLibraryNodes([node], source, now, newId);
		children.AddRange(built.Children);
		return built.Added;
	}
}
