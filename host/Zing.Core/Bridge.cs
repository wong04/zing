using System.Text.Json;
using System.Text.Json.Nodes;

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
