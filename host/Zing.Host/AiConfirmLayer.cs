using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>"AI wants to …" with Allow, Deny and Always allow on this site (#ai-confirm), under the strip of the tab in front.</summary>
public sealed class AiConfirmLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> page, Func<double> top, Action<RendererView> created, OverlayHost overlays)
	: AiBarLayer(window, scope, environment, bridge, page, top, created, overlays, "ai-confirm", OverlayLevel.AiConfirm);
