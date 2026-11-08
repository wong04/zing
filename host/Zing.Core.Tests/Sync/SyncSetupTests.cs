using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Sync;
using Zing.Core.Tests.Vault;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>Turning sync on and changing the Zing password while it's on, against the real server (fake Google issuer).</summary>
public sealed class SyncSetupTests : IDisposable
{
	/// <summary>A five-word passphrase the generator made (passwordStrength.vectors.json).</summary>
	private const string Strong = "teaching-focused-onward-activist-steel";
	private const string NextStrong = "labour-muzzle-medici-rushes-fiction";

	private const string Origin = "https://bank.example.test";
	private const string Username = "ada.lovelace";
	private const string LoginPassword = "hunter2-login-secret";
	private const string DisplayName = "Ada Lovelace";
	private const string ProfileEmail = "ada.profile@example.test";

	private readonly string _vaultDir = Directory.CreateTempSubdirectory("zing-syncsetup-vault-").FullName;
	private readonly string _accountDir = Directory.CreateTempSubdirectory("zing-syncsetup-account-").FullName;
	private readonly LocalSyncServer _server = new();
	private readonly FakeSealer _sealer = new();
	private readonly VaultStore _vault;
	private readonly SyncClient _client;

	public SyncSetupTests()
	{
		_vault = Open();
		_client = new SyncClient(_server.Address, _vault, _server.Handler());
	}

	public void Dispose()
	{
		_client.Dispose();
		_vault.Dispose();
		_server.Dispose();
		Directory.Delete(_vaultDir, recursive: true);
		Directory.Delete(_accountDir, recursive: true);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private VaultStore Open() => new(_vaultDir, new FakeCipher(), _sealer, new TickingClock(), new ManualScheduler(), new FakeSessionEvents());

	private AccountStore Account(string password)
	{
		var account = new AccountStore(_accountDir, new FakeCipher());
		account.SignUp("ada", DisplayName, ProfileEmail, password);
		return account;
	}

	private async Task SignInToSync() => await _client.SignInAsync(await _server.MintIdTokenAsync(), Ct);

	private async Task<string> TurnOn(AccountStore account, string password = Strong) => await new SyncKeys(account, _vault, _client).TurnOnAsync(password, Ct);

	private async Task<SyncKeysBlob> ServerKeys() => SyncKeysBlob.Parse((await _client.GetKeysAsync(Ct))!.Blob);

	private List<string> SyncCalls() => [.. _server.Wire.Log.Where(l => l is "PUT /keys" or "POST /changes" or "PUT /manifest")];

	public static TheoryData<string, bool> StrengthVectors()
	{
		var data = new TheoryData<string, bool>();
		foreach (var c in Vectors.Load("src/shared/vault/passwordStrength.vectors.json").GetProperty("cases").EnumerateArray())
			data.Add(c.GetProperty("password").GetString()!, c.GetProperty("accepted").GetBoolean());
		return data;
	}

	[Theory]
	[MemberData(nameof(StrengthVectors))]
	public void TheStrengthFloorGivesTheRenderersVerdict(string password, bool accepted) =>
		Assert.Equal(accepted, SyncPasswordStrength.IsStrongEnough(password));

	[Fact]
	public void TheFloorIsTheOneTheVectorsGiveTheRenderer()
	{
		var vectors = Vectors.Load("src/shared/vault/passwordStrength.vectors.json");
		Assert.Equal(SyncPasswordStrength.MinimumScore, vectors.GetProperty("minimumScore").GetInt32());
		Assert.Equal(SyncPasswordStrength.MinimumGuessesLog10, vectors.GetProperty("minimumGuessesLog10").GetDouble());
	}

	[Fact]
	public async Task AGuestGetsNoAccountAndNothingIsUploaded()
	{
		await SignInToSync();

		var refused = await Assert.ThrowsAsync<AccountException>(() => TurnOn(Accounts.Guest()));

		Assert.Equal(AccountErrorCode.NoAccount, refused.Code);
		Assert.Empty(SyncCalls());
		Assert.Null(_vault.SyncKeys);
	}

	[Fact]
	public async Task APasswordThatIsNotTheAccountsIsRefused()
	{
		await SignInToSync();

		var refused = await Assert.ThrowsAsync<AccountException>(() => TurnOn(Account(Strong), NextStrong));

		Assert.Equal(AccountErrorCode.WrongPassword, refused.Code);
		Assert.Empty(SyncCalls());
	}

	[Fact]
	public async Task CorrectHorseIsTooWeakAndNothingIsUploaded()
	{
		await SignInToSync();

		await Assert.ThrowsAsync<SyncPasswordTooWeakException>(() => TurnOn(Account("correct horse"), "correct horse"));

		Assert.Empty(SyncCalls());
		Assert.Equal(0, _sealer.Calls);
	}

	[Fact]
	public async Task TurningOnBeforeSigningInToSyncAsksNothing()
	{
		await Assert.ThrowsAsync<NotSignedInToSyncException>(() => TurnOn(Account(Strong)));

		Assert.Equal(0, _sealer.Calls);
	}

	[Fact]
	public async Task TurningOnUploadsTheKeysThenEveryItemInBatchesThenTheManifest()
	{
		var account = Account(Strong);
		await _vault.ImportAsync([.. Enumerable.Range(0, 150).Select(i => new NewLogin($"https://site{i}.example.test", $"user{i}", $"pw{i}", "", ""))]);
		await _vault.NeverSave("https://never.example.test");
		await SignInToSync();

		var recovery = await TurnOn(account);

		Assert.Equal(["PUT /keys", "POST /changes", "POST /changes", "PUT /manifest"], SyncCalls());
		Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){5}-[A-Z2-7]{2}$", recovery);
		var records = _vault.Records;
		var pulled = await _client.GetChangesAsync(0, cancellation: Ct);
		Assert.Equal(records.Select(r => (r.ItemId, r.Version)).Order(), pulled.Items.Select(i => (i.ItemId, (long)i.Version)).Order());
		Assert.Empty(_vault.Queued);

		using var keys = await _vault.KeyMaterialAsync();
		var manifest = await _client.GetManifestAsync(Ct);
		var listed = SyncManifest.Open(keys.IndexKey, keys.VaultId, manifest!.Version, manifest.Blob);
		Assert.Equal(records.Select(r => (r.ItemId, r.Version)).Order(), listed.Select(e => (e.ItemId, e.Version)).Order());
	}

	[Fact]
	public async Task TheServersKeysOpenWithThePasswordOrTheRecoveryKeyAndCarryTheProfile()
	{
		var account = Account(Strong);
		await _vault.SaveAsync(new NewLogin(Origin, Username, LoginPassword, "", ""));
		await SignInToSync();

		var recovery = await TurnOn(account);

		using var local = await _vault.KeyMaterialAsync();
		var blob = await ServerKeys();
		Assert.Equal(local.VaultId, blob.VaultId);
		Assert.Equal(KdfParams.FloorMemoryKiB, blob.Kdf.MemoryKiB);
		using (var byPassword = blob.UnwrapWithPassword(Strong))
		{
			Assert.Equal(local.IndexKey, byPassword.IndexKey);
			Assert.Equal(local.SecretKey, byPassword.SecretKey);
			Assert.Equal(new AccountProfile("ada", DisplayName, ProfileEmail), blob.OpenProfile(byPassword.IndexKey));
		}
		using var byRecovery = blob.UnwrapWithRecoveryKey(recovery);
		Assert.Equal(local.IndexKey, byRecovery.IndexKey);
		Assert.Equal(local.SecretKey, byRecovery.SecretKey);
	}

	[Fact]
	public async Task TheServerHoldsNoOriginUsernameOrPasswordNorAnythingMadeFromTheZingPassword()
	{
		var account = Account(Strong);
		await _vault.SaveAsync(new NewLogin(Origin, Username, LoginPassword, "Bank", ""));
		await SignInToSync();

		var recovery = await TurnOn(account);
		await new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct);

		var blob = await ServerKeys();
		var salt = Convert.FromHexString(JsonNode.Parse(File.ReadAllText(Path.Combine(_accountDir, "account.json")))!["salt"]!.GetValue<string>());
		byte[][] needles =
		[
			.. new[] { "bank.example.test", Username, LoginPassword, Strong, NextStrong, DisplayName, ProfileEmail, recovery, recovery.Replace("-", "") }.Select(Encoding.UTF8.GetBytes),
			Scrypt.Derive(Strong, salt),
			Scrypt.Derive(NextStrong, salt),
			PasswordKey.Derive(Strong, blob.Kdf),
			PasswordKey.Derive(NextStrong, blob.Kdf),
			RecoveryKey.ToKey(recovery),
		];
		var stored = _server.StoredBytes();
		Assert.NotEmpty(stored);
		foreach (var needle in needles) Assert.True(stored.AsSpan().IndexOf(needle) < 0, $"The server holds {Convert.ToHexString(needle)}");
	}

	[Fact]
	public async Task TheKdfParamsAndVaultIdAreAuthenticatedWithTheRecoveryWrapsToo()
	{
		var account = Account(Strong);
		await SignInToSync();
		var recovery = await TurnOn(account);
		var raw = (await _client.GetKeysAsync(Ct))!.Blob;

		var moreIterations = JsonNode.Parse(raw)!;
		moreIterations["kdf"]!["iterations"] = KdfParams.FloorIterations + 1;
		var otherVault = JsonNode.Parse(raw)!;
		otherVault["vaultId"] = new string('a', 32);

		Assert.Throws<VaultTamperedException>(() => SyncKeysBlob.Parse(Encoding.UTF8.GetBytes(moreIterations.ToJsonString())).UnwrapWithRecoveryKey(recovery));
		Assert.Throws<VaultTamperedException>(() => SyncKeysBlob.Parse(Encoding.UTF8.GetBytes(otherVault.ToJsonString())).UnwrapWithRecoveryKey(recovery));
		Assert.Throws<VaultTamperedException>(() => SyncKeysBlob.Parse(Encoding.UTF8.GetBytes(otherVault.ToJsonString())).UnwrapWithPassword(Strong));
	}

	[Fact]
	public async Task KdfParamsBelowTheFloorAreRefusedBeforeAnythingIsDerived()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		var weak = JsonNode.Parse((await _client.GetKeysAsync(Ct))!.Blob)!;
		weak["kdf"]!["memoryKiB"] = 64 * 1024;

		Assert.Throws<KdfParamsRejectedException>(() => SyncKeysBlob.Parse(Encoding.UTF8.GetBytes(weak.ToJsonString())));
	}

	[Fact]
	public async Task TheRecoveryKeyIsKeptInKeysBinSealedUnderTheSecretKeySoOnlyHelloShowsIt()
	{
		var account = Account(Strong);
		await SignInToSync();
		var recovery = await TurnOn(account);

		var keysBin = File.ReadAllBytes(Path.Combine(_vaultDir, VaultStore.KeysFileName));
		Assert.True(keysBin.AsSpan().IndexOf(Encoding.UTF8.GetBytes(recovery)) < 0);
		Assert.True(keysBin.AsSpan().IndexOf(Encoding.UTF8.GetBytes(recovery.Replace("-", ""))) < 0);
		Assert.False(_vault.SyncKeys!.RecoveryConfirmed);

		using var reopened = Open();
		_sealer.Result = HelloResult.Cancel;
		await Assert.ThrowsAsync<HelloCancelledException>(reopened.RecoveryKeyAsync);
		_sealer.Result = HelloResult.Pass;
		Assert.Equal(recovery, await reopened.RecoveryKeyAsync());

		reopened.ConfirmRecoverySaved();
		using var confirmed = Open();
		Assert.True(confirmed.SyncKeys!.RecoveryConfirmed);
	}

	[Fact]
	public async Task AGoogleAccountThatAlreadySyncsAVaultIsRefusedAndNothingChangesHere()
	{
		await SignInToSync();
		await _client.PutKeysAsync("someone else's"u8.ToArray(), 0, Ct);

		await Assert.ThrowsAsync<SyncAlreadySetUpElsewhereException>(() => TurnOn(Account(Strong)));

		Assert.Null(_vault.SyncKeys);
		Assert.DoesNotContain("POST /changes", _server.Wire.Log);
	}

	[Fact]
	public async Task TurningOnTwiceIsRefused()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);

		await Assert.ThrowsAsync<SyncAlreadyOnException>(() => TurnOn(account));
	}

	[Fact]
	public async Task WithSyncOnChangingThePasswordRewrapsTheKeysSoOnlyTheNewPasswordOpensThem()
	{
		var account = Account(Strong);
		await SignInToSync();
		var recovery = await TurnOn(account);
		var before = await _client.GetKeysAsync(Ct);

		await new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct);

		var after = await _client.GetKeysAsync(Ct);
		Assert.Equal(before!.Version + 1, after!.Version);
		Assert.Equal(after.Version, _vault.SyncKeys!.Version);
		var blob = SyncKeysBlob.Parse(after.Blob);
		Assert.Throws<VaultTamperedException>(() => blob.UnwrapWithPassword(Strong));
		using var local = await _vault.KeyMaterialAsync();
		using (var opened = blob.UnwrapWithPassword(NextStrong))
		{
			Assert.Equal(local.IndexKey, opened.IndexKey);
			Assert.Equal(local.SecretKey, opened.SecretKey);
		}
		using (var byRecovery = blob.UnwrapWithRecoveryKey(recovery)) Assert.Equal(local.SecretKey, byRecovery.SecretKey);
		account.SignOut();
		account.SignIn(NextStrong);
	}

	[Fact]
	public async Task WithSyncOnANewPasswordMustBeStrong()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);

		await Assert.ThrowsAsync<SyncPasswordTooWeakException>(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, "correct horse", Ct));

		account.CheckPassword(Strong);
	}

	[Fact]
	public async Task AWrongCurrentPasswordChangesNothing()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		var puts = _server.Wire.Count("PUT /keys");

		var refused = await Assert.ThrowsAsync<AccountException>(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(NextStrong, NextStrong, Ct));

		Assert.Equal(AccountErrorCode.WrongPassword, refused.Code);
		Assert.Equal(puts, _server.Wire.Count("PUT /keys"));
	}

	[Fact]
	public async Task KeysChangedOnAnotherPcRaiseAndLeaveThisPcsPasswordAsItWas()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		var stored = _vault.SyncKeys!;
		// Another PC changed the password first: the server's keys moved past the version this PC stored.
		await _client.PutKeysAsync(stored.Blob, stored.Version, Ct);

		await Assert.ThrowsAsync<SyncKeysChangedElsewhereException>(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct));

		account.CheckPassword(Strong);
		Assert.Equal(stored.Version, _vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task WithSyncOffChangingThePasswordIsLocalAndKeepsTheEightCharacterMinimum()
	{
		var account = Account(Strong);

		await new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, "longenough1", Ct);

		account.CheckPassword("longenough1");
		Assert.Empty(SyncCalls());
	}

	[Fact]
	public void ATruncatedKeysBlobIsRefusedWithItsOwnError() =>
		Assert.Throws<SyncKeysFormatException>(() => SyncKeysBlob.Parse(JsonSerializer.SerializeToUtf8Bytes(new { format = 1 })));

	/// <summary>What a turn-on stores before its PUT /keys: this vault's keys wrapped under <see cref="Strong"/> and a new recovery key.</summary>
	private async Task<(VaultPendingKeys Pending, string Recovery)> PendingTurnOn(AccountStore account)
	{
		var recovery = RecoveryKey.Generate();
		using var keys = await _vault.KeyMaterialAsync();
		var blob = SyncKeysBlob.Create(keys, Strong, recovery, account.State().Profile!).ToBytes();
		return (new VaultPendingKeys(_vault.SyncAccount()!.UserId, blob, 0, RecoverySeal.Seal(keys.SecretKey, keys.VaultId, recovery)), recovery);
	}

	[Fact]
	public async Task WithSyncSignedOutAPasswordChangeIsRefusedBeforeHelloOrTheServer()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		await _client.SignOutAsync(Ct);
		_vault.ForgetSecretKey();
		var hellos = _sealer.Calls;
		var puts = _server.Wire.Count("PUT /keys");

		await Assert.ThrowsAsync<SyncSignInNeededException>(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct));

		Assert.Equal(hellos, _sealer.Calls);
		Assert.Equal(puts, _server.Wire.Count("PUT /keys"));
		account.CheckPassword(Strong);
	}

	[Fact]
	public async Task SignedInAsAnotherGoogleAccountAPasswordChangeNeverWritesThatAccountsKeys()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		// The other account already syncs a vault from its own PC, at version 1 like this one.
		using var otherPc = new SyncClient(_server.Address, new MemoryAccountStore(), _server.Handler());
		await otherPc.SignInAsync(await _server.MintIdTokenAsync(LocalSyncServer.OtherSub, "other@example.test"), Ct);
		await otherPc.PutKeysAsync("the other account's keys"u8.ToArray(), 0, Ct);
		await _client.SignOutAsync(Ct);
		await _client.SignInAsync(await _server.MintIdTokenAsync(LocalSyncServer.OtherSub, "other@example.test"), Ct);

		await Assert.ThrowsAsync<SyncSignInNeededException>(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct));

		Assert.Equal("the other account's keys"u8.ToArray(), (await otherPc.GetKeysAsync(Ct))!.Blob);
		account.CheckPassword(Strong);
	}

	[Fact]
	public async Task ATurnOnWhoseKeysAnswerIsLostFindsTheServerTookThemAndCarriesOn()
	{
		var account = Account(Strong);
		await SignInToSync();
		_server.Wire.LoseNextAnswerTo = "PUT /keys";

		var recovery = await TurnOn(account);

		Assert.Equal(1, _server.Wire.Count("PUT /keys"));
		Assert.Equal(1, _vault.SyncKeys!.Version);
		Assert.Null(_vault.PendingSyncKeys);
		Assert.Contains("PUT /manifest", _server.Wire.Log);
		using var byRecovery = (await ServerKeys()).UnwrapWithRecoveryKey(recovery);
	}

	[Fact]
	public async Task APasswordChangeWhoseKeysAnswerIsLostFindsTheServerTookThemAndChangesTheLocalPasswordToo()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		_server.Wire.LoseNextAnswerTo = "PUT /keys";

		await new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct);

		Assert.Equal(2, _vault.SyncKeys!.Version);
		Assert.Null(_vault.PendingSyncKeys);
		account.CheckPassword(NextStrong);
		using var opened = (await ServerKeys()).UnwrapWithPassword(NextStrong);
	}

	[Fact]
	public async Task AKeysPutThatNeverLeftThisPcIsDroppedAndTurningOnAgainWorks()
	{
		var account = Account(Strong);
		await SignInToSync();
		_server.Wire.FailWhen = line => line == "PUT /keys";

		await Assert.ThrowsAsync<HttpRequestException>(() => TurnOn(account));
		Assert.Null(_vault.PendingSyncKeys);
		Assert.Null(_vault.SyncKeys);

		_server.Wire.FailWhen = null;
		var recovery = await TurnOn(account);
		using var byRecovery = (await ServerKeys()).UnwrapWithRecoveryKey(recovery);
	}

	[Fact]
	public async Task ACrashBetweenStoringThePendingKeysAndThePutLeavesNothingBehind()
	{
		var account = Account(Strong);
		await SignInToSync();
		var (pending, stale) = await PendingTurnOn(account);
		_vault.SavePendingSyncKeys(pending);

		var recovery = await TurnOn(account);

		Assert.NotEqual(stale, recovery);
		Assert.Null(_vault.PendingSyncKeys);
		using var byRecovery = (await ServerKeys()).UnwrapWithRecoveryKey(recovery);
	}

	[Fact]
	public async Task ACrashAfterTheServerTookTheKeysIsFinishedByTheNextTurnOnWithTheSameRecoveryKey()
	{
		var account = Account(Strong);
		await SignInToSync();
		var (pending, recovery) = await PendingTurnOn(account);
		_vault.SavePendingSyncKeys(pending);
		await _client.PutKeysAsync(pending.Blob, 0, Ct);

		var resumed = await TurnOn(account);

		Assert.Equal(recovery, resumed);
		Assert.Null(_vault.PendingSyncKeys);
		Assert.Equal(1, _vault.SyncKeys!.Version);
		Assert.False(_vault.SyncKeys.RecoveryConfirmed);
		Assert.Equal(1, _server.Wire.Count("PUT /keys"));
		Assert.Contains("PUT /manifest", _server.Wire.Log);
	}

	private const string ThirdStrong = "velvet-orbit-candle-meadow-thistle";

	/// <summary>A change the server took (wrapped under <see cref="NextStrong"/>) whose PC stopped before its own password changed.</summary>
	private async Task<VaultSyncKeys> CrashAfterTheServerTookAChange(AccountStore account)
	{
		await SignInToSync();
		await TurnOn(account);
		var stored = _vault.SyncKeys!;
		using var keys = await _vault.KeyMaterialAsync();
		var changed = SyncKeysBlob.Parse(stored.Blob).WithPassword(keys, NextStrong).ToBytes();
		_vault.SavePendingSyncKeys(new VaultPendingKeys(stored.UserId, changed, stored.Version, stored.SealedRecoveryKey));
		await _client.PutKeysAsync(changed, stored.Version, Ct);
		return stored;
	}

	[Fact]
	public async Task ACrashAfterTheServerTookAPasswordChangeIsCommittedAndTheNextChangeWrapsUnderThePasswordJustTyped()
	{
		var account = Account(Strong);
		var stored = await CrashAfterTheServerTookAChange(account);

		await new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, ThirdStrong, Ct);

		Assert.Null(_vault.PendingSyncKeys);
		Assert.Equal(stored.Version + 2, _vault.SyncKeys!.Version);
		account.CheckPassword(ThirdStrong);
		using var opened = (await ServerKeys()).UnwrapWithPassword(ThirdStrong);
	}

	[Fact]
	public async Task ASettleWhoseKeysFetchFailsKeepsThePendingKeysForTheRetry()
	{
		var account = Account(Strong);
		await CrashAfterTheServerTookAChange(account);
		_server.Wire.FailWhen = line => line == "GET /keys";

		await Assert.ThrowsAsync<HttpRequestException>(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, ThirdStrong, Ct));
		Assert.NotNull(_vault.PendingSyncKeys);
		account.CheckPassword(Strong);

		_server.Wire.FailWhen = null;
		await new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, ThirdStrong, Ct);
		account.CheckPassword(ThirdStrong);
		using var opened = (await ServerKeys()).UnwrapWithPassword(ThirdStrong);
	}

	[Fact]
	public async Task AGenuineConflictOnAChangeDropsThePendingKeys()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		var stored = _vault.SyncKeys!;
		await _client.PutKeysAsync(stored.Blob, stored.Version, Ct);

		await Assert.ThrowsAsync<SyncKeysChangedElsewhereException>(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct));

		Assert.Null(_vault.PendingSyncKeys);
	}

	[Fact]
	public async Task TooManyItemsForTheManifestAreRefusedBeforeTheKeysAreUploaded()
	{
		var account = Account(Strong);
		await _vault.ImportAsync([.. Enumerable.Range(0, 6000).Select(i => new NewLogin($"https://site{i}.example.test", $"user{i}", $"pw{i}", "", ""))]);
		await SignInToSync();

		await Assert.ThrowsAsync<SyncManifestTooLargeException>(() => TurnOn(account));

		Assert.Empty(SyncCalls());
		Assert.Null(_vault.SyncKeys);
	}

	/// <summary>The turn-on upload's answers are checked as a sync's are (O5, Ruling 60): a conflict for an item not sent, or one sent twice.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ATurnOnUploadAnswerWithAConflictItCannotHaveIsAProtocolError(bool twiceForASentItem)
	{
		var account = Account(Strong);
		await _vault.SaveAsync(new NewLogin("https://bank.example.test", "ada", "pw", "Bank", ""));
		var record = _vault.Records.Single();
		await SignInToSync();
		_server.Wire.Rewrite = (line, body) =>
		{
			if (line != "POST /changes") return body;
			var answer = JsonNode.Parse(body)!;
			var conflicts = answer["conflicts"]!.AsArray();
			if (!twiceForASentItem)
			{
				conflicts.Add(JsonSerializer.SerializeToNode(new SyncItem("never-sent", 1, 5, false, [], [], []), SyncJson.Options));
				return answer.ToJsonString();
			}
			// The sent item, as uploaded, moved from accepted to conflicts and listed twice.
			var accepted = answer["accepted"]!.AsArray();
			var revision = accepted.Single()!["revision"]!.GetValue<long>();
			accepted.Clear();
			var held = new SyncItem(record.ItemId, (int)record.Version, revision, record.Tombstone, record.Summary, record.Password, SyncWire.JoinHistory(record.History));
			for (var i = 0; i < 2; i++) conflicts.Add(JsonSerializer.SerializeToNode(held, SyncJson.Options));
			return answer.ToJsonString();
		};

		await Assert.ThrowsAsync<SyncProtocolException>(() => TurnOn(account));
	}

	[Fact]
	public async Task EachUploadedBatchLeavesTheQueueAtOnce()
	{
		var account = Account(Strong);
		await _vault.ImportAsync([.. Enumerable.Range(0, 150).Select(i => new NewLogin($"https://site{i}.example.test", $"user{i}", $"pw{i}", "", ""))]);
		await SignInToSync();
		var pushes = 0;
		_server.Wire.FailWhen = line => line == "POST /changes" && ++pushes == 2;

		await Assert.ThrowsAsync<HttpRequestException>(() => TurnOn(account));

		Assert.Equal(50, _vault.Queued.Count);
	}

	[Fact]
	public async Task OnlyThePendingKeysThemselvesCanBeCommitted()
	{
		var account = Account(Strong);
		await SignInToSync();
		var (pending, _) = await PendingTurnOn(account);
		var (other, _) = await PendingTurnOn(account);
		_vault.SavePendingSyncKeys(pending);

		Assert.Throws<SyncPendingKeysReplacedException>(() => _vault.CommitPendingSyncKeys(other.Blob, 1));

		Assert.Null(_vault.SyncKeys);
		Assert.Equal(pending.Blob, _vault.PendingSyncKeys!.Blob);
	}

	[Fact]
	public async Task TwoWindowsTurningOnAtOnceKeepTheRecoveryKeyTheServerHolds()
	{
		var account = Account(Strong);
		await SignInToSync();

		async Task<string?> Attempt()
		{
			try
			{
				return await new SyncKeys(account, _vault, _client).TurnOnAsync(Strong, Ct);
			}
			catch (SyncAlreadyOnException)
			{
				return null;
			}
		}

		var recovery = Assert.Single((await Task.WhenAll(Task.Run(Attempt, Ct), Task.Run(Attempt, Ct))).OfType<string>());

		Assert.Null(_vault.PendingSyncKeys);
		Assert.Equal(recovery, await _vault.RecoveryKeyAsync());
		using var byRecovery = (await ServerKeys()).UnwrapWithRecoveryKey(recovery);
		Assert.Equal(_vault.SyncKeys!.Blob, (await _client.GetKeysAsync(Ct))!.Blob);
	}

	[Fact]
	public async Task AnotherAccountsUnfinishedTurnOnIsClearedWithItsOwnErrorNotOverwritten()
	{
		var account = Account(Strong);
		await SignInToSync();
		var (pending, _) = await PendingTurnOn(account);
		_vault.SavePendingSyncKeys(pending with { UserId = "user-someone-else" });

		await Assert.ThrowsAsync<SyncTurnOnUnfinishedForAnotherAccountException>(() => TurnOn(account));

		Assert.Null(_vault.PendingSyncKeys);
		Assert.Empty(SyncCalls());
		var recovery = await TurnOn(account);
		using var byRecovery = (await ServerKeys()).UnwrapWithRecoveryKey(recovery);
	}

	/// <summary>Holds the first request with this line until the returned release is set; <c>Reached</c> completes once it is held.</summary>
	private (Task Reached, TaskCompletionSource Release) Hold(string line)
	{
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = 0;
		_server.Wire.BeforeSend = async sent =>
		{
			if (sent != line || Interlocked.Exchange(ref held, 1) == 1) return;
			reached.SetResult();
			await release.Task;
		};
		return (reached.Task, release);
	}

	/// <summary>Holds the next Hello prompt until the returned release is set; <c>Reached</c> completes once it is up.</summary>
	private (Task Reached, TaskCompletionSource Release) HoldHello()
	{
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = 0;
		_sealer.Prompting = async () =>
		{
			if (Interlocked.Exchange(ref held, 1) == 1) return;
			reached.SetResult();
			await release.Task;
		};
		return (reached.Task, release);
	}

	[Fact]
	public async Task APasswordChangeStartedDuringATurnOnsHelloWaitsAndIsSyncedSoTheServerHasTheNewPassword()
	{
		var account = Account(Strong);
		await SignInToSync();
		var (reached, release) = HoldHello();
		var turnOn = Task.Run(() => TurnOn(account), Ct);
		await reached;

		var change = Task.Run(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct), Ct);
		await SyncKeys.QueuedBehind(_vault).WaitAsync(TimeSpan.FromSeconds(10), Ct);
		Assert.False(change.IsCompleted);
		account.CheckPassword(Strong);
		release.SetResult();
		var recovery = await turnOn;
		await change;

		account.CheckPassword(NextStrong);
		using var byPassword = (await ServerKeys()).UnwrapWithPassword(NextStrong);
		using var byRecovery = (await ServerKeys()).UnwrapWithRecoveryKey(recovery);
		Assert.Equal(_vault.SyncKeys!.Blob, (await _client.GetKeysAsync(Ct))!.Blob);
	}

	[Fact]
	public async Task OfTwoOverlappingChangesTheSecondIsToldThePasswordJustChangedAndSendsNothing()
	{
		var account = Account(Strong);
		await SignInToSync();
		await TurnOn(account);
		var (reached, release) = Hold("PUT /keys");
		var first = Task.Run(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, NextStrong, Ct), Ct);
		await reached;

		var second = Task.Run(() => new SyncKeys(account, _vault, _client).ChangePasswordAsync(Strong, ThirdStrong, Ct), Ct);
		await SyncKeys.QueuedBehind(_vault).WaitAsync(TimeSpan.FromSeconds(10), Ct);
		release.SetResult();
		await first;

		await Assert.ThrowsAsync<AccountPasswordChangedMeanwhileException>(() => second);
		Assert.Equal(2, _server.Wire.Count("PUT /keys"));
		account.CheckPassword(NextStrong);
		using var opened = (await ServerKeys()).UnwrapWithPassword(NextStrong);
		Assert.Equal(2, _vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task AnAccountSwitchWhileATurnOnIsUnderWaySendsNothingMoreAsTheOtherAccount()
	{
		var account = Account(Strong);
		await SignInToSync();
		var (reached, release) = Hold("PUT /keys");
		var turnOn = Task.Run(() => TurnOn(account), Ct);
		await reached;

		await _client.SignOutAsync(Ct);
		await _client.SignInAsync(await _server.MintIdTokenAsync(LocalSyncServer.OtherSub, "other@example.test"), Ct);
		release.SetResult();

		await Assert.ThrowsAsync<SyncAccountChangedException>(() => turnOn);
		Assert.Equal(0, _server.Wire.Count("POST /changes"));
		Assert.Equal(0, _server.Wire.Count("PUT /manifest"));
		Assert.Null(await _client.GetKeysAsync(Ct));
	}

	[Fact]
	public async Task AnotherAccountsUnfinishedPasswordChangeIsKeptAndBlocksATurnOn()
	{
		var account = Account(Strong);
		await SignInToSync();
		var (pending, _) = await PendingTurnOn(account);
		var change = pending with { UserId = "user-someone-else", BaseVersion = 3 };
		_vault.SavePendingSyncKeys(change);

		await Assert.ThrowsAsync<SyncChangeUnfinishedForAnotherAccountException>(() => TurnOn(account));

		Assert.Equal(change.Blob, _vault.PendingSyncKeys!.Blob);
		Assert.Empty(SyncCalls());
	}
}
