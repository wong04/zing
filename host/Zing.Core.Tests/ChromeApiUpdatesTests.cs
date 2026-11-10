using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>update:get, update:check, update:relaunch and update:pill-size: the About controls and the pill, through the app's one Updater.</summary>
public sealed class ChromeApiUpdatesTests : IDisposable
{
	private sealed class ReadySource : IUpdateSource
	{
		public string CurrentVersion => "0.9.0";
		public string? Newer { get; set; }
		public int Restarts { get; private set; }
		public Task<string?> FindAsync(CancellationToken cancel) => Task.FromResult(Newer);
		public Task DownloadAsync(CancellationToken cancel) => Task.CompletedTask;
		public void RestartIntoUpdate() => Restarts++;
		public void ApplyOnExit() { }
	}

	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly ReadySource _source = new();
	private ChromeApi? _api;

	public void Dispose() => _api?.Dispose();

	private void Open(IUpdateSource? source)
	{
		_host.Updates = new Updater(source, _host.FakeScheduler, () => true);
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
	}

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		var result = JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
		Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
		return result.TryGetProperty("value", out var value) ? value : default;
	}

	[Fact]
	public async Task A_build_that_is_not_installed_answers_unavailable_with_no_version()
	{
		Open(null);
		var state = await Invoke("update:get");
		Assert.Equal("unavailable", state.GetProperty("status").GetString());
		Assert.Equal("", state.GetProperty("currentVersion").GetString());
		Assert.False(state.TryGetProperty("readyVersion", out _));
	}

	[Fact]
	public async Task Check_now_with_nothing_newer_answers_up_to_date_in_camel_case()
	{
		Open(_source);
		Assert.Equal("idle", (await Invoke("update:get")).GetProperty("status").GetString());
		var state = await Invoke("update:check");
		Assert.Equal("upToDate", state.GetProperty("status").GetString());
		Assert.Equal("0.9.0", state.GetProperty("currentVersion").GetString());
	}

	[Fact]
	public async Task Check_now_finds_and_downloads_a_newer_version_then_relaunch_restarts_once()
	{
		Open(_source);
		_source.Newer = "0.9.1";
		var state = await Invoke("update:check");
		Assert.Equal("ready", state.GetProperty("status").GetString());
		Assert.Equal("0.9.1", state.GetProperty("readyVersion").GetString());

		await Invoke("update:relaunch");
		await Invoke("update:relaunch");
		Assert.Equal(1, _source.Restarts);
	}

	[Fact]
	public async Task The_pills_measured_size_goes_to_the_window_rounded_up_to_whole_dips()
	{
		Open(_source);
		await Invoke("update:pill-size", 171.5, 40);
		Assert.Equal([(172, 40)], _host.UpdatePillSizes);
	}
}
