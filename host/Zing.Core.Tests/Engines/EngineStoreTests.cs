using System.Diagnostics;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed class EngineStoreTests : IDisposable
{
	private readonly EngineFixture _fixture = new();
	private static readonly IProgress<double> NoProgress = new InlineProgress<double>(_ => { });

	public void Dispose() => _fixture.Dispose();

	private static CancellationToken Never => CancellationToken.None;

	[Fact]
	public async Task A_single_file_engine_downloads_and_its_path_is_returned()
	{
		var body = EngineFixture.Bytes(200_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));

		var path = await store.EnsureAsync("ffmpeg", NoProgress, Never);

		Assert.Equal(Path.Combine(_fixture.Root, "ffmpeg", "ffmpeg.exe"), path);
		Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
		Assert.Equal(["ffmpeg"], _fixture.Entries());
	}

	[Fact]
	public async Task An_archive_engine_is_extracted_and_its_executable_found_inside()
	{
		var zip = EngineFixture.Zip(("pandoc-1/pandoc.exe", "program"), ("pandoc-1/COPYING", "licence"));
		_fixture.Serve("pandoc/pandoc.zip", zip);
		var store = _fixture.Store(_fixture.Archive("pandoc", zip, @"pandoc-1\pandoc.exe"));

		var path = await store.EnsureAsync("pandoc", NoProgress, Never);

		Assert.Equal(Path.Combine(_fixture.Root, "pandoc", "pandoc-1", "pandoc.exe"), path);
		Assert.Equal("program", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Progress_reaches_one_as_the_download_finishes()
	{
		var body = EngineFixture.Bytes(300_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		var seen = new List<double>();

		await store.EnsureAsync("ffmpeg", new InlineProgress<double>(seen.Add), Never);

		Assert.NotEmpty(seen);
		Assert.Equal(1.0, seen[^1]);
		Assert.Equal(seen.Order(), seen);
	}

	[Fact]
	public async Task Ensuring_twice_downloads_once()
	{
		var body = EngineFixture.Bytes(50_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));

		var first = await store.EnsureAsync("ffmpeg", NoProgress, Never);
		var second = await store.EnsureAsync("ffmpeg", NoProgress, Never);

		Assert.Equal(first, second);
		Assert.Equal(1, _fixture.Requests("ffmpeg/ffmpeg.exe"));
	}

	[Fact]
	public async Task Two_callers_at_once_share_one_download()
	{
		var body = EngineFixture.Bytes(50_000);
		var release = new TaskCompletionSource();
		_fixture.ServeSlowly("ffmpeg/ffmpeg.exe", body, 10_000, release.Task);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));

		var first = store.EnsureAsync("ffmpeg", NoProgress, Never);
		var second = store.EnsureAsync("ffmpeg", NoProgress, Never);
		release.SetResult();

		Assert.Equal(await first, await second);
		Assert.Equal(1, _fixture.Requests("ffmpeg/ffmpeg.exe"));
	}

	[Fact]
	public async Task One_caller_cancelling_leaves_the_download_to_the_other()
	{
		var body = EngineFixture.Bytes(50_000);
		var release = new TaskCompletionSource();
		_fixture.ServeSlowly("ffmpeg/ffmpeg.exe", body, 10_000, release.Task);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		using var cancel = new CancellationTokenSource();

		var leaving = store.EnsureAsync("ffmpeg", NoProgress, cancel.Token);
		var staying = store.EnsureAsync("ffmpeg", NoProgress, Never);
		await cancel.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
		release.SetResult();

		Assert.True(File.Exists(await staying));
	}

	[Fact]
	public async Task A_truncated_download_raises_and_leaves_no_engine_folder()
	{
		var body = EngineFixture.Bytes(100_000);
		_fixture.ServeTruncated("ffmpeg/ffmpeg.exe", body, 40_000);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.EnsureAsync("ffmpeg", NoProgress, Never));

		Assert.Equal("ffmpeg", error.EngineId);
		Assert.Equal(_fixture.Url("ffmpeg/ffmpeg.exe"), error.Url);
		Assert.Empty(_fixture.Entries());
	}

	[Fact]
	public async Task A_wrong_checksum_raises_and_leaves_no_engine_folder_and_a_retry_succeeds()
	{
		var good = EngineFixture.Bytes(60_000);
		var tampered = EngineFixture.Bytes(60_000, seed: 9);
		_fixture.Serve("ffmpeg/ffmpeg.exe", tampered);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", good));

		var error = await Assert.ThrowsAsync<EngineChecksumException>(() => store.EnsureAsync("ffmpeg", NoProgress, Never));
		Assert.Contains("ffmpeg", error.Message);
		Assert.Empty(_fixture.Entries());

		_fixture.Serve("ffmpeg/ffmpeg.exe", good);
		var path = await store.EnsureAsync("ffmpeg", NoProgress, Never);
		Assert.Equal(good, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task A_response_bigger_than_the_pinned_size_is_refused_early()
	{
		var pinned = EngineFixture.Bytes(10_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", EngineFixture.Bytes(5_000_000));
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", pinned));

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.EnsureAsync("ffmpeg", NoProgress, Never));

		Assert.Contains("larger", error.Message);
		Assert.Empty(_fixture.Entries());
	}

	[Fact]
	public async Task A_refused_request_raises_with_the_status()
	{
		var body = EngineFixture.Bytes(1_000);
		_fixture.ServeStatus("ffmpeg/ffmpeg.exe", 404);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.EnsureAsync("ffmpeg", NoProgress, Never));

		Assert.Contains("404", error.Message);
		Assert.Empty(_fixture.Entries());
	}

	[Fact]
	public async Task The_error_names_the_engine_and_the_address_to_fetch_by_hand()
	{
		var body = EngineFixture.Bytes(1_000);
		_fixture.ServeStatus("ffmpeg/ffmpeg.exe", 500);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.EnsureAsync("ffmpeg", NoProgress, Never));

		Assert.Contains(_fixture.Url("ffmpeg/ffmpeg.exe").ToString(), error.Message);
		Assert.Contains("by hand", error.Message);
	}

	[Fact]
	public async Task A_cancelled_download_cleans_up_like_a_failed_one()
	{
		var body = EngineFixture.Bytes(100_000);
		var release = new TaskCompletionSource();
		_fixture.ServeSlowly("ffmpeg/ffmpeg.exe", body, 20_000, release.Task);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		using var cancel = new CancellationTokenSource();
		var seenSome = new TaskCompletionSource();
		var running = store.EnsureAsync("ffmpeg", new InlineProgress<double>(f => seenSome.TrySetResult()), cancel.Token);
		await seenSome.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		await cancel.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
		release.SetResult();
		Assert.Empty(_fixture.Entries());
	}

	[Fact]
	public async Task The_install_is_staged_in_a_temp_folder_and_moved_in_whole()
	{
		var body = EngineFixture.Bytes(100_000);
		var release = new TaskCompletionSource();
		_fixture.ServeSlowly("ffmpeg/ffmpeg.exe", body, 20_000, release.Task);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		var seenSome = new TaskCompletionSource();

		var running = store.EnsureAsync("ffmpeg", new InlineProgress<double>(_ => seenSome.TrySetResult()), Never);
		await seenSome.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		var during = _fixture.Entries();
		Assert.DoesNotContain("ffmpeg", during);
		Assert.Single(during);
		Assert.StartsWith(".tmp-ffmpeg-", during[0]);

		release.SetResult();
		await running;
		Assert.Equal(["ffmpeg"], _fixture.Entries());
	}

	[Fact]
	public async Task An_archive_without_the_executable_raises_the_named_error_and_leaves_nothing()
	{
		var zip = EngineFixture.Zip(("pandoc-1/COPYING", "licence"));
		_fixture.Serve("pandoc/pandoc.zip", zip);
		var store = _fixture.Store(_fixture.Archive("pandoc", zip, @"pandoc-1\pandoc.exe"));

		var error = await Assert.ThrowsAsync<EngineExecutableMissingException>(() => store.EnsureAsync("pandoc", NoProgress, Never));

		Assert.Contains(@"pandoc-1\pandoc.exe", error.Message);
		Assert.Empty(_fixture.Entries());
	}

	[Theory]
	[InlineData("../evil.txt")]
	[InlineData("pandoc-1/../../evil.txt")]
	[InlineData("/evil.txt")]
	[InlineData("C:/evil.txt")]
	[InlineData("pandoc-1/pandoc.exe:stream")]
	public async Task An_archive_entry_that_escapes_the_folder_is_refused_and_nothing_is_written(string entryName)
	{
		var zip = EngineFixture.Zip(("pandoc-1/pandoc.exe", "program"), (entryName, "escaped"));
		_fixture.Serve("pandoc/pandoc.zip", zip);
		var store = _fixture.Store(_fixture.Archive("pandoc", zip, @"pandoc-1\pandoc.exe"));

		var error = await Assert.ThrowsAsync<UnsafeArchiveException>(() => store.EnsureAsync("pandoc", NoProgress, Never));

		Assert.Contains(entryName, error.Message);
		Assert.Empty(_fixture.Entries());
		Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_fixture.Root)!, "evil.txt")));
		Assert.False(File.Exists(Path.Combine(_fixture.Root, "evil.txt")));
	}

	[Fact]
	public async Task An_engine_folder_the_user_emptied_is_downloaded_again()
	{
		var body = EngineFixture.Bytes(20_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		var path = await store.EnsureAsync("ffmpeg", NoProgress, Never);
		File.Delete(path);

		await store.EnsureAsync("ffmpeg", NoProgress, Never);

		Assert.True(File.Exists(path));
		Assert.Equal(2, _fixture.Requests("ffmpeg/ffmpeg.exe"));
	}

	[Fact]
	public async Task An_installed_engine_is_installed_for_the_next_launch()
	{
		var body = EngineFixture.Bytes(20_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var engine = _fixture.SingleFile("ffmpeg", body);
		await _fixture.Store(engine).EnsureAsync("ffmpeg", NoProgress, Never);

		var relaunched = _fixture.Store(engine);

		var status = Assert.Single(relaunched.List(), s => s.Engine.Id == "ffmpeg");
		Assert.Equal("1.0", status.InstalledVersion);
		Assert.True(status.InstalledBytes >= body.Length);
		Assert.Equal(1, _fixture.Requests("ffmpeg/ffmpeg.exe"));
		await relaunched.EnsureAsync("ffmpeg", NoProgress, Never);
		Assert.Equal(1, _fixture.Requests("ffmpeg/ffmpeg.exe"));
	}

	[Fact]
	public void Listing_an_engine_that_is_not_installed_reports_it_so_and_downloads_nothing()
	{
		var body = EngineFixture.Bytes(20_000);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));

		var status = Assert.Single(store.List());

		Assert.Null(status.InstalledVersion);
		Assert.Equal(0, status.InstalledBytes);
		Assert.Equal(0, _fixture.Requests("ffmpeg/ffmpeg.exe"));
	}

	[Fact]
	public void Leftover_temp_folders_go_when_the_store_opens()
	{
		Directory.CreateDirectory(Path.Combine(_fixture.Root, ".tmp-ffmpeg-abc"));
		File.WriteAllText(Path.Combine(_fixture.Root, ".tmp-ffmpeg-abc", "download"), "half");
		Directory.CreateDirectory(Path.Combine(_fixture.Root, ".old-pandoc-def"));
		Directory.CreateDirectory(Path.Combine(_fixture.Root, "ffmpeg"));

		_ = _fixture.Store();

		Assert.Equal(["ffmpeg"], _fixture.Entries());
	}

	[Fact]
	public async Task Remove_deletes_the_engine_and_a_later_ensure_downloads_it_again()
	{
		var body = EngineFixture.Bytes(20_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		await store.EnsureAsync("ffmpeg", NoProgress, Never);

		store.Remove("ffmpeg");

		Assert.Empty(_fixture.Entries());
		Assert.Null(store.List()[0].InstalledVersion);
		await store.EnsureAsync("ffmpeg", NoProgress, Never);
		Assert.Equal(2, _fixture.Requests("ffmpeg/ffmpeg.exe"));
	}

	[Fact]
	public void Removing_an_unknown_engine_raises()
	{
		var store = _fixture.Store();

		Assert.Throws<UnknownEngineException>(() => store.Remove("vlc"));
	}

	[Fact]
	public async Task Removing_an_engine_in_use_raises_and_leaves_it_whole()
	{
		var body = EngineFixture.Bytes(20_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		var path = await store.EnsureAsync("ffmpeg", NoProgress, Never);

		using (store.Use("ffmpeg"))
		{
			Assert.Throws<EngineInUseException>(() => store.Remove("ffmpeg"));
			Assert.True(File.Exists(path));
		}

		store.Remove("ffmpeg");
		Assert.False(File.Exists(path));
	}

	[Fact]
	public async Task Removing_an_engine_whose_program_is_running_raises_and_leaves_it_whole()
	{
		var body = EngineFixture.Bytes(20_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		var path = await store.EnsureAsync("ffmpeg", NoProgress, Never);
		await using var holding = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

		Assert.Throws<EngineInUseException>(() => store.Remove("ffmpeg"));

		Assert.Equal(["ffmpeg"], _fixture.Entries());
		Assert.Equal(body.Length, holding.Length);
	}

	[Fact]
	public async Task Removing_an_engine_while_it_downloads_raises()
	{
		var body = EngineFixture.Bytes(50_000);
		var release = new TaskCompletionSource();
		_fixture.ServeSlowly("ffmpeg/ffmpeg.exe", body, 10_000, release.Task);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		var running = store.EnsureAsync("ffmpeg", NoProgress, Never);

		Assert.Throws<EngineInUseException>(() => store.Remove("ffmpeg"));

		release.SetResult();
		await running;
	}

	[Fact]
	public async Task The_fake_engine_installed_as_an_engine_runs_and_echoes_its_arguments()
	{
		var exe = Path.Combine(AppContext.BaseDirectory, "Zing.FakeEngine.exe");
		var body = await File.ReadAllBytesAsync(exe, TestContext.Current.CancellationToken);
		_fixture.Serve("fake/fake.exe", body);
		var engine = _fixture.SingleFile("fake", body) with { Executable = "fake.exe" };
		// The apphost needs its dll and runtime config beside it.
		var store = _fixture.Store(engine);
		var path = await store.EnsureAsync("fake", NoProgress, Never);
		foreach (var sibling in new[] { "Zing.FakeEngine.dll", "Zing.FakeEngine.runtimeconfig.json" })
		{
			File.Copy(Path.Combine(AppContext.BaseDirectory, sibling), Path.Combine(Path.GetDirectoryName(path)!, sibling));
		}
		var output = Path.Combine(_fixture.Root, "out.txt");

		var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, UseShellExecute = false };
		start.ArgumentList.Add("-rf.mov");
		start.ArgumentList.Add("my film é (1).heic");
		start.ArgumentList.Add("--fake-write");
		start.ArgumentList.Add(output);
		using var process = Process.Start(start)!;
		var echoed = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, process.ExitCode);
		Assert.Contains("\"-rf.mov\"", echoed);
		Assert.Contains("my film", echoed);
		Assert.Equal("fake engine output\n", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task A_bumped_pin_reinstalls_an_engine_installed_under_the_old_pin()
	{
		var v1 = EngineFixture.Bytes(20_000, seed: 1);
		var v2 = EngineFixture.Bytes(21_000, seed: 2);
		_fixture.Serve("ffmpeg/ffmpeg.exe", v1);
		var path = await _fixture.Store(_fixture.SingleFile("ffmpeg", v1, "1.0")).EnsureAsync("ffmpeg", NoProgress, Never);
		_fixture.Serve("ffmpeg/ffmpeg.exe", v2);

		var bumped = _fixture.Store(_fixture.SingleFile("ffmpeg", v2, "2.0"));

		Assert.Null(bumped.List()[0].InstalledVersion);
		await bumped.EnsureAsync("ffmpeg", NoProgress, Never);
		Assert.Equal("2.0", bumped.List()[0].InstalledVersion);
		Assert.Equal(v2, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
		Assert.Equal(["ffmpeg"], _fixture.Entries());
	}

	[Fact]
	public async Task A_pin_with_the_same_version_but_another_hash_also_reinstalls()
	{
		var first = EngineFixture.Bytes(20_000, seed: 1);
		var second = EngineFixture.Bytes(20_000, seed: 2);
		_fixture.Serve("ffmpeg/ffmpeg.exe", first);
		await _fixture.Store(_fixture.SingleFile("ffmpeg", first)).EnsureAsync("ffmpeg", NoProgress, Never);
		_fixture.Serve("ffmpeg/ffmpeg.exe", second);

		var path = await _fixture.Store(_fixture.SingleFile("ffmpeg", second)).EnsureAsync("ffmpeg", NoProgress, Never);

		Assert.Equal(second, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task A_caller_arriving_while_the_last_other_caller_is_stopping_the_download_gets_a_fresh_one()
	{
		var body = EngineFixture.Bytes(50_000);
		var release = new TaskCompletionSource();
		_fixture.ServeSlowly("ffmpeg/ffmpeg.exe", body, 10_000, release.Task);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		using var cancel = new CancellationTokenSource();
		var leaving = store.EnsureAsync("ffmpeg", NoProgress, cancel.Token);
		await WaitForRequests("ffmpeg/ffmpeg.exe", 1);

		var cancelling = cancel.CancelAsync();
		var arriving = store.EnsureAsync("ffmpeg", NoProgress, Never);
		await cancelling;
		release.SetResult();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
		Assert.True(File.Exists(await arriving));
		Assert.Equal(["ffmpeg"], _fixture.Entries());
	}

	private async Task WaitForRequests(string path, int count)
	{
		for (var i = 0; i < 200 && _fixture.Requests(path) < count; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
		Assert.True(_fixture.Requests(path) >= count);
	}

	[Fact]
	public async Task A_download_swapped_after_it_was_read_is_caught_before_it_is_unpacked()
	{
		var body = EngineFixture.Bytes(20_000);
		_fixture.Serve("ffmpeg/ffmpeg.exe", body);
		var store = _fixture.Store(_fixture.SingleFile("ffmpeg", body));
		store.AfterDownload = path => File.WriteAllBytes(path, EngineFixture.Bytes(20_000, seed: 99));

		var error = await Assert.ThrowsAsync<EngineChecksumException>(() => store.EnsureAsync("ffmpeg", NoProgress, Never));

		Assert.Contains("ffmpeg", error.Message);
		Assert.Empty(_fixture.Entries());
	}

	[Fact]
	public async Task An_archive_listing_a_file_twice_raises_a_named_error()
	{
		var zip = EngineFixture.Zip(("pandoc-1/pandoc.exe", "program"), ("pandoc-1/PANDOC.exe", "again"));
		_fixture.Serve("pandoc/pandoc.zip", zip);
		var store = _fixture.Store(_fixture.Archive("pandoc", zip, @"pandoc-1\pandoc.exe"));

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.EnsureAsync("pandoc", NoProgress, Never));

		Assert.Contains("more than once", error.Message);
		Assert.Empty(_fixture.Entries());
	}
}
