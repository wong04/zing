using System.Text.RegularExpressions;

namespace Zing.Core;

/// <summary>Links in a process's command line (src/main/launch-args.ts).</summary>
public static partial class LaunchArgs
{
	/// <summary>
	/// The web addresses in a command line, in order. Switches, the executable
	/// and the app path are skipped; only http(s) links qualify, so a file: or
	/// custom-scheme argument can't be smuggled in through a link click.
	/// </summary>
	public static List<string> Urls(IEnumerable<string> argv) => argv.Select(OpenableUrl).OfType<string>().ToList();

	/// <summary>
	/// The files in a command line that Windows' Open with hands Zing: an existing .pdf, .htm or .html file at an absolute
	/// path on a drive. Not a file: address, a share, or a relative path, so only the shell's own file associations reach here.
	/// </summary>
	public static List<LaunchFile> Files(IEnumerable<string> argv, Func<string, bool> exists) =>
		argv.Select(token => OpenableFile(token, exists)).OfType<LaunchFile>().ToList();

	private static LaunchFile? OpenableFile(string token, Func<string, bool> exists)
	{
		var path = Unquote(token);
		if (!DrivePath().IsMatch(path)) return null;
		var kind = Path.GetExtension(path).ToLowerInvariant() switch
		{
			".pdf" => LaunchFileKind.Pdf,
			".htm" or ".html" => LaunchFileKind.Html,
			_ => (LaunchFileKind?)null,
		};
		if (kind is null || !exists(path)) return null;
		return new LaunchFile(path, kind.Value);
	}

	private static string? OpenableUrl(string token)
	{
		var candidate = Unquote(token);
		if (candidate.StartsWith('-') || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
		return uri.Scheme is "http" or "https" ? candidate : null;
	}

	/// <summary>Windows hands a quoted argument through as-is when the shell's quoting reaches the process raw.</summary>
	private static string Unquote(string token)
	{
		var trimmed = token.Trim();
		if (trimmed.Length >= 2 && (trimmed[0] is '"' or '\'') && trimmed[^1] == trimmed[0]) return trimmed[1..^1].Trim();
		// A trailing backslash before a closing quote can leave one stray quote on either end.
		return trimmed.Trim('"', '\'').Trim();
	}

	[GeneratedRegex(@"^[A-Za-z]:\\[^\\]")]
	private static partial Regex DrivePath();
}

public enum LaunchFileKind
{
	Pdf,
	Html,
}

/// <summary>A file Windows asked Zing to open, in a tab at its file: address.</summary>
public sealed record LaunchFile(string Path, LaunchFileKind Kind)
{
	public string Address => new Uri(Path).AbsoluteUri;
}

/// <summary>Links that arrive before any window can take them wait here, as in Krackn's LinkInbox.</summary>
public sealed class LinkInbox
{
	private readonly List<string> _pending = [];
	private Action<string>? _sink;

	public int PendingCount => _pending.Count;

	public void Push(string url)
	{
		if (_sink is not null) _sink(url);
		else _pending.Add(url);
	}

	/// <summary>Hands over everything queued so far, then every later link directly.</summary>
	public void Attach(Action<string> sink)
	{
		_sink = sink;
		var queued = _pending.ToList();
		_pending.Clear();
		foreach (var url in queued) sink(url);
	}
}

/// <summary>A path handed to a window to open that isn't an existing .pdf, .htm or .html file on a drive.</summary>
public sealed class UnsupportedLaunchFileException(string path) : Exception($"Zing doesn't open {path}");
