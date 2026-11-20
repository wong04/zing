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
	Capturing,
	PictureInPicture,
	Uploading,
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
public sealed record PageProbe(double ScrollY, bool Dirty, bool Capturing, bool PictureInPicture, bool Uploading)
{
	/// <summary>
	/// Runs inside a page just before it sleeps, in the page and each same-origin frame. A form counts as unsaved when any
	/// field differs from what the markup gave it, or when the person typed into a field that still holds text: React keeps
	/// value and defaultValue equal, so only the typing shows its drafts. The camera, microphone and screen it captures,
	/// picture-in-picture and uploads come from <see cref="SleepGuards.Source"/> and the engine's own document state.
	/// </summary>
	public const string Source = """
		(() => {
			const key = Symbol.for('zing.sleepGuards')
			const windows = []
			const collect = (w) => {
				// A cross-origin frame's document can't be read (SecurityError); its own frames may still be same-origin.
				try {
					if (w.document) windows.push(w)
				} catch {}
				for (let i = 0; i < w.frames.length; i++) collect(w.frames[i])
			}
			collect(window)
			const edited = (w, el) => {
				if (el instanceof w.HTMLTextAreaElement) return el.value !== el.defaultValue
				if (el instanceof w.HTMLSelectElement) {
					if (el.multiple) return [...el.options].some((o) => o.selected !== o.defaultSelected)
					const expected = Math.max(0, [...el.options].findIndex((o) => o.defaultSelected))
					return el.selectedIndex !== expected
				}
				if (!(el instanceof w.HTMLInputElement)) return false
				if (['hidden', 'submit', 'button', 'reset', 'image', 'file'].includes(el.type)) return false
				if (el.type === 'checkbox' || el.type === 'radio') return el.checked !== el.defaultChecked
				return el.value !== el.defaultValue
			}
			const dirty = (w) => {
				const doc = w.document
				const richText = [...doc.querySelectorAll('[contenteditable=""], [contenteditable="true"]')]
					.some((el) => (el.textContent || '').trim() !== '')
				return richText || [...doc.querySelectorAll('input, textarea, select')].some((el) => edited(w, el)) || !!w[key]?.typed()
			}
			return {
				scrollY: Math.round(window.scrollY),
				dirty: windows.some(dirty),
				capturing: windows.some((w) => !!w[key]?.capturing()),
				pictureInPicture: !!window.documentPictureInPicture?.window || windows.some((w) => w.document.pictureInPictureElement !== null),
				uploading: windows.some((w) => !!w[key]?.uploading()),
			}
		})()
		""";

	/// <summary>Reads the script's JSON result; anything but {scrollY: finite number, and the four flags as bools} is refused.</summary>
	public static PageProbe Parse(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Object) throw new PageProbeException(value);
		if (!value.TryGetProperty("scrollY", out var scroll) || scroll.ValueKind != JsonValueKind.Number) throw new PageProbeException(value);
		if (!scroll.TryGetDouble(out var y) || !double.IsFinite(y)) throw new PageProbeException(value);
		return new PageProbe(Math.Max(0, y), Flag(value, "dirty"), Flag(value, "capturing"), Flag(value, "pictureInPicture"), Flag(value, "uploading"));
	}

	/// <summary>Why the page must stay running, or null if nothing it reported holds it.</summary>
	public KeepAwakeReason? KeepAwake()
	{
		// Dropping or freezing the page would end the call or share, close the floating video, or kill the upload.
		if (Capturing) return KeepAwakeReason.Capturing;
		if (PictureInPicture) return KeepAwakeReason.PictureInPicture;
		if (Uploading) return KeepAwakeReason.Uploading;
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

/// <summary>
/// Added to every document as it is created, in every frame, before the page's own scripts: it notes what the probe
/// can't see afterwards. Fields the person typed into (cleared when their form is submitted; a new document starts
/// afresh), the camera, microphone and screen tracks the page was given, and its uploads in progress (XHR or fetch with
/// a file, a stream or a large binary body). Its window property holds only those three answers.
/// </summary>
public static class SleepGuards
{
	public const string Source = """
		(() => {
			const key = Symbol.for('zing.sleepGuards')
			if (Object.prototype.hasOwnProperty.call(window, key)) return
			const typed = new Set()
			const tracks = new Set()
			let uploads = 0
			addEventListener('input', (e) => {
				if (e.isTrusted) typed.add(e.composedPath()[0])
			}, true)
			addEventListener('submit', (e) => {
				for (const el of typed) if (el.form === e.target || e.target.contains(el)) typed.delete(el)
			}, true)
			const holdsInput = (el) => {
				if (el.isContentEditable) return (el.textContent || '').trim() !== ''
				if (el.type === 'checkbox' || el.type === 'radio' || el.tagName === 'SELECT') return true
				return typeof el.value === 'string' && el.value.trim() !== ''
			}
			if (typeof MediaDevices !== 'undefined') {
				for (const name of ['getUserMedia', 'getDisplayMedia']) {
					const original = MediaDevices.prototype[name]
					if (typeof original !== 'function') continue
					MediaDevices.prototype[name] = function (...args) {
						return original.apply(this, args).then((stream) => {
							for (const track of stream.getTracks()) tracks.add(track)
							return stream
						})
					}
				}
				const clone = MediaStreamTrack.prototype.clone
				MediaStreamTrack.prototype.clone = function () {
					const copy = clone.call(this)
					if (tracks.has(this)) tracks.add(copy)
					return copy
				}
			}
			const isUpload = (body) =>
				body instanceof Blob ||
				body instanceof ReadableStream ||
				(body instanceof FormData && [...body.values()].some((v) => v instanceof Blob)) ||
				((body instanceof ArrayBuffer || ArrayBuffer.isView(body)) && body.byteLength >= 65536)
			const send = XMLHttpRequest.prototype.send
			XMLHttpRequest.prototype.send = function (body) {
				if (!isUpload(body)) return send.call(this, body)
				uploads++
				let open = true
				const done = () => {
					if (open) uploads--
					open = false
				}
				this.addEventListener('loadend', done, { once: true })
				try {
					return send.call(this, body)
				} catch (error) {
					done()
					throw error
				}
			}
			const fetch = window.fetch
			window.fetch = function (input, init) {
				const response = fetch.apply(this, arguments)
				if (!isUpload(init?.body)) return response
				uploads++
				const done = () => uploads--
				response.then(done, done)
				return response
			}
			Object.defineProperty(window, key, {
				value: Object.freeze({
					typed: () => {
						for (const el of typed) if (!el.isConnected) typed.delete(el)
						return [...typed].some(holdsInput)
					},
					capturing: () => {
						for (const track of tracks) if (track.readyState === 'ended') tracks.delete(track)
						return tracks.size > 0
					},
					uploading: () => uploads > 0,
				}),
			})
		})()
		""";
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
	/// <summary>Audio, capture, uploads and unsaved input have no event that says "now safe", so blocked tabs are looked at again after this.</summary>
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
