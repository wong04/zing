using System.Text;
using System.Text.Json;

namespace Zing.Core.Mcp;

public enum RefLookup { Found, Stale, Unknown }

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
		if (first is not null) Walk(first, 0, false);
		return new Snapshot(generation, text.ToString().TrimEnd('\n'), refs);

		void Walk(string id, int depth, bool underMasked)
		{
			if (underMasked) return;
			var node = nodes[id];
			var backendId = node.TryGetProperty("backendDOMNodeId", out var b) ? b.GetInt64() : (long?)null;
			var masked = IsProtected(node) || (backendId is not null && maskedNodes.Contains(backendId.Value));
			var kept = !node.GetProperty("ignored").GetBoolean();
			if (kept) Append(node, depth, backendId, masked);
			if (!node.TryGetProperty("childIds", out var children)) return;
			// A masked field's text children (StaticText, InlineTextBox) repeat what was typed, so they go too.
			// An ignored node's children still belong to the page; they take its place.
			foreach (var child in children.EnumerateArray())
				if (nodes.ContainsKey(child.GetString()!)) Walk(child.GetString()!, kept ? depth + 1 : depth, masked);
		}

		void Append(JsonElement node, int depth, long? backendId, bool masked)
		{
			var role = StringOf(node, "role");
			text.Append(' ', depth * 2).Append(role).Append(" \"").Append(Escape(StringOf(node, "name"))).Append('"');
			if (backendId is not null && InteractiveRoles.Contains(role))
			{
				var reference = $"e{refs.Count + 1}";
				refs[reference] = backendId.Value;
				text.Append(" [").Append(reference).Append(']');
			}
			var value = StringOf(node, "value");
			if (value.Length > 0) text.Append(" value=\"").Append(masked ? Mask : Escape(value)).Append('"');
			text.Append('\n');
		}
	}

	/// <summary>Resolves a ref from the snapshot of the current generation; a ref from an older page state is Stale.</summary>
	public RefLookup TryRef(string reference, int currentGeneration, out long backendNodeId)
	{
		backendNodeId = 0;
		if (currentGeneration != Generation) return RefLookup.Stale;
		return BackendNodeIds.TryGetValue(reference, out backendNodeId) ? RefLookup.Found : RefLookup.Unknown;
	}

	// Page-controlled text must not close the quote or start a new line, which would forge a ref.
	private static string Escape(string value)
	{
		var escaped = new StringBuilder(value.Length);
		foreach (var c in value)
		{
			if (c is '"' or '\\') escaped.Append('\\').Append(c);
			else if (c == '\n') escaped.Append("\\n");
			else if (c == '\r') escaped.Append("\\r");
			else if (c == '\t') escaped.Append("\\t");
			else if (char.IsControl(c) || c is '\u2028' or '\u2029') escaped.Append("\\u").Append(((int)c).ToString("x4"));
			else escaped.Append(c);
		}
		return escaped.ToString();
	}

	private static string StringOf(JsonElement node, string property) =>
		node.TryGetProperty(property, out var p) && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

	private static bool IsProtected(JsonElement node)
	{
		if (!node.TryGetProperty("properties", out var properties)) return false;
		foreach (var p in properties.EnumerateArray())
			if (p.GetProperty("name").GetString() == "protected" && p.TryGetProperty("value", out var v) && v.TryGetProperty("value", out var flag) && flag.ValueKind == JsonValueKind.True) return true;
		return false;
	}
}
