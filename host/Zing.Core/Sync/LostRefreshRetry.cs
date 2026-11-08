namespace Zing.Core.Sync;

/// <summary>
/// The sync engine's lost-refresh retry for a client no engine runs on, such as a restore's (final review I3): a refresh that got no
/// answer is sent again <see cref="SyncEngine.RefreshRetry"/> later, and again while none is answered, until
/// <see cref="SyncEngine.RefreshRetryWindow"/> after the first, inside the server's 2-minute retry window. Past it, the same token
/// would read as reuse and sign out every PC, so nothing more is sent. A retry that fails is told to the warning sink.
/// </summary>
public sealed class LostRefreshRetry : IDisposable
{
	private readonly Lock _lock = new();
	private readonly SyncClient _client;
	private readonly IScheduler _scheduler;
	private readonly IClock _clock;
	private readonly Action<string, Exception> _warn;
	private IDisposable? _next;
	private long? _unansweredSince;
	private bool _disposed;

	public LostRefreshRetry(SyncClient client, IScheduler scheduler, IClock clock, Action<string, Exception> warn)
	{
		_client = client;
		_scheduler = scheduler;
		_clock = clock;
		_warn = warn;
		client.RefreshLost += OnRefreshLost;
	}

	/// <summary>The last retry started, while it runs: tests wait on it.</summary>
	internal Task Retrying { get; private set; } = Task.CompletedTask;

	private void OnRefreshLost()
	{
		lock (_lock)
		{
			if (_disposed) return;
			var now = _clock.Now();
			_unansweredSince ??= now;
			_next?.Dispose();
			_next = null;
			if (now - _unansweredSince >= SyncEngine.RefreshRetryWindow.TotalMilliseconds)
			{
				_unansweredSince = null;
				return;
			}
			_next = _scheduler.Schedule(SyncEngine.RefreshRetry, Retry);
		}
	}

	private void Retry()
	{
		lock (_lock)
		{
			if (_disposed) return;
			_next = null;
		}
		Retrying = RetryAsync();
	}

	private async Task RetryAsync()
	{
		if (!_client.RefreshUnanswered)
		{
			// A request of the restore's own got its refresh answered meanwhile.
			Answered();
			return;
		}
		try
		{
			await _client.RetryRefreshAsync(CancellationToken.None);
			Answered();
		}
		catch (Exception error)
		{
			// Unanswered again, OnRefreshLost has scheduled the next one; anything else ends the retries here.
			_warn("retry a sync refresh that got no answer", error);
		}
	}

	private void Answered()
	{
		lock (_lock) _unansweredSince = null;
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;
			_disposed = true;
			_next?.Dispose();
		}
		_client.RefreshLost -= OnRefreshLost;
	}
}
