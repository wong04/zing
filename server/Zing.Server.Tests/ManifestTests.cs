using System.Net;
using System.Net.Http.Json;

namespace Zing.Server.Tests;

public class ManifestTests
{
	[Fact]
	public async Task A_stale_put_gets_409_with_the_current_manifest()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/manifest")).StatusCode);
		await client.PutAsJsonAsync("/manifest", new { blob = "b25l", baseVersion = 0 });

		var stale = await client.PutAsJsonAsync("/manifest", new { blob = "dHdv", baseVersion = 0 });

		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Equal(new BlobBody("b25l", 1), await stale.Content.ReadFromJsonAsync<BlobBody>(ServerFactory.JsonOpts));
		var next = await client.PutAsJsonAsync("/manifest", new { blob = "dHdv", baseVersion = 1 });
		Assert.Equal(2, (await next.Content.ReadFromJsonAsync<BlobBody>(ServerFactory.JsonOpts))!.Version);
		Assert.Equal(new BlobBody("dHdv", 2), await client.GetFromJsonAsync<BlobBody>("/manifest", ServerFactory.JsonOpts));
	}

	[Fact]
	public async Task Keys_and_manifest_are_stored_independently()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		await client.PutAsJsonAsync("/keys", new { blob = "a2V5", baseVersion = 0 });
		await client.PutAsJsonAsync("/manifest", new { blob = "bWFuaWY=", baseVersion = 0 });

		Assert.Equal("a2V5", (await client.GetFromJsonAsync<BlobBody>("/keys", ServerFactory.JsonOpts))!.Blob);
		Assert.Equal("bWFuaWY=", (await client.GetFromJsonAsync<BlobBody>("/manifest", ServerFactory.JsonOpts))!.Blob);
	}

	[Fact]
	public async Task The_manifest_is_per_user()
	{
		using var factory = new ServerFactory();
		var alice = await factory.SignedInClient(ServerFactory.AllowedSub);
		var bob = await factory.SignedInClient(ServerFactory.OtherAllowedSub);
		await alice.PutAsJsonAsync("/manifest", new { blob = "YWxpY2U=", baseVersion = 0 });

		Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/manifest")).StatusCode);
	}
}
