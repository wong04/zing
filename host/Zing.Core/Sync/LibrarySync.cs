using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>
/// Library sync can't go on from what the server holds (an older copy than this PC has seen, or one held back). Bookmarks stay as they
/// are on this PC; turning sync off and on again on this PC starts library sync over from what the server holds.
/// </summary>
public sealed class LibrarySyncStoppedException(string message)
	: Exception($"{message} Bookmarks and the Reading List stopped syncing; turn sync off and on again on this PC to start them over.");

/// <summary>Bookmarks and the Reading List are too large for the sync server's 32 items of 256 KiB each.</summary>
public sealed class LibraryTooLargeException(string shard)
	: Exception($"The bookmarks and Reading List are too large to sync ({shard} is over the sync server's 256 KiB), so they stay on this PC.");

/// <summary>
/// Bookmarks and the Reading List, end to end encrypted like the logins (spec, Sync design) and in their own collection on the server,
/// which a v1 client never pulls. Every node is an entry under its stable ID (<see cref="LibraryTree"/>), kept in one of 32 items by a
/// hash of that ID, so a big library is a few requests and any edit rewrites one or two items. Each item is sealed under the library key
/// (HKDF of the index key, so no Windows Hello), padded, and bound to its ID and version; the library's manifest lists each item's
/// version, checked as the vault's is. Merging is node by node against the copy this PC last synced (its base), so a conflict on an
/// item only means another PC changed some node in it.
/// </summary>
/// <remarks>
/// The base is the server's whole library as of this PC's last sync: after one, every item on the server is what this PC pushed or
/// pulled. The library is replaced before the state file is written, so a crash between them leaves a base that merges to the same
/// tree again.
/// </remarks>
public sealed class LibrarySync : IDisposable
{
	private const string Collection = "library";
	private const int PushRounds = 5;

	private static readonly JsonSerializerOptions Json = new(SyncJson.Options) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

	private readonly VaultStore _vault;
	private readonly LibraryStore _library;
	private readonly SettingsStore _settings;
	private readonly IClock _clock;
	private readonly Action _unsubscribe;
	private bool _applying;

	/// <param name="settings">Where this PC's choice to sync the library is (syncLibrary).</param>
	public LibrarySync(VaultStore vault, LibraryStore library, SettingsStore settings, IClock clock)
	{
		_vault = vault;
		_library = library;
		_settings = settings;
		_clock = clock;
		_unsubscribe = _library.Subscribe(_ =>
		{
			if (!_applying) Edited?.Invoke();
		});
	}

	/// <summary>Whether this PC syncs its library (the syncLibrary setting).</summary>
	public bool On => _settings.Get("syncLibrary")?.GetValue<bool>() ?? throw new InvalidDataException("Setting syncLibrary is missing");

	public void Dispose() => _unsubscribe();

	/// <summary>The library changed here, not from a sync: the engine's debounce follows it.</summary>
	public event Action? Edited;

	/// <summary>
	/// One library sync as <paramref name="user"/>: check the manifest, pull, merge, push, write the manifest, and put the merged library
	/// in place. Answers true when the library changed here while it ran, so another sync should follow.
	/// </summary>
	/// <exception cref="SyncFeatureMissingException">The server is from before library sync; nothing was sent.</exception>
	public async Task<bool> SyncAsync(SyncClient client, string user, CancellationToken cancellation)
	{
		var vaultId = _vault.VaultId ?? throw new InvalidOperationException("The vault has no keys yet");
		var key = _vault.CollectionKey(Collection);
		try
		{
			return await RunAsync(client, user, vaultId, key, cancellation);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
		}
	}

	private async Task<bool> RunAsync(SyncClient client, string user, string vaultId, byte[] key, CancellationToken cancellation)
	{
		var state = LoadState(user, vaultId);

		var manifest = await client.GetLibraryManifestAsync(cancellation, asUser: user);
		Guard(() =>
		{
			RollbackGuard.CheckManifest(state.ManifestFloor, manifest?.Version);
			if (manifest is not null) RollbackGuard.CheckManifestAtFloor(state.ManifestFloor, state.ManifestHash, manifest);
		});
		var listed = manifest is null ? [] : SyncManifest.Open(key, vaultId, manifest.Version, manifest.Blob);

		var (shards, theirs, revision) = await PullAsync(client, user, vaultId, key, state, cancellation);
		Guard(() => RollbackGuard.CheckListed(listed, id => shards.TryGetValue(id, out var shard) ? shard.Version : null));

		var common = state.Entries;
		Dictionary<string, LibraryEntry> local = [];
		Dictionary<string, LibraryEntry> merged = [];
		var floor = Math.Max(state.RevisionFloor, revision);
		for (var round = 0; ; round++)
		{
			local = LibraryTree.Flatten(_library.Snapshot);
			merged = LibraryTree.Normalize(LibraryTree.Merge(common, LibraryTree.MatchNew(common, local, Join(theirs)), Join(theirs)), _clock.Now());
			var groups = Group(merged);
			var changed = groups.Keys.Union(theirs.Keys).Where(id => !LibraryTree.Same(groups.GetValueOrDefault(id, []), theirs.GetValueOrDefault(id, []))).Order(StringComparer.Ordinal).ToList();
			if (changed.Count == 0) break;
			if (round == PushRounds) throw new SyncKeepsConflictingException(changed.Count);

			var pushes = changed.Select(id => Seal(key, vaultId, id, shards.TryGetValue(id, out var held) ? held : null, groups.GetValueOrDefault(id, []))).ToList();
			foreach (var batch in SyncWire.Batches(pushes, p => p))
			{
				var pushed = await client.PostLibraryChangesAsync(batch, cancellation, asUser: user);
				SyncWire.CheckPushAnswer([.. batch.Select(p => p.ItemId)], pushed);
				Guard(() => RollbackGuard.CheckAccepted(floor, pushed.Accepted));
				foreach (var accepted in pushed.Accepted)
				{
					var push = batch.Single(p => p.ItemId == accepted.ItemId);
					shards[accepted.ItemId] = new ShardState(accepted.Revision, push.Version);
					theirs[accepted.ItemId] = groups.GetValueOrDefault(accepted.ItemId, []);
					floor = Math.Max(floor, accepted.Revision);
				}
				foreach (var conflict in pushed.Conflicts) Take(key, vaultId, state, shards, theirs, conflict);
			}
		}

		var listing = shards.Select(s => new SyncManifestEntry(s.Key, s.Value.Version)).OrderBy(e => e.ItemId, StringComparer.Ordinal).ToList();
		var manifestFloor = state.ManifestFloor;
		var manifestHash = state.ManifestHash;
		if (!listing.SequenceEqual(listed.OrderBy(e => e.ItemId, StringComparer.Ordinal)))
		{
			var baseVersion = manifest?.Version ?? 0;
			var blob = SyncManifest.Seal(key, vaultId, baseVersion + 1, listing);
			try
			{
				var version = await client.PutLibraryManifestAsync(blob, baseVersion, cancellation, asUser: user);
				if (version != baseVersion + 1) throw new SyncProtocolException($"The sync server stored the library manifest as version {version}, not {baseVersion + 1}");
				(manifestFloor, manifestHash) = (version, SHA256.HashData(blob));
			}
			catch (SyncConflictException)
			{
				// Another PC wrote one meanwhile, listing what it pushed; the next sync reads it and lists this PC's pushes too.
			}
		}
		else if (manifest is not null)
		{
			(manifestFloor, manifestHash) = (manifest.Version, SHA256.HashData(manifest.Blob));
		}

		var again = Apply(local, merged);
		SaveState(new LibrarySyncState(1, user, vaultId, revision, floor, manifestFloor, manifestHash, shards, merged));
		return again;
	}

	/// <summary>
	/// Puts the merged library in place. An edit made here while the sync ran is merged onto it (the library as the sync read it is their
	/// common copy), so it's kept and goes out with the next sync.
	/// </summary>
	private bool Apply(Dictionary<string, LibraryEntry> read, Dictionary<string, LibraryEntry> merged)
	{
		var now = LibraryTree.Flatten(_library.Snapshot);
		var editedMeanwhile = !LibraryTree.Same(now, read);
		var result = editedMeanwhile ? LibraryTree.Normalize(LibraryTree.Merge(read, now, merged), _clock.Now()) : merged;
		if (LibraryTree.Same(result, now)) return editedMeanwhile;
		_applying = true;
		try
		{
			_library.ReplaceFromSync(LibraryTree.Build(result));
		}
		finally
		{
			_applying = false;
		}
		return editedMeanwhile;
	}

	/// <summary>
	/// Every item changed since the cursor, opened and checked against the version this PC has seen of it. A cursor older than the
	/// server's tombstone horizon (410) reads everything from 0, and then only what that read sent is the server's.
	/// </summary>
	private async Task<(Dictionary<string, ShardState> Shards, Dictionary<string, Dictionary<string, LibraryEntry>> Theirs, long Revision)> PullAsync(
		SyncClient client, string user, string vaultId, byte[] key, LibrarySyncState state, CancellationToken cancellation)
	{
		var since = state.Cursor;
		var shards = new Dictionary<string, ShardState>(state.Shards, StringComparer.Ordinal);
		var theirs = since == 0 ? new Dictionary<string, Dictionary<string, LibraryEntry>>(StringComparer.Ordinal) : Group(state.Entries);
		while (true)
		{
			SyncChanges page;
			try
			{
				page = await client.GetLibraryChangesAsync(since, cancellation, asUser: user);
			}
			catch (SyncHttpException gone) when (gone.Status == HttpStatusCode.Gone && since != 0)
			{
				since = 0;
				shards.Clear();
				theirs.Clear();
				continue;
			}
			SyncWire.CheckPage(since, page);
			Guard(() => RollbackGuard.CheckRevision(state.RevisionFloor, page.Revision));
			foreach (var item in page.Items) Take(key, vaultId, state, shards, theirs, item);
			if (!page.More) return (shards, theirs, page.Revision);
			since = page.Items[^1].Revision;
		}
	}

	/// <summary>A server copy of an item, pulled or sent back as a conflict, after checking it isn't older than one this PC has seen.</summary>
	private static void Take(byte[] key, string vaultId, LibrarySyncState state, Dictionary<string, ShardState> shards, Dictionary<string, Dictionary<string, LibraryEntry>> theirs, SyncItem item)
	{
		if (!item.ItemId.StartsWith("library-", StringComparison.Ordinal)) throw new SyncProtocolException($"The sync server sent {item.ItemId} as a library item");
		var seen = state.Shards.TryGetValue(item.ItemId, out var known) ? known.Version : 0;
		if (item.Version < seen) throw new LibrarySyncStoppedException($"The sync server sent library item {item.ItemId} at version {item.Version}, but this PC has seen version {seen}.");
		if (item.Tombstone || item.Revision == 0)
		{
			shards.Remove(item.ItemId);
			theirs.Remove(item.ItemId);
			return;
		}
		shards[item.ItemId] = new ShardState(item.Revision, item.Version);
		theirs[item.ItemId] = Open(key, vaultId, item);
	}

	private static SyncPush Seal(byte[] key, string vaultId, string shard, ShardState? held, Dictionary<string, LibraryEntry> entries)
	{
		var version = (held?.Version ?? 0) + 1;
		var plain = JsonSerializer.SerializeToUtf8Bytes(new Shard(entries), Json);
		var sealedBytes = VaultCrypto.Seal(key, plain, new ItemAd(vaultId, shard, version, ItemPart.Summary));
		if (sealedBytes.Length > SyncWire.MaxBlobBytes) throw new LibraryTooLargeException(shard);
		return new SyncPush(shard, checked((int)version), held?.Revision ?? 0, false, sealedBytes, [], []);
	}

	private static Dictionary<string, LibraryEntry> Open(byte[] key, string vaultId, SyncItem item)
	{
		var plain = VaultCrypto.Open(key, item.Summary, new ItemAd(vaultId, item.ItemId, item.Version, ItemPart.Summary));
		var shard = JsonSerializer.Deserialize<Shard>(plain, Json) ?? throw new SyncProtocolException($"Library item {item.ItemId} is empty");
		if (shard.Entries.Keys.Any(id => LibraryTree.ShardOf(id) != item.ItemId)) throw new SyncProtocolException($"Library item {item.ItemId} holds a node that belongs in another item");
		return new Dictionary<string, LibraryEntry>(shard.Entries, StringComparer.Ordinal);
	}

	private static Dictionary<string, Dictionary<string, LibraryEntry>> Group(IReadOnlyDictionary<string, LibraryEntry> entries) =>
		entries.GroupBy(e => LibraryTree.ShardOf(e.Key), StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal), StringComparer.Ordinal);

	private static Dictionary<string, LibraryEntry> Join(Dictionary<string, Dictionary<string, LibraryEntry>> shards) =>
		shards.Values.SelectMany(s => s).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

	/// <summary>The vault's rollback checks, with library sync's own way out.</summary>
	private static void Guard(Action check)
	{
		try
		{
			check();
		}
		catch (ServerRolledBackException rolledBack)
		{
			throw new LibrarySyncStoppedException(rolledBack.Message.Split(" The sync server has gone back", 2)[0]);
		}
		catch (ServerWithheldItemException withheld)
		{
			throw new LibrarySyncStoppedException($"The sync server didn't send library item {withheld.ItemId}, which the library's own list says it holds.");
		}
	}

	private string StatePath => _vault.CollectionStateFile(VaultStore.LibrarySyncFileName);

	/// <summary>This PC's state for <paramref name="user"/> and this vault; a fresh one for anyone else, or before the first sync.</summary>
	private LibrarySyncState LoadState(string user, string vaultId)
	{
		var fresh = new LibrarySyncState(1, user, vaultId, 0, 0, 0, null, [], []);
		if (!File.Exists(StatePath)) return fresh;
		var state = JsonSerializer.Deserialize<LibrarySyncState>(File.ReadAllBytes(StatePath), Json) ?? throw new InvalidDataException($"{StatePath} is empty");
		if (state.Format != 1) throw new InvalidDataException($"{StatePath} has format {state.Format}, which this build doesn't read");
		return state.UserId == user && state.VaultId == vaultId
			? state with { Shards = new(state.Shards, StringComparer.Ordinal), Entries = new(state.Entries, StringComparer.Ordinal) }
			: fresh;
	}

	private void SaveState(LibrarySyncState state) => JsonFiles.WriteAllBytesAtomic(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, Json));

	private sealed record Shard(Dictionary<string, LibraryEntry> Entries);
}

/// <summary>The revision and version of one library item as this PC last saw it on the server.</summary>
public sealed record ShardState(long Revision, long Version);

/// <summary>
/// library-sync.json, beside the vault: the pull cursor, the rollback floors, each item's revision and version, and the base (the server's
/// whole library as of the last sync), for one server user and vault.
/// </summary>
public sealed record LibrarySyncState(
	int Format,
	string UserId,
	string VaultId,
	long Cursor,
	long RevisionFloor,
	long ManifestFloor,
	byte[]? ManifestHash,
	Dictionary<string, ShardState> Shards,
	Dictionary<string, LibraryEntry> Entries);
