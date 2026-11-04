using System.Text.RegularExpressions;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed partial class EngineCatalogTests
{
	[GeneratedRegex("^[0-9a-f]{64}$")]
	private static partial Regex Sha256Hex();

	[Fact]
	public void The_pinned_catalog_has_the_five_engines()
	{
		Assert.Equal(["bgmodel", "ffmpeg", "magick", "pandoc", "ytdlp"], EngineCatalog.Pinned.Engines.Select(e => e.Id).Order());
	}

	[Fact]
	public void Every_pin_is_a_fixed_https_release_with_a_hash_and_a_size()
	{
		foreach (var engine in EngineCatalog.Pinned.Engines)
		{
			Assert.Equal("https", engine.Url.Scheme);
			Assert.Matches(Sha256Hex(), engine.Sha256);
			Assert.True(engine.Bytes > 1_000_000, engine.Id);
			Assert.False(string.Equals("latest", engine.Version, StringComparison.OrdinalIgnoreCase));
			Assert.DoesNotContain("latest", engine.Url.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
			Assert.False(string.IsNullOrWhiteSpace(engine.Licence), engine.Id);
		}
	}

	[Fact]
	public void Each_pin_comes_from_its_projects_own_release_page()
	{
		var paths = EngineCatalog.Pinned.Engines.ToDictionary(e => e.Id, e => e.Url);

		Assert.All(paths.Values, url => Assert.Equal("github.com", url.Host));
		Assert.StartsWith("/GyanD/codexffmpeg/releases/download/9.0.2/", paths["ffmpeg"].AbsolutePath);
		Assert.StartsWith("/ImageMagick/ImageMagick/releases/download/", paths["magick"].AbsolutePath);
		Assert.StartsWith("/jgm/pandoc/releases/download/", paths["pandoc"].AbsolutePath);
		Assert.StartsWith("/yt-dlp/yt-dlp/releases/download/", paths["ytdlp"].AbsolutePath);
		Assert.StartsWith("/yt-dlp/yt-dlp/releases/latest/download/", EngineCatalog.Pinned.YtDlpLatest.AbsolutePath);
	}

	[Fact]
	public void A_single_file_pin_is_the_program_and_no_pin_can_name_a_path_outside_its_folder()
	{
		foreach (var engine in EngineCatalog.Pinned.Engines)
		{
			var file = Path.GetFileName(engine.Url.AbsolutePath);
			var archive = file.EndsWith(".zip", StringComparison.Ordinal) || file.EndsWith(".7z", StringComparison.Ordinal);
			if (!archive) Assert.Equal(file, engine.Executable);
			Assert.DoesNotContain("..", engine.Executable);
			Assert.False(Path.IsPathRooted(engine.Executable), engine.Id);
		}
	}

	[Fact]
	public void The_ffmpeg_and_background_model_entries_state_their_licences()
	{
		Assert.Contains("GPL", EngineCatalog.Pinned.Get("ffmpeg").Licence);
		Assert.Contains("Apache", EngineCatalog.Pinned.Get("bgmodel").Licence);
	}

	[Fact]
	public void An_unknown_id_raises()
	{
		Assert.Throws<UnknownEngineException>(() => EngineCatalog.Pinned.Get("vlc"));
	}

	[Fact]
	public void A_fixture_manifest_points_the_catalog_at_a_local_server()
	{
		var manifest = """
			[{"id":"ffmpeg","version":"9.9","file":"ffmpeg.exe","sha256":"abc","bytes":12,"executable":"ffmpeg.exe"}]
			""";

		var catalog = EngineCatalog.FromFixture(new Uri("http://127.0.0.1:5555/"), manifest);

		var engine = Assert.Single(catalog.Engines);
		Assert.Equal(new Uri("http://127.0.0.1:5555/ffmpeg.exe"), engine.Url);
		Assert.Equal(12, engine.Bytes);
		Assert.Equal(new Uri("http://127.0.0.1:5555/yt-dlp/latest/SHA2-256SUMS"), catalog.YtDlpLatest);
	}

	[Fact]
	public void A_fixture_refuses_a_host_that_is_not_this_pc()
	{
		Assert.Throws<ArgumentException>(() => EngineCatalog.FromFixture(new Uri("https://example.com/"), "[]"));
	}
}
