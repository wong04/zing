using Microsoft.Data.Sqlite;

namespace Zing.Server;

public sealed record ItemPush(string? ItemId, int Version, long BaseRevision, bool Tombstone, string? Summary, string? Password, string? History);

public sealed record ChangesPush(List<ItemPush>? Items);

public sealed record ItemView(string ItemId, int Version, long Revision, bool Tombstone, string Summary, string Password, string History);

public sealed record AcceptedItem(string ItemId, long Revision);

public static class ChangesEndpoints
{
	private const string ItemColumns = "item_id, version, revision, tombstone, summary, password, history";

	private sealed record NewItem(string ItemId, int Version, long BaseRevision, bool Tombstone, byte[] Summary, byte[] Password, byte[] History);

	public static void MapChanges(this IEndpointRouteBuilder app)
	{
		app.MapGet("/changes", (HttpContext context, Db db, TimeProvider clock, long? since, int? limit) =>
			Pull(db, clock, SyncEndpoints.UserId(context), since ?? 0, limit ?? SyncLimits.MaxPageSize));
		app.MapPost("/changes", async (HttpContext context, Db db, TimeProvider clock) =>
			Push(db, clock, SyncEndpoints.UserId(context), await SyncEndpoints.ReadBody<ChangesPush>(context)));
	}

	private static IResult Pull(Db db, TimeProvider clock, string userId, long since, int limit)
	{
		if (since < 0) throw new RequestRejectedException(StatusCodes.Status400BadRequest, "since must not be negative.");
		if (limit < 1) throw new RequestRejectedException(StatusCodes.Status400BadRequest, "limit must be at least 1.");
		limit = Math.Min(limit, SyncLimits.MaxPageSize);

		using var connection = db.Open();
		using var transaction = connection.BeginTransaction(deferred: false);
		PurgeTombstones(connection, transaction, clock, userId);

		using var user = Db.Command(connection, "SELECT revision, horizon FROM users WHERE user_id = $userId", transaction);
		user.Parameters.AddWithValue("$userId", userId);
		long revision;
		long horizon;
		using (var reader = user.ExecuteReader())
		{
			if (!reader.Read()) throw new InvalidOperationException($"Session user {userId} has no user row.");
			revision = reader.GetInt64(0);
			horizon = reader.GetInt64(1);
		}
		// since=0 is the full resync the 410 tells clients to do, so it is always allowed.
		if (since > 0 && since < horizon) return Results.StatusCode(StatusCodes.Status410Gone);

		using var select = Db.Command(connection, $"""
			SELECT {ItemColumns} FROM items WHERE user_id = $userId AND revision > $since ORDER BY revision LIMIT $limit
			""", transaction);
		select.Parameters.AddWithValue("$userId", userId);
		select.Parameters.AddWithValue("$since", since);
		select.Parameters.AddWithValue("$limit", limit + 1);
		var items = new List<ItemView>();
		using (var reader = select.ExecuteReader())
		{
			while (reader.Read()) items.Add(ReadItem(reader));
		}

		var more = items.Count > limit;
		if (more) items.RemoveAt(items.Count - 1);
		return Results.Ok(new { items, revision, horizon, more });
	}

	// The horizon only moves forward, so a client that has seen everything up to it is never told to resync.
	private static void PurgeTombstones(SqliteConnection connection, SqliteTransaction transaction, TimeProvider clock, string userId)
	{
		var cutoff = (clock.GetUtcNow() - SyncLimits.TombstoneLifetime).ToUnixTimeSeconds();
		const string expired = "user_id = $userId AND tombstone = 1 AND stored_at < $cutoff";

		using var highest = Db.Command(connection, $"SELECT MAX(revision) FROM items WHERE {expired}", transaction);
		highest.Parameters.AddWithValue("$userId", userId);
		highest.Parameters.AddWithValue("$cutoff", cutoff);
		if (highest.ExecuteScalar() is not long purgedRevision) return;

		using var delete = Db.Command(connection, $"DELETE FROM items WHERE {expired}", transaction);
		delete.Parameters.AddWithValue("$userId", userId);
		delete.Parameters.AddWithValue("$cutoff", cutoff);
		delete.ExecuteNonQuery();

		using var raise = Db.Command(connection, "UPDATE users SET horizon = MAX(horizon, $horizon) WHERE user_id = $userId", transaction);
		raise.Parameters.AddWithValue("$horizon", purgedRevision);
		raise.Parameters.AddWithValue("$userId", userId);
		raise.ExecuteNonQuery();
	}

	private static IResult Push(Db db, TimeProvider clock, string userId, ChangesPush body)
	{
		if (body.Items is null) throw new RequestRejectedException(StatusCodes.Status400BadRequest, "items is required.");
		if (body.Items.Count > SyncLimits.MaxItemsPerPush)
			throw new RequestRejectedException(StatusCodes.Status400BadRequest, $"At most {SyncLimits.MaxItemsPerPush} items per request.");
		var pushed = body.Items.Select(Validate).ToList();
		var storedAt = clock.GetUtcNow().ToUnixTimeSeconds();

		using var connection = db.Open();
		using var transaction = connection.BeginTransaction(deferred: false);
		var start = CurrentRevision(connection, transaction, userId);
		var revision = start;
		var accepted = new List<AcceptedItem>();
		var conflicts = new List<ItemView>();
		foreach (var item in pushed)
		{
			if (TryWrite(connection, transaction, userId, item, revision + 1, storedAt))
			{
				revision++;
				accepted.Add(new AcceptedItem(item.ItemId, revision));
				continue;
			}
			conflicts.Add(Current(connection, transaction, userId, item.ItemId));
		}

		if (revision > start) SetRevision(connection, transaction, userId, start, revision);
		transaction.Commit();
		return Results.Ok(new { accepted, conflicts });
	}

	private static NewItem Validate(ItemPush item)
	{
		if (string.IsNullOrEmpty(item.ItemId) || item.ItemId.Length > SyncLimits.MaxItemIdLength)
			throw new RequestRejectedException(StatusCodes.Status400BadRequest, "itemId is required and at most 128 characters.");
		if (item.Version < 0) throw new RequestRejectedException(StatusCodes.Status400BadRequest, "version must not be negative.");
		if (item.BaseRevision < 0) throw new RequestRejectedException(StatusCodes.Status400BadRequest, "baseRevision must not be negative.");
		return new NewItem(
			item.ItemId, item.Version, item.BaseRevision, item.Tombstone,
			SyncEndpoints.DecodeBlob(item.Summary, "summary"),
			SyncEndpoints.DecodeBlob(item.Password, "password"),
			SyncEndpoints.DecodeBlob(item.History, "history"));
	}

	// A new item is an insert that must not collide; an existing one is an update that must still be at the revision the client saw.
	private static bool TryWrite(SqliteConnection connection, SqliteTransaction transaction, string userId, NewItem item, long revision, long storedAt)
	{
		var sql = item.BaseRevision == 0
			? """
				INSERT INTO items (user_id, item_id, version, revision, tombstone, summary, password, history, stored_at)
				VALUES ($userId, $itemId, $version, $revision, $tombstone, $summary, $password, $history, $storedAt)
				ON CONFLICT (user_id, item_id) DO NOTHING
				"""
			: """
				UPDATE items SET version = $version, revision = $revision, tombstone = $tombstone,
					summary = $summary, password = $password, history = $history, stored_at = $storedAt
				WHERE user_id = $userId AND item_id = $itemId AND revision = $base
				""";
		using var write = Db.Command(connection, sql, transaction);
		write.Parameters.AddWithValue("$userId", userId);
		write.Parameters.AddWithValue("$itemId", item.ItemId);
		write.Parameters.AddWithValue("$version", item.Version);
		write.Parameters.AddWithValue("$revision", revision);
		write.Parameters.AddWithValue("$tombstone", item.Tombstone ? 1 : 0);
		write.Parameters.AddWithValue("$summary", item.Summary);
		write.Parameters.AddWithValue("$password", item.Password);
		write.Parameters.AddWithValue("$history", item.History);
		write.Parameters.AddWithValue("$storedAt", storedAt);
		if (item.BaseRevision != 0) write.Parameters.AddWithValue("$base", item.BaseRevision);
		return write.ExecuteNonQuery() == 1;
	}

	// An item that no longer exists was purged as a tombstone, so it is reported as one at revision 0.
	private static ItemView Current(SqliteConnection connection, SqliteTransaction transaction, string userId, string itemId)
	{
		using var select = Db.Command(connection, $"SELECT {ItemColumns} FROM items WHERE user_id = $userId AND item_id = $itemId", transaction);
		select.Parameters.AddWithValue("$userId", userId);
		select.Parameters.AddWithValue("$itemId", itemId);
		using var reader = select.ExecuteReader();
		return reader.Read() ? ReadItem(reader) : new ItemView(itemId, 0, 0, true, "", "", "");
	}

	private static long CurrentRevision(SqliteConnection connection, SqliteTransaction transaction, string userId)
	{
		using var select = Db.Command(connection, "SELECT revision FROM users WHERE user_id = $userId", transaction);
		select.Parameters.AddWithValue("$userId", userId);
		return select.ExecuteScalar() as long? ?? throw new InvalidOperationException($"Session user {userId} has no user row.");
	}

	private static void SetRevision(SqliteConnection connection, SqliteTransaction transaction, string userId, long expected, long revision)
	{
		using var update = Db.Command(connection, "UPDATE users SET revision = $revision WHERE user_id = $userId AND revision = $expected", transaction);
		update.Parameters.AddWithValue("$revision", revision);
		update.Parameters.AddWithValue("$userId", userId);
		update.Parameters.AddWithValue("$expected", expected);
		if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException($"User {userId} revision changed during a write transaction.");
	}

	private static ItemView ReadItem(SqliteDataReader reader) => new(
		reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3) != 0,
		Blob(reader, 4), Blob(reader, 5), Blob(reader, 6));

	private static string Blob(SqliteDataReader reader, int column) =>
		reader.IsDBNull(column) ? "" : SyncEndpoints.EncodeBlob((byte[])reader[column]);
}
