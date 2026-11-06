using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

/// <summary>net:diagnose through the bridge: the DoH provider comes from Settings, and the report keeps the TS shape.</summary>
public sealed class NetBridgeTests : IDisposable
{
	private sealed class Probes : IProbes
	{
		public Task<IReadOnlyList<string>> ResolveSystemAsync(string host, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["93.184.216.34"]);
		public Task<bool> TcpAsync(string ip, CancellationToken ct) => Task.FromResult(true);
		public Task<TlsProbeResult> TlsAsync(string ip, string host, CancellationToken ct) => Task.FromResult(new TlsProbeResult(true, true));
	}

	private sealed class Doh : IDohClient
	{
		public string? ProviderAsked { get; private set; }

		public Task<IReadOnlyList<string>> ResolveAsync(string host, string provider, CancellationToken ct)
		{
			ProviderAsked = provider;
			return Task.FromResult<IReadOnlyList<string>>(["93.184.216.34"]);
		}
	}

	private readonly Bridge _bridge = new();
	private readonly SettingsStore _settings = new();
	private readonly Doh _doh = new();
	private readonly ChromeApi _api;

	public NetBridgeTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent(), Net: new NetServices(new Probes(), _doh)), new FakeWindowHost(), "test");
	}

	public void Dispose() => _api.Dispose();

	private async Task<JsonElement> Invoke(params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel = "net:diagnose", args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	[Fact]
	public async Task Answers_with_the_report()
	{
		var result = await Invoke("https://Example.com/x");
		Assert.True(result.GetProperty("ok").GetBoolean());
		var report = result.GetProperty("value");
		Assert.Equal("example.com", report.GetProperty("host").GetString());
		Assert.Equal("notBlocked", report.GetProperty("kind").GetString());
	}

	[Fact]
	public async Task Uses_the_DoH_provider_chosen_in_Settings()
	{
		_settings.Set("dohProvider", "quad9");
		await Invoke("example.com");
		Assert.Equal("quad9", _doh.ProviderAsked);
	}

	[Fact]
	public async Task A_name_that_is_not_a_site_rejects_with_its_message()
	{
		var result = await Invoke("not a site");
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Equal(nameof(InvalidHostError), result.GetProperty("error").GetProperty("name").GetString());
	}
}
