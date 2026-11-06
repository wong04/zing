using System.Text.Json;
using Zing.Core.Engines;
using Zing.Core.Tests.Engines;
using Zing.Core.Viewer;

namespace Zing.Core.Tests;

/// <summary>The viewer panel's side of the bridge: opening a picked file, converting a copy with an engine, and closing.</summary>
public sealed class ChromeApiViewerTests : IAsyncLifetime
{
	private const string App = "https://app.zing";
	private readonly RunnerFixture _engines = new();
	private readonly Bridge _bridge = new();
	private readonly FakeWindowHost _host = new();
	private ChromeApi _api = null!;
	private string _panel = "";
	private string _root = "";
	private bool _windowClosed;

	public async ValueTask InitializeAsync()
	{
		await _engines.InstallAsync();
		_root = Path.Combine(_engines.Inputs, "viewer-copies");
		var library = new LibraryStore(Library.EmptyLibrary(0), _ => { }, Deterministic.Clock(), Deterministic.Ids("id-"));
		var services = new AppServices(new SettingsStore(), library, Address.FromDefaults(), new TickingClock(), Accounts.Guest(), new Zing.Core.Net.RelayConsent(), Engines: _engines.Store, Runner: _engines.Runner, ViewerRoot: _root);
		_api = new ChromeApi(_bridge, services, _host, "test");
		_host.DownloadsDirectory = _engines.Outputs;
		_api.Start([], "about:blank");
		_api.Shell.OpenPanelCentred("tool:Hashes");
		_panel = _api.Shell.PanelsState().Panels[0].Id;
	}

	public ValueTask DisposeAsync()
	{
		if (!_windowClosed) _api.Dispose();
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

	private async Task<string> PickOne(string name, string content = "input")
	{
		_host.PickedPaths = [_engines.Make(name, content)];
		return Value(await Invoke("files:pick", (object)new[] { "any" }))[0].GetProperty("token").GetString()!;
	}

	private JsonElement[] Pushed(string channel)
	{
		lock (_host.Pushed) return [.. _host.Pushed.Where(p => p.Channel == channel).Select(p => JsonSerializer.SerializeToElement(p.Payload, ZingJson.Options))];
	}

	private async Task<ViewerReply> Fetch(string url, string? range = null) =>
		await _api.Viewer!.ServeAsync(new ViewerRequest("GET", url, range, App, null));

	private string[] Copies() => Directory.Exists(_root) ? [.. Directory.GetDirectories(_root)] : [];

	[Fact]
	public async Task A_file_chromium_shows_is_served_from_where_it_is_and_not_copied()
	{
		var token = await PickOne("pixel.png", "png bytes");

		var opened = Value(await Invoke("viewer:open", token, _panel));

		Assert.Equal("pixel.png", opened.GetProperty("name").GetString());
		Assert.Equal("image", opened.GetProperty("show").GetString());
		Assert.False(opened.GetProperty("stream").GetBoolean());
		Assert.False(opened.TryGetProperty("jobId", out var job) && job.ValueKind == JsonValueKind.String);
		var url = opened.GetProperty("url").GetString()!;
		Assert.Matches("^https://view.zing/[A-Za-z0-9_-]{32}$", url);
		Assert.DoesNotContain("pixel", url);
		Assert.DoesNotContain(_engines.Inputs, opened.ToString());
		var reply = await Fetch(url);
		Assert.Equal(200, reply.Status);
		Assert.Equal("png bytes", System.Text.Encoding.UTF8.GetString(reply.Body));
		Assert.Empty(Copies());
		Assert.Equal(0, _engines.Runner.Running);
	}

	[Fact]
	public async Task A_kind_of_file_it_cannot_show_is_refused_by_name()
	{
		var token = await PickOne("part.obj");

		Assert.Equal(nameof(UnsupportedViewerFormatException), ErrorName(await Invoke("viewer:open", token, _panel)));
		Assert.Equal(nameof(UnsupportedViewerFormatException), ErrorName(await Invoke("viewer:open", await PickOne("page.html"), _panel)));
		Assert.Equal(nameof(UnsupportedViewerFormatException), ErrorName(await Invoke("viewer:open", await PickOne("noextension"), _panel)));
	}

	[Fact]
	public async Task A_token_that_was_not_issued_and_a_panel_that_is_not_open_are_refused()
	{
		var token = await PickOne("pixel.png");

		Assert.Equal(nameof(UnknownFileGrantException), ErrorName(await Invoke("viewer:open", "made-up", _panel)));
		Assert.Equal(nameof(UnknownPanelException), ErrorName(await Invoke("viewer:open", token, "section:Files")));
	}

	[Fact]
	public async Task A_file_moved_away_since_it_was_picked_is_named_in_the_error()
	{
		var token = await PickOne("gone.png");
		File.Delete(Path.Combine(_engines.Inputs, "gone.png"));

		Assert.Equal(nameof(FileNotFoundException), ErrorName(await Invoke("viewer:open", token, _panel)));
	}

	[Fact]
	public async Task A_converted_file_without_its_engine_is_refused_and_leaves_no_copy()
	{
		_engines.Store.Remove("ffmpeg");
		var token = await PickOne("holiday.mov");

		Assert.Equal(nameof(EngineNotInstalledException), ErrorName(await Invoke("viewer:open", token, _panel)));
		Assert.Empty(Copies());
		Assert.Equal(0, _engines.Runner.Running);
		Assert.Null(_api.Viewer!.ForPanel(_panel));
	}

	[Fact]
	public async Task A_converted_video_is_a_stream_whose_exit_carries_no_token_and_whose_copy_goes_when_it_closes()
	{
		var token = await PickOne("holiday.mov", "pretend this is a movie");

		var opened = Value(await Invoke("viewer:open", token, _panel));

		Assert.True(opened.GetProperty("stream").GetBoolean());
		Assert.Equal("video", opened.GetProperty("show").GetString());
		var job = opened.GetProperty("jobId").GetString()!;
		var url = opened.GetProperty("url").GetString()!;
		var folder = Assert.Single(Copies());
		Assert.StartsWith($"{Environment.ProcessId}_", Path.GetFileName(folder));
		await _engines.Until(() => Pushed("engines:exit").Length >= 1, "the conversion to end");

		var exit = Pushed("engines:exit").Single();
		Assert.Equal(job, exit.GetProperty("jobId").GetString());
		Assert.Equal(0, exit.GetProperty("code").GetInt32());
		Assert.Equal(0, exit.GetProperty("outputs").GetArrayLength());
		Assert.True(File.Exists(Path.Combine(folder, "view.mp4")));
		// Finished: the copy answers from where it was placed, and 416 is its end.
		var all = await Fetch(url, "bytes=0-");
		Assert.Equal(206, all.Status);
		Assert.Equal("pretend this is a movie", System.Text.Encoding.UTF8.GetString(all.Body));
		Assert.Equal(416, (await Fetch(url, $"bytes={all.Body.Length}-")).Status);
		// Nothing went to Downloads: the viewer's copy is not a download.
		Assert.Empty(Directory.GetFiles(_engines.Outputs));

		Value(await Invoke("viewer:close", opened.GetProperty("id").GetString()));

		Assert.Empty(Copies());
		Assert.Equal(404, (await Fetch(url, "bytes=0-")).Status);
	}

	[Fact]
	public async Task Closing_the_panel_stops_the_job_and_deletes_the_copy()
	{
		var token = await PickOne("holiday.mov", "pretend this is a movie");
		var opened = Value(await Invoke("viewer:open", token, _panel));
		var url = opened.GetProperty("url").GetString()!;
		Assert.Single(Copies());

		_api.Shell.ClosePanel(_panel);

		await _engines.Until(() => Pushed("engines:exit").Length >= 1, "the cancelled job's exit");
		var exit = Pushed("engines:exit").Single();
		Assert.True(exit.GetProperty("cancelled").GetBoolean());
		await _engines.Until(() => Copies().Length == 0, "the copy to be deleted");
		Assert.Equal(404, (await Fetch(url, "bytes=0-")).Status);
		Assert.Empty(Directory.GetFiles(_engines.Outputs));
	}

	[Fact]
	public async Task Minimizing_the_panel_keeps_the_file()
	{
		var token = await PickOne("pixel.png");
		var opened = Value(await Invoke("viewer:open", token, _panel));

		_api.Shell.MinimizePanel(_panel);

		Assert.Equal(200, (await Fetch(opened.GetProperty("url").GetString()!)).Status);
	}

	[Fact]
	public async Task Opening_another_file_in_the_panel_stops_the_first_ones_job_and_deletes_its_copy()
	{
		var movie = Value(await Invoke("viewer:open", await PickOne("holiday.mov", "pretend this is a movie"), _panel));
		var movieUrl = movie.GetProperty("url").GetString()!;
		var firstFolder = Assert.Single(Copies());

		var picture = Value(await Invoke("viewer:open", await PickOne("pixel.png"), _panel));

		Assert.Equal(404, (await Fetch(movieUrl, "bytes=0-")).Status);
		Assert.Equal(200, (await Fetch(picture.GetProperty("url").GetString()!)).Status);
		await _engines.Until(() => !Directory.Exists(firstFolder), "the first copy to be deleted");
	}

	[Fact]
	public async Task Closing_the_window_deletes_every_copy()
	{
		await Invoke("viewer:open", await PickOne("holiday.mov", "pretend this is a movie"), _panel);
		Assert.Single(Copies());

		_windowClosed = true;
		_api.Dispose();

		await _engines.Until(() => Copies().Length == 0, "the copies to be deleted");
	}

	private static readonly byte[] PngHeader = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];

	private async Task<string> PickPicture(string name, byte[] bytes)
	{
		var path = Path.Combine(_engines.Inputs, name);
		File.WriteAllBytes(path, bytes);
		_host.PickedPaths = [path];
		return Value(await Invoke("files:pick", (object)new[] { "image" }))[0].GetProperty("token").GetString()!;
	}

	[Fact]
	public async Task A_picture_for_the_background_remover_opens_as_a_session_without_the_path_or_token()
	{
		var token = await PickPicture("cat.png", PngHeader);

		var opened = Value(await Invoke("bgremove:open", _panel, token));

		Assert.DoesNotContain(token, opened.ToString());
		Assert.DoesNotContain(_engines.Inputs.Replace("\\", "\\\\"), opened.ToString());
		Assert.Equal("cat.png", opened.GetProperty("name").GetString());
		Assert.Equal(200, (await Fetch(opened.GetProperty("url").GetString()!)).Status);
		Assert.Empty(Copies());
	}

	[Fact]
	public async Task A_file_that_is_not_a_picture_or_for_a_panel_that_is_not_open_is_refused_and_opens_nothing()
	{
		var heic = await PickPicture("IMG.png", [0, 0, 0, 24, .. "ftypheic"u8.ToArray()]);
		var good = await PickPicture("good.png", PngHeader);

		Assert.Equal(nameof(SessionFileRefusedException), ErrorName(await Invoke("bgremove:open", _panel, heic)));
		Assert.Equal(nameof(UnknownPanelException), ErrorName(await Invoke("bgremove:open", "panel-nobody", good)));
		Assert.Equal(nameof(UnknownFileGrantException), ErrorName(await Invoke("bgremove:open", _panel, new string('0', 32))));
		Assert.Null(_api.Viewer!.ForPanel(_panel));
	}

	[Fact]
	public async Task Closing_the_session_or_opening_another_picture_revokes_the_first()
	{
		var first = Value(await Invoke("bgremove:open", _panel, await PickPicture("one.png", PngHeader)));
		var firstUrl = first.GetProperty("url").GetString()!;

		var second = Value(await Invoke("bgremove:open", _panel, await PickPicture("two.png", PngHeader)));
		Assert.Equal(404, (await Fetch(firstUrl)).Status);
		var secondUrl = second.GetProperty("url").GetString()!;
		Assert.Equal(200, (await Fetch(secondUrl)).Status);

		Value(await Invoke("viewer:close", second.GetProperty("id").GetString()!));
		Assert.Equal(404, (await Fetch(secondUrl)).Status);
	}
}
