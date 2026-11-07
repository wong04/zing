namespace Zing.Core;

/// <summary>
/// The folder the built React renderer is served from as https://app.zing/, the origin that may call the bridge.
/// Only a folder Zing was told about: never one found by searching, since a renderer someone planted
/// (another user can make C:\out\renderer) would get the whole bridge.
/// </summary>
public static class RendererLocation
{
	/// <param name="configured">ZING_RENDERER_DIR (tests, packaging); wins when set.</param>
	/// <param name="built">The checkout's out/renderer, written into the app when it was built.</param>
	public static string Resolve(string? configured, string? built)
	{
		var folder = !string.IsNullOrEmpty(configured)
			? configured
			: built ?? throw new DirectoryNotFoundException("This build names no renderer folder: set ZING_RENDERER_DIR");
		if (!File.Exists(Path.Combine(folder, "index.html"))) throw new DirectoryNotFoundException($"{folder} has no index.html: run `npm run build`, or set ZING_RENDERER_DIR");
		return folder;
	}
}
