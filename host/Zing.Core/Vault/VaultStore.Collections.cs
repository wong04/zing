using System.Security.Cryptography;
using System.Text;

namespace Zing.Core.Vault;

/// <summary>What sync keeps beside the vault for the library and settings: their keys, made from the index key, and their state files.</summary>
public sealed partial class VaultStore
{
	public const string LibrarySyncFileName = "library-sync.json";
	public const string SettingsSyncFileName = "settings-sync.json";

	/// <summary>
	/// The key a collection other than the logins is sealed with: HKDF of the index key, so it needs no Hello and every PC with the
	/// vault's keys makes the same one, and labelled with the collection, so nothing sealed for one opens as another or as a login.
	/// </summary>
	/// <exception cref="InvalidOperationException">The vault has no keys yet.</exception>
	public byte[] CollectionKey(string collection)
	{
		VaultKeys keys;
		lock (_gate)
		{
			RequireOpen();
			keys = _keys ?? throw new InvalidOperationException("The vault has no keys yet");
		}
		var indexKey = keys.IndexKey(_cipher);
		try
		{
			return RecoveryKey.Hkdf(indexKey, [], Encoding.UTF8.GetBytes($"zing-sync-collection-v1/{collection}"), VaultCrypto.KeySize);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(indexKey);
		}
	}

	/// <summary>Where library or settings sync keeps its state for this vault.</summary>
	public string CollectionStateFile(string name) => System.IO.Path.Combine(_dir, name);

	/// <summary>Under <c>_gate</c>: turning sync off, or erasing the vault, starts library and settings sync over too.</summary>
	private void DeleteCollectionStates()
	{
		File.Delete(CollectionStateFile(LibrarySyncFileName));
		File.Delete(CollectionStateFile(SettingsSyncFileName));
	}
}
