using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

/// <summary>A loopback HTTP server that stands in for an engine's release host, and a scratch folder for the store.</summary>
public sealed class EngineFixture : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _requests = new(StringComparer.Ordinal);
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _loop;
	private readonly List<(string Where, Exception Error)> _errors = [];

	public EngineFixture()
	{
		Root = Path.Combine(Path.GetTempPath(), "zing-engine-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Root);
		Port = FreePort();
		_listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
		_listener.Start();
		_loop = Task.Run(Accept);
	}

	/// <summary>The folder the store keeps engines in.</summary>
	public string Root { get; }

	public int Port { get; }

	public Uri Url(string path) => new($"http://127.0.0.1:{Port}/{path}");

	public IReadOnlyList<(string Where, Exception Error)> Errors => _errors;

	public int Requests(string path) => _requests.GetValueOrDefault("/" + path);

	public void Serve(string path, byte[] body) => Route(path, async context =>
	{
		context.Response.ContentLength64 = body.Length;
		await context.Response.OutputStream.WriteAsync(body);
		context.Response.Close();
	});

	/// <summary>Promises the whole body, sends part of it, and drops the connection.</summary>
	public void ServeTruncated(string path, byte[] body, int sent) => Route(path, async context =>
	{
		context.Response.ContentLength64 = body.Length;
		await context.Response.OutputStream.WriteAsync(body.AsMemory(0, sent));
		await context.Response.OutputStream.FlushAsync();
		context.Response.Abort();
	});

	/// <summary>Sends part of the body, then waits for <paramref name="release"/> before the rest.</summary>
	public void ServeSlowly(string path, byte[] body, int first, Task release) => Route(path, async context =>
	{
		context.Response.ContentLength64 = body.Length;
		await context.Response.OutputStream.WriteAsync(body.AsMemory(0, first));
		await context.Response.OutputStream.FlushAsync();
		await release;
		await context.Response.OutputStream.WriteAsync(body.AsMemory(first));
		context.Response.Close();
	});

	public void ServeRedirect(string path, string to) => Route(path, context =>
	{
		context.Response.StatusCode = 302;
		context.Response.RedirectLocation = Url(to).ToString();
		context.Response.Close();
		return Task.CompletedTask;
	});

	public void ServeStatus(string path, int status) => Route(path, context =>
	{
		context.Response.StatusCode = status;
		context.Response.Close();
		return Task.CompletedTask;
	});

	private void Route(string path, Func<HttpListenerContext, Task> handler) => _routes["/" + path] = handler;

	private async Task Accept()
	{
		while (!_stop.IsCancellationRequested)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception) when (_stop.IsCancellationRequested)
			{
				return;
			}
			_ = Task.Run(() => Handle(context));
		}
	}

	private async Task Handle(HttpListenerContext context)
	{
		var path = context.Request.Url!.AbsolutePath;
		lock (_requests) _requests[path] = _requests.GetValueOrDefault(path) + 1;
		try
		{
			if (!_routes.TryGetValue(path, out var handler))
			{
				context.Response.StatusCode = 404;
				context.Response.Close();
				return;
			}
			await handler(context);
		}
		catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
		{
			// A client that went away mid-body is what the cancel and truncation tests do on purpose.
			lock (_errors) _errors.Add((path, error));
		}
	}

	private static int FreePort()
	{
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}

	public static string Sha256(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

	public static byte[] Bytes(int length, byte seed = 7)
	{
		var body = new byte[length];
		for (var i = 0; i < length; i++) body[i] = (byte)(seed + i * 31);
		return body;
	}

	/// <summary>A zip holding the named entries; names are written as given, so "../x" really is in the archive.</summary>
	public static byte[] Zip(params (string Name, string Content)[] entries)
	{
		using var memory = new MemoryStream();
		using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (name, content) in entries)
			{
				var entry = zip.CreateEntry(name);
				using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
				writer.Write(content);
			}
		}
		return memory.ToArray();
	}

	public Engine SingleFile(string id, byte[] body, string version = "1.0") =>
		new(id, version, Url($"{id}/{id}.exe"), Sha256(body), body.Length, $"{id}.exe", id, "test licence");

	public Engine Archive(string id, byte[] zip, string executable, string version = "1.0") =>
		new(id, version, Url($"{id}/{id}.zip"), Sha256(zip), zip.Length, executable, id, "test licence");

	public EngineStore Store(params Engine[] engines) => Store(new EngineCatalog(engines, Url("yt-dlp/latest/SHA2-256SUMS")));

	public EngineStore Store(EngineCatalog catalog) => new(Root, catalog, new HttpClient(), (where, error) => _errors.Add((where, error)));

	/// <summary>What is in the store's folder now: an engine's folder, or a temp folder that should not outlive an install.</summary>
	public string[] Entries() => Directory.Exists(Root) ? [.. Directory.GetFileSystemEntries(Root).Select(Path.GetFileName).Cast<string>().Order()] : [];

	public void Dispose()
	{
		_stop.Cancel();
		_listener.Close();
		try
		{
			_loop.Wait(TimeSpan.FromSeconds(5));
		}
		catch (AggregateException)
		{
			// The accept loop ends by the listener closing under it.
		}
		if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
	}
}

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
	public void Report(T value) => report(value);
}
