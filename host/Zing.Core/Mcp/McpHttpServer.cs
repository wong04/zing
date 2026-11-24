using System.Net;
using System.Text;

namespace Zing.Core.Mcp;

/// <summary>
/// The MCP Streamable HTTP transport on <c>http://127.0.0.1:port/mcp/</c>: one JSON-RPC message per POST, answered with
/// <c>application/json</c> (no SSE). Requests are served on the listener's threads; <paramref name="handle"/> takes each
/// message to the UI thread. <paramref name="report"/> gets bugs, <paramref name="warn"/> clients that went away mid-reply.
/// </summary>
public sealed class McpHttpServer(int port, Func<byte[]> token, McpHttpSessions sessions, Func<string, Task<string?>> handle, Action<Exception> report, Action<Exception> warn) : IMcpListener
{
	public const string SessionHeader = "Mcp-Session-Id";

	/// <summary>Far more than any tool call's arguments; a bigger body is refused before it is read in full.</summary>
	public const int MaxBody = 1024 * 1024;

	private readonly HttpListener _listener = new();
	// Set before the listener closes: a pending accept can fail with "aborted" while Close is still under way.
	private volatile bool _stopped;

	/// <summary>Throws <see cref="HttpListenerException"/> when the port is taken.</summary>
	public void Start()
	{
		_listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
		try
		{
			_listener.Start();
		}
		catch (HttpListenerException)
		{
			_listener.Close();
			throw;
		}
		_ = Task.Run(Accept);
	}

	public void Stop()
	{
		_stopped = true;
		_listener.Close();
	}

	private async Task Accept()
	{
		while (true)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync().ConfigureAwait(false);
			}
			catch (Exception) when (_stopped)
			{
				return;
			}
			catch (Exception error)
			{
				report(error);
				return;
			}
			_ = Task.Run(() => ServeGuarded(context));
		}
	}

	private async Task ServeGuarded(HttpListenerContext context)
	{
		try
		{
			await Serve(context.Request, context.Response).ConfigureAwait(false);
		}
		catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException)
		{
			warn(error);
		}
		catch (Exception error)
		{
			report(error);
			TryEnd(context.Response, HttpStatusCode.InternalServerError);
		}
	}

	private async Task Serve(HttpListenerRequest request, HttpListenerResponse response)
	{
		// http.sys accepts on every interface; the prefix's host only matches the Host header, which a remote caller can send.
		if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address))
		{
			End(response, HttpStatusCode.Forbidden);
			return;
		}
		switch (AiRequestCheck.Check(request.Headers["Host"], request.Headers["Origin"], request.Headers["Authorization"], port, token()))
		{
			case AiRequestCheck.Result.Forbidden:
				End(response, HttpStatusCode.Forbidden);
				return;
			case AiRequestCheck.Result.Unauthorized:
				response.AddHeader("WWW-Authenticate", "Bearer");
				End(response, HttpStatusCode.Unauthorized);
				return;
		}
		if (request.Url?.AbsolutePath is not ("/mcp/" or "/mcp"))
		{
			End(response, HttpStatusCode.NotFound);
			return;
		}
		if (request.ContentLength64 > MaxBody)
		{
			End(response, HttpStatusCode.RequestEntityTooLarge);
			return;
		}
		var body = request.HasEntityBody ? await ReadBody(request).ConfigureAwait(false) : "";
		if (body is null)
		{
			End(response, HttpStatusCode.RequestEntityTooLarge);
			return;
		}
		var admission = sessions.Admit(request.HttpMethod, request.Headers[SessionHeader], body);
		switch (admission.Verdict)
		{
			case McpHttpSessions.Verdict.BadRequest:
				End(response, HttpStatusCode.BadRequest);
				return;
			case McpHttpSessions.Verdict.NotFound:
				End(response, HttpStatusCode.NotFound);
				return;
			case McpHttpSessions.Verdict.MethodNotAllowed:
				response.AddHeader("Allow", "POST, DELETE");
				End(response, HttpStatusCode.MethodNotAllowed);
				return;
			case McpHttpSessions.Verdict.Ended:
				End(response, HttpStatusCode.OK);
				return;
		}
		var reply = await handle(body).ConfigureAwait(false);
		if (admission.NewSession is { } id) response.AddHeader(SessionHeader, id);
		if (reply is null)
		{
			End(response, HttpStatusCode.Accepted);
			return;
		}
		var bytes = Encoding.UTF8.GetBytes(reply);
		response.StatusCode = (int)HttpStatusCode.OK;
		response.ContentType = "application/json";
		response.ContentLength64 = bytes.Length;
		await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
		response.Close();
	}

	/// <summary>Null when the body runs past <see cref="MaxBody"/> (a chunked body has no length up front).</summary>
	private static async Task<string?> ReadBody(HttpListenerRequest request)
	{
		using var buffer = new MemoryStream();
		var chunk = new byte[16 * 1024];
		int read;
		while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > MaxBody) return null;
			buffer.Write(chunk, 0, read);
		}
		return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
	}

	private static void End(HttpListenerResponse response, HttpStatusCode status)
	{
		response.StatusCode = (int)status;
		response.ContentLength64 = 0;
		response.Close();
	}

	private void TryEnd(HttpListenerResponse response, HttpStatusCode status)
	{
		try
		{
			End(response, status);
		}
		catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
		{
			warn(error);
		}
	}
}
