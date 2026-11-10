namespace Zing.Core.Tests.Sync;

/// <summary>The test server itself: test classes run in parallel, each with its own server, and one's teardown must not reach another's.</summary>
public sealed class LocalSyncServerTests
{
	[Fact]
	public async Task DisposingOneServerLeavesAnothersDatabaseOpen()
	{
		using var kept = new LocalSyncServer();
		var gone = new LocalSyncServer();
		await kept.MintIdTokenAsync();
		await gone.MintIdTokenAsync();
		Assert.True(File.Exists(kept.DbPath + "-wal"));

		gone.Dispose();

		// SQLite deletes the write-ahead log when a database's last connection closes, taking the database's exclusive lock to
		// checkpoint it first: a Backup() in the kept server's test, which has no busy timeout, fails with "database is locked".
		Assert.True(File.Exists(kept.DbPath + "-wal"));
	}
}
