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
/// couldn't be reached, so its copy expires within 90 days).
/// </summary>
public sealed record SyncState(bool SignedIn, string? Email, string? Notice = null)
{
	public static SyncState SignedInAs(string email) => new(true, email);

	public static SyncState SignedOut(string? notice = null) => new(false, null, notice);
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
