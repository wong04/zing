using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>Restore from sync for a Google account that never turned sync on: the server holds no keys for it.</summary>
public sealed class SyncNothingToRestoreException()
	: Exception("This Google account has no synced passwords to restore. Turn on sync on the PC that has them first.");

/// <summary>A recovery key that is well formed but doesn't open the synced keys: another account's, or an old one.</summary>
public sealed class WrongRecoveryKeyException()
	: Exception("That recovery key doesn't open the passwords this Google account syncs.");

/// <summary>Restoring over a vault on this PC that holds logins under other keys: they would no longer open.</summary>
public sealed class SyncRestoreWouldReplaceLoginsException(int logins)
	: Exception($"This PC already has {logins} saved passwords that aren't the synced ones, so Zing didn't restore over them. Export them first, then turn the password vault off and on to start empty.");

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
/// made from the synced profile, or, when this PC has one already, given the password. The logins then come with the next sync. A
/// recovery key restore is finished by <see cref="ChangePasswordAfterRecoveryAsync"/>, which wraps the keys under the new password and
/// fresh Argon2id parameters; until then there's no account password to set. Every step runs under the vault's key lock.
/// </summary>
/// <param name="client">The vault's one client (Rulings 49-51).</param>
public sealed class SyncRestore(AccountStore account, VaultStore vault, SyncClient client)
{
	/// <summary>The recovery key and the profile a recovery key restore opened, kept for the new password; null otherwise.</summary>
	private (string RecoveryKey, AccountProfile Profile)? _recovered;

	/// <summary>A recovery key restore is waiting for <see cref="ChangePasswordAfterRecoveryAsync"/>.</summary>
	public bool MustSetNewPassword => _recovered is not null;

	/// <exception cref="AccountException">WrongPassword for a password that doesn't open the synced keys, or a password this PC's account can't take.</exception>
	/// <exception cref="NotSignedInToSyncException">This PC isn't signed in to sync.</exception>
	/// <exception cref="SyncNothingToRestoreException">The Google account syncs no vault.</exception>
	/// <exception cref="SyncRestoreWouldReplaceLoginsException">This PC holds other logins.</exception>
	public async Task RestoreWithPasswordAsync(string password, CancellationToken cancellation = default)
	{
		using var gate = await SyncKeys.EnterGateAsync(vault, cancellation);
		var (user, server, blob) = await ServerKeysAsync(cancellation);
		account.CheckNewPassword(password);
		AccountProfile profile;
		using (var keys = await UnwrapAsync(blob, password, cancellation))
		{
			profile = blob.OpenProfile(keys.IndexKey);
			await vault.RestoreKeysAsync(keys, new VaultSyncKeys(user, server.Blob, server.Version, SealedRecoveryKey: null, RecoveryConfirmed: true));
		}
		MakeAccount(profile, password);
		_recovered = null;
	}

	/// <summary>The keys are sealed on this PC and the recovery key with them; the account waits for <see cref="ChangePasswordAfterRecoveryAsync"/>.</summary>
	/// <exception cref="RecoveryKeyFormatException">The code isn't a recovery key (a mistyped character).</exception>
	/// <exception cref="WrongRecoveryKeyException">A recovery key, but not this vault's.</exception>
	/// <exception cref="NotSignedInToSyncException">This PC isn't signed in to sync.</exception>
	/// <exception cref="SyncNothingToRestoreException">The Google account syncs no vault.</exception>
	/// <exception cref="SyncRestoreWouldReplaceLoginsException">This PC holds other logins.</exception>
	public async Task RestoreWithRecoveryKeyAsync(string key, CancellationToken cancellation = default)
	{
		_recovered = null;
		var code = RecoveryKey.Canonical(key);
		using var gate = await SyncKeys.EnterGateAsync(vault, cancellation);
		var (user, server, blob) = await ServerKeysAsync(cancellation);
		AccountProfile profile;
		using (var keys = UnwrapWithRecoveryKey(blob, code))
		{
			profile = blob.OpenProfile(keys.IndexKey);
			await vault.RestoreKeysAsync(keys, new VaultSyncKeys(user, server.Blob, server.Version, RecoverySeal.Seal(keys.SecretKey, keys.VaultId, code), RecoveryConfirmed: true));
		}
		_recovered = (code, profile);
	}

	/// <summary>
	/// After a recovery key restore: the keys are wrapped under <paramref name="next"/> with fresh Argon2id parameters (a new salt; the
	/// recovery wraps are made again under them, with the same recovery key), uploaded over the version restored with a write-ahead
	/// (Ruling 53), and only then is the account made or given the password. The old password no longer opens the synced keys.
	/// </summary>
	/// <exception cref="SyncNoNewPasswordNeededException">No recovery key restore is waiting.</exception>
	/// <exception cref="SyncPasswordTooWeakException">Below the sync floor (Ruling 52); still waiting.</exception>
	/// <exception cref="SyncKeysChangedElsewhereException">The password was changed on another PC meanwhile; restore again.</exception>
	public async Task ChangePasswordAfterRecoveryAsync(string next, CancellationToken cancellation = default)
	{
		var (code, profile) = _recovered ?? throw new SyncNoNewPasswordNeededException();
		account.CheckNewPassword(next);
		if (!SyncPasswordStrength.IsStrongEnough(next)) throw new SyncPasswordTooWeakException();
		using var gate = await SyncKeys.EnterGateAsync(vault, cancellation);
		var signedIn = vault.SyncAccount() ?? throw new NotSignedInToSyncException();
		if (vault.SyncKeys is not { } stored || stored.UserId != signedIn.UserId) throw new SyncAccountChangedException();
		byte[] blob;
		using (var keys = UnwrapWithRecoveryKey(SyncKeysBlob.Parse(stored.Blob), code))
		{
			blob = await Task.Run(() => SyncKeysBlob.Create(keys, next, code, profile).ToBytes(), cancellation);
		}
		try
		{
			await SyncKeys.PutKeysWriteAheadAsync(vault, client, new VaultPendingKeys(signedIn.UserId, blob, stored.Version, stored.SealedRecoveryKey), cancellation);
		}
		catch (SyncConflictException)
		{
			_recovered = null;
			throw new SyncKeysChangedElsewhereException();
		}
		MakeAccount(profile, next);
		_recovered = null;
	}

	/// <summary>
	/// At the gate after sync found the password changed on another PC (<see cref="AskIfPasswordChangedElsewhere"/>): the password
	/// typed is checked by unwrapping the newer keys sync kept, which then become this PC's, and this PC's own check changes to it.
	/// No network and no Hello.
	/// </summary>
	/// <exception cref="AccountException">WrongPassword for a password that doesn't open the newer keys.</exception>
	public static async Task SignInWithPasswordChangedElsewhereAsync(AccountStore account, VaultStore vault, string password, CancellationToken cancellation = default)
	{
		using var gate = await SyncKeys.EnterGateAsync(vault, cancellation);
		if (vault.SyncKeys is not { NewerBlob: { } newer }) throw new SyncNoPasswordChangedElsewhereException();
		var blob = SyncKeysBlob.Parse(newer);
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

	/// <summary>Under the key lock: pending keys settled first (a forgotten password on a PC whose change never finished), then the server's.</summary>
	private async Task<(string User, VersionedBlob Server, SyncKeysBlob Blob)> ServerKeysAsync(CancellationToken cancellation)
	{
		var signedIn = vault.SyncAccount() ?? throw new NotSignedInToSyncException();
		await SyncKeys.SettlePendingAsync(vault, client, signedIn, cancellation);
		var server = await client.GetKeysAsync(cancellation, asUser: signedIn.UserId) ?? throw new SyncNothingToRestoreException();
		return (signedIn.UserId, server, SyncKeysBlob.Parse(server.Blob));
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
