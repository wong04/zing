using System.Text.Json;

namespace Zing.Core;

public sealed class LibraryFileException(string path, string detail, Exception? inner = null)
	: Exception($"Could not read the library from {path}: {detail}", inner)
{
	public string Path { get; } = path;
}

public sealed class InvalidLibraryActionException(string detail) : Exception($"invalid library action: {detail}");

/// <summary>Edits the side panel and the Favourites bar can ask for (src/shared/ipc.ts LibraryAction). `To` is an insertion point in the item's own list.</summary>
public abstract record LibraryAction;

public sealed record RenameAction(string Id, string Title) : LibraryAction;

public sealed record RemoveAction(IReadOnlyList<string> Ids) : LibraryAction;

public sealed record NewFolderAction(IReadOnlyList<string> Ids) : LibraryAction;

public sealed record MoveAction(string Id, int To) : LibraryAction;

public sealed record SetReadAction(string Id, bool Read) : LibraryAction;

public enum AddOutcome
{
	Added,
	Exists,
}

/// <summary>
/// Bookmarks, the Favourites bar and the Reading List, shared by every window.
/// The one store the browser writes on purpose: each entry is something the
/// user chose to keep, and browsing itself adds nothing.
/// </summary>
public sealed class LibraryStore
{
	public const string NewFolderTitle = "New Folder";

	private readonly Action<BrowserLibrary> _save;
	private readonly Func<long> _now;
	private readonly Func<string> _newId;
	private readonly List<Action<BrowserLibrary>> _listeners = [];

	public LibraryStore(BrowserLibrary initial, Action<BrowserLibrary> save, Func<long> now, Func<string> newId)
	{
		Snapshot = initial;
		_save = save;
		_now = now;
		_newId = newId;
	}

	public BrowserLibrary Snapshot { get; private set; }

	/// <summary>Returns the function that unsubscribes.</summary>
	public Action Subscribe(Action<BrowserLibrary> listener)
	{
		_listeners.Add(listener);
		return () => _listeners.Remove(listener);
	}

	public AddOutcome AddFavourite(string title, string url)
	{
		var bar = Library.Favourites(Snapshot);
		if (bar.Any(n => n is BookmarkLink link && link.Url == url)) return AddOutcome.Exists;
		var added = Library.Link(_newId(), title, url, _now());
		Set(Library.WithFavourites(Snapshot, [.. bar, added], _now()));
		return AddOutcome.Added;
	}

	public AddOutcome AddReading(string title, string url)
	{
		if (Snapshot.ReadingList.Any(i => i.Url == url)) return AddOutcome.Exists;
		var item = Library.Reading(_newId(), title, url, _now());
		Set(Snapshot with { ReadingList = [item, .. Snapshot.ReadingList] });
		return AddOutcome.Added;
	}

	/// <summary>The id of a folder this action created, so the panel can start renaming it.</summary>
	public string? Apply(LibraryAction action)
	{
		switch (action)
		{
			case RenameAction rename:
				Set(Snapshot with { Bookmarks = Library.Rename(rename.Id, rename.Title, Snapshot.Bookmarks, _now()) });
				return null;
			case RemoveAction remove:
				Set(new BrowserLibrary(
					Library.Remove(remove.Ids.ToHashSet(), Snapshot.Bookmarks),
					Snapshot.ReadingList.Where(i => !remove.Ids.Contains(i.Id)).ToList()));
				return null;
			case NewFolderAction newFolder:
				return NewFolder(newFolder.Ids);
			case MoveAction move:
				ApplyMove(move.Id, move.To);
				return null;
			case SetReadAction setRead:
				var time = _now();
				Set(Snapshot with { ReadingList = Snapshot.ReadingList.Select(i => i.Id == setRead.Id ? i with { IsRead = setRead.Read, UpdatedAt = time } : i).ToList() });
				return null;
			default:
				throw new InvalidLibraryActionException($"unhandled action {action.GetType().Name}");
		}
	}

	/// <summary>Where a bookmark or reading list item points.</summary>
	public string? UrlOf(string id)
	{
		var node = Library.Find(id, Snapshot.Bookmarks);
		if (node is not null) return node is BookmarkLink link ? link.Url : null;
		return Snapshot.ReadingList.FirstOrDefault(i => i.Id == id)?.Url;
	}

	public IReadOnlyList<BookmarkNode> ChildrenOf(string id) =>
		Library.Find(id, Snapshot.Bookmarks) is BookmarkFolder folder ? folder.Children : [];

	public bool IsFolderNode(string id) => Library.Find(id, Snapshot.Bookmarks) is BookmarkFolder;

	/// <summary>Every link under a node, depth-first: what Ctrl-clicking a bar folder opens.</summary>
	public IReadOnlyList<string> UrlsOf(string id) =>
		Library.Find(id, Snapshot.Bookmarks) is { } node ? Library.Urls(node) : [];

	private string NewFolder(IReadOnlyList<string> ids)
	{
		var id = _newId();
		if (ids.Count > 0)
		{
			var grouped = Library.GroupIntoFolder(ids.ToHashSet(), NewFolderTitle, Snapshot.Bookmarks, id, _now());
			if (grouped.FolderId is not null)
			{
				Set(Snapshot with { Bookmarks = grouped.Nodes });
				return grouped.FolderId;
			}
		}
		var created = Library.Folder(id, NewFolderTitle, [], _now());
		Set(Snapshot with { Bookmarks = [.. Snapshot.Bookmarks, created] });
		return created.Id;
	}

	private void ApplyMove(string id, int to)
	{
		var bookmarks = MoveNode(Snapshot.Bookmarks, id, to);
		if (bookmarks is not null)
		{
			Set(Snapshot with { Bookmarks = bookmarks });
			return;
		}
		var from = Snapshot.ReadingList.ToList().FindIndex(i => i.Id == id);
		if (from < 0) return;
		Set(Snapshot with { ReadingList = Library.Move(Snapshot.ReadingList, from, to) });
	}

	private void Set(BrowserLibrary next)
	{
		Snapshot = next;
		_save(next);
		foreach (var listener in _listeners.ToList()) listener(next);
	}

	/// <summary>`to` is an insertion point in the node's own sibling list (the `Move` convention). Null when `id` is not in `nodes`.</summary>
	public static IReadOnlyList<BookmarkNode>? MoveNode(IReadOnlyList<BookmarkNode> nodes, string id, int to)
	{
		var from = nodes.ToList().FindIndex(n => n.Id == id);
		if (from >= 0) return Library.Move(nodes, from, to);
		for (var index = 0; index < nodes.Count; index++)
		{
			if (nodes[index] is not BookmarkFolder folder) continue;
			var children = MoveNode(folder.Children, id, to);
			if (children is null) continue;
			var copy = nodes.ToList();
			copy[index] = folder with { Children = children };
			return copy;
		}
		return null;
	}

	/// <summary>Throws InvalidDataException naming the first malformed field; the Favourites folder is restored if the file lacks it.</summary>
	public static BrowserLibrary ParseLibrary(JsonElement raw, long now)
	{
		if (raw.ValueKind != JsonValueKind.Object
			|| !raw.TryGetProperty("bookmarks", out var bookmarkList) || bookmarkList.ValueKind != JsonValueKind.Array
			|| !raw.TryGetProperty("readingList", out var readingList) || readingList.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidDataException("expected an object with bookmarks and readingList lists");
		}
		var bookmarks = bookmarkList.EnumerateArray().Select((n, i) => Library.ParseNode(n, $"bookmarks[{i}]")).ToList();
		var reading = readingList.EnumerateArray().Select((n, i) => Library.ParseReading(n, $"readingList[{i}]")).ToList();
		var library = new BrowserLibrary(bookmarks, reading);
		return bookmarks.Any(n => n.Id == Library.FavouritesId) ? library : Library.WithFavourites(library, [], now);
	}

	/// <summary>No file means first run. Anything unreadable is an error, never a silent empty library that would overwrite it.</summary>
	public static BrowserLibrary LoadLibraryFile(string path, long now)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
		{
			return Library.EmptyLibrary(now);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new LibraryFileException(path, error.Message, error);
		}
		try
		{
			using var document = JsonDocument.Parse(text);
			return ParseLibrary(document.RootElement, now);
		}
		catch (Exception error) when (error is JsonException or InvalidDataException)
		{
			throw new LibraryFileException(path, error.Message, error);
		}
	}

	public static LibraryAction ParseLibraryAction(JsonElement raw)
	{
		if (raw.ValueKind != JsonValueKind.Object) throw new InvalidLibraryActionException("not an object");
		var type = Library.Str(raw, "type");
		switch (type)
		{
			case "rename":
				return new RenameAction(
					Library.Str(raw, "id") ?? throw new InvalidLibraryActionException("rename needs id and title"),
					Library.Str(raw, "title") ?? throw new InvalidLibraryActionException("rename needs id and title"));
			case "remove":
				return new RemoveAction(Strings(raw, "ids"));
			case "newFolder":
				return new NewFolderAction(Strings(raw, "ids"));
			case "move":
				if (Library.Str(raw, "id") is not { } moveId || !TryInteger(raw, "to", out var to))
					throw new InvalidLibraryActionException("move needs id and an integer to");
				return new MoveAction(moveId, to);
			case "setRead":
				if (Library.Str(raw, "id") is not { } readId || !Library.TryBool(raw, "read", out var read))
					throw new InvalidLibraryActionException("setRead needs id and read");
				return new SetReadAction(readId, read);
			default:
				throw new InvalidLibraryActionException($"unknown type {type ?? "undefined"}");
		}
	}

	private static List<string> Strings(JsonElement raw, string name)
	{
		if (!raw.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
			throw new InvalidLibraryActionException($"{name} must be a list of strings");
		return list.EnumerateArray().Select(v => v.GetString()!).ToList();
	}

	private static bool TryInteger(JsonElement raw, string name, out int value)
	{
		value = 0;
		if (!raw.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d)) return false;
		if (d != Math.Floor(d) || Math.Abs(d) > int.MaxValue) return false;
		value = (int)d;
		return true;
	}

	/// <summary>`ZING_LIBRARY_FILE` lets harnesses use a scratch file so they never touch real bookmarks. `createSave` builds the writer for the resolved path.</summary>
	public static LibraryStore Open(string defaultPath, Func<string, Action<BrowserLibrary>> createSave, Func<long> now, Func<string> newId)
	{
		var env = Environment.GetEnvironmentVariable("ZING_LIBRARY_FILE");
		var path = string.IsNullOrEmpty(env) ? defaultPath : env;
		return new LibraryStore(LoadLibraryFile(path, now()), createSave(path), now, newId);
	}
}
