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
	public void TheFloorIsTheScoreTheVectorsGiveTheRenderer() =>
		Assert.Equal(SyncPasswordStrength.MinimumScore, Vectors.Load("src/shared/vault/passwordStrength.vectors.json").GetProperty("minimumScore").GetInt32());

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

		await TurnOn(account);

		var blob = await ServerKeys();
		var salt = Convert.FromHexString(JsonNode.Parse(File.ReadAllText(Path.Combine(_accountDir, "account.json")))!["salt"]!.GetValue<string>());
		byte[][] needles =
		[
			.. new[] { "bank.example.test", Username, LoginPassword, Strong, DisplayName, ProfileEmail }.Select(Encoding.UTF8.GetBytes),
			Scrypt.Derive(Strong, salt),
			PasswordKey.Derive(Strong, blob.Kdf),
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
}
