using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class SessionTests : IDisposable
{
	private static readonly JsonElement V = Vectors.Load("src/shared/session/session.vectors.json");

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-session-").FullName;

	public void Dispose() => Directory.Delete(_dir, true);

	private static SessionTab Tab(JsonElement e) =>
		new(e.GetProperty("url").GetString()!, e.GetProperty("title").GetString()!, e.GetProperty("pinned").GetBoolean(), e.GetProperty("active").GetBoolean());

	private static List<SessionTab> Tabs(JsonElement array) => array.EnumerateArray().Select(Tab).ToList();

	public static TheoryData<string> Yes() => Urls("yes");

	public static TheoryData<string> No() => Urls("no");

	private static TheoryData<string> Urls(string key)
	{
		var data = new TheoryData<string>();
		foreach (var u in V.GetProperty("restorableUrls").GetProperty(key).EnumerateArray()) data.Add(u.GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(Yes))]
	public void Web_pages_are_restorable(string url) => Assert.True(Session.IsRestorableUrl(url));

	[Theory]
	[MemberData(nameof(No))]
	public void Everything_else_is_not(string url) => Assert.False(Session.IsRestorableUrl(url));

	[Fact]
	public void Preparing_keeps_every_tab_however_many_in_order()
	{
		var c = V.GetProperty("prepare")[0];
		var gen = c.GetProperty("generate");
		var loose = Enumerable.Range(0, gen.GetProperty("loose").GetInt32()).Select(i => new SessionTab($"https://loose{i}.com", "t", false, false));
		var pinned = Enumerable.Range(0, gen.GetProperty("pinned").GetInt32()).Select(i => new SessionTab($"https://pinned{i}.com", "t", true, false));
		var all = loose.Concat(pinned).ToList();
		var kept = Session.Prepare(all);
		Assert.Equal(c.GetProperty("expectLength").GetInt32(), kept.Count);
		Assert.Equal(c.GetProperty("expectPinned").GetInt32(), kept.Count(t => t.Pinned));
		Assert.Equal(all, kept);
	}

	[Fact]
	public void An_active_tab_and_a_split_far_down_the_list_survive_a_save()
	{
		var tabs = Enumerable.Range(0, 80).Select(i => new SessionTab($"https://t{i}.test/", "t", false, i == 70, Split: i >= 76 ? new SessionSplit("s", "row", i - 76, 0.25) : null)).ToList();
		var saved = Session.Decode(Session.Encode(tabs));
		Assert.Equal(80, saved.Count);
		Assert.True(saved[70].Active);
		Assert.Equal(4, saved.Count(t => t.Split is not null));
	}

	[Fact]
	public void Preparing_leaves_order_alone_under_the_cap()
	{
		var c = V.GetProperty("prepare")[1];
		Assert.Equal(Tabs(c.GetProperty("expect")), Session.Prepare(Tabs(c.GetProperty("tabs"))));
	}

	[Fact]
	public void Save_filters_unrestorable_tabs_and_remembers_pinned_and_active()
	{
		var trip = V.GetProperty("roundTrip");
		Assert.Equal(Tabs(trip.GetProperty("expect")), Session.Decode(Session.Encode(Tabs(trip.GetProperty("tabs")))));
	}

	[Fact]
	public void Encoded_tabs_use_the_ts_field_names()
	{
		using var doc = JsonDocument.Parse(Session.Encode([new SessionTab("https://a.com", "A", true, false)]));
		Assert.Equal(
			["active", "pinned", "title", "url"],
			doc.RootElement[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
	}

	public static TheoryData<string> DecodeNames()
	{
		var data = new TheoryData<string>();
		foreach (var c in V.GetProperty("decode").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(DecodeNames))]
	public void Decoding(string name)
	{
		var c = V.GetProperty("decode").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
		var text = c.GetProperty("text").GetString()!;
		if (c.TryGetProperty("error", out _))
		{
			var error = Assert.ThrowsAny<Exception>(() => Session.Decode(text));
			Assert.True(error is JsonException or InvalidDataException, $"{name}: {error.GetType().Name}");
			return;
		}
		Assert.Equal(Tabs(c.GetProperty("expect")), Session.Decode(text));
	}

	[Fact]
	public void Records_become_session_tabs_with_the_active_one_marked()
	{
		var v = V.GetProperty("toSessionTabs");
		var records = v.GetProperty("records").EnumerateArray()
			.Select(r => new TabRecord(r.GetProperty("id").GetString()!, r.GetProperty("url").GetString()!, r.GetProperty("title").GetString()!, r.GetProperty("pinned").GetBoolean(), 0, 0, 0))
			.ToList();
		Assert.Equal(Tabs(v.GetProperty("expect")), Session.ToSessionTabs(records, v.GetProperty("activeId").GetString()));
	}

	[Fact]
	public void A_missing_file_is_a_first_run()
	{
		Assert.Empty(Session.Load(Path.Combine(_dir, "none.json")));
	}

	[Fact]
	public void A_corrupt_file_is_an_error_not_no_tabs()
	{
		var path = Path.Combine(_dir, "s.json");
		File.WriteAllText(path, "{{{not json");
		var error = Assert.Throws<SessionFileException>(() => Session.Load(path));
		Assert.IsAssignableFrom<JsonException>(error.InnerException);
	}

	[Fact]
	public void A_saved_file_loads_back_with_every_window_in_order()
	{
		var path = Path.Combine(_dir, "s.json");
		List<SessionTab> first = [new("https://a.test/", "a", true, false), new("https://b.test/", "b", false, true)];
		List<SessionTab> second = [new("https://c.test/", "c", false, true, Split: new SessionSplit("s", "row", 0, 1))];
		File.WriteAllText(path, Session.EncodeWindows([first, second]));
		var loaded = Session.Load(path);
		Assert.Equal(2, loaded.Count);
		Assert.Equal(first, loaded[0]);
		Assert.Equal(second, loaded[1]);
	}

	[Fact]
	public void The_file_is_versioned_with_a_list_of_windows()
	{
		using var doc = JsonDocument.Parse(Session.EncodeWindows([[new SessionTab("https://a.test/", "a", false, true)]]));
		Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
		Assert.Equal("https://a.test/", doc.RootElement.GetProperty("windows")[0].GetProperty("tabs")[0].GetProperty("url").GetString());
	}

	[Fact]
	public void An_old_single_window_file_loads_as_one_window()
	{
		var path = Path.Combine(_dir, "s.json");
		var tabs = new List<SessionTab> { new("https://a.test/", "a", true, false), new("https://b.test/", "b", false, true) };
		File.WriteAllText(path, Session.Encode(tabs));
		Assert.Equal(tabs, Assert.Single(Session.Load(path)));
	}

	[Fact]
	public void An_old_file_with_no_web_page_is_no_window()
	{
		Assert.Empty(Session.DecodeWindows("[]"));
		Assert.Empty(Session.DecodeWindows("""[{"url": "zing://home", "title": "t", "pinned": false, "active": true}]"""));
	}

	[Fact]
	public void A_window_with_nothing_to_restore_is_left_out()
	{
		var text = Session.EncodeWindows([[new SessionTab("zing://home", "t", false, true)], [new SessionTab("https://a.test/", "a", false, true)]]);
		Assert.Equal("https://a.test/", Assert.Single(Assert.Single(Session.DecodeWindows(text))).Url);
	}

	[Theory]
	[InlineData("""{"version": 3, "windows": []}""")]
	[InlineData("""{"windows": []}""")]
	[InlineData("""{"version": 2}""")]
	[InlineData("""{"version": 2, "windows": [[]]}""")]
	[InlineData("""{"version": 2, "windows": [{"tabs": [{"url": "https://a.test/"}]}]}""")]
	[InlineData("\"tabs\"")]
	public void A_file_that_is_not_a_session_of_a_known_version_is_corrupt(string text)
	{
		Assert.Throws<InvalidDataException>(() => Session.DecodeWindows(text));
	}

	[Fact]
	public void The_relaunch_file_comes_back_once_and_is_deleted()
	{
		var path = Path.Combine(_dir, "relaunch.json");
		File.WriteAllText(path, Session.EncodeWindows([[new SessionTab("https://a.test/", "a", false, true)], [new SessionTab("https://b.test/", "b", false, true)]]));
		Assert.Equal(2, Session.TakeOnce(path, e => throw e).Count);
		Assert.False(File.Exists(path));
		Assert.Empty(Session.TakeOnce(path, e => throw e));
	}

	[Fact]
	public void A_corrupt_relaunch_file_is_an_error_and_is_still_deleted()
	{
		var path = Path.Combine(_dir, "relaunch.json");
		File.WriteAllText(path, "{{{not json");
		Assert.Throws<SessionFileException>(() => Session.TakeOnce(path, e => throw e));
		Assert.False(File.Exists(path));
	}

	[Fact]
	public void A_file_that_exists_but_cannot_be_read_is_an_error()
	{
		Assert.Throws<SessionFileException>(() => Session.Load(_dir));
	}

	[Theory]
	[InlineData("{{{not json")]
	[InlineData("{\"url\": \"https://a.test/\"}")]
	[InlineData("[{\"url\": \"https://a.test/\"}]")]
	public void At_startup_a_corrupt_file_is_kept_aside_as_settings_is_and_told(string text)
	{
		var path = Path.Combine(_dir, "s.json");
		File.WriteAllText(path, text);
		var loaded = Session.LoadOrRecover(path, new DateTime(2026, 11, 19, 10, 0, 0));
		Assert.Empty(loaded.Value);
		var problem = Assert.IsType<LoadProblem>(loaded.Problem);
		Assert.Equal($"{path}.corrupt-20261119-100000", problem.KeptAs);
		Assert.Equal(text, File.ReadAllText(problem.KeptAs!));
		Assert.False(File.Exists(path));
	}

	[Fact]
	public void At_startup_an_unreadable_file_is_no_tabs_and_a_problem_not_a_throw()
	{
		var loaded = Session.LoadOrRecover(_dir, new DateTime(2026, 11, 19));
		Assert.Empty(loaded.Value);
		var problem = Assert.IsType<LoadProblem>(loaded.Problem);
		Assert.IsType<SessionFileException>(problem.Error);
		Assert.True(problem.OriginalInPlace);
		Assert.True(Directory.Exists(_dir));
	}

	[Fact]
	public void Discarding_removes_the_file_and_tolerates_none()
	{
		var path = Path.Combine(_dir, "s.json");
		File.WriteAllText(path, "[]");
		Session.Discard(path);
		Assert.False(File.Exists(path));
		Session.Discard(path);
	}
}
