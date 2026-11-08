using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace Zing.Server.Tests;

// Ruling 49: sign-out revokes one sign-in's refresh tokens. Ruling 50: a repeat within 2 minutes is a lost response, not reuse.
public class RefreshFamilyTests
{
	private static Task<HttpResponseMessage> PostRefresh(HttpClient client, string refresh) =>
		client.PostAsJsonAsync("/auth/refresh", new { refresh });

	private static Task<HttpResponseMessage> PostSignOut(HttpClient client, string refresh) =>
		client.PostAsJsonAsync("/auth/signout", new { refresh });

	private static async Task<string> Rotate(HttpClient client, string refresh)
	{
		var response = await PostRefresh(client, refresh);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<Refreshed>(ServerFactory.JsonOpts))!.Refresh;
	}

	[Fact]
	public async Task A_lost_refresh_response_is_retried_without_revoking_the_users_other_sign_ins()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		var pc = await factory.SignIn(client);
		var otherPc = await factory.SignIn(client);

		var lost = await Rotate(client, pc.Refresh);
		clock.Advance(TimeSpan.FromSeconds(90));
		var retried = await Rotate(client, pc.Refresh);

		Assert.NotEqual(lost, retried);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, lost)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await PostRefresh(client, retried)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await PostRefresh(client, otherPc.Refresh)).StatusCode);
	}

	[Fact]
	public async Task A_repeat_after_two_minutes_is_reuse_and_revokes_every_sign_in()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		var pc = await factory.SignIn(client);
		var otherPc = await factory.SignIn(client);

		var successor = await Rotate(client, pc.Refresh);
		clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, pc.Refresh)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, successor)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, otherPc.Refresh)).StatusCode);
	}

	[Fact]
	public async Task A_repeat_after_the_successor_was_used_is_reuse()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		var pc = await factory.SignIn(client);
		var successor = await Rotate(client, pc.Refresh);
		var next = await Rotate(client, successor);

		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, pc.Refresh)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, next)).StatusCode);
	}

	[Fact]
	public async Task The_retry_window_counts_from_the_first_use_and_does_not_slide()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var client = factory.CreateClient();
		var pc = await factory.SignIn(client);

		await Rotate(client, pc.Refresh);
		clock.Advance(TimeSpan.FromSeconds(70));
		await Rotate(client, pc.Refresh);
		clock.Advance(TimeSpan.FromSeconds(70));

		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, pc.Refresh)).StatusCode);
	}

	[Fact]
	public async Task Sign_out_revokes_that_sign_in_and_leaves_the_users_other_sign_ins()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		var pc = await factory.SignIn(client);
		var otherPc = await factory.SignIn(client);
		var rotated = await Rotate(client, pc.Refresh);

		Assert.Equal(HttpStatusCode.NoContent, (await PostSignOut(client, rotated)).StatusCode);

		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, rotated)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await PostRefresh(client, otherPc.Refresh)).StatusCode);
	}

	[Fact]
	public async Task Sign_out_with_an_unknown_token_answers_the_same_and_revokes_nothing()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();
		var pc = await factory.SignIn(client);

		Assert.Equal(HttpStatusCode.NoContent, (await PostSignOut(client, "not-a-token")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await PostRefresh(client, pc.Refresh)).StatusCode);
	}

	[Fact]
	public async Task Sign_out_needs_a_token()
	{
		using var factory = new ServerFactory();
		var response = await factory.CreateClient().PostAsJsonAsync("/auth/signout", new { });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task A_database_from_before_families_is_upgraded_and_its_tokens_still_refresh()
	{
		using var first = new ServerFactory();
		var login = await first.SignIn(first.CreateClient());
		using (var connection = new SqliteConnection($"Data Source={first.DbPath}"))
		{
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = """
				CREATE TABLE old_tokens AS SELECT hash, user_id, created_at, used FROM refresh_tokens;
				DROP TABLE refresh_tokens;
				ALTER TABLE old_tokens RENAME TO refresh_tokens;
				""";
			command.ExecuteNonQuery();
		}
		SqliteConnection.ClearAllPools();

		using var upgraded = new ServerFactory(new() { ["ZING_DB_PATH"] = first.DbPath });
		var client = upgraded.CreateClient();
		var rotated = await Rotate(client, login.Refresh);
		Assert.Equal(HttpStatusCode.NoContent, (await PostSignOut(client, rotated)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefresh(client, rotated)).StatusCode);
	}
}
