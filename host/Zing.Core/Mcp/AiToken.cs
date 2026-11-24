using System.Security.Cryptography;

namespace Zing.Core.Mcp;

public sealed class AiTokenLengthException(string path, int length)
	: CryptographicException($"The AI token in {path} is {length} bytes, not 32.");

/// <summary>The bearer token a local AI client presents to Zing's MCP server. Kept sealed by the OS key store, never in the plain.</summary>
public sealed class AiToken(ISecretCipher cipher, string path)
{
	private const int Length = 32;

	private byte[]? _current;

	/// <summary>Raised after <see cref="Regenerate"/>, so the server can drop sessions that hold the old token.</summary>
	public event Action? Regenerated;

	/// <summary>
	/// A copy, created on first use. Throws <see cref="CryptographicException"/> when the file was sealed by another user or
	/// machine, and <see cref="AiTokenLengthException"/> when it holds something other than a token.
	/// </summary>
	public byte[] Current()
	{
		_current ??= File.Exists(path) ? Read() : Write();
		return _current.ToArray();
	}

	public byte[] Regenerate()
	{
		_current = Write();
		Regenerated?.Invoke();
		return _current.ToArray();
	}

	private byte[] Read()
	{
		var token = cipher.Decrypt(File.ReadAllBytes(path));
		if (token.Length != Length) throw new AiTokenLengthException(path, token.Length);
		return token;
	}

	private byte[] Write()
	{
		var token = RandomNumberGenerator.GetBytes(Length);
		JsonFiles.WriteAllBytesAtomic(path, cipher.Encrypt(token));
		return token;
	}
}
