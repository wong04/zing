using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>
/// One window's permission prompts: the engine's requests go to Zing's permission model, which asks
/// in the page UI and answers through the request's deferral.
/// </summary>
public sealed class PermissionHost(Func<ChromeApi?> api)
{
	public void Requested(WebView2Page page, CoreWebView2PermissionRequestedEventArgs e)
	{
		if (api() is not { } chrome)
		{
			// No window API yet, so no one to ask: refused, never left to Edge's own prompt.
			e.State = CoreWebView2PermissionState.Deny;
			TestHooks.Record("permission", new InvalidOperationException($"A permission request came before the window's API ({e.PermissionKind})"));
			return;
		}
		var (permission, details) = PermissionDeferral.Describe(e);
		chrome.Permissions.Request(page, page.Url, permission, details, new PermissionDeferral(e, e.GetDeferral()));
	}

	/// <summary>The page went to a new document or closed: its session-only grants and queued prompts go.</summary>
	public void Left(WebView2Page page) => api()?.Permissions.DropFor(page);
}
