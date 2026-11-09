using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zing.Core.Sync;

namespace Zing.Core.Vault;

/// <summary>A login to save: the origin may be any address on the site, and is stored as its exact origin.</summary>
public sealed record NewLogin(string Origin, string Username, string Password, string Title, string Note);

/// <summary>One old password, without the password: when it was replaced. Newest first; the index is what <see cref="VaultStore.HistoryPasswordAsync"/> takes.</summary>
public sealed record PasswordHistoryEntry(long ReplacedAt);

/// <summary>One stored item, sealed and exactly as the server stores it. Nothing but the ID, the version and the tombstone flag is in the clear.</summary>
public sealed record LoginRecord(string ItemId, long Version, byte[] Summary, byte[] Password, byte[][] History, bool Tombstone);

/// <summary>A change waiting in vault/sync.json for sync (Task 13) to send: the item and the version the change made.</summary>
public sealed record QueuedChange(string ItemId, long Version);

/// <summary>What an import did: logins it added, and rows that matched a login already held (or an earlier row) and merged into it.</summary>
public sealed record ImportOutcome(int Added, int Merged);

/// <summary>One login with its password, for an export.</summary>
public sealed record ExportedLogin(string Title, string Origin, string Username, string Password, string Note);

/// <summary>What the renderer shows about the vault without asking for any of it: on or off, Hello set up, and how many logins.</summary>
/// <param name="Count">Logins not in Recently deleted.</param>
/// <param name="FilesExist">Vault files are on disk, whether or not the vault is on or could be read: the privacy banner in Settings must warn about them.</param>
/// <param name="Error">"unreadable" when a vault file couldn't be read at launch, so the vault is off and its files are left as they are.</param>
public sealed record VaultState(bool Enabled, bool HelloAvailable, int Count, string? Error = null, bool FilesExist = false);

/// <summary>A vault call while <c>passwordAutofill</c> is off: there's no store to answer it.</summary>
public sealed class VaultOffException() : Exception("The password vault is off. Turn on \"Offer to fill saved passwords\" in Settings.");

/// <summary>A write to a store that was closed meanwhile (passwordAutofill turned off): nothing was written, so it can't land after a new store has read the files.</summary>
public sealed class VaultClosedException() : Exception("The password vault was turned off before this change was saved, so nothing was saved.");

public sealed class NothingToExportException() : Exception("The vault has no logins to export");

public sealed class NeverSaveNotFoundException(string origin) : Exception($"{origin} isn't in the list of sites Zing never offers to save");

public sealed class VaultItemNotFoundException(string id) : Exception($"No login with ID {id} in the vault");

/// <summary>Deleting a login that's already in Recently deleted, or restoring one that isn't.</summary>
public sealed class VaultItemStateException(string message) : Exception(message);

/// <summary>A vault file exists but can't be read, doesn't decrypt, or has no keys for its items. Nothing is moved aside: the file may hold the only copy of the passwords.</summary>
public sealed class VaultFileException(string path, Exception inner) : Exception($"Could not read the vault at {path}: {inner.Message}", inner)
{
	public string Path { get; } = path;
}

/// <summary>
/// The local password vault (vault/ in the profile). Summaries are decrypted once, with the DPAPI-sealed index key, and kept in
/// memory, so listing and searching never ask Windows Hello. A password read or a write that seals a password needs the secret key,
/// which Hello unseals; Hello asks at most once per <see cref="SecretKeyHeldFor"/> (Ruling 44, the owner's decision of 2026-11-07).
/// After a successful unseal the key is held for that fixed time, never extended by use, and then wiped; it is wiped sooner by
/// Windows locking or the session switching, by Hello going away, and by <see cref="Dispose"/> (the vault turned off or replaced,
/// Zing closing). Without lock detection nothing is held, so Hello asks every time (Ruling 47). Export always asks Hello, held key
/// or not. With no Hello, every write is refused.
/// </summary>
/// <remarks>
/// A password or old password is sealed under the version its item had when it was sealed, and the summary (sealed under the item's
/// current version) records each of those versions. So a summary-only change, a delete, and sync's merge, which keeps the winning
/// password's ciphertext and moves the losing one into the history as it is, bump the version without needing the secret key; and a
/// password can't be swapped for another item's, or for an older one, because its sealed version is pinned by the summary.
/// </remarks>
public sealed partial class VaultStore : IDisposable, ISyncAccountStore
{

	public const string VaultFileName = "vault.zing";
	public const string KeysFileName = "keys.bin";
	public const string SyncFileName = "sync.json";

	private const int Format = 1;

	/// <summary>How long a successful Hello unseal keeps the secret key, counted from that Hello (Ruling 44).</summary>
	public static readonly TimeSpan SecretKeyHeldFor = TimeSpan.FromMinutes(5);

	private readonly string _dir;
	private readonly ISecretCipher _cipher;
	private readonly IHelloSealer _sealer;
	private readonly IClock _clock;
	private readonly IScheduler _scheduler;
	private readonly ISessionEvents _session;
	private readonly Lock _gate = new();
	private readonly SemaphoreSlim _creatingKeys = new(1, 1);
	private readonly List<LoginRecord> _records;
	/// <summary>The server's copy of each item as this PC last synced it (<see cref="SyncBase"/>), kept in vault.zing with the records so both change in one write. Under <c>_gate</c>.</summary>
	private readonly Dictionary<string, SyncBase> _bases;
	/// <summary>The server user <see cref="_bases"/> belong to; null before the first sync. Under <c>_gate</c>.</summary>
	private string? _basesUser;
	/// <summary>The pull cursor and the rollback floors (sync.json). Under <c>_gate</c>.</summary>
	private SyncProgress? _progress;
	private readonly List<QueuedChange> _queued;
	/// <summary>Who this PC syncs as, its refresh token sealed; under <c>_gate</c>, and written with the queue into sync.json.</summary>
	private SyncAccountFile? _syncAccount;
	/// <summary>The server ended the last sign-in (Ruling 50); cleared by the next sign-in or a sign-out. Under <c>_gate</c>.</summary>
	private bool _syncSignedOut;
	private readonly Dictionary<string, SummaryPayload> _summaries;
	private VaultKeys? _keys;
	private readonly Action _availabilityChanged;
	/// <summary>Set under <c>_gate</c>; every write checks it under the same lock, so none lands once Dispose has returned.</summary>
	private bool _disposed;
	/// <summary>The secret key a Hello unsealed, and its wipe timer; both under <c>_gate</c>.</summary>
	private byte[]? _heldKey;
	private IDisposable? _heldTimer;
	/// <summary>Counts wipes under <c>_gate</c>, so a Hello answered after one (Windows locked while it asked) holds nothing.</summary>
	private int _wipes;

	/// <summary>Fires after every write, and when Hello becomes available or stops being.</summary>
	public event Action? Changed;

	/// <summary>Fires after a change made on this PC (a save, an edit, a delete, an import), not one sync brought in: sync's 2-second debounce follows it.</summary>
	public event Action? Edited;

	private void OnEdited()
	{
		Changed?.Invoke();
		Edited?.Invoke();
	}

	/// <exception cref="VaultFileException">A vault file can't be read or doesn't decrypt.</exception>
	/// <param name="scheduler">Wipes the held secret key when its 5 minutes are up.</param>
	/// <param name="session">Windows locking or the session switching wipes the held key; while it isn't watched, nothing is held.</param>
	public VaultStore(string dir, ISecretCipher cipher, IHelloSealer sealer, IClock clock, IScheduler scheduler, ISessionEvents session)
	{
		_dir = dir;
		_cipher = cipher;
		_sealer = sealer;
		_clock = clock;
		_scheduler = scheduler;
		_session = session;
		_keys = Guard(KeysFile, VaultKeys.Load);
		(_records, _bases, _basesUser) = Guard(VaultFile, LoadRecords);
		(_queued, _syncAccount, _syncSignedOut, _progress) = Guard(SyncFile, LoadSyncFile);
		_summaries = Guard(VaultFile, _ => DecryptSummaries());
		_availabilityChanged = () =>
		{
			// A key held from before Hello went away mustn't outlive it.
			if (!_sealer.Available) ForgetSecretKey();
			Changed?.Invoke();
		};
		_sealer.AvailabilityChanged += _availabilityChanged;
		_session.Switched += ForgetSecretKey;
	}

	/// <summary>
	/// Closes the store (passwordAutofill turned off): it stops listening to Hello, and every write from now on, one already
	/// waiting on Hello included, is refused with <see cref="VaultClosedException"/>. Writes commit under the same lock, so once
	/// this returns nothing more reaches the files, and a store opened afterwards reads them as they finally are. The held secret
	/// key is wiped, and a Hello still asking holds nothing.
	/// </summary>
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			WipeHeldKey();
		}
		_sealer.AvailabilityChanged -= _availabilityChanged;
		_session.Switched -= ForgetSecretKey;
	}

	/// <summary>Called under <c>_gate</c> before any file is written.</summary>
	private void RequireOpen()
	{
		if (_disposed) throw new VaultClosedException();
	}

	public bool HelloAvailable => _sealer.Available;

	/// <summary>Null until the first save makes the keys.</summary>
	public string? VaultId => _keys?.VaultId;

	/// <summary>Whether <paramref name="indexKey"/> is this vault's own, read from its DPAPI seal: no Hello (Ruling 71). False before there are keys.</summary>
	public bool HoldsIndexKey(byte[] indexKey)
	{
		VaultKeys? keys;
		lock (_gate) keys = _keys;
		if (keys is null) return false;
		var own = keys.IndexKey(_cipher);
		try
		{
			return CryptographicOperations.FixedTimeEquals(own, indexKey);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(own);
		}
	}

	public IReadOnlyList<LoginRecord> Records
	{
		get
		{
			lock (_gate) return [.. _records];
		}
	}

	public IReadOnlyList<QueuedChange> Queued
	{
		get
		{
			lock (_gate) return [.. _queued];
		}
	}

	/// <summary>Every login, those in Recently deleted included, and not the "never for this site" marks. No Hello.</summary>
	public IReadOnlyList<LoginSummary> Summaries()
	{
		lock (_gate) return [.. _records.Where(r => !r.Tombstone && !_summaries[r.ItemId].NeverSave).Select(r => _summaries[r.ItemId].ToSummary(r.ItemId))];
	}

	/// <summary>The origins marked "never for this site". No Hello.</summary>
	public IReadOnlyList<NeverSave> NeverSaves()
	{
		lock (_gate) return [.. _summaries.Values.Where(s => s.NeverSave && s.DeletedAt is null).Select(s => s.Origin).Distinct().Select(o => new NeverSave(o))];
	}

	public bool IsNeverSave(string origin)
	{
		var exact = VaultModel.OriginOf(origin);
		lock (_gate) return _summaries.Values.Any(s => s.NeverSave && s.DeletedAt is null && s.Origin == exact);
	}

	/// <summary>
	/// Marks the origin "never for this site": a summary-only item (no password, so no Hello) that syncs like any other.
	/// Only the very first write of a vault also makes its keys, and that asks Hello once, like the first save. Marking an
	/// origin that is already marked changes nothing.
	/// </summary>
	public async Task NeverSave(string origin)
	{
		var exact = VaultModel.OriginOf(origin);
		if (IsNeverSave(exact)) return;
		if (_keys is null)
		{
			RequireHello();
			var (_, secretKey) = await SecretKeyForWriteAsync();
			CryptographicOperations.ZeroMemory(secretKey);
		}
		lock (_gate)
		{
			RequireOpen();
			// Another call may have marked it while the keys were being made.
			if (_summaries.Values.Any(s => s.NeverSave && s.DeletedAt is null && s.Origin == exact)) return;
			var keys = Keys();
			var id = Guid.NewGuid().ToString("N");
			var now = _clock.Now();
			var summary = new SummaryPayload(exact, "", "", "", now, now, now, null, 0, [], NeverSave: true);
			Commit(null, new LoginRecord(id, 1, SealSummary(keys, id, 1, summary), [], [], false), summary);
		}
		OnEdited();
	}

	/// <summary>No Hello.</summary>
	public IReadOnlyList<PasswordHistoryEntry> History(string id)
	{
		lock (_gate) return [.. Summary(id).History.Select(h => new PasswordHistoryEntry(h.ReplacedAt))];
	}

	/// <summary>Asks Hello unless the secret key is held.</summary>
	public async Task<string> PasswordAsync(string id)
	{
		var (record, summary) = Find(id);
		return await OpenSecretAsync(record.ItemId, record.Password, summary.PasswordVersion);
	}

	/// <summary>Asks Hello unless the secret key is held. Index 0 is the password replaced most recently.</summary>
	public async Task<string> HistoryPasswordAsync(string id, int index)
	{
		var (record, summary) = Find(id);
		if (index < 0 || index >= summary.History.Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"The login has {summary.History.Count} old passwords");
		return await OpenSecretAsync(record.ItemId, record.History[index], summary.History[index].Version);
	}

	/// <summary>Asks Hello unless the secret key is held (the first save makes the keys, with one Hello call). Returns the new login's ID.</summary>
	public async Task<string> SaveAsync(NewLogin login)
	{
		var origin = VaultModel.OriginOf(login.Origin);
		RequireHello();
		var (keys, secretKey) = await SecretKeyForWriteAsync();
		try
		{
			string id;
			lock (_gate)
			{
				id = Guid.NewGuid().ToString("N");
				var now = _clock.Now();
				var password = Seal(secretKey, keys, id, 1, ItemPart.Password, login.Password);
				var summary = new SummaryPayload(origin, login.Username, login.Title, login.Note, now, now, now, null, 1, [], PasswordHash: CipherHash.Of(password));
				Commit(null, new LoginRecord(id, 1, SealSummary(keys, id, 1, summary), password, [], false), summary);
			}
			OnEdited();
			return id;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretKey);
		}
	}

	/// <summary>Asks Hello unless the secret key is held. The replaced password goes into the history as it is, without being decrypted.</summary>
	public Task UpdatePasswordAsync(string id, string password) => ChangePasswordAsync(id, password, s => s, skipIfSame: false);

	/// <summary>A new password and the summary's fields in one change: at most one Hello call, one version, one queued change. A cancel changes nothing.</summary>
	public Task UpdateLoginAsync(string id, string title, string note, string username, string password) =>
		ChangePasswordAsync(id, password, s => s with { Title = title, Note = note, Username = username }, skipIfSame: false);

	/// <summary>
	/// Like <see cref="UpdateLoginAsync"/>, but compares with the saved password inside the same single Hello and changes nothing
	/// when it is the same. False when it was the same.
	/// </summary>
	public Task<bool> UpdateLoginIfChangedAsync(string id, string title, string note, string username, string password) =>
		ChangePasswordAsync(id, password, s => s with { Title = title, Note = note, Username = username }, skipIfSame: true);

	private async Task<bool> ChangePasswordAsync(string id, string password, Func<SummaryPayload, SummaryPayload> edit, bool skipIfSame)
	{
		Find(id);
		RequireHello();
		var keys = Keys();
		var secretKey = await SecretKeyAsync(keys);
		try
		{
			lock (_gate)
			{
				// Read again: another change may have landed while Hello was asking.
				var (record, summary) = Find(id);
				if (skipIfSame && IsSame(secretKey, keys, record, summary, password)) return false;
				var (changedRecord, changed) = WithNewPassword(secretKey, keys, record, summary, password, edit);
				Commit(record, changedRecord, changed);
			}
			OnEdited();
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretKey);
		}
	}

	/// <summary>The item with a new password: the old one goes into the history as it is, without being decrypted. Writes nothing.</summary>
	private (LoginRecord Record, SummaryPayload Summary) WithNewPassword(byte[] secretKey, VaultKeys keys, LoginRecord record, SummaryPayload summary, string password, Func<SummaryPayload, SummaryPayload> edit)
	{
		var version = record.Version + 1;
		var now = _clock.Now();
		var sealedPassword = Seal(secretKey, keys, record.ItemId, version, ItemPart.Password, password);
		var changed = edit(summary) with
		{
			UpdatedAt = now,
			PasswordUpdatedAt = now,
			PasswordVersion = version,
			PasswordHash = CipherHash.Of(sealedPassword),
			History = [new HistoryPayload(summary.PasswordVersion, now, CipherHash.Of(record.Password)), .. summary.History],
		};
		return (record with { Version = version, Summary = SealSummary(keys, record.ItemId, version, changed), Password = sealedPassword, History = [record.Password, .. record.History] }, changed);
	}

	private static bool IsLive(SummaryPayload summary) => !summary.NeverSave && summary.DeletedAt is null;

	/// <summary>
	/// Adds every login, or merges it into the live login with the same origin and username (one held already, or an earlier row of
	/// this batch): a different password replaces the saved one, which goes into its history. At most one Hello call unseals the secret key for
	/// the whole batch (Ruling 3), the files are written once, and a bad address anywhere refuses the whole batch before anything is written.
	/// </summary>
	/// <exception cref="NotAWebOriginException">A login's address isn't an http(s) address.</exception>
	public async Task<ImportOutcome> ImportAsync(IReadOnlyList<NewLogin> logins)
	{
		if (logins.Count == 0) return new ImportOutcome(0, 0);
		var origins = logins.Select(l => VaultModel.OriginOf(l.Origin)).ToList();
		RequireHello();
		var (keys, secretKey) = await SecretKeyForWriteAsync();
		try
		{
			ImportOutcome outcome;
			bool wrote;
			lock (_gate)
			{
				RequireOpen();
				var live = new Dictionary<(string Origin, string Username), string>();
				foreach (var record in _records)
				{
					if (record.Tombstone || !IsLive(_summaries[record.ItemId])) continue;
					var held = _summaries[record.ItemId];
					live[(held.Origin, held.Username)] = record.ItemId;
				}
				var byId = _records.ToDictionary(r => r.ItemId, StringComparer.Ordinal);
				var changed = new Dictionary<string, (LoginRecord Record, SummaryPayload Summary)>(StringComparer.Ordinal);
				List<string> added = [];
				var merged = 0;
				for (var i = 0; i < logins.Count; i++)
				{
					var login = logins[i];
					if (live.TryGetValue((origins[i], login.Username), out var id))
					{
						merged++;
						var (record, summary) = changed.TryGetValue(id, out var latest) ? latest : (byId[id], _summaries[id]);
						if (IsSame(secretKey, keys, record, summary, login.Password)) continue;
						changed[id] = WithNewPassword(secretKey, keys, record, summary, login.Password, s => s);
						continue;
					}
					var newId = Guid.NewGuid().ToString("N");
					var now = _clock.Now();
					var password = Seal(secretKey, keys, newId, 1, ItemPart.Password, login.Password);
					var fresh = new SummaryPayload(origins[i], login.Username, login.Title, login.Note, now, now, now, null, 1, [], PasswordHash: CipherHash.Of(password));
					changed[newId] = (new LoginRecord(newId, 1, SealSummary(keys, newId, 1, fresh), password, [], false), fresh);
					added.Add(newId);
					live[(origins[i], login.Username)] = newId;
				}
				wrote = changed.Count > 0;
				if (wrote) CommitBatch(changed, added);
				outcome = new ImportOutcome(added.Count, merged);
			}
			if (wrote) OnEdited();
			return outcome;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretKey);
		}
	}

	/// <summary>
	/// Every login not in Recently deleted, with its password, for an export. Always asks Hello, even while the secret key is held
	/// (Ruling 44), and holds nothing: this key is wiped as soon as the passwords are read.
	/// </summary>
	/// <exception cref="NothingToExportException">No login to export.</exception>
	public async Task<IReadOnlyList<ExportedLogin>> ExportAsync()
	{
		if (Summaries().All(s => s.DeletedAt is not null)) throw new NothingToExportException();
		RequireHello();
		var keys = Keys();
		var secretKey = await keys.SecretKeyAsync(_cipher, _sealer);
		try
		{
			lock (_gate)
			{
				// Read again: the vault may have been turned off while Hello was asking.
				RequireOpen();
				return [.. _records
					.Where(r => !r.Tombstone && IsLive(_summaries[r.ItemId]))
					.Select(r => (Record: r, Summary: _summaries[r.ItemId]))
					.Select(x => new ExportedLogin(x.Summary.Title, x.Summary.Origin, x.Summary.Username, OpenPassword(secretKey, keys, x.Record, x.Summary), x.Summary.Note))];
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretKey);
		}
	}

	private static string OpenPassword(byte[] secretKey, VaultKeys keys, LoginRecord record, SummaryPayload summary)
	{
		var plain = VaultCrypto.Open(secretKey, record.Password, new ItemAd(keys.VaultId, record.ItemId, summary.PasswordVersion, ItemPart.Password));
		try
		{
			return Encoding.UTF8.GetString(plain);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plain);
		}
	}

	/// <summary>Takes the origin off the "never for this site" list so Zing offers to save there again. A summary-only change: no Hello.</summary>
	/// <exception cref="NeverSaveNotFoundException">The origin isn't on the list.</exception>
	public void RemoveNeverSave(string origin)
	{
		var exact = VaultModel.OriginOf(origin);
		lock (_gate)
		{
			RequireOpen();
			var marks = _records.Where(r => !r.Tombstone && _summaries[r.ItemId] is { NeverSave: true, DeletedAt: null } mark && mark.Origin == exact).ToList();
			if (marks.Count == 0) throw new NeverSaveNotFoundException(exact);
			var keys = Keys();
			var now = _clock.Now();
			var changed = new Dictionary<string, (LoginRecord Record, SummaryPayload Summary)>(StringComparer.Ordinal);
			foreach (var record in marks)
			{
				var removed = _summaries[record.ItemId] with { DeletedAt = now, UpdatedAt = now };
				var version = record.Version + 1;
				changed[record.ItemId] = (record with { Version = version, Summary = SealSummary(keys, record.ItemId, version, removed) }, removed);
			}
			CommitBatch(changed, []);
		}
		OnEdited();
	}

	private static bool IsSame(byte[] secretKey, VaultKeys keys, LoginRecord record, SummaryPayload summary, string password)
	{
		var current = VaultCrypto.Open(secretKey, record.Password, new ItemAd(keys.VaultId, record.ItemId, summary.PasswordVersion, ItemPart.Password));
		var typed = Encoding.UTF8.GetBytes(password);
		try
		{
			return CryptographicOperations.FixedTimeEquals(current, typed);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(current);
			CryptographicOperations.ZeroMemory(typed);
		}
	}

	/// <summary>No Hello.</summary>
	public void UpdateSummary(string id, string title, string note, string username) =>
		EditSummary(id, (s, now) => s with { Title = title, Note = note, Username = username, UpdatedAt = now });

	/// <summary>Moves the login to Recently deleted. No Hello.</summary>
	public void Delete(string id) =>
		EditSummary(id, (s, now) => s.DeletedAt is null ? s with { DeletedAt = now, UpdatedAt = now } : throw new VaultItemStateException($"Login {id} is already in Recently deleted"));

	/// <summary>Brings a login back from Recently deleted. No Hello.</summary>
	public void Restore(string id) =>
		EditSummary(id, (s, now) => s.DeletedAt is not null ? s with { DeletedAt = null, UpdatedAt = now } : throw new VaultItemStateException($"Login {id} isn't in Recently deleted"));

	private void EditSummary(string id, Func<SummaryPayload, long, SummaryPayload> edit)
	{
		RequireHello();
		lock (_gate)
		{
			RequireOpen();
			var (record, summary) = Find(id);
			var keys = Keys();
			var changed = edit(summary, _clock.Now());
			var version = record.Version + 1;
			Commit(record, record with { Version = version, Summary = SealSummary(keys, id, version, changed) }, changed);
		}
		OnEdited();
	}

	/// <summary>
	/// Writes the change: queued first, then the item. A crash between the two leaves a change queued but not stored (and the next write
	/// reuses that version), never one stored but not queued. So queue entries are hints: sync (Task 13) sends each item as it's stored now.
	/// </summary>
	private void Commit(LoginRecord? replaced, LoginRecord record, SummaryPayload summary)
	{
		RequireOpen();
		List<LoginRecord> records = replaced is null ? [.. _records, record] : [.. _records.Select(r => ReferenceEquals(r, replaced) ? record : r)];
		Persist(records, [new QueuedChange(record.ItemId, record.Version)], KeepingOriginals(replaced is null ? [] : [replaced]));
		_summaries[record.ItemId] = summary;
	}

	/// <summary>Several items in one write: <paramref name="added"/> are new, stored in that order; the rest replace the item of their ID. Each is queued once, at the version it now has.</summary>
	private void CommitBatch(IReadOnlyDictionary<string, (LoginRecord Record, SummaryPayload Summary)> changed, IReadOnlyList<string> added)
	{
		RequireOpen();
		List<LoginRecord> records = [.. _records.Select(r => changed.TryGetValue(r.ItemId, out var next) ? next.Record : r), .. added.Select(id => changed[id].Record)];
		Persist(records, [.. changed.Values.Select(c => new QueuedChange(c.Record.ItemId, c.Record.Version))], KeepingOriginals(_records.Where(r => changed.ContainsKey(r.ItemId))));
		foreach (var (id, change) in changed) _summaries[id] = change.Summary;
	}

	/// <summary>The sync account, its refresh token unsealed; null when this PC isn't signed in to sync.</summary>
	/// <exception cref="SyncAccountUnreadableException">The refresh token doesn't unseal.</exception>
	public SyncAccount? SyncAccount()
	{
		SyncAccountFile? stored;
		lock (_gate) stored = _syncAccount;
		if (stored is null) return null;
		try
		{
			return new(stored.UserId, stored.Email, Encoding.UTF8.GetString(_cipher.Decrypt(stored.SealedRefresh)));
		}
		catch (CryptographicException error)
		{
			throw new SyncAccountUnreadableException(SyncFile, error);
		}
	}

	/// <summary>The server ended the last sign-in (a refused refresh token), and nobody has signed in or out since.</summary>
	public bool SyncSignedOutByServer
	{
		get
		{
			lock (_gate) return _syncSignedOut;
		}
	}

	/// <summary>Fires after the sync account was saved, forgotten or ended by the server.</summary>
	public event Action? SyncChanged;

	/// <summary>Writes the account into sync.json beside the queue, the refresh token sealed (DPAPI). On disk when it returns. Replaces one that doesn't unseal.</summary>
	public void SaveSyncAccount(SyncAccount account) =>
		WriteSyncAccount(new SyncAccountFile(account.UserId, account.Email, _cipher.Encrypt(Encoding.UTF8.GetBytes(account.Refresh))), signedOut: false);

	/// <summary>Signs this PC out of sync: the account leaves sync.json; queued changes stay.</summary>
	public void ForgetSyncAccount() => WriteSyncAccount(null, signedOut: false);

	public void MarkSyncSignedOut() => WriteSyncAccount(null, signedOut: true);

	private void WriteSyncAccount(SyncAccountFile? account, bool signedOut)
	{
		lock (_gate)
		{
			RequireOpen();
			WriteSyncFile(_queued, account, signedOut);
			_syncAccount = account;
			_syncSignedOut = signedOut;
		}
		SyncChanged?.Invoke();
	}

	/// <summary>
	/// Turns sync off on this PC (Ruling 68), once its sign-in is gone: sync.json's queue, account and progress (cursor, floors,
	/// manifest hash), then the sync bases in vault.zing, then the sync keys and any pending ones in keys.bin, last, so a crash part way
	/// still reads as sync on and the person can turn it off again. Every login stays, unsynced. No Hello.
	/// </summary>
	public void TurnOffSync()
	{
		lock (_gate)
		{
			RequireOpen();
			WriteSyncFile([], null, false, null);
			_queued.Clear();
			_syncAccount = null;
			_syncSignedOut = false;
			_progress = null;
			WriteVaultFile([.. _records], new Dictionary<string, SyncBase>(StringComparer.Ordinal), null);
			if (_keys is { } keys && (keys.Sync is not null || keys.PendingSync is not null))
			{
				var next = keys.WithoutSync();
				next.Save(KeysFile);
				_keys = next;
			}
		}
		SyncChanged?.Invoke();
	}

	/// <summary>What turning on sync stored in keys.bin; null before. No Hello.</summary>
	public VaultSyncKeys? SyncKeys
	{
		get
		{
			lock (_gate) return _keys?.Sync;
		}
	}

	/// <summary>
	/// The vault's ID and both keys unsealed, for sync to wrap them: asks Hello unless the secret key is held, and on a vault with no
	/// keys yet makes them, with that one Hello call. The caller disposes it.
	/// </summary>
	public async Task<VaultKeyMaterial> KeyMaterialAsync()
	{
		RequireHello();
		var (keys, secretKey) = await SecretKeyForWriteAsync();
		try
		{
			return new VaultKeyMaterial(keys.VaultId, keys.IndexKey(_cipher), secretKey);
		}
		catch
		{
			CryptographicOperations.ZeroMemory(secretKey);
			throw;
		}
	}

	/// <summary>The keys a PUT /keys is sending or sent, until its outcome is settled; null otherwise. No Hello.</summary>
	public VaultPendingKeys? PendingSyncKeys
	{
		get
		{
			lock (_gate) return _keys?.PendingSync;
		}
	}

	/// <summary>Stores keys about to be sent with PUT /keys in keys.bin, replacing any pending before. On disk when it returns.</summary>
	public void SavePendingSyncKeys(VaultPendingKeys pending)
	{
		lock (_gate)
		{
			RequireOpen();
			var next = Keys().WithPending(pending);
			next.Save(KeysFile);
			_keys = next;
		}
	}

	/// <summary>
	/// The server took <paramref name="blob"/> at <paramref name="version"/>, and it is the pending keys byte for byte: they become
	/// the sync keys. A turn-on's recovery key is still to be confirmed; a password change keeps the confirmation it had.
	/// </summary>
	/// <exception cref="SyncPendingKeysReplacedException">The pending keys are other keys, or there are none.</exception>
	public void CommitPendingSyncKeys(byte[] blob, long version)
	{
		lock (_gate)
		{
			RequireOpen();
			var keys = Keys();
			if (keys.PendingSync is not { } pending || !pending.Blob.AsSpan().SequenceEqual(blob)) throw new SyncPendingKeysReplacedException();
			var confirmed = pending.BaseVersion > 0 && keys.Sync is { RecoveryConfirmed: true } stored && stored.UserId == pending.UserId;
			var next = keys.WithSync(new VaultSyncKeys(pending.UserId, pending.Blob, version, pending.SealedRecoveryKey, confirmed)).WithPending(null);
			next.Save(KeysFile);
			_keys = next;
		}
		SyncChanged?.Invoke();
	}

	/// <summary>The server didn't take the pending keys (or took others): forgets them.</summary>
	public void DropPendingSyncKeys()
	{
		lock (_gate)
		{
			RequireOpen();
			var next = Keys().WithPending(null);
			next.Save(KeysFile);
			_keys = next;
		}
	}

	/// <summary>The person confirmed they saved the recovery key, so Settings stops asking.</summary>
	public void ConfirmRecoverySaved()
	{
		lock (_gate)
		{
			var sync = _keys?.Sync ?? throw new SyncNotOnException();
			RequireOpen();
			var next = _keys.WithSync(sync with { RecoveryConfirmed = true });
			next.Save(KeysFile);
			_keys = next;
		}
		SyncChanged?.Invoke();
	}

	/// <summary>The recovery key, unsealed with the secret key: asks Hello unless it is held (Ruling 44). A cancelled Hello reveals nothing.</summary>
	/// <exception cref="SyncRecoveryKeyNotOnThisPcException">This PC was restored with the password, so it never had the recovery key.</exception>
	public async Task<string> RecoveryKeyAsync()
	{
		var sync = SyncKeys ?? throw new SyncNotOnException();
		var sealedRecovery = sync.SealedRecoveryKey ?? throw new SyncRecoveryKeyNotOnThisPcException();
		RequireHello();
		var keys = Keys();
		var secretKey = await SecretKeyAsync(keys);
		try
		{
			return RecoverySeal.Open(secretKey, keys.VaultId, sealedRecovery);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretKey);
		}
	}

	/// <summary>
	/// Restoring from sync (Task 14): <paramref name="keys"/>, unwrapped from the server's copy, become this PC's, sealed with one Hello
	/// call, and sync is on as <paramref name="sync"/> says. Pending keys go with the old ones. Refused before Hello, and again before
	/// the write, when this vault holds logins under other keys, which would no longer open.
	/// </summary>
	/// <exception cref="SyncRestoreWouldReplaceLoginsException">The vault holds logins under another vault ID.</exception>
	public async Task RestoreKeysAsync(VaultKeyMaterial keys, VaultSyncKeys sync)
	{
		RequireHello();
		lock (_gate) RequireRestorableOver(keys.VaultId);
		await _creatingKeys.WaitAsync();
		try
		{
			var restored = await VaultKeys.SealRestoredAsync(_cipher, _sealer, keys, sync);
			lock (_gate)
			{
				RequireOpen();
				RequireRestorableOver(keys.VaultId);
				restored.Save(KeysFile);
				_keys = restored;
				// A secret key held from before was the old keys' one.
				WipeHeldKey();
			}
		}
		finally
		{
			_creatingKeys.Release();
		}
		SyncChanged?.Invoke();
	}

	/// <summary>Under <c>_gate</c>.</summary>
	private void RequireRestorableOver(string vaultId)
	{
		if (_keys is { } held && held.VaultId != vaultId && _records.Count > 0) throw new SyncRestoreWouldReplaceLoginsException(_records.Count);
	}

	/// <summary>Sync found the server's keys at a later version than this PC's: the password was changed on another PC (Task 14).</summary>
	public event Action? SyncPasswordChangedElsewhere;

	/// <summary>
	/// Keeps the server's keys at <paramref name="version"/>, later than this PC's and than any it kept before, in keys.bin beside this
	/// PC's own, until the person enters the new password (<see cref="CommitNewerSyncKeys"/>). On disk when it returns.
	/// </summary>
	public void MarkPasswordChangedElsewhere(byte[] blob, long version)
	{
		lock (_gate)
		{
			RequireOpen();
			var keys = Keys();
			var sync = keys.Sync ?? throw new SyncNotOnException();
			if (version <= Math.Max(sync.Version, sync.NewerVersion)) throw new ArgumentOutOfRangeException(nameof(version), version, $"The keys this PC holds are already at version {Math.Max(sync.Version, sync.NewerVersion)}");
			var next = keys.WithSync(sync with { NewerBlob = blob, NewerVersion = version });
			next.Save(KeysFile);
			_keys = next;
		}
		SyncPasswordChangedElsewhere?.Invoke();
	}

	/// <summary>The newer keys were opened with the new password, so they become this PC's sync keys. On disk when it returns.</summary>
	/// <exception cref="SyncPendingKeysReplacedException">The newer keys are other keys, or there are none.</exception>
	public void CommitNewerSyncKeys(byte[] blob)
	{
		lock (_gate)
		{
			RequireOpen();
			var keys = Keys();
			if (keys.Sync is not { NewerBlob: { } newer } sync || !newer.AsSpan().SequenceEqual(blob)) throw new SyncPendingKeysReplacedException();
			var next = keys.WithSync(sync with { Blob = newer, Version = sync.NewerVersion, NewerBlob = null, NewerVersion = 0 });
			next.Save(KeysFile);
			_keys = next;
		}
		SyncChanged?.Invoke();
	}

	/// <summary>Under <c>_gate</c>.</summary>
	private void WriteSyncFile(IReadOnlyList<QueuedChange> queued, SyncAccountFile? account, bool signedOut) => WriteSyncFile(queued, account, signedOut, _progress);

	/// <summary>Under <c>_gate</c>. One atomic replace, so the queue, the account and the sync progress change together.</summary>
	private void WriteSyncFile(IReadOnlyList<QueuedChange> queued, SyncAccountFile? account, bool signedOut, SyncProgress? progress) =>
		JsonFiles.WriteAllBytesAtomic(SyncFile, JsonSerializer.SerializeToUtf8Bytes(new SyncFileContent(Format, queued, account, signedOut, progress), VaultJson.Options));

	/// <summary>A local change: queued first, then the items with their sync bases in one replace of vault.zing. Under <c>_gate</c>.</summary>
	private void Persist(List<LoginRecord> records, IReadOnlyList<QueuedChange> newlyQueued, Dictionary<string, SyncBase> bases)
	{
		List<QueuedChange> queued = [.. _queued, .. newlyQueued];
		WriteSyncFile(queued, _syncAccount, _syncSignedOut);
		WriteVaultFile(records, bases, _basesUser);
		_queued.AddRange(newlyQueued);
	}

	/// <summary>Under <c>_gate</c>. The items and their sync bases in one atomic replace, so an item and the server copy it is merged against never part.</summary>
	private void WriteVaultFile(List<LoginRecord> records, Dictionary<string, SyncBase> bases, string? basesUser)
	{
		var sync = basesUser is null ? null : new SyncBasesFile(basesUser, bases);
		JsonFiles.WriteAllBytesAtomic(VaultFile, JsonSerializer.SerializeToUtf8Bytes(new VaultFileContent(Format, records, sync), VaultJson.Options));
		_records.Clear();
		_records.AddRange(records);
		_basesUser = basesUser;
		if (ReferenceEquals(bases, _bases)) return;
		_bases.Clear();
		foreach (var (id, b) in bases) _bases[id] = b;
	}

	/// <summary>
	/// The bases after a local change replaced <paramref name="replaced"/>: an item that matched the server's copy keeps that copy as
	/// its base, so the change counts as unsent and a later conflict merges against it. Under <c>_gate</c>.
	/// </summary>
	private Dictionary<string, SyncBase> KeepingOriginals(IEnumerable<LoginRecord> replaced)
	{
		var bases = new Dictionary<string, SyncBase>(_bases, StringComparer.Ordinal);
		foreach (var record in replaced)
		{
			if (bases.TryGetValue(record.ItemId, out var b) && b.Original is null) bases[record.ItemId] = b with { Original = record };
		}
		return bases;
	}

	/// <summary>The secret key for a write: the held one or one unsealed with one Hello call, or, before the first save, made (and sealed) with one.</summary>
	private async Task<(VaultKeys Keys, byte[] SecretKey)> SecretKeyForWriteAsync()
	{
		if (_keys is { } existing) return (existing, await SecretKeyAsync(existing));
		await _creatingKeys.WaitAsync();
		try
		{
			// Another first save may have made them while this one waited.
			if (_keys is { } made) return (made, await SecretKeyAsync(made));
			var created = await VaultKeys.CreateAsync(_cipher, _sealer);
			try
			{
				lock (_gate)
				{
					RequireOpen();
					created.Keys.Save(KeysFile);
					_keys = created.Keys;
				}
				return created;
			}
			catch
			{
				CryptographicOperations.ZeroMemory(created.SecretKey);
				throw;
			}
		}
		finally
		{
			_creatingKeys.Release();
		}
	}

	private async Task<string> OpenSecretAsync(string id, byte[] sealedBytes, long version)
	{
		var keys = Keys();
		var secretKey = await SecretKeyAsync(keys);
		byte[]? plain = null;
		try
		{
			plain = VaultCrypto.Open(secretKey, sealedBytes, new ItemAd(keys.VaultId, id, version, ItemPart.Password));
			return Encoding.UTF8.GetString(plain);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretKey);
			if (plain is not null) CryptographicOperations.ZeroMemory(plain);
		}
	}

	private static byte[] Seal(byte[] secretKey, VaultKeys keys, string id, long version, ItemPart part, string text)
	{
		var plain = Encoding.UTF8.GetBytes(text);
		try
		{
			return VaultCrypto.Seal(secretKey, plain, new ItemAd(keys.VaultId, id, version, part));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plain);
		}
	}

	private byte[] SealSummary(VaultKeys keys, string id, long version, SummaryPayload summary)
	{
		var indexKey = keys.IndexKey(_cipher);
		try
		{
			return VaultCrypto.Seal(indexKey, JsonSerializer.SerializeToUtf8Bytes(summary, VaultJson.Options), new ItemAd(keys.VaultId, id, version, ItemPart.Summary));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(indexKey);
		}
	}

	/// <summary>
	/// The secret key, a copy the caller wipes: the held one, or one Hello unseals now, which is then held for
	/// <see cref="SecretKeyHeldFor"/> from this Hello. A cancelled or failed Hello holds nothing, nor does one answered after
	/// a wipe or once the store is closed, nor any while locks can't be seen (Ruling 47).
	/// </summary>
	private async Task<byte[]> SecretKeyAsync(VaultKeys keys)
	{
		int wipes;
		lock (_gate)
		{
			if (_heldKey is { } held) return [.. held];
			wipes = _wipes;
		}
		var secretKey = await keys.SecretKeyAsync(_cipher, _sealer);
		lock (_gate)
		{
			if (!_disposed && _session.Watching && _wipes == wipes && _heldKey is null)
			{
				var kept = (byte[])secretKey.Clone();
				_heldKey = kept;
				_heldTimer = _scheduler.Schedule(SecretKeyHeldFor, () => WipeIfHeld(kept));
			}
		}
		return secretKey;
	}

	/// <summary>Wipes the held secret key, so the next password read or write asks Hello: Windows locked, or the session switched.</summary>
	public void ForgetSecretKey()
	{
		lock (_gate) WipeHeldKey();
	}

	private void WipeIfHeld(byte[] key)
	{
		lock (_gate)
		{
			if (ReferenceEquals(_heldKey, key)) WipeHeldKey();
		}
	}

	/// <summary>Called under <c>_gate</c>.</summary>
	private void WipeHeldKey()
	{
		_wipes++;
		_heldTimer?.Dispose();
		_heldTimer = null;
		if (_heldKey is null) return;
		CryptographicOperations.ZeroMemory(_heldKey);
		_heldKey = null;
	}

	private void RequireHello()
	{
		if (!_sealer.Available) throw new HelloUnavailableException("Set up Windows Hello to use the password vault. Zing keeps passwords only behind Hello.");
	}

	private VaultKeys Keys() => _keys ?? throw new InvalidOperationException("The vault has no keys yet: only a save makes them");

	private (LoginRecord Record, SummaryPayload Summary) Find(string id)
	{
		lock (_gate)
		{
			// A "never for this site" mark isn't a login: it has no password to read, change or delete.
			var record = _records.Find(r => r.ItemId == id && !r.Tombstone && !_summaries[r.ItemId].NeverSave) ?? throw new VaultItemNotFoundException(id);
			return (record, _summaries[id]);
		}
	}

	private SummaryPayload Summary(string id) => Find(id).Summary;

	private string KeysFile => System.IO.Path.Combine(_dir, KeysFileName);

	private string VaultFile => System.IO.Path.Combine(_dir, VaultFileName);

	private string SyncFile => System.IO.Path.Combine(_dir, SyncFileName);

	/// <summary>Any failure to read or open a vault file is a <see cref="VaultFileException"/> naming it.</summary>
	private static T Guard<T>(string path, Func<string, T> load)
	{
		try
		{
			return load(path);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or CryptographicException or VaultTamperedException or PaddingException)
		{
			throw new VaultFileException(path, error);
		}
	}

	private static (List<LoginRecord> Records, Dictionary<string, SyncBase> Bases, string? BasesUser) LoadRecords(string path)
	{
		if (!File.Exists(path)) return ([], new(StringComparer.Ordinal), null);
		var file = JsonSerializer.Deserialize<VaultFileContent>(File.ReadAllBytes(path), VaultJson.Options) ?? throw new InvalidDataException($"{path} is empty");
		if (file.Format != Format) throw new InvalidDataException($"{path} is format {file.Format}; this Zing reads {Format}");
		if (file.Items is null) throw new InvalidDataException($"{path} has no items list");
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var item in file.Items)
		{
			if (item is null) throw new InvalidDataException($"{path} has a null item");
			if (string.IsNullOrEmpty(item.ItemId)) throw new InvalidDataException($"{path} has an item without an ID");
			if (!ids.Add(item.ItemId)) throw new InvalidDataException($"{path} holds item {item.ItemId} twice");
			if (item.Version < 1) throw new InvalidDataException($"Item {item.ItemId} has version {item.Version}; versions start at 1");
			if (item.Summary is null || item.Password is null || item.History is null || item.History.Any(h => h is null)) throw new InvalidDataException($"Item {item.ItemId} is missing a ciphertext");
		}
		var bases = new Dictionary<string, SyncBase>(StringComparer.Ordinal);
		if (file.Sync is not { } sync) return ([.. file.Items], bases, null);
		if (string.IsNullOrEmpty(sync.UserId) || sync.Bases is null) throw new InvalidDataException($"{path} has sync bases without their user");
		foreach (var (id, b) in sync.Bases)
		{
			if (!ids.Contains(id)) throw new InvalidDataException($"{path} has a sync base for item {id}, which it doesn't hold");
			if (b is null || b.Revision < 0) throw new InvalidDataException($"Item {id}'s sync base has no revision");
			if (b.Original is { } original && (original.ItemId != id || original.Summary is null || original.Password is null || original.History is null || original.History.Any(h => h is null)))
				throw new InvalidDataException($"Item {id}'s sync base isn't a copy of it");
			bases[id] = b;
		}
		return ([.. file.Items], bases, sync.UserId);
	}

	private static (List<QueuedChange> Queued, SyncAccountFile? Account, bool SignedOut, SyncProgress? Progress) LoadSyncFile(string path)
	{
		if (!File.Exists(path)) return ([], null, false, null);
		var file = JsonSerializer.Deserialize<SyncFileContent>(File.ReadAllBytes(path), VaultJson.Options) ?? throw new InvalidDataException($"{path} is empty");
		if (file.Format != Format) throw new InvalidDataException($"{path} is format {file.Format}; this Zing reads {Format}");
		if (file.Queued is null) throw new InvalidDataException($"{path} has no queued list");
		if (file.Queued.Any(c => c is null || string.IsNullOrEmpty(c.ItemId))) throw new InvalidDataException($"{path} has a queued change without an item ID");
		if (file.Account is { } account && (string.IsNullOrEmpty(account.UserId) || account.Email is null || account.SealedRefresh is not { Length: > 0 }))
			throw new InvalidDataException($"{path} has a sync account without its user, email or refresh token");
		if (file.Progress is { } progress && (string.IsNullOrEmpty(progress.UserId) || progress.Cursor < 0 || progress.RevisionFloor < 0 || progress.ManifestFloor < 0))
			throw new InvalidDataException($"{path} has sync progress without its user, or with a negative revision");
		// A wrong-length hash would read as a rollback at the next sync, so it's an unreadable file instead (O4, Ruling 60).
		if (file.Progress?.ManifestHash is { } hash && hash.Length != SHA256.HashSizeInBytes)
			throw new InvalidDataException($"{path} has a manifest hash of {hash.Length} bytes, not a SHA-256");
		return ([.. file.Queued], file.Account, file.SignedOut, file.Progress);
	}

	private Dictionary<string, SummaryPayload> DecryptSummaries()
	{
		var summaries = new Dictionary<string, SummaryPayload>(StringComparer.Ordinal);
		if (_records.Count == 0) return summaries;
		var keys = _keys ?? throw new InvalidDataException($"The vault has {_records.Count} items but no {KeysFileName} to open them");
		var indexKey = keys.IndexKey(_cipher);
		try
		{
			foreach (var record in _records)
			{
				var plain = VaultCrypto.Open(indexKey, record.Summary, new ItemAd(keys.VaultId, record.ItemId, record.Version, ItemPart.Summary));
				var summary = JsonSerializer.Deserialize<SummaryPayload>(plain, VaultJson.Options) ?? throw new InvalidDataException($"Login {record.ItemId} has an empty summary");
				CheckSummary(record, summary);
				if (!record.Tombstone) summaries[record.ItemId] = summary;
			}
			return summaries;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(indexKey);
		}
	}

	/// <summary>The checks every summary passes, those that come from the server included.</summary>
	private static void CheckSummary(LoginRecord record, SummaryPayload summary)
	{
		if (summary.Origin is null || summary.Username is null || summary.Title is null || summary.Note is null) throw new InvalidDataException($"Login {record.ItemId}'s summary is missing a field");
		// Ruling 25, both ways (Ruling 56): the tombstone flag is outside every ciphertext's associated data, so it counts only if the
		// sealed summary is a tombstone too. A flag on a login in Recently deleted would hide it, and a sealed tombstone with its flag
		// cleared would pass as a login.
		if (record.Tombstone != summary.Tombstone) throw new VaultTamperedException($"Item {record.ItemId}'s tombstone flag doesn't match its sealed summary");
		if (summary.Tombstone)
		{
			if (summary.DeletedAt is null) throw new VaultTamperedException($"Tombstone {record.ItemId}'s sealed summary isn't deleted");
			if (record.Password.Length != 0 || record.History.Length != 0 || summary.History is not { Count: 0 } || summary.PasswordHash is not null) throw new InvalidDataException($"Tombstone {record.ItemId} still holds a password");
			return;
		}
		if (summary.NeverSave)
		{
			if (record.Password.Length != 0 || record.History.Length != 0 || summary.PasswordVersion != 0 || summary.PasswordHash is not null) throw new InvalidDataException($"Item {record.ItemId} is a \"never for this site\" mark but holds a password");
			return;
		}
		if (summary.History is null || summary.History.Any(h => h is null)) throw new InvalidDataException($"Login {record.ItemId}'s summary has no history list");
		if (summary.History.Count != record.History.Length) throw new InvalidDataException($"Login {record.ItemId}'s summary lists {summary.History.Count} old passwords but it holds {record.History.Length}");
		if (summary.PasswordVersion < 1 || summary.PasswordVersion > record.Version) throw new InvalidDataException($"Login {record.ItemId}'s password is sealed at version {summary.PasswordVersion}, outside 1 to {record.Version}");
		// Ruling 56 (amends Ruling 24): two ciphertexts can share a version (a merge keeps both new passwords of version v+1), so their
		// associated data alone can't tell the password from an old one. The sealed summary names each ciphertext by its hash.
		if (!CipherHash.Matches(summary.PasswordHash, record.Password)) throw new VaultTamperedException($"Login {record.ItemId}'s password isn't the one its sealed summary names");
		for (var i = 0; i < record.History.Length; i++)
		{
			if (!CipherHash.Matches(summary.History[i].Hash, record.History[i])) throw new VaultTamperedException($"Login {record.ItemId}'s old password {i} isn't the one its sealed summary names");
		}
	}

	// Sync is absent from a vault.zing written before sync, which reads as no bases.
	private sealed record VaultFileContent(int Format, IReadOnlyList<LoginRecord> Items, SyncBasesFile? Sync = null);

	private sealed record SyncBasesFile(string UserId, Dictionary<string, SyncBase> Bases);

	private sealed record SyncFileContent(int Format, IReadOnlyList<QueuedChange> Queued, SyncAccountFile? Account = null, bool SignedOut = false, SyncProgress? Progress = null);

	private sealed record SyncAccountFile(string UserId, string Email, byte[] SealedRefresh);
}

/// <summary>What the summary ciphertext holds: the login's fields, and the version each password ciphertext was sealed under.</summary>
internal sealed record SummaryPayload(
	string Origin,
	string Username,
	string Title,
	string Note,
	long CreatedAt,
	long UpdatedAt,
	long PasswordUpdatedAt,
	long? DeletedAt,
	long PasswordVersion,
	IReadOnlyList<HistoryPayload> History,
	// A "never for this site" mark: a summary-only item, with no password and no history.
	bool NeverSave = false,
	// A login purged 30 days after its delete: only DeletedAt is kept, and the record is flagged too (Ruling 25 checks both ways).
	bool Tombstone = false,
	// Goes up by one when a login purged meanwhile is made again from this PC's edit (Ruling 18), so it orders after its own tombstone.
	long Generation = 0,
	// The SHA-256 of the password's ciphertext (Ruling 56); null for an item with no password ("never" marks and tombstones).
	byte[]? PasswordHash = null)
{
	public LoginSummary ToSummary(string id) => new(id, Origin, Username, Title, Note, CreatedAt, UpdatedAt, PasswordUpdatedAt, DeletedAt, History.Count);
}

/// <summary>One old password: the version it was sealed under (as the password), when it was replaced, and its ciphertext's SHA-256 (Ruling 56).</summary>
internal sealed record HistoryPayload(long Version, long ReplacedAt, byte[] Hash);

/// <summary>How a sealed summary names a password ciphertext (Ruling 56): its SHA-256, so the server can't swap two that share a version.</summary>
internal static class CipherHash
{
	public static byte[] Of(byte[] ciphertext) => SHA256.HashData(ciphertext);

	public static bool Matches(byte[]? hash, byte[] ciphertext) => hash is { Length: SHA256.HashSizeInBytes } && CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(ciphertext));
}
