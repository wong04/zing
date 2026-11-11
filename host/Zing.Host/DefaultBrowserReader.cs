using Zing.Core;
using Zing.Core.Import;

namespace Zing.Host;

/// <summary>
/// One registry read for both import and Phase 8's default-browser offer (DefaultBrowser.CurrentHttpsProgId).
/// E2e runs never read the registry: ZING_DEFAULT_BROWSER stands in, and unset means no default browser.
/// </summary>
public sealed class DefaultBrowserReader : IDefaultBrowserReader
{
	public string? HttpsProgId() => DefaultBrowser.CurrentHttpsProgId();
}
