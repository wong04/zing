using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zing.Core;

/// <summary>The installer-backed side of updating; Zing.Host implements it over Velopack so Core stays free of it.</summary>
public interface IUpdateSource
{
	string CurrentVersion { get; }

	/// <summary>The newer version, or null when this one is current.</summary>
	Task<string?> FindAsync(CancellationToken cancel);

	Task DownloadAsync(CancellationToken cancel);

	void RestartIntoUpdate();

	void ApplyOnExit();
}

[JsonConverter(typeof(UpdateStatusConverter))]
public enum UpdateStatus { Idle, Checking, Downloading, Ready, UpToDate, Failed, Unavailable }

public sealed class UpdateStatusConverter() : JsonStringEnumConverter<UpdateStatus>(JsonNamingPolicy.CamelCase);

public sealed record UpdateState(UpdateStatus Status, string CurrentVersion, string? ReadyVersion);

/// <summary>
/// Checks for updates shortly after startup and every few hours, downloads quietly, and restarts only when told to
/// (the pill) or on quit. A null source is a build that is not installed: nothing is ever checked. Runs on the UI thread.
/// </summary>
public sealed class Updater(IUpdateSource? source, IScheduler scheduler, Func<bool> enabled)
{
	public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
	public static readonly TimeSpan Interval = TimeSpan.FromHours(4);

	private readonly CancellationTokenSource _cancel = new();
	private IDisposable? _timer;
	private bool _started;
	private bool _checking;
	private bool _restarting;
	private bool _quitting;

	public UpdateState State { get; private set; } = source is null
		? new UpdateState(UpdateStatus.Unavailable, "", null)
		: new UpdateState(UpdateStatus.Idle, source.CurrentVersion, null);

	public event Action<UpdateState>? Changed;

	/// <summary>Raised once per failed check, relaunch or apply on quit. Handlers must not throw.</summary>
	public event Action<Exception>? Failed;

	public void Start()
	{
		if (source is null || _started) return;
		_started = true;
		ScheduleNext(FirstCheck);
	}

	/// <summary>Checks regardless of the setting. A check already in flight, or an update already ready, returns the current state.</summary>
	public async Task<UpdateState> CheckNowAsync()
	{
		if (source is null || _checking || _quitting || State.Status == UpdateStatus.Ready) return State;
		_checking = true;
		try
		{
			SetState(UpdateStatus.Checking, null);
			var version = await source.FindAsync(_cancel.Token);
			if (version is null)
			{
				SetState(UpdateStatus.UpToDate, null);
				return State;
			}
			SetState(UpdateStatus.Downloading, null);
			await source.DownloadAsync(_cancel.Token);
			SetState(UpdateStatus.Ready, version);
		}
		catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
		{
			SetState(UpdateStatus.Idle, null);
		}
		catch (Exception error)
		{
			SetState(UpdateStatus.Failed, null);
			Failed?.Invoke(error);
		}
		finally
		{
			_checking = false;
		}
		return State;
	}

	public void Relaunch()
	{
		if (source is null || State.Status != UpdateStatus.Ready || _restarting) return;
		_restarting = true;
		try
		{
			source.RestartIntoUpdate();
		}
		// Still Ready: the pill can try again, and quitting applies the update.
		catch (Exception error)
		{
			_restarting = false;
			Failed?.Invoke(error);
		}
	}

	public void Quitting()
	{
		if (source is null || _quitting) return;
		_quitting = true;
		_timer?.Dispose();
		_cancel.Cancel();
		if (State.Status != UpdateStatus.Ready || _restarting) return;
		// Nothing may stop Zing quitting; the update stays downloaded for the next try.
		try
		{
			source.ApplyOnExit();
		}
		catch (Exception error)
		{
			Failed?.Invoke(error);
		}
	}

	private void ScheduleNext(TimeSpan delay) => _timer = scheduler.Schedule(delay, OnTimer);

	private void OnTimer()
	{
		if (State.Status == UpdateStatus.Ready || _quitting) return;
		ScheduleNext(Interval);
		if (!enabled()) return;
		// CheckNowAsync captures every source failure into the Failed state, so the discarded task has nothing to leak.
		_ = CheckNowAsync();
	}

	private void SetState(UpdateStatus status, string? readyVersion)
	{
		State = State with { Status = status, ReadyVersion = readyVersion };
		Changed?.Invoke(State);
	}
}
