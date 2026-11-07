using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Zing.Server.Tests;

public class AuthTests
{
	private static Task<HttpResponseMessage> PostGoogle(HttpClient client, string idToken) =>
		client.PostAsJsonAsync("/auth/google", new { idToken });

	private static Task<HttpResponseMessage> PostRefresh(HttpClient client, string refresh) =>
		client.PostAsJsonAsync("/auth/refresh", new { refresh });

	private static async Task<HttpResponseMessage> GetMe(HttpClient client, string session)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
		return await client.SendAsync(request);
	}

	[Fact]
	public async Task Health_returns_200()
	{
		using var factory = new ServerFactory();
		var response = await factory.CreateClient().GetAsync("/health");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Allowed_sub_gets_a_session_and_a_random_user_id()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();

		var login = await factory.SignIn(client);
		var again = await factory.SignIn(client);

		Assert.NotEmpty(login.Session);
		Assert.NotEmpty(login.Refresh);
		Assert.NotEqual(ServerFactory.AllowedSub, login.UserId);
		Assert.Equal(login.UserId, again.UserId);
		Assert.NotEqual(login.UserId, (await factory.SignIn(client, ServerFactory.OtherAllowedSub)).UserId);
		Assert.Equal(HttpStatusCode.OK, (await GetMe(client, login.Session)).StatusCode);
	}

	[Fact]
	public async Task Sub_outside_the_allow_list_gets_403()
	{
		using var factory = new ServerFactory();
		var response = await PostGoogle(factory.CreateClient(), factory.MintIdToken("someone-else"));
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Expired_id_token_gets_401()
	{
		using var factory = new ServerFactory();
		var token = factory.MintIdToken(ServerFactory.AllowedSub, expiresIn: TimeSpan.FromMinutes(-10));
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostGoogle(factory.CreateClient(), token)).StatusCode);
	}

	[Fact]
	public async Task Forged_id_token_gets_401()
	{
		using var factory = new ServerFactory();
		using var attacker = RSA.Create(2048);
		var token = factory.MintIdToken(ServerFactory.AllowedSub, key: new RsaSecurityKey(attacker) { KeyId = "forged" });
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostGoogle(factory.CreateClient(), token)).StatusCode);
	}

	[Fact]
	public async Task Wrong_audience_gets_401()
	{
		using var factory = new ServerFactory();
		var token = factory.MintIdToken(ServerFactory.AllowedSub, audience: "another-client");
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostGoogle(factory.CreateClient(), token)).StatusCode);
	}

	[Fact]
	public async Task Wrong_issuer_gets_401()
	{
		using var factory = new ServerFactory();
		var token = factory.MintIdToken(ServerFactory.AllowedSub, issuer: "https://evil.example");
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostGoogle(factory.CreateClient(), token)).StatusCode);
	}

	[Fact]
	public async Task Unverified_email_gets_401()
	{
		using var factory = new ServerFactory();
		var token = factory.MintIdToken(ServerFactory.AllowedSub, emailVerified: false);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostGoogle(factory.CreateClient(), token)).StatusCode);
	}

	[Fact]
	public async Task Garbage_id_token_gets_401()
	{
		using var factory = new ServerFactory();
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostGoogle(factory.CreateClient(), "not-a-jwt")).StatusCode);
	}

	[Fact]
	public async Task Missing_id_token_gets_400()
	{
		using var factory = new ServerFactory();
		var response = await factory.CreateClient().PostAsJsonAsync("/auth/google", new { });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Refresh_rotates_the_token()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		var login = await factory.SignIn(client);

		var response = await PostRefresh(client, login.Refresh);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var refreshed = (await response.Content.ReadFromJsonAsync<Refreshed>(ServerFactory.JsonOpts))!;

		Assert.NotEqual(login.Refresh, refreshed.Refresh);
		Assert.Equal(HttpStatusCode.OK, (await GetMe(client, refreshed.Session)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await PostRefresh(client, refreshed.Refresh)).StatusCode);
	}

	[Fact]
	public async Task Unknown_refresh_token_gets_401()
	{
		using var factory = new ServerFactory();
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(factory.CreateClient(), "nope")).StatusCode);
	}

	[Fact]
	public async Task Reused_refresh_token_gets_401_and_revokes_that_users_tokens()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		var login = await factory.SignIn(client);
		var other = await factory.SignIn(client, ServerFactory.OtherAllowedSub);
		var rotated = (await (await PostRefresh(client, login.Refresh)).Content.ReadFromJsonAsync<Refreshed>(ServerFactory.JsonOpts))!;

		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, login.Refresh)).StatusCode);

		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, rotated.Refresh)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await PostRefresh(client, other.Refresh)).StatusCode);
	}

	[Fact]
	public async Task Refresh_tokens_are_stored_hashed()
	{
		using var factory = new ServerFactory();
		var login = await factory.SignIn(factory.CreateClient());

		using var connection = new SqliteConnection($"Data Source={factory.DbPath}");
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM refresh_tokens WHERE hash = $raw";
		command.Parameters.AddWithValue("$raw", login.Refresh);
		Assert.Equal(0L, (long)command.ExecuteScalar()!);
	}

	[Fact]
	public async Task Tampered_or_missing_session_gets_401()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		var login = await factory.SignIn(client);

		var tampered = (login.Session[0] == 'A' ? 'B' : 'A') + login.Session[1..];
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(client, tampered)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
	}

	[Fact]
	public async Task Session_signed_with_another_key_gets_401()
	{
		using var factory = new ServerFactory();
		using var other = new ServerFactory();
		var foreign = await other.SignIn(other.CreateClient());
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(factory.CreateClient(), foreign.Session)).StatusCode);
	}

	[Fact]
	public async Task The_31st_request_in_a_minute_from_one_user_gets_429()
	{
		using var factory = new ServerFactory(new() { ["ZING_RATE_USER_PER_MINUTE"] = "30" });
		var client = factory.CreateClient();
		var login = await factory.SignIn(client);

		for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await GetMe(client, login.Session)).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await GetMe(client, login.Session)).StatusCode);

		var other = await factory.SignIn(client, ServerFactory.OtherAllowedSub);
		Assert.Equal(HttpStatusCode.OK, (await GetMe(client, other.Session)).StatusCode);
	}

	[Fact]
	public async Task The_61st_request_in_a_minute_from_one_ip_gets_429()
	{
		using var factory = new ServerFactory(new() { ["ZING_RATE_IP_PER_MINUTE"] = "60" });
		var client = factory.CreateClient();

		for (var i = 0; i < 60; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/health")).StatusCode);
	}

	[Fact]
	public async Task Session_expires_after_15_minutes()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		var login = await factory.SignIn(client);

		clock.Advance(TimeSpan.FromMinutes(14));
		Assert.Equal(HttpStatusCode.OK, (await GetMe(client, login.Session)).StatusCode);
		clock.Advance(TimeSpan.FromMinutes(2));
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(client, login.Session)).StatusCode);
	}

	[Fact]
	public async Task Refresh_token_older_than_90_days_gets_401()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		var login = await factory.SignIn(client);

		clock.Advance(TimeSpan.FromDays(91));
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, login.Refresh)).StatusCode);
	}

	[Fact]
	public async Task Refresh_lifetime_slides_with_each_rotation()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		var refresh = (await factory.SignIn(client)).Refresh;

		for (var i = 0; i < 3; i++)
		{
			clock.Advance(TimeSpan.FromDays(60));
			var response = await PostRefresh(client, refresh);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			refresh = (await response.Content.ReadFromJsonAsync<Refreshed>(ServerFactory.JsonOpts))!.Refresh;
		}
	}

	[Fact]
	public async Task Refresh_prunes_rows_older_than_90_days()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		var old = await factory.SignIn(client);
		var usedOld = (await (await PostRefresh(client, old.Refresh)).Content.ReadFromJsonAsync<Refreshed>(ServerFactory.JsonOpts))!;
		clock.Advance(TimeSpan.FromDays(91));
		var fresh = await factory.SignIn(client, ServerFactory.OtherAllowedSub);
		Assert.Equal(HttpStatusCode.OK, (await PostRefresh(client, fresh.Refresh)).StatusCode);

		using var connection = new SqliteConnection($"Data Source={factory.DbPath}");
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM refresh_tokens";
		Assert.Equal(2L, (long)command.ExecuteScalar()!);
	}

	[Fact]
	public async Task Concurrent_refreshes_with_one_token_let_exactly_one_through()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		var login = await factory.SignIn(client);

		var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => PostRefresh(client, login.Refresh))));

		Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
		Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
	}

	[Fact]
	public async Task Refresh_for_a_sub_removed_from_the_allow_list_gets_403_and_revokes()
	{
		using var first = new ServerFactory();
		var login = await first.SignIn(first.CreateClient());

		using var narrowed = new ServerFactory(new()
		{
			["ZING_DB_PATH"] = first.DbPath,
			["ZING_ALLOWED_SUBS"] = ServerFactory.OtherAllowedSub,
		});
		var client = narrowed.CreateClient();
		Assert.Equal(HttpStatusCode.Forbidden, (await PostRefresh(client, login.Refresh)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, login.Refresh)).StatusCode);
	}

	[Fact]
	public async Task Unknown_kid_triggers_one_key_refresh_then_is_accepted()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		Assert.Equal(0, factory.Issuer.RefreshCount);

		var rotated = factory.Issuer.Rotate();
		var token = factory.MintIdToken(ServerFactory.AllowedSub, key: rotated);

		Assert.Equal(HttpStatusCode.OK, (await PostGoogle(client, token)).StatusCode);
		Assert.Equal(1, factory.Issuer.RefreshCount);

		Assert.Equal(HttpStatusCode.OK, (await PostGoogle(client, token)).StatusCode);
		Assert.Equal(1, factory.Issuer.RefreshCount);
	}

	[Fact]
	public async Task Unknown_kid_that_a_refresh_does_not_resolve_gets_401()
	{
		using var factory = new ServerFactory();
		using var attacker = RSA.Create(2048);
		var token = factory.MintIdToken(ServerFactory.AllowedSub, key: new RsaSecurityKey(attacker) { KeyId = "never-published" });
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostGoogle(factory.CreateClient(), token)).StatusCode);
		Assert.Equal(1, factory.Issuer.RefreshCount);
	}

	[Fact]
	public void Default_limits_are_30_per_user_and_60_per_ip()
	{
		var limits = RateLimitOptions.From(new ConfigurationBuilder().Build());
		Assert.Equal(30, limits.PerUserPerMinute);
		Assert.Equal(60, limits.PerIpPerMinute);
	}

	[Fact]
	public void Schema_has_the_tables_and_wal()
	{
		using var factory = new ServerFactory();
		factory.CreateClient();

		using var connection = new SqliteConnection($"Data Source={factory.DbPath}");
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
		var tables = new List<string>();
		using (var reader = command.ExecuteReader())
		{
			while (reader.Read()) tables.Add(reader.GetString(0));
		}
		Assert.Contains("users", tables);
		Assert.Contains("items", tables);
		Assert.Contains("refresh_tokens", tables);

		using var wal = connection.CreateCommand();
		wal.CommandText = "PRAGMA journal_mode";
		Assert.Equal("wal", (string)wal.ExecuteScalar()!);
	}
}

public class ConfigTests
{
	private static void AssertStartupFails(string name, Dictionary<string, string?> overrides)
	{
		using var factory = new ServerFactory(overrides);
		var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
		Assert.Contains(name, error.ToString());
	}

	[Fact]
	public void Fake_google_is_refused_in_production() =>
		AssertStartupFails("ZING_SERVER_FAKE_GOOGLE", new() { ["environment"] = "Production" });

	[Fact]
	public void Missing_session_key_fails_startup() =>
		AssertStartupFails("ZING_SESSION_KEY", new() { ["ZING_SESSION_KEY"] = null });

	[Fact]
	public void Short_session_key_fails_startup() =>
		AssertStartupFails("ZING_SESSION_KEY", new() { ["ZING_SESSION_KEY"] = Convert.ToBase64String(new byte[16]) });

	[Fact]
	public void Session_key_that_is_not_base64_fails_startup() =>
		AssertStartupFails("ZING_SESSION_KEY", new() { ["ZING_SESSION_KEY"] = "!!not base64!!" });

	[Fact]
	public void Missing_allowed_subs_fails_startup() =>
		AssertStartupFails("ZING_ALLOWED_SUBS", new() { ["ZING_ALLOWED_SUBS"] = null });

	[Fact]
	public void Missing_client_id_fails_startup_outside_fake_mode() =>
		AssertStartupFails("ZING_GOOGLE_CLIENT_ID", new()
		{
			["ZING_SERVER_FAKE_GOOGLE"] = null,
			["ZING_GOOGLE_CLIENT_ID"] = null,
		});
}

public class GoogleKeySourceTests
{
	private static (GoogleKeySource Source, Func<int> Fetches, ManualClock Clock) Build()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		var fetches = 0;
		var source = new GoogleKeySource(clock, _ =>
		{
			fetches++;
			var document = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration();
			document.SigningKeys.Add(new SymmetricSecurityKey(new byte[32]) { KeyId = $"key-{fetches}" });
			return Task.FromResult(document);
		});
		return (source, () => fetches, clock);
	}

	[Fact]
	public async Task Forced_refresh_fetches_once_inside_five_minutes_and_again_after()
	{
		var (source, fetches, clock) = Build();

		var first = await source.RefreshKeysAsync(CancellationToken.None);
		Assert.Equal("key-1", first.Single().KeyId);
		Assert.Equal(1, fetches());

		clock.Advance(TimeSpan.FromMinutes(4));
		var throttled = await source.RefreshKeysAsync(CancellationToken.None);
		Assert.Equal(1, fetches());
		Assert.Equal("key-1", throttled.Single().KeyId);

		clock.Advance(TimeSpan.FromMinutes(2));
		var second = await source.RefreshKeysAsync(CancellationToken.None);
		Assert.Equal(2, fetches());
		Assert.Equal("key-2", second.Single().KeyId);
	}
}
