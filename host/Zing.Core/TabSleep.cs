using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>The `sleepingTabs` setting (src/shared/layout/sleep.ts SleepSettings).</summary>
public sealed record SleepSettings(bool Enabled, double AfterMinutes, bool SleepPinned)
{
	public static readonly SleepSettings Default = new(true, 30, false);

	public const double MinMinutes = 1;
	public const double MaxMinutes = 7 * 24 * 60;

	/// <summary>The setting is exactly these three keys, with a sane inactivity window (sleepSettings.ts isSleepSettings).</summary>
	public static bool IsValid(JsonNode? value)
	{
		if (value is not JsonObject obj || obj.Count != 3) return false;
		if (!TryGet(obj, "enabled", out bool _) || !TryGet(obj, "sleepPinned", out bool _)) return false;
		if (!TryGet(obj, "afterMinutes", out double minutes)) return false;
		return double.IsFinite(minutes) && minutes >= MinMinutes && minutes <= MaxMinutes;
	}

	public static SleepSettings FromNode(JsonNode? value)
	{
		if (!IsValid(value)) throw new InvalidSettingException("sleepingTabs", value);
		var obj = (JsonObject)value!;
		return new SleepSettings(obj["enabled"]!.GetValue<bool>(), obj["afterMinutes"]!.GetValue<double>(), obj["sleepPinned"]!.GetValue<bool>());
	}

	private static bool TryGet<T>(JsonObject obj, string key, out T value)
	{
		value = default!;
		return obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue(out value!);
	}
}

public sealed record SleepCandidate(
	string Id,
	long LastActiveAt,
	bool Pinned,
	bool IsActive,
	bool VisibleInSplit,
	bool PlayingAudio,
	bool HasUnsavedForm,
	bool Asleep);

public enum KeepAwakeReason
{
	Disabled,
	AlreadyAsleep,
	Active,
	VisibleInSplit,
	PlayingAudio,
	UnsavedForm,
	Pinned,
	RecentlyUsed,
}

/// <summary>Which tabs may sleep and when to look again (src/shared/layout/sleep.ts).</summary>
public static class SleepRules
{
	private const long MinuteMs = 60_000;

	/// <summary>Why a tab stays awake, or null if it may sleep.</summary>
	public static KeepAwakeReason? KeepAwake(SleepCandidate tab, long now, SleepSettings settings)
	{
		if (!settings.Enabled) return KeepAwakeReason.Disabled;
		if (tab.Asleep) return KeepAwakeReason.AlreadyAsleep;
		if (tab.IsActive) return KeepAwakeReason.Active;
		if (tab.VisibleInSplit) return KeepAwakeReason.VisibleInSplit;
		if (tab.PlayingAudio) return KeepAwakeReason.PlayingAudio;
		// Discarding would throw away what the user typed.
		if (tab.HasUnsavedForm) return KeepAwakeReason.UnsavedForm;
		if (tab.Pinned && !settings.SleepPinned) return KeepAwakeReason.Pinned;
		if (now - tab.LastActiveAt < IdleMs(settings)) return KeepAwakeReason.RecentlyUsed;
		return null;
	}

	public static List<string> TabsToSleep(IEnumerable<SleepCandidate> tabs, long now, SleepSettings settings) =>
		tabs.Where(t => KeepAwake(t, now, settings) is null).Select(t => t.Id).ToList();

	/// <summary>The soonest moment any awake tab could become sleepable, so one timer replaces polling.</summary>
	public static long? NextSleepCheck(IEnumerable<SleepCandidate> tabs, long now, SleepSettings settings)
	{
		if (!settings.Enabled) return null;
		var waiting = tabs.Where(t => KeepAwake(t, now, settings) == KeepAwakeReason.RecentlyUsed).ToList();
		if (waiting.Count == 0) return null;
		return waiting.Min(t => t.LastActiveAt + IdleMs(settings));
	}

	private static long IdleMs(SleepSettings settings) => (long)(settings.AfterMinutes * MinuteMs);
}

/// <summary>What the page reports just before it sleeps (src/shared/session/pageProbe.ts).</summary>
public sealed record PageProbe(double ScrollY, bool Dirty)
{
	/// <summary>
	/// Runs inside a page just before it sleeps. A form counts as unsaved when any
	/// field differs from what the markup gave it, because discarding the page
	/// would throw away what was typed.
	/// </summary>
	public const string Source = """
		(() => {
			const edited = (el) => {
				if (el instanceof HTMLTextAreaElement) return el.value !== el.defaultValue
				if (el instanceof HTMLSelectElement) {
					if (el.multiple) return [...el.options].some((o) => o.selected !== o.defaultSelected)
					const expected = Math.max(0, [...el.options].findIndex((o) => o.defaultSelected))
					return el.selectedIndex !== expected
				}
				if (!(el instanceof HTMLInputElement)) return false
				if (['hidden', 'submit', 'button', 'reset', 'image', 'file'].includes(el.type)) return false
				if (el.type === 'checkbox' || el.type === 'radio') return el.checked !== el.defaultChecked
				return el.value !== el.defaultValue
			}
			const richText = [...document.querySelectorAll('[contenteditable=""], [contenteditable="true"]')]
				.some((el) => (el.textContent || '').trim() !== '')
			const dirty = richText || [...document.querySelectorAll('input, textarea, select')].some(edited)
			return { scrollY: Math.round(window.scrollY), dirty }
		})()
		""";

	/// <summary>Reads the script's JSON result; anything but {scrollY: finite number, dirty: bool} is refused.</summary>
	public static PageProbe Parse(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Object) throw new PageProbeException(value);
		if (!value.TryGetProperty("scrollY", out var scroll) || scroll.ValueKind != JsonValueKind.Number) throw new PageProbeException(value);
		if (!value.TryGetProperty("dirty", out var dirty) || dirty.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new PageProbeException(value);
		if (!scroll.TryGetDouble(out var y) || !double.IsFinite(y)) throw new PageProbeException(value);
		return new PageProbe(Math.Max(0, y), dirty.GetBoolean());
	}
}

public sealed class PageProbeException(JsonElement value) : Exception($"Page probe returned an unexpected value: {value.GetRawText()}")
{
	public JsonElement Value { get; } = value.Clone();
}

/// <summary>Thrown by the host when a page navigated away or died mid-probe, so its state is unknown.</summary>
public sealed class PageUnavailableException(string tabId, Exception? inner = null)
	: Exception($"Tab {tabId} could not be inspected", inner)
{
	public string TabId { get; } = tabId;
}

/// <summary>
/// Sleeps tabs that have sat idle: schedules one timer for the soonest moment a
/// tab could qualify, then drops that tab's renderer while the record, scroll
/// position and a small thumbnail stay behind.
/// </summary>
public sealed class TabSleeper
{
	/// <summary>Audio and unsaved input have no event that says "now safe", so blocked tabs are looked at again after this.</summary>
	public const long RetryMs = 5 * 60_000;
	private const int MaxThumbnails = 64;

	private readonly ILifecycleTabs _tabs;
	private readonly Func<SleepSettings> _settings;
	private readonly IClock _clock;
	private readonly OneShotTimer _timer;
	private readonly Action<Exception> _onError;
	private readonly Dictionary<string, double> _scroll = [];
	private readonly OrderedDictionary<string, string> _thumbnails = [];
	/// <summary>Tabs held awake last sweep by unsaved input or a page that couldn't be inspected.</summary>
	private readonly HashSet<string> _held = [];
	private long? _retryAt;
	private bool _sweeping;

	public TabSleeper(ILifecycleTabs tabs, Func<SleepSettings> settings, IClock clock, IScheduler scheduler, Action<Exception> onError)
	{
		_tabs = tabs;
		_settings = settings;
		_clock = clock;
		_onError = onError;
		_timer = new OneShotTimer(clock, scheduler, OnTimer);
	}

	public string? Thumbnail(string id) => _thumbnails.GetValueOrDefault(id);

	/// <summary>Recomputes the single wake-up. Cheap enough to call on every tab change.</summary>
	public void Reschedule()
	{
		Prune();
		if (_sweeping) return;
		_timer.ArmAt(NextTarget());
	}

	/// <summary>After the clock jumps: a target computed against the old time is stale.</summary>
	public void Rearm()
	{
		_timer.Cancel();
		Reschedule();
	}

	public void Cancel() => _timer.Cancel();

	/// <summary>
	/// A sleeping tab has a fresh page: its saved scroll position, to apply once it has loaded
	/// (null when there is none), and the thumbnail stops being needed.
	/// </summary>
	public double? TakeScroll(string id)
	{
		_thumbnails.Remove(id);
		if (!_scroll.Remove(id, out var y)) return null;
		return y > 0 ? y : null;
	}

	private void OnTimer() => _ = RunSweep();

	private async Task RunSweep()
	{
		try
		{
			await Sweep();
		}
		catch (Exception error)
		{
			_onError(error);
		}
	}

	private async Task Sweep()
	{
		_sweeping = true;
		try
		{
			_retryAt = null;
			_held.Clear();
			var ids = SleepRules.TabsToSleep(Candidates(), _clock.Now(), _settings());
			foreach (var id in ids)
			{
				await TrySleep(id);
				// Anything still awake was spared for a reason; not re-sweeping it right away avoids a spin.
				if (_tabs.HasPage(id)) _held.Add(id);
			}
		}
		finally
		{
			_sweeping = false;
			Reschedule();
		}
	}

	private async Task TrySleep(string id)
	{
		var before = Find(id);
		if (before is null || !_tabs.HasPage(id)) return;
		PageProbe probe;
		try
		{
			probe = await _tabs.ProbeAsync(id);
		}
		catch (PageUnavailableException error)
		{
			// The page navigated or crashed mid-probe; its state is unknown, so it stays awake.
			_held.Add(id);
			_onError(error);
			return;
		}
		if (probe.Dirty)
		{
			_held.Add(id);
			return;
		}
		var thumbnail = await _tabs.CaptureThumbnailAsync(id);
		// Awaits above let the user come back to the tab, or the page die.
		var after = Find(id);
		if (after is null || after.LastActiveAt != before.LastActiveAt) return;
		if (id == _tabs.ActiveId || !_tabs.HasPage(id) || _tabs.IsAudible(id)) return;
		_scroll[id] = probe.ScrollY;
		if (thumbnail is not null) Remember(id, thumbnail);
		_tabs.Sleep(id);
	}

	private void Remember(string id, string thumbnail)
	{
		_thumbnails.Remove(id);
		_thumbnails.Add(id, thumbnail);
		if (_thumbnails.Count > MaxThumbnails) _thumbnails.RemoveAt(0);
	}

	private TabRecord? Find(string id) => _tabs.Records.FirstOrDefault(t => t.Id == id);

	/// <summary>Only tabs with a renderer: one without is already asleep, or was never loaded.</summary>
	private List<SleepCandidate> Candidates() =>
		_tabs.Records
			.Where(t => _tabs.HasPage(t.Id))
			.Select(t => new SleepCandidate(
				t.Id,
				t.LastActiveAt,
				t.Pinned,
				t.Id == _tabs.ActiveId,
				// No split view yet; its owner flips this when splits land.
				VisibleInSplit: false,
				_tabs.IsAudible(t.Id),
				HasUnsavedForm: false,
				Asleep: false))
			.ToList();

	private long? NextTarget()
	{
		var settings = _settings();
		var now = _clock.Now();
		var candidates = Candidates();
		var blocked = _held.Count > 0 || candidates.Any(c => SleepRules.KeepAwake(c, now, settings) == KeepAwakeReason.PlayingAudio);
		if (!blocked) _retryAt = null;
		else _retryAt ??= now + RetryMs;
		// NextSleepCheck only looks ahead; a tab already past its idle time (the clock jumped, or the
		// machine slept) is due now.
		var due = SleepRules.TabsToSleep(candidates, now, settings).Any(id => !_held.Contains(id));
		var targets = new[] { due ? now : (long?)null, SleepRules.NextSleepCheck(candidates, now, settings), _retryAt }
			.OfType<long>()
			.ToList();
		return targets.Count == 0 ? null : targets.Min();
	}

	private void Prune()
	{
		if (_scroll.Count == 0 && _thumbnails.Count == 0 && _held.Count == 0) return;
		var open = _tabs.Records.Select(t => t.Id).ToHashSet();
		foreach (var id in _scroll.Keys.Where(id => !open.Contains(id)).ToList()) _scroll.Remove(id);
		foreach (var id in _thumbnails.Keys.Where(id => !open.Contains(id)).ToList()) _thumbnails.Remove(id);
		_held.RemoveWhere(id => !open.Contains(id));
	}
}
