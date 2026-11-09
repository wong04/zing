using Zing.Core.Sync;
using Zing.Core.Tests.Vault;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>The sync account lives in the vault's sync.json beside the queue, its refresh token sealed (DPAPI in Zing, a fake here).</summary>
public sealed class SyncAccountFileTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-syncaccount-").FullName;

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private VaultStore Open() => new(_dir, new FakeCipher(), new FakeSealer(), new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

	private string SyncFile => Path.Combine(_dir, VaultStore.SyncFileName);

	private static readonly SyncAccount Account = new("user-1", "ada@example.test", "refresh-token-plain");

	[Fact]
	public void TheAccountIsKeptAcrossARestartWithItsRefreshTokenSealed()
	{
		using (var vault = Open()) vault.SaveSyncAccount(Account);

		Assert.DoesNotContain(Account.Refresh, File.ReadAllText(SyncFile));
		using var reopened = Open();
		Assert.Equal(Account, reopened.SyncAccount());
	}

	[Fact]
	public async Task ASavedLoginKeepsTheAccountAndTheAccountKeepsTheQueue()
	{
		using var vault = Open();
		vault.SaveSyncAccount(Account);
		await vault.SaveAsync(new NewLogin("https://example.com", "ada", "pw", "", ""));
		vault.SaveSyncAccount(Account with { Refresh = "rotated" });

		using var reopened = Open();
		Assert.Equal("rotated", reopened.SyncAccount()!.Refresh);
		Assert.Single(reopened.Queued);
	}

	[Fact]
	public async Task ForgettingTheAccountLeavesTheQueue()
	{
		using var vault = Open();
		await vault.SaveAsync(new NewLogin("https://example.com", "ada", "pw", "", ""));
		vault.SaveSyncAccount(Account);

		vault.ForgetSyncAccount();

		using var reopened = Open();
		Assert.Null(reopened.SyncAccount());
		Assert.Single(reopened.Queued);
		Assert.DoesNotContain("ada@example.test", File.ReadAllText(SyncFile));
	}

	[Fact]
	public void ASyncFileFromBeforeSyncHasNoAccount()
	{
		File.WriteAllText(SyncFile, """{"format":1,"queued":[]}""");

		using var vault = Open();

		Assert.Null(vault.SyncAccount());
	}

	[Fact]
	public void ARefreshTokenThatNoLongerUnsealsIsAnErrorNotAnEmptyAccount()
	{
		using (var vault = Open()) vault.SaveSyncAccount(Account);
		using var foreign = new VaultStore(_dir, new FakeCipher(foreign: true), new FakeSealer(), new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

		Assert.Throws<SyncAccountUnreadableException>(() => foreign.SyncAccount());
	}

	[Fact]
	public void SignedOutByTheServerIsKeptUntilTheNextSignIn()
	{
		using (var vault = Open())
		{
			vault.SaveSyncAccount(Account);
			vault.MarkSyncSignedOut();
		}

		using var reopened = Open();
		Assert.Null(reopened.SyncAccount());
		Assert.True(reopened.SyncSignedOutByServer);
		reopened.SaveSyncAccount(Account);
		Assert.False(reopened.SyncSignedOutByServer);
		reopened.ForgetSyncAccount();
		Assert.False(reopened.SyncSignedOutByServer);
	}

	[Fact]
	public void EveryAccountChangeIsAnnounced()
	{
		using var vault = Open();
		var changes = 0;
		vault.SyncChanged += () => changes++;

		vault.SaveSyncAccount(Account);
		vault.MarkSyncSignedOut();
		vault.ForgetSyncAccount();

		Assert.Equal(3, changes);
	}

	[Fact]
	public void AnUnreadableAccountCanBeForgottenAndReplaced()
	{
		using (var vault = Open()) vault.SaveSyncAccount(Account);
		using var foreign = new VaultStore(_dir, new FakeCipher(foreign: true), new FakeSealer(), new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

		foreign.SaveSyncAccount(Account with { Refresh = "replaced" });

		foreign.Dispose();
		using var reopened = Open();
		Assert.Equal("replaced", reopened.SyncAccount()!.Refresh);
	}

	[Fact]
	public void AClosedVaultRefusesToSaveTheAccount()
	{
		var vault = Open();
		vault.Dispose();

		Assert.Throws<VaultClosedException>(() => vault.SaveSyncAccount(Account));
	}
}
