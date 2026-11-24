namespace Zing.Core.Mcp;

/// <summary>A key as DevTools' Input.dispatchKeyEvent takes it; <see cref="Text"/> is what it types, null for keys that type nothing.</summary>
public sealed record CdpKey(string Key, string Code, int VirtualKey, string? Text);

public sealed class UnknownKeyException(string key) : ArgumentException($"'{key}' isn't a key press_key knows: use a KeyboardEvent.key value such as Enter, ArrowDown or a single character.");

/// <summary>The keys press_key can send, by KeyboardEvent.key.</summary>
public static class CdpKeys
{
	private static readonly Dictionary<string, CdpKey> Named = new CdpKey[]
	{
		new("Enter", "Enter", 13, "\r"),
		new("Tab", "Tab", 9, null),
		new("Escape", "Escape", 27, null),
		new("Backspace", "Backspace", 8, null),
		new("Delete", "Delete", 46, null),
		new("ArrowLeft", "ArrowLeft", 37, null),
		new("ArrowUp", "ArrowUp", 38, null),
		new("ArrowRight", "ArrowRight", 39, null),
		new("ArrowDown", "ArrowDown", 40, null),
		new("Home", "Home", 36, null),
		new("End", "End", 35, null),
		new("PageUp", "PageUp", 33, null),
		new("PageDown", "PageDown", 34, null),
		new(" ", "Space", 32, " "),
	}.ToDictionary(k => k.Key, StringComparer.OrdinalIgnoreCase);

	public static CdpKey Of(string key)
	{
		if (Named.TryGetValue(key, out var named)) return named;
		// A control character would be a key by another name ("\r" is Enter) and slip past the submit check.
		if (key.Length != 1 || char.IsControl(key[0])) throw new UnknownKeyException(key);
		var c = key[0];
		if (char.IsAsciiLetter(c)) return new CdpKey(key, $"Key{char.ToUpperInvariant(c)}", char.ToUpperInvariant(c), key);
		if (char.IsAsciiDigit(c)) return new CdpKey(key, $"Digit{c}", c, key);
		// Other characters type themselves; DevTools needs no code for them.
		return new CdpKey(key, "", 0, key);
	}
}
