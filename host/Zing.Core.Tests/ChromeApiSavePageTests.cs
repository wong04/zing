using System.Text;
using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>page:save, the direct route: the active tab's HTML into the Downloads folder under a numbered name.</summary>
public sealed class ChromeApiSavePageTests : IDisposable
{
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly string _downloads = Path.Combine(Path.GetTempPath(), "zing-save-page-" + Guid.NewGuid().ToString("N"));
	private readonly ChromeApi _api;

	public ChromeApiSavePageTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest()), _host, "test");
		_host.DownloadsDirectory = _downloads;
		_api.Start([], "about:blank");
	}

	public void Dispose()
	{
		_api.Dispose();
		if (Directory.Exists(_downloads)) Directory.Delete(_downloads, recursive: true);
	}

	private FakePage ActivePage => (FakePage)_api.Tabs.ActivePage!;

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	private async Task<JsonElement> OpenPage(string html, string title = "A page", string url = "https://example.com/a")
	{
		await Invoke("tabs:navigate", _api.Tabs.ActiveId!, url);
		ActivePage.Url = url;
		ActivePage.Title = title;
		ActivePage.ProbeResult = JsonSerializer.SerializeToElement(html);
		return await Invoke("page:save");
	}

	private string[] Saved() => Directory.Exists(_downloads) ? [.. Directory.GetFileSystemEntries(_downloads).Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal)] : [];

	[Fact]
	public async Task Writes_the_pages_html_to_downloads_as_utf8_and_names_the_file_for_the_renderer_without_a_path()
	{
		var result = await OpenPage("<!DOCTYPE html><html><body>caf\u00e9</body></html>", "My page");

		Assert.True(result.GetProperty("ok").GetBoolean());
		var value = result.GetProperty("value");
		Assert.Equal("My page.html", value.GetProperty("name").GetString());
		Assert.Matches("^[0-9a-f]{32}$", value.GetProperty("token").GetString()!);
		Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), value.ToString());
		var bytes = File.ReadAllBytes(Path.Combine(_downloads, "My page.html"));
		// A byte-order mark, so a meta charset in the page can't make the saved copy read as another encoding.
		Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
		Assert.Equal("<!DOCTYPE html><html><body>caf\u00e9</body></html>", Encoding.UTF8.GetString(bytes[3..]));
	}

	[Fact]
	public async Task A_second_save_is_numbered_and_the_first_is_untouched()
	{
		await OpenPage("<p>one</p>", "Same");
		var second = await Invoke("page:save");

		Assert.Equal("Same 2.html", second.GetProperty("value").GetProperty("name").GetString());
		Assert.Equal(["Same 2.html", "Same.html"], Saved());
		Assert.Contains("one", File.ReadAllText(Path.Combine(_downloads, "Same.html")));
	}

	[Fact]
	public async Task The_title_is_cleaned_into_a_safe_name_and_a_missing_one_becomes_page()
	{
		await OpenPage("x", "a/b:c*d?\"e\" <f>|g");
		await OpenPage("x", "   ");
		await OpenPage("x", "CON");

		Assert.Equal(["CON_.html", "abcde fg.html", "page.html"], Saved());
	}

	[Fact]
	public async Task A_very_long_title_is_shortened()
	{
		await OpenPage("x", new string('t', 500));

		var name = Assert.Single(Saved());
		Assert.True(name.Length <= 90, name);
		Assert.EndsWith(".html", name);
	}

	[Theory]
	[InlineData("about:blank")]
	[InlineData("file:///C:/secret.html")]
	[InlineData("javascript:alert(1)")]
	[InlineData("data:text/html,hi")]
	public async Task Only_a_web_page_can_be_saved(string url)
	{
		var result = await OpenPage("x", "t", url);

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Equal(nameof(PageNotSavableException), ErrorName(result));
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task A_page_in_reader_mode_is_refused_because_the_copy_would_not_be_the_page()
	{
		await Invoke("tabs:navigate", _api.Tabs.ActiveId!, "https://example.com/a");
		ActivePage.Url = "https://example.com/a";
		await ActivePage.ShowReaderAsync();

		var result = await Invoke("page:save");

		Assert.Equal(nameof(PageNotSavableException), ErrorName(result));
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task A_page_that_answers_with_something_other_than_text_is_refused()
	{
		await Invoke("tabs:navigate", _api.Tabs.ActiveId!, "https://example.com/a");
		ActivePage.Url = "https://example.com/a";
		ActivePage.ProbeResult = JsonSerializer.SerializeToElement(new { not = "text" });

		var result = await Invoke("page:save");

		Assert.Equal(nameof(PageNotSavableException), ErrorName(result));
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task A_page_over_the_size_limit_is_refused_and_leaves_nothing()
	{
		var result = await OpenPage(new string('x', PageSave.MaxBytes + 1));

		Assert.Equal(nameof(PageNotSavableException), ErrorName(result));
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task Nothing_is_left_beside_the_file_once_it_is_written()
	{
		await OpenPage("<p>x</p>", "Clean");

		Assert.Equal(["Clean.html"], Saved());
	}
}
