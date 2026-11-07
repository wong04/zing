using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

/// <summary>Import and export are one operation each, so each asks Windows Hello once (Ruling 3); a never-saved mark is removed without it (Ruling 39).</summary>
public sealed class VaultImportExportTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-vaultio-").FullName;
	private readonly FakeSealer _sealer = new();

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private VaultStore Open() => new(_dir, new FakeCipher(), _sealer, new TickingClock());

	private static readonly NewLogin Login = new("https://accounts.example.com/sign-in", "ada.lovelace", "correct horse battery staple", "Example", "work account");

	private static NewLogin Row(string origin, string username, string password) => new(origin, username, password, "", "");

	private byte[][] Snapshot() => [.. new[] { VaultStore.VaultFileName, VaultStore.SyncFileName, VaultStore.KeysFileName }.Select(f => Path.Combine(_dir, f)).Select(f => File.Exists(f) ? File.ReadAllBytes(f) : [])];

	[Fact]
	public async Task ImportAddsEveryLoginWithOneHelloForTheWholeBatch()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var calls = _sealer.Calls;

		var outcome = await store.ImportAsync([Row("https://a.example/x", "u1", "p1"), Row("https://b.example", "u2", "p2"), Row("https://c.example", "u3", "p3")]);

		Assert.Equal(new ImportOutcome(3, 0), outcome);
		Assert.Equal(calls + 1, _sealer.Calls);
		Assert.Equal(4, store.Summaries().Count);
		var b = store.Summaries().Single(s => s.Origin == "https://b.example");
		Assert.Equal("p2", await store.PasswordAsync(b.Id));
	}

	[Fact]
	public async Task ImportWritesTheFilesOnceForTheBatch()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var changes = 0;
		store.Changed += () => changes++;

		await store.ImportAsync([Row("https://a.example", "u1", "p1"), Row("https://b.example", "u2", "p2")]);

		Assert.Equal(1, changes);
		Assert.Equal(3, store.Records.Count);
		Assert.Equal(3, store.Queued.Count);
	}

	[Fact]
	public async Task ImportMergesALoginTheVaultAlreadyHoldsAndKeepsTheOldPasswordInItsHistory()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);

		var same = await store.ImportAsync([Row("https://accounts.example.com/other", "ada.lovelace", "correct horse battery staple")]);
		var changedPassword = await store.ImportAsync([Row("https://accounts.example.com", "ada.lovelace", "a different one")]);

		Assert.Equal(new ImportOutcome(0, 1), same);
		Assert.Equal(new ImportOutcome(0, 1), changedPassword);
		Assert.Single(store.Summaries());
		Assert.Equal("a different one", await store.PasswordAsync(id));
		Assert.Equal("correct horse battery staple", await store.HistoryPasswordAsync(id, 0));
		Assert.Single(store.History(id));
	}

	[Fact]
	public async Task ImportMergesDuplicateRowsOfTheSameFile()
	{
		var store = Open();

		var outcome = await store.ImportAsync([Row("https://a.example", "u", "first"), Row("https://a.example/login", "u", "first"), Row("https://a.example", "u", "second"), Row("https://a.example", "other", "x")]);

		Assert.Equal(new ImportOutcome(2, 2), outcome);
		var login = store.Summaries().Single(s => s.Username == "u");
		Assert.Equal("second", await store.PasswordAsync(login.Id));
		Assert.Equal(2, store.Summaries().Count);
		Assert.Single(store.History(login.Id));
	}

	[Fact]
	public async Task ImportAddsALoginWhoseTwinIsInRecentlyDeleted()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		store.Delete(id);

		var outcome = await store.ImportAsync([Row("https://accounts.example.com", "ada.lovelace", "new")]);

		Assert.Equal(new ImportOutcome(1, 0), outcome);
		Assert.Equal(2, store.Summaries().Count);
	}

	[Fact]
	public async Task ImportOfAnAddressThatIsNotAWebOriginIsRefusedAndNothingIsWritten()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var files = Snapshot();

		await Assert.ThrowsAsync<NotAWebOriginException>(() => store.ImportAsync([Row("https://ok.example", "u", "p"), Row("android://x", "u", "p")]));

		Assert.Equal(files, Snapshot());
	}

	[Fact]
	public async Task ACancelledImportChangesNothing()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var files = Snapshot();
		_sealer.Result = HelloResult.Cancel;

		await Assert.ThrowsAsync<HelloCancelledException>(() => store.ImportAsync([Row("https://a.example", "u", "p")]));

		Assert.Equal(files, Snapshot());
	}

	[Fact]
	public async Task ImportIntoAStoreClosedDuringHelloWritesNothing()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var files = Snapshot();
		_sealer.Unsealing = store.Dispose;

		await Assert.ThrowsAsync<VaultClosedException>(() => store.ImportAsync([Row("https://a.example", "u", "p")]));

		Assert.Equal(files, Snapshot());
	}

	[Fact]
	public async Task ImportIntoANewVaultMakesTheKeysWithOneHello()
	{
		var store = Open();

		await store.ImportAsync([Row("https://a.example", "u", "p"), Row("https://b.example", "u", "p")]);

		Assert.Equal((1, 0), (_sealer.Seals, _sealer.Unseals));
		Assert.Equal(2, store.Summaries().Count);
	}

	[Fact]
	public async Task ImportingNothingAsksNothing()
	{
		var store = Open();

		Assert.Equal(new ImportOutcome(0, 0), await store.ImportAsync([]));
		Assert.Equal(0, _sealer.Calls);
	}

	[Fact]
	public async Task ExportReadsEveryLivePasswordWithOneHelloAndLeavesOutDeletedLoginsAndMarks()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var gone = await store.SaveAsync(Login with { Username = "gone" });
		await store.SaveAsync(Login with { Username = "grace", Password = "grace pw" });
		store.Delete(gone);
		await store.NeverSave("https://never.example");
		var calls = _sealer.Calls;

		var exported = await store.ExportAsync();

		Assert.Equal(calls + 1, _sealer.Calls);
		Assert.Equal(["ada.lovelace", "grace"], exported.Select(e => e.Username).Order());
		Assert.Equal("grace pw", exported.Single(e => e.Username == "grace").Password);
		Assert.All(exported, e => Assert.Equal("https://accounts.example.com", e.Origin));
	}

	[Fact]
	public async Task ExportWithNothingToExportSaysSo()
	{
		var store = Open();
		await store.SaveAsync(Login);
		store.Delete(store.Summaries()[0].Id);

		await Assert.ThrowsAsync<NothingToExportException>(() => store.ExportAsync());
	}

	[Fact]
	public async Task ACancelledExportRaisesAndReadsNothing()
	{
		var store = Open();
		await store.SaveAsync(Login);
		_sealer.Result = HelloResult.Cancel;

		await Assert.ThrowsAsync<HelloCancelledException>(() => store.ExportAsync());
	}

	[Fact]
	public async Task RemovingANeverMarkNeedsNoHelloAndOffersTheSiteAgain()
	{
		var store = Open();
		await store.NeverSave("https://never.example/login");
		var calls = _sealer.Calls;
		var changes = 0;
		store.Changed += () => changes++;

		store.RemoveNeverSave("https://never.example/other");

		Assert.Equal(calls, _sealer.Calls);
		Assert.Equal(1, changes);
		Assert.False(store.IsNeverSave("https://never.example"));
		Assert.Empty(store.NeverSaves());
		Assert.Empty(Open().NeverSaves());
	}

	[Fact]
	public async Task RemovingAMarkThatIsntThereIsRefused()
	{
		var store = Open();
		await store.NeverSave("https://never.example");

		Assert.Throws<NeverSaveNotFoundException>(() => store.RemoveNeverSave("https://other.example"));
		Assert.True(store.IsNeverSave("https://never.example"));
	}

	[Fact]
	public async Task AMarkRemovedAndSetAgainIsMarkedAgain()
	{
		var store = Open();
		await store.NeverSave("https://never.example");
		store.RemoveNeverSave("https://never.example");

		await store.NeverSave("https://never.example");

		Assert.True(store.IsNeverSave("https://never.example"));
		Assert.Single(store.NeverSaves());
	}

	[Fact]
	public async Task AnImportThatChangesNothingTellsNobody()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var changes = 0;
		store.Changed += () => changes++;

		var outcome = await store.ImportAsync([Row("https://accounts.example.com", "ada.lovelace", "correct horse battery staple")]);

		Assert.Equal(new ImportOutcome(0, 1), outcome);
		Assert.Equal(0, changes);
	}
}
