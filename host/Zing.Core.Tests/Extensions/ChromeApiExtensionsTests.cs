using System.Text.Json;
using Zing.Core.Extensions;

namespace Zing.Core.Tests.Extensions;

/// <summary>Settings' Extensions and the toolbar's extensions button, over the bridge.</summary>
public sealed class ChromeApiExtensionsTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-ext-api-").FullName;
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private readonly FakeExtensionEngine _engine = new();
	private ChromeApi? _api;

	public void Dispose()
	{
		_api?.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	private async Task<ExtensionService> Start(bool running)
	{
		var service = new ExtensionService(new ExtensionStore(Path.Combine(_root, "Extensions")), running);
		if (running) await service.AttachAsync(_engine);
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		_api = new ChromeApi(_bridge, new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent(), Extensions: service), _host, "test");
		_api.Start([], "about:blank");
		return service;
	}

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	/// <summary>The answer's value; an answer of null has none, and reads as JSON null.</summary>
	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.ToString());
		return result.TryGetProperty("value", out var value) ? value : JsonSerializer.SerializeToElement<object?>(null);
	}

	[Fact]
	public async Task Load_unpacked_copies_the_picked_folder_and_loads_it_while_running()
	{
		await Start(running: true);
		_host.PickedFolder = Unpacked.Make(Path.Combine(_root, "src"), "Probe");

		var state = Value(await Invoke("extensions:load-unpacked"));

		Assert.True(state.GetProperty("running").GetBoolean());
		var item = state.GetProperty("items").EnumerateArray().Single();
		Assert.Equal("Probe", item.GetProperty("name").GetString());
		Assert.True(item.GetProperty("hasPopup").GetBoolean());
		Assert.Single(_engine.Loaded);
		Assert.Equal(["Probe added"], _host.Notices);
	}

	[Fact]
	public async Task Load_unpacked_while_not_running_says_when_it_will_run()
	{
		await Start(running: false);
		_host.PickedFolder = Unpacked.Make(Path.Combine(_root, "src"), "Probe");

		var state = Value(await Invoke("extensions:load-unpacked"));

		Assert.False(state.GetProperty("running").GetBoolean());
		Assert.Empty(_engine.Calls);
		Assert.Equal(["Probe added. It runs once extensions are on and Zing relaunches."], _host.Notices);
	}

	[Fact]
	public async Task A_cancelled_pick_answers_null_and_installs_nothing()
	{
		await Start(running: true);

		var result = Value(await Invoke("extensions:load-unpacked"));

		Assert.Equal(JsonValueKind.Null, result.ValueKind);
		Assert.Empty(Value(await Invoke("extensions:get")).GetProperty("items").EnumerateArray());
	}

	[Fact]
	public async Task A_folder_that_isnt_an_extension_rejects_with_why()
	{
		await Start(running: true);
		_host.PickedFolder = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;

		var result = await Invoke("extensions:load-unpacked");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("no manifest.json", result.GetProperty("error").GetProperty("message").GetString());
	}

	[Fact]
	public async Task Set_enabled_and_remove_answer_the_new_state()
	{
		var service = await Start(running: true);
		var row = await service.InstallAsync(Unpacked.Make(Path.Combine(_root, "src")));

		var off = Value(await Invoke("extensions:set-enabled", row.Key, false));
		Assert.False(off.GetProperty("items")[0].GetProperty("enabled").GetBoolean());

		var removed = Value(await Invoke("extensions:remove", row.Key));
		Assert.Empty(removed.GetProperty("items").EnumerateArray());
		Assert.Empty(_engine.Loaded);
	}

	[Fact]
	public async Task The_toolbar_menu_lists_running_extensions_with_a_popup_and_opens_one()
	{
		var service = await Start(running: true);
		var withPopup = await service.InstallAsync(Unpacked.Make(Path.Combine(_root, "src"), "Has Popup"));
		await service.InstallAsync(Unpacked.Make(Path.Combine(_root, "src"), "No Popup", popup: null));
		var off = await service.InstallAsync(Unpacked.Make(Path.Combine(_root, "src"), "Turned Off"));
		await service.SetEnabledAsync(off.Key, false);

		Value(await Invoke("extensions:menu", new { x = 10, y = 20 }));

		var menu = _host.Menus.Single();
		Assert.Equal(["Has Popup", null, "Manage Extensions"], menu.Select(i => i.Label));
		menu[0].Click!();
		Assert.Equal([$"chrome-extension://id-{withPopup.Key}/popup.html"], _host.ExtensionPopups);
	}

	[Fact]
	public async Task The_toolbar_menu_offers_only_manage_while_not_running()
	{
		var service = await Start(running: false);
		await service.InstallAsync(Unpacked.Make(Path.Combine(_root, "src"), "Has Popup"));

		Value(await Invoke("extensions:menu", new { x = 10, y = 20 }));

		Assert.Equal(["Manage Extensions"], _host.Menus.Single().Select(i => i.Label));
	}

	[Fact]
	public async Task Manage_extensions_opens_settings_at_extensions()
	{
		await Start(running: true);

		Value(await Invoke("extensions:menu", new { x = 0, y = 0 }));
		_host.Menus.Single().Single().Click!();

		var shell = Value(await Invoke("shell:get"));
		Assert.Equal("extensions", shell.GetProperty("settingsCategory").GetString());
	}
}
