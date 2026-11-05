using System.Net;
using System.Text.Json;

namespace Zing.Core.Engines;

public static class EngineIds
{
	public const string Ffmpeg = "ffmpeg";
	public const string Magick = "magick";
	public const string Pandoc = "pandoc";
	public const string YtDlp = "ytdlp";
	public const string BackgroundModel = "bgmodel";
}

/// <summary>
/// One program (or model) Zing downloads the first time a tool needs it.
/// <paramref name="Url"/> is a fixed release, never "latest". <paramref name="Bytes"/> is the size of that
/// download, used to refuse a bigger response early. <paramref name="Executable"/> is the file to run (or the
/// model to load), relative to the engine's folder once unpacked.
/// </summary>
public sealed record Engine(string Id, string Version, Uri Url, string Sha256, long Bytes, string Executable, string Name, string Licence);

public sealed class EngineCatalog
{
	public EngineCatalog(IReadOnlyList<Engine> engines, Uri ytDlpLatest, Uri ytDlpReleases)
	{
		Engines = engines;
		YtDlpLatest = ytDlpLatest;
		YtDlpReleases = ytDlpReleases;
	}

	public IReadOnlyList<Engine> Engines { get; }

	/// <summary>The newest yt-dlp release's SHA2-256SUMS. GitHub redirects it to the release's own address, which names the version.</summary>
	public Uri YtDlpLatest { get; }

	/// <summary>Where tagged releases live ("&lt;base&gt;&lt;tag&gt;/yt-dlp.exe"); the update builds its addresses from this, not from redirects.</summary>
	public Uri YtDlpReleases { get; }

	public Engine Get(string id) => Engines.FirstOrDefault(e => e.Id == id) ?? throw new UnknownEngineException(id);

	/// <summary>The name shown to a person, for errors raised before a catalog is at hand.</summary>
	public static string NameOf(string id) => Pinned.Engines.FirstOrDefault(e => e.Id == id)?.Name ?? id;

	// Every hash below was computed by Zing's maintainers from the file the URL serves, not copied from a page.
	// Nothing here is a bundled binary: Zing hosts nothing, and an engine is fetched only when a person starts a
	// tool that needs it. Each engine is a separate program that Zing runs and never links against, so Zing's own
	// MIT licence is unaffected by theirs.
	public static EngineCatalog Pinned { get; } = new(
		[
			// FFmpeg has no official Windows binaries, only source; ffmpeg.org's download page points to Gyan Doshi's
			// builds, which he publishes under the GitHub release tag below. GPL v3 (the "essentials" build links
			// libx264 and libx265, which are GPL). Zing doesn't redistribute it and runs it as its own process, so
			// the GPL covers FFmpeg and not Zing. Two things are NOT settled by that, and are the owner's to decide:
			//  - HEVC (H.265): libx265 is a software encoder under the GPL, but HEVC is also covered by patent
			//    pools (Access Advance, Via LA) whose licences are separate from the source licence, and nothing
			//    in this download grants one. Zing doesn't ship or sell the build; the person downloads it from its
			//    publisher. That is a position, not legal advice or a guarantee.
			//  - MP3 encoding: libmp3lame (LGPL). As far as is known the MP3 patents expired in 2017, which is
			//    a general understanding to confirm, not a licence in this download.
			new Engine(
				EngineIds.Ffmpeg,
				"9.0.2",
				new Uri("https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip"),
				"60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba",
				114_768_076,
				@"ffmpeg-9.0.2-essentials_build\bin\ffmpeg.exe",
				"FFmpeg",
				"GPL v3 (essentials build with libx264, libx265, libmp3lame)"),
			// ImageMagick's own download page links this GitHub release; the portable build is a 7z only.
			// ImageMagick License (Apache-2.0 style, permits commercial use).
			new Engine(
				EngineIds.Magick,
				"7.1.2-35",
				new Uri("https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-35/ImageMagick-7.1.2-35-portable-Q16-HDRI-x64.7z"),
				"34db4364da26c88d6b8cc5bbc1fff22b85d00d3b399c82fe85ee4790efacbbf1",
				12_117_825,
				"magick.exe",
				"ImageMagick",
				"ImageMagick License (Apache-2.0 style)"),
			// Pandoc: GPL v2 or later, run as its own process.
			new Engine(
				EngineIds.Pandoc,
				"3.13",
				new Uri("https://github.com/jgm/pandoc/releases/download/3.13/pandoc-3.13-windows-x86_64.zip"),
				"23e1f2e978a3df80db8bf65c9c59534f41a23140480cfb4f783a882ab7dc6a4e",
				42_983_200,
				@"pandoc-3.13\pandoc.exe",
				"Pandoc",
				"GPL v2 or later"),
			// yt-dlp: The Unlicense (public domain). Its releases publish SHA2-256SUMS, which is how an update is
			// checked; that file comes from the same host as the program, so it catches a corrupt or truncated
			// download but not a compromised release.
			new Engine(
				EngineIds.YtDlp,
				"2026.10.26",
				new Uri("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe"),
				"f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d",
				23_575_744,
				"yt-dlp.exe",
				"yt-dlp",
				"The Unlicense"),
			// IS-Net "general use" (xuebinqin/DIS), converted to ONNX and hosted by rembg's v0.0.0 release. The DIS repository
			// is Apache-2.0 ("our code and evaluation metric"); the weights' own licence and the terms of the data they were
			// trained on (DIS5K has separate terms of use) are not stated, so the owner should confirm this reading. The DIS
			// authors publish the original weights on Google Drive only, so rembg's GitHub release is the nearest stable URL
			// and is a third-party re-host. Its MD5 matches the one rembg's own source pins for this file.
			new Engine(
				EngineIds.BackgroundModel,
				"v0.0.0",
				new Uri("https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx"),
				"60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a",
				178_648_008,
				"isnet-general-use.onnx",
				"Background remover model (IS-Net)",
				"The weights' licence isn't stated; the DIS repository's code is Apache-2.0"),
		],
		new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS"),
		new Uri("https://github.com/yt-dlp/yt-dlp/releases/download/"));

	/// <summary>
	/// A test run's catalog (ZING_ENGINE_BASE_URL and ZING_ENGINE_CATALOG): the same engines, served by a fixture on this PC.
	/// </summary>
	public static EngineCatalog FromFixture(Uri baseUrl, string manifestJson)
	{
		if (!IsThisPc(baseUrl)) throw new ArgumentException($"A fixture catalog must point at this PC, not {baseUrl.Host}", nameof(baseUrl));
		var entries = JsonSerializer.Deserialize<List<FixtureEntry>>(manifestJson, ZingJson.Options) ?? throw new InvalidDataException("The fixture catalog is empty");
		foreach (var entry in entries)
		{
			// "file" is joined onto the base address, so it must stay below it.
			if (Path.IsPathRooted(entry.File) || entry.File.Contains("..") || entry.File.Contains(':')) throw new ArgumentException($"A fixture file must be a plain name below the base address, not {entry.File}", nameof(manifestJson));
		}
		var engines = entries.Select(e => new Engine(e.Id, e.Version, new Uri(baseUrl, e.File), e.Sha256, e.Bytes, e.Executable, NameOf(e.Id), "test fixture")).ToList();
		return new EngineCatalog(engines, new Uri(baseUrl, "yt-dlp/latest/SHA2-256SUMS"), new Uri(baseUrl, "yt-dlp/"));
	}

	private sealed record FixtureEntry(string Id, string Version, string File, string Sha256, long Bytes, string Executable);

	private static bool IsThisPc(Uri url) => url.IsLoopback || url.Host == "localhost" || IPAddress.TryParse(url.Host, out var ip) && IPAddress.IsLoopback(ip);
}
