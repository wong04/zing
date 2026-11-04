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
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", $"yt-dlp/{New}/SHA2-256SUMS");
		_fixture.Serve($"yt-dlp/{New}/SHA2-256SUMS", Encoding.UTF8.GetBytes(sums));
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
		_fixture.Serve($"yt-dlp/{Old}/SHA2-256SUMS", Encoding.UTF8.GetBytes(Sums(EngineFixture.Sha256(_oldExe))));

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
		_fixture.ServeRedirect("yt-dlp/latest/SHA2-256SUMS", $"yt-dlp/{New}/SHA2-256SUMS");
		_fixture.Serve($"yt-dlp/{New}/SHA2-256SUMS", Encoding.UTF8.GetBytes(Sums(EngineFixture.Sha256(huge))));
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
}
