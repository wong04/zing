using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Zing.Server;

public enum RefreshOutcome { Rotated, Unknown, Reused }

public sealed class Db
{
	private readonly string connectionString;

	public Db(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
	}

	public SqliteConnection Open()
	{
		var connection = new SqliteConnection(connectionString);
		connection.Open();
		return connection;
	}

	public void Initialize()
	{
		using var connection = Open();
		Execute(connection, "PRAGMA journal_mode = WAL");
		Execute(connection, """
			CREATE TABLE IF NOT EXISTS users (
				sub TEXT PRIMARY KEY,
				user_id TEXT NOT NULL UNIQUE,
				keys_blob BLOB,
				keys_version INTEGER NOT NULL DEFAULT 0,
				profile_blob BLOB,
				manifest_blob BLOB,
				manifest_version INTEGER NOT NULL DEFAULT 0,
				revision INTEGER NOT NULL DEFAULT 0
			);
			CREATE TABLE IF NOT EXISTS items (
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
			CREATE INDEX IF NOT EXISTS items_by_revision ON items (user_id, revision);
			CREATE TABLE IF NOT EXISTS refresh_tokens (
				hash TEXT PRIMARY KEY,
				user_id TEXT NOT NULL,
				family TEXT NOT NULL,
				used INTEGER NOT NULL DEFAULT 0
			);
			CREATE INDEX IF NOT EXISTS refresh_tokens_by_user ON refresh_tokens (user_id);
			""");
	}

	public string FindOrCreateUser(string sub)
	{
		using var connection = Open();
		using var insert = Command(connection, "INSERT OR IGNORE INTO users (sub, user_id) VALUES ($sub, $userId)");
		insert.Parameters.AddWithValue("$sub", sub);
		insert.Parameters.AddWithValue("$userId", Tokens.RandomId());
		insert.ExecuteNonQuery();

		using var select = Command(connection, "SELECT user_id FROM users WHERE sub = $sub");
		select.Parameters.AddWithValue("$sub", sub);
		return (string)(select.ExecuteScalar() ?? throw new InvalidOperationException("User row missing right after insert."));
	}

	public void StoreRefresh(string hash, string userId, string family)
	{
		using var connection = Open();
		InsertRefresh(connection, null, hash, userId, family);
	}

	// A used token coming back means it leaked or was replayed, so every token of that user is revoked.
	public (RefreshOutcome Outcome, string? UserId) RotateRefresh(string oldHash, string newHash)
	{
		using var connection = Open();
		using var transaction = connection.BeginTransaction();

		using var select = Command(connection, "SELECT user_id, family, used FROM refresh_tokens WHERE hash = $hash", transaction);
		select.Parameters.AddWithValue("$hash", oldHash);
		string userId;
		string family;
		bool used;
		using (var reader = select.ExecuteReader())
		{
			if (!reader.Read()) return (RefreshOutcome.Unknown, null);
			userId = reader.GetString(0);
			family = reader.GetString(1);
			used = reader.GetInt64(2) != 0;
		}

		if (used)
		{
			using var revoke = Command(connection, "DELETE FROM refresh_tokens WHERE user_id = $userId", transaction);
			revoke.Parameters.AddWithValue("$userId", userId);
			revoke.ExecuteNonQuery();
			transaction.Commit();
			return (RefreshOutcome.Reused, userId);
		}

		using var markUsed = Command(connection, "UPDATE refresh_tokens SET used = 1 WHERE hash = $hash", transaction);
		markUsed.Parameters.AddWithValue("$hash", oldHash);
		markUsed.ExecuteNonQuery();
		InsertRefresh(connection, transaction, newHash, userId, family);
		transaction.Commit();
		return (RefreshOutcome.Rotated, userId);
	}

	private static void InsertRefresh(SqliteConnection connection, SqliteTransaction? transaction, string hash, string userId, string family)
	{
		using var insert = Command(connection, "INSERT INTO refresh_tokens (hash, user_id, family) VALUES ($hash, $userId, $family)", transaction);
		insert.Parameters.AddWithValue("$hash", hash);
		insert.Parameters.AddWithValue("$userId", userId);
		insert.Parameters.AddWithValue("$family", family);
		insert.ExecuteNonQuery();
	}

	private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
	{
		var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Transaction = transaction;
		return command;
	}

	private static void Execute(SqliteConnection connection, string sql)
	{
		using var command = Command(connection, sql);
		command.ExecuteNonQuery();
	}
}

public static class Tokens
{
	public static string RandomId() => Base64Url(RandomNumberGenerator.GetBytes(16));

	public static string NewRefreshToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

	public static string HashRefresh(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

	private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
