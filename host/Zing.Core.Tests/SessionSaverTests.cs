using Zing.Core;

namespace Zing.Core.Tests;

public sealed class SessionSaverTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-session-").FullName;
	private readonly ManualScheduler _scheduler = new();
	private readonly SettingsStore _settings = new();
	private readonly TabStore _tabs = new(Deterministic.Clock(), Deterministic.Ids());

	private string File => Path.Combine(_dir, "session.json");

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private SessionSaver Saver() => new(File, _settings, () => (_tabs.Records, _tabs.ActiveId), _scheduler);

	[Fact]
	public void Nothing_is_written_while_restore_is_off()
	{
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
		var tab = Assert.Single(Session.Load(File));
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
}
