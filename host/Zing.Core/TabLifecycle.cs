using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>Wall-clock time (epoch milliseconds) as a dependency, so lifecycle timers can be driven quickly in tests.</summary>
public interface IClock
{
	long Now();

	/// <summary>Fires after the clock jumps forward, so timers armed against the old time can be recomputed.</summary>
	IDisposable OnAdvance(Action listener);
}

public sealed class SystemClock : IClock
{
	public long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	public IDisposable OnAdvance(Action listener) => new Subscription(() => { });
}

/// <summary>Real time plus a movable offset. Only ever constructed under the test env var.</summary>
public sealed class OffsetClock : IClock
{
	private long _offset;
	private readonly List<Action> _listeners = [];

	public long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _offset;

	public void Advance(long ms)
	{
		_offset += ms;
		foreach (var listener in _listeners.ToArray()) listener();
	}

	public IDisposable OnAdvance(Action listener)
	{
		_listeners.Add(listener);
		return new Subscription(() => _listeners.Remove(listener));
	}
}

internal sealed class Subscription(Action remove) : IDisposable
{
	public void Dispose() => remove();
}

/// <summary>One pending wake-up at an absolute time. Re-arming for the same time is a no-op.</summary>
public sealed class OneShotTimer(IClock clock, IScheduler scheduler, Action fire)
{
	/// <summary>Keeps a floor under the delay so a target in the past can't become a hot loop.</summary>
	public const long MinDelayMs = 50;

	/// <summary>A longer wait just fires early and is recomputed.</summary>
	public const long MaxDelayMs = int.MaxValue;

	private IDisposable? _handle;
	private long? _target;

	public void ArmAt(long? target)
	{
		if (target == _target) return;
		Cancel();
		if (target is not { } at) return;
		_target = at;
		var delay = Math.Min(Math.Max(at - clock.Now(), MinDelayMs), MaxDelayMs);
		_handle = scheduler.Schedule(TimeSpan.FromMilliseconds(delay), () =>
		{
			_handle = null;
			_target = null;
			fire();
		});
	}

	public void Cancel()
	{
		_handle?.Dispose();
		_handle = null;
		_target = null;
	}
}

/// <summary>How long a tab may sit untouched before it's closed.</summary>
public enum AutoCloseRule
{
	Hour,
	Day,
	Week,
	Custom,
}

/// <summary>The auto-close settings (src/shared/browser/tabLifecycle.ts AutoClosePolicy).</summary>
public sealed record AutoClosePolicy(
	bool Enabled = false,
	AutoCloseRule Rule = AutoCloseRule.Day,
	double CustomHours = 12,
	bool AtTimeEnabled = false,
	int Hour = 3,
	int Minute = 0)
{
	private const long HourMs = 3_600_000;

	public static readonly AutoClosePolicy Default = new();

	public static AutoClosePolicy FromSettings(SettingsStore settings) => new(
		Flag(settings, "autoCloseEnabled"),
		ParseRule(settings),
		Number(settings, "autoCloseCustomHours"),
		Flag(settings, "autoCloseAtTimeEnabled"),
		(int)Number(settings, "autoCloseHour"),
		(int)Number(settings, "autoCloseMinute"));

	/// <summary>Milliseconds of inactivity after which a tab is closed.</summary>
	public long Interval() => Rule switch
	{
		AutoCloseRule.Hour => HourMs,
		AutoCloseRule.Day => 24 * HourMs,
		AutoCloseRule.Week => 7 * 24 * HourMs,
		AutoCloseRule.Custom => (long)(EffectiveCustomHours() * HourMs),
		_ => throw new ArgumentOutOfRangeException(nameof(Rule), Rule, null),
	};

	public string DescribedInterval()
	{
		switch (Rule)
		{
			case AutoCloseRule.Hour: return "1 hour";
			case AutoCloseRule.Day: return "1 day";
			case AutoCloseRule.Week: return "7 days";
		}
		var hours = EffectiveCustomHours();
		if (hours < 1) return $"{(int)(hours * 60)} minutes";
		if (hours == 1) return "1 hour";
		if (hours % 24 == 0)
		{
			var days = (int)(hours / 24);
			return days == 1 ? "1 day" : $"{days} days";
		}
		return $"{(int)hours} hours";
	}

	private double EffectiveCustomHours() => Math.Max(CustomHours, 0.25);

	public static AutoCloseRule ParseRule(string name) => name switch
	{
		"hour" => AutoCloseRule.Hour,
		"day" => AutoCloseRule.Day,
		"week" => AutoCloseRule.Week,
		"custom" => AutoCloseRule.Custom,
		_ => throw new ArgumentException($"Unknown auto-close rule \"{name}\"", nameof(name)),
	};

	private static AutoCloseRule ParseRule(SettingsStore settings) =>
		ParseRule(Read(settings, "autoCloseRule").GetValue<string>());

	private static bool Flag(SettingsStore settings, string key) => Read(settings, key).GetValue<bool>();

	private static double Number(SettingsStore settings, string key) => Read(settings, key).GetValue<double>();

	private static JsonNode Read(SettingsStore settings, string key) =>
		settings.Get(key) ?? throw new InvalidDataException($"Setting {key} is missing");
}

/// <summary>A tab reduced to what auto-close needs (src/shared/browser/tabOrganizer.ts AutoCloseTab).</summary>
public sealed record AutoCloseTab(string Id, long LastAccessed, bool IsPinned, bool IsActive);

/// <summary>The calendar arithmetic the daily sweep needs, so tests can pin a time zone.</summary>
public interface IDayCalendar
{
	long StartOfDay(long time);

	/// <summary>The instant at <paramref name="hour"/>:<paramref name="minute"/> on the day that starts at <paramref name="dayStart"/>.</summary>
	long AtTime(long dayStart, int hour, int minute);

	long AddDays(long time, int days);
}

public sealed class TimeZoneCalendar(TimeZoneInfo zone) : IDayCalendar
{
	public static readonly TimeZoneCalendar Local = new(TimeZoneInfo.Local);
	public static readonly TimeZoneCalendar Utc = new(TimeZoneInfo.Utc);

	public long StartOfDay(long time) => ToEpoch(ToLocal(time).Date);

	public long AtTime(long dayStart, int hour, int minute) => ToEpoch(ToLocal(dayStart).Date.AddHours(hour).AddMinutes(minute));

	public long AddDays(long time, int days) => ToEpoch(ToLocal(time).AddDays(days));

	private DateTime ToLocal(long time) =>
		TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime, zone);

	// A wall-clock time skipped by a spring-forward gap rolls forward, as JavaScript's setHours does.
	private long ToEpoch(DateTime local)
	{
		var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
		if (zone.IsInvalidTime(unspecified)) unspecified += zone.GetUtcOffset(unspecified.AddDays(1)) - zone.GetUtcOffset(unspecified.AddDays(-1));
		return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero).ToUnixTimeMilliseconds();
	}
}

/// <summary>When auto-close acts (tabLifecycle.ts and session/autoCloseSchedule.ts).</summary>
public static class AutoClose
{
	/// <summary>
	/// Pinned tabs are spared because being spared is what pinning means, the
	/// active tab because closing what someone is looking at is indefensible, and
	/// the final tab because a browser with no tabs is a broken browser.
	/// </summary>
	private static List<string> Closable(IReadOnlyList<AutoCloseTab> tabs, Func<AutoCloseTab, bool> isEligible)
	{
		var doomed = new List<string>();
		var survivors = tabs.Count;
		foreach (var tab in tabs)
		{
			if (survivors <= 1) break;
			if (tab.IsPinned || tab.IsActive || !isEligible(tab)) continue;
			doomed.Add(tab.Id);
			survivors--;
		}
		return doomed;
	}

	/// <summary>Which tabs the sweeper should close.</summary>
	public static List<string> TabsToClose(IReadOnlyList<AutoCloseTab> tabs, AutoClosePolicy policy, long now)
	{
		if (!policy.Enabled) return [];
		var cutoff = now - policy.Interval();
		return Closable(tabs, t => t.LastAccessed < cutoff);
	}

	/// <summary>
	/// Everything eligible for the scheduled daily sweep: same exemptions,
	/// but age doesn't matter because the user asked for a clean slate at a particular time.
	/// </summary>
	public static List<string> TabsToCloseOnSchedule(IReadOnlyList<AutoCloseTab> tabs) => Closable(tabs, _ => true);

	/// <summary>
	/// Whether the daily sweep instant fell between two checks. Asking whether the instant lies in
	/// the interval since the last check can't miss it, where "is it 03:00 now" would.
	/// </summary>
	public static bool ScheduleElapsed(AutoClosePolicy policy, long since, long now, IDayCalendar calendar)
	{
		if (!policy.Enabled || !policy.AtTimeEnabled || now <= since) return false;
		// Walk each day the interval spans, so an interval longer than a day still fires once.
		for (var probe = calendar.StartOfDay(since); probe <= now; probe = calendar.AddDays(probe, 1))
		{
			var fire = calendar.AtTime(probe, policy.Hour, policy.Minute);
			if (fire > since && fire <= now) return true;
		}
		return false;
	}

	/// <summary>Next instant of the daily time strictly after <paramref name="now"/>.</summary>
	public static long NextDailyFire(AutoClosePolicy policy, long now, IDayCalendar calendar)
	{
		var today = calendar.StartOfDay(now);
		var fire = calendar.AtTime(today, policy.Hour, policy.Minute);
		if (fire > now) return fire;
		return calendar.AtTime(calendar.StartOfDay(calendar.AddDays(today, 1)), policy.Hour, policy.Minute);
	}

	/// <summary>
	/// The soonest moment a sweep could do something, so one timer replaces polling.
	/// TabsToClose closes a tab once its age is strictly past the interval, hence the +1.
	/// </summary>
	public static long? NextCheck(IReadOnlyList<AutoCloseTab> tabs, AutoClosePolicy policy, long now, IDayCalendar calendar)
	{
		if (!policy.Enabled) return null;
		var targets = new List<long>();
		if (policy.AtTimeEnabled) targets.Add(NextDailyFire(policy, now, calendar));
		// A lone tab is never closed, so waiting on its age would only spin.
		if (tabs.Count > 1)
		{
			var interval = policy.Interval();
			targets.AddRange(tabs.Where(t => !t.IsPinned && !t.IsActive).Select(t => t.LastAccessed + interval + 1));
		}
		return targets.Count == 0 ? null : targets.Min();
	}
}

/// <summary>What auto-close and sleeping need from the tab service; the host implements it.</summary>
public interface ILifecycleTabs
{
	string? ActiveId { get; }

	IReadOnlyList<TabRecord> Records { get; }

	/// <summary>Whether the tab has a live page (a sleeping or never-loaded tab does not).</summary>
	bool HasPage(string id);

	bool IsAudible(string id);

	/// <summary>
	/// Runs <see cref="PageProbe.Source"/> in the page and parses it with <see cref="PageProbe.Parse"/>.
	/// Throws <see cref="PageUnavailableException"/> if the page navigated or crashed mid-probe.
	/// </summary>
	Task<PageProbe> ProbeAsync(string id);

	/// <summary>A small JPEG data URL of the page, or null if it can't be captured in time (the host sets the timeout).</summary>
	Task<string?> CaptureThumbnailAsync(string id);

	/// <summary>Drops the tab's page but keeps its record.</summary>
	void Sleep(string id);

	void Close(string id);
}

/// <summary>
/// Closes idle tabs and sweeps at a fixed time of day, with one timer aimed at
/// the next moment either could matter. Spared tabs are the policy's business:
/// pinned, active, and the last one standing.
/// </summary>
public sealed class TabAutoCloser
{
	private readonly ILifecycleTabs _tabs;
	private readonly Func<AutoClosePolicy> _policy;
	private readonly IClock _clock;
	private readonly IDayCalendar _calendar;
	private readonly OneShotTimer _timer;
	/// <summary>Start of the window ScheduleElapsed asks about; only meaningful while the daily sweep is on.</summary>
	private long _lastSweep;

	public TabAutoCloser(ILifecycleTabs tabs, Func<AutoClosePolicy> policy, IClock clock, IScheduler scheduler, IDayCalendar calendar)
	{
		_tabs = tabs;
		_policy = policy;
		_clock = clock;
		_calendar = calendar;
		_lastSweep = clock.Now();
		_timer = new OneShotTimer(clock, scheduler, Sweep);
	}

	public void Reschedule()
	{
		var policy = _policy();
		var now = _clock.Now();
		// Turning the daily sweep on must not fire for a time that passed before it was asked for.
		if (!policy.Enabled || !policy.AtTimeEnabled) _lastSweep = now;
		_timer.ArmAt(AutoClose.NextCheck(Snapshots(), policy, now, _calendar));
	}

	public void Rearm()
	{
		_timer.Cancel();
		Reschedule();
	}

	public void Cancel() => _timer.Cancel();

	private void Sweep()
	{
		var policy = _policy();
		var now = _clock.Now();
		if (!policy.Enabled) return;
		var daily = AutoClose.ScheduleElapsed(policy, _lastSweep, now, _calendar);
		_lastSweep = now;
		CloseAll(AutoClose.TabsToClose(Snapshots(), policy, now));
		if (daily) CloseAll(AutoClose.TabsToCloseOnSchedule(Snapshots()));
		Reschedule();
	}

	private void CloseAll(IEnumerable<string> ids)
	{
		foreach (var id in ids) _tabs.Close(id);
	}

	private List<AutoCloseTab> Snapshots() =>
		_tabs.Records.Select(t => new AutoCloseTab(t.Id, t.LastActiveAt, t.Pinned, t.Id == _tabs.ActiveId)).ToList();
}

/// <summary>A window's auto-close and sleeping-tab timers, kept in step with its tabs and settings.</summary>
public sealed class TabLifecycle : IDisposable
{
	private static readonly HashSet<string> WatchedSettings =
	[
		"autoCloseEnabled",
		"autoCloseRule",
		"autoCloseCustomHours",
		"autoCloseAtTimeEnabled",
		"autoCloseHour",
		"autoCloseMinute",
		"sleepingTabs",
	];

	private readonly TabAutoCloser _autoCloser;
	private readonly TabSleeper _sleeper;
	private readonly List<IDisposable> _subscriptions;

	/// <param name="onError">Receives failures of background sweeps, which have no caller to throw to.</param>
	/// <param name="calendar">Defaults to the machine's time zone.</param>
	public TabLifecycle(
		ILifecycleTabs tabs,
		SettingsStore settings,
		IClock clock,
		IScheduler scheduler,
		Action<Exception> onError,
		IDayCalendar? calendar = null)
	{
		_autoCloser = new TabAutoCloser(tabs, () => AutoClosePolicy.FromSettings(settings), clock, scheduler, calendar ?? TimeZoneCalendar.Local);
		_sleeper = new TabSleeper(tabs, () => SleepSettings.FromNode(settings.Get("sleepingTabs")), clock, scheduler, onError);
		_subscriptions =
		[
			settings.Subscribe((_, key) =>
			{
				if (WatchedSettings.Contains(key)) Reschedule();
			}),
			clock.OnAdvance(() =>
			{
				_autoCloser.Rearm();
				_sleeper.Rearm();
			}),
		];
		Reschedule();
	}

	public string? Thumbnail(string id) => _sleeper.Thumbnail(id);

	/// <summary>Call whenever tabs are added, removed, activated or touched.</summary>
	public void Reschedule()
	{
		_autoCloser.Reschedule();
		_sleeper.Reschedule();
	}

	/// <summary>A sleeping tab has a fresh page: the scroll position to restore once it has loaded, if any.</summary>
	public double? Woke(string id) => _sleeper.TakeScroll(id);

	public void Dispose()
	{
		foreach (var subscription in _subscriptions) subscription.Dispose();
		_autoCloser.Cancel();
		_sleeper.Cancel();
	}
}
