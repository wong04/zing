namespace Zing.Core;

public sealed class UnknownStackException(string key) : Exception($"No stack with key {key}")
{
	public string Key { get; } = key;
}

/// <summary>
/// Keeps a window's tab list in the order the tabOrganization setting asks for and turns drags into
/// list changes (src/main/tab-ordering.ts). Pinned tabs are never sorted, and a hand-made change under
/// a sorted arrangement first freezes what is on screen as the manual order, so nothing jumps.
/// </summary>
public sealed class TabOrdering(TabService tabs, SettingsStore settings, Action<string> notify, IScheduler scheduler) : IDisposable
{
	public const string ManualNotice = "Tabs are now in manual order. Change it from the tab strip menu.";

	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

	private IDisposable? _timer;

	public TabOrganization Organization => TabOrganizations.Parse(settings.Get("tabOrganization")!.GetValue<string>());

	public bool Dropdowns => settings.Get("tabDropdowns")!.GetValue<bool>();

	/// <summary>The stacks as the Dropdown style shows them now, pinned tabs excluded.</summary>
	public List<Stack> Stacks() => TabOrganizer.DisplayedStacks(Snapshots(), Organization);

	/// <summary>Re-sorts now. Manual does nothing, which is why dragging works there.</summary>
	public void Reorganize()
	{
		var organization = Organization;
		if (organization == TabOrganization.Manual) return;
		tabs.ApplyOrder(TabOrganizer.Order(Snapshots(), organization));
	}

	/// <summary>
	/// A new tab has no site until its page loads, so sorting on creation alone left "By site" looking
	/// broken. The debounce folds a redirect chain into one move instead of a tab hopping across the strip.
	/// </summary>
	public void ScheduleReorganize()
	{
		if (Organization == TabOrganization.Manual) return;
		_timer?.Dispose();
		_timer = scheduler.Schedule(Settle, Reorganize);
	}

	public void Cancel()
	{
		_timer?.Dispose();
		_timer = null;
	}

	public void Dispose() => Cancel();

	public void SetOrganization(TabOrganization organization)
	{
		settings.Set("tabOrganization", organization.ToSetting());
		Reorganize();
	}

	public void SetDropdowns(bool on) => settings.Set("tabDropdowns", on);

	/// <summary>A tab chip dragged to a new position in the strip (Separate style, or a pinned tab).</summary>
	public void Move(string id, int to)
	{
		LeaveSortedOrder();
		var snaps = Snapshots();
		var from = snaps.FindIndex(s => s.Id == id);
		tabs.ApplyOrder(TabOrganizer.MoveTab(snaps, from, to));
		Reorganize();
	}

	/// <summary>Dropdown style: a stack dragged along the strip with all its pages. <paramref name="to"/> is its place among the stacks.</summary>
	public void MoveStack(string key, int to)
	{
		FreezeAndLeaveSortedOrder();
		var stacks = Stacks();
		var from = stacks.FindIndex(s => s.Key == key);
		if (from < 0) throw new UnknownStackException(key);
		var next = TabOrganizer.MovingStack(stacks.Select(s => s.Ids).ToList(), from, to);
		tabs.ApplyOrder(TabOrganizer.Arrange(Snapshots(), next));
		Reorganize();
	}

	/// <summary>A page dragged inside its stack's dropdown. <paramref name="index"/> is its new place among the stack's pages.</summary>
	public void MoveWithin(string id, string key, int index)
	{
		FreezeAndLeaveSortedOrder();
		var stack = Stacks().FirstOrDefault(s => s.Key == key) ?? throw new UnknownStackException(key);
		tabs.ApplyOrder(TabOrganizer.MoveWithin(Snapshots(), id, stack.Ids, index));
		Reorganize();
	}

	private void LeaveSortedOrder()
	{
		if (!Organization.DragSwitchesToManual()) return;
		settings.Set("tabOrganization", TabOrganization.Manual.ToSetting());
		notify(ManualNotice);
	}

	private void FreezeAndLeaveSortedOrder()
	{
		var organization = Organization;
		if (!organization.DragSwitchesToManual()) return;
		tabs.ApplyOrder(TabOrganizer.FreezeDisplayedOrder(Snapshots(), organization));
		LeaveSortedOrder();
	}

	private List<TabSnapshot> Snapshots() =>
		tabs.Records.Select(r => TabSnapshot.Of(r, r.Id == tabs.ActiveId)).ToList();
}
