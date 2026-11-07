using System.Text.Json;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>
/// The save and update prompts: what a submit and a landing turn into, what is held and for how long, and that the
/// password stays in the host. The vectors hold the success rule in step with src/shared/vault/signInOutcome.ts.
/// </summary>
public sealed class SignInWatchTests : IDisposable
{
	private const string Site = "https://example.com";
	private const string Page = "tab-1";

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-signin-").FullName;
	private readonly FakeSealer _sealer = new();
	private readonly ManualScheduler _scheduler = new();
	private readonly List<Task> _background = [];
	private readonly List<SaveLoginPrompt?> _prompts = [];
	private VaultStore? _store;
	private readonly SignInWatch _watch;
	private bool _shown = true;

	public SignInWatchTests()
	{
		_store = NewStore();
		_watch = new SignInWatch(() => _store, _ => _shown, _scheduler, _prompts.Add, (task, _) => _background.Add(task));
	}

	private VaultStore NewStore() => new(_dir, new FakeCipher(), _sealer, new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

	private VaultStore Store => _store ?? throw new Xunit.Sdk.XunitException("the vault is off");

	public void Dispose()
	{
		_watch.Dispose();
		_store?.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private async Task Settle() => await Task.WhenAll(_background);

	private async Task<string> Save(string username, string password = "hunter22", string origin = Site) =>
		await Store.SaveAsync(new NewLogin(origin, username, password, "", ""));

	private async Task SignIn(string username, string password, bool landed = true, string origin = Site)
	{
		_watch.Submitted(Page, origin, username, password);
		if (landed) _watch.Landed(Page, origin, hasVisiblePasswordBox: false);
		await Settle();
	}

	private SaveLoginPrompt? Shown => _watch.Prompt;

	// -- the rule, shared with the renderer --

	private static JsonElement Vectors => Zing.Core.Tests.Vectors.Load("src/shared/vault/signInOutcome.vectors.json");

	public static TheoryData<string> OutcomeCases()
	{
		var data = new TheoryData<string>();
		foreach (var v in Vectors.GetProperty("outcomes").EnumerateArray()) data.Add(v.GetProperty("name").GetString()!);
		return data;
	}

	[Theory]
	[MemberData(nameof(OutcomeCases))]
	public void The_outcome_follows_the_shared_vectors(string name)
	{
		var v = Vectors.GetProperty("outcomes").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
		var expected = v.GetProperty("expected").GetString() switch
		{
			"success" => SignInOutcome.Success,
			"failed" => SignInOutcome.Failed,
			"pending" => SignInOutcome.Pending,
			var other => throw new InvalidDataException($"unknown outcome {other}"),
		};
		Assert.Equal(expected, SignInOutcomes.Of(v.GetProperty("submitted").GetString()!, v.GetProperty("landed").GetString()!, v.GetProperty("hasVisiblePasswordBox").GetBoolean()));
	}

	[Fact]
	public void The_limits_are_the_shared_ones()
	{
		var limits = Vectors.GetProperty("limits");
		Assert.Equal(SignInWatch.UsernameMax, limits.GetProperty("usernameMax").GetInt32());
		Assert.Equal(SignInWatch.PasswordMax, limits.GetProperty("passwordMax").GetInt32());
		Assert.Equal(limits.GetProperty("holdMs").GetInt32(), (int)SignInWatch.HoldFor.TotalMilliseconds);
	}

	// -- prompting --

	[Fact]
	public async Task A_successful_sign_in_to_a_new_site_offers_to_save()
	{
		await SignIn("ada@example.test", "s3cret");

		Assert.Equal(new SaveLoginPrompt(Page, "save", Site, "ada@example.test", null), Shown);
		Assert.DoesNotContain("s3cret", Shown!.ToString());
		Assert.DoesNotContain("s3cret", JsonSerializer.Serialize(Shown));
	}

	[Fact]
	public async Task A_sign_in_that_lands_on_the_form_again_gets_no_prompt_and_is_forgotten()
	{
		_watch.Submitted(Page, Site, "ada@example.test", "wrong");
		_watch.Landed(Page, Site, hasVisiblePasswordBox: true);
		await Settle();

		Assert.Null(Shown);
		Assert.Equal(0, _watch.HeldCount);
		// A later landing without the box is some other sign-in's, with nothing held.
		_watch.Landed(Page, Site, hasVisiblePasswordBox: false);
		await Settle();
		Assert.Null(Shown);
	}

	[Fact]
	public async Task A_landing_in_another_origin_waits_for_the_next()
	{
		_watch.Submitted(Page, Site, "ada@example.test", "s3cret");
		_watch.Landed(Page, "https://sso.example", hasVisiblePasswordBox: false);
		await Settle();
		Assert.Null(Shown);
		Assert.Equal(1, _watch.HeldCount);

		_watch.Landed(Page, Site, hasVisiblePasswordBox: false);
		await Settle();
		Assert.Equal("save", Shown?.Kind);
	}

	[Fact]
	public async Task Held_credentials_are_gone_after_ten_seconds_without_a_landing()
	{
		_watch.Submitted(Page, Site, "ada@example.test", "s3cret");
		_scheduler.Advance(TimeSpan.FromMilliseconds(9_999));
		Assert.Equal(1, _watch.HeldCount);

		_scheduler.Advance(TimeSpan.FromMilliseconds(1));
		Assert.Equal(0, _watch.HeldCount);
		_watch.Landed(Page, Site, hasVisiblePasswordBox: false);
		await Settle();
		Assert.Null(Shown);
		Assert.Empty(_prompts);
	}

	[Fact]
	public async Task The_latest_submit_of_a_page_wins_and_a_landing_prompts_once()
	{
		_watch.Submitted(Page, Site, "first@example.test", "one");
		_watch.Submitted(Page, Site, "second@example.test", "two");
		Assert.Equal(1, _watch.HeldCount);

		_watch.Landed(Page, Site, hasVisiblePasswordBox: false);
		_watch.Landed(Page, Site, hasVisiblePasswordBox: false);
		await Settle();

		Assert.Equal("second@example.test", Shown?.Username);
		Assert.Single(_prompts);
	}

	[Fact]
	public async Task A_new_submit_closes_the_prompt_for_the_old_one()
	{
		await SignIn("ada@example.test", "s3cret");
		Assert.NotNull(Shown);

		_watch.Submitted(Page, Site, "ada@example.test", "other");

		Assert.Null(Shown);
		Assert.Equal(1, _watch.HeldCount);
	}

	[Fact]
	public async Task A_saved_username_with_a_different_password_offers_an_update_naming_the_login()
	{
		await Save("ada@example.test", "old password");

		await SignIn("ada@example.test", "new password");

		Assert.Equal(new SaveLoginPrompt(Page, "update", Site, "ada@example.test", "ada@example.test"), Shown);
	}

	[Fact]
	public async Task A_landing_never_asks_Hello_and_a_saved_username_gets_an_update_prompt_straight_away()
	{
		await Save("ada@example.test", "same password");
		var calls = _sealer.Calls;

		await SignIn("ada@example.test", "same password");

		Assert.Equal(calls, _sealer.Calls);
		Assert.Equal("update", Shown?.Kind);
	}

	[Fact]
	public async Task Update_with_the_password_already_saved_stores_nothing_in_the_one_Hello_and_closes()
	{
		var id = await Save("ada@example.test", "same password");
		await SignIn("ada@example.test", "same password");
		var calls = _sealer.Calls;

		Assert.Equal(SaveOutcomes.Unchanged, await _watch.AnswerAsync(Page, "save"));

		Assert.Equal(calls + 1, _sealer.Calls);
		Assert.Null(Shown);
		Assert.Empty(Store.History(id));
	}

	[Fact]
	public async Task A_forged_submit_and_landing_loop_never_asks_Hello()
	{
		await Save("ada@example.test", "real password");
		var calls = _sealer.Calls;

		for (var i = 0; i < 20; i++)
		{
			_watch.Message(Page, new Uri(Site), new AutofillMessage.Submitted("ada@example.test", "x" + i));
			_watch.Message(Page, new Uri(Site), new AutofillMessage.Landed(false));
		}
		await Settle();

		Assert.Equal(calls, _sealer.Calls);
		Assert.Equal("update", Shown?.Kind);
	}

	[Fact]
	public async Task The_form_coming_back_while_a_prompt_is_open_withdraws_it_and_its_password()
	{
		await SignIn("ada@example.test", "wrong password");
		Assert.NotNull(Shown);

		_watch.Landed(Page, "https://other.example", hasVisiblePasswordBox: true);
		Assert.NotNull(Shown);
		_watch.Landed(Page, Site, hasVisiblePasswordBox: true);

		Assert.Null(Shown);
		Assert.Equal(SaveOutcomes.Gone, await _watch.AnswerAsync(Page, "save"));
		Assert.Empty(Store.Summaries());
	}

	[Fact]
	public async Task Leaving_for_another_origin_closes_the_prompt_but_staying_does_not()
	{
		await SignIn("ada@example.test", "s3cret");

		_watch.Message(Page, new Uri(Site + "/next"), new AutofillMessage.Left());
		Assert.NotNull(Shown);
		_watch.Message(Page, new Uri("https://b.example/"), new AutofillMessage.Left());
		Assert.Null(Shown);
	}

	[Fact]
	public async Task An_exception_in_the_landing_path_still_drops_what_was_held()
	{
		var broken = false;
		var watch = new SignInWatch(() => broken ? throw new InvalidOperationException("the vault broke") : _store, _ => true, _scheduler, _ => { }, (task, _) => _background.Add(task));
		watch.Submitted(Page, Site, "ada@example.test", "s3cret");
		broken = true;

		watch.Landed(Page, Site, hasVisiblePasswordBox: false);

		var failed = Assert.Single(_background);
		await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
		Assert.Equal(0, watch.HeldCount);
		Assert.Null(watch.Prompt);
		_background.Clear();
		watch.Dispose();
	}

	[Fact]
	public async Task A_never_marked_origin_holds_nothing()
	{
		await Store.NeverSave(Site);

		_watch.Submitted(Page, Site, "ada@example.test", "s3cret");

		Assert.Equal(0, _watch.HeldCount);
	}

	[Fact]
	public async Task Saving_when_the_login_was_saved_meanwhile_stores_a_duplicate_of_nothing()
	{
		await SignIn("ada@example.test", "s3cret");
		await Save("ada@example.test", "other");

		Assert.Equal(SaveOutcomes.Gone, await _watch.AnswerAsync(Page, "save"));

		Assert.Single(Store.Summaries());
	}

	[Fact]
	public async Task A_vault_closed_during_the_answers_Hello_is_gone_not_an_error()
	{
		await Save("other@example.test");
		await SignIn("ada@example.test", "s3cret");
		_sealer.Unsealing = () => Store.Dispose();

		Assert.Equal(SaveOutcomes.Gone, await _watch.AnswerAsync(Page, "save"));
	}

	[Fact]
	public async Task A_login_Zing_just_filled_is_not_compared_so_Hello_is_not_asked_again()
	{
		await Save("ada@example.test", "same password");
		_watch.Filled(Page, Site, "ada@example.test", "same password");
		var calls = _sealer.Calls;

		await SignIn("ada@example.test", "same password");

		Assert.Null(Shown);
		Assert.Equal(calls, _sealer.Calls);
	}

	[Fact]
	public async Task A_fill_does_not_excuse_a_different_password()
	{
		await Save("ada@example.test", "same password");
		_watch.Filled(Page, Site, "ada@example.test", "same password");

		await SignIn("ada@example.test", "edited by hand");

		Assert.Equal("update", Shown?.Kind);
	}

	[Fact]
	public async Task A_cancelled_Hello_on_the_updates_compare_stores_nothing()
	{
		var id = await Save("ada@example.test", "old password");
		await SignIn("ada@example.test", "new password");
		_sealer.Result = HelloResult.Cancel;

		Assert.Equal(SaveOutcomes.Cancelled, await _watch.AnswerAsync(Page, "save"));

		_sealer.Result = HelloResult.Pass;
		Assert.Equal("old password", await Store.PasswordAsync(id));
	}

	[Fact]
	public async Task A_login_in_Recently_deleted_gets_a_save_prompt()
	{
		var id = await Save("ada@example.test", "old password");
		Store.Delete(id);

		await SignIn("ada@example.test", "old password");

		Assert.Equal("save", Shown?.Kind);
	}

	[Fact]
	public void A_sign_in_with_no_username_is_dropped_before_it_is_held()
	{
		Assert.Equal(PageMessageVerdict.Dropped, AutofillMessage.Read("""{"zingCredentialField":{"kind":"submit","username":"","password":"x"}}""", out _));
	}

	[Fact]
	public async Task With_the_vault_off_nothing_is_held()
	{
		_store = null;
		_watch.Submitted(Page, Site, "ada@example.test", "s3cret");
		Assert.Equal(0, _watch.HeldCount);
		await Settle();
	}

	// -- answering --

	[Fact]
	public async Task Save_stores_the_login_after_Hello_and_closes_the_prompt()
	{
		await SignIn("ada@example.test", "s3cret");

		Assert.Equal(SaveOutcomes.Saved, await _watch.AnswerAsync(Page, "save"));

		Assert.Null(Shown);
		var saved = Assert.Single(Store.Summaries());
		Assert.Equal((Site, "ada@example.test"), (saved.Origin, saved.Username));
		Assert.Equal("s3cret", await Store.PasswordAsync(saved.Id));
		Assert.Equal(0, _watch.HeldCount);
	}

	[Fact]
	public async Task A_cancelled_Hello_on_save_stores_nothing_and_closes_the_prompt_quietly()
	{
		await Save("other@example.test");
		await SignIn("ada@example.test", "s3cret");
		_sealer.Result = HelloResult.Cancel;

		Assert.Equal(SaveOutcomes.Cancelled, await _watch.AnswerAsync(Page, "save"));

		Assert.Null(Shown);
		Assert.Single(Store.Summaries());
	}

	[Fact]
	public async Task Update_replaces_the_password_in_one_Hello_and_keeps_the_old_one_in_history()
	{
		var id = await Save("ada@example.test", "old password");
		await SignIn("ada@example.test", "new password");
		var calls = _sealer.Calls;

		Assert.Equal(SaveOutcomes.Updated, await _watch.AnswerAsync(Page, "save"));

		Assert.Equal(calls + 1, _sealer.Calls);
		Assert.Equal("new password", await Store.PasswordAsync(id));
		Assert.Single(Store.History(id));
		Assert.Single(Store.Summaries());
	}

	[Fact]
	public async Task A_cancelled_Hello_on_update_changes_nothing()
	{
		var id = await Save("ada@example.test", "old password");
		await SignIn("ada@example.test", "new password");
		_sealer.Result = HelloResult.Cancel;

		Assert.Equal(SaveOutcomes.Cancelled, await _watch.AnswerAsync(Page, "save"));

		_sealer.Result = HelloResult.Pass;
		Assert.Equal("old password", await Store.PasswordAsync(id));
		Assert.Empty(Store.History(id));
	}

	[Fact]
	public async Task Not_now_stores_nothing_and_a_second_answer_finds_nothing()
	{
		await SignIn("ada@example.test", "s3cret");

		Assert.Equal(SaveOutcomes.Dismissed, await _watch.AnswerAsync(Page, "not-now"));

		Assert.Null(Shown);
		Assert.Empty(Store.Summaries());
		Assert.Equal(SaveOutcomes.Gone, await _watch.AnswerAsync(Page, "save"));
		Assert.Empty(Store.Summaries());
	}

	[Fact]
	public async Task An_answer_for_another_tab_finds_nothing()
	{
		await SignIn("ada@example.test", "s3cret");

		Assert.Equal(SaveOutcomes.Gone, await _watch.AnswerAsync("tab-2", "save"));

		Assert.NotNull(Shown);
	}

	[Fact]
	public async Task An_unknown_answer_is_refused_and_leaves_the_prompt()
	{
		await SignIn("ada@example.test", "s3cret");

		await Assert.ThrowsAsync<SaveAnswerException>(() => _watch.AnswerAsync(Page, "maybe"));

		Assert.NotNull(Shown);
	}

	[Fact]
	public async Task Never_marks_the_site_and_stops_every_later_prompt_for_it_save_and_update_alike()
	{
		await SignIn("ada@example.test", "s3cret");
		Assert.Equal(SaveOutcomes.Dismissed, await _watch.AnswerAsync(Page, "never"));
		Assert.True(Store.IsNeverSave(Site));

		await SignIn("bob@example.test", "other");
		Assert.Null(Shown);

		// An update prompt for a saved login is suppressed too (Ruling 32).
		await Save("carol@example.test", "old");
		await SignIn("carol@example.test", "new");
		Assert.Null(Shown);

		// Another origin is unaffected.
		await SignIn("ada@example.test", "s3cret", origin: "https://other.example");
		Assert.Equal("save", Shown?.Kind);
	}

	[Fact]
	public async Task Hello_is_not_asked_for_never_once_the_vault_has_keys()
	{
		await Save("ada@example.test");
		var calls = _sealer.Calls;

		await Store.NeverSave("https://news.example/some/page");

		Assert.Equal(calls, _sealer.Calls);
		Assert.True(Store.IsNeverSave("https://news.example"));
		Assert.False(Store.IsNeverSave("https://other.example"));
		Assert.False(Store.IsNeverSave("http://news.example"));
	}

	// -- where it goes away --

	[Fact]
	public async Task Closing_the_tab_drops_what_is_held_and_the_prompt()
	{
		_watch.Submitted(Page, Site, "ada@example.test", "s3cret");
		_watch.PageClosed(Page);
		Assert.Equal(0, _watch.HeldCount);

		await SignIn("ada@example.test", "s3cret");
		Assert.NotNull(Shown);
		_watch.PageClosed(Page);
		Assert.Null(Shown);
		Assert.Equal(SaveOutcomes.Gone, await _watch.AnswerAsync(Page, "save"));
		Assert.Empty(Store.Summaries());
	}

	[Fact]
	public async Task Turning_the_vault_off_drops_what_is_held_and_the_prompt()
	{
		_watch.Submitted("tab-2", Site, "bob@example.test", "pw");
		await SignIn("ada@example.test", "s3cret");

		_watch.Clear();

		Assert.Null(Shown);
		Assert.Equal(0, _watch.HeldCount);
	}

	[Fact]
	public async Task A_prompt_for_a_tab_that_left_the_screen_goes()
	{
		await SignIn("ada@example.test", "s3cret");
		_shown = false;

		_watch.PagesChanged();

		Assert.Null(Shown);
	}

	[Fact]
	public async Task An_unanswered_prompt_closes_by_itself()
	{
		await SignIn("ada@example.test", "s3cret");

		_scheduler.Advance(SignInWatch.PromptFor);

		Assert.Null(Shown);
		Assert.Equal(SaveOutcomes.Gone, await _watch.AnswerAsync(Page, "save"));
	}

	// -- the page channel (Ruling 33) --

	[Fact]
	public async Task The_origin_is_the_frames_source_not_anything_the_page_said()
	{
		_watch.Message(Page, new Uri("https://example.com/login"), new AutofillMessage.Submitted("ada@example.test", "s3cret"));
		// Another origin's landing, however it is reported, says nothing about this sign-in.
		_watch.Message(Page, new Uri("https://evil.example/"), new AutofillMessage.Landed(false));
		await Settle();
		Assert.Null(Shown);

		_watch.Message(Page, new Uri("https://example.com/home"), new AutofillMessage.Landed(false));
		await Settle();
		Assert.Equal(Site, Shown?.Origin);
	}

	[Fact]
	public async Task A_page_that_is_not_web_holds_nothing()
	{
		_watch.Message(Page, new Uri("file:///C:/x.html"), new AutofillMessage.Submitted("ada@example.test", "s3cret"));
		_watch.Message(Page, new Uri("zing://start/"), new AutofillMessage.Submitted("ada@example.test", "s3cret"));
		Assert.Equal(0, _watch.HeldCount);
		await Settle();
	}

	[Theory]
	[InlineData(512, 1024, true)]
	[InlineData(513, 10, false)]
	[InlineData(10, 1025, false)]
	[InlineData(0, 10, false)]
	[InlineData(10, 0, false)]
	public void A_submit_report_is_held_to_the_limits(int usernameLength, int passwordLength, bool accepted)
	{
		var json = JsonSerializer.Serialize(new { zingCredentialField = new { kind = "submit", username = new string('u', usernameLength), password = new string('p', passwordLength) } });
		var verdict = AutofillMessage.Read(json, out var message);
		Assert.Equal(accepted ? PageMessageVerdict.Autofill : PageMessageVerdict.Dropped, verdict);
		Assert.Equal(accepted, message is AutofillMessage.Submitted);
	}

	[Theory]
	[InlineData("""{"zingCredentialField":{"kind":"submit","username":"a"}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"submit","username":1,"password":"b"}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"submit","username":{"x":{"y":1}},"password":"b"}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"landed"}}""")]
	[InlineData("""{"zingCredentialField":{"kind":"landed","password":"yes"}}""")]
	public void A_malformed_submit_or_landing_is_dropped_never_thrown(string json)
	{
		Assert.Equal(PageMessageVerdict.Dropped, AutofillMessage.Read(json, out var message));
		Assert.Null(message);
	}

	[Fact]
	public void A_landing_report_reads_both_ways()
	{
		Assert.Equal(PageMessageVerdict.Autofill, AutofillMessage.Read("""{"zingCredentialField":{"kind":"landed","password":false}}""", out var gone));
		Assert.Equal(new AutofillMessage.Landed(false), gone);
		Assert.Equal(PageMessageVerdict.Autofill, AutofillMessage.Read("""{"zingCredentialField":{"kind":"landed","password":true}}""", out var back));
		Assert.Equal(new AutofillMessage.Landed(true), back);
	}

	// -- the never-save mark in the store --

	[Fact]
	public async Task A_never_mark_is_not_a_login_and_survives_a_reopen()
	{
		await Save("ada@example.test");
		await Store.NeverSave(Site);
		await Store.NeverSave(Site);

		Assert.Single(Store.Summaries());
		Assert.Equal([new NeverSave(Site)], Store.NeverSaves());
		Assert.Equal(2, Store.Records.Count);

		var id = Store.Records.Single(r => r.Password.Length == 0).ItemId;
		await Assert.ThrowsAsync<VaultItemNotFoundException>(() => Store.PasswordAsync(id));
		Assert.Throws<VaultItemNotFoundException>(() => Store.Delete(id));

		Store.Dispose();
		_store = NewStore();
		Assert.True(Store.IsNeverSave(Site));
		Assert.Single(Store.Summaries());
	}

	[Fact]
	public async Task A_never_mark_is_queued_to_sync_like_any_item()
	{
		await Save("ada@example.test");
		await Store.NeverSave("https://news.example");

		Assert.Equal(2, Store.Queued.Count);
	}

	[Fact]
	public async Task The_first_write_of_a_vault_may_be_a_never_mark_and_asks_Hello_once_for_the_keys()
	{
		await Store.NeverSave(Site);

		Assert.Equal(1, _sealer.Calls);
		Assert.True(Store.IsNeverSave(Site));
		// The keys exist now: the first save does not make them again.
		var vaultId = Store.VaultId;
		await Save("ada@example.test");
		Assert.Equal(vaultId, Store.VaultId);
	}

	[Fact]
	public async Task A_cancelled_Hello_for_the_first_never_mark_stores_nothing()
	{
		_sealer.Result = HelloResult.Cancel;

		await Assert.ThrowsAsync<HelloCancelledException>(() => Store.NeverSave(Site));

		Assert.False(Store.IsNeverSave(Site));
		Assert.Empty(Store.Records);
	}

	[Fact]
	public async Task A_never_mark_for_something_that_is_not_a_web_origin_is_refused()
	{
		await Assert.ThrowsAsync<NotAWebOriginException>(() => Store.NeverSave("file:///C:/x.html"));
	}

	// Task 6 review I3: a window closing with a prompt up pushed "no prompt" to a view that was closing, which threw and
	// stopped ChromeApi.Dispose half way.
	[Fact]
	public async Task DisposingWithAPromptUpPushesNothingToTheClosingView()
	{
		var pushes = new List<SaveLoginPrompt?>();
		var closing = false;
		var background = new List<Task>();
		var watch = new SignInWatch(() => _store, _ => _shown, _scheduler, prompt =>
		{
			if (closing) throw new InvalidOperationException("the view is closing");
			pushes.Add(prompt);
		}, (task, _) => background.Add(task));
		watch.Submitted(Page, Site, "ada", "hunter22");
		watch.Landed(Page, Site, hasVisiblePasswordBox: false);
		await Task.WhenAll(background);
		Assert.NotNull(watch.Prompt);
		closing = true;

		watch.Dispose();

		Assert.Single(pushes);
	}
}
