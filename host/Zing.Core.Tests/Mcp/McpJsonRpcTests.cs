using System.Text.Json.Nodes;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class McpJsonRpcTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("zing-jsonrpc-").FullName;
	private readonly McpJsonRpc _rpc;

	public McpJsonRpcTests()
	{
		var scheduler = new ManualScheduler();
		var shares = new AiShares();
		var policy = new SitePolicy(Path.Combine(_dir, "ai-sites.json"));
		var tools = new AiTools(new FakePageDriver(), shares, policy, new ConfirmGate(scheduler, shares, policy), scheduler) { Enabled = true };
		_rpc = new McpJsonRpc(tools, "1.2.3");
	}

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	[Fact]
	public async Task InitializeAnswersWithServerInfo()
	{
		var response = await _rpc.Handle(Request(1, "initialize"));
		Assert.Equal("2025-06-18", response!["result"]!["protocolVersion"]!.GetValue<string>());
		Assert.Equal("zing", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
		Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
	}

	[Fact]
	public async Task InitializedNotificationGetsNoResponse()
	{
		Assert.Null(await _rpc.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));
	}

	[Fact]
	public async Task ToolsListCarriesSchemas()
	{
		var response = await _rpc.Handle(Request(2, "tools/list"));
		var tools = response!["result"]!["tools"]!.AsArray();
		Assert.Equal(17, tools.Count);
		Assert.All(tools, t => Assert.NotNull(t!["inputSchema"]));
	}

	[Fact]
	public async Task ToolsCallReturnsToolErrorsAsResults()
	{
		var response = await _rpc.Handle(Request(3, "tools/call", new JsonObject { ["name"] = "snapshot", ["arguments"] = new JsonObject { ["tab"] = "nope" } }));
		Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
		Assert.Equal(AiErrors.NotShared, response["result"]!["structuredContent"]!["code"]!.GetValue<string>());
	}

	[Fact]
	public async Task UnknownToolIsInvalidParams()
	{
		var response = await _rpc.Handle(Request(4, "tools/call", new JsonObject { ["name"] = "evaluate" }));
		Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
	}

	[Fact]
	public async Task UnknownMethodIsMethodNotFound()
	{
		var response = await _rpc.Handle(Request(5, "resources/list"));
		Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
		Assert.Equal(5, response["id"]!.GetValue<int>());
	}

	private static JsonObject Request(int id, string method, JsonObject? parameters = null) =>
		new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
}
