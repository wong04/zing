using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class ChromeApiTests
{
	private sealed class FakePage : IPage
	{
		public List<string> Calls { get; } = [];
		public bool CanGoBack { get; set; }
		public bool CanGoForward { get; set; }
		public bool IsLoading { get; set; }
		public void Navigate(string url) => Calls.Add($"navigate {url}");
		public void GoBack() => Calls.Add("back");
		public void GoForward() => Calls.Add("forward");
		public void Reload() => Calls.Add("reload");
		public void Stop() => Calls.Add("stop");
	}

	private readonly Bridge _bridge = new();
	private readonly TabStore _tabs = new(() => 1);
	private readonly FakePage _page = new();
	private readonly List<(string Channel, object? Payload)> _pushed = [];
	private readonly string _tabId;

	public ChromeApiTests()
	{
		var settings = ZingJson.LoadDefaults("settings.json").AsObject();
		_ = new ChromeApi(_bridge, _tabs, Address.FromDefaults(), settings, ZingJson.LoadDefaults("library.json"), _ => _page, (c, p) => _pushed.Add((c, p)), "test");
		_tabId = _tabs.Open("about:blank").Id;
	}

	private async Task<JsonElement> Invoke(string channel, params object[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	/// <summary>The result's value; a handler that returns nothing sends no `value`, which the renderer reads as undefined.</summary>
	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : default;
	}

	[Fact]
	public async Task Typed_text_is_resolved_and_loaded_in_that_tab()
	{
		Value(await Invoke("tabs:navigate", _tabId, "hello world"));
		Assert.Equal(["navigate https://www.google.com/search?q=hello%20world"], _page.Calls);
	}

	[Fact]
	public async Task Blank_input_does_nothing()
	{
		Value(await Invoke("tabs:navigate", _tabId, "   "));
		Assert.Empty(_page.Calls);
	}

	[Fact]
	public async Task Navigating_an_unknown_tab_is_an_error()
	{
		var result = await Invoke("tabs:navigate", "nope", "example.com");
		Assert.Equal(nameof(UnknownTabException), result.GetProperty("error").GetProperty("name").GetString());
	}

	[Fact]
	public async Task Back_is_done_only_when_there_is_history()
	{
		Assert.Equal("not-applicable", Value(await Invoke("command:run", "history.back")).GetProperty("status").GetString());
		_page.CanGoBack = true;
		Assert.Equal("done", Value(await Invoke("command:run", "history.back")).GetProperty("status").GetString());
		Assert.Equal(["back"], _page.Calls);
	}

	[Fact]
	public async Task Commands_not_ported_yet_say_so_with_their_label()
	{
		var outcome = Value(await Invoke("command:run", "page.print"));
		Assert.Equal("not-implemented", outcome.GetProperty("status").GetString());
		Assert.Equal("Print…", outcome.GetProperty("label").GetString());
	}

	[Fact]
	public async Task Open_location_focuses_the_address_bar()
	{
		Value(await Invoke("command:run", "location.open"));
		Assert.Contains(_pushed, p => p.Channel == "chrome:focus-address");
	}

	[Fact]
	public async Task The_chrome_gets_the_typescript_defaults()
	{
		var settings = Value(await Invoke("settings:get"));
		Assert.Equal("google", settings.GetProperty("searchEngine").GetString());
		var library = Value(await Invoke("library:get"));
		Assert.Equal("Favourites", library.GetProperty("bookmarks")[0].GetProperty("title").GetString());
	}

	[Fact]
	public async Task Tabs_state_lists_the_tab_with_the_strip_settings()
	{
		var state = Value(await Invoke("tabs:get"));
		Assert.Equal(_tabId, state.GetProperty("activeId").GetString());
		Assert.Equal("manual", state.GetProperty("organization").GetString());
		Assert.Equal(1, state.GetProperty("tabs").GetArrayLength());
	}

	[Fact]
	public void Settings_without_a_reader_fail_loudly()
	{
		var broken = new JsonObject { ["tabDropdowns"] = false };
		var api = new ChromeApi(new Bridge(), _tabs, Address.FromDefaults(), broken, ZingJson.LoadDefaults("library.json"), _ => _page, (_, _) => { }, "test");
		Assert.Throws<InvalidDataException>(() => api.Snapshot());
	}
}
