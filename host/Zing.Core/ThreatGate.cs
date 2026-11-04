using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Zing.Core.Net;

namespace Zing.Core;

/// <summary>
/// Link inspection (X11): checks the address of each main-frame navigation before it loads and, for a flagged one, puts
/// the zing://warning page in its place. The check is local and on the address alone; it sends nothing anywhere and
/// keeps nothing about what it flagged. The addresses a person chose to visit anyway live in this object, in memory, and go
/// with the process.
/// </summary>
/// <param name="enabled">Read at each navigation, so turning the setting off needs no restart.</param>
/// <param name="inspectedAs">
/// Test-only (an e2e run): the address to inspect in place of the one requested, so a local test server can stand for a
/// flagged site. Null in a real run.
/// </param>
public sealed class ThreatGate(Func<bool> enabled, Func<Uri, Uri>? inspectedAs = null)
{
	public const string WarningHost = "warning";
	public const string VisitAnywayHost = "visit-anyway";

	/// <summary>How many tabs' warnings are kept; one is dropped only when this many other tabs have warned since.</summary>
	public const int TabsKept = 512;

	/// <summary>
	/// A warning the host made for a tab. What the page says (a relay, an empty history) and the key Visit anyway needs live
	/// here, set by the host alone: nothing in a page's query is believed.
	/// </summary>
	private sealed record Warning(string Id, string Key, string To, bool ViaRelay, bool BackHome);

	private sealed record Target(Uri Address, bool ViaRelay);

	private readonly object _lock = new();
	private readonly HashSet<string> _allowedOrigins = new(StringComparer.Ordinal);
	private readonly HashSet<string> _allowedAddresses = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Warning> _warnings = new(StringComparer.Ordinal);
	private readonly Queue<string> _warnedTabs = new();

	/// <summary>How many warnings are held: one per tab at most.</summary>
	public int KeysHeld
	{
		get
		{
			lock (_lock) return _warnings.Count;
		}
	}

	/// <summary>A gate that reads the securityRealtime setting at each navigation.</summary>
	public static ThreatGate ForSettings(SettingsStore settings, Func<Uri, Uri>? inspectedAs = null) =>
		new(() => settings.Get("securityRealtime")?.GetValue<bool>() ?? throw new InvalidDataException("Setting securityRealtime is missing"), inspectedAs);

	/// <summary>The address a zing://warning page is for, so a session or pinned tab keeps that and not the warning; null for any other address.</summary>
	public static string? WarningTarget(string url)
	{
		if (StartPage.ZingHost(url) != WarningHost || StartPage.QueryParam(url, "to") is not { } to) return null;
		return Web(to)?.AbsoluteUri;
	}

	/// <summary>Whether a request is for the document of a navigation that was sent to a warning: the engine still sends it, and it is refused.</summary>
	public static bool IsWithheld(string? warnedDocument, string request, bool isDocument) =>
		isDocument && warnedDocument is not null && RelayedNavigation.Same(warnedDocument, request);

	/// <summary>
	/// What to do with a navigation of <paramref name="tab"/> from <paramref name="from"/> to <paramref name="to"/>: null
	/// leaves it to the rest of the browser, a redirect sends it to the warning (or, for Visit anyway, on to the address),
	/// a cancel refuses it.
	/// </summary>
	public NavigationDecision? Check(string tab, string from, string to)
	{
		var host = StartPage.ZingHost(to);
		if (host == VisitAnywayHost) return VisitAnyway(tab, from, to);
		if (host == WarningHost) return Revisit(tab, from, to);
		if (!enabled()) return null;
		if (Destination(to) is not { } destination) return null;
		if (!NeedsWarning(destination.Address)) return null;
		return Warn(tab, from, destination.Address, destination.ViaRelay);
	}

	private bool NeedsWarning(Uri address)
	{
		var inspected = Inspected(address);
		if (ThreatModel.InspectUrl(inspected).Level < ThreatLevel.Suspicious) return false;
		lock (_lock)
		{
			if (_allowedAddresses.Contains(inspected.AbsoluteUri)) return false;
			// A name in front of the host is judged on every link, whatever was allowed before.
			return inspected.UserInfo.Length > 0 || !_allowedOrigins.Contains(OriginOf(inspected));
		}
	}

	/// <summary>
	/// A navigation to a zing://warning address. Our own landing on a warning is left to be served; any other (a page's link, a
	/// session restored, Back to a warning that has since been replaced) becomes a new warning of the host's own making.
	/// </summary>
	private NavigationDecision? Revisit(string tab, string from, string to)
	{
		var id = StartPage.QueryParam(to, "w");
		var target = StartPage.QueryParam(to, "to");
		lock (_lock)
		{
			if (_warnings.TryGetValue(tab, out var live) && live.Id == id && live.To == target) return null;
		}
		if (target is null || Web(target) is not { } address) return null;
		if (ThreatModel.InspectUrl(Inspected(address)).Level < ThreatLevel.Suspicious) return null;
		return Warn(tab, from, address, false);
	}

	private NavigationDecision.Redirect Warn(string tab, string from, Uri address, bool viaRelay)
	{
		var warning = new Warning(Token(), Token(), address.AbsoluteUri, viaRelay, !IsPage(from));
		lock (_lock)
		{
			if (!_warnings.ContainsKey(tab))
			{
				_warnedTabs.Enqueue(tab);
				while (_warnedTabs.Count >= TabsKept) _warnings.Remove(_warnedTabs.Dequeue());
			}
			_warnings[tab] = warning;
		}
		return new NavigationDecision.Redirect($"zing://{WarningHost}?to={Uri.EscapeDataString(warning.To)}&w={warning.Id}");
	}

	private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

	/// <summary>The zing://warning page the host made, or null for any other address. A warning nobody made is refused.</summary>
	public StartPageResponse? Serve(string url, JsonObject settings)
	{
		if (StartPage.ZingHost(url) != WarningHost) return null;
		var theme = StartPage.ThemeFor(settings);
		var id = StartPage.QueryParam(url, "w");
		var to = StartPage.QueryParam(url, "to");
		Warning? warning;
		lock (_lock) warning = _warnings.Values.FirstOrDefault(w => w.Id == id && w.To == to);
		if (warning is null || Web(warning.To) is not { } address)
			return Refusal(theme, "There is nothing to warn about here.");
		var verdict = ThreatModel.InspectUrl(Inspected(address));
		if (verdict.Level < ThreatLevel.Suspicious)
			return Refusal(theme, "Zing found nothing to warn about in this address.");
		var visit = $"zing://{VisitAnywayHost}?to={Uri.EscapeDataString(warning.To)}&k={warning.Key}";
		var script = warning.BackHome ? WarningPage.HomeScript : WarningPage.BackScript;
		return new StartPageResponse(200, "text/html; charset=utf-8", "no-store", WarningPage.Html(theme, warning.To, Inspected(address).IdnHost, verdict, visit, warning.ViaRelay, script));
	}

	private NavigationDecision VisitAnyway(string tab, string from, string to)
	{
		var target = StartPage.QueryParam(to, "to");
		var key = StartPage.QueryParam(to, "k");
		if (target is null || key is null) return NavigationDecision.Cancel;
		// Only the click on the warning for this very address; a page can't link, redirect or script its way here.
		if (StartPage.ZingHost(from) != WarningHost || StartPage.QueryParam(from, "to") != target) return NavigationDecision.Cancel;
		if (Web(target) is not { } address) return NavigationDecision.Cancel;
		if (Take(tab, key, target) is not { } warning) return NavigationDecision.Cancel;
		var next = warning.ViaRelay ? RelayedContent.Encode(address) : address;
		if (next is null) return NavigationDecision.Cancel;
		var inspected = Inspected(address);
		lock (_lock)
		{
			// A link with a name in front of its host is allowed as that link alone, never for the host.
			if (inspected.UserInfo.Length > 0) _allowedAddresses.Add(inspected.AbsoluteUri);
			else _allowedOrigins.Add(OriginOf(inspected));
		}
		return new NavigationDecision.Redirect(next.AbsoluteUri);
	}

	private static Target? Destination(string to)
	{
		if (RelayedContent.IsRelayUrl(to)) return RelayedContent.Decode(to) is { } real ? new Target(real, true) : null;
		return Web(to) is { } web ? new Target(web, false) : null;
	}

	private static bool IsPage(string address) => Web(address) is not null || StartPage.ZingHost(address) is not null;

	private static Uri? Web(string text)
	{
		if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
		return uri.Scheme is "http" or "https" ? uri : null;
	}

	private Uri Inspected(Uri address) => inspectedAs?.Invoke(address) ?? address;

	private static string OriginOf(Uri address) => $"{address.Scheme}://{address.IdnHost.ToLowerInvariant()}:{address.Port}";

	private Warning? Take(string tab, string key, string to)
	{
		lock (_lock)
		{
			if (!_warnings.TryGetValue(tab, out var warning) || warning.Key != key || warning.To != to) return null;
			_warnings.Remove(tab);
			return warning;
		}
	}

	private static StartPageResponse Refusal(StartPageTheme theme, string message) =>
		new(400, "text/html; charset=utf-8", "no-store", WarningPage.RefusalHtml(theme, message));
}
