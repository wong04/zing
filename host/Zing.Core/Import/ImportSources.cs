using System.Text.Json;

namespace Zing.Core.Import;

public enum ImportBrowser { Chrome, Edge, Brave, Arc }

/// <param name="Id">"chrome:Default", "edge:Profile 1", "arc".</param>
/// <param name="Profile">The profile folder name; null for Arc.</param>
/// <param name="Display">"Chrome (Personal)", "Arc"; also the merge source key.</param>
/// <param name="Path">The Bookmarks file or StorableSidebar.json. Host-only: never sent to the renderer.</param>
/// <param name="Bookmarks">The link count; null when the file couldn't be read or parsed, and then <paramref name="Error"/> says why.</param>
public sealed record ImportSource(string Id, ImportBrowser Browser, string? Profile, string Display, string Path, int? Bookmarks, string? Error, bool LastUsed);

/// <summary>Finds the browsers whose bookmarks Zing can import from this PC. The C# successor of the Electron-era profiles.node.ts.</summary>
public static class ImportSources
{
	private static readonly (ImportBrowser Browser, string Label, string[] UserData)[] ChromiumBrowsers =
	[
		(ImportBrowser.Chrome, "Chrome", ["Google", "Chrome", "User Data"]),
		(ImportBrowser.Edge, "Edge", ["Microsoft", "Edge", "User Data"]),
		(ImportBrowser.Brave, "Brave", ["BraveSoftware", "Brave-Browser", "User Data"]),
	];

	// Chromium creates these itself; they're never someone's bookmarks.
	private static readonly HashSet<string> NotUserProfiles = ["Guest Profile", "System Profile"];

	/// <summary>The display names ("Work", "Person 1") live in Local State, not in the profile folders, which are just "Default", "Profile 1".</summary>
	/// <exception cref="JsonException">Malformed JSON.</exception>
	public static IReadOnlyDictionary<string, string> ProfileNames(string localStateJson)
	{
		using var document = JsonDocument.Parse(localStateJson);
		Dictionary<string, string> names = [];
		if (!TryProfileSection(document.RootElement, "info_cache", out var cache) || cache.ValueKind != JsonValueKind.Object) return names;
		foreach (var entry in cache.EnumerateObject())
			names[entry.Name] = entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text ? text : entry.Name;
		return names;
	}

	/// <remarks>A file that can't be read or parsed is still a row, with <see cref="ImportSource.Error"/> set. A missing or malformed Local State is not caught.</remarks>
	/// <exception cref="JsonException">A malformed Local State.</exception>
	public static IReadOnlyList<ImportSource> Find(string localAppData)
	{
		List<ImportSource> found = [];
		foreach (var (browser, label, userDataParts) in ChromiumBrowsers)
			found.AddRange(FindChromium(browser, label, System.IO.Path.Combine([localAppData, .. userDataParts])));
		if (FindArc(localAppData) is { } arc) found.Add(arc);
		return found;
	}

	/// <exception cref="BookmarksFileDamagedException">Malformed or too deeply nested JSON.</exception>
	/// <exception cref="ChromiumBookmarksFormatException"/>
	/// <exception cref="ArcSidebarFormatException"/>
	/// <exception cref="ImportFileTooLargeException"/>
	/// <exception cref="ImportFileNotUtf8Exception"/>
	/// <exception cref="IOException"/>
	/// <exception cref="UnauthorizedAccessException"/>
	public static BookmarkImport Read(ImportSource source)
	{
		var text = ImportFile.ReadText(source.Path);
		try
		{
			return source.Browser == ImportBrowser.Arc ? ArcSidebar.Parse(text) : ChromiumBookmarks.Parse(text);
		}
		catch (JsonException)
		{
			throw new BookmarksFileDamagedException(System.IO.Path.GetFileName(source.Path));
		}
	}

	private static IEnumerable<ImportSource> FindChromium(ImportBrowser browser, string label, string userData)
	{
		var localStatePath = System.IO.Path.Combine(userData, "Local State");
		if (!File.Exists(localStatePath)) yield break;
		var localState = ImportFile.ReadText(localStatePath);
		var lastUsed = LastUsedProfile(localState);
		foreach (var (dir, name) in ProfileNames(localState))
		{
			var bookmarks = System.IO.Path.Combine(userData, dir, "Bookmarks");
			if (NotUserProfiles.Contains(dir) || !File.Exists(bookmarks)) continue;
			yield return Describe(new ImportSource($"{label.ToLowerInvariant()}:{dir}", browser, dir, $"{label} ({name})", bookmarks, null, null, dir == lastUsed));
		}
	}

	private static ImportSource? FindArc(string localAppData)
	{
		var packages = System.IO.Path.Combine(localAppData, "Packages");
		if (!Directory.Exists(packages)) return null;
		foreach (var package in Directory.GetDirectories(packages, "TheBrowserCompany.Arc_*").Order(StringComparer.Ordinal))
		{
			var sidebar = System.IO.Path.Combine(package, "LocalCache", "Local", "Arc", "StorableSidebar.json");
			if (File.Exists(sidebar)) return Describe(new ImportSource("arc", ImportBrowser.Arc, null, "Arc", sidebar, null, null, false));
		}
		return null;
	}

	private static ImportSource Describe(ImportSource source)
	{
		try
		{
			var import = Read(source);
			return source with { Bookmarks = BookmarkImport.CountLinks(import.Bar) + BookmarkImport.CountLinks(import.Other) };
		}
		catch (Exception error) when (error is BookmarksFileDamagedException or ChromiumBookmarksFormatException or ArcSidebarFormatException or ImportFileTooLargeException or ImportFileNotUtf8Exception or IOException or UnauthorizedAccessException)
		{
			return source with { Error = error.Message };
		}
	}

	private static string? LastUsedProfile(string localStateJson)
	{
		using var document = JsonDocument.Parse(localStateJson);
		return TryProfileSection(document.RootElement, "last_used", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	}

	private static bool TryProfileSection(JsonElement root, string property, out JsonElement value)
	{
		value = default;
		return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty(property, out value);
	}
}
