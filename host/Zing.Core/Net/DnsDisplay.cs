using System.Globalization;
using System.Text;

namespace Zing.Core.Net;

/// <summary>
/// Text from the system (an adapter's name) shown to the person. Control and invisible formatting characters, among them the ones that
/// reorder the text around them (right-to-left override, isolates), are spelled out as \uXXXX so what is read is what is there.
/// </summary>
public static class DnsDisplay
{
	/// <summary>A name or a line: every such character is spelled out, line breaks included.</summary>
	public static string Visible(string text) => Spell(text, _ => false);

	/// <summary>A whole script: its own line breaks and tabs stay, everything else is spelled out.</summary>
	public static string VisibleScript(string script) => Spell(script, c => c is '\n' or '\r' or '\t');

	private static string Spell(string text, Func<char, bool> keep)
	{
		var shown = new StringBuilder();
		foreach (var c in text)
		{
			if (keep(c) || !Hidden(c)) shown.Append(c);
			else shown.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
		}
		return shown.ToString();
	}

	private static bool Hidden(char c) =>
		char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
