using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Mcp;

/// <summary>One JSON-RPC message as text in, its response as one line of text out, for both the HTTP endpoint and the pipe.</summary>
public static class McpText
{
	private const int ParseError = -32700;
	private const int InvalidRequest = -32600;
	private const int InternalError = -32603;

	/// <summary>The reply to a message past the size limit; the connection then ends, since the rest of it can't be framed.</summary>
	public static readonly string TooLong = Error(null, ParseError, "Message too long.");

	/// <summary>
	/// Null for a notification. An exception out of <paramref name="handle"/> is a bug: it goes to <paramref name="report"/>
	/// and the client gets an internal error without its message, which may name what the person browsed.
	/// </summary>
	public static async Task<string?> Handle(Func<JsonObject, Task<JsonObject?>> handle, string text, Action<Exception> report)
	{
		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(text);
		}
		catch (JsonException)
		{
			return Error(null, ParseError, "Not JSON.");
		}
		if (parsed is not JsonObject message) return Error(null, InvalidRequest, "A message must be one JSON object.");
		JsonObject? response;
		try
		{
			response = await handle(message);
		}
		catch (Exception error)
		{
			report(error);
			return Error(message["id"]?.DeepClone(), InternalError, "Zing hit an internal error.");
		}
		return response?.ToJsonString();
	}

	internal static string Error(JsonNode? id, int code, string message) =>
		new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
