namespace Zing.Core;

/// <summary>What closing a window asks first, as Firefox does: the person closed it themselves (its close button, Alt+F4, Ctrl+Shift+W).</summary>
public static class WindowClose
{
	private const string WontReopen = "They won't reopen next time unless Reopen tabs from last time is on in Settings.";

	/// <summary>
	/// Null when it closes without asking: one tab is little to lose and a misclick away from a window of them is the case to
	/// catch, and Zing's last window with Reopen tabs on loses nothing. Otherwise the question. Only the tabs that could come
	/// back count (<see cref="Session.Prepare"/>, as <see cref="ClosedWindows.Push"/> keeps them), so a window it promises
	/// Ctrl+Shift+T for is always one that is kept, and new tabs and Settings pages are nothing to lose.
	/// </summary>
	public static string? Question(IReadOnlyList<SessionTab> tabs, bool lastWindow, bool tabsReopen)
	{
		var count = Session.Prepare(tabs).Count;
		if (count <= 1 || (lastWindow && tabsReopen)) return null;
		return lastWindow
			? $"Close Zing and its {count} tabs?\n\n{WontReopen}"
			: $"Close this window and its {count} tabs?\n\nCtrl+Shift+T in another window brings it back.";
	}

	/// <summary>Quitting (every window together): asks once, and only when tabs would be lost, with Reopen tabs off.</summary>
	public static string? QuitQuestion(IReadOnlyList<IReadOnlyList<SessionTab>> windows, bool tabsReopen)
	{
		var count = windows.Sum(w => Session.Prepare(w).Count);
		if (tabsReopen || count <= 1) return null;
		var those = windows.Count == 1 ? "its" : $"its {windows.Count} windows and";
		return $"Quit Zing and close {those} {count} tabs?\n\n{WontReopen}";
	}
}

/// <summary>
/// Close requests (WM_CLOSE) that come together: the taskbar's "Close all windows" posts one to each window at once, which
/// is quitting, and must save every window rather than drop all but the last from the session one by one. Requests are
/// collected for a moment and handed over together.
/// </summary>
public sealed class CloseBurst<T>(IScheduler scheduler, Action<IReadOnlyList<T>> closing) where T : notnull
{
	/// <summary>The posted messages are all queued by then: they are handled before the timer that ends the wait.</summary>
	public static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(200);

	private readonly List<T> _requests = [];

	public void Request(T window)
	{
		if (_requests.Contains(window)) return;
		_requests.Add(window);
		if (_requests.Count == 1) scheduler.Schedule(Wait, Flush);
	}

	private void Flush()
	{
		var requests = _requests.ToList();
		_requests.Clear();
		closing(requests);
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
