using Microsoft.Data.Sqlite;

namespace Zing.Server;

// The column names are compile-time constants of the two slots below, never request data.
public sealed record BlobSlot(string BlobColumn, string VersionColumn)
{
	public static readonly BlobSlot Keys = new("keys_blob", "keys_version");
	public static readonly BlobSlot Manifest = new("manifest_blob", "manifest_version");
}

public sealed record BlobPut(string? Blob, long BaseVersion);

public sealed record BlobView(string? Blob, long Version);

public static class VersionedBlobs
{
	public static IResult Get(Db db, string userId, BlobSlot slot)
	{
		using var connection = db.Open();
		using var transaction = connection.BeginTransaction(deferred: false);
		var (blob, version) = Read(connection, transaction, userId, slot);
		if (version == 0) return Results.NotFound();
		return Results.Ok(new BlobView(SyncEndpoints.EncodeBlob(blob), version));
	}

	public static IResult Put(Db db, string userId, BlobSlot slot, BlobPut body)
	{
		if (string.IsNullOrEmpty(body.Blob)) throw new RequestRejectedException(StatusCodes.Status400BadRequest, "blob is required.");
		if (body.BaseVersion < 0) throw new RequestRejectedException(StatusCodes.Status400BadRequest, "baseVersion must not be negative.");
		var blob = SyncEndpoints.DecodeBlob(body.Blob, "blob");

		using var connection = db.Open();
		using var transaction = connection.BeginTransaction(deferred: false);
		using var update = Db.Command(connection, $"""
			UPDATE users SET {slot.BlobColumn} = $blob, {slot.VersionColumn} = {slot.VersionColumn} + 1
			WHERE user_id = $userId AND {slot.VersionColumn} = $base
			RETURNING {slot.VersionColumn}
			""", transaction);
		update.Parameters.AddWithValue("$blob", blob);
		update.Parameters.AddWithValue("$userId", userId);
		update.Parameters.AddWithValue("$base", body.BaseVersion);
		if (update.ExecuteScalar() is long version)
		{
			transaction.Commit();
			return Results.Ok(new { version });
		}

		var (current, currentVersion) = Read(connection, transaction, userId, slot);
		var currentBlob = currentVersion == 0 ? null : SyncEndpoints.EncodeBlob(current);
		return Results.Json(new BlobView(currentBlob, currentVersion), statusCode: StatusCodes.Status409Conflict);
	}

	private static (byte[] Blob, long Version) Read(SqliteConnection connection, SqliteTransaction transaction, string userId, BlobSlot slot)
	{
		using var select = Db.Command(connection, $"SELECT {slot.BlobColumn}, {slot.VersionColumn} FROM users WHERE user_id = $userId", transaction);
		select.Parameters.AddWithValue("$userId", userId);
		using var reader = select.ExecuteReader();
		if (!reader.Read()) throw new InvalidOperationException($"Session user {userId} has no user row.");
		return (reader.IsDBNull(0) ? [] : (byte[])reader[0], reader.GetInt64(1));
	}
}

public static class KeysEndpoints
{
	public static void MapKeys(this IEndpointRouteBuilder app)
	{
		app.MapGet("/keys", (HttpContext context, Db db) => VersionedBlobs.Get(db, SyncEndpoints.UserId(context), BlobSlot.Keys));
		app.MapPut("/keys", async (HttpContext context, Db db) =>
			VersionedBlobs.Put(db, SyncEndpoints.UserId(context), BlobSlot.Keys, await SyncEndpoints.ReadBody<BlobPut>(context)));
	}
}
