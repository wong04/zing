using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Zing.Server.Tests;

public sealed record Login(string Session, string Refresh, string UserId);

public sealed record Refreshed(string Session, string Refresh);

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
	private DateTimeOffset now = start;

	public override DateTimeOffset GetUtcNow() => now;

	public void Advance(TimeSpan by) => now += by;
}

public sealed class ServerFactory : WebApplicationFactory<Program>
{
	public const string ClientId = "zing-test-client";
	public const string AllowedSub = "sub-allowed-1";
	public const string OtherAllowedSub = "sub-allowed-2";
	public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

	private readonly Dictionary<string, string?> settings;
	private readonly string dbDir = Directory.CreateTempSubdirectory("zing-server-tests").FullName;

	private readonly TimeProvider? clock;

	public ServerFactory(Dictionary<string, string?>? overrides = null, TimeProvider? clock = null)
	{
		this.clock = clock;
		settings = new Dictionary<string, string?>
		{
			["ZING_SERVER_FAKE_GOOGLE"] = "1",
			["ZING_GOOGLE_CLIENT_ID"] = ClientId,
			["ZING_SESSION_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
			["ZING_ALLOWED_SUBS"] = $"{AllowedSub},{OtherAllowedSub}",
			["ZING_DB_PATH"] = Path.Combine(dbDir, "zing.db"),
			["ZING_RATE_IP_PER_MINUTE"] = "100000",
			["ZING_RATE_USER_PER_MINUTE"] = "100000",
		};
		if (overrides is null) return;
		foreach (var (key, value) in overrides) settings[key] = value;
	}

	public string DbPath => settings["ZING_DB_PATH"]!;

	public FakeGoogleIssuer Issuer => Services.GetRequiredService<FakeGoogleIssuer>();

	public SecurityKey FakeSigningKey => Issuer.SigningKey;

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		foreach (var (key, value) in settings)
		{
			if (value is not null) builder.UseSetting(key, value);
		}
		if (clock is null) return;
		builder.ConfigureTestServices(services => services.AddSingleton(clock));
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (!disposing) return;
		SqliteConnection.ClearAllPools();
		try { Directory.Delete(dbDir, true); } catch (IOException) { }
	}

	public string MintIdToken(
		string sub,
		string audience = ClientId,
		string issuer = "https://accounts.google.com",
		TimeSpan? expiresIn = null,
		bool emailVerified = true,
		SecurityKey? key = null)
	{
		// On the server's clock, which also checks the token's lifetime.
		var expires = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime + (expiresIn ?? TimeSpan.FromMinutes(30));
		var descriptor = new SecurityTokenDescriptor
		{
			Issuer = issuer,
			Audience = audience,
			IssuedAt = expires - TimeSpan.FromHours(1),
			NotBefore = expires - TimeSpan.FromHours(1),
			Expires = expires,
			Claims = new Dictionary<string, object>
			{
				["sub"] = sub,
				["email"] = $"{sub}@example.test",
				["email_verified"] = emailVerified,
			},
			SigningCredentials = new SigningCredentials(key ?? FakeSigningKey, SecurityAlgorithms.RsaSha256),
		};
		return new JsonWebTokenHandler().CreateToken(descriptor);
	}

	public async Task<Login> SignIn(HttpClient client, string sub = AllowedSub)
	{
		var response = await client.PostAsJsonAsync("/auth/google", new { idToken = MintIdToken(sub) });
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<Login>(JsonOpts))!;
	}
}
