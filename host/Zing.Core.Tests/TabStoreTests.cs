using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class TabStoreTests
{
	private const string VectorsPath = "src/shared/model/tab-store.vectors.json";

	public static TheoryData<string> CaseNames()
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("cases").EnumerateArray())
			data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(CaseNames))]
	public void Shared_vector(string name)
	{
		var cases = Vectors.Load(VectorsPath).GetProperty("cases").EnumerateArray();
		new VectorRun(cases.Single(c => c.GetProperty("name").GetString() == name)).Execute();
	}

	[Fact]
	public void Default_ids_are_guids()
	{
		var store = new TabStore(() => 1);
		Assert.True(Guid.TryParse(store.Add("https://a.test").Id, out _));
	}

	[Fact]
	public void The_snapshot_carries_live_page_state_and_the_strip_settings()
	{
		var store = new TabStore(() => 1);
		var tab = store.Add("https://example.com/");
		store.Activate(tab.Id);
		var state = store.Snapshot("by-site", true, _ => new PageState(Loading: true, CanGoBack: true, CanGoForward: false));
		var view = Assert.Single(state.Tabs);
		Assert.True(view.Loading);
		Assert.True(view.CanGoBack);
		Assert.False(view.CanGoForward);
		Assert.Equal(tab.Id, state.ActiveId);
		Assert.Equal("by-site", state.Organization);
		Assert.True(state.Dropdowns);
	}

	[Fact]
	public void A_tab_without_a_page_has_idle_page_state()
	{
		var store = new TabStore(() => 1);
		store.Add("https://example.com/");
		var view = Assert.Single(store.Snapshot("manual", false, _ => null).Tabs);
		Assert.False(view.Loading);
		Assert.False(view.CanGoBack);
	}

	[Fact]
	public void The_snapshot_serialises_like_the_ts_tabs_state()
	{
		var store = new TabStore(() => 5, () => "t1");
		store.Add("https://example.com/");
		var json = JsonSerializer.Serialize(store.Snapshot("manual", false, _ => new PageState(false, false, false)), ZingJson.Options);
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		Assert.False(root.TryGetProperty("activeId", out _));
		Assert.False(root.TryGetProperty("openStack", out _));
		Assert.Equal("manual", root.GetProperty("organization").GetString());
		var tab = root.GetProperty("tabs")[0];
		Assert.Equal(
			["canGoBack", "canGoForward", "createdAt", "id", "lastActiveAt", "loading", "pinned", "title", "updatedAt", "url"],
			tab.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void The_closed_stack_keeps_only_its_capacity_newest_first()
	{
		var stack = new ClosedStack<int>(2);
		stack.Push(1);
		stack.Push(2);
		stack.Push(3);
		Assert.True(stack.TryPop(out var top));
		Assert.Equal(3, top);
		Assert.True(stack.TryPop(out top));
		Assert.Equal(2, top);
		Assert.False(stack.TryPop(out _));
	}

	[Theory]
	[InlineData(0, 3, 1)]
	[InlineData(2, 3, 0)]
	[InlineData(-1, 3, 0)]
	public void Next_tab_wraps(int after, int count, int expected) => Assert.Equal(expected, TabNavigation.NextTab(after, count));

	[Fact]
	public void Tab_navigation_handles_the_ends_and_empty_lists()
	{
		Assert.Null(TabNavigation.NextTab(0, 0));
		Assert.Equal(2, TabNavigation.PreviousTab(0, 3));
		Assert.Null(TabNavigation.PreviousTab(0, 0));
		Assert.Equal(1, TabNavigation.TabForShortcut(2, 3));
		Assert.Equal(2, TabNavigation.TabForShortcut(9, 3));
		Assert.Null(TabNavigation.TabForShortcut(5, 3));
		Assert.Null(TabNavigation.TabForShortcut(0, 3));
		Assert.Null(TabNavigation.TabForShortcut(10, 3));
	}

	private sealed class VectorRun(JsonElement vector)
	{
		private long _clock = 1000;
		private int _created;
		private readonly Dictionary<string, string> _real = [];
		private readonly Dictionary<string, string> _names = [];
		private TabStore? _store;

		public void Execute()
		{
			_store = new TabStore(() => _clock++, Create);
			var name = vector.GetProperty("name").GetString()!;
			foreach (var op in vector.GetProperty("ops").EnumerateArray()) Run(op, name);
			Check(vector.GetProperty("expect"));
		}

		private string Create()
		{
			var real = $"real-{++_created}";
			_real[$"t{_created}"] = real;
			_names[real] = $"t{_created}";
			return real;
		}

		private string Real(string id) => _real.GetValueOrDefault(id, id);

		private string? Name(string? id) => id is null ? null : _names.GetValueOrDefault(id, id);

		private void Run(JsonElement op, string caseName)
		{
			var kind = op.GetProperty("op").GetString()!;
			if (op.TryGetProperty("throws", out _))
			{
				Assert.Throws<UnknownTabException>(() => Perform(op, kind));
				return;
			}
			var result = Perform(op, kind);
			if (!op.TryGetProperty("returns", out var returns)) return;
			var expected = returns.ValueKind == JsonValueKind.Null ? null : returns.GetString();
			Assert.True(expected == result, $"{caseName}: {kind} returned {result ?? "null"}, expected {expected ?? "null"}");
		}

		private string? Perform(JsonElement op, string kind)
		{
			var store = _store!;
			string Str(string key) => op.GetProperty(key).GetString()!;
			switch (kind)
			{
				case "add": return Name(store.Add(Str("url")).Id);
				case "addAfter": return Name(store.AddAfter(Real(Str("anchor")), Str("url")).Id);
				case "addRestored": return Name(store.AddRestored(Str("url"), Str("title"), op.GetProperty("pinned").GetBoolean()).Id);
				case "activate": return Name(store.Activate(Real(Str("id"))).Id);
				case "touch": store.Touch(Real(Str("id"))); return null;
				case "update":
					var patch = op.GetProperty("patch");
					return Name(store.Update(Real(Str("id")), new TabContentPatch(Optional(patch, "url"), Optional(patch, "title"), Optional(patch, "faviconUrl"))).Id);
				case "close": return Name(store.Close(Real(Str("id"))).NextActiveId);
				case "reopenClosed":
					var record = store.ReopenClosed();
					if (record is null) return null;
					if (op.TryGetProperty("returnsUrl", out var url)) Assert.Equal(url.GetString(), record.Url);
					return Name(record.Id);
				case "move": store.Move(Real(Str("id")), op.GetProperty("index").GetInt32()); return null;
				case "pin": store.Pin(Real(Str("id"))); return null;
				case "unpin": store.Unpin(Real(Str("id"))); return null;
				case "nextId": return Name(store.NextId());
				case "previousId": return Name(store.PreviousId());
				case "idForShortcut": return Name(store.IdForShortcut(op.GetProperty("n").GetInt32()));
				default: throw new InvalidOperationException($"Unknown op {kind}");
			}
		}

		private static string? Optional(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) ? v.GetString() : null;

		private void Check(JsonElement expect)
		{
			var store = _store!;
			if (expect.TryGetProperty("urls", out var urls))
				Assert.Equal(urls.EnumerateArray().Select(u => u.GetString()), store.Records.Select(t => t.Url));
			if (expect.TryGetProperty("pinned", out var pinned))
				Assert.Equal(pinned.EnumerateArray().Select(u => u.GetString()), store.Records.Where(t => t.Pinned).Select(t => t.Url));
			if (expect.TryGetProperty("activeId", out var active))
				Assert.Equal(active.ValueKind == JsonValueKind.Null ? null : active.GetString(), Name(store.ActiveId));
			if (expect.TryGetProperty("canReopen", out var canReopen))
				Assert.Equal(canReopen.GetBoolean(), store.CanReopen);
			if (!expect.TryGetProperty("records", out var records)) return;
			foreach (var entry in records.EnumerateObject())
			{
				var record = store.Get(Real(entry.Name));
				foreach (var field in entry.Value.EnumerateObject()) AssertField(record, field);
			}
		}

		private static void AssertField(TabRecord record, JsonProperty field)
		{
			switch (field.Name)
			{
				case "url": Assert.Equal(field.Value.GetString(), record.Url); break;
				case "title": Assert.Equal(field.Value.GetString(), record.Title); break;
				case "pinned": Assert.Equal(field.Value.GetBoolean(), record.Pinned); break;
				case "createdAt": Assert.Equal(field.Value.GetInt64(), record.CreatedAt); break;
				case "updatedAt": Assert.Equal(field.Value.GetInt64(), record.UpdatedAt); break;
				case "lastActiveAt": Assert.Equal(field.Value.GetInt64(), record.LastActiveAt); break;
				default: throw new InvalidOperationException($"Unknown record field {field.Name}");
			}
		}
	}
}
