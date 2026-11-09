using System.Net;
using System.Net.Http.Json;

namespace Zing.Server.Tests;

// Zing's own e2e runs start this server with the fake issuer and ask it for an ID token, so no test reaches Google.
public class FakeGoogleTests
{
	private sealed record Minted(string IdToken);

	[Fact]
	public async Task The_fake_issuer_mints_a_token_the_server_signs_in_with()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();

		var minted = await client.PostAsJsonAsync("/fake-google/token", new { sub = ServerFactory.AllowedSub });
		minted.EnsureSuccessStatusCode();
		var idToken = (await minted.Content.ReadFromJsonAsync<Minted>(ServerFactory.JsonOpts))!.IdToken;

		var signIn = await client.PostAsJsonAsync("/auth/google", new { idToken });
		Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
	}

	[Fact]
	public async Task An_id_tokens_lifetime_is_checked_on_the_servers_clock()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		clock.Advance(TimeSpan.FromDays(60));

		var fresh = await MintAsync(client);
		Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/google", new { idToken = fresh })).StatusCode);

		var stale = await MintAsync(client);
		clock.Advance(TimeSpan.FromHours(2));
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/google", new { idToken = stale })).StatusCode);
	}

	private static async Task<string> MintAsync(HttpClient client)
	{
		var minted = await client.PostAsJsonAsync("/fake-google/token", new { sub = ServerFactory.AllowedSub });
		minted.EnsureSuccessStatusCode();
		return (await minted.Content.ReadFromJsonAsync<Minted>(ServerFactory.JsonOpts))!.IdToken;
	}

	[Fact]
	public async Task A_minted_token_carries_the_email_it_was_asked_for()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();

		var minted = await client.PostAsJsonAsync("/fake-google/token", new { sub = ServerFactory.AllowedSub, email = "ada@example.test" });
		var idToken = (await minted.Content.ReadFromJsonAsync<Minted>(ServerFactory.JsonOpts))!.IdToken;

		var claims = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(idToken);
		Assert.Equal("ada@example.test", claims.GetClaim("email").Value);
		Assert.Equal("true", claims.GetClaim("email_verified").Value);
		Assert.Equal(ServerFactory.ClientId, claims.Audiences.Single());
	}

	[Fact]
	public async Task The_fake_issuer_needs_a_sub()
	{
		using var factory = new ServerFactory();
		var response = await factory.CreateClient().PostAsJsonAsync("/fake-google/token", new { email = "ada@example.test" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Without_the_fake_issuer_there_is_no_token_endpoint()
	{
		using var factory = new ServerFactory(new Dictionary<string, string?> { ["ZING_SERVER_FAKE_GOOGLE"] = null });
		var response = await factory.CreateClient().PostAsJsonAsync("/fake-google/token", new { sub = ServerFactory.AllowedSub });
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}
}
