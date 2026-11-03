namespace Zing.Core.Net;

/// <summary>An encrypted DNS service and where Windows reaches it: the servers it answers on and the address it speaks HTTPS at.</summary>
public sealed record DnsProvider(string Name, IReadOnlyList<string> V4, IReadOnlyList<string> V6, string Template)
{
	public IEnumerable<string> Servers => V4.Concat(V6);
}

/// <summary>The fixed list, keyed by the dohProvider setting's values. Nothing outside it can be applied.</summary>
public static class DnsProviders
{
	private static readonly Dictionary<string, DnsProvider> All = new()
	{
		["cloudflare"] = new("Cloudflare", ["1.1.1.1", "1.0.0.1"], ["2606:4700:4700::1111", "2606:4700:4700::1001"], "https://cloudflare-dns.com/dns-query"),
		["cloudflare-security"] = new("Cloudflare (malware blocking)", ["1.1.1.2", "1.0.0.2"], ["2606:4700:4700::1112", "2606:4700:4700::1002"], "https://security.cloudflare-dns.com/dns-query"),
		["google"] = new("Google", ["8.8.8.8", "8.8.4.4"], ["2001:4860:4860::8888", "2001:4860:4860::8844"], "https://dns.google/dns-query"),
		["quad9"] = new("Quad9", ["9.9.9.9", "149.112.112.112"], ["2620:fe::fe", "2620:fe::9"], "https://dns.quad9.net/dns-query"),
	};

	public static DnsProvider Get(string key) => All.TryGetValue(key, out var provider) ? provider : throw new UnknownDohProviderError(key);
}
