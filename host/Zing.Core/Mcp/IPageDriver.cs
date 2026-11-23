using System.Text.Json;

namespace Zing.Core.Mcp;

public enum FillResult { Filled, Denied, NoSavedLogin }

/// <summary>
/// What the AI tools do to a page. Nodes are CDP backend node ids. The host's driver throws <see cref="TabGoneException"/>,
/// <see cref="DialogOpenException"/>, <see cref="NotVisibleException"/> or <see cref="CdpTimeoutException"/> when it can't.
/// </summary>
public interface IPageDriver
{
	Task<JsonElement> AxTree(TabRef tab);
	Task<string> MainText(TabRef tab);
	/// <summary>A PNG of the visible page, or of <paramref name="node"/> only, with <paramref name="blackOut"/> painted over.</summary>
	Task<byte[]> Screenshot(TabRef tab, long? node, IReadOnlySet<long> blackOut);
	/// <summary>The node the page hit-tests at the centre of <paramref name="node"/>'s box, or null when there is none.</summary>
	Task<long?> NodeAtCentre(TabRef tab, long node);
	Task Click(TabRef tab, long node);
	Task Type(TabRef tab, long node, string text, bool submit);
	Task Select(TabRef tab, long node, string value);
	Task Scroll(TabRef tab, long? node, int deltaX, int deltaY);
	Task PressKey(TabRef tab, long? node, string key);
	Task Navigate(TabRef tab, Uri url);
	Task Back(TabRef tab);
	Task Forward(TabRef tab);
	Task<TabRef> OpenTab(Uri url);
	Task CloseTab(TabRef tab);
	Task Download(TabRef tab, Uri url);
	Task<(FillResult Result, IReadOnlySet<long> FilledNodes)> FillLogin(TabRef tab);
	Uri UrlOf(TabRef tab);
}

public sealed class TabGoneException(TabRef tab) : InvalidOperationException($"Tab {tab.TabId} in window {tab.WindowId} is gone.");

public sealed class DialogOpenException(TabRef tab) : InvalidOperationException($"Tab {tab.TabId} has a JavaScript dialog open.");

public sealed class NotVisibleException(TabRef tab) : InvalidOperationException($"Tab {tab.TabId} was not shown in time.");

public sealed class CdpTimeoutException(string method) : TimeoutException($"CDP call {method} timed out.");
