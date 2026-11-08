using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zing.Core.Sync;

namespace Zing.Host;

/// <summary>Google sign-in didn't finish: the person said no, the redirect didn't match, or the token endpoint refused.</summary>
public sealed class GoogleSignInException(string message) : Exception(message);

/// <summary>
/// Sign in with Google in the system browser, for sync (check C4, Ruling 23): an authorization code over a loopback redirect on a
/// free port, with PKCE and a state check, traded for an ID token with the <c>openid email</c> scopes. Google's token endpoint
/// wants the Desktop client's secret even with PKCE; Google documents it as not confidential for installed apps (Ruling 48). The
/// secret and the tokens are never logged.
/// </summary>
public sealed class GoogleSignIn(string clientId, string? clientSecret) : IGoogleSignIn
{
	private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
	private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

	public async Task<string> SignInAsync(CancellationToken cancellation)
	{
		var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var state = Base64Url(RandomNumberGenerator.GetBytes(16));

		using var listener = StartListener(out var redirect);
		var authorize = AuthorizeEndpoint + "?" + Query(
			("client_id", clientId),
			("redirect_uri", redirect),
			("response_type", "code"),
			("scope", "openid email"),
			("code_challenge", challenge),
			("code_challenge_method", "S256"),
			("state", state));
		Process.Start(new ProcessStartInfo(authorize) { UseShellExecute = true });

		var code = await ReceiveCodeAsync(listener, state, cancellation);
		return await RedeemAsync(code, verifier, redirect, cancellation);
	}

	private async Task<string> RedeemAsync(string code, string verifier, string redirect, CancellationToken cancellation)
	{
		var form = new List<KeyValuePair<string, string>>
		{
			new("client_id", clientId),
			new("code", code),
			new("code_verifier", verifier),
			new("grant_type", "authorization_code"),
			new("redirect_uri", redirect),
		};
		if (clientSecret is not null) form.Add(new("client_secret", clientSecret));
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellation);
		// Google's error body names the problem (invalid_grant and the like) and holds no secret.
		if (!response.IsSuccessStatusCode) throw new GoogleSignInException($"Google's token endpoint answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellation)}");
		var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
		if (!token.TryGetProperty("id_token", out var idToken) || idToken.GetString() is not { Length: > 0 } text) throw new GoogleSignInException("Google's token response has no ID token");
		return text;
	}

	private static HttpListener StartListener(out string redirect)
	{
		// HttpListener can't bind port 0, so a free port is found first, then bound again.
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		redirect = $"http://127.0.0.1:{port}/";
		var listener = new HttpListener();
		listener.Prefixes.Add(redirect);
		listener.Start();
		return listener;
	}

	/// <summary>Waits for Google's redirect. Anything else the browser asks this port for (a favicon) gets a 404 and is ignored.</summary>
	private static async Task<string> ReceiveCodeAsync(HttpListener listener, string state, CancellationToken cancellation)
	{
		using var stop = cancellation.Register(listener.Stop);
		while (true)
		{
			HttpListenerContext context;
			try
			{
				context = await listener.GetContextAsync();
			}
			catch (Exception error) when (cancellation.IsCancellationRequested && error is HttpListenerException or ObjectDisposedException)
			{
				throw new OperationCanceledException("Google sign-in was given up before the browser came back", error, cancellation);
			}

			var query = context.Request.QueryString;
			if (query["code"] is null && query["error"] is null)
			{
				context.Response.StatusCode = 404;
				context.Response.Close();
				continue;
			}
			Answer(context, query["error"] is null ? "You're signed in. You can close this tab and go back to Zing." : "Sign-in didn't finish. You can close this tab and go back to Zing.");
			if (query["state"] != state) throw new GoogleSignInException("Google's redirect doesn't carry this sign-in's state");
			if (query["error"] is { } refused) throw new GoogleSignInException($"Google ended the sign-in: {refused}");
			return query["code"]!;
		}
	}

	private static void Answer(HttpListenerContext context, string text)
	{
		var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>Zing</title><p>{WebUtility.HtmlEncode(text)}</p>");
		context.Response.ContentType = "text/html; charset=utf-8";
		context.Response.OutputStream.Write(bytes);
		context.Response.Close();
	}

	private static string Query(params (string Key, string Value)[] pairs) => string.Join("&", pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The e2e stand-in for Google (AGENTS.md: no test reaches a real service): asks the local sync server's fake issuer
/// (ZING_SERVER_FAKE_GOOGLE=1, which Production refuses) for an ID token. Created only by <see cref="TestHooks.Sync"/>.
/// </summary>
public sealed class FakeIssuerSignIn(Uri server, string sub) : IGoogleSignIn
{
	public async Task<string> SignInAsync(CancellationToken cancellation)
	{
		using var http = new HttpClient { BaseAddress = server };
		using var response = await http.PostAsJsonAsync("fake-google/token", new { sub }, cancellation);
		if (!response.IsSuccessStatusCode) throw new GoogleSignInException($"The local server's fake issuer answered {(int)response.StatusCode}");
		var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
		return token.GetProperty("idToken").GetString() ?? throw new GoogleSignInException("The fake issuer sent no ID token");
	}
}

/// <summary>
/// What this build syncs with (Ruling 48): the Google client ID and the Desktop client's secret from the MSBuild properties
/// ZingGoogleClientId and ZingGoogleClientSecret, and the server from ZingSyncServerUrl. A build without a client ID has no sync,
/// and Turn on sync says so.
/// </summary>
public static class SyncBuild
{
	public static SyncSetup? Setup()
	{
		var clientId = Metadata("ZingGoogleClientId");
		if (clientId is null) return null;
		var server = Metadata("ZingSyncServerUrl") ?? throw new InvalidOperationException("This build has a Google client ID but no ZingSyncServerUrl");
		return new SyncSetup(new Uri(server), new GoogleSignIn(clientId, Metadata("ZingGoogleClientSecret")));
	}

	private static string? Metadata(string key) =>
		typeof(SyncBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value ? value : null;
}
