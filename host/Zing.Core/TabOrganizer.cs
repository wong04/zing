using System.Globalization;

namespace Zing.Core;

/// <summary>
/// Everything the organiser and the auto-closer need to know about a tab, without a page
/// (src/shared/browser/tabOrganizer.ts TabSnapshot). Times are epoch milliseconds.
/// </summary>
/// <param name="Domain">Registrable domain, e.g. google.com for docs.google.com.</param>
/// <param name="Group">Which dropdown the tab joins.</param>
/// <param name="GroupName">What that dropdown is called.</param>
public sealed record TabSnapshot(
	string Id,
	string Title,
	string Domain,
	string Group,
	string GroupName,
	long LastAccessed,
	long CreatedAt,
	bool IsPinned,
	bool IsActive)
{
	public static TabSnapshot Make(
		string id,
		string title,
		string domain,
		long lastAccessed,
		long createdAt,
		string? group = null,
		string? groupName = null,
		bool isPinned = false,
		bool isActive = false) =>
		new(id, title, domain, group ?? domain, groupName ?? group ?? domain, lastAccessed, createdAt, isPinned, isActive);

	/// <summary>BrowserTab.snapshot: a tab with no site yet (a blank tab) has an empty domain and group.</summary>
	public static TabSnapshot Of(TabRecord tab, bool isActive)
	{
		if (!Uri.TryCreate(tab.Url, UriKind.Absolute, out var url) || url.IdnHost.Length == 0)
			return Make(tab.Id, tab.Title, "", tab.LastActiveAt, tab.CreatedAt, isPinned: tab.Pinned, isActive: isActive);
		var group = SiteGroup.OfUri(url);
		return Make(tab.Id, tab.Title, RegistrableDomain.Of(url.IdnHost), tab.LastActiveAt, tab.CreatedAt, group.Key, group.Name, tab.Pinned, isActive);
	}
}

/// <summary>How the tab strip keeps itself in order.</summary>
public enum TabOrganization
{
	Manual,
	Domain,
	Alphabetical,
	RecentlyUsed,
	Newest,
}

public static class TabOrganizations
{
	/// <summary>The setting value, as src/shared/browser/tabOrganizer.ts spells it.</summary>
	public static string ToSetting(this TabOrganization organization) => organization switch
	{
		TabOrganization.Manual => "manual",
		TabOrganization.Domain => "domain",
		TabOrganization.Alphabetical => "alphabetical",
		TabOrganization.RecentlyUsed => "recentlyUsed",
		TabOrganization.Newest => "newest",
		_ => throw new ArgumentOutOfRangeException(nameof(organization), organization, null),
	};

	public static TabOrganization Parse(string setting) => setting switch
	{
		"manual" => TabOrganization.Manual,
		"domain" => TabOrganization.Domain,
		"alphabetical" => TabOrganization.Alphabetical,
		"recentlyUsed" => TabOrganization.RecentlyUsed,
		"newest" => TabOrganization.Newest,
		_ => throw new ArgumentException($"Unknown tab organization '{setting}'", nameof(setting)),
	};

	/// <summary>
	/// Sorted arrangements have no room for a hand-placed tab, so a drag there switches the strip to
	/// manual (and says so) rather than snapping back.
	/// </summary>
	public static bool DragSwitchesToManual(this TabOrganization organization) =>
		organization is TabOrganization.Alphabetical or TabOrganization.RecentlyUsed or TabOrganization.Newest;
}

/// <summary>One entry per site, for the Dropdown tab style (src/shared/browser/tabOrganizer.ts Stack).</summary>
/// <param name="Domain">The group: a domain or a Google product. Empty for a new tab.</param>
/// <param name="Name">What the stack is called on screen.</param>
/// <param name="Key">Stable identity for animation: the group, or a new tab's own id.</param>
public sealed record Stack(string Domain, IReadOnlyList<string> Ids, string Name, string Key)
{
	public static Stack Make(string domain, IReadOnlyList<string> ids, string name) =>
		new(domain, ids, name, domain.Length == 0 ? (ids.Count > 0 ? ids[0] : "") : domain);
}

public sealed class TabArrangementException(string detail) : Exception(detail);

/// <summary>src/shared/browser/tabOrganizer.ts: arrangements, stacks and the drag operations on them.</summary>
public static class TabOrganizer
{
	// Case- and diacritic-insensitive so "iPhone" and "Álvaro" land where a person would look for them.
	private const CompareOptions TitleCompare = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
	// Like Swift's localizedCaseInsensitiveCompare: case folds, accents still count.
	private const CompareOptions NameCompare = CompareOptions.IgnoreCase;

	/// <summary>
	/// The ids in the order the strip should show them. Pinned tabs hold their relative order at the
	/// front; only the unpinned remainder is sorted.
	/// </summary>
	public static List<string> Order(IReadOnlyList<TabSnapshot> tabs, TabOrganization strategy)
	{
		var pinned = tabs.Where(t => t.IsPinned);
		var loose = tabs.Where(t => !t.IsPinned).ToList();
		return pinned.Concat(SortLoose(loose, strategy)).Select(t => t.Id).ToList();
	}

	private static IEnumerable<TabSnapshot> SortLoose(List<TabSnapshot> loose, TabOrganization strategy) => strategy switch
	{
		TabOrganization.Manual => loose,
		TabOrganization.Alphabetical => loose.OrderBy(t => t.Title, Comparer(TitleCompare)),
		TabOrganization.RecentlyUsed => loose.OrderByDescending(t => t.LastAccessed),
		TabOrganization.Newest => loose.OrderByDescending(t => t.CreatedAt),
		TabOrganization.Domain => GroupByDomain(loose),
		_ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
	};

	private static Comparer<string> Comparer(CompareOptions options) =>
		Comparer<string>.Create((a, b) => CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, options));

	/// <summary>Sites appear in the order you first opened one, and tabs keep their order within a site.</summary>
	private static IEnumerable<TabSnapshot> GroupByDomain(List<TabSnapshot> tabs)
	{
		// Tabs with no domain yet (a blank new tab) are their own bucket so they don't pile under the first real site.
		var order = new List<string>();
		var buckets = new Dictionary<string, List<TabSnapshot>>(StringComparer.Ordinal);
		foreach (var tab in tabs)
		{
			var key = tab.Domain.Length == 0 ? "￿new" : tab.Domain;
			if (!buckets.TryGetValue(key, out var bucket))
			{
				bucket = [];
				buckets[key] = bucket;
				order.Add(key);
			}
			bucket.Add(tab);
		}
		return order.SelectMany(key => buckets[key]);
	}

	/// <summary>
	/// The arrangement decides the order: this takes tabs already arranged, so a site sits where its
	/// first page falls and its pages keep their order. Pinned tabs aren't stacked.
	/// </summary>
	public static List<Stack> Stacks(IReadOnlyList<TabSnapshot> tabs)
	{
		var order = new List<string>();
		var groups = new Dictionary<string, (List<string> Ids, string Name, bool IsNew)>(StringComparer.Ordinal);
		foreach (var tab in tabs)
		{
			if (tab.IsPinned) continue;
			var isNew = tab.Group.Length == 0;
			var key = isNew ? $"new:{tab.Id}" : tab.Group;
			if (groups.TryGetValue(key, out var group))
			{
				group.Ids.Add(tab.Id);
				continue;
			}
			order.Add(key);
			groups[key] = ([tab.Id], tab.GroupName, isNew);
		}
		return order.Select(key =>
		{
			var group = groups[key];
			return group.IsNew ? Stack.Make("", group.Ids, "") : Stack.Make(key, group.Ids, group.Name);
		}).ToList();
	}

	/// <summary>A-Z is the exception: sites sort by name, with new tabs after them in their own order.</summary>
	public static List<Stack> StacksByName(IReadOnlyList<TabSnapshot> tabs)
	{
		var all = Stacks(tabs);
		var sites = all.Where(s => s.Domain.Length != 0).OrderBy(s => s.Name, Comparer(NameCompare));
		return sites.Concat(all.Where(s => s.Domain.Length == 0)).ToList();
	}

	/// <summary>The stacks as the Dropdown style shows them under an arrangement.</summary>
	public static List<Stack> DisplayedStacks(IReadOnlyList<TabSnapshot> tabs, TabOrganization organization) =>
		organization == TabOrganization.Alphabetical ? StacksByName(tabs) : Stacks(tabs);

	/// <summary>A stack dragged along the strip: the stacks in their new order, every page moving with it.</summary>
	public static List<List<string>> MovingStack(IReadOnlyList<IReadOnlyList<string>> stackIds, int from, int to)
	{
		var result = stackIds.Select(ids => ids.ToList()).ToList();
		if (!InRange(from, stackIds.Count) || !InRange(to, stackIds.Count) || from == to) return result;
		var moved = result[from];
		result.RemoveAt(from);
		result.Insert(to, moved);
		return result;
	}

	/// <summary>One id moved to <paramref name="index"/> within a list, clamped to the list.</summary>
	public static List<string> Moving(string id, IReadOnlyList<string> ids, int index)
	{
		var result = ids.ToList();
		var from = result.IndexOf(id);
		if (from < 0) return result;
		result.RemoveAt(from);
		result.Insert(Math.Min(Math.Max(index, 0), result.Count), id);
		return result;
	}

	/// <summary>The pinned run at the front of the strip.</summary>
	public static int PinnedCount(IReadOnlyList<TabSnapshot> tabs)
	{
		for (var i = 0; i < tabs.Count; i++)
			if (!tabs[i].IsPinned) return i;
		return tabs.Count;
	}

	/// <summary>
	/// BrowserModel.moveTab: a tab only moves within its own section (pinned or not); an index outside
	/// the strip changes nothing. Returns the strip's new order of ids.
	/// </summary>
	public static List<string> MoveTab(IReadOnlyList<TabSnapshot> tabs, int from, int to)
	{
		var ids = tabs.Select(t => t.Id).ToList();
		if (!InRange(from, tabs.Count) || !InRange(to, tabs.Count) || from == to) return ids;
		var pinned = PinnedCount(tabs);
		var clamped = tabs[from].IsPinned ? Math.Min(to, Math.Max(pinned - 1, 0)) : Math.Max(to, pinned);
		if (!InRange(clamped, tabs.Count) || clamped == from) return ids;
		var moved = ids[from];
		ids.RemoveAt(from);
		ids.Insert(clamped, moved);
		return ids;
	}

	/// <summary>
	/// BrowserModel.move(_:within:to:): a page dragged within its stack's dropdown. The stack's pages
	/// swap among the places they already hold. <paramref name="index"/> is the page's new position among them.
	/// </summary>
	public static List<string> MoveWithin(IReadOnlyList<TabSnapshot> tabs, string id, IReadOnlyList<string> stackIds, int index)
	{
		var ids = tabs.Select(t => t.Id).ToList();
		var members = stackIds.ToHashSet(StringComparer.Ordinal);
		var slots = Enumerable.Range(0, ids.Count).Where(slot => members.Contains(ids[slot])).ToList();
		var reordered = Moving(id, slots.Select(slot => ids[slot]).ToList(), index);
		for (var i = 0; i < slots.Count; i++) ids[slots[i]] = reordered[i];
		return ids;
	}

	/// <summary>
	/// Lays the unpinned tabs out as these stacks, in this order, pinned tabs in front. Refused unless
	/// it accounts for every tab exactly once. Returns the new order of ids.
	/// </summary>
	public static List<string> Arrange(IReadOnlyList<TabSnapshot> tabs, IReadOnlyList<IReadOnlyList<string>> stackIds)
	{
		var byId = tabs.ToDictionary(t => t.Id, StringComparer.Ordinal);
		var looseIds = stackIds.SelectMany(ids => ids).ToList();
		var pinnedIds = tabs.Where(t => t.IsPinned).Select(t => t.Id).ToList();
		if (looseIds.Any(id => !byId.ContainsKey(id))) throw new TabArrangementException("arrangement names a tab that is not open");
		if (looseIds.Distinct(StringComparer.Ordinal).Count() != looseIds.Count) throw new TabArrangementException("arrangement lists a tab more than once");
		if (looseIds.Any(id => byId[id].IsPinned)) throw new TabArrangementException("arrangement includes a pinned tab");
		if (pinnedIds.Count + looseIds.Count != tabs.Count) throw new TabArrangementException("arrangement leaves out an open tab");
		return pinnedIds.Concat(looseIds).ToList();
	}

	/// <summary>Before a hand-made change in a sorted arrangement: makes what is on screen the manual order.</summary>
	public static List<string> FreezeDisplayedOrder(IReadOnlyList<TabSnapshot> tabs, TabOrganization organization) =>
		Arrange(tabs, DisplayedStacks(tabs, organization).Select(s => s.Ids).ToList());

	private static bool InRange(int index, int count) => index >= 0 && index < count;
}

/// <summary>What the stack dropdown shows (src/shared/ipc.ts StackDropdownState).</summary>
public sealed record StackDropdownState(string Key, string Name, double AnchorX, IReadOnlyList<TabView> Tabs, string? ActiveId);

/// <summary>The pages of a stack (src/main/stack-dropdown.ts StackMembers).</summary>
public sealed record StackMembers(string Name, IReadOnlyList<TabView> Tabs, string? ActiveId)
{
	/// <summary>The live pages of a stack, or null once it holds fewer than two.</summary>
	public static StackMembers? Of(TabsState list, IReadOnlyList<Stack> stacks, string key)
	{
		var stack = stacks.FirstOrDefault(s => s.Key == key && s.Ids.Count > 1);
		if (stack is null) return null;
		var tabs = stack.Ids.SelectMany(id => list.Tabs.Where(t => t.Id == id)).ToList();
		return new StackMembers(stack.Name, tabs, list.ActiveId);
	}

	public StackDropdownState ToState(string key, double anchorX) => new(key, Name, anchorX, Tabs, ActiveId);
}
