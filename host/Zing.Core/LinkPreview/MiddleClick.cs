using System.Text.Json;

namespace Zing.Core.LinkPreview;

/// <summary>
/// Telling a middle click on a link from a plain new-window request. Chromium opens the link on the button's release
/// (auxclick), so the button is already up when NewWindowRequested arrives; a page script reports the press instead.
/// A page can post the same message, and all it gains is that its next window opens as a background tab, as before previews.
/// </summary>
public static class MiddleClick
{
	public const string MessageKey = "zingMiddleClick";

	/// <summary>How long after the press a new-window request still counts as made by it.</summary>
	public const long WindowMs = 1000;

	/// <summary>The longest message read at all; the script's is under 30 characters.</summary>
	private const int MaxLength = 64;

	/// <summary>Added to every document; the top frame only, since a frame's messages reach nothing the host listens to.</summary>
	public static readonly string Script =
		"(() => { if (window.top !== window || !window.chrome || !window.chrome.webview) return; "
		+ "addEventListener('mousedown', (e) => { "
		+ "if (e.button !== 1 || !e.isTrusted || !(e.target instanceof Element) || !e.target.closest('a[href]')) return; "
		+ $"window.chrome.webview.postMessage({{ {MessageKey}: true }}); "
		+ "}, true); })()";

	/// <summary>Whether a page message is exactly the script's: <c>{"zingMiddleClick": true}</c> and nothing else. Never throws.</summary>
	public static bool IsMessage(string json)
	{
		if (json.Length > MaxLength) return false;
		try
		{
			using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return false;
			var properties = root.EnumerateObject().ToList();
			return properties.Count == 1 && properties[0].Name == MessageKey && properties[0].Value.ValueKind == JsonValueKind.True;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	/// <summary>Whether a press at <paramref name="pressedAtMs"/> made a request at <paramref name="nowMs"/> (both monotonic milliseconds).</summary>
	public static bool Made(long? pressedAtMs, long nowMs) => pressedAtMs is { } at && nowMs >= at && nowMs - at <= WindowMs;
}
