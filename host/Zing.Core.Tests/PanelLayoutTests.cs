using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class PanelLayoutTests
{
	private const string LayoutPath = "src/shared/shell/panelLayout.vectors.json";
	private const string RecentsPath = "src/shared/shell/recentList.vectors.json";

	public static TheoryData<string> LayoutNames() => Names(LayoutPath);

	public static TheoryData<string> RecentNames() => Names(RecentsPath);

	private static TheoryData<string> Names(string path)
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(path).GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	private static JsonElement Case(string path, string name) =>
		Vectors.Load(path).GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

	public static Rect ToRect(JsonElement t) => new(t[0].GetDouble(), t[1].GetDouble(), t[2].GetDouble(), t[3].GetDouble());

	private static double Field(Rect r, string field) => field switch
	{
		"x" => r.X,
		"y" => r.Y,
		"width" => r.Width,
		"height" => r.Height,
		_ => throw new InvalidOperationException($"Unknown field {field}"),
	};

	[Theory]
	[MemberData(nameof(LayoutNames))]
	public void Shared_placement_vector(string name)
	{
		var c = Case(LayoutPath, name);
		var bounds = ToRect(c.GetProperty("bounds"));
		var inner = PanelLayout.InsetBy(bounds, PanelLayout.Inset, PanelLayout.Inset);
		var frames = c.GetProperty("frames").EnumerateArray().Select(ToRect).ToList();
		var fixedIndex = c.GetProperty("fixed").GetInt32();
		var result = c.GetProperty("op").GetString() switch
		{
			"resolve" => PanelLayout.Resolve(fixedIndex, frames, bounds),
			"resolveAfterDrop" => PanelLayout.ResolveAfterDrop(fixedIndex, frames, bounds),
			var op => throw new InvalidOperationException($"Unknown op {op}"),
		};
		var want = c.GetProperty("want");

		if (want.TryGetProperty("unchanged", out var unchanged))
			foreach (var i in unchanged.EnumerateArray().Select(e => e.GetInt32())) Assert.Equal(frames[i], result[i]);
		if (want.TryGetProperty("moved", out var moved))
			foreach (var i in moved.EnumerateArray().Select(e => e.GetInt32())) Assert.NotEqual(frames[i], result[i]);
		if (want.TryGetProperty("noOverlaps", out _))
			for (var i = 0; i < result.Length; i++)
				for (var j = i + 1; j < result.Length; j++) Assert.False(PanelLayout.Intersects(result[i], result[j]), $"{i} overlaps {j}");
		if (want.TryGetProperty("inside", out var inside))
			foreach (var i in inside.EnumerateArray().Select(e => e.GetInt32())) Assert.True(PanelLayout.Contains(inner, result[i]), $"{i} is outside");
		if (want.TryGetProperty("insideOrOriginal", out _))
			Assert.All(result, f => Assert.True(PanelLayout.Contains(inner, f) || frames.Contains(f)));
		if (want.TryGetProperty("disjoint", out var disjoint))
			foreach (var pair in disjoint.EnumerateArray()) Assert.False(PanelLayout.Intersects(result[pair[0].GetInt32()], result[pair[1].GetInt32()]));
		if (want.TryGetProperty("fieldEquals", out var equals))
			foreach (var e in equals.EnumerateArray())
				Assert.Equal(e.GetProperty("value").GetDouble(), Field(result[e.GetProperty("index").GetInt32()], e.GetProperty("field").GetString()!));
		if (want.TryGetProperty("fieldGreater", out var greater))
			foreach (var e in greater.EnumerateArray())
				Assert.True(Field(result[e.GetProperty("index").GetInt32()], e.GetProperty("field").GetString()!) > e.GetProperty("than").GetDouble());
	}

	[Fact]
	public void An_index_with_no_panel_is_a_programming_error()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => PanelLayout.Resolve(5, [new Rect(0, 0, 400, 300)], new Rect(0, 0, 1200, 800)));
	}

	[Fact]
	public void Three_panels_resolve_quickly_enough_for_every_drag_frame()
	{
		Rect[] frames = [new(300, 200, 400, 300), new(320, 220, 400, 300), new(340, 240, 400, 300)];
		var window = new Rect(0, 0, 1200, 800);
		PanelLayout.Resolve(2, frames, window);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		for (var i = 0; i < 100; i++) PanelLayout.Resolve(2, frames, window);
		Assert.True(watch.Elapsed.TotalMilliseconds / 100 < 4, $"took {watch.Elapsed.TotalMilliseconds / 100} ms per call");
	}

	[Theory]
	[MemberData(nameof(RecentNames))]
	public void Shared_recents_vector(string name)
	{
		var list = new List<string>();
		foreach (var step in Case(RecentsPath, name).GetProperty("steps").EnumerateArray())
		{
			var id = step.GetProperty("id").GetString()!;
			list = step.GetProperty("op").GetString() switch
			{
				"touch" => RecentList.Touching(id, list),
				"remove" => RecentList.Removing(id, list),
				var op => throw new InvalidOperationException($"Unknown op {op}"),
			};
			Assert.Equal(step.GetProperty("want").EnumerateArray().Select(e => e.GetString()!), list);
		}
	}

	[Fact]
	public void The_shell_shapes_serialise_like_the_typescript()
	{
		var state = new ShellState(false, "general", true, false, new Point(0, 0), "1.0.0");
		Assert.Equal(
			"""{"settingsOpen":false,"settingsCategory":"general","sidebarRevealed":true,"gated":false,"origin":{"x":0,"y":0},"appVersion":"1.0.0"}""",
			JsonSerializer.Serialize(state, ZingJson.Options));
		Assert.Equal("""{"panels":[],"dragging":false}""", JsonSerializer.Serialize(ToolPanelsState.Empty, ZingJson.Options));
		var panels = new ToolPanelsState([new ToolPanelView("panel-1", "tool:Hashes", "Hashes", new Rect(1, 2, 3, 4), false)], "panel-1", true);
		Assert.Equal(
			"""{"panels":[{"id":"panel-1","content":"tool:Hashes","title":"Hashes","frame":{"x":1,"y":2,"width":3,"height":4},"minimized":false}],"frontId":"panel-1","dragging":true}""",
			JsonSerializer.Serialize(panels, ZingJson.Options));
		Assert.Equal("""{"status":"full"}""", JsonSerializer.Serialize(OpenPanelOutcome.Full, ZingJson.Options));
	}
}
