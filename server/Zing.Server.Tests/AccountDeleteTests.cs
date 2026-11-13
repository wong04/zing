using System.Net;
using System.Net.Http.Json;

namespace Zing.Server.Tests;

/// <summary>Start over for a forgotten Zing password: a Google sign-in made just now deletes everything its account syncs.</summary>
public class AccountDeleteTests
{
	/// <summary>An ID token Google issued just now: MintIdToken dates its issue an hour before it expires.</summary>
	private static string Fresh(ServerFactory factory, string sub = ServerFactory.AllowedSub) => factory.MintIdToken(sub, expiresIn: TimeSpan.FromHours(1));

	private static Task<HttpResponseMessage> Delete(HttpClient client, string idToken, string userId) =>
		client.PostAsJsonAsync("/account/delete", new { idToken, userId });

	private static async Task<(Login Login, HttpClient Client)> SyncingUser(ServerFactory factory, string sub = ServerFactory.AllowedSub)
	{
		var login = await factory.SignIn(factory.CreateClient(), sub);
		var client = factory.SignedIn(login.Session);
		(await client.PutAsJsonAsync("/keys", new { blob = "a2V5cw==", baseVersion = 0 })).EnsureSuccessStatusCode();
		(await client.PutAsJsonAsync("/manifest", new { blob = "bWFuaWZlc3Q=", baseVersion = 0 })).EnsureSuccessStatusCode();
		await client.PushOk(new PushItem("item-1", 0));
		return (login, client);
	}

	[Fact]
	public async Task A_fresh_google_sign_in_deletes_the_keys_items_and_sign_ins_and_a_new_sign_in_starts_empty()
	{
		using var factory = new ServerFactory();
		var (login, client) = await SyncingUser(factory);
		var (other, otherClient) = await SyncingUser(factory, ServerFactory.OtherAllowedSub);

		var deleted = await Delete(factory.CreateClient(), Fresh(factory), login.UserId);

		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
		// The session it had, still within its lifetime, reads and writes nothing any more; its refresh token is gone.
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/keys")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.Push(new PushItem("item-2", 0))).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/auth/refresh", new { refresh = login.Refresh })).StatusCode);

		var again = await factory.SignIn(factory.CreateClient());
		Assert.NotEqual(login.UserId, again.UserId);
		var fresh = factory.SignedIn(again.Session);
		Assert.Equal(HttpStatusCode.NotFound, (await fresh.GetAsync("/keys")).StatusCode);
		Assert.Empty((await fresh.PullOk()).Items);
		Assert.Equal(HttpStatusCode.OK, (await fresh.PutAsJsonAsync("/keys", new { blob = "bmV3", baseVersion = 0 })).StatusCode);

		// Another Google account's data is untouched.
		Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/keys")).StatusCode);
		Assert.Single((await otherClient.PullOk()).Items);
		Assert.NotEqual(other.UserId, again.UserId);
	}

	[Fact]
	public async Task A_sign_in_older_than_five_minutes_deletes_nothing()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var (login, client) = await SyncingUser(factory);
		var token = Fresh(factory);
		clock.Advance(TimeSpan.FromMinutes(6));

		Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(factory.CreateClient(), token, login.UserId)).StatusCode);
		// The default token, still valid for sign-in, was issued half an hour ago.
		Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(factory.CreateClient(), factory.MintIdToken(ServerFactory.AllowedSub), login.UserId)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys")).StatusCode);
	}

	[Fact]
	public async Task Another_google_account_deletes_nothing()
	{
		using var factory = new ServerFactory();
		var (login, client) = await SyncingUser(factory);

		// One that never synced, and one that does: neither is this user's.
		Assert.Equal(HttpStatusCode.Conflict, (await Delete(factory.CreateClient(), Fresh(factory, ServerFactory.OtherAllowedSub), login.UserId)).StatusCode);
		await SyncingUser(factory, ServerFactory.OtherAllowedSub);
		Assert.Equal(HttpStatusCode.Conflict, (await Delete(factory.CreateClient(), Fresh(factory, ServerFactory.OtherAllowedSub), login.UserId)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys")).StatusCode);
	}

	[Fact]
	public async Task A_session_alone_or_a_bad_or_forbidden_token_deletes_nothing()
	{
		using var factory = new ServerFactory();
		var (login, client) = await SyncingUser(factory);

		Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/account/delete", new { userId = login.UserId })).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await Delete(factory.CreateClient(), Fresh(factory), "")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(factory.CreateClient(), factory.MintIdToken(ServerFactory.AllowedSub, audience: "someone-else", expiresIn: TimeSpan.FromHours(1)), login.UserId)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(factory.CreateClient(), factory.MintIdToken(ServerFactory.AllowedSub, emailVerified: false, expiresIn: TimeSpan.FromHours(1)), login.UserId)).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await Delete(factory.CreateClient(), Fresh(factory, "someone-else"), login.UserId)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys")).StatusCode);
	}

	[Fact]
	public async Task A_google_account_with_nothing_on_the_server_has_nothing_left_to_delete()
	{
		using var factory = new ServerFactory();
		var (login, _) = await SyncingUser(factory);
		Assert.Equal(HttpStatusCode.NoContent, (await Delete(factory.CreateClient(), Fresh(factory), login.UserId)).StatusCode);

		Assert.Equal(HttpStatusCode.NoContent, (await Delete(factory.CreateClient(), Fresh(factory), login.UserId)).StatusCode);
	}
}
