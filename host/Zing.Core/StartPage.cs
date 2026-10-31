using System.Text;
using System.Text.Json.Nodes;

namespace Zing.Core;

/// <summary>One appearance's colours (src/shared/browser/startPage.ts StartPageTokens).</summary>
public sealed record StartPageTokens(
	string Background,
	string Surface,
	string Tint,
	string Well,
	string Outline,
	string Accent,
	string OnAccent,
	string Text,
	string TextSecondary,
	string Control,
	string Raised,
	string AccentInk);

public sealed record StartPageTheme(StartPageTokens Light, StartPageTokens Dark);

/// <param name="SearchUrl">Search endpoint the form submits to, e.g. <c>https://www.google.com/search</c>.</param>
public sealed record StartPageInput(string SearchUrl, IReadOnlyList<StartShortcut> Shortcuts, StartPageTheme Theme);

/// <summary>A theme token that is not a plain CSS value (src/shared/browser/startPage.ts ThemeTokenError).</summary>
public sealed class ThemeTokenException(string name, string value)
	: Exception($"theme token {name} is not a plain CSS value: {StartShortcuts.JsString(value)}");

/// <summary>The search engine address is not http(s) (src/shared/browser/startPage.ts SearchURLError).</summary>
public sealed class SearchUrlException(string searchUrl)
	: Exception($"the search engine must be an http(s) address: {StartShortcuts.JsString(searchUrl)}");

/// <summary>A zing: URL the start page does not serve (src/main/start-page.ts StartPageSchemeError).</summary>
public sealed class StartPageSchemeException(string url) : Exception($"{url} is not a page the zing: scheme serves")
{
	public string Url { get; } = url;
}

/// <summary>What a navigation starting on the start page, or heading for its special addresses, should do.</summary>
public abstract record StartPageNavigation
{
	/// <summary>The start page's own search form was submitted: run <see cref="Query"/> through the address bar's rule.</summary>
	public sealed record Search(string Query) : StartPageNavigation;

	/// <summary>The "Edit shortcuts" link, from the start page: cancel the navigation and open Settings, New Tab.</summary>
	public sealed record EditShortcuts : StartPageNavigation;

	/// <summary>A link to the edit-shortcuts address from anywhere but the start page: cancel it and do nothing.</summary>
	public sealed record Block : StartPageNavigation;

	public sealed record Allow : StartPageNavigation;
}

public sealed record StartPageResponse(int Status, string ContentType, string CacheControl, string Body);

/// <summary>
/// The zing://home page, generated from settings and theme tokens (src/shared/browser/startPage.ts and
/// src/main/start-page.ts). The output is held byte-for-byte to the TypeScript by startPage.vectors.json.
/// </summary>
public static class StartPage
{
	public const string Scheme = "zing";
	public const string Url = "zing://home";
	public const string EditShortcutsHost = "edit-shortcuts";
	public const string EditShortcutsUrl = "zing://edit-shortcuts";
	/// <summary>The <c>&lt;style&gt;</c> holding the palette, swapped in place on a theme change.</summary>
	public const string ThemeStyleId = "zing-theme";

	private static readonly Lazy<JsonObject> SearchUrls = new(() => ZingJson.LoadDefaults("search-engines.json").AsObject());

	// MARK: - Theme

	public static StartPageTokens Tokens(PaletteTokens p) => new(
		p.Background, p.Surface, p.Tint, p.Well, p.Outline, p.Accent, p.OnAccent, p.Text, p.TextSecondary,
		Palette.Control(p), Palette.Raised(p), Palette.AccentInk(p));

	public static StartPageTheme Theme(string accent) => new(Tokens(Palette.LightTokens(accent)), Tokens(Palette.DarkTokens(accent)));

	private static string CssValue(string name, string value)
	{
		if (AddressInput.JsTrim(value) == "" || value.IndexOfAny(new[] {';', '{', '}', '<', '>', '\\' }) >= 0 || value.Contains("/*", StringComparison.Ordinal)) {
			throw new ThemeTokenException(name, value);
		}
		return value;
	}

	private static string TokenBlock(StartPageTokens t, string indent)
	{
		string V(string name, string value) => CssValue(name, value);
		string[] lines =
		[
			$"--bg: {V("background", t.Background)};      --surface: {V("surface", t.Surface)};",
			$"--tint: {V("tint", t.Tint)};          --well: {V("well", t.Well)};",
			$"--outline: {V("outline", t.Outline)};    --accent: {V("accent", t.Accent)};",
			$"--on-accent: {V("onAccent", t.OnAccent)}; --text: {V("text", t.Text)};",
			$"--muted: {V("textSecondary", t.TextSecondary)};",
			$"--control: {V("control", t.Control)};   --raised: {V("raised", t.Raised)};",
			$"--accent-ink: {V("accentInk", t.AccentInk)};",
			"--separator: color-mix(in srgb, var(--outline) 13%, transparent);",
		];
		return string.Join('\n', lines.Select(line => indent + line));
	}

	/// <summary>Both appearances, selected with <c>prefers-color-scheme</c>; in a style of its own so a theme change can swap it in place.</summary>
	public static string ThemeCss(StartPageTheme theme) => string.Join('\n',
		"  :root {",
		TokenBlock(theme.Light, "    "),
		"  }",
		"  @media (prefers-color-scheme: dark) {",
		"    :root {",
		TokenBlock(theme.Dark, "      "),
		"    }",
		"  }");

	/// <summary>Run in an open start page to recolour it without a reload.</summary>
	public static string RestyleScript(StartPageTheme theme) =>
		$"(() => {{ const s = document.getElementById('{ThemeStyleId}'); if (s) s.textContent = {StartShortcuts.JsString(ThemeCss(theme))}; }})()";

	// MARK: - Page

	private static string SearchOrigin(string searchUrl)
	{
		var uri = StartShortcuts.WebUri(searchUrl) ?? throw new SearchUrlException(searchUrl);
		return $"{uri.Scheme}://{StartShortcuts.Hostname(uri)}";
	}

	private static string TileHtml(StartShortcut shortcut) =>
		$"""
		    <a class="tile" href="{StartShortcuts.EscapeHtml(shortcut.Url)}" title="{StartShortcuts.EscapeHtml(shortcut.Title)}">
		      <span class="glyph">{StartShortcuts.EscapeHtml(StartShortcuts.Glyph(shortcut))}</span>
		      <span class="label">{StartShortcuts.EscapeHtml(shortcut.Title)}</span>
		    </a>
		""";

	private static string PrefetchHtml(StartShortcut shortcut) =>
		$"<link rel=\"dns-prefetch\" href=\"https://{StartShortcuts.EscapeHtml(StartShortcuts.Hostname(StartShortcuts.WebUri(shortcut.Url)!))}\">";

	/// <summary>Builds the zing://home page. Only http(s) shortcuts become tiles.</summary>
	public static string Html(StartPageInput input)
	{
		var origin = StartShortcuts.EscapeHtml(SearchOrigin(input.SearchUrl));
		var tiles = input.Shortcuts.Where(s => StartShortcuts.IsWebAddress(s.Url)).ToList();
		var prefetch = string.Join('\n', tiles.Select(PrefetchHtml));
		var tileHtml = string.Join('\n', tiles.Select(TileHtml));
		var themeCss = ThemeCss(input.Theme);
		return $$"""
		<!doctype html>
		<html>
		<head>
		<meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<link rel="preconnect" href="{{origin}}" crossorigin>
		<link rel="preconnect" href="{{origin}}">
		{{prefetch}}
		<style id="{{ThemeStyleId}}">
		{{themeCss}}
		</style>
		<style>
		{{PageCss}}
		</style>
		</head>
		<body>
		  <div class="clock">
		    <div class="time">
		      <span id="hh">--</span><span class="colon" id="c1">:</span><span id="mm">--</span><span class="seconds" id="ss"></span><span class="meridiem" id="ap"></span>
		    </div>
		    <div class="date" id="date"></div>
		  </div>

		  <form action="{{StartShortcuts.EscapeHtml(input.SearchUrl)}}" method="GET" autocomplete="off">
		    <div class="search">
		      <svg width="15" height="15" viewBox="0 0 16 16" fill="none"
		           stroke="currentColor" stroke-width="1.8">
		        <circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5L14 14"/>
		      </svg>
		      <input type="text" name="q" placeholder="Search the web…" autofocus>
		    </div>
		  </form>

		  <div class="tiles">
		{{tileHtml}}
		  </div>
		  <a class="edit" href="{{EditShortcutsUrl}}">Edit shortcuts</a>

		<script>
		{{PageScript}}
		</script>
		</body>
		</html>

		""";
	}

	// MARK: - Settings and requests (src/main/start-page.ts)

	/// <summary>The engine's search endpoint without the query, for a form that adds <c>?q=</c> itself.</summary>
	public static string SearchFormAction(string engine)
	{
		var url = SearchUrls.Value[engine]?.GetValue<string>() ?? throw new Address.UnknownSearchEngineException(engine);
		return url.EndsWith("?q=", StringComparison.Ordinal) ? url[..^3] : url;
	}

	private static JsonNode? Setting(JsonObject settings, string key) =>
		settings.TryGetPropertyValue(key, out var value) ? value : throw new UnknownSettingException(key);

	private static string TextSetting(JsonObject settings, string key) =>
		Setting(settings, key) is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidSettingException(key, Setting(settings, key));

	/// <summary>The page for the current settings: search engine, shortcuts and accent.</summary>
	public static string Html(JsonObject settings) => Html(new StartPageInput(
		SearchFormAction(TextSetting(settings, "searchEngine")),
		StartShortcuts.FromSetting(Setting(settings, "startShortcuts")),
		Theme(TextSetting(settings, "accent"))));

	/// <summary>
	/// The host of a zing: URL as the URL standard reads it: zing is not a special scheme, so the host keeps its
	/// case. Null for anything that is not a zing: URL with a host.
	/// </summary>
	private static string? ZingHost(string url)
	{
		if (!url.StartsWith("zing://", StringComparison.OrdinalIgnoreCase)) return null;
		var authority = url["zing://".Length..].Split('/', '?', '#')[0];
		var hostAndPort = authority[(authority.LastIndexOf('@') + 1)..];
		var colon = hostAndPort.LastIndexOf(':');
		return colon >= 0 && hostAndPort[(colon + 1)..].All(char.IsAsciiDigit) ? hostAndPort[..colon] : hostAndPort;
	}

	/// <summary>Whether this is the home page itself, with or without a trailing slash.</summary>
	public static bool IsStartPage(string url) => ZingHost(url) == "home";

	/// <summary>Serves the zing: scheme. Throws <see cref="StartPageSchemeException"/> for any page it doesn't serve.</summary>
	public static StartPageResponse Handle(string url, JsonObject settings)
	{
		if (!IsStartPage(url)) throw new StartPageSchemeException(url);
		return new StartPageResponse(200, "text/html; charset=utf-8", "no-store", Html(settings));
	}

	/// <summary>
	/// What a navigation from <paramref name="fromUrl"/> to <paramref name="toUrl"/> should do. Only the start
	/// page's own search form and Edit shortcuts link are special, so no website can open Settings by linking
	/// to the edit-shortcuts address.
	/// </summary>
	public static StartPageNavigation ClassifyNavigation(string fromUrl, string toUrl, string engine)
	{
		if (!Uri.TryCreate(toUrl, UriKind.Absolute, out var to)) return new StartPageNavigation.Allow();
		var fromStartPage = IsStartPage(fromUrl);
		if (ZingHost(toUrl) == EditShortcutsHost) {
			return fromStartPage ? new StartPageNavigation.EditShortcuts() : new StartPageNavigation.Block();
		}
		if (!fromStartPage) return new StartPageNavigation.Allow();
		var action = new Uri(SearchFormAction(engine));
		var query = QueryParam(toUrl, "q");
		if (query is null || to.Scheme != action.Scheme || !to.Authority.Equals(action.Authority, StringComparison.OrdinalIgnoreCase) || to.AbsolutePath != action.AbsolutePath) {
			return new StartPageNavigation.Allow();
		}
		return new StartPageNavigation.Search(query);
	}

	/// <summary>URLSearchParams.get: the first value for <paramref name="name"/>, form-decoded, or null.</summary>
	private static string? QueryParam(string url, string name)
	{
		var start = url.IndexOf('?');
		if (start < 0) return null;
		var end = url.IndexOf('#', start);
		var query = end < 0 ? url[(start + 1)..] : url[(start + 1)..end];
		foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var eq = pair.IndexOf('=');
			var key = FormDecode(eq < 0 ? pair : pair[..eq]);
			if (key == name) return eq < 0 ? "" : FormDecode(pair[(eq + 1)..]);
		}
		return null;
	}

	private static string FormDecode(string text)
	{
		var bytes = new List<byte>();
		var plain = Encoding.UTF8.GetBytes(text.Replace('+', ' '));
		for (var i = 0; i < plain.Length; i++) {
			var isEscape = plain[i] == '%' && i + 2 < plain.Length && Uri.IsHexDigit((char)plain[i + 1]) && Uri.IsHexDigit((char)plain[i + 2]);
			if (!isEscape) {
				bytes.Add(plain[i]);
				continue;
			}
			bytes.Add((byte)((Uri.FromHex((char)plain[i + 1]) << 4) | Uri.FromHex((char)plain[i + 2])));
			i += 2;
		}
		return Encoding.UTF8.GetString([.. bytes]);
	}

	private const string PageCss = """
  * { box-sizing: border-box; }
  html, body { height: 100%; margin: 0; }
  body {
    background: var(--bg);
    color: var(--text);
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Helvetica Neue', sans-serif;
    display: flex; flex-direction: column;
    align-items: center; justify-content: center;
    gap: 34px; padding: 40px 24px;
    -webkit-font-smoothing: antialiased;
  }
  /* Clock: tabular figures so the digits never shift width as the seconds
     tick over. */
  .clock { text-align: center; user-select: none; }
  .clock .time {
    font-size: 72px; font-weight: 600; line-height: 1;
    letter-spacing: -0.04em;
    font-variant-numeric: tabular-nums;
    font-feature-settings: "tnum" 1;
    display: flex; align-items: baseline; justify-content: center; gap: 2px;
  }
  .clock .seconds {
    font-size: 28px; font-weight: 500;
    color: var(--muted); letter-spacing: -0.01em;
    font-variant-numeric: tabular-nums;
  }
  .clock .meridiem {
    font-size: 22px; font-weight: 500; color: var(--muted);
    margin-left: 6px; letter-spacing: 0;
  }
  .clock .date {
    margin-top: 10px; font-size: 14px; font-weight: 500;
    color: var(--muted);
  }
  /* The colon blinks on the half-second, as the system clock does. */
  .colon { transition: opacity .18s ease; }

  /* Same field treatment as the native app: a recessed control with a
     hairline, and a soft focus ring outside it rather than a recoloured
     border. */
  form { width: 100%; max-width: 560px; }
  .search {
    display: flex; align-items: center; gap: 10px;
    background: var(--control);
    border: 1px solid var(--separator);
    border-radius: 12px;
    padding: 12px 16px;
    transition: box-shadow .15s ease, background .15s ease;
  }
  .search:focus-within {
    background: var(--raised);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent) 55%, transparent);
  }
  .search svg { flex: none; opacity: .5; }
  input[type=text] {
    flex: 1; border: 0; outline: 0; background: transparent;
    color: var(--text); font-size: 15px; font-family: inherit;
  }
  input::placeholder { color: var(--muted); }

  .tiles {
    display: grid; gap: 10px; width: 100%; max-width: 560px;
    grid-template-columns: repeat(auto-fit, minmax(122px, 1fr));
  }
  .tile {
    display: flex; align-items: center; gap: 9px;
    padding: 10px 12px; border-radius: 12px;
    background: var(--surface);
    border: 1px solid var(--separator);
    box-shadow: 0 1px 3px rgba(0,0,0,.04);
    color: var(--text); text-decoration: none;
    font-size: 12.5px; font-weight: 500;
    transition: box-shadow .15s ease;
  }
  .tile:hover { box-shadow: 0 3px 10px rgba(0,0,0,.09); }
  .tile .glyph {
    width: 26px; height: 26px; flex: none; border-radius: 7px;
    background: color-mix(in srgb, var(--accent) 18%, transparent);
    color: var(--accent-ink);
    display: grid; place-items: center; font-size: 13px;
  }
  .tile .label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .hint { color: var(--muted); font-size: 11.5px; }
  /* Quiet until you're near the tiles: it's for now and then. */
  .edit {
    margin-top: -22px; color: var(--muted); font-size: 11.5px;
    text-decoration: none; opacity: 0; transition: opacity .2s ease;
    padding: 4px 8px; border-radius: 6px;
  }
  .tiles:hover + .edit, .edit:hover, .edit:focus { opacity: 1; }
  .edit:hover { background: var(--well); color: var(--text); }
""";

	private const string PageScript = """
(function () {
  var hh = document.getElementById('hh'), mm = document.getElementById('mm'),
      ss = document.getElementById('ss'), ap = document.getElementById('ap'),
      c1 = document.getElementById('c1'), dateEl = document.getElementById('date'),
      input = document.querySelector('input[name=q]');

  // Whether the system is on a 12-hour clock, taken from the locale
  // rather than assumed.
  var uses12 = new Intl.DateTimeFormat([], { hour: 'numeric' })
      .resolvedOptions().hour12;
  var dateFmt = new Intl.DateTimeFormat([], {
    weekday: 'long', month: 'long', day: 'numeric'
  });

  function pad(n) { return n < 10 ? '0' + n : '' + n; }

  function tick() {
    var now = new Date();
    var h = now.getHours();
    if (uses12) {
      ap.textContent = h < 12 ? 'AM' : 'PM';
      h = h % 12; if (h === 0) h = 12;
      hh.textContent = h;
    } else {
      ap.textContent = '';
      hh.textContent = pad(h);
    }
    mm.textContent = pad(now.getMinutes());
    ss.textContent = pad(now.getSeconds());
    // Blink in step with the seconds hand.
    c1.style.opacity = now.getMilliseconds() < 500 ? '1' : '0.35';
    dateEl.textContent = dateFmt.format(now);
  }

  tick();
  // 100ms keeps the colon blink smooth without a per-frame timer.
  setInterval(tick, 100);

  // Type-to-search: any printable key anywhere on the page focuses the
  // box and starts the query, so a new tab needs no click first.
  function focusSearch() { if (input) { input.focus(); } }
  focusSearch();
  window.addEventListener('focus', focusSearch);
  document.addEventListener('click', function (e) {
    if (!e.target.closest('a')) focusSearch();
  });
  document.addEventListener('keydown', function (e) {
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    if (document.activeElement === input) return;
    // Single printable character, or a correction key.
    if (e.key.length === 1 || e.key === 'Backspace') {
      input.focus();
    }
  });
})();
""";
}
