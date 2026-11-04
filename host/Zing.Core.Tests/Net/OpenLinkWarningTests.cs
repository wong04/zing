using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

/// <summary>Open a Link on a flagged address: the warning comes first, before anything is fetched.</summary>
public sealed class OpenLinkWarningTests : IDisposable
{
	private const string Flagged = "https://apple.com.secure-login.tk/verify";

	private sealed class NoProbes : IProbes
	{
		public Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct) => throw new InvalidOperationException("probed");
		public Task<bool> TcpAsync(string ip, CancellationToken ct) => throw new InvalidOperationException("probed");
		public Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct) => throw new InvalidOperationException("probed");
	}

	private sealed class NoDoh : IDohClient
	{
		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct) => throw new InvalidOperationException("asked DoH");
	}

	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly RelayEngineTests.FakeTransport _transport = new();
	private readonly ThreatGate _gate;
	private readonly ChromeApi _api;
	private bool _on = true;

	public OpenLinkWarningTests()
	{
		_gate = new ThreatGate(() => _on);
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var app = new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Net: new NetServices(new NoProbes(), new NoDoh(), _transport), Threats: _gate);
		_api = new ChromeApi(_bridge, app, _host, "test");
		_api.Start([], "about:blank");
	}

	public void Dispose() => _api.Dispose();

	private async Task<JsonElement> Open(string input)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "relay:open", args = new[] { input, "run-1" } });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement.GetProperty("value");
	}

	[Fact]
	public async Task A_flagged_address_opens_the_warning_and_nothing_is_fetched()
	{
		_transport.Plain = _ => RelayEngineTests.Ok("<p>hi</p>");
		var value = await Open(Flagged);
		Assert.Equal("warned", value.GetProperty("outcome").GetString());
		Assert.Empty(_transport.Calls);
		Assert.DoesNotContain(_host.Pushed, p => p.Channel == "relay:attempt");
		Assert.Contains(_api.Tabs.Records, t => t.Url == RelayedContent.Encode(new Uri(Flagged))!.AbsoluteUri);
	}

	[Fact]
	public async Task With_link_inspection_off_the_address_is_fetched_as_any_other()
	{
		_on = false;
		_transport.Plain = _ => RelayEngineTests.Ok("<p>hi</p>");
		var value = await Open(Flagged);
		Assert.Equal("opened", value.GetProperty("outcome").GetString());
		Assert.NotEmpty(_transport.Calls);
	}

	[Fact]
	public async Task An_origin_already_allowed_is_fetched_as_any_other()
	{
		var warning = Assert.IsType<NavigationDecision.Redirect>(_gate.Check("t1", "https://news.example.org/", Flagged)).Url;
		var html = _gate.Serve(warning, new SettingsStore().Snapshot())!.Body;
		var visit = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "href=\"(zing://visit-anyway[^\"]*)\"").Groups[1].Value);
		_gate.Check("t1", warning, visit);
		_transport.Plain = _ => RelayEngineTests.Ok("<p>hi</p>");
		var value = await Open(Flagged);
		Assert.Equal("opened", value.GetProperty("outcome").GetString());
	}
}
