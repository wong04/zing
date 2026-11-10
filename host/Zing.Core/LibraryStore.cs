using System.Text.Json;
using Zing.Core.Import;

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

/// <summary>Moves a bookmark into a folder (its own or another), before the child now at `To` there.</summary>
public sealed record MoveIntoAction(string Id, string Parent, int To) : LibraryAction;

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
			case MoveIntoAction moveInto:
				Set(Snapshot with { Bookmarks = MoveInto(Snapshot.Bookmarks, moveInto.Id, moveInto.Parent, moveInto.To) });
				return null;
			case SetReadAction setRead:
				var time = _now();
				Set(Snapshot with { ReadingList = Snapshot.ReadingList.Select(i => i.Id == setRead.Id ? i with { IsRead = setRead.Read, UpdatedAt = time } : i).ToList() });
				return null;
			default:
				throw new InvalidLibraryActionException($"unhandled action {action.GetType().Name}");
		}
	}

	/// <summary>Adds a link to a folder at a position (clamped to its children), and returns its id: a tab saved to the rail.</summary>
	/// <exception cref="InvalidLibraryActionException">The parent isn't a folder.</exception>
	public string AddLink(string parent, int at, string title, string url)
	{
		if (Library.Find(parent, Snapshot.Bookmarks) is not BookmarkFolder) throw new InvalidLibraryActionException($"{parent} is not a folder");
		var link = Library.Link(_newId(), title, url, _now());
		Set(Snapshot with { Bookmarks = Insert(Snapshot.Bookmarks, parent, at, link) });
		return link.Id;
	}

	/// <summary>Points a link at a new page, keeping its place and id: "save this page here" for a saved tab.</summary>
	public void SetLink(string id, string title, string url)
	{
		if (Library.Find(id, Snapshot.Bookmarks) is not BookmarkLink) throw new InvalidLibraryActionException($"{id} is not a link");
		var time = _now();
		Set(Snapshot with { Bookmarks = Replace(Snapshot.Bookmarks, id, node => ((BookmarkLink)node) with { Title = title, Url = url, UpdatedAt = time }) });
	}

	/// <summary>Whether a node sits in Favourites or Pinned, at any depth: a saved tab's item.</summary>
	public bool IsSavedTab(string id) =>
		Library.Find(id, [.. Library.Favourites(Snapshot), .. Library.Pinned(Snapshot)]) is BookmarkLink;

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

	/// <summary>Merges what a parser read into the library: it only adds (see <see cref="ImportMerge.Merge"/>).</summary>
	public MergeOutcome Import(BookmarkImport result, string source)
	{
		var outcome = ImportMerge.Merge(Snapshot, result, source, _now(), _newId);
		if (!ReferenceEquals(outcome.Library, Snapshot)) Set(outcome.Library);
		return outcome;
	}

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

	/// <summary>Takes a node out of wherever it is and puts it into a folder before the child now at `to`. Fixed folders stay put, and a folder never goes inside itself.</summary>
	public static IReadOnlyList<BookmarkNode> MoveInto(IReadOnlyList<BookmarkNode> nodes, string id, string parent, int to)
	{
		if (Library.IsFixedFolder(id)) throw new InvalidLibraryActionException("a fixed folder can't be moved");
		var node = Library.Find(id, nodes) ?? throw new InvalidLibraryActionException($"no bookmark {id}");
		if (Library.Find(parent, nodes) is not BookmarkFolder target) throw new InvalidLibraryActionException($"{parent} is not a folder");
		if (node is BookmarkFolder moving && (moving.Id == parent || Library.Find(parent, moving.Children) is not null)) throw new InvalidLibraryActionException("a folder can't go inside itself");
		// The place counts the node itself when it is already in that folder before the place.
		var from = target.Children.ToList().FindIndex(n => n.Id == id);
		var at = from >= 0 && from < to ? to - 1 : to;
		return Insert(Library.Remove(new HashSet<string> { id }, nodes), parent, at, node);
	}

	private static IReadOnlyList<BookmarkNode> Insert(IReadOnlyList<BookmarkNode> nodes, string parent, int at, BookmarkNode node) =>
		Replace(nodes, parent, found =>
		{
			var folder = (BookmarkFolder)found;
			var children = folder.Children.ToList();
			children.Insert(Math.Clamp(at, 0, children.Count), node);
			return folder with { Children = children };
		});

	private static IReadOnlyList<BookmarkNode> Replace(IReadOnlyList<BookmarkNode> nodes, string id, Func<BookmarkNode, BookmarkNode> change) =>
		nodes.Select(n => n.Id == id ? change(n) : n is BookmarkFolder f ? f with { Children = Replace(f.Children, id, change) } : n).ToList();

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

	/// <summary>Throws InvalidDataException naming the first malformed field; a fixed folder the file lacks (Favourites, Pinned) is added.</summary>
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
		return Library.WithFixedFolders(library, now);
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
			case "moveInto":
				if (Library.Str(raw, "id") is not { } intoId || Library.Str(raw, "parent") is not { } parent || !TryInteger(raw, "to", out var intoAt))
					throw new InvalidLibraryActionException("moveInto needs id, parent and an integer to");
				return new MoveIntoAction(intoId, parent, intoAt);
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

	/// <summary>
	/// The library for this launch. An invalid file is moved aside and the library starts empty; an unreadable one is left alone
	/// (UserData.Load). Either way startup goes on and the problem is returned.
	/// </summary>
	public static Loaded<BrowserLibrary> LoadLibraryOrRecover(string path, long now, DateTime stamp) =>
		UserData.Load(path, () => LoadLibraryFile(path, now), () => Library.EmptyLibrary(now), stamp);

	/// <summary>
	/// `ZING_LIBRARY_FILE` lets harnesses use a scratch file so they never touch real bookmarks. `createSave` builds the writer for the resolved path.
	/// When a file that couldn't be read is still in place, this launch's edits aren't saved, so they never overwrite bookmarks that may be fine.
	/// </summary>
	public static Loaded<LibraryStore> Open(string defaultPath, Func<string, Action<BrowserLibrary>> createSave, Func<long> now, Func<string> newId, DateTime stamp)
	{
		var env = Environment.GetEnvironmentVariable("ZING_LIBRARY_FILE");
		var path = string.IsNullOrEmpty(env) ? defaultPath : env;
		var loaded = LoadLibraryOrRecover(path, now(), stamp);
		Action<BrowserLibrary> save = loaded.Problem is { OriginalInPlace: true } ? _ => { } : createSave(path);
		return new Loaded<LibraryStore>(new LibraryStore(loaded.Value, save, now, newId), loaded.Problem);
	}
}
