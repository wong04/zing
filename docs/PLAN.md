# Zing: port Krackn to Chromium on Windows

## Context

[yityler/Krackn](https://github.com/yityler/Krackn) is a macOS-only "super-app": a privacy browser (WebKit, in-memory data store) plus a toolbox with Open a Link/unblock relay, Diagnose, Network & VPN, Convert (196 formats), Download (yt-dlp), Security, file Viewer, a graphing calculator, background remover and about a dozen dev utilities. It is 22.7k lines of SwiftUI/AppKit and has no third-party deps, but it relies on ~15 Apple-only frameworks (WebKit, Network, Vision, SceneKit, PDFKit, AVKit, Quartz/QuickLook, Security/Keychain, CryptoKit, `sips`, `textutil`, Gatekeeper/XProtect). None of that runs on Windows, so **"switch to Chromium + works on Windows" is a rewrite, not a patch**. The risk is quietly losing features along the way. This plan makes parity measurable: every Krackn feature becomes a tracked item, and an item closes only with a passing test or a screenshot.

New work on top of parity: rename to **Zing**, a new logo, a **top / left / right tab bar** setting, and **colourful themes** (today it's 7 accent hues on a near-grey palette, `App/Accent.swift` + `App/Theme.swift`).

## Stack decision (tradeoffs)

| | Electron (TS) | WebView2 + C# (WinUI 3) | CEF (C++ / CefSharp / JCEF) | Full Chromium fork (C++) |
|---|---|---|---|---|
| Effort to parity | **Lowest**: UI is HTML/CSS, which suits Krackn's custom pills/glass look | Medium | High | **Very high**: ~100 GB checkout, multi-hour Windows builds, rebasing every 4 weeks |
| Platforms | Win + mac + Linux | **Windows only** | All (C++); CefSharp is Windows only | All |
| Themes + vertical tabs | CSS variables, easy | XAML, fine | Depends on UI toolkit | C++ Views, hard |
| Private in-memory session | `session.fromPartition('x')` (no `persist:`) | InPrivate profile | In-memory request context | Native incognito |
| Intercept/relay https | `protocol.handle('https')`, which **could fix Krackn's "web apps can't use the relay" limit** | `WebResourceRequested` | Resource handlers | Full control |
| Built-in DoH | `app.configureHostResolver` | Edge settings only | Chromium flags | Native |
| Chrome extensions | Partial (MV3 subset) | Edge extension API (limited) | Very limited | Full |
| Google sign-in | Needs a clean UA; Google sometimes blocks embedded browsers | Generally works (Edge UA) | Same risk as Electron | Works |
| DRM (Netflix etc.) | Needs the castlabs Electron build | Yes (Edge) | Needs a Widevine license | Needs a Widevine license |
| Install size / RAM | ~100 MB / higher | **~5 MB** (runtime ships with Windows) / low | ~150 MB | ~150 MB |
| Chromium updates | You bump Electron (every 8 weeks) | Microsoft auto-updates | You bump CEF | You rebase patches |

Considered and rejected: **Tauri** (Chromium only on Windows, WebKit on mac, multi-webview still unstable) and **JCEF + Java** (matches your Java, but Swing/JavaFX theming is painful and the JCEF API surface is small).

**Decided: Electron + TypeScript + React (electron-vite, electron-builder, npm), Windows only.** Being Windows-only means Windows APIs (DPAPI, Windows Hello, Mica, registry) can be used directly with no cross-platform abstraction, and CI runs only on `windows-latest`.

**Other decisions:** public repo `wong04/zing`, MIT, crediting Krackn in the README. Password autofill becomes a **built-in vault** (Phase 6b).

**v1 scope beyond parity:** split view (Phase 4) and import from other browsers (Phase 6c). The built-in ad/tracker blocker replaces Chrome extensions. **No AI, ever.** Browser sync comes later and will be E2E encrypted, so data gets stable IDs now. On the roadmap after v1: Spaces/profiles, Ctrl+K command bar, Chrome Web Store extensions, sync.

## macOS → Zing mapping (the risky items)

- **Privacy model**: in-memory partition, `clearStorageData({origin})` on tab close, tracker-param stripping + 48-domain blocklist via `webRequest`. Session restore and persistent profile stay opt-in, and the warning banner stays.
- **Overlays over the page**: a `WebContentsView` paints *above* the chrome renderer, which is the same trap as Krackn's "SwiftUI drawn over the page doesn't get clicks". Tab dropdown, menus, find bar and floating panels go in a transparent top-layer overlay view (or the page view shrinks). Decide this in Phase 1, before building chrome.
- **Popups/OAuth**: `setWindowOpenHandler` with `createWindow` so `window.opener` survives (Krackn gotcha).
- **Unblock**: DoH via `fetch`; `PinnedHTTPClient` becomes Node `tls.connect({host: ip, servername})`, so no hand-rolled HTTP/1.1. `aiorelay://` may become a real https intercept.
- **Network & VPN**: `.mobileconfig` is replaced by a generated PowerShell script (`Add-DnsClientDohServerAddress`, run as admin by the user), and `wg-quick` by WireGuard for Windows commands. The "honest" copy stays.
- **Convert**: `sips` → sharp (HEIC needs libheif/ffmpeg, which sharp's prebuilt lacks); `textutil` → mammoth/pandoc; PDF via `printToPDF`; ffmpeg/yt-dlp installed via `winget` instead of `brew` (`Core/ToolInstaller.swift`). The 196-format catalog gets a coverage matrix, and any format Windows can't do is listed explicitly, not dropped silently.
- **Security**: Gatekeeper/notarization → Authenticode (`WinVerifyTrust`); quarantine → Mark-of-the-Web; XProtect → Defender status + `MpCmdRun` scan of the file; SIP/FileVault → Secure Boot/BitLocker/SmartScreen status. Add Windows executable types (.exe .scr .msi .lnk .ps1 .hta .vbs) to the disguised-file checks.
- **Viewer**: SceneKit → three.js loaders; AVKit → `<video>`; QuickLook fallback → metadata + "open with default app".
- **Background remover**: Vision → ONNX Runtime + a segmentation model. Check the model license (popular ones are AGPL or non-commercial).
- **Password autofill**: the Keychain bridge has no Windows equivalent, so it's replaced by the built-in vault (Phase 6b). Krackn's field detection logic (main frame + same-origin frames, shadow DOM, autofocus polling, cross-origin never filled) is reused, running in an isolated world.
- **Window chrome**: traffic-light placement → frameless window + `titleBarOverlay` (keeps Win11 snap layouts), Mica background. Default browser via registry capabilities + `ms-settings:defaultapps`. Links from other apps via single-instance lock + `second-instance` (the `LinkInbox` analog). ⌘ shortcuts → Ctrl.
- **Drop**: `WebKitTuning`, canvas diagnostics, `Migration.swift` (all Mac/WebKit-specific).

## Looking ahead: Arc, Dia, and what a remake has to plan for

**Market moment.** Arc is being sunset on **Dec 22, 2026**, and The Browser Company (now owned by Atlassian) is all-in on Dia, an AI browser that is Mac-only with a Windows waitlist. That leaves Windows users of sidebar-tab browsers without a home right now. Zing's vertical tabs, split view and colour themes put it directly in that gap, and a no-AI, keeps-nothing browser is a clear contrast to Dia.

**Lessons from Arc.** It died of complexity, a steep learning curve and no business model, not of bad engineering. Its Windows port took a year because they ported Swift to Windows (we avoid that). For Zing: keep a small core, make power features opt-in, and keep the first-run experience boring.

**Decisions that have to land in Phase 1/3, because they're expensive to retrofit:**
1. **A tab is a record, not a `webContents`.** Tabs need to exist without a live renderer, which enables sleeping/discarded tabs (Electron has no built-in discard and memory is its weak spot), split view, session restore, import, and later Spaces. `TabSnapshot` in `TabOrganizer` already points this way.
2. **Stable UUIDs + `updatedAt` on every tab, bookmark, reading-list item and vault entry**, so E2E sync can be added later without a migration.
3. **Session is a parameter, not a global.** v1 has a single in-memory partition, but passing it explicitly lets roadmap Spaces/profiles each get their own isolated session.
4. **Ad/tracker blocking at full strength.** Use `@ghostery/adblocker-electron` with EasyList/EasyPrivacy, a big upgrade from Krackn's 48 domains and not subject to MV3's rule caps. Lists update on a schedule. Keep tracking-param stripping.
5. **One command registry.** Krackn's `Commands.swift` already routes every shortcut through named actions. Keep that shape so menus, shortcuts and the roadmap Ctrl+K bar all share it.
6. **Extensions are on the roadmap, not designed out.** Electron's support is partial and officially a non-goal. `electron-chrome-extensions` + `electron-chrome-web-store` would add store installs, but MV3 is still pending upstream. Keep tab operations behind a service that a `chrome.tabs` shim could wrap later.

**Ongoing costs to commit to now:**
- **Security cadence.** A browser is only as safe as its Chromium. Electron supports the latest 3 majors, and Chromium ships security fixes weekly. Commit to shipping Electron security releases within ~7 days, with a Dependabot/Renovate PR → CI → signed release → auto-update. This is the biggest long-term cost of a browser and it never ends.
- **Following upstream Krackn.** Krackn is still active (last pushed today), and a rewrite can't `git merge` it. Record the baseline Krackn SHA in `PARITY.md`, and add a weekly GitHub Action that lists new Krackn commits since that SHA and opens a "port or skip" issue.
- **Web-compat traps.** Google sign-in blocks some embedded browsers (needs a clean Chrome UA, tested in CI); Widevine DRM needs castlabs' Electron build plus free EVS signing; passkeys/WebAuthn through Windows Hello need verifying in Electron early.
- **Windows polish people notice.** Accessibility (ARIA across all chrome, keyboard navigation, `forced-colors` so themes respect Windows contrast themes), per-monitor DPI, Win11 snap layouts, default-browser flow, winget manifest + optional MSIX/Microsoft Store, SmartScreen reputation (signing).
- **Legal/notices.** Chromium/Electron credits page, ffmpeg (LGPL/GPL, so download it via winget rather than bundling), yt-dlp, model licenses, and a name check on "Zing". Keep Krackn's scope note about school/work filters.
- **Privacy stance vs. future features.** Krackn "keeps nothing". Sync, imported data, the vault and crash reporting all store something, so each has to be opt-in, flip the privacy banner, and (for sync) be end-to-end encrypted, with a Railway backend that only ever sees ciphertext.

## Efficiency (Electron's weak spot, so it's designed in, not tuned later)

**Budgets, enforced in CI** by a `perf` Playwright suite reading `app.getAppMetrics()`. A regression of more than 10% fails the build.

| Metric | Budget |
|---|---|
| Cold start → window painted | < 800 ms |
| Idle, 1 tab (all processes) | < 250 MB |
| Each sleeping tab | ~0 MB (no renderer) |
| Chrome UI renderer | < 60 MB |
| Tab switch | < 50 ms |
| Tab drag / sidebar resize | 60 fps |
| Installer | < 120 MB |

**Memory**
- **Sleeping tabs**: discard the `webContents` of a tab that has been inactive for a while (30 min by default, configurable), keeping the URL, title, favicon, scroll position and a small thumbnail. Never discard the active tab, a visible split pane, pinned tabs (configurable), tabs playing audio or tabs with unsaved form input. This reuses the policy shape of Krackn's auto-close in `TabOrganizer`.
- **Lazy session restore**: restored and imported tabs are records only, and only the active tab loads.
- **Bounded caches**: favicons (still memory-only), thumbnails and the Reader cache are LRU with size caps.
- **Virtualized tab list**, so a 300-tab vertical sidebar renders only the visible rows.

**CPU / responsiveness**
- **Nothing heavy on the UI thread.** Conversion, large-file hashing, ffmpeg/yt-dlp supervision, DoH diagnostics, ONNX background removal and import parsing all run in Electron `utilityProcess` workers.
- **Batched IPC**: tab title/loading/favicon updates are coalesced to one message per animation frame, with a typed IPC contract.
- **Background tabs**: keep Chromium's background throttling on. An **Energy saver** setting lowers frame rate for non-visible splits/tabs (`setFrameRate`) and pauses autoplay. Never disable site isolation for performance, because it's a security boundary.
- **The ad/tracker blocker is the biggest win**: it means fewer requests and less third-party JS on every page. Ghostery's engine is serialized to a binary cache, so it loads in milliseconds at startup, with list updates in the background.

**Startup**
- Show the window on `ready-to-show` with only the chrome bundle. Defer blocklist refresh, update check, import detection and toolbox modules until after first paint.
- Bundle with electron-vite/esbuild and enable the V8 compile cache for the main process. Code-split every Toolbox tool (three.js, graphing, ONNX, PDF) so it loads on first open.

**Disk / install size**
- Don't bundle ffmpeg or yt-dlp (get them via winget, which also avoids LGPL/GPL redistribution). The background-removal model downloads on first use from a GitHub Release with a SHA-256 check.
- Use electron-builder `compression: maximum`, ASAR, prune devDependencies, and Electron fuses (disable `RunAsNode` etc., which also tightens security).
- Debounced, atomic writes (temp file + rename) for `library.json`, session and vault, so there's no write per keystroke and no torn file on crash.

**Build efficiency (getting to v1 faster)**
- Port Phase 2's pure logic file by file with its Swift tests as the spec: translate, run, fix until the assertion counts match. This is mechanical, can be parallelized across files, and is the fastest way to lock in behaviour.
- After Phase 2, run Phases 3, 6 and 7 as separate parallel sessions on their own branches, since they touch different folders (`src/browser`, `src/toolbox`, `assets/brand`).

## Phases

**0. Repo + tracking**
- Local checkout at `C:\Users\USER\OneDrive\Desktop\Coding\zing`, next to your other projects. Turn on `git config core.longpaths true` (Krackn's clone already hit Windows' path limit). Exclude `node_modules`/`out` from OneDrive sync, or move the checkout outside OneDrive if sync churn becomes a problem. The session moves there before any files are written.
- `gh repo create wong04/zing --public`, MIT LICENSE, with Krackn's Swift kept under `reference/krackn/` (read-only) until parity, then deleted.
- `PARITY.md` (recording the baseline Krackn commit SHA): one row per Krackn feature/setting, generated from README, CLAUDE.md, the 30 `AppSettings` keys and the Toolbox catalog. Each row becomes a GitHub issue (labels `area:*`, `parity` | `new`), with milestones per phase and a GitHub Project board. This is how you sync across devices.
- CI on `windows-latest`: lint, typecheck, Vitest, Playwright-Electron, installer build.

**1. Shell on Windows**: Electron skeleton named Zing from day 1 (app ID `app.zing.browser`, schemes `zing://home`, user-data dir, strings). One window, one tab, in-memory session, frameless chrome, overlay-view architecture, and CSS theme tokens ported from `Theme.swift`/`Accent.swift` (`lightTokens`/`darkTokens` already exist as CSS).

**2. Port pure logic + tests first**: port these 1:1 to TS with their assertions (Krackn has 969): `TabOrganizer` (+`SiteGroup` Google split, 24 cases), `AddressInput` (22 inputs), `ThreatModel` (16 must-be-clean sites), `BlockDiagnostics.classify` (32 combos), `Expression`, `GraphEngine`, `PanelLayout`, `ToolboxSearch` (34 queries), `StartShortcuts`, `FaviconChoice`, `DownloadPolicy`, `DevTools`, the accent generator (WCAG AA). The tests lock in behaviour before any UI exists.

**3. Browser parity**: tabs (pin, drag-reorder, the 5 arrangements, Separate/Dropdown stacks, auto-close rules), popups, downloads, fullscreen, bookmarks/Reading List (`library.json`), Favourites bar, find, Reader (@mozilla/readability), text zoom, print, tab overview, history long-press, start page + shortcuts, memory-only favicons, permissions (camera/mic), session restore, multi-window, default browser, the full shortcut set (`Commands.swift`), sidebar + edge reveal, floating panels (max 3), account gate (`safeStorage`/DPAPI instead of Keychain).

**4. Tab bar position (new)**: `tabBarPosition: top | left | right` in settings. The vertical strip has the same features as top: pinned tabs as an icon grid, stacks as collapsible groups instead of dropdowns, vertical drag-reorder, resizable width, collapse-to-icons with hover expand. When tabs sit on the left, the app nav sidebar folds into the top of the tab sidebar so there aren't two side rails. The ordering/stack logic from Phase 2 is reused unchanged; only layout differs.

**4b. Split view (new)**: 2–4 tabs in one window (horizontal or vertical split, draggable dividers), shown as a single joined chip/row in the tab strip at every bar position. Create it by dragging a tab onto the page edge, from the context menu, or with a shortcut. The focused pane drives the address bar. Split groups survive session restore, sleeping tabs and theme changes. Built on the tab-record model: a split is a layout over tab IDs, not a special kind of tab. Ordering strategies treat a split as one unit, the same way `TabOrganizer.movingStack` treats stacks. Tests: layout math (pure), and e2e for create/close/drag-out and focus routing.

**5. Themes (new)**: move from "accent on grey" to full themes where background, surfaces, tab strip, sidebar and start page all carry colour. 8–10 presets (e.g. Ocean, Sunset, Forest, Grape, Candy, Midnight, plus Mono for current fans), each with light/dark, and a custom theme from one seed colour, extending the existing hue-rotation + contrast-lift generator. Win11 Mica tint follows the theme. Keep the WCAG AA test over every generated token pair.

**6. Toolbox parity**: Open a Link (4 methods), Diagnose, Network & VPN (Windows), Convert + format matrix, Download (yt-dlp, Save page via relay), Security (Windows checks), Viewer, Utilities, Graphing calculator, Background remover, Tool setup (winget).

**6b. Password vault (new, replaces Keychain autofill)**: `vault.zing` in userData, encrypted with `safeStorage` (DPAPI) and unlocked with Windows Hello (WinRT `UserConsentVerifier` through a small napi addon). If Hello isn't set up, the vault shows an explicit "set up Windows Hello" error rather than falling back silently. Includes the key icon in sign-in fields, a save prompt on submit, exact-origin matching (not registrable domain), CSV import from Chrome/Edge/Bitwarden, the existing password generator, and a manage page in Settings. Keeps the privacy model honest: when the vault holds entries, the Settings privacy banner turns from green to a warning, the same way `restoreTabs` does. Tests: origin matching, the cross-origin-frame refusal, encryption round-trip.

**6c. Import from other browsers (new)**: one import wizard with a parser per source, each a pure function with fixture tests:
- **Chrome / Edge / Brave on this PC**: auto-detect profiles under `%LOCALAPPDATA%`, read `Bookmarks` (JSON) for bookmarks + Favourites bar, and optionally the last session's open/pinned tabs. Passwords come from the browser's own CSV export (Chrome's encrypted store uses app-bound encryption, which other apps aren't meant to read, so we don't try).
- **Dia (Mac laptop)**: Dia doesn't run on Windows, so import its exported bookmarks HTML + passwords CSV, copied over. Verify Dia's export options early, since this decides how good the experience is.
- **Arc**: pinned tabs, folders and Spaces from `StorableSidebar.json`. Spaces become bookmark folders until the Spaces roadmap item lands. Cheap, and timely with Arc's shutdown.
- **Generic**: Netscape bookmarks HTML (every browser exports it) and Chrome/Bitwarden/1Password CSV into the vault.
- **Not imported:** history and cookies, because Zing keeps neither. Imported data is a stored record, so the privacy banner flips.

**7. Logo + branding** (can run anytime, needed before 8): 3–4 SVG concepts → you pick → generator script outputs `.ico` (16–256), `.icns`, PNGs and installer art (replaces `Tools/make-icon.swift`). Port `Tests/logo`. Quick name-collision check on "Zing" for browsers/software.

**8. Packaging**: electron-builder NSIS per-user installer, auto-update via `electron-updater` + GitHub Releases, code signing (Azure Trusted Signing, about $10/mo, otherwise SmartScreen warns).

**9. Parity audit**: walk `PARITY.md`. Every row is closed with a linked test or screenshot, or listed in a "Known differences on Windows" section in the README. Then delete `reference/krackn/`.

**10. Ongoing**: Renovate for Electron security bumps (ship within ~7 days), the weekly upstream-Krackn diff issue, and blocklist updates. After v1, the roadmap in milestone order: E2E sync, Spaces/profiles, Ctrl+K command bar, Chrome Web Store extensions.

Parallelizable once Phase 2 lands: 3, 6 and 7 are independent; 4 and 5 follow 3's tab strip; 6c only needs the bookmark/vault stores.

## Verification

- `npm run test:perf`: the efficiency budgets above, run in CI on every PR.
- `npm test` (Vitest: ported pure-logic suites, assertion counts compared to Krackn's), `npm run test:e2e` (Playwright `_electron.launch`: tabs, popups/opener, downloads, privacy purge, render, tab bar positions, theme switching without losing tabs, the Krackn gotcha being guarded against).
- CI is green on `windows-latest`. The installer installs, sets Zing as default browser, opens external links in an existing window, and auto-updates from a test release.
- Every visual change gets a screenshot, as Krackn's conventions require. Theme presets × 3 tab positions × split view are screenshotted in light and dark.
- Import: run against your real Chrome profile on the PC and a Dia export from the laptop. Counts in Zing must match the source.
- Web-compat smoke test in CI: Google sign-in page loads with no "browser not secure" message, a YouTube video plays and goes fullscreen, an OAuth popup returns to its opener.
