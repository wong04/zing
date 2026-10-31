using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class TabOrganizerTests
{
	private const string VectorsPath = "src/shared/browser/tabOrganizer.vectors.json";
	private const long Minute = 60_000;

	public static TheoryData<string> CaseNames()
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	public static TheoryData<string> SnapshotNames()
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("snapshotOf").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	public static TheoryData<string> DomainHosts()
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load("src/shared/browser/registrableDomain.vectors.json").GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("host").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(CaseNames))]
	public void Shared_vector(string name)
	{
		var root = Vectors.Load(VectorsPath);
		var c = root.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
		var actual = Normalise(Perform(root, c));
		var want = JsonNode.Parse(c.GetProperty("want").GetRawText());
		Assert.True(JsonNode.DeepEquals(want, actual), $"{name}: want {want?.ToJsonString()} got {actual?.ToJsonString()}");
	}

	[Theory]
	[MemberData(nameof(SnapshotNames))]
	public void Shared_snapshot_vector(string name)
	{
		var c = Vectors.Load(VectorsPath).GetProperty("snapshotOf").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
		var record = new TabRecord("a", c.GetProperty("url").GetString()!, "a", false, 1, 1, 1);
		var snap = (JsonObject)Normalise(TabSnapshot.Of(record, c.GetProperty("active").GetBoolean()))!;
		foreach (var field in c.GetProperty("want").EnumerateObject())
			Assert.True(JsonNode.DeepEquals(JsonNode.Parse(field.Value.GetRawText()), snap[field.Name]), $"{name}: {field.Name} was {snap[field.Name]?.ToJsonString()}");
	}

	[Theory]
	[MemberData(nameof(DomainHosts))]
	public void Registrable_domain_vector(string host)
	{
		var c = Vectors.Load("src/shared/browser/registrableDomain.vectors.json").GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("host").GetString() == host);
		Assert.Equal(c.GetProperty("want").GetString(), RegistrableDomain.Of(host));
	}

	[Fact]
	public void Stacks_serialise_like_the_ts_stack()
	{
		var stack = Stack.Make("a.com", ["a", "b"], "a.com");
		Assert.Equal("""{"domain":"a.com","ids":["a","b"],"name":"a.com","key":"a.com"}""", JsonSerializer.Serialize(stack, ZingJson.Options));
	}

	[Fact]
	public void The_dropdown_state_serialises_like_the_ts_stack_dropdown_state()
	{
		var tab = new TabView("a", "https://a.test/", "A", false, 1, 1, 1, false, false, false);
		var members = new StackMembers("a.test", [tab], "a");
		var json = JsonSerializer.Serialize(members.ToState("a.test", 120.5), ZingJson.Options);
		Assert.Equal("""{"key":"a.test","name":"a.test","anchorX":120.5,"tabs":[{"id":"a","url":"https://a.test/","title":"A","pinned":false,"createdAt":1,"lastActiveAt":1,"updatedAt":1,"loading":false,"canGoBack":false,"canGoForward":false}],"activeId":"a"}""", json);
	}

	[Fact]
	public void Stack_members_are_the_live_pages_in_stack_order_and_need_two_pages()
	{
		TabView View(string id) => new(id, $"https://{id}.test/", id, false, 1, 1, 1, false, false, false);
		var list = new TabsState([View("x"), View("a1"), View("a2")], "a2", "manual", true, null);
		var stacks = new[] { Stack.Make("a.test", ["a2", "gone", "a1"], "a.test"), Stack.Make("x.test", ["x"], "x.test") };
		var members = StackMembers.Of(list, stacks, "a.test")!;
		Assert.Equal("a.test", members.Name);
		Assert.Equal(["a2", "a1"], members.Tabs.Select(t => t.Id));
		Assert.Equal("a2", members.ActiveId);
		Assert.Null(StackMembers.Of(list, stacks, "x.test"));
		Assert.Null(StackMembers.Of(list, stacks, "nope"));
	}

	[Fact]
	public void Organisation_names_round_trip_through_the_setting_spelling()
	{
		foreach (var organization in Enum.GetValues<TabOrganization>())
			Assert.Equal(organization, TabOrganizations.Parse(organization.ToSetting()));
		Assert.Equal("recentlyUsed", TabOrganization.RecentlyUsed.ToSetting());
		Assert.Throws<ArgumentException>(() => TabOrganizations.Parse("sideways"));
	}

	private static JsonNode? Normalise(object? value) => JsonNode.Parse(JsonSerializer.Serialize(value, ZingJson.Options));

	private static object? Perform(JsonElement root, JsonElement c)
	{
		var op = c.GetProperty("op").GetString();
		switch (op)
		{
			case "order": return TabOrganizer.Order(TabsOf(root, c), Organization(c));
			case "stacks": return Project(TabOrganizer.Stacks(TabsOf(root, c)), c);
			case "stacksByName": return Project(TabOrganizer.StacksByName(TabsOf(root, c)), c);
			case "orderedStacks":
			{
				var tabs = TabsOf(root, c);
				var byId = tabs.ToDictionary(t => t.Id);
				var arranged = TabOrganizer.Order(tabs, Organization(c)).Select(id => byId[id]).ToList();
				return Project(TabOrganizer.Stacks(arranged), c);
			}
			case "movingStack": return TabOrganizer.MovingStack(Groups(c), Int(c, "from"), Int(c, "to")).SelectMany(g => g).ToList();
			case "moving": return TabOrganizer.Moving(Str(c, "id"), Strings(c, "ids"), Int(c, "index"));
			case "freeze": return TabOrganizer.FreezeDisplayedOrder(TabsOf(root, c), Organization(c));
			case "arrange":
				try
				{
					return TabOrganizer.Arrange(TabsOf(root, c), Groups(c));
				}
				catch (TabArrangementException)
				{
					return new { throws = "TabArrangementError" };
				}
			case "moveTab": return TabOrganizer.MoveTab(TabsOf(root, c), Int(c, "from"), Int(c, "to"));
			case "moveWithin": return TabOrganizer.MoveWithin(TabsOf(root, c), Str(c, "id"), Strings(c, "stackIds"), Int(c, "index"));
			case "siteGroup": return new { name = SiteGroup.OfUri(new Uri(Str(c, "url"))).Name };
			case "siteGroupKeys": return Strings(c, "urls").Select(url => SiteGroup.OfUri(new Uri(url)).Key).ToList();
			case "sameKey":
			{
				var keys = Strings(c, "urls").Select(url => SiteGroup.OfUri(new Uri(url)).Key).ToList();
				return keys[0] == keys[1];
			}
			default: throw new InvalidOperationException($"Unknown op {op}");
		}
	}

	private static object? Project(List<Stack> list, JsonElement c) => Str(c, "project") switch
	{
		"count" => list.Count,
		"domain" => list.Select(s => s.Domain).ToList(),
		"name" => list.Select(s => s.Name).ToList(),
		"nameCount" => list.Select(s => $"{s.Name} {s.Ids.Count}").ToList(),
		"firstIds" => list.FirstOrDefault()?.Ids,
		"firstIdsLength" => list.FirstOrDefault()?.Ids.Count,
		"firstDomain" => list.FirstOrDefault()?.Domain,
		"lastDomain" => list.LastOrDefault()?.Domain,
		"full" => list,
		var other => throw new InvalidOperationException($"Unknown projection {other}"),
	};

	private static List<TabSnapshot> TabsOf(JsonElement root, JsonElement c)
	{
		var specs = new List<JsonElement>();
		if (c.TryGetProperty("prepend", out var prepend)) specs.AddRange(prepend.EnumerateArray());
		if (c.TryGetProperty("tabs", out var tabs))
			specs.AddRange(tabs.ValueKind == JsonValueKind.String ? root.GetProperty("fixtures").GetProperty(tabs.GetString()!).EnumerateArray() : tabs.EnumerateArray());
		if (c.TryGetProperty("append", out var append)) specs.AddRange(append.EnumerateArray());
		return specs.Select(spec => Snapshot(root, spec)).ToList();
	}

	private static TabSnapshot Snapshot(JsonElement root, JsonElement spec)
	{
		if (spec.TryGetProperty("url", out var urlElement))
		{
			var url = urlElement.GetString()!;
			var parsed = new Uri(url);
			var group = SiteGroup.OfUri(parsed);
			return TabSnapshot.Make(url, url, RegistrableDomain.Of(parsed.IdnHost), 0, 0, group.Key, group.Name);
		}
		var baseTime = root.GetProperty("base").GetInt64();
		var id = Str(spec, "id");
		return TabSnapshot.Make(
			id,
			id,
			Str(spec, "domain"),
			baseTime - Opt(spec, "accessedMinutesAgo") * Minute,
			baseTime - Opt(spec, "createdMinutesAgo") * Minute,
			isPinned: spec.TryGetProperty("pinned", out var pinned) && pinned.GetBoolean());
	}

	private static long Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetInt64() : 0;
	private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
	private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();
	private static List<string> Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToList();
	private static TabOrganization Organization(JsonElement c) => TabOrganizations.Parse(Str(c, "organization"));
	private static List<IReadOnlyList<string>> Groups(JsonElement c) => c.GetProperty("groups").EnumerateArray().Select(g => (IReadOnlyList<string>)g.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList();
}
