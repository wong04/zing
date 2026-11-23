using System.Text.Json;
using Zing.Core.Mcp;

namespace Zing.Core.Tests.Mcp;

public sealed class AxTextTests
{
	[Fact]
	public void TheTextIsThePagesStaticTextWithoutWhatWasTyped()
	{
		var text = AxText.From(Vectors.Load("host/Zing.Core.Tests/Mcp/ax-login.json"));

		Assert.Equal("Sign in", text);
	}

	[Fact]
	public void TextComesInDocumentOrderAndSkipsIgnoredAndInlineBoxes()
	{
		using var tree = JsonDocument.Parse("""
			{ "nodes": [
				{ "nodeId": "1", "ignored": false, "role": { "value": "RootWebArea" }, "childIds": ["2", "5", "6"] },
				{ "nodeId": "2", "ignored": true, "role": { "value": "generic" }, "childIds": ["3"] },
				{ "nodeId": "3", "ignored": false, "role": { "value": "StaticText" }, "name": { "value": "First" }, "childIds": ["4"] },
				{ "nodeId": "4", "ignored": false, "role": { "value": "InlineTextBox" }, "name": { "value": "First" } },
				{ "nodeId": "5", "ignored": true, "role": { "value": "StaticText" }, "name": { "value": "hidden" } },
				{ "nodeId": "6", "ignored": false, "role": { "value": "paragraph" }, "childIds": ["7", "8"] },
				{ "nodeId": "7", "ignored": false, "role": { "value": "StaticText" }, "name": { "value": " Second " } },
				{ "nodeId": "8", "ignored": false, "role": { "value": "generic" }, "properties": [{ "name": "editable", "value": { "value": "richtext" } }], "childIds": ["9"] },
				{ "nodeId": "9", "ignored": false, "role": { "value": "StaticText" }, "name": { "value": "draft" } }
			] }
			""");

		Assert.Equal("First\nSecond", AxText.From(tree.RootElement));
	}

	[Fact]
	public void AnEmptyTreeHasNoText()
	{
		using var tree = JsonDocument.Parse("""{ "nodes": [] }""");

		Assert.Equal("", AxText.From(tree.RootElement));
	}
}
