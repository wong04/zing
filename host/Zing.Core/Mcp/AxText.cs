using System.Text.Json;

namespace Zing.Core.Mcp;

/// <summary>The readable text of a CDP Accessibility.getFullAXTree result (read_text): its StaticText in document order, one per line.</summary>
/// <remarks>Fields are left out whole, with what was typed or filled into them: that is the person's input, not the page's text.</remarks>
public static class AxText
{
	public static string From(JsonElement axTree)
	{
		var nodes = new Dictionary<string, JsonElement>();
		string? first = null;
		foreach (var node in axTree.GetProperty("nodes").EnumerateArray())
		{
			var id = node.GetProperty("nodeId").GetString()!;
			nodes[id] = node;
			first ??= id;
		}
		var lines = new List<string>();
		if (first is null) return "";
		// Explicit so a deeply nested page can't exhaust the stack.
		var pending = new Stack<string>();
		pending.Push(first);
		while (pending.Count > 0)
		{
			if (!nodes.TryGetValue(pending.Pop(), out var node)) continue;
			var role = Snapshot.StringOf(node, "role");
			if (Snapshot.IsProtected(node) || Snapshot.IsEditable(node, role)) continue;
			if (role == "StaticText")
			{
				// Its InlineTextBox children repeat it.
				var text = Snapshot.StringOf(node, "name").Trim();
				if (text.Length > 0 && !node.GetProperty("ignored").GetBoolean()) lines.Add(text);
				continue;
			}
			if (!node.TryGetProperty("childIds", out var children)) continue;
			foreach (var child in children.EnumerateArray().Reverse()) pending.Push(child.GetString()!);
		}
		return string.Join('\n', lines);
	}
}
