using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Tests;

/// <summary>
/// The start page held byte-for-byte to the TypeScript through startPage.vectors.json, plus ports of
/// startShortcuts.test.ts, startPage.test.ts, start-page.test.ts and theme.test.ts.
/// </summary>
public sealed class StartPageTests
{
	private const string VectorsPath = "host/Zing.Core.Tests/vectors/startPage.vectors.json";

	private static readonly Lazy<JsonElement> Root = new(() => Vectors.Load(VectorsPath));

	private static JsonElement Section(string name) => Root.Value.GetProperty(name);

	private static IEnumerable<JsonElement> Items(JsonElement array) => array.EnumerateArray();

	private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

	private static PaletteTokens Palette(JsonElement e) => new(
		Str(e, "background"), Str(e, "surface"), Str(e, "tint"), Str(e, "well"), Str(e, "border"), Str(e, "outline"),
		Str(e, "accent"), Str(e, "onAccent"), Str(e, "selection"), Str(e, "text"), Str(e, "textSecondary"));

	private static StartPageTokens Tokens(JsonElement e) => new(
		Str(e, "background"), Str(e, "surface"), Str(e, "tint"), Str(e, "well"), Str(e, "outline"), Str(e, "accent"),
		Str(e, "onAccent"), Str(e, "text"), Str(e, "textSecondary"), Str(e, "control"), Str(e, "raised"), Str(e, "accentInk"));

	private static StartPageTheme Theme(JsonElement e) => new(Tokens(e.GetProperty("light")), Tokens(e.GetProperty("dark")));

	private static StartShortcut Shortcut(JsonElement e) => new(Str(e, "title"), Str(e, "url"));

	private static List<StartShortcut> ShortcutList(JsonElement e) => [.. Items(e).Select(Shortcut)];

	private static StartPageInput Input(JsonElement e) =>
		new(Str(e, "searchURL"), ShortcutList(e.GetProperty("shortcuts")), Theme(e.GetProperty("theme")));

	private static void AssertSame(IReadOnlyList<StartShortcut> expected, IReadOnlyList<StartShortcut> actual) => Assert.Equal(expected, actual);

	private static TheoryData<string> Names(string section)
	{
		var data = new TheoryData<string>();
		foreach (var item in Items(Section(section))) data.Add(Str(item, "name"));
		return data;
	}

	private static JsonElement Named(string section, string name) => Items(Section(section)).Single(i => Str(i, "name") == name);

	public static TheoryData<string> PageNames() => Names("pages");

	public static TheoryData<string> PageErrorNames() => Names("pageErrors");

	public static TheoryData<string> ThemeNames() => Names("themes");

	public static TheoryData<string> HandleNames() => Names("handle");

	// MARK: - Palette

	[Fact]
	public void HslMatchesTheVectors()
	{
		foreach (var v in Items(Section("palette").GetProperty("hsl")))
			Assert.Equal(Str(v, "hex"), Colors.HexFromHsl(v.GetProperty("hue").GetDouble(), v.GetProperty("saturation").GetDouble(), v.GetProperty("lightness").GetDouble()));
	}

	[Fact]
	public void MixMatchesTheVectors()
	{
		foreach (var v in Items(Section("palette").GetProperty("mix")))
			Assert.Equal(Str(v, "hex"), Colors.Mix(Str(v, "a"), Str(v, "b"), v.GetProperty("amount").GetDouble()));
	}

	[Fact]
	public void ContrastMatchesTheVectors()
	{
		foreach (var v in Items(Section("palette").GetProperty("contrast")))
			Assert.Equal(v.GetProperty("ratio").GetDouble(), Colors.ContrastRatio(Str(v, "a"), Str(v, "b")), 1e-12);
	}

	[Fact]
	public void MalformedHexIsRefused()
	{
		foreach (var hex in Items(Section("palette").GetProperty("badHex")))
			Assert.Throws<InvalidHexColorException>(() => Colors.RgbFromHex(hex.GetString()!));
	}

	[Fact]
	public void LowerCaseHexIsAccepted() => Assert.Equal((1.0, 1.0, 1.0), Colors.RgbFromHex("ffffff"));

	[Fact]
	public void EveryAccentMatchesTheVectors()
	{
		var accents = Items(Section("palette").GetProperty("accents")).ToList();
		Assert.Equal(Zing.Core.Palette.Accents, accents.Select(a => Str(a, "accent")));
		foreach (var a in accents) {
			var accent = Str(a, "accent");
			var light = Zing.Core.Palette.LightTokens(accent);
			var dark = Zing.Core.Palette.DarkTokens(accent);
			Assert.Equal(Palette(a.GetProperty("light")), light);
			Assert.Equal(Palette(a.GetProperty("dark")), dark);
			Assert.Equal(Str(a.GetProperty("lightDerived"), "control"), Zing.Core.Palette.Control(light));
			Assert.Equal(Str(a.GetProperty("lightDerived"), "raised"), Zing.Core.Palette.Raised(light));
			Assert.Equal(Str(a.GetProperty("lightDerived"), "accentInk"), Zing.Core.Palette.AccentInk(light));
			Assert.Equal(Str(a.GetProperty("darkDerived"), "control"), Zing.Core.Palette.Control(dark));
			Assert.Equal(Str(a.GetProperty("darkDerived"), "raised"), Zing.Core.Palette.Raised(dark));
			Assert.Equal(Str(a.GetProperty("darkDerived"), "accentInk"), Zing.Core.Palette.AccentInk(dark));
			Assert.Equal(Theme(a.GetProperty("startPageTheme")), StartPage.Theme(accent));
		}
	}

	[Fact]
	public void YellowKeepsTheReferenceExactly()
	{
		Assert.Equal("#F7F2E9", Zing.Core.Palette.LightTokens("yellow").Background);
		Assert.Equal("#D9B872", Zing.Core.Palette.LightTokens("yellow").Accent);
		Assert.Equal("#1A1714", Zing.Core.Palette.DarkTokens("yellow").Background);
		Assert.Equal("#F0E7D8", Zing.Core.Palette.DarkTokens("yellow").Text);
	}

	[Fact]
	public void AnUnknownAccentIsRefused() => Assert.Throws<UnknownAccentException>(() => Zing.Core.Palette.LightTokens("teal"));

	public static TheoryData<string, string, string, string> LegibilityPairs()
	{
		var data = new TheoryData<string, string, string, string>();
		foreach (var accent in Zing.Core.Palette.Accents) {
			foreach (var (mode, tokens) in new[] { ("light", Zing.Core.Palette.LightTokens(accent)), ("dark", Zing.Core.Palette.DarkTokens(accent)) }) {
				var ink = Zing.Core.Palette.AccentInk(tokens);
				var pairs = new (string Label, string Fg, string Bg)[]
				{
					("text on background", tokens.Text, tokens.Background),
					("text on surface", tokens.Text, tokens.Surface),
					("secondary on background", tokens.TextSecondary, tokens.Background),
					("onAccent on accent", tokens.OnAccent, tokens.Accent),
					("text on well", tokens.Text, tokens.Well),
					("accentInk on surface", ink, tokens.Surface),
					("accentInk on selection", ink, tokens.Selection),
					("text on control", tokens.Text, Zing.Core.Palette.Control(tokens)),
					("text on raised", tokens.Text, Zing.Core.Palette.Raised(tokens)),
				};
				foreach (var pair in pairs) data.Add($"{accent} {mode} {pair.Label}", pair.Label, pair.Fg, pair.Bg);
			}
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(LegibilityPairs))]
	public void EveryFamilyIsLegibleInBothModes(string name, string label, string foreground, string background)
	{
		Assert.False(string.IsNullOrEmpty(label), name);
		Assert.True(Colors.ContrastRatio(foreground, background) >= 4.5, name);
	}

	[Fact]
	public void LegibilityCoversEveryPair() => Assert.Equal(126, LegibilityPairs().Count());

	[Fact]
	public void ASelectedSegmentReadsAsRaisedNotSunken()
	{
		foreach (var accent in Zing.Core.Palette.Accents) {
			foreach (var tokens in new[] { Zing.Core.Palette.LightTokens(accent), Zing.Core.Palette.DarkTokens(accent) }) {
				Assert.True(
					Colors.RelativeLuminance(Zing.Core.Palette.Raised(tokens)) > Colors.RelativeLuminance(Zing.Core.Palette.Control(tokens)),
					$"{accent} raised above control");
			}
		}
	}

	[Fact]
	public void FamiliesAreDistinct() =>
		Assert.Equal(Zing.Core.Palette.Accents.Count, Zing.Core.Palette.Accents.Select(a => Zing.Core.Palette.LightTokens(a).Accent).Distinct().Count());

	[Fact]
	public void LiftingKeepsADarkHueLegible()
	{
		var purple = Zing.Core.Palette.LightTokens("purple");
		Assert.True(Colors.ContrastRatio(purple.OnAccent, purple.Accent) >= 4.6);
	}

	// MARK: - Shortcuts

	[Fact]
	public void ShortcutLimitAndDefaultsMatch()
	{
		Assert.Equal(StartShortcuts.Limit, Section("shortcuts").GetProperty("limit").GetInt32());
		AssertSame(ShortcutList(Section("shortcuts").GetProperty("defaults")), StartShortcuts.Defaults);
		Assert.Equal(8, StartShortcuts.Defaults.Count);
	}

	[Fact]
	public void DecodeMatchesTheVectors()
	{
		foreach (var v in Items(Section("shortcuts").GetProperty("decode"))) {
			var raw = v.GetProperty("raw").GetString();
			AssertSame(ShortcutList(v.GetProperty("list")), StartShortcuts.Decode(raw));
		}
	}

	[Fact]
	public void EncodeMatchesTheVectors()
	{
		foreach (var v in Items(Section("shortcuts").GetProperty("encode")))
			Assert.Equal(Str(v, "encoded"), StartShortcuts.Encode(ShortcutList(v.GetProperty("list"))));
	}

	[Fact]
	public void MakeMatchesTheVectors()
	{
		foreach (var v in Items(Section("shortcuts").GetProperty("make"))) {
			var result = v.GetProperty("result");
			var expected = result.ValueKind == JsonValueKind.Null ? null : Shortcut(result);
			Assert.Equal(expected, StartShortcuts.Make(Str(v, "title"), Str(v, "address")));
		}
	}

	[Fact]
	public void NameMatchesTheVectors()
	{
		foreach (var v in Items(Section("shortcuts").GetProperty("name")))
			Assert.Equal(Str(v, "name"), StartShortcuts.Name(Str(v, "url")));
	}

	[Fact]
	public void GlyphMatchesTheVectors()
	{
		foreach (var v in Items(Section("shortcuts").GetProperty("glyph")))
			Assert.Equal(Str(v, "glyph"), StartShortcuts.Glyph(Shortcut(v.GetProperty("shortcut"))));
	}

	[Fact]
	public void IsWebAddressMatchesTheVectors()
	{
		foreach (var v in Items(Section("shortcuts").GetProperty("isWebAddress")))
			Assert.Equal(v.GetProperty("result").GetBoolean(), StartShortcuts.IsWebAddress(Str(v, "text")));
	}

	[Fact]
	public void EscapeMatchesTheVectors()
	{
		foreach (var v in Items(Section("shortcuts").GetProperty("escape")))
			Assert.Equal(Str(v, "escaped"), StartShortcuts.EscapeHtml(Str(v, "text")));
	}

	[Fact]
	public void NothingChosenIsTheBuiltInEight()
	{
		AssertSame(StartShortcuts.Defaults, StartShortcuts.Decode(null));
		Assert.Equal("◱", StartShortcuts.Glyph(StartShortcuts.Defaults[0]));
	}

	[Fact]
	public void ABareAddressBecomesHttpsAndIsNamedAfterTheSite()
	{
		var apple = StartShortcuts.Make("", "apple.com");
		Assert.Equal(new StartShortcut("Apple", "https://apple.com"), apple);
		Assert.Equal("A", StartShortcuts.Glyph(apple!));
	}

	[Fact]
	public void AGivenNameIsKeptTrimmedAndPathsSurvive()
	{
		var docs = StartShortcuts.Make("  My Docs ", "docs.google.com/document/u/0/");
		Assert.Equal(new StartShortcut("My Docs", "https://docs.google.com/document/u/0/"), docs);
	}

	[Theory]
	[InlineData("best pizza")]
	[InlineData("javascript:alert(1)")]
	[InlineData("file:///etc/passwd")]
	public void NonWebAddressesAreRefused(string address) => Assert.Null(StartShortcuts.Make("x", address));

	[Fact]
	public void AChosenListRoundTripsAndAnEmptyOneStaysEmpty()
	{
		var chosen = new List<StartShortcut> { new("Apple", "https://apple.com"), new("My Docs", "https://docs.google.com/x") };
		AssertSame(chosen, StartShortcuts.Decode(StartShortcuts.Encode(chosen)));
		AssertSame([], StartShortcuts.Decode(StartShortcuts.Encode([])));
	}

	[Fact]
	public void TheListIsCappedAtTheLimit()
	{
		var many = Enumerable.Range(0, 30).Select(i => new StartShortcut($"s{i}", $"https://s{i}.com")).ToList();
		Assert.Equal(StartShortcuts.Limit, StartShortcuts.Decode(StartShortcuts.Encode(many)).Count);
	}

	[Fact]
	public void TheSettingBecomesTheList()
	{
		AssertSame(StartShortcuts.Defaults, StartShortcuts.FromSetting(null));
		var stored = JsonNode.Parse("""[{"title":"Mine","url":"https://mine.test"},{"title":"js","url":"javascript:alert(1)"}]""");
		AssertSame([new StartShortcut("Mine", "https://mine.test")], StartShortcuts.FromSetting(stored));
	}

	// MARK: - The page

	[Theory]
	[MemberData(nameof(PageNames))]
	public void PageMatchesTheVectors(string name)
	{
		var vector = Named("pages", name);
		Assert.Equal(Str(vector, "html"), StartPage.Html(Input(vector.GetProperty("input"))));
	}

	[Theory]
	[MemberData(nameof(PageErrorNames))]
	public void PageErrorsMatchTheVectors(string name)
	{
		var vector = Named("pageErrors", name);
		var input = Input(vector.GetProperty("input"));
		switch (Str(vector, "error")) {
			case "SearchURLError":
				Assert.Throws<SearchUrlException>(() => StartPage.Html(input));
				break;
			case "ThemeTokenError":
				Assert.Throws<ThemeTokenException>(() => StartPage.Html(input));
				break;
			default:
				throw new InvalidDataException($"vectors name an error this test doesn't know: {vector}");
		}
	}

	[Theory]
	[MemberData(nameof(ThemeNames))]
	public void ThemeCssAndRestyleMatchTheVectors(string name)
	{
		var vector = Named("themes", name);
		var theme = Theme(vector.GetProperty("theme"));
		Assert.Equal(Str(vector, "css"), StartPage.ThemeCss(theme));
		Assert.Equal(Str(vector, "restyle"), StartPage.RestyleScript(theme));
	}

	[Fact]
	public void TheEditLinkMatches() => Assert.Equal(StartPage.EditShortcutsUrl, Str(Root.Value, "editShortcutsURL"));

	[Fact]
	public void SearchFormActionMatchesTheVectors()
	{
		foreach (var v in Items(Section("searchFormAction")))
			Assert.Equal(Str(v, "action"), StartPage.SearchFormAction(Str(v, "engine")));
	}

	[Fact]
	public void AnUnknownEngineIsRefused() => Assert.Throws<Address.UnknownSearchEngineException>(() => StartPage.SearchFormAction("altavista"));

	[Fact]
	public void IsStartPageMatchesTheVectors()
	{
		foreach (var v in Items(Section("isStartPage")))
			Assert.True(v.GetProperty("result").GetBoolean() == StartPage.IsStartPage(Str(v, "url")), Str(v, "url"));
	}

	[Fact]
	public void NavigationMatchesTheVectors()
	{
		foreach (var v in Items(Section("navigation"))) {
			var result = v.GetProperty("result");
			StartPageNavigation expected = Str(result, "kind") switch {
				"search" => new StartPageNavigation.Search(Str(result, "query")),
				"edit-shortcuts" => new StartPageNavigation.EditShortcuts(),
				"block" => new StartPageNavigation.Block(),
				"allow" => new StartPageNavigation.Allow(),
				var other => throw new InvalidDataException($"unknown navigation kind {other}"),
			};
			Assert.Equal(expected, StartPage.ClassifyNavigation(Str(v, "from"), Str(v, "to"), Str(v, "engine")));
		}
	}

	[Theory]
	[MemberData(nameof(HandleNames))]
	public void ServedPageMatchesTheVectors(string name)
	{
		var vector = Named("handle", name);
		var settings = Settings.Parse(JsonNode.Parse(vector.GetProperty("stored").GetRawText()));
		var response = StartPage.Handle("zing://home", settings);
		Assert.Equal(200, response.Status);
		Assert.Equal("text/html; charset=utf-8", response.ContentType);
		Assert.Equal("no-store", response.CacheControl);
		Assert.Equal(Str(vector, "html"), response.Body);
	}

	[Theory]
	[InlineData("zing://edit-shortcuts")]
	[InlineData("https://home")]
	[InlineData("not a url")]
	public void OtherPagesAreNotServed(string url) =>
		Assert.Throws<StartPageSchemeException>(() => StartPage.Handle(url, SettingsSchema.Defaults()));

	[Fact]
	public void ASlashAfterTheHostIsStillTheStartPage() =>
		Assert.Equal(200, StartPage.Handle("zing://home/", SettingsSchema.Defaults()).Status);

	[Fact]
	public void MissingSettingsAreRefusedNotDefaulted() =>
		Assert.Throws<UnknownSettingException>(() => StartPage.Html(new JsonObject { ["searchEngine"] = "google" }));

	// MARK: - Behaviour pinned by startPage.test.ts

	private static string Page(IReadOnlyList<StartShortcut>? shortcuts = null, string search = "https://www.google.com/search") =>
		StartPage.Html(new StartPageInput(search, shortcuts ?? StartShortcuts.Defaults, StartPage.Theme("yellow")));

	private static int Count(string html, string needle) => html.Split(needle).Length - 1;

	[Fact]
	public void PageHasTheClockFormTilesAndEditLink()
	{
		var html = Page();
		Assert.Contains("<div class=\"clock\">", html);
		Assert.Contains("<form action=\"https://www.google.com/search\" method=\"GET\"", html);
		Assert.Contains("<input type=\"text\" name=\"q\"", html);
		Assert.Equal(8, Count(html, "<a class=\"tile\" "));
		Assert.Contains("<a class=\"edit\" href=\"zing://edit-shortcuts\">Edit shortcuts</a>", html);
	}

	[Fact]
	public void OnlyWebTilesAreShown()
	{
		var html = Page([
			new("ok", "https://ok.com"), new("js", "javascript:alert(1)"), new("data", "data:text/html,hi"),
			new("file", "file:///C:/Windows/win.ini"), new("plain", "http://plain.com"),
		]);
		Assert.Equal(2, Count(html, "<a class=\"tile\" "));
		Assert.DoesNotContain("javascript:", html);
	}

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("not a url")]
	public void TheSearchEngineMustBeWebToo(string search) => Assert.Throws<SearchUrlException>(() => Page(null, search));

	[Fact]
	public void NoUserTextReachesThePageUnescaped()
	{
		var html = Page([new("<script>alert(\"t\")</script>", "https://ok.com/"), new("\" onmouseover=\"alert(1)", "https://x.com/\"onmouseover=\"alert(2)")]);
		Assert.Equal(1, Count(html, "<script"));
		Assert.Contains("title=\"&lt;script&gt;alert(&quot;t&quot;)&lt;/script&gt;\"", html);
		Assert.Contains("href=\"https://x.com/&quot;onmouseover=&quot;alert(2)\"", html);
	}

	[Fact]
	public void ATokenThatCouldEndTheStyleIsRefused()
	{
		var theme = StartPage.Theme("yellow");
		Assert.Throws<ThemeTokenException>(() => StartPage.ThemeCss(theme with { Light = theme.Light with { Accent = "red;}</style><script>alert(1)</script>" } }));
		Assert.Throws<ThemeTokenException>(() => StartPage.ThemeCss(theme with { Dark = theme.Dark with { Text = " " } }));
	}

	[Fact]
	public void FunctionalColoursAreFine()
	{
		var theme = StartPage.Theme("yellow");
		Assert.Contains("--accent: rgb(217 184 114 / 90%);", StartPage.ThemeCss(theme with { Light = theme.Light with { Accent = "rgb(217 184 114 / 90%)" } }));
	}

	[Fact]
	public void ThePageAndTheRestyleScriptCarryTheSameCss()
	{
		var theme = StartPage.Theme("yellow");
		Assert.Contains($"<style id=\"zing-theme\">\n{StartPage.ThemeCss(theme)}\n</style>", Page());
	}

	[Fact]
	public void ServedPageShowsTheEditedShortcutsAndEngine()
	{
		var settings = SettingsSchema.Defaults();
		settings["startShortcuts"] = JsonNode.Parse("""[{"title":"Mine","url":"https://mine.test"}]""");
		settings["searchEngine"] = "duckduckgo";
		var html = StartPage.Html(settings);
		Assert.Equal(1, Count(html, "class=\"tile\""));
		Assert.Contains("<form action=\"https://duckduckgo.com/\"", html);
	}
}
