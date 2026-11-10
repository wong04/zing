using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

/// <summary>default-browser:get and default-browser:dismiss: the Settings row's state and the offer's "Don't ask again".</summary>
public sealed class ChromeApiDefaultBrowserTests : IDisposable
{
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly SettingsStore _settings = new();
	private readonly ChromeApi _api;

	public ChromeApiDefaultBrowserTests()
	{
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(_settings, library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), _host, "test");
	}

	public void Dispose() => _api.Dispose();

	private async Task<JsonElement> Invoke(string channel)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args = Array.Empty<object>() });
		var result = JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
		Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
		return result.TryGetProperty("value", out var value) ? value : default;
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task Get_answers_whether_zing_is_installed_and_the_default(bool registered, bool isDefault)
	{
		_host.DefaultBrowserState = new DefaultBrowserState(registered, isDefault);
		var state = await Invoke("default-browser:get");
		Assert.Equal(registered, state.GetProperty("registered").GetBoolean());
		Assert.Equal(isDefault, state.GetProperty("isDefault").GetBoolean());
	}

	[Fact]
	public async Task Dont_ask_again_remembers_it_and_takes_the_offer_down()
	{
		Assert.False(_settings.Get("defaultBrowserDismissed")!.GetValue<bool>());
		await Invoke("default-browser:dismiss");
		Assert.True(_settings.Get("defaultBrowserDismissed")!.GetValue<bool>());
		Assert.Equal(1, _host.OfferDismissals);
	}
}
