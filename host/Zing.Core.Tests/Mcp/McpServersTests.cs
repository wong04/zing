using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class McpServersTests : IDisposable
{
	private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
	private const string ToolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
	private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private byte[] _token = RandomNumberGenerator.GetBytes(32);
	private readonly McpHttpSessions _sessions = new();
	private readonly List<Exception> _reported = [];
	private readonly McpHttpServer _http;
	private readonly int _port;
	private readonly HttpClient _client = new();

	public McpServersTests()
	{
		for (var attempt = 0; ; attempt++)
		{
			_port = Random.Shared.Next(49152, 65000);
			_http = new McpHttpServer(_port, () => _token, _sessions, Echo, _reported.Add, _ => { });
			try
			{
				_http.Start();
				break;
			}
			catch (HttpListenerException) when (attempt < 20)
			{
			}
		}
	}

	public void Dispose()
	{
		_http.Stop();
		_client.Dispose();
		Assert.Empty(_reported);
	}

	private static Task<string?> Echo(string body) => Task.FromResult<string?>(body.Contains("\"id\"") ? $"{{\"echo\":{body}}}" : null);

	private HttpRequestMessage Post(string body, string? session = null, bool auth = true)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/mcp/") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		if (auth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AiRequestCheck.Encode(_token));
		if (session is not null) request.Headers.Add(McpHttpServer.SessionHeader, session);
		return request;
	}

	[Fact]
	public async Task InitializeThenCallWithTheSession()
	{
		var init = await _client.SendAsync(Post(Initialize), Ct);
		Assert.Equal(HttpStatusCode.OK, init.StatusCode);
		Assert.Equal("application/json", init.Content.Headers.ContentType?.MediaType);
		var session = Assert.Single(init.Headers.GetValues(McpHttpServer.SessionHeader));

		var list = await _client.SendAsync(Post(ToolsList, session), Ct);
		Assert.Equal(HttpStatusCode.OK, list.StatusCode);
		Assert.Contains("tools/list", await list.Content.ReadAsStringAsync(Ct));

		Assert.Equal(HttpStatusCode.Accepted, (await _client.SendAsync(Post(Initialized, session), Ct)).StatusCode);
	}

	[Fact]
	public async Task NoTokenIsUnauthorized() =>
		Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Post(Initialize, auth: false), Ct)).StatusCode);

	[Fact]
	public async Task ForeignOriginIsForbidden()
	{
		var request = Post(Initialize);
		request.Headers.Add("Origin", "https://evil.example");
		Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request, Ct)).StatusCode);
	}

	[Fact]
	public async Task UnknownSessionIsNotFound() =>
		Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Post(ToolsList, "made-up"), Ct)).StatusCode);

	[Fact]
	public async Task GetIsNotAllowed()
	{
		var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}/mcp/");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AiRequestCheck.Encode(_token));
		Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.SendAsync(request, Ct)).StatusCode);
	}

	[Fact]
	public async Task ANewTokenDropsTheSessions()
	{
		var session = Assert.Single((await _client.SendAsync(Post(Initialize), Ct)).Headers.GetValues(McpHttpServer.SessionHeader));
		_token = RandomNumberGenerator.GetBytes(32);
		_sessions.DropAll();
		Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Post(ToolsList, session), Ct)).StatusCode);
	}

	[Fact]
	public async Task ATooBigBodyIsRefused() =>
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.SendAsync(Post(new string(' ', 2 * 1024 * 1024)), Ct)).StatusCode);

	[Fact]
	public async Task PipeAnswersOneLinePerMessage()
	{
		var name = "zing-mcp-test-" + Guid.NewGuid().ToString("N");
		var pipe = new McpPipeServer(name, Echo, _reported.Add, _ => { });
		pipe.Start();
		try
		{
			await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await client.ConnectAsync(5000, Ct);
			using var reader = new StreamReader(client, leaveOpen: true);
			await using var writer = new StreamWriter(client, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
			await writer.WriteLineAsync(Initialized.AsMemory(), Ct);
			await writer.WriteLineAsync(ToolsList.AsMemory(), Ct);
			Assert.Equal($"{{\"echo\":{ToolsList}}}", await reader.ReadLineAsync(Ct));
		}
		finally
		{
			pipe.Stop();
		}
	}

	[Fact]
	public void PipeNameIsInUse()
	{
		var name = "zing-mcp-test-" + Guid.NewGuid().ToString("N");
		var first = new McpPipeServer(name, Echo, _reported.Add, _ => { });
		first.Start();
		try
		{
			Assert.ThrowsAny<Exception>(() => new McpPipeServer(name, Echo, _reported.Add, _ => { }).Start());
		}
		finally
		{
			first.Stop();
		}
	}

	[Fact]
	public void PipeAllowsOnlyTheCurrentUser()
	{
		var rules = McpPipeServer.UserOnly().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
		var rule = Assert.Single(rules);
		Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
		Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
	}

	[Fact]
	public void PipeOwnerIsTheTokenOwnerSoAnElevatedClientAcceptsIt() =>
		Assert.Equal(WindowsIdentity.GetCurrent().Owner, McpPipeServer.UserOnly().GetOwner(typeof(SecurityIdentifier)));

	[Fact]
	public void PipeNameFollowsTheProfileKey() =>
		Assert.Matches(@"^zing-mcp-Zing-ABC-\d+$", McpPipeServer.Name("Zing-ABC"));

	[Fact]
	public async Task APipeLinePastTheLimitIsAParseErrorAndEndsTheConnection()
	{
		var name = "zing-mcp-test-" + Guid.NewGuid().ToString("N");
		var pipe = new McpPipeServer(name, Echo, _reported.Add, _ => { });
		pipe.Start();
		try
		{
			await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await client.ConnectAsync(5000, Ct);
			using var reader = new StreamReader(client, leaveOpen: true);
			var huge = Encoding.UTF8.GetBytes(new string('x', McpHttpServer.MaxBody + 1) + "\n");
			try
			{
				await client.WriteAsync(huge, Ct);
			}
			catch (IOException)
			{
				// The server may hang up before the whole line is written.
			}
			Assert.Contains("-32700", await reader.ReadLineAsync(Ct));
			Assert.Null(await reader.ReadLineAsync(Ct));
		}
		finally
		{
			pipe.Stop();
		}
	}

	[Fact]
	public void ATakenPortThrowsFromStart() =>
		Assert.Throws<HttpListenerException>(() => new McpHttpServer(_port, () => _token, _sessions, Echo, _reported.Add, _ => { }).Start());

	[Fact]
	public async Task LinesAreSplitAndCappedInBytes()
	{
		var lines = new LineReader(new MemoryStream(Encoding.UTF8.GetBytes("ab\r\ncd\n12345\n")), 4);
		Assert.Equal("ab", await lines.Next(Ct));
		Assert.Equal("cd", await lines.Next(Ct));
		Assert.Null(await lines.Next(Ct));
		Assert.True(lines.TooLong);
	}
}
