using System.Xml.Linq;
using Zing.Core.Engines;

namespace Zing.Core.Tests.Engines;

public sealed class EngineFlagPolicyTests
{
	private static JobArg[] Args(params string[] tokens) =>
		[.. tokens.Select(t => t switch
		{
			"@in" => (JobArg)new JobArg.Input(Path.Combine(Path.GetTempPath(), "in.mov")),
			"@out" => new JobArg.Output(0),
			_ => new JobArg.Literal(t),
		})];

	public static TheoryData<string, string[]> Bypasses => new()
	{
		// The ways to reach a file, the network or a program that the old denylist let through.
		{ "ffmpeg", ["-i", "concat:C:/x|C:/y"] },
		{ "ffmpeg", ["-i", "subfile,,start,0,end,9,,:C:/x"] },
		{ "ffmpeg", ["movie=C:/x"] },
		{ "ffmpeg", ["-f", "tee", "[f=mp4]C:/anywhere/out.mp4"] },
		{ "ffmpeg", ["-i", "http://example.com/a.mp4"] },
		{ "ffmpeg", ["-f", "lavfi"] },
		{ "ffmpeg", ["-f", "concat"] },
		{ "ffmpeg", ["-f", "image2", "-i", "@in"] },
		{ "ffmpeg", ["-f", "segment"] },
		{ "ffmpeg", ["-f", "hls"] },
		{ "ffmpeg", ["-filter_complex", "movie=C:/x"] },
		{ "ffmpeg", ["-vf", "scale=iw/2:ih/2"] },
		{ "ffmpeg", ["-vf", "movie=C:/x"] },
		{ "ffmpeg", ["-preset", "veryslow"] },
		{ "ffmpeg", ["-pix_fmt", "C:/x"] },
		{ "ffmpeg", ["-b:v", "10M"] },
		{ "ffmpeg", ["-crf", "movie=C:/x"] },
		{ "ffmpeg", ["-f", "mov", "-i", "C:/anywhere.mov"] },
		{ "magick", ["-delete", "0"] },
		{ "magick", ["-delete", "C:/x"] },
		{ "ffmpeg", ["-attach", "@in"] },
		{ "ffmpeg", ["-dump_attachment:t", "x"] },
		{ "ffmpeg", ["-report"] },
		{ "ffmpeg", ["-c:v", "concat:x"] },
		{ "ffmpeg", ["-c:v", "libx264:C"] },
		{ "ffmpeg", ["-c:v", "-an"] },
		{ "ffmpeg", ["-c:v", "a/b"] },
		{ "ffmpeg", ["-c:v", "C:\\x"] },
		{ "ffmpeg", ["-c:v", "@in-list"] },
		{ "ffmpeg", ["-ss", "00:00:10"] },
		{ "ffmpeg", ["-t", "10; calc"] },
		{ "ffmpeg", ["-i"] },
		{ "ffmpeg", ["-i", "@out"] },
		// A renamed concat or playlist file is read by FFmpeg unless the format is named first (checked with the real engine).
		{ "ffmpeg", ["-i", "@in", "@out"] },
		{ "ffmpeg", ["@in", "@out"] },
		{ "ffmpeg", ["-c:v", "libx264", "-i", "@in", "@out"] },
		{ "ffmpeg", ["-f", "mp4", "-c:v", "libx264", "-i", "@in", "@out"] },
		{ "ffmpeg", ["-f", "mp4", "-i", "@in", "-i", "@in", "@out"] },
		{ "ffmpeg", ["-f", "mp4", "-i", "@in", "@in"] },
		{ "ffmpeg", ["-f", "mp4", "-i", "@in", "-loop", "1", "@out"] },
		{ "ffmpeg", ["-f", "mp4", "-i", "@in", "-profile:v", "1", "@out"] },
		// ImageMagick overwrites silently: its last argument must be an output, so no input can be the destination.
		{ "magick", ["@in", "@in"] },
		{ "magick", ["@in", "-auto-orient"] },
		{ "magick", [] },
		{ "magick", ["@in", "@out", "@in"] },
		{ "ffmpeg", ["-protocol_whitelist", "file,http,tcp"] },
		{ "ffmpeg", ["--exec", "calc"] },
		{ "magick", ["label:@C:/x"] },
		{ "magick", ["msl:"] },
		{ "magick", ["ephemeral:"] },
		{ "magick", ["@C:/list.txt"] },
		{ "magick", ["-write", "C:/x"] },
		{ "magick", ["-quality", "label:x"] },
		{ "pandoc", ["--lua-filter", "x.lua"] },
		{ "pandoc", ["--filter", "x"] },
		{ "pandoc", ["--extract-media", "x"] },
		{ "pandoc", ["--resource-path", "x"] },
		{ "pandoc", ["--data-dir", "x"] },
		{ "pandoc", ["--include-in-header", "x"] },
		{ "pandoc", ["--template", "x"] },
		{ "pandoc", ["-o", "C:/anywhere.html"] },
		{ "pandoc", ["-f", "markdown+raw_html+lua"] },
		{ "ytdlp", ["--exec", "calc"] },
		{ "ytdlp", ["--plugin-dirs", "x"] },
		{ "ytdlp", ["--config-locations", "x"] },
		{ "ytdlp", ["--cookies", "x"] },
		{ "ytdlp", ["--cookies-from-browser", "chrome"] },
		{ "ytdlp", ["--batch-file", "x"] },
		{ "ytdlp", ["--load-info-json", "x"] },
		{ "ytdlp", ["-o", "C:/x"] },
		{ "ytdlp", ["--audio-format", "mp3;calc"] },
		{ "bgmodel", ["--anything"] },
	};

	[Theory]
	[MemberData(nameof(Bypasses))]
	public void A_flag_or_value_outside_the_allow_list_is_refused_by_name(string engine, string[] tokens)
	{
		Assert.ThrowsAny<UnsafeEngineArgumentException>(() => EngineFlagPolicy.Pinned.For(engine).Check(Args(tokens)));
	}

	public static TheoryData<string, string[]> Allowed => new()
	{
		{ "ffmpeg", ["-f", "mov", "-i", "@in", "-c:v", "libx264", "-c:a", "aac", "-b:a", "192k", "-an", "-vn", "-ss", "1.5", "-t", "10", "-f", "mp4", "@out"] },
		{ "ffmpeg", ["-f", "mp3", "-i", "@in", "-q:a", "2", "@out"] },
		{ "pandoc", ["-f", "markdown", "-t", "html", "--standalone", "-o", "@out", "@in"] },
		{ "magick", ["@in", "-quality", "85", "-strip", "-auto-orient", "@out"] },
		// What Convert sends (src/shared/convert/commands.ts): the input first, the output last.
		{ "magick", ["@in", "-delete", "1--1", "-auto-orient", "-quality", "85", "@out"] },
		{ "magick", ["@in", "-auto-orient", "@out"] },
		{ "ffmpeg", ["-f", "mov", "-i", "@in", "-c:v", "libx264", "-crf", "11", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "@out"] },
		{ "ffmpeg", ["-f", "mp4", "-i", "@in", "-c:v", "libvpx-vp9", "-crf", "12", "-b:v", "0", "-pix_fmt", "yuv420p", "-c:a", "libopus", "@out"] },
		{ "ffmpeg", ["-f", "mov", "-i", "@in", "-c:v", "prores_ks", "-profile:v", "3", "-c:a", "pcm_s16le", "@out"] },
		{ "ffmpeg", ["-f", "mpegts", "-i", "@in", "-vf", EngineFlagPolicy.GifFilter, "-loop", "0", "@out"] },
		{ "ffmpeg", ["-f", "avi", "-i", "@in", "-vn", "-c:a", "libmp3lame", "-q:a", "2", "@out"] },
		{ "ffmpeg", ["-f", "mp3", "-i", "@in", "-vn", "-c:a", "libopus", "-b:a", "160k", "@out"] },
		{ "pandoc", ["-f", "markdown", "@in", "-t", "docx", "--standalone", "-o", "@out"] },
		{ "pandoc", ["-f", "html", "@in", "-t", "plain", "-o", "@out"] },
		{ "ytdlp", ["--newline", "--no-playlist", "-x", "--audio-format", "mp3", "--audio-quality", "0"] },
		{ "ytdlp", ["--newline", "--no-playlist", "-x", "--audio-format", "mp3", "--audio-quality", "0", "-o", "@out"] },
		{ "ytdlp", ["--newline", "--no-playlist", "-f", "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b", "--merge-output-format", "mp4", "-o", "@out"] },
	};

	[Theory]
	[MemberData(nameof(Allowed))]
	public void The_flags_the_tools_need_pass(string engine, string[] tokens)
	{
		EngineFlagPolicy.Pinned.For(engine).Check(Args(tokens));
	}

	[Fact]
	public void The_refusal_names_the_flag_and_the_engine()
	{
		var error = Assert.Throws<DisallowedEngineFlagException>(() => EngineFlagPolicy.Pinned.For("pandoc").Check(Args("--lua-filter", "x.lua")));

		Assert.Contains("--lua-filter", error.Message);
		Assert.Contains("Pandoc", error.Message);
	}

	[Fact]
	public void A_scheme_like_value_passes_only_where_the_flag_says_so()
	{
		var policy = EngineFlagPolicy.Pinned.Plus("ffmpeg", new Dictionary<string, FlagSpec>
		{
			["--strict"] = new FlagSpec(FlagValue.Word),
			["--scheme"] = new FlagSpec(FlagValue.Word) { AllowsScheme = true },
		});

		Assert.ThrowsAny<UnsafeEngineArgumentException>(() => policy.For("ffmpeg").Check(Args("--strict", "label:x")));
		policy.For("ffmpeg").Check(Args("--scheme", "label:x"));
		policy.For("ffmpeg").Check(Args("--strict", "0:v:0"));
	}

	[Fact]
	public void Option_looking_files_are_fine_because_files_are_never_literals()
	{
		EngineFlagPolicy.Pinned.For("ffmpeg").Check([new JobArg.Literal("-f"), new JobArg.Literal("mp4"), new JobArg.Literal("-i"), new JobArg.Input(Path.GetFullPath("-rf.mov")), new JobArg.Output(0)]);
	}

	[Fact]
	public void FFmpeg_gets_no_stdin_and_a_protocol_whitelist_before_every_input()
	{
		var policy = EngineFlagPolicy.Pinned.For("ffmpeg");

		Assert.Equal(["-nostdin"], policy.SafetyFirst);
		Assert.Equal(["-protocol_whitelist", "file,pipe"], policy.SafetyBefore["-i"]);
	}

	[Fact]
	public void The_other_engines_get_their_sandbox_flags_first()
	{
		Assert.Equal(["--sandbox"], EngineFlagPolicy.Pinned.For("pandoc").SafetyFirst);
		Assert.Equal(["--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist"], EngineFlagPolicy.Pinned.For("ytdlp").SafetyFirst);
	}

	[Fact]
	public void Only_yt_dlp_is_given_FFmpegs_location_by_the_host()
	{
		Assert.True(EngineFlagPolicy.Pinned.For("ytdlp").UsesFfmpeg);
		Assert.All(new[] { "ffmpeg", "magick", "pandoc", "bgmodel" }, id => Assert.False(EngineFlagPolicy.Pinned.For(id).UsesFfmpeg));
		Assert.DoesNotContain("--ffmpeg-location", EngineFlagPolicy.Pinned.For("ytdlp").Flags.Keys);
	}

	[Fact]
	public void Only_yt_dlp_has_the_network()
	{
		Assert.True(EngineFlagPolicy.Pinned.For("ytdlp").Network);
		Assert.All(new[] { "ffmpeg", "magick", "pandoc", "bgmodel" }, id => Assert.False(EngineFlagPolicy.Pinned.For(id).Network));
	}

	[Fact]
	public void The_ImageMagick_policy_blocks_the_dangerous_coders_and_at_paths()
	{
		var document = XDocument.Parse(MagickPolicy.Xml);
		var policies = document.Root!.Elements("policy").ToList();

		var coders = policies.Single(p => (string?)p.Attribute("domain") == "coder" && (string?)p.Attribute("rights") == "none").Attribute("pattern")!.Value;
		foreach (var coder in new[] { "MSL", "MVG", "EPHEMERAL", "URL", "HTTP", "HTTPS", "FTP", "TEXT", "LABEL", "CAPTION", "PS", "EPS", "PDF", "XPS" }) Assert.Contains(coder, coders.Trim('{', '}').Split(','));
		Assert.Contains(policies, p => (string?)p.Attribute("domain") == "path" && (string?)p.Attribute("pattern") == "@*" && (string?)p.Attribute("rights") == "none");
		Assert.Contains(policies, p => (string?)p.Attribute("domain") == "delegate" && (string?)p.Attribute("rights") == "none");
	}
}
