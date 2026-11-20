using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>A beforeunload prompt as the host raises one: answered through the window's dialogs, its outcome the page's.</summary>
internal sealed class FakeLeavePrompt : IScriptDialogAnswer
{
	private readonly TaskCompletionSource<bool> _left = new();
	public Task<bool> Left => _left.Task;
	public void Accept(string? text) => _left.TrySetResult(true);
	public void Dismiss() => _left.TrySetResult(false);

	/// <summary>Makes <paramref name="page"/> ask before it leaves, as a page whose beforeunload handler cancels the event.</summary>
	public static void Ask(FakePage page, ScriptDialogs dialogs) => page.Leaving = _ =>
	{
		var prompt = new FakeLeavePrompt();
		dialogs.Open(page, ScriptDialogKinds.BeforeUnload, page.Url, "", null, prompt);
		return prompt.Left;
	};
}

public sealed class LeaveCheckTests : IDisposable
{
	private readonly FakeWindowHost _host = new() { HoldsBackground = true };
	private readonly SettingsStore _settings = new();
	private readonly Bridge _bridge = new();
	private readonly ChromeApi _api;

	public LeaveCheckTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
		_api.Start([], "about:blank");
	}

	public void Dispose() => _api.Dispose();

	private FakePage PageOf(string id) => _host.FakePages.PageFor(_api.Tabs, id);

	private string OpenShown(string url)
	{
		var record = _api.Tabs.Open(url);
		_api.Tabs.Activate(record.Id);
		PageOf(record.Id).Url = url;
		return record.Id;
	}

	private ScriptDialogPrompt Shown => ShownIn(_api);

	private static ScriptDialogPrompt ShownIn(ChromeApi api) => api.PageUi.State.Dialog ?? throw new Xunit.Sdk.XunitException("No dialog is shown");

	[Fact]
	public void A_tab_whose_page_asks_closes_only_once_the_person_chooses_Leave()
	{
		var id = OpenShown("https://draft.test/");
		FakeLeavePrompt.Ask(PageOf(id), _api.Dialogs);
		_api.CloseTabs([id]);
		Assert.True(_api.Tabs.Has(id));
		Assert.Equal((ScriptDialogKinds.BeforeUnload, "https://draft.test"), (Shown.Kind, Shown.Origin));
		_api.Dialogs.Answer(Shown.Id, true, null);
		Assert.False(_api.Tabs.Has(id));
		Assert.Null(_api.PageUi.State.Dialog);
	}

	[Fact]
	public void Stay_keeps_the_tab_and_its_page()
	{
		var id = OpenShown("https://draft.test/");
		var page = PageOf(id);
		FakeLeavePrompt.Ask(page, _api.Dialogs);
		_api.CloseTabs([id]);
		_api.Dialogs.Answer(Shown.Id, false, null);
		Assert.True(_api.Tabs.Has(id));
		Assert.False(page.Closed);
		Assert.Null(_api.PageUi.State.Dialog);
	}

	[Fact]
	public void A_page_that_asks_nothing_closes_at_once()
	{
		var id = OpenShown("https://plain.test/");
		var page = PageOf(id);
		_api.CloseTabs([id]);
		Assert.False(_api.Tabs.Has(id));
		Assert.Equal(1, page.BeforeUnloads);
	}

	[Fact]
	public void A_page_that_never_answers_closes_after_the_wait()
	{
		var id = OpenShown("https://hung.test/");
		PageOf(id).Leaving = _ => new TaskCompletionSource<bool>().Task;
		_api.CloseTabs([id]);
		Assert.True(_api.Tabs.Has(id));
		_host.FakeScheduler.Advance(LeaveCheck.AnswerWait);
		Assert.False(_api.Tabs.Has(id));
	}

	[Fact]
	public void The_wait_never_cuts_off_a_person_deciding()
	{
		var id = OpenShown("https://draft.test/");
		FakeLeavePrompt.Ask(PageOf(id), _api.Dialogs);
		_api.CloseTabs([id]);
		_host.FakeScheduler.Advance(LeaveCheck.AnswerWait * 10);
		Assert.True(_api.Tabs.Has(id));
		Assert.NotNull(_api.PageUi.State.Dialog);
	}

	[Fact]
	public async Task One_Stay_keeps_the_window_open_and_takes_down_the_other_pages_prompts()
	{
		var first = OpenShown("https://one.test/");
		var second = OpenShown("https://two.test/");
		FakeLeavePrompt.Ask(PageOf(first), _api.Dialogs);
		FakeLeavePrompt.Ask(PageOf(second), _api.Dialogs);
		var closing = _api.MayCloseAsync(CancellationToken.None);
		var asking = Shown;
		_api.Dialogs.Answer(asking.Id, false, null);
		Assert.False(await closing);
		Assert.Null(_api.PageUi.State.Dialog);
		Assert.True(_api.Tabs.Has(first) && _api.Tabs.Has(second));
	}

	[Fact]
	public async Task Leave_on_every_page_lets_the_window_close()
	{
		var first = OpenShown("https://one.test/");
		var second = OpenShown("https://two.test/");
		FakeLeavePrompt.Ask(PageOf(first), _api.Dialogs);
		FakeLeavePrompt.Ask(PageOf(second), _api.Dialogs);
		var closing = _api.MayCloseAsync(CancellationToken.None);
		_api.Dialogs.Answer(Shown.Id, true, null);
		Assert.False(closing.IsCompleted);
		_api.Dialogs.Answer(Shown.Id, true, null);
		Assert.True(await closing);
	}

	[Fact]
	public async Task Another_window_staying_takes_this_windows_prompt_down()
	{
		var id = OpenShown("https://one.test/");
		FakeLeavePrompt.Ask(PageOf(id), _api.Dialogs);
		using var stay = new CancellationTokenSource();
		var closing = _api.MayCloseAsync(stay.Token);
		Assert.NotNull(_api.PageUi.State.Dialog);
		stay.Cancel();
		Assert.False(await closing);
		Assert.Null(_api.PageUi.State.Dialog);
	}

	[Fact]
	public void A_hidden_tab_that_asks_comes_to_the_front()
	{
		var hidden = OpenShown("https://draft.test/");
		var front = OpenShown("https://front.test/");
		FakeLeavePrompt.Ask(PageOf(hidden), _api.Dialogs);
		_api.CloseTabs([hidden, front]);
		Assert.Equal(hidden, _api.Tabs.ActiveId);
		Assert.Null(Shown.Tab);
	}

	[Fact]
	public void Closing_other_tabs_keeps_them_all_when_one_page_stays()
	{
		var keep = OpenShown("https://keep.test/");
		var draft = OpenShown("https://draft.test/");
		FakeLeavePrompt.Ask(PageOf(draft), _api.Dialogs);
		var before = _api.Tabs.Records.Count;
		_api.CloseOtherTabs(keep);
		_api.Dialogs.Answer(Shown.Id, false, null);
		Assert.Equal(before, _api.Tabs.Records.Count);
	}

	[Fact]
	public async Task Close_tab_through_the_bridge_answers_once_the_page_has_and_an_unknown_tab_still_throws()
	{
		var bridge = new Bridge();
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		using var api = new ChromeApi(bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
		api.Start([], "about:blank");
		var stays = api.Tabs.Open("https://draft.test/");
		FakeLeavePrompt.Ask(_host.FakePages.PageFor(api.Tabs, stays.Id), api.Dialogs);
		var staying = bridge.HandleAsync(JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "tabs:close", args = new[] { stays.Id } }));
		Assert.False(staying.IsCompleted);
		api.Dialogs.Answer(ShownIn(api).Id, false, null);
		Assert.True(JsonDocument.Parse(await staying).RootElement.GetProperty("ok").GetBoolean());
		Assert.True(api.Tabs.Has(stays.Id));

		// The renderer reads the tabs again once closeTab resolves (the overview does), so it resolves after the close.
		var leaves = api.Tabs.Open("https://left.test/");
		FakeLeavePrompt.Ask(_host.FakePages.PageFor(api.Tabs, leaves.Id), api.Dialogs);
		var leaving = bridge.HandleAsync(JsonSerializer.Serialize(new { kind = "invoke", id = 2, channel = "tabs:close", args = new[] { leaves.Id } }));
		api.Dialogs.Answer(ShownIn(api).Id, true, null);
		Assert.True(JsonDocument.Parse(await leaving).RootElement.GetProperty("ok").GetBoolean());
		Assert.False(api.Tabs.Has(leaves.Id));
		var unknown = JsonDocument.Parse(await bridge.HandleAsync(JsonSerializer.Serialize(new { kind = "invoke", id = 2, channel = "tabs:close", args = new[] { "nope" } }))).RootElement;
		Assert.False(unknown.GetProperty("ok").GetBoolean());
	}

	[Fact]
	public async Task Relaunch_to_update_goes_through_the_window_so_pages_can_ask()
	{
		await _bridge.HandleAsync(JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "update:relaunch", args = Array.Empty<object>() }));
		Assert.Equal(1, _host.Relaunches);
	}

	[Fact]
	public async Task A_page_that_would_ask_before_leaving_is_kept_awake_as_unsaved()
	{
		var host = new FakePageHost();
		var tabs = new TabService(new TabStore(Deterministic.Clock(), Deterministic.Ids()), host, "about:blank", () => { }, (_, _) => { });
		var draft = tabs.Open("https://draft.test/");
		var plain = tabs.Open("https://plain.test/");
		tabs.Open("https://other.test/");
		host.PageFor(tabs, draft.Id).AsksBeforeLeaving = true;
		var overview = new TabOverview(tabs, () => tabs.State("", false), _ => { }, (_, _) => { });
		var lifecycle = new LifecycleTabs(tabs, overview, TimeSpan.FromSeconds(5));
		Assert.Equal(KeepAwakeReason.UnsavedForm, (await lifecycle.ProbeAsync(draft.Id)).KeepAwake());
		Assert.Null((await lifecycle.ProbeAsync(plain.Id)).KeepAwake());
	}

	[Fact]
	public async Task AllAsync_stops_at_the_first_Stay_and_calls_the_rest_off()
	{
		var slow = new TaskCompletionSource<bool>();
		var calledOff = false;
		var all = LeaveCheck.AllAsync(
		[
			stay =>
			{
				stay.Register(() =>
				{
					calledOff = true;
					slow.TrySetResult(false);
				});
				return slow.Task;
			},
			_ => Task.FromResult(false),
		], CancellationToken.None);
		Assert.False(await all);
		Assert.True(calledOff);
	}

	[Fact]
	public async Task AllAsync_with_nothing_to_ask_is_true()
	{
		Assert.True(await LeaveCheck.AllAsync([], CancellationToken.None));
		Assert.True(await LeaveCheck.AllAsync([_ => Task.FromResult(true), _ => Task.FromResult(true)], CancellationToken.None));
	}
}

public sealed class ScriptDialogsTests
{
	private sealed class Answer : IScriptDialogAnswer
	{
		public List<string> Calls { get; } = [];
		public void Accept(string? text) => Calls.Add($"accept {text}");
		public void Dismiss() => Calls.Add("dismiss");
	}

	private readonly PageUiStore _ui = new();
	private readonly object _a = new();
	private readonly object _b = new();
	private string? _hiddenTitle;

	private ScriptDialogs Dialogs() => new(_ui, page => ReferenceEquals(page, _b) ? _hiddenTitle : null);

	[Fact]
	public void Dialogs_show_one_at_a_time_in_the_order_they_came()
	{
		var dialogs = Dialogs();
		var first = new Answer();
		var second = new Answer();
		dialogs.Open(_a, ScriptDialogKinds.Alert, "https://a.test/page", "Hello", null, first);
		dialogs.Open(_b, ScriptDialogKinds.Confirm, "https://b.test/", "Sure?", null, second);
		Assert.Equal(new ScriptDialogPrompt(1, ScriptDialogKinds.Alert, "https://a.test", "Hello"), _ui.State.Dialog);
		dialogs.Answer(1, true, null);
		Assert.Equal(["accept "], first.Calls);
		Assert.Equal((ScriptDialogKinds.Confirm, "Sure?"), (_ui.State.Dialog!.Kind, _ui.State.Dialog.Message));
		dialogs.Answer(2, false, null);
		Assert.Equal(["dismiss"], second.Calls);
		Assert.Null(_ui.State.Dialog);
		Assert.False(_ui.State.HasVisiblePageUi);
	}

	[Fact]
	public void A_prompt_keeps_its_starting_text_and_hands_back_what_was_typed()
	{
		var dialogs = Dialogs();
		var answer = new Answer();
		dialogs.Open(_a, ScriptDialogKinds.Prompt, "https://a.test/", "Name?", "Ada", answer);
		Assert.Equal("Ada", _ui.State.Dialog!.DefaultText);
		Assert.True(_ui.State.HasVisiblePageUi);
		dialogs.Answer(_ui.State.Dialog.Id, true, "Grace");
		Assert.Equal(["accept Grace"], answer.Calls);
	}

	[Fact]
	public void A_hidden_tabs_dialog_says_whose_it_is()
	{
		_hiddenTitle = "Inbox";
		var dialogs = Dialogs();
		dialogs.Open(_b, ScriptDialogKinds.Alert, "https://b.test/", "Ping", null, new Answer());
		Assert.Equal("Inbox", _ui.State.Dialog!.Tab);
		_hiddenTitle = null;
		dialogs.Refresh();
		Assert.Null(_ui.State.Dialog!.Tab);
	}

	[Fact]
	public void A_page_that_goes_dismisses_its_dialogs_and_the_next_one_shows()
	{
		var dialogs = Dialogs();
		var gone = new Answer();
		dialogs.Open(_a, ScriptDialogKinds.Confirm, "https://a.test/", "Sure?", null, gone);
		dialogs.Open(_b, ScriptDialogKinds.Alert, "https://b.test/", "Hi", null, new Answer());
		dialogs.DropFor(_a);
		Assert.Equal(["dismiss"], gone.Calls);
		Assert.Equal("Hi", _ui.State.Dialog!.Message);
	}

	[Fact]
	public void An_answer_to_a_dialog_no_longer_there_is_ignored()
	{
		var dialogs = Dialogs();
		var answer = new Answer();
		dialogs.Open(_a, ScriptDialogKinds.Alert, "https://a.test/", "Hi", null, answer);
		dialogs.Answer(1, true, null);
		dialogs.Answer(1, false, null);
		Assert.Equal(["accept "], answer.Calls);
	}

	[Fact]
	public void Only_a_beforeunload_prompt_counts_as_asking_to_stay()
	{
		var dialogs = Dialogs();
		dialogs.Open(_a, ScriptDialogKinds.Alert, "https://a.test/", "Hi", null, new Answer());
		Assert.False(dialogs.IsAsking(_a));
		dialogs.Open(_b, ScriptDialogKinds.BeforeUnload, "https://b.test/", "", null, new Answer());
		Assert.True(dialogs.IsAsking(_b));
	}

	[Fact]
	public void A_page_with_no_web_origin_is_this_page()
	{
		var dialogs = Dialogs();
		dialogs.Open(_a, ScriptDialogKinds.Alert, "about:blank", "Hi", null, new Answer());
		Assert.Equal("null", _ui.State.Dialog!.Origin);
	}

	[Fact]
	public void An_unknown_kind_is_refused()
	{
		Assert.Throws<ArgumentException>(() => Dialogs().Open(_a, "print", "https://a.test/", "", null, new Answer()));
	}
}

public sealed class CloseConfirmTests
{
	private static IReadOnlyList<SessionTab> Web(int n) => [.. Enumerable.Range(0, n).Select(i => new SessionTab($"https://t{i}.test/", "t", false, i == 0))];

	[Fact]
	public void Asks_by_default_and_Dont_ask_again_turns_both_questions_off()
	{
		var settings = new SettingsStore();
		var confirm = new CloseConfirm(settings);
		Assert.NotNull(confirm.Question(Web(2), false, false));
		Assert.NotNull(confirm.QuitQuestion([Web(1), Web(1)], false));
		confirm.StopAsking();
		Assert.False(settings.Get(CloseConfirm.Setting)!.GetValue<bool>());
		Assert.Null(confirm.Question(Web(2), false, false));
		Assert.Null(confirm.QuitQuestion([Web(1), Web(1)], false));
		settings.Set(CloseConfirm.Setting, true);
		Assert.NotNull(confirm.Question(Web(2), false, false));
	}
}
