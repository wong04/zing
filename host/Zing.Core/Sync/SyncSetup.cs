using System.Runtime.CompilerServices;
using Zing.Core.Vault;

namespace Zing.Core.Sync;

/// <summary>Google sign-in for sync: the system browser, PKCE and a loopback port in Zing (Zing.Host.GoogleSignIn); the server's fake issuer in e2e.</summary>
public interface IGoogleSignIn
{
	/// <summary>A Google ID token for the person who signed in, with the <c>openid email</c> scopes.</summary>
	Task<string> SignInAsync(CancellationToken cancellation);
}

/// <summary>What this build syncs with (Ruling 48): null when it was built without a Google client ID.</summary>
/// <param name="Handler">How requests reach the server; null is the network. Tests pass the in-process server's.</param>
public sealed record SyncSetup(Uri Server, IGoogleSignIn SignIn, HttpMessageHandler? Handler = null)
{
	/// <summary>Sync as the build's metadata gives it: null for a build without a Google client, which Turn on sync then says.</summary>
	/// <param name="signIn">Makes the Google sign-in from the client ID and secret.</param>
	public static SyncSetup? FromBuild(Func<string, string?> metadata, Func<string, string, IGoogleSignIn> signIn) =>
		SyncBuildSettings.Read(metadata) is { } build ? new SyncSetup(build.Server, signIn(build.ClientId, build.ClientSecret)) : null;
}

/// <summary>
/// The build's sync settings, from the MSBuild properties ZingGoogleClientId, ZingGoogleClientSecret and ZingSyncServerUrl, carried as
/// assembly metadata (Ruling 48). Google's token endpoint refuses a Desktop client without its secret, but only after the person has
/// consented, so a build with one and not the other is refused at launch.
/// </summary>
public sealed record SyncBuildSettings(string ClientId, string ClientSecret, Uri Server)
{
	public static SyncBuildSettings? Read(Func<string, string?> metadata)
	{
		var clientId = Value(metadata, "ZingGoogleClientId");
		var secret = Value(metadata, "ZingGoogleClientSecret");
		if (clientId is null && secret is null) return null;
		if (clientId is null) throw new SyncBuildException("This build has ZingGoogleClientSecret but no ZingGoogleClientId");
		if (secret is null) throw new SyncBuildException("This build has ZingGoogleClientId but no ZingGoogleClientSecret, which Google's token endpoint needs for a Desktop client");
		var server = Value(metadata, "ZingSyncServerUrl") ?? throw new SyncBuildException("This build has a Google client but no ZingSyncServerUrl");
		if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new SyncBuildException($"ZingSyncServerUrl must be an https address, not {server}");
		return new SyncBuildSettings(clientId, secret, uri);
	}

	private static string? Value(Func<string, string?> metadata, string key) => metadata(key) is { Length: > 0 } value ? value : null;
}

/// <summary>The build's sync properties don't make a working sync.</summary>
public sealed class SyncBuildException(string message) : Exception(message);

/// <summary>
/// What Settings shows. <see cref="Email"/> is set exactly when signed in. <see cref="Notice"/>, when signed out: "signed-out" (the
/// server ended the sign-in), "unreadable" (the saved sign-in doesn't unseal), or, from a sign-out only, "kept-on-server" (the server
/// couldn't be reached, so its copy expires within 90 days). <see cref="On"/>: signed in, and this PC turned sync on for that
/// account (its keys are on the server). <see cref="RecoveryPending"/>: this PC turned sync on and the person hasn't yet confirmed they
/// saved the recovery key, whoever is signed in now.
/// </summary>
public sealed record SyncState(bool SignedIn, string? Email, string? Notice = null, bool On = false, bool RecoveryPending = false)
{
	/// <param name="keys">What keys.bin holds, for whichever account.</param>
	public static SyncState SignedInAs(SyncAccount account, VaultSyncKeys? keys = null) =>
		new(true, account.Email, On: keys is not null && keys.UserId == account.UserId, RecoveryPending: Unconfirmed(keys));

	public static SyncState SignedOut(string? notice = null, VaultSyncKeys? keys = null) => new(false, null, notice, RecoveryPending: Unconfirmed(keys));

	private static bool Unconfirmed(VaultSyncKeys? keys) => keys is { RecoveryConfirmed: false };
}

/// <summary>Turning on sync, or changing the password while it's on, with a Zing password below the floor (<see cref="SyncPasswordStrength"/>).</summary>
public sealed class SyncPasswordTooWeakException()
	: Exception("Sync needs a stronger Zing password: about 60 bits or more, such as five random words. Choose a new one to turn on sync.");

/// <summary>Changing the password while sync is on, signed out of sync or signed in as another Google account than the one it was turned on for (Ruling 53).</summary>
public sealed class SyncSignInNeededException()
	: Exception("Sync is on for this PC, so sign in to sync with the Google account you turned it on with before changing your Zing password.");

/// <summary>Changing the password while the vault is off and its keys.bin says sync is on: the keys can't be re-wrapped without the vault.</summary>
public sealed class SyncNeedsTheVaultException()
	: Exception("Turn on the password vault to change your Zing password while sync is on.");

/// <summary>A reset of a forgotten Zing password while sync is on: the server's keys stay wrapped by the old one until Task 14's recovery-key path.</summary>
public sealed class SyncNeedsRecoveryKeyException()
	: Exception("Sync is on for this PC, so resetting a forgotten Zing password needs your recovery key. That isn't available yet.");

/// <summary>A commit of keys that aren't the ones pending: another call replaced them, so the server may not hold these.</summary>
public sealed class SyncPendingKeysReplacedException()
	: Exception("The keys waiting for the sync server were replaced while they were sent, so Zing kept neither.");

/// <summary>Turning on sync while a turn-on for another Google account never finished. That one is cleared, and this one stops.</summary>
public sealed class SyncTurnOnUnfinishedForAnotherAccountException()
	: Exception("This PC hadn't finished turning on sync for another Google account. That was cancelled, so turn on sync again.");

/// <summary>So many items that the manifest listing them would be over the server's limit.</summary>
public sealed class SyncManifestTooLargeException(int items)
	: Exception($"Your {items} saved passwords are more than sync can list, so sync wasn't turned on.");

/// <summary>The keys on the server are newer than the ones this PC stored: the password was changed on another PC.</summary>
public sealed class SyncKeysChangedElsewhereException()
	: Exception("Your Zing password was changed on another PC. Enter the newest password first, then change it again if you want to.");

/// <summary>The Google account already has a vault's keys on the server, so turning on sync here would replace another PC's.</summary>
public sealed class SyncAlreadySetUpElsewhereException()
	: Exception("This Google account already syncs a vault. Restore from sync on this PC instead of turning it on again.");

/// <summary>This PC already turned sync on for the account it's signed in as.</summary>
public sealed class SyncAlreadyOnException() : Exception("Sync is already on for this PC.");

/// <summary>A sync call that needs the keys this PC uploaded, before it turned sync on.</summary>
public sealed class SyncNotOnException() : Exception("Sync isn't on for this PC, so there's no recovery key to show.");

/// <summary>The server refused an item while sync was being turned on, though it held no keys before: it isn't the server this PC expects.</summary>
public sealed class SyncUploadRefusedException(string itemId)
	: Exception($"The sync server already held item {itemId} while sync was being turned on, so Zing stopped.");

/// <summary>
/// The sync floor for the Zing password (Ruling 52): zxcvbn score 4 and at least 10^18 guesses, about 60 bits (zxcvbn-core), the
/// same verdict as the renderer's meter (passwordStrength.vectors.json). Score 4 alone is only about 33 bits.
/// </summary>
public static class SyncPasswordStrength
{
	public const int MinimumScore = 4;

	public const double MinimumGuessesLog10 = 18;

	public static bool IsStrongEnough(string password)
	{
		var verdict = Zxcvbn.Core.EvaluatePassword(password);
		return verdict.Score >= MinimumScore && verdict.GuessesLog10 >= MinimumGuessesLog10;
	}
}

/// <summary>
/// Turning sync on, and changing the Zing password while it's on. Only wrapped keys and ciphertexts reach the server: never the
/// password, nor a hash or verifier made from it (the spec's "nothing derived from the password leaves the PC").
/// </summary>
/// <param name="client">The vault's one client (Rulings 49-51), so its refresh lock covers every call.</param>
public sealed class SyncKeys(AccountStore account, VaultStore vault, SyncClient client)
{
	/// <summary>The server takes at most this many items per POST /changes.</summary>
	public const int PushBatch = 100;

	/// <summary>
	/// Checks the account, its password and the strength floor before anything asks Hello or reaches the server. Then wraps the
	/// index and secret keys with the password key and a new recovery key, seals the profile, and uploads the keys, then every item
	/// in batches, then the manifest. The keys go first and over version 0, so a Google account that already has keys is refused
	/// before anything else is sent. Returns the recovery key, which keys.bin keeps sealed under the secret key. A turn-on whose keys
	/// the server took but whose answer never arrived (or whose PC stopped) is finished by the next one, with the same recovery key.
	/// </summary>
	/// <exception cref="AccountException">NoAccount for a guest, WrongPassword for a password that isn't the account's.</exception>
	public async Task<string> TurnOnAsync(string password, CancellationToken cancellation = default)
	{
		var profile = account.State().Profile ?? throw new AccountException(AccountErrorCode.NoAccount);
		account.CheckPassword(password);
		if (!SyncPasswordStrength.IsStrongEnough(password)) throw new SyncPasswordTooWeakException();
		var signedIn = vault.SyncAccount() ?? throw new NotSignedInToSyncException();
		var gate = Gate();
		await gate.WaitAsync(cancellation);
		try
		{
			return await TurnOnAsync(password, profile, signedIn, cancellation);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Under <see cref="Gate"/>.</summary>
	private async Task<string> TurnOnAsync(string password, AccountProfile profile, SyncAccount signedIn, CancellationToken cancellation)
	{
		if (vault.PendingSyncKeys is { } unfinished && unfinished.UserId != signedIn.UserId)
		{
			vault.DropPendingSyncKeys();
			throw new SyncTurnOnUnfinishedForAnotherAccountException();
		}
		var resumed = await SettlePendingAsync(signedIn, cancellation);
		if (vault.SyncKeys is { } stored && stored.UserId == signedIn.UserId)
		{
			if (!resumed) throw new SyncAlreadyOnException();
			using var held = await vault.KeyMaterialAsync();
			await UploadAsync(Snapshot(held), cancellation);
			return RecoverySeal.Open(held.SecretKey, held.VaultId, stored.SealedRecoveryKey);
		}

		var recovery = RecoveryKey.Generate();
		using var keys = await vault.KeyMaterialAsync();
		var upload = Snapshot(keys);
		var blob = await Task.Run(() => SyncKeysBlob.Create(keys, password, recovery, profile).ToBytes(), cancellation);
		try
		{
			await PutKeysWriteAheadAsync(new VaultPendingKeys(signedIn.UserId, blob, 0, RecoverySeal.Seal(keys.SecretKey, keys.VaultId, recovery)), cancellation);
		}
		catch (SyncConflictException)
		{
			throw new SyncAlreadySetUpElsewhereException();
		}
		await UploadAsync(upload, cancellation);
		return recovery;
	}

	/// <summary>
	/// With sync off, the local change alone (the 8-character minimum). With it on: only while signed in to sync as the account it
	/// was turned on for, and the new password must pass the floor, both checked before Hello. The keys are re-wrapped with the new
	/// password key and uploaded over the version this PC stored, and only then does this PC's own password check change. A server
	/// that moved past that version raises <see cref="SyncKeysChangedElsewhereException"/>, and nothing changes here.
	/// </summary>
	public async Task ChangePasswordAsync(string current, string next, CancellationToken cancellation = default)
	{
		account.CheckPassword(current);
		var syncedAs = vault.SyncKeys?.UserId ?? vault.PendingSyncKeys?.UserId;
		if (syncedAs is null)
		{
			account.ChangePassword(current, next);
			return;
		}
		if (vault.SyncAccount() is not { } signedIn || signedIn.UserId != syncedAs) throw new SyncSignInNeededException();
		if (!SyncPasswordStrength.IsStrongEnough(next)) throw new SyncPasswordTooWeakException();
		var gate = Gate();
		await gate.WaitAsync(cancellation);
		try
		{
			await ChangeSyncedPasswordAsync(current, next, signedIn, cancellation);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Under <see cref="Gate"/>. Settling first may commit a change whose PC stopped before its own password changed, wrapped under a
	/// password nobody is typing now. The keys are wrapped afresh under <paramref name="next"/> over that version either way, so the
	/// server, keys.bin and this PC's password check all end on the password just entered.
	/// </summary>
	private async Task ChangeSyncedPasswordAsync(string current, string next, SyncAccount signedIn, CancellationToken cancellation)
	{
		await SettlePendingAsync(signedIn, cancellation);
		if (vault.SyncKeys is not { } stored || stored.UserId != signedIn.UserId)
		{
			account.ChangePassword(current, next);
			return;
		}

		using var keys = await vault.KeyMaterialAsync();
		var blob = await Task.Run(() => SyncKeysBlob.Parse(stored.Blob).WithPassword(keys, next).ToBytes(), cancellation);
		try
		{
			await PutKeysWriteAheadAsync(new VaultPendingKeys(stored.UserId, blob, stored.Version, stored.SealedRecoveryKey), cancellation);
		}
		catch (SyncConflictException)
		{
			throw new SyncKeysChangedElsewhereException();
		}
		account.ChangePassword(current, next);
	}

	/// <summary>
	/// Settles keys a PUT /keys left pending (its answer was lost, or Zing stopped) against the server's, byte for byte (Ruling 54).
	/// Keys the server holds are committed, a password change's too; a turn-on answers true so the caller finishes it. Keys the
	/// server doesn't hold are dropped. A failed fetch raises and leaves them pending for the next settle. Pending keys for another
	/// account are left. Under <see cref="Gate"/>.
	/// </summary>
	private async Task<bool> SettlePendingAsync(SyncAccount signedIn, CancellationToken cancellation)
	{
		if (vault.PendingSyncKeys is not { } pending || pending.UserId != signedIn.UserId) return false;
		var server = await client.GetKeysAsync(cancellation);
		if (server is null || !server.Blob.AsSpan().SequenceEqual(pending.Blob))
		{
			vault.DropPendingSyncKeys();
			return false;
		}
		vault.CommitPendingSyncKeys(server.Blob, server.Version);
		return pending.BaseVersion == 0;
	}

	/// <summary>One key operation at a time on a vault, whichever window starts it: the pending keys are one slot (Ruling 54).</summary>
	private static readonly ConditionalWeakTable<VaultStore, SemaphoreSlim> Gates = new();

	private SemaphoreSlim Gate() => Gates.GetValue(vault, _ => new SemaphoreSlim(1, 1));

	/// <summary>
	/// PUT /keys with a write-ahead (Ruling 53): the keys are stored as pending first. When the answer is lost, or is a conflict, the
	/// server's keys are fetched and compared byte for byte: the same bytes mean the server took these, and they are committed.
	/// Otherwise they're dropped and the error is raised. When even the server's keys can't be fetched, they stay pending for the next
	/// turn-on or change to settle.
	/// </summary>
	private async Task PutKeysWriteAheadAsync(VaultPendingKeys pending, CancellationToken cancellation)
	{
		vault.SavePendingSyncKeys(pending);
		long version;
		try
		{
			version = await client.PutKeysAsync(pending.Blob, pending.BaseVersion, cancellation);
		}
		catch (SyncConflictException conflict)
		{
			if (conflict.Current is { } current && current.Blob.AsSpan().SequenceEqual(pending.Blob))
			{
				vault.CommitPendingSyncKeys(current.Blob, current.Version);
				return;
			}
			vault.DropPendingSyncKeys();
			throw;
		}
		catch (Exception lost) when (AnswerLost(lost, cancellation))
		{
			var server = await client.GetKeysAsync(cancellation);
			if (server is null || !server.Blob.AsSpan().SequenceEqual(pending.Blob))
			{
				vault.DropPendingSyncKeys();
				throw;
			}
			version = server.Version;
		}
		vault.CommitPendingSyncKeys(pending.Blob, version);
	}

	/// <summary>A request that may or may not have reached the server: a dropped connection, a timeout that wasn't the caller's cancel, or a server failure.</summary>
	private static bool AnswerLost(Exception error, CancellationToken cancellation) => error switch
	{
		HttpRequestException => true,
		OperationCanceledException => !cancellation.IsCancellationRequested,
		SyncHttpException http => (int)http.Status >= 500,
		_ => false,
	};

	/// <summary>The items to upload and the manifest that lists them, sealed before the keys are sent so a manifest the server would refuse stops the turn-on first.</summary>
	private (IReadOnlyList<LoginRecord> Records, byte[] Manifest) Snapshot(VaultKeyMaterial keys)
	{
		var records = vault.Records;
		var manifest = SyncManifest.Seal(keys.IndexKey, keys.VaultId, 1, records.Select(r => new SyncManifestEntry(r.ItemId, r.Version)));
		if (manifest.Length > SyncManifest.MaxBytes) throw new SyncManifestTooLargeException(records.Count);
		return (records, manifest);
	}

	/// <summary>Every item in batches, each leaving the queue as soon as the server takes it, then the manifest.</summary>
	private async Task UploadAsync((IReadOnlyList<LoginRecord> Records, byte[] Manifest) upload, CancellationToken cancellation)
	{
		foreach (var batch in upload.Records.Chunk(PushBatch))
		{
			var pushed = await client.PostChangesAsync([.. batch.Select(SyncWire.NewItem)], cancellation);
			if (pushed.Conflicts.Count > 0) throw new SyncUploadRefusedException(pushed.Conflicts[0].ItemId);
			vault.Dequeue([.. batch.Select(r => new QueuedChange(r.ItemId, r.Version))]);
		}
		await client.PutManifestAsync(upload.Manifest, 0, cancellation);
	}
}

/// <summary>Turn on sync in a build made without a Google client ID. Never a silent fallback (Ruling 48).</summary>
public sealed class SyncNotSetUpException() : Exception("Sync isn't set up in this build of Zing");

/// <summary>The server refused this PC's refresh token (reused, revoked, or the account no longer allowed), so this PC is signed out (Ruling 50).</summary>
public sealed class SyncSignedOutException() : Exception("Signed out of sync, sign in again");

/// <summary>sync.json holds an account whose refresh token doesn't unseal (another Windows user's file, or a damaged one).</summary>
public sealed class SyncAccountUnreadableException(string path, Exception inner)
	: Exception($"This PC's sync sign-in can't be read ({path}: {inner.Message})", inner);

/// <summary>Google sign-in wasn't finished in the browser within 5 minutes, so Zing stopped waiting for it.</summary>
public sealed class SyncSignInTimeoutException(Exception inner)
	: Exception("Google sign-in took longer than 5 minutes, so Zing stopped waiting. Turn on sync to try again.", inner);

/// <summary>
/// The open vault's one <see cref="SyncClient"/>, shared by every window (Rulings 49-51): its refresh lock only works if sign-in,
/// sign-out, turning on and syncing all go through the same instance. A different store gets a new client, and turning the vault
/// off disposes the old one at once.
/// </summary>
public sealed class SyncClients : IDisposable
{
	private readonly Lock _gate = new();
	private readonly SyncSetup _setup;
	private readonly VaultSwitch _vaults;
	private VaultStore? _vault;
	private SyncClient? _client;

	public SyncClients(SyncSetup setup, VaultSwitch vaults)
	{
		_setup = setup;
		_vaults = vaults;
		vaults.Changed += DropStale;
	}

	private void DropStale()
	{
		lock (_gate)
		{
			if (ReferenceEquals(_vault, _vaults.Current)) return;
			_client?.Dispose();
			_client = null;
			_vault = null;
		}
	}

	public SyncClient For(VaultStore vault)
	{
		lock (_gate)
		{
			if (_client is not null && ReferenceEquals(_vault, vault)) return _client;
			_client?.Dispose();
			_vault = vault;
			_client = new SyncClient(_setup.Server, vault, _setup.Handler);
			return _client;
		}
	}

	public void Dispose()
	{
		_vaults.Changed -= DropStale;
		lock (_gate)
		{
			_client?.Dispose();
			_client = null;
			_vault = null;
		}
	}
}
