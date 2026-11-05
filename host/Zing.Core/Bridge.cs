using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Engines;

namespace Zing.Core;

/// <summary>
/// The host side of the renderer bridge (renderer side: src/shared/bridge/webviewBridge.ts).
/// renderer -> host  { kind: 'invoke', id, channel, args }
/// host -> renderer  { kind: 'result', id, ok, value | error } and { kind: 'event', channel, payload }
/// </summary>
public sealed class Bridge
{
	public delegate Task<object?> Handler(JsonElement[] args);

	private readonly Dictionary<string, Handler> _handlers = new(StringComparer.Ordinal);
	private DropInbox? _drops;

	/// <summary>Where a Zing view's drop is left for the renderer's <c>files:open-dropped</c> call; set by the window's ChromeApi.</summary>
	public void UseDropInbox(DropInbox drops) => _drops = drops;

	/// <summary>The host read these paths from a drop on <paramref name="source"/>. Refused unless it is one of Zing's own views.</summary>
	public void OfferDrop(string source, IReadOnlyList<string> paths) =>
		(_drops ?? throw new InvalidOperationException("This window takes no drops")).Offer(source, paths);

	/// <summary>A drop nobody collected is forgotten, so the next plain call can't pick it up.</summary>
	public void ClearDrop() => _drops?.Clear();

	/// <summary>Every channel with a handler, for checking against the contract (Defaults/channels.json).</summary>
	public IReadOnlyCollection<string> Channels => _handlers.Keys;

	public void Register(string channel, Handler handler)
	{
		if (!_handlers.TryAdd(channel, handler)) throw new ArgumentException($"{channel} already has a handler", nameof(channel));
	}

	public void Register(string channel, Func<JsonElement[], object?> handler) =>
		Register(channel, args => Task.FromResult(handler(args)));

	/// <summary>Answers one message from the renderer. Never throws for a handler failure: it becomes an error result the renderer rejects with.</summary>
	public async Task<string> HandleAsync(string json)
	{
		var message = Parse(json);
		if (!_handlers.TryGetValue(message.Channel, out var handler))
		{
			return Error(message.Id, "NotImplementedError", $"{message.Channel} isn't available on the WebView2 host yet");
		}
		try
		{
			var value = await handler(message.Args);
			return JsonSerializer.Serialize(new { kind = "result", id = message.Id, ok = true, value }, ZingJson.Options);
		}
		catch (Exception error)
		{
			return Error(message.Id, error.GetType().Name, error.Message);
		}
	}

	public static string Event(string channel, object? payload) =>
		JsonSerializer.Serialize(new { kind = "event", channel, payload }, ZingJson.Options);

	private static string Error(int id, string name, string message) =>
		JsonSerializer.Serialize(new { kind = "result", id, ok = false, error = new { name, message } }, ZingJson.Options);

	private sealed record InvokeMessage(int Id, string Channel, JsonElement[] Args);

	private static InvokeMessage Parse(string json)
	{
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object || root.GetProperty("kind").GetString() != "invoke")
		{
			throw new BridgeProtocolException($"Not an invoke message: {json}");
		}
		var args = root.GetProperty("args").EnumerateArray().Select(a => a.Clone()).ToArray();
		return new InvokeMessage(root.GetProperty("id").GetInt32(), root.GetProperty("channel").GetString()!, args);
	}
}

public sealed class BridgeProtocolException(string message) : Exception(message);

public static class ZingJson
{
	/// <summary>
	/// camelCase like the TypeScript types, and nulls left out so they arrive as
	/// `undefined` (the renderer checks `=== undefined`, not `null`).
	/// </summary>
	public static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public static JsonNode LoadDefaults(string name)
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Defaults", name);
		return JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} is empty");
	}
}
