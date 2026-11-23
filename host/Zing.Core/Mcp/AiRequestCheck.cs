using System.Buffers.Text;
using System.Security.Cryptography;

namespace Zing.Core.Mcp;

/// <summary>
/// The gate on every HTTP request to the MCP endpoint, before its body is read. Host and Origin stop a web page that reaches
/// the port through DNS rebinding or a cross-site fetch; the bearer token stops other local software that wasn't given it.
/// </summary>
public static class AiRequestCheck
{
	public enum Result { Ok, Forbidden, Unauthorized }

	private const string Scheme = "Bearer ";

	/// <summary>How the token is written in the Authorization header and shown in Settings: unpadded base64url.</summary>
	public static string Encode(ReadOnlySpan<byte> token) => Base64Url.EncodeToString(token);

	public static Result Check(string? host, string? origin, string? authorization, int port, ReadOnlySpan<byte> token)
	{
		if (!IsLoopbackHost(host, port)) return Result.Forbidden;
		if (origin is not null && !IsLoopbackOrigin(origin, port)) return Result.Forbidden;
		if (authorization is null || !authorization.StartsWith(Scheme, StringComparison.Ordinal)) return Result.Unauthorized;
		var presented = authorization[Scheme.Length..].Trim();
		if (!Base64Url.IsValid(presented)) return Result.Unauthorized;
		return CryptographicOperations.FixedTimeEquals(Base64Url.DecodeFromChars(presented), token) ? Result.Ok : Result.Unauthorized;
	}

	private static bool IsLoopbackHost(string? host, int port) =>
		host is not null && (host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase));

	private static bool IsLoopbackOrigin(string origin, int port) =>
		origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) || origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
}
