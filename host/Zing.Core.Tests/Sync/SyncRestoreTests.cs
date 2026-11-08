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

	private SyncRestore Restore(SyncPc pc, AccountStore account) => new(account, pc.Vault, pc.Client);

	private async Task<string> TurnOnAWithALoginAsync()
	{
		await _pcs.SaveOnAAsync(password: "bank-secret");
		await _pcs.A.SignInAsync();
		return await new SyncKeys(_pcs.Account, _pcs.A.Vault, _pcs.A.Client).TurnOnAsync(Strong, Ct);
	}

	private async Task<SyncKeysBlob> ServerKeysAsync() => SyncKeysBlob.Parse((await _pcs.A.Client.GetKeysAsync(Ct))!.Blob);

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
		using (SyncKeysBlob.Parse(_pcs.A.Vault.SyncKeys!.Blob).UnwrapWithPassword(NextStrong)) { }
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
