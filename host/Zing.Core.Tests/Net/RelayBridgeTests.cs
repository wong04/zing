using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

/// <summary>relay:open through the bridge: each attempt is pushed as it happens, tagged with the run it belongs to.</summary>
public sealed class RelayBridgeTests : IDisposable
{
	private sealed class NoProbes : IProbes
	{
		public Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct) => throw new InvalidOperationException("not used");
		public Task<bool> TcpAsync(string ip, CancellationToken ct) => throw new InvalidOperationException("not used");
		public Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct) => throw new InvalidOperationException("not used");
	}

	private sealed class NoDoh : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => throw new InvalidOperationException("not used");
	}

	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly RelayEngineTests.FakeTransport _transport = new();
	private readonly RelayConsent _consent = new();
	private readonly ChromeApi _api;

	public RelayBridgeTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var app = new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), Net: new NetServices(new NoProbes(), new NoDoh(), _transport), Consent: _consent);
		_api = new ChromeApi(_bridge, app, _host, "test");
	}

	public void Dispose() => _api.Dispose();

	private async Task<JsonElement> Invoke(string input, string runId)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "relay:open", args = new[] { input, runId } });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	private List<JsonElement> Events(string runId) =>
		[.. _host.Pushed.Where(p => p.Channel == "relay:attempt")
			.Select(p => JsonSerializer.SerializeToElement(p.Payload, ZingJson.Options))
			.Where(e => e.GetProperty("runId").GetString() == runId)];

	[Fact]
	public async Task Answers_with_the_result_and_pushes_each_attempt_with_the_run_id()
	{
		_transport.Plain = _ => RelayEngineTests.Ok("<p>hi</p>");
		var reply = await Invoke("example.com", "run-1");
		Assert.True(reply.GetProperty("ok").GetBoolean());
		var value = reply.GetProperty("value");
		Assert.Equal("opened", value.GetProperty("outcome").GetString());
		Assert.Equal("direct", value.GetProperty("strategy").GetString());
		Assert.Equal("<p>hi</p>", value.GetProperty("content").GetString());
		Assert.Equal(["direct:trying", "direct:worked"], Events("run-1").Select(e => $"{e.GetProperty("strategy").GetString()}:{e.GetProperty("status").GetString()}"));
	}

	[Fact]
	public async Task Pushes_from_two_runs_at_once_stay_with_their_own_run()
	{
		var release = new TaskCompletionSource();
		_transport.Plain = async request =>
		{
			if (request.Url.Host == "slow.test") await release.Task;
			return await RelayEngineTests.Ok("ok");
		};
		var slow = Invoke("slow.test", "run-slow");
		var quick = await Invoke("quick.test", "run-quick");
		Assert.True(quick.GetProperty("ok").GetBoolean());
		release.SetResult();
		await slow;
		Assert.Equal(2, Events("run-slow").Count);
		Assert.Equal(2, Events("run-quick").Count);
		Assert.Equal(4, _host.Pushed.Count(p => p.Channel == "relay:attempt"));
	}

	[Fact]
	public async Task Third_parties_wait_for_the_session_consent()
	{
		var needs = (await Invoke("example.com", "run-1")).GetProperty("value");
		Assert.Equal("needsConsent", needs.GetProperty("outcome").GetString());
		Assert.DoesNotContain(_transport.Calls, c => c.Request.Url.Host == "r.jina.ai");

		_consent.Allow();
		_transport.Plain = request => request.Url.Host == "r.jina.ai" ? RelayEngineTests.Ok("text") : throw new HttpRequestException("blocked");
		var opened = (await Invoke("example.com", "run-2")).GetProperty("value");
		Assert.Equal("readerRelay", opened.GetProperty("strategy").GetString());
	}

	[Fact]
	public async Task When_every_method_fails_the_call_rejects_and_names_each()
	{
		_consent.Allow();
		var reply = await Invoke("example.com", "run-1");
		Assert.False(reply.GetProperty("ok").GetBoolean());
		Assert.Equal(nameof(AllStrategiesFailedError), reply.GetProperty("error").GetProperty("name").GetString());
		Assert.Contains("Wayback archive", reply.GetProperty("error").GetProperty("message").GetString());
	}

	[Fact]
	public async Task Something_that_is_not_an_address_rejects_with_BadURLError()
	{
		var reply = await Invoke("not a url", "run-1");
		Assert.Equal(nameof(BadURLError), reply.GetProperty("error").GetProperty("name").GetString());
		Assert.Empty(Events("run-1"));
	}
}
