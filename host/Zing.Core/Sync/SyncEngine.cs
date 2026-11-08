using System.Net;
using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>
/// What Settings shows of the engine: the last sync's error, as a code the renderer acts on and a message it shows (null after a
/// sync that worked), and how many logins haven't uploaded yet.
/// </summary>
public sealed record SyncStatus(string? Error, string? Message, int Unsent);

/// <summary>A push kept meeting newer copies from another PC; what is left goes next time.</summary>
public sealed class SyncKeepsConflictingException(int left)
	: Exception($"Another PC kept changing the same logins while this one synced, so {left} changes are left for the next sync.");

/// <summary>Upload this PC's copy asked for when the last sync didn't stop on a rollback or a held-back login (Ruling 56).</summary>
public sealed class SyncUploadNotOfferedException()
	: Exception("Upload this PC's copy is only for when the sync server went back to an older copy or held a login back.");

/// <summary>Another PC kept replacing the account manifest while this one wrote it.</summary>
public sealed class SyncManifestBusyException()
	: Exception("Another PC kept updating the sync list while this one synced. Zing tries again in 5 minutes.");

/// <summary>
/// The open vault's sync with the server (spec, Sync design): pull since the last revision, push what changed here in batches of
/// 100, merge on a conflict and push again, then write the account manifest. It runs at start, 2 s after a change here (debounced),
/// and every 5 minutes. It never needs Windows Hello (spec decision 10): summaries open with the index key, and passwords move sealed.
/// </summary>
/// <remarks>
/// Locking: a sync holds the vault's key lock (<see cref="SyncKeys.EnterGateAsync"/>) for its whole run, so it never overlaps a
/// turn-on, a password change or another sync, and settles pending keys inside it first. Under it, every store call is synchronous
/// under the store's own <c>_gate</c>, which is never held across an await; the client's refresh lock is only taken inside a request.
/// So the order is always key lock, then refresh lock or store gate, and neither of those waits on anything else.
/// </remarks>
public sealed class SyncEngine : IDisposable
{
	/// <summary>A change here is sent this long after the last one (spec).</summary>
	public static readonly TimeSpan AfterChange = TimeSpan.FromSeconds(2);

	/// <summary>A sync runs this often while Zing is open (spec).</summary>
	public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

	/// <summary>
	/// After a refresh got no answer, the next try comes this soon (N4): the server takes the same token again only within 2 minutes
	/// of first seeing it, and the refresh itself may have waited 90 s.
	/// </summary>
	public static readonly TimeSpan RefreshRetry = TimeSpan.FromSeconds(10);

	/// <summary>How long after the first unanswered refresh <see cref="RefreshRetry"/> is used, within the server's 2-minute window.</summary>
	private static readonly TimeSpan RefreshRetryWindow = TimeSpan.FromSeconds(110);

	private const int PushRounds = 5;
	private const int ManifestAttempts = 3;

	private readonly VaultStore _vault;
	private readonly SyncClient _client;
	private readonly IScheduler _scheduler;
	private readonly IClock _clock;
	private readonly CancellationTokenSource _stopping = new();
	private readonly Lock _lock = new();
	private IDisposable? _soon;
	private IDisposable? _next;
	private bool _started;
	private bool _running;
	private bool _again;
	private bool _disposed;
	private string _syncedAs = "";
	private long? _refreshUnansweredSince;
	private (string Code, string Message)? _error;
	private TaskCompletionSource _ran = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <param name="client">The vault's one client (N6), shared with sign-in, sign-out and the key operations.</param>
	public SyncEngine(VaultStore vault, SyncClient client, IScheduler scheduler, IClock clock)
	{
		_vault = vault;
		_client = client;
		_scheduler = scheduler;
		_clock = clock;
	}

	/// <summary>Fires after each sync, and after each change here that is waiting to upload.</summary>
	public event Action? StatusChanged;

	public SyncStatus Status
	{
		get
		{
			(string Code, string Message)? error;
			lock (_lock) error = _error;
			return new SyncStatus(error?.Code, error?.Message, _vault.UnsentLogins);
		}
	}

	/// <summary>Completes when the next scheduled sync has finished, whatever its outcome: tests' signal, read before firing it.</summary>
	internal Task NextRun
	{
		get
		{
			lock (_lock) return _ran.Task;
		}
	}

	/// <summary>Follows the vault's changes and schedules the first sync at once (spec: when Zing starts).</summary>
	public void Start()
	{
		lock (_lock)
		{
			if (_started || _disposed) return;
			_started = true;
			_syncedAs = SyncedAs();
		}
		_vault.Edited += OnEdited;
		_vault.SyncChanged += OnSyncChanged;
		_client.RefreshLost += OnRefreshLost;
		Soon(TimeSpan.Zero);
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;
			_disposed = true;
			_soon?.Dispose();
			_next?.Dispose();
		}
		_vault.Edited -= OnEdited;
		_vault.SyncChanged -= OnSyncChanged;
		_client.RefreshLost -= OnRefreshLost;
		// Not disposed: a sync still running reads its token until it notices.
		_stopping.Cancel();
	}

	/// <summary>
	/// A refresh got no answer, in a sync or in anything else the shared client did, such as a turn-on or a sign-out (M12): the next
	/// sync comes in <see cref="RefreshRetry"/>, while the server would still take that token as a retry. A sync running now schedules
	/// it itself when it ends.
	/// </summary>
	private void OnRefreshLost()
	{
		try
		{
			lock (_lock)
			{
				if (_disposed) return;
				_refreshUnansweredSince ??= _clock.Now();
				if (_running) return;
				_next?.Dispose();
				_next = _scheduler.Schedule(RefreshRetry, Run);
			}
		}
		catch (Exception error)
		{
			// The caller is mid-request and must see its own error (N2); this one goes to Settings, since no retry is scheduled.
			Report(error);
		}
	}

	private void OnEdited()
	{
		Soon(AfterChange);
		StatusChanged?.Invoke();
	}

	/// <summary>A sign-in, a sign-out or sync turned on: sync soon. A refresh saving its rotated token changes none of that, so it doesn't.</summary>
	private void OnSyncChanged()
	{
		var now = SyncedAs();
		lock (_lock)
		{
			if (now == _syncedAs) return;
			_syncedAs = now;
		}
		Soon(AfterChange);
		StatusChanged?.Invoke();
	}

	/// <summary>Who this PC syncs as, and whether sync is on for them, as one comparable value.</summary>
	private string SyncedAs()
	{
		try
		{
			return $"{_vault.SyncAccount()?.UserId}|{_vault.SyncKeys?.UserId}|{_vault.PendingSyncKeys?.UserId}";
		}
		catch (SyncAccountUnreadableException)
		{
			return "unreadable";
		}
	}

	/// <summary>Debounced: a sync already waiting to start is moved to <paramref name="delay"/> from now.</summary>
	private void Soon(TimeSpan delay)
	{
		lock (_lock)
		{
			if (_disposed) return;
			_soon?.Dispose();
			_soon = _scheduler.Schedule(delay, Run);
		}
	}

	/// <summary>Starts a sync, or, while one runs, has another run straight after it, so a change made meanwhile isn't left for 5 minutes.</summary>
	private void Run()
	{
		lock (_lock)
		{
			if (_disposed) return;
			if (_running)
			{
				_again = true;
				return;
			}
			_running = true;
		}
		_ = LoopAsync();
	}

	private async Task LoopAsync()
	{
		while (true)
		{
			try
			{
				await SyncAsync(_stopping.Token);
			}
			catch (Exception) when (_stopping.IsCancellationRequested)
			{
				// Disposed while syncing (the vault was turned off or replaced): whatever the run met on its way out ends with it.
			}
			catch (Exception)
			{
				// SyncAsync has put the error in Status, which Settings shows (sync:state); the next sync is scheduled below.
			}
			lock (_lock)
			{
				_refreshUnansweredSince = _client.RefreshUnanswered ? _refreshUnansweredSince ?? _clock.Now() : null;
				if (_disposed || !_again)
				{
					_running = false;
					break;
				}
				_again = false;
			}
		}
		try
		{
			ScheduleNext();
		}
		catch (Exception error)
		{
			// Nothing would sync again until Zing restarts, so Settings says so (M6).
			Report(error);
		}
		finally
		{
			TaskCompletionSource ran;
			lock (_lock)
			{
				ran = _ran;
				_ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			}
			ran.SetResult();
		}
	}

	/// <summary>The next sync: in 5 minutes, or soon after a refresh that got no answer, while the server would still take it as a retry.</summary>
	private void ScheduleNext()
	{
		lock (_lock)
		{
			if (_disposed) return;
			_next?.Dispose();
			var retry = _refreshUnansweredSince is { } since && _clock.Now() - since < RefreshRetryWindow.TotalMilliseconds;
			_next = _scheduler.Schedule(retry ? RefreshRetry : Every, Run);
		}
	}

	/// <summary>
	/// One sync, under the vault's key lock: settle pending keys (Task 12's carry-over), then, if sync is on for the account signed in,
	/// make due tombstones, read the manifest, pull, check the manifest against what came, push, and write the manifest. Every error
	/// is put in <see cref="Status"/> for Settings and raised.
	/// </summary>
	public async Task SyncAsync(CancellationToken cancellation = default)
	{
		try
		{
			await RunAsync(cancellation);
			Report(null);
		}
		catch (Exception error)
		{
			Report(error);
			throw;
		}
	}

	private async Task RunAsync(CancellationToken cancellation)
	{
		using var gate = await SyncKeys.EnterGateAsync(_vault, cancellation);
		if (_vault.SyncAccount() is not { } signedIn) return;
		await SyncKeys.SettlePendingAsync(_vault, _client, signedIn, cancellation);
		if (_vault.SyncKeys?.UserId != signedIn.UserId) return;
		var user = signedIn.UserId;
		_vault.StartSyncAs(user);
		_vault.TombstoneDue(_clock.Now());
		for (var attempt = 0; attempt < ManifestAttempts; attempt++)
		{
			if (!await RoundAsync(user, cancellation)) continue;
			_vault.DequeueSent();
			return;
		}
		throw new SyncManifestBusyException();
	}

	/// <summary>One pass; false when another PC replaced the manifest while this one wrote it, so the caller goes round again.</summary>
	private async Task<bool> RoundAsync(string user, CancellationToken cancellation)
	{
		// Read before the pull: every PC writes its items before its manifest, so whatever this lists, the pull brings.
		var manifest = await _client.GetManifestAsync(cancellation, asUser: user);
		var progress = _vault.SyncProgressFor(user);
		RollbackGuard.CheckManifest(progress.ManifestFloor, manifest?.Version);
		if (manifest is not null) RollbackGuard.CheckManifestAtFloor(progress.ManifestFloor, progress.ManifestHash, manifest);
		IReadOnlyList<SyncManifestEntry> listed = manifest is null ? [] : _vault.OpenManifest(manifest);
		if (manifest is not null) _vault.RaiseManifestFloor(user, manifest.Version, manifest.Blob);

		var served = await PullAsync(user, cancellation);
		if (served is null)
		{
			RollbackGuard.CheckListed(listed, _vault.KnownVersion);
			_vault.RequireStillListed(user, listed);
		}
		else RollbackGuard.CheckListed(listed, id => served.TryGetValue(id, out var version) ? version : null);
		_vault.MarkListed(user, listed);
		// Checked before anything is taken as purged, so a server holding back a login can't have it deleted here: a listed one fails
		// the check above, and one no manifest listed yet fails MarkPurged's own (Ruling 56).
		if (served is not null) _vault.MarkPurged(user, served.Keys);

		await PushAsync(user, cancellation);
		return await WriteManifestAsync(user, manifest, listed, cancellation);
	}

	/// <summary>
	/// Every change since the cursor, a page at a time, each stored before the cursor moves past it. A cursor older than the server's
	/// tombstone horizon (410, Ruling 17) means a full resync from 0, and a full resync moves the cursor only once it is complete, so
	/// one cut short starts again. Answers what a full resync served (item and version), or null for a delta.
	/// </summary>
	private async Task<Dictionary<string, long>?> PullAsync(string user, CancellationToken cancellation)
	{
		var since = _vault.SyncProgressFor(user).Cursor;
		var served = since == 0 ? new Dictionary<string, long>(StringComparer.Ordinal) : null;
		while (true)
		{
			SyncChanges page;
			try
			{
				page = await _client.GetChangesAsync(since, cancellation: cancellation, asUser: user);
			}
			catch (SyncHttpException gone) when (gone.Status == HttpStatusCode.Gone && served is null)
			{
				since = 0;
				served = new Dictionary<string, long>(StringComparer.Ordinal);
				continue;
			}
			SyncWire.CheckPage(since, page);
			RollbackGuard.CheckRevision(_vault.SyncProgressFor(user).RevisionFloor, page.Revision);
			var next = page.More ? page.Items[^1].Revision : page.Revision;
			var cursor = served is null || !page.More ? next : (long?)null;
			_vault.ApplyFromServer(user, page.Items, cursor, page.Revision);
			if (served is not null)
			{
				foreach (var item in page.Items) served[item.ItemId] = item.Version;
			}
			if (!page.More) return served;
			since = next;
		}
	}

	/// <summary>
	/// Everything changed here, in batches of 100, each over the server copy it was based on. The server's newer copy of a refused
	/// item is merged in (or found to be this one, already uploaded) and goes in the next round.
	/// </summary>
	private async Task PushAsync(string user, CancellationToken cancellation)
	{
		for (var round = 0; round < PushRounds; round++)
		{
			var unsent = _vault.Unsent();
			if (unsent.Count == 0) return;
			foreach (var batch in unsent.Chunk(SyncKeys.PushBatch))
			{
				var pushed = await _client.PostChangesAsync([.. batch.Select(SyncWire.Push)], cancellation, asUser: user);
				SyncWire.CheckPushAnswer([.. batch.Select(u => u.Record.ItemId)], pushed);
				RollbackGuard.CheckAccepted(_vault.SyncProgressFor(user).RevisionFloor, pushed.Accepted);
				_vault.MarkPushed(user, [.. batch.Select(u => u.Record)], pushed.Accepted);
				_vault.ApplyConflicts(user, batch, pushed.Conflicts);
			}
		}
		var left = _vault.Unsent().Count;
		if (left > 0) throw new SyncKeepsConflictingException(left);
	}

	/// <summary>The manifest this PC's view gives, written over the version read if it differs. False when another PC wrote one meanwhile.</summary>
	private async Task<bool> WriteManifestAsync(string user, VersionedBlob? manifest, IReadOnlyList<SyncManifestEntry> listed, CancellationToken cancellation)
	{
		var entries = _vault.ManifestEntries();
		if (manifest is null ? entries.Count == 0 : entries.SequenceEqual(listed.OrderBy(e => e.ItemId, StringComparer.Ordinal))) return true;
		return await PutManifestAsync(user, manifest?.Version ?? 0, entries, cancellation);
	}

	private async Task<bool> PutManifestAsync(string user, long baseVersion, IReadOnlyList<SyncManifestEntry> entries, CancellationToken cancellation)
	{
		var blob = _vault.SealManifest(entries, baseVersion + 1);
		long version;
		try
		{
			version = await _client.PutManifestAsync(blob, baseVersion, cancellation, asUser: user);
		}
		catch (SyncConflictException)
		{
			return false;
		}
		if (version != baseVersion + 1) throw new SyncProtocolException($"The sync server stored the manifest as version {version}, not {baseVersion + 1}");
		_vault.RaiseManifestFloor(user, version, blob);
		_vault.MarkListed(user, entries);
		return true;
	}

	/// <summary>
	/// Upload this PC's copy (spec, Rollback protection), the answer to <see cref="ServerRolledBackException"/> and
	/// <see cref="ServerWithheldItemException"/>: everything the server holds now is read without the floors, this PC's copy of every
	/// item is sent over it, and the manifest is written from this PC's view. The floors then start again from what the server holds.
	/// </summary>
	/// <exception cref="SyncUploadNotOfferedException">The last sync didn't stop on a rollback or a held-back login.</exception>
	/// <exception cref="NotSignedInToSyncException">Not signed in, or sync isn't on for the account signed in.</exception>
	public async Task UploadLocalCopyAsync(CancellationToken cancellation = default)
	{
		try
		{
			await UploadAsync(cancellation);
			Report(null);
		}
		catch (Exception error)
		{
			ReportUploadFailure(error);
			throw;
		}
	}

	/// <summary>The errors Settings answers with Upload this PC's copy.</summary>
	public static bool OffersUpload(string? error) => error is "server-rolled-back" or "server-withheld-item";

	private async Task UploadAsync(CancellationToken cancellation)
	{
		using var gate = await SyncKeys.EnterGateAsync(_vault, cancellation);
		// Read under the gate (N3): a second click waits for the first upload, which clears the error, and is then refused.
		if (!OffersUpload(Status.Error)) throw new SyncUploadNotOfferedException();
		var signedIn = _vault.SyncAccount() ?? throw new NotSignedInToSyncException();
		await SyncKeys.SettlePendingAsync(_vault, _client, signedIn, cancellation);
		if (_vault.SyncKeys?.UserId != signedIn.UserId) throw new NotSignedInToSyncException();
		var user = signedIn.UserId;
		_vault.StartSyncAs(user);

		var held = new List<SyncItem>();
		long since = 0;
		SyncChanges page;
		do
		{
			page = await _client.GetChangesAsync(since, cancellation: cancellation, asUser: user);
			SyncWire.CheckPage(since, page);
			held.AddRange(page.Items);
			if (page.More) since = page.Items[^1].Revision;
		}
		while (page.More);
		_vault.TakeOverServer(user, held, page.Revision);

		await PushAsync(user, cancellation);
		for (var attempt = 0; attempt < ManifestAttempts; attempt++)
		{
			var manifest = await _client.GetManifestAsync(cancellation, asUser: user);
			if (!await PutManifestAsync(user, manifest?.Version ?? 0, _vault.ManifestEntries(), cancellation)) continue;
			_vault.DequeueSent();
			return;
		}
		throw new SyncManifestBusyException();
	}

	private void Report(Exception? error)
	{
		var described = error is null ? null : Describe(error);
		lock (_lock) _error = described;
		StatusChanged?.Invoke();
	}

	/// <summary>
	/// An upload that failed for a reason Settings shows another way (signed out, the vault turned off) uploaded nothing, so the error
	/// that offered it stays (M7).
	/// </summary>
	private void ReportUploadFailure(Exception error)
	{
		if (Describe(error) is not { } described) return;
		lock (_lock) _error = described;
		StatusChanged?.Invoke();
	}

	/// <summary>
	/// The code Settings acts on, and the message it shows. Null for what Settings already says another way: signed out (its notice),
	/// a sign-in this PC can't read, or the vault turned off.
	/// </summary>
	private (string Code, string Message)? Describe(Exception error) => error switch
	{
		ServerRolledBackException => ("server-rolled-back", error.Message),
		ServerWithheldItemException => ("server-withheld-item", error.Message),
		VaultTamperedException => ("server-tampered", $"A login from the sync server failed its integrity check, so sync stopped and nothing of it was stored. {error.Message}"),
		SyncProtocolException or InvalidDataException => ("server-invalid", $"The sync server sent something Zing can't use, so sync stopped. {error.Message}"),
		SyncSignedOutException or NotSignedInToSyncException or SyncAccountUnreadableException or VaultClosedException or SyncUploadNotOfferedException => null,
		OperationCanceledException when _stopping.IsCancellationRequested => null,
		HttpRequestException or OperationCanceledException => ("unreachable", "Zing couldn't reach the sync server. It tries again in 5 minutes."),
		SyncAccountChangedException => ("account-changed", error.Message),
		SyncHttpException => ("server-error", error.Message),
		_ => ("failed", error.Message),
	};
}
