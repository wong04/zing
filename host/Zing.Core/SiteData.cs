namespace Zing.Core;

public sealed record SiteToPurge(string Domain, IReadOnlyList<string> Origins);

/// <summary>
/// Which sites each tab has touched (site-usage.ts). A site's data is purged
/// when the last tab that touched it goes, keyed by registrable domain so
/// mail.x.com and docs.x.com are one site, as everywhere else in the app.
/// </summary>
public sealed class SiteUsage
{
	private readonly Dictionary<string, HashSet<string>> _domainsByTab = new(StringComparer.Ordinal);
	private readonly Dictionary<string, HashSet<string>> _originsByDomain = new(StringComparer.Ordinal);

	public void Visit(string tabKey, string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
		var domain = RegistrableDomain.Of(uri.IdnHost);
		if (!_domainsByTab.TryGetValue(tabKey, out var domains)) _domainsByTab[tabKey] = domains = new HashSet<string>(StringComparer.Ordinal);
		domains.Add(domain);
		if (!_originsByDomain.TryGetValue(domain, out var origins)) _originsByDomain[domain] = origins = new HashSet<string>(StringComparer.Ordinal);
		origins.Add(uri.GetLeftPart(UriPartial.Authority));
	}

	/// <summary>Forgets the tab and returns the sites that no remaining tab is using.</summary>
	public List<SiteToPurge> Close(string tabKey)
	{
		if (!_domainsByTab.Remove(tabKey, out var domains)) return [];
		var stillOpen = _domainsByTab.Values.SelectMany(d => d).ToHashSet(StringComparer.Ordinal);
		var gone = new List<SiteToPurge>();
		foreach (var domain in domains)
		{
			if (stillOpen.Contains(domain)) continue;
			_originsByDomain.Remove(domain, out var origins);
			gone.Add(new SiteToPurge(domain, origins?.ToList() ?? []));
		}
		return gone;
	}
}

/// <summary>Clears one site's storage and cookies in the browsing profile. The host does it through the engine.</summary>
public interface ISitePurger
{
	Task PurgeAsync(SiteToPurge site);
}

/// <summary>
/// Keeps nothing (privacy.ts): the profile is in memory, and on top of that a
/// site's data is purged the moment the last tab that touched it closes. The
/// only thing remembered is which sites open tabs have used, in memory. Shared
/// by every window, since they share one profile.
/// </summary>
public sealed class SiteDataKeeper(ISitePurger purger, Action<string, Exception> failed)
{
	private readonly SiteUsage _usage = new();

	public void Visit(string tabKey, string url) => _usage.Visit(tabKey, url);

	/// <summary>The tab is gone for good: purge whatever sites no other open tab is using. Call after its page has closed.</summary>
	public void Release(string tabKey)
	{
		foreach (var site in _usage.Close(tabKey)) _ = PurgeAsync(site);
	}

	private async Task PurgeAsync(SiteToPurge site)
	{
		try
		{
			await purger.PurgeAsync(site);
		}
		catch (Exception error)
		{
			failed(site.Domain, error);
		}
	}
}
