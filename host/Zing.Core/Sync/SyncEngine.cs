using System.Net;
using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>
/// What Settings shows of the engine: the last sync's error, as a code the renderer acts on and a message it shows (null after a
/// sync that worked), and how many logins haven't uploaded yet.
/// </summary>
public sealed record SyncStatus(string? Error, string? Message, int Unsent, string? Warning = null);

/// <summary>A push kept meeting newer copies from another PC; what is left goes next time.</summary>
public sealed class SyncKeepsConflictingException(int left)
	: Exception($"Another PC kept changing the same logins while this one synced, so {left} changes are left for the next sync.");

/// <summary>Upload this PC's copy asked for when the last sync didn't stop on a rollback or a held-back login (Ruling 56).</summary>
public sealed class SyncUploadNotOfferedException()
	: Exception("Upload this PC's copy is only for when the sync server went back to an older copy or held a login back.");

/// <summary>
/// What a sync that worked says of logins too large for the sync server (a ciphertext over its 256 KiB): they stay on this PC only and
/// are never sent, since the server would refuse the whole batch they were in. A merge of two long histories can make one, so the way
/// out is a shorter note or a delete, both of which the vault always takes.
/// </summary>
public static class SyncTooLarge
{
	public static string Warning(int count) => count == 1
		? "One login is too large to sync (a long note or many old passwords), so it stays on this PC only. Shorten its note, or delete it if you don't need it."
		: $"{count} logins are too large to sync (long notes or many old passwords), so they stay on this PC only. Shorten their notes, or delete the ones you don't need.";
}

/// <summary>Another PC kept replacing the account manifest while this one wrote it.</summary>
public sealed class SyncManifestBusyException()
	: Exception("Another PC kept updating the sync list while this one synced. Zing tries again in 5 minutes.");

/// <summary>
/// The open vault's sync with the server (spec, Sync design): pull since the last revision, push what changed here in batches of
/// 100 (and at most the server's body size), merge on a conflict and push again, then write the account manifest. It runs at start,
/// 2 s after a change here (debounced, and no sooner than <see cref="ChangeGap"/> after the last sync began), and every 5 minutes; after
/// a 429 it waits as the server says, or backs off. It never needs Windows Hello (spec decision 10): summaries open with the index
/// key, and passwords move sealed.
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
	/// A change starts a sync no sooner than this after the last one began. A sync is about five requests and the server takes 30 a
	/// minute per account, so edits made one after another a few seconds apart share a sync instead of running into its limit.
	/// </summary>
	public static readonly TimeSpan ChangeGap = TimeSpan.FromSeconds(15);

	/// <summary>The first wait after a 429 that says nothing about how long; it doubles after each one in a row, up to <see cref="Every"/>.</summary>
	public static readonly TimeSpan RateLimitedRetry = TimeSpan.FromMinutes(1);

	/// <summary>The longest a 429 makes Zing wait, whatever its Retry-After says: a server asking for days would stop sync unseen.</summary>
	public static readonly TimeSpan MaxRateLimitedWait = TimeSpan.FromHours(1);

	/// <summary>
	/// After a refresh got no answer, the next try comes this soon (N4): the server takes the same token again only within 2 minutes
	/// of first seeing it, and the refresh itself may have waited 90 s.
	/// </summary>
	public static readonly TimeSpan RefreshRetry = TimeSpan.FromSeconds(10);

	/// <summary>How long after the first unanswered refresh <see cref="RefreshRetry"/> is used, within the server's 2-minute window.</summary>
	public static readonly TimeSpan RefreshRetryWindow = TimeSpan.FromSeconds(110);

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
	/// <summary>When a change may start the next sync: <see cref="ChangeGap"/> after the last began, or once a 429's wait is over.</summary>
	private long _changeNotBefore;
	private int _rateLimitedRuns;
	/// <summary>How long to wait after the last sync's 429; null when it didn't get one.</summary>
	private TimeSpan? _rateLimitedWait;
	/// <summary>What the last sync that worked warned of (<see cref="SyncTooLarge"/>), beside its error, which is then null.</summary>
	private string? _warning;
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
			string? warning;
			lock (_lock) (error, warning) = (_error, _warning);
			return new SyncStatus(error?.Code, error?.Message, _vault.UnsentLogins, warning);
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

	/// <summary>
	/// Debounced: a sync already waiting to start is moved to <paramref name="delay"/> from now, or later, to when a change may start
	/// one (<see cref="ChangeGap"/> after the last began, or once a 429's wait is over).
	/// </summary>
	private void Soon(TimeSpan delay)
	{
		lock (_lock)
		{
			if (_disposed) return;
			var gap = TimeSpan.FromMilliseconds(_changeNotBefore - _clock.Now());
			_soon?.Dispose();
			_soon = _scheduler.Schedule(gap > delay ? gap : delay, Run);
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
			_changeNotBefore = _clock.Now() + (long)ChangeGap.TotalMilliseconds;
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
				// After a 429 nothing runs before the retry scheduled below, which sends any change made meanwhile: RateLimited dropped
				// the run asked for during this one and the one waiting to start.
				if (_disposed || !_again || _rateLimitedWait is not null)
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
			_next = _scheduler.Schedule(retry ? RefreshRetry : _rateLimitedWait ?? Every, Run);
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
			var tooLarge = await RunAsync(cancellation);
			RateLimited(null);
			ReportWorked(tooLarge);
		}
		catch (Exception error)
		{
			RateLimited(error as SyncRateLimitedException);
			Report(error);
			throw;
		}
	}

	/// <summary>
	/// After a 429 the next sync waits as long as the server said, up to <see cref="MaxRateLimitedWait"/>, or, when it didn't say, a
	/// minute doubled for each 429 in a row up to <see cref="Every"/>. Nothing runs sooner: the sync waiting to start and one asked for
	/// while this one ran are dropped, and a change or sign-in meanwhile waits as long. Anything else ends the backoff.
	/// </summary>
	private void RateLimited(SyncRateLimitedException? limited)
	{
		lock (_lock)
		{
			if (limited is null)
			{
				_rateLimitedRuns = 0;
				_rateLimitedWait = null;
				return;
			}
			_rateLimitedRuns++;
			var doubled = RateLimitedRetry * Math.Pow(2, Math.Min(_rateLimitedRuns - 1, 8));
			var wait = limited.RetryAfter is { } asked && asked > TimeSpan.Zero ? (asked < MaxRateLimitedWait ? asked : MaxRateLimitedWait) : doubled < Every ? doubled : Every;
			_rateLimitedWait = wait;
			_changeNotBefore = Math.Max(_changeNotBefore, _clock.Now() + (long)wait.TotalMilliseconds);
			_soon?.Dispose();
			_soon = null;
			_again = false;
		}
	}

	/// <summary>A wait as Settings says it: "30 seconds", "a minute", "4 minutes", "an hour".</summary>
	private static string Words(TimeSpan wait)
	{
		var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
		if (seconds < 60) return seconds == 1 ? "a second" : $"{seconds} seconds";
		var minutes = (int)Math.Ceiling(wait.TotalMinutes);
		if (minutes < 60) return minutes == 1 ? "a minute" : $"{minutes} minutes";
		var hours = (int)Math.Ceiling(wait.TotalHours);
		return hours == 1 ? "an hour" : $"{hours} hours";
	}

	/// <returns>How many logins were too large to send.</returns>
	private async Task<int> RunAsync(CancellationToken cancellation)
	{
		using var gate = await SyncKeys.EnterGateAsync(_vault, cancellation);
		if (_vault.SyncAccount() is not { } signedIn) return 0;
		await SyncKeys.SettlePendingAsync(_vault, _client, signedIn, cancellation);
		if (_vault.SyncKeys?.UserId != signedIn.UserId) return 0;
		var user = signedIn.UserId;
		await CheckKeysVersionAsync(user, cancellation);
		_vault.StartSyncAs(user);
		_vault.TombstoneDue(_clock.Now());
		for (var attempt = 0; attempt < ManifestAttempts; attempt++)
		{
			if (!await RoundAsync(user, cancellation)) continue;
			_vault.DequeueSent();
			return TooLarge();
		}
		throw new SyncManifestBusyException();
	}

	/// <summary>The logins left behind once everything else is synced, for Settings' warning.</summary>
	private int TooLarge() => _vault.Unsent().Count(u => !SyncWire.Fits(u.Record));

	/// <summary>
	/// The server's keys at a later version than this PC's mean the password was changed on another PC (Task 14): they're kept for the
	/// next start, which asks for the new password and checks it by unwrapping them, and the vault says so once. The keys themselves are
	/// the same, so the sync goes on. Keys for another vault, keys written for another version than they're served at (old keys served
	/// again, M1), or keys that aren't Zing's, stop it.
	/// </summary>
	private async Task CheckKeysVersionAsync(string user, CancellationToken cancellation)
	{
		var stored = _vault.SyncKeys!;
		var server = await _client.GetKeysAsync(cancellation, asUser: user);
		if (server is null || server.Version <= Math.Max(stored.Version, stored.NewerVersion)) return;
		var blob = SyncKeysBlob.Parse(server.Blob, server.Version);
		if (blob.VaultId != _vault.VaultId) throw new SyncProtocolException($"The sync server's keys are for vault {blob.VaultId}, not this vault ({_vault.VaultId})");
		_vault.MarkPasswordChangedElsewhere(server.Blob, server.Version);
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
			await _vault.ApplyFromServerAsync(user, page.Items, cursor, page.Revision);
			if (served is not null)
			{
				foreach (var item in page.Items) served[item.ItemId] = item.Version;
			}
			if (!page.More) return served;
			since = next;
		}
	}

	/// <summary>
	/// Everything changed here that the server would take, in batches of at most 100 and its body size, each over the server copy it
	/// was based on. The server's newer copy of a refused item is merged in (or found to be this one, already uploaded) and goes in the
	/// next round. A login too large to send is left out (<see cref="SyncTooLarge"/>), so it can't stop the rest.
	/// </summary>
	private async Task PushAsync(string user, CancellationToken cancellation)
	{
		for (var round = 0; round < PushRounds; round++)
		{
			List<SyncUnsent> unsent = [.. _vault.Unsent().Where(u => SyncWire.Fits(u.Record))];
			if (unsent.Count == 0) return;
			foreach (var batch in SyncWire.Batches(unsent, SyncWire.Push))
			{
				var pushed = await _client.PostChangesAsync([.. batch.Select(SyncWire.Push)], cancellation, asUser: user);
				SyncWire.CheckPushAnswer([.. batch.Select(u => u.Record.ItemId)], pushed);
				RollbackGuard.CheckAccepted(_vault.SyncProgressFor(user).RevisionFloor, pushed.Accepted);
				_vault.MarkPushed(user, [.. batch.Select(u => u.Record)], pushed.Accepted);
				_vault.ApplyConflicts(user, batch, pushed.Conflicts);
			}
		}
		var left = _vault.Unsent().Count(u => SyncWire.Fits(u.Record));
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
			var tooLarge = await UploadAsync(cancellation);
			ReportWorked(tooLarge);
		}
		catch (Exception error)
		{
			ReportUploadFailure(error);
			throw;
		}
	}

	/// <summary>The errors Settings answers with Upload this PC's copy.</summary>
	public static bool OffersUpload(string? error) => error is "server-rolled-back" or "server-withheld-item";

	/// <returns>How many logins were too large to send.</returns>
	private async Task<int> UploadAsync(CancellationToken cancellation)
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
			return TooLarge();
		}
		throw new SyncManifestBusyException();
	}

	/// <summary>A sync or upload that failed: its error, beside the warning the last one that worked left.</summary>
	private void Report(Exception error)
	{
		var described = Describe(error);
		lock (_lock) _error = described;
		StatusChanged?.Invoke();
	}

	/// <summary>A sync or upload that worked, and the logins it left behind as too large, if any.</summary>
	private void ReportWorked(int tooLarge)
	{
		lock (_lock)
		{
			_error = null;
			_warning = tooLarge > 0 ? SyncTooLarge.Warning(tooLarge) : null;
		}
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
		SyncRateLimitedException => ("rate-limited", $"The sync server asked Zing to slow down, so it syncs again in {Words(_rateLimitedWait ?? RateLimitedRetry)}."),
		SyncHttpException => ("server-error", error.Message),
		_ => ("failed", error.Message),
	};
}
