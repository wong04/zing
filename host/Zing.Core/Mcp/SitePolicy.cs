using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zing.Core.Mcp;

public enum SiteLevel { AskSubmit, AskAll, Never }

/// <summary>The ai-sites file exists but can't be read or isn't a map of site to level.</summary>
public sealed class SitePolicyFileException(string path, Exception inner)
	: Exception($"Could not read the AI site levels from {path}: {inner.Message}", inner)
{
	public string Path { get; } = path;
}

public sealed class NoSiteException(Uri url) : InvalidOperationException($"{url} has no site a level can be set for.");

/// <summary>
/// How much an AI client may do on each site before the user is asked. Keyed by the PublicSuffix site, so alice.github.io and
/// bob.github.io are separate. Only levels other than the default are stored, and the file is deleted when none are left.
/// </summary>
public sealed class SitePolicy
{
	public const SiteLevel Default = SiteLevel.AskSubmit;

	private readonly string _path;
	private readonly SortedDictionary<string, SiteLevel> _levels = new(StringComparer.Ordinal);

	public SitePolicy(string path)
	{
		_path = path;
		if (!File.Exists(path)) return;
		try
		{
			var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("The file is empty");
			foreach (var (site, node) in root)
			{
				var name = node?.GetValue<string>();
				// By name only: Enum.TryParse would also take "1".
				if (name is null || !Enum.GetNames<SiteLevel>().Contains(name) || !Enum.TryParse<SiteLevel>(name, out var level)) throw new InvalidDataException($"Unknown level '{name}' for {site}");
				_levels[site] = level;
			}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException or FormatException)
		{
			throw new SitePolicyFileException(path, error);
		}
	}

	/// <summary>Whether any level is stored; the privacy banner warns while it is.</summary>
	public bool HasRules => _levels.Count > 0;

	public IReadOnlyDictionary<string, SiteLevel> All() => new Dictionary<string, SiteLevel>(_levels);

	public SiteLevel LevelFor(Uri url)
	{
		var site = SiteOf(url);
		return site is not null && _levels.TryGetValue(site, out var level) ? level : Default;
	}

	public void Set(Uri url, SiteLevel level)
	{
		var site = SiteOf(url) ?? throw new NoSiteException(url);
		var next = new SortedDictionary<string, SiteLevel>(_levels, StringComparer.Ordinal);
		if (level == Default) next.Remove(site);
		else next[site] = level;
		// Saved first, so a failed write leaves memory matching the file.
		Save(next);
		if (level == Default) _levels.Remove(site);
		else _levels[site] = level;
	}

	public bool NeedsConfirm(Uri url, AiAction action, AxNode target, string? key) => LevelFor(url) switch
	{
		SiteLevel.AskAll => true,
		SiteLevel.Never => false,
		_ => SubmitDetector.IsSubmit(action, target, key),
	};

	private static string? SiteOf(Uri url) => PublicSuffix.RegistrableDomain(url.IdnHost);

	private void Save(IReadOnlyDictionary<string, SiteLevel> levels)
	{
		if (levels.Count == 0)
		{
			File.Delete(_path);
			return;
		}
		JsonFiles.WriteJsonAtomic(_path, levels.ToDictionary(l => l.Key, l => l.Value.ToString()));
	}
}
