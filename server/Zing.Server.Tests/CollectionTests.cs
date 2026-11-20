using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace Zing.Server.Tests;

/// <summary>
/// Library items live in their own collection. A v1 client only knows /changes and treats every item it pulls as a login, so library
/// items must never reach /changes, whatever the revisions, and a /changes write must never touch one.
/// </summary>
public class CollectionTests
{
	[Fact]
	public async Task A_v1_client_never_pulls_library_items()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushOk(new PushItem("login-1", 0));
		await client.PushLibraryOk(new PushItem("library-00", 0, Summary: "bGli"));
		await client.PushOk(new PushItem("login-2", 0));
		await client.PushLibraryOk(new PushItem("library-01", 0, Summary: "bGli"));

		var full = await client.PullOk();
		var delta = await client.PullOk(since: 1);
		var paged = await client.PullOk(limit: 1);

		Assert.Equal(["login-1", "login-2"], full.Items.Select(i => i.ItemId));
		Assert.Equal(["login-2"], delta.Items.Select(i => i.ItemId));
		Assert.Equal(["login-1"], paged.Items.Select(i => i.ItemId));
		Assert.Equal(4, full.Revision);
	}

	[Fact]
	public async Task The_library_pull_holds_only_library_items()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushOk(new PushItem("login-1", 0));
		await client.PushLibraryOk(new PushItem("library-00", 0, Summary: "bGli"));

		var pulled = await client.PullLibraryOk();

		Assert.Equal(["library-00"], pulled.Items.Select(i => i.ItemId));
		Assert.Equal(2, pulled.Revision);
	}

	[Fact]
	public async Task An_item_id_in_one_collection_cannot_be_written_from_the_other()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushLibraryOk(new PushItem("shared-id", 0, Summary: "bGli"));
		await client.PushOk(new PushItem("login-id", 0));

		var asLogin = await client.Push(new PushItem("shared-id", 0));
		var overLogin = await client.Push(new PushItem("shared-id", 1));
		var asLibrary = await client.PushLibrary(new PushItem("login-id", 2));

		Assert.Equal(HttpStatusCode.BadRequest, asLogin.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, overLogin.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, asLibrary.StatusCode);
		Assert.Equal(["login-id"], (await client.PullOk()).Items.Select(i => i.ItemId));
		var library = Assert.Single((await client.PullLibraryOk()).Items);
		Assert.Equal(("shared-id", "bGli"), (library.ItemId, library.Summary));
	}

	[Fact]
	public async Task A_library_conflict_carries_the_library_copy()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PushLibraryOk(new PushItem("library-00", 0, Summary: "b25l"));
		await client.PushLibraryOk(new PushItem("library-00", 1, Summary: "dHdv", Version: 2));

		var stale = await client.PushLibraryOk(new PushItem("library-00", 1, Summary: "bG9zZXI="));

		var current = Assert.Single(stale.Conflicts);
		Assert.Equal(("library-00", 2, "dHdv"), (current.ItemId, current.Version, current.Summary));
	}

	[Fact]
	public async Task A_database_from_before_collections_is_migrated_with_every_item_a_login()
	{
		using var factory = new ServerFactory();
		WriteV1Database(factory.DbPath);

		var client = await factory.SignedInClient();

		var logins = await client.PullOk();
		Assert.Equal(["old-login"], logins.Items.Select(i => i.ItemId));
		Assert.Empty((await client.PullLibraryOk()).Items);
		Assert.Equal(5, logins.Revision);
		var next = await client.PushLibraryOk(new PushItem("library-00", 0, Summary: "bGli"));
		Assert.Equal(6, Assert.Single(next.Accepted).Revision);
		Assert.Equal(["old-login"], (await client.PullOk()).Items.Select(i => i.ItemId));
	}

	[Fact]
	public async Task Migrating_twice_changes_nothing()
	{
		using var factory = new ServerFactory();
		WriteV1Database(factory.DbPath);
		var client = await factory.SignedInClient();
		await client.PushLibraryOk(new PushItem("library-00", 0, Summary: "bGli"));

		new Db(factory.DbPath, TimeProvider.System).Initialize();

		Assert.Equal(["old-login"], (await client.PullOk()).Items.Select(i => i.ItemId));
		Assert.Equal(["library-00"], (await client.PullLibraryOk()).Items.Select(i => i.ItemId));
	}

	[Theory]
	[InlineData("/settings")]
	[InlineData("/library/manifest")]
	public async Task A_new_slot_answers_version_0_before_any_write_and_conflicts_on_a_stale_one(string path)
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();

		Assert.Equal(new BlobBody(null, 0), await client.GetFromJsonAsync<BlobBody>(path, ServerFactory.JsonOpts));
		(await client.PutAsJsonAsync(path, new { blob = "b25l", baseVersion = 0 })).EnsureSuccessStatusCode();
		var stale = await client.PutAsJsonAsync(path, new { blob = "dHdv", baseVersion = 0 });

		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Equal(new BlobBody("b25l", 1), await client.GetFromJsonAsync<BlobBody>(path, ServerFactory.JsonOpts));
	}

	[Fact]
	public async Task Settings_library_manifest_and_manifest_are_separate_slots()
	{
		using var factory = new ServerFactory();
		var client = await factory.SignedInClient();
		await client.PutAsJsonAsync("/manifest", new { blob = "bWFuaWY=", baseVersion = 0 });
		await client.PutAsJsonAsync("/library/manifest", new { blob = "bGlibWFu", baseVersion = 0 });
		await client.PutAsJsonAsync("/settings", new { blob = "c2V0", baseVersion = 0 });

		Assert.Equal("bWFuaWY=", (await client.GetFromJsonAsync<BlobBody>("/manifest", ServerFactory.JsonOpts))!.Blob);
		Assert.Equal("bGlibWFu", (await client.GetFromJsonAsync<BlobBody>("/library/manifest", ServerFactory.JsonOpts))!.Blob);
		Assert.Equal("c2V0", (await client.GetFromJsonAsync<BlobBody>("/settings", ServerFactory.JsonOpts))!.Blob);
	}

	[Fact]
	public async Task Library_and_settings_need_a_session_and_are_per_user()
	{
		using var factory = new ServerFactory();
		var anonymous = factory.CreateClient();
		Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/library/changes")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/settings")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/library/manifest")).StatusCode);

		var alice = await factory.SignedInClient(ServerFactory.AllowedSub);
		var bob = await factory.SignedInClient(ServerFactory.OtherAllowedSub);
		await alice.PushLibraryOk(new PushItem("library-00", 0, Summary: "bGli"));
		await alice.PutAsJsonAsync("/settings", new { blob = "c2V0", baseVersion = 0 });

		Assert.Empty((await bob.PullLibraryOk()).Items);
		Assert.Equal(new BlobBody(null, 0), await bob.GetFromJsonAsync<BlobBody>("/settings", ServerFactory.JsonOpts));
	}

	/// <summary>The schema the first deploy made: no collection column and no library or settings slots, holding one login at revision 5.</summary>
	private static void WriteV1Database(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = $"""
			CREATE TABLE users (
				sub TEXT PRIMARY KEY,
				user_id TEXT NOT NULL UNIQUE,
				keys_blob BLOB,
				keys_version INTEGER NOT NULL DEFAULT 0,
				profile_blob BLOB,
				manifest_blob BLOB,
				manifest_version INTEGER NOT NULL DEFAULT 0,
				revision INTEGER NOT NULL DEFAULT 0,
				horizon INTEGER NOT NULL DEFAULT 0
			);
			CREATE TABLE items (
				user_id TEXT NOT NULL,
				item_id TEXT NOT NULL,
				version INTEGER NOT NULL,
				revision INTEGER NOT NULL,
				tombstone INTEGER NOT NULL DEFAULT 0,
				summary BLOB,
				password BLOB,
				history BLOB,
				stored_at INTEGER NOT NULL,
				PRIMARY KEY (user_id, item_id)
			);
			CREATE INDEX items_by_revision ON items (user_id, revision);
			INSERT INTO users (sub, user_id, revision) VALUES ('{ServerFactory.AllowedSub}', 'old-user', 5);
			INSERT INTO items (user_id, item_id, version, revision, summary, password, history, stored_at)
				VALUES ('old-user', 'old-login', 3, 5, x'01', x'02', x'', 0);
			""";
		command.ExecuteNonQuery();
		connection.Close();
		SqliteConnection.ClearAllPools();
	}
}
