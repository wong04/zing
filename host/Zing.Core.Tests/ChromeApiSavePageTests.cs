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
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
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

	private static JsonElement Html(string html) => JsonSerializer.SerializeToElement(html);

	private async Task<FakePage> WebPage()
	{
		await Invoke("tabs:navigate", _api.Tabs.ActiveId!, "https://example.com/a");
		ActivePage.Url = "https://example.com/a";
		ActivePage.Title = "First";
		return ActivePage;
	}

	[Fact]
	public async Task A_tab_that_navigates_while_the_text_is_read_is_refused_rather_than_saved_under_the_wrong_page()
	{
		var page = await WebPage();
		page.Evaluating = () =>
		{
			page.Url = "https://example.org/b";
			page.Title = "Second";
			return Task.FromResult(Html("<p>second page</p>"));
		};

		var result = await Invoke("page:save");

		Assert.Equal(nameof(PageNotSavableException), ErrorName(result));
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task A_tab_that_leaves_the_web_while_the_text_is_read_is_refused()
	{
		var page = await WebPage();
		page.Evaluating = () =>
		{
			page.Url = "about:blank";
			return Task.FromResult(Html("<p>x</p>"));
		};

		Assert.Equal(nameof(PageNotSavableException), ErrorName(await Invoke("page:save")));
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task A_change_of_the_fragment_alone_is_still_the_same_page()
	{
		var page = await WebPage();
		page.Evaluating = () =>
		{
			page.Url = "https://example.com/a#section";
			return Task.FromResult(Html("<p>same</p>"));
		};

		Assert.True((await Invoke("page:save")).GetProperty("ok").GetBoolean());
		Assert.Equal(["First.html"], Saved());
	}

	[Fact]
	public async Task The_name_comes_from_the_title_the_tab_had_before_the_script_ran()
	{
		var page = await WebPage();
		page.Evaluating = () =>
		{
			page.Title = "Changed by the page";
			return Task.FromResult(Html("<p>x</p>"));
		};

		await Invoke("page:save");

		Assert.Equal(["First.html"], Saved());
	}

	[Fact]
	public async Task A_script_that_fails_is_reported_as_a_page_that_cant_be_saved()
	{
		var page = await WebPage();
		page.Evaluating = () => Task.FromException<JsonElement>(new InvalidOperationException("WebView2 process failed"));

		var result = await Invoke("page:save");

		Assert.Equal(nameof(PageNotSavableException), ErrorName(result));
		Assert.Empty(Saved());
	}

	[Fact]
	public async Task A_dead_view_or_a_bad_answer_is_a_page_that_cant_be_saved()
	{
		var page = await WebPage();
		foreach (Exception failure in new Exception[] { new System.Runtime.InteropServices.COMException("WebView2 gone"), new InvalidOperationException("The page is not running"), new JsonException("not json") })
		{
			page.Evaluating = () => Task.FromException<JsonElement>(failure);
			await Assert.ThrowsAsync<PageNotSavableException>(() => PageSave.ReadAsync(page));
		}
	}

	[Fact]
	public async Task A_programming_error_is_not_reported_as_a_crashed_page()
	{
		var page = await WebPage();
		foreach (Exception failure in new Exception[] { new NullReferenceException(), new ObjectDisposedException("x"), new ArgumentException("bad") })
		{
			page.Evaluating = () => Task.FromException<JsonElement>(failure);
			var raised = await Assert.ThrowsAnyAsync<Exception>(() => PageSave.ReadAsync(page));
			Assert.IsNotType<PageNotSavableException>(raised);
			Assert.Same(failure.GetType(), raised.GetType());
		}
	}

	[Fact]
	public async Task A_page_that_never_answers_times_out_as_a_page_that_cant_be_saved()
	{
		var page = await WebPage();
		page.Evaluating = () => new TaskCompletionSource<JsonElement>().Task;

		var error = await Assert.ThrowsAsync<PageNotSavableException>(() => PageSave.ReadAsync(page, TimeSpan.FromMilliseconds(100)));

		Assert.Contains("didn't answer", error.Message);
		Assert.Empty(Saved());
	}

	[Fact]
	public void A_title_is_cut_without_splitting_a_surrogate_pair()
	{
		var directory = Path.Combine(_downloads, "cut");
		var path = PageSave.Write(directory, new string('a', 79) + "😀tail", "x");

		var name = Path.GetFileNameWithoutExtension(path);
		Assert.Equal(new string('a', 79), name);
		Assert.DoesNotContain(name, char.IsSurrogate);
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
