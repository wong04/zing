using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Zing.Core.Sync;

namespace Zing.Core.Tests.Sync;

/// <summary>
/// Google for <see cref="GoogleSignIn"/>'s tests, on this PC: a token endpoint that answers with <paramref name="idToken"/>'s ID
/// token and keeps every form it was sent, and sign-in windows (<see cref="FakeSignInPopup"/>) a test drives as a person would.
/// </summary>
public sealed class FakeGoogle : IDisposable
{
	private readonly HttpListener _listener;
	private readonly Func<Task<string>> _idToken;
	private readonly Task _serving;
	private readonly List<FakeSignInPopup> _popups = [];

	public FakeGoogle(Func<Task<string>> idToken)
	{
		_idToken = idToken;
		(_listener, var port) = LoopbackHttp.Start();
		Endpoints = new GoogleEndpoints(new Uri($"http://127.0.0.1:{port}/authorize"), new Uri($"http://127.0.0.1:{port}/token"));
		_serving = ServeAsync();
	}

	public GoogleEndpoints Endpoints { get; }

	/// <summary>Every form the token endpoint was sent.</summary>
	public ConcurrentQueue<IReadOnlyDictionary<string, string>> TokenRequests { get; } = new();

	public IReadOnlyList<FakeSignInPopup> Popups
	{
		get
		{
			lock (_popups) return [.. _popups];
		}
	}

	public GoogleSignIn SignIn() => new("test-client", "test-secret", Endpoints, Open);

	private FakeSignInPopup Open(Uri authorize)
	{
		var popup = new FakeSignInPopup(authorize);
		lock (_popups) _popups.Add(popup);
		return popup;
	}

	private async Task ServeAsync()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception error) when (error is HttpListenerException or ObjectDisposedException && !_listener.IsListening)
			{
				return;
			}
			using var response = context.Response;
			if (context.Request.HttpMethod != "POST" || context.Request.Url!.AbsolutePath != "/token")
			{
				response.StatusCode = 404;
				continue;
			}
			using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
			var form = HttpUtility.ParseQueryString(await reader.ReadToEndAsync());
			TokenRequests.Enqueue(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
			var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id_token = await _idToken() }));
			response.ContentType = "application/json";
			await response.OutputStream.WriteAsync(body);
		}
	}

	public void Dispose()
	{
		_listener.Stop();
		_listener.Close();
		_serving.Wait(TimeSpan.FromSeconds(5));
	}
}

/// <summary>A sign-in window with no WebView2: the test is the person, and sends Google's redirect itself.</summary>
public sealed class FakeSignInPopup(Uri authorize) : ISignInPopup
{
	private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _broughtToFront;

	public Uri Authorize => authorize;

	public Task Closed => _closed.Task;

	public int BroughtToFront => Volatile.Read(ref _broughtToFront);

	/// <summary>The sign-in closed the window itself.</summary>
	public bool ClosedBySignIn { get; private set; }

	public void BringToFront() => Interlocked.Increment(ref _broughtToFront);

	public void Close()
	{
		if (_closed.Task.IsCompleted) return;
		ClosedBySignIn = true;
		_closed.SetResult();
	}

	/// <summary>The person clicked the window's close button.</summary>
	public void CloseAsThePersonWould() => _closed.TrySetResult();

	/// <summary>A parameter of the authorize address.</summary>
	public string Query(string name) => HttpUtility.ParseQueryString(authorize.Query)[name] ?? throw new KeyNotFoundException($"The authorize address has no {name}");

	/// <summary>Google sending the person back to the loopback port, as the window's page would load it.</summary>
	public async Task<HttpStatusCode> RedirectAsync(string query)
	{
		using var http = new HttpClient();
		using var response = await http.GetAsync($"{Query("redirect_uri")}?{query}");
		return response.StatusCode;
	}
}
