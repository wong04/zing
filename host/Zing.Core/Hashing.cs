using System.Security.Cryptography;
using System.Text;

namespace Zing.Core;

public sealed class UnknownHashAlgorithmException(string name) : ArgumentException($"{name} isn't a hash algorithm Zing offers (MD5, SHA-1, SHA-256, SHA-512)");

/// <summary>
/// Digests for the Hashes and HMAC tools (src/shared/devtools/hashAlgorithm.ts names the
/// algorithms). The host does this because the renderer's SubtleCrypto has no MD5.
/// Lowercase hex, over the UTF-8 bytes of text.
/// </summary>
public static class Hashing
{
	private static HashAlgorithmName Name(string algorithm) => algorithm switch
	{
		"MD5" => HashAlgorithmName.MD5,
		"SHA-1" => HashAlgorithmName.SHA1,
		"SHA-256" => HashAlgorithmName.SHA256,
		"SHA-512" => HashAlgorithmName.SHA512,
		_ => throw new UnknownHashAlgorithmException(algorithm),
	};

	public static string Text(string text, string algorithm)
	{
		using var hash = IncrementalHash.CreateHash(Name(algorithm));
		hash.AppendData(Encoding.UTF8.GetBytes(text));
		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	/// <summary>Streamed, so a large download doesn't have to fit in memory.</summary>
	public static async Task<string> FileAsync(string path, string algorithm)
	{
		using var hash = IncrementalHash.CreateHash(Name(algorithm));
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
		var buffer = new byte[1 << 16];
		int read;
		while ((read = await stream.ReadAsync(buffer)) > 0) hash.AppendData(buffer, 0, read);
		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	/// <summary>A keyed signature, as API requests and webhooks use.</summary>
	public static string Hmac(string message, string key, string algorithm)
	{
		using var hmac = IncrementalHash.CreateHMAC(Name(algorithm), Encoding.UTF8.GetBytes(key));
		hmac.AppendData(Encoding.UTF8.GetBytes(message));
		return Convert.ToHexStringLower(hmac.GetHashAndReset());
	}
}
