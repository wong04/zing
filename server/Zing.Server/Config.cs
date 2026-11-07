namespace Zing.Server;

public sealed class ConfigurationException(string message) : InvalidOperationException(message);

public sealed record ServerConfig(
	byte[] SessionKey,
	IReadOnlySet<string> AllowedSubs,
	string GoogleClientId,
	bool FakeGoogle,
	string DbPath)
{
	public const int MinSessionKeyBytes = 32;
	private const string FakeClientId = "zing-fake-client";

	public static ServerConfig Load(IConfiguration configuration)
	{
		var fake = configuration["ZING_SERVER_FAKE_GOOGLE"] == "1";
		var clientId = fake
			? configuration["ZING_GOOGLE_CLIENT_ID"] ?? FakeClientId
			: Required(configuration, "ZING_GOOGLE_CLIENT_ID");
		return new ServerConfig(
			ParseSessionKey(Required(configuration, "ZING_SESSION_KEY")),
			ParseAllowedSubs(Required(configuration, "ZING_ALLOWED_SUBS")),
			clientId,
			fake,
			configuration["ZING_DB_PATH"] ?? "/data/zing.db");
	}

	private static string Required(IConfiguration configuration, string name)
	{
		var value = configuration[name];
		if (string.IsNullOrWhiteSpace(value)) throw new ConfigurationException($"{name} is required and has no default.");
		return value;
	}

	private static byte[] ParseSessionKey(string value)
	{
		byte[] key;
		try
		{
			key = Convert.FromBase64String(value);
		}
		catch (FormatException error)
		{
			throw new ConfigurationException($"ZING_SESSION_KEY must be base64. {error.Message}");
		}
		if (key.Length < MinSessionKeyBytes) throw new ConfigurationException($"ZING_SESSION_KEY must decode to at least {MinSessionKeyBytes} bytes.");
		return key;
	}

	private static HashSet<string> ParseAllowedSubs(string value)
	{
		var subs = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
		if (subs.Count == 0) throw new ConfigurationException("ZING_ALLOWED_SUBS must list at least one sub.");
		return subs;
	}
}
