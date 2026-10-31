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
