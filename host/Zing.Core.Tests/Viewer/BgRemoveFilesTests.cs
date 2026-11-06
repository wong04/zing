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
			ModelOpener = Opener,
			SaveDirectory = Path.Combine(_dir, "downloads"),
		};
	}

	private (string Path, IDisposable Lease) Opener()
	{
		var lease = _engines.Store.Use(EngineIds.BackgroundModel);
		return (_engines.Store.InstalledPath(EngineIds.BackgroundModel), lease);
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

	private ViewerSession Open(string name = "cat.png", byte[]? bytes = null) => _viewer.OpenSession(Write(name, bytes ?? Png()), Panel, [SessionKinds.Image]);

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

		Assert.Throws<SessionFileRefusedException>(() => _viewer.OpenSession(Write("x.png", bytes), Panel, [SessionKinds.Image]));
		Assert.Null(_viewer.ForPanel(Panel));
	}

	[Fact]
	public void A_file_longer_than_the_cap_is_refused_without_reading_it()
	{
		var path = Path.Combine(_dir, "huge.png");
		using (var file = File.Create(path))
		{
			file.Write(Png());
			file.SetLength(SessionKinds.Image.ReadCap + 1);
		}

		var error = Assert.Throws<SessionFileRefusedException>(() => _viewer.OpenSession(path, Panel, [SessionKinds.Image]));
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
		_viewer.ModelOpener = () => throw new EngineNotInstalledException(EngineIds.BackgroundModel);
		Assert.Equal(404, (await _viewer.ServeAsync(Get(Model))).Status);
		_viewer.ModelOpener = () => throw new EngineBusyException(EngineIds.BackgroundModel);
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
		var big = await _viewer.ServeAsync(Put(session, "big.png", [], declared: SaveFormats.Png.Cap + 1L, stream: new MemoryStream(Png())));

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

	private static readonly byte[] PdfBytes = "%PDF-1.7 fake"u8.ToArray();

	private ViewerSession OpenPdf(string name = "doc.pdf", byte[]? bytes = null, string panel = "section:Pdf") =>
		_viewer.OpenSession(Write(name, bytes ?? PdfBytes), panel, [SessionKinds.Pdf, SessionKinds.Image]);

	[Fact]
	public void A_tool_that_takes_pdf_and_images_opens_either_by_first_bytes_and_refuses_the_rest()
	{
		Assert.Equal("application/pdf", OpenPdf("a.dat").Format.ContentType);
		Assert.Equal("image/png", _viewer.OpenSession(Write("p.pdf", Png()), "section:Pdf", [SessionKinds.Pdf, SessionKinds.Image]).Format.ContentType);
		Assert.Throws<SessionFileRefusedException>(() => OpenPdf("x.pdf", "not a pdf"u8.ToArray()));
		Assert.Throws<SessionFileRefusedException>(() => _viewer.OpenSession(Write("y.png", PdfBytes), Panel, [SessionKinds.Image]));
		Assert.Throws<SessionFileRefusedException>(() => _viewer.OpenSession(Write("z.pdf", Png()), Panel, [SessionKinds.Pdf]));
	}

	[Fact]
	public void A_pdf_over_fifty_megabytes_is_refused_and_unknown_kinds_are_not_in_the_table()
	{
		var path = Path.Combine(_dir, "big.pdf");
		using (var file = File.Create(path))
		{
			file.Write(PdfBytes);
			file.SetLength(SessionKinds.Pdf.ReadCap + 1);
		}

		Assert.Throws<SessionFileRefusedException>(() => _viewer.OpenSession(path, Panel, [SessionKinds.Pdf]));
		Assert.Throws<UnknownSessionKindException>(() => SessionKinds.Named(["image", "exe"]));
		Assert.Throws<UnknownSessionKindException>(() => SessionKinds.Named([]));
	}

	[Fact]
	public async Task A_pdf_session_saves_pdfs_and_jpegs_with_the_right_magic_and_a_token_and_not_pngs()
	{
		var session = _viewer.OpenSession(Write("only.pdf", PdfBytes), "section:Pdf", [SessionKinds.Pdf]);
		_viewer.IssueToken = path => "token-for-" + Path.GetFileName(path);
		byte[] jpg = [0xff, 0xd8, 0xff, 0xe0, 1, 2];

		var pdf = await _viewer.ServeAsync(Put(session, "merged.pdf", PdfBytes));
		var jpeg = await _viewer.ServeAsync(Put(session, "page 1.jpg", jpg));
		var png = await _viewer.ServeAsync(Put(session, "page.png", Png()));
		var wrongMagic = await _viewer.ServeAsync(Put(session, "fake.pdf", Png()));

		Assert.Equal(201, pdf.Status);
		Assert.Equal("token-for-merged.pdf", System.Text.Json.JsonDocument.Parse(pdf.Body).RootElement.GetProperty("token").GetString());
		Assert.Equal(201, jpeg.Status);
		Assert.Equal(400, png.Status);
		Assert.Equal(400, wrongMagic.Status);
		Assert.Equal(["merged.pdf", "page 1.jpg"], Directory.GetFiles(Path.Combine(_dir, "downloads")).Select(Path.GetFileName).Order());
	}

	[Fact]
	public async Task A_pdf_save_over_fifty_megabytes_is_refused_before_reading_and_one_quota_covers_every_format()
	{
		var session = OpenPdf();
		var untouched = new MemoryStream(PdfBytes);

		var big = await _viewer.ServeAsync(Put(session, "huge.pdf", [], declared: SaveFormats.Pdf.Cap + 1, stream: untouched));

		Assert.Equal(413, big.Status);
		Assert.Equal(0, untouched.Position);
		_viewer.SaveBudget = PdfBytes.Length + 4;
		Assert.Equal(201, (await _viewer.ServeAsync(Put(session, "a.pdf", PdfBytes))).Status);
		Assert.Equal(507, (await _viewer.ServeAsync(Put(session, "b.jpg", [0xff, 0xd8, 0xff, 1, 2, 3, 4, 5]))).Status);
	}

	[Fact]
	public async Task The_model_is_not_served_to_a_tool_that_opened_only_a_pdf()
	{
		_viewer.OpenSession(Write("d.pdf", PdfBytes), "section:Pdf", [SessionKinds.Pdf]);

		Assert.Equal(404, (await _viewer.ServeAsync(Get(Model))).Status);
	}

	private static ViewerRequest PutWithReferer(ViewerSession session, byte[] body, string method = "PUT") =>
		new(method, $"https://{ViewerFiles.Host}/save/{session.Id}/a.png", null, null, "https://app.zing/index.html#surface", new MemoryStream(body), body.Length);

	[Fact]
	public async Task A_PUT_or_a_preflight_needs_Zing_s_origin_header_a_referer_is_not_enough()
	{
		var session = Open();

		Assert.Equal(404, (await _viewer.ServeAsync(PutWithReferer(session, Png()))).Status);
		Assert.Equal(404, (await _viewer.ServeAsync(PutWithReferer(session, Png(), "OPTIONS"))).Status);
		Assert.Equal(200, (await _viewer.ServeAsync(Get(session.Url, origin: null, referer: "https://app.zing/index.html#surface"))).Status);
		Assert.False(Directory.Exists(Path.Combine(_dir, "downloads")) && Directory.GetFileSystemEntries(Path.Combine(_dir, "downloads")).Length > 0);
	}

	[Fact]
	public async Task A_PUT_with_no_body_is_a_400_and_nothing_goes_to_the_error_hook()
	{
		var session = Open();
		var bodiless = new ViewerRequest("PUT", $"https://{ViewerFiles.Host}/save/{session.Id}/a.png", null, App, null, null, null);

		var reply = await _viewer.ServeAsync(bodiless);

		Assert.Equal(400, reply.Status);
		Assert.Contains("no body", Encoding.UTF8.GetString(reply.Body));
		Assert.Empty(_errors);
	}

	[Fact]
	public async Task A_save_that_cannot_be_written_says_it_cannot_be_saved_not_that_it_cannot_be_read()
	{
		var session = Open();
		_viewer.SaveDirectory = Write("not-a-folder", [1]);

		var reply = await _viewer.ServeAsync(Put(session, "a.png", Png()));

		Assert.Equal(500, reply.Status);
		var text = Encoding.UTF8.GetString(reply.Body);
		Assert.Contains("can't be saved", text);
		Assert.DoesNotContain("read", text);
		Assert.Single(_errors);
		_errors.Clear();
	}

	[Fact]
	public async Task A_body_that_stops_short_of_its_content_length_saves_nothing_and_gives_no_token()
	{
		var session = Open();
		_viewer.IssueToken = _ => throw new Xunit.Sdk.XunitException("no token for a file that was not saved");

		var reply = await _viewer.ServeAsync(Put(session, "a.png", Png(30), declared: Png(30).Length + 100));

		Assert.Equal(400, reply.Status);
		Assert.Contains("stopped early", Encoding.UTF8.GetString(reply.Body));
		Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "downloads")));
	}

	/// <summary>A body that gives its bytes when released, so a save can be left in flight.</summary>
	private sealed class GatedBody(byte[] bytes) : Stream
	{
		private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _at;
		public Task Asked => _asked.Task;
		public void Release() => _go.TrySetResult();
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => bytes.Length;
		public override long Position { get => _at; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			_asked.TrySetResult();
			await _go.Task.WaitAsync(cancellationToken);
			var count = Math.Min(buffer.Length, bytes.Length - _at);
			bytes.AsMemory(_at, count).CopyTo(buffer);
			_at += count;
			return count;
		}
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	[Fact]
	public async Task Saves_in_flight_reserve_their_length_so_the_quota_cannot_be_overshot_and_a_failed_save_gives_it_back()
	{
		var session = Open();
		_viewer.SaveBudget = 150;
		var slow = new GatedBody(Png(92));
		var first = _viewer.ServeAsync(Put(session, "a.png", [], declared: 100, stream: slow));
		await slow.Asked;

		var second = await _viewer.ServeAsync(Put(session, "b.png", Png(92), declared: 100));

		Assert.Equal(507, second.Status);
		slow.Release();
		Assert.Equal(201, (await first).Status);

		// A save that fails returns what it reserved.
		_viewer.SaveBudget = 100 + 100;
		var bad = await _viewer.ServeAsync(Put(session, "c.png", Encoding.ASCII.GetBytes(new string('x', 100)), declared: 100));
		Assert.Equal(400, bad.Status);
		Assert.Equal(201, (await _viewer.ServeAsync(Put(session, "d.png", Png(92), declared: 100))).Status);
	}

	[Fact]
	public async Task Closing_the_session_while_a_save_is_arriving_stops_it_and_the_file_never_lands()
	{
		var session = Open();
		var slow = new GatedBody(Png(40));
		var save = _viewer.ServeAsync(Put(session, "a.png", [], declared: Png(40).Length, stream: slow));
		await slow.Asked;

		_viewer.Close(session.Id);
		slow.Release();

		Assert.Equal(404, (await save).Status);
		Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "downloads")));
	}

	[Fact]
	public async Task A_closed_panel_stops_its_save_too()
	{
		var session = Open();
		var slow = new GatedBody(Png(40));
		var save = _viewer.ServeAsync(Put(session, "a.png", [], declared: Png(40).Length, stream: slow));
		await slow.Asked;

		_viewer.CloseExcept(new HashSet<string>());
		slow.Release();

		Assert.Equal(404, (await save).Status);
		Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "downloads")));
	}

	[Fact]
	public async Task An_upload_that_stalls_is_ended_with_a_408_and_leaves_nothing()
	{
		var session = Open();
		_viewer.SaveIdleTimeout = TimeSpan.FromMilliseconds(200);

		var reply = await _viewer.ServeAsync(Put(session, "a.png", [], declared: 100, stream: new GatedBody(Png(92))));

		Assert.Equal(408, reply.Status);
		Assert.Contains("stalled", Encoding.UTF8.GetString(reply.Body));
		Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "downloads")));
	}

	[Fact]
	public async Task The_model_stays_held_while_its_reply_is_open_and_is_released_when_that_is_disposed()
	{
		Open();

		var reply = await _viewer.ServeAsync(Get(Model));

		Assert.NotNull(reply.Lease);
		Assert.Throws<EngineInUseException>(() => _engines.Store.Remove(EngineIds.BackgroundModel));
		reply.Lease.Dispose();
		_engines.Store.Remove(EngineIds.BackgroundModel);
	}

	[Fact]
	public async Task A_HEAD_for_the_model_holds_nothing()
	{
		Open();

		var reply = await _viewer.ServeAsync(Get(Model, method: "HEAD"));

		Assert.Equal(200, reply.Status);
		Assert.Null(reply.Lease);
		_engines.Store.Remove(EngineIds.BackgroundModel);
	}

	[Fact]
	public async Task A_model_file_locked_by_another_program_is_a_503_and_the_hold_is_given_back()
	{
		Open();
		using var hold = new FileStream(_engines.Store.InstalledPath(EngineIds.BackgroundModel), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		var reply = await _viewer.ServeAsync(Get(Model));

		Assert.Equal(503, reply.Status);
		Assert.Null(reply.Lease);
		Assert.Null(reply.WholeFile);
		hold.Dispose();
		_engines.Store.Remove(EngineIds.BackgroundModel);
		_errors.Clear();
	}

	[Fact]
	public async Task A_large_picture_locked_by_another_program_is_a_503_before_the_host_opens_it()
	{
		var path = Path.Combine(_dir, "large.png");
		using (var file = File.Create(path))
		{
			file.Write(Png());
			file.SetLength(ViewerFiles.WholeFileLimit + 1);
		}
		var session = _viewer.OpenSession(path, Panel, [SessionKinds.Image]);
		using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		var reply = await _viewer.ServeAsync(Get(session.Url));

		Assert.Equal(503, reply.Status);
		Assert.Null(reply.WholeFile);
		_errors.Clear();
	}
}
