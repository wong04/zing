using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
public sealed class VaultStore : IDisposable
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
	private readonly List<QueuedChange> _queued;
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
		_records = Guard(VaultFile, LoadRecords);
		_queued = Guard(SyncFile, LoadQueue);
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
		Changed?.Invoke();
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
				var summary = new SummaryPayload(origin, login.Username, login.Title, login.Note, now, now, now, null, 1, []);
				var password = Seal(secretKey, keys, id, 1, ItemPart.Password, login.Password);
				Commit(null, new LoginRecord(id, 1, SealSummary(keys, id, 1, summary), password, [], false), summary);
			}
			Changed?.Invoke();
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
			Changed?.Invoke();
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
		var changed = edit(summary) with
		{
			UpdatedAt = now,
			PasswordUpdatedAt = now,
			PasswordVersion = version,
			History = [new HistoryPayload(summary.PasswordVersion, now), .. summary.History],
		};
		var sealedPassword = Seal(secretKey, keys, record.ItemId, version, ItemPart.Password, password);
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
					var fresh = new SummaryPayload(origins[i], login.Username, login.Title, login.Note, now, now, now, null, 1, []);
					changed[newId] = (new LoginRecord(newId, 1, SealSummary(keys, newId, 1, fresh), Seal(secretKey, keys, newId, 1, ItemPart.Password, login.Password), [], false), fresh);
					added.Add(newId);
					live[(origins[i], login.Username)] = newId;
				}
				wrote = changed.Count > 0;
				if (wrote) CommitBatch(changed, added);
				outcome = new ImportOutcome(added.Count, merged);
			}
			if (wrote) Changed?.Invoke();
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
		Changed?.Invoke();
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
		Changed?.Invoke();
	}

	/// <summary>
	/// Writes the change: queued first, then the item. A crash between the two leaves a change queued but not stored (and the next write
	/// reuses that version), never one stored but not queued. So queue entries are hints: sync (Task 13) sends each item as it's stored now.
	/// </summary>
	private void Commit(LoginRecord? replaced, LoginRecord record, SummaryPayload summary)
	{
		RequireOpen();
		List<LoginRecord> records = replaced is null ? [.. _records, record] : [.. _records.Select(r => ReferenceEquals(r, replaced) ? record : r)];
		Persist(records, [new QueuedChange(record.ItemId, record.Version)]);
		_summaries[record.ItemId] = summary;
	}

	/// <summary>Several items in one write: <paramref name="added"/> are new, stored in that order; the rest replace the item of their ID. Each is queued once, at the version it now has.</summary>
	private void CommitBatch(IReadOnlyDictionary<string, (LoginRecord Record, SummaryPayload Summary)> changed, IReadOnlyList<string> added)
	{
		RequireOpen();
		List<LoginRecord> records = [.. _records.Select(r => changed.TryGetValue(r.ItemId, out var next) ? next.Record : r), .. added.Select(id => changed[id].Record)];
		Persist(records, [.. changed.Values.Select(c => new QueuedChange(c.Record.ItemId, c.Record.Version))]);
		foreach (var (id, change) in changed) _summaries[id] = change.Summary;
	}

	private void Persist(List<LoginRecord> records, IReadOnlyList<QueuedChange> newlyQueued)
	{
		List<QueuedChange> queued = [.. _queued, .. newlyQueued];
		JsonFiles.WriteAllBytesAtomic(SyncFile, JsonSerializer.SerializeToUtf8Bytes(new SyncFileContent(Format, queued), VaultJson.Options));
		JsonFiles.WriteAllBytesAtomic(VaultFile, JsonSerializer.SerializeToUtf8Bytes(new VaultFileContent(Format, records), VaultJson.Options));
		_records.Clear();
		_records.AddRange(records);
		_queued.AddRange(newlyQueued);
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

	private static List<LoginRecord> LoadRecords(string path)
	{
		if (!File.Exists(path)) return [];
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
		return [.. file.Items];
	}

	private static List<QueuedChange> LoadQueue(string path)
	{
		if (!File.Exists(path)) return [];
		var file = JsonSerializer.Deserialize<SyncFileContent>(File.ReadAllBytes(path), VaultJson.Options) ?? throw new InvalidDataException($"{path} is empty");
		if (file.Format != Format) throw new InvalidDataException($"{path} is format {file.Format}; this Zing reads {Format}");
		if (file.Queued is null) throw new InvalidDataException($"{path} has no queued list");
		if (file.Queued.Any(c => c is null || string.IsNullOrEmpty(c.ItemId))) throw new InvalidDataException($"{path} has a queued change without an item ID");
		return [.. file.Queued];
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
				// Ruling 25: the tombstone flag is outside every ciphertext's associated data, so it counts only if the sealed summary is deleted too.
				if (record.Tombstone && summary.DeletedAt is null) throw new VaultTamperedException($"Item {record.ItemId} is flagged as a tombstone but its sealed summary isn't deleted");
				if (!record.Tombstone) summaries[record.ItemId] = summary;
			}
			return summaries;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(indexKey);
		}
	}

	private static void CheckSummary(LoginRecord record, SummaryPayload summary)
	{
		if (summary.Origin is null || summary.Username is null || summary.Title is null || summary.Note is null) throw new InvalidDataException($"Login {record.ItemId}'s summary is missing a field");
		if (summary.NeverSave)
		{
			if (record.Password.Length != 0 || record.History.Length != 0 || summary.PasswordVersion != 0) throw new InvalidDataException($"Item {record.ItemId} is a \"never for this site\" mark but holds a password");
			return;
		}
		if (summary.History is null || summary.History.Any(h => h is null)) throw new InvalidDataException($"Login {record.ItemId}'s summary has no history list");
		if (summary.History.Count != record.History.Length) throw new InvalidDataException($"Login {record.ItemId}'s summary lists {summary.History.Count} old passwords but it holds {record.History.Length}");
		if (summary.PasswordVersion < 1 || summary.PasswordVersion > record.Version) throw new InvalidDataException($"Login {record.ItemId}'s password is sealed at version {summary.PasswordVersion}, outside 1 to {record.Version}");
	}

	private sealed record VaultFileContent(int Format, IReadOnlyList<LoginRecord> Items);

	private sealed record SyncFileContent(int Format, IReadOnlyList<QueuedChange> Queued);
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
	bool NeverSave = false)
{
	public LoginSummary ToSummary(string id) => new(id, Origin, Username, Title, Note, CreatedAt, UpdatedAt, PasswordUpdatedAt, DeletedAt);
}

/// <summary>One old password: the version it was sealed under (as the password), and when it was replaced.</summary>
internal sealed record HistoryPayload(long Version, long ReplacedAt);
