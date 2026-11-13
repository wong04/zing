using System.Security.Cryptography;
using System.Text;

namespace Zing.Core;

/// <summary>
/// The icons of bookmarked sites the person has visited, one small PNG per bookmarked address in a folder beside library.json.
/// Favicons otherwise live in memory only (PARITY B22); a bookmark's is kept because the person chose to keep the bookmark.
/// Nothing is ever fetched for this: an icon is stored only from a page the engine loaded and already had the icon for, so a
/// bookmark whose site hasn't been visited (an imported one, say) stays blank.
/// </summary>
public sealed class BookmarkIcons
{
	/// <summary>A favicon is a few KB; anything bigger isn't a favicon worth keeping, and isn't stored.</summary>
	public const int MaxBytes = 64 * 1024;

	private const string DataUrlPrefix = "data:image/png;base64,";
	private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	private readonly string _directory;
	/// <summary>Data URLs by file name; null means the file is known to be absent.</summary>
	private readonly Dictionary<string, string?> _cache = [];
	private readonly List<Action> _listeners = [];

	public BookmarkIcons(string directory) => _directory = directory;

	/// <summary>
	/// What makes a page the page a bookmark points at. Two addresses match when their keys are equal:
	/// http or https alike; the host lowercased, with a leading "www." dropped; the port only when it isn't the scheme's
	/// default; the path with any trailing "/" dropped. The query and fragment are ignored. So the host and path must agree:
	/// mail.google.com never matches docs.google.com, and docs.google.com/document never matches docs.google.com/spreadsheets,
	/// which is the mistake Krackn's per-site icons made (src/shared/browser/faviconChoice.ts). A redirect to another address
	/// (gmail.com to mail.google.com) doesn't match either, so such a bookmark stays blank. Null for anything not http(s).
	/// </summary>
	public static string? MatchKey(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
		var host = uri.Host.ToLowerInvariant();
		if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
		var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
		return $"{host}{port}{uri.AbsolutePath.TrimEnd('/')}";
	}

	/// <summary>Called whenever the stored icons change.</summary>
	public Action Subscribe(Action listener)
	{
		_listeners.Add(listener);
		return () => _listeners.Remove(listener);
	}

	/// <summary>
	/// A page at <paramref name="pageUrl"/> loaded with this icon (a PNG data URL, as the engine gave it). Stored when a link in the
	/// library matches the page, replacing an older icon that differs. Returns whether anything was written.
	/// </summary>
	public bool Visited(BrowserLibrary library, string pageUrl, string dataUrl)
	{
		if (MatchKey(pageUrl) is not { } key || !Links(library).Any(link => MatchKey(link.Url) == key)) return false;
		if (Png(dataUrl) is not { } png) return false;
		var name = FileName(key);
		var url = DataUrlPrefix + Convert.ToBase64String(png);
		if (Read(name) == url) return false;
		JsonFiles.WriteAllBytesAtomic(Path.Combine(_directory, name), png);
		_cache[name] = url;
		foreach (var listener in _listeners.ToList()) listener();
		return true;
	}

	/// <summary>Each link's stored icon as a data URL, by bookmark id; links with none are left out.</summary>
	public IReadOnlyDictionary<string, string> ForLibrary(BrowserLibrary library)
	{
		var icons = new Dictionary<string, string>();
		foreach (var link in Links(library))
		{
			if (MatchKey(link.Url) is { } key && Read(FileName(key)) is { } url) icons[link.Id] = url;
		}
		return icons;
	}

	/// <summary>Deletes every icon no link in the library points at any more: a bookmark removed, or moved to another address.</summary>
	public void Prune(BrowserLibrary library)
	{
		if (!Directory.Exists(_directory)) return;
		var wanted = Links(library).Select(link => MatchKey(link.Url)).OfType<string>().Select(FileName).ToHashSet();
		var removed = false;
		foreach (var path in Directory.EnumerateFiles(_directory, "*.png"))
		{
			var name = Path.GetFileName(path);
			// Windows' "*.png" also matches longer extensions such as ".pngx".
			if (!name.EndsWith(".png", StringComparison.Ordinal) || wanted.Contains(name)) continue;
			File.Delete(path);
			_cache[name] = null;
			removed = true;
		}
		if (removed) foreach (var listener in _listeners.ToList()) listener();
	}

	/// <summary>The key's file: a hash, so the folder never spells out an address.</summary>
	public static string FileName(string key) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".png";

	/// <summary>The PNG in a data URL, or null when it isn't one or is bigger than <see cref="MaxBytes"/>.</summary>
	public static byte[]? Png(string dataUrl)
	{
		if (!dataUrl.StartsWith(DataUrlPrefix, StringComparison.Ordinal)) return null;
		var base64 = dataUrl.AsSpan(DataUrlPrefix.Length);
		// Base64 is 4 characters for every 3 bytes, so a longer string can't decode to MaxBytes or less.
		if (base64.Length > (MaxBytes + 2) / 3 * 4) return null;
		var bytes = new byte[base64.Length / 4 * 3 + 3];
		if (!Convert.TryFromBase64Chars(base64, bytes, out var written)) return null;
		var png = bytes[..written];
		return png.Length <= MaxBytes && png.AsSpan().StartsWith(PngSignature) ? png : null;
	}

	private string? Read(string name)
	{
		if (_cache.TryGetValue(name, out var cached)) return cached;
		var path = Path.Combine(_directory, name);
		string? url = null;
		try
		{
			url = DataUrlPrefix + Convert.ToBase64String(File.ReadAllBytes(path));
		}
		catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
		{
		}
		_cache[name] = url;
		return url;
	}

	private static IEnumerable<BookmarkLink> Links(BrowserLibrary library) => library.Bookmarks.SelectMany(LinksIn);

	private static IEnumerable<BookmarkLink> LinksIn(BookmarkNode node) => node switch
	{
		BookmarkLink link => [link],
		BookmarkFolder folder => folder.Children.SelectMany(LinksIn),
		_ => throw new NotSupportedException($"Unknown bookmark node {node.GetType().Name}"),
	};
}
