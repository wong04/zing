using System.Net;
using System.Security.Cryptography;

namespace Zing.Core.Mcp;

public interface IMcpListener
{
	void Start();
	void Stop();
}

/// <summary>
/// Which of the two listeners run. Each is tracked on its own, so one that couldn't start (a port in use, a token that
/// couldn't be opened) is tried again on the next <see cref="Ensure"/> or token change while the other keeps running.
/// On the UI thread.
/// </summary>
public sealed class McpEndpoint(Func<int, IMcpListener> newHttp, Func<IMcpListener> newPipe, Action<string, Exception> warn)
{
	private IMcpListener? _http;
	private IMcpListener? _pipe;
	private int? _port;
	private string? _httpError;
	private string? _pipeError;

	public bool HttpRunning => _http is not null;
	public bool PipeRunning => _pipe is not null;

	/// <summary>Why a listener isn't running, for Settings; null when both are (or the endpoint is stopped).</summary>
	public string? LastError => (_httpError, _pipeError) switch
	{
		(null, null) => null,
		(var h, null) => h,
		(null, var p) => p,
		(var h, var p) => $"{h} {p}",
	};

	/// <summary>Starts whichever listener isn't running; a new port restarts HTTP on it.</summary>
	public void Ensure(int port)
	{
		if (_port != port) StopHttp();
		_port = port;
		if (_http is null) StartHttp(port);
		if (_pipe is null) StartPipe();
	}

	/// <summary>A new token: HTTP may now start if the old one couldn't be opened.</summary>
	public void TokenChanged()
	{
		if (_port is { } port && _http is null) StartHttp(port);
	}

	public void Stop()
	{
		StopHttp();
		_port = null;
		_pipe?.Stop();
		_pipe = null;
		_pipeError = null;
	}

	private void StopHttp()
	{
		_http?.Stop();
		_http = null;
		_httpError = null;
	}

	private void StartHttp(int port)
	{
		try
		{
			var http = newHttp(port);
			http.Start();
			_http = http;
			_httpError = null;
		}
		catch (HttpListenerException error)
		{
			warn("ai http start", error);
			_httpError = $"Port {port} is in use, so AI tools can't connect over HTTP. Choose another port.";
		}
		catch (CryptographicException error)
		{
			warn("ai token", error);
			_httpError = "Zing couldn't open its AI token. Regenerate it to connect over HTTP.";
		}
	}

	private void StartPipe()
	{
		try
		{
			var pipe = newPipe();
			pipe.Start();
			_pipe = pipe;
			_pipeError = null;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			warn("ai pipe start", error);
			_pipeError = "Another program holds Zing's AI pipe, so the Claude desktop app can't connect.";
		}
	}
}
