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

	private readonly byte[] _sealedIndexKey;
	private readonly byte[] _sealedSecretKey;

	private VaultKeys(string vaultId, byte[] sealedIndexKey, byte[] sealedSecretKey)
	{
		VaultId = vaultId;
		_sealedIndexKey = sealedIndexKey;
		_sealedSecretKey = sealedSecretKey;
	}

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

	/// <summary>Asks Hello once. The caller wipes it.</summary>
	public Task<byte[]> SecretKeyAsync(ISecretCipher cipher, IHelloSealer sealer) => sealer.UnsealAsync(cipher.Decrypt(_sealedSecretKey));

	/// <summary>Null when there's no file yet: the keys are made by the first save.</summary>
	public static VaultKeys? Load(string path)
	{
		if (!File.Exists(path)) return null;
		var file = JsonSerializer.Deserialize<KeysFile>(File.ReadAllBytes(path), VaultJson.Options) ?? throw new InvalidDataException($"{path} is empty");
		if (file.Format != Format) throw new InvalidDataException($"{path} is format {file.Format}; this Zing reads {Format}");
		if (string.IsNullOrEmpty(file.VaultId) || file.IndexKey is not { Length: > 0 } || file.SecretKey is not { Length: > 0 }) throw new InvalidDataException($"{path} is missing a key or the vault ID");
		return new VaultKeys(file.VaultId, file.IndexKey, file.SecretKey);
	}

	public void Save(string path) =>
		JsonFiles.WriteAllBytesAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new KeysFile(Format, VaultId, _sealedIndexKey, _sealedSecretKey), VaultJson.Options));

	private sealed record KeysFile(int Format, string VaultId, byte[] IndexKey, byte[] SecretKey);
}

internal static class VaultJson
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
