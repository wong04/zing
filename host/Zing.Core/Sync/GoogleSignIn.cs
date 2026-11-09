using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zing.Core.Sync;

/// <summary>Google sign-in didn't finish: the person said no, or the token endpoint refused.</summary>
public sealed class GoogleSignInException(string message) : Exception(message);

/// <summary>The person closed the sign-in window before Google sent them back: the sign-in is cancelled, as Cancel in Settings does.</summary>
public sealed class SignInWindowClosedException() : OperationCanceledException("The Google sign-in window was closed.");

/// <summary>
/// Another sign-in started while this one's window was open, and took the window over (only one is ever open): this one is
/// cancelled, and the window's answer goes to the newer one.
/// </summary>
public sealed class SignInTakenOverException() : OperationCanceledException("Another Google sign-in took over the open sign-in window.");

/// <summary>
/// The sign-in window couldn't show Google's page (its engine didn't start, or its process ended): an error Settings shows, not a
/// cancellation, since the person didn't choose to stop.
/// </summary>
public sealed class SignInWindowFailedException(Exception inner) : Exception("Zing couldn't open the Google sign-in window. Try turning on sync again.", inner);

/// <summary>Google's OAuth endpoints: Google's own, or an e2e run's local fake (Zing.Host.TestHooks.Sync), which never reaches Google.</summary>
public sealed record GoogleEndpoints(Uri Authorize, Uri Token)
{
	public static GoogleEndpoints Google { get; } = new(new Uri("https://accounts.google.com/o/oauth2/v2/auth"), new Uri("https://oauth2.googleapis.com/token"));
}

/// <summary>
/// The small window Google's sign-in page shows in, owned by Zing's window (Zing.Host.SignInPopup). Behind this interface so the
/// host tests drive a sign-in without WebView2.
/// </summary>
public interface ISignInPopup
{
	/// <summary>Completes once the window is gone, whoever closed it; faults with <see cref="SignInWindowFailedException"/> when it couldn't show the page.</summary>
	Task Closed { get; }

	/// <summary>Brings the window in front of the others, restoring it if it was minimised.</summary>
	void BringToFront();

	/// <summary>Closes the window; nothing happens when it's already gone.</summary>
	void Close();
}

/// <summary>
/// Sign in with Google for sync (check C4): the authorize page in a small Zing window (<see cref="ISignInPopup"/>), and an
/// authorization code over a loopback redirect, with PKCE and a state check, traded for an ID token with the <c>openid email</c>
/// scopes. Google's token endpoint wants the Desktop client's secret even with PKCE; Google documents it as not confidential for
/// installed apps (Ruling 48). The secret and the tokens are never logged. Only one sign-in window is open at a time: a sign-in
/// started while one is open brings it to the front and takes it over.
/// </summary>
/// <param name="openPopup">Opens the sign-in window at the authorize address. Called on the thread that calls <see cref="SignInAsync"/>.</param>
/// <param name="warn">Told what tidying up after a sign-in couldn't do (closing its window), which no caller is left to hear.</param>
public sealed class GoogleSignIn(string clientId, string clientSecret, GoogleEndpoints endpoints, Func<Uri, ISignInPopup> openPopup, Action<string, Exception> warn) : IGoogleSignIn
{
	private const int MaxRequestBytes = 16 * 1024;
	/// <summary>Short: connections are read one at a time, and a browser's idle preconnect mustn't hold up the redirect behind it for long.</summary>
	private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(3);

	private readonly Lock _gate = new();
	private Attempt? _open;

	/// <summary>The authorize address for one sign-in, its one source.</summary>
	private Uri AuthorizeAddress(string redirect, string challenge, string state) => new(endpoints.Authorize + "?" + Query(
		("client_id", clientId),
		("redirect_uri", redirect),
		("response_type", "code"),
		("scope", "openid email"),
		("code_challenge", challenge),
		("code_challenge_method", "S256"),
		("state", state)));

	private ISignInPopup OpenPopup(Uri authorize) => openPopup(authorize);

	private void Warn(string what, Exception error) => warn(what, error);

	/// <exception cref="SignInWindowClosedException">The person closed the window before Google sent them back.</exception>
	/// <exception cref="SignInTakenOverException">Another sign-in took the window over.</exception>
	/// <exception cref="GoogleSignInException">Google ended the sign-in, or its token endpoint refused the code.</exception>
	/// <exception cref="SignInWindowFailedException">The window couldn't show Google's page.</exception>
	public async Task<string> SignInAsync(CancellationToken cancellation)
	{
		cancellation.ThrowIfCancellationRequested();
		Attempt attempt;
		Task<string> code;
		lock (_gate)
		{
			if (_open is { } open && open.TryJoin(cancellation) is { } joined)
			{
				attempt = open;
				code = joined;
			}
			else
			{
				// Kept as the open one only once it started: one that failed to (no port, no window) must not be joined.
				var fresh = new Attempt(this);
				code = fresh.Start(cancellation);
				attempt = _open = fresh;
			}
		}
		return await RedeemAsync(await code, attempt.Verifier, attempt.Redirect, cancellation);
	}

	/// <summary>
	/// One sign-in window and its loopback listener. Whoever joined last waits for its code; the window and listener end when the
	/// code or Google's error arrives, when the window closes, or when the last one waiting stops waiting. All of its state changes
	/// under the sign-in's lock.
	/// </summary>
	private sealed class Attempt
	{
		private readonly GoogleSignIn _owner;
		private readonly CancellationTokenSource _stop = new();
		private readonly string _state = Base64Url(RandomNumberGenerator.GetBytes(16));
		// Bound by the socket itself to 127.0.0.1 on a port the system picks: nothing else on the network reaches it, and no other process can take the port first.
		private readonly TcpListener _listener = new(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
		private ISignInPopup? _popup;
		private TaskCompletionSource<string>? _waiter;
		private bool _ended;

		public Attempt(GoogleSignIn owner)
		{
			_owner = owner;
			Verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
		}

		public string Verifier { get; }

		public string Redirect { get; private set; } = "";

		/// <summary>Under the lock: listens, opens the window at the authorize address, and waits for it with the first waiter.</summary>
		public Task<string> Start(CancellationToken cancellation)
		{
			var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
			try
			{
				_listener.Start();
				Redirect = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
				_popup = _owner.OpenPopup(_owner.AuthorizeAddress(Redirect, challenge, _state));
			}
			catch
			{
				_ended = true;
				_listener.Stop();
				_stop.Dispose();
				throw;
			}
			var waiting = Wait(cancellation);
			_ = RunAsync(_popup).ContinueWith(run => _owner.Warn("google sign-in window", run.Exception!.GetBaseException()), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			return waiting;
		}

		/// <summary>Under the lock: takes the open window over and brings it to the front; null when it's already ending.</summary>
		public Task<string>? TryJoin(CancellationToken cancellation)
		{
			if (_ended || _stop.IsCancellationRequested) return null;
			_waiter!.TrySetException(new SignInTakenOverException());
			var code = Wait(cancellation);
			_popup!.BringToFront();
			return code;
		}

		private Task<string> Wait(CancellationToken cancellation)
		{
			var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			_waiter = waiter;
			var registration = cancellation.Register(() => Leave(waiter, cancellation));
			_ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			return waiter.Task;
		}

		/// <summary>
		/// A waiter stopped waiting (Cancel, the timeout, its Zing window closing). The window ends only if that was the last one
		/// waiting: one that was taken over leaves the window to the newer sign-in.
		/// </summary>
		private void Leave(TaskCompletionSource<string> waiter, CancellationToken cancellation)
		{
			lock (_owner._gate)
			{
				waiter.TrySetCanceled(cancellation);
				if (!_ended && _waiter == waiter) _stop.Cancel();
			}
		}

		/// <summary>What ends the attempt (the code, Google's error, the window closing) goes to whoever waits last.</summary>
		private async Task RunAsync(ISignInPopup popup)
		{
			var receive = ReceiveCodeAsync(_listener, _state, _stop.Token);
			try
			{
				var finished = await Task.WhenAny(receive, popup.Closed);
				lock (_owner._gate)
				{
					var waiter = _waiter!;
					if (finished != receive) waiter.TrySetException(popup.Closed.Exception?.GetBaseException() ?? new SignInWindowClosedException());
					else if (receive.IsCompletedSuccessfully) waiter.TrySetResult(receive.Result);
					else if (receive.Exception is { } error) waiter.TrySetException(error.InnerExceptions);
					// Stopped because the last waiter left, which is already cancelled.
					else waiter.TrySetCanceled();
				}
			}
			finally
			{
				lock (_owner._gate)
				{
					_ended = true;
					if (_owner._open == this) _owner._open = null;
				}
				_stop.Cancel();
				// The listener's loop ends on the cancellation (or already has), before its socket closes under it.
				await Task.WhenAny(receive);
				_listener.Stop();
				try
				{
					popup.Close();
				}
				finally
				{
					_stop.Dispose();
				}
			}
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
		using var response = await http.PostAsync(endpoints.Token, new FormUrlEncodedContent(form), cancellation);
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
			await AnswerAsync(stream, 200, "You can close this window and go back to Zing.", cancellation);
			if (request.Kind == LoopbackRequestKind.Error) throw new GoogleSignInException(request.Value == "access_denied" ? "Google sign-in was cancelled." : $"Google ended the sign-in ({request.Value}). Try turning on sync again.");
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
			// The window closed before the page arrived; what matters is what it sent.
		}
	}

	private static string Query(params (string Key, string Value)[] pairs) => string.Join("&", pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
