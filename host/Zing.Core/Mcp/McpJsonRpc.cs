using System.Text.Json.Nodes;

namespace Zing.Core.Mcp;

/// <summary>
/// The MCP JSON-RPC layer over <see cref="AiTools"/>, written by hand so the HTTP listener and the pipe can both feed it one
/// message at a time on the UI thread.
/// </summary>
public sealed class McpJsonRpc(AiTools tools, string version)
{
	public const string ProtocolVersion = "2025-06-18";

	private const int MethodNotFound = -32601;
	private const int InvalidParams = -32602;
	private const int InvalidRequest = -32600;

	/// <summary>The response to send, or null for a notification.</summary>
	public async Task<JsonObject?> Handle(JsonObject message)
	{
		var id = message["id"]?.DeepClone();
		if (message["method"] is not JsonValue m || !m.TryGetValue<string>(out var method)) return Error(id, InvalidRequest, "No method.");
		if (id is null) return null;
		var parameters = message["params"] as JsonObject ?? [];
		try
		{
			return method switch
			{
				"initialize" => Result(id, Initialize()),
				"ping" => Result(id, new JsonObject()),
				"tools/list" => Result(id, ToolsList()),
				"tools/call" => Result(id, await ToolsCall(parameters)),
				_ => Error(id, MethodNotFound, $"Unknown method {method}."),
			};
		}
		catch (UnknownToolException error)
		{
			return Error(id, InvalidParams, error.Message);
		}
		catch (ToolArgumentException error)
		{
			return Error(id, InvalidParams, error.Message);
		}
	}

	private JsonObject Initialize() => new()
	{
		["protocolVersion"] = ProtocolVersion,
		["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
		["serverInfo"] = new JsonObject { ["name"] = "zing", ["version"] = version },
	};

	private JsonObject ToolsList()
	{
		var list = new JsonArray();
		foreach (var tool in tools.List())
			list.Add(new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["inputSchema"] = tool.InputSchema.DeepClone() });
		return new JsonObject { ["tools"] = list };
	}

	private async Task<JsonNode> ToolsCall(JsonObject parameters)
	{
		if (parameters["name"] is not JsonValue n || !n.TryGetValue<string>(out var name)) throw new ToolArgumentException("tools/call", "name", "is required");
		var args = parameters["arguments"] switch
		{
			null => [],
			JsonObject o => (JsonObject)o.DeepClone(),
			_ => throw new ToolArgumentException(name, "arguments", "must be an object"),
		};
		return await tools.Call(name, args);
	}

	private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

	private static JsonObject Error(JsonNode? id, int code, string message) =>
		new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
