using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zing.Core.Tests.Net;

/// <summary>A local HTTP server (no TLS) that answers each request path with whatever the test says, so a plain transport can be tried without the internet.</summary>
internal sealed class PlainServer : IDisposable
{
	private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
	private readonly CancellationTokenSource _stop = new();
	private readonly Func<string, byte[]> _respond;

	/// <param name="respond">The raw response (status line, headers and body) for a request path.</param>
	public PlainServer(Func<string, byte[]> respond)
	{
		_respond = respond;
		_listener.Start();
		_ = Task.Run(AcceptAsync);
	}

	public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

	public Uri To(string path) => new($"http://127.0.0.1:{Port}{path}");

	public List<string> Requests { get; } = [];

	public static byte[] Response(string status, string headers, byte[] body) =>
		[.. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];

	public static byte[] Ok(string body) => Response("200 OK", "Content-Type: text/html\r\n", Encoding.UTF8.GetBytes(body));

	public static byte[] Redirect(string to) => Response("302 Found", $"Location: {to}\r\n", []);

	private async Task AcceptAsync()
	{
		while (!_stop.IsCancellationRequested)
		{
			TcpClient client;
			try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
			catch (OperationCanceledException) { return; }
			_ = Task.Run(() => ServeAsync(client));
		}
	}

	private async Task ServeAsync(TcpClient client)
	{
		using var _ = client;
		try
		{
			var stream = client.GetStream();
			var buffer = new byte[4096];
			var seen = new StringBuilder();
			while (!seen.ToString().Contains("\r\n\r\n"))
			{
				var read = await stream.ReadAsync(buffer);
				if (read == 0) return;
				seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
			}
			var head = seen.ToString();
			lock (Requests) Requests.Add(head);
			var path = head.Split(' ')[1];
			await stream.WriteAsync(_respond(path));
		}
		// A client that stops reading (a body past its limit) drops the connection; that is what those tests are about.
		catch (Exception e) when (e is IOException or ObjectDisposedException) { }
	}

	public void Dispose()
	{
		_stop.Cancel();
		_listener.Stop();
	}
}
