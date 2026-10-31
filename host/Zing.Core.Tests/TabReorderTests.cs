using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class TabReorderTests
{
	private const string VectorsPath = "src/shared/browser/tabReorder.vectors.json";

	public static TheoryData<string> GeometryNames() => Names("geometry");

	public static TheoryData<string> LayoutNames() => Names("layouts");

	private static TheoryData<string> Names(string section)
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	private static JsonElement Case(string section, string name) =>
		Vectors.Load(VectorsPath).GetProperty(section).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

	[Theory]
	[MemberData(nameof(GeometryNames))]
	public void Shared_geometry_vector(string name)
	{
		var c = Case("geometry", name);
		var x = 8.0;
		var frames = new List<Rect>();
		foreach (var width in c.GetProperty("widths").EnumerateArray().Select(w => w.GetDouble()))
		{
			frames.Add(c.TryGetProperty("transpose", out var t) && t.GetBoolean() ? new Rect(0, x, 28, width) : new Rect(x, 0, width, 28));
			x += width + 4;
		}
		var axis = c.TryGetProperty("axis", out var a) && a.GetString() == "y" ? Axis.Y : Axis.X;
		var want = c.GetProperty("want").GetDouble();
		var got = c.GetProperty("op").GetString() switch
		{
			"targetIndex" => TabReorder.TargetIndex(c.GetProperty("position").GetDouble(), frames, axis),
			"targetIndexDragging" => TabReorder.TargetIndexDragging(c.GetProperty("index").GetInt32(), c.GetProperty("center").GetDouble(), frames, axis),
			"centerAfterMove" => TabReorder.CenterAfterMove(c.GetProperty("from").GetInt32(), c.GetProperty("to").GetInt32(), frames, axis),
			var op => throw new InvalidOperationException($"Unknown op {op}"),
		};
		Assert.Equal(want, got, 9);
	}

	[Theory]
	[MemberData(nameof(LayoutNames))]
	public void Shared_layout_vector(string name)
	{
		var c = Case("layouts", name);
		var input = c.GetProperty("input");
		var active = input.GetProperty("activeLoose");
		var got = TabReorder.TabBarLayout(
			input.GetProperty("total").GetDouble(),
			input.GetProperty("pinned").GetInt32(),
			input.GetProperty("loose").GetInt32(),
			active.ValueKind == JsonValueKind.Null ? null : active.GetInt32());
		var want = c.GetProperty("want");
		Assert.Equal(want.GetProperty("compact").EnumerateArray().Select(v => v.GetBoolean()), got.Compact);
		var widths = want.GetProperty("looseWidths").EnumerateArray().Select(v => v.GetDouble()).ToList();
		Assert.Equal(widths.Count, got.LooseWidths.Count);
		for (var i = 0; i < widths.Count; i++) Assert.Equal(widths[i], got.LooseWidths[i], 9);
	}
}
