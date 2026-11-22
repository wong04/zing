using System.Text;
using System.Text.Json;

namespace Zing.Core.Mcp;

/// <summary>A page's accessibility tree as text an AI reads, plus the refs in it mapped back to DOM nodes.</summary>
public sealed record Snapshot(int Generation, string Text, IReadOnlyDictionary<string, long> BackendNodeIds)
{
	private const string Mask = "••••";

	private static readonly HashSet<string> InteractiveRoles =
		["button", "link", "textbox", "searchbox", "combobox", "checkbox", "radio", "menuitem", "tab", "option", "switch", "slider", "spinbutton"];

	/// <summary>Reads a CDP Accessibility.getFullAXTree result. Refs are e1, e2... in document order.</summary>
	public static Snapshot Build(JsonElement axTree, int generation, IReadOnlySet<long> maskedNodes)
	{
		var nodes = new Dictionary<string, JsonElement>();
		var first = default(string);
		foreach (var node in axTree.GetProperty("nodes").EnumerateArray())
		{
			var id = node.GetProperty("nodeId").GetString()!;
			nodes[id] = node;
			first ??= id;
		}

		var text = new StringBuilder();
		var refs = new Dictionary<string, long>();
		if (first is not null) Walk(first, 0);
		return new Snapshot(generation, text.ToString().TrimEnd('\n'), refs);

		void Walk(string id, int depth)
		{
			var node = nodes[id];
			var kept = !node.GetProperty("ignored").GetBoolean();
			if (kept) Append(node, depth);
			if (!node.TryGetProperty("childIds", out var children)) return;
			// An ignored node's children still belong to the page; they take its place.
			foreach (var child in children.EnumerateArray())
				if (nodes.ContainsKey(child.GetString()!)) Walk(child.GetString()!, kept ? depth + 1 : depth);
		}

		void Append(JsonElement node, int depth)
		{
			var role = StringOf(node, "role");
			var backendId = node.TryGetProperty("backendDOMNodeId", out var b) ? b.GetInt64() : (long?)null;
			text.Append(' ', depth * 2).Append(role).Append(" \"").Append(StringOf(node, "name")).Append('"');
			if (backendId is not null && InteractiveRoles.Contains(role))
			{
				var reference = $"e{refs.Count + 1}";
				refs[reference] = backendId.Value;
				text.Append(" [").Append(reference).Append(']');
			}
			var value = StringOf(node, "value");
			if (value.Length > 0)
			{
				var masked = IsProtected(node) || (backendId is not null && maskedNodes.Contains(backendId.Value));
				text.Append(" value=\"").Append(masked ? Mask : value).Append('"');
			}
			text.Append('\n');
		}
	}

	/// <summary>Resolves a ref from the snapshot of the current generation; a ref from an older page state is refused.</summary>
	public bool TryRef(string reference, int currentGeneration, out long backendNodeId)
	{
		backendNodeId = 0;
		return currentGeneration == Generation && BackendNodeIds.TryGetValue(reference, out backendNodeId);
	}

	private static string StringOf(JsonElement node, string property) =>
		node.TryGetProperty(property, out var p) && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

	private static bool IsProtected(JsonElement node)
	{
		if (!node.TryGetProperty("properties", out var properties)) return false;
		foreach (var p in properties.EnumerateArray())
			if (p.GetProperty("name").GetString() == "protected" && p.GetProperty("value").GetProperty("value").ValueKind == JsonValueKind.True) return true;
		return false;
	}
}
