namespace Zing.Core.LinkPreview;

/// <summary>A new window held, hidden, until its first real document shows where it is going.</summary>
public sealed class PendingPopup(IPage page, string openerTabId, Func<string, PeekVerdict> decide)
{
	public IPage Page { get; } = page;

	public string OpenerTabId { get; } = openerTabId;

	/// <summary>Called from the page's first Navigating; returns null while the URL is not yet http(s).</summary>
	public PeekVerdict? Navigating(string to)
	{
		if (!Uri.TryCreate(to, UriKind.Absolute, out var uri)) return null;
		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
		return decide(to);
	}
}
