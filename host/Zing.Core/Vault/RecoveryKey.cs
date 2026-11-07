using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Zing.Core.Vault;

/// <summary>A recovery code that isn't 26 base32 characters, or whose checksum doesn't match (a mistyped character).</summary>
public sealed class RecoveryKeyFormatException(string message) : Exception(message);

/// <summary>
/// 128 random bits shown as 26 RFC 4648 base32 characters in groups of four. 128 bits fill 25.6 characters,
/// so the 26th character's last two bits are the first two bits of SHA-256 over the key, which catch most typos.
/// </summary>
public static class RecoveryKey
{
	private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
	private const int KeyBytes = 16;
	private const int Characters = 26;
	private const int GroupSize = 4;

	private static readonly byte[] Info = "zing-recovery-v1"u8.ToArray();

	public static string Generate()
	{
		var bytes = RandomNumberGenerator.GetBytes(KeyBytes);
		try
		{
			return Format(bytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	/// <summary>The 32-byte key the code stands for. Accepts lowercase, and hyphens or spaces between groups.</summary>
	public static byte[] ToKey(string code)
	{
		var bytes = Parse(code);
		try
		{
			return Hkdf(bytes, [], Info, VaultCrypto.KeySize);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	internal static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length) =>
		KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(ikm, salt, info, length);

	internal static string Format(ReadOnlySpan<byte> key)
	{
		if (key.Length != KeyBytes) throw new ArgumentException($"A recovery key is {KeyBytes} bytes, not {key.Length}", nameof(key));
		var chars = new StringBuilder(Characters);
		int buffer = 0, bits = 0;
		foreach (var b in key)
		{
			buffer = (buffer << 8 | b) & 0xFFF;
			bits += 8;
			for (; bits >= 5; bits -= 5) chars.Append(Alphabet[buffer >> (bits - 5) & 31]);
		}
		buffer = (buffer << 2 | Checksum(key)) & 31;
		chars.Append(Alphabet[buffer]);
		return string.Join('-', chars.ToString().Chunk(GroupSize).Select(group => new string(group)));
	}

	private static byte[] Parse(string code)
	{
		var chars = code.ToUpperInvariant().Where(c => c != '-' && c != ' ').ToArray();
		if (chars.Length != Characters) throw new RecoveryKeyFormatException($"A recovery key has {Characters} characters, not {chars.Length}");
		var key = new byte[KeyBytes];
		int buffer = 0, bits = 0, at = 0;
		foreach (var c in chars)
		{
			var value = Alphabet.IndexOf(c);
			if (value < 0) throw new RecoveryKeyFormatException($"'{c}' isn't a recovery key character");
			buffer = (buffer << 5 | value) & 0xFFF;
			bits += 5;
			if (bits < 8) continue;
			bits -= 8;
			key[at++] = (byte)(buffer >> bits);
		}
		// 130 bits read, 128 into the key: the 2 left over are the checksum.
		if ((buffer & 3) == Checksum(key)) return key;
		CryptographicOperations.ZeroMemory(key);
		throw new RecoveryKeyFormatException("The recovery key's checksum doesn't match, so a character is mistyped");
	}

	private static int Checksum(ReadOnlySpan<byte> key) => SHA256.HashData(key)[0] >> 6;
}
