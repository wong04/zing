using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>
/// What typing in the address bar opens. W1 stand-in: W2 ports the full rule
/// (src/shared/browser/addressInput.ts with its real TLD list, plus
/// src/main/address.ts) and checks it against the TypeScript tests' vectors.
/// The engines' URLs come from src/shared/browser/searchEngines.ts via Defaults.
/// </summary>
public sealed class Address(IReadOnlyDictionary<string, string> searchUrls)
{
	public static Address FromDefaults()
	{
		var engines = ZingJson.LoadDefaults("search-engines.json").AsObject();
		return new Address(engines.ToDictionary(e => e.Key, e => e.Value!.GetValue<string>(), StringComparer.Ordinal));
	}

	public sealed class UnknownSearchEngineException(string engine) : Exception($"Unknown search engine {engine}");

	public string Resolve(string input, string searchEngine)
	{
		var text = input.Trim();
		if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "about" or "zing") return absolute.ToString();
		if (!text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var site)) return site.ToString();
		if (!searchUrls.TryGetValue(searchEngine, out var search)) throw new UnknownSearchEngineException(searchEngine);
		return search + Uri.EscapeDataString(text);
	}
}
