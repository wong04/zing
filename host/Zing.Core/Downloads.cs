using System.Globalization;
using System.Text.RegularExpressions;

namespace Zing.Core;

/// <param name="CanShow">Whether the engine can display the response's MIME type.</param>
/// <param name="ContentDisposition">The response's `Content-Disposition` header, if any.</param>
/// <param name="IsMainFrame">Whether this is the page itself, not an embedded frame.</param>
public sealed record DownloadResponse(bool CanShow, string? ContentDisposition, bool IsMainFrame);

/// <summary>Port of src/shared/browser/downloadPolicy.ts.</summary>
public static class DownloadPolicy
{
	public static bool ShouldDownload(DownloadResponse response)
	{
		// How most download buttons work, often from a hidden frame, so frames count here too.
		if (response.ContentDisposition?.Trim().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) == true) return true;
		// An odd content type in an embedded ad frame shouldn't drop a file in Downloads.
		return response.IsMainFrame && !response.CanShow;
	}
}

/// <summary>Port of src/shared/browser/downloadName.ts (and the `safeFileBase` it uses from convert/commands.ts).</summary>
public static partial class DownloadNames
{
	[GeneratedRegex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ReservedName();

	private static readonly char[] ForbiddenInWindows = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

	/// <summary>The file name a download is saved under: safe for Windows, and numbered ("report 2.pdf") rather than overwriting.</summary>
	public static string FileName(string filename, Func<string, bool> exists)
	{
		var dot = filename.LastIndexOf('.');
		var hasExtension = dot > 0 && dot < filename.Length - 1;
		var baseName = SafeFileBase(hasExtension ? filename[..dot] : filename);
		var extension = hasExtension ? "." + filename[(dot + 1)..] : "";
		var candidate = baseName + extension;
		for (var counter = 2; exists(candidate); counter++) candidate = $"{baseName} {counter}{extension}";
		return candidate;
	}

	public static string SafeFileBase(string name)
	{
		var printable = new string([.. name.Where(ch => ch >= 0x20 && !ForbiddenInWindows.Contains(ch))]);
		var cleaned = printable.TrimEnd('.', ' ').Trim();
		if (cleaned == "") return "file";
		return ReservedName().IsMatch(cleaned) ? cleaned + "_" : cleaned;
	}
}

public enum MediaMode { Video, Audio }

public sealed record DownloadProgress(double Percent, double? TotalBytes = null, double? SpeedBytesPerSecond = null, double? EtaSeconds = null);

/// <summary>Port of src/shared/download/ytdlp.ts: arguments, progress and failures.</summary>
public static partial class YtDlp
{
	public static IReadOnlyList<string> Args(MediaMode mode, string outputTemplate, string url)
	{
		List<string> args = ["--newline", "--no-playlist", "-o", outputTemplate];
		if (mode == MediaMode.Audio) args.AddRange(["-x", "--audio-format", "mp3", "--audio-quality", "0"]);
		// A pre-merged mp4 so a missing ffmpeg doesn't block stitching separate streams.
		if (mode == MediaMode.Video) args.AddRange(["-f", "best[ext=mp4]/bestvideo+bestaudio/best"]);
		args.Add(url);
		return args;
	}

	// Krackn measured that adding web_safari and tv made things worse, so it's `default` only.
	public static IReadOnlyList<string> RetryArgs(IReadOnlyList<string> args) =>
		["--extractor-args", "youtube:player_client=default", .. args];

	private static readonly string[] Benign = ["private video", "video unavailable", "members-only", "is not a valid url", "unsupported url", "no space left", "sign in to confirm your age"];

	private static readonly string[] BreakageSignals =
	[
		"403", "forbidden", "unable to download video data", "unable to extract", "nsig extraction failed",
		"signature extraction failed", "unable to extract player response", "requested format is not available",
	];

	/// <summary>Failures that mean "the extractor is out of date", as opposed to a bad URL or a private video.</summary>
	public static bool LooksLikeExtractorBreakage(string stderr)
	{
		var text = stderr.ToLowerInvariant();
		// Checked first: ordinary refusals no update can fix, several of which mention a status code.
		if (Benign.Any(text.Contains)) return false;
		return BreakageSignals.Any(text.Contains);
	}

	public static string ExplainFailure(string stderr, int exitCode)
	{
		var text = stderr.ToLowerInvariant();
		if (text.Contains("private video")) return "This video is private.";
		if (text.Contains("video unavailable")) return "This video isn't available.";
		if (text.Contains("sign in to confirm your age") || text.Contains("age-restricted"))
			return "This video is age-restricted, so it can't be downloaded without signing in.";
		if (text.Contains("no space left")) return "The disk is full.";
		if (text.Contains("is not a valid url") || text.Contains("unsupported url")) return "That link isn't one yt-dlp recognises.";
		if (text.Contains("403") || text.Contains("forbidden"))
			return "YouTube refused the download (HTTP 403). This nearly always means yt-dlp needs updating: YouTube changes how it signs media links every few weeks, and an older copy stops being able to follow them.";
		if (text.Contains("ffmpeg")) return "This needs FFmpeg to finish. Install it from Convert → Set Up Tools.";
		var last = stderr.Split(["\r\n", "\n"], StringSplitOptions.None).LastOrDefault(l => l.Trim() != "");
		return last ?? $"yt-dlp exited with code {exitCode}.";
	}

	private static readonly Dictionary<string, double> Units = new()
	{
		["B"] = 1, ["KiB"] = 1024, ["MiB"] = 1024.0 * 1024, ["GiB"] = Math.Pow(1024, 3), ["TiB"] = Math.Pow(1024, 4),
		["KB"] = 1e3, ["MB"] = 1e6, ["GB"] = 1e9,
	};

	// ECMAScript so \w, \d and \s mean what they do in the TS original.
	[GeneratedRegex(@"^\[download\]\s+([\d.]+)%(?:\s+of\s+~?\s*([\d.]+)(\w+))?(?:\s+at\s+([\d.]+)(\w+)/s)?(?:\s+ETA\s+([\d:]+))?", RegexOptions.ECMAScript)]
	private static partial Regex ProgressLine();

	/// <summary>"[download]  42.3% of ~ 12.34MiB at 1.23MiB/s ETA 00:10 (frag 3/9)"; null for any other line.</summary>
	public static DownloadProgress? ParseProgress(string line)
	{
		var match = ProgressLine().Match(line.Trim());
		if (!match.Success) return null;
		if (!TryNumber(match.Groups[1].Value, out var percent)) return null;
		var progress = new DownloadProgress(percent);
		if (match.Groups[2].Success && match.Groups[3].Success) progress = progress with { TotalBytes = Bytes(match.Groups[2].Value, match.Groups[3].Value) };
		if (match.Groups[4].Success && match.Groups[5].Success) progress = progress with { SpeedBytesPerSecond = Bytes(match.Groups[4].Value, match.Groups[5].Value) };
		if (match.Groups[6].Success) progress = progress with { EtaSeconds = match.Groups[6].Value.Split(':').Aggregate(0.0, (sum, part) => sum * 60 + (part == "" ? 0 : double.Parse(part, CultureInfo.InvariantCulture))) };
		return progress;
	}

	private static bool TryNumber(string text, out double value) =>
		double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	private static double? Bytes(string amount, string unit) =>
		Units.TryGetValue(unit, out var scale) && TryNumber(amount, out var n) ? n * scale : null;
}

public interface IMarkStore
{
	ZoneMark? Read();
	void Write(ZoneMark mark);
}

/// <summary>A Zone.Identifier mark (Mark of the Web). Mirrors `MarkOfTheWeb` in src/platform/windows/security.ts.</summary>
public sealed record ZoneMark(int ZoneId, string? HostUrl = null, string? ReferrerUrl = null);

/// <summary>
/// Port of src/platform/windows/holdMark.ts. Chromium (Edge's engine too) stamps a generic
/// `about:internet` mark shortly after a download finishes, so a mark we write once can be
/// replaced. The host only needs this if it wants the real HostUrl/ReferrerUrl on the file:
/// WebView2's own download manager already writes a mark, so it is optional there.
/// </summary>
public static class MarkOfTheWeb
{
	public static async Task HoldAsync(IMarkStore store, ZoneMark ours, int windowMs, int pollMs, Func<int, Task> sleep)
	{
		store.Write(ours);
		for (var waited = 0; waited < windowMs; waited += pollMs)
		{
			await sleep(pollMs);
			var current = store.Read();
			if (current is not null && current.HostUrl == ours.HostUrl && current.ZoneId == ours.ZoneId) continue;
			store.Write(ours);
			// The engine writes its mark once; after overruling it there is nothing left to wait for.
			if (current is not null) return;
		}
	}
}

public enum DownloadOutcome { Completed, Cancelled, Interrupted }

public sealed class UnknownDownloadException(int id) : Exception($"No download with id {id}")
{
	public int Id { get; } = id;
}

/// <summary>What <see cref="DownloadList"/> needs from the host.</summary>
public interface IDownloadHost
{
	bool FileExists(string path);

	/// <summary>Opens the file; returns the failure text, or null when it opened (like Electron's `shell.openPath`).</summary>
	string? OpenFile(string path);

	void ShowInFolder(string path);

	/// <summary>Cancels the engine's download. Only called for a download still in progress.</summary>
	void Cancel(int id);
}

public sealed record StartedDownload(int Id, string Path);

/// <summary>Port of the pure state in src/main/downloads.ts: one window's download list and what the panel's buttons do to it.</summary>
public sealed class DownloadList(string directory, IDownloadHost host, PageUiStore ui, Action<string> notify)
{
	private readonly Dictionary<int, string> _paths = [];
	private readonly HashSet<int> _active = [];
	// Names handed out but not yet on disk, so two simultaneous downloads of one name don't collide.
	private readonly HashSet<string> _reserved = [];
	private List<DownloadEntry> _entries = [];
	private int _nextId = 1;

	public IReadOnlyList<DownloadEntry> Entries => _entries;

	/// <summary>Claims a free file name and adds a progressing entry. The host saves to <see cref="StartedDownload.Path"/>.</summary>
	public StartedDownload Start(string filename, long total)
	{
		var name = DownloadNames.FileName(filename, candidate =>
		{
			var path = Path.Combine(directory, candidate);
			return host.FileExists(path) || _reserved.Contains(path);
		});
		var saveAs = Path.Combine(directory, name);
		_reserved.Add(saveAs);
		var id = _nextId++;
		_paths[id] = saveAs;
		_active.Add(id);
		_entries = [.. _entries, new DownloadEntry(id, filename, DownloadStatuses.Progressing, 0, total)];
		Publish();
		return new StartedDownload(id, saveAs);
	}

	/// <summary>Records progress without pushing; the host throttles and calls <see cref="Publish"/>.</summary>
	public void Progress(int id, long received, long total) => Update(id, e => e with { Received = received, Total = total });

	public void Done(int id, DownloadOutcome outcome, long received)
	{
		var path = PathOf(id);
		_active.Remove(id);
		_reserved.Remove(path);
		var status = outcome switch
		{
			DownloadOutcome.Completed => DownloadStatuses.Completed,
			DownloadOutcome.Cancelled => DownloadStatuses.Cancelled,
			_ => DownloadStatuses.Failed,
		};
		Update(id, e => e with { Status = status, Received = received });
		Publish();
	}

	/// <summary>The file was saved but couldn't be marked as downloaded.</summary>
	public void MarkFailed(int id, string reason)
	{
		Update(id, e => e with { Error = $"Saved, but couldn't mark it as downloaded ({reason})" });
		Publish();
	}

	public void Open(int id)
	{
		var path = PathOf(id);
		var error = host.OpenFile(path);
		if (error is not null) notify($"Couldn't open {path} ({error})");
	}

	public void Show(int id) => host.ShowInFolder(PathOf(id));

	public void Cancel(int id)
	{
		if (_active.Contains(id)) host.Cancel(id);
	}

	public void Clear()
	{
		_entries = [.. _entries.Where(e => e.Status == DownloadStatuses.Progressing)];
		ui.Update(s => s with { Downloads = _entries, DownloadsOpen = _entries.Count > 0 });
	}

	public void Dismiss() => ui.Update(s => s with { DownloadsOpen = false });

	public void Publish()
	{
		var entries = _entries;
		ui.Update(s => s with { Downloads = entries, DownloadsOpen = true });
	}

	private string PathOf(int id) => _paths.TryGetValue(id, out var path) ? path : throw new UnknownDownloadException(id);

	private void Update(int id, Func<DownloadEntry, DownloadEntry> change) =>
		_entries = [.. _entries.Select(e => e.Id == id ? change(e) : e)];
}

/// <summary>The Zone.Identifier stream's text (parseZoneIdentifier / formatZoneIdentifier in security.ts).</summary>
public static class ZoneIdentifier
{
	public const int InternetZone = 3;

	public static ZoneMark? Parse(string text)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in text.Split('\n'))
		{
			var at = line.IndexOf('=');
			if (at > 0) values[line[..at].Trim().ToLowerInvariant()] = line[(at + 1)..].Trim();
		}
		if (!values.TryGetValue("zoneid", out var zone) || zone.Length == 0 || !zone.All(char.IsAsciiDigit)) return null;
		return new ZoneMark(int.Parse(zone, CultureInfo.InvariantCulture), values.GetValueOrDefault("hosturl"), values.GetValueOrDefault("referrerurl"));
	}

	public static string Format(ZoneMark mark)
	{
		List<string> lines = ["[ZoneTransfer]", $"ZoneId={mark.ZoneId}"];
		if (!string.IsNullOrEmpty(mark.ReferrerUrl)) lines.Add($"ReferrerUrl={mark.ReferrerUrl}");
		if (!string.IsNullOrEmpty(mark.HostUrl)) lines.Add($"HostUrl={mark.HostUrl}");
		return string.Join("\r\n", lines) + "\r\n";
	}
}
