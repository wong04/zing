using System.Security.Cryptography;
using System.Text.Json;
using Zing.Core.Sync;

namespace Zing.Core.Vault;

/// <summary>
/// The server's copy of an item as this PC last synced it. <see cref="Original"/> is null while the item still is that copy; a
/// change made here keeps the copy it replaced, so the item is unsent and a conflict merges against it.
/// </summary>
/// <param name="Revision">The server revision of that copy; 0 for a login the server purged, which this PC keeps as a tombstone.</param>
/// <param name="Listed">
/// A manifest this PC read or wrote listed the item (Ruling 56): only then may a full resync that doesn't serve it take it as purged.
/// </param>
public sealed record SyncBase(long Revision, LoginRecord? Original, bool Listed = false);

/// <summary>
/// Where this PC's next pull starts, and the rollback floors: the highest server revision and manifest version it has seen, and the
/// SHA-256 of that manifest's blob (Ruling 58), so another manifest at the same version is caught; null until one is seen.
/// </summary>
public sealed record SyncProgress(string UserId, long Cursor, long RevisionFloor, long ManifestFloor, byte[]? ManifestHash = null)
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

	/// <summary>
	/// Logins (ones in Recently deleted included) not yet on the server as they are here: Settings' "not uploaded yet". "Never" marks
	/// and tombstones don't count: neither is a login the person sees.
	/// </summary>
	public int UnsentLogins
	{
		get
		{
			lock (_gate) return _records.Count(r => !r.Tombstone && IsUnsent(r) && !(_summaries.TryGetValue(r.ItemId, out var s) && s.NeverSave));
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
				var listed = _bases.TryGetValue(item.ItemId, out var held) && held.Listed;
				bases[item.ItemId] = new SyncBase(item.Revision, current is not null && SameRecord(current, record) ? null : record, listed);
				versions[item.ItemId] = record.Version;
			}
			var progress = ProgressFor(userId);
			progress = progress with { RevisionFloor = Math.Max(progress.RevisionFloor, accepted.Max(a => a.Revision)) };
			List<QueuedChange> left = [.. _queued.Where(c => !versions.TryGetValue(c.ItemId, out var version) || c.Version > version)];
			// The floor first (M4): a crash between the two writes leaves the floor above the bases, which is safe, since the next push
			// finds the items already uploaded. Bases above the floor would let a server restored into the gap pass unseen.
			WriteSyncFile(left, _syncAccount, _syncSignedOut, progress);
			_progress = progress;
			_queued.Clear();
			_queued.AddRange(left);
			WriteVaultFile([.. _records], bases, userId);
		}
	}

	/// <summary>
	/// Stores a pulled page of server copies. Each is checked first (its tag, Ruling 25, and this PC's floor for it), and nothing of
	/// the page is stored if one fails. A copy of an item unchanged here replaces it; one changed here too is merged
	/// (<see cref="Merge.Items"/>) and stays unsent. The items and their bases are written in one replace, after the floor and before the cursor.
	/// </summary>
	/// <param name="cursor">Where the next pull starts once these are stored; null leaves it.</param>
	/// <param name="serverRevision">The revision the server reported with them, for the floor.</param>
	/// <exception cref="SyncProtocolException">A purged placeholder (revision 0), which a pull never sends (Ruling 56).</exception>
	/// <exception cref="ServerRolledBackException">A copy older than one this PC has seen.</exception>
	/// <exception cref="VaultTamperedException">A copy that fails its tag, or whose tombstone flag its sealed summary doesn't back.</exception>
	public void ApplyFromServer(string userId, IReadOnlyList<SyncItem> items, long? cursor, long serverRevision)
	{
		RequireNoPlaceholder(items);
		ApplyStaged(userId, stage =>
		{
			foreach (var item in items) stage.Apply(item);
		}, cursor, serverRevision);
	}

	/// <summary>
	/// Stores the server copies a push conflicted with. Each must be for an item of <paramref name="sent"/>, at a revision past the one
	/// it was sent over; a purged placeholder (revision 0) only for one sent over a revision above 0, Ruling 18's only case (Ruling 56).
	/// Each is then applied as a pulled copy is, and a placeholder makes a login edited here again, or a tombstone.
	/// </summary>
	/// <exception cref="SyncProtocolException">A conflict for an item that wasn't sent, or at a revision that can't be one.</exception>
	public void ApplyConflicts(string userId, IReadOnlyList<SyncUnsent> sent, IReadOnlyList<SyncItem> conflicts)
	{
		if (conflicts.Count == 0) return;
		var sentOver = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (var unsent in sent) sentOver[unsent.Record.ItemId] = unsent.BaseRevision;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var conflict in conflicts)
		{
			if (!sentOver.TryGetValue(conflict.ItemId, out var baseRevision)) throw new SyncProtocolException($"The sync server sent a conflict for item {conflict.ItemId}, which wasn't sent");
			if (!seen.Add(conflict.ItemId)) throw new SyncProtocolException($"The sync server sent two conflicts for item {conflict.ItemId}");
			if (conflict.Revision == 0 && baseRevision == 0) throw new SyncProtocolException($"The sync server said item {conflict.ItemId} was purged, but it was sent as new");
			if (conflict.Revision != 0 && conflict.Revision <= baseRevision) throw new SyncProtocolException($"The sync server sent a conflict for item {conflict.ItemId} at revision {conflict.Revision}, not past {baseRevision}");
		}
		ApplyStaged(userId, stage =>
		{
			foreach (var conflict in conflicts)
			{
				if (conflict.Revision == 0) stage.Purged(conflict.ItemId);
				else stage.Apply(conflict);
			}
		}, null, null);
	}

	private static void RequireNoPlaceholder(IReadOnlyList<SyncItem> pulled)
	{
		var placeholder = pulled.FirstOrDefault(i => i.Revision <= 0);
		if (placeholder is not null) throw new SyncProtocolException($"The sync server sent item {placeholder.ItemId} at revision {placeholder.Revision} in a pull");
	}

	/// <summary>
	/// Runs <paramref name="apply"/> on a stage, then writes the raised floor, then the items and bases if any changed, then the cursor
	/// (final review I1). The floor goes first for the reason <see cref="MarkPushed"/> gives; the cursor goes last, so a crash or a
	/// failed write of vault.zing leaves it before the page, which the next pull serves again and finds the same as stored, rather
	/// than past items this PC never stored, which would read as a server holding them back.
	/// </summary>
	private void ApplyStaged(string userId, Action<Stage> apply, long? cursor, long? serverRevision)
	{
		bool changed;
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			using var stage = new Stage(this);
			apply(stage);
			changed = stage.Changed;
			var progress = ProgressFor(userId);
			var floored = progress with { RevisionFloor = Math.Max(progress.RevisionFloor, serverRevision ?? 0) };
			if (floored != progress || _progress is null) WriteProgress(floored);
			if (changed) stage.Write(userId);
			if (cursor is { } next && next != floored.Cursor) WriteProgress(floored with { Cursor = next });
		}
		if (changed) Changed?.Invoke();
	}

	/// <summary>Under <c>_gate</c>: on disk first, then in memory, so a failed write leaves both as they were.</summary>
	private void WriteProgress(SyncProgress progress)
	{
		WriteSyncFile(_queued, _syncAccount, _syncSignedOut, progress);
		_progress = progress;
	}

	/// <summary>
	/// After a full resync: an item this PC synced that the server no longer holds was purged as a tombstone (spec: a PC past the
	/// horizon). One unchanged here becomes a tombstone here too, so it can't come back; one changed here is left unsent, and its
	/// push meets the purge (Ruling 18). A live login counts as purged only if a manifest once listed it and the one read now (checked
	/// first) doesn't, since a login leaves the manifest only once its tombstone is on the server (Ruling 56).
	/// </summary>
	/// <exception cref="ServerWithheldItemException">A live login this PC synced that the resync didn't serve, and no manifest listed yet.</exception>
	public void MarkPurged(string userId, IReadOnlyCollection<string> served)
	{
		var held = served as IReadOnlySet<string> ?? served.ToHashSet(StringComparer.Ordinal);
		bool changed;
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			var gone = _records.Where(r => _bases.TryGetValue(r.ItemId, out var b) && b.Revision > 0 && b.Original is null && !held.Contains(r.ItemId)).ToList();
			var unlisted = gone.FirstOrDefault(r => !r.Tombstone && !_bases[r.ItemId].Listed);
			if (unlisted is not null) throw new ServerWithheldItemException(unlisted.ItemId);
			using var stage = new Stage(this);
			foreach (var record in gone) stage.MakePurged(record.ItemId);
			changed = stage.Changed;
			if (changed) stage.Write(userId);
		}
		if (changed) Changed?.Invoke();
	}

	/// <summary>A manifest listed these (read and checked, or written): from now on, a full resync that doesn't serve one may take it as purged.</summary>
	public void MarkListed(string userId, IReadOnlyList<SyncManifestEntry> entries)
	{
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			Dictionary<string, SyncBase>? bases = null;
			foreach (var entry in entries)
			{
				if (!_bases.TryGetValue(entry.ItemId, out var b) || b.Revision == 0 || b.Listed) continue;
				bases ??= new Dictionary<string, SyncBase>(_bases, StringComparer.Ordinal);
				bases[entry.ItemId] = b with { Listed = true };
			}
			if (bases is null) return;
			WriteVaultFile([.. _records], bases, userId);
		}
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

	/// <summary>
	/// Queued hints for items the server now holds as they are here. Nothing is decided from the queue any more (the bases are the
	/// truth). sync.json keeps it because its format 1 has it and it records each edit as the edit is written, which the edit paths'
	/// tests observe; it is pruned here so it doesn't grow.
	/// </summary>
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

	/// <summary>
	/// Raises the manifest floor to <paramref name="manifest"/>, remembering the hash of its <paramref name="blob"/> (read or written);
	/// floors never go down. At the floor already, only a hash not yet known is filled in.
	/// </summary>
	public void RaiseManifestFloor(string userId, long manifest, byte[] blob)
	{
		lock (_gate)
		{
			RequireOpen();
			var progress = ProgressFor(userId);
			if (progress.ManifestFloor > manifest && _progress is not null) return;
			if (progress.ManifestFloor == manifest && progress.ManifestHash is not null && _progress is not null) return;
			var next = progress with { ManifestFloor = manifest, ManifestHash = SHA256.HashData(blob) };
			WriteSyncFile(_queued, _syncAccount, _syncSignedOut, next);
			_progress = next;
		}
	}

	/// <summary>
	/// After a delta pull (Ruling 58): a live login this PC synced, hasn't changed since, and saw listed, that the manifest just read
	/// no longer lists. A login leaves the manifest only once its tombstone is on the server, and the manifest is read before the
	/// pull, so an honest pull has brought that tombstone. Not one means writes the server lost (a restore) or a delete held back.
	/// </summary>
	/// <exception cref="ServerWithheldItemException">The first such login.</exception>
	public void RequireStillListed(string userId, IReadOnlyList<SyncManifestEntry> listed)
	{
		var ids = listed.Select(e => e.ItemId).ToHashSet(StringComparer.Ordinal);
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			var dropped = _records.FirstOrDefault(r => !r.Tombstone && _bases.TryGetValue(r.ItemId, out var b) && b.Revision > 0 && b.Original is null && b.Listed && !ids.Contains(r.ItemId));
			if (dropped is not null) throw new ServerWithheldItemException(dropped.ItemId);
		}
	}

	/// <summary>
	/// Upload this PC's copy (spec, Rollback protection; Ruling 56), after the server went back: <paramref name="server"/> is everything
	/// it holds now, read without the floors. Each item both hold is merged as a conflict is, against this PC's base copy when it has
	/// one, so no password is lost and an older backup copy loses on <c>updatedAt</c>; a live server copy beats a tombstone here only
	/// if it changed after the delete. An item only the server holds is taken as it is, and a login only this PC holds goes as new; a
	/// tombstone only this PC holds doesn't go (the server has nothing of it). Only summaries are sealed again, so no Hello. The floors
	/// restart from what the server reports now.
	/// </summary>
	/// <exception cref="SyncProtocolException">A purged placeholder (revision 0), which a pull never sends.</exception>
	public void TakeOverServer(string userId, IReadOnlyList<SyncItem> server, long serverRevision)
	{
		RequireNoPlaceholder(server);
		lock (_gate)
		{
			RequireOpen();
			RequireSyncUser(userId);
			using var stage = new Stage(this);
			var onServer = new HashSet<string>(StringComparer.Ordinal);
			foreach (var item in server)
			{
				onServer.Add(item.ItemId);
				stage.TakeOver(item);
			}
			foreach (var record in _records.Where(r => !onServer.Contains(r.ItemId)))
			{
				if (record.Tombstone) stage.Bases[record.ItemId] = new SyncBase(0, null);
				else stage.Bases.Remove(record.ItemId);
			}
			var progress = new SyncProgress(userId, serverRevision, serverRevision, 0);
			WriteSyncFile(_queued, _syncAccount, _syncSignedOut, progress);
			_progress = progress;
			stage.Write(userId);
		}
		Changed?.Invoke();
	}

	/// <summary>The same item at the same version, byte for byte: one this PC holds and a copy of it, here or on the server.</summary>
	internal static bool SameRecord(LoginRecord a, LoginRecord b) =>
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

		/// <summary>The item's new base; one still on the server keeps whether a manifest listed it.</summary>
		private void SetBase(string id, SyncBase value)
		{
			var held = Bases.GetValueOrDefault(id);
			if (value.Revision > 0 && held is { Listed: true }) value = value with { Listed = true };
			if (held is not null && held.Revision == value.Revision && ReferenceEquals(held.Original, value.Original) && held.Listed == value.Listed) return;
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

		/// <summary>One server copy at a revision above 0: pulled, or a push's conflict.</summary>
		public void Apply(SyncItem item)
		{
			if (item.Revision <= 0) throw new SyncProtocolException($"The sync server sent item {item.ItemId} at revision {item.Revision}");
			var server = SyncWire.ToRecord(item);
			var theirs = Open(server);
			var at = IndexOf(item.ItemId);
			var local = at < 0 ? null : _records[at];
			Bases.TryGetValue(item.ItemId, out var b);
			var floor = b is not null ? b.Original ?? local : local is { Tombstone: true } ? local : null;
			if (floor is not null)
			{
				var seen = Order(floor, Open(floor));
				RollbackGuard.CheckItem(item.ItemId, Order(server, theirs), seen);
				// M3: the same version with other bytes is a copy the server refused (another PC's), served in place of the one it took.
				if (Order(server, theirs) == seen && !SameRecord(floor, server)) throw new ServerRolledBackException($"The sync server sent another copy of login {item.ItemId} at the version this PC has.");
			}

			if (local is null)
			{
				Put(-1, server, theirs);
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			if (SameRecord(local, server))
			{
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			if (b is { Original: null } && Descends(local, server, theirs))
			{
				Put(at, server, theirs);
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			// Changed here too, or a newer copy that dropped this PC's password: one written over a copy the server lost (a backup
			// restore, then another PC's Upload this PC's copy). Merged with nothing in common, so no password is lost.
			Conflict(at, local, b?.Original, server, theirs, item.Revision);
		}

		/// <summary>
		/// The server copy follows from this PC's: every change keeps the password it replaces (as it is, in the history), so a live
		/// copy that holds neither this PC's password nor that in its history was written without it. A tombstone drops passwords by design.
		/// </summary>
		private static bool Descends(LoginRecord local, LoginRecord server, SummaryPayload theirs)
		{
			if (theirs.Tombstone || local.Tombstone || local.Password.Length == 0) return true;
			return local.Password.AsSpan().SequenceEqual(server.Password) || server.History.Any(h => h.AsSpan().SequenceEqual(local.Password));
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
		public void Purged(string id)
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

		/// <summary>Upload this PC's copy: <see cref="TakeOverServer"/> for one server item, with no floor (the server went back).</summary>
		public void TakeOver(SyncItem item)
		{
			var server = SyncWire.ToRecord(item);
			var theirs = Open(server);
			var at = IndexOf(item.ItemId);
			if (at < 0)
			{
				Adopt(-1, server, theirs, item.Revision);
				return;
			}
			var local = _records[at];
			if (SameRecord(local, server))
			{
				SetBase(item.ItemId, new SyncBase(item.Revision, null));
				return;
			}
			var mine = Open(local);
			if (mine.Tombstone && !theirs.Tombstone)
			{
				// After a rollback the server's copy can be older than this PC's delete: it wins only if it changed after.
				if (theirs.UpdatedAt > mine.DeletedAt) Adopt(at, server, theirs, item.Revision);
				else KeepMine(at, local, mine, server, theirs, item.Revision);
				return;
			}
			Conflict(at, local, Bases.GetValueOrDefault(item.ItemId)?.Original, server, theirs, item.Revision);
		}

		private static bool SameContent(MergeInput a, MergeInput b) =>
			JsonSerializer.SerializeToUtf8Bytes(a.Summary, VaultJson.Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b.Summary, VaultJson.Options))
			&& a.Password.AsSpan().SequenceEqual(b.Password)
			&& a.History.Count == b.History.Count && a.History.Zip(b.History).All(h => h.First.AsSpan().SequenceEqual(h.Second));
	}
}
