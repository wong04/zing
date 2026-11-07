using System.Net;
using System.Net.Http.Json;

namespace Zing.Server.Tests;

public class KeysTests
{
	[Fact]
	public async Task Nothing_stored_is_404_and_the_first_put_stores_version_1()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/keys")).StatusCode);
		var put = await client.PutAsJsonAsync("/keys", new { blob = "a2V5cw==", baseVersion = 0 });

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		Assert.Equal(1, (await put.Content.ReadFromJsonAsync<BlobBody>(ServerFactory.JsonOpts))!.Version);
		var got = (await client.GetFromJsonAsync<BlobBody>("/keys", ServerFactory.JsonOpts))!;
		Assert.Equal(new BlobBody("a2V5cw==", 1), got);
	}

	[Fact]
	public async Task A_stale_put_gets_409_with_the_current_blob_and_version()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PutAsJsonAsync("/keys", new { blob = "b25l", baseVersion = 0 });
		await client.PutAsJsonAsync("/keys", new { blob = "dHdv", baseVersion = 1 });

		var stale = await client.PutAsJsonAsync("/keys", new { blob = "bG9zZXI=", baseVersion = 1 });
		var ahead = await client.PutAsJsonAsync("/keys", new { blob = "bG9zZXI=", baseVersion = 9 });

		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Equal(new BlobBody("dHdv", 2), await stale.Content.ReadFromJsonAsync<BlobBody>(ServerFactory.JsonOpts));
		Assert.Equal(HttpStatusCode.Conflict, ahead.StatusCode);
		Assert.Equal(new BlobBody("dHdv", 2), (await client.GetFromJsonAsync<BlobBody>("/keys", ServerFactory.JsonOpts)));
	}

	[Fact]
	public async Task Putting_over_nothing_with_a_nonzero_base_conflicts_with_version_0()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		var response = await client.PutAsJsonAsync("/keys", new { blob = "b25l", baseVersion = 3 });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal(new BlobBody(null, 0), await response.Content.ReadFromJsonAsync<BlobBody>(ServerFactory.JsonOpts));
	}

	[Fact]
	public async Task A_put_needs_a_valid_blob()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/keys", new { baseVersion = 0 })).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/keys", new { blob = "***", baseVersion = 0 })).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/keys", new { blob = "b25l", baseVersion = -1 })).StatusCode);
	}

	[Fact]
	public async Task Keys_are_per_user()
	{
		using var factory = new ServerFactory();
		var alice = await factory.SignedInClient(ServerFactory.AllowedSub);
		var bob = await factory.SignedInClient(ServerFactory.OtherAllowedSub);
		await alice.PutAsJsonAsync("/keys", new { blob = "YWxpY2U=", baseVersion = 0 });

		Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/keys")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync("/keys", new { blob = "Ym9i", baseVersion = 0 })).StatusCode);
		Assert.Equal("YWxpY2U=", (await alice.GetFromJsonAsync<BlobBody>("/keys", ServerFactory.JsonOpts))!.Blob);
	}
}
