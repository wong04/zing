using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zing.Core.Extensions;

/// <summary>One installed extension as Zing keeps it.</summary>
/// <param name="Key">Its folder's name under the store's root; stable for as long as it is installed.</param>
/// <param name="Source">The folder it was loaded from; loading the same folder again replaces it.</param>
/// <param name="Id">The engine's id for it, once the engine has loaded it; null until then.</param>
public sealed record InstalledExtension(string Key, string Source, bool Enabled, string? Id);

/// <summary>
/// The extensions folder (%LOCALAPPDATA%\Zing\Extensions, or the scratch profile's under e2e): one copy of each loaded
/// folder, so the extension keeps working after the original moves, and state.json listing them in install order.
/// The engine keeps its own registration in the browsing profile, which is deleted with "Stay signed in to sites";
/// this folder is what loads them again.
/// </summary>
public sealed partial class ExtensionStore
{
	private const string StateFile = "state.json";
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	private readonly string _root;
	private List<InstalledExtension> _items;
	/// <summary>Engine ids of extensions removed while the engine wasn't there to forget them.</summary>
	private List<string> _forget;

	public ExtensionStore(string root)
	{
		_root = root;
		Directory.CreateDirectory(root);
		var state = LoadState(Path.Combine(root, StateFile));
		_items = [.. state.Extensions.Where(e => Directory.Exists(FolderOf(e.Key)))];
		_forget = [.. state.Forget];
	}

	public string Root => _root;

	public IReadOnlyList<InstalledExtension> Items => _items;

	public IReadOnlyList<string> Forget => _forget;

	public string FolderOf(string key) => Path.Combine(_root, key);

	public InstalledExtension Get(string key) => _items.FirstOrDefault(e => e.Key == key) ?? throw new UnknownExtensionException(key);

	/// <summary>
	/// Copies an unpacked extension in. The same source folder again replaces the copy in place (an update), keeping its
	/// key and so its engine id; the returned flag says so.
	/// </summary>
	public (InstalledExtension Item, bool Replaced) Install(string source)
	{
		var full = Path.GetFullPath(source);
		if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"{full} isn't there");
		var manifest = ExtensionManifest.Read(full);
		if (IsInside(full, _root)) throw new InvalidExtensionException(full, "it is already in Zing's extensions folder");
		var existing = _items.FirstOrDefault(e => string.Equals(e.Source, full, StringComparison.OrdinalIgnoreCase));
		var key = existing?.Key ?? NewKey(manifest.Name);
		var staging = Path.Combine(_root, ".tmp-" + Guid.NewGuid().ToString("N"));
		try
		{
			CopyTree(full, staging);
			var target = FolderOf(key);
			if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
			Directory.Move(staging, target);
		}
		finally
		{
			if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
		}
		var item = existing ?? new InstalledExtension(key, full, Enabled: true, Id: null);
		if (existing is null) _items.Add(item);
		Save();
		return (item, existing is not null);
	}

	public ExtensionManifest ManifestOf(string key) => ExtensionManifest.Read(FolderOf(Get(key).Key));

	public void SetEnabled(string key, bool enabled) => Replace(Get(key) with { Enabled = enabled });

	public void SetId(string key, string id) => Replace(Get(key) with { Id = id });

	/// <summary>Deletes the copy; an engine id is kept to forget once the engine is there (<paramref name="engineForgot"/> false).</summary>
	public void Remove(string key, bool engineForgot)
	{
		var item = Get(key);
		_items.Remove(item);
		if (!engineForgot && item.Id is { } id && !_forget.Contains(id)) _forget.Add(id);
		Save();
		Directory.Delete(FolderOf(key), recursive: true);
	}

	public void Forgotten(string id)
	{
		if (_forget.Remove(id)) Save();
	}

	private void Replace(InstalledExtension item)
	{
		var index = _items.FindIndex(e => e.Key == item.Key);
		_items[index] = item;
		Save();
	}

	private string NewKey(string name)
	{
		var slug = Slug().Replace(name.ToLowerInvariant(), "-").Trim('-');
		if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
		return $"{(slug.Length == 0 ? "extension" : slug)}-{Guid.NewGuid().ToString("N")[..8]}";
	}

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex Slug();

	private static bool IsInside(string path, string folder)
	{
		var relative = Path.GetRelativePath(folder, path);
		return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
	}

	/// <summary>Links are refused rather than followed: a junction could pull in any folder on the PC.</summary>
	private static void CopyTree(string from, string to)
	{
		Directory.CreateDirectory(to);
		foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
		{
			if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidExtensionException(from, $"{entry.Name} is a link, which Zing doesn't copy");
			var target = Path.Combine(to, entry.Name);
			if (entry is DirectoryInfo) CopyTree(entry.FullName, target);
			else File.Copy(entry.FullName, target);
		}
	}

	private void Save()
	{
		var path = Path.Combine(_root, StateFile);
		var temp = path + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(new State(_items, _forget), Json));
		File.Move(temp, path, overwrite: true);
	}

	private static State LoadState(string path)
	{
		if (!File.Exists(path)) return new State([], []);
		var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty");
		return new State(state.Extensions ?? [], state.Forget ?? []);
	}

	private sealed record State(List<InstalledExtension> Extensions, List<string> Forget);
}
