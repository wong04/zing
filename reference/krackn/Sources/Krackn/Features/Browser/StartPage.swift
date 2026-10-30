import Foundation
import WebKit
import AppKit

/// The new-tab page, served from the app rather than the network.
///
/// It's rendered under a private `aiohome://` scheme for two reasons: the page
/// and its font get cached by WebKit instead of being re-embedded into every
/// tab, and nothing about opening a tab touches the network — so a new tab can
/// never land on a blocked or filtered page.
enum StartPage {
    static let scheme = "aiohome"
    static let url = URL(string: "aiohome://start")!

    /// The palette, in a `<style>` of its own so an accent change can swap it
    /// in place (`BrowserModel.restyleStartPages`) instead of reloading.
    static var themeCSS: String {
        let light = Palette.lightTokens
        let dark = Palette.darkTokens
        return """
          :root {
            --bg: \(light.background);      --surface: \(light.surface);
            --tint: \(light.tint);          --well: \(light.well);
            --outline: \(light.outline);    --accent: \(light.accent);
            --on-accent: \(light.onAccent); --text: \(light.text);
            --muted: \(light.textSecondary);
              --control: \(light.control);   --raised: \(light.raised);
              --accent-ink: \(light.accentInk);
              --separator: color-mix(in srgb, var(--outline) 13%, transparent);
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: \(dark.background);      --surface: \(dark.surface);
              --tint: \(dark.tint);          --well: \(dark.well);
              --outline: \(dark.outline);    --accent: \(dark.accent);
              --on-accent: \(dark.onAccent); --text: \(dark.text);
              --muted: \(dark.textSecondary);
              --control: \(dark.control);   --raised: \(dark.raised);
              --accent-ink: \(dark.accentInk);
              --separator: color-mix(in srgb, var(--outline) 13%, transparent);
            }
          }
        """
    }

    /// The page's "Edit shortcuts" link. Never loaded: `BrowserTab` catches it,
    /// from this page only, and opens Settings › New Tab.
    static let editHost = "edit-shortcuts"

    static func escape(_ text: String) -> String { StartShortcuts.escapeHTML(text) }

    /// Builds the page. Both palettes are emitted and selected with
    /// `prefers-color-scheme`; the web view's appearance is pinned to the app's
    /// theme, so "Light" stays light even when macOS is dark.
    static func html(searchURL: String) -> String {

        // Your own names and addresses go into this page, so every one is
        // escaped: a tile called `<script>` is just a tile with brackets.
        let shortcuts = StartShortcuts.current
        let tiles = shortcuts.map { shortcut in
            """
            <a class="tile" href="\(escape(shortcut.url))" title="\(escape(shortcut.title))">
              <span class="glyph">\(escape(StartShortcuts.glyph(for: shortcut)))</span>
              <span class="label">\(escape(shortcut.title))</span>
            </a>
            """
        }.joined(separator: "\n")

        // Warm DNS, TCP and TLS for the search engine while the page is just
        // sitting there. By the time a query is submitted the connection is
        // already open, which removes the handshake from the critical path —
        // the bulk of the delay between typing and seeing results.
        let searchOrigin = URL(string: searchURL)
            .flatMap { url -> String? in
                guard let scheme = url.scheme, let host = url.host else { return nil }
                return "\(scheme)://\(host)"
            } ?? "https://www.google.com"

        let prefetch = shortcuts.compactMap { shortcut -> String? in
            guard let host = URL(string: shortcut.url)?.host else { return nil }
            return "<link rel=\"dns-prefetch\" href=\"https://\(escape(host))\">"
        }.joined(separator: "\n  ")

        return """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <link rel="preconnect" href="\(searchOrigin)" crossorigin>
        <link rel="preconnect" href="\(searchOrigin)">
        \(prefetch)
        <style id="krackn-theme">
        \(themeCSS)
        </style>
        <style>
          @font-face {
            font-family: 'AppInter';
            src: url('aiohome://font') format('truetype');
            font-weight: 100 900;
          }
          * { box-sizing: border-box; }
          html, body { height: 100%; margin: 0; }
          body {
            background: var(--bg);
            color: var(--text);
            font-family: -apple-system, BlinkMacSystemFont, 'Helvetica Neue', sans-serif;
            display: flex; flex-direction: column;
            align-items: center; justify-content: center;
            gap: 34px; padding: 40px 24px;
            -webkit-font-smoothing: antialiased;
          }
          /* Clock, matching the macOS menu-bar clock: tabular figures so the
             digits never shift width as the seconds tick over. */
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
        </style>
        </head>
        <body>
          <div class="clock">
            <div class="time">
              <span id="hh">--</span><span class="colon" id="c1">:</span><span id="mm">--</span><span class="seconds" id="ss"></span><span class="meridiem" id="ap"></span>
            </div>
            <div class="date" id="date"></div>
          </div>

          <form action="\(searchURL)" method="GET" autocomplete="off">
            <div class="search">
              <svg width="15" height="15" viewBox="0 0 16 16" fill="none"
                   stroke="currentColor" stroke-width="1.8">
                <circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5L14 14"/>
              </svg>
              <input type="text" name="q" placeholder="Search the web…" autofocus>
            </div>
          </form>

          <div class="tiles">
        \(tiles)
          </div>
          <a class="edit" href="\(scheme)://\(editHost)">Edit shortcuts</a>


        <script>
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
        </script>
        </body>
        </html>
        """
    }
}

/// Serves the start page and its font under the `aiohome://` scheme.
final class StartPageHandler: NSObject, WKURLSchemeHandler {
    /// Search endpoint the form posts to, e.g. Google's `/search`.
    var searchURL: String

    init(searchURL: String) {
        self.searchURL = searchURL
    }

    private var live = Set<ObjectIdentifier>()

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        let id = ObjectIdentifier(task)
        live.insert(id)

        let path = task.request.url?.host ?? "start"
        let payload: Data
        let mime: String

        if path == "font" {
            // The same Inter the native UI uses, so the page matches exactly.
            payload = fontData() ?? Data()
            mime = "font/ttf"
        } else {
            payload = Data(StartPage.html(searchURL: searchURL).utf8)
            mime = "text/html"
        }

        guard live.contains(id) else { return }
        live.remove(id)

        let response = URLResponse(
            url: task.request.url!, mimeType: mime,
            expectedContentLength: payload.count,
            textEncodingName: mime == "text/html" ? "utf-8" : nil)
        task.didReceive(response)
        task.didReceive(payload)
        task.didFinish()
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {
        live.remove(ObjectIdentifier(task))
    }

    /// Reads the bundled font, falling back to the dev-build location.
    private func fontData() -> Data? {
        if let url = Bundle.main.url(forResource: "Inter", withExtension: "ttf"),
           let data = try? Data(contentsOf: url) {
            return data
        }
        let beside = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Resources/Inter.ttf")
        if let data = try? Data(contentsOf: beside) { return data }
        return try? Data(contentsOf: URL(fileURLWithPath: "Resources/Inter.ttf"))
    }
}
