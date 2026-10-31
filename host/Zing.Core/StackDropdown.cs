namespace Zing.Core;

/// <summary>
/// The tab-stack dropdown's state (stack-dropdown.ts). While open it covers
/// everything under the tab strip, so a click anywhere else lands on its
/// backdrop and closes it, and the page beneath never sees the click.
/// </summary>
public sealed class StackDropdown(Func<string, StackMembers?> members, IScheduler scheduler, Action<StackDropdownState?> shown)
{
	/// <summary>A file held over the stack opens it; leaving both chip and list closes it again after this long.</summary>
	private static readonly TimeSpan DragClose = TimeSpan.FromMilliseconds(300);

	private double _anchorX;
	private bool _openedByDrag;
	private bool _overChip;
	private bool _overPanel;
	private IDisposable? _dragCloseTimer;

	public string? OpenKey { get; private set; }

	/// <summary>For a dropdown renderer that finished loading after the first push.</summary>
	public StackDropdownState? Current { get; private set; }

	public void Toggle(string key, double anchorX)
	{
		if (OpenKey == key)
		{
			Close();
			return;
		}
		Open(key, anchorX, byDrag: false);
	}

	public void OpenForDrag(string key, double anchorX)
	{
		if (OpenKey == key) return;
		Open(key, anchorX, byDrag: true);
	}

	public void Close()
	{
		CancelDragClose();
		_openedByDrag = false;
		_overChip = _overPanel = false;
		if (OpenKey is null) return;
		OpenKey = null;
		Current = null;
		shown(null);
	}

	/// <summary>Re-sends the pages after any tab change, or closes when the stack no longer has two.</summary>
	public void Refresh()
	{
		if (OpenKey is null) return;
		if (members(OpenKey) is not { } current)
		{
			Close();
			return;
		}
		Send(OpenKey, current);
	}

	public void DragPresence(string source, bool inside)
	{
		if (source == "chip") _overChip = inside;
		else if (source == "panel") _overPanel = inside;
		else throw new ArgumentException($"Unknown drag presence source {source}");
		CancelDragClose();
		if (!_openedByDrag || _overChip || _overPanel) return;
		_dragCloseTimer = scheduler.Schedule(DragClose, Close);
	}

	private void Open(string key, double anchorX, bool byDrag)
	{
		if (members(key) is not { } current) return;
		OpenKey = key;
		_anchorX = anchorX;
		_openedByDrag = byDrag;
		_overChip = byDrag;
		_overPanel = false;
		Send(key, current);
	}

	private void Send(string key, StackMembers current)
	{
		Current = current.ToState(key, _anchorX);
		shown(Current);
	}

	private void CancelDragClose()
	{
		_dragCloseTimer?.Dispose();
		_dragCloseTimer = null;
	}
}
