using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zing.Server;

// Format: userId.expiryUnixSeconds.base64url(HMAC-SHA256 over "userId.expiryUnixSeconds").
public sealed class SessionTokens(byte[] key, TimeProvider clock)
{
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

	public string Issue(string userId)
	{
		var expires = (clock.GetUtcNow() + Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
		var payload = $"{userId}.{expires}";
		return $"{payload}.{Base64Url.EncodeToString(Sign(payload))}";
	}

	public bool TryValidate(string? token, out string userId)
	{
		userId = "";
		if (string.IsNullOrEmpty(token)) return false;

		var parts = token.Split('.');
		if (parts.Length != 3) return false;
		if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)) return false;

		byte[] presented;
		try
		{
			presented = Base64Url.DecodeFromChars(parts[2]);
		}
		catch (FormatException)
		{
			return false;
		}
		if (!CryptographicOperations.FixedTimeEquals(presented, Sign($"{parts[0]}.{parts[1]}"))) return false;
		if (clock.GetUtcNow().ToUnixTimeSeconds() >= expires) return false;

		userId = parts[0];
		return true;
	}

	public bool TryValidate(HttpContext context, out string userId)
	{
		userId = "";
		const string prefix = "Bearer ";
		var header = context.Request.Headers.Authorization.ToString();
		if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;
		return TryValidate(header[prefix.Length..], out userId);
	}

	private byte[] Sign(string payload) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
}
