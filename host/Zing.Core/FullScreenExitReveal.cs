namespace Zing.Core;

/// <summary>
/// When the Exit full screen button shows over a fullscreen page (Chrome's pattern): once the cursor reaches the top edge,
/// until it moves down past the button or leaves the window. Nothing else reveals itself over a fullscreen page (the
/// window's Cursor counts as away there), and a page such as the PDF viewer may draw no way out of its own, so this is the
/// one besides Esc. Polls the cursor like <see cref="SidebarReveal"/>; give it a scheduler that runs on the UI thread.
/// </summary>
public sealed class FullScreenExitReveal(Func<Point?> cursor, IScheduler scheduler) : IDisposable
{
	/// <summary>How near the top counts as reaching for it: a fullscreen window's top is the screen's, where the cursor stops.</summary>
	public const double Edge = 4;

	/// <summary>Below this the button goes: past it, with room to move onto it from the edge.</summary>
	public const double HideBelow = 120;

	private IDisposable? _timer;
	private bool _enabled;

	public bool Shown { get; private set; }

	/// <summary>Raised with the new value whenever the button shows or goes.</summary>
	public event Action<bool>? Changed;

	/// <summary>On while a page is fullscreen. Off stops polling and takes the button down at once.</summary>
	public void SetEnabled(bool enabled)
	{
		_timer?.Dispose();
		_timer = null;
		_enabled = enabled;
		if (!enabled)
		{
			Set(false);
			return;
		}
		Arm();
	}

	public void Dispose()
	{
		_enabled = false;
		_timer?.Dispose();
		_timer = null;
	}

	private void Arm() => _timer = scheduler.Schedule(SidebarReveal.PollInterval, Tick);

	private void Tick()
	{
		Update(cursor());
		// A Changed handler may have turned it off.
		if (_enabled) Arm();
	}

	private void Update(Point? at)
	{
		if (at is not { } point || point.Y > HideBelow)
		{
			Set(false);
			return;
		}
		if (point.Y <= Edge) Set(true);
	}

	private void Set(bool shown)
	{
		if (Shown == shown) return;
		Shown = shown;
		Changed?.Invoke(shown);
	}
}
