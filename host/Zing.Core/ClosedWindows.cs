namespace Zing.Core;

/// <summary>What closing a window asks first, as Firefox does: the person closed it themselves (its close button, Alt+F4, Ctrl+Shift+W).</summary>
public static class WindowClose
{
	/// <summary>
	/// Null when it closes without asking: one tab is little to lose and a misclick away from a window of them is the case to
	/// catch, and Zing's last window with Reopen tabs on loses nothing. Otherwise the question.
	/// </summary>
	public static string? Question(int tabs, bool lastWindow, bool tabsReopen)
	{
		if (tabs <= 1 || (lastWindow && tabsReopen)) return null;
		return lastWindow
			? $"Close Zing and its {tabs} tabs?\n\nThey won't reopen next time unless Reopen tabs from last time is on in Settings."
			: $"Close this window and its {tabs} tabs?\n\nCtrl+Shift+T in another window brings it back.";
	}
}

/// <summary>
/// Windows the person closed, newest last, kept for the app and not for one window (whose closed tabs go with it), so
/// Ctrl+Shift+T brings the last one back once a window has no closed tab of its own left. In memory only, like closed tabs.
/// </summary>
public sealed class ClosedWindows
{
	/// <summary>Chrome keeps 25 closed tabs and windows together; a window is many tabs, so fewer are kept.</summary>
	public const int Capacity = 10;

	private readonly List<IReadOnlyList<SessionTab>> _closed = [];

	public int Count => _closed.Count;

	/// <summary>A window with no web page to bring back (only new tabs) isn't kept.</summary>
	public void Push(IReadOnlyList<SessionTab> tabs)
	{
		var kept = Session.Prepare(tabs);
		if (kept.Count == 0) return;
		_closed.Add(kept);
		if (_closed.Count > Capacity) _closed.RemoveAt(0);
	}

	/// <summary>The last window closed, taken off the list; null when there is none.</summary>
	public IReadOnlyList<SessionTab>? Pop()
	{
		if (_closed.Count == 0) return null;
		var last = _closed[^1];
		_closed.RemoveAt(_closed.Count - 1);
		return last;
	}
}
