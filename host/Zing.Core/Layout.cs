using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>Where the chrome ends and the page begins on each side, in DIPs (src/shared/layout/chrome.ts).</summary>
public readonly record struct ChromeInsets(int Top, int Left, int Right);

public sealed class UnknownTabBarPositionException(string position) : Exception($"Unknown tab bar position {position}");

/// <summary>The chrome's fixed sizes, from layout.json, which is generated from the TypeScript.</summary>
public static class Layout
{
	private static readonly Lazy<JsonObject> Sizes = new(() => ZingJson.LoadDefaults("layout.json").AsObject());

	public static int TabStripHeight => Size("tabStripHeight");
	public static int ToolbarHeight => Size("toolbarHeight");
	public static int FavouritesBarHeight => Size("favouritesBarHeight");
	public static int SidebarColumnWidth => Size("sidebarColumnWidth");
	public static int LibraryPanelWidth => Size("libraryPanelWidth");
	public static int RailDefaultWidth => Sizes.Value["verticalTabs"]!["defaultWidth"]!.GetValue<int>();

	// With tabs on the side the tab strip row is gone: the toolbar becomes the
	// top band and the rail takes a column on its side.
	public static ChromeInsets Insets(string position, bool favouritesBar, int railWidth, int leftDock)
	{
		var bar = favouritesBar ? FavouritesBarHeight : 0;
		var top = ToolbarHeight + bar;
		return position switch
		{
			"top" => new ChromeInsets(TabStripHeight + ToolbarHeight + bar, leftDock, 0),
			"left" => new ChromeInsets(top, railWidth + leftDock, 0),
			"right" => new ChromeInsets(top, leftDock, railWidth),
			_ => throw new UnknownTabBarPositionException(position),
		};
	}

	private static int Size(string key) => Sizes.Value[key]?.GetValue<int>() ?? throw new InvalidDataException($"layout.json has no {key}");
}
