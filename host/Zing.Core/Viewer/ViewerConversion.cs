using Zing.Core.Engines;

namespace Zing.Core.Viewer;

/// <summary>
/// The engine commands that make a copy the page can show. Every option is a fixed constant here (see engine-rules.md): the
/// file's name, type or contents never become option text, and the only file arguments are the input the person picked and
/// the one output.
/// </summary>
public static class ViewerConversion
{
	public static string EngineFor(ViewerKind kind) => kind switch
	{
		ViewerKind.Image => EngineIds.Magick,
		ViewerKind.Video or ViewerKind.Audio => EngineIds.Ffmpeg,
		_ => throw new ArgumentException($"{kind} needs no conversion", nameof(kind)),
	};

	public static IReadOnlyList<JobArg> Args(ViewerKind kind, string input) => kind switch
	{
		// First frame only (a multi-page TIFF would otherwise write several files and leave the named one missing), turned upright,
		// no bigger than a screen needs. The output is last: ImageMagick overwrites whatever its last argument names.
		ViewerKind.Image =>
		[
			new JobArg.Input(input),
			Flag("-auto-orient"), Flag("-delete"), Flag("1--1"), Flag("-resize"), Flag("4096x4096>"),
			new JobArg.Output(0),
		],
		// Fragmented H.264 and AAC in an MP4 that Chromium's MediaSource takes: a keyframe every 48 frames makes a fragment at least that often,
		// 8-bit 4:2:0 and stereo are what every decoder plays, and no subtitle or data streams (they can't go in an MP4 as they are).
		ViewerKind.Video =>
		[
			Flag("-i"), new JobArg.Input(input),
			Flag("-sn"), Flag("-dn"),
			Flag("-c:v"), Flag("libx264"), Flag("-preset"), Flag("veryfast"), Flag("-crf"), Flag("23"), Flag("-g"), Flag("48"), Flag("-pix_fmt"), Flag("yuv420p"),
			Flag("-c:a"), Flag("aac"), Flag("-b:a"), Flag("160k"), Flag("-ac"), Flag("2"),
			Flag("-movflags"), Flag("frag_keyframe+empty_moov+default_base_moof"), Flag("-f"), Flag("mp4"),
			new JobArg.Output(0),
		],
		ViewerKind.Audio =>
		[
			Flag("-i"), new JobArg.Input(input),
			Flag("-vn"), Flag("-sn"), Flag("-dn"),
			Flag("-c:a"), Flag("aac"), Flag("-b:a"), Flag("192k"), Flag("-ac"), Flag("2"), Flag("-f"), Flag("ipod"),
			new JobArg.Output(0),
		],
		_ => throw new ArgumentException($"{kind} needs no conversion", nameof(kind)),
	};

	private static JobArg Flag(string text) => new JobArg.Literal(text);
}
