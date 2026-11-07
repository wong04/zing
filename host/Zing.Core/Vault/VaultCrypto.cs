using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Zing.Core.Vault;

/// <summary>A ciphertext failed its tag: a wrong key, wrong associated data, or bytes changed in storage or by the server.</summary>
public sealed class VaultTamperedException(string message) : Exception(message);

// The numbers are part of every ciphertext's associated data, so they must never change.
public enum ItemPart : byte { Summary = 0, Password = 1, History = 2 }

/// <summary>Binds a ciphertext to its user, item, version and part, so the server can't move, swap or relabel one.</summary>
public sealed record ItemAd(string UserId, string ItemId, long Version, ItemPart Part)
{
	private static readonly byte[] DomainTag = "zing-item-v1"u8.ToArray();

	/// <summary>The domain tag and both strings as length-prefixed UTF-8, then the version as a big-endian int64, then the part as one byte.</summary>
	public byte[] ToBytes()
	{
		if (!Enum.IsDefined(Part)) throw new ArgumentOutOfRangeException(nameof(Part), Part, "Not an item part");
		var strings = AdEncoding.Strings(DomainTag, Encoding.UTF8.GetBytes(UserId), Encoding.UTF8.GetBytes(ItemId));
		var bytes = new byte[strings.Length + 8 + 1];
		strings.CopyTo(bytes, 0);
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(strings.Length), Version);
		bytes[^1] = (byte)Part;
		return bytes;
	}
}

internal static class AdEncoding
{
	/// <summary>Each string as a big-endian uint32 length then its bytes, so no field boundary can shift.</summary>
	public static byte[] Strings(params byte[][] strings)
	{
		var bytes = new byte[strings.Sum(s => 4 + s.Length)];
		var at = 0;
		foreach (var s in strings)
		{
			BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)s.Length);
			s.CopyTo(bytes, at + 4);
			at += 4 + s.Length;
		}
		return bytes;
	}
}

/// <summary>XChaCha20-Poly1305 with a random 24-byte nonce: random nonces are safe across PCs sharing a key. Sealed bytes are nonce ‖ ciphertext ‖ tag.</summary>
public static class VaultCrypto
{
	public const int KeySize = 32;
	public const int NonceSize = 24;
	public const int TagSize = 16;

	private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

	public static byte[] Seal(ReadOnlySpan<byte> key, byte[] plain, ItemAd ad)
	{
		var padded = Padding.Pad(plain);
		try
		{
			return Encrypt(key, padded, ad.ToBytes());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(padded);
		}
	}

	public static byte[] Open(ReadOnlySpan<byte> key, byte[] sealedBytes, ItemAd ad)
	{
		var padded = Decrypt(key, sealedBytes, ad.ToBytes());
		try
		{
			return Padding.Unpad(padded);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(padded);
		}
	}

	internal static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> ad) =>
		Encrypt(key, RandomNumberGenerator.GetBytes(NonceSize), plain, ad);

	/// <summary>Takes the nonce only so the known-answer test can fix it. Everything else uses a random one.</summary>
	internal static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> ad)
	{
		if (nonce.Length != NonceSize) throw new ArgumentException($"Nonce must be {NonceSize} bytes, not {nonce.Length}", nameof(nonce));
		using var aeadKey = ImportKey(key);
		var sealedBytes = new byte[NonceSize + plain.Length + TagSize];
		nonce.CopyTo(sealedBytes);
		Aead.Encrypt(aeadKey, nonce, ad, plain, sealedBytes.AsSpan(NonceSize));
		return sealedBytes;
	}

	internal static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> ad)
	{
		if (sealedBytes.Length < NonceSize + TagSize) throw new VaultTamperedException($"Sealed bytes are {sealedBytes.Length} long, shorter than a nonce and a tag");
		using var aeadKey = ImportKey(key);
		var plain = new byte[sealedBytes.Length - NonceSize - TagSize];
		if (!Aead.Decrypt(aeadKey, sealedBytes[..NonceSize], ad, sealedBytes[NonceSize..], plain)) throw new VaultTamperedException("The ciphertext failed its tag");
		return plain;
	}

	private static Key ImportKey(ReadOnlySpan<byte> key)
	{
		if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes, not {key.Length}", nameof(key));
		return Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey);
	}
}

/// <summary>Wraps one key under another, with a label as associated data so a wrapped index key can't pass as the secret key.</summary>
public static class KeyWrap
{
	private static readonly byte[] DomainTag = "zing-wrap-v1"u8.ToArray();

	public static byte[] Wrap(byte[] wrappingKey, byte[] key, string label) => VaultCrypto.Encrypt(wrappingKey, key, Ad(label));

	public static byte[] Unwrap(byte[] wrappingKey, byte[] wrapped, string label) => VaultCrypto.Decrypt(wrappingKey, wrapped, Ad(label));

	/// <summary>The domain tag then the label, both as length-prefixed UTF-8.</summary>
	internal static byte[] Ad(string label) => AdEncoding.Strings(DomainTag, Encoding.UTF8.GetBytes(label));
}
