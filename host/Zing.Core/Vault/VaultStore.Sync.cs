using System.Security.Cryptography;
using System.Text.Json;
using Zing.Core.Sync;

namespace Zing.Core.Vault;

/// <summary>
/// The server's copy of an item as this PC last synced it. <see cref="Original"/> is null while the item still is that copy; a
/// change made here keeps the copy it replaced, so the item is unsent and a conflict merges against it.
/// </summary>
/// <param name="Revision">The server revision of that copy; 0 for a login the server purged, which this PC keeps as a tombstone.</param>
public sealed record SyncBase(long Revision, LoginRecord? Original);

/// <summary>Where this PC's next pull starts, and the rollback floors: the highest server revision and manifest version it has seen.</summary>
public sealed record SyncProgress(string UserId, long Cursor, long RevisionFloor, long ManifestFloor)
{
	public static SyncProgress Fresh(string userId) => new(userId, 0, 0, 0);
}

/// <summary>An item changed here since the server's copy, or never sent, and the revision of the server copy it replaces (0: none).</summary>
public sealed record SyncUnsent(LoginRecord Record, long BaseRevision);

/// <summary>Sync bases or progress used for a server user they don't belong to.</summary>
public sealed class SyncUserMismatchException(string expected, string? held)
	: Exception($"This PC's sync records are for server user {held ?? "nobody"}, not {expected}");

/// <summary>
/// The store's side of sync (Task 13). Everything here runs under <c>_gate</c> and opens summaries with the DPAPI-sealed index key
/// only, so sync never needs Windows Hello (spec decision 10). The truth of what to send is each item against its sync base, written
/// with the items in one replace of vault.zing; the queue in sync.json is only a hint (the line 104 carry-over), so a crash between
/// a push and its bookkeeping sends that item again and finds it already there, never twice.
/// </summary>
public sealed partial class VaultStore
{
	/// <summary>This PC's sync progress for <paramref name="userId"/>, or a fresh start if it has none for that user.</summary>
	public SyncProgress SyncProgressFor(string userId)
	{
		lock (_gate) return ProgressFor(userId);
	}

	private SyncProgress ProgressFor(string userId) => _progress is { } progress && progress.UserId == userId ? progress : SyncProgress.Fresh(userId);

	/// <summary>
	/// Makes the sync bases and progress <paramref name="userId"/>'s. Ones kept for another server user are forgotten, since their
	/// revisions mean nothing on this one: every item is then unsent, and the next pull starts from 0.
	/// </summary>
	public void StartSyncAs(string userId)
	{
		lock (_gate)
		{
			RequireOpen();
			if (_basesUser != userId) WriteVaultFile([.. _records], new Dictionary<string, SyncBase>(StringComparer.Ordinal), userId);
			if (_progress?.UserId == userId) return;
			var fresh = SyncProgress.Fresh(userId);
			WriteSyncFile(_queued, _syncAccount, _syncSignedOut, fresh);
			_progress = fresh;
		}
	}

	private void RequireSyncUser(string userId)
	{
		if (_basesUser != userId) throw new SyncUserMismatchException(userId, _basesUser);
	}

	private bool IsUnsent(LoginRecord record) => !_bases.TryGetValue(record.ItemId, out var b) || b.Original is not null;

	/// <summary>Every item changed here since the server's copy, or never sent, in the order the vault holds them.</summary>
	public IReadOnlyList<SyncUnsent> Unsent()
	{
		lock (_gate) return [.. _records.Where(IsUnsent).Select(r => new SyncUnsent(r, _bases.TryGetValue(r.ItemId, out var b) ? b.Revision : 0))];
	}

	/// <summary>Logins (deleted ones included) not yet on the server as they are here: Settings' "not uploaded yet". "Never" marks don't count.</summary>
	public int UnsentLogins
	{
		get
		{
			lock (_gate) return _records.Count(r => IsUnsent(r) && !(_summaries.TryGetValue(r.ItemId, out var s) && s.NeverSave));
		}
	}

	/// <summary>
	/// The server took these at the revisions it gave: each becomes its item's base, and its queued hints go. An item changed here
	/// meanwhile keeps what was sent as its base, so the newer change is still unsent. Raises the revision floor.
	/// </summary>
	/// <exception cref="SyncProtocolException">The server took an item that wasn't sent.</exception>
	public void MarkPushed(string userId, IReadOnlyList<LoginRecord> sent, IReadOnlyList<SyncAccepted> accepted)
	{
		if (accepted.Count == 0) return;
		var byId = sent.ToDictionary(r => r.ItemId, StringComparer.Ordinal);
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			var bases = new Dictionary<string, SyncBase>(_bases, StringComparer.Ordinal);
			var versions = new Dictionary<string, long>(StringComparer.Ordinal);
			foreach (var item in accepted)
			{
				if (!byId.TryGetValue(item.ItemId, out var record)) throw new SyncProtocolException($"The sync server took item {item.ItemId}, which wasn't sent");
				var current = _records.Find(r => r.ItemId == item.ItemId);
				bases[item.ItemId] = new SyncBase(item.Revision, current is not null && SameRecord(current, record) ? null : record);
				versions[item.ItemId] = record.Version;
			}
			WriteVaultFile([.. _records], bases, userId);
			var progress = ProgressFor(userId);
			progress = progress with { RevisionFloor = Math.Max(progress.RevisionFloor, accepted.Max(a => a.Revision)) };
			List<QueuedChange> left = [.. _queued.Where(c => !versions.TryGetValue(c.ItemId, out var version) || c.Version > version)];
			WriteSyncFile(left, _syncAccount, _syncSignedOut, progress);
			_progress = progress;
			_queued.Clear();
			_queued.AddRange(left);
		}
	}

	/// <summary>
	/// Stores server copies: a pulled page, or the copies a push conflicted with. Each is checked first (its tag, Ruling 25, and
	/// this PC's floor for it), and nothing of the page is stored if one fails. A copy of an item unchanged here replaces it; one
	/// changed here too is merged (<see cref="Merge.Items"/>) and stays unsent. A conflict at revision 0 means the server purged the
	/// item (Ruling 18). The items and their bases are written in one replace, then the cursor and floor.
	/// </summary>
	/// <param name="cursor">Where the next pull starts once these are stored; null leaves it.</param>
	/// <param name="serverRevision">The revision the server reported with them, for the floor; null leaves it.</param>
	/// <exception cref="ServerRolledBackException">A copy older than one this PC has seen.</exception>
	/// <exception cref="VaultTamperedException">A copy that fails its tag, or whose tombstone flag its sealed summary doesn't back.</exception>
	public void ApplyFromServer(string userId, IReadOnlyList<SyncItem> items, long? cursor = null, long? serverRevision = null)
	{
		bool changed;
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			using var stage = new Stage(this);
			foreach (var item in items) stage.Apply(item);
			changed = stage.Changed;
			if (changed) stage.Write(userId);
			var progress = ProgressFor(userId);
			var next = progress with
			{
				Cursor = cursor ?? progress.Cursor,
				RevisionFloor = Math.Max(progress.RevisionFloor, serverRevision ?? 0),
			};
			if (next != progress || _progress is null)
			{
				WriteSyncFile(_queued, _syncAccount, _syncSignedOut, next);
				_progress = next;
			}
		}
		if (changed) Changed?.Invoke();
	}

	/// <summary>
	/// After a full resync: an item this PC synced that the server no longer holds was purged as a tombstone (spec: a PC past the
	/// horizon). One unchanged here becomes a tombstone here too, so it can't come back; one changed here is left unsent, and its
	/// push meets the purge (Ruling 18).
	/// </summary>
	public void MarkPurged(string userId, IReadOnlyCollection<string> served)
	{
		var held = served as IReadOnlySet<string> ?? served.ToHashSet(StringComparer.Ordinal);
		bool changed;
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			using var stage = new Stage(this);
			foreach (var record in _records)
			{
				if (!_bases.TryGetValue(record.ItemId, out var b) || b.Revision == 0 || b.Original is not null || held.Contains(record.ItemId)) continue;
				stage.MakePurged(record.ItemId);
			}
			changed = stage.Changed;
			if (changed) stage.Write(userId);
		}
		if (changed) Changed?.Invoke();
	}

	/// <summary>
	/// Every login (or "never" mark) deleted 30 days ago or more becomes a tombstone (spec, deletes): its password and history go, and
	/// its sealed summary keeps only when it was deleted. Unsent until a push takes it. No Hello: only the summary is sealed.
	/// </summary>
	public int TombstoneDue(long now)
	{
		int count;
		lock (_gate)
		{
			RequireOpen();
			var due = _records.Where(r => !r.Tombstone && _summaries[r.ItemId] is { DeletedAt: { } deletedAt } && now - deletedAt >= VaultModel.RetentionMs).ToList();
			count = due.Count;
			if (count == 0) return 0;
			var keys = Keys();
			var indexKey = keys.IndexKey(_cipher);
			try
			{
				var tombstones = due.ToDictionary(r => r.ItemId, r => TombstoneOf(indexKey, keys, r, _summaries[r.ItemId], _summaries[r.ItemId].DeletedAt!.Value), StringComparer.Ordinal);
				List<LoginRecord> records = [.. _records.Select(r => tombstones.TryGetValue(r.ItemId, out var t) ? t : r)];
				Persist(records, [.. tombstones.Values.Select(t => new QueuedChange(t.ItemId, t.Version))], KeepingOriginals(due));
				foreach (var id in tombstones.Keys) _summaries.Remove(id);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(indexKey);
			}
		}
		Changed?.Invoke();
		return count;
	}

	/// <summary>Queued hints for items the server now holds as they are here.</summary>
	public void DequeueSent()
	{
		lock (_gate)
		{
			RequireOpen();
			var byId = _records.ToDictionary(r => r.ItemId, StringComparer.Ordinal);
			List<QueuedChange> left = [.. _queued.Where(c => byId.TryGetValue(c.ItemId, out var record) && IsUnsent(record))];
			if (left.Count == _queued.Count) return;
			WriteSyncFile(left, _syncAccount, _syncSignedOut);
			_queued.Clear();
			_queued.AddRange(left);
		}
	}

	/// <summary>The account manifest's entries as this PC knows the server: each item it synced that isn't a tombstone, at the server's version.</summary>
	public IReadOnlyList<SyncManifestEntry> ManifestEntries()
	{
		lock (_gate)
		{
			return [.. _records
				.Where(r => _bases.TryGetValue(r.ItemId, out var b) && b.Revision > 0 && !(b.Original ?? r).Tombstone)
				.Select(r => new SyncManifestEntry(r.ItemId, (_bases[r.ItemId].Original ?? r).Version))
				.OrderBy(e => e.ItemId, StringComparer.Ordinal)];
		}
	}

	/// <summary>
	/// The version of <paramref name="itemId"/> this PC has from the server, for the manifest check: its base's, or a purged login's
	/// own tombstone (the server holds nothing of it); null for an item it never had from the server.
	/// </summary>
	public long? KnownVersion(string itemId)
	{
		lock (_gate)
		{
			if (!_bases.TryGetValue(itemId, out var b)) return null;
			var record = _records.Find(r => r.ItemId == itemId)!;
			return (b.Original ?? record).Version;
		}
	}

	/// <exception cref="SyncManifestTooLargeException">More items than one server blob can list.</exception>
	public byte[] SealManifest(IReadOnlyList<SyncManifestEntry> entries, long version)
	{
		lock (_gate)
		{
			var keys = Keys();
			var indexKey = keys.IndexKey(_cipher);
			try
			{
				var manifest = SyncManifest.Seal(indexKey, keys.VaultId, version, entries);
				if (manifest.Length > SyncManifest.MaxBytes) throw new SyncManifestTooLargeException(entries.Count);
				return manifest;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(indexKey);
			}
		}
	}

	/// <exception cref="VaultTamperedException">Another vault's manifest, another version's, or one that was changed.</exception>
	public IReadOnlyList<SyncManifestEntry> OpenManifest(VersionedBlob manifest)
	{
		lock (_gate)
		{
			var keys = Keys();
			var indexKey = keys.IndexKey(_cipher);
			try
			{
				return SyncManifest.Open(indexKey, keys.VaultId, manifest.Version, manifest.Blob);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(indexKey);
			}
		}
	}

	/// <summary>Raises the manifest floor to <paramref name="manifest"/>; floors never go down.</summary>
	public void RaiseManifestFloor(string userId, long manifest)
	{
		lock (_gate)
		{
			RequireOpen();
			var progress = ProgressFor(userId);
			if (progress.ManifestFloor >= manifest && _progress is not null) return;
			var next = progress with { ManifestFloor = Math.Max(progress.ManifestFloor, manifest) };
			WriteSyncFile(_queued, _syncAccount, _syncSignedOut, next);
			_progress = next;
		}
	}

	/// <summary>
	/// Upload this PC's copy (spec, Rollback protection), after the server went back: <paramref name="server"/> is everything it holds
	/// now. Every item this PC holds becomes unsent over the server's copy, its version raised past that copy's where needed (the summary
	/// is sealed again, nothing else, so no Hello), and an item only the server holds is taken as it is. The floors restart from what
	/// the server reports now, since this PC's copy is what the server is about to hold.
	/// </summary>
	public void TakeOverServer(string userId, IReadOnlyList<SyncItem> server, long serverRevision)
	{
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			using var stage = new Stage(this);
			var onServer = new HashSet<string>(StringComparer.Ordinal);
			foreach (var item in server.Where(i => i.Revision > 0))
			{
				onServer.Add(item.ItemId);
				stage.TakeOver(item);
			}
			foreach (var record in _records.Where(r => !onServer.Contains(r.ItemId))) stage.Bases.Remove(record.ItemId);
			stage.Write(userId);
			var progress = new SyncProgress(userId, serverRevision, serverRevision, 0);
			WriteSyncFile(_queued, _syncAccount, _syncSignedOut, progress);
			_progress = progress;
		}
		Changed?.Invoke();
	}

	private static bool SameRecord(LoginRecord a, LoginRecord b) =>
		a.ItemId == b.ItemId && a.Version == b.Version && a.Tombstone == b.Tombstone
		&& a.Summary.AsSpan().SequenceEqual(b.Summary) && a.Password.AsSpan().SequenceEqual(b.Password)
		&& a.History.Length == b.History.Length && a.History.Zip(b.History).All(h => h.First.AsSpan().SequenceEqual(h.Second));

	private static byte[] SealSummaryWith(byte[] indexKey, VaultKeys keys, string id, long version, SummaryPayload summary) =>
		VaultCrypto.Seal(indexKey, JsonSerializer.SerializeToUtf8Bytes(summary, VaultJson.Options), new ItemAd(keys.VaultId, id, version, ItemPart.Summary));

	/// <summary>A tombstone for the record: its version raised by one, no password or history, and a summary that keeps only the delete.</summary>
	private static LoginRecord TombstoneOf(byte[] indexKey, VaultKeys keys, LoginRecord record, SummaryPayload summary, long deletedAt)
	{
		var version = record.Version + 1;
		var tombstone = new SummaryPayload("", "", "", "", 0, deletedAt, 0, deletedAt, 0, [], Tombstone: true, Generation: summary.Generation);
		return new LoginRecord(record.ItemId, version, SealSummaryWith(indexKey, keys, record.ItemId, version, tombstone), [], [], true);
	}

	/// <summary>Opens and checks a summary as every summary is checked at load (CheckSummary, Ruling 25).</summary>
	private static SummaryPayload OpenSummary(byte[] indexKey, VaultKeys keys, LoginRecord record)
	{
		var plain = VaultCrypto.Open(indexKey, record.Summary, new ItemAd(keys.VaultId, record.ItemId, record.Version, ItemPart.Summary));
		SummaryPayload summary;
		try
		{
			summary = JsonSerializer.Deserialize<SummaryPayload>(plain, VaultJson.Options) ?? throw new InvalidDataException($"Login {record.ItemId} has an empty summary");
		}
		catch (JsonException error)
		{
			throw new InvalidDataException($"Login {record.ItemId}'s summary isn't readable: {error.Message}", error);
		}
		CheckSummary(record, summary);
		return summary;
	}

	/// <summary>
	/// Changes to the items and their bases, made on copies and written in one replace (<see cref="Write"/>), so a check that fails
	/// part way leaves the store as it was. Lives under <c>_gate</c>, and holds the index key until disposed.
	/// </summary>
	private sealed class Stage : IDisposable
	{
		private readonly VaultStore _store;
		private readonly VaultKeys _keys;
		private readonly byte[] _indexKey;
		private readonly List<LoginRecord> _records;
		private readonly Dictionary<string, SummaryPayload?> _summaries = new(StringComparer.Ordinal);

		public Dictionary<string, SyncBase> Bases { get; }

		public bool Changed { get; private set; }

		public Stage(VaultStore store)
		{
			_store = store;
			_keys = store.Keys();
			_indexKey = _keys.IndexKey(store._cipher);
			_records = [.. store._records];
			Bases = new Dictionary<string, SyncBase>(store._bases, StringComparer.Ordinal);
		}

		public void Dispose() => CryptographicOperations.ZeroMemory(_indexKey);

		public void Write(string userId)
		{
			_store.WriteVaultFile(_records, Bases, userId);
			foreach (var (id, summary) in _summaries)
			{
				if (summary is null) _store._summaries.Remove(id);
				else _store._summaries[id] = summary;
			}
		}

		private int IndexOf(string id) => _records.FindIndex(r => r.ItemId == id);

		private SummaryPayload Open(LoginRecord record) => OpenSummary(_indexKey, _keys, record);

		private byte[] Seal(string id, long version, SummaryPayload summary) => SealSummaryWith(_indexKey, _keys, id, version, summary);

		private void SetBase(string id, SyncBase value)
		{
			if (Bases.TryGetValue(id, out var held) && held.Revision == value.Revision && ReferenceEquals(held.Original, value.Original)) return;
			Bases[id] = value;
			Changed = true;
		}

		private void Put(int at, LoginRecord record, SummaryPayload summary)
		{
			if (at < 0) _records.Add(record);
			else _records[at] = record;
			_summaries[record.ItemId] = record.Tombstone ? null : summary;
			Changed = true;
		}

		private static ItemOrder Order(LoginRecord record, SummaryPayload summary) => new(summary.Generation, record.Version);

		public void Apply(SyncItem item)
		{
			if (item.Revision == 0)
			{
				Purged(item.ItemId);
				return;
			}
			var server = SyncWire.ToRecord(item);
			var theirs = Open(server);
			var at = IndexOf(item.ItemId);
			var local = at < 0 ? null : _records[at];
			Bases.TryGetValue(item.ItemId, out var b);
			var floor = b is not null ? b.Original ?? local : local is { Tombstone: true } ? local : null;
			if (floor is not null) RollbackGuard.CheckItem(item.ItemId, Order(server, theirs), Order(floor, Open(floor)));

			if (local is null)
			{
				Put(-1, server, theirs);
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			if (SameRecord(local, server) || b is { Original: null })
			{
				if (!SameRecord(local, server)) Put(at, server, theirs);
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			Conflict(at, local, b?.Original, server, theirs, item.Revision);
		}

		/// <summary>Both sides changed the item since <paramref name="original"/> (null: no copy in common).</summary>
		private void Conflict(int at, LoginRecord local, LoginRecord? original, LoginRecord server, SummaryPayload theirs, long revision)
		{
			var mine = Open(local);
			if (theirs.Tombstone)
			{
				// A login restored or kept here outlives a purge decided elsewhere: a conflict never loses a password.
				if (!mine.Tombstone && mine.DeletedAt is null) KeepMine(at, local, mine, server, theirs, revision);
				else Adopt(at, server, theirs, revision);
				return;
			}
			if (mine.Tombstone)
			{
				if (theirs.DeletedAt is null) Adopt(at, server, theirs, revision);
				else KeepMine(at, local, mine, server, theirs, revision);
				return;
			}

			MergeInput? common = null;
			if (original is not null && Open(original) is { Tombstone: false } before) common = new MergeInput(before, original.Password, original.History);
			var theirsInput = new MergeInput(theirs, server.Password, server.History);
			var merged = Merge.Items(common, new MergeInput(mine, local.Password, local.History), theirsInput);
			if (SameContent(merged, theirsInput))
			{
				Adopt(at, server, theirs, revision);
				return;
			}
			var version = Math.Max(local.Version, server.Version) + 1;
			Put(at, new LoginRecord(local.ItemId, version, Seal(local.ItemId, version, merged.Summary), merged.Password, [.. merged.History], false), merged.Summary);
			SetBase(local.ItemId, new SyncBase(revision, server));
		}

		private void Adopt(int at, LoginRecord server, SummaryPayload theirs, long revision)
		{
			Put(at, server, theirs);
			SetBase(server.ItemId, new SyncBase(revision, null));
		}

		/// <summary>This PC's copy wins as it is, at a version past the server's so every PC orders it after.</summary>
		private void KeepMine(int at, LoginRecord local, SummaryPayload mine, LoginRecord server, SummaryPayload theirs, long revision)
		{
			var version = Math.Max(local.Version, server.Version) + 1;
			var kept = mine with { Generation = Math.Max(mine.Generation, theirs.Generation) };
			Put(at, local with { Version = version, Summary = Seal(local.ItemId, version, kept) }, kept);
			SetBase(local.ItemId, new SyncBase(revision, server));
		}

		/// <summary>
		/// The server purged the item (a revision-0 conflict, Ruling 18). A login edited here and not deleted is made again: the next
		/// push sends it as new, a generation on, so PCs holding its tombstone take it. Anything else becomes a tombstone here.
		/// </summary>
		private void Purged(string id)
		{
			var at = IndexOf(id);
			if (at < 0) return;
			var local = _records[at];
			var mine = Open(local);
			var unsent = !Bases.TryGetValue(id, out var b) || b.Original is not null;
			if (unsent && !mine.Tombstone && mine.DeletedAt is null)
			{
				var version = local.Version + 1;
				var again = mine with { Generation = mine.Generation + 1 };
				Put(at, local with { Version = version, Summary = Seal(id, version, again) }, again);
				Bases.Remove(id);
				return;
			}
			MakePurged(id);
		}

		/// <summary>The item becomes a tombstone here that the server holds nothing of, and nothing more is sent for it.</summary>
		public void MakePurged(string id)
		{
			var at = IndexOf(id);
			var local = _records[at];
			if (local.Tombstone)
			{
				SetBase(id, new SyncBase(0, null));
				return;
			}
			var mine = Open(local);
			var tombstone = TombstoneOf(_indexKey, _keys, local, mine, mine.DeletedAt ?? _store._clock.Now());
			Put(at, tombstone, Open(tombstone));
			SetBase(id, new SyncBase(0, null));
		}

		/// <summary>Upload this PC's copy: <see cref="TakeOverServer"/> for one server item.</summary>
		public void TakeOver(SyncItem item)
		{
			var server = SyncWire.ToRecord(item);
			var theirs = Open(server);
			var at = IndexOf(item.ItemId);
			if (at < 0)
			{
				Put(-1, server, theirs);
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			var local = _records[at];
			if (SameRecord(local, server))
			{
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			var mine = Open(local);
			var generation = Math.Max(mine.Generation, theirs.Generation);
			var version = local.Version > server.Version ? local.Version : server.Version + 1;
			if (version != local.Version || generation != mine.Generation)
			{
				var raised = mine with { Generation = generation };
				Put(at, local with { Version = version, Summary = Seal(local.ItemId, version, raised) }, raised);
			}
			Bases[item.ItemId] = new SyncBase(item.Revision, server);
			Changed = true;
		}

		private static bool SameContent(MergeInput a, MergeInput b) =>
			JsonSerializer.SerializeToUtf8Bytes(a.Summary, VaultJson.Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b.Summary, VaultJson.Options))
			&& a.Password.AsSpan().SequenceEqual(b.Password)
			&& a.History.Count == b.History.Count && a.History.Zip(b.History).All(h => h.First.AsSpan().SequenceEqual(h.Second));
	}
}
