using System.Text.Json;

namespace Zing.Core.Tests;

/// <summary>
/// Test cases shared with the TypeScript tests: `*.vectors.json` files under
/// src/shared, read by vitest and xUnit alike, so the C# port and the TS
/// original are held to exactly the same cases.
/// </summary>
public static class Vectors
{
	public static JsonElement Load(string repoRelativePath)
	{
		var path = Path.Combine(RepoRoot(), repoRelativePath);
		if (!File.Exists(path)) throw new FileNotFoundException($"No vectors at {path}", path);
		return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
	}

	private static string RepoRoot()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, "package.json")) && Directory.Exists(Path.Combine(dir.FullName, "host"))) return dir.FullName;
		throw new DirectoryNotFoundException("No repo root (package.json beside host/) above the test assembly");
	}
}
