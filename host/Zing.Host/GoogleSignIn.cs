using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zing.Core.Sync;

namespace Zing.Host;

/// <summary>Google sign-in didn't finish: the person said no, or the token endpoint refused.</summary>
public sealed class GoogleSignInException(string message) : Exception(message);

/// <summary>
/// Sign in with Google in the system browser, for sync (check C4, Ruling 23): an authorization code over a loopback redirect, with
/// PKCE and a state check, traded for an ID token with the <c>openid email</c> scopes. Google's token endpoint wants the Desktop
/// client's secret even with PKCE; Google documents it as not confidential for installed apps (Ruling 48). The secret and the tokens
/// are never logged.
/// </summary>
public sealed class GoogleSignIn(string clientId, string clientSecret) : IGoogleSignIn
{
	private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
	private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
	private const int MaxRequestBytes = 16 * 1024;
	/// <summary>Short: connections are read one at a time, and a browser's idle preconnect mustn't hold up the redirect behind it for long.</summary>
	private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(3);

	public async Task<string> SignInAsync(CancellationToken cancellation)
	{
		var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var state = Base64Url(RandomNumberGenerator.GetBytes(16));

		// Bound by the socket itself to 127.0.0.1 on a port the system picks: nothing else on the network reaches it, and no other process can take the port first.
		var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
		listener.Start();
		try
		{
			var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
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
		finally
		{
			listener.Stop();
		}
	}

	private async Task<string> RedeemAsync(string code, string verifier, string redirect, CancellationToken cancellation)
	{
		var form = new List<KeyValuePair<string, string>>
		{
			new("client_id", clientId),
			new("client_secret", clientSecret),
			new("code", code),
			new("code_verifier", verifier),
			new("grant_type", "authorization_code"),
			new("redirect_uri", redirect),
		};
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellation);
		if (!response.IsSuccessStatusCode) throw new GoogleSignInException(await RefusalAsync(response, cancellation));
		var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
		if (!token.TryGetProperty("id_token", out var idToken) || idToken.GetString() is not { Length: > 0 } text) throw new GoogleSignInException("Google's answer had no ID token. Try turning on sync again.");
		return text;
	}

	/// <summary>Google's error code and description as one sentence; the raw body stays out of Settings.</summary>
	private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken cancellation)
	{
		var reason = $"status {(int)response.StatusCode}";
		try
		{
			var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
			if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
			{
				reason = body.TryGetProperty("error_description", out var description) && description.GetString() is { Length: > 0 } text ? $"{error.GetString()}: {text}" : error.GetString()!;
			}
		}
		catch (JsonException)
		{
			// Not Google's JSON error shape: the status is all there is to say.
		}
		return $"Google refused the sign-in ({reason}). Try turning on sync again.";
	}

	/// <summary>
	/// Waits for Google's redirect. A request that isn't one (a favicon), or whose state isn't this sign-in's, is answered and
	/// ignored, so another program guessing the port can't end the sign-in.
	/// </summary>
	private static async Task<string> ReceiveCodeAsync(TcpListener listener, string state, CancellationToken cancellation)
	{
		while (true)
		{
			using var connection = await listener.AcceptTcpClientAsync(cancellation);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			timeout.CancelAfter(ConnectionTimeout);
			var stream = connection.GetStream();
			string head;
			try
			{
				head = await ReadRequestHeadAsync(stream, timeout.Token);
			}
			catch (Exception error) when (!cancellation.IsCancellationRequested && error is OperationCanceledException or IOException)
			{
				// A connection that stalls or drops is one more stray request.
				continue;
			}

			var request = LoopbackRedirect.Read(head, state);
			switch (request.Kind)
			{
				case LoopbackRequestKind.NotHttp:
					await AnswerAsync(stream, 400, "This isn't a request Zing understands.", cancellation);
					continue;
				case LoopbackRequestKind.NotFound:
					await AnswerAsync(stream, 404, "Nothing here.", cancellation);
					continue;
				case LoopbackRequestKind.WrongState:
					await AnswerAsync(stream, 400, "This sign-in isn't the one Zing started.", cancellation);
					continue;
			}
			await AnswerAsync(stream, 200, "You can close this tab and go back to Zing.", cancellation);
			if (request.Kind == LoopbackRequestKind.Error) throw new GoogleSignInException(request.Value == "access_denied" ? "Google sign-in was cancelled in the browser." : $"Google ended the sign-in ({request.Value}). Try turning on sync again.");
			return request.Value!;
		}
	}

	/// <summary>The request head as ASCII, up to the blank line or 16 KiB, whichever comes first.</summary>
	private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellation)
	{
		var head = new MemoryStream();
		var buffer = new byte[2048];
		while (head.Length < MaxRequestBytes)
		{
			var read = await stream.ReadAsync(buffer, cancellation);
			if (read == 0) break;
			head.Write(buffer, 0, read);
			if (Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
		}
		return Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
	}

	private static async Task AnswerAsync(NetworkStream stream, int status, string text, CancellationToken cancellation)
	{
		var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>Zing</title><p>{WebUtility.HtmlEncode(text)}</p>");
		var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
		var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
		try
		{
			await stream.WriteAsync(head, cancellation);
			await stream.WriteAsync(body, cancellation);
		}
		catch (IOException)
		{
			// The browser closed the tab before the page arrived; what matters is what it sent.
		}
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

/// <summary>What this build syncs with (Ruling 48), from its assembly metadata (Zing.Host.csproj): see <see cref="SyncBuildSettings"/>.</summary>
public static class SyncBuild
{
	public static SyncSetup? Setup() => SyncSetup.FromBuild(Metadata, (clientId, secret) => new GoogleSignIn(clientId, secret), Faults.Warning);

	private static string? Metadata(string key) =>
		typeof(SyncBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == key)?.Value;
}
