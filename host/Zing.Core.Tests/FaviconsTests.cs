using System.Text.Json;

namespace Zing.Core.Tests;

public sealed class FaviconsTests
{
	private const string VectorsPath = "src/shared/browser/faviconChoice.vectors.json";

	private static FaviconLink Link(JsonElement init) => FaviconLink.Make(
		init.GetProperty("href").GetString()!,
		init.GetProperty("rel").GetString()!,
		init.TryGetProperty("sizes", out var sizes) ? sizes.GetString() : null,
		init.TryGetProperty("type", out var type) ? type.GetString() : null,
		init.TryGetProperty("mediaMatches", out var media) ? media.GetBoolean() : null);

	private static string? Choose(JsonElement links) => FaviconChoice.Best(links.EnumerateArray().Select(Link));

	[Fact]
	public void The_pages_own_best_icon_is_chosen()
	{
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("best").EnumerateArray())
		{
			var expected = c.GetProperty("expected");
			Assert.True(
				(expected.ValueKind == JsonValueKind.Null ? null : expected.GetString()) == Choose(c.GetProperty("links")),
				c.GetProperty("name").GetString());
		}
	}

	[Fact]
	public void Products_on_one_host_keep_different_icons()
	{
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("distinct").EnumerateArray())
		{
			var first = Choose(c.GetProperty("a"));
			Assert.NotNull(first);
			Assert.NotEqual(first, Choose(c.GetProperty("b")));
		}
	}

	[Fact]
	public void Sizes_parse()
	{
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("largestSize").EnumerateArray())
		{
			var expected = c.GetProperty("expected");
			Assert.Equal(expected.ValueKind == JsonValueKind.Null ? null : expected.GetInt32(), FaviconChoice.LargestSize(c.GetProperty("sizes").GetString()!));
		}
	}

	[Fact]
	public void The_probe_result_becomes_links_and_skips_entries_without_an_href()
	{
		using var probe = JsonDocument.Parse("""[{"href":"https://x.com/A.png","rel":"ICON","sizes":"32X32","type":"","mediaMatches":false},{"rel":"icon"},5]""");
		var link = Assert.Single(FaviconChoice.ParseProbe(probe.RootElement));
		Assert.Equal(new FaviconLink("https://x.com/A.png", "icon", "32x32", "", false), link);
		using var notAnArray = JsonDocument.Parse("null");
		Assert.Empty(FaviconChoice.ParseProbe(notAnArray.RootElement));
	}

	private sealed class Fetcher
	{
		public List<IReadOnlyList<string>> Calls { get; } = [];
		public bool Miss { get; init; }

		public Task<string?> Fetch(IReadOnlyList<string> candidates)
		{
			Calls.Add(candidates);
			return Task.FromResult<string?>(Miss ? null : $"data:image/png;base64,{candidates[0]}");
		}
	}

	private static (FaviconStore Store, Fetcher Fetcher, List<int> Changes) NewStore(int capacity = 256, bool miss = false)
	{
		var fetcher = new Fetcher { Miss = miss };
		var changes = new List<int>();
		return (new FaviconStore(fetcher.Fetch, () => changes.Add(1), capacity), fetcher, changes);
	}

	[Fact]
	public async Task The_declared_icon_beats_the_host_icon()
	{
		var (store, _, _) = NewStore();
		await store.LoadHost("docs.google.com");
		await store.LoadIcon("https://docs.google.com/docs-icon.png");
		Assert.Contains("docs-icon.png", store.Icon("https://docs.google.com/docs-icon.png", "docs.google.com"));
	}

	[Fact]
	public async Task The_host_icon_stands_in_until_the_declared_one_loads()
	{
		var (store, _, _) = NewStore();
		await store.LoadHost("example.com");
		Assert.Contains("https://example.com/favicon.ico", store.Icon("https://example.com/declared.png", "example.com"));
	}

	[Fact]
	public async Task Sibling_subdomains_never_share_an_icon()
	{
		var (store, _, _) = NewStore();
		await store.LoadHost("mail.google.com");
		Assert.NotNull(store.Icon(null, "mail.google.com"));
		Assert.Null(store.Icon(null, "docs.google.com"));
		Assert.Null(store.Icon(null, "google.com"));
	}

	[Fact]
	public async Task Host_case_does_not_matter()
	{
		var (store, _, _) = NewStore();
		await store.LoadHost("Example.COM");
		Assert.NotNull(store.Icon(null, "example.com"));
	}

	[Fact]
	public async Task Each_key_is_fetched_once_even_when_requested_concurrently()
	{
		var (store, fetcher, _) = NewStore();
		await Task.WhenAll(store.LoadIcon("https://a.test/i.png"), store.LoadIcon("https://a.test/i.png"));
		await store.LoadIcon("https://a.test/i.png");
		Assert.Single(fetcher.Calls);
	}

	[Fact]
	public async Task A_miss_is_remembered()
	{
		var (store, fetcher, _) = NewStore(miss: true);
		await store.LoadHost("none.test");
		await store.LoadHost("none.test");
		Assert.Single(fetcher.Calls);
		Assert.Null(store.Icon(null, "none.test"));
	}

	[Fact]
	public async Task The_host_is_asked_for_its_conventional_locations()
	{
		var (store, fetcher, _) = NewStore();
		await store.LoadHost("example.com");
		Assert.Equal(FaviconStore.ConventionalIconUrls("example.com"), Assert.Single(fetcher.Calls));
	}

	[Fact]
	public async Task The_least_recently_used_entry_goes_first()
	{
		var (store, _, _) = NewStore(2);
		await store.LoadIcon("https://a.test/1.png");
		await store.LoadIcon("https://a.test/2.png");
		store.Icon("https://a.test/1.png", "");
		await store.LoadIcon("https://a.test/3.png");
		Assert.Equal(2, store.Size);
		Assert.Null(store.Icon("https://a.test/2.png", ""));
		Assert.NotNull(store.Icon("https://a.test/1.png", ""));
		Assert.NotNull(store.Icon("https://a.test/3.png", ""));
	}

	[Fact]
	public async Task A_change_is_reported_after_each_load()
	{
		var (store, _, changes) = NewStore();
		await store.LoadIcon("https://a.test/1.png");
		Assert.Single(changes);
	}

	[Fact]
	public void Only_web_pages_have_a_host()
	{
		Assert.Equal("example.com", FaviconService.HostOf("https://example.com/x"));
		Assert.Equal("", FaviconService.HostOf("about:blank"));
		Assert.Equal("", FaviconService.HostOf("file:///C:/a.html"));
		Assert.Equal("", FaviconService.HostOf("not a url"));
	}

	[Fact]
	public async Task A_probed_icon_is_recorded_on_the_tab_and_loaded()
	{
		var (store, fetcher, _) = NewStore();
		var service = new FaviconService(store);
		var recorded = new List<string>();
		using var probe = JsonDocument.Parse("""[{"href":"https://example.com/i.png","rel":"icon"}]""");
		await service.OnProbed("https://example.com/", probe.RootElement, recorded.Add);
		Assert.Equal(["https://example.com/i.png"], recorded);
		Assert.Equal(["https://example.com/i.png"], Assert.Single(fetcher.Calls));
		Assert.NotNull(service.IconFor("https://example.com/i.png", "https://example.com/"));
	}

	[Fact]
	public async Task A_page_that_declares_nothing_gets_its_hosts_conventional_icon()
	{
		var (store, fetcher, _) = NewStore();
		var service = new FaviconService(store);
		using var probe = JsonDocument.Parse("[]");
		await service.OnProbed("https://example.com/", probe.RootElement, _ => throw new InvalidOperationException("nothing to record"));
		Assert.Equal(FaviconStore.ConventionalIconUrls("example.com"), Assert.Single(fetcher.Calls));
		await service.OnProbed("about:blank", probe.RootElement, null);
		Assert.Single(fetcher.Calls);
	}

	[Fact]
	public async Task Candidates_are_tried_in_order_and_dead_ones_skipped()
	{
		var tried = new List<string>();
		var loaded = await IconLimits.FirstLoadedAsync(["ftp://x/i", "https://a/1", "https://a/2"], url =>
		{
			tried.Add(url);
			return url == "https://a/1" ? throw new IconRejectedException(url, "HTTP 404") : Task.FromResult("data:ok");
		});
		Assert.Equal("data:ok", loaded);
		Assert.Equal(["https://a/1", "https://a/2"], tried);
		Assert.Null(await IconLimits.FirstLoadedAsync(["https://a/1"], url => throw new IconRejectedException(url, "gone")));
	}

	[Fact]
	public void Implausible_bodies_are_rejected()
	{
		Assert.Throws<IconRejectedException>(() => IconLimits.CheckDownloaded("https://a/i", IconLimits.MinBytes - 1));
		Assert.Throws<IconRejectedException>(() => IconLimits.CheckDownloaded("https://a/i", IconLimits.MaxBytes + 1));
		IconLimits.CheckDownloaded("https://a/i", 500);
		Assert.Throws<IconRejectedException>(() => IconLimits.CheckDataUrl(new string('a', IconLimits.MaxDataUrlChars + 1)));
	}
}
