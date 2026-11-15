namespace Zing.Core;

/// <summary>uxtheme's PreferredAppMode, the process-wide opt-in that lets Win32 popup menus draw dark.</summary>
public enum PreferredAppMode
{
	Default = 0,
	AllowDark = 1,
	ForceDark = 2,
	ForceLight = 3,
}

/// <summary>
/// Which mode Zing's native menus take: forced to the effective theme, the Appearance setting with "system"
/// resolved against Windows' app mode, so a menu never disagrees with the chrome it opens from.
/// </summary>
public static class MenuTheme
{
	public static PreferredAppMode Mode(string theme, bool systemDark) => theme switch
	{
		"light" => PreferredAppMode.ForceLight,
		"dark" => PreferredAppMode.ForceDark,
		"system" => systemDark ? PreferredAppMode.ForceDark : PreferredAppMode.ForceLight,
		_ => throw new InvalidDataException($"Unknown theme {theme}"),
	};
}

/// <summary>
/// uxtheme's dark-mode exports. They are undocumented and exported by ordinal only (stable since Windows 10 1903),
/// so each is looked up by number; <paramref name="export"/> returns 0 for one that is missing.
/// </summary>
public sealed record UxThemeExports(nint SetPreferredAppMode, nint AllowDarkModeForWindow, nint FlushMenuThemes)
{
	public const int SetPreferredAppModeOrdinal = 135, AllowDarkModeForWindowOrdinal = 133, FlushMenuThemesOrdinal = 136;

	public static UxThemeExports Resolve(Func<int, nint> export) => new(
		Require(export, SetPreferredAppModeOrdinal, "SetPreferredAppMode"),
		Require(export, AllowDarkModeForWindowOrdinal, "AllowDarkModeForWindow"),
		Require(export, FlushMenuThemesOrdinal, "FlushMenuThemes"));

	private static nint Require(Func<int, nint> export, int ordinal, string name)
	{
		var address = export(ordinal);
		if (address == 0) throw new UxThemeExportMissingException(name, ordinal);
		return address;
	}
}

public sealed class UxThemeExportMissingException(string name, int ordinal)
	: Exception($"uxtheme.dll has no {name} (ordinal {ordinal}), so native menus can't follow Zing's theme")
{
	public string Name { get; } = name;
	public int Ordinal { get; } = ordinal;
}
