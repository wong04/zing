using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

public sealed class DnsServiceTests : IDisposable
{
	private sealed class FakeSystem : IDnsSystem
	{
		public DnsSnapshot Now { get; set; } = new([new DnsAdapter("Ethernet", ["10.0.0.1"], []), new DnsAdapter("Wi-Fi", [], null)], [new DohEntry("1.1.1.1", "https://cloudflare-dns.com/dns-query", false, false)]);
		public ElevatedRun Result { get; set; } = new ElevatedRun.Exited(0);
		public List<string> Ran { get; } = [];
		/// <summary>Runs once, just before the script would run: the adapters change between the preview and the click.</summary>
		public Action? BeforeRun { get; set; }

		public Task<DnsSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(Now);

		/// <summary>The elevated process doesn't end until <see cref="Finish"/> or <see cref="Crash"/>, as one stuck behind a prompt nobody answers.</summary>
		public bool Hangs { get; set; }

		private readonly TaskCompletionSource _ends = new();

		public void Finish() => _ends.SetResult();

		public void Crash(Exception error) => _ends.SetException(error);

		public async Task<ElevatedRun> RunElevatedAsync(string script, CancellationToken ct)
		{
			BeforeRun?.Invoke();
			Ran.Add(script);
			if (Hangs) await _ends.Task;
			return Result;
		}
	}

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "zing-dns-" + Guid.NewGuid().ToString("N"));
	private readonly FakeSystem _system = new();
	private readonly DnsService _service;
	private string BackupPath => Path.Combine(_directory, "dns-before.json");

	public DnsServiceTests()
	{
		_service = new DnsService(_system, BackupPath, () => new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), _ => { });
	}

	public void Dispose()
	{
		if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public async Task Preview_shows_the_script_for_the_up_adapters_and_runs_nothing()
	{
		var preview = await _service.PreviewAsync("cloudflare");
		Assert.False(preview.Applied);
		Assert.Equal("Cloudflare", preview.ProviderName);
		Assert.Equal(["Ethernet", "Wi-Fi"], preview.Adapters);
		Assert.Equal(DnsScript.Apply("cloudflare", _system.Now.Adapters), preview.Script);
		Assert.Empty(_system.Ran);
		Assert.False(File.Exists(BackupPath));
	}

	[Fact]
	public async Task Apply_runs_exactly_the_script_that_was_previewed_and_keeps_the_old_settings_for_undo()
	{
		var preview = await _service.PreviewAsync("cloudflare");
		var outcome = await _service.ApplyAsync("cloudflare", preview.Token);
		Assert.Equal(DnsOutcome.Applied, outcome.Status);
		Assert.Equal([preview.Script], _system.Ran);
		var saved = DnsBackup.Load(BackupPath);
		Assert.Equal("cloudflare", saved.Provider);
		Assert.Equal(["10.0.0.1"], saved.Adapters[0].V4);
		Assert.Null(saved.Adapters[1].V6);
		Assert.Equal("1.1.1.1", Assert.Single(saved.Doh).Address);
	}

	[Fact]
	public async Task Apply_shows_the_new_script_instead_of_running_one_nobody_saw_when_the_adapters_changed()
	{
		var preview = await _service.PreviewAsync("cloudflare");
		_system.Now = new DnsSnapshot([new DnsAdapter("Ethernet", [], []), new DnsAdapter("Docking station", [], [])], []);
		var outcome = await _service.ApplyAsync("cloudflare", preview.Token);
		Assert.Equal(DnsOutcome.Changed, outcome.Status);
		Assert.Empty(_system.Ran);
		Assert.False(File.Exists(BackupPath));
		Assert.Contains("'Docking station'", outcome.Preview!.Script);
		Assert.NotEqual(preview.Token, outcome.Preview.Token);
		// Clicking again on what is now shown runs that script.
		await _service.ApplyAsync("cloudflare", outcome.Preview.Token);
		Assert.Equal([outcome.Preview.Script], _system.Ran);
	}

	[Fact]
	public async Task Apply_does_not_run_when_the_provider_changed_since_the_preview()
	{
		var preview = await _service.PreviewAsync("cloudflare");
		var outcome = await _service.ApplyAsync("google", preview.Token);
		Assert.Equal(DnsOutcome.Changed, outcome.Status);
		Assert.Empty(_system.Ran);
	}

	[Fact]
	public async Task Declining_the_admin_prompt_is_an_ordinary_result_and_leaves_nothing_behind()
	{
		_system.Result = ElevatedRun.Declined;
		var preview = await _service.PreviewAsync("cloudflare");
		var outcome = await _service.ApplyAsync("cloudflare", preview.Token);
		Assert.Equal(DnsOutcome.Declined, outcome.Status);
		Assert.False(File.Exists(BackupPath));
		Assert.False((await _service.PreviewAsync("cloudflare")).Applied);
	}

	[Fact]
	public async Task A_script_that_fails_is_reported_and_keeps_the_old_settings_since_it_may_have_changed_some()
	{
		_system.Result = new ElevatedRun.Exited(3);
		var preview = await _service.PreviewAsync("cloudflare");
		var failure = await Assert.ThrowsAsync<DnsScriptFailedError>(() => _service.ApplyAsync("cloudflare", preview.Token));
		Assert.Equal(3, failure.ExitCode);
		Assert.True(File.Exists(BackupPath));
		Assert.True((await _service.PreviewAsync("cloudflare")).Applied);
	}

	[Fact]
	public async Task A_windows_without_encrypted_dns_changed_nothing_and_says_so()
	{
		_system.Result = new ElevatedRun.Exited(DnsScript.NoEncryptedDnsExit);
		var preview = await _service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<NoEncryptedDnsError>(() => _service.ApplyAsync("cloudflare", preview.Token));
		Assert.False(File.Exists(BackupPath));
	}

	[Fact]
	public async Task Applying_twice_refuses_and_keeps_the_true_original_settings()
	{
		var preview = await _service.PreviewAsync("cloudflare");
		await _service.ApplyAsync("cloudflare", preview.Token);
		var original = await File.ReadAllTextAsync(BackupPath, TestContext.Current.CancellationToken);
		// The adapters now read as the provider's, which is what a second backup would wrongly keep.
		_system.Now = new DnsSnapshot([new DnsAdapter("Ethernet", ["1.1.1.1", "1.0.0.1"], []), new DnsAdapter("Wi-Fi", [], null)], []);
		await Assert.ThrowsAsync<DnsAlreadyAppliedError>(() => _service.ApplyAsync("cloudflare", preview.Token));
		Assert.Equal(original, await File.ReadAllTextAsync(BackupPath, TestContext.Current.CancellationToken));
		Assert.Single(_system.Ran);
	}

	[Fact]
	public async Task Once_applied_the_preview_is_the_undo_script_from_the_saved_settings()
	{
		var first = await _service.PreviewAsync("cloudflare");
		await _service.ApplyAsync("cloudflare", first.Token);
		// Whatever the setting says now, undo is for what was applied.
		var preview = await _service.PreviewAsync("quad9");
		Assert.True(preview.Applied);
		Assert.Equal("Cloudflare", preview.ProviderName);
		Assert.Equal(DnsScript.Undo(DnsBackup.Load(BackupPath).Adapters, DnsBackup.Load(BackupPath).Doh, "cloudflare"), preview.Script);
	}

	[Fact]
	public async Task Undo_runs_the_previewed_script_and_then_forgets_the_old_settings()
	{
		var first = await _service.PreviewAsync("cloudflare");
		await _service.ApplyAsync("cloudflare", first.Token);
		var preview = await _service.PreviewAsync("cloudflare");
		var outcome = await _service.UndoAsync(preview.Token);
		Assert.Equal(DnsOutcome.Undone, outcome.Status);
		Assert.Equal(preview.Script, _system.Ran[^1]);
		Assert.False(File.Exists(BackupPath));
		Assert.False((await _service.PreviewAsync("cloudflare")).Applied);
	}

	[Fact]
	public async Task A_failed_undo_keeps_the_old_settings_so_it_can_be_tried_again()
	{
		var first = await _service.PreviewAsync("cloudflare");
		await _service.ApplyAsync("cloudflare", first.Token);
		_system.Result = new ElevatedRun.Exited(3);
		var preview = await _service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsScriptFailedError>(() => _service.UndoAsync(preview.Token));
		Assert.True(File.Exists(BackupPath));
	}

	[Fact]
	public async Task A_declined_undo_keeps_the_old_settings()
	{
		var first = await _service.PreviewAsync("cloudflare");
		await _service.ApplyAsync("cloudflare", first.Token);
		_system.Result = ElevatedRun.Declined;
		var preview = await _service.PreviewAsync("cloudflare");
		Assert.Equal(DnsOutcome.Declined, (await _service.UndoAsync(preview.Token)).Status);
		Assert.True(File.Exists(BackupPath));
	}

	[Fact]
	public async Task Undo_with_nothing_to_undo_says_so()
	{
		await Assert.ThrowsAsync<NoDnsBackupError>(() => _service.UndoAsync("anything"));
	}

	[Fact]
	public async Task Undo_with_a_stale_token_shows_the_script_instead_of_running_it()
	{
		var first = await _service.PreviewAsync("cloudflare");
		await _service.ApplyAsync("cloudflare", first.Token);
		var outcome = await _service.UndoAsync("stale");
		Assert.Equal(DnsOutcome.Changed, outcome.Status);
		Assert.True(outcome.Preview!.Applied);
		Assert.Single(_system.Ran);
	}

	[Fact]
	public async Task A_corrupt_saved_file_is_a_named_error_for_preview_apply_and_undo_and_is_left_alone()
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(BackupPath, "{ not json", TestContext.Current.CancellationToken);
		var error = await Assert.ThrowsAsync<DnsBackupCorruptError>(() => _service.PreviewAsync("cloudflare"));
		Assert.Contains(BackupPath, error.Message);
		await Assert.ThrowsAsync<DnsBackupCorruptError>(() => _service.ApplyAsync("cloudflare", "x"));
		await Assert.ThrowsAsync<DnsBackupCorruptError>(() => _service.UndoAsync("x"));
		Assert.Equal("{ not json", await File.ReadAllTextAsync(BackupPath, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task A_provider_outside_the_fixed_list_is_refused_before_anything_is_read_or_run()
	{
		await Assert.ThrowsAsync<UnknownDohProviderError>(() => _service.PreviewAsync("evil"));
		await Assert.ThrowsAsync<UnknownDohProviderError>(() => _service.ApplyAsync("evil", "x"));
		Assert.Empty(_system.Ran);
	}

	[Fact]
	public async Task A_machine_with_no_adapter_up_is_refused()
	{
		_system.Now = new DnsSnapshot([], []);
		await Assert.ThrowsAsync<NoDnsAdaptersError>(() => _service.PreviewAsync("cloudflare"));
	}

	[Fact]
	public async Task A_windows_without_the_commands_is_said_before_anything_and_apply_is_refused()
	{
		_system.Now = _system.Now with { DohSupported = false };
		var preview = await _service.PreviewAsync("cloudflare");
		Assert.False(preview.DohSupported);
		await Assert.ThrowsAsync<NoEncryptedDnsError>(() => _service.ApplyAsync("cloudflare", preview.Token));
		Assert.Empty(_system.Ran);
		Assert.False(File.Exists(BackupPath));
	}

	[Fact]
	public async Task A_run_that_never_ends_is_given_up_on_with_the_outcome_unknown_and_undo_stays_available()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		_system.Hangs = true;
		var preview = await service.PreviewAsync("cloudflare");
		var error = await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.ApplyAsync("cloudflare", preview.Token));
		Assert.Contains("check", error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.True(File.Exists(BackupPath));
		// The lock was let go: the page can ask again, and sees Undo.
		Assert.True((await service.PreviewAsync("cloudflare")).Applied);
	}

	[Fact]
	public async Task An_undo_that_never_ends_keeps_the_saved_settings()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		var first = await service.PreviewAsync("cloudflare");
		await service.ApplyAsync("cloudflare", first.Token);
		_system.Hangs = true;
		var undo = await service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.UndoAsync(undo.Token));
		Assert.True(File.Exists(BackupPath));
	}

	[Fact]
	public async Task A_run_given_up_on_blocks_new_runs_until_it_ends_so_an_old_prompt_cannot_apply_after_an_undo()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		_system.Hangs = true;
		var preview = await service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.ApplyAsync("cloudflare", preview.Token));
		_system.Hangs = false;
		var undo = await service.PreviewAsync("cloudflare");
		// The old prompt could still be accepted: nothing may run, or it would apply with the saved settings already gone.
		var waiting = await Assert.ThrowsAsync<DnsEarlierRunWaitingError>(() => service.UndoAsync(undo.Token));
		Assert.Contains("admin", waiting.Message, StringComparison.OrdinalIgnoreCase);
		await Assert.ThrowsAsync<DnsEarlierRunWaitingError>(() => service.ApplyAsync("cloudflare", undo.Token));
		Assert.Single(_system.Ran);
		Assert.True(File.Exists(BackupPath));
		// Previews still work, and show the real state.
		Assert.True((await service.PreviewAsync("cloudflare")).Applied);
		_system.Finish();
		var outcome = await service.UndoAsync(undo.Token);
		Assert.Equal(DnsOutcome.Undone, outcome.Status);
		Assert.False(File.Exists(BackupPath));
	}

	[Fact]
	public async Task A_run_given_up_on_that_finishes_changes_nothing_but_the_marker()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		_system.Hangs = true;
		var preview = await service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.ApplyAsync("cloudflare", preview.Token));
		var saved = await File.ReadAllTextAsync(BackupPath, TestContext.Current.CancellationToken);
		_system.Finish();
		Assert.Equal(saved, await File.ReadAllTextAsync(BackupPath, TestContext.Current.CancellationToken));
		Assert.True((await service.PreviewAsync("cloudflare")).Applied);
	}

	[Fact]
	public async Task An_apply_given_up_on_that_then_ends_declined_leaves_no_saved_file_to_claim_it_was_applied()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		_system.Hangs = true;
		var preview = await service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.ApplyAsync("cloudflare", preview.Token));
		_system.Result = ElevatedRun.Declined;
		_system.Finish();
		Assert.False((await service.PreviewAsync("cloudflare")).Applied);
		Assert.False(File.Exists(BackupPath));
	}

	[Fact]
	public async Task An_apply_given_up_on_that_then_ends_without_encrypted_dns_support_leaves_no_saved_file()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		_system.Hangs = true;
		var preview = await service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.ApplyAsync("cloudflare", preview.Token));
		_system.Result = new ElevatedRun.Exited(DnsScript.NoEncryptedDnsExit);
		_system.Finish();
		Assert.False((await service.PreviewAsync("cloudflare")).Applied);
		Assert.False(File.Exists(BackupPath));
	}

	[Fact]
	public async Task An_undo_given_up_on_that_then_ends_declined_keeps_the_saved_file_because_the_settings_are_still_changed()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		var first = await service.PreviewAsync("cloudflare");
		await service.ApplyAsync("cloudflare", first.Token);
		_system.Hangs = true;
		var undo = await service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.UndoAsync(undo.Token));
		_system.Result = ElevatedRun.Declined;
		_system.Finish();
		Assert.True((await service.PreviewAsync("cloudflare")).Applied);
		Assert.True(File.Exists(BackupPath));
	}

	[Fact]
	public async Task A_run_given_up_on_that_fails_later_is_reported_not_lost()
	{
		var faults = new List<Exception>();
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, faults.Add, TimeSpan.FromMilliseconds(150));
		_system.Hangs = true;
		var preview = await service.PreviewAsync("cloudflare");
		await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.ApplyAsync("cloudflare", preview.Token));
		_system.Crash(new InvalidOperationException("elevated start failed"));
		Assert.Equal("elevated start failed", Assert.Single(faults).Message);
		// And the marker cleared: the person can go on.
		_system.Hangs = false;
		var undo = await service.PreviewAsync("cloudflare");
		Assert.Equal(DnsOutcome.Undone, (await service.UndoAsync(undo.Token)).Status);
	}

	[Fact]
	public async Task The_unknown_outcome_message_says_an_open_prompt_can_still_change_things()
	{
		var service = new DnsService(_system, BackupPath, () => DateTimeOffset.UnixEpoch, _ => { }, TimeSpan.FromMilliseconds(150));
		_system.Hangs = true;
		var preview = await service.PreviewAsync("cloudflare");
		var error = await Assert.ThrowsAsync<DnsOutcomeUnknownError>(() => service.ApplyAsync("cloudflare", preview.Token));
		Assert.Contains("prompt", error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("still", error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task The_undo_preview_lists_what_undo_will_set_including_what_a_tampered_file_says()
	{
		var first = await _service.PreviewAsync("cloudflare");
		await _service.ApplyAsync("cloudflare", first.Token);
		// Valid-looking, but not what was saved: the list is built from the file as it is now, the same data the script is.
		var saved = DnsBackup.Load(BackupPath);
		DnsBackup.Save(BackupPath, saved with { Adapters = [new DnsAdapter("Ethernet", ["6.6.6.6"], []), saved.Adapters[1]] });
		var preview = await _service.PreviewAsync("cloudflare");
		Assert.Contains("Ethernet: 6.6.6.6", preview.Changes);
		Assert.Contains("Wi-Fi: automatic (DHCP)", preview.Changes);
		Assert.Contains("'6.6.6.6'", preview.Script);
	}

	[Fact]
	public async Task The_apply_preview_has_no_undo_list()
	{
		Assert.Empty((await _service.PreviewAsync("cloudflare")).Changes);
	}

	[Fact]
	public async Task Names_are_shown_with_hidden_characters_spelled_out_and_the_script_runs_with_the_real_ones()
	{
		_system.Now = new DnsSnapshot([new DnsAdapter("a\u202Eb", [], [])], []);
		var preview = await _service.PreviewAsync("cloudflare");
		Assert.Equal(["a\\u202Eb"], preview.Adapters);
		Assert.Contains("\\u202E", preview.ScriptShown);
		Assert.DoesNotContain("\u202E", preview.ScriptShown, StringComparison.Ordinal);
		Assert.Contains("\u202E", preview.Script, StringComparison.Ordinal);
		await _service.ApplyAsync("cloudflare", preview.Token);
		Assert.Equal([preview.Script], _system.Ran);
	}

	[Fact]
	public async Task Two_clicks_at_once_run_the_script_once()
	{
		var preview = await _service.PreviewAsync("cloudflare");
		var results = await Task.WhenAll(Settle(_service.ApplyAsync("cloudflare", preview.Token)), Settle(_service.ApplyAsync("cloudflare", preview.Token)));
		Assert.Single(_system.Ran);
		Assert.Single(results, r => r is DnsOutcome);
		Assert.Single(results, r => r is DnsAlreadyAppliedError);
	}

	private static async Task<object> Settle(Task<DnsOutcome> task)
	{
		try { return await task; }
		catch (DnsAlreadyAppliedError error) { return error; }
	}
}

public sealed class DnsBackupTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "zing-dnsb-" + Guid.NewGuid().ToString("N"));
	private string Path_ => Path.Combine(_directory, "dns-before.json");

	public void Dispose()
	{
		if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
	}

	private static DnsBackupFile Sample() => new("quad9", new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), [new DnsAdapter("Ethernet", ["10.0.0.1"], ["fd00::1"]), new DnsAdapter("Wi-Fi", [], null)], [new DohEntry("9.9.9.9", "https://dns.quad9.net/dns-query", false, false)]);

	[Fact]
	public void What_is_saved_comes_back_the_same()
	{
		DnsBackup.Save(Path_, Sample());
		var loaded = DnsBackup.Load(Path_);
		Assert.Equal("quad9", loaded.Provider);
		Assert.Equal(Sample().SavedAt, loaded.SavedAt);
		Assert.Equal(["10.0.0.1"], loaded.Adapters[0].V4);
		Assert.Equal(["fd00::1"], loaded.Adapters[0].V6);
		Assert.Null(loaded.Adapters[1].V6);
		Assert.Equal(Sample().Doh, loaded.Doh);
	}

	[Fact]
	public void A_missing_file_is_a_named_error()
	{
		Assert.Throws<NoDnsBackupError>(() => DnsBackup.Load(Path_));
		Assert.False(DnsBackup.Exists(Path_));
	}

	[Theory]
	[InlineData("")]
	[InlineData("{")]
	[InlineData("[]")]
	[InlineData("{}")]
	[InlineData("""{"version":2,"provider":"quad9","savedAt":"2026-11-03T09:00:00+00:00","adapters":[],"doh":[]}""")]
	[InlineData("""{"version":1,"provider":"nobody","savedAt":"2026-11-03T09:00:00+00:00","adapters":[],"doh":[]}""")]
	[InlineData("""{"version":1,"provider":"quad9","savedAt":"2026-11-03T09:00:00+00:00","adapters":[{"name":"E","v4":["not an ip"],"v6":null}],"doh":[]}""")]
	[InlineData("""{"version":1,"provider":"quad9","savedAt":"2026-11-03T09:00:00+00:00","adapters":[null],"doh":[]}""")]
	[InlineData("""{"version":1,"provider":"quad9","savedAt":"2026-11-03T09:00:00+00:00","adapters":[],"doh":[],"extra":1}""")]
	public void A_file_that_is_not_what_was_saved_is_a_named_error(string text)
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(Path_, text);
		var error = Assert.Throws<DnsBackupCorruptError>(() => DnsBackup.Load(Path_));
		Assert.Contains(Path_, error.Message);
	}

	[Fact]
	public void Delete_removes_the_file_and_a_missing_one_is_not_an_error()
	{
		DnsBackup.Save(Path_, Sample());
		DnsBackup.Delete(Path_);
		Assert.False(File.Exists(Path_));
		DnsBackup.Delete(Path_);
	}
}
