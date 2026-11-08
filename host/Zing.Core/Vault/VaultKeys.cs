using System.Security.Cryptography;
using System.Text.Json;

namespace Zing.Core.Vault;

/// <summary>
/// vault/keys.bin: the vault's ID and its two keys, never unsealed on disk. The index key is sealed by DPAPI only, so summaries
/// open without a prompt; the secret key by Windows Hello and then DPAPI as the outer layer, so every password needs Hello.
/// </summary>
public sealed class VaultKeys
{
	private const int Format = 1;

	/// <summary>
	/// The vault's own random ID, the user ID in every item's associated data (Ruling 22). Made once with the keys and never
	/// changed, even when sync is turned on later.
	/// </summary>
	public string VaultId { get; }

	/// <summary>What turning on sync stored here; null until then.</summary>
	public VaultSyncKeys? Sync { get; }

	private readonly byte[] _sealedIndexKey;
	private readonly byte[] _sealedSecretKey;

	private VaultKeys(string vaultId, byte[] sealedIndexKey, byte[] sealedSecretKey, VaultSyncKeys? sync = null)
	{
		VaultId = vaultId;
		_sealedIndexKey = sealedIndexKey;
		_sealedSecretKey = sealedSecretKey;
		Sync = sync;
	}

	/// <summary>The same keys with <paramref name="sync"/> stored beside them. Nothing is written until <see cref="Save"/>.</summary>
	public VaultKeys WithSync(VaultSyncKeys sync) => new(VaultId, _sealedIndexKey, _sealedSecretKey, sync);

	/// <summary>
	/// New keys and a new vault ID, sealed; one Hello call. Also returns the unsealed secret key for the write that needed the
	/// keys, so that write doesn't prompt a second time. The caller wipes it.
	/// </summary>
	public static async Task<(VaultKeys Keys, byte[] SecretKey)> CreateAsync(ISecretCipher cipher, IHelloSealer sealer)
	{
		var indexKey = RandomNumberGenerator.GetBytes(VaultCrypto.KeySize);
		var secretKey = RandomNumberGenerator.GetBytes(VaultCrypto.KeySize);
		try
		{
			var helloSealed = await sealer.SealAsync(secretKey);
			var vaultId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
			return (new VaultKeys(vaultId, cipher.Encrypt(indexKey), cipher.Encrypt(helloSealed)), secretKey);
		}
		catch
		{
			CryptographicOperations.ZeroMemory(secretKey);
			throw;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(indexKey);
		}
	}

	/// <summary>DPAPI only, no prompt. The caller wipes it.</summary>
	public byte[] IndexKey(ISecretCipher cipher) => cipher.Decrypt(_sealedIndexKey);

	/// <summary>Asks Hello once, every call; VaultStore holds what it returns (Ruling 44). The caller wipes it.</summary>
	/// <exception cref="VaultSetUpAgainException">The Hello key was reset, or this blob wasn't sealed by this sealer.</exception>
	public async Task<byte[]> SecretKeyAsync(ISecretCipher cipher, IHelloSealer sealer)
	{
		var helloSealed = cipher.Decrypt(_sealedSecretKey);
		try
		{
			return await sealer.UnsealAsync(helloSealed);
		}
		catch (Exception error) when (error is VaultTamperedException or HelloSealFormatException)
		{
			throw new VaultSetUpAgainException(error);
		}
	}

	/// <summary>Null when there's no file yet: the keys are made by the first save.</summary>
	public static VaultKeys? Load(string path)
	{
		if (!File.Exists(path)) return null;
		var file = JsonSerializer.Deserialize<KeysFile>(File.ReadAllBytes(path), VaultJson.Options) ?? throw new InvalidDataException($"{path} is empty");
		if (file.Format != Format) throw new InvalidDataException($"{path} is format {file.Format}; this Zing reads {Format}");
		if (string.IsNullOrEmpty(file.VaultId) || file.IndexKey is not { Length: > 0 } || file.SecretKey is not { Length: > 0 }) throw new InvalidDataException($"{path} is missing a key or the vault ID");
		if (file.Sync is { } sync && (string.IsNullOrEmpty(sync.UserId) || sync.Blob is not { Length: > 0 } || sync.Version < 1 || sync.SealedRecoveryKey is not { Length: > 0 }))
			throw new InvalidDataException($"{path} has sync keys without their user, blob, version or recovery key");
		return new VaultKeys(file.VaultId, file.IndexKey, file.SecretKey, file.Sync);
	}

	public void Save(string path) =>
		JsonFiles.WriteAllBytesAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new KeysFile(Format, VaultId, _sealedIndexKey, _sealedSecretKey, Sync), VaultJson.Options));

	// Sync is absent from a keys.bin written before sync was turned on, which reads as null.
	private sealed record KeysFile(int Format, string VaultId, byte[] IndexKey, byte[] SecretKey, VaultSyncKeys? Sync = null);
}

internal static class VaultJson
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// What turning on sync keeps in keys.bin: the server user it was turned on for, the wrapped keys as uploaded and the version the
/// server gave them (a password change re-wraps from these, over that version), and the recovery key sealed under the secret key,
/// so it can only be shown again after Windows Hello. <see cref="RecoveryConfirmed"/> is false until the person says they saved it.
/// </summary>
public sealed record VaultSyncKeys(string UserId, byte[] Blob, long Version, byte[] SealedRecoveryKey, bool RecoveryConfirmed);

/// <summary>The vault's ID and both keys unsealed, for sync to wrap. Dispose wipes the keys.</summary>
public sealed class VaultKeyMaterial(string vaultId, byte[] indexKey, byte[] secretKey) : IDisposable
{
	public string VaultId { get; } = vaultId;
	public byte[] IndexKey { get; } = indexKey;
	public byte[] SecretKey { get; } = secretKey;

	public void Dispose()
	{
		CryptographicOperations.ZeroMemory(IndexKey);
		CryptographicOperations.ZeroMemory(SecretKey);
	}
}
