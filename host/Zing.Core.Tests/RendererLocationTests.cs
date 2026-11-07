namespace Zing.Core.Tests;

/// <summary>Where the privileged renderer is loaded from: only a folder Zing was told about, never one found by searching.</summary>
public sealed class RendererLocationTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("zing-renderer-").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private string Renderer(string relative)
	{
		var folder = Path.Combine(_root, relative);
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, "index.html"), "<!doctype html>");
		return folder;
	}

	[Fact]
	public void The_configured_folder_wins()
	{
		var configured = Renderer("configured");
		Assert.Equal(configured, RendererLocation.Resolve(configured, Renderer("built")));
	}

	[Fact]
	public void Without_a_configured_folder_the_built_one_is_used()
	{
		var built = Renderer("built");
		Assert.Equal(built, RendererLocation.Resolve(null, built));
		Assert.Equal(built, RendererLocation.Resolve("", built));
	}

	[Fact]
	public void A_renderer_planted_in_a_parent_folder_is_never_used()
	{
		// Another user can make C:\out\renderer; the old search up from the app found it.
		Renderer("out/renderer");
		var built = Path.Combine(_root, "app", "out", "renderer");
		Assert.Throws<DirectoryNotFoundException>(() => RendererLocation.Resolve(null, built));
	}

	[Fact]
	public void A_build_that_names_no_folder_fails()
	{
		Assert.Throws<DirectoryNotFoundException>(() => RendererLocation.Resolve(null, null));
	}

	[Fact]
	public void A_configured_folder_without_an_index_fails()
	{
		var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
		Assert.Throws<DirectoryNotFoundException>(() => RendererLocation.Resolve(empty, Renderer("built")));
	}
}
