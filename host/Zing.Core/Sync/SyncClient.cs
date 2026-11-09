using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Zing.Core.Sync;

/// <summary>The sync server answered with a status the call can't go on from.</summary>
public class SyncHttpException(HttpStatusCode status, string message) : Exception(message)
{
	public HttpStatusCode Status { get; } = status;
}

/// <summary>A versioned write (keys or manifest) was based on an old version; <see cref="Current"/> is what the server holds now (null when it holds none).</summary>
public sealed class SyncConflictException(VersionedBlob? current)
	: SyncHttpException(HttpStatusCode.Conflict, "The sync server has a newer copy than the one this write was based on")
{
	public VersionedBlob? Current { get; } = current;
}

/// <summary>A sync call with no account to call as: sign in first.</summary>
public sealed class NotSignedInToSyncException() : Exception("Zing isn't signed in to sync on this PC. Sign in with Google first.");

/// <summary>A replaced sign-in's refresh token couldn't be revoked: it stays unused until it expires.</summary>
public sealed class SyncRevokeFailedException(string message) : Exception(message);

/// <summary>The server's answer had no body, or not the one its endpoint documents.</summary>
public sealed class SyncProtocolException(string message) : Exception(message);

/// <summary>Who this PC syncs as. <see cref="Refresh"/> is the server's refresh token, in plain text here and sealed on disk.</summary>
public sealed record SyncAccount(string UserId, string Email, string Refresh);

/// <summary>Where the account is kept. A save must be on disk when it returns: the server has already retired the token it replaces.</summary>
public interface ISyncAccountStore
{
	SyncAccount? SyncAccount();
	void SaveSyncAccount(SyncAccount account);
	/// <summary>The person signed out.</summary>
	void ForgetSyncAccount();
	/// <summary>The server refused the refresh token: forget the account, and remember that the server ended it, for Settings to say.</summary>
	void MarkSyncSignedOut();
}

/// <summary>A sign-in kept in memory only: a restore's, until the restore succeeds and the vault keeps it (M4 of the Task 14 review).</summary>
public sealed class HeldSyncSignIn : ISyncAccountStore
{
	private readonly Lock _gate = new();
	private SyncAccount? _account;

	public SyncAccount? SyncAccount()
	{
		lock (_gate) return _account;
	}

	public void SaveSyncAccount(SyncAccount account)
	{
		lock (_gate) _account = account;
	}

	public void ForgetSyncAccount()
	{
		lock (_gate) _account = null;
	}

	public void MarkSyncSignedOut() => ForgetSyncAccount();
}

/// <summary>The wrapped keys or the account manifest, with the version the server gave it.</summary>
public sealed record VersionedBlob(byte[] Blob, long Version);

/// <summary>One item as the server holds it. Ciphertexts are opaque here.</summary>
public sealed record SyncItem(string ItemId, int Version, long Revision, bool Tombstone, byte[] Summary, byte[] Password, byte[] History);

/// <summary>One item to write, based on the revision of the copy this PC last saw (0 for a new item).</summary>
public sealed record SyncPush(string ItemId, int Version, long BaseRevision, bool Tombstone, byte[] Summary, byte[] Password, byte[] History);

public sealed record SyncAccepted(string ItemId, long Revision);

/// <summary>What a push did: the items written, with their new revisions, and the server's copy of each one refused.</summary>
public sealed record SyncPushResult(IReadOnlyList<SyncAccepted> Accepted, IReadOnlyList<SyncItem> Conflicts);

/// <summary>A page of changes after a revision. <see cref="More"/> means ask again from the last item's revision.</summary>
public sealed record SyncChanges(IReadOnlyList<SyncItem> Items, long Revision, long Horizon, bool More);

public static class SyncJson
{
	/// <summary>The server's: camelCase, and byte arrays as base64.</summary>
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The sync server's HTTP calls (server/Zing.Server), and nothing else. Every call but sign-in carries the 15-minute session; a
/// 401 refreshes it once and retries, and a second 401 raises. Refresh tokens rotate and reusing one revokes them all, so a
/// refresh runs one at a time, is never cancelled once sent, and the rotated token is saved before anything else runs (Ruling 50).
/// A refresh the server refuses signs this PC out.
/// </summary>
public sealed class SyncClient : IDisposable
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

	/// <summary>Longer than a request's: giving up on a refresh the server may have done costs the token (the server's retry window is 2 minutes).</summary>
	private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(90);

	private readonly HttpClient _http;
	private readonly ISyncAccountStore _store;
	private readonly SemaphoreSlim _refreshing = new(1, 1);
	private Session? _session;

	/// <summary>
	/// The last refresh was sent but no answer came back (or it never left this PC; the two can't be told apart). The server treats
	/// the same token again within 2 minutes as a retry, so the sync engine tries again well inside that (N4). Cleared by a refresh that
	/// gets an answer.
	/// </summary>
	public bool RefreshUnanswered { get; private set; }

	/// <summary>The last revoke of a replaced sign-in, which runs in the background (N6): tests wait on it.</summary>
	internal Task Revoking { get; private set; } = Task.CompletedTask;

	private readonly Action<string, Exception> _warn;

	/// <summary>A refresh got no answer, whatever sent it (M12): the sync engine retries within the server's 2-minute window.</summary>
	public event Action? RefreshLost;

	/// <summary>A server session and the user it was issued for, read together so a request is never sent as someone it wasn't meant for.</summary>
	private sealed record Session(string Token, string UserId);

	/// <param name="handler">How requests are sent; null sends them over the network. Not disposed with the client.</param>
	/// <param name="warn">Told when a background revoke fails; null traces it.</param>
	public SyncClient(Uri server, ISyncAccountStore store, HttpMessageHandler? handler = null, Action<string, Exception>? warn = null)
	{
		_warn = warn ?? ((what, error) => System.Diagnostics.Trace.TraceWarning($"{what}: {error}"));
		_http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
		_http.BaseAddress = server;
		// Each call sets its own: a refresh gets longer than the rest.
		_http.Timeout = Timeout.InfiniteTimeSpan;
		_store = store;
	}

	public void Dispose()
	{
		_http.Dispose();
		_refreshing.Dispose();
	}

	/// <summary>
	/// Trades a Google ID token for a session and stores the account (email from the token, which the server has just checked). A
	/// sign-in it replaces is revoked on the server, best effort (Ruling 63).
	/// </summary>
	public async Task<SyncAccount> SignInAsync(string idToken, CancellationToken cancellation = default)
	{
		var email = IdTokenEmail(idToken);
		using var timeout = Limit(cancellation, RequestTimeout);
		using var response = await _http.PostAsJsonAsync("auth/google", new { idToken }, SyncJson.Options, timeout.Token);
		await EnsureSuccess(response, "auth/google", cancellation);
		var login = await Read<LoginResponse>(response, cancellation);
		var account = new SyncAccount(login.UserId, email, login.Refresh);
		SyncAccount? replaced;
		// Under the refresh lock, as AdoptAsync is (M5): a refresh finishing meanwhile can't write the old account back over this one.
		await _refreshing.WaitAsync(CancellationToken.None);
		try
		{
			replaced = StoredToReplace();
			_store.SaveSyncAccount(account);
			_session = new Session(login.Session, login.UserId);
		}
		finally
		{
			_refreshing.Release();
		}
		RevokeReplaced(replaced, account);
		return account;
	}

	/// <summary>
	/// Takes over a sign-in another client made (a restore's, held in memory until the restore succeeded, M4 of the Task 14 review): it's
	/// stored, a session this client held for an earlier sign-in is dropped, and the sign-in it replaces is revoked, best effort
	/// (Ruling 63). The other client is disposed after, unused.
	/// </summary>
	public async Task AdoptAsync(SyncAccount account, CancellationToken cancellation = default)
	{
		SyncAccount? replaced;
		await _refreshing.WaitAsync(cancellation);
		try
		{
			replaced = StoredToReplace();
			_store.SaveSyncAccount(account);
			_session = null;
		}
		finally
		{
			_refreshing.Release();
		}
		RevokeReplaced(replaced, account);
	}

	/// <summary>
	/// Sends the refresh that got no answer again (<see cref="RefreshUnanswered"/>), so the server still takes the same token as a retry:
	/// for a client no sync engine runs on, such as a restore's (final review I3). A refresh answered meanwhile is used instead.
	/// </summary>
	/// <exception cref="InvalidOperationException">The last refresh was answered: another one would spend a token for nothing.</exception>
	public async Task RetryRefreshAsync(CancellationToken cancellation = default)
	{
		if (!RefreshUnanswered) throw new InvalidOperationException("The last refresh was answered, so there is nothing to retry");
		await RefreshAsync(_session, cancellation);
	}

	/// <summary>The sign-in a save is about to replace; null when there's none, or when it doesn't unseal and so can't be revoked.</summary>
	private SyncAccount? StoredToReplace()
	{
		try
		{
			return _store.SyncAccount();
		}
		catch (SyncAccountUnreadableException)
		{
			return null;
		}
	}

	/// <summary>
	/// A replaced sign-in's refresh token is revoked best effort, as signing out does (Ruling 49), in the background so the sign-in
	/// that replaced it answers at once (N6). An unreachable server leaves it to expire unused within 90 days; either way, a revoke
	/// that didn't happen is told to <c>warn</c>.
	/// </summary>
	private void RevokeReplaced(SyncAccount? replaced, SyncAccount account)
	{
		if (replaced is null || replaced.Refresh == account.Refresh) return;
		Revoking = RevokeInBackgroundAsync(replaced.Refresh);
	}

	private async Task RevokeInBackgroundAsync(string refresh)
	{
		try
		{
			if (!await RevokeAsync(refresh, CancellationToken.None)) _warn("revoke a replaced sync sign-in", new SyncRevokeFailedException("The sync server couldn't be reached to revoke a replaced sign-in; it expires unused"));
		}
		catch (Exception error)
		{
			_warn("revoke a replaced sync sign-in", error);
		}
	}

	/// <summary>
	/// Signs this PC out (Ruling 49): asks the server to revoke this sign-in's refresh tokens, then forgets the account whatever the
	/// server said. Answers whether the server revoked them; when it couldn't be reached, its copy expires unused within 90 days.
	/// </summary>
	public Task<bool> SignOutAsync(CancellationToken cancellation = default) => SignOutThenAsync(_store.ForgetSyncAccount, cancellation);

	/// <summary>
	/// Signs out as <see cref="SignOutAsync"/> does, then <paramref name="forget"/> forgets the account, still under the refresh lock: a
	/// sign-in or refresh waiting on it saves only after, so nothing <paramref name="forget"/> wipes was left unrevoked (N3 of the final
	/// re-review). A saved sign-in whose token can't be read has nothing to revoke: it answers false, and is forgotten.
	/// </summary>
	public async Task<bool> SignOutThenAsync(Action forget, CancellationToken cancellation)
	{
		// Under the refresh lock, so a refresh finishing meanwhile can't save the account back.
		await _refreshing.WaitAsync(cancellation);
		try
		{
			var revoked = await RevokeStoredAsync(cancellation);
			forget();
			_session = null;
			return revoked;
		}
		finally
		{
			_refreshing.Release();
		}
	}

	private async Task<bool> RevokeStoredAsync(CancellationToken cancellation)
	{
		SyncAccount? account;
		try
		{
			account = _store.SyncAccount();
		}
		catch (SyncAccountUnreadableException)
		{
			return false;
		}
		return account is null || await RevokeAsync(account.Refresh, cancellation);
	}

	/// <summary>Only an unreachable or failing server answers false; the caller's own cancel still raises.</summary>
	private async Task<bool> RevokeAsync(string refresh, CancellationToken cancellation)
	{
		using var timeout = Limit(cancellation, RequestTimeout);
		try
		{
			using var response = await _http.PostAsJsonAsync("auth/signout", new { refresh }, SyncJson.Options, timeout.Token);
			return response.IsSuccessStatusCode;
		}
		catch (HttpRequestException)
		{
			return false;
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
		{
			return false;
		}
	}

	// asUser, on the calls that take it: sent only as that server user, else SyncAccountChangedException before anything is sent
	// (Ruling 55). Null sends as whoever is signed in.

	/// <summary>The wrapped keys, or null before any were stored.</summary>
	public Task<VersionedBlob?> GetKeysAsync(CancellationToken cancellation = default, string? asUser = null) => GetBlobAsync("keys", asUser, cancellation);

	/// <summary>Stores the wrapped keys over <paramref name="baseVersion"/> and answers the new version.</summary>
	public Task<long> PutKeysAsync(byte[] blob, long baseVersion, CancellationToken cancellation = default, string? asUser = null) => PutBlobAsync("keys", blob, baseVersion, asUser, cancellation);

	/// <summary>The account manifest, or null before one was stored.</summary>
	public Task<VersionedBlob?> GetManifestAsync(CancellationToken cancellation = default, string? asUser = null) => GetBlobAsync("manifest", asUser, cancellation);

	public Task<long> PutManifestAsync(byte[] blob, long baseVersion, CancellationToken cancellation = default, string? asUser = null) => PutBlobAsync("manifest", blob, baseVersion, asUser, cancellation);

	/// <summary>Changes after <paramref name="since"/>. A 410 (a cursor older than the tombstone horizon) raises; the caller resyncs from 0.</summary>
	public async Task<SyncChanges> GetChangesAsync(long since, int? limit = null, CancellationToken cancellation = default, string? asUser = null)
	{
		var path = limit is null ? $"changes?since={since}" : $"changes?since={since}&limit={limit}";
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), asUser, cancellation);
		await EnsureSuccess(response, path, cancellation);
		var page = await Read<ChangesResponse>(response, cancellation);
		return new SyncChanges([.. page.Items.Select(ToItem)], page.Revision, page.Horizon, page.More);
	}

	public async Task<SyncPushResult> PostChangesAsync(IReadOnlyList<SyncPush> items, CancellationToken cancellation = default, string? asUser = null)
	{
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "changes") { Content = JsonContent.Create(new { items }, options: SyncJson.Options) }, asUser, cancellation);
		await EnsureSuccess(response, "changes", cancellation);
		var pushed = await Read<PushResponse>(response, cancellation);
		return new SyncPushResult([.. pushed.Accepted.Select(a => new SyncAccepted(a.ItemId, a.Revision))], [.. pushed.Conflicts.Select(ToItem)]);
	}

	private async Task<VersionedBlob?> GetBlobAsync(string path, string? asUser, CancellationToken cancellation)
	{
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), asUser, cancellation);
		if (response.StatusCode == HttpStatusCode.NotFound) return null;
		await EnsureSuccess(response, path, cancellation);
		return ToBlob(await Read<BlobResponse>(response, cancellation));
	}

	private async Task<long> PutBlobAsync(string path, byte[] blob, long baseVersion, string? asUser, CancellationToken cancellation)
	{
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(new { blob, baseVersion }, options: SyncJson.Options) }, asUser, cancellation);
		if (response.StatusCode == HttpStatusCode.Conflict)
		{
			var current = await Read<BlobResponse>(response, cancellation);
			throw new SyncConflictException(current.Version == 0 ? null : ToBlob(current));
		}
		await EnsureSuccess(response, path, cancellation);
		return (await Read<PutResponse>(response, cancellation)).Version;
	}

	/// <summary>
	/// Sends with the session, refreshing it first if there is none; a 401 refreshes once and sends again. A session this call has
	/// just got that is refused raises at once: a second refresh would not help. A request can't be sent twice, so it is made anew.
	/// </summary>
	private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, string? asUser, CancellationToken cancellation)
	{
		var fresh = _session is null;
		var session = _session ?? await RefreshAsync(stale: null, cancellation);
		var response = await SendWith(request(), session, asUser, cancellation);
		if (response.StatusCode != HttpStatusCode.Unauthorized || fresh) return response;

		response.Dispose();
		var renewed = await RefreshAsync(session, cancellation);
		return await SendWith(request(), renewed, asUser, cancellation);
	}

	private async Task<HttpResponseMessage> SendWith(HttpRequestMessage request, Session session, string? asUser, CancellationToken cancellation)
	{
		using (request)
		{
			if (asUser is not null && session.UserId != asUser) throw new SyncAccountChangedException();
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
			using var timeout = Limit(cancellation, RequestTimeout);
			return await _http.SendAsync(request, timeout.Token);
		}
	}

	/// <summary>
	/// A new session for one that failed (<paramref name="stale"/>; null when there was none). Callers queue here, and one that
	/// finds the session already replaced uses the new one, so two calls failing at once spend one refresh token, not the same one twice.
	/// </summary>
	private async Task<Session> RefreshAsync(Session? stale, CancellationToken cancellation)
	{
		await _refreshing.WaitAsync(cancellation);
		try
		{
			if (_session is { } current && current != stale) return current;

			var account = _store.SyncAccount() ?? throw new NotSignedInToSyncException();
			cancellation.ThrowIfCancellationRequested();

			// Once sent, the server may rotate the token whether or not the answer gets back, so nothing the caller does stops it.
			using var limit = new CancellationTokenSource(RefreshTimeout);
			HttpResponseMessage? answered = null;
			ExceptionDispatchInfo? unanswered = null;
			try
			{
				answered = await _http.PostAsJsonAsync("auth/refresh", new { refresh = account.Refresh }, SyncJson.Options, limit.Token);
			}
			catch (Exception lost) when (lost is HttpRequestException or OperationCanceledException)
			{
				RefreshUnanswered = true;
				unanswered = ExceptionDispatchInfo.Capture(lost);
			}
			if (unanswered is not null)
			{
				// Outside the catch (N2): the engine schedules its retry here, and the caller still gets the request's own error.
				RefreshLost?.Invoke();
				unanswered.Throw();
			}
			RefreshUnanswered = false;
			using var response = answered!;
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				_session = null;
				_store.MarkSyncSignedOut();
				throw new SyncSignedOutException();
			}
			await EnsureSuccess(response, "auth/refresh", limit.Token);
			var refreshed = await Read<RefreshResponse>(response, limit.Token);
			// Saved before anything else: the old token is spent, and only this one gets the account back after a restart.
			_store.SaveSyncAccount(account with { Refresh = refreshed.Refresh });
			var session = new Session(refreshed.Session, account.UserId);
			_session = session;
			cancellation.ThrowIfCancellationRequested();
			return session;
		}
		finally
		{
			_refreshing.Release();
		}
	}

	private static CancellationTokenSource Limit(CancellationToken cancellation, TimeSpan after)
	{
		var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		limit.CancelAfter(after);
		return limit;
	}

	private static async Task EnsureSuccess(HttpResponseMessage response, string path, CancellationToken cancellation)
	{
		if (response.IsSuccessStatusCode) return;
		var body = await response.Content.ReadAsStringAsync(cancellation);
		var detail = body.Length == 0 ? "" : $": {(body.Length > 200 ? body[..200] : body)}";
		throw new SyncHttpException(response.StatusCode, $"The sync server answered {(int)response.StatusCode} to {path}{detail}");
	}

	private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancellation) where T : class
	{
		try
		{
			return await response.Content.ReadFromJsonAsync<T>(SyncJson.Options, cancellation)
				?? throw new SyncProtocolException($"The sync server's answer to {response.RequestMessage?.RequestUri} was empty");
		}
		catch (JsonException error)
		{
			throw new SyncProtocolException($"The sync server's answer to {response.RequestMessage?.RequestUri} wasn't what it documents: {error.Message}");
		}
	}

	/// <summary>
	/// The email the ID token names, read without checking its signature: it is only shown, and the server checks the token
	/// itself. Google includes it with the <c>email</c> scope (Ruling 23).
	/// </summary>
	private static string IdTokenEmail(string idToken)
	{
		var parts = idToken.Split('.');
		if (parts.Length != 3) throw new SyncProtocolException("The Google ID token isn't a JWT");
		try
		{
			using var claims = JsonDocument.Parse(Base64UrlDecode(parts[1]));
			if (claims.RootElement.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String && email.GetString() is { Length: > 0 } text) return text;
			throw new SyncProtocolException("The Google ID token has no email; sign-in must ask for the email scope");
		}
		catch (Exception error) when (error is JsonException or FormatException)
		{
			throw new SyncProtocolException($"The Google ID token's claims can't be read: {error.Message}");
		}
	}

	private static byte[] Base64UrlDecode(string text)
	{
		var padded = text.Replace('-', '+').Replace('_', '/');
		return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
	}

	private static VersionedBlob ToBlob(BlobResponse blob) =>
		new(blob.Blob ?? throw new SyncProtocolException("The sync server sent a version without its blob"), blob.Version);

	private static SyncItem ToItem(ItemResponse item) => new(item.ItemId, item.Version, item.Revision, item.Tombstone, item.Summary, item.Password, item.History);

	private sealed record LoginResponse(string Session, string Refresh, string UserId);

	private sealed record RefreshResponse(string Session, string Refresh);

	private sealed record BlobResponse(byte[]? Blob, long Version);

	private sealed record PutResponse(long Version);

	private sealed record ItemResponse(string ItemId, int Version, long Revision, bool Tombstone, byte[] Summary, byte[] Password, byte[] History);

	private sealed record ChangesResponse(List<ItemResponse> Items, long Revision, long Horizon, bool More);

	private sealed record AcceptedResponse(string ItemId, long Revision);

	private sealed record PushResponse(List<AcceptedResponse> Accepted, List<ItemResponse> Conflicts);
}
