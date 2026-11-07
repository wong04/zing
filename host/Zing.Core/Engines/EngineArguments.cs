namespace Zing.Core.Engines;

/// <summary>What the renderer's text becomes before an engine sees it. Arguments always go through ArgumentList, never a shell; this is the rest.</summary>
public static class EngineArguments
{
	/// <summary>FFmpeg and yt-dlp read an operand that starts with "-" as an option, so a file named "-rf.mov" must reach them as an absolute path, which cannot.</summary>
	public static string FilePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new UnsafeEngineArgumentException("A file path can't be empty or contain a NUL character");
		return Path.GetFullPath(path);
	}

	/// <summary>
	/// The operands for yt-dlp-style tools: a "--" so nothing after it is an option, then each address. Only absolute
	/// http and https addresses pass: no file:, no relative text, nothing that starts with "-", no sign-in in the address,
	/// no space or control character, and nothing that names this PC or the local network (a renderer can't be trusted to
	/// have checked: this is the only engine with the network).
	/// </summary>
	public static IReadOnlyList<string> Urls(IEnumerable<string> urls)
	{
		List<string> operands = ["--"];
		foreach (var url in urls)
		{
			// A backslash is a path separator to some parsers and part of the host to others (Python reads "a.com\@127.0.0.1" as
			// 127.0.0.1), and a percent in the host is decoded by some and not others: neither is worth the doubt.
			var valid = !url.StartsWith('-') && !url.Any(c => c <= ' ' || c == '\u007f' || c == '\\') && !Authority(url).Contains('%') && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" && parsed.UserInfo.Length == 0;
			if (!valid) throw new UnsafeEngineArgumentException($"{url} isn't an http or https address");
			var checkedUri = new Uri(url);
			if (LocalAddresses.IsLocal(checkedUri)) throw new UnsafeEngineArgumentException($"{url} is on this PC or the local network");
			// The text that was checked is the text the engine gets: the host in its ASCII form (IdnHost), which is the one classified.
			var ascii = checkedUri.HostNameType == UriHostNameType.Dns ? new UriBuilder(checkedUri) { Host = checkedUri.IdnHost }.Uri : checkedUri;
			operands.Add(ascii.AbsoluteUri);
		}
		return operands;
	}

	private static string Authority(string url)
	{
		var start = url.IndexOf("://", StringComparison.Ordinal);
		if (start < 0) return "";
		var rest = url[(start + 3)..];
		var end = rest.IndexOfAny(['/', '?', '#']);
		return end < 0 ? rest : rest[..end];
	}
}
