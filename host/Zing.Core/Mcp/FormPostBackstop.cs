namespace Zing.Core.Mcp;

/// <summary>
/// The spec's backstop ("Never reachable"): a top-level form POST that starts within <see cref="Window"/> of an AI action on
/// the same tab is held for the owner's yes on sites that ask, whatever the button said. XHR and fetch are never held. An
/// action the owner already allowed lets its POST go; each action holds at most one POST.
/// </summary>
public sealed class FormPostBackstop(SitePolicy policy, Func<DateTime> now)
{
	public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

	private readonly Dictionary<TabRef, (DateTime At, bool Confirmed)> _last = [];

	/// <summary>An AI action is about to run on the tab; <paramref name="confirmed"/> when the owner allowed it on the bar.</summary>
	public void Acted(TabRef tab, bool confirmed) => _last[tab] = (now(), confirmed);

	public void Forget(TabRef tab) => _last.Remove(tab);

	/// <summary>Whether a request the tab starts for a document on <paramref name="site"/> waits for the owner.</summary>
	public bool Holds(TabRef tab, Uri site, string method, bool mainFrame)
	{
		if (!mainFrame || !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
		if (!_last.Remove(tab, out var last)) return false;
		if (now() - last.At > Window || last.Confirmed) return false;
		return policy.LevelFor(site) != SiteLevel.Never;
	}
}
