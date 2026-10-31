namespace Zing.Core;

/// <param name="X">Relative to the window's content area, in DIPs.</param>
/// <param name="InWindow">False once the cursor has left the content area.</param>
/// <param name="Now">Milliseconds, any monotonic clock.</param>
public readonly record struct CursorSample(double X, double Y, bool InWindow, double Now);

/// <summary>
/// The edge-reveal decision (src/main/sidebar.ts, a port of Krackn's EdgeRevealMonitor). Pure: the
/// caller samples the cursor. The reveal comes from watching the cursor, not from a hover strip,
/// because an invisible strip along the left edge would sit over the web view and eat its clicks.
/// </summary>
public sealed class EdgeReveal(Action<bool> onChange, double edge = EdgeReveal.DefaultEdge, double dwellMs = EdgeReveal.DefaultDwellMs, double? hideBeyond = null)
{
	/// <summary>How near the left edge counts as touching it.</summary>
	public const double DefaultEdge = 6;

	/// <summary>A short dwell, so sweeping past the edge on the way to another window does not fling the sidebar out.</summary>
	public const double DefaultDwellMs = 70;

	public const double DefaultPeekMs = 1400;

	private readonly double _hideBeyond = hideBeyond ?? Layout.SidebarColumnWidth + 36;
	private double? _dwellStart;
	private double? _peekUntil;

	public bool Revealed { get; private set; }

	public bool Enabled { get; private set; }

	/// <summary>Enabled while the sidebar is unpinned; turning it off puts the sidebar away at once.</summary>
	public void SetEnabled(bool enabled)
	{
		Enabled = enabled;
		_dwellStart = null;
		_peekUntil = null;
		if (!enabled) Set(false);
	}

	/// <summary>Shows the sidebar briefly, as feedback that Unpin worked and where it went.</summary>
	public void Peek(double now, double durationMs = DefaultPeekMs)
	{
		if (!Enabled) return;
		_peekUntil = now + durationMs;
		Set(true);
	}

	public void Update(CursorSample sample)
	{
		if (!Enabled) return;
		if (Revealed)
		{
			UpdateRevealed(sample);
			return;
		}
		var atEdge = sample.InWindow && sample.X <= edge && sample.Y > 0;
		if (!atEdge)
		{
			_dwellStart = null;
			return;
		}
		_dwellStart ??= sample.Now;
		if (sample.Now - _dwellStart < dwellMs) return;
		_dwellStart = null;
		Set(true);
	}

	private void UpdateRevealed(CursorSample sample)
	{
		if (_peekUntil is { } until && sample.Now < until) return;
		_peekUntil = null;
		if (sample.InWindow && sample.X <= _hideBeyond) return;
		Set(false);
	}

	private void Set(bool revealed)
	{
		if (Revealed == revealed) return;
		Revealed = revealed;
		onChange(revealed);
	}
}

/// <summary>
/// Polls the cursor while the sidebar is unpinned and drives an <see cref="EdgeReveal"/>. Not
/// thread-safe: give it a scheduler that runs on the UI thread, and read the cursor there.
/// </summary>
public sealed class SidebarReveal : IDisposable
{
	public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

	private readonly Func<Point?> _cursor;
	private readonly IScheduler _scheduler;
	private readonly EdgeReveal _reveal;
	private IDisposable? _timer;
	private double _now;
	private bool _disposed;

	/// <param name="cursor">
	/// The cursor relative to the window's content area, in DIPs. Null when it is outside the content
	/// area or Zing is not the focused app: unfocused counts as "the cursor is elsewhere", so a revealed
	/// sidebar puts itself away when the user switches apps, and nothing is revealed in a window they
	/// are not using.
	/// </param>
	public SidebarReveal(Func<Point?> cursor, IScheduler scheduler)
	{
		_cursor = cursor;
		_scheduler = scheduler;
		_reveal = new EdgeReveal(revealed => Changed?.Invoke(revealed));
	}

	/// <summary>Raised with the new value whenever the sidebar is revealed or put away.</summary>
	public event Action<bool>? Changed;

	public bool Revealed => _reveal.Revealed;

	public bool Enabled => _reveal.Enabled;

	/// <summary>Enabled while the sidebar is unpinned. Turning it off stops polling and puts the sidebar away.</summary>
	public void SetEnabled(bool enabled)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_timer?.Dispose();
		_timer = null;
		_reveal.SetEnabled(enabled);
		if (enabled) Arm();
	}

	public void Peek() => _reveal.Peek(_now);

	public void Dispose()
	{
		_disposed = true;
		_timer?.Dispose();
		_timer = null;
	}

	private void Arm() => _timer = _scheduler.Schedule(PollInterval, Tick);

	private void Tick()
	{
		// The clock advances by one poll per tick: dwell and peek only need elapsed time, and this
		// keeps them driven by the scheduler alone.
		_now += PollInterval.TotalMilliseconds;
		var cursor = _cursor();
		_reveal.Update(cursor is { } p
			? new CursorSample(p.X, p.Y, true, _now)
			: new CursorSample(-1, -1, false, _now));
		if (_reveal.Enabled && !_disposed) Arm();
	}
}
