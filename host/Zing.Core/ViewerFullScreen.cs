namespace Zing.Core;

/// <summary>
/// Whether the engine's PDF viewer of one page is fullscreen. The viewer draws in a guest view (a DevTools "webview" target
/// whose parent is the page's target), not in a frame of the page, so its own fullscreen button makes the guest's document
/// fullscreen and the page's ContainsFullScreenElement never says so. The host attaches to each such guest and installs
/// <see cref="Script"/>, which reports every change through the <see cref="Binding"/> binding.
/// </summary>
public sealed class ViewerFullScreen
{
	public const string Binding = "zingViewerFullScreen";

	/// <summary>
	/// Reports the guest's fullscreen; once per document, however often it is installed. A busy viewer can run it after its
	/// button was already pressed, so a fullscreen it finds is reported at once.
	/// </summary>
	public const string Script = """
		(() => {
			if (globalThis.__zingViewerFullScreen) return;
			globalThis.__zingViewerFullScreen = true;
			const report = () => zingViewerFullScreen(String(document.fullscreenElement !== null));
			document.addEventListener('fullscreenchange', report);
			if (document.fullscreenElement !== null) report();
		})()
		""";

	public const string ExitScript = "document.fullscreenElement !== null && void document.exitFullscreen()";

	private readonly Dictionary<string, bool> _guests = [];
	private readonly HashSet<string> _targets = [];
	private string? _page;

	public bool KnowsPage => _page is not null;

	public bool IsFullScreen => _guests.ContainsValue(true);

	/// <summary>The DevTools sessions of the guests that are fullscreen now.</summary>
	public IReadOnlyList<string> FullScreenSessions => [.. _guests.Where(g => g.Value).Select(g => g.Key)];

	/// <summary>The page's own target id; guests are recognised by it.</summary>
	public void PageIs(string targetId) => _page = targetId;

	/// <summary>Whether a target is a viewer guest of this page not yet attached to: never another page's, nor the page's frames.</summary>
	public bool IsNewGuest(string targetId, string type, string? parentId) => _page is not null && type == "webview" && parentId == _page && !_targets.Contains(targetId);

	public void Attached(string targetId, string sessionId)
	{
		_targets.Add(targetId);
		_guests[sessionId] = false;
	}

	/// <summary>A guest's script reported its fullscreen. True when the page's fullscreen changed.</summary>
	public bool Reported(string sessionId, string name, string payload)
	{
		if (name != Binding || !_guests.ContainsKey(sessionId)) return false;
		var fullScreen = payload switch
		{
			"true" => true,
			"false" => false,
			_ => throw new ArgumentException($"Unexpected viewer fullscreen report '{payload}'", nameof(payload)),
		};
		var before = IsFullScreen;
		_guests[sessionId] = fullScreen;
		return before != IsFullScreen;
	}

	/// <summary>The page started a new document, and its guests went with the last one. True when the page's fullscreen changed.</summary>
	public bool Left()
	{
		var before = IsFullScreen;
		_guests.Clear();
		_targets.Clear();
		return before;
	}
}
