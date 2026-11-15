namespace Zing.Core;

/// <summary>
/// WebView2 hands over a page's icon at 16×16 whatever the screen, which a tile on a 2× screen blurs. So the host reads the icon
/// file the engine chose through the page's own network (usually its cache) and keeps it at its own size, up to <see cref="MaxSize"/>.
/// </summary>
public static class SharpIcon
{
	/// <summary>Enough for an 18 px tile on a 3× screen.</summary>
	public const uint MaxSize = 64;

	/// <summary>The size the engine already gives; a file no bigger gains nothing.</summary>
	public const uint EngineSize = 16;

	/// <summary>An icon file bigger than this isn't read; the engine's icon is used.</summary>
	public const int MaxSourceBytes = 1024 * 1024;

	/// <summary>Only an icon on the web is read again; anything else (Zing's own pages, data URLs) keeps the engine's.</summary>
	public static bool CanFetch(string uri) =>
		Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https";

	/// <summary>The index of the largest frame (an .ico holds several), or null when none is bigger than the engine's icon.</summary>
	public static int? BestFrame(IReadOnlyList<(uint Width, uint Height)> frames)
	{
		int? best = null;
		for (var i = 0; i < frames.Count; i++)
		{
			if (Math.Max(frames[i].Width, frames[i].Height) <= EngineSize) continue;
			if (best is { } b && Area(frames[b]) >= Area(frames[i])) continue;
			best = i;
		}
		return best;
	}

	/// <summary>The size an icon is kept at: its own, scaled down so its longer side is at most <see cref="MaxSize"/>.</summary>
	public static (uint Width, uint Height) KeptSize(uint width, uint height)
	{
		var longer = Math.Max(width, height);
		if (longer <= MaxSize) return (width, height);
		return (Math.Max(1, width * MaxSize / longer), Math.Max(1, height * MaxSize / longer));
	}

	/// <summary>Whether the file is an SVG, which Windows' image decoders can't read.</summary>
	public static bool IsSvg(ReadOnlySpan<byte> file)
	{
		var text = file[..Math.Min(file.Length, 512)];
		var start = 0;
		while (start < text.Length && text[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF) start++;
		if (start >= text.Length || text[start] != (byte)'<') return false;
		return text.IndexOf("<svg"u8) >= 0;
	}

	private static ulong Area((uint Width, uint Height) frame) => (ulong)frame.Width * frame.Height;
}
