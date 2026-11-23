using System.Text.Json.Nodes;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class McpTextTests
{
	[Fact]
	public async Task NotJsonIsAParseError()
	{
		var reply = await McpText.Handle(_ => throw new InvalidOperationException("unreached"), "{nope", _ => { });
		Assert.Equal(-32700, JsonNode.Parse(reply!)!["error"]!["code"]!.GetValue<int>());
	}

	[Fact]
	public async Task NotAnObjectIsAnInvalidRequest()
	{
		var reply = await McpText.Handle(_ => throw new InvalidOperationException("unreached"), "[1,2]", _ => { });
		Assert.Equal(-32600, JsonNode.Parse(reply!)!["error"]!["code"]!.GetValue<int>());
	}

	[Fact]
	public async Task AThrowingHandlerIsAnInternalErrorAndIsReported()
	{
		var reported = new List<Exception>();
		var reply = await McpText.Handle(_ => throw new FormatException("boom"), """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", reported.Add);
		var json = JsonNode.Parse(reply!)!;
		Assert.Equal(-32603, json["error"]!["code"]!.GetValue<int>());
		Assert.Equal(7, json["id"]!.GetValue<int>());
		Assert.DoesNotContain("boom", reply);
		Assert.IsType<FormatException>(Assert.Single(reported));
	}

	[Fact]
	public async Task ANotificationHasNoReply() =>
		Assert.Null(await McpText.Handle(_ => Task.FromResult<JsonObject?>(null), """{"jsonrpc":"2.0","method":"notifications/initialized"}""", _ => { }));

	[Fact]
	public async Task AReplyIsOneLine()
	{
		var reply = await McpText.Handle(m => Task.FromResult<JsonObject?>(new JsonObject { ["id"] = m["id"]!.DeepClone(), ["result"] = new JsonObject { ["a"] = "x\ny" } }), """{"id":1,"method":"ping"}""", _ => { });
		Assert.DoesNotContain('\n', reply!);
	}
}
