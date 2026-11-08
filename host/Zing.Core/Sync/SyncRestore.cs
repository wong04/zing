using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>Restore from sync for a Google account that never turned sync on: the server holds no keys for it.</summary>
public sealed class SyncNothingToRestoreException()
	: Exception("This Google account has no synced passwords to restore. Turn on sync on the PC that has them first.");

/// <summary>A recovery key that is well formed but doesn't open the synced keys: another account's, or an old one.</summary>
public sealed class WrongRecoveryKeyException()
	: Exception("That recovery key doesn't open the passwords this Google account syncs.");

/// <summary>
/// Restoring over a vault on this PC that holds logins under other keys: they would no longer open. Nothing in Zing empties the vault
/// (turning it off keeps its files, M4), so the advice is to export, then remove those files with Zing closed.
/// </summary>
public sealed class SyncRestoreWouldReplaceLoginsException(int logins)
	: Exception($"This PC already has {logins} saved passwords that aren't the synced ones, so Zing didn't restore over them. Export them first, then close Zing and delete the vault folder in %APPDATA%\\Zing to start empty.");

/// <summary>
/// Restoring on a PC whose Zing account doesn't sync with the signed-in Google account (I1): only the forgotten-password case, keys.bin
/// holding this Google account's keys for the same vault, may give an existing account a password from sync.
/// </summary>
public sealed class SyncRestoreOverAnotherAccountException()
	: Exception("This PC's Zing account doesn't sync with that Google account, so Zing didn't restore over it. Sign in with this PC's Zing password instead.");

/// <summary>
/// A password restore over this PC's own keys when the sync server holds an older version of them (N1 of the Task 14 re-review): the
/// password typed opens the server's older keys but not this PC's, so taking it would bring an old password back.
/// </summary>
public sealed class SyncServerBehindException()
	: Exception("The sync server holds older keys than this PC, so Zing didn't restore from it. Sign in with your current Zing password, or restore with your recovery key.");

/// <summary>A restore step started while another one in the same window is still running.</summary>
public sealed class SyncRestoreBusyException()
	: Exception("Zing is still working on the last restore step. Wait for it to finish, then try again.");

/// <summary>A new password after recovery asked for when no recovery key restore is waiting for one.</summary>
public sealed class SyncNoNewPasswordNeededException()
	: Exception("Zing isn't waiting for a new password: restore from sync with your recovery key first.");

/// <summary>Showing the recovery key on a PC restored with the password, which never had it.</summary>
public sealed class SyncRecoveryKeyNotOnThisPcException()
	: Exception("This PC was restored with your Zing password, so it doesn't have your recovery key. Use the copy you saved, or show it on the PC that turned sync on.");

/// <summary>Signing in with a password changed on another PC when this PC holds no newer keys to check it against.</summary>
public sealed class SyncNoPasswordChangedElsewhereException()
	: Exception("This PC hasn't seen your Zing password change on another PC.");

/// <summary>
/// A new PC, or a forgotten password, restored from sync (spec, New PC or recovery): the wrapped keys are downloaded and unwrapped with
/// the Zing password or the recovery key, sealed on this PC (DPAPI, and Windows Hello for the secret key), and the local account is
/// made from the synced profile, or, when this PC has one already, given the password. The logins then come with the next sync.
/// Nothing on this PC changes until the keys have opened (Task 14 review, M3 and M4): the vault is asked for only then, and a recovery
/// key restore keeps what it opened in memory until <see cref="ChangePasswordAfterRecoveryAsync"/>, which wraps the keys under the new
/// password and fresh Argon2id parameters and only then writes keys.bin. A PC that has an account takes a restore only for the Google
/// account and vault its keys.bin already syncs (I1). Every write runs under the vault's key lock.
/// </summary>
/// <param name="vault">The vault, opened when the restore is about to write it: the caller turns it on then if it's off.</param>
/// <param name="client">A client signed in as <paramref name="signIn"/> holds.</param>
/// <param name="signIn">The Google sign-in to restore for. The caller keeps it in the vault's sync.json once the restore succeeds.</param>
public sealed class SyncRestore(AccountStore account, Func<VaultStore> vault, SyncClient client, ISyncAccountStore signIn)
{
	/// <summary>What a recovery key opened: the server's keys as they were, the key and the profile; null otherwise.</summary>
	private sealed record Recovered(string UserId, VersionedBlob Server, string RecoveryKey, AccountProfile Profile);

	private Recovered? _recovered;

	/// <summary>A recovery key restore is waiting for <see cref="ChangePasswordAfterRecoveryAsync"/>.</summary>
	public bool MustSetNewPassword => _recovered is not null;

	/// <exception cref="AccountException">WrongPassword for a password that doesn't open the synced keys, or a password this PC's account can't take.</exception>
	/// <exception cref="NotSignedInToSyncException">There's no Google sign-in to restore for.</exception>
	/// <exception cref="SyncNothingToRestoreException">The Google account syncs no vault.</exception>
	/// <exception cref="SyncKeysFormatException">The server's keys aren't Zing's, or were written for another version than they're served at.</exception>
	/// <exception cref="SyncRestoreOverAnotherAccountException">This PC's account syncs with another Google account or vault, or not at all.</exception>
	/// <exception cref="SyncRestoreWouldReplaceLoginsException">This PC holds other logins.</exception>
	/// <exception cref="SyncServerBehindException">This PC holds later keys than the server's, which the password doesn't open.</exception>
	public async Task RestoreWithPasswordAsync(string password, CancellationToken cancellation = default)
	{
		_recovered = null;
		var signedIn = signIn.SyncAccount() ?? throw new NotSignedInToSyncException();
		account.CheckNewPassword(password);
		var server = await ServerKeysAsync(signedIn, cancellation);
		var blob = SyncKeysBlob.Parse(server.Blob, server.Version);
		using var keys = await UnwrapAsync(blob, password, cancellation);
		var profile = blob.OpenProfile(keys.IndexKey);

		var store = vault();
		using var gate = await SyncKeys.EnterGateAsync(store, cancellation);
		RequireRestorableHere(store, signedIn.UserId, keys.VaultId);
		// After the settle (N1 of re-review 2): it may commit later keys, and the check must see the keys that are kept.
		await SyncKeys.SettlePendingAsync(store, client, signedIn, cancellation);
		await RequireServerNotBehindAsync(store, signedIn.UserId, keys.VaultId, server, password, cancellation);
		await store.RestoreKeysAsync(keys, KeptWith(store, signedIn.UserId, keys.VaultId, server));
		MakeAccount(profile, password);
	}

	/// <summary>Checks the key and keeps what it opened for <see cref="ChangePasswordAfterRecoveryAsync"/>; nothing on this PC changes.</summary>
	/// <exception cref="RecoveryKeyFormatException">The code isn't a recovery key (a mistyped character).</exception>
	/// <exception cref="WrongRecoveryKeyException">A recovery key, but not this vault's.</exception>
	/// <exception cref="NotSignedInToSyncException">There's no Google sign-in to restore for.</exception>
	/// <exception cref="SyncNothingToRestoreException">The Google account syncs no vault.</exception>
	public async Task RestoreWithRecoveryKeyAsync(string key, CancellationToken cancellation = default)
	{
		_recovered = null;
		var code = RecoveryKey.Canonical(key);
		var signedIn = signIn.SyncAccount() ?? throw new NotSignedInToSyncException();
		var server = await ServerKeysAsync(signedIn, cancellation);
		var blob = SyncKeysBlob.Parse(server.Blob, server.Version);
		using var keys = UnwrapWithRecoveryKey(blob, code);
		_recovered = new Recovered(signedIn.UserId, server, code, blob.OpenProfile(keys.IndexKey));
	}

	/// <summary>
	/// After a recovery key restore: the keys are sealed on this PC, wrapped under <paramref name="next"/> with fresh Argon2id
	/// parameters (a new salt; the recovery wraps are made again under them, with the same recovery key), uploaded over the version
	/// restored with a write-ahead (Ruling 53), and only then is the account made or given the password. The old password no longer
	/// opens the synced keys. Keys an earlier try left pending are settled first (M2): when the server took them, this try goes on over
	/// them, and when they already open with <paramref name="next"/> there's nothing more to upload.
	/// </summary>
	/// <exception cref="SyncNoNewPasswordNeededException">No recovery key restore is waiting.</exception>
	/// <exception cref="SyncPasswordTooWeakException">Below the sync floor (Ruling 52); still waiting.</exception>
	/// <exception cref="SyncAccountChangedException">The Google sign-in changed since the recovery key was checked.</exception>
	/// <exception cref="SyncRestoreOverAnotherAccountException">This PC's account syncs with another Google account or vault, or not at all.</exception>
	/// <exception cref="SyncKeysChangedElsewhereException">The password was changed on another PC meanwhile; restore again.</exception>
	public async Task ChangePasswordAfterRecoveryAsync(string next, CancellationToken cancellation = default)
	{
		var recovered = _recovered ?? throw new SyncNoNewPasswordNeededException();
		account.CheckNewPassword(next);
		if (!SyncPasswordStrength.IsStrongEnough(next)) throw new SyncPasswordTooWeakException();
		var signedIn = signIn.SyncAccount() ?? throw new NotSignedInToSyncException();
		if (signedIn.UserId != recovered.UserId) throw new SyncAccountChangedException();
		using var keys = UnwrapWithRecoveryKey(SyncKeysBlob.Parse(recovered.Server.Blob, recovered.Server.Version), recovered.RecoveryKey);

		var store = vault();
		using var gate = await SyncKeys.EnterGateAsync(store, cancellation);
		RequireRestorableHere(store, signedIn.UserId, keys.VaultId);
		await SyncKeys.SettlePendingAsync(store, client, signedIn, cancellation);
		var baseVersion = recovered.Server.Version;
		if (store.SyncKeys is { } taken && taken.UserId == signedIn.UserId && store.VaultId == keys.VaultId && taken.Version > baseVersion)
		{
			if (await OpensWithAsync(taken, next, cancellation))
			{
				Finish(recovered, next);
				return;
			}
			baseVersion = taken.Version;
		}
		else
		{
			await store.RestoreKeysAsync(keys, new VaultSyncKeys(signedIn.UserId, recovered.Server.Blob, recovered.Server.Version, RecoverySeal.Seal(keys.SecretKey, keys.VaultId, recovered.RecoveryKey), RecoveryConfirmed: true));
		}

		var blob = await Task.Run(() => SyncKeysBlob.Create(keys, next, recovered.RecoveryKey, recovered.Profile, baseVersion + 1).ToBytes(), cancellation);
		try
		{
			await SyncKeys.PutKeysWriteAheadAsync(store, client, new VaultPendingKeys(signedIn.UserId, blob, baseVersion, RecoverySeal.Seal(keys.SecretKey, keys.VaultId, recovered.RecoveryKey)), cancellation);
		}
		catch (SyncConflictException)
		{
			_recovered = null;
			throw new SyncKeysChangedElsewhereException();
		}
		Finish(recovered, next);
	}

	private void Finish(Recovered recovered, string next)
	{
		MakeAccount(recovered.Profile, next);
		_recovered = null;
	}

	/// <summary>
	/// At the gate after sync found the password changed on another PC (<see cref="AskIfPasswordChangedElsewhere"/>): the password
	/// typed is checked by unwrapping the newer keys sync kept, which then become this PC's, and this PC's own check changes to it.
	/// No network and no Hello. Keys written for another version than the one sync found them at are refused (M1).
	/// </summary>
	/// <exception cref="AccountException">WrongPassword for a password that doesn't open the newer keys.</exception>
	/// <exception cref="SyncKeysFormatException">The newer keys are another vault's, or were written for another version.</exception>
	public static async Task SignInWithPasswordChangedElsewhereAsync(AccountStore account, VaultStore vault, string password, CancellationToken cancellation = default)
	{
		using var gate = await SyncKeys.EnterGateAsync(vault, cancellation);
		if (vault.SyncKeys is not { NewerBlob: { } newer } sync) throw new SyncNoPasswordChangedElsewhereException();
		var blob = SyncKeysBlob.Parse(newer, sync.NewerVersion);
		if (blob.VaultId != vault.VaultId) throw new SyncKeysFormatException($"The newer keys are vault {blob.VaultId}'s, not this vault's ({vault.VaultId})");
		account.CheckNewPassword(password);
		using (await UnwrapAsync(blob, password, cancellation)) { }
		vault.CommitNewerSyncKeys(newer);
		account.SetPasswordFromSync(password);
	}

	/// <summary>At start: when sync found the password changed on another PC, the gate asks for the new one, once.</summary>
	public static void AskIfPasswordChangedElsewhere(AccountStore account, VaultStore? vault)
	{
		if (vault?.SyncKeys is not { NewerBlob: not null }) return;
		// With no account there's nothing to sign in to: the gate offers Restore from sync.
		if (!account.State().HasAccount) return;
		account.AskForPasswordChangedElsewhere();
	}

	private async Task<VersionedBlob> ServerKeysAsync(SyncAccount signedIn, CancellationToken cancellation) =>
		await client.GetKeysAsync(cancellation, asUser: signedIn.UserId) ?? throw new SyncNothingToRestoreException();

	/// <summary>
	/// Under the key lock, before Hello (I1). With no account here, anything may be restored. With one, only the forgotten-password case:
	/// keys.bin already holds this Google account's keys, stored or pending, for the same vault. Otherwise another person's Google account
	/// could give this PC's account its password.
	/// </summary>
	private void RequireRestorableHere(VaultStore store, string userId, string vaultId)
	{
		if (!account.State().HasAccount) return;
		var syncsThis = store.SyncKeys?.UserId == userId || store.PendingSyncKeys?.UserId == userId;
		if (!syncsThis || store.VaultId != vaultId) throw new SyncRestoreOverAnotherAccountException();
	}

	/// <summary>
	/// Under the key lock, after the settle and before Hello (N1 of the Task 14 re-reviews). Over this PC's own keys for the same account and vault at a later
	/// version than the server's, the password must open those too: one that opens only the server's older keys is an old password, and
	/// making it the account password would split it from the sync password.
	/// </summary>
	private static async Task RequireServerNotBehindAsync(VaultStore store, string userId, string vaultId, VersionedBlob server, string password, CancellationToken cancellation)
	{
		if (store.SyncKeys is not { } held || held.UserId != userId || store.VaultId != vaultId || held.Version <= server.Version) return;
		if (!await OpensWithAsync(held, password, cancellation)) throw new SyncServerBehindException();
	}

	/// <summary>
	/// The sync keys a password restore keeps (I2). Over this PC's own keys for the same account and vault, its sealed recovery key and
	/// whether it was saved stay, and so do its keys when they're at a later version than the server's (which the password was checked
	/// against first). Otherwise the server's, with no
	/// recovery key: a password restore never sees it.
	/// </summary>
	private static VaultSyncKeys KeptWith(VaultStore store, string userId, string vaultId, VersionedBlob server)
	{
		if (store.SyncKeys is not { } held || held.UserId != userId || store.VaultId != vaultId) return new VaultSyncKeys(userId, server.Blob, server.Version, SealedRecoveryKey: null, RecoveryConfirmed: true);
		if (held.Version > server.Version) return held;
		return held with { Blob = server.Blob, Version = server.Version, NewerBlob = held.NewerVersion > server.Version ? held.NewerBlob : null, NewerVersion = held.NewerVersion > server.Version ? held.NewerVersion : 0 };
	}

	private static async Task<bool> OpensWithAsync(VaultSyncKeys taken, string password, CancellationToken cancellation)
	{
		try
		{
			using (await Task.Run(() => SyncKeysBlob.Parse(taken.Blob, taken.Version).UnwrapWithPassword(password), cancellation)) { }
			return true;
		}
		catch (VaultTamperedException)
		{
			return false;
		}
	}

	private static async Task<VaultKeyMaterial> UnwrapAsync(SyncKeysBlob blob, string password, CancellationToken cancellation)
	{
		try
		{
			return await Task.Run(() => blob.UnwrapWithPassword(password), cancellation);
		}
		catch (VaultTamperedException)
		{
			throw new AccountException(AccountErrorCode.WrongPassword);
		}
	}

	private static VaultKeyMaterial UnwrapWithRecoveryKey(SyncKeysBlob blob, string code)
	{
		try
		{
			return blob.UnwrapWithRecoveryKey(code);
		}
		catch (VaultTamperedException)
		{
			throw new WrongRecoveryKeyException();
		}
	}

	/// <summary>The account from the synced profile on a new PC; on one that has an account, its profile stays and its password changes.</summary>
	private void MakeAccount(AccountProfile profile, string password)
	{
		if (account.State().HasAccount)
		{
			account.SetPasswordFromSync(password);
			return;
		}
		account.SignUp(profile.Username, profile.DisplayName, profile.Email, password);
	}
}
