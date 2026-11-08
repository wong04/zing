using Zing.Core.Sync;
using Zing.Core.Tests.Vault;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>A new PC, a forgotten password and a password changed on another PC (Task 14), against the real server (fake Google issuer).</summary>
public sealed class SyncRestoreTests : IDisposable
{
	private const string Strong = TwoPcs.Strong;
	private const string NextStrong = "labour-muzzle-medici-rushes-fiction";

	private readonly TwoPcs _pcs = new();
	private readonly string _bAccountDir = Directory.CreateTempSubdirectory("zing-syncrestore-account-").FullName;
	private readonly SyncPc _b;
	private readonly AccountStore _bAccount;

	public SyncRestoreTests()
	{
		_b = new SyncPc(_pcs.Server);
		_bAccount = new AccountStore(_bAccountDir, new FakeCipher());
	}

	public void Dispose()
	{
		_b.Dispose();
		_pcs.Dispose();
		Directory.Delete(_bAccountDir, recursive: true);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private static SyncRestore Restore(SyncPc pc, AccountStore account) => new(account, () => pc.Vault, pc.Client, pc.Vault);

	private async Task<string> TurnOnAWithALoginAsync()
	{
		await _pcs.SaveOnAAsync(password: "bank-secret");
		await _pcs.A.SignInAsync();
		return await new SyncKeys(_pcs.Account, _pcs.A.Vault, _pcs.A.Client).TurnOnAsync(Strong, Ct);
	}

	private async Task<SyncKeysBlob> ServerKeysAsync()
	{
		var served = (await _pcs.A.Client.GetKeysAsync(Ct))!;
		return SyncKeysBlob.Parse(served.Blob, served.Version);
	}

	[Fact]
	public async Task RestoringWithThePasswordOnANewPcMakesTheAccountFromTheProfileAndBringsBackEveryLogin()
	{
		var id = await _pcs.SaveOnAAsync(password: "bank-secret");
		var other = await _pcs.SaveOnAAsync("https://mail.example.test", "ada.mail", "mail-secret");
		await _pcs.TurnOnAAsync();
		await _b.SignInAsync();

		await Restore(_b, _bAccount).RestoreWithPasswordAsync(Strong, Ct);
		await _b.SyncAsync();

		var state = _bAccount.State();
		Assert.True(state.SignedIn);
		Assert.Equal(new AccountProfile("ada", "Ada Lovelace", "ada.profile@example.test"), state.Profile);
		_bAccount.CheckPassword(Strong);
		Assert.Equal(_pcs.A.Vault.VaultId, _b.Vault.VaultId);
		Assert.Equal(["https://bank.example.test", "https://mail.example.test"], _b.Vault.Summaries().Select(s => s.Origin).Order());
		Assert.Equal("bank-secret", await _b.Vault.PasswordAsync(id));
		Assert.Equal("mail-secret", await _b.Vault.PasswordAsync(other));
		Assert.Equal(_pcs.A.Vault.SyncKeys!.Version, _b.Vault.SyncKeys!.Version);
		Assert.True(_b.Vault.SyncKeys.RecoveryConfirmed);
	}

	[Fact]
	public async Task AWrongPasswordIsRefusedAndNothingIsCreated()
	{
		await TurnOnAWithALoginAsync();
		await _b.SignInAsync();

		var refused = await Assert.ThrowsAsync<AccountException>(() => Restore(_b, _bAccount).RestoreWithPasswordAsync(NextStrong, Ct));

		Assert.Equal(AccountErrorCode.WrongPassword, refused.Code);
		Assert.False(_bAccount.State().HasAccount);
		Assert.Null(_b.Vault.VaultId);
		Assert.Null(_b.Vault.SyncKeys);
		Assert.False(File.Exists(Path.Combine(_b.Dir, VaultStore.KeysFileName)));
		Assert.Equal(0, _b.Sealer.Calls);
	}

	[Fact]
	public async Task RestoringNeedsTheSyncSignInAndKeysOnTheServer()
	{
		await Assert.ThrowsAsync<NotSignedInToSyncException>(() => Restore(_b, _bAccount).RestoreWithPasswordAsync(Strong, Ct));
		await _b.SignInAsync();

		await Assert.ThrowsAsync<SyncNothingToRestoreException>(() => Restore(_b, _bAccount).RestoreWithPasswordAsync(Strong, Ct));
		Assert.False(_bAccount.State().HasAccount);
	}

	[Fact]
	public async Task RestoringWithTheRecoveryKeyAsksForANewPasswordAndRewrapsUnderFreshKdfParameters()
	{
		var recovery = await TurnOnAWithALoginAsync();
		var before = await ServerKeysAsync();
		await _b.SignInAsync();
		var restore = Restore(_b, _bAccount);

		await restore.RestoreWithRecoveryKeyAsync(recovery.ToLowerInvariant().Replace('-', ' '), Ct);

		Assert.True(restore.MustSetNewPassword);
		Assert.False(_bAccount.State().HasAccount);
		Assert.False(File.Exists(Path.Combine(_b.Dir, VaultStore.KeysFileName)));
		Assert.Equal(0, _b.Sealer.Calls);
		await Assert.ThrowsAsync<SyncPasswordTooWeakException>(() => restore.ChangePasswordAfterRecoveryAsync("correct horse", Ct));
		Assert.True(restore.MustSetNewPassword);

		await restore.ChangePasswordAfterRecoveryAsync(NextStrong, Ct);

		Assert.False(restore.MustSetNewPassword);
		Assert.True(_bAccount.State().SignedIn);
		_bAccount.CheckPassword(NextStrong);
		var after = await ServerKeysAsync();
		Assert.NotEqual(before.Kdf.Salt, after.Kdf.Salt);
		Assert.Throws<VaultTamperedException>(() => after.UnwrapWithPassword(Strong));
		using (after.UnwrapWithPassword(NextStrong)) { }
		using (after.UnwrapWithRecoveryKey(recovery)) { }
		Assert.Equal(recovery, await _b.Vault.RecoveryKeyAsync());
		Assert.Equal((await _pcs.A.Client.GetKeysAsync(Ct))!.Version, _b.Vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task ANewPasswordWhoseUploadAnswerWasLostIsFinishedByTheNextTry()
	{
		var recovery = await TurnOnAWithALoginAsync();
		var before = (await _pcs.A.Client.GetKeysAsync(Ct))!.Version;
		await _b.SignInAsync();
		var restore = Restore(_b, _bAccount);
		await restore.RestoreWithRecoveryKeyAsync(recovery, Ct);
		var wire = _pcs.Server.Wire;
		var dropped = false;
		wire.LoseNextAnswerTo = "PUT /keys";
		wire.FailWhen = line =>
		{
			if (dropped || line != "GET /keys" || wire.Count("PUT /keys") == 0) return false;
			dropped = true;
			return true;
		};

		await Assert.ThrowsAsync<HttpRequestException>(() => restore.ChangePasswordAfterRecoveryAsync(NextStrong, Ct));
		Assert.NotNull(_b.Vault.PendingSyncKeys);
		Assert.True(restore.MustSetNewPassword);

		await restore.ChangePasswordAfterRecoveryAsync(NextStrong, Ct);

		Assert.False(restore.MustSetNewPassword);
		_bAccount.CheckPassword(NextStrong);
		Assert.Null(_b.Vault.PendingSyncKeys);
		Assert.Equal(before + 1, (await _pcs.A.Client.GetKeysAsync(Ct))!.Version);
		Assert.Equal(before + 1, _b.Vault.SyncKeys!.Version);
		// A's turn-on and the first try: the second only settled it.
		Assert.Equal(2, wire.Count("PUT /keys"));
	}

	[Fact]
	public async Task AfterARecoveryTheOldPasswordStopsWorkingOnTheNextRestore()
	{
		var recovery = await TurnOnAWithALoginAsync();
		await _b.SignInAsync();
		var restore = Restore(_b, _bAccount);
		await restore.RestoreWithRecoveryKeyAsync(recovery, Ct);
		await restore.ChangePasswordAfterRecoveryAsync(NextStrong, Ct);

		using var c = new SyncPc(_pcs.Server);
		var cAccountDir = Directory.CreateTempSubdirectory("zing-syncrestore-c-").FullName;
		try
		{
			var cAccount = new AccountStore(cAccountDir, new FakeCipher());
			await c.SignInAsync();
			var refused = await Assert.ThrowsAsync<AccountException>(() => Restore(c, cAccount).RestoreWithPasswordAsync(Strong, Ct));
			Assert.Equal(AccountErrorCode.WrongPassword, refused.Code);

			await Restore(c, cAccount).RestoreWithPasswordAsync(NextStrong, Ct);
			cAccount.CheckPassword(NextStrong);
		}
		finally
		{
			Directory.Delete(cAccountDir, recursive: true);
		}
	}

	[Fact]
	public async Task AMistypedOrAnotherRecoveryKeyIsRefusedAndNothingIsCreated()
	{
		var recovery = await TurnOnAWithALoginAsync();
		await _b.SignInAsync();
		var restore = Restore(_b, _bAccount);

		await Assert.ThrowsAsync<RecoveryKeyFormatException>(() => restore.RestoreWithRecoveryKeyAsync(recovery[..^1], Ct));
		await Assert.ThrowsAsync<WrongRecoveryKeyException>(() => restore.RestoreWithRecoveryKeyAsync(RecoveryKey.Generate(), Ct));

		Assert.False(restore.MustSetNewPassword);
		Assert.Null(_b.Vault.VaultId);
		Assert.False(_bAccount.State().HasAccount);
		await Assert.ThrowsAsync<SyncNoNewPasswordNeededException>(() => restore.ChangePasswordAfterRecoveryAsync(NextStrong, Ct));
	}

	[Fact]
	public async Task AForgottenPasswordWithSyncOnIsResetWithTheRecoveryKeyOnTheSamePc()
	{
		var recovery = await TurnOnAWithALoginAsync();
		var id = _pcs.A.Vault.Summaries().Single().Id;
		_pcs.Account.SignOut();
		var restore = Restore(_pcs.A, _pcs.Account);

		await restore.RestoreWithRecoveryKeyAsync(recovery, Ct);
		await restore.ChangePasswordAfterRecoveryAsync(NextStrong, Ct);

		Assert.True(_pcs.Account.State().SignedIn);
		Assert.Equal("ada", _pcs.Account.State().Profile!.Username);
		_pcs.Account.CheckPassword(NextStrong);
		Assert.Equal("bank-secret", await _pcs.A.Vault.PasswordAsync(id));
		using (SyncKeysBlob.Parse(_pcs.A.Vault.SyncKeys!.Blob, _pcs.A.Vault.SyncKeys.Version).UnwrapWithPassword(NextStrong)) { }
		await _pcs.A.SyncAsync();
		Assert.Null(_pcs.A.Engine.Status.Error);
	}

	[Fact]
	public async Task RestoringOverLoginsThatArentTheSyncedOnesIsRefused()
	{
		await TurnOnAWithALoginAsync();
		await _b.Vault.SaveAsync(new NewLogin("https://shop.example.test", "ada", "shop-secret", "Shop", ""));
		var vaultId = _b.Vault.VaultId;
		await _b.SignInAsync();

		await Assert.ThrowsAsync<SyncRestoreWouldReplaceLoginsException>(() => Restore(_b, _bAccount).RestoreWithPasswordAsync(Strong, Ct));

		Assert.Equal(vaultId, _b.Vault.VaultId);
		Assert.Single(_b.Vault.Summaries());
		Assert.False(_bAccount.State().HasAccount);
	}

	[Fact]
	public async Task ARestoreByAnotherGoogleAccountOverThisPcsAccountIsRefusedAndChangesNothing()
	{
		const string bobPassword = "violet-anchor-pebble-harbour";
		_bAccount.SignUp("bob", "Bob", "bob@example.test", bobPassword);
		using (await _b.Vault.KeyMaterialAsync()) { }
		var keysFile = Path.Combine(_b.Dir, VaultStore.KeysFileName);
		var keysBefore = File.ReadAllBytes(keysFile);
		await _pcs.SaveOnAAsync(password: "bank-secret");
		await _pcs.A.SignInAsync(LocalSyncServer.OtherSub);
		var recovery = await new SyncKeys(_pcs.Account, _pcs.A.Vault, _pcs.A.Client).TurnOnAsync(Strong, Ct);
		await _b.SignInAsync(LocalSyncServer.OtherSub);
		var hello = _b.Sealer.Calls;

		await Assert.ThrowsAsync<SyncRestoreOverAnotherAccountException>(() => Restore(_b, _bAccount).RestoreWithPasswordAsync(Strong, Ct));
		var restore = Restore(_b, _bAccount);
		await restore.RestoreWithRecoveryKeyAsync(recovery, Ct);
		await Assert.ThrowsAsync<SyncRestoreOverAnotherAccountException>(() => restore.ChangePasswordAfterRecoveryAsync(NextStrong, Ct));

		_bAccount.CheckPassword(bobPassword);
		Assert.Throws<AccountException>(() => _bAccount.CheckPassword(Strong));
		Assert.Equal(keysBefore, File.ReadAllBytes(keysFile));
		Assert.Equal(hello, _b.Sealer.Calls);
		Assert.Null(_b.Vault.SyncKeys);
	}

	[Fact]
	public async Task APasswordRestoreOnThePcThatTurnedSyncOnKeepsItsRecoveryKeyAndWhetherItWasSaved()
	{
		var recovery = await TurnOnAWithALoginAsync();
		var version = _pcs.A.Vault.SyncKeys!.Version;
		_pcs.Account.SignOut();

		await Restore(_pcs.A, _pcs.Account).RestoreWithPasswordAsync(Strong, Ct);

		Assert.Equal(recovery, await _pcs.A.Vault.RecoveryKeyAsync());
		Assert.False(_pcs.A.Vault.SyncKeys!.RecoveryConfirmed);
		Assert.Equal(version, _pcs.A.Vault.SyncKeys.Version);
		Assert.True(_pcs.Account.State().SignedIn);
	}

	[Fact]
	public async Task APasswordRestoreOverThisPcsLaterKeysIsRefusedWhenTheServerIsBehind()
	{
		await TurnOnAWithALoginAsync();
		var backup = _pcs.Server.Backup();
		await new SyncKeys(_pcs.Account, _pcs.A.Vault, _pcs.A.Client).ChangePasswordAsync(Strong, NextStrong, Ct);
		_pcs.Server.Restore(backup);
		_pcs.Account.SignOut();
		var hello = _pcs.A.Sealer.Calls;

		await Assert.ThrowsAsync<SyncServerBehindException>(() => Restore(_pcs.A, _pcs.Account).RestoreWithPasswordAsync(Strong, Ct));

		Assert.False(_pcs.Account.State().SignedIn);
		_pcs.Account.CheckPassword(NextStrong);
		Assert.Throws<AccountException>(() => _pcs.Account.CheckPassword(Strong));
		Assert.Equal(2, _pcs.A.Vault.SyncKeys!.Version);
		Assert.Equal(hello, _pcs.A.Sealer.Calls);
	}

	[Fact]
	public async Task APasswordRestoreIsRefusedWhenTheSettleCommitsLaterKeysThanTheServerFirstServed()
	{
		await TurnOnAWithALoginAsync();
		var wire = _pcs.Server.Wire;
		string? v1Body = null;
		wire.Rewrite = (line, body) =>
		{
			if (line == "GET /keys") v1Body = body;
			return body;
		};
		await _pcs.A.Client.GetKeysAsync(Ct);
		wire.Rewrite = null;
		var dropped = false;
		wire.LoseNextAnswerTo = "PUT /keys";
		wire.FailWhen = line =>
		{
			if (dropped || line != "GET /keys" || wire.Count("PUT /keys") < 2) return false;
			dropped = true;
			return true;
		};
		await Assert.ThrowsAsync<HttpRequestException>(() => new SyncKeys(_pcs.Account, _pcs.A.Vault, _pcs.A.Client).ChangePasswordAsync(Strong, NextStrong, Ct));
		wire.FailWhen = null;
		Assert.Equal(2, _pcs.A.Vault.PendingSyncKeys!.BaseVersion + 1);
		_pcs.Account.SignOut();
		var served = false;
		wire.Rewrite = (line, body) =>
		{
			if (line != "GET /keys" || served) return body;
			served = true;
			return v1Body!;
		};

		await Assert.ThrowsAsync<SyncServerBehindException>(() => Restore(_pcs.A, _pcs.Account).RestoreWithPasswordAsync(Strong, Ct));

		Assert.False(_pcs.Account.State().SignedIn);
		_pcs.Account.CheckPassword(Strong);
		Assert.Equal(2, _pcs.A.Vault.SyncKeys!.Version);
	}

	/// <summary>A turns sync on (v1, Strong) and changes the password (v2, NextStrong); the server then serves v1's bytes as v3.</summary>
	private async Task<VersionedBlob> ReplayOldKeysAsync()
	{
		await TurnOnAWithALoginAsync();
		var old = (await _pcs.A.Client.GetKeysAsync(Ct))!;
		await new SyncKeys(_pcs.Account, _pcs.A.Vault, _pcs.A.Client).ChangePasswordAsync(Strong, NextStrong, Ct);
		Assert.Equal(3, await _pcs.A.Client.PutKeysAsync(old.Blob, 2, Ct));
		return old;
	}

	[Fact]
	public async Task SyncDoesntTakeOldKeysServedAtALaterVersionForAPasswordChange()
	{
		await ReplayOldKeysAsync();

		await Assert.ThrowsAsync<SyncKeysFormatException>(_pcs.A.SyncAsync);

		Assert.Null(_pcs.A.Vault.SyncKeys!.NewerBlob);
		Assert.Equal("failed", _pcs.A.Engine.Status.Error);
	}

	[Fact]
	public async Task TheGateDoesntTakeTheOldPasswordForOldKeysKeptAtALaterVersion()
	{
		var old = await ReplayOldKeysAsync();
		_pcs.A.Vault.MarkPasswordChangedElsewhere(old.Blob, 3);
		_pcs.Account.AskForPasswordChangedElsewhere();

		await Assert.ThrowsAsync<SyncKeysFormatException>(() => SyncRestore.SignInWithPasswordChangedElsewhereAsync(_pcs.Account, _pcs.A.Vault, Strong, Ct));

		Assert.False(_pcs.Account.State().SignedIn);
		_pcs.Account.CheckPassword(NextStrong);
		Assert.Equal(2, _pcs.A.Vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task ANewPcDoesntRestoreOldKeysServedAtALaterVersion()
	{
		await ReplayOldKeysAsync();
		await _b.SignInAsync();

		await Assert.ThrowsAsync<SyncKeysFormatException>(() => Restore(_b, _bAccount).RestoreWithPasswordAsync(Strong, Ct));

		Assert.False(_bAccount.State().HasAccount);
		Assert.Null(_b.Vault.SyncKeys);
	}

	/// <summary>Wrapped keys for a vault other than A's, under <see cref="Strong"/>.</summary>
	private async Task<byte[]> AnotherVaultsKeysAsync()
	{
		using var keys = await _b.Vault.KeyMaterialAsync();
		return SyncKeysBlob.Create(keys, Strong, RecoveryKey.Generate(), _pcs.Account.State().Profile!, version: 2).ToBytes();
	}

	[Fact]
	public async Task SyncStopsAtNewerKeysForAnotherVault()
	{
		await TurnOnAWithALoginAsync();
		await _pcs.A.Client.PutKeysAsync(await AnotherVaultsKeysAsync(), 1, Ct);

		var stopped = await Assert.ThrowsAsync<SyncProtocolException>(_pcs.A.SyncAsync);

		Assert.Contains("not this vault", stopped.Message);
		Assert.Null(_pcs.A.Vault.SyncKeys!.NewerBlob);
	}

	[Fact]
	public async Task TheGateRefusesNewerKeysKeptForAnotherVault()
	{
		await TurnOnAWithALoginAsync();
		_pcs.A.Vault.MarkPasswordChangedElsewhere(await AnotherVaultsKeysAsync(), 2);
		_pcs.Account.AskForPasswordChangedElsewhere();

		await Assert.ThrowsAsync<SyncKeysFormatException>(() => SyncRestore.SignInWithPasswordChangedElsewhereAsync(_pcs.Account, _pcs.A.Vault, Strong, Ct));

		Assert.False(_pcs.Account.State().SignedIn);
		Assert.Equal(1, _pcs.A.Vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task TwoChangesElsewhereKeepTheLatestAndTheGateOpensWithItsPassword()
	{
		const string third = "orchid-tumble-saddle-proverb-ink";
		await TurnOnAWithALoginAsync();
		byte[] second, latest;
		using (var keys = await _pcs.A.Vault.KeyMaterialAsync())
		{
			var stored = SyncKeysBlob.Parse(_pcs.A.Vault.SyncKeys!.Blob, 1);
			second = stored.WithPassword(keys, NextStrong, 2).ToBytes();
			latest = stored.WithPassword(keys, third, 3).ToBytes();
		}

		_pcs.A.Vault.MarkPasswordChangedElsewhere(second, 2);
		_pcs.A.Vault.MarkPasswordChangedElsewhere(latest, 3);
		_pcs.Account.AskForPasswordChangedElsewhere();

		Assert.Equal(3, _pcs.A.Vault.SyncKeys!.NewerVersion);
		var refused = await Assert.ThrowsAsync<AccountException>(() => SyncRestore.SignInWithPasswordChangedElsewhereAsync(_pcs.Account, _pcs.A.Vault, NextStrong, Ct));
		Assert.Equal(AccountErrorCode.WrongPassword, refused.Code);
		await SyncRestore.SignInWithPasswordChangedElsewhereAsync(_pcs.Account, _pcs.A.Vault, third, Ct);
		Assert.True(_pcs.Account.State().SignedIn);
		_pcs.Account.CheckPassword(third);
		Assert.Equal(3, _pcs.A.Vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task APasswordChangedOnAnotherPcIsNoticedBySyncAndAskedForOnceAtTheNextStart()
	{
		await TurnOnAWithALoginAsync();
		await _b.SignInAsync();
		await Restore(_b, _bAccount).RestoreWithPasswordAsync(Strong, Ct);
		await _b.SyncAsync();
		await new SyncKeys(_pcs.Account, _pcs.A.Vault, _pcs.A.Client).ChangePasswordAsync(Strong, NextStrong, Ct);
		var noticed = 0;
		_b.Vault.SyncPasswordChangedElsewhere += () => noticed++;

		await _b.SyncAsync();

		Assert.Equal(1, noticed);
		Assert.Null(_b.Engine.Status.Error);
		Assert.Equal(_pcs.A.Vault.SyncKeys!.Version, _b.Vault.SyncKeys!.NewerVersion);
		_b.Restart();
		SyncRestore.AskIfPasswordChangedElsewhere(_bAccount, _b.Vault);
		Assert.True(_bAccount.State().PasswordChangedElsewhere);
		Assert.False(_bAccount.State().SignedIn);

		var refused = await Assert.ThrowsAsync<AccountException>(() => SyncRestore.SignInWithPasswordChangedElsewhereAsync(_bAccount, _b.Vault, Strong, Ct));
		Assert.Equal(AccountErrorCode.WrongPassword, refused.Code);
		Assert.Throws<AccountPasswordChangedElsewhereException>(() => _bAccount.SignIn(Strong));
		await SyncRestore.SignInWithPasswordChangedElsewhereAsync(_bAccount, _b.Vault, NextStrong, Ct);

		Assert.True(_bAccount.State().SignedIn);
		Assert.False(_bAccount.State().PasswordChangedElsewhere);
		_bAccount.CheckPassword(NextStrong);
		Assert.Equal(_pcs.A.Vault.SyncKeys.Version, _b.Vault.SyncKeys!.Version);
		Assert.Equal(0, _b.Vault.SyncKeys.NewerVersion);
		_b.Restart();
		SyncRestore.AskIfPasswordChangedElsewhere(_bAccount, _b.Vault);
		Assert.False(_bAccount.State().PasswordChangedElsewhere);
		_b.Vault.SyncPasswordChangedElsewhere += () => noticed++;
		await _b.SyncAsync();
		Assert.Equal(1, noticed);
	}
}
