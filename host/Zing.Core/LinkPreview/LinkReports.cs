using System.Text.Json;

namespace Zing.Core.LinkPreview;

/// <summary>What the link-report script tells the host.</summary>
public enum LinkReportKind
{
	/// <summary>A trusted middle press on a link: Chromium opens it on the release (auxclick), when the button is already up.</summary>
	Middle,
}

/// <summary>
/// A channel from each page to the host that page scripts can't use: a script in a DevTools isolated world calls a binding
/// that only that world has, with a <c>{kind}</c> payload. DOM events are shared across worlds, so the script sees the page's
/// input; the page's own script sees neither the script nor the binding. One binding for every report, told apart by kind.
/// </summary>
public static class LinkReports
{
	public const string WorldName = "zing-links";
	public const string BindingName = "__zingLink";

	/// <summary>How long after a middle press a new-window request still counts as made by it.</summary>
	public const long MiddleWindowMs = 1000;

	/// <summary>The longest payload read at all; the script's are under 20 characters.</summary>
	private const int MaxLength = 64;

	/// <summary>Evaluated in the isolated world of every new document; it reports from the top frame only.</summary>
	public static readonly string Script =
		"(() => { if (window.top !== window) return; "
		+ $"const report = (kind) => globalThis.{BindingName}(JSON.stringify({{ kind }})); "
		+ "addEventListener('mousedown', (e) => { "
		+ "if (e.button === 1 && e.isTrusted && e.target instanceof Element && e.target.closest('a[href]')) report('middle'); "
		+ "}, true); })()";

	/// <summary>The report a payload is, or null when it isn't exactly one the script sends. Never throws.</summary>
	public static LinkReportKind? Read(string payload)
	{
		if (payload.Length > MaxLength) return null;
		try
		{
			using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 2 });
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return null;
			var properties = root.EnumerateObject().ToList();
			if (properties.Count != 1 || properties[0].Name != "kind" || properties[0].Value.ValueKind != JsonValueKind.String) return null;
			return properties[0].Value.GetString() switch
			{
				"middle" => LinkReportKind.Middle,
				_ => null,
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>Whether a middle press at <paramref name="pressedAtMs"/> made a request at <paramref name="nowMs"/> (both monotonic milliseconds).</summary>
	public static bool MiddleMade(long? pressedAtMs, long nowMs) => pressedAtMs is { } at && nowMs >= at && nowMs - at <= MiddleWindowMs;
}
