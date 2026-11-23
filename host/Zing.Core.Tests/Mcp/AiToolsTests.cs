using System.Text.Json;
using System.Text.Json.Nodes;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class AiToolsTests : IDisposable
{
	private static readonly TabRef Tab = new("w1", "t1");
	private static readonly Uri Shop = new("https://shop.example.com/cart");

	private const string CartTree = """
		{ "nodes": [
			{ "nodeId": "1", "ignored": false, "role": { "value": "RootWebArea" }, "name": { "value": "Cart" }, "childIds": ["2", "4"], "backendDOMNodeId": 1 },
			{ "nodeId": "2", "ignored": false, "role": { "value": "form" }, "name": { "value": "" }, "childIds": ["3"], "backendDOMNodeId": 2 },
			{ "nodeId": "3", "ignored": false, "role": { "value": "button" }, "name": { "value": "Place order" }, "childIds": [], "backendDOMNodeId": 20 },
			{ "nodeId": "4", "ignored": false, "role": { "value": "link" }, "name": { "value": "Help" }, "childIds": [], "backendDOMNodeId": 21 }
		] }
		""";

	private readonly string _dir = Directory.CreateTempSubdirectory("zing-aitools-").FullName;
	private readonly ManualScheduler _scheduler = new();
	private readonly AiShares _shares = new();
	private readonly ConfirmGate _gate;
	private readonly FakePageDriver _driver = new();
	private readonly AiTools _tools;
	private readonly string _aiId;

	public AiToolsTests()
	{
		var policy = new SitePolicy(Path.Combine(_dir, "ai-sites.json"));
		_gate = new ConfirmGate(_scheduler, _shares, policy);
		_tools = new AiTools(_driver, _shares, policy, _gate, _scheduler) { Enabled = true };
		_driver.Tree = JsonDocument.Parse(CartTree).RootElement;
		_driver.Urls[Tab] = Shop;
		_aiId = _shares.Share(Tab, Shop);
	}

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	[Fact]
	public void ListHasExactlyTheSpecTools()
	{
		string[] expected =
		[
			"list_tabs", "snapshot", "read_text", "screenshot", "open_tab", "close_tab", "navigate", "back", "forward",
			"click", "type", "select", "scroll", "press_key", "wait_for", "download", "fill_login",
		];
		var tools = _tools.List();
		Assert.Equal(expected.Order(), tools.Select(t => t.Name).Order());
		Assert.All(tools, t => Assert.Equal("object", t.InputSchema["type"]!.GetValue<string>()));
	}

	[Fact]
	public async Task UnsharedTabIsNotShared()
	{
		_shares.Unshare(Tab);
		var result = await _tools.Call("snapshot", Args(("tab", _aiId)));
		Assert.Equal(AiErrors.NotShared, CodeOf(result));
	}

	[Fact]
	public async Task ClickOnPlaceOrderWaitsForConfirm()
	{
		await Snapshot();
		var click = _tools.Call("click", Args(("tab", _aiId), ("ref", "e1")));

		Assert.False(click.IsCompleted);
		Assert.Equal([Tab], _gate.Waiting());
		Assert.DoesNotContain("click 20", _driver.Calls);

		_gate.Answer(Tab, ConfirmAnswer.Allow);
		var result = await click;

		Assert.Null(CodeOf(result));
		Assert.Contains("click 20", _driver.Calls);
	}

	[Fact]
	public async Task HelpLinkClicksWithoutConfirm()
	{
		await Snapshot();
		var result = await _tools.Call("click", Args(("tab", _aiId), ("ref", "e2")));
		Assert.Null(CodeOf(result));
		Assert.Contains("click 21", _driver.Calls);
		Assert.Empty(_gate.Waiting());
	}

	[Fact]
	public async Task CoveredClickFails()
	{
		await Snapshot();
		_driver.HitNode = 99;
		var result = await _tools.Call("click", Args(("tab", _aiId), ("ref", "e2")));
		Assert.Equal(AiErrors.Covered, CodeOf(result));
		Assert.DoesNotContain("click 21", _driver.Calls);
	}

	[Fact]
	public async Task RefFromOlderSnapshotIsStale()
	{
		await Snapshot();
		var ask = _tools.Call("click", Args(("tab", _aiId), ("ref", "e1")));
		await Snapshot();
		_gate.Answer(Tab, ConfirmAnswer.Allow);
		Assert.Equal(AiErrors.StaleRef, CodeOf(await ask));
		Assert.DoesNotContain("click 20", _driver.Calls);
	}

	[Fact]
	public async Task UrlChangeDuringConfirmIsDenied()
	{
		await Snapshot();
		var ask = _tools.Call("click", Args(("tab", _aiId), ("ref", "e1")));
		_driver.Urls[Tab] = new Uri("https://shop.example.com/other");
		_gate.Answer(Tab, ConfirmAnswer.Allow);
		Assert.Equal(AiErrors.Denied, CodeOf(await ask));
	}

	[Theory]
	[InlineData("navigate", "file:///C:/Windows/win.ini")]
	[InlineData("navigate", "javascript:alert(1)")]
	[InlineData("navigate", "zing://settings")]
	[InlineData("navigate", "/relative")]
	[InlineData("open_tab", "file:///C:/Windows/win.ini")]
	public async Task NavigateToFileIsBlocked(string tool, string url)
	{
		var result = await _tools.Call(tool, Args(("tab", _aiId), ("url", url)));
		Assert.Equal(AiErrors.BlockedUrl, CodeOf(result));
		Assert.Empty(_driver.Calls);
	}

	[Fact]
	public async Task OpenTabSharesTheNewTab()
	{
		var result = await _tools.Call("open_tab", Args(("url", "https://example.com/")));
		Assert.Null(CodeOf(result));
		Assert.True(_shares.IsShared(_driver.NextTab));
	}

	[Fact]
	public async Task UnsharedDuringConfirmReturnsNotShared()
	{
		await Snapshot();
		var click = _tools.Call("click", Args(("tab", _aiId), ("ref", "e1")));
		_shares.Unshare(Tab);
		Assert.Equal(AiErrors.NotShared, CodeOf(await click));
		Assert.DoesNotContain("click 20", _driver.Calls);
	}

	[Fact]
	public async Task ScreenshotBlacksOutFilledNodes()
	{
		_driver.Fill = (FillResult.Filled, new HashSet<long> { 12, 13 });
		Assert.Null(CodeOf(await _tools.Call("fill_login", Args(("tab", _aiId)))));

		var result = await _tools.Call("screenshot", Args(("tab", _aiId)));

		Assert.Null(CodeOf(result));
		Assert.Equal([12L, 13L], _driver.LastBlackOut!.Order());
		var image = result["content"]![0]!;
		Assert.Equal("image", image["type"]!.GetValue<string>());
		Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
	}

	[Fact]
	public async Task NoSavedLoginIsAnError()
	{
		Assert.Equal(AiErrors.NoSavedLogin, CodeOf(await _tools.Call("fill_login", Args(("tab", _aiId)))));
	}

	[Fact]
	public async Task DriverExceptionsMapToCodes()
	{
		_driver.Urls.Remove(Tab);
		Assert.Equal(AiErrors.TabGone, CodeOf(await _tools.Call("back", Args(("tab", _aiId)))));
	}

	[Fact]
	public async Task DisabledReturnsDisabled()
	{
		_tools.Enabled = false;
		var result = await _tools.Call("list_tabs", []);
		Assert.Equal(AiErrors.Disabled, CodeOf(result));
	}

	[Fact]
	public async Task ListTabsShowsOnlyAiIds()
	{
		var result = await _tools.Call("list_tabs", []);
		var text = result["content"]![0]!["text"]!.GetValue<string>();
		Assert.Contains(_aiId, text);
		Assert.DoesNotContain("\"t1\"", text);
	}

	[Fact]
	public async Task UnknownToolThrows()
	{
		await Assert.ThrowsAsync<UnknownToolException>(() => _tools.Call("evaluate", []));
	}

	private async Task Snapshot() => Assert.Null(CodeOf(await _tools.Call("snapshot", Args(("tab", _aiId)))));

	private static JsonObject Args(params (string Key, string Value)[] pairs)
	{
		var args = new JsonObject();
		foreach (var (key, value) in pairs) args[key] = value;
		return args;
	}

	private static string? CodeOf(JsonNode result) =>
		result["isError"]?.GetValue<bool>() == true ? result["structuredContent"]!["code"]!.GetValue<string>() : null;
}
