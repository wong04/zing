// Port of Krackn's `StartPage` (Features/Browser/StartPage.swift): the new-tab page,
// served from the app rather than the network, so opening a tab never touches the
// network and can never land on a blocked or filtered page.
import { escapeHTML as escape, glyph, isWebAddress, type StartShortcut } from './startShortcuts'

export const startPageScheme = 'zing'
export const startPageURL = 'zing://home'
/**
 * The page's "Edit shortcuts" link. Never loaded: the host catches it, from
 * this page only, and opens Settings › New Tab — so no website can pop
 * Settings open by linking here.
 */
export const editShortcutsHost = 'edit-shortcuts'
export const editShortcutsURL = `${startPageScheme}://${editShortcutsHost}`
/** The `<style>` holding the palette, swapped in place on a theme change. */
export const themeStyleId = 'zing-theme'

/** One appearance's colours (Krackn's `PaletteTokens` plus its derived roles). */
export interface StartPageTokens {
	background: string
	surface: string
	tint: string
	well: string
	outline: string
	accent: string
	onAccent: string
	text: string
	textSecondary: string
	control: string
	raised: string
	accentInk: string
}

export interface StartPageTheme {
	light: StartPageTokens
	dark: StartPageTokens
}

export interface StartPageInput {
	/** Search endpoint the form submits to, e.g. `https://www.google.com/search`. */
	searchURL: string
	shortcuts: readonly StartShortcut[]
	theme: StartPageTheme
}

export class ThemeTokenError extends Error {
	override name = 'ThemeTokenError'
}

export class SearchURLError extends Error {
	override name = 'SearchURLError'
}

// Tokens are written into a <style>: anything that could end a declaration, a
// rule or the element itself would let a token rewrite the page.
const unsafeInCSS = /[;{}<>\\]|\/\*/

function cssValue(name: string, value: string): string {
	if (value.trim() === '' || unsafeInCSS.test(value)) {
		throw new ThemeTokenError(`theme token ${name} is not a plain CSS value: ${JSON.stringify(value)}`)
	}
	return value
}

function tokenBlock(tokens: StartPageTokens, indent: string): string {
	const v = (name: keyof StartPageTokens): string => cssValue(name, tokens[name])
	return [
		`--bg: ${v('background')};      --surface: ${v('surface')};`,
		`--tint: ${v('tint')};          --well: ${v('well')};`,
		`--outline: ${v('outline')};    --accent: ${v('accent')};`,
		`--on-accent: ${v('onAccent')}; --text: ${v('text')};`,
		`--muted: ${v('textSecondary')};`,
		`--control: ${v('control')};   --raised: ${v('raised')};`,
		`--accent-ink: ${v('accentInk')};`,
		'--separator: color-mix(in srgb, var(--outline) 13%, transparent);',
	]
		.map((line) => indent + line)
		.join('\n')
}

/**
 * The palette, in a `<style>` of its own so a theme change can swap it in
 * place (`restyleScript`) instead of reloading. Both appearances are emitted
 * and selected with `prefers-color-scheme`; the host pins the page's colour
 * scheme to the app's theme, so "Light" stays light on a dark system.
 */
export function themeCSS(theme: StartPageTheme): string {
	return [
		'  :root {',
		tokenBlock(theme.light, '    '),
		'  }',
		'  @media (prefers-color-scheme: dark) {',
		'    :root {',
		tokenBlock(theme.dark, '      '),
		'    }',
		'  }',
	].join('\n')
}

/**
 * Run in an open start page to recolour it without a reload (Krackn's
 * `restyleStartPages`). `JSON.stringify` makes the CSS a safe JS string literal.
 */
export function restyleScript(theme: StartPageTheme): string {
	return `(() => { const s = document.getElementById('${themeStyleId}'); if (s) s.textContent = ${JSON.stringify(themeCSS(theme))}; })()`
}

function searchOrigin(searchURL: string): string {
	if (!isWebAddress(searchURL)) {
		throw new SearchURLError(`the search engine must be an http(s) address: ${JSON.stringify(searchURL)}`)
	}
	const url = new URL(searchURL)
	return `${url.protocol}//${url.hostname}`
}

// Your own names and addresses go into this page, so every one is escaped: a
// tile called `<script>` is just a tile with brackets. Only http(s) addresses
// become tiles; `javascript:` must never reach an href.
function tileHTML(shortcut: StartShortcut): string {
	return `    <a class="tile" href="${escape(shortcut.url)}" title="${escape(shortcut.title)}">
      <span class="glyph">${escape(glyph(shortcut))}</span>
      <span class="label">${escape(shortcut.title)}</span>
    </a>`
}

// Warm DNS for each tile's site while the page is just sitting there.
function prefetchHTML(shortcut: StartShortcut): string {
	return `<link rel="dns-prefetch" href="https://${escape(new URL(shortcut.url).hostname)}">`
}

const pageCSS = `  * { box-sizing: border-box; }
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
  .edit:hover { background: var(--well); color: var(--text); }`

const pageScript = `(function () {
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
})();`

/** Builds the zing://home page. */
export function startPageHTML({ searchURL, shortcuts, theme }: StartPageInput): string {
	// Warm DNS, TCP and TLS for the search engine while the page is just
	// sitting there. By the time a query is submitted the connection is
	// already open, which removes the handshake from the critical path —
	// the bulk of the delay between typing and seeing results.
	const origin = escape(searchOrigin(searchURL))
	const tiles = shortcuts.filter((shortcut) => isWebAddress(shortcut.url))

	return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="preconnect" href="${origin}" crossorigin>
<link rel="preconnect" href="${origin}">
${tiles.map(prefetchHTML).join('\n')}
<style id="${themeStyleId}">
${themeCSS(theme)}
</style>
<style>
${pageCSS}
</style>
</head>
<body>
  <div class="clock">
    <div class="time">
      <span id="hh">--</span><span class="colon" id="c1">:</span><span id="mm">--</span><span class="seconds" id="ss"></span><span class="meridiem" id="ap"></span>
    </div>
    <div class="date" id="date"></div>
  </div>

  <form action="${escape(searchURL)}" method="GET" autocomplete="off">
    <div class="search">
      <svg width="15" height="15" viewBox="0 0 16 16" fill="none"
           stroke="currentColor" stroke-width="1.8">
        <circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5L14 14"/>
      </svg>
      <input type="text" name="q" placeholder="Search the web…" autofocus>
    </div>
  </form>

  <div class="tiles">
${tiles.map(tileHTML).join('\n')}
  </div>
  <a class="edit" href="${editShortcutsURL}">Edit shortcuts</a>

<script>
${pageScript}
</script>
</body>
</html>
`
}
