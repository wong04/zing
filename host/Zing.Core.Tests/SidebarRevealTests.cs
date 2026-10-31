using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class SidebarRevealTests
{
	private const string VectorsPath = "src/main/sidebar.vectors.json";

	public static TheoryData<string> CaseNames()
	{
		var data = new TheoryData<string>();
		foreach (var c in Vectors.Load(VectorsPath).GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(CaseNames))]
	public void Shared_edge_reveal_vector(string name)
	{
		var c = Vectors.Load(VectorsPath).GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
		var changes = new List<bool>();
		var reveal = new EdgeReveal(changes.Add);
		reveal.SetEnabled(true);
		changes.Clear();
		foreach (var step in c.GetProperty("steps").EnumerateArray())
		{
			switch (step.GetProperty("op").GetString())
			{
				case "update":
					reveal.Update(new CursorSample(
						step.GetProperty("x").GetDouble(),
						step.TryGetProperty("y", out var y) ? y.GetDouble() : 300,
						!step.TryGetProperty("inWindow", out var inWindow) || inWindow.GetBoolean(),
						step.GetProperty("now").GetDouble()));
					break;
				case "setEnabled":
					reveal.SetEnabled(step.GetProperty("value").GetBoolean());
					break;
				case "peek":
					reveal.Peek(step.GetProperty("now").GetDouble());
					break;
				case var op:
					throw new InvalidOperationException($"Unknown op {op}");
			}
			if (step.TryGetProperty("revealed", out var revealed)) Assert.Equal(revealed.GetBoolean(), reveal.Revealed);
			if (step.TryGetProperty("changes", out var want)) Assert.Equal(want.EnumerateArray().Select(e => e.GetBoolean()), changes);
		}
	}

	private sealed class Cursor
	{
		public Point? At { get; set; }
	}

	private static (SidebarReveal Reveal, ManualScheduler Clock, Cursor Cursor, List<bool> Changes) Setup()
	{
		var clock = new ManualScheduler();
		var cursor = new Cursor();
		var reveal = new SidebarReveal(() => cursor.At, clock);
		var changes = new List<bool>();
		reveal.Changed += changes.Add;
		return (reveal, clock, cursor, changes);
	}

	private static void Ticks(ManualScheduler clock, int count)
	{
		for (var i = 0; i < count; i++) clock.Advance(SidebarReveal.PollInterval);
	}

	[Fact]
	public void The_cursor_dwelling_at_the_edge_reveals_it_and_leaving_hides_it()
	{
		var (reveal, clock, cursor, changes) = Setup();
		reveal.SetEnabled(true);
		cursor.At = new Point(2, 300);
		Ticks(clock, 1);
		Assert.False(reveal.Revealed);
		Ticks(clock, 2);
		Assert.True(reveal.Revealed);
		cursor.At = new Point(150, 300);
		Ticks(clock, 2);
		Assert.True(reveal.Revealed);
		cursor.At = new Point(400, 300);
		Ticks(clock, 1);
		Assert.False(reveal.Revealed);
		Assert.Equal([true, false], changes);
	}

	[Fact]
	public void Losing_focus_puts_a_revealed_sidebar_away()
	{
		var (reveal, clock, cursor, _) = Setup();
		reveal.SetEnabled(true);
		cursor.At = new Point(2, 300);
		Ticks(clock, 3);
		Assert.True(reveal.Revealed);
		cursor.At = null;
		Ticks(clock, 1);
		Assert.False(reveal.Revealed);
	}

	[Fact]
	public void Nothing_is_revealed_without_a_cursor_in_the_window()
	{
		var (reveal, clock, _, changes) = Setup();
		reveal.SetEnabled(true);
		Ticks(clock, 10);
		Assert.Empty(changes);
	}

	[Fact]
	public void Peek_shows_it_for_a_moment_even_with_the_cursor_elsewhere()
	{
		var (reveal, clock, cursor, _) = Setup();
		reveal.SetEnabled(true);
		cursor.At = new Point(900, 300);
		reveal.Peek();
		Assert.True(reveal.Revealed);
		Ticks(clock, 20);
		Assert.True(reveal.Revealed);
		Ticks(clock, 20);
		Assert.False(reveal.Revealed);
	}

	[Fact]
	public void Disabling_stops_polling_and_hides_at_once()
	{
		var (reveal, clock, cursor, _) = Setup();
		reveal.SetEnabled(true);
		cursor.At = new Point(2, 300);
		Ticks(clock, 3);
		Assert.True(reveal.Revealed);
		reveal.SetEnabled(false);
		Assert.False(reveal.Revealed);
		Assert.Equal(0, clock.PendingCount);
	}

	[Fact]
	public void Disposing_stops_polling()
	{
		var (reveal, clock, _, _) = Setup();
		reveal.SetEnabled(true);
		Assert.Equal(1, clock.PendingCount);
		reveal.Dispose();
		Assert.Equal(0, clock.PendingCount);
	}
}
