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
			var valid = !url.StartsWith('-') && !url.Any(c => c <= ' ' || c == '') && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" && parsed.UserInfo.Length == 0;
			if (!valid) throw new UnsafeEngineArgumentException($"{url} isn't an http or https address");
			if (LocalAddresses.IsLocal(new Uri(url))) throw new UnsafeEngineArgumentException($"{url} is on this PC or the local network");
			operands.Add(url);
		}
		return operands;
	}
}
