using System.Text.Json.Nodes;

namespace Zing.Core.Mcp;

/// <summary>The error codes an AI client sees. Each is returned as an MCP tool error, not a JSON-RPC error.</summary>
public static class AiErrors
{
	public const string NotShared = nameof(NotShared);
	public const string StaleRef = nameof(StaleRef);
	public const string Denied = nameof(Denied);
	public const string NoSavedLogin = nameof(NoSavedLogin);
	public const string TabGone = nameof(TabGone);
	public const string Disabled = nameof(Disabled);
	public const string BlockedUrl = nameof(BlockedUrl);
	public const string Covered = nameof(Covered);
	public const string DialogOpen = nameof(DialogOpen);
	public const string NotVisible = nameof(NotVisible);
	public const string Timeout = nameof(Timeout);

	public static JsonObject Result(string code, string message) => new()
	{
		["isError"] = true,
		["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"{code}: {message}" }),
		["structuredContent"] = new JsonObject { ["code"] = code },
	};
}

/// <summary>A tool call that ends in one of the <see cref="AiErrors"/> codes.</summary>
public sealed class AiToolException(string code, string message) : Exception(message)
{
	public string Code { get; } = code;
}

/// <summary>The client named a tool that doesn't exist; a JSON-RPC invalid-params error, not a tool result.</summary>
public sealed class UnknownToolException(string name) : ArgumentException($"There is no tool named '{name}'.");

/// <summary>A tool's arguments are missing or of the wrong type; a JSON-RPC invalid-params error.</summary>
public sealed class ToolArgumentException(string tool, string argument, string problem)
	: ArgumentException($"{tool}: argument '{argument}' {problem}.");
