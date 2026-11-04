using static Zing.Core.StartShortcuts;

namespace Zing.Core;

/// <summary>The markup of zing://warning. Everything that came from an address is escaped text here.</summary>
internal static class WarningPage
{
	private const string Css = """
  * { box-sizing: border-box; }
  html, body { height: 100%; margin: 0; }
  body {
    background: var(--bg); color: var(--text);
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Helvetica Neue', sans-serif;
    display: flex; align-items: center; justify-content: center; padding: 40px 24px;
    -webkit-font-smoothing: antialiased;
  }
  main { width: 100%; max-width: 560px; }
  h1 { font-size: 22px; font-weight: 600; margin: 0 0 18px; letter-spacing: -0.01em; }
  .host { font-size: 28px; font-weight: 600; margin: 0 0 6px; overflow-wrap: anywhere; }
  .address { font-size: 13px; color: var(--muted); margin: 0 0 22px; overflow-wrap: anywhere; }
  .lede { margin: 0 0 8px; }
  ul { list-style: none; margin: 0 0 18px; padding: 0; display: grid; gap: 10px; }
  li { background: var(--surface); border-radius: 10px; padding: 12px 14px; }
  li strong { display: block; margin-bottom: 2px; }
  li span { color: var(--muted); font-size: 14px; line-height: 1.45; }
  .note { color: var(--muted); font-size: 14px; line-height: 1.45; margin: 0 0 26px; }
  .actions { display: flex; align-items: center; gap: 22px; }
  .primary {
    font: inherit; font-weight: 600; border: 0; cursor: pointer;
    background: var(--accent); color: var(--on-accent);
    padding: 10px 26px; border-radius: 999px;
  }
  .minor { font-size: 13px; color: var(--muted); }
""";

	public const string BackScript = """
document.getElementById('back').addEventListener('click', () => {
  // The flagged page never loaded, so going back lands on the page the link was on. A tab that opened straight on
  // this warning has nowhere to go back to.
  if (history.length > 1) history.back();
  else location.replace('zing://home');
});
""";

	public const string HomeScript = """
document.getElementById('back').addEventListener('click', () => location.replace('zing://home'));
""";

	public static string Html(StartPageTheme theme, string to, string host, ThreatVerdict verdict, string visitUrl, bool viaRelay, string script)
	{
		var reasons = string.Join('\n', verdict.Findings.Select(f => $"      <li><strong>{EscapeHtml(f.Title)}</strong><span>{EscapeHtml(f.Detail)}</span></li>"));
		var relay = viaRelay ? "\n    <p class=\"note\">You asked to open it through Open a Link, and it will go that way.</p>" : "";
		var contact = viaRelay
			? "Nothing was sent anywhere by this check. If a redirect brought you here, the site may already have been asked for the page."
			: "Nothing was sent anywhere, and the site has not been contacted.";
		return Page(theme, "Check this address", $"""
    <h1>Check this address before you go</h1>
    <p class="host">{EscapeHtml(host)}</p>
    <p class="address">{EscapeHtml(to)}</p>
    <p class="lede">Zing looked at the address on this PC and noticed:</p>
    <ul>
{reasons}
    </ul>
    <p class="note">This is a guess from the address alone, so it can be wrong. {contact}</p>{relay}
    <div class="actions">
      <button class="primary" id="back" type="button">Go back</button>
      <a class="minor" href="{EscapeHtml(visitUrl)}">Visit anyway</a>
    </div>
""", script);
	}

	public static string RefusalHtml(StartPageTheme theme, string message) =>
		Page(theme, "Nothing to check", $"    <h1>{EscapeHtml(message)}</h1>", "");

	private static string Page(StartPageTheme theme, string title, string body, string script)
	{
		var scriptTag = script == "" ? "" : $"\n<script>\n{script}\n</script>";
		return $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>{EscapeHtml(title)}</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<style id="{StartPage.ThemeStyleId}">
{StartPage.ThemeCss(theme)}
</style>
<style>
{Css}
</style>
</head>
<body>
  <main>
{body}
  </main>{scriptTag}
</body>
</html>

""";
	}
}
