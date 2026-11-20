using Zing.Core;

namespace Zing.Core.Tests;

public sealed class SessionSaverTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-session-").FullName;
	private readonly ManualScheduler _scheduler = new();
	private readonly SettingsStore _settings = new();
	private readonly TabStore _tabs = new(Deterministic.Clock(), Deterministic.Ids());
	private readonly TabStore _second = new(Deterministic.Clock(), Deterministic.Ids());
	private bool _twoWindows;

	private string File => Path.Combine(_dir, "session.json");

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private SessionSaver Saver() => new(File, _settings, Windows, _scheduler, error => throw new InvalidOperationException("unexpected session write failure", error));

	private IReadOnlyList<IReadOnlyList<SessionTab>> Windows() =>
		_twoWindows
			? [Session.ToSessionTabs(_tabs.Records, _tabs.ActiveId), Session.ToSessionTabs(_second.Records, _second.ActiveId)]
			: [Session.ToSessionTabs(_tabs.Records, _tabs.ActiveId)];

	[Fact]
	public void Nothing_is_written_while_restore_is_off()
	{
		_settings.Set("restoreTabs", false);
		using var saver = Saver();
		_tabs.Activate(_tabs.Add("https://example.com/").Id);
		saver.Changed();
		saver.Flush();
		Assert.False(System.IO.File.Exists(File));
	}

	[Fact]
	public void With_restore_on_the_tabs_are_written_after_the_debounce_and_load_back()
	{
		using var saver = Saver();
		_settings.Set("restoreTabs", true);
		_tabs.Activate(_tabs.Add("https://example.com/").Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		var tab = Assert.Single(Assert.Single(Session.Load(File)));
		Assert.Equal(new SessionTab("https://example.com/", TabStore.NewTabTitle, false, true), tab);
	}

	[Fact]
	public void Turning_restore_off_removes_the_file_and_no_pending_write_brings_it_back()
	{
		using var saver = Saver();
		_settings.Set("restoreTabs", true);
		_tabs.Activate(_tabs.Add("https://example.com/").Id);
		saver.Changed();
		_settings.Set("restoreTabs", false);
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.False(System.IO.File.Exists(File));
	}

	[Fact]
	public void A_file_left_as_it_is_is_not_overwritten_by_a_window_of_blank_tabs()
	{
		System.IO.File.WriteAllText(File, "unread");
		using var saver = Saver();
		saver.KeepUnreadFile();
		_settings.Set("restoreTabs", true);
		_tabs.Activate(_tabs.Add(TabStore.NewTabUrl).Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		saver.Flush();
		Assert.Equal("unread", System.IO.File.ReadAllText(File));
	}

	[Fact]
	public void A_file_left_as_it_is_is_replaced_once_a_page_is_opened()
	{
		System.IO.File.WriteAllText(File, "unread");
		using var saver = Saver();
		saver.KeepUnreadFile();
		_settings.Set("restoreTabs", true);
		_tabs.Activate(_tabs.Add("https://example.com/").Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal("https://example.com/", Assert.Single(Assert.Single(Session.Load(File))).Url);
	}

	[Fact]
	public void Every_window_is_saved_oldest_first()
	{
		using var saver = Saver();
		_settings.Set("restoreTabs", true);
		_twoWindows = true;
		_tabs.Activate(_tabs.Add("https://one.test/").Id);
		_second.Activate(_second.Add("https://two.test/").Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal(new[] { "https://one.test/", "https://two.test/" }, Session.Load(File).Select(w => Assert.Single(w).Url));
	}

	[Fact]
	public void The_relaunch_file_is_written_now_with_restore_off_and_session_json_stays_away()
	{
		using var saver = Saver();
		_twoWindows = true;
		_tabs.Activate(_tabs.Add("https://one.test/").Id);
		_second.Activate(_second.Add("https://two.test/").Id);
		var relaunch = Path.Combine(_dir, "relaunch-session.json");
		saver.SaveOnce(relaunch);
		Assert.Equal(2, Session.TakeOnce(relaunch, e => throw e).Count);
		Assert.False(System.IO.File.Exists(File));
	}
	[Fact]
	public void A_title_change_alone_writes_nothing_until_quit_and_then_the_new_title()
	{
		using var saver = Saver();
		_settings.Set("restoreTabs", true);
		var tab = _tabs.Add("https://example.com/");
		_tabs.Activate(tab.Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		System.IO.File.Delete(File);
		// A page that retitles itself every second, as a chat or a timer does.
		for (var i = 0; i < 10; i++)
		{
			_tabs.Update(tab.Id, new TabContentPatch(Title: $"({i}) Inbox"));
			saver.Changed();
			_scheduler.Advance(TimeSpan.FromSeconds(1));
		}
		Assert.False(System.IO.File.Exists(File));
		saver.Flush();
		Assert.Equal("(9) Inbox", Assert.Single(Assert.Single(Session.Load(File))).Title);
	}

	[Fact]
	public void A_change_that_is_not_only_a_title_is_written_with_the_latest_titles()
	{
		using var saver = Saver();
		_settings.Set("restoreTabs", true);
		var tab = _tabs.Add("https://example.com/");
		_tabs.Activate(tab.Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		_tabs.Update(tab.Id, new TabContentPatch(Title: "Retitled"));
		saver.Changed();
		_tabs.Update(tab.Id, new TabContentPatch(Url: "https://example.com/next"));
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		var saved = Assert.Single(Assert.Single(Session.Load(File)));
		Assert.Equal(("https://example.com/next", "Retitled"), (saved.Url, saved.Title));
	}

	[Fact]
	public void Nothing_changed_writes_nothing()
	{
		using var saver = Saver();
		_settings.Set("restoreTabs", true);
		_tabs.Activate(_tabs.Add("https://example.com/").Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		System.IO.File.Delete(File);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		saver.Flush();
		Assert.False(System.IO.File.Exists(File));
	}

	[Fact]
	public void Turning_restore_off_and_on_again_writes_the_tabs_again()
	{
		using var saver = Saver();
		_settings.Set("restoreTabs", true);
		_tabs.Activate(_tabs.Add("https://example.com/").Id);
		saver.Changed();
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		_settings.Set("restoreTabs", false);
		_settings.Set("restoreTabs", true);
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.True(System.IO.File.Exists(File));
	}
}
