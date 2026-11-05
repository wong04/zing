using System.Text;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed class YtDlpUpdateTests : IDisposable
{
	private const string Old = "2026.10.01";
	private const string New = "2026.11.01";

	private readonly EngineFixture _fixture = new();
	private readonly byte[] _oldExe = EngineFixture.Bytes(40_000, seed: 1);
	private readonly byte[] _newExe = EngineFixture.Bytes(41_000, seed: 2);

	public void Dispose() => _fixture.Dispose();

	private async Task<EngineStore> InstalledOldAsync()
	{
		_fixture.Serve($"yt-dlp/{Old}/yt-dlp.exe", _oldExe);
		var old = new Engine("ytdlp", Old, _fixture.Url($"yt-dlp/{Old}/yt-dlp.exe"), EngineFixture.Sha256(_oldExe), _oldExe.Length, "yt-dlp.exe", "yt-dlp", "Unlicense");
		var store = _fixture.Store(old);
		await store.EnsureAsync("ytdlp", new InlineProgress<double>(_ => { }), CancellationToken.None);
		return store;
	}

	/// <summary>The release page's checksum file, reached through "latest", which redirects to the release's own address.</summary>
	private void PublishRelease(string sums)
	{
		// As on GitHub: "latest" redirects to the tagged address, which redirects again to a storage host whose path has no tag.
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", $"yt-dlp/{New}/SHA2-256SUMS");
		_fixture.ServeRedirect($"yt-dlp/{New}/SHA2-256SUMS", "assets/9f3c/blob");
		_fixture.Serve("assets/9f3c/blob", Encoding.UTF8.GetBytes(sums));
		_fixture.Serve($"yt-dlp/{New}/yt-dlp.exe", _newExe);
	}

	private static string Sums(string exeHash) => $"{new string('a', 64)}  yt-dlp\n{exeHash}  yt-dlp.exe\n{new string('b', 64)}  yt-dlp_x86.exe\n";

	private static string InstalledVersion(EngineStore store) => store.List().Single(s => s.Engine.Id == "ytdlp").InstalledVersion!;

	private string ExePath => Path.Combine(_fixture.Root, "ytdlp", "yt-dlp.exe");

	[Fact]
	public async Task A_release_whose_checksum_matches_replaces_the_old_version()
	{
		var store = await InstalledOldAsync();
		PublishRelease(Sums(EngineFixture.Sha256(_newExe)));

		var update = await store.UpdateYtDlpAsync(CancellationToken.None);

		Assert.Equal(New, update.Version);
		Assert.True(update.Changed);
		Assert.Equal(New, InstalledVersion(store));
		Assert.Equal(_newExe, await File.ReadAllBytesAsync(ExePath, TestContext.Current.CancellationToken));
		Assert.Equal(["ytdlp"], _fixture.Entries());
	}

	[Fact]
	public async Task A_mismatch_with_the_published_checksum_keeps_the_old_version()
	{
		var store = await InstalledOldAsync();
		PublishRelease(Sums(EngineFixture.Sha256(EngineFixture.Bytes(10, seed: 99))));

		var error = await Assert.ThrowsAsync<EngineChecksumException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Equal("ytdlp", error.EngineId);
		Assert.Equal(Old, InstalledVersion(store));
		Assert.Equal(_oldExe, await File.ReadAllBytesAsync(ExePath, TestContext.Current.CancellationToken));
		Assert.Equal(["ytdlp"], _fixture.Entries());
	}

	[Fact]
	public async Task A_release_already_installed_changes_nothing_and_fetches_no_program()
	{
		var store = await InstalledOldAsync();
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", $"yt-dlp/{Old}/SHA2-256SUMS");
		_fixture.ServeRedirect($"yt-dlp/{Old}/SHA2-256SUMS", "assets/old/blob");
		_fixture.Serve("assets/old/blob", Encoding.UTF8.GetBytes(Sums(EngineFixture.Sha256(_oldExe))));

		var update = await store.UpdateYtDlpAsync(CancellationToken.None);

		Assert.False(update.Changed);
		Assert.Equal(Old, update.Version);
		Assert.Equal(1, _fixture.Requests($"yt-dlp/{Old}/yt-dlp.exe"));
	}

	[Fact]
	public async Task A_checksum_file_without_the_program_raises_and_keeps_the_old_version()
	{
		var store = await InstalledOldAsync();
		PublishRelease($"{new string('a', 64)}  yt-dlp\n");

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Contains("yt-dlp.exe", error.Message);
		Assert.Equal(Old, InstalledVersion(store));
		Assert.Equal(0, _fixture.Requests($"yt-dlp/{New}/yt-dlp.exe"));
	}

	[Fact]
	public async Task A_truncated_new_program_keeps_the_old_version()
	{
		var store = await InstalledOldAsync();
		PublishRelease(Sums(EngineFixture.Sha256(_newExe)));
		_fixture.ServeTruncated($"yt-dlp/{New}/yt-dlp.exe", _newExe, 10_000);

		await Assert.ThrowsAsync<EngineDownloadException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Equal(Old, InstalledVersion(store));
		Assert.Equal(["ytdlp"], _fixture.Entries());
	}

	[Fact]
	public async Task A_new_program_far_bigger_than_the_old_one_is_refused()
	{
		var store = await InstalledOldAsync();
		var huge = EngineFixture.Bytes(_oldExe.Length * 5);
		PublishRelease(Sums(EngineFixture.Sha256(huge)));
		_fixture.Serve($"yt-dlp/{New}/yt-dlp.exe", huge);

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Contains("larger", error.Message);
		Assert.Equal(Old, InstalledVersion(store));
	}

	[Fact]
	public async Task An_update_while_the_program_runs_raises_in_use_and_keeps_the_old_version()
	{
		var store = await InstalledOldAsync();
		PublishRelease(Sums(EngineFixture.Sha256(_newExe)));
		await using var running = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);

		await Assert.ThrowsAsync<EngineInUseException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Equal(Old, InstalledVersion(store));
		Assert.Equal(["ytdlp"], _fixture.Entries());
	}

	[Fact]
	public async Task Updating_an_engine_that_was_never_downloaded_raises()
	{
		var store = _fixture.Store(new Engine("ytdlp", Old, _fixture.Url("x"), new string('0', 64), 1, "yt-dlp.exe", "yt-dlp", "Unlicense"));
		PublishRelease(Sums(EngineFixture.Sha256(_newExe)));

		await Assert.ThrowsAsync<EngineNotInstalledException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Equal(0, _fixture.Requests($"yt-dlp/{New}/yt-dlp.exe"));
	}

	[Fact]
	public async Task The_program_is_fetched_from_the_tagged_release_not_from_where_the_redirects_ended()
	{
		var store = await InstalledOldAsync();
		PublishRelease(Sums(EngineFixture.Sha256(_newExe)));

		await store.UpdateYtDlpAsync(CancellationToken.None);

		Assert.Equal(1, _fixture.Requests($"yt-dlp/{New}/yt-dlp.exe"));
		Assert.Equal(0, _fixture.Requests("assets/9f3c/yt-dlp.exe"));
	}

	[Theory]
	[InlineData("yt-dlp/latest-but-odd/SHA2-256SUMS")]
	[InlineData("yt-dlp/v1/SHA2-256SUMS")]
	[InlineData("yt-dlp/2026.11/SHA2-256SUMS")]
	[InlineData("assets/9f3c/SHA2-256SUMS")]
	[InlineData("yt-dlp/2026.11.01/extra/SHA2-256SUMS")]
	public async Task A_redirect_that_does_not_name_a_dated_release_raises_and_keeps_the_old_version(string location)
	{
		var store = await InstalledOldAsync();
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", location);

		var error = await Assert.ThrowsAsync<EngineDownloadException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Contains("version", error.Message);
		Assert.Equal(Old, InstalledVersion(store));
	}

	[Fact]
	public async Task A_release_page_that_does_not_redirect_raises()
	{
		var store = await InstalledOldAsync();
		_fixture.Serve("yt-dlp/latest/SHA2-256SUMS", Encoding.UTF8.GetBytes(Sums(EngineFixture.Sha256(_newExe))));

		await Assert.ThrowsAsync<EngineDownloadException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Equal(Old, InstalledVersion(store));
	}

	[Fact]
	public async Task A_redirect_to_another_host_raises()
	{
		var store = await InstalledOldAsync();
		_fixture.Route302("yt-dlp/latest/SHA2-256SUMS", $"http://localhost:{_fixture.Port}/yt-dlp/{New}/SHA2-256SUMS");

		await Assert.ThrowsAsync<EngineDownloadException>(() => store.UpdateYtDlpAsync(CancellationToken.None));

		Assert.Equal(Old, InstalledVersion(store));
	}

	[Fact]
	public async Task An_older_latest_release_is_not_installed_over_a_newer_one()
	{
		var store = await InstalledOldAsync();
		const string older = "2026.09.01";
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", $"yt-dlp/{older}/SHA2-256SUMS");
		_fixture.Serve($"yt-dlp/{older}/SHA2-256SUMS", Encoding.UTF8.GetBytes(Sums(EngineFixture.Sha256(_newExe))));
		_fixture.Serve($"yt-dlp/{older}/yt-dlp.exe", _newExe);

		var update = await store.UpdateYtDlpAsync(CancellationToken.None);

		Assert.False(update.Changed);
		Assert.Equal(Old, update.Version);
		Assert.Equal(0, _fixture.Requests($"yt-dlp/{older}/yt-dlp.exe"));
	}

	[Fact]
	public async Task Versions_compare_as_numbers_so_a_ninth_is_older_than_a_tenth()
	{
		var store = await InstalledOldAsync();
		const string tenth = "2026.10.01.10";
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", $"yt-dlp/{tenth}/SHA2-256SUMS");
		_fixture.Serve($"yt-dlp/{tenth}/SHA2-256SUMS", Encoding.UTF8.GetBytes(Sums(EngineFixture.Sha256(_newExe))));
		_fixture.Serve($"yt-dlp/{tenth}/yt-dlp.exe", _newExe);
		await store.UpdateYtDlpAsync(CancellationToken.None);
		const string ninth = "2026.10.01.9";
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", $"yt-dlp/{ninth}/SHA2-256SUMS");

		var update = await store.UpdateYtDlpAsync(CancellationToken.None);

		Assert.False(update.Changed);
		Assert.Equal(tenth, InstalledVersion(store));
	}

	[Fact]
	public async Task An_updated_yt_dlp_stays_while_it_is_newer_than_the_pin_and_the_pin_wins_once_it_overtakes()
	{
		var store = await InstalledOldAsync();
		PublishRelease(Sums(EngineFixture.Sha256(_newExe)));
		await store.UpdateYtDlpAsync(CancellationToken.None);
		var samePin = new Engine("ytdlp", Old, _fixture.Url($"yt-dlp/{Old}/yt-dlp.exe"), EngineFixture.Sha256(_oldExe), _oldExe.Length, "yt-dlp.exe", "yt-dlp", "Unlicense");

		var relaunched = _fixture.Store(samePin);
		await relaunched.EnsureAsync("ytdlp", new InlineProgress<double>(_ => { }), CancellationToken.None);
		Assert.Equal(New, InstalledVersion(relaunched));
		Assert.Equal(1, _fixture.Requests($"yt-dlp/{Old}/yt-dlp.exe"));

		const string pinned = "2026.12.01";
		var fresh = EngineFixture.Bytes(42_000, seed: 5);
		_fixture.Serve($"yt-dlp/{pinned}/yt-dlp.exe", fresh);
		var overtaken = new Engine("ytdlp", pinned, _fixture.Url($"yt-dlp/{pinned}/yt-dlp.exe"), EngineFixture.Sha256(fresh), fresh.Length, "yt-dlp.exe", "yt-dlp", "Unlicense");
		var later = _fixture.Store(overtaken);
		Assert.Null(later.List()[0].InstalledVersion);
		await later.EnsureAsync("ytdlp", new InlineProgress<double>(_ => { }), CancellationToken.None);
		Assert.Equal(pinned, InstalledVersion(later));
		Assert.Equal(fresh, await File.ReadAllBytesAsync(ExePath, TestContext.Current.CancellationToken));
	}
}
