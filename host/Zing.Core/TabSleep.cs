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
	LiveStream,
	PictureInPicture,
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

	/// <summary>
	/// When a sweep should next run: now if a tab not <paramref name="held"/> is due, the soonest tab to become due, or a
	/// retry for tabs held awake by audio, unsaved input or a refusal, which have no event that says "now safe".
	/// </summary>
	public static SweepTarget SweepTarget(IReadOnlyList<SleepCandidate> candidates, long now, SleepSettings settings, IReadOnlySet<string> held, long? retryAt)
	{
		var blocked = held.Count > 0 || candidates.Any(c => KeepAwake(c, now, settings) == KeepAwakeReason.PlayingAudio);
		long? retry = blocked ? retryAt ?? now + TabSleeper.RetryMs : null;
		// NextSleepCheck only looks ahead; a tab already past its idle time (the clock jumped, or the
		// machine slept) is due now.
		var due = TabsToSleep(candidates, now, settings).Any(id => !held.Contains(id));
		var targets = new[] { due ? now : (long?)null, NextSleepCheck(candidates, now, settings), retry }
			.OfType<long>()
			.ToList();
		return new SweepTarget(targets.Count == 0 ? null : targets.Min(), retry);
	}

	private static long IdleMs(SleepSettings settings) => (long)(settings.AfterMinutes * MinuteMs);
}

/// <summary>When a sweep is next due (null: never), and the retry time for held tabs it carries forward.</summary>
public readonly record struct SweepTarget(long? At, long? RetryAt);

/// <summary>What the page reports just before it sleeps or is suspended (src/shared/session/pageProbe.ts).</summary>
public sealed record PageProbe(double ScrollY, bool Dirty, bool LiveStream, bool PictureInPicture)
{
	/// <summary>
	/// The isolated world the probe and <see cref="WorldSource"/> run in: they share the page's DOM but none of its
	/// scripts, so the page can neither see them nor change what they report.
	/// </summary>
	public const string World = "zing-sleep";

	/// <summary>
	/// Runs in <see cref="World"/> as each document is created, in every frame. It notes the text fields and
	/// rich-text editors the person types into, with what they held before the first keystroke. They are held weakly
	/// and dropped once they leave the page, and submitting their form forgets them. A typed field counts even while
	/// hidden (a collapsed reply box, another wizard step): losing a draft is worse than a tab kept awake.
	/// </summary>
	public const string WorldSource = """
		(() => {
			const textTypes = ['text', 'search', 'email', 'url', 'tel', 'password', 'number']
			const textOf = (el) => {
				if (el instanceof HTMLTextAreaElement) return el.value
				if (el instanceof HTMLInputElement) return textTypes.includes(el.type) ? el.value : null
				return el instanceof HTMLElement && el.isContentEditable ? el.textContent : null
			}
			const before = new WeakMap()
			const edited = new Set()
			const prune = () => {
				for (const ref of edited) if (!ref.deref()?.isConnected) edited.delete(ref)
			}
			const typing = (e) => {
				if (!e.isTrusted) return
				prune()
				const el = e.composedPath().find((node) => node instanceof Element)
				if (!el || before.has(el)) return
				const text = textOf(el)
				if (text === null) return
				before.set(el, text)
				edited.add(new WeakRef(el))
			}
			const submitted = (e) => {
				for (const ref of edited) {
					const el = ref.deref()
					if (el && (el.form === e.target || e.target.contains(el))) {
						edited.delete(ref)
						before.delete(el)
					}
				}
			}
			const listen = () => {
				addEventListener('beforeinput', typing, true)
				addEventListener('submit', submitted, true)
			}
			listen()
			// document.open() (editors that write their frame, e.g. CKEditor 4, TinyMCE) erases the window's listeners and
			// replaces the document's children; adding the same listeners again is a no-op when they are still there.
			new MutationObserver(listen).observe(document, { childList: true })
			globalThis.zingSleep = {
				textOf,
				typed: () => {
					prune()
					return [...edited].some((ref) => {
						const el = ref.deref()
						const text = textOf(el)
						return text !== null && text.trim() !== '' && text !== before.get(el)
					})
				},
			}
		})()
		""";

	/// <summary>
	/// Runs in <see cref="World"/> of the page and of each same-origin frame just before the tab sleeps. A draft is
	/// unsaved while a field or editor the person typed into holds text that differs from what it held before, or a
	/// visible text field holds text that differs from what the markup gave it. Typing is the only sign in a React form, which keeps value and defaultValue
	/// equal. Checkboxes, radio buttons, selects, sliders and pickers never count. A live stream is a video or audio
	/// element playing a live camera, mic, screen or call track; picture-in-picture includes Document PiP.
	/// </summary>
	public const string Source = """
		(() => {
			const watch = globalThis.zingSleep
			const fromMarkup = !!watch && [...document.querySelectorAll('input, textarea')].some((el) => {
				const text = watch.textOf(el)
				return text !== null && text.trim() !== '' && text !== el.defaultValue && el.checkVisibility()
			})
			const live = (media) => media.srcObject instanceof MediaStream && media.srcObject.getTracks().some((track) => track.readyState === 'live')
			return {
				scrollY: Math.round(scrollY),
				dirty: fromMarkup || !!watch?.typed(),
				liveStream: [...document.querySelectorAll('video, audio')].some(live),
				pictureInPicture: document.pictureInPictureElement !== null || !!globalThis.documentPictureInPicture?.window,
			}
		})()
		""";

	/// <summary>Reads one frame's answer; anything but {scrollY: finite number, and dirty, liveStream, pictureInPicture as bools} is refused.</summary>
	public static PageProbe Parse(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Object) throw new PageProbeException(value);
		if (!value.TryGetProperty("scrollY", out var scroll) || scroll.ValueKind != JsonValueKind.Number) throw new PageProbeException(value);
		if (!scroll.TryGetDouble(out var y) || !double.IsFinite(y)) throw new PageProbeException(value);
		return new PageProbe(Math.Max(0, y), Flag(value, "dirty"), Flag(value, "liveStream"), Flag(value, "pictureInPicture"));
	}

	/// <summary>The page's answer from its frames' (the top frame first): its scroll position, and what any frame reported.</summary>
	public static PageProbe Combine(IReadOnlyList<PageProbe> frames)
	{
		if (frames.Count == 0) throw new ArgumentException("A page has at least its top frame", nameof(frames));
		return new PageProbe(frames[0].ScrollY, frames.Any(f => f.Dirty), frames.Any(f => f.LiveStream), frames.Any(f => f.PictureInPicture));
	}

	/// <summary>Why the page must stay running, or null if nothing it reported holds it.</summary>
	public KeepAwakeReason? KeepAwake()
	{
		// Dropping the page would end the call or capture, or close the floating video.
		if (LiveStream) return KeepAwakeReason.LiveStream;
		if (PictureInPicture) return KeepAwakeReason.PictureInPicture;
		// Discarding would throw away what the user typed.
		if (Dirty) return KeepAwakeReason.UnsavedForm;
		return null;
	}

	private static bool Flag(JsonElement value, string name)
	{
		if (!value.TryGetProperty(name, out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new PageProbeException(value);
		return flag.GetBoolean();
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
	/// <summary>Audio, live streams and unsaved input have no event that says "now safe", so blocked tabs are looked at again after this.</summary>
	public const long RetryMs = 5 * 60_000;
	private const int MaxThumbnails = 64;

	private readonly ILifecycleTabs _tabs;
	private readonly Func<SleepSettings> _settings;
	private readonly IClock _clock;
	private readonly OneShotTimer _timer;
	private readonly Action<Exception> _onError;
	private readonly Dictionary<string, double> _scroll = [];
	private readonly OrderedDictionary<string, string> _thumbnails = [];
	/// <summary>Tabs held awake last sweep by what their page reported (<see cref="PageProbe.KeepAwake"/>) or a page that couldn't be inspected.</summary>
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
		// A dialog waits for the person, and a stuck page can't be read: dropping either could lose what it holds.
		if (_tabs.IsBlocked(id))
		{
			_held.Add(id);
			return;
		}
		PageProbe probe;
		try
		{
			probe = await _tabs.ProbeAsync(id);
		}
		catch (PageUnavailableException) when (Find(id) is null || !_tabs.HasPage(id))
		{
			// The tab closed (auto-close, the person) or lost its page while it was read: nothing left to sleep.
			return;
		}
		catch (PageUnavailableException error)
		{
			// The page navigated or crashed mid-probe; its state is unknown, so it stays awake.
			_held.Add(id);
			_onError(error);
			return;
		}
		if (probe.KeepAwake() is not null)
		{
			_held.Add(id);
			return;
		}
		var thumbnail = await _tabs.CaptureThumbnailAsync(id);
		// Awaits above let the user come back to the tab, or the page die.
		var after = Find(id);
		if (after is null || after.LastActiveAt != before.LastActiveAt) return;
		if (_tabs.IsShown(id) || !_tabs.HasPage(id) || _tabs.IsAudible(id)) return;
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
			.Select(t => CandidateOf(_tabs, t))
			.ToList();

	internal static SleepCandidate CandidateOf(ILifecycleTabs tabs, TabRecord t) => new(
		t.Id,
		t.LastActiveAt,
		t.Pinned,
		t.Id == tabs.ActiveId,
		VisibleInSplit: t.Id != tabs.ActiveId && tabs.IsShown(t.Id),
		tabs.IsAudible(t.Id),
		HasUnsavedForm: false,
		Asleep: false);

	private long? NextTarget()
	{
		var target = SleepRules.SweepTarget(Candidates(), _clock.Now(), _settings(), _held, _retryAt);
		_retryAt = target.RetryAt;
		return target.At;
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

/// <summary>
/// Suspends the renderer of a tab left hidden for a few minutes (WebView2's TrySuspendAsync), long before it sleeps:
/// the page stays, so it comes back at once when shown. The tabs sleep spares are spared here too, and with sleeping
/// off nothing is suspended.
/// </summary>
public sealed class TabSuspender
{
	public const double AfterMinutes = 5;

	private readonly ILifecycleTabs _tabs;
	private readonly Func<SleepSettings> _settings;
	private readonly IClock _clock;
	private readonly OneShotTimer _timer;
	private readonly Action<Exception> _onError;
	/// <summary>Tabs held awake last sweep by what their page reported, a page that couldn't be inspected, or the engine's refusal.</summary>
	private readonly HashSet<string> _held = [];
	private long? _retryAt;
	private bool _sweeping;

	public TabSuspender(ILifecycleTabs tabs, Func<SleepSettings> settings, IClock clock, IScheduler scheduler, Action<Exception> onError)
	{
		_tabs = tabs;
		_settings = settings;
		_clock = clock;
		_onError = onError;
		_timer = new OneShotTimer(clock, scheduler, OnTimer);
	}

	/// <summary>Recomputes the single wake-up. Cheap enough to call on every tab change.</summary>
	public void Reschedule()
	{
		_held.RemoveWhere(id => !_tabs.HasPage(id));
		if (_sweeping) return;
		var target = SleepRules.SweepTarget(Candidates(), _clock.Now(), Settings(), _held, _retryAt);
		_retryAt = target.RetryAt;
		_timer.ArmAt(target.At);
	}

	public void Rearm()
	{
		_timer.Cancel();
		Reschedule();
	}

	public void Cancel() => _timer.Cancel();

	/// <summary>The sleep rules with the shorter wait. With sleep as soon or sooner nothing is suspended: the tab sleeps instead.</summary>
	private SleepSettings Settings()
	{
		var settings = _settings();
		if (settings.AfterMinutes <= AfterMinutes) return settings with { Enabled = false };
		return settings with { AfterMinutes = AfterMinutes };
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
			foreach (var id in SleepRules.TabsToSleep(Candidates(), _clock.Now(), Settings()))
			{
				if (!await TrySuspend(id)) _held.Add(id);
			}
		}
		finally
		{
			_sweeping = false;
			Reschedule();
		}
	}

	/// <summary>False when the tab stays running for a reason no event will clear, so it is looked at again after a while.</summary>
	private async Task<bool> TrySuspend(string id)
	{
		var before = Find(id);
		if (before is null || !_tabs.HasPage(id)) return true;
		if (_tabs.IsBlocked(id)) return false;
		PageProbe probe;
		try
		{
			probe = await _tabs.ProbeAsync(id);
		}
		catch (PageUnavailableException) when (Find(id) is null || !_tabs.HasPage(id))
		{
			return true;
		}
		catch (PageUnavailableException error)
		{
			_onError(error);
			return false;
		}
		if (probe.KeepAwake() is not null) return false;
		// The await let the person come back to the tab, or the page go; the reschedule after the sweep looks again.
		var after = Find(id);
		if (after is null || after.LastActiveAt != before.LastActiveAt) return true;
		if (_tabs.IsShown(id) || !_tabs.HasPage(id) || _tabs.IsAudible(id)) return true;
		return await _tabs.SuspendAsync(id);
	}

	private TabRecord? Find(string id) => _tabs.Records.FirstOrDefault(t => t.Id == id);

	/// <summary>Tabs whose renderer runs: a suspended one is done, and one without a page is asleep or never loaded.</summary>
	private List<SleepCandidate> Candidates() =>
		_tabs.Records
			.Where(t => _tabs.HasPage(t.Id) && !_tabs.IsSuspended(t.Id))
			.Select(t => TabSleeper.CandidateOf(_tabs, t))
			.ToList();
}
