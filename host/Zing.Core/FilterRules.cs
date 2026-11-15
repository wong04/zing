namespace Zing.Core;

/// <summary>
/// The domain rules of Adblock Plus-syntax filter lists (EasyList, EasyPrivacy), which blockTrackers refuses alongside
/// <see cref="TrackerRules"/>. Only <c>||host^</c> rules are read, with no options or <c>$third-party</c>: most of both
/// lists' network rules, and the ones a host lookup can answer. Path, type and per-site rules, and cosmetic (element
/// hiding) rules, are not read. A host with any <c>@@||host^</c> exception is never blocked, whatever the exception's
/// options: an exception Zing cannot read in full must not break the site it was written for.
/// </summary>
public sealed class FilterRules
{
	[Flags]
	private enum Kind : byte
	{
		Always = 1,
		ThirdParty = 2,
		Exception = 4,
	}

	private readonly Dictionary<string, Kind>.AlternateLookup<ReadOnlySpan<char>> _hosts;

	public int Count { get; }

	private FilterRules(Dictionary<string, Kind> hosts)
	{
		_hosts = hosts.GetAlternateLookup<ReadOnlySpan<char>>();
		Count = hosts.Count;
	}

	/// <summary>EasyList and EasyPrivacy as bundled in Defaults, read once, on first use.</summary>
	public static FilterRules Bundled => BundledRules.Value;

	private static readonly string[] BundledLists = ["easylist.txt", "easyprivacy.txt"];

	private static readonly Lazy<FilterRules> BundledRules = new(() => Parse(BundledLists.SelectMany(name => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Defaults", name)))));

	public static FilterRules Parse(IEnumerable<string> lines)
	{
		var hosts = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
		foreach (var line in lines)
		{
			if (KindOf(line.AsSpan().Trim(), out var host) is not { } kind) continue;
			hosts[host] = hosts.GetValueOrDefault(host) | kind;
		}
		if (hosts.Count == 0) throw new InvalidDataException("The filter lists have no domain rules");
		return new FilterRules(hosts);
	}

	/// <summary>A <c>||host^</c> or <c>@@||host^</c> rule's host and what it does; null for any other line. Read by hand: the lists are 135,000 lines.</summary>
	private static Kind? KindOf(ReadOnlySpan<char> line, out string host)
	{
		host = "";
		var exception = line.StartsWith("@@");
		if (exception) line = line[2..];
		if (!line.StartsWith("||")) return null;
		line = line[2..];
		var end = line.IndexOf('^');
		if (end <= 0 || line[..end].ContainsAnyExcept(HostChars)) return null;
		var rest = line[(end + 1)..];
		Kind? kind = exception ? (OnlyHidesElements(rest) ? null : Kind.Exception)
			: rest.IsEmpty ? Kind.Always
			: rest.Equals("$third-party", StringComparison.OrdinalIgnoreCase) || rest.Equals("$3p", StringComparison.OrdinalIgnoreCase) ? Kind.ThirdParty
			: null;
		if (kind is not null) host = line[..end].ToString();
		return kind;
	}

	private static readonly System.Buffers.SearchValues<char> HostChars = System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-");

	/// <summary>An exception that only turns off element hiding leaves network blocking as it is.</summary>
	private static bool OnlyHidesElements(ReadOnlySpan<char> rest)
	{
		if (!rest.StartsWith('$')) return false;
		foreach (var option in rest[1..].ToString().ToLowerInvariant().Split(','))
			if (option is not ("elemhide" or "ehide" or "generichide" or "ghide" or "specifichide" or "shide")) return false;
		return true;
	}

	/// <summary>
	/// Whether the lists refuse a web request to <paramref name="requestHost"/> from the page at <paramref name="pageUrl"/>;
	/// a page with no web address makes every request third-party. Asked for every request a page makes, on the UI thread,
	/// so a host the lists don't name is one lookup per label and no allocation.
	/// </summary>
	public bool Blocks(string requestHost, string pageUrl)
	{
		var found = Lookup(requestHost);
		if ((found & Kind.Exception) != 0) return false;
		if ((found & Kind.Always) != 0) return true;
		if ((found & Kind.ThirdParty) == 0) return false;
		return WebHostOf(pageUrl) is not { } page || RegistrableDomain.Of(requestHost) != RegistrableDomain.Of(page);
	}

	/// <summary>The lists block the host from some page: the cheap first check, which needs no page address.</summary>
	public bool Names(string requestHost)
	{
		var found = Lookup(requestHost);
		return (found & Kind.Exception) == 0 && (found & (Kind.Always | Kind.ThirdParty)) != 0;
	}

	/// <summary>The rules for the host and every domain above it.</summary>
	private Kind Lookup(string rawHost)
	{
		var host = rawHost.AsSpan();
		if (host.EndsWith('.')) host = host[..^1];
		Kind found = 0;
		for (;;)
		{
			if (_hosts.TryGetValue(host, out var kind)) found |= kind;
			var dot = host.IndexOf('.');
			if (dot < 0) return found;
			host = host[(dot + 1)..];
		}
	}

	private static string? WebHostOf(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
		return parsed.Scheme is "http" or "https" ? parsed.IdnHost : null;
	}
}
