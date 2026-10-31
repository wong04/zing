using System.Text.RegularExpressions;

namespace Zing.Core;

/// <param name="Key">What tabs are grouped by.</param>
/// <param name="Name">What the dropdown is called.</param>
public sealed record SiteGroup(string Key, string Name)
{
	private static readonly Dictionary<string, string> DocsEditors = new(StringComparer.Ordinal)
	{
		["document"] = "Google Docs",
		["spreadsheets"] = "Google Sheets",
		["presentation"] = "Google Slides",
		["forms"] = "Google Forms",
		["drawings"] = "Google Drawings",
	};

	private static readonly Dictionary<string, string> Products = new(StringComparer.Ordinal)
	{
		["mail"] = "Gmail", ["drive"] = "Google Drive", ["gemini"] = "Gemini",
		["calendar"] = "Google Calendar", ["meet"] = "Google Meet", ["maps"] = "Google Maps",
		["photos"] = "Google Photos", ["keep"] = "Google Keep", ["classroom"] = "Google Classroom",
		["translate"] = "Google Translate", ["sites"] = "Google Sites", ["chat"] = "Google Chat",
		["contacts"] = "Google Contacts", ["news"] = "Google News", ["scholar"] = "Google Scholar",
		["accounts"] = "Google Account", ["myaccount"] = "Google Account", ["notebooklm"] = "NotebookLM",
		["play"] = "Google Play", ["earth"] = "Google Earth", ["books"] = "Google Books",
		["jamboard"] = "Jamboard", ["voice"] = "Google Voice", ["groups"] = "Google Groups",
		["analytics"] = "Google Analytics", ["ads"] = "Google Ads", ["console.cloud"] = "Google Cloud",
		["cloud"] = "Google Cloud", ["colab.research"] = "Colab", ["aistudio"] = "Google AI Studio",
	};

	private static readonly Regex TwoLetters = new(@"^\p{L}{2}\z");

	/// <summary>google.com and its country domains (google.de, google.co.uk, google.com.au) and nothing else.</summary>
	public static bool IsGoogle(string domain)
	{
		if (!domain.StartsWith("google.", StringComparison.Ordinal)) return false;
		var rest = domain["google.".Length..].Split('.');
		return rest.Length switch
		{
			1 => rest[0] == "com" || TwoLetters.IsMatch(rest[0]),
			2 => rest[0] is "co" or "com" && TwoLetters.IsMatch(rest[1]),
			_ => false,
		};
	}

	/// <summary>
	/// Which dropdown a page belongs to: one per domain, except Google, where one domain is a dozen
	/// products (and Docs, Sheets and Slides share a host, so the path decides those).
	/// <paramref name="domain"/> is the host's registrable domain.
	/// </summary>
	public static SiteGroup Of(string rawHost, string path, string domain)
	{
		var host = rawHost.ToLowerInvariant();
		if (!IsGoogle(domain)) return new SiteGroup(domain, domain);
		var sub = host.EndsWith('.' + domain, StringComparison.Ordinal) ? host[..^(domain.Length + 1)] : "";
		var first = (path.Split('/').FirstOrDefault(segment => segment.Length > 0) ?? "").ToLowerInvariant();

		if (sub == "docs")
		{
			var editor = DocsEditors.GetValueOrDefault(first);
			return new SiteGroup($"docs.{domain}/{(editor is null ? "" : first)}", editor ?? "Google Docs");
		}
		if (sub is "" or "www")
		{
			return first == "maps" ? new SiteGroup($"{domain}/maps", "Google Maps") : new SiteGroup(domain, "Google");
		}
		// The product's own subdomain; an unknown one is named by its host.
		return new SiteGroup($"{sub}.{domain}", Products.GetValueOrDefault(sub) ?? host);
	}

	/// <summary>The group for a page's address, as BrowserTab.snapshot derives it.</summary>
	public static SiteGroup OfUri(Uri url) => Of(url.IdnHost, url.AbsolutePath, RegistrableDomain.Of(url.IdnHost));
}
