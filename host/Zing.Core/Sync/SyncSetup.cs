namespace Zing.Core.Sync;

/// <summary>Google sign-in for sync: the system browser, PKCE and a loopback port in Zing (Zing.Host.GoogleSignIn); the server's fake issuer in e2e.</summary>
public interface IGoogleSignIn
{
	/// <summary>A Google ID token for the person who signed in, with the <c>openid email</c> scopes.</summary>
	Task<string> SignInAsync(CancellationToken cancellation);
}

/// <summary>What this build syncs with (Ruling 48): null when it was built without a Google client ID.</summary>
/// <param name="Handler">How requests reach the server; null is the network. Tests pass the in-process server's.</param>
public sealed record SyncSetup(Uri Server, IGoogleSignIn SignIn, HttpMessageHandler? Handler = null);

/// <summary>What Settings shows: signed in or not, and as whom.</summary>
public sealed record SyncState(bool SignedIn, string? Email);

/// <summary>Turn on sync in a build made without a Google client ID. Never a silent fallback (Ruling 48).</summary>
public sealed class SyncNotSetUpException() : Exception("Sync isn't set up in this build of Zing");

/// <summary>sync.json holds an account whose refresh token doesn't unseal (another Windows user's file, or a damaged one).</summary>
public sealed class SyncAccountUnreadableException(string path, Exception inner)
	: Exception($"The sync sign-in in {path} can't be read: {inner.Message}. Sign out of sync and turn it on again.", inner);
