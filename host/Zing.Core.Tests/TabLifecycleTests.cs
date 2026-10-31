using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>A clock tests move by hand; <see cref="Jump"/> also tells listeners, like a system clock change.</summary>
public sealed class TestClock(long start) : IClock
{
	private readonly List<Action> _listeners = [];

	public long Value { get; set; } = start;

	public long Now() => Value;

	public IDisposable OnAdvance(Action listener)
	{
		_listeners.Add(listener);
		return new Remover(() => _listeners.Remove(listener));
	}

	public void Jump(long ms)
	{
		Value += ms;
		foreach (var listener in _listeners.ToArray()) listener();
	}

	private sealed class Remover(Action remove) : IDisposable
	{
		public void Dispose() => remove();
	}
}

/// <summary>A clock and scheduler that move together.</summary>
public sealed class TestTime(long start)
{
	public TestClock Clock { get; } = new(start);
	public ManualScheduler Scheduler { get; } = new();

	public void Advance(long ms)
	{
		while (ms > 0)
		{
			// One second at a time while a timer is pending, so it fires close to its due time.
			var step = Scheduler.PendingCount == 0 ? ms : Math.Min(ms, 1000);
			Clock.Value += step;
			Scheduler.Advance(TimeSpan.FromMilliseconds(step));
			ms -= step;
		}
	}
}

public sealed class PageSim
{
	public bool Audible { get; set; }
	public bool Dirty { get; set; }
	public double ScrollY { get; set; }
	public bool ProbeFails { get; set; }
	public Action? OnProbe { get; set; }
}

public sealed class FakeLifecycleTabs : ILifecycleTabs
{
	private readonly List<TabRecord> _records = [];

	public string? ActiveId { get; set; }
	public IReadOnlyList<TabRecord> Records => _records;
	public Dictionary<string, PageSim> Pages { get; } = [];
	public List<string> Slept { get; } = [];
	public List<string> Closed { get; } = [];
	/// <summary>What the real host does after a tab changes: tell the lifecycle.</summary>
	public Action Changed { get; set; } = () => { };

	public void Add(string id, long lastActiveAt, bool pinned = false, PageSim? page = null)
	{
		_records.Add(new TabRecord(id, $"https://{id}.test/", id, pinned, lastActiveAt, lastActiveAt, lastActiveAt));
		if (page is not null) Pages[id] = page;
	}

	public void Touch(string id, long at)
	{
		var i = _records.FindIndex(t => t.Id == id);
		_records[i] = _records[i] with { LastActiveAt = at };
	}

	public bool HasPage(string id) => Pages.ContainsKey(id);

	public bool IsAudible(string id) => Pages[id].Audible;

	public Task<PageProbe> ProbeAsync(string id)
	{
		var page = Pages[id];
		page.OnProbe?.Invoke();
		if (page.ProbeFails) return Task.FromException<PageProbe>(new PageUnavailableException(id));
		return Task.FromResult(new PageProbe(page.ScrollY, page.Dirty));
	}

	public Task<string?> CaptureThumbnailAsync(string id) => Task.FromResult<string?>(null);

	public void Sleep(string id)
	{
		Slept.Add(id);
		Pages.Remove(id);
		Changed();
	}

	public void Close(string id)
	{
		Closed.Add(id);
		_records.RemoveAll(t => t.Id == id);
		Pages.Remove(id);
		Changed();
	}
}

public class TabLifecycleTests
{
	private static readonly JsonElement V = Vectors.Load("src/shared/browser/tabLifecycle.vectors.json");
	private static readonly JsonElement Schedule = Vectors.Load("src/shared/session/autoCloseSchedule.vectors.json");
	private static readonly JsonElement SleepVectors = Vectors.Load("src/shared/session/sleepSettings.vectors.json");
	private static readonly JsonElement ProbeVectors = Vectors.Load("src/shared/session/pageProbe.vectors.json");

	private const long Minute = 60_000;
	private static readonly long Now = V.GetProperty("now").GetInt64();
	private static readonly long ScenarioStart = Parse(V.GetProperty("scenarioStart").GetString()!);

	private static long Parse(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

	private static TheoryData<string> Names(JsonElement array)
	{
		var data = new TheoryData<string>();
		foreach (var e in array.EnumerateArray()) data.Add(e.GetProperty("name").GetString()!);
		return data;
	}

	private static JsonElement Case(JsonElement array, string name) =>
		array.EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);

	public static TheoryData<string> AutoCloseNames() => Names(V.GetProperty("autoClose"));
	public static TheoryData<string> ScheduledSweepNames() => Names(V.GetProperty("scheduledSweep"));
	public static TheoryData<string> ScheduleElapsedNames() => Names(V.GetProperty("scheduleElapsed").GetProperty("cases"));
	public static TheoryData<string> IntervalNames() => Names(V.GetProperty("intervals"));
	public static TheoryData<string> KeepAwakeNames() => Names(V.GetProperty("keepAwake").GetProperty("cases"));
	public static TheoryData<string> NextSleepCheckNames() => Names(V.GetProperty("keepAwake").GetProperty("nextSleepCheck"));
	public static TheoryData<string> NextDailyFireNames() => Names(Schedule.GetProperty("nextDailyFire"));
	public static TheoryData<string> NextCheckNames() => Names(Schedule.GetProperty("nextAutoCloseCheck"));
	public static TheoryData<string> AutoCloserNames() => Names(V.GetProperty("autoCloser"));
	public static TheoryData<string> SleeperNames() => Names(V.GetProperty("sleeper"));

	// ---- policy and tab-list helpers ----

	private static AutoClosePolicy PolicyOf(JsonElement? changes, bool enabled)
	{
		var policy = AutoClosePolicy.Default with { Enabled = enabled };
		if (changes is not { } c) return policy;
		foreach (var p in c.EnumerateObject())
		{
			policy = p.Name switch
			{
				"enabled" => policy with { Enabled = p.Value.GetBoolean() },
				"rule" => policy with { Rule = AutoClosePolicy.ParseRule(p.Value.GetString()!) },
				"customHours" => policy with { CustomHours = p.Value.GetDouble() },
				"atTimeEnabled" => policy with { AtTimeEnabled = p.Value.GetBoolean() },
				"hour" => policy with { Hour = p.Value.GetInt32() },
				"minute" => policy with { Minute = p.Value.GetInt32() },
				_ => throw new InvalidDataException($"Unknown policy field {p.Name}"),
			};
		}
		return policy;
	}

	private static JsonElement? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v : null;

	private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.GetBoolean();

	private static JsonElement TabList(JsonElement root, JsonElement specs) =>
		specs.ValueKind == JsonValueKind.String ? root.GetProperty("tabSets").GetProperty(specs.GetString()!) : specs;

	private static List<TabSnapshot> AgedSnapshots(JsonElement specs) =>
		TabList(V, specs).EnumerateArray()
			.Select(t => new TabSnapshot(t.GetProperty("id").GetString()!, Now - t.GetProperty("minutesAgo").GetInt64() * Minute, Flag(t, "pinned"), Flag(t, "active")))
			.ToList();

	private static List<TabSnapshot> MsSnapshots(JsonElement specs, long now) =>
		TabList(Schedule, specs).EnumerateArray()
			.Select(t => new TabSnapshot(t.GetProperty("id").GetString()!, now - t.GetProperty("agoMs").GetInt64(), Flag(t, "isPinned"), Flag(t, "isActive")))
			.ToList();

	private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

	// ---- pure rules ----

	[Theory]
	[MemberData(nameof(AutoCloseNames))]
	public void TabsToCloseFollowsThePolicy(string name)
	{
		var c = Case(V.GetProperty("autoClose"), name);
		var closing = AutoClose.TabsToClose(AgedSnapshots(c.GetProperty("tabs")), PolicyOf(c.GetProperty("policy"), false), Now).Order().ToArray();
		if (c.TryGetProperty("expect", out var want)) Assert.Equal(Strings(want), closing);
		if (c.TryGetProperty("expectCount", out var count)) Assert.Equal(count.GetInt32(), closing.Length);
	}

	[Theory]
	[MemberData(nameof(ScheduledSweepNames))]
	public void ScheduledSweepSparesTheSameTabs(string name)
	{
		var c = Case(V.GetProperty("scheduledSweep"), name);
		Assert.Equal(Strings(c.GetProperty("expect")), AutoClose.TabsToCloseOnSchedule(AgedSnapshots(c.GetProperty("tabs"))).Order());
	}

	[Theory]
	[MemberData(nameof(ScheduleElapsedNames))]
	public void ScheduleElapsedAsksWhetherTheInstantFellInTheWindow(string name)
	{
		var section = V.GetProperty("scheduleElapsed");
		var c = Case(section.GetProperty("cases"), name);
		var policy = PolicyOf(section.GetProperty("policy"), false);
		if (c.TryGetProperty("policyChanges", out var changes)) policy = PolicyOf(changes, policy.Enabled) with { Rule = policy.Rule, Hour = policy.Hour, Minute = policy.Minute };
		var elapsed = AutoClose.ScheduleElapsed(policy, Parse(c.GetProperty("since").GetString()!), Parse(c.GetProperty("until").GetString()!), TimeZoneCalendar.Utc);
		Assert.Equal(c.GetProperty("expect").GetBoolean(), elapsed);
	}

	[Fact]
	public void IntervalLabelsReadNaturally()
	{
		foreach (var c in V.GetProperty("intervalLabels").EnumerateArray())
		{
			var policy = AutoClosePolicy.Default with { Enabled = true, Rule = AutoCloseRule.Custom, CustomHours = c.GetProperty("hours").GetDouble() };
			Assert.Equal(c.GetProperty("expect").GetString(), policy.DescribedInterval());
		}
	}

	[Theory]
	[MemberData(nameof(IntervalNames))]
	public void IntervalMatchesTheRule(string name)
	{
		var c = Case(V.GetProperty("intervals"), name);
		Assert.Equal(c.GetProperty("expectMs").GetInt64(), PolicyOf(c.GetProperty("policy"), false).Interval());
	}

	private static SleepSettings SettingsOf(JsonElement baseSettings, JsonElement? changes)
	{
		var merged = Merge(baseSettings, changes);
		return new SleepSettings(merged["enabled"]!.GetValue<bool>(), merged["afterMinutes"]!.GetValue<double>(), merged["sleepPinned"]!.GetValue<bool>());
	}

	private static JsonObject Merge(JsonElement baseObject, JsonElement? changes)
	{
		var merged = JsonNode.Parse(baseObject.GetRawText())!.AsObject();
		if (changes is not { } c) return merged;
		foreach (var p in c.EnumerateObject()) merged[p.Name] = JsonNode.Parse(p.Value.GetRawText());
		return merged;
	}

	private static SleepCandidate CandidateOf(JsonElement tab, JsonElement? overrides)
	{
		var t = Merge(V.GetProperty("keepAwake").GetProperty("base"), overrides is { } o ? o : tab);
		if (overrides is not null) foreach (var p in tab.EnumerateObject()) t[p.Name] = JsonNode.Parse(p.Value.GetRawText());
		return new SleepCandidate(
			t["id"]!.GetValue<string>(),
			Now - (long)(t["lastActiveAgoMinutes"]!.GetValue<double>() * Minute),
			t["pinned"]!.GetValue<bool>(),
			t["isActive"]!.GetValue<bool>(),
			t["visibleInSplit"]!.GetValue<bool>(),
			t["playingAudio"]!.GetValue<bool>(),
			t["hasUnsavedForm"]!.GetValue<bool>(),
			t["asleep"]!.GetValue<bool>());
	}

	private static SleepCandidate CandidateOf(JsonElement tab) => CandidateOf(tab, null);

	private static string Kebab(KeepAwakeReason reason) => Regex.Replace(reason.ToString(), "(?<!^)([A-Z])", "-$1").ToLowerInvariant();

	[Theory]
	[MemberData(nameof(KeepAwakeNames))]
	public void KeepAwakeSaysWhy(string name)
	{
		var keep = V.GetProperty("keepAwake");
		var c = Case(keep.GetProperty("cases"), name);
		var settings = SettingsOf(keep.GetProperty("settings"), Optional(c, "settingsChanges"));
		var tab = c.TryGetProperty("tab", out var changes) ? CandidateOf(changes, new JsonElement?()) : CandidateOf(JsonDocument.Parse("{}").RootElement);
		var reason = SleepRules.KeepAwake(tab, Now, settings);
		var want = c.GetProperty("expect");
		Assert.Equal(want.ValueKind == JsonValueKind.Null ? null : want.GetString(), reason is { } r ? Kebab(r) : null);
	}

	[Fact]
	public void TabsToSleepPicksTheIdleUnprotectedTabs()
	{
		var keep = V.GetProperty("keepAwake");
		var section = keep.GetProperty("tabsToSleep");
		var tabs = section.GetProperty("tabs").EnumerateArray().Select(CandidateOf).ToList();
		var settings = SettingsOf(keep.GetProperty("settings"), null);
		Assert.Equal(Strings(section.GetProperty("expect")), SleepRules.TabsToSleep(tabs, Now, settings));
	}

	[Theory]
	[MemberData(nameof(NextSleepCheckNames))]
	public void NextSleepCheckIsTheSoonestRecentlyUsedTab(string name)
	{
		var keep = V.GetProperty("keepAwake");
		var c = Case(keep.GetProperty("nextSleepCheck"), name);
		var settings = SettingsOf(keep.GetProperty("settings"), Optional(c, "settingsChanges"));
		var next = SleepRules.NextSleepCheck(c.GetProperty("tabs").EnumerateArray().Select(CandidateOf).ToList(), Now, settings);
		var want = c.GetProperty("expectInMinutes");
		Assert.Equal(want.ValueKind == JsonValueKind.Null ? null : (long?)(want.GetDouble() * Minute), next - Now);
	}

	[Theory]
	[MemberData(nameof(NextDailyFireNames))]
	public void NextDailyFireIsStrictlyAfterNow(string name)
	{
		var c = Case(Schedule.GetProperty("nextDailyFire"), name);
		var now = Parse(Schedule.GetProperty("now").GetString()!);
		Assert.Equal(now + c.GetProperty("expectOffsetMs").GetInt64(), AutoClose.NextDailyFire(PolicyOf(c.GetProperty("policy"), true), now, TimeZoneCalendar.Utc));
	}

	[Theory]
	[MemberData(nameof(NextCheckNames))]
	public void NextCheckIsTheSoonestSweepThatCouldDoSomething(string name)
	{
		var c = Case(Schedule.GetProperty("nextAutoCloseCheck"), name);
		var now = Parse(Schedule.GetProperty("now").GetString()!);
		var next = AutoClose.NextCheck(MsSnapshots(c.GetProperty("tabs"), now), PolicyOf(c.GetProperty("policy"), true), now, TimeZoneCalendar.Utc);
		var want = c.GetProperty("expectOffsetMs");
		Assert.Equal(want.ValueKind == JsonValueKind.Null ? null : now + want.GetInt64(), next);
	}

	// ---- settings and probe ----

	[Fact]
	public void SleepSettingsAcceptAndRefuseTheSameValuesAsTypeScript()
	{
		var expected = SleepVectors.GetProperty("default");
		Assert.Equal(expected.GetProperty("afterMinutes").GetDouble(), SleepSettings.Default.AfterMinutes);
		Assert.True(SleepSettings.IsValid(JsonNode.Parse(expected.GetRawText())));
		foreach (var v in SleepVectors.GetProperty("valid").EnumerateArray()) Assert.True(SleepSettings.IsValid(JsonNode.Parse(v.GetRawText())), v.GetRawText());
		foreach (var v in SleepVectors.GetProperty("invalid").EnumerateArray()) Assert.False(SleepSettings.IsValid(JsonNode.Parse(v.GetRawText())), v.GetRawText());
	}

	[Fact]
	public void SleepSettingsFromNodeRefusesInvalidValues()
	{
		Assert.Equal(new SleepSettings(false, 5, true), SleepSettings.FromNode(JsonNode.Parse("""{"enabled":false,"afterMinutes":5,"sleepPinned":true}""")));
		Assert.Throws<InvalidSettingException>(() => SleepSettings.FromNode(JsonNode.Parse("""{"enabled":true,"afterMinutes":0,"sleepPinned":false}""")));
	}

	[Fact]
	public void DefaultSettingsHoldTheLifecycleKeys()
	{
		var settings = new SettingsStore();
		Assert.Equal(AutoClosePolicy.Default, AutoClosePolicy.FromSettings(settings));
		Assert.Equal(SleepSettings.Default, SleepSettings.FromNode(settings.Get("sleepingTabs")));
	}

	[Fact]
	public void PageProbeParsesWhatTheScriptReturns()
	{
		foreach (var c in ProbeVectors.GetProperty("accepted").EnumerateArray())
		{
			var probe = PageProbe.Parse(c.GetProperty("input"));
			var want = c.GetProperty("expect");
			Assert.Equal(new PageProbe(want.GetProperty("scrollY").GetDouble(), want.GetProperty("dirty").GetBoolean()), probe);
		}
	}

	[Fact]
	public void PageProbeRefusesAnythingElse()
	{
		foreach (var v in ProbeVectors.GetProperty("rejected").EnumerateArray()) Assert.Throws<PageProbeException>(() => PageProbe.Parse(v));
	}

	// ---- calendars ----

	[Fact]
	public void DailyTimeInASpringForwardGapRollsForward()
	{
		var ny = new TimeZoneCalendar(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
		var dayStart = ny.StartOfDay(Parse("2026-03-08T12:00:00Z"));
		Assert.Equal(Parse("2026-03-08T05:00:00Z"), dayStart);
		Assert.Equal(Parse("2026-03-08T07:30:00Z"), ny.AtTime(dayStart, 2, 30));
	}

	[Fact]
	public void AddDaysKeepsTheLocalTimeAcrossDst()
	{
		var ny = new TimeZoneCalendar(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
		Assert.Equal(Parse("2026-03-09T04:00:00Z"), ny.AddDays(Parse("2026-03-08T05:00:00Z"), 1));
	}

	// ---- the timer ----

	[Fact]
	public void OneShotTimerRearmingTheSameTargetKeepsOneWakeUp()
	{
		var time = new TestTime(0);
		var fired = 0;
		var timer = new OneShotTimer(time.Clock, time.Scheduler, () => fired++);
		timer.ArmAt(10_000);
		timer.ArmAt(10_000);
		Assert.Equal(1, time.Scheduler.PendingCount);
		timer.ArmAt(20_000);
		Assert.Equal(1, time.Scheduler.PendingCount);
		time.Advance(19_999);
		Assert.Equal(0, fired);
		time.Advance(1);
		Assert.Equal(1, fired);
		Assert.Equal(0, time.Scheduler.PendingCount);
	}

	[Fact]
	public void OneShotTimerCancelAndNullTargetDisarm()
	{
		var time = new TestTime(0);
		var timer = new OneShotTimer(time.Clock, time.Scheduler, () => throw new InvalidOperationException("fired"));
		timer.ArmAt(1_000);
		timer.ArmAt(null);
		Assert.Equal(0, time.Scheduler.PendingCount);
		timer.ArmAt(1_000);
		timer.Cancel();
		time.Advance(5_000);
	}

	[Fact]
	public void OneShotTimerNeverFiresSoonerThanTheFloorForATargetInThePast()
	{
		var time = new TestTime(100_000);
		var fired = 0;
		new OneShotTimer(time.Clock, time.Scheduler, () => fired++).ArmAt(1);
		time.Advance(OneShotTimer.MinDelayMs - 1);
		Assert.Equal(0, fired);
		time.Advance(1);
		Assert.Equal(1, fired);
	}

	// ---- scenarios ----

	private static bool Is(JsonElement step, string key, out JsonElement value) => step.TryGetProperty(key, out value);

	[Theory]
	[MemberData(nameof(AutoCloserNames))]
	public void AutoCloserScenario(string name)
	{
		var c = Case(V.GetProperty("autoCloser"), name);
		var time = new TestTime(ScenarioStart);
		var tabs = new FakeLifecycleTabs
		{
			ActiveId = c.GetProperty("active").ValueKind == JsonValueKind.Null ? null : c.GetProperty("active").GetString(),
		};
		foreach (var t in c.GetProperty("tabs").EnumerateArray()) tabs.Add(t.GetProperty("id").GetString()!, ScenarioStart, Flag(t, "pinned"));
		var policy = PolicyOf(c.GetProperty("policy"), true);
		var closer = new TabAutoCloser(tabs, () => policy, time.Clock, time.Scheduler, TimeZoneCalendar.Utc);
		tabs.Changed = closer.Reschedule;
		closer.Reschedule();

		foreach (var step in c.GetProperty("steps").EnumerateArray())
		{
			if (Is(step, "advanceMinutes", out var minutes)) time.Advance(minutes.GetInt64() * Minute);
			else if (Is(step, "touch", out var touched)) tabs.Touch(touched.GetString()!, time.Clock.Now());
			else if (Is(step, "policy", out var changes))
			{
				policy = PolicyOf(changes, true);
				closer.Reschedule();
			}
			else if (Is(step, "reschedule", out var times)) for (var i = 0; i < times.GetInt32(); i++) closer.Reschedule();
			else if (Is(step, "expectClosed", out var closed)) Assert.Equal(Strings(closed), tabs.Closed);
			else if (Is(step, "expectRemaining", out var remaining)) Assert.Equal(Strings(remaining), tabs.Records.Select(t => t.Id));
			else if (Is(step, "expectRemainingCount", out var count)) Assert.Equal(count.GetInt32(), tabs.Records.Count);
			else if (Is(step, "expectTimers", out var timers)) Assert.Equal(timers.GetInt32(), time.Scheduler.PendingCount);
			else throw new InvalidDataException($"Unknown step {step.GetRawText()}");
		}
	}

	[Theory]
	[MemberData(nameof(SleeperNames))]
	public void SleeperScenario(string name)
	{
		var c = Case(V.GetProperty("sleeper"), name);
		var time = new TestTime(ScenarioStart);
		var tabs = new FakeLifecycleTabs { ActiveId = c.GetProperty("active").GetString() };
		foreach (var t in c.GetProperty("tabs").EnumerateArray())
		{
			var id = t.GetProperty("id").GetString()!;
			var idle = t.TryGetProperty("idleMinutes", out var m) ? m.GetInt64() * Minute : 0;
			var page = new PageSim
			{
				Audible = Flag(t, "audible"),
				Dirty = Flag(t, "dirty"),
				ProbeFails = Flag(t, "probeFails"),
				ScrollY = t.TryGetProperty("scrollY", out var y) ? y.GetDouble() : 0,
			};
			if (Flag(t, "touchOnProbe")) page.OnProbe = () => tabs.Touch(id, time.Clock.Now());
			tabs.Add(id, ScenarioStart - idle, Flag(t, "pinned"), page);
		}
		var errors = new List<Exception>();
		var sleeper = new TabSleeper(tabs, () => SleepSettings.Default, time.Clock, time.Scheduler, errors.Add);
		tabs.Changed = sleeper.Reschedule;
		sleeper.Reschedule();

		foreach (var step in c.GetProperty("steps").EnumerateArray())
		{
			if (Is(step, "advanceMinutes", out var minutes)) time.Advance(minutes.GetInt64() * Minute);
			else if (Is(step, "advanceMs", out var ms)) time.Advance(ms.GetInt64());
			else if (Is(step, "reschedule", out var times)) for (var i = 0; i < times.GetInt32(); i++) sleeper.Reschedule();
			else if (Is(step, "setAudible", out var audible)) tabs.Pages[audible.GetString()!].Audible = step.GetProperty("value").GetBoolean();
			else if (Is(step, "setDirty", out var dirty)) tabs.Pages[dirty.GetString()!].Dirty = step.GetProperty("value").GetBoolean();
			else if (Is(step, "setProbeFails", out var fails)) tabs.Pages[fails.GetString()!].ProbeFails = step.GetProperty("value").GetBoolean();
			else if (Is(step, "expectSlept", out var slept)) Assert.Equal(Strings(slept), tabs.Slept);
			else if (Is(step, "expectTimers", out var timers)) Assert.Equal(timers.GetInt32(), time.Scheduler.PendingCount);
			else if (Is(step, "expectErrors", out var count)) Assert.Equal(count.GetInt32(), errors.Count);
			else if (Is(step, "expectWakeScroll", out var wake))
			{
				var want = step.GetProperty("value");
				Assert.Equal(want.ValueKind == JsonValueKind.Null ? null : want.GetDouble(), sleeper.TakeScroll(wake.GetString()!));
			}
			else throw new InvalidDataException($"Unknown step {step.GetRawText()}");
		}
		Assert.All(errors, e => Assert.IsType<PageUnavailableException>(e));
	}

	// ---- wiring ----

	private sealed class Wired
	{
		public TestTime Time { get; } = new(ScenarioStart);
		public FakeLifecycleTabs Tabs { get; } = new() { ActiveId = "a" };
		public SettingsStore Settings { get; } = new();
		public List<Exception> Errors { get; } = [];
		public TabLifecycle Lifecycle { get; }

		public Wired()
		{
			Tabs.Add("a", ScenarioStart, page: new PageSim());
			Tabs.Add("b", ScenarioStart, page: new PageSim());
			Lifecycle = new TabLifecycle(Tabs, Settings, Time.Clock, Time.Scheduler, Errors.Add, TimeZoneCalendar.Utc);
			Tabs.Changed = Lifecycle.Reschedule;
		}
	}

	[Fact]
	public void TabLifecycleArmsSleepOnlyUntilAutoCloseIsSwitchedOn()
	{
		var w = new Wired();
		Assert.Equal(1, w.Time.Scheduler.PendingCount);
		w.Settings.Set("autoCloseEnabled", true);
		Assert.Equal(2, w.Time.Scheduler.PendingCount);
		w.Settings.Set("sleepingTabs", JsonNode.Parse("""{"enabled":false,"afterMinutes":30,"sleepPinned":false}"""));
		Assert.Equal(1, w.Time.Scheduler.PendingCount);
	}

	[Fact]
	public void TabLifecycleSleepsAndClosesIdleTabsInTime()
	{
		var w = new Wired();
		w.Settings.Set("autoCloseEnabled", true);
		w.Settings.Set("autoCloseRule", "hour");
		w.Time.Advance(31 * Minute);
		Assert.Equal(["b"], w.Tabs.Slept);
		Assert.Empty(w.Tabs.Closed);
		w.Time.Advance(31 * Minute);
		Assert.Equal(["b"], w.Tabs.Closed);
		Assert.Empty(w.Errors);
	}

	[Fact]
	public void TabLifecycleRecomputesAfterTheClockJumps()
	{
		var w = new Wired();
		w.Time.Clock.Jump(2 * 60 * Minute);
		w.Time.Scheduler.Advance(TimeSpan.FromMilliseconds(OneShotTimer.MinDelayMs));
		Assert.Equal(["b"], w.Tabs.Slept);
	}

	[Fact]
	public void TabLifecycleDisposeCancelsEverything()
	{
		var w = new Wired();
		w.Settings.Set("autoCloseEnabled", true);
		w.Lifecycle.Dispose();
		Assert.Equal(0, w.Time.Scheduler.PendingCount);
		w.Settings.Set("autoCloseRule", "hour");
		w.Time.Clock.Jump(Minute);
		Assert.Equal(0, w.Time.Scheduler.PendingCount);
	}

	[Fact]
	public void TabLifecycleRemembersAThumbnaillessScrollForTheWakeOnly()
	{
		var w = new Wired();
		w.Tabs.Pages["b"].ScrollY = 250;
		w.Time.Advance(31 * Minute);
		Assert.Null(w.Lifecycle.Thumbnail("b"));
		Assert.Equal(250, w.Lifecycle.Woke("b"));
		Assert.Null(w.Lifecycle.Woke("b"));
	}
}
