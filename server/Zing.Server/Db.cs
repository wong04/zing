using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Zing.Server;

public enum RefreshOutcome { Rotated, Unknown, Reused, Forbidden }

public sealed class Db
{
	public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(90);

	/// <summary>A used token coming back this soon after its first use, while its successor is unused, is a client retrying a lost response (Ruling 50).</summary>
	public static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(2);

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
		UpgradeRefreshTokens(connection);
	}

	// Columns added after the first deploy (Rulings 49 to 51), in one transaction (SQLite's DDL is transactional), so a crash leaves
	// the table as it was or fully upgraded. A token from before them is its own family. Every step can run again.
	private static void UpgradeRefreshTokens(SqliteConnection connection)
	{
		using var transaction = connection.BeginTransaction();
		var columns = new HashSet<string>(StringComparer.Ordinal);
		using (var info = Command(connection, "PRAGMA table_info(refresh_tokens)", transaction))
		using (var reader = info.ExecuteReader())
		{
			while (reader.Read()) columns.Add(reader.GetString(1));
		}
		if (!columns.Contains("family")) Execute(connection, "ALTER TABLE refresh_tokens ADD COLUMN family TEXT", transaction);
		if (!columns.Contains("used_at")) Execute(connection, "ALTER TABLE refresh_tokens ADD COLUMN used_at INTEGER", transaction);
		if (!columns.Contains("successor")) Execute(connection, "ALTER TABLE refresh_tokens ADD COLUMN successor TEXT", transaction);
		Execute(connection, "UPDATE refresh_tokens SET family = hash WHERE family IS NULL", transaction);
		Execute(connection, "CREATE INDEX IF NOT EXISTS refresh_tokens_by_family ON refresh_tokens (family)", transaction);
		transaction.Commit();
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

	public bool UserExists(string userId)
	{
		using var connection = Open();
		using var select = Command(connection, "SELECT 1 FROM users WHERE user_id = $userId");
		select.Parameters.AddWithValue("$userId", userId);
		return select.ExecuteScalar() is not null;
	}

	/// <summary>
	/// Everything the server holds for <paramref name="userId"/>: its keys, manifest and items, and every sign-in, in one transaction.
	/// Only when that user is <paramref name="sub"/>'s; a user that's gone has nothing left to delete. A later sign-in with the same
	/// Google account starts a new user.
	/// </summary>
	public DeleteOutcome DeleteUser(string sub, string userId)
	{
		using var connection = Open();
		using var transaction = connection.BeginTransaction(deferred: false);
		using var select = Command(connection, "SELECT sub FROM users WHERE user_id = $userId", transaction);
		select.Parameters.AddWithValue("$userId", userId);
		if (select.ExecuteScalar() is not string owner) return DeleteOutcome.NoSuchUser;
		if (owner != sub) return DeleteOutcome.OtherUser;
		foreach (var sql in new[] { "DELETE FROM items WHERE user_id = $userId", "DELETE FROM refresh_tokens WHERE user_id = $userId", "DELETE FROM users WHERE user_id = $userId" })
		{
			using var delete = Command(connection, sql, transaction);
			delete.Parameters.AddWithValue("$userId", userId);
			delete.ExecuteNonQuery();
		}
		transaction.Commit();
		return DeleteOutcome.Deleted;
	}

	/// <summary>A sign-in's first refresh token, starting a new family: the chain of tokens that rotating it makes.</summary>
	public void StoreRefresh(string hash, string userId)
	{
		using var connection = Open();
		InsertRefresh(connection, null, hash, userId, Tokens.RandomId());
	}

	// Each use re-issues the token, so the 90 days slide. A used token coming back means it leaked or was replayed, so every
	// token of that user is revoked; except a repeat within the retry window while its successor is still unused, which is a
	// client that never got the answer (Ruling 50): it gets a new pair, and the successor it never saw is revoked.
	public (RefreshOutcome Outcome, string? UserId) RotateRefresh(string oldHash, string newHash, IReadOnlySet<string> allowedSubs)
	{
		using var connection = Open();
		Prune(connection);
		using var transaction = connection.BeginTransaction();

		using var select = Command(connection, """
			SELECT r.user_id, r.used, u.sub, COALESCE(r.family, r.hash), r.used_at, r.successor
			FROM refresh_tokens r JOIN users u ON u.user_id = r.user_id WHERE r.hash = $hash
			""", transaction);
		select.Parameters.AddWithValue("$hash", oldHash);
		string userId;
		string sub;
		bool used;
		string family;
		long? usedAt;
		string? successor;
		using (var reader = select.ExecuteReader())
		{
			if (!reader.Read()) return (RefreshOutcome.Unknown, null);
			userId = reader.GetString(0);
			used = reader.GetInt64(1) != 0;
			sub = reader.GetString(2);
			family = reader.GetString(3);
			usedAt = reader.IsDBNull(4) ? null : reader.GetInt64(4);
			successor = reader.IsDBNull(5) ? null : reader.GetString(5);
		}

		var now = clock.GetUtcNow().ToUnixTimeSeconds();
		if (used && !IsRetry(connection, transaction, now, usedAt, successor)) return Revoke(connection, transaction, userId, RefreshOutcome.Reused);
		if (!allowedSubs.Contains(sub)) return Revoke(connection, transaction, userId, RefreshOutcome.Forbidden);

		if (used)
		{
			// The successor the client never saw stays as a spent marker with no first-use time (Ruling 51): whoever presents it later
			// is reusing it, which revokes everything. So a thief who wins the retry window loses the sign-in as soon as the PC comes back.
			using var dropSuccessor = Command(connection, "UPDATE refresh_tokens SET used = 1, used_at = NULL, successor = NULL WHERE hash = $successor", transaction);
			dropSuccessor.Parameters.AddWithValue("$successor", successor);
			dropSuccessor.ExecuteNonQuery();
			using var repoint = Command(connection, "UPDATE refresh_tokens SET successor = $next WHERE hash = $hash", transaction);
			repoint.Parameters.AddWithValue("$next", newHash);
			repoint.Parameters.AddWithValue("$hash", oldHash);
			repoint.ExecuteNonQuery();
		}
		else
		{
			using var markUsed = Command(connection, "UPDATE refresh_tokens SET used = 1, used_at = $now, successor = $next WHERE hash = $hash AND used = 0", transaction);
			markUsed.Parameters.AddWithValue("$hash", oldHash);
			markUsed.Parameters.AddWithValue("$now", now);
			markUsed.Parameters.AddWithValue("$next", newHash);
			if (markUsed.ExecuteNonQuery() == 0) return Revoke(connection, transaction, userId, RefreshOutcome.Reused);
		}

		InsertRefresh(connection, transaction, newHash, userId, family);
		transaction.Commit();
		return (RefreshOutcome.Rotated, userId);
	}

	/// <summary>Within the window counted from the first use (it never slides), and the successor was never used.</summary>
	private static bool IsRetry(SqliteConnection connection, SqliteTransaction transaction, long now, long? usedAt, string? successor)
	{
		if (usedAt is null || successor is null) return false;
		if (now - usedAt.Value > (long)RetryWindow.TotalSeconds) return false;
		using var check = Command(connection, "SELECT used FROM refresh_tokens WHERE hash = $successor", transaction);
		check.Parameters.AddWithValue("$successor", successor);
		return check.ExecuteScalar() is long successorUsed && successorUsed == 0;
	}

	/// <summary>Sign-out (Ruling 49): every token of the sign-in this one belongs to goes. An unknown token revokes nothing.</summary>
	public void RevokeFamily(string hash)
	{
		using var connection = Open();
		using var revoke = Command(connection, """
			DELETE FROM refresh_tokens WHERE COALESCE(family, hash) = (SELECT COALESCE(family, hash) FROM refresh_tokens WHERE hash = $hash)
			""");
		revoke.Parameters.AddWithValue("$hash", hash);
		revoke.ExecuteNonQuery();
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

	private void InsertRefresh(SqliteConnection connection, SqliteTransaction? transaction, string hash, string userId, string family)
	{
		using var insert = Command(connection, "INSERT INTO refresh_tokens (hash, user_id, created_at, family) VALUES ($hash, $userId, $createdAt, $family)", transaction);
		insert.Parameters.AddWithValue("$hash", hash);
		insert.Parameters.AddWithValue("$userId", userId);
		insert.Parameters.AddWithValue("$family", family);
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

	private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
	{
		using var command = Command(connection, sql, transaction);
		command.ExecuteNonQuery();
	}
}

public enum DeleteOutcome
{
	Deleted,
	NoSuchUser,
	OtherUser,
}

public static class Tokens
{
	public static string RandomId() => Base64Url(RandomNumberGenerator.GetBytes(16));

	public static string NewRefreshToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

	public static string HashRefresh(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

	private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
