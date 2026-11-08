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

//// <summary>
/// What Settings shows. <see cref="Email"/> is set exactly when signed in. <see cref="Notice"/>, when signed out: "signed-out" (the
/// server ended the sign-in), "unreadable" (the saved sign-in doesn't unseal), or, from a sign-out only, "kept-on-server" (the server
/// couldn't be reached, so its copy expires within 90 days). <see cref="On"/>: signed in, and this PC turned sync on for that
/// account (its keys are on the server). <see cref="RecoveryPending"/>: on, and the person hasn't yet confirmed they saved the recovery key.
/// </summary>
public sealed record SyncState(bool SignedIn, string? Email, string? Notice = null, bool On = false, bool RecoveryPending = false)
{
	public static SyncState SignedInAs(string email, VaultSyncKeys? keys = null) => new(true, email, On: keys is not null, RecoveryPending: keys is { RecoveryConfirmed: false });

	public static SyncState SignedOut(string? notice = null) => new(false, null, notice);
}

/// <summary>Turning on sync, or changing the password while it's on, with a Zing password zxcvbn scores below 4.</summary>
public sealed class SyncPasswordTooWeakException()
	: Exception("Sync needs a stronger Zing password: about 60 bits or more, such as five random words. Choose a new one to turn on sync.");

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

/// <summary>The sync floor for the Zing password: zxcvbn score 4 (zxcvbn-core), the same verdict as the renderer's meter (passwordStrength.vectors.json).</summary>
public static class SyncPasswordStrength
{
	public const int MinimumScore = 4;

	public static bool IsStrongEnough(string password) => Zxcvbn.Core.EvaluatePassword(password).Score >= MinimumScore;
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
	/// before anything else is sent. Returns the recovery key, which keys.bin keeps sealed under the secret key.
	/// </summary>
	/// <exception cref="AccountException">NoAccount for a guest, WrongPassword for a password that isn't the account's.</exception>
	public async Task<string> TurnOnAsync(string password, CancellationToken cancellation = default)
	{
		var profile = account.State().Profile ?? throw new AccountException(AccountErrorCode.NoAccount);
		account.CheckPassword(password);
		if (!SyncPasswordStrength.IsStrongEnough(password)) throw new SyncPasswordTooWeakException();
		var signedIn = vault.SyncAccount() ?? throw new NotSignedInToSyncException();
		if (vault.SyncKeys is { } stored && stored.UserId == signedIn.UserId) throw new SyncAlreadyOnException();

		var recovery = RecoveryKey.Generate();
		using var keys = await vault.KeyMaterialAsync();
		var blob = SyncKeysBlob.Create(keys, password, recovery, profile).ToBytes();
		long version;
		try
		{
			version = await client.PutKeysAsync(blob, 0, cancellation);
		}
		catch (SyncConflictException)
		{
			throw new SyncAlreadySetUpElsewhereException();
		}
		vault.SaveSyncKeys(new VaultSyncKeys(signedIn.UserId, blob, version, RecoverySeal.Seal(keys.SecretKey, keys.VaultId, recovery), RecoveryConfirmed: false));

		var records = vault.Records;
		foreach (var batch in records.Chunk(PushBatch))
		{
			var pushed = await client.PostChangesAsync([.. batch.Select(SyncWire.NewItem)], cancellation);
			if (pushed.Conflicts.Count > 0) throw new SyncUploadRefusedException(pushed.Conflicts[0].ItemId);
		}
		var listed = records.Select(r => new SyncManifestEntry(r.ItemId, r.Version)).ToList();
		await client.PutManifestAsync(SyncManifest.Seal(keys.IndexKey, keys.VaultId, 1, listed), 0, cancellation);
		vault.Dequeue([.. listed.Select(e => new QueuedChange(e.ItemId, e.Version))]);
		return recovery;
	}

	/// <summary>
	/// With sync off, the local change alone (the 8-character minimum). With it on: the new password must pass the floor; the keys
	/// are re-wrapped with the new password key and uploaded over the version this PC stored, and only then does this PC's own
	/// password check change. A server that moved past that version raises <see cref="SyncKeysChangedElsewhereException"/>, and
	/// nothing changes here.
	/// </summary>
	public async Task ChangePasswordAsync(string current, string next, CancellationToken cancellation = default)
	{
		account.CheckPassword(current);
		if (vault.SyncKeys is not { } stored)
		{
			account.ChangePassword(current, next);
			return;
		}
		if (!SyncPasswordStrength.IsStrongEnough(next)) throw new SyncPasswordTooWeakException();

		using var keys = await vault.KeyMaterialAsync();
		var blob = SyncKeysBlob.Parse(stored.Blob).WithPassword(keys, next).ToBytes();
		long version;
		try
		{
			version = await client.PutKeysAsync(blob, stored.Version, cancellation);
		}
		catch (SyncConflictException)
		{
			throw new SyncKeysChangedElsewhereException();
		}
		vault.SaveSyncKeys(stored with { Blob = blob, Version = version });
		account.ChangePassword(current, next);
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
/// sign-out, turning on and syncing all go through the same instance. A different store (the vault turned off and on) gets a new
/// client, and the old one is disposed.
/// </summary>
public sealed class SyncClients(SyncSetup setup) : IDisposable
{
	private readonly Lock _gate = new();
	private VaultStore? _vault;
	private SyncClient? _client;

	public SyncClient For(VaultStore vault)
	{
		lock (_gate)
		{
			if (_client is not null && ReferenceEquals(_vault, vault)) return _client;
			_client?.Dispose();
			_vault = vault;
			_client = new SyncClient(setup.Server, vault, setup.Handler);
			return _client;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_client?.Dispose();
			_client = null;
			_vault = null;
		}
	}
}
