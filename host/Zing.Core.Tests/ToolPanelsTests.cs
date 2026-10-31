using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class ToolPanelsTests
{
	private const string VectorsPath = "host/Zing.Core.Tests/vectors/panels.vectors.json";

	public static TheoryData<string> ScenarioNames() => Names("scenarios");

	public static TheoryData<string> ClampNames() => Names("clamp");

	public static TheoryData<string> SurfaceNames() => Names("surface");

	private static TheoryData<string> Names(string section)
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	private static JsonElement Case(string section, string name) =>
		Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

	private static Point ToPoint(JsonElement t) => new(t[0].GetDouble(), t[1].GetDouble());

	[Theory]
	[MemberData(nameof(ScenarioNames))]
	public void Shared_panel_scenario(string name)
	{
		var s = Case("scenarios", name);
		var store = new ToolPanels();
		var area = PanelLayoutTests.ToRect(s.GetProperty("area"));
		var ids = new Dictionary<string, string>();

		string IdOf(JsonElement step) => step.TryGetProperty("id", out var id) ? id.GetString()! : ids[step.GetProperty("panel").GetString()!];
		Rect FrameOf(string panel) => store.All.Single(p => p.Id == ids[panel]).Frame;

		foreach (var step in s.GetProperty("steps").EnumerateArray())
		{
			Func<OpenPanelOutcome?> run = () =>
			{
				switch (step.GetProperty("op").GetString())
				{
					case "open":
						var content = PanelContent.FromId(step.GetProperty("content").GetString()!) ?? throw new InvalidOperationException("bad content");
						return store.Open(content, ToPoint(step.GetProperty("at")), area);
					case "minimize":
						store.Minimize(IdOf(step));
						return null;
					case "close":
						store.Close(IdOf(step));
						return null;
					case "restore":
						return store.Restore(IdOf(step), area);
					case "focus":
						store.BringToFront(IdOf(step));
						return null;
					case "beginDrag":
						store.BeginDrag(IdOf(step));
						return null;
					case "endDrag":
						var origin = step.TryGetProperty("originFromFrame", out var from)
							? new Point(FrameOf(from.GetProperty("panel").GetString()!).X + from.GetProperty("dx").GetDouble(), FrameOf(from.GetProperty("panel").GetString()!).Y + from.GetProperty("dy").GetDouble())
							: ToPoint(step.GetProperty("origin"));
						store.EndDrag(IdOf(step), origin, area);
						return null;
					case var op:
						throw new InvalidOperationException($"Unknown op {op}");
				}
			};

			if (step.TryGetProperty("throws", out var throws))
			{
				if (throws.GetString() == "notTearable") Assert.Throws<PanelNotTearableException>(() => run());
				else Assert.Throws<UnknownPanelException>(() => run());
				continue;
			}
			var outcome = run();
			if (step.TryGetProperty("want", out var want)) Assert.Equal(want.GetString(), outcome?.Status);
			if (step.TryGetProperty("as", out var asName)) ids[asName.GetString()!] = store.All[^1].Id;
		}

		var c = s.GetProperty("checks");
		if (c.TryGetProperty("all", out var all)) Assert.Equal(all.GetInt32(), store.All.Count);
		if (c.TryGetProperty("visible", out var visible)) Assert.Equal(visible.GetInt32(), store.Visible.Count);
		if (c.TryGetProperty("isFull", out var isFull)) Assert.Equal(isFull.GetBoolean(), store.IsFull);
		if (c.TryGetProperty("dragging", out var dragging)) Assert.Equal(dragging.GetBoolean(), store.Dragging);
		if (c.TryGetProperty("front", out var front)) Assert.Equal(ids[front.GetString()!], store.State().FrontId);
		if (c.TryGetProperty("minimized", out var minimized))
			foreach (var m in minimized.EnumerateObject())
				Assert.Equal(m.Value.GetBoolean(), store.All.Single(p => p.Id == ids[m.Name]).Minimized);
		if (c.TryGetProperty("frame", out var frame))
		{
			var f = FrameOf(frame.GetProperty("panel").GetString()!);
			Assert.Equal(frame.GetProperty("x").GetDouble(), f.X);
			Assert.Equal(frame.GetProperty("y").GetDouble(), f.Y);
			if (frame.TryGetProperty("width", out var w)) Assert.Equal(w.GetDouble(), f.Width);
			if (frame.TryGetProperty("height", out var h)) Assert.Equal(h.GetDouble(), f.Height);
		}
		if (c.TryGetProperty("wholePixels", out var whole))
		{
			var f = FrameOf(whole.GetString()!);
			foreach (var n in new[] { f.X, f.Y, f.Width, f.Height }) Assert.Equal(Math.Floor(n), n);
		}
		if (c.TryGetProperty("disjointVisible", out _))
		{
			var shown = store.Visible;
			for (var i = 0; i < shown.Count; i++)
				for (var j = i + 1; j < shown.Count; j++) Assert.False(PanelLayout.Intersects(shown[i].Frame, shown[j].Frame));
		}
	}

	[Theory]
	[MemberData(nameof(ClampNames))]
	public void Shared_clamp_vector(string name)
	{
		var c = Case("clamp", name);
		var size = new PanelSize(c.GetProperty("size")[0].GetDouble(), c.GetProperty("size")[1].GetDouble());
		var got = ToolPanels.ClampOrigin(ToPoint(c.GetProperty("point")), size, PanelLayoutTests.ToRect(c.GetProperty("area")));
		Assert.Equal(ToPoint(c.GetProperty("want")), got);
	}

	[Theory]
	[MemberData(nameof(SurfaceNames))]
	public void Shared_surface_region_vector(string name)
	{
		var c = Case("surface", name);
		var i = c.GetProperty("input");
		var input = new SurfaceInput(
			new ContentSize(i.GetProperty("content")[0].GetDouble(), i.GetProperty("content")[1].GetDouble()),
			PanelLayoutTests.ToRect(i.GetProperty("page")),
			i.GetProperty("gated").GetBoolean(),
			i.GetProperty("settingsOpen").GetBoolean(),
			i.GetProperty("sidebarRevealed").GetBoolean(),
			i.GetProperty("panelFrames").EnumerateArray().Select(PanelLayoutTests.ToRect).ToList(),
			i.GetProperty("dragging").GetBoolean());
		var want = c.GetProperty("want");
		Assert.Equal(want.ValueKind == JsonValueKind.Null ? null : PanelLayoutTests.ToRect(want), SurfaceRegion.Compute(input));
	}

	[Fact]
	public void Panel_ids_round_trip_and_unknown_ids_name_nothing()
	{
		foreach (var tool in PanelTool.All) Assert.Equal(PanelContent.Tool(tool), PanelContent.FromId(PanelContent.Tool(tool).Id));
		foreach (var section in PanelSection.All) Assert.Equal(PanelContent.Section(section), PanelContent.FromId(PanelContent.Section(section).Id));
		Assert.Null(PanelContent.FromId("tool:Nope"));
		Assert.Null(PanelContent.FromId("Hashes"));
		Assert.Equal("tool:Hashes", PanelContent.Tool(PanelTool.Hash).Id);
	}

	[Fact]
	public void Browser_toolbox_and_settings_cannot_be_torn_off()
	{
		Assert.False(PanelContent.Section(PanelSection.Browser).IsTearable);
		Assert.False(PanelContent.Section(PanelSection.Utilities).IsTearable);
		Assert.False(PanelContent.Section(PanelSection.Settings).IsTearable);
		Assert.True(PanelContent.Section(PanelSection.Viewer).IsTearable);
		Assert.True(PanelContent.Tool(PanelTool.Hash).IsTearable);
	}

	[Fact]
	public void Panels_are_sized_to_what_they_hold()
	{
		Assert.Equal(new PanelSize(640, 560), PanelContent.Tool(PanelTool.Graphing).PreferredSize);
		Assert.Equal(new PanelSize(500, 620), PanelContent.Tool(PanelTool.Qr).PreferredSize);
		Assert.Equal(new PanelSize(520, 540), PanelContent.Tool(PanelTool.Calculator).PreferredSize);
		Assert.Equal(new PanelSize(460, 500), PanelContent.Tool(PanelTool.Uuid).PreferredSize);
		Assert.Equal(new PanelSize(540, 620), PanelContent.Section(PanelSection.Security).PreferredSize);
		Assert.Equal(new PanelSize(480, 540), PanelContent.Section(PanelSection.Download).PreferredSize);
	}

	[Fact]
	public void Rounding_follows_javascript_so_half_pixels_go_up()
	{
		var store = new ToolPanels();
		var area = new Rect(0, 0, 3000, 2000);
		// 1000.5 - 260 = 740.5 rounds to 741 (Math.round), not 740 (banker's rounding).
		store.Open(PanelContent.Tool(PanelTool.Hash), new Point(1000.5, 500), area);
		Assert.Equal(741, store.All[0].Frame.X);
	}
}
