namespace Zing.Core.Import;

/// <summary>Reads the https handler's ProgId from the OS. Zing.Host implements it, so Zing.Core stays free of the registry.</summary>
public interface IDefaultBrowserReader
{
	/// <summary>The ProgId, or null when no default browser is set.</summary>
	string? HttpsProgId();
}

/// <summary>Names the import source that belongs to the user's default browser.</summary>
public static class DefaultBrowserSource
{
	// A side-by-side install (Beta, Dev, Canary, or one per user) registers the base ProgId plus "." and a suffix.
	private static readonly (string ProgId, ImportBrowser Browser)[] ChromiumProgIds =
	[
		("ChromeHTML", ImportBrowser.Chrome),
		("MSEdgeHTM", ImportBrowser.Edge),
		("BraveHTML", ImportBrowser.Brave),
	];

	public static ImportBrowser? BrowserFor(string? progId)
	{
		if (string.IsNullOrEmpty(progId)) return null;
		// Arc's ProgId isn't known yet: any "Arc..." matches. To verify in the owner's hand check (Task 16).
		if (progId.StartsWith("Arc", StringComparison.OrdinalIgnoreCase)) return ImportBrowser.Arc;
		foreach (var (known, browser) in ChromiumProgIds)
		{
			if (progId.Equals(known, StringComparison.OrdinalIgnoreCase)) return browser;
			if (progId.Length > known.Length + 1 && progId.StartsWith(known + ".", StringComparison.OrdinalIgnoreCase)) return browser;
		}
		return null;
	}

	public static ImportSource? Pick(string? progId, IReadOnlyList<ImportSource> sources)
	{
		if (BrowserFor(progId) is not { } browser) return null;
		var usable = sources.Where(s => s.Browser == browser && s.Error is null).ToList();
		return usable.FirstOrDefault(s => s.LastUsed) ?? usable.FirstOrDefault();
	}
}
