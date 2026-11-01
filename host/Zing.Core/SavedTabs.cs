namespace Zing.Core;

/// <summary>
/// Favourites and pinned items as saved tabs (docs/phase-4.md, part B), as in Arc and Dia. Each item in the
/// Favourites or Pinned folder owns at most one tab per window: opening the item switches to that tab, or starts
/// one at the item's address. Closing the tab keeps the item. The item is saved like any bookmark; its page is
/// not, so the tab always starts fresh and Zing still keeps no browsing data.
/// </summary>
public sealed class SavedTabs : IDisposable
{
	private readonly TabService _tabs;
	private readonly LibraryStore _library;
	private readonly Action _unsubscribe;

	public SavedTabs(TabService tabs, LibraryStore library)
	{
		_tabs = tabs;
		_library = library;
		// An item removed (from the rail, the Bookmarks panel, another window) lets its tab go free.
		_unsubscribe = library.Subscribe(_ => UntieRemoved());
	}

	/// <summary>Switches to the item's tab, or opens one at its address.</summary>
	/// <exception cref="InvalidLibraryActionException">The item is a folder, or gone.</exception>
	public void Open(string itemId)
	{
		if (!_library.IsSavedTab(itemId) || _library.UrlOf(itemId) is not { } url) throw new InvalidLibraryActionException($"{itemId} is not a saved tab");
		_tabs.OpenItem(itemId, url);
	}

	/// <summary>
	/// Saves a tab into a folder of Favourites or Pinned at a place, tied to it. A tab that already has an item
	/// moves that item there instead, so dragging a saved tab between sections keeps one item.
	/// </summary>
	public void Save(string tabId, string folderId, int at)
	{
		var tab = _tabs.Get(tabId);
		if (tab.ItemId is { } itemId && _library.IsSavedTab(itemId))
		{
			_library.Apply(new MoveIntoAction(itemId, folderId, at));
			return;
		}
		var id = _library.AddLink(folderId, at, TitleOf(tab), tab.Url);
		_tabs.Bind(tabId, id);
	}

	/// <summary>Pin Tab: the tab becomes the last item in Pinned.</summary>
	public void Pin(string tabId) => Save(tabId, Library.PinnedId, int.MaxValue);

	/// <summary>Unpin Tab: its item goes, and the tab stays open as an ordinary tab.</summary>
	public void Unpin(string tabId)
	{
		if (_tabs.Get(tabId).ItemId is { } itemId) _library.Apply(new RemoveAction([itemId]));
		else _tabs.Unpin(tabId);
	}

	/// <summary>
	/// Add to Favourites (Ctrl+D): the tab becomes the last favourite. When its address is already a favourite, the tab
	/// is tied to that item instead, unless the item has a tab already; then this one stays as it is.
	/// </summary>
	public AddOutcome Favourite(string tabId)
	{
		var tab = _tabs.Get(tabId);
		if (tab.ItemId is not null) return AddOutcome.Exists;
		var existing = Library.Favourites(_library.Snapshot).OfType<BookmarkLink>().FirstOrDefault(l => l.Url == tab.Url);
		if (existing is null)
		{
			Save(tabId, Library.FavouritesId, int.MaxValue);
			return AddOutcome.Added;
		}
		if (_tabs.IdForItem(existing.Id) is null) _tabs.Bind(tabId, existing.Id);
		return AddOutcome.Exists;
	}

	/// <summary>Back to the item's saved address.</summary>
	public void Reset(string tabId)
	{
		if (_tabs.Get(tabId).ItemId is not { } itemId || _library.UrlOf(itemId) is not { } url) return;
		_tabs.Navigate(tabId, url);
	}

	/// <summary>Saves the tab's current page as its item's address and title.</summary>
	public void Update(string tabId)
	{
		var tab = _tabs.Get(tabId);
		if (tab.ItemId is not { } itemId) return;
		_library.SetLink(itemId, TitleOf(tab), tab.Url);
	}

	/// <summary>Whether the tab has moved off its item's address, so Reset has somewhere to go.</summary>
	public bool HasLeftItsAddress(string tabId) =>
		_tabs.Get(tabId) is { ItemId: { } itemId } tab && _library.UrlOf(itemId) is { } url && url != tab.Url;

	/// <summary>
	/// Pinned tabs from a session saved before saved tabs existed become pinned items, so the rail shows them where
	/// they were pinned. Call once, after a restore.
	/// </summary>
	public void AdoptPinnedTabs()
	{
		foreach (var tab in _tabs.Records.Where(t => t.Pinned && t.ItemId is null).ToList()) Pin(tab.Id);
	}

	public void Dispose() => _unsubscribe();

	private void UntieRemoved()
	{
		foreach (var tab in _tabs.Records.Where(t => t.ItemId is { } id && !_library.IsSavedTab(id)).ToList()) _tabs.Unbind(tab.Id);
	}

	private static string TitleOf(TabRecord tab)
	{
		if (!string.IsNullOrWhiteSpace(tab.Title) && tab.Title != TabStore.NewTabTitle) return tab.Title.Trim();
		return Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : tab.Url;
	}
}
