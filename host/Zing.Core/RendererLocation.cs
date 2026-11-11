namespace Zing.Core;

/// <summary>
/// The folder the built React renderer is served from as https://app.zing/, the origin that may call the bridge.
/// Only a folder Zing was told about: never one found by searching, since a renderer someone planted
/// (another user can make C:\out\renderer) would get the whole bridge.
/// </summary>
public static class RendererLocation
{
	/// <param name="configured">ZING_RENDERER_DIR (tests, packaging); wins when set.</param>
	/// <param name="built">The renderer folder written into the app when it was built: the checkout's out/renderer (absolute), or a folder beside the exe (relative, in a release).</param>
	/// <param name="appFolder">The folder the exe is in; a relative <paramref name="built"/> is resolved against it.</param>
	public static string Resolve(string? configured, string? built, string appFolder)
	{
		var folder = !string.IsNullOrEmpty(configured)
			? configured
			: built is null
				? throw new DirectoryNotFoundException("This build names no renderer folder: set ZING_RENDERER_DIR")
				: BesideApp(built, appFolder);
		if (!File.Exists(Path.Combine(folder, "index.html"))) throw new DirectoryNotFoundException($"{folder} has no index.html: run `npm run build`, or set ZING_RENDERER_DIR");
		return folder;
	}

	private static string BesideApp(string built, string appFolder)
	{
		if (Path.IsPathRooted(built)) return built;
		var app = Path.GetFullPath(appFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		var folder = Path.GetFullPath(Path.Combine(app, built));
		if (!folder.StartsWith(app, StringComparison.OrdinalIgnoreCase)) throw new DirectoryNotFoundException($"The renderer folder {built} leaves the app folder");
		return folder;
	}
}
