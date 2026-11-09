using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>A key combination by physical key (KeyboardEvent.code), as in src/shared/commands/shortcut.ts.</summary>
public sealed record Shortcut(string Code, bool Ctrl, bool Shift, bool Alt, string Label);

public sealed record Command(string Id, string Label, IReadOnlyList<Shortcut> Shortcuts);

/// <summary>A key press as the host saw it, already mapped to a KeyboardEvent.code.</summary>
/// <param name="Repeat">The key was already down: the keyboard's auto-repeat of a held key.</param>
public readonly record struct KeyInput(string Code, bool Ctrl, bool Shift, bool Alt, bool Repeat = false);

/// <summary>The command registry (src/shared/commands/registry.ts), from the generated commands.json.</summary>
public static class Commands
{
	public static IReadOnlyList<Command> All { get; } = Load();

	private static readonly Dictionary<string, Command> ById = All.ToDictionary(c => c.Id, StringComparer.Ordinal);

	public static Command Get(string id) => ById.TryGetValue(id, out var command) ? command : throw new UnknownCommandException(id);

	/// <summary>The command a key press runs, if any (commandForInput).</summary>
	public static Command? ForInput(KeyInput input) =>
		All.FirstOrDefault(c => c.Shortcuts.Any(s => s.Code == input.Code && s.Ctrl == input.Ctrl && s.Shift == input.Shift && s.Alt == input.Alt));

	/// <summary>
	/// The KeyboardEvent.code of a Windows virtual-key, for the keys the
	/// registry uses; null for every other key, which no shortcut can match.
	/// </summary>
	public static string? CodeOf(int virtualKey) => virtualKey switch
	{
		>= 0x41 and <= 0x5A => $"Key{(char)virtualKey}",
		>= 0x30 and <= 0x39 => $"Digit{(char)virtualKey}",
		>= 0x70 and <= 0x7B => $"F{virtualKey - 0x6F}",
		0x60 => "Numpad0",
		0x6B => "NumpadAdd",
		0x6D => "NumpadSubtract",
		0xBB => "Equal",
		0xBD => "Minus",
		0xBC => "Comma",
		0xBE => "Period",
		0xDC => "Backslash",
		0x09 => "Tab",
		0x1B => "Escape",
		0x24 => "Home",
		0x21 => "PageUp",
		0x22 => "PageDown",
		0x25 => "ArrowLeft",
		0x27 => "ArrowRight",
		_ => null,
	};

	private static List<Command> Load() =>
		ZingJson.LoadDefaults("commands.json").AsArray().Select(c => new Command(
			c!["id"]!.GetValue<string>(),
			c["label"]!.GetValue<string>(),
			c["shortcuts"]!.AsArray().Select(s => new Shortcut(
				s!["code"]!.GetValue<string>(),
				s["ctrl"]!.GetValue<bool>(),
				s["shift"]!.GetValue<bool>(),
				s["alt"]!.GetValue<bool>(),
				s["label"]!.GetValue<string>())).ToList())).ToList();
}

/// <summary>
/// A platform-neutral menu, built and tested here and drawn by the host as a
/// native flyout. A separator has no label.
/// </summary>
public sealed record MenuItem(string? Label, Action? Click = null, bool Enabled = true, bool? Checked = null, IReadOnlyList<MenuItem>? Submenu = null, string? Accelerator = null)
{
	public static readonly MenuItem Separator = new((string?)null);

	public bool IsSeparator => Label is null;
}

/// <summary>What the menus need from the platform: the clipboard and the default browser.</summary>
public interface IShellActions
{
	void CopyText(string text);
	void OpenExternal(string url);
}

/// <summary>The menus of the chrome (menu.ts, tab-menus.ts, library-controller.ts).</summary>
public static class Menus
{
	/// <summary>The app menu, from the shared layout: each section a submenu of commands with their first shortcut shown.</summary>
	public static IReadOnlyList<MenuItem> App(Action<string> run) =>
		ZingJson.LoadDefaults("menu.json").AsArray().Select(section => new MenuItem(
			section!["label"]!.GetValue<string>(),
			Submenu: section["items"]!.AsArray().Select(entry => Entry(entry!.GetValue<string>(), run)).ToList())).ToList();

	/// <summary>The right-click menu of a tab.</summary>
	public static IReadOnlyList<MenuItem> Tab(TabService tabs, SavedTabs saved, string id, IShellActions shell)
	{
		var record = tabs.Get(id);
		var loose = tabs.Records.Any(t => t.Id != id && !t.Pinned);
		var web = record.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || record.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
		List<MenuItem> items = [record.Pinned ? new MenuItem("Unpin Tab", () => saved.Unpin(id)) : new MenuItem("Pin Tab", () => saved.Pin(id))];
		// A saved tab can go back to its item's address, or make the page it is on the item's.
		// Split view: beside the tab in front, or out of the split the tab is in.
		if (tabs.Splits.Of(id) is not null) items.Add(new MenuItem("Remove from Split", () => tabs.Unsplit(id)));
		else if (tabs.ActiveId is { } front && front != id && tabs.Splits.Of(front) is not { TabIds.Count: >= Split.MaxPanes })
			items.Add(new MenuItem("Show Beside Current Tab", () => tabs.SplitWith(front, id)));
		if (record.ItemId is not null)
		{
			items.Add(new MenuItem("Back to Saved Page", () => saved.Reset(id), Enabled: saved.HasLeftItsAddress(id)));
			items.Add(new MenuItem("Save This Page Here", () => saved.Update(id), Enabled: saved.HasLeftItsAddress(id) && web));
		}
		IReadOnlyList<MenuItem> menu =
		[
			.. items,
			MenuItem.Separator,
			new MenuItem("Reload", () => tabs.PageOf(id)?.Reload()),
			new MenuItem("Open in Default Browser", () => shell.OpenExternal(record.Url), Enabled: web),
			new MenuItem("Copy Address", () => shell.CopyText(Net.RelayedContent.ToWebAddress(record.Url)), Enabled: record.Url != TabStore.NewTabUrl),
			MenuItem.Separator,
			// A saved tab closes and its item stays; a pinned tab without one isn't closed by accident.
			new MenuItem("Close Tab", () => tabs.Close(id), Enabled: !record.Pinned || record.ItemId is not null),
			new MenuItem("Close Other Tabs", () => tabs.CloseOthers(id), Enabled: loose),
		];
		// The native menu runs its own loop, and the page may close its tab (window.close) while it is open.
		return [.. menu.Select(item => item.Click is { } click ? item with { Click = () => { if (tabs.Has(id)) click(); } } : item)];
	}

	/// <summary>A stack chip offers its lead page's menu plus closing the whole stack.</summary>
	public static IReadOnlyList<MenuItem> Stack(TabService tabs, SavedTabs saved, TabOrdering ordering, string key, IShellActions shell)
	{
		var stack = ordering.Stacks().FirstOrDefault(s => s.Key == key && s.Ids.Count > 1);
		if (stack is null) return [];
		var lead = stack.Ids.FirstOrDefault(id => id == tabs.ActiveId) ?? stack.Ids[0];
		return [.. Tab(tabs, saved, lead, shell), MenuItem.Separator, new MenuItem($"Close All {stack.Ids.Count} Tabs from {stack.Name}", () =>
		{
			foreach (var id in stack.Ids.Where(tabs.Has).ToList()) tabs.Close(id);
		})];
	}

	/// <summary>Right-click on the empty strip: how tabs are arranged and drawn.</summary>
	public static IReadOnlyList<MenuItem> Strip(TabOrdering ordering) =>
	[
		new MenuItem("Arrange Tabs", Submenu: ZingJson.LoadDefaults("tab-organizations.json").AsArray().Select(o =>
		{
			var organization = TabOrganizations.Parse(o!["id"]!.GetValue<string>());
			return new MenuItem(o["label"]!.GetValue<string>(), () => ordering.SetOrganization(organization), Checked: ordering.Organization == organization);
		}).ToList()),
		new MenuItem("Group Sites into Dropdowns", () => ordering.SetDropdowns(!ordering.Dropdowns), Checked: ordering.Dropdowns),
	];

	/// <summary>A Favourites-bar folder: its contents, then Open All.</summary>
	public static IReadOnlyList<MenuItem> LibraryFolder(LibraryStore library, string id, Action<string> open, Action<string> openAll)
	{
		var children = library.ChildrenOf(id);
		if (children.Count == 0) return [];
		return [.. children.Select(node => LibraryNode(node, open)), MenuItem.Separator, new MenuItem("Open All in New Tabs", () => openAll(id))];
	}

	/// <summary>The right-click menu of a library item.</summary>
	public static IReadOnlyList<MenuItem> LibraryItem(LibraryStore library, string id, Action<string, string> open, Action<string> openAll, SavedTabs saved)
	{
		List<MenuItem> items = library.IsFolderNode(id)
			? [new MenuItem("Open All in New Tabs", () => openAll(id))]
			: library.IsSavedTab(id)
				// A saved tab opens as its own tab; a copy in an ordinary tab is still there for the asking.
				? [new MenuItem("Open", () => saved.Open(id)), new MenuItem("Open a Copy in New Tab", () => open(id, "tab"))]
				: [new MenuItem("Open", () => open(id, "current")), new MenuItem("Open in New Tab", () => open(id, "tab"))];
		items.Add(MenuItem.Separator);
		items.Add(new MenuItem("Remove", () => library.Apply(new RemoveAction([id]))));
		return items;
	}

	private static MenuItem LibraryNode(BookmarkNode node, Action<string> open) => node switch
	{
		BookmarkFolder folder => new MenuItem(folder.Title, Submenu: folder.Children.Count > 0 ? folder.Children.Select(child => LibraryNode(child, open)).ToList() : [new MenuItem("(Empty)", Enabled: false)]),
		_ => new MenuItem(node.Title, () => open(node.Id)),
	};

	private static MenuItem Entry(string entry, Action<string> run)
	{
		if (entry == "separator") return MenuItem.Separator;
		var command = Commands.Get(entry);
		return new MenuItem(command.Label, () => run(command.Id), Accelerator: command.Shortcuts.FirstOrDefault()?.Label);
	}
}
