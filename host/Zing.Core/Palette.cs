using System.Globalization;

namespace Zing.Core;

/// <summary>A colour that is not <c>#RRGGBB</c> (src/shared/theme/color.ts InvalidHexColorError).</summary>
public sealed class InvalidHexColorException(string hex) : Exception($"Not a #RRGGBB colour: \"{hex}\"")
{
	public string Hex { get; } = hex;
}

/// <summary>No such accent family (src/shared/theme/accent.ts).</summary>
public sealed class UnknownAccentException(string accent) : Exception($"Unknown accent \"{accent}\"")
{
	public string Accent { get; } = accent;
}

/// <summary>The palette as CSS-ready hex (src/shared/theme/palette.ts PaletteTokens).</summary>
public sealed record PaletteTokens(
	string Background,
	string Surface,
	string Tint,
	string Well,
	string Border,
	string Outline,
	string Accent,
	string OnAccent,
	string Selection,
	string Text,
	string TextSecondary);

/// <summary>Saturation and lightness for one role, 0..1.</summary>
public readonly record struct Role(double Saturation, double Lightness);

public sealed record Profile(
	Role Background,
	Role Surface,
	Role Tint,
	Role Well,
	Role Border,
	Role Outline,
	Role Accent,
	Role OnAccent,
	Role Selection,
	Role Text,
	Role TextSecondary,
	double InkDamping);

/// <summary>The stacked chrome (src/shared/theme/palette.ts StackTokens): window, band, field, page, cards and ribbons.</summary>
public sealed record StackTokens(
	string Window,
	string WindowInk,
	string Band,
	string BandInk,
	string Field,
	string FieldInk,
	string Page,
	string PageInk,
	IReadOnlyList<string> Cards,
	IReadOnlyList<string> CardInks,
	IReadOnlyList<string> Ribbons,
	IReadOnlyList<string> RibbonInks,
	string More,
	string MoreInk);

/// <summary>Colour maths (src/shared/theme/color.ts). Colours are <c>#RRGGBB</c> strings.</summary>
public static class Colors
{
	/// <summary>JS Math.round: halves go toward +infinity, where Math.Round goes to even.</summary>
	private static double JsRound(double value)
	{
		var floor = Math.Floor(value);
		return value - floor >= 0.5 ? floor + 1 : floor;
	}

	private static double Clamp01(double value) => Math.Min(Math.Max(value, 0), 1);

	/// <summary>Hue in degrees (wraps); saturation and lightness 0..1 (clamped).</summary>
	public static string HexFromHsl(double hue, double saturation, double lightness)
	{
		var h = ((hue % 360 + 360) % 360) / 360;
		var s = Clamp01(saturation);
		var l = Clamp01(lightness);
		if (s <= 0) {
			var v = (int)JsRound(l * 255);
			return HexFromBytes(v, v, v);
		}

		var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
		var p = 2 * l - q;
		double Channel(double offset)
		{
			var t = h + offset;
			if (t < 0) t += 1;
			if (t > 1) t -= 1;
			if (t < 1.0 / 6) return p + (q - p) * 6 * t;
			if (t < 1.0 / 2) return q;
			if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
			return p;
		}
		return HexFromBytes((int)JsRound(Channel(1.0 / 3) * 255), (int)JsRound(Channel(0) * 255), (int)JsRound(Channel(-1.0 / 3) * 255));
	}

	/// <summary>#RRGGBB to hue in degrees 0..360, saturation and lightness 0..1. Greys have hue 0.</summary>
	public static (double Hue, double Saturation, double Lightness) HslFromHex(string hex)
	{
		var (r, g, b) = RgbFromHex(hex);
		var max = Math.Max(r, Math.Max(g, b));
		var min = Math.Min(r, Math.Min(g, b));
		var lightness = (max + min) / 2;
		var delta = max - min;
		if (delta == 0) return (0, 0, lightness);

		var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
		var sector = max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
		return ((sector * 60 + 360) % 360, saturation, lightness);
	}

	/// <summary>Parses <c>#RRGGBB</c> (the <c>#</c> is optional) into channels 0..1.</summary>
	public static (double R, double G, double B) RgbFromHex(string hex)
	{
		var digits = hex.StartsWith('#') ? hex[1..] : hex;
		if (digits.Length != 6 || !digits.All(char.IsAsciiHexDigit)) throw new InvalidHexColorException(hex);
		var value = int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return (((value >> 16) & 0xff) / 255.0, ((value >> 8) & 0xff) / 255.0, (value & 0xff) / 255.0);
	}

	private static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

	public static double RelativeLuminance(string hex)
	{
		var (r, g, b) = RgbFromHex(hex);
		return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
	}

	public static double ContrastRatio(string a, string b)
	{
		var first = RelativeLuminance(a);
		var second = RelativeLuminance(b);
		return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
	}

	/// <summary>Blend in sRGB channel space; <paramref name="amount"/> 0 is a, 1 is b (clamped).</summary>
	public static string Mix(string a, string b, double amount)
	{
		var x = RgbFromHex(a);
		var y = RgbFromHex(b);
		var t = Clamp01(amount);
		int Channel(double p, double q) => (int)JsRound((p + (q - p) * t) * 255);
		return HexFromBytes(Channel(x.R, y.R), Channel(x.G, y.G), Channel(x.B, y.B));
	}

	/// <summary>Luma of the gamma-encoded channels: a quick light-or-dark test, not a contrast measure.</summary>
	public static double GammaLuma(string hex)
	{
		var (r, g, b) = RgbFromHex(hex);
		return 0.2126 * r + 0.7152 * g + 0.0722 * b;
	}

	private static string HexFromBytes(int r, int g, int b) =>
		string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
}

/// <summary>The palette generator and its derived roles (src/shared/theme/palette.ts, accent.ts).</summary>
public static class Palette
{
	public const double LegibleContrast = 4.6;
	private const double LiftStep = 0.01;
	private const double LiftCeiling = 0.92;
	private const int LiftMaxSteps = 40;
	private const double InkStep = 0.05;

	/// <summary>The accent name for a user-chosen seed colour, as opposed to a preset family.</summary>
	public const string CustomAccent = "custom";

	public static readonly PaletteTokens ReferenceLight = new(
		"#F7F2E9", "#FFFDF8", "#EFE6D6", "#EBE1CF", "#DECFB6", "#2A2420", "#D9B872", "#2A2118", "#E6D3AC", "#241F19", "#6E6355");

	public static readonly PaletteTokens ReferenceDark = new(
		"#1A1714", "#241F1A", "#201B16", "#2C251E", "#3A322A", "#CDBFA6", "#C9A45E", "#1A1410", "#3D3325", "#F0E7D8", "#A69884");

	public static readonly Profile LightProfile = new(
		Background: new(0.47, 0.941), Surface: new(1.0, 0.986), Tint: new(0.44, 0.888), Well: new(0.41, 0.867),
		Border: new(0.38, 0.792), Outline: new(0.135, 0.145), Accent: new(0.575, 0.649), OnAccent: new(0.27, 0.13),
		Selection: new(0.54, 0.79), Text: new(0.18, 0.12), TextSecondary: new(0.128, 0.382), InkDamping: 0.55);

	public static readonly Profile DarkProfile = new(
		Background: new(0.13, 0.09), Surface: new(0.16, 0.122), Tint: new(0.185, 0.106), Well: new(0.19, 0.145),
		Border: new(0.16, 0.196), Outline: new(0.28, 0.727), Accent: new(0.498, 0.578), OnAccent: new(0.236, 0.082),
		Selection: new(0.245, 0.192), Text: new(0.44, 0.894), TextSecondary: new(0.16, 0.584), InkDamping: 0.55);

	public static readonly IReadOnlyList<string> Accents = ["yellow", "green", "blue", "magenta", "purple", "red", "mono", "midnight"];

	private static readonly Dictionary<string, (double Hue, double Saturation)> HueProfiles = new(StringComparer.Ordinal)
	{
		["yellow"] = (38, 1.0),
		["green"] = (142, 0.82),
		["blue"] = (211, 0.88),
		["magenta"] = (322, 0.8),
		["purple"] = (272, 0.82),
		["red"] = (14, 0.84),
		["mono"] = (34, 0.07),
		["midnight"] = (232, 0.7),
	};

	/// <summary>A full palette for one hue, holding the profile's lightness for every role.</summary>
	public static PaletteTokens Generate(Profile profile, double hue, double saturationScale)
	{
		string RoleColor(Role r) => Colors.HexFromHsl(hue, r.Saturation * saturationScale, r.Lightness);
		string Ink(Role r) => Colors.HexFromHsl(hue, r.Saturation * saturationScale * profile.InkDamping, r.Lightness);

		var onAccent = Ink(profile.OnAccent);
		var accent = LiftUntilLegible(new Role(profile.Accent.Saturation * saturationScale, profile.Accent.Lightness), hue, onAccent);
		return new PaletteTokens(
			RoleColor(profile.Background),
			RoleColor(profile.Surface),
			RoleColor(profile.Tint),
			RoleColor(profile.Well),
			RoleColor(profile.Border),
			Ink(profile.Outline),
			accent,
			onAccent,
			RoleColor(profile.Selection),
			Ink(profile.Text),
			Ink(profile.TextSecondary));
	}

	/// <summary>Raises lightness in small steps until the colour clears <see cref="LegibleContrast"/> against the foreground; may stop short at the ceiling.</summary>
	public static string LiftUntilLegible(Role baseRole, double hue, string foreground)
	{
		var lightness = baseRole.Lightness;
		var candidate = Colors.HexFromHsl(hue, baseRole.Saturation, lightness);
		for (var steps = 0; Colors.ContrastRatio(candidate, foreground) < LegibleContrast && lightness < LiftCeiling && steps < LiftMaxSteps; steps++) {
			lightness += LiftStep;
			candidate = Colors.HexFromHsl(hue, baseRole.Saturation, lightness);
		}
		return candidate;
	}

	private static (double Hue, double Saturation) HueProfile(string accent) =>
		HueProfiles.TryGetValue(accent, out var profile) ? profile : throw new UnknownAccentException(accent);

	public static PaletteTokens LightTokens(string accent)
	{
		var (hue, saturation) = HueProfile(accent);
		return accent == "yellow" ? ReferenceLight : Generate(LightProfile, hue, saturation);
	}

	public static PaletteTokens DarkTokens(string accent)
	{
		var (hue, saturation) = HueProfile(accent);
		return accent == "yellow" ? ReferenceDark : Generate(DarkProfile, hue, saturation);
	}

	/// <summary>A hue and saturation from a seed colour. Saturation has a floor so a grey seed still gets a faint tint.</summary>
	public static (double Hue, double Saturation) SeedProfile(string hex)
	{
		var (hue, saturation, _) = Colors.HslFromHex(hex);
		return (hue, Math.Min(Math.Max(saturation, 0.07), 1));
	}

	/// <summary>The hue profile for a named accent, or for the seed when the accent is custom.</summary>
	public static (double Hue, double Saturation) ThemeProfile(string accent, string customColor)
	{
		if (accent == CustomAccent) return SeedProfile(customColor);
		return HueProfile(accent);
	}

	/// <summary>The palette for a named accent, or for the seed when the accent is custom.</summary>
	public static PaletteTokens ThemeTokens(string accent, string customColor, bool dark)
	{
		if (accent != CustomAccent) return dark ? DarkTokens(accent) : LightTokens(accent);
		var (hue, saturation) = SeedProfile(customColor);
		return Generate(dark ? DarkProfile : LightProfile, hue, saturation);
	}

	/// <summary>The stacked chrome, tinted from one hue (src/shared/theme/palette.ts stackTokens).</summary>
	public static StackTokens Stack((double Hue, double Saturation) profile, bool dark)
	{
		var (hue, baseSaturation) = profile;
		var saturation = baseSaturation * 0.75;
		var window = dark ? Colors.HexFromHsl(hue, 0.4 * saturation, 0.15) : Colors.HexFromHsl(hue, 0.55 * saturation, 0.82);
		var page = dark ? Colors.HexFromHsl(hue, 0.2 * saturation, 0.085) : Colors.HexFromHsl(hue, 0.3 * saturation, 0.99);
		var band = Colors.Mix(window, dark ? "#000000" : Colors.HexFromHsl(hue, 0.6 * saturation, 0.3), dark ? 0.3 : 0.22);
		var field = Colors.Mix(band, "#FFFFFF", dark ? 0.1 : 0.62);
		string Paint(double h) => Colors.HexFromHsl(h, 0.75 * saturation, dark ? 0.42 : 0.6);
		var ribbons = Triad(hue, Paint);
		var cards = Triad(hue, h => Colors.Mix(window, Paint(h), 0.62));
		var more = Colors.Mix(window, InkOn(window), 0.78);
		return new StackTokens(
			window, InkOn(window), band, InkOn(band), field, InkOn(field), page, InkOn(page),
			cards, InksOn(cards), ribbons, InksOn(ribbons), more, InkOn(more));
	}

	/// <summary>Black or white, whichever reads better on the background.</summary>
	public static string InkOn(string background) =>
		Colors.ContrastRatio("#000000", background) >= Colors.ContrastRatio("#FFFFFF", background) ? "#000000" : "#FFFFFF";

	/// <summary>The base hue and the two either side of it, 45 degrees apart.</summary>
	private static IReadOnlyList<string> Triad(double hue, Func<double, string> colour) => [colour(hue), colour(hue + 45), colour(hue - 45)];

	private static IReadOnlyList<string> InksOn(IReadOnlyList<string> colours) => [InkOn(colours[0]), InkOn(colours[1]), InkOn(colours[2])];

	/// <summary>The accent as a foreground: walked toward the text colour until it is legible on the surface and selection plate.</summary>
	public static string AccentInk(PaletteTokens tokens)
	{
		bool Legible(string c) =>
			Colors.ContrastRatio(c, tokens.Surface) >= LegibleContrast && Colors.ContrastRatio(c, tokens.Selection) >= LegibleContrast;
		var candidate = tokens.Accent;
		for (double step = 0; !Legible(candidate) && step < 1;) {
			step += InkStep;
			candidate = Colors.Mix(tokens.Accent, tokens.Text, step);
		}
		return candidate;
	}

	public static bool IsDark(PaletteTokens tokens) => Colors.GammaLuma(tokens.Background) < 0.5;

	/// <summary>Resting fill of a neutral control.</summary>
	public static string Control(PaletteTokens tokens) => IsDark(tokens) ? tokens.Well : Colors.Mix(tokens.Well, tokens.Surface, 0.45);

	/// <summary>The plate a selected segment sits on.</summary>
	public static string Raised(PaletteTokens tokens) => IsDark(tokens) ? Colors.Mix(tokens.Well, tokens.Text, 0.13) : tokens.Surface;
}
