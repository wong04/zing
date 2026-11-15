using Zing.Core;

namespace Zing.Core.Tests;

public sealed class MenuThemeTests
{
	[Theory]
	[InlineData("light", false, PreferredAppMode.ForceLight)]
	[InlineData("light", true, PreferredAppMode.ForceLight)]
	[InlineData("dark", false, PreferredAppMode.ForceDark)]
	[InlineData("dark", true, PreferredAppMode.ForceDark)]
	[InlineData("system", false, PreferredAppMode.ForceLight)]
	[InlineData("system", true, PreferredAppMode.ForceDark)]
	public void Menus_take_the_effective_theme(string theme, bool systemDark, PreferredAppMode mode)
	{
		Assert.Equal(mode, MenuTheme.Mode(theme, systemDark));
	}

	[Fact]
	public void An_unknown_theme_is_an_error()
	{
		Assert.Throws<InvalidDataException>(() => MenuTheme.Mode("sepia", systemDark: false));
	}

	[Fact]
	public void The_exports_are_found_by_their_ordinals()
	{
		var exports = UxThemeExports.Resolve(ordinal => ordinal * 10);
		Assert.Equal(new UxThemeExports(1350, 1330, 1360), exports);
	}

	[Theory]
	[InlineData(135, "SetPreferredAppMode")]
	[InlineData(133, "AllowDarkModeForWindow")]
	[InlineData(136, "FlushMenuThemes")]
	public void A_missing_export_is_named_in_the_error(int missing, string name)
	{
		var error = Assert.Throws<UxThemeExportMissingException>(() => UxThemeExports.Resolve(ordinal => ordinal == missing ? 0 : 1));
		Assert.Equal(name, error.Name);
		Assert.Equal(missing, error.Ordinal);
	}
}
