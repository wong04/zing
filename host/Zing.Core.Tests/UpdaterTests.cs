using Zing.Core;

namespace Zing.Core.Tests;

public sealed class UpdaterTests
{
	private sealed class FakeUpdateSource : IUpdateSource
	{
		public string CurrentVersion => "0.9.0";
		public string? Newer { get; set; }
		public Exception? FindError { get; set; }
		public TaskCompletionSource<string?>? Gate { get; set; }
		public int Finds { get; private set; }
		public int Downloads { get; private set; }
		public int Restarts { get; private set; }
		public int AppliedOnExit { get; private set; }

		public Task<string?> FindAsync(CancellationToken cancel)
		{
			Finds++;
			if (FindError is not null) return Task.FromException<string?>(FindError);
			return Gate?.Task ?? Task.FromResult(Newer);
		}

		public Task DownloadAsync(CancellationToken cancel)
		{
			Downloads++;
			return Task.CompletedTask;
		}

		public void RestartIntoUpdate() => Restarts++;

		public void ApplyOnExit() => AppliedOnExit++;
	}

	private readonly ManualScheduler _scheduler = new();
	private readonly FakeUpdateSource _source = new();
	private bool _enabled = true;
	private readonly List<UpdateStatus> _seen = [];
	private readonly List<Exception> _failures = [];

	private Updater Make(bool withSource = true)
	{
		var updater = new Updater(withSource ? _source : null, _scheduler, () => _enabled);
		updater.Changed += state => _seen.Add(state.Status);
		updater.Failed += _failures.Add;
		return updater;
	}

	[Fact]
	public void Start_checks_after_thirty_seconds_then_every_four_hours()
	{
		var updater = Make();
		updater.Start();
		_scheduler.Advance(Updater.FirstCheck - TimeSpan.FromSeconds(1));
		Assert.Equal(0, _source.Finds);
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal(1, _source.Finds);
		_scheduler.Advance(Updater.Interval - TimeSpan.FromSeconds(1));
		Assert.Equal(1, _source.Finds);
		_scheduler.Advance(TimeSpan.FromSeconds(1));
		Assert.Equal(2, _source.Finds);
	}

	[Fact]
	public void Disabled_at_fire_time_skips_the_check()
	{
		var updater = Make();
		updater.Start();
		_enabled = false;
		_scheduler.Advance(Updater.FirstCheck);
		Assert.Equal(0, _source.Finds);
		Assert.Equal(1, _scheduler.PendingCount);
		_enabled = true;
		_scheduler.Advance(Updater.Interval);
		Assert.Equal(1, _source.Finds);
	}

	[Fact]
	public void A_found_update_downloads_then_is_ready()
	{
		_source.Newer = "0.9.1";
		var updater = Make();
		updater.Start();
		_scheduler.Advance(Updater.FirstCheck);
		Assert.Equal([UpdateStatus.Checking, UpdateStatus.Downloading, UpdateStatus.Ready], _seen);
		Assert.Equal(new UpdateState(UpdateStatus.Ready, "0.9.0", "0.9.1"), updater.State);
		Assert.Equal(1, _source.Downloads);
	}

	[Fact]
	public void No_update_is_up_to_date()
	{
		var updater = Make();
		updater.Start();
		_scheduler.Advance(Updater.FirstCheck);
		Assert.Equal([UpdateStatus.Checking, UpdateStatus.UpToDate], _seen);
		Assert.Equal(new UpdateState(UpdateStatus.UpToDate, "0.9.0", null), updater.State);
		Assert.Equal(0, _source.Downloads);
	}

	[Fact]
	public void A_failed_check_retries_at_the_next_interval()
	{
		_source.FindError = new HttpRequestException("offline");
		var updater = Make();
		updater.Start();
		_scheduler.Advance(Updater.FirstCheck);
		Assert.Equal(UpdateStatus.Failed, updater.State.Status);
		Assert.Single(_failures);
		Assert.IsType<HttpRequestException>(_failures[0]);

		_source.FindError = null;
		_scheduler.Advance(Updater.Interval);
		Assert.Equal(2, _source.Finds);
		Assert.Equal(UpdateStatus.UpToDate, updater.State.Status);
		Assert.Single(_failures);
	}

	[Fact]
	public void Ready_stops_further_checks()
	{
		_source.Newer = "0.9.1";
		var updater = Make();
		updater.Start();
		_scheduler.Advance(Updater.FirstCheck);
		_scheduler.Advance(Updater.Interval * 3);
		Assert.Equal(1, _source.Finds);
		Assert.Equal(0, _scheduler.PendingCount);
		Assert.Equal(UpdateStatus.Ready, updater.State.Status);
	}

	[Fact]
	public async Task Check_now_ignores_the_setting()
	{
		_enabled = false;
		var updater = Make();
		var state = await updater.CheckNowAsync();
		Assert.Equal(UpdateStatus.UpToDate, state.Status);
		Assert.Equal(1, _source.Finds);
	}

	[Fact]
	public async Task A_check_started_while_one_is_in_flight_returns_the_current_state()
	{
		_source.Gate = new TaskCompletionSource<string?>();
		var updater = Make();
		var first = updater.CheckNowAsync();
		var second = await updater.CheckNowAsync();
		Assert.Equal(UpdateStatus.Checking, second.Status);
		Assert.Equal(1, _source.Finds);
		_source.Gate.SetResult(null);
		Assert.Equal(UpdateStatus.UpToDate, (await first).Status);
	}

	[Fact]
	public void Relaunch_before_ready_does_nothing()
	{
		var updater = Make();
		updater.Relaunch();
		Assert.Equal(0, _source.Restarts);
	}

	[Fact]
	public async Task Relaunch_twice_restarts_once()
	{
		_source.Newer = "0.9.1";
		var updater = Make();
		await updater.CheckNowAsync();
		updater.Relaunch();
		updater.Relaunch();
		Assert.Equal(1, _source.Restarts);
	}

	[Fact]
	public async Task Quitting_when_ready_applies_on_exit()
	{
		_source.Newer = "0.9.1";
		var updater = Make();
		await updater.CheckNowAsync();
		updater.Quitting();
		updater.Quitting();
		Assert.Equal(1, _source.AppliedOnExit);
	}

	[Fact]
	public async Task Quitting_after_relaunch_does_not_apply_again()
	{
		_source.Newer = "0.9.1";
		var updater = Make();
		await updater.CheckNowAsync();
		updater.Relaunch();
		updater.Quitting();
		Assert.Equal(0, _source.AppliedOnExit);
	}

	[Fact]
	public void Quitting_when_not_ready_does_nothing()
	{
		var updater = Make();
		updater.Quitting();
		Assert.Equal(0, _source.AppliedOnExit);
	}

	[Fact]
	public async Task Without_a_source_everything_is_unavailable()
	{
		var updater = Make(withSource: false);
		updater.Start();
		Assert.Equal(0, _scheduler.PendingCount);
		Assert.Equal(UpdateStatus.Unavailable, (await updater.CheckNowAsync()).Status);
		updater.Relaunch();
		updater.Quitting();
		Assert.Equal(UpdateStatus.Unavailable, updater.State.Status);
		Assert.Empty(_seen);
	}
}
