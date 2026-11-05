using Zing.Core.Engines;
using Zing.Core.Viewer;

namespace Zing.Core.Tests.Viewer;

public sealed class ViewerConversionTests
{
	private static readonly string Input = Path.Combine(Path.GetTempPath(), "holiday.mov");

	private static string Extension(ViewerKind kind) => kind == ViewerKind.Audio ? ".wma" : ".mov";

	public static TheoryData<ViewerKind> Converted => new() { ViewerKind.Image, ViewerKind.Video, ViewerKind.Audio };

	[Theory]
	[MemberData(nameof(Converted))]
	public void Every_option_is_one_the_engines_allow_list_accepts(ViewerKind kind)
	{
		var policy = EngineFlagPolicy.Pinned.For(ViewerConversion.EngineFor(kind));
		policy.Check(ViewerConversion.Args(kind, Input, Extension(kind)));
	}

	[Theory]
	[MemberData(nameof(Converted))]
	public void The_only_files_are_the_picked_input_and_the_one_output_and_the_output_is_last(ViewerKind kind)
	{
		var args = ViewerConversion.Args(kind, Input, Extension(kind));
		Assert.Equal(Input, Assert.Single(args.OfType<JobArg.Input>()).Path);
		Assert.Equal(0, Assert.Single(args.OfType<JobArg.Output>()).Index);
		Assert.IsType<JobArg.Output>(args[^1]);
	}

	[Theory]
	[MemberData(nameof(Converted))]
	public void No_option_text_names_a_file_an_address_or_a_drive(ViewerKind kind)
	{
		foreach (var literal in ViewerConversion.Args(kind, Input, Extension(kind)).OfType<JobArg.Literal>())
		{
			Assert.DoesNotContain(":\\", literal.Text);
			Assert.DoesNotContain("//", literal.Text);
			Assert.DoesNotContain("|", literal.Text);
			Assert.False(literal.Text.StartsWith('@'));
		}
	}

	[Fact]
	public void The_output_names_are_fixed_per_kind_and_allowed_by_the_engine()
	{
		Assert.Contains("png", EngineFlagPolicy.Pinned.For(EngineIds.Magick).OutputExtensions);
		Assert.Contains("mp4", EngineFlagPolicy.Pinned.For(EngineIds.Ffmpeg).OutputExtensions);
		Assert.Contains("m4a", EngineFlagPolicy.Pinned.For(EngineIds.Ffmpeg).OutputExtensions);
		Assert.Equal("view.png", ViewerFormats.For("heic")!.OutputName);
		Assert.Equal("view.mp4", ViewerFormats.For("mov")!.OutputName);
		Assert.Equal("view.m4a", ViewerFormats.For("wma")!.OutputName);
	}

	[Fact]
	public void Video_is_a_fragmented_mp4_with_no_stream_that_cannot_go_in_one()
	{
		var text = string.Join(' ', ViewerConversion.Args(ViewerKind.Video, Input, ".mov").OfType<JobArg.Literal>().Select(l => l.Text));
		Assert.Contains("frag_keyframe+empty_moov+default_base_moof", text);
		Assert.Contains("-sn", text);
		Assert.Contains("-pix_fmt yuv420p", text);
	}

	[Theory]
	[InlineData(ViewerKind.Video, ".mov", "mov")]
	[InlineData(ViewerKind.Video, ".MKV", "matroska")]
	[InlineData(ViewerKind.Audio, ".wma", "asf")]
	public void FFmpeg_is_told_the_demuxer_before_the_input_so_a_concat_file_renamed_to_a_movie_is_refused(ViewerKind kind, string extension, string demuxer)
	{
		var args = ViewerConversion.Args(kind, Path.Combine(Path.GetTempPath(), "playlist" + extension), extension);
		var texts = args.Select(a => a is JobArg.Literal l ? l.Text : null).ToList();
		var input = args.ToList().FindIndex(a => a is JobArg.Input);
		Assert.Equal("-i", texts[input - 1]);
		Assert.Equal(demuxer, texts[input - 2]);
		Assert.Equal("-f", texts[input - 3]);
		EngineFlagPolicy.Pinned.For(EngineIds.Ffmpeg).Check(args);
	}

	[Fact]
	public void Every_viewer_converted_video_and_audio_extension_has_a_demuxer_and_a_playlist_has_none()
	{
		foreach (var ext in new[] { "mov", "mkv", "avi", "flv", "wmv", "mpg", "mpeg", "mts", "m2ts", "vob", "3gp", "3g2", "asf", "f4v", "mxf", "divx", "rm", "rmvb", "wma", "aiff", "aif", "alac", "amr", "ac3", "dts", "caf", "au", "mka", "spx", "ape", "wv", "mp2", "voc", "w64", "ra" })
		{
			Assert.NotEmpty(ViewerConversion.DemuxerFor(ext));
		}
		foreach (var ext in new[] { "m3u8", "m3u", "ffconcat", "txt", "sdp" }) Assert.Throws<UnsupportedViewerFormatException>(() => ViewerConversion.DemuxerFor(ext));
	}

	[Fact]
	public void A_native_format_has_no_conversion()
	{
		Assert.Throws<ArgumentException>(() => ViewerConversion.Args(ViewerKind.Native, Input, ".mov"));
		Assert.Throws<ArgumentException>(() => ViewerConversion.EngineFor(ViewerKind.Native));
	}

	[Fact]
	public void The_flags_added_for_the_viewer_still_refuse_what_they_must()
	{
		var policy = EngineFlagPolicy.Pinned.For(EngineIds.Ffmpeg);
		Assert.Throws<UnsafeEngineArgumentException>(() => policy.Check([new JobArg.Literal("-movflags"), new JobArg.Literal("frag_keyframe")]));
		Assert.Throws<UnsafeEngineArgumentException>(() => policy.Check([new JobArg.Literal("-preset"), new JobArg.Literal("C:/x")]));
		Assert.Throws<UnsafeEngineArgumentException>(() => policy.Check([new JobArg.Literal("-pix_fmt"), new JobArg.Literal("yuv444p")]));
		var magick = EngineFlagPolicy.Pinned.For(EngineIds.Magick);
		Assert.Throws<UnsafeEngineArgumentException>(() => magick.Check([new JobArg.Literal("-resize"), new JobArg.Literal("50%")]));
		Assert.Throws<UnsafeEngineArgumentException>(() => magick.Check([new JobArg.Literal("-delete"), new JobArg.Literal("0")]));
	}
}
