using Zing.Core.Sync;
using Zing.Core.Vault;

namespace Zing.Core;

/// <summary>Start over can't erase a vault whose files can't be read: they're left as they are, and so is the account.</summary>
public sealed class StartOverVaultUnreadableException()
	: Exception("Zing can't read the saved-passwords file on this PC, so it didn't start over. Nothing was deleted.");

/// <summary>Deleting the sync account when this PC doesn't sync with one.</summary>
public sealed class StartOverNoSyncAccountException()
	: Exception("This PC doesn't sync with a Google account, so there's no sync account to delete.");

/// <summary>
/// "Forgot your password? Start over." For a person with no Zing password and no recovery key: this PC's account and every password
/// saved in its vault are deleted, so the gate offers a new account. Nothing here is unlocked by the password, which the gate's own
/// "Skip for now" shows is a convenience lock: what goes is what the person can no longer open anyway. The server's copy is deleted
/// only on request, and only with a Google sign-in made for it (<see cref="SyncClient.DeleteServerAccountAsync"/>).
/// </summary>
public static class StartOver
{
	/// <summary>
	/// The sync user this PC turned sync on for (keys.bin, stored or pending), or failing that the one it's signed in as; null when
	/// it syncs with none. A sign-in whose token can't be read still names its user.
	/// </summary>
	public static string? SyncedUser(VaultStore vault)
	{
		if ((vault.SyncKeys?.UserId ?? vault.PendingSyncKeys?.UserId) is { } keysUser) return keysUser;
		try
		{
			return vault.SyncAccount()?.UserId;
		}
		catch (SyncAccountUnreadableException)
		{
			return null;
		}
	}

	/// <summary>
	/// Turns sync off on this PC (signed out with a best-effort revoke, sync keys forgotten), erases the vault, then deletes the account,
	/// last, so a failure part way leaves the gate where it was and Start over can run again.
	/// </summary>
	/// <param name="vault">The vault, open; null when this PC has none.</param>
	/// <param name="client">The vault's sync client; null in a build without sync.</param>
	public static async Task EraseThisPcAsync(AccountStore account, VaultStore? vault, SyncClient? client, CancellationToken cancellation = default)
	{
		if (vault is not null)
		{
			await SyncKeys.TurnOffAsync(vault, client, cancellation);
			using (await SyncKeys.EnterGateAsync(vault, cancellation)) vault.Erase();
		}
		account.DeleteAccount();
	}
}
