using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Vault;

public enum HelloResult { Pass, Cancel, Unavailable }

/// <summary>Stands in for Windows Hello: never prompts, counts every call, and answers as <see cref="Result"/> says.</summary>
internal sealed class FakeSealer : IHelloSealer
{
	private static readonly byte[] Mark = "HELLO:"u8.ToArray();

	public HelloResult Result { get; set; } = HelloResult.Pass;

	public int Seals { get; private set; }

	public int Unseals { get; private set; }

	public int Calls => Seals + Unseals;

	public bool Available => Result != HelloResult.Unavailable;

	public event Action? AvailabilityChanged;

	public void Announce() => AvailabilityChanged?.Invoke();

	/// <summary>How many stores listen for availability: one per store not yet disposed.</summary>
	public int Listeners => AvailabilityChanged?.GetInvocationList().Length ?? 0;

	/// <summary>Awaited while the fake prompt is "up", seal or unseal: a test holds Hello here to start something else meanwhile.</summary>
	public Func<Task>? Prompting { get; set; }

	public async Task<byte[]> SealAsync(byte[] key)
	{
		Seals++;
		if (Prompting is { } prompting) await prompting();
		Answer();
		return [.. Mark, .. key.Reverse()];
	}

	/// <summary>Runs while the fake prompt is "up", e.g. to navigate the page during Hello.</summary>
	public Action? Unsealing { get; set; }

	/// <summary>What a reset Hello key or a key sealed by another sealer looks like: the unseal itself fails.</summary>
	public Exception? UnsealFailure { get; set; }

	public async Task<byte[]> UnsealAsync(byte[] sealedBytes)
	{
		Unseals++;
		Unsealing?.Invoke();
		if (Prompting is { } prompting) await prompting();
		Answer();
		if (UnsealFailure is { } failure) throw failure;
		if (!sealedBytes.AsSpan().StartsWith(Mark)) throw new CryptographicException("not sealed by this fake");
		return [.. sealedBytes.Skip(Mark.Length).Reverse()];
	}

	private void Answer()
	{
		if (Result == HelloResult.Cancel) throw new HelloCancelledException("cancelled at the fake prompt");
		if (Result == HelloResult.Unavailable) throw new HelloUnavailableException("the fake has no Hello");
	}
}

public sealed class VaultStoreTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-vault-").FullName;
	private readonly FakeSealer _sealer = new();

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private VaultStore Open(FakeSealer? sealer = null) => new(_dir, new FakeCipher(), sealer ?? _sealer, new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

	private string VaultFile => Path.Combine(_dir, VaultStore.VaultFileName);

	private string SyncFile => Path.Combine(_dir, VaultStore.SyncFileName);

	private string KeysFile => Path.Combine(_dir, VaultStore.KeysFileName);

	private static readonly NewLogin Login = new("https://accounts.example.com/sign-in", "ada.lovelace", "correct horse battery staple", "Example", "work account");

	private byte[][] Snapshot() => [.. new[] { VaultFile, SyncFile, KeysFile }.Select(f => File.Exists(f) ? File.ReadAllBytes(f) : [])];

	[Fact]
	public async Task SavedPasswordReadsBackAfterReopening()
	{
		var id = await Open().SaveAsync(Login);

		var reopened = Open();
		Assert.Equal("correct horse battery staple", await reopened.PasswordAsync(id));
		var summary = Assert.Single(reopened.Summaries());
		Assert.Equal(id, summary.Id);
		Assert.Equal("https://accounts.example.com", summary.Origin);
		Assert.Equal("ada.lovelace", summary.Username);
		Assert.Equal("Example", summary.Title);
		Assert.Equal("work account", summary.Note);
		Assert.Null(summary.DeletedAt);
	}

	public static TheoryData<Exception> UnsealFailures => new()
	{
		new VaultTamperedException("the Hello key was reset"),
		new HelloSealFormatException("sealed by another sealer"),
	};

	[Theory]
	[MemberData(nameof(UnsealFailures))]
	public async Task AKeyHelloCantOpenAsksToSetTheVaultUpAgainOnEveryPathThatNeedsIt(Exception failure)
	{
		var id = await Open().SaveAsync(Login);
		var store = Open();
		_sealer.UnsealFailure = failure;

		var errors = new[]
		{
			await Assert.ThrowsAsync<VaultSetUpAgainException>(() => store.PasswordAsync(id)),
			await Assert.ThrowsAsync<VaultSetUpAgainException>(() => store.SaveAsync(Login with { Username = "grace" })),
			await Assert.ThrowsAsync<VaultSetUpAgainException>(() => store.UpdatePasswordAsync(id, "new")),
			await Assert.ThrowsAsync<VaultSetUpAgainException>(() => store.ExportAsync()),
			await Assert.ThrowsAsync<VaultSetUpAgainException>(() => store.ImportAsync([new NewLogin("https://b.example", "bob", "pw", "", "")])),
		};

		Assert.All(errors, e => Assert.Equal("Set up the vault again on this PC", e.Message));
		Assert.All(errors, e => Assert.Same(failure, e.InnerException));
		Assert.Single(store.Summaries());
	}

	[Fact]
	public async Task OpeningTheVaultAndListingItNeverNeedsTheHelloKey()
	{
		await Open().SaveAsync(Login);
		_sealer.UnsealFailure = new VaultTamperedException("the Hello key was reset");

		Assert.Single(Open().Summaries());
	}

	[Fact]
	public async Task SummariesNeverCallTheSealer()
	{
		await Open().SaveAsync(Login);
		var sealer = new FakeSealer();

		var reopened = Open(sealer);
		Assert.Single(reopened.Summaries());
		reopened.UpdateSummary(reopened.Summaries()[0].Id, "Renamed", "", "ada");
		reopened.History(reopened.Summaries()[0].Id);

		Assert.Equal(0, sealer.Calls);
	}

	[Fact]
	public async Task PasswordAsyncAsksHelloOnceThenReadsWithTheHeldKey()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		var before = _sealer.Unseals;

		for (var i = 0; i < 3; i++) await store.PasswordAsync(id);

		Assert.Equal(before + 1, _sealer.Unseals);
	}

	[Fact]
	public async Task OnlyTheFirstSaveSealsANewSecretKeyAndTheLaterWritesUnsealItOnceBetweenThem()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		Assert.Equal((1, 0), (_sealer.Seals, _sealer.Unseals));

		await store.SaveAsync(Login with { Username = "grace" });
		await store.UpdatePasswordAsync(id, "a new one");

		Assert.Equal((1, 1), (_sealer.Seals, _sealer.Unseals));
	}

	[Fact]
	public async Task ACancelledPromptRaisesAndChangesNothing()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		var files = Snapshot();
		var changes = 0;
		store.Changed += () => changes++;
		_sealer.Result = HelloResult.Cancel;

		await Assert.ThrowsAsync<HelloCancelledException>(() => store.PasswordAsync(id));
		await Assert.ThrowsAsync<HelloCancelledException>(() => store.SaveAsync(Login with { Username = "grace" }));
		await Assert.ThrowsAsync<HelloCancelledException>(() => store.UpdatePasswordAsync(id, "something else"));

		Assert.Equal(files, Snapshot());
		Assert.Equal(0, changes);
		Assert.Single(store.Summaries());
		Assert.Empty(store.History(id));
		_sealer.Result = HelloResult.Pass;
		Assert.Equal("correct horse battery staple", await store.PasswordAsync(id));
	}

	[Fact]
	public async Task ACancelledFirstSaveCreatesNoKeys()
	{
		_sealer.Result = HelloResult.Cancel;

		await Assert.ThrowsAsync<HelloCancelledException>(() => Open().SaveAsync(Login));

		Assert.False(File.Exists(KeysFile));
		Assert.False(File.Exists(VaultFile));
	}

	[Fact]
	public async Task NoHelloMakesEveryWriteRaise()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		store.Delete(id);
		var files = Snapshot();
		_sealer.Result = HelloResult.Unavailable;

		await Assert.ThrowsAsync<HelloUnavailableException>(() => store.SaveAsync(Login with { Username = "grace" }));
		await Assert.ThrowsAsync<HelloUnavailableException>(() => store.UpdatePasswordAsync(id, "new"));
		Assert.Throws<HelloUnavailableException>(() => store.UpdateSummary(id, "t", "n", "u"));
		Assert.Throws<HelloUnavailableException>(() => store.Restore(id));
		Assert.Throws<HelloUnavailableException>(() => store.Delete(id));

		Assert.Equal(files, Snapshot());
		Assert.False(store.HelloAvailable);
	}

	[Fact]
	public async Task NoHelloNeverPromptsForAWrite()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		var calls = _sealer.Calls;
		_sealer.Result = HelloResult.Unavailable;

		await Assert.ThrowsAsync<HelloUnavailableException>(() => store.UpdatePasswordAsync(id, "new"));

		Assert.Equal(calls, _sealer.Calls);
	}

	[Fact]
	public async Task AnUpdatedPasswordMovesTheOldOneIntoTheHistory()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);

		await store.UpdatePasswordAsync(id, "second");
		await store.UpdatePasswordAsync(id, "third");

		var reopened = Open();
		Assert.Equal("third", await reopened.PasswordAsync(id));
		Assert.Equal(2, reopened.History(id).Count);
		// The manage page offers "2 earlier passwords" from the summary, without asking for the list.
		Assert.Equal(2, reopened.Summaries().Single().HistoryCount);
		Assert.Equal("second", await reopened.HistoryPasswordAsync(id, 0));
		Assert.Equal("correct horse battery staple", await reopened.HistoryPasswordAsync(id, 1));
		var history = reopened.History(id);
		Assert.True(history[0].ReplacedAt > history[1].ReplacedAt);
		Assert.Equal(history[0].ReplacedAt, reopened.Summaries()[0].PasswordUpdatedAt);
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reopened.HistoryPasswordAsync(id, 2));
	}

	[Fact]
	public async Task EachChangeBumpsTheVersionAndQueuesOneChange()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		Assert.Equal(1, store.Records.Single().Version);

		await store.UpdatePasswordAsync(id, "second");
		store.UpdateSummary(id, "Renamed", "a note", "ada");
		store.Delete(id);
		store.Restore(id);

		var reopened = Open();
		Assert.Equal(5, reopened.Records.Single().Version);
		Assert.Equal([1L, 2, 3, 4, 5], reopened.Queued.Where(c => c.ItemId == id).Select(c => c.Version));
		Assert.Equal(5, reopened.Queued.Count);
	}

	[Fact]
	public async Task DeleteAndRestoreAreSummaryEditsWithoutHello()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		var calls = _sealer.Calls;

		store.Delete(id);
		Assert.NotNull(store.Summaries().Single().DeletedAt);
		Assert.Throws<VaultItemStateException>(() => store.Delete(id));
		store.Restore(id);
		Assert.Null(store.Summaries().Single().DeletedAt);
		Assert.Throws<VaultItemStateException>(() => store.Restore(id));

		Assert.Equal(calls, _sealer.Calls);
		Assert.Equal("correct horse battery staple", await store.PasswordAsync(id));
	}

	[Fact]
	public async Task EditingTheSummaryKeepsThePasswordReadable()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);

		store.UpdateSummary(id, "Renamed", "new note", "ada");

		var reopened = Open();
		var summary = reopened.Summaries().Single();
		Assert.Equal(("Renamed", "new note", "ada"), (summary.Title, summary.Note, summary.Username));
		Assert.Equal("correct horse battery staple", await reopened.PasswordAsync(id));
	}

	[Fact]
	public async Task EveryWriteRaisesChanged()
	{
		var store = Open();
		var changes = 0;
		store.Changed += () => changes++;

		var id = await store.SaveAsync(Login);
		await store.UpdatePasswordAsync(id, "second");
		store.UpdateSummary(id, "t", "n", "u");
		store.Delete(id);
		store.Restore(id);
		_sealer.Announce();

		Assert.Equal(6, changes);
	}

	[Fact]
	public async Task TheVaultFileHoldsNoOriginUsernameOrPasswordInClearText()
	{
		await Open().SaveAsync(Login);

		var bytes = File.ReadAllBytes(VaultFile);
		foreach (var clear in new[] { "accounts.example.com", "ada.lovelace", "correct horse", "work account", "Example" })
		{
			Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(clear)));
			Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(clear)));
		}
	}

	[Fact]
	public async Task TheVaultIdIsMadeOnceAndKeptAcrossReopening()
	{
		var store = Open();
		await store.SaveAsync(Login);
		var vaultId = store.VaultId;

		var reopened = Open();
		await reopened.SaveAsync(Login with { Username = "grace" });

		Assert.Equal(vaultId, reopened.VaultId);
		Assert.Equal(vaultId, JsonNode.Parse(File.ReadAllText(KeysFile))!["vaultId"]!.GetValue<string>());
		Assert.Equal(1, _sealer.Seals);
	}

	[Fact]
	public async Task BothKeysAreSealedByTheCipherAndTheSecretKeyAlsoByHello()
	{
		await Open().SaveAsync(Login);

		var keys = JsonNode.Parse(File.ReadAllText(KeysFile))!;
		var index = Convert.FromBase64String(keys["indexKey"]!.GetValue<string>());
		var secret = Convert.FromBase64String(keys["secretKey"]!.GetValue<string>());
		Assert.True(index.AsSpan().StartsWith(FakeCipher.Mark));
		Assert.True(secret.AsSpan().StartsWith(FakeCipher.Mark));
		var helloSealed = new FakeCipher().Decrypt(secret);
		Assert.True(helloSealed.AsSpan().StartsWith("HELLO:"u8));
	}

	[Fact]
	public async Task APasswordMovedToAnotherItemFailsItsTag()
	{
		var store = Open();
		await store.SaveAsync(Login);
		await store.SaveAsync(Login with { Username = "grace", Password = "grace's" });
		var file = JsonNode.Parse(File.ReadAllText(VaultFile))!;
		var items = file["items"]!.AsArray();
		(items[0]!["password"], items[1]!["password"]) = (items[1]!["password"]!.DeepClone(), items[0]!["password"]!.DeepClone());
		File.WriteAllText(VaultFile, file.ToJsonString());

		// Caught at load now: each sealed summary names its password's ciphertext (Ruling 56), before any tag is tried.
		var error = Assert.Throws<VaultFileException>(() => Open());
		Assert.IsType<VaultTamperedException>(error.InnerException);
	}

	[Fact]
	public async Task ARelabelledVersionFailsTheSummaryTag()
	{
		await Open().SaveAsync(Login);
		var file = JsonNode.Parse(File.ReadAllText(VaultFile))!;
		file["items"]![0]!["version"] = 9;
		File.WriteAllText(VaultFile, file.ToJsonString());

		var error = Assert.Throws<VaultFileException>(() => Open());
		Assert.IsType<VaultTamperedException>(error.InnerException);
	}

	[Fact]
	public async Task AVaultFileWithoutItsKeysIsRefused()
	{
		await Open().SaveAsync(Login);
		File.Delete(KeysFile);

		var error = Assert.Throws<VaultFileException>(() => Open());
		Assert.Equal(VaultFile, error.Path);
	}

	/// <summary>Opening must raise VaultFileException naming the file, and leave every file's bytes as they were.</summary>
	private void AssertRefusedUnchanged(string path)
	{
		var files = Snapshot();

		var error = Assert.Throws<VaultFileException>(() => Open());

		Assert.Equal(path, error.Path);
		Assert.Equal(files, Snapshot());
	}

	private async Task<JsonNode> SavedVaultJson()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		await store.UpdatePasswordAsync(id, "second");
		return JsonNode.Parse(File.ReadAllText(VaultFile))!;
	}

	public static TheoryData<string> MalformedVaultFiles() =>
	[
		"",
		"""{"format":1""",
		"null",
		"[]",
		"""{"format":1}""",
		"""{"format":2,"items":[]}""",
		"""{"items":[]}""",
		"""{"format":1,"items":null}""",
		"""{"format":1,"items":[null]}""",
	];

	[Theory]
	[MemberData(nameof(MalformedVaultFiles))]
	public async Task AMalformedVaultFileIsRefusedAndLeftAsItIs(string content)
	{
		await Open().SaveAsync(Login);
		File.WriteAllText(VaultFile, content);

		AssertRefusedUnchanged(VaultFile);
	}

	[Theory]
	[InlineData("itemId", null)]
	[InlineData("itemId", "")]
	[InlineData("summary", null)]
	[InlineData("password", null)]
	[InlineData("history", null)]
	[InlineData("version", 0)]
	public async Task AnItemWithANullOrEmptyFieldIsRefused(string field, object? value)
	{
		var file = await SavedVaultJson();
		file["items"]![0]![field] = value switch
		{
			null => null,
			string text => JsonValue.Create(text),
			int number => JsonValue.Create(number),
			_ => throw new ArgumentException($"No JSON for {value}"),
		};
		File.WriteAllText(VaultFile, file.ToJsonString());

		AssertRefusedUnchanged(VaultFile);
	}

	[Theory]
	[InlineData("itemId")]
	[InlineData("summary")]
	[InlineData("password")]
	[InlineData("history")]
	[InlineData("version")]
	public async Task AnItemWithoutAFieldIsRefused(string field)
	{
		var file = await SavedVaultJson();
		file["items"]![0]!.AsObject().Remove(field);
		File.WriteAllText(VaultFile, file.ToJsonString());

		AssertRefusedUnchanged(VaultFile);
	}

	[Fact]
	public async Task ANullHistoryEntryIsRefused()
	{
		var file = await SavedVaultJson();
		file["items"]![0]!["history"]!.AsArray().Add(null);
		File.WriteAllText(VaultFile, file.ToJsonString());

		AssertRefusedUnchanged(VaultFile);
	}

	[Fact]
	public async Task ATruncatedVaultFileIsRefused()
	{
		await SavedVaultJson();
		var bytes = File.ReadAllBytes(VaultFile);
		File.WriteAllBytes(VaultFile, bytes[..(bytes.Length / 2)]);

		AssertRefusedUnchanged(VaultFile);
	}

	[Fact]
	public async Task TwoItemsWithOneIdAreRefused()
	{
		var file = await SavedVaultJson();
		var items = file["items"]!.AsArray();
		items.Add(items[0]!.DeepClone());
		File.WriteAllText(VaultFile, file.ToJsonString());

		AssertRefusedUnchanged(VaultFile);
	}

	[Theory]
	[InlineData("""{"origin":null,"username":"u","title":"","note":"","passwordVersion":1,"history":[]}""")]
	[InlineData("""{"origin":"https://a.example","username":"u","title":"","note":"","passwordVersion":1}""")]
	[InlineData("""{"origin":"https://a.example","username":"u","title":"","note":"","passwordVersion":1,"history":[null]}""")]
	[InlineData("""{"origin":"https://a.example","title":"","note":"","passwordVersion":1,"history":[]}""")]
	[InlineData("""{"origin":"https://a.example","username":"u","title":"","note":"","passwordVersion":0,"history":[]}""")]
	[InlineData("null")]
	public async Task AMalformedSealedSummaryIsRefused(string payload)
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		var indexKey = new FakeCipher().Decrypt(Convert.FromBase64String(JsonNode.Parse(File.ReadAllText(KeysFile))!["indexKey"]!.GetValue<string>()));
		var file = JsonNode.Parse(File.ReadAllText(VaultFile))!;
		file["items"]![0]!["summary"] = Convert.ToBase64String(VaultCrypto.Seal(indexKey, Encoding.UTF8.GetBytes(payload), new ItemAd(store.VaultId!, id, 1, ItemPart.Summary)));
		File.WriteAllText(VaultFile, file.ToJsonString());

		AssertRefusedUnchanged(VaultFile);
	}

	public static TheoryData<string> MalformedSyncFiles() =>
	[
		"",
		"""{"format":1""",
		"null",
		"""{"format":1}""",
		"""{"format":2,"queued":[]}""",
		"""{"format":1,"queued":null}""",
		"""{"format":1,"queued":[null]}""",
		"""{"format":1,"queued":[{"version":1}]}""",
		// A manifest hash that isn't a SHA-256 (O4, Ruling 60): unreadable, not a rollback at the next sync.
		"""{"format":1,"queued":[],"progress":{"userId":"u","cursor":0,"revisionFloor":0,"manifestFloor":1,"manifestHash":"AAAA"}}""",
	];

	[Theory]
	[MemberData(nameof(MalformedSyncFiles))]
	public async Task AMalformedSyncFileIsRefusedAndLeftAsItIs(string content)
	{
		await Open().SaveAsync(Login);
		File.WriteAllText(SyncFile, content);

		AssertRefusedUnchanged(SyncFile);
	}

	public static TheoryData<string> MalformedKeysFiles() =>
	[
		"",
		"""{"format":1""",
		"null",
		"""{"format":1}""",
		"""{"format":2,"vaultId":"v","indexKey":"AA==","secretKey":"AA=="}""",
		"""{"format":1,"vaultId":"v","indexKey":null,"secretKey":"AA=="}""",
		"""{"format":1,"vaultId":null,"indexKey":"AA==","secretKey":"AA=="}""",
		"""{"format":1,"vaultId":"v","indexKey":"not base64!","secretKey":"AA=="}""",
	];

	[Theory]
	[MemberData(nameof(MalformedKeysFiles))]
	public async Task AMalformedKeysFileIsRefusedAndLeftAsItIs(string content)
	{
		await Open().SaveAsync(Login);
		File.WriteAllText(KeysFile, content);

		AssertRefusedUnchanged(KeysFile);
	}

	[Fact]
	public async Task ATombstoneFlagTheSealedSummaryDoesntBackIsTampering()
	{
		var file = await SavedVaultJson();
		file["items"]![0]!["tombstone"] = true;
		File.WriteAllText(VaultFile, file.ToJsonString());

		var error = Assert.Throws<VaultFileException>(() => Open());
		Assert.IsType<VaultTamperedException>(error.InnerException);
	}

	[Fact]
	public async Task ATombstoneFlagOnALoginInRecentlyDeletedIsTampering()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		store.Delete(id);
		var file = JsonNode.Parse(File.ReadAllText(VaultFile))!;
		file["items"]![0]!["tombstone"] = true;
		File.WriteAllText(VaultFile, file.ToJsonString());

		// Ruling 25 both ways (Ruling 56): a deleted summary isn't a sealed tombstone, so the flag would hide it from Recently deleted.
		var error = Assert.Throws<VaultFileException>(() => Open());
		Assert.IsType<VaultTamperedException>(error.InnerException);
	}

	[Fact]
	public async Task SwappingThePasswordWithAHistoryEntryFailsToOpen()
	{
		var file = await SavedVaultJson();
		var item = file["items"]![0]!;
		var password = item["password"]!.DeepClone();
		item["password"] = item["history"]![0]!.DeepClone();
		item["history"]![0] = password;
		File.WriteAllText(VaultFile, file.ToJsonString());

		// Caught at load: the sealed summary names the password and each old one by its ciphertext's hash (Ruling 56).
		var error = Assert.Throws<VaultFileException>(() => Open());
		Assert.IsType<VaultTamperedException>(error.InnerException);
	}

	[Fact]
	public async Task UpdateLoginChangesThePasswordAndSummaryInOneChange()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		var changes = 0;
		store.Changed += () => changes++;

		await store.UpdateLoginAsync(id, "Renamed", "new note", "ada", "second");

		var reopened = Open();
		var summary = reopened.Summaries().Single();
		Assert.Equal(("Renamed", "new note", "ada"), (summary.Title, summary.Note, summary.Username));
		Assert.Equal("second", await reopened.PasswordAsync(id));
		Assert.Equal("correct horse battery staple", await reopened.HistoryPasswordAsync(id, 0));
		Assert.Equal(2, reopened.Records.Single().Version);
		Assert.Equal([1L, 2], reopened.Queued.Select(c => c.Version));
		Assert.Equal(1, changes);
	}

	[Fact]
	public async Task ACancelledUpdateLoginChangesNothing()
	{
		var store = Open();
		var id = await store.SaveAsync(Login);
		var files = Snapshot();
		_sealer.Result = HelloResult.Cancel;

		await Assert.ThrowsAsync<HelloCancelledException>(() => store.UpdateLoginAsync(id, "Renamed", "n", "u", "second"));

		Assert.Equal(files, Snapshot());
		Assert.Equal("ada.lovelace", store.Summaries().Single().Username);
	}

	[Fact]
	public async Task AnUnknownIdIsRefused()
	{
		var store = Open();
		await store.SaveAsync(Login);

		await Assert.ThrowsAsync<VaultItemNotFoundException>(() => store.PasswordAsync("nope"));
		await Assert.ThrowsAsync<VaultItemNotFoundException>(() => store.UpdatePasswordAsync("nope", "x"));
		Assert.Throws<VaultItemNotFoundException>(() => store.Delete("nope"));
		Assert.Throws<VaultItemNotFoundException>(() => store.History("nope"));
	}

	[Fact]
	public async Task ANonWebOriginIsRefusedBeforeAnyPrompt()
	{
		var store = Open();

		await Assert.ThrowsAsync<NotAWebOriginException>(() => store.SaveAsync(Login with { Origin = "android://com.example" }));

		Assert.Equal(0, _sealer.Calls);
	}

	[Fact]
	public void AnEmptyVaultHasNoSummariesAndNoFiles()
	{
		var store = Open();

		Assert.Empty(store.Summaries());
		Assert.False(Directory.EnumerateFileSystemEntries(_dir).Any());
	}
}

public sealed class HelloKeySealTests
{
	private static readonly byte[] Signature = RandomNumberGenerator.GetBytes(256);

	[Fact]
	public void OpensWithTheSameSignature()
	{
		var key = RandomNumberGenerator.GetBytes(VaultCrypto.KeySize);

		var sealedBytes = HelloKeySeal.Seal(Signature, key);

		Assert.Equal(key, HelloKeySeal.Open(Signature, sealedBytes));
	}

	[Fact]
	public void AnotherSignatureFailsTheTag()
	{
		var sealedBytes = HelloKeySeal.Seal(Signature, RandomNumberGenerator.GetBytes(VaultCrypto.KeySize));

		Assert.Throws<VaultTamperedException>(() => HelloKeySeal.Open(RandomNumberGenerator.GetBytes(256), sealedBytes));
	}

	[Fact]
	public void TheBlobStartsWithItsVersionThenTheFixedChallenge()
	{
		var sealedBytes = HelloKeySeal.Seal(Signature, RandomNumberGenerator.GetBytes(VaultCrypto.KeySize));

		Assert.Equal(HelloKeySeal.Version, sealedBytes[0]);
		Assert.Equal(HelloKeySeal.Challenge, HelloKeySeal.ChallengeOf(sealedBytes));
		Assert.Equal(32, HelloKeySeal.Challenge.Length);
		Assert.Equal(SHA256.HashData("zing-hello-challenge-v1"u8), HelloKeySeal.Challenge);
	}

	[Fact]
	public void AnUnknownVersionIsRefused()
	{
		var sealedBytes = HelloKeySeal.Seal(Signature, RandomNumberGenerator.GetBytes(VaultCrypto.KeySize));
		sealedBytes[0] = 2;

		Assert.Throws<HelloSealFormatException>(() => HelloKeySeal.ChallengeOf(sealedBytes));
		Assert.Throws<HelloSealFormatException>(() => HelloKeySeal.Open(Signature, sealedBytes));
	}

	[Fact]
	public void TheCredentialIsNamedZingVault() => Assert.Equal("Zing vault", HelloKeySeal.CredentialName);
}
