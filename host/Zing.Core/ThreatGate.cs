using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Zing.Core.Net;

namespace Zing.Core;

/// <summary>
/// Link inspection (X11): checks the address of each main-frame navigation before it loads and, for a flagged one, puts
/// the zing://warning page in its place. The check is local and on the address alone; it sends nothing anywhere and
/// keeps nothing about what it flagged. The hosts a person chose to visit anyway live in this object, in memory, and go
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

	/// <summary>How many warning pages can be waiting for a click; a page that only asks for warnings can't grow this.</summary>
	public const int KeysKept = 64;

	private sealed record Pending(string To, bool ViaRelay);

	private readonly object _lock = new();
	private readonly HashSet<string> _allowedHosts = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Pending> _keys = new(StringComparer.Ordinal);
	private readonly Queue<string> _keyOrder = new();

	public int KeysHeld
	{
		get
		{
			lock (_lock) return _keys.Count;
		}
	}

	/// <summary>A gate that reads the securityRealtime setting at each navigation.</summary>
	public static ThreatGate ForSettings(SettingsStore settings, Func<Uri, Uri>? inspectedAs = null) =>
		new(() => settings.Get("securityRealtime")?.GetValue<bool>() ?? throw new InvalidDataException("Setting securityRealtime is missing"), inspectedAs);

	public static string WarningUrl(string to, bool viaRelay) =>
		$"zing://{WarningHost}?to={Uri.EscapeDataString(to)}{(viaRelay ? "&via=relay" : "")}";

	/// <summary>
	/// What to do with a navigation from <paramref name="from"/> to <paramref name="to"/>: null leaves it to the rest of
	/// the browser, a redirect sends it to the warning (or, for Visit anyway, on to the address), a cancel refuses it.
	/// </summary>
	public NavigationDecision? Check(string from, string to)
	{
		if (StartPage.ZingHost(to) == VisitAnywayHost) return VisitAnyway(from, to);
		if (!enabled()) return null;
		if (Destination(to) is not { } destination) return null;
		var inspected = Inspected(destination.Address);
		if (IsAllowed(inspected)) return null;
		if (ThreatModel.InspectUrl(inspected).Level < ThreatLevel.Suspicious) return null;
		// A tab that opened on this address has only its blank starting entry behind it; Back would land there.
		var warning = WarningUrl(destination.Address.AbsoluteUri, destination.ViaRelay);
		return new NavigationDecision.Redirect(IsPage(from) ? warning : warning + "&back=home");
	}

	/// <summary>The zing://warning page, or null for any other address.</summary>
	public StartPageResponse? Serve(string url, JsonObject settings)
	{
		if (StartPage.ZingHost(url) != WarningHost) return null;
		var theme = StartPage.ThemeFor(settings);
		var to = StartPage.QueryParam(url, "to");
		var viaRelay = StartPage.QueryParam(url, "via") == "relay";
		if (to is null || Web(to) is not { } address || (viaRelay && address.Scheme != Uri.UriSchemeHttps))
			return Refusal(theme, "That isn't a web address, so there is nothing to warn about.");
		var verdict = ThreatModel.InspectUrl(Inspected(address));
		if (verdict.Level < ThreatLevel.Suspicious)
			return Refusal(theme, "Zing found nothing to warn about in this address.");
		var visit = $"zing://{VisitAnywayHost}?to={Uri.EscapeDataString(to)}&k={IssueKey(to, viaRelay)}";
		return new StartPageResponse(200, "text/html; charset=utf-8", "no-store", WarningPage.Html(theme, to, Inspected(address).IdnHost, verdict, visit, viaRelay, StartPage.QueryParam(url, "back") == "home" ? WarningPage.HomeScript : WarningPage.BackScript));
	}

	private NavigationDecision VisitAnyway(string from, string to)
	{
		var target = StartPage.QueryParam(to, "to");
		var key = StartPage.QueryParam(to, "k");
		if (target is null || key is null) return NavigationDecision.Cancel;
		// Only the click on the warning for this very address; a page can't link, redirect or script its way here.
		if (StartPage.ZingHost(from) != WarningHost || StartPage.QueryParam(from, "to") != target) return NavigationDecision.Cancel;
		if (Web(target) is not { } address) return NavigationDecision.Cancel;
		if (TakeKey(key, target) is not { } pending) return NavigationDecision.Cancel;
		var next = pending.ViaRelay ? RelayedContent.Encode(address) : address;
		if (next is null) return NavigationDecision.Cancel;
		lock (_lock) _allowedHosts.Add(HostOf(Inspected(address)));
		return new NavigationDecision.Redirect(next.AbsoluteUri);
	}

	private record Target(Uri Address, bool ViaRelay);

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

	private static string HostOf(Uri address) => address.IdnHost.ToLowerInvariant();

	private bool IsAllowed(Uri inspected)
	{
		lock (_lock) return _allowedHosts.Contains(HostOf(inspected));
	}

	private string IssueKey(string to, bool viaRelay)
	{
		var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
		lock (_lock)
		{
			_keys[key] = new Pending(to, viaRelay);
			_keyOrder.Enqueue(key);
			while (_keyOrder.Count > KeysKept) _keys.Remove(_keyOrder.Dequeue());
		}
		return key;
	}

	private Pending? TakeKey(string key, string to)
	{
		lock (_lock)
		{
			if (!_keys.TryGetValue(key, out var pending) || pending.To != to) return null;
			_keys.Remove(key);
			return pending;
		}
	}

	private static StartPageResponse Refusal(StartPageTheme theme, string message) =>
		new(400, "text/html; charset=utf-8", "no-store", WarningPage.RefusalHtml(theme, message));
}
