using System.Text.RegularExpressions;

namespace Zing.Core.Engines;

internal enum FlagValue
{
	/// <summary>An optionally negative decimal such as 2, 1.5 or -1.</summary>
	Number,

	/// <summary>Up to 32 of A-Z a-z 0-9 _ . + : -, not starting with "-", and without a scheme-like "word:" prefix unless the flag says so.</summary>
	Word,

	/// <summary>One of the flag's listed choices, exactly.</summary>
	OneOf,

	/// <summary>The next argument must be a file the person picked or dropped.</summary>
	Input,

	/// <summary>The next argument must be one of the job's own outputs.</summary>
	Output,

	/// <summary>Any text. Only the test engine's flags use this; no entry for a real engine may.</summary>
	Unchecked,
}

/// <summary>What follows one allowed flag, in order. <see cref="Choices"/> lists the options for each <see cref="FlagValue.OneOf"/>.</summary>
internal sealed record FlagSpec(params FlagValue[] Values)
{
	public IReadOnlyList<string> Choices { get; init; } = [];

	/// <summary>The value may start like a URL scheme ("pipe:1"). No flag of a real engine needs it yet.</summary>
	public bool AllowsScheme { get; init; }
}

/// <param name="Network">Whether the engine may reach the network: it then also gets the proxy variables.</param>
/// <param name="SafetyFirst">Flags the runner puts before everything else; the renderer can't remove them.</param>
/// <param name="SafetyBefore">Flags the runner puts right before each occurrence of a flag (FFmpeg's whitelist is an input option, so it must precede each -i).</param>
/// <param name="OutputExtensions">The extensions a job's outputs may have (lower case, no dot).</param>
internal sealed partial record EnginePolicy(
	string EngineId,
	IReadOnlyDictionary<string, FlagSpec> Flags,
	IReadOnlyList<string> SafetyFirst,
	IReadOnlyDictionary<string, string[]> SafetyBefore,
	IReadOnlyList<string> OutputExtensions,
	bool Network)
{
	/// <summary>
	/// Every literal must be a flag in the table, followed by values of exactly the shape the flag declares; files are
	/// only ever <see cref="JobArg.Input"/> and <see cref="JobArg.Output"/>. There is no free text, so a literal can't be a
	/// path, an address, a protocol ("concat:"), a coder ("label:"), an @-list or a filter graph.
	/// </summary>
	/// <exception cref="DisallowedEngineFlagException">A literal where a flag belongs isn't on the engine's list.</exception>
	/// <exception cref="UnsafeEngineArgumentException">A value has the wrong shape, or is missing.</exception>
	public void Check(IReadOnlyList<JobArg> args)
	{
		for (var i = 0; i < args.Count; i++)
		{
			if (args[i] is not JobArg.Literal literal) continue;
			if (!Flags.TryGetValue(literal.Text, out var spec)) throw new DisallowedEngineFlagException(EngineId, literal.Text);
			var choice = 0;
			foreach (var kind in spec.Values)
			{
				i++;
				if (i >= args.Count) throw new UnsafeEngineArgumentException($"{literal.Text} needs a value");
				CheckValue(literal.Text, spec, kind, args[i], ref choice);
			}
		}
	}

	private static void CheckValue(string flag, FlagSpec spec, FlagValue kind, JobArg value, ref int choice)
	{
		switch (kind)
		{
			case FlagValue.Input when value is not JobArg.Input:
				throw new UnsafeEngineArgumentException($"{flag} takes a file the person picked or dropped");
			case FlagValue.Output when value is not JobArg.Output:
				throw new UnsafeEngineArgumentException($"{flag} takes one of the job's own outputs");
			case FlagValue.Input or FlagValue.Output:
				return;
		}
		if (value is not JobArg.Literal literal) throw new UnsafeEngineArgumentException($"{flag} takes plain text, not a file");
		var text = literal.Text;
		var ok = kind switch
		{
			FlagValue.Number => NumberShape().IsMatch(text),
			FlagValue.Word => WordShape().IsMatch(text) && (spec.AllowsScheme || !SchemeLike().IsMatch(text)),
			FlagValue.OneOf => spec.Choices.Contains(text, StringComparer.Ordinal),
			FlagValue.Unchecked => true,
			_ => false,
		};
		if (!ok) throw new UnsafeEngineArgumentException($"\"{text}\" isn't a value {flag} accepts");
	}

	[GeneratedRegex(@"^-?\d{1,9}(\.\d{1,6})?$")]
	private static partial Regex NumberShape();

	[GeneratedRegex(@"^[A-Za-z0-9_.+:][A-Za-z0-9_.+:-]{0,31}$")]
	private static partial Regex WordShape();

	[GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
	private static partial Regex SchemeLike();
}

/// <summary>
/// The only flags an engine may be given, with the reason none can reach a file, the network or a program (see
/// engine-rules.md). A new flag is a new entry here, reviewed on its own. Tool code builds its option text from these
/// and never passes a person's text through.
/// </summary>
internal sealed class EngineFlagPolicy
{
	private readonly Dictionary<string, EnginePolicy> _engines;

	private EngineFlagPolicy(Dictionary<string, EnginePolicy> engines) => _engines = engines;

	public EnginePolicy For(string engineId) =>
		_engines.TryGetValue(engineId, out var policy) ? policy : throw new UnknownEngineException(engineId);

	/// <summary>Test seam: the same policy with extra flags and output extensions for one engine (the fake engine's).</summary>
	public EngineFlagPolicy Plus(string engineId, IReadOnlyDictionary<string, FlagSpec> flags, params string[] extensions)
	{
		var current = For(engineId);
		var merged = new Dictionary<string, FlagSpec>(current.Flags, StringComparer.Ordinal);
		foreach (var (flag, spec) in flags) merged[flag] = spec;
		var copy = new Dictionary<string, EnginePolicy>(_engines, StringComparer.Ordinal)
		{
			[engineId] = current with { Flags = merged, OutputExtensions = [.. current.OutputExtensions, .. extensions] },
		};
		return new EngineFlagPolicy(copy);
	}

	private static Dictionary<string, FlagSpec> Table(params (string Flag, FlagSpec Spec)[] entries) =>
		entries.ToDictionary(e => e.Flag, e => e.Spec, StringComparer.Ordinal);

	private static readonly string[] FfmpegFormats = ["mp4", "mov", "matroska", "webm", "mp3", "wav", "flac", "ogg", "ipod", "gif", "image2"];
	private static readonly string[] PandocFormats = ["markdown", "gfm", "commonmark", "html", "docx", "odt", "epub", "rtf", "latex", "plain"];

	/// <summary>The flags Zing.FakeEngine understands: for tests and for e2e runs against the fixture, never for a real engine.</summary>
	internal static readonly IReadOnlyDictionary<string, FlagSpec> FakeEngineFlags = new Dictionary<string, FlagSpec>(StringComparer.Ordinal)
	{
		["--fake-write"] = new FlagSpec(FlagValue.Output),
		["--write-after"] = new FlagSpec(FlagValue.Number, FlagValue.Output),
		["--print-cwd"] = new FlagSpec(),
		["--spawn-child"] = new FlagSpec(),
		["--sleep"] = new FlagSpec(FlagValue.Number),
		["--chatty"] = new FlagSpec(FlagValue.Number),
		["--long-line"] = new FlagSpec(FlagValue.Number),
		["--err"] = new FlagSpec(FlagValue.Unchecked),
		["--exit"] = new FlagSpec(FlagValue.Number),
		["--print-env"] = new FlagSpec(),
		["--print-magick-policy"] = new FlagSpec(),
	};

	/// <summary>Pinned, plus the fake engine's flags and a .txt output on every engine (the e2e fixture serves it as each).</summary>
	internal static EngineFlagPolicy WithFakeEngine()
	{
		var policy = Pinned;
		foreach (var id in new[] { EngineIds.Ffmpeg, EngineIds.Magick, EngineIds.Pandoc, EngineIds.YtDlp }) policy = policy.Plus(id, FakeEngineFlags, "txt");
		return policy;
	}

	public static EngineFlagPolicy Pinned { get; } = new(new Dictionary<string, EnginePolicy>(StringComparer.Ordinal)
	{
		// -i takes a granted file, and the runner puts a protocol whitelist of file and pipe before each, so nothing here
		// can open a network or concat/subfile address. -f takes a fixed list that leaves out lavfi, concat, tee, segment and
		// hls. No filter strings: a filter graph can read files (movie=), so a tool needing one adds a named, fixed one.
		[EngineIds.Ffmpeg] = new EnginePolicy(
			EngineIds.Ffmpeg,
			Table(
				("-i", new FlagSpec(FlagValue.Input)),
				("-c:v", new FlagSpec(FlagValue.Word)),
				("-c:a", new FlagSpec(FlagValue.Word)),
				("-b:a", new FlagSpec(FlagValue.Word)),
				("-q:a", new FlagSpec(FlagValue.Number)),
				("-ss", new FlagSpec(FlagValue.Number)),
				("-t", new FlagSpec(FlagValue.Number)),
				("-vn", new FlagSpec()),
				("-an", new FlagSpec()),
				("-f", new FlagSpec(FlagValue.OneOf) { Choices = FfmpegFormats }),
				// The file viewer's conversions. None names a file, an address or a program: -sn and -dn drop streams, the rest are
				// numbers or a fixed word, and -movflags takes the one fixed set that makes a fragmented MP4.
				("-sn", new FlagSpec()),
				("-dn", new FlagSpec()),
				("-preset", new FlagSpec(FlagValue.OneOf) { Choices = ["ultrafast", "superfast", "veryfast", "faster", "fast"] }),
				("-crf", new FlagSpec(FlagValue.Number)),
				("-g", new FlagSpec(FlagValue.Number)),
				("-pix_fmt", new FlagSpec(FlagValue.OneOf) { Choices = ["yuv420p"] }),
				("-ac", new FlagSpec(FlagValue.OneOf) { Choices = ["2"] }),
				("-movflags", new FlagSpec(FlagValue.OneOf) { Choices = ["frag_keyframe+empty_moov+default_base_moof"] })),
			["-nostdin"],
			new Dictionary<string, string[]> { ["-i"] = ["-protocol_whitelist", "file,pipe"] },
			["mp4", "mov", "mkv", "webm", "mp3", "wav", "flac", "aac", "m4a", "ogg", "opus", "gif", "jpg", "jpeg", "png", "webp"],
			Network: false),
		// -o takes one of the job's outputs. --sandbox makes Pandoc refuse to read or write any other file or run a program.
		[EngineIds.Pandoc] = new EnginePolicy(
			EngineIds.Pandoc,
			Table(
				("-f", new FlagSpec(FlagValue.OneOf) { Choices = PandocFormats }),
				("--from", new FlagSpec(FlagValue.OneOf) { Choices = PandocFormats }),
				("-t", new FlagSpec(FlagValue.OneOf) { Choices = PandocFormats }),
				("--to", new FlagSpec(FlagValue.OneOf) { Choices = PandocFormats }),
				("-o", new FlagSpec(FlagValue.Output)),
				("--standalone", new FlagSpec())),
			["--sandbox"],
			new Dictionary<string, string[]>(),
			["html", "docx", "odt", "epub", "rtf", "tex", "md", "txt"],
			Network: false),
		// The policy Zing writes (MagickPolicy) blocks the coders and paths that reach files or the network.
		[EngineIds.Magick] = new EnginePolicy(
			EngineIds.Magick,
			Table(
				("-quality", new FlagSpec(FlagValue.Number)),
				("-strip", new FlagSpec()),
				("-auto-orient", new FlagSpec()),
				// The file viewer shows the first frame of a picture, no bigger than a screen needs. Both take one fixed word.
				("-delete", new FlagSpec(FlagValue.OneOf) { Choices = ["1--1"] }),
				("-resize", new FlagSpec(FlagValue.OneOf) { Choices = ["4096x4096>"] })),
			[],
			new Dictionary<string, string[]>(),
			["jpg", "jpeg", "png", "webp", "gif", "bmp", "tif", "tiff", "avif", "heic", "ico"],
			Network: false),
		// The only engine with the network, as it fetches a page. Its config files, plugins and cache are switched off; -o
		// takes one of the job's outputs; addresses come only through the job's urls, after a "--".
		[EngineIds.YtDlp] = new EnginePolicy(
			EngineIds.YtDlp,
			Table(
				("--newline", new FlagSpec()),
				("--no-playlist", new FlagSpec()),
				("-x", new FlagSpec()),
				("--audio-format", new FlagSpec(FlagValue.OneOf) { Choices = ["mp3", "m4a", "opus", "wav"] }),
				("--audio-quality", new FlagSpec(FlagValue.Number)),
				("-o", new FlagSpec(FlagValue.Output))),
			["--ignore-config", "--no-plugin-dirs", "--no-cache-dir"],
			new Dictionary<string, string[]>(),
			["mp4", "mkv", "webm", "mp3", "m4a", "opus", "wav"],
			Network: true),
		// A model file, not a program: it is never started.
		[EngineIds.BackgroundModel] = new EnginePolicy(EngineIds.BackgroundModel, Table(), [], new Dictionary<string, string[]>(), [], Network: false),
	});
}
