using System.Text.Json;

namespace Zing.Core;

/// <summary>
/// The requests a page is sending a large body with, read from DevTools' Network events by the host: nothing runs in the page
/// or wraps its APIs. A request counts from the headers it went out with (Network.requestWillBeSentExtraInfo), when they give
/// a Content-Length of at least <see cref="MinBytes"/>, until it finishes or fails (Network.loadingFinished, Network.loadingFailed).
/// <para>
/// Not seen: a body streamed without a length (fetch with a ReadableStream body), data sent over a WebSocket or WebRTC, and
/// requests made by a service worker or by a frame of another site, which has its own DevTools target.
/// </para>
/// </summary>
public sealed class UploadTracker
{
	/// <summary>Beacons, form posts and long polls stay well under this; a file or photo being sent doesn't.</summary>
	public const long MinBytes = 256 * 1024;

	private readonly HashSet<string> _sending = new(StringComparer.Ordinal);

	public bool IsUploading => _sending.Count > 0;

	/// <summary>Network.requestWillBeSentExtraInfo's parameters.</summary>
	public void HeadersSent(string json)
	{
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		var id = RequestId(root);
		if (!root.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object) return;
		if (ContentLength(headers) >= MinBytes) _sending.Add(id);
	}

	/// <summary>Network.loadingFinished's or Network.loadingFailed's parameters.</summary>
	public void Ended(string json)
	{
		using var document = JsonDocument.Parse(json);
		_sending.Remove(RequestId(document.RootElement));
	}

	/// <summary>The renderer went, and its requests with it, whether or not the engine says so.</summary>
	public void Clear() => _sending.Clear();

	private static string RequestId(JsonElement root) =>
		root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
			? id.GetString()!
			: throw new InvalidDataException($"A Network event has no requestId: {root.GetRawText()}");

	private static long ContentLength(JsonElement headers)
	{
		foreach (var header in headers.EnumerateObject())
		{
			if (!header.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
			return header.Value.ValueKind == JsonValueKind.String && long.TryParse(header.Value.GetString(), out var length) ? length : 0;
		}
		return 0;
	}
}
