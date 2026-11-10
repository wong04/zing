using System.Globalization;

namespace Zing.Core;

/// <summary>
/// The registrable domain by the full Public Suffix List (Defaults/public_suffix_list.dat, from publicsuffix.org, ICANN and
/// private sections), as Chrome's password manager matches sites. <see cref="Zing.Core.RegistrableDomain"/>'s short list
/// is enough for grouping tabs, not for deciding whose sign-in a username belongs to: it makes alice.github.io and
/// bob.github.io one site.
/// </summary>
public static class PublicSuffix
{
	private sealed record Rules(HashSet<string> Plain, HashSet<string> Wildcards, HashSet<string> Exceptions);

	private static readonly Lazy<Rules> List = new(Load);
	private static readonly IdnMapping Idn = new();

	/// <summary>
	/// The host's registrable domain in ASCII, or null when the host is itself a public suffix (or has an empty label).
	/// An IP address is its own site.
	/// </summary>
	public static string? RegistrableDomain(string rawHost)
	{
		var host = rawHost.ToLowerInvariant();
		if (host.EndsWith('.')) host = host[..^1];
		if (host.StartsWith('[') || Zing.Core.RegistrableDomain.IsRawIPv4(host)) return host;
		if (host.Length == 0 || host.StartsWith('.')) return null;
		var labels = Idn.GetAscii(host).Split('.');
		if (labels.Any(l => l.Length == 0)) return null;
		var suffixStart = SuffixStart(labels, List.Value);
		return suffixStart == 0 ? null : string.Join('.', labels[(suffixStart - 1)..]);
	}

	/// <summary>Where the public suffix starts: an exception rule wins, then the longest rule, then the implicit "*".</summary>
	private static int SuffixStart(string[] labels, Rules rules)
	{
		for (var i = 0; i < labels.Length; i++)
		{
			var candidate = string.Join('.', labels[i..]);
			if (rules.Exceptions.Contains(candidate)) return i + 1;
			if (rules.Plain.Contains(candidate)) return i;
			if (i + 1 < labels.Length && rules.Wildcards.Contains(string.Join('.', labels[(i + 1)..]))) return i;
		}
		return labels.Length - 1;
	}

	private static Rules Load()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Defaults", "public_suffix_list.dat");
		var rules = new Rules(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
		foreach (var raw in File.ReadLines(path))
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
			var rule = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
			if (rule.StartsWith('!')) rules.Exceptions.Add(Idn.GetAscii(rule[1..]));
			else if (rule.StartsWith("*.", StringComparison.Ordinal)) rules.Wildcards.Add(Idn.GetAscii(rule[2..]));
			else rules.Plain.Add(Idn.GetAscii(rule));
		}
		if (rules.Plain.Count == 0) throw new InvalidDataException($"{path} has no rules");
		return rules;
	}
}
