namespace Zing.Core;

/// <summary>Links in a process's command line (src/main/launch-args.ts).</summary>
public static class LaunchArgs
{
	/// <summary>
	/// The web addresses in a command line, in order. Switches, the executable
	/// and the app path are skipped; only http(s) links qualify, so a file: or
	/// custom-scheme argument can't be smuggled in through a link click.
	/// </summary>
	public static List<string> Urls(IEnumerable<string> argv) => argv.Select(OpenableUrl).OfType<string>().ToList();

	/// <summary>Splits a raw Windows command line the way the C runtime does, for activations that arrive as one string.</summary>
	public static List<string> Split(string commandLine)
	{
		var tokens = new List<string>();
		var current = new System.Text.StringBuilder();
		var quoted = false;
		var any = false;
		foreach (var c in commandLine)
		{
			if (c == '"')
			{
				quoted = !quoted;
				any = true;
				continue;
			}
			if (char.IsWhiteSpace(c) && !quoted)
			{
				if (any) tokens.Add(current.ToString());
				current.Clear();
				any = false;
				continue;
			}
			current.Append(c);
			any = true;
		}
		if (any) tokens.Add(current.ToString());
		return tokens;
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
