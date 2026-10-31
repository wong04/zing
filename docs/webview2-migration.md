# Zing: moving from Electron to WebView2

## Context

Google blocks sign-in in Electron browsers ("This browser or app may not be secure"). Making Zing pass by imitating Chrome was flagged as evading a bot check, so that route is out. A plain WebView2 window (Edge's Chromium, built into Windows) passed Google sign-in on 2026-10-31 with nothing disguised, and the user needs Google every day. So Zing moves to WebView2 before Phase 4.

This plan covers:
- what in today's Electron codebase survives, gets ported or gets replaced
- the decisions to make now
- how the phases change

## What exists today (measured from the repo, tests excluded)

| Area | Lines | Depends on | Fate on WebView2 |
|---|---|---|---|
| `src/shared/**`: pure TS logic (tabs, library, vault, import, convert, security, unblock, maths, theme, toolbox, layout, settings schema, commands) | 14,876 (+5,542 test) | nothing | **Kept as is.** Its ~1,600 vitest tests keep running unchanged. |
| `src/renderer/src/**`: React chrome, Settings, sidebar, panels, overlays | 4,852 | `window.zing` (82 IPC channels in `src/shared/ipc.ts`) | **Kept.** Only `src/preload` changes: the bridge moves from Electron IPC to WebView2's `chrome.webview` messaging, behind the same `ZingApi` interface. |
| `src/main/*` without Electron: `panels`, `tab-lifecycle`, `tab-ordering`, `sidebar`, `favicons`, `thumbnails`, `site-usage`, `launch-args`, `quit-policy`, `address`, `reader-extract`, `frame-batcher` | ~1,100 | none, or just node `fs`/`crypto` (`library-store`, `account`, `session-restore`, `settings-file`) | **Kept or lightly moved,** depending on decision 1 |
| `src/main/*` built on Electron: `zing-window`, `tab-service`, `windows`, `index`, `ipc-handlers`, `shell`, `overlay`, `pageui-layer`, `stack-dropdown`, `tab-overview`, `downloads`, `permissions`, `find`, `page-actions`, `privacy`, `adblock`, `start-page`, `favicon-service`, `taskbar-icon`, `single-instance`, `default-browser`, `test-hooks` | ~5,300 | `BrowserWindow`, `WebContentsView`, `session`, `protocol`, `dialog`, `Menu`, `safeStorage`… | **Rewritten** against WebView2. The Electron code and its e2e tests are the spec. |
| `src/platform/**`: process runner, DoH/pinned fetch/relay/diagnose, Windows security checks | 1,156 (+633 test) | Node (`net`, `tls`, `https`, `child_process`) | Kept as Node code or ported, depending on decision 1 |
| `e2e/**`: Playwright | 1,358 | `_electron.launch`; 33 `app.evaluate` calls into main | **Harness rewritten.** Playwright attaches to WebView2 over CDP (`--remote-debugging-port`), and the main-process hooks become a test-only host API. Most of the page-level steps carry over. |

**Net result:** about 70% of the code (shared + renderer + its tests) moves over untouched. The rewrite is the ~5,300 lines of Electron plumbing.

## Electron feature to WebView2 equivalent (the risky items, all checked by the W0 spike)

| Zing feature | Electron today | WebView2 |
|---|---|---|
| Private session | in-memory partition | InPrivate profile (`IsInPrivateModeEnabled`) |
| Purge a site when its last tab closes | `clearStorageData({origin})` | No per-origin API, so: CDP `Storage.clearDataForOrigin` via `CallDevToolsProtocolMethodAsync`, plus `CookieManager` (**spike**) |
| Popups keep `window.opener` | `createWindow` | `NewWindowRequested` + deferral, `args.NewWindow = <new CoreWebView2>` (**spike**) |
| UI over the page (dropdowns, find, panels) | transparent overlay `WebContentsView` | Transparent WebView2 stacked above the page. A WebView2 is its own window, so XAML can't draw over it, the same "airspace" rule as Electron's (**spike**) |
| `zing://home` | `protocol.handle` | `CoreWebView2CustomSchemeRegistration` + `WebResourceRequested` |
| Ad/tracker blocking | Ghostery engine on `webRequest` | Edge's built-in tracking prevention (Strict), plus tracker-param stripping on `NavigationStarting`. Full EasyList via uBlock Origin Lite comes with the extension converter (decision 2) |
| Downloads + Mark of the Web | `will-download` + our own MOTW writer | `DownloadStarting`. Edge's runtime probably writes MOTW itself; check that, and keep `holdMarkOfTheWeb` only if needed |
| Find, print, fullscreen, zoom, favicons, history | Electron APIs | `CoreWebView2.Find`, `ShowPrintUI`, `ContainsFullScreenElementChanged`, `ZoomFactor`, `FaviconChanged`, history via CDP |
| Permissions | `setPermissionRequestHandler` | `PermissionRequested` |
| User agent | stripped Electron UA | WebView2's own Edge UA, untouched. That's what passed Google, so `userAgent.ts` and the `defaultUserAgent` setting are retired |
| Account gate secrets | `safeStorage` | DPAPI (`ProtectedData`) |
| Window chrome | frameless + `titleBarOverlay` + Mica | WinUI 3 `ExtendsContentIntoTitleBar` + `MicaBackdrop` |
| Single instance, default browser, taskbar icon | Electron `app` | Windows App SDK `AppInstance` redirection, registry capabilities, `AppWindow.SetIcon` |
| Security updates | ship a new Electron every Chromium fix | **The Evergreen runtime is updated by Windows,** which removes the biggest ongoing cost |
| Installer | electron-builder, ~100 MB | Velopack (GitHub Releases auto-update), a few MB; the runtime is already on Windows 11 |

## Decisions (answered 2026-10-31)

- **Logic:** **A**. The C# host owns the app logic.
- **Ad blocking:** **Edge Strict now, and the extension converter moves up** so uBlock Origin Lite comes right after W.

## The options that were weighed

1. **Where app logic lives**, the one real fork:
   - **(A) C# host owns it.** WinUI 3 + C# hosts the windows and WebView2s, and ports `src/main` plumbing plus `src/platform` to C#, tested with xUnit. TS keeps the UI and `src/shared`. This is the conventional and simplest runtime model.
   - **(B) Thin C# host, TS "brain".** A hidden WebView2 runs the existing `src/main` logic, with Electron calls swapped for a host bridge. Less rewrite, but an unusual architecture, a bridge hop on every page event, and harder debugging.
   - *Recommended:* **A**. The Electron-bound part is plumbing that has to change anyway, and C# is the language WebView2 is designed for.
2. **Ad blocking in the meantime:**
   - Edge Strict tracking prevention now, with uBlock Origin Lite once the extension converter lands. That means moving the converter (roadmap 11) up to just after the migration.
   - Or keep the Ghostery engine by filtering in C# on `WebResourceRequested`, with a C# filter engine.
   - *Recommended:* **Edge Strict now, and the converter moved up.**
3. **Electron code:** delete it from `main` when the migration PR lands. It stays in git history and in the merged PRs as the reference. Keep PR #115 and merge it first, since it's the final Electron state.

## How the phases change

| Phase | Before | After |
|---|---|---|
| 0–3 | done (Electron) | Stay done as **spec**: PARITY rows B1–B34 are re-verified on WebView2, not redone from scratch |
| **W (new, before 4)** | – | Migration: **W0** spike, **W1** host + bridge + one tab, **W2** tab service, windows and overlays, **W3** bring Phase 3 features back one at a time against the existing e2e tests, **W4** delete Electron. Details below. |
| 4 tab bar position + split view | Electron layout | Same goals. `chromeInsets` (TS) is kept; split view means several WebView2 controls laid out by the host. Starts after W. |
| 5 themes | – | Unchanged. Mica tint comes from WinUI 3 natively. |
| 6 toolbox | Node `src/platform` | `src/platform` ported to C#: .NET `HttpClient` with `ConnectCallback` for IP pinning plus SNI, `Process` for tools, Windows security APIs. The TS pure logic is unchanged. |
| 6b vault | `safeStorage` + isolated world | DPAPI; the autofill script is injected with `AddScriptToExecuteOnDocumentCreatedAsync`, which is easier than before |
| 6c import | Node `fs` | Unchanged parsers; files are read by the host |
| 7 logo | done (C3) | Unchanged; `make-icons` still generates the `.ico` |
| 8 packaging | electron-builder, Electron auto-update | **Velopack** + GitHub Releases. WebView2 runtime check/bootstrapper for Windows 10. Default-browser registration. Signing unchanged. |
| 10 ongoing | Electron security bumps every ~7 days | **Gone.** The Evergreen runtime self-updates. Instead: Windows App SDK / .NET updates, and the weekly Krackn watch |
| 11 extensions (roadmap) | after v1 | **Moved up to right after W**, because it gives full ad blocking (uBO Lite) and the password managers |

### Phase W in detail

- **W0, spike** (throwaway, ~1 day). Tiny WinUI 3 app. Each item is checked by a Playwright-over-CDP test plus a manual check:
  - Google sign-in (you)
  - popup with `window.opener`
  - transparent overlay WebView2 over a page
  - 30 tabs' memory
  - per-origin purge via CDP
  - custom scheme
  - Playwright attaching to the app over CDP

  **Stop and reassess if any fail.**
- **W1, host + bridge.**
  - `host/` (.NET 9, WinUI 3) loads the existing `src/renderer` build in a chrome WebView2.
  - `src/preload` becomes `chrome.webview` messaging behind the unchanged `ZingApi`.
  - One page WebView2.
- **W2, core plumbing.** Port `tab-service`, `zing-window`, `windows`, overlays and settings/library/session file I/O to C#, keeping the TS ordering/lifecycle decisions in `src/shared`. The xUnit tests mirror the Electron unit tests.
- **W3, feature parity.**
  - Port the e2e harness (`e2e/zing.ts` → CDP attach; `test-hooks` → a host test API with the same main-process error guard, off-screen windows and fake cursor).
  - Bring back B1–B34 one area at a time, each green before the next.
- **W4, cleanup.**
  - Remove `electron`, `electron-vite` main/preload and `@ghostery/adblocker-electron`.
  - Update `AGENTS.md`, `CLAUDE.md`, `docs/PLAN.md`, `PARITY.md` and CI (`dotnet build/test` + vitest + Playwright on `windows-latest`).

## Prerequisites

- Install the **.NET 9 SDK** and **Windows App SDK** templates via winget. This needs your OK, since they're system-wide developer tools.
- Merge PR #115 first.

## Verification

- **W0:** you sign in to Google in the spike, and the spike's checklist tests pass.
- **Each W step:** `dotnet test`, `npm test` (vitest, ~1,600), `npm run lint`, `npm run typecheck`, and `npm run test:e2e`, with Playwright attaching to the WebView2 host. All green before the next step.
- **W3 done:** every Phase 3 e2e scenario passes on WebView2, and PARITY B1–B34 are re-ticked with evidence.
- **Final:** the opt-in web-compat test (`ZING_WEBCOMPAT=1`) passes, and you complete a real Google sign-in in Zing itself.

## W0 results (2026-10-31): go

The spike is in `spikes/webview2/`; its checks run with `npx playwright test -c spikes/webview2/playwright.config.ts`.

| Check | Result |
|---|---|
| Google sign-in (the user, by hand) | ✓ reached the password step and signed in. Nothing about the browser was disguised. |
| Playwright attaches over CDP | ✓ |
| `zing://home` served by the host | ✓ |
| Popup keeps `window.opener`, posts to it and closes | ✓ |
| Transparent overlay WebView2 above the page | ✓ |
| Per-origin purge (`Storage.clearDataForOrigin`) clears localStorage and cookies | ✓ |
| Memory (sum of working sets) | 545 MB with 1 tab, 2,463 MB with 31 (about 64 MB per extra tab). Today's Electron Zing measured the same way: 779 MB with 1 tab. |

WebView2 quirks found (carry these into W1):
- **XAML resources:** `XamlControlsResources` throws in a project without XAML resources ("Cannot find a resource with the given key"). Zing doesn't use it, since the UI is the React renderer.
- **Custom schemes:** `CoreWebView2EnvironmentOptions.CustomSchemeRegistrations` returns a copy. `.Add()` registers nothing; assign a whole list.
- **Projection types:** in this projection `TreatAsSecure` is an `int` and `HasAuthorityComponent` a `bool`.
- **Request filters:** custom-scheme requests need the three-argument `AddWebResourceRequestedFilter(..., CoreWebView2WebResourceRequestSourceKinds.All)`.

## W1 (2026-10-31): done

- **`host/Zing.Core`** (net9.0, no UI): `Bridge`, the message protocol that matches `src/shared/bridge/webviewBridge.ts`; `TabStore`; `Address`, a stand-in until W2 ports `addressInput`; and `ChromeApi`, the chrome's channels over an `IPage`. It's tested by `host/Zing.Core.Tests` (xUnit v3, 31 tests).
- **`host/Zing.Host`** (WinUI 3, self-contained, unpackaged; it builds `Zing.exe`):
  - The chrome WebView2 loads the built React renderer from `https://app.zing/` (a virtual host mapped to `out/renderer`).
  - One InPrivate page WebView2 sits in the page area.
  - Mica and an extended title bar. `-webkit-app-region` works through `IsNonClientRegionSupportEnabled`.
  - Off-screen and error-recording behaviour under `ZING_E2E`, like the Electron hooks.
- **Shared with Electron:**
  - `src/shared/zingApi.ts` is the single method→channel table.
  - `src/renderer/src/api.ts` picks the Electron preload or the WebView2 bridge.
- **One source of truth:** `npm run gen:host-defaults` exports the settings defaults, empty library, commands, search engines and layout from TypeScript into `host/Zing.Core/Defaults`, and `src/shared/hostDefaults.test.ts` fails if they drift.
- **Checks:**
  - `npm run test:host`
  - `npm run test:e2e:webview2`, which runs `e2e-webview2/` (chrome loads with one tab, typing an address navigates, Back/Forward)
  - CI runs both on `windows-latest`

**Not in W1, and still to do in W2:**
- settings writes
- more than one tab
- the overlays (stack dropdown, page UI, surface)
- the full address rule
- the start page
- per-site purge
- everything the 82-channel contract has beyond the ten channels answered now: `tabs:get`, `tabs:activate`, `tabs:navigate`, `command:run`, `settings:get`, `library:get`, `account:get`, `shell:get`, `toolpanel:get`, `chrome:titlebar-colors`

Every other channel replies `NotImplementedError`.

## W2 (2026-10-31): core plumbing done

**Ported to C# in `host/Zing.Core`:**
- **Pure logic, held to the TypeScript by shared test vectors.** These are `*.vectors.json` files under `src/shared`, read by vitest and by xUnit through `host/Zing.Core.Tests/Vectors.cs`:
  - `Address` / `AddressInput` (the full address rule) and `LinkCleaner` (tracker stripping)
  - `Settings` (schema rules come from the generated `settings-schema.json`), `SettingsStore`, and `JsonFiles` (atomic writes and the debounced writer)
  - `Library` and `LibraryStore`
  - `TabStore` (the full tab-store model: pinning, moving, the closed stack, shortcuts)
  - `Session` (the session codec)
- **`TabService`:** pages are created on first activation. Popups are adopted as the engine's own `CoreWebView2`, so `window.opener` works. Trackers are stripped on every navigation, and closing the last tab leaves a fresh one.
- **`ChromeApi`:** one window's bridge channels. It covers tabs, settings get/set (`settings:changed`), library get/apply/open/panel, the settings sheet (`shell:open-settings` / `shell:close-settings`), and the commands that have a host behind them. Every other command gives an "isn't implemented yet" notice, as in Electron.
- **`Layout.Insets`:** the `chromeInsets` rule, with sizes from the generated `layout.json`.
- **`SessionSaver`:** saves the primary window's tabs.

**In `host/Zing.Host`:**
- **`ZingApp`:** shared services, one WebView2 environment, the window list (the oldest window is primary), and flushing writes on quit.
- **`MainWindow`:** the chrome, pages laid out from `Insets()`, the settings surface over the whole window, the `NoticeLayer`, and New Window / Close Window.
- **`RendererView`:** any root of the React bundle. Every view of a window shares that window's bridge, and only `https://app.zing/` may call it.
- **`WebView2Page`:** queues calls until the engine is ready.
- **`AppPaths`:** the profile folder. It is `%APPDATA%\Zing`, the same folder the Electron build used; e2e runs must pass `ZING_USER_DATA`.

**Fixes in shared code:**
- Under WebView2, `api.ts` puts the bridge on `window.zing`, as the preload does.
- `webviewBridge.ts` hands the latest event that arrived before any listener to the first listener. Without it, a layer's first notice was lost.

**Checks:**
- `e2e-webview2/core.spec.ts` covers new tab and close, pinning, a setting surviving a relaunch, an invalid setting being refused, a favourite surviving a relaunch, restoreTabs, the sidebar narrowing the page, a notice, a popup keeping `window.opener`, and New Window. All 13 WebView2 e2e tests pass.
- The harness now quits by closing every window, so pending writes are flushed. It fails if Zing doesn't exit.

**Still to do (W3):**
- the start page (new tabs open `about:blank` for now)
- the stack dropdown and tab organisation (`tabOrganizer`)
- the tab overview, page UI (find, downloads, permissions) and native menus (`MenuFlyout`)
- the sidebar reveal and tool panels
- the account gate
- sleeping tabs and auto-close
- favicons
- per-site purge
- single instance and default browser
- `persistentProfile`
- the rest of B1–B34

## W3a (2026-10-31): menus, keys, links, start page, tab ordering

**Ported to `host/Zing.Core`.** All of these are held to the TypeScript by shared vectors; the start page is checked byte-for-byte:
- `Commands` (the registry, matching keys to shortcuts, and virtual-key to `KeyboardEvent.code`)
- `Menus` (the app, tab, stack, strip and library menus as a platform-neutral model)
- `LaunchArgs` and `LinkInbox`
- `StartPage`, `StartShortcuts` and `Palette`
- `TabOrganizer`, `TabOrdering`, `TabReorder`, `SiteGroup` and `RegistrableDomain`
- `TabLifecycle` and `TabSleep` (sleeping tabs and auto-close)
- `Downloads`, `Favicons`, `Permissions` and `PageUi`

The last two items are ported and tested but not yet wired into the host (W3b).

**Host:**
- Native `MenuFlyout` menus.
- Keyboard shortcuts from every view: WebView2 raises accelerator keys as `KeyDown` on the root grid.
- Single instance per profile through `AppInstance` redirection. A second launch hands its links over and exits; launch links open in the first window.
- `zing://home` is served by `WebResourceRequested`. The start page's search and Edit Shortcuts links are intercepted, and no web page can open Settings. It restyles when the accent or theme changes, and reloads when the shortcuts or search engine change.
- New tabs open the start page.
- Tab arrangements and the strip menu.

**Checks:**
- `e2e-webview2/windows.spec.ts` and `startpage.spec.ts`; 20 WebView2 e2e tests in all.
- `ZING_E2E_OFFLINE=1` points every page at a dead proxy, so no test reaches a live site.

**Open:**
- When two windows are closed within milliseconds of each other over CDP, the second window's bridge message was sometimes never received by the host. The harness now closes windows one at a time. Check this again with real input before W4.
- Keyboard shortcuts are covered by unit tests only, because CDP key events go straight to the renderer and skip the WebView2 accelerator path.

## W3b (2026-10-31): find, downloads, permission prompts

- **Page UI layer:** `PageUiLayer`, the `#pageui` renderer root at the page's top-right. It is sized from `pageui:height` and hidden when empty.
- **Find in page:** WebView2's `CoreWebView2.Find`, with Edge's own find dialog suppressed. `FindController` comes from the port; `ActiveMatchIndex` is already 1-based.
- **Downloads:**
  - `DownloadStarting` saves to the `DownloadList`'s free name and replaces Edge's download bubble with Zing's list (`e.Handled`).
  - Progress is published at most every 250 ms.
  - Open and Show in folder throw in e2e runs.
- **Mark of the Web:** the engine writes only a generic `HostUrl=about:internet`. Zing writes its own mark (the real HostUrl, plus the referrer when it's a web page) and holds it for 10 s (`MarkOfTheWeb.HoldAsync`, `ZoneFile`).
- **Permissions:** `PermissionRequested` maps WebView2 kinds to Electron's names and is decided by `PermissionBroker`. Grants are dropped on every new document and on close.
- **Checks:** `e2e-webview2/page.spec.ts` covers find (1 of 3, next, previous, close), a download landing with its mark and numbered on collision, and a notification prompt denied from the page UI.
- **Open:** in one full run, `a changed setting … survives a relaunch` failed once, and it then passed about 60 times in a row. The error text wasn't kept; from now on e2e logs are saved in full so a repeat can be diagnosed.

## W3c (2026-10-31): per-site purge, sleeping tabs, favicons, UI thread

- **The UI thread fix (the important one).** The custom `Main` never installed WinUI's `DispatcherQueueSynchronizationContext`, which the XAML-generated `Main` sets up. Every `await` on the UI thread therefore resumed on the thread pool, where WinUI and WebView2 calls fail with `RPC_E_WRONG_THREAD`. That caused notices sometimes failing from a background thread. It may also explain the lost close message when two windows closed together and the one-off settings-relaunch failure noted in W3a and W3b. `Program.Main` now installs the context.
- **Per-site purge:**
  - `SiteUsage` and `SiteDataKeeper` are in `Zing.Core`, shared by every window.
  - The host's `WebViewSitePurger` clears each origin with DevTools' `Storage.clearDataForOrigin` and deletes the registrable domain's cookies, through any live page.
  - It runs after the tab's page has closed.
  - Covered by `e2e-webview2/privacy.spec.ts`: a site keeps its data while any tab uses it and has nothing once its last tab closes.
- **Sleeping tabs and auto-close:**
  - `TabService.Sleep` drops the page and keeps the record. `TabLifecycle` (ported) decides, probing the page for unsaved input and checking whether it's playing audio.
  - A woken tab gets its scroll back once it has loaded.
  - With `ZING_TEST_CLOCK=1`, an e2e run can move the app clock with a `{kind: "test", action: "advance-clock"}` message, which only an e2e host accepts.
  - Thumbnails come with the tab overview.
  - Covered by `e2e-webview2/lifecycle.spec.ts`.
- **Favicons:**
  - WebView2 picks and fetches the icon itself (`FaviconChanged`, `GetFaviconAsync`). The tab keeps it as a PNG data URL in memory only, and keeps it while asleep.
  - The C# port of Electron's favicon chooser was removed, because the engine does that job. The TS module and its vectors stay until W4.
- **Checks:** the WebView2 e2e suite is 27 tests. One run saw the Playwright worker process itself crash (`0xC0000409`); it didn't repeat in 20 runs of that spec.

## W3d (2026-10-31): account gate, settings sheet and panels, stack dropdown, overview, text size, print

**Ported to `Zing.Core`:**
- `AccountStore`, with the TS's scrypt implemented in C# and checked against node and RFC 7914, and DPAPI in place of `safeStorage`.
  - An account made by the Electron build keeps its profile, but its verifier was sealed by Chromium's os_crypt rather than raw DPAPI. So the user is asked to reset their password once (`needsPasswordReset`).
- `ShellModel`, `ToolPanels`, `SidebarReveal`, `PanelLayout` and `DropRouter`.
- `StackDropdown`, `TabOverview` with `ThumbnailCache`, and `TextSize`.

**Host:**
- The #surface view is placed wherever the shell model's surface region says:
  - the whole window for the account gate and the settings sheet
  - the revealed sidebar plus visible tool panels otherwise
  - hidden when nothing floats
- The account gate covers the window until sign-in, and blocks commands meanwhile.
- `CoveringLayer` hosts `#overlay-stack` (below the tab strip) and `#overlay-overview` (over the page).
- The overview pictures each page with `CapturePreviewAsync` as it leaves the screen and when the overview opens. Sleeping tabs reuse that picture, because a hidden WebView2 never finishes a capture.
- Text size is a style override re-applied on every `DOMContentLoaded`. Print uses Edge's in-page preview.
- Zoom: WinUI's WebView2 has no zoom API, but the engine zooms on Ctrl+Plus, Ctrl+Minus and Ctrl+0 itself. So the zoom commands answer "not applicable" and let the keys through; the zoom menu items do nothing for now.
- The cursor for sidebar reveal comes from `GetCursorPos` and is never read in e2e runs.

**Checks:** 33 WebView2 e2e tests, adding:
- the settings sheet surviving a relaunch
- the account gate storing no plaintext secret and remembering the user
- arrangements and the stack dropdown
- the tab overview
- text size

**Open:**
- **Dropdown clicks:** DevTools mouse input never reaches the `#overlay-stack` view, while it does reach the chrome and `#pageui`. The dropdown test therefore drives its rows with DOM pointer events, so real clicks on dropdown rows need a hand check.
- **Still to do in W3:** reader mode, the history menu, the default-browser prompt, the taskbar icon, `persistentProfile`, and the toolbox (still "not implemented", as in Electron).

## W3e (2026-10-31): reader, history menu, persistent profile, taskbar icon. W3 is complete

- **Reader mode:**
  - Readability runs inside the page, in an isolated world opened through DevTools (`Page.createIsolatedWorld` and `Runtime.evaluate`), so page scripts can't see or tamper with it.
  - The TS lives in `src/shared/browser/readerPage.ts`, shared with Electron's extractor through `readerArticle.ts`. `npm run gen:reader` (part of `npm run build:host`) bundles it into `host/Zing.Host/Assets/reader.js` with its stylesheet.
  - Toggling reader mode off again reloads the page.
- **History menu:** read through `Page.getNavigationHistory` (WebView2 has no history list), with entries chosen through `Page.navigateToHistoryEntry`. Items come from the ported `HistoryMenu`.
- **`persistentProfile`:**
  - It's read at launch. When on, pages use a normal on-disk profile (`zing`) and nothing is purged; when off, they use the in-memory `zing-private` profile.
  - Zing's own UI views now use an in-memory profile too (`zing-chrome`), as Electron's partition did.
  - e2e runs keep the engine's data in their scratch profile.
- **Taskbar icon:** the C3 tile that contrasts with the Windows taskbar theme (registry `SystemUsesLightTheme`), following `UISettings.ColorValuesChanged`. The .exe carries the frosted tile.
- **Default browser:** `default-browser:open-settings` opens Windows' Default apps page. Registering Zing as a browser candidate is installer work (Phase 8, Velopack).
- **Every command in the registry now has a handler.** A host test checks that none answers "not implemented".
- **Harness:** the Playwright worker crashed with `0xC0000409` about once per full run, always right after a test closed its Zing. The harness no longer closes a DevTools connection that the exited host had already dropped.

**W3 result:** every Phase 0–3 feature of the Electron build runs on the WebView2 host. There are 958 host tests and 36 WebView2 e2e tests. Still to check by hand: real mouse clicks on the stack dropdown's rows.
