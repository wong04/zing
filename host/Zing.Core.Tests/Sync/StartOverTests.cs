using Zing.Core.Sync;
using Zing.Core.Vault;

namespace Zing.Core.Tests.Sync;

/// <summary>"Forgot your password? Start over." against the real server (fake Google issuer): this PC, and the sync account on request.</summary>
public sealed class StartOverTests : IDisposable
{
	private const string NextStrong = "labour-muzzle-medici-rushes-fiction7";

	private readonly TwoPcs _pcs = new();

	public void Dispose() => _pcs.Dispose();

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private SyncPc A => _pcs.A;

	private string KeysFile => Path.Combine(A.Dir, VaultStore.KeysFileName);

	[Fact]
	public async Task StartingOverErasesTheAccountAndVaultAndSignsOutButLeavesTheServerCopy()
	{
		await _pcs.SaveOnAAsync(password: "bank-secret");
		await _pcs.TurnOnAAsync();
		var user = StartOver.SyncedUser(A.Vault)!;

		await StartOver.EraseThisPcAsync(_pcs.Account, A.Vault, A.Client, Ct);

		Assert.False(_pcs.Account.State().HasAccount);
		Assert.False(_pcs.Account.State().SignedIn);
		Assert.Empty(A.Vault.Summaries());
		Assert.Null(A.Vault.VaultId);
		Assert.Null(A.Vault.SyncKeys);
		Assert.Null(A.Vault.SyncAccount());
		Assert.Null(StartOver.SyncedUser(A.Vault));
		Assert.False(File.Exists(KeysFile));
		Assert.False(File.Exists(Path.Combine(A.Dir, VaultStore.VaultFileName)));
		A.Restart();
		Assert.Empty(A.Vault.Summaries());

		// The server copy stays: a new sign-in as the same Google account finds its keys.
		await A.SignInAsync();
		Assert.Equal(user, A.Vault.SyncAccount()!.UserId);
		Assert.NotNull(await A.Client.GetKeysAsync(Ct));
	}

	[Fact]
	public async Task DeletingTheSyncAccountWithAFreshGoogleSignInLetsTheSameGoogleAccountTurnSyncOnAgainFromScratch()
	{
		await _pcs.SaveOnAAsync(password: "bank-secret");
		await _pcs.TurnOnAAsync();
		var user = StartOver.SyncedUser(A.Vault)!;

		await A.Client.DeleteServerAccountAsync(await _pcs.Server.MintIdTokenAsync(), user, Ct);
		await StartOver.EraseThisPcAsync(_pcs.Account, A.Vault, A.Client, Ct);

		_pcs.Account.SignUp("ada", "Ada", "", NextStrong);
		await A.Vault.SaveAsync(new NewLogin("https://mail.example.test", "ada", "mail-secret", "Mail", ""));
		await A.SignInAsync();
		Assert.NotEqual(user, A.Vault.SyncAccount()!.UserId);
		Assert.Null(await A.Client.GetKeysAsync(Ct));
		await new SyncKeys(_pcs.Account, A.Vault, A.Client).TurnOnAsync(NextStrong, Ct);
		Assert.Equal(1, A.Vault.SyncKeys!.Version);
	}

	[Fact]
	public async Task AGoogleSignInForAnotherAccountDeletesNothingOnTheServer()
	{
		await _pcs.TurnOnAAsync();
		var user = StartOver.SyncedUser(A.Vault)!;

		await Assert.ThrowsAsync<SyncDeleteWithAnotherGoogleAccountException>(async () =>
			await A.Client.DeleteServerAccountAsync(await _pcs.Server.MintIdTokenAsync(LocalSyncServer.OtherSub), user, Ct));

		Assert.NotNull(await A.Client.GetKeysAsync(Ct));
		Assert.True(_pcs.Account.State().HasAccount);
	}

	[Fact]
	public async Task StartingOverWithNoVaultDeletesTheAccount()
	{
		await StartOver.EraseThisPcAsync(_pcs.Account, null, null, Ct);

		Assert.False(_pcs.Account.State().HasAccount);
	}

	[Fact]
	public void ErasingAVaultThatStillSyncsIsRefused()
	{
		A.Vault.SaveSyncAccount(new SyncAccount("user-1", "ada@example.test", "refresh"));

		Assert.Throws<InvalidOperationException>(A.Vault.Erase);
	}
}
