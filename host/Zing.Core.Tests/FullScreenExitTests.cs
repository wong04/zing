using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class FullScreenExitTests
{
	private sealed class Cursor
	{
		public Point? At { get; set; }
	}

	private static (FullScreenExitReveal Reveal, ManualScheduler Clock, Cursor Cursor, List<bool> Changes) Setup()
	{
		var clock = new ManualScheduler();
		var cursor = new Cursor();
		var reveal = new FullScreenExitReveal(() => cursor.At, clock);
		var changes = new List<bool>();
		reveal.Changed += changes.Add;
		reveal.SetEnabled(true);
		return (reveal, clock, cursor, changes);
	}

	private static void Poll(ManualScheduler clock) => clock.Advance(SidebarReveal.PollInterval);

	[Fact]
	public void Shows_once_the_cursor_reaches_the_top_edge()
	{
		var (reveal, clock, cursor, changes) = Setup();
		cursor.At = new Point(600, 300);
		Poll(clock);
		Assert.False(reveal.Shown);
		cursor.At = new Point(600, 0);
		Poll(clock);
		Assert.True(reveal.Shown);
		Assert.Equal([true], changes);
	}

	[Fact]
	public void Stays_while_the_cursor_moves_onto_the_button_and_goes_once_it_moves_down_past_it()
	{
		var (reveal, clock, cursor, changes) = Setup();
		cursor.At = new Point(600, 0);
		Poll(clock);
		cursor.At = new Point(600, 40);
		Poll(clock);
		Assert.True(reveal.Shown);
		cursor.At = new Point(600, FullScreenExitReveal.HideBelow + 1);
		Poll(clock);
		Assert.False(reveal.Shown);
		Assert.Equal([true, false], changes);
	}

	[Fact]
	public void Near_the_top_but_not_at_the_edge_shows_nothing()
	{
		var (reveal, clock, cursor, _) = Setup();
		cursor.At = new Point(600, 40);
		Poll(clock);
		Assert.False(reveal.Shown);
	}

	[Fact]
	public void Goes_when_the_cursor_leaves_the_window()
	{
		var (reveal, clock, cursor, _) = Setup();
		cursor.At = new Point(600, 0);
		Poll(clock);
		cursor.At = null;
		Poll(clock);
		Assert.False(reveal.Shown);
	}

	[Fact]
	public void Leaving_fullscreen_takes_it_down_and_stops_polling()
	{
		var (reveal, clock, cursor, changes) = Setup();
		cursor.At = new Point(600, 0);
		Poll(clock);
		reveal.SetEnabled(false);
		Assert.False(reveal.Shown);
		Assert.Equal([true, false], changes);
		Assert.Equal(0, clock.PendingCount);
		Poll(clock);
		Assert.False(reveal.Shown);
	}

	[Fact]
	public void Nothing_shows_before_it_is_enabled()
	{
		var clock = new ManualScheduler();
		var reveal = new FullScreenExitReveal(() => new Point(600, 0), clock);
		Poll(clock);
		Assert.False(reveal.Shown);
		Assert.Equal(0, clock.PendingCount);
	}

	[Fact]
	public async Task The_button_asks_the_window_to_leave_fullscreen()
	{
		var bridge = new Bridge();
		var host = new FakeWindowHost();
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), host, "test");
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "page:exit-fullscreen", args = Array.Empty<object>() });
		var result = JsonDocument.Parse(await bridge.HandleAsync(json)).RootElement;
		Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
		Assert.Equal(1, host.FullScreenExits);
	}
}
