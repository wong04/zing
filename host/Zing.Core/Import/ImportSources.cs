using System.Text.Json;

namespace Zing.Core.Import;

public enum ImportBrowser { Chrome, Edge, Brave, Dia, Arc }

/// <param name="Id">"chrome:Default", "edge:Profile 1", "dia:Default", "arc".</param>
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
		return ProfileNames(document.RootElement);
	}

	private static Dictionary<string, string> ProfileNames(JsonElement localState)
	{
		Dictionary<string, string> names = [];
		if (!TryProfileSection(localState, "info_cache", out var cache) || cache.ValueKind != JsonValueKind.Object) return names;
		foreach (var entry in cache.EnumerateObject())
			names[entry.Name] = entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text ? text : entry.Name;
		return names;
	}

	/// <remarks>A file that can't be read or parsed is still a row, with <see cref="ImportSource.Error"/> set, and so is a damaged Local State (one row for the browser). A missing Local State means no rows.</remarks>
	public static IReadOnlyList<ImportSource> Find(string localAppData)
	{
		List<ImportSource> found = [];
		foreach (var (browser, label, userDataParts) in ChromiumBrowsers)
			found.AddRange(FindChromium(browser, label, System.IO.Path.Combine([localAppData, .. userDataParts])));
		found.AddRange(FindDia(localAppData));
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

	private static List<ImportSource> FindChromium(ImportBrowser browser, string label, string userData)
	{
		var localStatePath = System.IO.Path.Combine(userData, "Local State");
		if (!File.Exists(localStatePath)) return [];
		const string what = "this browser's profile list";
		try
		{
			using var document = JsonDocument.Parse(ImportFile.ReadText(localStatePath));
			var lastUsed = TryProfileSection(document.RootElement, "last_used", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
			List<ImportSource> rows = [];
			foreach (var (dir, name) in ProfileNames(document.RootElement))
			{
				var bookmarks = System.IO.Path.Combine(userData, dir, "Bookmarks");
				if (NotUserProfiles.Contains(dir) || !File.Exists(bookmarks)) continue;
				rows.Add(Describe(new ImportSource($"{label.ToLowerInvariant()}:{dir}", browser, dir, $"{label} ({name})", bookmarks, null, null, dir == lastUsed)));
			}
			return rows;
		}
		catch (Exception error) when (IsReadFailure(error))
		{
			return [new ImportSource(label.ToLowerInvariant(), browser, null, label, localStatePath, null, ImportErrors.Explain(error, what), false)];
		}
	}

	/// <summary>Dia installs as a package, and Windows keeps its Chromium user data inside the package folder.</summary>
	private static List<ImportSource> FindDia(string localAppData)
	{
		var packages = System.IO.Path.Combine(localAppData, "Packages");
		if (!Directory.Exists(packages)) return [];
		string[] diaPackages;
		try
		{
			diaPackages = Directory.GetDirectories(packages, "TheBrowserCompany.Dia_*");
		}
		catch (Exception error) when (IsReadFailure(error))
		{
			// The Arc row already says the Packages folder can't be read; a Dia row would claim Dia is installed when that isn't known.
			return [];
		}
		foreach (var package in diaPackages.Order(StringComparer.Ordinal))
		{
			var userData = System.IO.Path.Combine(package, "LocalCache", "Local", "Dia", "User Data");
			if (File.Exists(System.IO.Path.Combine(userData, "Local State"))) return FindChromium(ImportBrowser.Dia, "Dia", userData);
		}
		return [];
	}

	private static ImportSource? FindArc(string localAppData)
	{
		var packages = System.IO.Path.Combine(localAppData, "Packages");
		if (!Directory.Exists(packages)) return null;
		string[] arcPackages;
		try
		{
			arcPackages = Directory.GetDirectories(packages, "TheBrowserCompany.Arc_*");
		}
		catch (Exception error) when (IsReadFailure(error))
		{
			return new ImportSource("arc", ImportBrowser.Arc, null, "Arc", packages, null, ImportErrors.Explain(error, "Arc's sidebar"), false);
		}
		foreach (var package in arcPackages.Order(StringComparer.Ordinal))
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
		catch (Exception error) when (IsReadFailure(error))
		{
			return source with { Error = ImportErrors.Explain(error, "this profile's bookmarks") };
		}
	}

	private static bool IsReadFailure(Exception error) =>
		error is BookmarksFileDamagedException or ChromiumBookmarksFormatException or ArcSidebarFormatException or ImportFileTooLargeException or ImportFileNotUtf8Exception or JsonException or IOException or UnauthorizedAccessException;

	private static bool TryProfileSection(JsonElement root, string property, out JsonElement value)
	{
		value = default;
		return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty(property, out value);
	}
}
