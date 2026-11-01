# Architecture review: Zing against other WebView2 apps, and a plan for bugs

*2026-11-01, after W5. Paths are relative to `host/Zing.Host/` unless they name another folder.*

## Context

This reviews Zing's design, compares it with other WebView2 applications, and sets out a plan for bugs as classes rather than one at a time. Zing moved Electron → WinUI 3 → plain Win32 + WebView2 within two days (W0–W5, all 2026-10-31/11-01). Every fault found so far was fixed one at a time. The git history (80 commits, ~10 fix commits) shows the same few classes recurring: overlay z-order, focus/keys, teardown races, and threading. The plan targets those classes rather than individual bugs.

Each workstream in section 4 is sized to become its own issue and branch.

---

## 1. What Zing is today (verified)

- **Process/hosting:** one `CoreWebView2Environment` per app (`Program.cs:60,159`), three profiles (`zing-chrome` InPrivate for UI, `zing` / `zing-private` for pages). A plain Win32 `HostWindow`, with WinUI `AppWindow` only for the title bar.
- **Views:** windowed hosting, one controller per view.
  - The chrome and each started tab are `WS_CHILD` windows.
  - Up to 5 overlays (surface, notices, pageui, overlay-stack, overview) are owned `WS_POPUP` windows with transparent WebView2s (`HostedView.cs:179-195`).
  - That's 1 + N + ≤5 controllers per window.
- **Logic split:** `Zing.Core` (7.9k lines, xUnit, no UI) owns all state. `Zing.Host` (2.4k lines, untested except via e2e) is the Win32/WebView2 layer. React (4.9k lines) renders the full state snapshots the host pushes (`host/Zing.Core/ChromeApi.cs:114-153,541`).
- **Bridge:** JSON invoke/result/event over `postMessage`, origin-checked (`RendererView.cs:80`).
  - The TS `ipc.ts` is the contract. C# registers channels by string, and the record shapes are hand-copied.
  - There's no codegen, no version handshake, and no test that every channel has a handler.
- **Tests:** 958 host tests, ~742 vitest, 36 e2e (Playwright over CDP, with off-screen windows and an error file). CI runs all of them on `windows-latest`.

## 2. Comparison with other WebView2 apps

| | **Zing** | **WebView2Browser** (MS sample browser) | **New Teams / new Outlook** | **Tauri (wry)** | **Electron (Zing's past)** |
|---|---|---|---|---|---|
| UI chrome | React in its own WebView2 | HTML in its own WebView2 (chrome + options) | React app is the whole UI | Web UI | Renderer |
| Content views | 1 windowed controller per tab | 1 windowed controller per tab | Mostly one main WebView2; embedded apps run as out-of-process iframes in the same renderer | Child webviews (multi-webview still unstable) | `WebContentsView` |
| Overlays over pages | Owned transparent popups | None: dropdowns stay inside the chrome strip | In-DOM (one view) | In-DOM | Transparent overlay views |
| IPC | Hand-written JSON channels, string-keyed on C# | Hand-written JSON message ids | GraphQL client data layer over IPC, data work in a worker | Typed `invoke` commands, generated TS bindings (e.g. tauri-specta), capability permissions | Typed `ipcMain` |
| Process-failure handling | **None** | Handles `ProcessFailed` (reload / recreate prompt) | Yes, and periodic memory page-out | wry surfaces it | `render-process-gone` |
| Runtime updates | Not handled | n/a | Prompts restart | n/a | App ships Chromium |

**Takeaways:**
1. **Zing's base is the right one.** Windowed controllers on a plain Win32 parent, one shared environment, and C# owning logic with xUnit is exactly the WebView2Browser pattern. W5 moving off WinUI's content window was correct; Microsoft's own browser sample never used it.
2. **Zing goes beyond every reference app in one place: overlays as separate top-level popups over live pages.** None of the reference apps does this. Teams and Outlook avoid it by keeping UI in one DOM. WebView2Browser avoids it by keeping its dropdowns in the chrome strip. This is where most of the recent bugs started: black overlays, menus under views, focus stolen on activation, DevTools input not reaching `#overlay-stack`, and `Cursor` reading the wrong foreground HWND (`MainWindow.cs:207`). Phase 4 split view multiplies it (more panes, divider overlays).
3. **Zing skips the robustness work every production WebView2 app does.** Microsoft's guidance says apps should handle `ProcessFailed` (renderer crash or hang, browser-process crash, GPU exit), `BrowserProcessExited`, and `NewBrowserVersionAvailable`, check the runtime is installed, and retry `CreateCoreWebView2Controller`. Zing handles none of these (grep finds none). A browser runs arbitrary sites, so renderer crashes are routine and will show as "blank tab / frozen window" bugs.
4. **Typed, generated IPC is the norm in mature hybrids.** Tauri generates bindings; Teams uses a data-layer schema. Zing's string channels plus hand-copied records already produced a dead feature: `shell:clear-session` has no C# handler, so Settings' "Clear session" always fails (`src/renderer/src/settings/CustomRows.tsx:225`).

## 3. Bug classes and the design gap behind each

| Class | Evidence (verified) | Root design gap |
|---|---|---|
| **A. Teardown / lifetime races** | `MainWindow.StartAsync` doesn't check `_closed` after its awaits (`MainWindow.cs:71,98`). `RendererView.CreateAsync` awaits navigation with no closed check (`RendererView.cs:55`). Layers' `Destroy()` only closes views whose creation finished (`StackLayer.cs:37`, `NoticeLayer.cs:56`, `PageUiLayer.cs:58`), so a view still loading at close leaks into `_renderers` after it was walked. `WebView2Page` doesn't re-check `IsOpen` after awaits in Capture/Reader/History/Evaluate. | Lifetime is a set of ad-hoc flags (`_closed`, `IsClosed`, `IsOpen`) checked by convention. Nothing owns "everything this window started". |
| **B. Errors that crash or vanish** | `async void` handlers record, then **rethrow** (`WebView2Page.cs:363`, `RendererView.cs:93`, FaviconChanged `:285`), which crashes the process. In production `TestHooks.Record` is a no-op (`TestHooks.cs:93`) and `Warn` goes to `Debug` only, so users' faults leave no trace. The renderer has 97 `void zing.*` calls and no `.catch`. | No error boundary or production log; the e2e error file is the only sink. |
| **C. Threading** | `SystemScheduler` fires `DebouncedJsonWriter` on a thread-pool `Timer` (`host/Zing.Core/JsonFiles.cs:56`, used at `Program.cs:134,140`). That reads stores that "don't lock" while the UI thread mutates them, and an I/O error there is unhandled, which crashes the process. `TryEnqueue` results are ignored. | The UI-thread rule (CLAUDE.md) is enforced only by review. |
| **D. Startup fragility** | A corrupt `settings.json` / library throws `SettingsFileException` / `LibraryFileException`, uncaught in `OpenServices` (`Program.cs:133-139`), so Zing won't launch and shows no message. Atomic writes don't flush before the move. | The "optional features never fail startup" rule doesn't cover user data. |
| **E. Engine failures** | No `ProcessFailed`, `BrowserProcessExited` or `NewBrowserVersionAvailable` handling, and no runtime-presence check. | Missing robustness layer (see §2, takeaway 3). |
| **F. Contract drift** | `shell:clear-session` is unhandled. Stale Electron comments in `ipc.ts` / `zingApi.ts`. 5 C#-only vectors are frozen. `TextSize`, `StartShortcuts`, `HistoryMenu`, `Palette` and `PageUi` are duplicated TS/C# without vectors. | Contract is TS-only; C# conforms by hand. |
| **G. Overlay / focus / z-order** | W5's three faults, the stack-dropdown input gap, `Cursor` foreground check, focus handoff tracked by `GotFocus` + `WM_SETFOCUS` (`HostWindow.cs:118,152`). | Overlays are separate HWNDs (§2, takeaway 2), and the accelerator, focus and real-input paths have no automated test. |
| **H. Untracked / unmeasured** | 111 issues open with no `bug` label. PARITY shows 1/112 done despite W3 shipping B1–B34. No crash or telemetry signal, even local. | Bugs live in commit messages and doc "Open" lists. |

## 4. The plan: six workstreams, in order

Each workstream ends with a guard (test, analyzer or harness rule) so its class can't come back. That matches the AGENTS.md style of "the rule that prevents each".

### W-0. Make bugs visible (first, small)
- Add a `bug` label and an issue template. It requires a failing test or repro, per the AGENTS.md rule that "a bug report needs a failing test".
- Re-tick PARITY rows B1–B34 that W3 shipped, with evidence links, and close those issues. Then the open list is the real list.
- **Production fault log.** `TestHooks.Record` always writes to a rolling `%LOCALAPPDATA%\Zing\logs\faults.log` (local only, never uploaded, so it fits the privacy model; mention it on the privacy banner page). `ZING_ERRORS_FILE` stays for e2e. Route `Warn` there too.
- Add a renderer-side `unhandledrejection` / `error` listener that forwards to the host over the bridge, so e2e fails on renderer errors too, not only host ones.

### W-1. Error boundary (class B)
- **Rule:** no `async void` except via one helper, `Fire(Task, string kind)`, which records and never rethrows. Convert `OnNewWindowRequested`, `FaviconChanged` and `RendererView.OnMessage`.
- A bridge failure always answers the caller. Malformed messages get an error reply where an id exists.
- **Renderer:** wrap the invoke proxy in `zingApi.ts` so a rejection shows a notice and is reported once, instead of 97 call-site `.catch`es.
- **Guard:** a Roslyn analyzer or a simple xUnit reflection/grep test bans `async void` outside the helper. Add an e2e test that forces a bridge handler to throw and asserts a reply plus a recorded error, not a crash.

### W-2. Lifetime scopes (class A, the biggest one)
- **Introduce `WindowScope`** (Zing.Host): a `CancellationToken` plus a list of disposables, created first in `MainWindow`.
  - Every async creation (`RendererView.CreateAsync`, `HostedView.CreateAsync`, layer `ShowAsync`, `WebView2Page.StartAsync`) takes the scope and **registers its view before awaiting**, not after it completes.
  - On close: cancel, then dispose everything registered, including views still loading.
  - After every await: `scope.ThrowIfClosed()` throws `ViewGoneException`. That replaces `_closed` / `IsClosed` / `IsOpen` checks scattered by convention. `TestHooks.Background` already treats it as expected.
- Pages get a per-page child scope, so Capture/Reader/History/Evaluate resume safely.
- **Guard:** a "close at every await" e2e. A test hook makes the host close the window at the Nth await of startup, popup creation, or layer show (N = 1..k). Assert no recorded error and no leaked HWNDs (`HostWindow.Open` / `HostedView._registered` counts back to zero). This is the systematic version of the Phase 3 popup/teardown failures.

### W-3. Threading and startup resilience (classes C, D)
- Replace `SystemScheduler` in the host with the `DispatcherScheduler` (as CLAUDE.md requires). Writes run on the UI thread; for big files, snapshot on the UI thread and write off-thread. Remove `SystemScheduler` from production paths.
- Debug-build `UiThread.Assert()` at Zing.Core's mutation entry points via an injected `IThreadGuard` (Core stays UI-free). It's a no-op in release and active in e2e builds, so any off-thread mutation fails a test.
- Check `TryEnqueue` results; a `false` gets recorded.
- **Corrupt user data:** on a load exception, rename to `settings.json.corrupt-<timestamp>`, start from defaults, and show a notice. Same for library and session. `Flush(true)` before the atomic move.
- **Guard:** xUnit tests for each corrupt-file path, plus an e2e test that launches with a garbage `settings.json` and expects a window and a notice.

### W-4. Engine robustness (class E)
- **`ProcessFailed`** on every page and view:
  - Render process exited or unresponsive in a **page**: show a "This page crashed – Reload" state in the tab (the tab record survives, by design: "a tab is a record").
  - In a **UI view**: recreate that view from state; the snapshot model makes this cheap, since a new view just fetches state.
  - GPU or utility exits: log only.
  - Browser process exited: tear down all controllers, wait for `BrowserProcessExited`, recreate the environment, and rebuild windows from `TabStore`/session.
- `NewBrowserVersionAvailable`: offer a "restart to update" notice (session is saved first).
- Startup: check `GetAvailableBrowserVersionString`, with a clear message plus the bootstrapper link if missing (feeds Phase 8). Retry controller creation per Microsoft guidance.
- **Guard:** e2e tests that navigate a page to `edge://kill` / `chrome://crash` (or call CDP `Page.crash`) and assert the crashed-tab state and a working reload. Another kills a chrome view's renderer and asserts the chrome returns.

### W-5. One contract, generated (class F)
- Extend `scripts/gen-host-defaults.ts` (already the TS → C# generator, drift-checked by `hostDefaults.test.ts`) to emit `host/Zing.Core/Defaults/channels.json`: every invoke and event channel with its arg arity.
- An xUnit test asserts every invoke channel has a handler registered in `ChromeApi` / `MainWindow` and vice versa. That would have caught `shell:clear-session`; then implement it.
- **Later, optionally:** generate the C# record shapes too (e.g. from TS types via a JSON schema generator), and add a protocol version check on bridge connect.
- Add vectors for the duplicated-without-vectors modules (`TextSize`, `StartShortcuts`, `HistoryMenu`, `Palette`, `PageUi`).
- Decide each oracle-only TS module's fate. Either keep it as an oracle under a clear `src/shared/oracles/` naming, or delete it, making C# the truth like the 5 frozen vectors.
- Fix the stale Electron comments.

### W-6. Overlay architecture decision gate (class G), before Phase 4 split view
- **Spike first (a throwaway in `spikes/`, as W0 and W5 did):** visual hosting (`CoreWebView2CompositionController` + DirectComposition/Windows.UI.Composition) with page, chrome and one overlay as visuals in one HWND's tree.
  - **Gains:** z-order is a tree, not popup windows; no owned-popup focus/activation dance; overlays clip and animate with the page; one HWND for input, so the "DevTools input doesn't reach `#overlay-stack`" gap disappears.
  - **Costs:** Zing must forward mouse and pointer input (`SendMouseInput` / `SendPointerInput`) and handle cursor, drag-drop, and UI Automation.
- **Go/no-go criteria:** keys reach the focused view, IME works, drag-drop works, Narrator reads overlays, and real clicks land on dropdown rows. Measure and compare against the current popup model.
- **If no-go:** keep popups but centralise them. One `OverlayHost` per window owns all popups, their z-order and focus policy, replacing per-layer `Raise()` calls (`MainWindow.cs:392`). Fix `Cursor` to use `HostWindow.IsForeground` (`MainWindow.cs:207` vs `HostWindow.cs:193`).
- **Either way:**
  - Split the `MainWindow` god-class (426 lines, three host interfaces) into `WindowLayout`, `OverlayHost`, `DownloadHost` (+ MOTW) and `PermissionHost`.
  - Add a real-input smoke tier: a few tests marked `@real-input` that place the window on-screen and use `SendInput`. They run manually or on a dedicated runner, never in normal CI, and cover accelerators, focus after tab close, and dropdown clicks.

**Decision (2026-11-01): keep owned popups.**
- Overlays stay owned popup windows. `OverlayHost` holds them to one stacking order and one focus policy.
- Phase 4 builds split view's dividers and drop zones as new `OverlayHost` levels.
- Why: the popup model passes every e2e test and the real-input tier on Windows. The visual-hosting spike passed 5 of 6 automated checks, but a switch would cost 4–6 weeks with Narrator, IME and drag-drop unproven.
- Revisit only if split view brings the overlay bug class (G) back. `spikes/visual-hosting/README.md` records where to start.

### Order and why
1. **W-0 and W-1** (days): every later bug becomes visible and survivable instead of a crash.
2. **W-2 and W-3** (≈1 week): remove the races that cause most flakes and the startup bricking.
3. **W-4** (≈1 week): the reliability floor any browser needs before daily use.
4. **W-5** (ongoing, cheap): stops drift as Phase 4–6 add channels.
5. **W-6** before Phase 4: split view is the feature that would multiply overlay bugs, so decide the hosting model first.

Phase 4 waits for W-0..W-3. W-4 and W-5 can run in parallel worktrees per AGENTS.md: the shared contract (`WindowScope`, `Fire`, `channels.json`) lands first, then one branch is integrated at a time.

## 5. Checked and fine

- **Pages on window close:** `ChromeApi.Dispose` calls `Tabs.Destroy()`, which closes every page and releases each tab's site data, so a closed window's sites are still purged (`host/Zing.Core/ChromeApi.cs:297`, `host/Zing.Core/TabService.cs:290-294`).

## 6. Status (2026-11-01, after waves 1–3 and Windows e2e)

Everything below is on `claude/funny-lovelace-vzz8lv`. It is checked on Linux (vitest, xUnit with 1,114 tests, lint, typecheck, a `Zing.Host` compile) and on CI's `windows-latest`. On commit `02f7b85` (run 41289375469), everything passed: lint, typecheck, vitest (1,806), xUnit (1,114), **the whole e2e suite (46 tests)**, and `crash.spec.ts` repeated 8 times. `ci.yml` now also runs on pushes to `claude/**`; drop that before merging if it isn't wanted.

**What the Windows runs found:**
- **The renderer reporter was too eager.** It recorded a failed host call that its caller had awaited and expected (an invalid setting). Now a failure is reported only when nothing handles it.
- **A real engine crash** (found by `crash.spec.ts`, about 1 run in 2):
  - The overview's picture of a tab leaving the screen was started and the page hidden at once. A hidden WebView2 never finishes a capture, so the call stayed pending forever and the tab got no picture.
  - If that page's renderer then died, the WebView2 browser process crashed (0xC0000005) and took every view with it.
  - `MainWindow.Hide` now keeps a leaving page drawn under the new one until its picture is taken (at most 1.5 s). `live-counts` reports captures in flight, and the test checks they drain.
- **Known flake, not fixed:** on a busy runner the engine sometimes holds the profile lock for more than 15 s after Zing quits. It happened once, in `windows.spec.ts`. It predates this work: one test's close took 27 s in the first wave 1 run.

| Workstream | Done | Still open |
|---|---|---|
| W-0 | Bug issue template. `app:report-error`: a view's unhandled errors are recorded like the host's. Local fault log, kept only by test runs (decided 2026-11-01: a real install keeps none, since Zing keeps nothing by default), with only types and stack frames outside e2e. The e2e harness prints how the host exited, plus its fault log, when Zing goes before `close()`. | – (the `bug` label already existed; PARITY B rows re-verified: 20 Done and closed with test evidence, 10 Partial, 4 Open, with the gaps listed in PARITY.md) |
| W-1 | No `async void` in `Zing.Host` (`HostSourceRulesTests`). A malformed bridge message gets an error reply. Fire-and-forget work and refused `TryEnqueue` are recorded everywhere. A failed host call is reported only when nothing handles it. | – |
| W-2 | `WindowScope`: each window tracks every view as its creation starts and checks itself after each await. `e2e/teardown.spec.ts` closes a new window at each of its 6 startup checkpoints. | – |
| W-3 | Saves run on the UI thread, flushed to disk. Corrupt settings, library, session and account files are set aside and the first window says so (`e2e/userdata.spec.ts`). | – |
| W-4 | `ProcessFailed`: a crashed page keeps its tab with a Reload affordance, Zing's own views reload themselves, and a failed browser process rebuilds the engine with the primary window's tabs. Runtime check at startup, and an engine-update notice (`e2e/crash.spec.ts`). | The rebuild path has no e2e (it would need killing the engine's browser process) |
| W-5 | `channels.json` generated from `ipc.ts` and checked both ways (`ChannelContractTests`). `shell:clear-session` restored. Vectors for text size, start shortcuts, page UI and palette, which found one drift: JS-style upper-casing of tile initials. | Generated C# record shapes (optional) |
| W-6 | `OverlayHost` owns the popups' stacking order. `MainWindow` is split into `WindowLayout`, `DownloadHost` and `PermissionHost`. The sidebar's cursor check counts owned overlays. A real-input tier (`npm run test:e2e:real-input`); all 3 tests passed on a Windows runner ([run 41290988995](https://github.com/wong04/zing/actions/runs/41290988995)), including a real click on a stack dropdown row, the hand check open since W3d. The visual-hosting spike is in `spikes/visual-hosting`, and its README is the decision record. | – (decided: keep popups; see §4 W-6). The spike's results are its README's record: 5 of 6 automated checks pass on Windows, and the cursor check fails on the projection gap. |

**Hand checks still to do:**
- Settings → Clear session signs out of sites.
- A popup that fails to start: `window.open` returns null (before, an Edge window opened).
