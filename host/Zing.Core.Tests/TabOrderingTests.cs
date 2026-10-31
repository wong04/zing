using Zing.Core;

namespace Zing.Core.Tests;

public sealed class TabOrderingTests
{
	private static readonly string[] Mixed = ["https://a.test/1", "https://b.test/", "https://a.test/2", "https://c.test/"];

	private readonly TabStore _store = new(Deterministic.Clock(), Deterministic.Ids());
	private readonly TabService _tabs;
	private readonly SettingsStore _settings = new();
	private readonly ManualScheduler _scheduler = new();
	private readonly List<string> _notices = [];
	private readonly TabOrdering _ordering;
	private readonly List<string> _ids = [];

	public TabOrderingTests() : this(Mixed)
	{
	}

	private TabOrderingTests(string[] urls)
	{
		_tabs = new TabService(_store, new FakePageHost(), "about:blank", () => { }, (_, _) => { });
		foreach (var url in urls) _ids.Add(_tabs.Create(url).Id);
		_tabs.Activate(_ids[0]);
		_ordering = new TabOrdering(_tabs, _settings, _notices.Add, _scheduler);
	}

	private List<string> Order() => _tabs.Records.Select(t => t.Id).ToList();

	private string[] Ids(params int[] indexes) => indexes.Select(i => _ids[i]).ToArray();

	private string Organization() => _settings.Get("tabOrganization")!.GetValue<string>();

	[Fact]
	public void Reorganize_leaves_a_manual_strip_alone()
	{
		_ordering.Reorganize();
		Assert.Equal(_ids, Order());
	}

	[Fact]
	public void Groups_by_site_and_keeps_pinned_tabs_in_front()
	{
		_tabs.Pin(_ids[3]);
		_ordering.SetOrganization(TabOrganization.Domain);
		Assert.Equal(Ids(3, 0, 2, 1), Order());
	}

	[Fact]
	public void Sorts_A_to_Z_by_title()
	{
		var other = new TabOrderingTests(["https://x.test/", "https://y.test/"]);
		other._store.Update(other._ids[0], new TabContentPatch(Title: "Zebra"));
		other._store.Update(other._ids[1], new TabContentPatch(Title: "Apple"));
		other._ordering.SetOrganization(TabOrganization.Alphabetical);
		Assert.Equal(other.Ids(1, 0), other.Order());
	}

	[Fact]
	public void Puts_the_newest_tab_first()
	{
		_ordering.SetOrganization(TabOrganization.Newest);
		Assert.Equal(Enumerable.Reverse(_ids), Order());
	}

	[Fact]
	public void Moves_a_loose_tab_but_not_into_the_pinned_run()
	{
		_tabs.Pin(_ids[0]);
		_ordering.Move(_ids[3], 0);
		Assert.Equal(Ids(0, 3, 1, 2), Order());
	}

	[Fact]
	public void A_drag_switches_a_sorted_arrangement_to_manual_says_so_and_keeps_the_dragged_order()
	{
		_ordering.SetOrganization(TabOrganization.Newest);
		var before = Order();
		_ordering.Move(before[0], 2);
		Assert.Equal("manual", Organization());
		Assert.Equal([TabOrdering.ManualNotice], _notices);
		Assert.Equal([before[1], before[2], before[0], before[3]], Order());
	}

	[Fact]
	public void Moves_a_stack_with_all_its_pages()
	{
		_ordering.SetDropdowns(true);
		_ordering.SetOrganization(TabOrganization.Domain);
		Assert.Equal(Ids(0, 2, 1, 3), Order());
		_ordering.MoveStack("a.test", 2);
		Assert.Equal(Ids(1, 3, 0, 2), Order());
	}

	[Fact]
	public void Freezes_the_shown_order_before_a_stack_drag_under_A_to_Z()
	{
		string[] titles = ["m", "b", "a", "z"];
		for (var i = 0; i < 4; i++) _store.Update(_ids[i], new TabContentPatch(Title: titles[i]));
		_ordering.SetDropdowns(true);
		_ordering.SetOrganization(TabOrganization.Alphabetical);
		_ordering.MoveStack("c.test", 0);
		Assert.Equal("manual", Organization());
		Assert.Equal(_ids[3], Order()[0]);
	}

	[Fact]
	public void Reorders_pages_inside_a_stack_without_moving_the_rest_of_the_strip()
	{
		_ordering.MoveWithin(_ids[2], "a.test", 0);
		Assert.Equal(Ids(2, 1, 0, 3), Order());
	}

	[Fact]
	public void Refuses_an_unknown_stack()
	{
		var error = Assert.Throws<UnknownStackException>(() => _ordering.MoveStack("nope.test", 0));
		Assert.Equal("nope.test", error.Key);
		Assert.Throws<UnknownStackException>(() => _ordering.MoveWithin(_ids[0], "nope.test", 0));
	}

	[Fact]
	public void The_stacks_follow_the_arrangement_and_leave_pinned_tabs_out()
	{
		_tabs.Pin(_ids[3]);
		var stacks = _ordering.Stacks();
		Assert.Equal(["a.test", "b.test"], stacks.Select(s => s.Key));
		Assert.Equal(Ids(0, 2), stacks[0].Ids);
	}

	[Fact]
	public void Reads_the_organization_and_dropdowns_from_the_settings()
	{
		Assert.Equal(TabOrganization.Manual, _ordering.Organization);
		Assert.False(_ordering.Dropdowns);
		_settings.Set("tabOrganization", System.Text.Json.Nodes.JsonValue.Create("newest"));
		_ordering.SetDropdowns(true);
		Assert.Equal(TabOrganization.Newest, _ordering.Organization);
		Assert.True(_ordering.Dropdowns);
	}

	[Fact]
	public void A_scheduled_reorganize_waits_for_the_page_to_settle_and_folds_repeats_into_one_move()
	{
		_settings.Set("tabOrganization", System.Text.Json.Nodes.JsonValue.Create("newest"));
		_ordering.ScheduleReorganize();
		_scheduler.Advance(TimeSpan.FromMilliseconds(200));
		Assert.Equal(_ids, Order());
		_ordering.ScheduleReorganize();
		_scheduler.Advance(TimeSpan.FromMilliseconds(200));
		Assert.Equal(_ids, Order());
		_scheduler.Advance(TimeSpan.FromMilliseconds(100));
		Assert.Equal(Enumerable.Reverse(_ids), Order());
		Assert.Equal(0, _scheduler.PendingCount);
	}

	[Fact]
	public void Scheduling_does_nothing_for_a_manual_strip_and_cancel_drops_a_pending_move()
	{
		_ordering.ScheduleReorganize();
		Assert.Equal(0, _scheduler.PendingCount);
		_settings.Set("tabOrganization", System.Text.Json.Nodes.JsonValue.Create("newest"));
		_ordering.ScheduleReorganize();
		_ordering.Cancel();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal(_ids, Order());
	}
}
