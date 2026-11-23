using Microsoft.Web.WebView2.Core;
using Zing.Core;

namespace Zing.Host;

/// <summary>"AI is using this tab" with Stop (#ai-strip), at the top of a shared tab's page while it is in front.</summary>
public sealed class AiStripLayer(HostWindow window, WindowScope scope, CoreWebView2Environment environment, Bridge bridge, Func<Rect> page, Action<RendererView> created, OverlayHost overlays)
	: AiBarLayer(window, scope, environment, bridge, page, () => page().Y, created, overlays, "ai-strip", OverlayLevel.AiStrip);
