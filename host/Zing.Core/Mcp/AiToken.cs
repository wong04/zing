using System.Security.Cryptography;

namespace Zing.Core.Mcp;

/// <summary>The bearer token a local AI client presents to Zing's MCP server. Kept sealed by the OS key store, never in the plain.</summary>
public sealed class AiToken(ISecretCipher cipher, string path)
{
	private const int Length = 32;

	private byte[]? _current;

	/// <summary>Raised after <see cref="Regenerate"/>, so the server can drop sessions that hold the old token.</summary>
	public event Action? Regenerated;

	/// <summary>Created on first use. Throws <see cref="CryptographicException"/> when the file was sealed by another user or machine.</summary>
	public byte[] Current()
	{
		if (_current is not null) return _current;
		_current = File.Exists(path) ? cipher.Decrypt(File.ReadAllBytes(path)) : Write();
		return _current;
	}

	public byte[] Regenerate()
	{
		_current = Write();
		Regenerated?.Invoke();
		return _current;
	}

	private byte[] Write()
	{
		var token = RandomNumberGenerator.GetBytes(Length);
		JsonFiles.WriteAllBytesAtomic(path, cipher.Encrypt(token));
		return token;
	}
}
