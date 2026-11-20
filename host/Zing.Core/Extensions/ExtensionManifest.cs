using System.Text.Json;

namespace Zing.Core.Extensions;

/// <summary>What Zing reads from an unpacked extension's manifest.json: enough to list it and open its popup.</summary>
/// <param name="Popup">The action's popup page, relative to the extension's folder; null when it has none.</param>
public sealed record ExtensionManifest(string Name, string Version, string? Popup)
{
	public const string FileName = "manifest.json";

	/// <summary>Reads and checks a folder's manifest. Only Manifest V3 is accepted: WebView2's engine no longer runs V2.</summary>
	public static ExtensionManifest Read(string folder)
	{
		var path = Path.Combine(folder, FileName);
		if (!File.Exists(path)) throw new InvalidExtensionException(folder, "it has no manifest.json");
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
		}
		catch (JsonException error)
		{
			throw new InvalidExtensionException(folder, $"its manifest.json can't be read ({error.Message})");
		}
		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) throw new InvalidExtensionException(folder, "its manifest.json isn't an object");
			if (!root.TryGetProperty("manifest_version", out var manifestVersion) || manifestVersion.ValueKind != JsonValueKind.Number || manifestVersion.GetInt32() != 3)
				throw new InvalidExtensionException(folder, "only Manifest V3 extensions run");
			var name = Text(root, "name") ?? throw new InvalidExtensionException(folder, "its manifest has no name");
			var version = Text(root, "version") ?? throw new InvalidExtensionException(folder, "its manifest has no version");
			var popup = root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object ? Text(action, "default_popup") : null;
			return new ExtensionManifest(Localized(folder, root, name), version, popup is { Length: > 0 } ? popup.TrimStart('/') : null);
		}
	}

	private static string? Text(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	/// <summary>A name given as __MSG_key__ is looked up in the default locale's messages.json, as Chromium does; keys ignore case.</summary>
	private static string Localized(string folder, JsonElement root, string name)
	{
		if (!name.StartsWith("__MSG_", StringComparison.Ordinal) || !name.EndsWith("__", StringComparison.Ordinal)) return name;
		var key = name[6..^2];
		var locale = Text(root, "default_locale") ?? throw new InvalidExtensionException(folder, $"its name is the message {key} but it has no default_locale");
		var messages = Path.Combine(folder, "_locales", locale, "messages.json");
		if (!File.Exists(messages)) throw new InvalidExtensionException(folder, $"_locales/{locale}/messages.json is missing");
		using var document = JsonDocument.Parse(File.ReadAllText(messages), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
		foreach (var entry in document.RootElement.EnumerateObject())
			if (string.Equals(entry.Name, key, StringComparison.OrdinalIgnoreCase) && Text(entry.Value, "message") is { Length: > 0 } message) return message;
		throw new InvalidExtensionException(folder, $"_locales/{locale}/messages.json has no {key} message");
	}
}
