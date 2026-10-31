using System.Text.Json;
using Zing.Core;

namespace Zing.Core.Tests;

public sealed class BridgeTests
{
	private static async Task<JsonElement> Run(Bridge bridge, string json) => JsonDocument.Parse(await bridge.HandleAsync(json)).RootElement;

	[Fact]
	public async Task Answers_an_invoke_with_the_handlers_value_in_camelCase()
	{
		var bridge = new Bridge();
		bridge.Register("tabs:get", _ => new TabsState([], null, "manual", false, null));
		var result = await Run(bridge, """{"kind":"invoke","id":7,"channel":"tabs:get","args":[]}""");
		Assert.Equal("result", result.GetProperty("kind").GetString());
		Assert.Equal(7, result.GetProperty("id").GetInt32());
		Assert.True(result.GetProperty("ok").GetBoolean());
		Assert.Equal("manual", result.GetProperty("value").GetProperty("organization").GetString());
	}

	[Fact]
	public async Task Leaves_nulls_out_so_the_renderer_sees_undefined()
	{
		var bridge = new Bridge();
		bridge.Register("tabs:get", _ => new TabsState([], null, "manual", false, null));
		var result = await Run(bridge, """{"kind":"invoke","id":1,"channel":"tabs:get","args":[]}""");
		Assert.False(result.GetProperty("value").TryGetProperty("activeId", out _));
	}

	[Fact]
	public async Task Passes_arguments_in_order()
	{
		var bridge = new Bridge();
		bridge.Register("tabs:navigate", args => $"{args[0].GetString()} -> {args[1].GetString()}");
		var result = await Run(bridge, """{"kind":"invoke","id":1,"channel":"tabs:navigate","args":["t1","example.com"]}""");
		Assert.Equal("t1 -> example.com", result.GetProperty("value").GetString());
	}

	[Fact]
	public async Task An_unknown_channel_is_an_explicit_not_implemented_error()
	{
		var result = await Run(new Bridge(), """{"kind":"invoke","id":3,"channel":"vault:get","args":[]}""");
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Equal("NotImplementedError", result.GetProperty("error").GetProperty("name").GetString());
	}

	[Fact]
	public async Task A_handler_failure_becomes_an_error_result_with_its_type()
	{
		var bridge = new Bridge();
		bridge.Register("tabs:activate", args => throw new UnknownTabException(args[0].GetString()!));
		var result = await Run(bridge, """{"kind":"invoke","id":4,"channel":"tabs:activate","args":["nope"]}""");
		var error = result.GetProperty("error");
		Assert.Equal(nameof(UnknownTabException), error.GetProperty("name").GetString());
		Assert.Equal("No tab nope", error.GetProperty("message").GetString());
	}

	[Fact]
	public Task A_message_that_is_not_an_invoke_is_a_protocol_error() =>
		Assert.ThrowsAsync<BridgeProtocolException>(() => new Bridge().HandleAsync("""{"kind":"hello"}"""));

	[Fact]
	public void A_channel_cannot_get_two_handlers()
	{
		var bridge = new Bridge();
		bridge.Register("tabs:get", _ => (object?)null);
		Assert.Throws<ArgumentException>(() => bridge.Register("tabs:get", _ => (object?)null));
	}

	[Fact]
	public void Events_carry_channel_and_payload()
	{
		var e = JsonDocument.Parse(Bridge.Event("tabs:state", new { activeId = "t1" })).RootElement;
		Assert.Equal("event", e.GetProperty("kind").GetString());
		Assert.Equal("tabs:state", e.GetProperty("channel").GetString());
		Assert.Equal("t1", e.GetProperty("payload").GetProperty("activeId").GetString());
	}
}
