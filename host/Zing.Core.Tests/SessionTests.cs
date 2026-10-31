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
	public void Preparing_respects_the_cap_and_keeps_every_pinned_tab_through_it()
	{
		var c = V.GetProperty("prepare")[0];
		var gen = c.GetProperty("generate");
		var loose = Enumerable.Range(0, gen.GetProperty("loose").GetInt32()).Select(i => new SessionTab($"https://loose{i}.com", "t", false, false));
		var pinned = Enumerable.Range(0, gen.GetProperty("pinned").GetInt32()).Select(i => new SessionTab($"https://pinned{i}.com", "t", true, false));
		var capped = Session.Prepare(loose.Concat(pinned));
		Assert.Equal(Session.MaxSessionTabs, c.GetProperty("expectLength").GetInt32());
		Assert.Equal(Session.MaxSessionTabs, capped.Count);
		Assert.Equal(c.GetProperty("expectPinned").GetInt32(), capped.Count(t => t.Pinned));
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
		Assert.Equal(Tabs(c.GetProperty("expect")), Session.Decode(c.GetProperty("text").GetString()!));
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
	public void A_corrupt_file_is_no_tabs()
	{
		var path = Path.Combine(_dir, "s.json");
		File.WriteAllText(path, "{{{not json");
		Assert.Empty(Session.Load(path));
	}

	[Fact]
	public void A_saved_file_loads_back()
	{
		var path = Path.Combine(_dir, "s.json");
		var tabs = new List<SessionTab> { new("https://a.test/", "a", true, false), new("https://b.test/", "b", false, true) };
		File.WriteAllText(path, Session.Encode(tabs));
		Assert.Equal(tabs, Session.Load(path));
	}

	[Fact]
	public void A_file_that_exists_but_cannot_be_read_is_an_error()
	{
		Assert.Throws<SessionFileException>(() => Session.Load(_dir));
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
