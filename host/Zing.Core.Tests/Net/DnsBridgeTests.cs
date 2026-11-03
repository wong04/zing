using System.Text.Json;
using Zing.Core.Net;

namespace Zing.Core.Tests.Net;

/// <summary>The dns channels: the renderer names a script by its token and never supplies one.</summary>
public sealed class DnsBridgeTests : IDisposable
{
	private sealed class FakeSystem : IDnsSystem
	{
		public List<string> Ran { get; } = [];

		public Task<DnsSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(new DnsSnapshot([new DnsAdapter("Ethernet", [], [])], []));

		public Task<ElevatedRun> RunElevatedAsync(string script, CancellationToken ct)
		{
			Ran.Add(script);
			return Task.FromResult<ElevatedRun>(new ElevatedRun.Exited(0));
		}
	}

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "zing-dnsbridge-" + Guid.NewGuid().ToString("N"));
	private readonly Bridge _bridge = new();
	private readonly FakeSystem _system = new();
	private readonly ChromeApi _api;
	private readonly ChromeApi _unsupported;

	public DnsBridgeTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var service = new DnsService(_system, Path.Combine(_directory, "dns-before.json"), () => DateTimeOffset.UnixEpoch);
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent(), Dns: service), new FakeWindowHost(), "test");
		_api.Start([], "about:blank");
		_unsupported = new ChromeApi(_other, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new RelayConsent()), new FakeWindowHost(), "test");
	}

	private readonly Bridge _other = new();

	public void Dispose()
	{
		_api.Dispose();
		_unsupported.Dispose();
		if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
	}

	private static async Task<JsonElement> Invoke(Bridge bridge, string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await bridge.HandleAsync(json)).RootElement;
	}

	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : throw new Xunit.Sdk.XunitException(result.ToString());
	}

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	[Fact]
	public async Task Preview_shows_the_script_for_the_provider_in_settings_and_runs_nothing()
	{
		var preview = Value(await Invoke(_bridge, "dns:preview"));
		Assert.False(preview.GetProperty("applied").GetBoolean());
		Assert.Equal("cloudflare", preview.GetProperty("provider").GetString());
		Assert.Contains("'1.1.1.1'", preview.GetProperty("script").GetString());
		Assert.Empty(_system.Ran);
	}

	[Fact]
	public async Task Apply_runs_the_script_the_token_names()
	{
		var preview = Value(await Invoke(_bridge, "dns:preview"));
		var outcome = Value(await Invoke(_bridge, "dns:apply", preview.GetProperty("token").GetString()));
		Assert.Equal("applied", outcome.GetProperty("status").GetString());
		Assert.Equal([preview.GetProperty("script").GetString()], _system.Ran);
	}

	[Fact]
	public async Task Apply_takes_a_token_and_nothing_a_renderer_adds_becomes_script()
	{
		var preview = Value(await Invoke(_bridge, "dns:preview"));
		Value(await Invoke(_bridge, "dns:apply", preview.GetProperty("token").GetString(), "calc.exe"));
		Assert.DoesNotContain("calc", Assert.Single(_system.Ran));
	}

	[Fact]
	public async Task Apply_with_a_token_that_is_not_a_known_script_runs_nothing()
	{
		var outcome = Value(await Invoke(_bridge, "dns:apply", "Start-Process calc"));
		Assert.Equal("changed", outcome.GetProperty("status").GetString());
		Assert.Empty(_system.Ran);
	}

	[Fact]
	public async Task Undo_with_nothing_saved_is_a_named_error()
	{
		Assert.Equal(nameof(NoDnsBackupError), ErrorName(await Invoke(_bridge, "dns:undo", "x")));
	}

	[Fact]
	public async Task A_host_that_cannot_change_dns_says_so()
	{
		Assert.Equal(nameof(InvalidOperationException), ErrorName(await Invoke(_other, "dns:preview")));
		Assert.Equal(nameof(InvalidOperationException), ErrorName(await Invoke(_other, "dns:apply", "x")));
		Assert.Equal(nameof(InvalidOperationException), ErrorName(await Invoke(_other, "dns:undo", "x")));
	}
}
