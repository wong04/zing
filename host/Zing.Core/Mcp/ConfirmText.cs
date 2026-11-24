using System.Globalization;
using System.Text;

namespace Zing.Core.Mcp;

/// <summary>
/// What a confirm bar may show: page-influenced text (an accessible name, a key, a URL) made one bounded line. Format
/// characters (bidi overrides) go entirely, control and line-break characters become spaces, runs of space collapse.
/// </summary>
public static class ConfirmText
{
	public const int MaxLength = 200;

	public static string Clamp(string text)
	{
		var line = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
			var spaced = char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c;
			if (spaced == ' ' && (line.Length == 0 || line[^1] == ' ')) continue;
			line.Append(spaced);
		}
		var clean = line.ToString().TrimEnd();
		if (clean.Length <= MaxLength) return clean;
		var cut = clean[..(MaxLength - 1)];
		// Never leave half a surrogate pair at the cut.
		if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
		return cut + "\u2026";
	}
}
