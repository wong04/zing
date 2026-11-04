namespace Zing.Core;

/// <summary>How serious a finding is, ordered so a verdict's level is the worst thing in it (src/shared/security/threatModel.ts).</summary>
public enum ThreatLevel
{
	Clean = 0,
	Notice = 1,
	Suspicious = 2,
	Dangerous = 3,
}

/// <summary>One thing noticed about an address, with the reason spelled out.</summary>
public sealed record ThreatFinding(ThreatLevel Level, string Title, string Detail);

public sealed record ThreatVerdict(string Subject, IReadOnlyList<ThreatFinding> Findings)
{
	public ThreatLevel Level => Findings.Count == 0 ? ThreatLevel.Clean : Findings.Max(f => f.Level);
}

/// <summary>
/// The address half of src/shared/security/threatModel.ts: pure pattern-matching over an address, nothing sent anywhere
/// and nothing kept. The TS is the spec; threatModel.vectors.json holds the two to the same cases.
/// </summary>
public static class ThreatModel
{
	private static readonly HashSet<string> HighRiskTlds = new(StringComparer.Ordinal)
	{
		"zip", "mov", "tk", "ml", "ga", "cf", "gq", "top", "xyz", "gdn", "work", "click", "link",
		"country", "kim", "science", "party", "review",
	};

	private static readonly string[] CommonlyImpersonated =
	[
		"apple", "icloud", "paypal", "amazon", "microsoft", "office365",
		"google", "gmail", "facebook", "instagram", "netflix", "coinbase",
		"binance", "metamask", "dhl", "fedex", "usps", "hmrc", "irs",
		"chase", "wellsfargo", "barclays", "santander", "steam", "discord",
	];

	private static readonly HashSet<string> PhishWords = new(StringComparer.Ordinal)
	{
		"login", "signin", "verify", "verification", "secure", "security",
		"account", "update", "confirm", "recovery", "unlock", "billing",
		"wallet", "authenticate", "support",
	};

	private static readonly IReadOnlyList<ThreatFinding> None = [];

	public static ThreatVerdict InspectUrl(Uri url)
	{
		// IdnHost is the ASCII form (xn--…) the TS gets from URL.hostname.
		var host = url.HostNameType == UriHostNameType.Dns ? url.IdnHost.ToLowerInvariant() : url.Host.ToLowerInvariant();
		var findings = new List<ThreatFinding>();
		if (url.UserInfo.Length > 0)
		{
			findings.Add(new ThreatFinding(
				ThreatLevel.Dangerous,
				"Link disguises its real destination",
				"This URL puts a username before the host, so the part that "
				+ "looks like the site name isn't where it goes. It actually "
				+ $"connects to {(host.Length == 0 ? "an unnamed host" : host)}."));
		}
		if (host.Length > 0) findings.AddRange(InspectHost(host, url.Scheme.ToLowerInvariant()));
		return new ThreatVerdict(url.AbsoluteUri, findings);
	}

	public static IReadOnlyList<ThreatFinding> InspectHost(string rawHost, string scheme)
	{
		var host = rawHost.ToLowerInvariant();
		var labels = RegistrableDomain.LabelsOf(host);
		var findings = new List<ThreatFinding>(0);

		if (host.Contains("xn--", StringComparison.Ordinal))
		{
			findings.Add(new ThreatFinding(
				ThreatLevel.Suspicious,
				"Address uses look-alike characters",
				$"{host} is written with non-Latin characters that can be "
				+ "made to look identical to a familiar name. Check it letter "
				+ "by letter before entering anything."));
		}

		if (RegistrableDomain.IsRawIPv4(host) && !IsLocalNetworkIPv4(labels))
		{
			findings.Add(new ThreatFinding(
				scheme == "https" ? ThreatLevel.Notice : ThreatLevel.Suspicious,
				"Connects to a raw IP address",
				"There's no domain name here, so there's nothing to check a "
				+ "certificate against and no owner to look up."));
		}

		if (labels.Length > 0 && HighRiskTlds.Contains(labels[^1])) findings.Add(HighRiskEnding(labels[^1]));

		if (labels.Length >= 6)
		{
			findings.Add(new ThreatFinding(
				ThreatLevel.Suspicious,
				"Unusually deep subdomain",
				$"{labels.Length} levels deep. This is often used to bury the "
				+ "real domain past the edge of the address bar."));
		}

		// The registrable domain is only worked out for a host that has a brand name in it.
		string? registrable = null;
		foreach (var name in CommonlyImpersonated)
		{
			if (!HasLabel(host, name)) continue;
			registrable ??= RegistrableDomain.Of(host);
			if (HasLabel(registrable, name)) continue;
			findings.Add(new ThreatFinding(
				ThreatLevel.Dangerous,
				$"Impersonates {char.ToUpperInvariant(name[0])}{name[1..]}",
				$"\"{name}\" appears in this address, but the site is actually "
				+ $"{registrable} — not {name}. This is the standard shape of a "
				+ "credential-stealing page."));
			break;
		}

		var phishHits = PhishHits(host);
		if (phishHits.Count >= 2 && findings.All(f => f.Level < ThreatLevel.Dangerous))
		{
			phishHits.Sort(StringComparer.Ordinal);
			findings.Add(new ThreatFinding(
				ThreatLevel.Suspicious,
				"Hostname reads like a sign-in trap",
				$"Contains {string.Join(", ", phishHits)}. Real "
				+ "services put sign-in on their own domain, not in the hostname."));
		}

		return findings.Count == 0 ? None : findings;
	}

	private static ThreatFinding HighRiskEnding(string tld)
	{
		var collides = tld is "zip" or "mov";
		return new ThreatFinding(
			ThreatLevel.Suspicious,
			$"High-risk domain ending (.{tld})",
			collides
				? $"The .{tld} ending is also a file extension, so text that looks "
					+ "like a filename becomes a link to somebody's server."
				: "This ending is used disproportionately for phishing and "
					+ "malware because registrations are free or unverified.");
	}

	/// <summary>Loopback, private (RFC 1918) and link-local addresses: a router page or a dev server, never a payload host.</summary>
	private static bool IsLocalNetworkIPv4(string[] labels)
	{
		var first = int.Parse(labels[0].TrimStart('+'), System.Globalization.CultureInfo.InvariantCulture);
		var second = int.Parse(labels[1].TrimStart('+'), System.Globalization.CultureInfo.InvariantCulture);
		return first is 127 or 10 || (first == 192 && second == 168) || (first == 169 && second == 254) || (first == 172 && second is >= 16 and <= 31);
	}

	/// <summary>Whether <paramref name="word"/> stands alone as a label of the host, bounded by a dot or a hyphen.</summary>
	private static bool HasLabel(string text, string word)
	{
		for (var from = 0; from < text.Length;)
		{
			var at = text.IndexOf(word, from, StringComparison.Ordinal);
			if (at < 0) return false;
			var end = at + word.Length;
			if ((at == 0 || text[at - 1] is '.' or '-') && (end == text.Length || text[end] is '.' or '-')) return true;
			from = at + 1;
		}
		return false;
	}

	/// <summary>The distinct security words in the host, where words are the runs of letters.</summary>
	private static List<string> PhishHits(string host)
	{
		var hits = new List<string>(0);
		var lookup = PhishWords.GetAlternateLookup<ReadOnlySpan<char>>();
		var start = -1;
		for (var i = 0; i <= host.Length; i++)
		{
			var letter = i < host.Length && char.IsLetter(host[i]);
			if (letter && start < 0) start = i;
			if (letter || start < 0) continue;
			if (lookup.TryGetValue(host.AsSpan(start, i - start), out var word) && !hits.Contains(word)) hits.Add(word);
			start = -1;
		}
		return hits;
	}
}
