using System.Text.Json;
using Zing.Core.Engines;
using Zing.Core.Tests.Engines;

namespace Zing.Core.Tests;

/// <summary>Running an engine and picking files through the bridge: the renderer's side of Task 15.</summary>
public sealed class ChromeApiEnginesTests : IAsyncLifetime
{
	private readonly RunnerFixture _engines = new();
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private ChromeApi _api = null!;
	private string _panel = "";

	public async ValueTask InitializeAsync()
	{
		await _engines.InstallAsync();
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var services = new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent(), Engines: _engines.Store, Runner: _engines.Runner);
		_api = new ChromeApi(_bridge, services, _host, "test");
		_host.DownloadsDirectory = _engines.Outputs;
		_api.Start([], "about:blank");
		_api.Shell.OpenPanelCentred("tool:Hashes");
		_panel = _api.Shell.PanelsState().Panels[0].Id;
	}

	public ValueTask DisposeAsync()
	{
		_api.Dispose();
		_engines.Dispose();
		return ValueTask.CompletedTask;
	}

	private async Task<JsonElement> Invoke(string channel, params object?[] args)
	{
		var json = JsonSerializer.Serialize(new { kind = "invoke", id = 1, channel, args });
		return JsonDocument.Parse(await _bridge.HandleAsync(json)).RootElement;
	}

	private static JsonElement Value(JsonElement result)
	{
		if (!result.GetProperty("ok").GetBoolean()) throw new Xunit.Sdk.XunitException(result.GetProperty("error").ToString());
		return result.TryGetProperty("value", out var value) ? value : default;
	}

	private static string ErrorName(JsonElement result) => result.GetProperty("error").GetProperty("name").GetString()!;

	private async Task<string> PickOne(string name)
	{
		_host.PickedPaths = [_engines.Make(name)];
		return Value(await Invoke("files:pick", (object)new[] { "video" }))[0].GetProperty("token").GetString()!;
	}

	private JsonElement[] Pushed(string channel)
	{
		lock (_host.Pushed) return [.. _host.Pushed.Where(p => p.Channel == channel).Select(p => JsonSerializer.SerializeToElement(p.Payload, ZingJson.Options))];
	}

	private async Task WaitForPush(string channel, int count = 1) => await _engines.Until(() => Pushed(channel).Length >= count, $"{count} {channel}");

	[Fact]
	public async Task Picking_gives_the_renderer_a_name_and_a_token_never_the_path()
	{
		var path = _engines.Make("holiday.mov");
		_host.PickedPaths = [path];

		var picked = Value(await Invoke("files:pick", (object)new[] { "video", "audio" }));

		Assert.Equal("holiday.mov", picked[0].GetProperty("name").GetString());
		Assert.DoesNotContain("holiday", picked[0].GetProperty("token").GetString());
		Assert.DoesNotContain(_engines.Inputs, picked.ToString());
		Assert.Contains("mov", _host.PickedExtensions!);
		Assert.Contains("mp3", _host.PickedExtensions!);
	}

	[Fact]
	public async Task Cancelling_the_picker_gives_an_empty_list()
	{
		_host.PickedPaths = [];

		Assert.Equal(0, Value(await Invoke("files:pick", (object)new[] { "any" })).GetArrayLength());
	}

	[Fact]
	public async Task An_unknown_kind_of_file_is_refused()
	{
		Assert.Equal(nameof(UnknownFileKindException), ErrorName(await Invoke("files:pick", (object)new[] { "exe" })));
	}

	[Fact]
	public async Task A_picked_file_reaches_the_engine_as_an_absolute_path_and_the_job_reports_its_output()
	{
		var token = await PickOne("-rf.mov");
		var spec = new { args = new object[] { "--fake-write", new { output = 0 }, new { input = token } }, outputs = new[] { "clip.txt" } };

		var id = Value(await Invoke("engines:run", "ffmpeg", _panel, spec)).GetString()!;
		await WaitForPush("engines:exit");

		var exit = Pushed("engines:exit").Single();
		Assert.Equal(id, exit.GetProperty("jobId").GetString());
		Assert.Equal(0, exit.GetProperty("code").GetInt32());
		var output = exit.GetProperty("outputs")[0];
		var placed = Path.Combine(_engines.Outputs, "clip.txt");
		// A name and a token, never the path; the token is good for a later job's input.
		Assert.Equal("clip.txt", output.GetProperty("name").GetString());
		Assert.DoesNotContain(_engines.Outputs, output.ToString());
		Assert.Equal(placed, _api.Grants.Resolve(output.GetProperty("token").GetString()!));
		Assert.True(File.Exists(placed));
		var echoed = Pushed("engines:output").SelectMany(p => p.GetProperty("lines").EnumerateArray()).Select(l => l.GetProperty("text").GetString()!).First();
		Assert.Contains("-rf.mov", echoed);
	}

	[Fact]
	public async Task A_path_the_renderer_makes_up_is_not_a_file_it_was_given()
	{
		await PickOne("a.mov");
		var spec = new { args = new object[] { new { input = @"C:\Windows\win.ini" } } };

		Assert.Equal(nameof(UnknownFileGrantException), ErrorName(await Invoke("engines:run", "ffmpeg", _panel, spec)));
		Assert.Equal(0, _engines.Runner.Running);
	}

	[Fact]
	public async Task A_path_typed_as_option_text_is_refused()
	{
		var spec = new { args = new object[] { "-i", @"C:\Windows\win.ini" } };

		Assert.Equal(nameof(UnsafeEngineArgumentException), ErrorName(await Invoke("engines:run", "ffmpeg", _panel, spec)));
	}

	[Fact]
	public async Task A_picked_file_that_was_moved_away_is_named_in_the_error()
	{
		var token = await PickOne("gone.mov");
		File.Delete(Path.Combine(_engines.Inputs, "gone.mov"));

		var result = await Invoke("engines:run", "ffmpeg", _panel, new { args = new object[] { new { input = token } } });

		Assert.Equal(nameof(FileNotFoundException), ErrorName(result));
		Assert.Contains("gone.mov", result.GetProperty("error").GetProperty("message").GetString());
	}

	[Fact]
	public async Task A_job_needs_an_open_panel_to_belong_to()
	{
		var spec = new { args = new object[] { "--exit", "0" } };

		Assert.Equal(nameof(UnknownPanelException), ErrorName(await Invoke("engines:run", "ffmpeg", "no-such-panel", spec)));
	}

	[Fact]
	public async Task An_unknown_engine_is_refused()
	{
		Assert.Equal(nameof(UnknownEngineException), ErrorName(await Invoke("engines:run", "notepad", _panel, new { args = new object[] { "x" } })));
	}

	[Fact]
	public async Task Closing_the_panel_kills_the_job_and_minimizing_it_does_not()
	{
		var spec = new { args = new object[] { "--sleep", "60000" } };
		Value(await Invoke("engines:run", "ffmpeg", _panel, spec));

		Value(await Invoke("toolpanel:minimize", _panel));
		await Task.Delay(300, TestContext.Current.CancellationToken);
		Assert.Equal(1, _engines.Runner.Running);
		Assert.Empty(Pushed("engines:exit"));

		Value(await Invoke("toolpanel:close", _panel));
		await WaitForPush("engines:exit");

		Assert.True(Pushed("engines:exit").Single().GetProperty("cancelled").GetBoolean());
	}

	[Fact]
	public async Task Cancelling_a_job_by_id_stops_it()
	{
		var id = Value(await Invoke("engines:run", "ffmpeg", _panel, new { args = new object[] { "--sleep", "60000" } })).GetString()!;

		Value(await Invoke("engines:cancel-job", id));
		await WaitForPush("engines:exit");

		Assert.True(Pushed("engines:exit").Single().GetProperty("cancelled").GetBoolean());
	}

	[Fact]
	public async Task Cancelling_a_job_nobody_started_is_an_error()
	{
		Assert.Equal(nameof(UnknownJobException), ErrorName(await Invoke("engines:cancel-job", "nonsense")));
	}

	[Fact]
	public async Task Closing_the_window_kills_its_jobs_and_pushes_nothing_afterwards()
	{
		Value(await Invoke("engines:run", "ffmpeg", _panel, new { args = new object[] { "--sleep", "60000" } }));

		_api.Dispose();
		await _engines.Until(() => _engines.Runner.Running == 0, "the job to end");
		await Task.Delay(300, TestContext.Current.CancellationToken);

		Assert.Empty(Pushed("engines:exit"));
		_api = new ChromeApi(new Bridge(), new AppServices(new SettingsStore(), new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("x-")), Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent()), new FakeWindowHost(), "test");
	}

	[Fact]
	public async Task A_drop_is_collected_once_and_only_after_a_zing_view_offered_it()
	{
		Assert.Equal(nameof(NoDroppedFilesException), ErrorName(await Invoke("files:open-dropped")));

		_api.Drops.Offer("https://app.zing/index.html", [_engines.Make("dropped.png")]);
		var dropped = Value(await Invoke("files:open-dropped"));

		Assert.Equal("dropped.png", dropped[0].GetProperty("name").GetString());
		Assert.Equal(nameof(NoDroppedFilesException), ErrorName(await Invoke("files:open-dropped")));
	}

	[Fact]
	public async Task A_dropped_file_can_be_given_to_an_engine()
	{
		_api.Drops.Offer("https://app.zing/index.html", [_engines.Make("dropped.png")]);
		var token = Value(await Invoke("files:open-dropped"))[0].GetProperty("token").GetString()!;

		Value(await Invoke("engines:run", "ffmpeg", _panel, new { args = new object[] { new { input = token } } }));
		await WaitForPush("engines:exit");

		Assert.Equal(0, Pushed("engines:exit").Single().GetProperty("code").GetInt32());
	}

	[Fact]
	public async Task An_html_file_is_printed_to_a_pdf_in_Downloads_and_answered_as_a_name_and_a_token()
	{
		_host.PickedPaths = [_engines.Make("page.html", "<h1>Hi</h1>")];
		var token = Value(await Invoke("files:pick", (object)new[] { "document" }))[0].GetProperty("token").GetString()!;

		var printed = Value(await Invoke("convert:html-to-pdf", token, "page.pdf"));

		Assert.Equal("page.pdf", printed.GetProperty("name").GetString());
		Assert.Matches("^[0-9a-f]{32}$", printed.GetProperty("token").GetString()!);
		Assert.Equal("%PDF-fake", File.ReadAllText(Path.Combine(_engines.Outputs, "page.pdf")));
		Assert.Equal(["<h1>Hi</h1>"], _host.Printed);
		Assert.DoesNotContain(_engines.Outputs.Replace("\\", "\\\\"), printed.ToString());
	}

	[Fact]
	public async Task A_path_the_renderer_made_up_is_not_printed()
	{
		Assert.Equal(nameof(UnknownFileGrantException), ErrorName(await Invoke("convert:html-to-pdf", @"C:\Windows\win.ini", "x.pdf")));
		Assert.Empty(_host.Printed);
	}

	[Fact]
	public async Task A_file_that_is_not_html_is_not_printed()
	{
		_host.PickedPaths = [_engines.Make("notes.txt")];
		var token = Value(await Invoke("files:pick", (object)new[] { "document" }))[0].GetProperty("token").GetString()!;

		Assert.Equal(nameof(NotHtmlException), ErrorName(await Invoke("convert:html-to-pdf", token, "notes.pdf")));
		Assert.Empty(_host.Printed);
	}
}
