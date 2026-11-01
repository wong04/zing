using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Tests;

/// <summary>
/// The C# twins of TS modules held to the TS by shared vectors: src/shared/browser/textSize, startShortcuts and pageUi,
/// and src/shared/theme/palette. The vectors were generated from the TS, which is the spec.
/// </summary>
public sealed class SharedVectorTests
{
	private static JsonElement Section(string path, string name) => Vectors.Load(path).GetProperty(name);

	private static TheoryData<string> Cases(string path, string name)
	{
		var data = new TheoryData<string>();
		foreach (var v in Section(path, name).EnumerateArray()) data.Add(v.GetRawText());
		return data;
	}

	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	private static string? Text(JsonElement v, string key) => v.GetProperty(key).ValueKind == JsonValueKind.Null ? null : v.GetProperty(key).GetString();

	// TextSize

	private const string TextSizeVectors = "src/shared/browser/textSize.vectors.json";

	public static TheoryData<string> TextSizes() => Cases(TextSizeVectors, "sizes");

	[Fact]
	public void Text_size_has_the_same_steps_and_default()
	{
		Assert.Equal(Section(TextSizeVectors, "steps").EnumerateArray().Select(s => s.GetInt32()), TextSize.Steps);
		Assert.Equal(TextSize.Default, Section(TextSizeVectors, "default").GetInt32());
	}

	[Theory]
	[MemberData(nameof(TextSizes))]
	public void Text_size_steps_like_the_TS(string json)
	{
		var v = Parse(json);
		var percent = v.GetProperty("percent").GetInt32();
		Assert.Equal(v.GetProperty("bigger").GetInt32(), TextSize.Bigger(percent));
		Assert.Equal(v.GetProperty("smaller").GetInt32(), TextSize.Smaller(percent));
		Assert.Equal(v.GetProperty("css").GetString(), TextSize.Css(percent));
	}

	// StartShortcuts

	private const string ShortcutVectors = "src/shared/browser/startShortcuts.vectors.json";

	private static StartShortcut? Shortcut(JsonElement v) =>
		v.ValueKind == JsonValueKind.Null ? null : new StartShortcut(v.GetProperty("title").GetString()!, v.GetProperty("url").GetString()!);

	private static List<StartShortcut> Shortcuts(JsonElement v) => [.. v.EnumerateArray().Select(s => Shortcut(s)!)];

	public static TheoryData<string> Decodes() => Cases(ShortcutVectors, "decode");

	public static TheoryData<string> Encodes() => Cases(ShortcutVectors, "encode");

	public static TheoryData<string> Makes() => Cases(ShortcutVectors, "make");

	public static TheoryData<string> Names() => Cases(ShortcutVectors, "name");

	public static TheoryData<string> Glyphs() => Cases(ShortcutVectors, "glyph");

	public static TheoryData<string> WebAddresses() => Cases(ShortcutVectors, "isWebAddress");

	public static TheoryData<string> Escapes() => Cases(ShortcutVectors, "escapeHTML");

	[Theory]
	[MemberData(nameof(Decodes))]
	public void Shortcuts_decode_like_the_TS(string json)
	{
		var v = Parse(json);
		Assert.Equal(Shortcuts(v.GetProperty("expected")), StartShortcuts.Decode(Text(v, "raw")));
	}

	[Theory]
	[MemberData(nameof(Encodes))]
	public void Shortcuts_encode_like_the_TS(string json)
	{
		var v = Parse(json);
		Assert.Equal(v.GetProperty("expected").GetString(), StartShortcuts.Encode(Shortcuts(v.GetProperty("list"))));
	}

	[Theory]
	[MemberData(nameof(Makes))]
	public void Shortcuts_are_made_like_the_TS(string json)
	{
		var v = Parse(json);
		Assert.Equal(Shortcut(v.GetProperty("expected")), StartShortcuts.Make(v.GetProperty("title").GetString()!, v.GetProperty("address").GetString()!));
	}

	[Theory]
	[MemberData(nameof(Names))]
	public void Shortcuts_are_named_like_the_TS(string json)
	{
		var v = Parse(json);
		Assert.Equal(v.GetProperty("expected").GetString(), StartShortcuts.Name(v.GetProperty("url").GetString()!));
	}

	[Theory]
	[MemberData(nameof(Glyphs))]
	public void Shortcut_glyphs_match_the_TS(string json)
	{
		var v = Parse(json);
		Assert.Equal(v.GetProperty("expected").GetString(), StartShortcuts.Glyph(new StartShortcut(v.GetProperty("title").GetString()!, v.GetProperty("url").GetString()!)));
	}

	[Theory]
	[MemberData(nameof(WebAddresses))]
	public void Web_addresses_are_told_apart_like_the_TS(string json)
	{
		var v = Parse(json);
		Assert.Equal(v.GetProperty("expected").GetBoolean(), StartShortcuts.IsWebAddress(v.GetProperty("text").GetString()!));
	}

	[Theory]
	[MemberData(nameof(Escapes))]
	public void Html_is_escaped_like_the_TS(string json)
	{
		var v = Parse(json);
		Assert.Equal(v.GetProperty("expected").GetString(), StartShortcuts.EscapeHtml(v.GetProperty("text").GetString()!));
	}

	// PageUi

	public static TheoryData<string> PageUiStates() => Cases("src/shared/browser/pageUi.vectors.json", "states");

	[Theory]
	[MemberData(nameof(PageUiStates))]
	public void Page_ui_state_has_the_TS_shape_and_visibility(string json)
	{
		var v = JsonNode.Parse(json)!;
		var stateJson = v["state"]!;
		var state = stateJson.Deserialize<PageUiState>(ZingJson.Options)!;
		Assert.Equal(v["visible"]!.GetValue<bool>(), state.HasVisiblePageUi);
		Assert.True(JsonNode.DeepEquals(stateJson, JsonSerializer.SerializeToNode(state, ZingJson.Options)), $"serialised as {JsonSerializer.Serialize(state, ZingJson.Options)}");
	}

	// Palette

	public static TheoryData<string> Palettes() => Cases("src/shared/theme/palette.vectors.json", "accents");

	[Theory]
	[MemberData(nameof(Palettes))]
	public void Palette_matches_the_TS(string json)
	{
		var v = Parse(json);
		var accent = v.GetProperty("accent").GetString()!;
		var tokens = v.GetProperty("mode").GetString() == "light" ? Palette.LightTokens(accent) : Palette.DarkTokens(accent);
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse(v.GetProperty("tokens").GetRawText()), JsonSerializer.SerializeToNode(tokens, ZingJson.Options)));
		Assert.Equal(v.GetProperty("accentInk").GetString(), Palette.AccentInk(tokens));
		Assert.Equal(v.GetProperty("control").GetString(), Palette.Control(tokens));
		Assert.Equal(v.GetProperty("raised").GetString(), Palette.Raised(tokens));
		Assert.Equal(v.GetProperty("isDark").GetBoolean(), Palette.IsDark(tokens));
	}

	[Fact]
	public void The_vectors_cover_every_accent() =>
		Assert.Equal(Palette.Accents.Order(), Section("src/shared/theme/palette.vectors.json", "accents").EnumerateArray().Select(a => a.GetProperty("accent").GetString()!).Distinct().Order());
}
