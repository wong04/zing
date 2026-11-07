using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace Zing.Server.Tests;

public class ChangesTests
{
	[Fact]
	public async Task Requests_without_a_session_are_rejected()
	{
		using var factory = new ServerFactory();
		var client = factory.CreateClient();

		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/changes")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.Push(new PushItem("a", 0))).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/keys")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/manifest")).StatusCode);
	}

	[Fact]
	public async Task Each_accepted_write_takes_the_next_revision()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		var first = await client.PushOk(new PushItem("a", 0), new PushItem("b", 0));
		var second = await client.PushOk(new PushItem("a", 1));

		Assert.Equal([new Accepted("a", 1), new Accepted("b", 2)], first.Accepted);
		Assert.Equal([new Accepted("a", 3)], second.Accepted);
		var pulled = await client.PullOk();
		Assert.Equal(3, pulled.Revision);
		Assert.Equal(["b", "a"], pulled.Items.Select(item => item.ItemId));
		Assert.Equal([2L, 3L], pulled.Items.Select(item => item.Revision));
	}

	[Fact]
	public async Task A_stale_base_revision_conflicts_and_carries_the_current_item()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushOk(new PushItem("a", 0, Summary: "b25l"));
		await client.PushOk(new PushItem("a", 1, Summary: "dHdv", Version: 2));

		var stale = await client.PushOk(new PushItem("a", 1, Summary: "bG9zZXI="));

		Assert.Empty(stale.Accepted);
		var current = Assert.Single(stale.Conflicts);
		Assert.Equal(new PulledItem("a", 2, 2, false, "dHdv", "cGFzcw==", ""), current);
		Assert.Equal(2, (await client.PullOk()).Revision);
	}

	[Fact]
	public async Task A_new_item_pushed_with_a_nonzero_base_conflicts_and_an_existing_item_with_base_zero_conflicts()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushOk(new PushItem("a", 0));

		var result = await client.PushOk(new PushItem("a", 0), new PushItem("ghost", 5));

		Assert.Empty(result.Accepted);
		Assert.Equal(["a", "ghost"], result.Conflicts.Select(item => item.ItemId));
		Assert.Equal(1, result.Conflicts[0].Revision);
		Assert.Equal(0, result.Conflicts[1].Revision);
		Assert.True(result.Conflicts[1].Tombstone);
		Assert.Equal(1, (await client.PullOk()).Revision);
	}

	[Fact]
	public async Task Accepted_items_and_conflicts_mix_in_one_push_and_the_same_item_twice_conflicts_the_second()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushOk(new PushItem("a", 0));

		var result = await client.PushOk(new PushItem("b", 0), new PushItem("a", 9), new PushItem("c", 0), new PushItem("c", 0));

		Assert.Equal([new Accepted("b", 2), new Accepted("c", 3)], result.Accepted);
		Assert.Equal(["a", "c"], result.Conflicts.Select(item => item.ItemId));
		Assert.Equal(3, (await client.PullOk()).Revision);
	}

	[Fact]
	public async Task Pulling_pages_through_newer_items_in_revision_order()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushOk(Enumerable.Range(1, 5).Select(i => new PushItem($"i{i}", 0)).ToArray());

		var first = await client.PullOk(0, 2);
		var second = await client.PullOk(first.Items[^1].Revision, 2);
		var third = await client.PullOk(second.Items[^1].Revision, 2);

		Assert.Equal(["i1", "i2"], first.Items.Select(item => item.ItemId));
		Assert.True(first.More);
		Assert.Equal(["i3", "i4"], second.Items.Select(item => item.ItemId));
		Assert.True(second.More);
		Assert.Equal(["i5"], third.Items.Select(item => item.ItemId));
		Assert.False(third.More);
		Assert.Equal(5, third.Revision);
		Assert.Empty((await client.PullOk(5)).Items);
	}

	[Fact]
	public async Task The_page_size_defaults_to_and_is_capped_at_200()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		for (var batch = 0; batch < 3; batch++)
			await client.PushOk(Enumerable.Range(0, 100).Select(i => new PushItem($"b{batch}-{i}", 0)).ToArray());

		var byDefault = await client.PullOk();
		var capped = await client.PullOk(0, 5000);

		Assert.Equal(200, byDefault.Items.Count);
		Assert.True(byDefault.More);
		Assert.Equal(200, capped.Items.Count);
		Assert.True(capped.More);
	}

	[Theory]
	[InlineData("/changes?since=-1")]
	[InlineData("/changes?limit=0")]
	public async Task Bad_pull_parameters_get_400(string url)
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url)).StatusCode);
	}

	[Fact]
	public async Task More_than_100_items_in_a_push_gets_400_and_nothing_is_stored()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		var tooMany = await client.Push(Enumerable.Range(0, 101).Select(i => new PushItem($"i{i}", 0)).ToArray());
		var exactly = await client.Push(Enumerable.Range(0, 100).Select(i => new PushItem($"j{i}", 0)).ToArray());

		Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
		Assert.Equal(HttpStatusCode.OK, exactly.StatusCode);
		Assert.Equal(100, (await client.PullOk()).Revision);
	}

	[Fact]
	public async Task Invalid_items_reject_the_whole_push()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		var notBase64 = await client.Push(new PushItem("ok", 0), new PushItem("bad", 0, Summary: "***"));
		var noId = await client.Push(new PushItem("", 0));
		var negative = await client.Push(new PushItem("n", -1));
		var noItems = await client.PostAsJsonAsync("/changes", new { }, ServerFactory.JsonOpts);

		Assert.Equal(HttpStatusCode.BadRequest, notBase64.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, noItems.StatusCode);
		Assert.Equal(0, (await client.PullOk()).Revision);
	}

	[Fact]
	public async Task A_null_item_gets_400()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		using var content = new StringContent("{\"items\":[null]}", System.Text.Encoding.UTF8, "application/json");

		Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/changes", content)).StatusCode);
	}

	[Fact]
	public async Task A_blob_is_limited_to_256_KiB_decoded()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		var atLimit = await client.Push(new PushItem("fits", 0, Summary: SyncSupport.Base64OfSize(256 * 1024)));
		var over = await client.Push(new PushItem("big", 0, Password: SyncSupport.Base64OfSize(256 * 1024 + 1)));
		var keysOver = await client.PutAsJsonAsync("/keys", new { blob = SyncSupport.Base64OfSize(256 * 1024 + 1), baseVersion = 0 });

		Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, keysOver.StatusCode);
	}

	[Fact]
	public async Task A_body_over_4_MiB_gets_413()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		using var content = new StringContent(new string(' ', 4 * 1024 * 1024 + 1), System.Text.Encoding.UTF8, "application/json");

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/changes", content)).StatusCode);
	}

	[Fact]
	public async Task A_tombstone_round_trips_with_its_flag()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushOk(new PushItem("a", 0));

		await client.PushOk(new PushItem("a", 1, Summary: "", Password: "", Tombstone: true));

		var item = Assert.Single((await client.PullOk()).Items);
		Assert.True(item.Tombstone);
		Assert.Equal("", item.Summary);
	}

	[Fact]
	public async Task A_tombstone_older_than_30_days_is_purged_and_the_horizon_moves_past_it()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var login = await factory.SignIn(factory.CreateClient());
		var client = factory.SignedIn(login.Session);
		await client.PushOk(new PushItem("live", 0), new PushItem("gone", 0));
		await client.PushOk(new PushItem("gone", 2, Summary: "", Password: "", Tombstone: true));
		Assert.Equal(0, (await client.PullOk()).Horizon);

		clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
		client = factory.SignedIn(factory.SessionFor(login.UserId));

		var resync = await client.PullOk(0);
		Assert.Equal(3, resync.Horizon);
		Assert.Equal(["live"], resync.Items.Select(item => item.ItemId));
		Assert.Equal(3, resync.Revision);
		Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync("/changes?since=2")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/changes?since=3")).StatusCode);
	}

	[Fact]
	public async Task A_tombstone_younger_than_30_days_and_old_live_items_are_kept()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var login = await factory.SignIn(factory.CreateClient());
		var client = factory.SignedIn(login.Session);
		await client.PushOk(new PushItem("old", 0));
		clock.Advance(TimeSpan.FromDays(29));
		client = factory.SignedIn(factory.SessionFor(login.UserId));
		await client.PushOk(new PushItem("recent", 0, Summary: "", Tombstone: true));
		clock.Advance(TimeSpan.FromDays(20));
		client = factory.SignedIn(factory.SessionFor(login.UserId));

		var pulled = await client.PullOk(0);

		Assert.Equal(0, pulled.Horizon);
		Assert.Equal(["old", "recent"], pulled.Items.Select(item => item.ItemId));
	}

	[Fact]
	public async Task A_purge_only_touches_the_requesting_user()
	{
		var clock = new ManualClock(DateTimeOffset.UtcNow);
		using var factory = new ServerFactory(clock: clock);
		var loginA = await factory.SignIn(factory.CreateClient(), ServerFactory.AllowedSub);
		var loginB = await factory.SignIn(factory.CreateClient(), ServerFactory.OtherAllowedSub);
		await factory.SignedIn(loginA.Session).PushOk(new PushItem("t", 0, Tombstone: true));
		await factory.SignedIn(loginB.Session).PushOk(new PushItem("t", 0, Tombstone: true));
		clock.Advance(TimeSpan.FromDays(31));

		var a = await factory.SignedIn(factory.SessionFor(loginA.UserId)).PullOk(0);
		Assert.Empty(a.Items);
		Assert.Equal(1, a.Horizon);

		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = factory.DbPath }.ToString());
		connection.Open();
		using var count = connection.CreateCommand();
		count.CommandText = "SELECT COUNT(*) FROM items WHERE user_id = $user";
		count.Parameters.AddWithValue("$user", loginB.UserId);
		Assert.Equal(1L, count.ExecuteScalar());
	}

	[Fact]
	public async Task One_user_cannot_read_or_write_another_users_items()
	{
		using var factory = new ServerFactory();
		var alice = await factory.SignedInClient(ServerFactory.AllowedSub);
		var bob = await factory.SignedInClient(ServerFactory.OtherAllowedSub);
		await alice.PushOk(new PushItem("shared-id", 0, Summary: "YWxpY2U="));

		Assert.Empty((await bob.PullOk()).Items);
		Assert.Equal(0, (await bob.PullOk()).Revision);

		var bobPush = await bob.PushOk(new PushItem("shared-id", 0, Summary: "Ym9i"));
		Assert.Equal([new Accepted("shared-id", 1)], bobPush.Accepted);

		var bobStale = await bob.PushOk(new PushItem("shared-id", 7));
		Assert.Equal("Ym9i", Assert.Single(bobStale.Conflicts).Summary);
		Assert.Equal("YWxpY2U=", Assert.Single((await alice.PullOk()).Items).Summary);
	}
}
