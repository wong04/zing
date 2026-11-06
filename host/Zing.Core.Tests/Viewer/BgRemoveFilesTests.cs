using System.Text;
using Zing.Core.Engines;
using Zing.Core.Tests.Engines;
using Zing.Core.Viewer;

namespace Zing.Core.Tests.Viewer;

/// <summary>
/// What the viewer's file server does for the background remover: a picture only if its own first bytes say PNG, JPEG or
/// WebP, bound to one panel; the model by its engine id, only while a picture is open; and the result PUT back, tied to the same
/// session. Page tabs never get here (WebView2Page answers the address 404), and a view's request without Zing's Origin is 404.
/// </summary>
public sealed class BgRemoveFilesTests : IAsyncLifetime
{
	private const string App = "https://app.zing";
	private const string Panel = "tool:Background Remover";
	private static readonly byte[] PngHead = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

	private readonly RunnerFixture _engines = new(engineId: EngineIds.BackgroundModel);
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "zing-bg-test-" + Guid.NewGuid().ToString("N"));
	private readonly List<(string What, Exception Error)> _errors = [];
	private ViewerFiles _viewer = null!;

	public async ValueTask InitializeAsync()
	{
		await _engines.InstallAsync();
		Directory.CreateDirectory(_dir);
		_viewer = new ViewerFiles(Path.Combine(_dir, "viewer"), (what, error) => _errors.Add((what, error)))
		{
			ModelPath = () => _engines.Store.InstalledPath(EngineIds.BackgroundModel),
			SaveDirectory = Path.Combine(_dir, "downloads"),
		};
	}

	public ValueTask DisposeAsync()
	{
		_viewer.Dispose();
		_engines.Dispose();
		Directory.Delete(_dir, recursive: true);
		return ValueTask.CompletedTask;
	}

	private string Write(string name, byte[] bytes)
	{
		var path = Path.Combine(_dir, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	private static byte[] Png(int extra = 20) => [.. PngHead, .. new byte[extra]];
	private static byte[] Jpeg() => [0xff, 0xd8, 0xff, 0xe0, 0, 16, .. "JFIF"u8.ToArray(), 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
	private static byte[] WebP() => [.. "RIFF"u8.ToArray(), 30, 0, 0, 0, .. "WEBPVP8 "u8.ToArray(), 10, 0, 0, 0, 0, 0, 0, 0x9d, 1, 0x2a, 1, 0, 1, 0];

	private ViewerSession Open(string name = "cat.png", byte[]? bytes = null) => _viewer.OpenPicture(Write(name, bytes ?? Png()), Panel);

	private static ViewerRequest Get(string url, string? origin = App, string? referer = null, string method = "GET") => new(method, url, null, origin, referer);

	private static string Model => $"https://{ViewerFiles.Host}/model/bgmodel";

	private static ViewerRequest Put(ViewerSession session, string name, byte[] body, long? declared = null, string? origin = App, Stream? stream = null) =>
		new("PUT", $"https://{ViewerFiles.Host}/save/{session.Id}/{Uri.EscapeDataString(name)}", null, origin, null, stream ?? new MemoryStream(body), declared ?? body.Length);

	[Fact]
	public void A_picture_is_opened_by_what_its_first_bytes_say_not_by_its_name()
	{
		Assert.Equal("image/png", Open("a.png").Format.ContentType);
		Assert.Equal("image/jpeg", Open("b.png", Jpeg()).Format.ContentType);
		Assert.Equal("image/webp", Open("c.dat", WebP()).Format.ContentType);
	}

	[Theory]
	[InlineData("heic")]
	[InlineData("text")]
	[InlineData("svg")]
	[InlineData("pdf")]
	[InlineData("empty")]
	[InlineData("short")]
	public void Anything_else_is_refused_even_under_a_picture_name(string kind)
	{
		byte[] bytes = kind switch
		{
			"heic" => [0, 0, 0, 24, .. "ftypheic"u8.ToArray(), 0, 0, 0, 0],
			"text" => "this is not a picture"u8.ToArray(),
			"svg" => "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(),
			"pdf" => "%PDF-1.7 blah"u8.ToArray(),
			"empty" => [],
			_ => PngHead[..4],
		};

		Assert.Throws<PictureRefusedException>(() => _viewer.OpenPicture(Write("x.png", bytes), Panel));
		Assert.Null(_viewer.ForPanel(Panel));
	}

	[Fact]
	public void A_file_longer_than_the_cap_is_refused_without_reading_it()
	{
		var path = Path.Combine(_dir, "huge.png");
		using (var file = File.Create(path))
		{
			file.Write(Png());
			file.SetLength(ViewerFiles.PictureLimit + 1);
		}

		var error = Assert.Throws<PictureRefusedException>(() => _viewer.OpenPicture(path, Panel));
		Assert.Contains("MB", error.Message);
	}

	[Fact]
	public async Task The_picture_is_served_at_its_session_and_a_new_picture_or_a_closed_panel_revokes_it()
	{
		var first = Open("one.png", Png(30));
		var served = await _viewer.ServeAsync(Get(first.Url));
		Assert.Equal(200, served.Status);
		Assert.Equal(Png(30), served.Body);
		Assert.Equal("image/png", served.Headers["Content-Type"]);

		var second = Open("two.png", Png(40));
		Assert.Equal(404, (await _viewer.ServeAsync(Get(first.Url))).Status);
		Assert.Equal(200, (await _viewer.ServeAsync(Get(second.Url))).Status);

		_viewer.CloseExcept(new HashSet<string>());
		Assert.Equal(404, (await _viewer.ServeAsync(Get(second.Url))).Status);
	}

	[Fact]
	public async Task Closing_the_session_ends_the_models_and_the_saves_permission_too()
	{
		var session = Open();
		_viewer.Close(session.Id);

		Assert.Equal(404, (await _viewer.ServeAsync(Get(Model))).Status);
		Assert.Equal(404, (await _viewer.ServeAsync(Put(session, "cat.png", Png()))).Status);
	}

	[Fact]
	public async Task The_model_is_served_by_id_to_a_Zing_view_while_a_picture_is_open()
	{
		Open();
		var installed = _engines.Store.InstalledPath(EngineIds.BackgroundModel);

		var reply = await _viewer.ServeAsync(Get(Model));

		Assert.Equal(200, reply.Status);
		Assert.Equal(installed, reply.WholeFile);
		Assert.Equal(new FileInfo(installed).Length.ToString(), reply.Headers["Content-Length"]);
		Assert.True(new FileInfo(installed).Length > 1000);
		Assert.Equal("application/octet-stream", reply.Headers["Content-Type"]);
		Assert.Equal(App, reply.Headers["Access-Control-Allow-Origin"]);
	}

	[Fact]
	public async Task The_model_is_not_served_with_no_picture_open()
	{
		Assert.Equal(404, (await _viewer.ServeAsync(Get(Model))).Status);
	}

	[Theory]
	[InlineData("/model/ffmpeg")]
	[InlineData("/model/ytdlp")]
	[InlineData("/model/BGMODEL")]
	[InlineData("/model/bgmodel/")]
	[InlineData("/model/bgmodel/../ffmpeg")]
	[InlineData("/model/bg%6Dodel")]
	[InlineData("/model/")]
	[InlineData("/model")]
	[InlineData("/models/bgmodel")]
	[InlineData("/engine/bgmodel")]
	[InlineData("/model/bgmodel?x=1")]
	[InlineData("/model/bgmodel#x")]
	public async Task Any_other_id_or_shape_is_a_404_that_says_nothing(string path)
	{
		Open();

		var reply = await _viewer.ServeAsync(Get($"https://{ViewerFiles.Host}{path}"));

		Assert.Equal(404, reply.Status);
		Assert.Empty(reply.Body);
		Assert.Null(reply.WholeFile);
	}

	[Fact]
	public async Task Without_Zing_s_origin_the_model_and_the_picture_are_404()
	{
		var session = Open();

		foreach (var url in new[] { Model, session.Url })
		{
			Assert.Equal(404, (await _viewer.ServeAsync(Get(url, origin: null))).Status);
			Assert.Equal(404, (await _viewer.ServeAsync(Get(url, origin: "https://evil.example"))).Status);
			Assert.Equal(404, (await _viewer.ServeAsync(Get(url, origin: null, referer: "https://evil.example/"))).Status);
			Assert.Equal(404, (await _viewer.ServeAsync(Get(url, origin: null, referer: "https://app.zing.evil.example/"))).Status);
		}
	}

	[Fact]
	public async Task A_model_that_is_not_installed_is_404_and_a_busy_one_is_503()
	{
		Open();
		_viewer.ModelPath = () => throw new EngineNotInstalledException(EngineIds.BackgroundModel);
		Assert.Equal(404, (await _viewer.ServeAsync(Get(Model))).Status);
		_viewer.ModelPath = () => throw new EngineBusyException(EngineIds.BackgroundModel);
		Assert.Equal(503, (await _viewer.ServeAsync(Get(Model))).Status);
		Assert.Empty(_errors);
	}

	[Fact]
	public async Task A_picture_locked_for_writing_by_another_program_is_a_status_not_a_crash()
	{
		var session = Open();
		var path = Path.Combine(_dir, "cat.png");
		using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		var reply = await _viewer.ServeAsync(Get(session.Url));

		Assert.Equal(503, reply.Status);
		Assert.Equal(App, reply.Headers["Access-Control-Allow-Origin"]);
		var reported = Assert.Single(_errors);
		Assert.IsAssignableFrom<IOException>(reported.Error);
		_errors.Clear();
	}

	[Fact]
	public async Task A_picture_that_was_deleted_since_it_was_opened_is_404()
	{
		var session = Open();
		File.Delete(Path.Combine(_dir, "cat.png"));

		Assert.Equal(404, (await _viewer.ServeAsync(Get(session.Url))).Status);
	}

	[Fact]
	public async Task The_result_is_saved_by_a_PUT_tied_to_the_session_and_numbered()
	{
		var session = Open();

		var first = await _viewer.ServeAsync(Put(session, "cat no background.png", Png(50)));
		var second = await _viewer.ServeAsync(Put(session, "cat no background.png", Png(60)));

		Assert.Equal(201, first.Status);
		Assert.Equal(201, second.Status);
		Assert.Equal("cat no background.png", Saved(first));
		Assert.Equal("cat no background 2.png", Saved(second));
		Assert.Equal(Png(50), File.ReadAllBytes(Path.Combine(_dir, "downloads", "cat no background.png")));
		Assert.Equal(App, first.Headers["Access-Control-Allow-Origin"]);
	}

	private static string Saved(ViewerReply reply) => System.Text.Json.JsonDocument.Parse(reply.Body).RootElement.GetProperty("name").GetString()!;

	[Fact]
	public async Task A_save_with_a_session_that_is_not_a_picture_session_or_unknown_is_404()
	{
		var viewerSession = _viewer.OpenNative(Write("clip.mp4", new byte[10]), "section:Files", ViewerFormats.For("mp4")!);
		var picture = Open();
		var unknown = new ViewerRequest("PUT", $"https://{ViewerFiles.Host}/save/{new string('a', 32)}/x.png", null, App, null, new MemoryStream(Png()), Png().Length);

		Assert.Equal(404, (await _viewer.ServeAsync(Put(viewerSession, "x.png", Png()))).Status);
		Assert.Equal(404, (await _viewer.ServeAsync(unknown)).Status);
		Assert.Equal(404, (await _viewer.ServeAsync(Put(picture, "x.png", Png(), origin: null))).Status);
		Assert.False(Directory.Exists(Path.Combine(_dir, "downloads")) && Directory.GetFileSystemEntries(Path.Combine(_dir, "downloads")).Length > 0);
	}

	[Fact]
	public async Task A_save_is_refused_with_a_reason_for_a_bad_name_bytes_that_are_not_a_PNG_and_a_picture_too_large()
	{
		var session = Open();

		var name = await _viewer.ServeAsync(Put(session, @"..\evil.png", Png()));
		var notPng = await _viewer.ServeAsync(Put(session, "ok.png", Encoding.ASCII.GetBytes("MZ not a picture")));
		var big = await _viewer.ServeAsync(Put(session, "big.png", [], declared: PngSave.MaxBytes + 1L, stream: new MemoryStream(Png())));

		Assert.Equal(400, name.Status);
		Assert.Contains("plain name", Encoding.UTF8.GetString(name.Body));
		Assert.Equal(400, notPng.Status);
		Assert.Equal(413, big.Status);
		Assert.Contains("64 MB", Encoding.UTF8.GetString(big.Body));
		Assert.Equal("text/plain; charset=utf-8", big.Headers["Content-Type"]);
		Assert.False(Directory.Exists(Path.Combine(_dir, "downloads")) && Directory.GetFileSystemEntries(Path.Combine(_dir, "downloads")).Length > 0);
	}

	[Fact]
	public async Task Only_PUT_saves_and_only_GET_reads()
	{
		var session = Open();

		Assert.Equal(405, (await _viewer.ServeAsync(new ViewerRequest("POST", $"https://{ViewerFiles.Host}/save/{session.Id}/a.png", null, App, null, new MemoryStream(Png()), 28))).Status);
		Assert.Equal(405, (await _viewer.ServeAsync(Get(session.Url, method: "PUT"))).Status);
		Assert.Equal(405, (await _viewer.ServeAsync(Get(Model, method: "PUT"))).Status);
	}

	[Fact]
	public async Task A_window_can_save_only_so_much_this_way_and_is_told_plainly()
	{
		var session = Open();
		_viewer.SaveBudget = Png(100).Length * 2L + 5;

		Assert.Equal(201, (await _viewer.ServeAsync(Put(session, "a.png", Png(100)))).Status);
		Assert.Equal(201, (await _viewer.ServeAsync(Put(session, "b.png", Png(100)))).Status);
		var third = await _viewer.ServeAsync(Put(session, "c.png", Png(100)));

		Assert.Equal(507, third.Status);
		Assert.Contains("as much", Encoding.UTF8.GetString(third.Body));
		Assert.Equal(["a.png", "b.png"], Directory.GetFiles(Path.Combine(_dir, "downloads")).Select(Path.GetFileName).Order());
	}

	[Fact]
	public void The_default_budget_is_about_a_gigabyte()
	{
		Assert.InRange(new ViewerFiles(_dir, (_, _) => { }).SaveBudget, 900L * 1024 * 1024, 1100L * 1024 * 1024);
	}

	[Fact]
	public async Task A_preflight_for_a_save_is_answered_for_Zing_s_origin_only()
	{
		var session = Open();
		var url = $"https://{ViewerFiles.Host}/save/{session.Id}/a.png";

		var good = await _viewer.ServeAsync(Get(url, method: "OPTIONS"));
		var bad = await _viewer.ServeAsync(Get(url, origin: "https://evil.example", method: "OPTIONS"));

		Assert.Equal(204, good.Status);
		Assert.Contains("PUT", good.Headers["Access-Control-Allow-Methods"]);
		Assert.Equal(404, bad.Status);
	}
}
