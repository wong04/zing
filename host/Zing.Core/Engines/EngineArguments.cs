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
	/// http and https addresses pass, so no file:, no relative text, and nothing that starts with "-".
	/// </summary>
	public static IReadOnlyList<string> Urls(IEnumerable<string> urls)
	{
		List<string> operands = ["--"];
		foreach (var url in urls)
		{
			var valid = Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" && !url.StartsWith('-');
			if (!valid) throw new UnsafeEngineArgumentException($"{url} isn't an http or https address");
			operands.Add(url);
		}
		return operands;
	}
}
