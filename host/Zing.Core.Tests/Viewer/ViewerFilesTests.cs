using System.Diagnostics;
using System.Text;
using Zing.Core.Viewer;

namespace Zing.Core.Tests.Viewer;

public sealed class ViewerFilesTests : IDisposable
{
	private const string App = "https://app.zing";
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "zing-viewer-test-" + Guid.NewGuid().ToString("N"));
	private readonly List<(string What, Exception Error)> _errors = [];
	private readonly ViewerFiles _viewer;

	public ViewerFilesTests()
	{
		Directory.CreateDirectory(_dir);
		_viewer = new ViewerFiles(Path.Combine(_dir, "viewer"), (what, error) => _errors.Add((what, error)));
	}

	public void Dispose()
	{
		_viewer.Dispose();
		Assert.Empty(_errors);
		Directory.Delete(_dir, recursive: true);
	}

	private string Write(string name, byte[] bytes)
	{
		var path = Path.Combine(_dir, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	private static byte[] Bytes(int count) => [.. Enumerable.Range(0, count).Select(i => (byte)(i % 251))];

	private static ViewerRequest Get(ViewerSession session, string? range = null, string? origin = App, string? referer = null, string method = "GET") =>
		new(method, session.Url, range, origin, referer);

	private ViewerSession OpenNative(string name, byte[] bytes, string panel = "section:Files")
	{
		var path = Write(name, bytes);
		return _viewer.OpenNative(path, panel, ViewerFormats.For(Path.GetExtension(name))!);
	}

	[Fact]
	public void Formats_come_from_the_generated_lists()
	{
		Assert.Equal(ViewerKind.Image, ViewerFormats.For("heic")!.Kind);
		Assert.Equal(ViewerKind.Image, ViewerFormats.For(".CR2")!.Kind);
		Assert.Equal(ViewerKind.Video, ViewerFormats.For("mov")!.Kind);
		Assert.Equal(ViewerKind.Video, ViewerFormats.For("mkv")!.Kind);
		Assert.Equal(ViewerKind.Audio, ViewerFormats.For("wma")!.Kind);
		Assert.Equal(ViewerKind.Native, ViewerFormats.For("pdf")!.Kind);
		Assert.Equal(ViewerKind.Native, ViewerFormats.For("mp4")!.Kind);
		Assert.Equal("application/pdf", ViewerFormats.For("pdf")!.ContentType);
		Assert.Null(ViewerFormats.For("obj"));
		Assert.Null(ViewerFormats.For("html"));
		Assert.Null(ViewerFormats.For("svg"));
		Assert.Null(ViewerFormats.For(""));
	}

	[Fact]
	public async Task A_native_file_is_served_whole_with_a_type_chosen_from_its_extension()
	{
		var bytes = Bytes(1000);
		var session = OpenNative("clip.mp4", bytes);
		var reply = await _viewer.ServeAsync(Get(session));
		Assert.Equal(200, reply.Status);
		Assert.Equal(bytes, reply.Body);
		Assert.Equal("video/mp4", reply.Headers["Content-Type"]);
		Assert.Equal("1000", reply.Headers["Content-Length"]);
		Assert.Equal("bytes", reply.Headers["Accept-Ranges"]);
		Assert.Equal("nosniff", reply.Headers["X-Content-Type-Options"]);
		Assert.Equal(App, reply.Headers["Access-Control-Allow-Origin"]);
		Assert.Equal("no-store", reply.Headers["Cache-Control"]);
	}

	[Fact]
	public async Task A_page_renamed_to_an_image_is_still_an_image_that_the_browser_may_not_sniff()
	{
		var session = OpenNative("evil.png", Encoding.UTF8.GetBytes("<html><script>alert(1)</script>"));
		var reply = await _viewer.ServeAsync(Get(session));
		Assert.Equal("image/png", reply.Headers["Content-Type"]);
		Assert.Equal("nosniff", reply.Headers["X-Content-Type-Options"]);
		Assert.StartsWith("sandbox;", reply.Headers["Content-Security-Policy"]);
		Assert.Contains("default-src 'none'", reply.Headers["Content-Security-Policy"]);
	}

	[Fact]
	public async Task A_pdf_is_not_sandboxed_because_the_pdf_viewer_would_refuse_it_but_a_page_cannot_frame_it()
	{
		var session = OpenNative("doc.pdf", Bytes(50));
		var reply = await _viewer.ServeAsync(Get(session));
		var policy = reply.Headers["Content-Security-Policy"];
		Assert.DoesNotContain("sandbox", policy);
		Assert.Contains("frame-ancestors https://app.zing", policy);
		Assert.Equal("application/pdf", reply.Headers["Content-Type"]);
	}

	[Fact]
	public async Task A_range_gets_206_with_its_bytes_and_the_total()
	{
		var bytes = Bytes(1000);
		var session = OpenNative("clip.mp4", bytes);
		var reply = await _viewer.ServeAsync(Get(session, "bytes=100-199"));
		Assert.Equal(206, reply.Status);
		Assert.Equal(bytes[100..200], reply.Body);
		Assert.Equal("bytes 100-199/1000", reply.Headers["Content-Range"]);
		Assert.Equal("100", reply.Headers["Content-Length"]);
	}

	[Fact]
	public async Task An_open_ended_range_and_a_suffix_range_are_answered()
	{
		var bytes = Bytes(1000);
		var session = OpenNative("clip.mp4", bytes);
		var open = await _viewer.ServeAsync(Get(session, "bytes=900-"));
		Assert.Equal(206, open.Status);
		Assert.Equal(bytes[900..], open.Body);
		var suffix = await _viewer.ServeAsync(Get(session, "bytes=-10"));
		Assert.Equal(206, suffix.Status);
		Assert.Equal(bytes[990..], suffix.Body);
		Assert.Equal("bytes 990-999/1000", suffix.Headers["Content-Range"]);
	}

	[Fact]
	public async Task A_range_past_the_end_is_416_and_says_how_long_the_file_is()
	{
		var session = OpenNative("clip.mp4", Bytes(1000));
		var reply = await _viewer.ServeAsync(Get(session, "bytes=1000-"));
		Assert.Equal(416, reply.Status);
		Assert.Equal("bytes */1000", reply.Headers["Content-Range"]);
		Assert.Empty(reply.Body);
	}

	[Theory]
	[InlineData("bytes=5-2")]
	[InlineData("bytes=0-1,5-9")]
	[InlineData("items=0-5")]
	[InlineData("bytes=abc")]
	[InlineData("bytes=-")]
	public async Task A_range_it_cannot_make_sense_of_is_416(string range)
	{
		var session = OpenNative("clip.mp4", Bytes(1000));
		Assert.Equal(416, (await _viewer.ServeAsync(Get(session, range))).Status);
	}

	[Fact]
	public async Task A_big_file_is_served_a_chunk_at_a_time_and_never_read_whole()
	{
		var path = Path.Combine(_dir, "big.mp4");
		using (var big = new FileStream(path, FileMode.Create)) big.SetLength(ViewerFiles.WholeFileLimit * 4L);
		var session = _viewer.OpenNative(path, "section:Files", ViewerFormats.For("mp4")!);
		var reply = await _viewer.ServeAsync(Get(session, "bytes=0-"));
		Assert.Equal(206, reply.Status);
		Assert.Equal(ViewerFiles.MaxChunk, reply.Body.Length);
		Assert.Equal($"bytes 0-{ViewerFiles.MaxChunk - 1}/{ViewerFiles.WholeFileLimit * 4L}", reply.Headers["Content-Range"]);
	}

	[Fact]
	public async Task A_big_file_asked_for_without_a_range_is_handed_over_as_a_file_not_a_buffer()
	{
		var path = Path.Combine(_dir, "big.pdf");
		using (var big = new FileStream(path, FileMode.Create)) big.SetLength(ViewerFiles.WholeFileLimit + 1);
		var session = _viewer.OpenNative(path, "section:Files", ViewerFormats.For("pdf")!);
		var reply = await _viewer.ServeAsync(Get(session));
		Assert.Equal(200, reply.Status);
		Assert.Equal(path, reply.WholeFile);
		Assert.Empty(reply.Body);
		Assert.Equal((ViewerFiles.WholeFileLimit + 1).ToString(), reply.Headers["Content-Length"]);
	}

	[Fact]
	public async Task HEAD_has_the_headers_and_no_body()
	{
		var session = OpenNative("clip.mp4", Bytes(1000));
		var reply = await _viewer.ServeAsync(Get(session, method: "HEAD"));
		Assert.Equal(200, reply.Status);
		Assert.Empty(reply.Body);
		Assert.Equal("1000", reply.Headers["Content-Length"]);
	}

	[Fact]
	public async Task A_request_that_does_not_come_from_a_zing_view_gets_404()
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		foreach (var (origin, referer) in new (string?, string?)[] { (null, null), ("https://evil.example", null), ("https://app.zing.evil.example", null), ("http://app.zing", null), (null, "https://evil.example/"), (null, "https://app.zing.evil.example/x"), ("https://evil.example", "https://app.zing/") })
		{
			var reply = await _viewer.ServeAsync(Get(session, origin: origin, referer: referer));
			Assert.Equal(404, reply.Status);
			Assert.Empty(reply.Body);
			Assert.False(reply.Headers.ContainsKey("Access-Control-Allow-Origin"));
		}
	}

	[Fact]
	public async Task A_zing_view_is_known_by_its_origin_or_its_referer()
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		Assert.Equal(200, (await _viewer.ServeAsync(Get(session, origin: App))).Status);
		Assert.Equal(200, (await _viewer.ServeAsync(Get(session, origin: null, referer: "https://app.zing/"))).Status);
		Assert.Equal(200, (await _viewer.ServeAsync(Get(session, origin: null, referer: "https://app.zing/index.html#overlay"))).Status);
	}

	[Fact]
	public async Task A_token_that_was_never_issued_or_has_been_closed_gets_404()
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		var unknown = new ViewerRequest("GET", "https://view.zing/" + new string('a', 32), null, App, null);
		Assert.Equal(404, (await _viewer.ServeAsync(unknown)).Status);
		_viewer.Close(session.Id);
		Assert.Equal(404, (await _viewer.ServeAsync(Get(session))).Status);
		_viewer.Close(session.Id);
	}

	[Fact]
	public async Task Another_window_cannot_read_this_windows_file()
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		using var other = new ViewerFiles(Path.Combine(_dir, "other"), (_, _) => { });
		Assert.Equal(404, (await other.ServeAsync(Get(session))).Status);
	}

	[Theory]
	[InlineData("/")]
	[InlineData("/{t}/more")]
	[InlineData("/{t}?x=1")]
	[InlineData("/{t}/")]
	[InlineData("/{T}")]
	public async Task Only_the_exact_address_works(string shape)
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		var url = "https://view.zing" + shape.Replace("{t}", session.Id).Replace("{T}", session.Id.ToUpperInvariant());
		Assert.Equal(404, (await _viewer.ServeAsync(new ViewerRequest("GET", url, null, App, null))).Status);
	}

	[Fact]
	public async Task Another_host_is_not_ours_to_answer()
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		var url = session.Url.Replace("view.zing", "view.zing.evil.example", StringComparison.Ordinal);
		Assert.Equal(404, (await _viewer.ServeAsync(new ViewerRequest("GET", url, null, App, null))).Status);
	}

	[Fact]
	public async Task Methods_that_write_are_refused()
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		Assert.Equal(405, (await _viewer.ServeAsync(Get(session, method: "POST"))).Status);
		Assert.Equal(405, (await _viewer.ServeAsync(Get(session, method: "DELETE"))).Status);
	}

	[Fact]
	public async Task A_preflight_from_a_zing_view_is_answered_and_from_anything_else_is_not()
	{
		var session = OpenNative("clip.mp4", Bytes(100));
		var ok = await _viewer.ServeAsync(Get(session, method: "OPTIONS"));
		Assert.Equal(204, ok.Status);
		Assert.Equal(App, ok.Headers["Access-Control-Allow-Origin"]);
		Assert.Contains("Range", ok.Headers["Access-Control-Allow-Headers"]);
		Assert.Equal(404, (await _viewer.ServeAsync(Get(session, origin: "https://evil.example", method: "OPTIONS"))).Status);
	}

	[Fact]
	public async Task Opening_a_second_file_in_a_panel_closes_the_first()
	{
		var first = OpenNative("a.mp4", Bytes(10));
		var second = OpenNative("b.mp4", Bytes(10));
		Assert.Equal(404, (await _viewer.ServeAsync(Get(first))).Status);
		Assert.Equal(200, (await _viewer.ServeAsync(Get(second))).Status);
	}

	[Fact]
	public async Task Panels_that_are_gone_lose_their_files_and_minimized_ones_keep_theirs()
	{
		var kept = OpenNative("a.mp4", Bytes(10), "panel-a");
		var lost = OpenNative("b.mp4", Bytes(10), "panel-b");
		_viewer.CloseExcept(new HashSet<string> { "panel-a" });
		Assert.Equal(200, (await _viewer.ServeAsync(Get(kept))).Status);
		Assert.Equal(404, (await _viewer.ServeAsync(Get(lost))).Status);
	}

	[Fact]
	public void A_native_file_is_not_copied_and_closing_it_leaves_the_original()
	{
		var path = Write("clip.mp4", Bytes(10));
		var session = _viewer.OpenNative(path, "p", ViewerFormats.For("mp4")!);
		Assert.Null(session.Folder);
		_viewer.Close(session.Id);
		Assert.True(File.Exists(path));
	}

	// --- converted copies

	private ViewerSession OpenConverted(string source, string panel = "section:Files") =>
		_viewer.OpenConverted(source, panel, ViewerFormats.For(Path.GetExtension(source))!);

	[Fact]
	public void A_converted_copy_lives_in_a_folder_named_for_this_process_under_the_root()
	{
		var session = OpenConverted("holiday.mov");
		Assert.Equal(Path.Combine(_dir, "viewer"), Path.GetDirectoryName(session.Folder));
		Assert.StartsWith($"{Environment.ProcessId}_", Path.GetFileName(session.Folder));
		Assert.True(Directory.Exists(session.Folder));
		Assert.Equal("view.mp4", session.OutputName);
		Assert.True(session.Stream);
		Assert.Equal("view.png", OpenConverted("shot.heic", "q").OutputName);
		Assert.False(OpenConverted("shot.heic", "r").Stream);
		Assert.Equal("view.m4a", OpenConverted("song.wma", "s").OutputName);
	}

	[Fact]
	public async Task A_growing_copy_answers_204_until_bytes_are_ready_and_206_with_an_unknown_total_after()
	{
		var session = OpenConverted("holiday.mov");
		var live = Path.Combine(session.Folder!, "live.mp4");
		_viewer.BeginLive(session.Id, live);
		// The engine has not made the file yet.
		var none = await _viewer.ServeAsync(Get(session, "bytes=0-"));
		Assert.Equal(204, none.Status);
		Assert.Empty(none.Body);
		await File.WriteAllBytesAsync(live, Bytes(100), TestContext.Current.CancellationToken);
		var some = await _viewer.ServeAsync(Get(session, "bytes=0-"));
		Assert.Equal(206, some.Status);
		Assert.Equal(Bytes(100), some.Body);
		Assert.Equal("bytes 0-99/*", some.Headers["Content-Range"]);
		// Nothing past what is written yet.
		Assert.Equal(204, (await _viewer.ServeAsync(Get(session, "bytes=100-"))).Status);
		await using (var append = new FileStream(live, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) await append.WriteAsync(Bytes(50), TestContext.Current.CancellationToken);
		var more = await _viewer.ServeAsync(Get(session, "bytes=100-"));
		Assert.Equal(206, more.Status);
		Assert.Equal(50, more.Body.Length);
	}

	[Fact]
	public async Task A_growing_copy_can_be_read_while_the_engine_still_has_it_open()
	{
		var session = OpenConverted("holiday.mov");
		var live = Path.Combine(session.Folder!, "live.mp4");
		_viewer.BeginLive(session.Id, live);
		await using var writer = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
		await writer.WriteAsync(Bytes(64), TestContext.Current.CancellationToken);
		await writer.FlushAsync(TestContext.Current.CancellationToken);
		Assert.Equal(206, (await _viewer.ServeAsync(Get(session, "bytes=0-"))).Status);
	}

	[Fact]
	public async Task The_instant_between_the_engine_moving_its_file_and_the_exit_is_204_not_an_error()
	{
		var session = OpenConverted("holiday.mov");
		_viewer.BeginLive(session.Id, Path.Combine(session.Folder!, "gone-already.mp4"));
		Assert.Equal(204, (await _viewer.ServeAsync(Get(session, "bytes=0-"))).Status);
	}

	[Fact]
	public async Task A_growing_copy_whose_file_is_being_deleted_is_not_yet_not_an_error()
	{
		var session = OpenConverted("holiday.mov");
		var live = Path.Combine(session.Folder!, "live.mp4");
		_viewer.BeginLive(session.Id, live);
		// The engine's job folder is being removed while it still has the file open: Windows keeps the name, and refuses to open it.
		await using var writer = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
		await writer.WriteAsync(Bytes(10), TestContext.Current.CancellationToken);
		File.Delete(live);
		var reply = await _viewer.ServeAsync(Get(session, "bytes=0-"));
		Assert.Equal(204, reply.Status);
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public async Task A_growing_copy_that_exists_but_cannot_be_opened_is_an_error_not_a_wait()
	{
		var session = OpenConverted("holiday.mov");
		var live = Path.Combine(session.Folder!, "live.mp4");
		_viewer.BeginLive(session.Id, live);
		await File.WriteAllBytesAsync(live, Bytes(10), TestContext.Current.CancellationToken);
		var file = new FileInfo(live);
		var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
		var security = file.GetAccessControl();
		var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
		security.AddAccessRule(deny);
		file.SetAccessControl(security);
		try
		{
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _viewer.ServeAsync(Get(session, "bytes=0-")));
		}
		finally
		{
			security.RemoveAccessRule(deny);
			file.SetAccessControl(security);
		}
	}

	[Fact]
	public async Task A_finished_copy_reads_from_its_final_place_and_416_is_the_real_end()
	{
		var session = OpenConverted("holiday.mov");
		_viewer.BeginLive(session.Id, Path.Combine(session.Folder!, "live.mp4"));
		var final = Path.Combine(session.Folder!, session.OutputName!);
		await File.WriteAllBytesAsync(final, Bytes(100), TestContext.Current.CancellationToken);
		_viewer.Complete(session.Id, final);
		var tail = await _viewer.ServeAsync(Get(session, "bytes=90-"));
		Assert.Equal(206, tail.Status);
		Assert.Equal("bytes 90-99/100", tail.Headers["Content-Range"]);
		var end = await _viewer.ServeAsync(Get(session, "bytes=100-"));
		Assert.Equal(416, end.Status);
		Assert.Equal("bytes */100", end.Headers["Content-Range"]);
	}

	[Fact]
	public async Task A_failed_conversion_answers_404()
	{
		var session = OpenConverted("holiday.mov");
		await File.WriteAllBytesAsync(Path.Combine(session.Folder!, "half-made.mp4"), Bytes(10), TestContext.Current.CancellationToken);
		_viewer.Fail(session.Id);
		Assert.Equal(404, (await _viewer.ServeAsync(Get(session, "bytes=0-"))).Status);
		Assert.False(Directory.Exists(session.Folder));
	}

	[Fact]
	public async Task A_converted_image_is_404_until_it_is_done()
	{
		var session = OpenConverted("shot.heic");
		Assert.Equal(404, (await _viewer.ServeAsync(Get(session))).Status);
		var final = Path.Combine(session.Folder!, session.OutputName!);
		await File.WriteAllBytesAsync(final, Bytes(30), TestContext.Current.CancellationToken);
		_viewer.Complete(session.Id, final);
		var reply = await _viewer.ServeAsync(Get(session));
		Assert.Equal(200, reply.Status);
		Assert.Equal("image/png", reply.Headers["Content-Type"]);
		Assert.Equal(Bytes(30), reply.Body);
	}

	[Fact]
	public async Task Closing_a_converted_file_deletes_its_copy_and_a_late_exit_deletes_it_again()
	{
		var session = OpenConverted("holiday.mov");
		var folder = session.Folder!;
		var final = Path.Combine(folder, session.OutputName!);
		await File.WriteAllBytesAsync(final, Bytes(10), TestContext.Current.CancellationToken);
		_viewer.Close(session.Id);
		Assert.False(Directory.Exists(folder));
		// The engine had begun placing its file when the close came; the exit still arrives.
		Directory.CreateDirectory(folder);
		await File.WriteAllBytesAsync(final, Bytes(10), TestContext.Current.CancellationToken);
		_viewer.Complete(session.Id, final);
		Assert.False(Directory.Exists(folder));
		Assert.Equal(404, (await _viewer.ServeAsync(Get(session))).Status);
	}

	[Fact]
	public void Disposing_deletes_every_copy()
	{
		var one = OpenConverted("a.mov", "p1");
		var two = OpenConverted("b.heic", "p2");
		_viewer.Dispose();
		Assert.False(Directory.Exists(one.Folder));
		Assert.False(Directory.Exists(two.Folder));
	}

	[Fact]
	public async Task A_copy_that_a_reader_still_holds_is_deleted_by_a_retry_without_a_report()
	{
		_viewer.RetryDelay = TimeSpan.FromMilliseconds(150);
		var session = OpenConverted("a.mov");
		var final = Path.Combine(session.Folder!, "view.mp4");
		var held = new FileStream(final, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
		_viewer.Close(session.Id);
		// The close itself does not wait: the folder is still there and nothing is reported yet.
		Assert.True(Directory.Exists(session.Folder));
		Assert.Empty(_errors);
		held.Dispose();
		await Task.Delay(800, TestContext.Current.CancellationToken);
		Assert.False(Directory.Exists(session.Folder));
		Assert.Empty(_errors);
	}

	[Fact]
	public async Task A_copy_that_is_still_held_after_the_retry_is_reported_and_left_for_the_next_start()
	{
		_viewer.RetryDelay = TimeSpan.FromMilliseconds(100);
		var session = OpenConverted("a.mov");
		var final = Path.Combine(session.Folder!, "view.mp4");
		using (new FileStream(final, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
		{
			_viewer.Close(session.Id);
			for (var i = 0; i < 50 && _errors.Count == 0; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
			Assert.Single(_errors);
			Assert.True(Directory.Exists(session.Folder));
		}
		_errors.Clear();
	}

	// --- the sweep at the next start

	[Fact]
	public void The_sweep_deletes_what_a_dead_zing_left_and_keeps_what_a_live_one_has()
	{
		var root = Path.Combine(_dir, "sweep");
		var dead = Path.Combine(root, "4000000_aaaa");
		var alive = Path.Combine(root, "4000001_bbbb");
		var strange = Path.Combine(root, "not-ours");
		foreach (var folder in new[] { dead, alive, strange }) Directory.CreateDirectory(Path.Combine(folder, "inner"));
		File.WriteAllText(Path.Combine(dead, "inner", "view.mp4"), "x");
		File.WriteAllText(Path.Combine(root, "stray.txt"), "x");
		ViewerFiles.SweepLeftovers(root, pid => pid == 4000001, (_, _) => { });
		Assert.False(Directory.Exists(dead));
		Assert.False(Directory.Exists(strange));
		Assert.False(File.Exists(Path.Combine(root, "stray.txt")));
		Assert.True(Directory.Exists(alive));
	}

	[Fact]
	public void The_sweep_of_a_root_that_is_not_there_is_nothing()
	{
		ViewerFiles.SweepLeftovers(Path.Combine(_dir, "nothing-here"), _ => false, (_, _) => { });
	}

	[Fact]
	public void The_sweep_does_not_follow_a_junction_out_of_the_folder()
	{
		var decoy = Path.Combine(_dir, "decoy");
		Directory.CreateDirectory(Path.Combine(decoy, "keep"));
		File.WriteAllText(Path.Combine(decoy, "keep", "precious.txt"), "do not delete");
		File.WriteAllText(Path.Combine(decoy, "top.txt"), "do not delete");
		var root = Path.Combine(_dir, "sweep2");
		var leftover = Path.Combine(root, "4000000_cccc");
		Directory.CreateDirectory(leftover);
		MakeJunction(Path.Combine(leftover, "link"), decoy);
		ViewerFiles.SweepLeftovers(root, _ => false, (what, error) => throw new InvalidOperationException($"{what}: {error}"));
		Assert.False(Directory.Exists(leftover));
		Assert.True(File.Exists(Path.Combine(decoy, "keep", "precious.txt")));
		Assert.True(File.Exists(Path.Combine(decoy, "top.txt")));
	}

	[Fact]
	public void The_sweep_does_not_follow_a_junction_at_the_top_either()
	{
		var decoy = Path.Combine(_dir, "decoy2");
		Directory.CreateDirectory(decoy);
		File.WriteAllText(Path.Combine(decoy, "precious.txt"), "do not delete");
		var root = Path.Combine(_dir, "sweep3");
		Directory.CreateDirectory(root);
		MakeJunction(Path.Combine(root, "4000000_dddd"), decoy);
		ViewerFiles.SweepLeftovers(root, _ => false, (what, error) => throw new InvalidOperationException($"{what}: {error}"));
		Assert.False(Directory.Exists(Path.Combine(root, "4000000_dddd")));
		Assert.True(File.Exists(Path.Combine(decoy, "precious.txt")));
	}

	[Fact]
	public void The_sweep_of_a_root_that_is_itself_a_junction_removes_the_link_and_leaves_the_target_alone()
	{
		var target = Path.Combine(_dir, "users-folder");
		Directory.CreateDirectory(Path.Combine(target, "docs"));
		File.WriteAllText(Path.Combine(target, "docs", "mine.txt"), "keep");
		File.WriteAllText(Path.Combine(target, "4000000_looks-like-ours"), "keep");
		var root = Path.Combine(_dir, "planted");
		MakeJunction(root, target);
		ViewerFiles.SweepLeftovers(root, _ => false, (what, error) => throw new InvalidOperationException($"{what}: {error}"));
		Assert.True(File.Exists(Path.Combine(target, "docs", "mine.txt")));
		Assert.True(File.Exists(Path.Combine(target, "4000000_looks-like-ours")));
		Assert.True(Directory.Exists(root));
		Assert.False(new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint));
	}

	[Fact]
	public void Opening_a_copy_when_the_root_is_a_junction_replaces_the_link_and_never_writes_through_it()
	{
		var target = Path.Combine(_dir, "users-folder2");
		Directory.CreateDirectory(target);
		var root = Path.Combine(_dir, "planted2");
		MakeJunction(root, target);
		using var viewer = new ViewerFiles(root, (_, _) => { });
		var session = viewer.OpenConverted("a.mov", "p", ViewerFormats.For("mov")!);
		Assert.Empty(Directory.GetFileSystemEntries(target));
		Assert.False(new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint));
		Assert.True(Directory.Exists(session.Folder));
	}

	[Fact]
	public void A_zing_that_is_running_is_known_by_its_process_name()
	{
		Assert.False(ViewerFiles.IsZingRunning(int.MaxValue));
		// This test host is a process that is running, but it is not Zing.
		Assert.False(ViewerFiles.IsZingRunning(Environment.ProcessId));
	}

	private static void MakeJunction(string link, string target)
	{
		var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
		using var process = Process.Start(start)!;
		process.WaitForExit();
		Assert.Equal(0, process.ExitCode);
	}
}
