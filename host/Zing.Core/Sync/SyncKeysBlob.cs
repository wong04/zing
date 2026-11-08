using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>Wrapped keys from the server (or this PC's copy of them) that aren't in the format this Zing writes.</summary>
public sealed class SyncKeysFormatException(string message) : Exception(message);

/// <summary>
/// The wrapped keys as the server stores them (PUT /keys), opaque to it: the vault's ID, the Argon2id parameters, the index and
/// secret keys wrapped by the password key and again by the recovery key, and the account profile sealed under the index key.
/// Every wrap's associated data names the wrapping key, the key wrapped, the vault ID and the serialized KDF parameters (the
/// carry-over from Task 2), so the server can't lower the parameters, swap a wrap for another, or move the keys to another vault.
/// </summary>
public sealed partial class SyncKeysBlob
{
	public const int CurrentFormat = 1;

	private const string ByPasswordKey = "password";
	private const string ByRecoveryKey = "recovery";
	private static readonly byte[] ProfileTag = "zing-profile-v1"u8.ToArray();

	public string VaultId { get; }
	public KdfParams Kdf { get; }

	private readonly WrappedKeys _byPassword;
	private readonly WrappedKeys _byRecovery;
	private readonly byte[] _profile;

	private SyncKeysBlob(string vaultId, KdfParams kdf, WrappedKeys byPassword, WrappedKeys byRecovery, byte[] profile)
	{
		VaultId = vaultId;
		Kdf = kdf;
		_byPassword = byPassword;
		_byRecovery = byRecovery;
		_profile = profile;
	}

	/// <summary>New KDF parameters (the floor, a new salt), both keys wrapped twice, and the profile sealed.</summary>
	public static SyncKeysBlob Create(VaultKeyMaterial keys, string password, string recoveryCode, AccountProfile profile)
	{
		var kdf = KdfParams.Generate();
		var byPassword = WrapWithPassword(keys, kdf, password);
		var recoveryKey = RecoveryKey.ToKey(recoveryCode);
		try
		{
			return new SyncKeysBlob(keys.VaultId, kdf, byPassword, Wrap(recoveryKey, keys, ByRecoveryKey, kdf), SealProfile(keys, profile));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(recoveryKey);
		}
	}

	/// <summary>
	/// The same keys wrapped by a new password. The KDF parameters stay, salt included: the recovery wraps are bound to them, and
	/// re-wrapping those would need the recovery key, which a PC restored with the password doesn't have.
	/// </summary>
	public SyncKeysBlob WithPassword(VaultKeyMaterial keys, string password)
	{
		if (keys.VaultId != VaultId) throw new SyncKeysFormatException($"These wrapped keys are vault {VaultId}'s, not this vault's ({keys.VaultId})");
		return new SyncKeysBlob(VaultId, Kdf, WrapWithPassword(keys, Kdf, password), _byRecovery, _profile);
	}

	/// <summary>Argon2id on the password, then both keys. The caller disposes what comes back.</summary>
	/// <exception cref="VaultTamperedException">A wrong password, or wraps or parameters that were changed.</exception>
	public VaultKeyMaterial UnwrapWithPassword(string password)
	{
		var passwordKey = PasswordKey.Derive(password, Kdf);
		try
		{
			return Unwrap(passwordKey, _byPassword, ByPasswordKey);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(passwordKey);
		}
	}

	/// <exception cref="RecoveryKeyFormatException">The code isn't a recovery key.</exception>
	/// <exception cref="VaultTamperedException">Another recovery key, or wraps or parameters that were changed.</exception>
	public VaultKeyMaterial UnwrapWithRecoveryKey(string code)
	{
		var recoveryKey = RecoveryKey.ToKey(code);
		try
		{
			return Unwrap(recoveryKey, _byRecovery, ByRecoveryKey);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(recoveryKey);
		}
	}

	/// <summary>The account profile, which restoring on a new PC makes the local account from.</summary>
	public AccountProfile OpenProfile(byte[] indexKey)
	{
		var padded = VaultCrypto.Decrypt(indexKey, _profile, ProfileAd(VaultId));
		try
		{
			var plain = Padding.Unpad(padded);
			var profile = JsonSerializer.Deserialize<AccountProfile>(plain, SyncJson.Options);
			if (profile is null || profile.Username is null || profile.DisplayName is null || profile.Email is null) throw new SyncKeysFormatException("The sealed account profile is missing a field");
			return profile;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(padded);
		}
	}

	public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(new Stored(CurrentFormat, VaultId, Kdf, _byPassword, _byRecovery, _profile), SyncJson.Options);

	/// <exception cref="SyncKeysFormatException">Not wrapped keys this Zing reads.</exception>
	/// <exception cref="KdfParamsRejectedException">Parameters below the floor or above the caps, checked before anything is derived.</exception>
	public static SyncKeysBlob Parse(byte[] blob)
	{
		Stored? stored;
		try
		{
			stored = JsonSerializer.Deserialize<Stored>(blob, SyncJson.Options);
		}
		catch (JsonException error)
		{
			throw new SyncKeysFormatException($"The wrapped keys aren't readable: {error.Message}");
		}
		if (stored is null) throw new SyncKeysFormatException("The wrapped keys are empty");
		if (stored.Format != CurrentFormat) throw new SyncKeysFormatException($"The wrapped keys are format {stored.Format}; this Zing reads {CurrentFormat}");
		if (stored.VaultId is null || !VaultIdPattern().IsMatch(stored.VaultId)) throw new SyncKeysFormatException("The wrapped keys have no vault ID, or not one Zing makes");
		var kdf = stored.Kdf ?? throw new SyncKeysFormatException("The wrapped keys have no KDF parameters");
		kdf.EnforceFloor();
		if (!Complete(stored.ByPassword) || !Complete(stored.ByRecoveryKey) || stored.Profile is not { Length: > 0 }) throw new SyncKeysFormatException("The wrapped keys are missing a wrap or the profile");
		return new SyncKeysBlob(stored.VaultId, kdf, stored.ByPassword!, stored.ByRecoveryKey!, stored.Profile);
	}

	private static bool Complete(WrappedKeys? wrapped) => wrapped is { IndexKey.Length: > 0, SecretKey.Length: > 0 };

	private static WrappedKeys WrapWithPassword(VaultKeyMaterial keys, KdfParams kdf, string password)
	{
		var passwordKey = PasswordKey.Derive(password, kdf);
		try
		{
			return Wrap(passwordKey, keys, ByPasswordKey, kdf);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(passwordKey);
		}
	}

	private static WrappedKeys Wrap(byte[] wrappingKey, VaultKeyMaterial keys, string by, KdfParams kdf) => new(
		KeyWrap.Wrap(wrappingKey, keys.IndexKey, Label(by, "index", keys.VaultId, kdf)),
		KeyWrap.Wrap(wrappingKey, keys.SecretKey, Label(by, "secret", keys.VaultId, kdf)));

	private VaultKeyMaterial Unwrap(byte[] wrappingKey, WrappedKeys wrapped, string by)
	{
		var indexKey = KeyWrap.Unwrap(wrappingKey, wrapped.IndexKey, Label(by, "index", VaultId, Kdf));
		try
		{
			return new VaultKeyMaterial(VaultId, indexKey, KeyWrap.Unwrap(wrappingKey, wrapped.SecretKey, Label(by, "secret", VaultId, Kdf)));
		}
		catch
		{
			CryptographicOperations.ZeroMemory(indexKey);
			throw;
		}
	}

	/// <summary>The wrap's label: which key wraps, which is wrapped, the vault, and the KDF parameters exactly as they're stored.</summary>
	private static string Label(string by, string wrapped, string vaultId, KdfParams kdf) =>
		string.Create(CultureInfo.InvariantCulture, $"zing-sync-keys-v1/{by}/{wrapped}/{vaultId}/argon2id m={kdf.MemoryKiB} t={kdf.Iterations} p={kdf.Parallelism} salt={Convert.ToBase64String(kdf.Salt)}");

	private static byte[] SealProfile(VaultKeyMaterial keys, AccountProfile profile)
	{
		var padded = Padding.Pad(JsonSerializer.SerializeToUtf8Bytes(profile, SyncJson.Options));
		try
		{
			return VaultCrypto.Encrypt(keys.IndexKey, padded, ProfileAd(keys.VaultId));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(padded);
		}
	}

	private static byte[] ProfileAd(string vaultId) => AdEncoding.Strings(ProfileTag, Encoding.UTF8.GetBytes(vaultId));

	[GeneratedRegex("^[0-9a-f]{32}$")]
	private static partial Regex VaultIdPattern();

	private sealed record WrappedKeys(byte[] IndexKey, byte[] SecretKey);

	private sealed record Stored(int Format, string? VaultId, KdfParams? Kdf, WrappedKeys? ByPassword, WrappedKeys? ByRecoveryKey, byte[]? Profile);
}

public sealed record SyncManifestEntry(string ItemId, long Version);

/// <summary>
/// The account manifest (PUT /manifest): every item ID and its version, sealed under the index key. Its associated data names
/// the vault and the manifest version it was written as, so the server can't serve an older manifest as the current one.
/// </summary>
public static class SyncManifest
{
	/// <summary>The most the server stores in one blob (server SyncLimits.MaxBlobBytes).</summary>
	public const int MaxBytes = 256 * 1024;

	private static readonly byte[] Tag = "zing-manifest-v1"u8.ToArray();

	public static byte[] Seal(byte[] indexKey, string vaultId, long version, IEnumerable<SyncManifestEntry> items)
	{
		var padded = Padding.Pad(JsonSerializer.SerializeToUtf8Bytes(new Stored([.. items]), SyncJson.Options));
		return VaultCrypto.Encrypt(indexKey, padded, Ad(vaultId, version));
	}

	/// <exception cref="VaultTamperedException">Another vault's manifest, another version's, or one that was changed.</exception>
	public static IReadOnlyList<SyncManifestEntry> Open(byte[] indexKey, string vaultId, long version, byte[] blob)
	{
		var plain = Padding.Unpad(VaultCrypto.Decrypt(indexKey, blob, Ad(vaultId, version)));
		var stored = JsonSerializer.Deserialize<Stored>(plain, SyncJson.Options);
		if (stored?.Items is null || stored.Items.Any(i => i is null || string.IsNullOrEmpty(i.ItemId))) throw new SyncKeysFormatException("The manifest has no item list, or an item without an ID");
		return stored.Items;
	}

	private static byte[] Ad(string vaultId, long version) =>
		AdEncoding.Strings(Tag, Encoding.UTF8.GetBytes(vaultId), Encoding.UTF8.GetBytes(version.ToString(CultureInfo.InvariantCulture)));

	private sealed record Stored(IReadOnlyList<SyncManifestEntry> Items);
}

/// <summary>The recovery key as keys.bin keeps it: sealed under the secret key, so showing it again needs Windows Hello.</summary>
public static class RecoverySeal
{
	private static readonly byte[] Tag = "zing-recovery-seal-v1"u8.ToArray();

	public static byte[] Seal(byte[] secretKey, string vaultId, string code)
	{
		var padded = Padding.Pad(Encoding.UTF8.GetBytes(code));
		try
		{
			return VaultCrypto.Encrypt(secretKey, padded, Ad(vaultId));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(padded);
		}
	}

	public static string Open(byte[] secretKey, string vaultId, byte[] sealedBytes)
	{
		var padded = VaultCrypto.Decrypt(secretKey, sealedBytes, Ad(vaultId));
		try
		{
			return Encoding.UTF8.GetString(Padding.Unpad(padded));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(padded);
		}
	}

	private static byte[] Ad(string vaultId) => AdEncoding.Strings(Tag, Encoding.UTF8.GetBytes(vaultId));
}

/// <summary>How a stored login travels to the server: its ciphertexts as they are, the history as one blob.</summary>
public static class SyncWire
{
	/// <summary>An item the server hasn't seen: based on revision 0.</summary>
	public static SyncPush NewItem(LoginRecord record) =>
		new(record.ItemId, checked((int)record.Version), 0, record.Tombstone, record.Summary, record.Password, JoinHistory(record.History));

	/// <summary>An item changed here, over the server copy it was based on.</summary>
	public static SyncPush Push(SyncUnsent unsent) =>
		new(unsent.Record.ItemId, checked((int)unsent.Record.Version), unsent.BaseRevision, unsent.Record.Tombstone, unsent.Record.Summary, unsent.Record.Password, JoinHistory(unsent.Record.History));

	/// <summary>
	/// A pulled page as a pull can use it (Ruling 56): its items strictly after <paramref name="since"/>, in rising revision order, none
	/// past the page's own revision; and a page that says there is more holds at least one, so the cursor always moves on.
	/// </summary>
	/// <exception cref="SyncProtocolException">The page breaks one of those.</exception>
	public static void CheckPage(long since, SyncChanges page)
	{
		var last = since;
		foreach (var item in page.Items)
		{
			if (item.Revision <= last || item.Revision > page.Revision) throw new SyncProtocolException($"The sync server sent item {item.ItemId} at revision {item.Revision} in a page after {last} that ends at {page.Revision}");
			last = item.Revision;
		}
		if (page.More && page.Items.Count == 0) throw new SyncProtocolException("The sync server said there was more to pull but sent nothing");
	}

	/// <summary>Each old password's ciphertext as a big-endian uint32 length then its bytes, newest first.</summary>
	public static byte[] JoinHistory(IReadOnlyList<byte[]> history) => AdEncoding.Strings([.. history]);

	/// <summary>A server item as the vault stores it. Its ciphertexts are checked when opened, not here.</summary>
	/// <exception cref="SyncProtocolException">The history isn't a list of length-prefixed ciphertexts.</exception>
	public static LoginRecord ToRecord(SyncItem item) =>
		new(item.ItemId, item.Version, item.Summary, item.Password, SplitHistory(item.ItemId, item.History), item.Tombstone);

	private static byte[][] SplitHistory(string itemId, byte[] joined)
	{
		var entries = new List<byte[]>();
		var at = 0;
		while (at < joined.Length)
		{
			if (joined.Length - at < 4) throw new SyncProtocolException($"Item {itemId}'s history ends part way through a length");
			var length = BinaryPrimitives.ReadUInt32BigEndian(joined.AsSpan(at));
			at += 4;
			if (length > (uint)(joined.Length - at)) throw new SyncProtocolException($"Item {itemId}'s history has an entry longer than what is left of it");
			entries.Add(joined[at..(at + (int)length)]);
			at += (int)length;
		}
		return [.. entries];
	}
}
