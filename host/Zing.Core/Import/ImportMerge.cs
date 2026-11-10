namespace Zing.Core.Import;

public sealed record MergeOutcome(BrowserLibrary Library, int AddedToBar, int AddedToFolder, string FolderTitle);

/// <summary>The C# twin of mergeImport in src/shared/import/importResult.ts, held to it by merge.vectors.json.</summary>
public static class ImportMerge
{
	/// <param name="Children">The same list as the existing children when nothing changed.</param>
	private sealed record Merged(IReadOnlyList<BookmarkNode> Children, int Added);

	private delegate bool FolderMatch(BookmarkFolder existing, ImportedFolder incoming);

	/// <summary>
	/// Bar items join the Favourites bar; everything else lands in one "Imported from &lt;source&gt;" folder, found again by its importedFrom mark
	/// even if renamed or moved. Re-importing only adds: nothing is ever removed, renamed or reordered, and nothing the source imported before is
	/// added again. When nothing is added, <paramref name="library"/> itself comes back.
	/// </summary>
	public static MergeOutcome Merge(BrowserLibrary library, BookmarkImport result, string source, long now, Func<string> newId)
	{
		var folderTitle = $"Imported from {source}";
		var imported = ImportedUrls(library.Bookmarks, source, []);
		var bar = Library.Favourites(library);
		var barMerge = MergeChildren(bar, result.Bar, (existing, incoming) => existing.Title == incoming.Title && existing.ImportedFrom == source, imported, source, now, newId);
		var merged = ReferenceEquals(barMerge.Children, bar) ? library : Library.WithFavourites(library, barMerge.Children, now);
		var unchanged = new MergeOutcome(merged, barMerge.Added, 0, folderTitle);
		if (result.Other.Count == 0) return unchanged;

		if (FindImportFolder(merged.Bookmarks, source, parentMarked: false) is not { } existing)
		{
			var other = WithoutImported(result.Other, imported);
			if (other.Count == 0) return unchanged;
			var id = newId();
			var built = ToLibraryNodes(other, source, now, newId);
			var folder = new BookmarkFolder(id, folderTitle, built.Children, now, source);
			return unchanged with { Library = merged with { Bookmarks = [.. merged.Bookmarks, folder] }, AddedToFolder = built.Added };
		}

		var inner = MergeChildren(existing.Children, result.Other, (e, i) => e.Title == i.Title, imported, source, now, newId);
		if (ReferenceEquals(inner.Children, existing.Children)) return unchanged;
		var bookmarks = ReplaceFolder(merged.Bookmarks, existing with { Children = inner.Children, UpdatedAt = now });
		return unchanged with { Library = merged with { Bookmarks = bookmarks }, AddedToFolder = inner.Added };
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

	// Every URL this source already put in the library, wherever the user has since moved it, so a re-import never adds it twice.
	private static HashSet<string> ImportedUrls(IEnumerable<BookmarkNode> nodes, string source, HashSet<string> into)
	{
		foreach (var node in nodes)
		{
			if (node is BookmarkFolder folder) ImportedUrls(folder.Children, source, into);
			else if (node is BookmarkLink { ImportedFrom: var from, Url: var url } && from == source) into.Add(url);
		}
		return into;
	}

	// Drops links already imported. A folder whose links were all imported goes too; one that never had links stays.
	private static List<ImportedNode> WithoutImported(IEnumerable<ImportedNode> nodes, HashSet<string> imported)
	{
		List<ImportedNode> kept = [];
		foreach (var node in nodes)
		{
			if (node is ImportedLink link)
			{
				if (!imported.Contains(link.Url)) kept.Add(link);
				continue;
			}
			var folder = (ImportedFolder)node;
			var children = WithoutImported(folder.Children, imported);
			if (children.Count == 0 && BookmarkImport.CountLinks(folder.Children) > 0) continue;
			kept.Add(folder with { Children = children });
		}
		return kept;
	}

	// The first folder marked with the source whose parent isn't, depth first, outside Favourites: the import folder, wherever the user put it.
	private static BookmarkFolder? FindImportFolder(IEnumerable<BookmarkNode> nodes, string source, bool parentMarked)
	{
		foreach (var node in nodes)
		{
			if (node is not BookmarkFolder folder || folder.Id == Library.FavouritesId) continue;
			var marked = folder.ImportedFrom == source;
			if (marked && !parentMarked) return folder;
			if (FindImportFolder(folder.Children, source, marked) is { } found) return found;
		}
		return null;
	}

	// Copies only the folders on the way down to the replaced one.
	private static IReadOnlyList<BookmarkNode> ReplaceFolder(IReadOnlyList<BookmarkNode> nodes, BookmarkFolder replacement)
	{
		List<BookmarkNode>? result = null;
		for (var i = 0; i < nodes.Count; i++)
		{
			if (nodes[i] is not BookmarkFolder folder) continue;
			BookmarkNode next;
			if (folder.Id == replacement.Id) next = replacement;
			else
			{
				var children = ReplaceFolder(folder.Children, replacement);
				if (ReferenceEquals(children, folder.Children)) continue;
				next = folder with { Children = children };
			}
			result ??= [.. nodes];
			result[i] = next;
		}
		return result ?? nodes;
	}

	// Adds what isn't there yet and touches nothing else. A link is "there" when a direct child has its address or the source imported it before;
	// a folder when `matches` finds one, and then we go one level down. The user's own edits (renames, moves, extra links) stay.
	private static Merged MergeChildren(IReadOnlyList<BookmarkNode> existing, IReadOnlyList<ImportedNode> incoming, FolderMatch matches, HashSet<string> imported, string source, long now, Func<string> newId)
	{
		var urls = existing.OfType<BookmarkLink>().Select(l => l.Url).ToHashSet();
		var children = existing.ToList();
		// Folders match only nodes from before this merge, each at most once, so two same-titled incoming folders never merge into each other.
		var taken = new HashSet<int>();
		var changed = false;
		var added = 0;
		foreach (var node in incoming)
		{
			if (node is ImportedLink link)
			{
				if (urls.Contains(link.Url) || imported.Contains(link.Url)) continue;
				added += Append(children, [node], source, now, newId);
				changed = true;
				continue;
			}
			var folder = (ImportedFolder)node;
			var index = FindMatch(existing, taken, folder, matches);
			if (index == -1)
			{
				var pruned = WithoutImported([folder], imported);
				if (pruned.Count == 0) continue;
				added += Append(children, pruned, source, now, newId);
				changed = true;
				continue;
			}
			taken.Add(index);
			var match = (BookmarkFolder)children[index];
			var inner = MergeChildren(match.Children, folder.Children, matches, imported, source, now, newId);
			if (ReferenceEquals(inner.Children, match.Children)) continue;
			children[index] = match with { Children = inner.Children, UpdatedAt = now };
			added += inner.Added;
			changed = true;
		}
		return new Merged(changed ? children : existing, added);
	}

	private static int FindMatch(IReadOnlyList<BookmarkNode> existing, HashSet<int> taken, ImportedFolder incoming, FolderMatch matches)
	{
		for (var i = 0; i < existing.Count; i++)
			if (!taken.Contains(i) && existing[i] is BookmarkFolder folder && matches(folder, incoming)) return i;
		return -1;
	}

	private static int Append(List<BookmarkNode> children, IEnumerable<ImportedNode> nodes, string source, long now, Func<string> newId)
	{
		var built = ToLibraryNodes(nodes, source, now, newId);
		children.AddRange(built.Children);
		return built.Added;
	}
}
