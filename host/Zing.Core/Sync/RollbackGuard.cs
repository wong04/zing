using System.Security.Cryptography;

namespace Zing.Core.Sync;

/// <summary>
/// The sync server went back to an older state than this PC has seen (restored from a backup, or replaying old copies), so sync
/// stopped. Settings offers Upload this PC's copy (spec, Rollback protection).
/// </summary>
public sealed class ServerRolledBackException(string message)
	: Exception($"{message} The sync server has gone back to an older copy, so sync stopped. Upload this PC's copy to put this PC's logins back on it.");

/// <summary>The account manifest lists an item the server didn't send: the server is holding it back.</summary>
public sealed class ServerWithheldItemException(string itemId)
	: Exception($"The sync server didn't send login {itemId}, which the account's own list says it holds, so sync stopped. Upload this PC's copy to put this PC's logins back on it.")
{
	public string ItemId { get; } = itemId;
}

/// <summary>
/// How copies of one item are ordered: by generation, which goes up when a login purged meanwhile is made again (Ruling 18), then by
/// version, which goes up with every accepted change.
/// </summary>
public readonly record struct ItemOrder(long Generation, long Version) : IComparable<ItemOrder>
{
	public int CompareTo(ItemOrder other) => Generation != other.Generation ? Generation.CompareTo(other.Generation) : Version.CompareTo(other.Version);

	public static bool operator <(ItemOrder a, ItemOrder b) => a.CompareTo(b) < 0;

	public static bool operator >(ItemOrder a, ItemOrder b) => a.CompareTo(b) > 0;

	public static bool operator <=(ItemOrder a, ItemOrder b) => a.CompareTo(b) <= 0;

	public static bool operator >=(ItemOrder a, ItemOrder b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// The rollback checks (spec, Rollback protection), against the floors this PC keeps: the highest server revision and manifest
/// version it has seen (sync.json), and for each item the server copy it last synced (its sync base in vault.zing). Each check runs
/// before anything it guards is stored, and raises rather than going on.
/// </summary>
public static class RollbackGuard
{
	/// <exception cref="ServerRolledBackException">The server reports a lower revision than this PC has seen.</exception>
	public static void CheckRevision(long floor, long served)
	{
		if (served < floor) throw new ServerRolledBackException($"The sync server reports revision {served}, but this PC has seen revision {floor}.");
	}

	/// <summary>Every write the server takes gets a revision above every one before it.</summary>
	/// <exception cref="ServerRolledBackException">A write was given a revision this PC has already seen.</exception>
	public static void CheckAccepted(long floor, IEnumerable<SyncAccepted> accepted)
	{
		foreach (var item in accepted)
		{
			if (item.Revision <= floor) throw new ServerRolledBackException($"The sync server stored login {item.ItemId} at revision {item.Revision}, but this PC has seen revision {floor}.");
		}
	}

	/// <param name="served">The manifest version the server holds; null when it holds none.</param>
	/// <exception cref="ServerRolledBackException">An older manifest than this PC has seen, or none after one was seen.</exception>
	public static void CheckManifest(long floor, long? served)
	{
		if (served is null && floor > 0) throw new ServerRolledBackException($"The sync server has no account manifest, but this PC has seen version {floor}.");
		if (served < floor) throw new ServerRolledBackException($"The sync server's account manifest is version {served}, but this PC has seen version {floor}.");
	}

	/// <summary>
	/// O2 (Ruling 58): a manifest at the version this PC already holds must be the same blob; another one at that version is a
	/// forked manifest history, such as one written over a restored server by a PC that never saw this PC's.
	/// </summary>
	/// <param name="floorHash">The SHA-256 of the manifest blob at <paramref name="floor"/>; null when this PC doesn't know it.</param>
	/// <exception cref="ServerRolledBackException">Another manifest at the floor's version.</exception>
	public static void CheckManifestAtFloor(long floor, byte[]? floorHash, VersionedBlob served)
	{
		if (served.Version != floor || floorHash is null) return;
		if (CryptographicOperations.FixedTimeEquals(floorHash, SHA256.HashData(served.Blob))) return;
		throw new ServerRolledBackException($"The sync server's account manifest version {floor} isn't the one this PC has seen.");
	}

	/// <param name="floor">The copy this PC last synced (or holds as a tombstone); null when it never had one from the server.</param>
	/// <exception cref="ServerRolledBackException">The server sent an older copy of the item than this PC has seen.</exception>
	public static void CheckItem(string itemId, ItemOrder served, ItemOrder? floor)
	{
		if (floor is { } seen && served < seen)
			throw new ServerRolledBackException($"The sync server sent login {itemId} at version {served.Version}, but this PC has seen version {seen.Version}.");
	}

	/// <param name="known">The version of each item this PC now has from the server; null for one it doesn't have.</param>
	/// <exception cref="ServerWithheldItemException">The manifest lists an item, or a version of it, that the server didn't send.</exception>
	public static void CheckListed(IEnumerable<SyncManifestEntry> listed, Func<string, long?> known)
	{
		foreach (var entry in listed)
		{
			if (known(entry.ItemId) is not { } version || version < entry.Version) throw new ServerWithheldItemException(entry.ItemId);
		}
	}
}
