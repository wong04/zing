using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Zing.Server;

public enum RefreshOutcome { Rotated, Unknown, Reused, Forbidden }

public sealed class Db
{
	public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(90);

	private readonly string connectionString;
	private readonly TimeProvider clock;

	public Db(string path, TimeProvider clock)
	{
		this.clock = clock;
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
				revision INTEGER NOT NULL DEFAULT 0,
				horizon INTEGER NOT NULL DEFAULT 0
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
				created_at INTEGER NOT NULL,
				used INTEGER NOT NULL DEFAULT 0
			);
			CREATE INDEX IF NOT EXISTS refresh_tokens_by_user ON refresh_tokens (user_id);
			""");
		AddHorizonColumn(connection);
	}

	// Databases created by Task 8 predate the column, and CREATE TABLE IF NOT EXISTS leaves them alone.
	private static void AddHorizonColumn(SqliteConnection connection)
	{
		using var info = Command(connection, "SELECT COUNT(*) FROM pragma_table_info('users') WHERE name = 'horizon'");
		if ((long)info.ExecuteScalar()! != 0) return;
		Execute(connection, "ALTER TABLE users ADD COLUMN horizon INTEGER NOT NULL DEFAULT 0");
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

	public void StoreRefresh(string hash, string userId)
	{
		using var connection = Open();
		InsertRefresh(connection, null, hash, userId);
	}

	// Each use re-issues the token, so the 90 days slide. A used token coming back means it leaked or
	// was replayed, so every token of that user is revoked.
	public (RefreshOutcome Outcome, string? UserId) RotateRefresh(string oldHash, string newHash, IReadOnlySet<string> allowedSubs)
	{
		using var connection = Open();
		Prune(connection);
		using var transaction = connection.BeginTransaction();

		using var select = Command(connection, """
			SELECT r.user_id, r.used, u.sub FROM refresh_tokens r JOIN users u ON u.user_id = r.user_id WHERE r.hash = $hash
			""", transaction);
		select.Parameters.AddWithValue("$hash", oldHash);
		string userId;
		string sub;
		bool used;
		using (var reader = select.ExecuteReader())
		{
			if (!reader.Read()) return (RefreshOutcome.Unknown, null);
			userId = reader.GetString(0);
			used = reader.GetInt64(1) != 0;
			sub = reader.GetString(2);
		}

		if (used) return Revoke(connection, transaction, userId, RefreshOutcome.Reused);
		if (!allowedSubs.Contains(sub)) return Revoke(connection, transaction, userId, RefreshOutcome.Forbidden);

		using var markUsed = Command(connection, "UPDATE refresh_tokens SET used = 1 WHERE hash = $hash AND used = 0", transaction);
		markUsed.Parameters.AddWithValue("$hash", oldHash);
		if (markUsed.ExecuteNonQuery() == 0) return Revoke(connection, transaction, userId, RefreshOutcome.Reused);

		InsertRefresh(connection, transaction, newHash, userId);
		transaction.Commit();
		return (RefreshOutcome.Rotated, userId);
	}

	private void Prune(SqliteConnection connection)
	{
		using var delete = Command(connection, "DELETE FROM refresh_tokens WHERE created_at < $cutoff");
		delete.Parameters.AddWithValue("$cutoff", (clock.GetUtcNow() - RefreshLifetime).ToUnixTimeSeconds());
		delete.ExecuteNonQuery();
	}

	private static (RefreshOutcome Outcome, string? UserId) Revoke(
		SqliteConnection connection, SqliteTransaction transaction, string userId, RefreshOutcome outcome)
	{
		using var revoke = Command(connection, "DELETE FROM refresh_tokens WHERE user_id = $userId", transaction);
		revoke.Parameters.AddWithValue("$userId", userId);
		revoke.ExecuteNonQuery();
		transaction.Commit();
		return (outcome, userId);
	}

	private void InsertRefresh(SqliteConnection connection, SqliteTransaction? transaction, string hash, string userId)
	{
		using var insert = Command(connection, "INSERT INTO refresh_tokens (hash, user_id, created_at) VALUES ($hash, $userId, $createdAt)", transaction);
		insert.Parameters.AddWithValue("$hash", hash);
		insert.Parameters.AddWithValue("$userId", userId);
		insert.Parameters.AddWithValue("$createdAt", clock.GetUtcNow().ToUnixTimeSeconds());
		insert.ExecuteNonQuery();
	}

	internal static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
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
